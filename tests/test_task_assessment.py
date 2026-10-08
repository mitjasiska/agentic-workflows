"""Readiness contracts and configuration, without live model execution."""

from dataclasses import replace
from contextlib import contextmanager
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from task_start import TaskError
from task_start.config import IssueStructureConfig, TaskAssessmentConfig, load_local
from task_start.handoff import implementation_handoff
from task_start.implementation_pass import parse_implementation
from task_start.workspace import Workspace
from test_task_start import ISSUE


def assessment(state='ready'):
    return dict(state=state, summary='Enough context.' if state == 'ready' else 'Retention policy is missing.',
                questions=[] if state == 'ready' else ['How long should imported records be retained?'])


class TaskAssessmentTests(unittest.TestCase):
    def test_toml_defaults_and_boolean_override(self):
        for section, enabled in (
            ('', True), ('[implementation]', True), ('[implementation.task_assessment]', True),
            ('[implementation.task_assessment]\nenabled = true', True),
            ('[implementation.task_assessment]\nenabled = false', False),
        ):
            with self.subTest(section=section), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / 'config.toml'
                path.write_text(f'projects_root = {json.dumps(temp)}\n[linear]\napi_key = "test-placeholder"\n{section}\n')
                self.assertEqual(load_local(path).task_assessment, TaskAssessmentConfig(enabled))

    def test_invalid_configuration_is_private_and_no_agent_skips_it(self):
        values = [None, False, '', [], {'enabled': True}, {'unknown': {}}]
        values += [dict(task_assessment=value) for value in (None, False, '', [], {'unknown': True})]
        values += [dict(task_assessment=dict(enabled=value)) for value in (None, 'false', 0, 1, [], {})]
        for value in values:
            with self.subTest(value=value), patch('task_start.config.read_toml', return_value=dict(
                    projects_root='/projects', linear=dict(api_key='test-placeholder'), implementation=value)):
                with self.assertRaisesRegex(TaskError, 'implementation') as caught:
                    load_local()
                self.assertNotIn('test-placeholder', str(caught.exception))
                self.assertEqual(load_local(no_agent=True).task_assessment, TaskAssessmentConfig())

    def test_shared_handoff_preserves_description_and_bounded_assessment(self):
        workspace = Workspace('dev-7-task', Path('/task'), 'workspace', 'tab', 'pane', 'existing')
        for description in ('Add --version to the CLI; verify its output.\r\nKeep existing flags.\n',
                            'Objective first.\n+++ Agent instructions\nGuidance\n+++\nConstraints last.'):
            for enabled in (True, False):
                with self.subTest(description=description, enabled=enabled):
                    prompt = implementation_handoff(replace(ISSUE, description=description), workspace,
                                                    assessment=TaskAssessmentConfig(enabled))
                    self.assertIn(f'Task:\n{description}\n\nWorkflow-owned implementation instructions:', prompt)
                    self.assertIn('Do not connect to Linear', prompt)
                    self.assertIn('Do not commit, push, merge, or open a PR', prompt)
                    if enabled:
                        for text in ('TASK READINESS ASSESSMENT', 'objective/outcome', 'scope and constraints',
                                     'completion can be verified', 'feasibility', 'blocking ambiguity',
                                     'before implementation mutations', 'same agent/session',
                                     'Do not require', 'Agent instructions', 'full suite',
                                     '"state": "ready"', '"state": "blocked"', '"questions"'):
                            self.assertIn(text, prompt)
                    else:
                        self.assertNotIn('TASK READINESS ASSESSMENT', prompt)
                        self.assertNotIn('task_assessment', prompt)
                        self.assertIn('Skip the explicit assessment', prompt)
                        self.assertIn('overriding any assessment instruction from earlier turns', prompt)

    def result(self, **changes):
        return dict(pass_id='pass', state='completed', summary='Implemented.', checks=[], resolutions=[], **changes)

    def test_ready_and_blocked_results_and_disabled_legacy_contract(self):
        for state in ('ready', 'blocked'):
            value = self.result(task_assessment=assessment(state))
            if state == 'blocked':
                value['state'] = 'blocked'
            self.assertEqual(parse_implementation(json.dumps(value), 'pass', [], assessment_enabled=True), value)
        legacy = self.result()
        self.assertEqual(parse_implementation(json.dumps(legacy), 'pass', [], assessment_enabled=False), legacy)
        with self.assertRaises(TaskError):
            parse_implementation(json.dumps(legacy), 'pass', [], assessment_enabled=True)
        # A genuine execution failure can occur before readiness was assessed.
        legacy['state'] = 'failed'
        self.assertEqual(parse_implementation(json.dumps(legacy), 'pass', [], assessment_enabled=True), legacy)

    def test_malformed_or_inconsistent_assessments_fail_closed(self):
        invalid = [None, [], {}, dict(assessment(), state='unknown'), dict(assessment(), summary=' '),
                   dict(assessment(), questions=['Unneeded question?']), dict(assessment('blocked'), questions=[]),
                   dict(assessment('blocked'), questions=[' ']), dict(assessment('blocked'), questions='Question?'),
                   dict(assessment(), extra=True)]
        for item in invalid:
            with self.subTest(item=item), self.assertRaises(TaskError):
                parse_implementation(json.dumps(self.result(task_assessment=item)), 'pass', [], assessment_enabled=True)
        with self.assertRaises(TaskError):
            parse_implementation(json.dumps(self.result(task_assessment=assessment('blocked'))), 'pass', [],
                                 assessment_enabled=True)
        with self.assertRaises(TaskError):
            parse_implementation(json.dumps(self.result(task_assessment=assessment())), 'pass', [], assessment_enabled=False)


