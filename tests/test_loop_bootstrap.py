"""Initial loop launches with real disposable Git/checkpoint/registry boundaries."""

from dataclasses import replace
import io
import json
import re
from types import SimpleNamespace
from pathlib import Path
import unittest
from unittest.mock import patch

from task_start import TaskError, cli
from task_start.agent import AgentOptions, Codex, LaunchResult, Pi, adapter_for
from task_start.config import IssueStructureConfig, load_local
from task_start.contexts import ContextRegistry, context_reference
from task_start.handoff import implementation_handoff
from task_start.loop import loop
from task_start.loop_state import LoopStore
from task_start.workspace import Git, Herdr
from test_loop import ImplementationAdapter, finding, findings
import test_loop as loop_fixture
import test_review as fixture
from codex_startup_fixture import TRUST_SCREEN, CodexStartupTransport


UPDATE_BASE = Git.update_base
REMOTE_BRANCHES = Git.remote_branches


class PiInitialTransport:
    """Real Pi launch/status/history verification with controlled Herdr reports."""

    def __init__(self, test, *, history_at):
        self.test, self.history_at = test, history_at
        self.path = test.repo.parent / 'initial-pi.jsonl'
        self.reference = dict(agent='pi', kind='path', value=str(self.path), source='hook')
        self.now = self.gets = self.polls = 0
        self.on_get = lambda: None
        self.saved_identities = []
        test.enterContext(patch('task_start.implementation_pass.adapter_for', side_effect=Pi))
        test.enterContext(patch.object(Pi, 'check_available'))
        test.enterContext(patch.object(Pi, 'model_mode_capabilities', return_value=('implementer', 'low', ('low',))))
        self.command = test.enterContext(patch.object(Pi, 'command', side_effect=self.herdr))
        test.enterContext(patch('task_start.implementation_pass.time',
                               SimpleNamespace(monotonic=lambda: self.now, sleep=self.advance)))
        update = test.registry.update
        def record(context_id, **values):
            update(context_id, **values)
            if context_id == 'DEV-7-I1':
                self.saved_identities.append(context_reference(test.registry.get(context_id)))
        test.enterContext(patch.object(test.registry, 'update', side_effect=record))

    def advance(self, seconds):
        self.now += seconds

    def history(self, conversation_id='original-implementation', *, messages=True):
        entries = [dict(type='session', id=conversation_id, cwd=str(self.test.path))]
        if messages:
            entries.append(dict(type='message', message=dict(role='user', content=[])))
        self.path.write_text(''.join(json.dumps(entry) + '\n' for entry in entries))

    def herdr(self, group, operation, *args):
        test = self.test
        pane = test.panes[0]
        if (group, operation) == ('pane', 'list'):
            return dict(panes=[dict(p) for p in test.panes])
        if (group, operation) == ('agent', 'start'):
            state, _ = test.store.read()
            test.assertEqual((state['status'], state['pass_count'], state['review_count']), ('running', 1, 0))
            test.assertEqual(state['active_pass']['context_id'], 'DEV-7-I1')
            test.assertIsNotNone(state['active_pass']['pass_id'])
            test.assertIn('--extension', args)
            self.history(messages=False)
            pane.update(agent='pi', agent_status='idle', foreground_cwd=str(test.path),
                        agent_session=dict(self.reference))
            return dict(agent=dict(pane), argv=['pi', *args[args.index('--') + 1:]])
        if (group, operation) == ('agent', 'prompt'):
            test.assertEqual(args[0], 'p1')
            prompt = args[1]
            test.impl_prompts.append(prompt)
            test.assertIn('fresh conversation', prompt)
            if self.history_at == 'prompt':
                self.history()
            output = Path(re.search(r'Write your result to (.*?)\. This temporary', prompt).group(1))
            pass_id = re.search(r'Pass ID: ([^\n]+)', prompt).group(1)
            output.write_text(json.dumps(dict(pass_id=pass_id, state='completed', summary='Implemented',
                                              checks=[], resolutions=[])))
            pane['agent_status'] = 'working'
            return dict(agent=dict(pane))
        if (group, operation) == ('agent', 'get'):
            self.gets += 1
            if self.gets >= 3:  # First two reads confirm startup and prompt delivery.
                self.polls += 1
                if self.polls == 1 and self.history_at == 'poll':
                    self.history()
                pane['agent_status'] = 'done' if self.polls >= 3 else 'working'
            self.on_get()
            return dict(agent=dict(pane))
        test.fail((group, operation, args))

    def assert_identity_retained(self):
        expected = dict(self.reference, conversation_id='original-implementation')
        context = self.test.registry.get('DEV-7-I1')
        self.test.assertEqual(context_reference(context), expected)
        self.test.assertEqual(context['resumability'], 'yes')
        state, _ = self.test.store.read()
        self.test.assertEqual(state['implementation']['session']['conversation_id'], expected['conversation_id'])
        verified = [i for i, ref in enumerate(self.saved_identities) if ref and 'conversation_id' in ref]
        self.test.assertTrue(verified, 'Pi history must be verified')
        for ref in self.saved_identities[verified[0]:]:
            self.test.assertEqual(ref, expected, 'No later persistence update may erase verified identity')
        effects = [call.args[:2] for call in self.command.call_args_list]
        self.test.assertEqual(effects.count(('agent', 'start')), 1)
        self.test.assertEqual(effects.count(('agent', 'prompt')), 1)


