#!/usr/bin/env python3.12
"""Synchronize the ChatGPT plugin from the canonical create-linear-task skill."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import sys
import tempfile
from typing import Any


PLUGIN_ROOT = Path(__file__).resolve().parent.parent
REPOSITORY_ROOT = PLUGIN_ROOT.parent.parent
CANONICAL_SKILL = REPOSITORY_ROOT / "skills" / "create-linear-task"
BUNDLED_SKILL = PLUGIN_ROOT / "skills" / "create-linear-task"
PORTABLE_MANIFEST = PLUGIN_ROOT / "plugin.json"
COMPATIBILITY_MANIFEST = PLUGIN_ROOT / ".codex-plugin" / "plugin.json"
IGNORED_PARTS = {"__pycache__"}
IGNORED_SUFFIXES = {".pyc", ".pyo"}


class SyncError(RuntimeError):
    """Raised when the plugin cannot be synchronized safely."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Copy the canonical create-linear-task skill into the ChatGPT plugin "
            "and generate its compatibility manifest."
        )
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="fail if generated plugin files differ; do not write anything",
    )
    return parser.parse_args()


def load_portable_manifest() -> dict[str, Any]:
    try:
        manifest = json.loads(PORTABLE_MANIFEST.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise SyncError(f"cannot read portable manifest: {error}") from error
    if not isinstance(manifest, dict):
        raise SyncError("portable manifest must contain a JSON object")

    required_strings = ("name", "version", "description")
    for field in required_strings:
        value = manifest.get(field)
        if not isinstance(value, str) or not value.strip():
            raise SyncError(f"portable manifest field {field!r} must be non-empty")

    author = manifest.get("author")
    if (
        not isinstance(author, dict)
        or not isinstance(author.get("name"), str)
        or not author["name"].strip()
    ):
        raise SyncError("portable manifest field 'author.name' must be present")

    extensions = manifest.get("extensions")
    extension = extensions.get("com.openai") if isinstance(extensions, dict) else None
    if not isinstance(extension, dict) or not isinstance(extension.get("interface"), dict):
        raise SyncError("portable manifest must define extensions.com.openai.interface")
    return manifest


def render_compatibility_manifest(manifest: dict[str, Any]) -> bytes:
    compatibility = {
        "name": manifest["name"],
        "version": manifest["version"],
        "description": manifest["description"],
        "author": manifest["author"],
        "homepage": manifest.get("homepage"),
        "repository": manifest.get("repository"),
        "keywords": manifest.get("keywords", []),
        "skills": "./skills/",
        "interface": manifest["extensions"]["com.openai"]["interface"],
    }
    compatibility = {
        key: value for key, value in compatibility.items() if value is not None
    }
    return (json.dumps(compatibility, ensure_ascii=False, indent=2) + "\n").encode()


def skill_files(root: Path) -> dict[Path, bytes]:
    if not root.is_dir():
        raise SyncError(f"canonical skill directory is missing: {root}")

    files: dict[Path, bytes] = {}
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root)
        if any(part in IGNORED_PARTS for part in relative.parts):
            continue
        if path.suffix in IGNORED_SUFFIXES:
            continue
        if path.is_symlink():
            raise SyncError(f"skill contains an unsupported symlink: {relative}")
        if path.is_file():
            files[relative] = path.read_bytes()
    if Path("SKILL.md") not in files:
        raise SyncError("canonical skill does not contain SKILL.md")
    return files


def check_generated_files(manifest: dict[str, Any]) -> list[str]:
    differences: list[str] = []
    expected_skill = skill_files(CANONICAL_SKILL)
    actual_skill = skill_files(BUNDLED_SKILL) if BUNDLED_SKILL.is_dir() else {}

    expected_paths = set(expected_skill)
    actual_paths = set(actual_skill)
    for path in sorted(expected_paths - actual_paths):
        differences.append(f"missing bundled skill file: {path}")
    for path in sorted(actual_paths - expected_paths):
        differences.append(f"unexpected bundled skill file: {path}")
    for path in sorted(expected_paths & actual_paths):
        if expected_skill[path] != actual_skill[path]:
            differences.append(f"bundled skill file differs: {path}")
        elif (CANONICAL_SKILL / path).stat().st_mode & 0o777 != (
            BUNDLED_SKILL / path
        ).stat().st_mode & 0o777:
            differences.append(f"bundled skill file mode differs: {path}")

    skills_root = BUNDLED_SKILL.parent
    if skills_root.is_dir():
        for child in sorted(skills_root.iterdir()):
            if child != BUNDLED_SKILL:
                differences.append(f"unexpected entry in plugin skills directory: {child.name}")

    expected_compatibility = render_compatibility_manifest(manifest)
    try:
        actual_compatibility = COMPATIBILITY_MANIFEST.read_bytes()
    except FileNotFoundError:
        differences.append("missing generated .codex-plugin/plugin.json")
    else:
        if actual_compatibility != expected_compatibility:
            differences.append("generated .codex-plugin/plugin.json differs")
    if COMPATIBILITY_MANIFEST.parent.is_dir():
        for child in sorted(COMPATIBILITY_MANIFEST.parent.iterdir()):
            if child != COMPATIBILITY_MANIFEST:
                differences.append(
                    f"unexpected entry in .codex-plugin directory: {child.name}"
                )
    return differences


def synchronize(manifest: dict[str, Any]) -> None:
    files = skill_files(CANONICAL_SKILL)
    temporary_root = Path(tempfile.mkdtemp(prefix=".create-linear-task-", dir=PLUGIN_ROOT))
    staged_skill = temporary_root / "create-linear-task"
    try:
        for relative, content in files.items():
            destination = staged_skill / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(content)
            shutil.copymode(CANONICAL_SKILL / relative, destination)

        BUNDLED_SKILL.parent.mkdir(parents=True, exist_ok=True)
        if BUNDLED_SKILL.is_symlink():
            raise SyncError(f"refusing to replace symlink: {BUNDLED_SKILL}")
        if BUNDLED_SKILL.exists():
            shutil.rmtree(BUNDLED_SKILL)
        staged_skill.replace(BUNDLED_SKILL)
    finally:
        shutil.rmtree(temporary_root, ignore_errors=True)

    COMPATIBILITY_MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    COMPATIBILITY_MANIFEST.write_bytes(render_compatibility_manifest(manifest))


def main() -> None:
    args = parse_args()
    try:
        manifest = load_portable_manifest()
        if args.check:
            differences = check_generated_files(manifest)
            if differences:
                for difference in differences:
                    print(f"error: {difference}", file=sys.stderr)
                raise SystemExit(1)
            print("Plugin synchronization check passed.")
            return

        synchronize(manifest)
        differences = check_generated_files(manifest)
        if differences:
            raise SyncError("synchronization completed with unexpected differences")
        print(f"Synchronized plugin skill from {CANONICAL_SKILL}")
    except SyncError as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1) from error


if __name__ == "__main__":
    main()