class TaskAssessmentLifecycleTests(unittest.TestCase):
    """Controlled agent outcomes through actual start/loop orchestration."""

    @contextmanager
    def fixture(self, loop_owned):
        from test_loop_bootstrap import BootstrapLoopTests
        from test_task_start import OrchestrationTests
        case = BootstrapLoopTests() if loop_owned else OrchestrationTests()
        try:
            case.setUp()
            yield case
        finally:
            case.doCleanups()

    def test_start_loop_enabled_disabled_and_structure_policy_parity(self):
        from task_start import cli
        from test_task_start import LOCAL
        for loop_owned in (False, True):
            for enabled in (True, False):
                for mode in ('required', 'warn', 'ignore'):
                    for structured in (False, True):
                        with self.subTest(loop=loop_owned, enabled=enabled, mode=mode, structured=structured), \
                                self.fixture(loop_owned) as case:
                            description = 'Add --version to the CLI; verify its output.\r\nKeep existing flags.\n'
                            if structured:
                                description += '+++ Build notes\nUse the existing argument parser.\n+++\n'
                            issue = replace(ISSUE, description=description)
                            case.linear.get_issue.return_value = issue
                            local = replace(case.local if loop_owned else LOCAL,
                                            issue_structure=IssueStructureConfig(mode, 'Build notes'),
                                            task_assessment=TaskAssessmentConfig(enabled))
                            case.local = local
                            with patch('task_start.cli.load_local', return_value=local), \
                                    patch('sys.stderr', new_callable=io.StringIO) as stderr:
                                run = case.run_loop if loop_owned else lambda: cli.start('DEV-7')
                                if mode == 'required' and not structured:
                                    with self.assertRaisesRegex(TaskError, 'mode=required'):
                                        run()
                                    case.linear.start.assert_not_called()
                                    if loop_owned:
                                        self.assertEqual((case.launches, case.preparation), ([], []))
                                    else:
                                        case.git.update_base.assert_not_called()
                                        case.agent.launch.assert_not_called()
                                    continue
                                result = run()
                            if loop_owned:
                                self.assertEqual(result.state, 'clean', result.render())
                                self.assertEqual((len(case.launches), len(case.impl_prompts), len(case.prompts)), (1, 1, 1))
                                self.assertEqual((result.data['passes'], result.data['reviews']), (2, 1))
                                execution = case.launches[0]
                                record = result.data['implementation'][0]
                                self.assertEqual('task_assessment' in record, enabled)
                                if enabled:
                                    self.assertEqual(record['task_assessment']['state'], 'ready')
                            else:
                                case.agent.launch.assert_called_once()
                                execution = case.agent.launch.call_args.args[0]
                            shared = implementation_handoff(issue, execution.workspace, assessment=local.task_assessment)
                            self.assertTrue(execution.handoff.startswith(shared))
                            self.assertEqual('TASK READINESS ASSESSMENT' in shared, enabled)
                            self.assertIn(f'Task:\n{description}\n\nWorkflow-owned implementation instructions:', shared)
                            self.assertEqual('warning:' in stderr.getvalue(), mode == 'warn' and not structured)

    def test_blocked_stops_unchanged_and_new_after_clarification_reuses_session(self):
        from task_start.loop import loop
        with self.fixture(True) as case:
            case.linear.get_issue.return_value = replace(ISSUE, description='Change retention behavior.')
            case.impl_overrides = dict(state='blocked', task_assessment=assessment('blocked'), summary='Missing decision.')
            result = case.run_loop()
            self.assertEqual(result.state, 'escalated', result.render())
            self.assertEqual((result.data['passes'], result.data['reviews']), (1, 0))
            self.assertIsNone(result.data['active_pass'])
            self.assertIsNone(result.data['next_phase'])
            self.assertIn(assessment('blocked')['questions'][0], result.render())
            self.assertEqual(result.data['implementation'][0]['task_assessment'], assessment('blocked'))
            self.assertEqual(loop('DEV-7', action='status').data, result.data)
            self.assertEqual(case.command(case.path, 'status', '--porcelain'), '')
            self.assertEqual(case.command(case.path, 'rev-parse', 'HEAD'), case.before)
            self.assertEqual((len(case.launches), len(case.impl_prompts), len(case.prompts)), (1, 1, 0))
            previous = case.store.read()[0]['implementation']
            with self.assertRaisesRegex(TaskError, 'Only a paused'):
                case.continue_loop()
            # The user clarifies Linear; the workflow fetches that snapshot on --new.
            case.linear.get_issue.return_value = replace(ISSUE, description='Retain imported records for 30 days. Verify expiration.')
            case.impl_overrides = dict(task_assessment=assessment())
            result = case.run_loop(action='new')
            self.assertEqual(result.state, 'clean', result.render())
            self.assertEqual(case.store.read()[0]['implementation'], previous)
            self.assertEqual((len(case.launches), len(case.impl_prompts), len(case.prompts)), (1, 2, 1))
            self.assertIn('Continue in YOUR existing implementation conversation', case.impl_prompts[-1])
            self.assertIn(case.linear.get_issue.return_value.description, case.impl_prompts[-1])

    def test_blocked_mutation_and_malformed_outcomes_never_advance_to_review(self):
        for malformed in (False, True):
            with self.subTest(malformed=malformed), self.fixture(True) as case:
                value = assessment('blocked')
                if malformed:
                    value['questions'] = []
                else:
                    case.on_implementation = lambda *_: (case.path / 'unexpected.txt').write_text('unexpected edit')
                case.impl_overrides = dict(state='blocked', task_assessment=value)
                result = case.run_loop()
                self.assertEqual(result.state, 'escalated', result.render())
                self.assertEqual((len(case.launches), len(case.impl_prompts), len(case.prompts)), (1, 1, 0))
                self.assertIn('Malformed' if malformed else 'Checkout changed during blocked task assessment', result.data['reason'])


    def test_disabled_continuation_skips_assessment_and_checkpoint_evidence_is_validated(self):
        import copy
        from task_start.loop_state import LoopStore
        with self.fixture(True) as case:
            case.on_implementation = lambda *_: case.pause()
            result = case.run_loop()
            self.assertEqual(result.state, 'paused', result.render())
            state, pause = case.store.read()
            # Checkpoints from before readiness support remain readable.
            legacy = copy.deepcopy(state)
            del legacy['records'][0]['task_assessment']
            self.assertEqual(LoopStore.decode((json.dumps(legacy), pause))[0], legacy)
            invalid = copy.deepcopy(state)
            invalid['records'][0]['task_assessment'] = assessment('blocked')
            with self.assertRaisesRegex(TaskError, 'Malformed loop checkpoint'):
                LoopStore.decode((json.dumps(invalid), pause))
            # Pause recovery does not replay the ready implementation turn.
            case.local = replace(case.local, task_assessment=TaskAssessmentConfig(False))
            case.on_implementation = None
            result = case.continue_loop()
            self.assertEqual(result.state, 'clean', result.render())
            self.assertEqual((len(case.launches), len(case.impl_prompts), len(case.prompts)), (1, 1, 1))
            # A later handoff explicitly overrides earlier enabled guidance.
            result = case.run_loop(action='new')
            self.assertEqual(result.state, 'clean', result.render())
            self.assertNotIn('task_assessment', result.data['implementation'][0])
            self.assertIn('Skip the explicit assessment', case.impl_prompts[-1])
            self.assertNotIn('Also include task_assessment', case.impl_prompts[-1])
            self.assertEqual(len(case.launches), 1)


if __name__ == '__main__':
    unittest.main()
