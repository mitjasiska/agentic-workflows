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
from task_start.implementation_pass import parse_implementation
from task_start.review_result import parse_verdict
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

    def test_implementation_preserves_requirements_with_thorough_tests_and_focused_execution(self):
        for requirement in self.requirements():
            with self.subTest(requirement=requirement):
                prompt = implementation_handoff(replace(ISSUE, description=requirement), self.workspace())
                self.assertIn(f'Task:\n{requirement}\n\nWorkflow-owned implementation instructions:', prompt)
                instructions = prompt.split('Workflow-owned implementation instructions:\n')[1]
                for text in ('thorough automated tests', 'meaningful assertions', 'failure paths, and invariants',
                             'do not reduce coverage', 'arbitrary test-count targets or caps',
                             'test-first / red-green-refactor when useful; TDD is optional',
                             'new or changed tests', 'syntax/lint/diff checks',
                             'Do not routinely run the full suite merely to end the implementation pass',
                             'Explicit Linear issue and repository validation requirements remain authoritative',
                             'Complete checks explicitly required locally or for this pass',
                             'Merge-time requirements may be satisfied externally where permitted',
                             'never assume CI exists', 'pending final validation to the human',
                             'fix the complete batch and rerun relevant tests',
                             'Never report an unrun check as passed', 'source and applicable revision'):
                    self.assertIn(text, instructions)
                self.assertNotIn('complete required final validation before reporting ready', instructions)

    @staticmethod
    def requirements():
        return ('Cover retry failures and the configured limit.',
                'All tests must pass. Full regression must pass before merge.',
                'This agent must run the full suite locally before handoff.\n'
                'Run the full suite on every review pass. Use strict red-green-refactor.')

    @staticmethod
    def workspace():
        return Workspace('dev-7-task', Path('/task'), 'workspace', 'tab', 'pane', 'existing')

    def test_fresh_and_resumed_review_policy_preserves_full_inspection_and_evidence(self):
        for strategy in ('focused_first', 'exhaustive'):
            for kind in ('fresh', 'resumed'):
                for requirement in self.requirements():
                    for feedback in (None, {}, {'findings': [{'id': 'F1'}], 'resolutions': [
                            {'finding_id': 'F1', 'summary': 'Fixed retry failure coverage.'}]}):
                        with self.subTest(strategy=strategy, kind=kind, requirement=requirement, feedback=feedback):
                            self.assert_review_policy(strategy, kind, requirement, feedback)

    def assert_review_policy(self, strategy, kind, requirement, feedback):
        prompt = review_handoff(
            replace(ISSUE, description=requirement),
            Path('/repo'), self.workspace(), 'main', SimpleNamespace(as_dict=lambda: {}),
            'DEV-7-R1', kind, AgentOptions('codex', 'model', 'high'), 'pass', Path('/tmp/result.json'),
            validation=ReviewValidationConfig(strategy), loop_feedback=feedback)
        instructions = ' '.join(prompt.split('RESOLVED REVIEW METADATA')[0].split())
        for text in ('Inspect the full diff and requirements', 'meaningful assertions and missing behavioral',
                     'failure-path, and invariant coverage', 'do not discourage thorough test creation',
                     'complete batch of substantive actionable findings', 'Do not stop at the first defect',
                     'Run targeted tests, including adversarial checks',
                     'Complete checks explicitly required locally or on this pass',
                     'external CI cannot replace those obligations', 'report blocked rather than clean',
                     'Merge-time requirements may be satisfied externally where permitted',
                     'remain pending until evidenced', 'Never assume CI exists',
                     'surface pending final validation to the human',
                     'tracked or untracked Git-visible task state', 'not_run', 'Never report an unrun check as passed',
                     'source and applicable revision', 'clean independent code review is not CI approval',
                     'carry pending limitations into the summary and publication.validation',
                     'same reviewer rechecks fixes and regressions'):
            self.assertIn(text, instructions)
        self.assertNotIn('When otherwise clean, run all final validation', instructions)
        encoded = prompt.split('LATEST LINEAR REQUIREMENTS (context data)\n')[1].split('\n\nEND OF LINEAR CONTEXT')[0]
        self.assertEqual(json.loads(encoded)['description'], requirement)
        if strategy == 'focused_first':
            self.assertIn('skip expensive full-suite validation', instructions)
            self.assertIn('Use relevant focused checks on clean passes too', instructions)
            self.assertIn('do not escalate to a full suite merely because review is otherwise clean', instructions)
            self.assertIn('Continue inspecting for other defects', instructions)
            self.assertIn('explicitly required locally or on every pass', instructions)
        else:
            self.assertIn('full validation on every review pass, even with actionable findings', instructions)
            self.assertNotIn('skip expensive full-suite validation', instructions)
            self.assertNotIn('Use relevant focused checks on clean passes too', instructions)
        if kind == 'resumed':
            self.assertIn('YOUR existing conversation', instructions)
        if feedback is not None:
            self.assertIn(json.dumps(feedback), prompt)

    def test_existing_result_contracts_preserve_pending_checks_and_reject_failed_completion(self):
        checks = [dict(name='Focused retry tests', result='passed', details='python -m unittest tests.test_retry: 8 passed'),
                  dict(name='Full regression', result='not_run',
                       details='Pending before merge; no external CI gate established. Human verification required.')]
        implementation = dict(pass_id='pass', state='completed', summary='Focused validation complete; regression pending.',
                              checks=checks, resolutions=[])
        review = dict(pass_id='pass', state='clean', summary='No findings; full regression pending human verification.',
                      findings=[], checks=checks,
                      publication=dict(summary='fix retry limits', description='Enforce the configured retry limit.',
                                       validation='Focused tests passed; full regression pending human verification.'))
        self.assertEqual(parse_implementation(json.dumps(implementation), 'pass', []), implementation)
        self.assertEqual(parse_verdict(json.dumps(review), 'pass'), review)
        for result in ('failed', 'unknown'):
            with self.subTest(result=result):
                checks[1]['result'] = result
                with self.assertRaises(TaskError):
                    parse_implementation(json.dumps(implementation), 'pass', [])
                with self.assertRaises(TaskError):
                    parse_verdict(json.dumps(review), 'pass')


if __name__ == '__main__':
    unittest.main()