class InitialAdapter(ImplementationAdapter):
    def launch(self, execution):
        test = self.test
        state, _ = test.store.read()
        test.assertEqual((state['status'], state['pass_count'], state['review_count']), ('running', 1, 0))
        test.assertEqual(state['active_pass']['context_id'], 'DEV-7-I1')
        test.assertIsNotNone(state['active_pass']['pass_id'])
        test.assertIn('fresh conversation', execution.handoff)
        test.assertNotIn('Continue in YOUR existing', execution.handoff)
        test.launches.append(execution)
        pane = test.panes[0]
        pane.update(agent=execution.options.kind,
                    agent_session=dict(agent='pi', kind='path', value='/implementation.jsonl'))
        execution.runtime_observer(dict(herdr_session=json.dumps(pane['agent_session'])))
        # Session identity is retained before delivery can become uncertain.
        state, _ = test.store.read()
        test.assertEqual(state['implementation']['session']['conversation_id'], 'original-implementation')
        if test.launch_error:
            raise test.launch_error
        self.resume(execution, pane['agent_session'], recreate=False)
        return LaunchResult(execution.options.kind, pane['pane_id'], 'Initial task delivered',
                            '/implementation.jsonl', session_kind='path', resumability='yes')


class BootstrapLoopTests(unittest.TestCase):
    command = loop_fixture.LoopIntegrationTests.command
    pane_command = loop_fixture.LoopIntegrationTests.pane_command
    run_loop = loop_fixture.LoopIntegrationTests.run_loop
    pause = loop_fixture.LoopIntegrationTests.pause
    continue_loop = loop_fixture.LoopIntegrationTests.continue_loop

    def select_adapter(self, options):
        # Validate supported options even with controlled agent transport.
        adapter_for(options)
        self.options.append(options)
        return self.implementer if options.kind == 'pi' else self.adapter

    def setUp(self):
        loop_fixture.LoopIntegrationTests.setUp(self)
        # Use a clean registry, shell and untouched issue; the earlier fixture's
        # registry remains in its disposable directory and is never consulted.
        self.registry = ContextRegistry(self.repo.parent / 'initial-contexts.sqlite3')
        self.enterContext(patch('task_start.loop.ContextRegistry', return_value=self.registry))
        self.panes[0].update(agent=None, agent_session=None, label='Shell')
        self.implementer = InitialAdapter(self)
        self.enterContext(patch('task_start.implementation_pass.adapter_for', side_effect=self.implementation_adapter))
        self.linear.get_issue.return_value = fixture.ISSUE
        self.launches, self.preparation = [], []
        self.launch_error = None
        self.base_error = self.workspace_error = False
        self.command(self.repo, 'worktree', 'remove', str(self.path))
        self.command(self.repo, 'branch', '-d', self.branch)
        self.branch = 'dev-7-add-ingestion-cli'
        self.enterContext(patch.object(Git, 'update_base', lambda git, branch: self.update_base(git, branch)))
        self.enterContext(patch.object(Git, 'remote_branches', REMOTE_BRANCHES))
        self.history = self.enterContext(patch.object(Git, 'check_history'))
        self.linear.start.side_effect = lambda _: self.preparation.append('linear')
        # LoopStore computes the private path only after Herdr creates the tree.
        self.store = None

    def implementation_adapter(self, options):
        adapter_for(options)
        return self.implementer

    def update_base(self, git, branch):
        self.preparation.append('base')
        if self.base_error:
            raise TaskError('Base preparation failed')
        UPDATE_BASE(git, branch)

    def worktrees(self, operation, *args):
        if operation == 'list':
            return loop_fixture.LoopIntegrationTests.worktrees(self, operation, *args)
        self.preparation.append(operation)
        if self.workspace_error:
            raise TaskError('Workspace preparation failed')
        if operation == 'create':
            self.assertEqual(args, ('--base', 'main', '--branch', self.branch, '--label', 'DEV-7', '--focus'))
            self.command(self.repo, 'worktree', 'add', '-b', self.branch, str(self.path), 'main')
        else:
            self.assertEqual(operation, 'open')
        self.store = LoopStore(self.path)
        tree = dict(path=str(self.path), branch=self.branch, is_linked_worktree=True,
                    is_bare=False, is_detached=False, is_prunable=False)
        return dict(worktree=tree,
                    workspace=dict(workspace_id='w1', focused=True,
                        worktree=dict(repo_root=str(self.repo), checkout_path=str(self.path))),
                    tab=dict(workspace_id='w1', tab_id='t1'),
                    root_pane=dict(workspace_id='w1', tab_id='t1', pane_id='p1'))

    def prepared_workspace(self, scope=None):
        self.command(self.repo, 'worktree', 'add', '-b', self.branch, str(self.path))
        self.git.save_scope(self.path, self.branch, 'DEV-7', scope)
        self.store = LoopStore(self.path)

    def pause_before_launch(self):
        original = LoopStore.begin
        def pause(store, state):
            store.pause()
            return original(store, state)
        with patch.object(LoopStore, 'begin', pause):
            result = self.run_loop()
        self.assertEqual(result.state, 'paused', result.render())
        self.assertEqual(result.data['next_phase'], 'initial_implementation')
        self.assertEqual((result.data['passes'], result.data['reviews']), (0, 0))
        self.assertEqual(self.launches, [])
        return result

    def test_untouched_issue_initial_handoff_then_fresh_review(self):
        self.local = replace(self.local, issue_structure=IssueStructureConfig('required'))
        result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual(self.preparation, ['base', 'create', 'linear'])
        self.assertEqual((len(self.launches), len(self.impl_prompts), len(self.prompts)), (1, 1, 1))
        self.assertEqual((result.data['passes'], result.data['reviews']), (2, 1))
        self.assertEqual((result.data['implementation_context'], result.data['reviewer_context']), ('DEV-7-I1', 'DEV-7-R1'))
        self.assertEqual(self.launches[0].options, AgentOptions('pi', 'implementer', 'low'))
        self.assertIn(fixture.ISSUE.description, self.impl_prompts[0])
        self.assertIn('fresh independent review', self.prompts[0])
        self.assertNotIn('Implementation completed and validated', self.prompts[0])
        state, _ = self.store.read()
        self.assertEqual(state['version'], 3)
        self.assertEqual([r['phase'] for r in state['records']], ['initial_implementation', 'review'])
        self.assertEqual(self.command(self.path, 'rev-parse', 'HEAD'), self.before)
        self.assertNotIn(fixture.ISSUE.description, self.store.path.read_bytes().decode(errors='ignore'))

    def assert_structure_handoff(self, description, *, warning):
        issue = replace(fixture.ISSUE, description=description)
        self.linear.get_issue.return_value = issue
        with patch('sys.stderr', new_callable=io.StringIO) as stderr:
            result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual(self.preparation, ['base', 'create', 'linear'])
        if warning:
            self.assertIn('warning: Linear issue DEV-7', stderr.getvalue())
            self.assertIn(repr(self.local.issue_structure.block_name), stderr.getvalue())
            self.assertIn('Continuing agent execution', stderr.getvalue())
            self.assertEqual(stderr.getvalue().count('warning:'), 1)
        else:
            self.assertEqual(stderr.getvalue(), '')
        execution = self.launches[0]
        shared_handoff = implementation_handoff(issue, execution.workspace)
        self.assertTrue(execution.handoff.startswith(shared_handoff))
        self.assertIn(f'Task:\n{description}\n\nWorkflow-owned implementation instructions:', shared_handoff)
        self.assertIn('Users may organize the issue description however they choose', shared_handoff)
        self.assertEqual(execution.issue.description, description)

    def test_default_warn_accepts_ordinary_issue_and_preserves_full_description(self):
        self.assert_structure_handoff('Build ingestion.\r\n  Constraints α anywhere.\r\n', warning=True)

    def test_ignore_accepts_empty_issue_without_inspecting_structure(self):
        self.local = replace(self.local, issue_structure=IssueStructureConfig('ignore'))
        self.assert_structure_handoff('', warning=False)

    def test_custom_required_block_preserves_complete_description(self):
        self.local = replace(self.local, issue_structure=IssueStructureConfig('required', 'Build notes (α)'))
        self.assert_structure_handoff('Requirements first.\r\n>>> Build notes (α) \t\r\nGuidance\r\n>>>\r\nMore constraints.',
                                      warning=False)

    def test_custom_warn_does_not_treat_default_block_as_configured_block(self):
        self.local = replace(self.local, issue_structure=IssueStructureConfig('warn', 'Build notes'))
        self.assert_structure_handoff(fixture.ISSUE.description, warning=True)

    def test_invalid_structure_configuration_fails_before_preparation(self):
        with patch('task_start.config.read_toml', return_value=dict(projects_root='/projects',
                linear=dict(api_key='test-placeholder', issue_structure=dict(mode='ignore', block_name='')))), \
                patch('task_start.loop.load_local', side_effect=load_local):
            with self.assertRaisesRegex(TaskError, 'linear.issue_structure.block_name'):
                self.run_loop()
        self.linear.get_issue.assert_not_called()
        self.assertEqual(self.preparation, [])
        self.linear.start.assert_not_called()
        self.assertEqual(self.registry.list(), [])
        self.assertFalse(self.path.exists())

    def codex_transport(self):
        self.local = replace(self.local, agent=fixture.AgentConfig('codex', 'initial-codex', 'high'))
        self.enterContext(patch('task_start.implementation_pass.adapter_for', side_effect=Codex))
        transport = CodexStartupTransport(self, lambda: self.panes)
        self.enterContext(patch('task_start.implementation_pass.time',
                                SimpleNamespace(monotonic=lambda: transport.now, sleep=transport.advance)))
        return transport

    def test_pi_path_only_confirmation_after_verification_reaches_fresh_review(self):
        transport = PiInitialTransport(self, history_at='prompt')
        result = loop('DEV-7', timeout=2)
        self.assertEqual(result.state, 'clean', result.render())
        transport.assert_identity_retained()
        self.assertGreaterEqual(transport.polls, 3)
        self.assertEqual((result.data['passes'], result.data['reviews']), (2, 1))
        self.assertEqual((len(self.impl_prompts), len(self.prompts)), (1, 1))
        self.assertIn('fresh independent review', self.prompts[0])

    def test_pi_path_only_polls_after_delayed_verification_reach_fresh_review(self):
        transport = PiInitialTransport(self, history_at='poll')
        result = loop('DEV-7', timeout=2)
        self.assertEqual(result.state, 'clean', result.render())
        transport.assert_identity_retained()
        self.assertGreaterEqual(transport.polls, 3)
        self.assertEqual((result.data['passes'], result.data['reviews']), (2, 1))
        self.assertEqual((len(self.impl_prompts), len(self.prompts)), (1, 1))
        self.assertIn('fresh independent review', self.prompts[0])

    def test_pi_uncertain_prompt_confirmation_keeps_verified_identity_without_replay(self):
        transport = PiInitialTransport(self, history_at='prompt')
        def uncertain():
            if transport.gets == 2:
                raise TaskError('Confirmation unavailable')
        transport.on_get = uncertain
        result = loop('DEV-7', timeout=2)
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertIn('Confirmation unavailable', result.data['reason'])
        self.assertEqual(self.registry.get('DEV-7-I1')['state'], 'uncertain')
        self.assertEqual(self.prompts, [])
        for kwargs in ({}, dict(action='new'), dict(action='continue')):
            with self.assertRaises(TaskError):
                loop('DEV-7', **kwargs)
        transport.assert_identity_retained()

    def test_pi_replaced_history_after_path_only_polls_refuses_review(self):
        transport = PiInitialTransport(self, history_at='poll')
        def replace_history():
            if transport.polls == 3:
                transport.history('replacement')
        transport.on_get = replace_history
        result = loop('DEV-7', timeout=2)
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertIn('matching conversation identity', result.data['reason'])
        self.assertEqual(self.prompts, [])
        transport.assert_identity_retained()

    def test_pi_conflicting_runtime_after_verification_preserves_original_binding(self):
        for changes in (dict(terminal_id='replacement'),
                        dict(agent_session=dict(agent='pi', kind='path', value='/replacement.jsonl')),
                        dict(conversation_id='replacement')):
            with self.subTest(changes=changes):
                case = BootstrapLoopTests()
                case.setUp()
                try:
                    transport = PiInitialTransport(case, history_at='poll')
                    def conflict():
                        if transport.polls == 2:
                            if 'conversation_id' in changes:
                                case.panes[0]['agent_session'].update(changes)
                            else:
                                case.panes[0].update(changes)
                    transport.on_get = conflict
                    result = loop('DEV-7', timeout=2)
                    self.assertEqual(result.state, 'escalated', result.render())
                    self.assertEqual(case.prompts, [])
                    transport.assert_identity_retained()
                finally:
                    case.doCleanups()

    def test_codex_initial_structured_task_is_queued_once_before_fresh_review(self):
        transport = self.codex_transport()
        def deliver(prompt):
            self.impl_prompts.append(prompt)
            state, _ = self.store.read()
            self.assertEqual(state['status'], 'running')
            self.assertEqual(state['implementation']['session']['value'], transport.thread_id)
            output = Path(re.search(r'Write your result to (.*?)\. This temporary', prompt).group(1))
            pass_id = re.search(r'Pass ID: ([^\n]+)', prompt).group(1)
            (self.path / 'implemented.txt').write_text('initial implementation')
            output.write_text(json.dumps(dict(pass_id=pass_id, state='completed', summary='Implemented',
                                              checks=[], resolutions=[])))
        transport.on_queue = deliver
        result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual((len(self.impl_prompts), len(self.prompts)), (1, 1))
        self.assertIn('fresh conversation', self.impl_prompts[0])
        transport.assert_effects(1, 1)
        self.assertEqual(self.store.read()[0]['implementation']['session']['value'], transport.thread_id)

    def test_initial_timeout_budget_starts_after_bounded_startup(self):
        transport = self.codex_transport()
        result = loop('DEV-7', timeout=0.5)
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertIn('Timed out waiting for validated implementation', result.data['reason'])
        self.assertEqual(transport.now, transport.queued_at + transport.receipt_delay + 0.5)
        self.assertEqual((result.data['passes'], result.data['reviews']), (1, 0))
        transport.assert_effects(1, 1)
        self.assertEqual(self.prompts, [])

    def test_initial_codex_trust_wait_retains_claim_and_continues_same_pass(self):
        transport = self.codex_transport()
        transport.blocker = TRUST_SCREEN
        pending = []
        def accept():
            state, _ = self.store.read()
            pending.append(state['active_pass']['pass_id'])
            self.assertEqual(state['status'], 'running')
            self.assertEqual(state['implementation'], dict(context_id='DEV-7-I1'))
            self.assertEqual(self.registry.get('DEV-7-I1')['state'], 'awaiting_user')
            transport.assert_effects(1, 0)
            transport.advance(2000)
            transport.accept_setup()
            return '\n'
        def deliver(prompt):
            output = Path(re.search(r'Write your result to (.*?)\. This temporary', prompt).group(1))
            pass_id = re.search(r'Pass ID: ([^\n]+)', prompt).group(1)
            self.assertEqual(pending, [pass_id])
            self.assertTrue(output.parent.is_dir())
            output.write_text(json.dumps(dict(pass_id=pass_id, state='completed', summary='Implemented',
                                              checks=[], resolutions=[])))
        transport.on_queue = deliver
        with patch('task_start.agent.sys.stdin') as stdin, patch('task_start.agent.sys.stderr', new_callable=io.StringIO):
            stdin.isatty.return_value = True
            stdin.readline.side_effect = accept
            result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual((result.data['passes'], result.data['reviews']), (2, 1))
        self.assertEqual(self.store.read()[0]['implementation']['session']['value'], transport.thread_id)
        transport.assert_effects(1, 1)

    def test_codex_uncertain_queue_preserves_observed_identity_and_never_requeues(self):
        transport = self.codex_transport()
        transport.queue_error = TaskError('Queue acknowledgement lost')
        result = self.run_loop()
        self.assertEqual(result.state, 'escalated', result.render())
        state, _ = self.store.read()
        self.assertEqual(state['implementation']['session']['value'], transport.thread_id)
        self.assertEqual(self.registry.get('DEV-7-I1')['state'], 'uncertain')
        self.assertEqual(self.prompts, [])
        for kwargs in ({}, dict(action='continue'), dict(action='new')):
            with self.assertRaises(TaskError):
                loop('DEV-7', **kwargs)
        transport.assert_effects(1, 1)

    def test_initial_codex_stable_session_waits_for_setup_before_reading_history(self):
        transport = self.codex_transport()
        first = dict(agent='codex', kind='id', value=transport.thread_id)
        transport.blocker, transport.start_not_ready = TRUST_SCREEN, False
        transport.start_changes = dict(agent_status='blocked', agent_session=first)
        transport.get_changes = dict(agent_status='blocked', agent_session=first)
        pending, premature_reads, verified_reads = [], [], []
        def request(method, params, **kwargs):
            if method == 'thread/read':
                if transport.blocker:
                    premature_reads.append(params['threadId'])
                    raise TaskError('Provider history is temporarily unreadable during setup')
                if transport.queued_at is None:
                    verified_reads.append((params['threadId'], kwargs.get('timeout')))
            return transport.request(method, params, **kwargs)
        transport.rpc.request.side_effect = request
        def accept():
            state, _ = self.store.read()
            pending.append(state['active_pass']['pass_id'])
            self.assertEqual(state['implementation'], dict(context_id='DEV-7-I1'))
            row = self.registry.get('DEV-7-I1')
            self.assertEqual(context_reference(row), first)
            self.assertEqual((row['state'], row['resumability']), ('awaiting_user', 'unknown'))
            self.assertEqual((row['pane_id'], row['terminal_id']), ('p1', self.panes[0]['terminal_id']))
            self.assertEqual(premature_reads, [])
            transport.assert_effects(1, 0)
            transport.accept_setup()
            transport.get_changes = dict(agent_session=first)
            return '\n'
        def deliver(prompt):
            output = Path(re.search(r'Write your result to (.*?)\. This temporary', prompt).group(1))
            pass_id = re.search(r'Pass ID: ([^\n]+)', prompt).group(1)
            self.assertEqual(pending, [pass_id])
            self.assertTrue(verified_reads)
            for session_id, timeout in verified_reads:
                self.assertEqual(session_id, first['value'])
                self.assertIsNotNone(timeout, 'Pre-delivery history reads must use the reconciliation budget')
                self.assertTrue(0 < timeout <= Codex.POST_TRUST_READY_TIMEOUT)
            state, _ = self.store.read()
            self.assertEqual(state['implementation']['session'], first)
            self.assertTrue(output.parent.is_dir())
            output.write_text(json.dumps(dict(pass_id=pass_id, state='completed', summary='Implemented',
                                              checks=[], resolutions=[])))
        transport.on_queue = deliver
        with patch('task_start.agent.sys.stdin') as stdin, patch('task_start.agent.sys.stderr', new_callable=io.StringIO):
            stdin.isatty.return_value = True
            stdin.readline.side_effect = accept
            result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual((result.data['passes'], result.data['reviews']), (2, 1))
        self.assertEqual(premature_reads, [])
        self.assertEqual(len(pending), 1)
        self.assertEqual(self.store.read()[0]['implementation']['session'], first)
        self.assertEqual([c['context_id'] for c in self.registry.list() if c['role'] == 'implementation'], ['DEV-7-I1'])
        transport.assert_effects(1, 1)

    def test_initial_codex_unreadable_history_after_setup_never_delivers(self):
        transport = self.codex_transport()
        first = dict(agent='codex', kind='id', value=transport.thread_id)
        transport.blocker, transport.start_not_ready = TRUST_SCREEN, False
        transport.start_changes = dict(agent_status='blocked', agent_session=first)
        transport.get_changes = dict(agent_session=first)
        actions = []
        def request(method, params, **kwargs):
            if method == 'thread/read':
                raise TaskError('Provider history is temporarily unreadable')
            return transport.request(method, params, **kwargs)
        transport.rpc.request.side_effect = request
        def accept():
            actions.append(True)
            transport.accept_setup()
            return '\n'
        with patch('task_start.agent.sys.stdin') as stdin, patch('task_start.agent.sys.stderr', new_callable=io.StringIO):
            stdin.isatty.return_value = True
            stdin.readline.side_effect = accept
            result = self.run_loop()
        self.assertEqual(actions, [True])
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertIn('Provider history is temporarily unreadable', result.data['reason'])
        self.assertEqual(context_reference(self.registry.get('DEV-7-I1')), first)
        self.assertEqual(self.registry.get('DEV-7-I1')['resumability'], 'unknown')
        self.assertEqual(self.prompts, [])
        transport.assert_effects(1, 0)

    def test_initial_codex_identity_conflict_after_setup_never_delivers(self):
        transport = self.codex_transport()
        first = dict(agent='codex', kind='id', value=transport.thread_id)
        transport.blocker, transport.start_not_ready = TRUST_SCREEN, False
        transport.start_changes = dict(agent_status='blocked', agent_session=first)
        transport.get_changes = dict(agent_session=first)
        actions = []
        def accept():
            actions.append(True)
            transport.accept_setup()
            transport.get_changes = dict(agent_session=dict(first, value='11a0d314-bd68-7203-8b68-f2520f892afa'))
            return '\n'
        with patch('task_start.agent.sys.stdin') as stdin, patch('task_start.agent.sys.stderr', new_callable=io.StringIO):
            stdin.isatty.return_value = True
            stdin.readline.side_effect = accept
            result = self.run_loop()
        self.assertEqual(actions, [True])
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertIn('session changed', result.data['reason'])
        self.assertEqual(context_reference(self.registry.get('DEV-7-I1')), first)
        self.assertEqual(self.prompts, [])
        transport.assert_effects(1, 0)

    def test_findings_use_original_implementation_and_same_reviewer(self):
        self.review_results = [findings(finding()), {}]
        result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual((result.data['passes'], result.data['reviews']), (4, 2))
        self.assertEqual((len(self.launches), len(self.impl_prompts), len(self.prompts)), (1, 2, 2))
        state, _ = self.store.read()
        self.assertEqual([r['context_id'] for r in state['records']], ['DEV-7-I1', 'DEV-7-R1', 'DEV-7-I1', 'DEV-7-R1'])
        self.assertIn('Continue in YOUR existing', self.impl_prompts[1])
        self.assertIn('"finding_id": "F1"', self.prompts[1])
        self.assertEqual(self.recreated, [False])

    def test_existing_default_workspace_is_reused_and_work_preserved(self):
        self.prepared_workspace()
        (self.path / 'work.txt').write_text('existing work')
        result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual(self.preparation, ['base', 'open', 'linear'])
        self.assertEqual((self.path / 'work.txt').read_text(), 'existing work')
        self.history.assert_called_once_with('main', self.branch, existing=True)

    def test_base_advances_before_fresh_workspace(self):
        fixture.baseline.LocalGitIntegrationTests.advance_remote(self)
        result = self.run_loop()
        self.assertEqual(result.state, 'clean', result.render())
        upstream = self.command(self.remote, 'rev-parse', 'HEAD')
        self.assertEqual(self.command(self.repo, 'rev-parse', 'HEAD'), upstream)
        self.assertEqual(self.command(self.path, 'rev-parse', 'HEAD'), upstream)

    def test_initial_outcomes_fail_closed_without_review(self):
        for outcome in ('blocked', 'failed', 'malformed', 'timeout', 'failed_check', 'busy'):
            with self.subTest(outcome=outcome):
                case = BootstrapLoopTests()
                case.setUp()
                try:
                    if outcome in ('blocked', 'failed'):
                        case.impl_overrides = dict(state=outcome)
                    elif outcome in ('malformed', 'timeout'):
                        case.impl_raw = '{}' if outcome == 'malformed' else 'missing'
                    elif outcome == 'busy':
                        case.impl_status = 'working'
                    else:
                        case.impl_overrides = dict(checks=[dict(name='tests', result='failed', details='failure')])
                    result = case.run_loop()
                    self.assertEqual(result.state, 'escalated', result.render())
                    self.assertEqual((result.data['passes'], result.data['reviews']), (1, 0))
                    self.assertEqual(case.prompts, [])
                    self.assertEqual(len(case.launches), 1)
                    with self.assertRaisesRegex(TaskError, 'Only a paused'):
                        case.continue_loop()
                    self.assertEqual(loop('DEV-7', action='status').data, result.data)
                finally:
                    case.doCleanups()

    def test_preparation_failures_do_not_launch(self):
        for failure in ('base', 'workspace', 'linear'):
            with self.subTest(failure=failure):
                case = BootstrapLoopTests()
                case.setUp()
                try:
                    case.base_error = failure == 'base'
                    case.workspace_error = failure == 'workspace'
                    if failure == 'linear':
                        case.linear.start.side_effect = TaskError('Linear update failed')
                    with self.assertRaises(TaskError):
                        case.run_loop()
                    if failure != 'linear':
                        case.linear.start.assert_not_called()
                    else:
                        self.assertTrue(case.path.is_dir())
                    self.assertEqual(case.launches, [])
                    self.assertEqual(case.registry.list(), [])
                    if case.store:
                        self.assertFalse(case.store.path.exists())
                finally:
                    case.doCleanups()

    def test_preflight_refusals_before_base_workspace_linear_or_allocation(self):
        original = self.local
        for changes, overrides in (
            (dict(agent=fixture.AgentConfig('pi', None, 'low')), {}),
            (dict(agent=fixture.AgentConfig('pi', 'model', None)), {}),
            (dict(reviewer=fixture.AgentConfig('codex', None, 'high')), {}),
            ({}, dict(impl_model='bad model')),
            ({}, dict(impl_mode='unsupported')),
            ({}, dict(impl_agent_kind='unknown')),
        ):
            with self.subTest(changes=changes, overrides=overrides):
                self.local = replace(original, **changes)
                with self.assertRaises(TaskError):
                    self.run_loop(**overrides)
        self.local = original
        with patch.object(self.implementer, 'check_available', side_effect=TaskError('Agent unavailable')):
            with self.assertRaisesRegex(TaskError, 'unavailable'):
                self.run_loop()
        self.linear.get_issue.return_value = replace(fixture.ISSUE, description='No guidance')
        for name in ('Agent instructions', 'Build notes'):
            with self.subTest(block_name=name):
                self.local = replace(original, issue_structure=IssueStructureConfig('required', name))
                with self.assertRaisesRegex(TaskError, f'{name}.*mode=required'):
                    self.run_loop()
        self.assertEqual(self.preparation, [])
        self.linear.start.assert_not_called()
        self.assertEqual(self.registry.list(), [])
        self.assertFalse(self.path.exists())

    def test_plain_loop_cli_uses_both_configured_roles(self):
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            self.assertEqual(cli.main(['loop', 'DEV-7']), 0)
        self.assertIn('DEV-7 loop: clean', output.getvalue())
        self.assertEqual(self.launches[0].options, AgentOptions('pi', 'implementer', 'low'))
        self.assertEqual(self.store.read()[0]['reviewer_options'],
                         dict(kind='codex', model='review-model', mode='high'))

    def test_cli_partial_overrides_inherit_remaining_role_settings(self):
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            self.assertEqual(cli.main(['loop', 'DEV-7', '--i-model', 'initial-override',
                                       '--r-mode', 'medium']), 0)
        self.assertIn('DEV-7 loop: clean', output.getvalue())
        self.assertEqual(self.launches[0].options, AgentOptions('pi', 'initial-override', 'low'))
        self.assertEqual(self.store.read()[0]['reviewer_options'],
                         dict(kind='codex', model='review-model', mode='medium'))

    def test_cli_missing_role_config_names_available_overrides_before_mutation(self):
        configured = self.local
        for role, prefix in (('agent', 'i'), ('reviewer', 'r')):
            for config, hint in ((None, f'--{prefix}-agent'),
                                 (fixture.AgentConfig('codex', None, 'high'), f'--{prefix}-model'),
                                 (fixture.AgentConfig('codex', 'model', None), f'--{prefix}-mode')):
                with self.subTest(role=role, config=config):
                    self.local = replace(configured, **{role: config})
                    with patch('sys.stderr', new_callable=io.StringIO) as error:
                        self.assertEqual(cli.main(['loop', 'DEV-7']), 1)
                    self.assertIn(hint, error.getvalue())
        self.assertEqual(self.preparation, [])
        self.linear.start.assert_not_called()
        self.assertEqual(self.registry.list(), [])

    def test_cli_role_overrides_select_implementation_and_reviewer_independently(self):
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            code = cli.main(['loop', 'DEV-7', '--i-agent', 'pi', '--i-model', 'initial-model',
                             '--i-mode', 'medium', '--r-agent', 'codex', '--r-model', 'review-override',
                             '--r-mode', 'low', '--json'])
        self.assertEqual(code, 0, output.getvalue())
        self.assertEqual(self.launches[0].options, AgentOptions('pi', 'initial-model', 'medium'))
        state, _ = self.store.read()
        self.assertEqual(state['reviewer_options'], dict(kind='codex', model='review-override', mode='low'))
        self.assertEqual(json.loads(output.getvalue())['state'], 'clean')

    def test_pause_before_initial_launch_status_continue_preserves_settings(self):
        result = self.pause_before_launch()
        with patch('task_start.loop.load_local', side_effect=AssertionError('No config for status')):
            self.assertEqual(loop('DEV-7', action='status').data, result.data)
            self.assertEqual(self.pause().state, 'paused')
        self.local = replace(self.local, agent=fixture.AgentConfig('pi', 'changed', 'max'))
        continued = self.continue_loop()
        self.assertEqual(continued.state, 'clean', continued.render())
        self.assertEqual(self.launches[0].options, AgentOptions('pi', 'implementer', 'low'))
        self.assertEqual(self.preparation, ['base', 'create', 'linear'])

    def test_pause_during_initial_pass_collects_then_continues_review_only(self):
        self.on_implementation = lambda *_: self.pause()
        result = self.run_loop()
        self.assertEqual(result.state, 'paused', result.render())
        self.assertEqual(result.data['next_phase'], 'review')
        self.assertEqual((result.data['passes'], result.data['reviews']), (1, 0))
        self.assertEqual(self.prompts, [])
        self.on_implementation = None
        result = self.continue_loop()
        self.assertEqual(result.state, 'clean', result.render())
        self.assertEqual((len(self.launches), len(self.impl_prompts), len(self.prompts)), (1, 1, 1))

    def test_initial_pass_counts_against_budget(self):
        result = self.run_loop(max_passes=1)
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertIn('limit reached', result.data['reason'])
        self.assertEqual((result.data['passes'], result.data['reviews']), (1, 0))
        self.assertEqual(self.prompts, [])

    def test_interrupted_or_uncertain_launch_retains_claim_and_session_without_replay(self):
        for error in (KeyboardInterrupt(), TaskError('Uncertain delivery')):
            with self.subTest(error=type(error)):
                case = BootstrapLoopTests()
                case.setUp()
                try:
                    case.launch_error = error
                    result = case.run_loop()
                    self.assertEqual(result.state, 'interrupted' if isinstance(error, KeyboardInterrupt) else 'escalated')
                    state, _ = case.store.read()
                    self.assertEqual(state['active_pass']['context_id'], 'DEV-7-I1')
                    self.assertEqual(state['implementation']['session']['conversation_id'], 'original-implementation')
                    self.assertEqual(case.registry.get('DEV-7-I1')['state'], 'uncertain')
                    for kwargs in ({}, dict(action='new'), dict(action='continue')):
                        with self.assertRaises(TaskError):
                            loop('DEV-7', **kwargs)
                    self.assertEqual(len(case.launches), 1)
                    self.assertEqual(case.prompts, [])
                    self.assertEqual(loop('DEV-7', action='status').data, result.data)
                finally:
                    case.doCleanups()

    def test_failed_startup_before_session_observation_retains_unbound_claim(self):
        with patch.object(self.implementer, 'launch', side_effect=TaskError('Startup uncertain')) as launch:
            result = self.run_loop()
        self.assertEqual(result.state, 'escalated', result.render())
        launch.assert_called_once()
        state, _ = self.store.read()
        self.assertEqual(state['implementation'], dict(context_id='DEV-7-I1'))
        self.assertEqual(state['active_pass']['context_id'], 'DEV-7-I1')
        self.assertIsNotNone(state['active_pass']['pass_id'])
        self.assertEqual(self.registry.get('DEV-7-I1')['state'], 'uncertain')
        self.assertEqual(loop('DEV-7', action='status').data, result.data)
        for kwargs in ({}, dict(action='continue'), dict(action='new')):
            with self.assertRaises(TaskError):
                loop('DEV-7', **kwargs)
        self.assertEqual(self.prompts, [])

    def test_orphaned_initial_claim_cannot_continue_or_launch_replacement(self):
        self.pause_before_launch()
        state = self.store.continue_paused()
        self.store.begin(state)  # Simulate process death immediately after claim.
        for kwargs in ({}, dict(action='new'), dict(action='continue')):
            with self.assertRaises(TaskError):
                loop('DEV-7', **kwargs)
        self.assertEqual(loop('DEV-7', action='status').state, 'running')
        self.assertEqual(self.launches, [])
        self.assertEqual(len(self.registry.list()), 1)

    def test_interruption_after_reservation_before_checkpoint_cannot_relaunch(self):
        with patch.object(LoopStore, 'create', side_effect=KeyboardInterrupt()):
            with self.assertRaises(KeyboardInterrupt):
                self.run_loop()
        self.assertEqual(self.registry.get('DEV-7-I1')['state'], 'launching')
        with self.assertRaises(TaskError):
            self.run_loop()
        self.assertEqual(self.launches, [])

    def test_paused_initial_boundary_rechecks_shell_checkout_and_context(self):
        self.pause_before_launch()
        self.panes[0]['terminal_id'] = 'replacement'
        result = self.continue_loop()
        self.assertEqual(result.state, 'escalated', result.render())
        self.assertEqual(self.launches, [])

    def test_pause_before_launch_refuses_requirement_checkout_settings_and_session_drift(self):
        for fault in ('requirements', 'checkout', 'settings', 'session'):
            with self.subTest(fault=fault):
                case = BootstrapLoopTests()
                case.setUp()
                try:
                    case.pause_before_launch()
                    if fault == 'requirements':
                        case.linear.get_issue.return_value = replace(fixture.ISSUE, description='Changed scope')
                    elif fault == 'checkout':
                        (case.path / 'external.txt').write_text('out of band change')
                    elif fault == 'settings':
                        with case.registry.connection(write=True) as db:
                            db.execute('UPDATE contexts SET model=?', ('changed-model',))
                    else:
                        case.registry.update('DEV-7-I1', session_kind='path', session_id='/unexpected.jsonl')
                    result = case.continue_loop()
                    self.assertEqual(result.state, 'escalated', result.render())
                    self.assertEqual(case.launches, [])
                    self.assertEqual(case.prompts, [])
                finally:
                    case.doCleanups()

    def test_initial_checkpoint_rejects_missing_origin_settings_or_session(self):
        self.pause_before_launch()
        state, pause = self.store.read()
        variants = [dict(state, version=1), dict(state, initial_phase='implementation'),
                    dict(state, next_phase='review'), dict(state, implementation_options={}),
                    {k: v for k, v in state.items() if k != 'initial_phase'}]
        for value in variants:
            with self.assertRaisesRegex(TaskError, 'Malformed loop checkpoint'):
                LoopStore.decode((json.dumps(value), pause))
        self.assertEqual(self.continue_loop().state, 'clean')
        state, pause = self.store.read()
        state['implementation'] = dict(context_id='DEV-7-I1')
        with self.assertRaisesRegex(TaskError, 'Malformed loop checkpoint'):
            LoopStore.decode((json.dumps(state), pause))

    def test_sliced_historical_and_ambiguous_workspaces_are_refused(self):
        self.prepared_workspace(scope='add-ingestion-cli')
        with self.assertRaisesRegex(TaskError, 'unsliced'):
            self.run_loop()
        self.linear.start.assert_not_called()
        self.assertEqual(self.registry.list(), [])
        with patch.object(Git, 'check_history', side_effect=TaskError('Historical branch')):
            with self.assertRaisesRegex(TaskError, 'Historical'):
                self.run_loop()
        self.command(self.repo, 'branch', 'dev-7-other')
        with self.assertRaises(TaskError):
            self.run_loop()
        self.assertNotIn('open', self.preparation)
        self.assertEqual(self.launches, [])

    def test_retired_context_history_and_from_review_do_not_bootstrap(self):
        self.prepared_workspace()
        context = self.registry.allocate('DEV-7', 'implementation', agent='pi', repository=str(self.repo),
            worktree=str(self.path), endpoint='/server.sock', workspace_id='w1')
        self.registry.retire('DEV-7', self.repo, self.path, endpoint='/server.sock', workspace_id='w1')
        with self.assertRaisesRegex(TaskError, 'history'):
            self.run_loop()
        with self.assertRaises(TaskError):
            self.run_loop(from_review=True)
        self.assertEqual(self.registry.get(context)['state'], 'retired')
        self.assertEqual(self.preparation, [])
        self.assertEqual(self.launches, [])

    def test_implementation_flags_rejected_for_existing_context_and_controls(self):
        self.assertEqual(self.run_loop().state, 'clean')
        for kwargs in (dict(action='new', impl_model='other'), dict(from_review=True, impl_mode='low'),
                       dict(action='status', impl_agent_kind='pi'), dict(action='continue', impl_model='other'),
                       dict(action='pause', impl_mode='low')):
            with self.subTest(kwargs=kwargs), self.assertRaises(TaskError):
                loop('DEV-7', **kwargs)
        before = self.registry.get('DEV-7-I1')
        checkpoint = self.store.read()
        for flag, value in (('--i-agent', 'codex'), ('--i-model', 'other'), ('--i-mode', 'high')):
            with self.subTest(flag=flag), patch('sys.stderr', new_callable=io.StringIO) as error:
                self.assertEqual(cli.main(['loop', 'DEV-7', '--new', flag, value]), 1)
                self.assertIn('existing implementation settings are preserved', error.getvalue())
        self.assertEqual(self.registry.get('DEV-7-I1'), before)
        self.assertEqual(self.store.read(), checkpoint)
        self.assertEqual(len(self.launches), 1)
