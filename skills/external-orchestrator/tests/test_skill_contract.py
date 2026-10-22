from __future__ import annotations

import re
import unittest
from pathlib import Path


SKILL_ROOT = Path(__file__).parents[1]
REPOSITORY_ROOT = SKILL_ROOT.parents[1]


class SkillContractTests(unittest.TestCase):
    def test_activation_and_managed_role_exclusion_are_explicit(self) -> None:
        skill = (SKILL_ROOT / "SKILL.md").read_text(encoding="utf-8")
        agents = (REPOSITORY_ROOT / "AGENTS.md").read_text(encoding="utf-8")
        metadata = (SKILL_ROOT / "agents" / "openai.yaml").read_text(encoding="utf-8")

        self.assertIn("If the user invokes `$external-orchestrator`", skill)
        self.assertIn("use it automatically", skill)
        self.assertIn("managed Implementer or Reviewer", skill)
        self.assertIn("use the `external-orchestrator` skill", agents)
        self.assertIn('default_prompt: "Use $external-orchestrator', metadata)
        self.assertIn("allow_implicit_invocation: true", metadata)
        self.assertIn('value: "linear"', metadata)

    def test_blocking_and_idempotency_guards_are_present(self) -> None:
        preparation = (SKILL_ROOT / "references" / "task-preparation.md").read_text(
            encoding="utf-8"
        )
        reconciliation = (
            SKILL_ROOT / "references" / "architecture-reconciliation.md"
        ).read_text(encoding="utf-8")

        self.assertIn("Do not create, update, relate, or transition", preparation)
        self.assertIn("Search current Linear state", preparation)
        self.assertIn("A second run must refine the same issue", preparation)
        self.assertIn("obtain explicit approval", reconciliation)
        self.assertIn("make no write when it already matches", reconciliation)

        scenarios = (SKILL_ROOT / "references" / "validation-scenarios.md").read_text(
            encoding="utf-8"
        )
        self.assertIn("Verified creation", scenarios)
        self.assertIn("creates no duplicate", scenarios)
        self.assertIn("makes no Linear write", scenarios)

    def test_skill_contains_no_fixed_linear_target(self) -> None:
        files = [SKILL_ROOT / "SKILL.md"]
        files.extend((SKILL_ROOT / "agents").rglob("*.yaml"))
        files.extend((SKILL_ROOT / "references").rglob("*.md"))
        files.extend((SKILL_ROOT / "scripts").rglob("*.py"))
        text = "\n".join(path.read_text(encoding="utf-8") for path in files)

        self.assertNotRegex(text, re.compile(r"\bDEV-\d+\b"))
        self.assertNotIn("Development Lab", text)
        self.assertNotIn("Agentic Workflows", text)


if __name__ == "__main__":
    unittest.main()
