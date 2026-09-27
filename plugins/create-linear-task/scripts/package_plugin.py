#!/usr/bin/env python3.12
"""Build a deterministic ZIP archive of the create-linear-task plugin."""

from __future__ import annotations

import argparse
import io
from pathlib import Path, PurePosixPath
import sys
import zipfile

import sync_plugin


FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build the synchronized create-linear-task ChatGPT plugin archive."
    )
    parser.add_argument("--output", required=True, type=Path, help="output ZIP path")
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify an existing archive without rewriting it",
    )
    return parser.parse_args()


def archive_files() -> list[tuple[PurePosixPath, Path]]:
    files = [
        (PurePosixPath("plugin.json"), sync_plugin.PORTABLE_MANIFEST),
        (
            PurePosixPath(".codex-plugin/plugin.json"),
            sync_plugin.COMPATIBILITY_MANIFEST,
        ),
    ]
    for relative in sync_plugin.skill_files(sync_plugin.BUNDLED_SKILL):
        archive_path = PurePosixPath("skills/create-linear-task") / relative.as_posix()
        files.append((archive_path, sync_plugin.BUNDLED_SKILL / relative))
    return sorted(files, key=lambda entry: entry[0].as_posix())


def build_archive() -> bytes:
    manifest = sync_plugin.load_portable_manifest()
    differences = sync_plugin.check_generated_files(manifest)
    if differences:
        joined = "; ".join(differences)
        raise sync_plugin.SyncError(f"plugin is not synchronized: {joined}")

    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        for archive_path, source_path in archive_files():
            info = zipfile.ZipInfo(archive_path.as_posix(), FIXED_TIMESTAMP)
            info.create_system = 3
            info.external_attr = (source_path.stat().st_mode & 0xFFFF) << 16
            archive.writestr(info, source_path.read_bytes())
    return buffer.getvalue()


def main() -> None:
    args = parse_args()
    try:
        expected = build_archive()
        output = args.output.expanduser().resolve()
        if args.check:
            try:
                actual = output.read_bytes()
            except FileNotFoundError:
                print(f"error: archive is missing: {output}", file=sys.stderr)
                raise SystemExit(1)
            if actual != expected:
                print(f"error: archive is stale: {output}", file=sys.stderr)
                raise SystemExit(1)
            print(f"Plugin archive check passed: {output}")
            return

        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(expected)
        print(f"Built plugin archive: {output}")
    except sync_plugin.SyncError as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1) from error


if __name__ == "__main__":
    main()
