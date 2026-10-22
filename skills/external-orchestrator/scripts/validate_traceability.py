#!/usr/bin/env python3
"""Validate and normalize an External Orchestrator traceability record."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any


READINESS = {"Ready", "Architecture decision required"}
GUIDANCE_STATUSES = {
    "Product Invariant",
    "Design Direction",
    "Open Area",
    "Implementation Latitude",
}
OPERATIONS = {"prepare-task", "reconcile-architecture"}


class ValidationError(ValueError):
    """Raised when a traceability record violates the contract."""


def _object(value: Any, path: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValidationError(f"{path} must be an object")
    return value


def _list(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list):
        raise ValidationError(f"{path} must be an array")
    return value


def _text(value: Any, path: str, *, nullable: bool = False) -> str | None:
    if nullable and value is None:
        return None
    if not isinstance(value, str) or not value.strip():
        raise ValidationError(f"{path} must be a non-empty string")
    return value.strip()


def _require_keys(value: dict[str, Any], path: str, keys: set[str]) -> None:
    missing = sorted(keys - value.keys())
    if missing:
        raise ValidationError(f"{path} is missing: {', '.join(missing)}")


def _text_list(value: Any, path: str, *, nonempty: bool = False) -> list[str]:
    items = _list(value, path)
    if nonempty and not items:
        raise ValidationError(f"{path} must not be empty")
    return [_text(item, f"{path}[{index}]") for index, item in enumerate(items)]  # type: ignore[list-item]


def validate(record: Any) -> dict[str, Any]:
    root = _object(record, "trace")
    _require_keys(
        root,
        "trace",
        {
            "schema_version",
            "operation",
            "source_state",
            "authority_sources",
            "work_item",
            "guidance",
            "implementation_latitude",
            "conflicts",
            "affected_tasks",
            "readiness",
        },
    )

    if root["schema_version"] != 1:
        raise ValidationError("schema_version must be 1")
    if root["operation"] not in OPERATIONS:
        raise ValidationError(f"operation must be one of: {', '.join(sorted(OPERATIONS))}")
    if root["readiness"] not in READINESS:
        raise ValidationError(f"readiness must be one of: {', '.join(sorted(READINESS))}")

    source_state = _object(root["source_state"], "source_state")
    _require_keys(source_state, "source_state", {"kind", "revision", "dirty", "observed_at"})
    _text(source_state["kind"], "source_state.kind")
    _text(source_state["revision"], "source_state.revision")
    if not isinstance(source_state["dirty"], bool):
        raise ValidationError("source_state.dirty must be a boolean")
    _text(source_state["observed_at"], "source_state.observed_at")

    _text_list(root["authority_sources"], "authority_sources", nonempty=True)

    work_item = _object(root["work_item"], "work_item")
    _require_keys(work_item, "work_item", {"identifier", "title", "requirements"})
    _text(work_item["identifier"], "work_item.identifier", nullable=True)
    _text(work_item["title"], "work_item.title")
    _text_list(work_item["requirements"], "work_item.requirements", nonempty=True)

    guidance = _list(root["guidance"], "guidance")
    material_open_area = False
    for index, item in enumerate(guidance):
        path = f"guidance[{index}]"
        entry = _object(item, path)
        _require_keys(entry, path, {"status", "source", "section", "summary", "material"})
        if entry["status"] not in GUIDANCE_STATUSES:
            raise ValidationError(
                f"{path}.status must be one of: {', '.join(sorted(GUIDANCE_STATUSES))}"
            )
        _text(entry["source"], f"{path}.source")
        _text(entry["section"], f"{path}.section")
        _text(entry["summary"], f"{path}.summary")
        if not isinstance(entry["material"], bool):
            raise ValidationError(f"{path}.material must be a boolean")
        material_open_area |= entry["status"] == "Open Area" and entry["material"]

    _text_list(root["implementation_latitude"], "implementation_latitude")

    conflicts = _list(root["conflicts"], "conflicts")
    for index, item in enumerate(conflicts):
        path = f"conflicts[{index}]"
        entry = _object(item, path)
        _require_keys(entry, path, {"source", "section", "summary"})
        _text(entry["source"], f"{path}.source")
        _text(entry["section"], f"{path}.section")
        _text(entry["summary"], f"{path}.summary")

    affected_tasks = _list(root["affected_tasks"], "affected_tasks")
    for index, item in enumerate(affected_tasks):
        path = f"affected_tasks[{index}]"
        entry = _object(item, path)
        _require_keys(entry, path, {"identifier", "title", "reason", "proposed_action"})
        _text(entry["identifier"], f"{path}.identifier")
        _text(entry["title"], f"{path}.title")
        _text(entry["reason"], f"{path}.reason")
        _text(entry["proposed_action"], f"{path}.proposed_action")

    blocked_evidence = bool(conflicts) or material_open_area
    if root["readiness"] == "Ready" and blocked_evidence:
        raise ValidationError("Ready traces cannot contain conflicts or a material Open Area")
    if root["readiness"] == "Architecture decision required" and not blocked_evidence:
        raise ValidationError(
            "Architecture decision required needs a conflict or material Open Area as evidence"
        )

    return root


def _load(path: str) -> Any:
    if path == "-":
        return json.load(sys.stdin)
    with Path(path).open(encoding="utf-8") as handle:
        return json.load(handle)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace", help="JSON trace path, or - for standard input")
    args = parser.parse_args(argv)

    try:
        record = validate(_load(args.trace))
    except (OSError, json.JSONDecodeError, ValidationError) as error:
        print(f"invalid trace: {error}", file=sys.stderr)
        return 1

    json.dump(record, sys.stdout, indent=2, sort_keys=True, ensure_ascii=False)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
