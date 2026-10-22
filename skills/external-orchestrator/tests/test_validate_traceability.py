from __future__ import annotations

import copy
import importlib.util
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "scripts" / "validate_traceability.py"
SPEC = importlib.util.spec_from_file_location("validate_traceability", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def ready_trace() -> dict:
    return {
        "schema_version": 1,
        "operation": "prepare-task",
        "source_state": {
            "kind": "git",
            "revision": "abc123",
            "dirty": False,
            "observed_at": "2030-01-01T10:00:00Z",
        },
        "authority_sources": ["AGENTS.md", "docs/architecture/example.md"],
        "work_item": {
            "identifier": None,
            "title": "Deliver a bounded outcome",
            "requirements": ["Preserve the documented boundary"],
        },
        "guidance": [
            {
                "status": "Implementation Latitude",
                "source": "docs/architecture/example.md",
                "section": "Storage",
                "summary": "The internal representation is reversible.",
                "material": False,
            }
        ],
        "implementation_latitude": ["Choose the internal representation"],
        "conflicts": [],
        "affected_tasks": [],
        "readiness": "Ready",
    }


class ValidateTraceabilityTests(unittest.TestCase):
    def test_ready_with_implementation_latitude_is_valid(self) -> None:
        self.assertEqual(MODULE.validate(ready_trace())["readiness"], "Ready")

    def test_ready_with_conflict_is_invalid(self) -> None:
        trace = ready_trace()
        trace["conflicts"] = [
            {"source": "architecture.md", "section": "Boundary", "summary": "Conflict"}
        ]
        with self.assertRaisesRegex(MODULE.ValidationError, "Ready traces"):
            MODULE.validate(trace)

    def test_material_open_area_blocks_ready(self) -> None:
        trace = ready_trace()
        trace["guidance"][0].update({"status": "Open Area", "material": True})
        with self.assertRaisesRegex(MODULE.ValidationError, "material Open Area"):
            MODULE.validate(trace)

    def test_blocked_requires_evidence(self) -> None:
        trace = ready_trace()
        trace["readiness"] = "Architecture decision required"
        with self.assertRaisesRegex(MODULE.ValidationError, "needs a conflict"):
            MODULE.validate(trace)

    def test_invariant_conflict_can_block(self) -> None:
        trace = ready_trace()
        trace["guidance"][0].update({"status": "Product Invariant", "material": True})
        trace["conflicts"] = [
            {"source": "architecture.md", "section": "Boundary", "summary": "Task crosses it"}
        ]
        trace["readiness"] = "Architecture decision required"
        self.assertEqual(MODULE.validate(trace)["readiness"], "Architecture decision required")

    def test_reconciliation_records_stale_task_without_fixed_identifier_format(self) -> None:
        trace = ready_trace()
        trace["operation"] = "reconcile-architecture"
        trace["affected_tasks"] = [
            {
                "identifier": "opaque-work-item-id",
                "title": "Update the integration",
                "reason": "Its acceptance criteria cite superseded behavior.",
                "proposed_action": "Refine the affected acceptance criterion.",
            }
        ]
        self.assertEqual(len(MODULE.validate(trace)["affected_tasks"]), 1)

    def test_validation_does_not_mutate_input(self) -> None:
        trace = ready_trace()
        original = copy.deepcopy(trace)
        MODULE.validate(trace)
        self.assertEqual(trace, original)


if __name__ == "__main__":
    unittest.main()
