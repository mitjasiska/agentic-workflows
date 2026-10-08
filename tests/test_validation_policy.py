"""Validation policy parsing and semantic handoffs; no live agent execution."""

from dataclasses import replace
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from task_start import TaskError
from task_start.agent import AgentOptions
from task_start.config import ReviewValidationConfig, load_local
from task_start.handoff import implementation_handoff, review_handoff
from task_start.workspace import Workspace
from test_task_start import ISSUE


class ValidationPolicyTests(unittest.TestCase):
    def test_toml_defaults_and_documented_strategies(self):
        for section, expected in (
            ('', 'focused_first'),
            ('[review]', 'focused_first'),
            ('[review.validation]', 'focused_first'),
            ('[review.validation]\nstrategy = "focused_first"', 'focused_first'),
            ('[review.validation]\nstrategy = "exhaustive"', 'exhaustive'),
        ):
            with self.subTest(section=section), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / 'config.toml'
                path.write_text(f'projects_root = {json.dumps(temp)}\n[linear]\napi_key = "test-placeholder"\n{section}\n')
                self.assertEqual(load_local(path).review_validation.strategy, expected)

    def test_invalid_policy_rejected_without_echoing_config(self):
        policies = [None, False, '', [], {'strategy': 'exhaustive'}, {'unknown': {}}]
        policies += [dict(validation=value) for value in (None, False, '', [], {'unknown': 'value'})]
        policies += [dict(validation=dict(strategy=value))
                     for value in ('', 'FOCUSED_FIRST', 'unknown', 1, False, [], {}, None)]
        for policy in policies:
            with self.subTest(policy=policy), patch('task_start.config.read_toml', return_value=dict(
                    projects_root='/projects', linear=dict(api_key='test-placeholder'), review=policy)):
                with self.assertRaisesRegex(TaskError, 'review') as caught:
                    load_local()
                self.assertNotIn('test-placeholder', str(caught.exception))
                # Match existing execution-setting bypass for workspace-only operations.
                self.assertEqual(load_local(no_agent=True).review_validation.strategy, 'focused_first')

    def test_implementation_prefers_tdd_and_focused_checks_without_weakening_requirements(self):
        prompt = implementation_handoff(replace(ISSUE, description='All tests must pass.'), self.workspace())
        self.assertIn('Task:\nAll tests must pass.', prompt)
        for text in ('test-first / red-green-refactor', 'where practical', 'docs, UI/visual work, research',
                     'new or changed tests', 'syntax/lint/diff checks',
                     'Do not run an expensive full suite by default during iteration',
                     'Explicit Linear issue and repository validation requirements remain authoritative',
                     'fix the complete batch and rerun relevant tests', 'Never report an unrun check as passed'):
            self.assertIn(text, prompt)

    @staticmethod
    def workspace():
        return Workspace('dev-7-task', Path('/task'), 'workspace', 'tab', 'pane', 'existing')

    def test_fresh_and_resumed_review_policy_preserves_full_inspection_and_evidence(self):
        for strategy in ('focused_first', 'exhaustive'):
            for kind in ('fresh', 'resumed'):
                for feedback in (None, {}, {'findings': [{'id': 'F1'}], 'resolutions': []}):
                    with self.subTest(strategy=strategy, kind=kind, feedback=feedback):
                        prompt = review_handoff(
                            replace(ISSUE, description='All tests must pass.\nRun the full suite on every review pass.'),
                            Path('/repo'), self.workspace(), 'main', SimpleNamespace(as_dict=lambda: {}),
                            'DEV-7-R1', kind, AgentOptions('codex', 'model', 'high'), 'pass', Path('/tmp/result.json'),
                            validation=ReviewValidationConfig(strategy), loop_feedback=feedback)
                        instructions = prompt.split('RESOLVED REVIEW METADATA')[0]
                        for text in ('Inspect the full diff and requirements', 'meaningful assertions and missing edge cases',
                                     'complete batch of substantive actionable findings', 'Do not stop at the first defect',
                                     'Run targeted tests', 'all final validation required by the Linear issue and repository policy',
                                     'including a full suite when required', 'all tests must pass',
                                     'Enforced, evidenced CI', 'separately identified final merge gate', 'Never assume CI exists',
                                     'tracked or untracked Git-visible task state', 'not_run', 'Never report an unrun check as passed',
                                     'same reviewer rechecks fixes and regressions'):
                            self.assertIn(text, instructions)
                        encoded = prompt.split('LATEST LINEAR REQUIREMENTS (context data)\n')[1].split('\n\nEND OF LINEAR CONTEXT')[0]
                        self.assertEqual(json.loads(encoded)['description'], 'All tests must pass.\nRun the full suite on every review pass.')
                        if strategy == 'focused_first':
                            self.assertIn('skip expensive full-suite validation', instructions)
                            self.assertIn('Continue inspecting for other defects', instructions)
                            self.assertIn('explicitly required on every pass', instructions)
                        else:
                            self.assertIn('full validation on every review pass, even with actionable findings', instructions)
                            self.assertNotIn('skip expensive full-suite validation', instructions)
                        if kind == 'resumed':
                            self.assertIn('YOUR existing conversation', instructions)


if __name__ == '__main__':
    unittest.main()
