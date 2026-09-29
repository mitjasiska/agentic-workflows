import copy
from dataclasses import replace
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from task_start import TaskError, cli
from task_start.agent import AgentOptions, Codex
from task_start.contexts import ContextRegistry, HerdrContexts
from task_start.handoff import implementation_handoff
from task_start.workspace import Workspace
from codex_startup_fixture import CodexStartupTransport
from test_task_start import ISSUE, LOCAL, PROJECT


class FreshCodexStartTests(unittest.TestCase):
    """Normal start orchestration and registry with sequenced transport boundaries."""

    def setUp(self):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.workspace = Workspace("dev-7-test", directory, "w1", "t1", "p1", "created")
        self.pane = dict(pane_id="p1", tab_id="t1", workspace_id="w1", terminal_id="term1",
                         cwd=str(directory), agent=None, agent_session=None, label="Shell")
        self.registry = ContextRegistry(directory / "contexts.sqlite3")
        identities = Mock(spec=HerdrContexts)
        identities.endpoint.return_value = "/server.sock"
        identities.snapshot.side_effect = lambda: [copy.deepcopy(self.pane)]
        identities.label.side_effect = lambda pane, label: self.pane.update(label=label)
        self.enterContext(patch("task_start.contexts.ContextRegistry", return_value=self.registry))
        self.enterContext(patch("task_start.contexts.HerdrContexts", return_value=identities))
        self.enterContext(patch("task_start.cli.load_local", return_value=LOCAL))
        self.enterContext(patch("task_start.cli.load_projects", return_value=[PROJECT]))
        self.enterContext(patch("task_start.cli.Linear")).return_value.get_issue.return_value = ISSUE
        self.enterContext(patch("task_start.cli.Git"))
        self.herdr = self.enterContext(patch("task_start.cli.Herdr")).return_value
        self.herdr.prepare.return_value = self.workspace
        self.transport = CodexStartupTransport(self, lambda: [self.pane])

    def test_first_start_waits_for_shell_runtime_session_and_receipt_once(self):
        result = cli.start("DEV-7")
        self.assertIn("DEV-7-I1: Codex turn task-turn confirmed", result)
        t = self.transport
        self.assertEqual(t.process_reads, 2)
        self.assertEqual(t.started_at, 0.25)
        self.assertEqual(t.now, 1.75)
        t.assert_effects(1, 1)
        self.assertEqual(t.queued["input"][0]["text"], implementation_handoff(ISSUE, self.workspace))
        row = self.registry.get("DEV-7-I1")
        self.assertEqual((row["state"], row["session_id"], row["resumability"]), ("active", t.thread_id, "yes"))

    def test_ready_reopened_workspace_needs_no_poll_delay(self):
        self.herdr.prepare.return_value = replace(self.workspace, action="reopened")
        t = self.transport
        t.processes = t.processes[-1:]
        t.runtime_delay = t.session_delay = t.receipt_delay = 0
        cli.start("DEV-7")
        t.sleep.assert_not_called()
        t.assert_effects(1, 1)
        # Reusing an occupied workspace still refuses a duplicate/context allocation.
        with self.assertRaisesRegex(TaskError, "already occupies"):
            cli.start("DEV-7")
        t.assert_effects(1, 1)
        self.assertEqual(len(self.registry.list()), 1)

    def test_observation_time_reduces_remaining_readiness_budget(self):
        t = self.transport
        t.process_durations = [0.125]
        with patch.object(Codex, "SHELL_READY_TIMEOUT", 1):
            cli.start("DEV-7")
        self.assertEqual(t.process_timeouts, [1, 0.625])
        self.assertEqual(t.started_at, 0.5)
        t.assert_effects(1, 1)

    def test_ready_observation_returning_after_deadline_sends_no_input(self):
        t = self.transport
        t.processes = t.processes[-1:]
        t.process_durations = [31]
        with self.assertRaisesRegex(TaskError, r"pane readiness.*Timed out.*foreground_pids=\[123\]"):
            cli.start("DEV-7")
        self.assertEqual(t.process_timeouts, [30])
        t.keys.assert_not_called()
        t.assert_effects(0, 0)
        self.assertIsNone(self.registry.get("DEV-7-I1")["session_id"])

    def test_ready_observation_returning_at_deadline_sends_no_input(self):
        t = self.transport
        t.processes = t.processes[-1:]
        t.process_durations = [0.5]
        with patch.object(Codex, "SHELL_READY_TIMEOUT", 0.5), self.assertRaisesRegex(TaskError, "Timed out"):
            cli.start("DEV-7")
        self.assertEqual(t.process_timeouts, [0.5])
        t.keys.assert_not_called()
        t.assert_effects(0, 0)

    def test_no_observation_starts_after_budget_is_consumed(self):
        t = self.transport
        t.process_durations = [0.375, 0]
        with patch.object(Codex, "SHELL_READY_TIMEOUT", 0.5), self.assertRaisesRegex(TaskError, "Timed out"):
            cli.start("DEV-7")
        self.assertEqual(t.now, 0.5)
        self.assertEqual(t.process_reads, 1)
        self.assertEqual(t.process_timeouts, [0.5])
        t.keys.assert_not_called()
        t.assert_effects(0, 0)

    def test_missing_initial_shell_metadata_can_settle(self):
        t = self.transport
        t.processes.insert(0, dict(shell_pid=None, foreground_process_group_id=None, foreground_processes=[]))
        cli.start("DEV-7")
        self.assertEqual(t.process_reads, 3)
        self.assertEqual(t.started_at, 0.5)
        t.assert_effects(1, 1)

    def test_shell_timeout_sends_no_input_and_consumes_ordinal(self):
        t = self.transport
        t.processes = t.processes[:1]
        with patch.object(Codex, "SHELL_READY_TIMEOUT", 0.5), self.assertRaisesRegex(
                TaskError, r"pane readiness.*Timed out.*shell_pid=123.*foreground_pids=\[123, 124, 125\]"):
            cli.start("DEV-7")
        self.assertEqual(t.now, 0.5)
        t.keys.assert_not_called()
        t.assert_effects(0, 0)
        row = self.registry.get("DEV-7-I1")
        self.assertEqual((row["state"], row["session_id"]), ("uncertain", None))
        t.processes = [dict(shell_pid=123, foreground_process_group_id=123, foreground_processes=[dict(pid=123)])]
        self.assertIn("DEV-7-I2", cli.start("DEV-7"))
        self.assertEqual([r["ordinal"] for r in self.registry.list()], [1, 2])

    def test_changed_shell_identity_is_not_polled_away(self):
        t = self.transport
        t.processes[1] = dict(shell_pid=456, foreground_process_group_id=456, foreground_processes=[dict(pid=456)])
        with self.assertRaisesRegex(TaskError, "pane readiness.*Shell identity changed.*shell_pid=456"):
            cli.start("DEV-7")
        t.keys.assert_not_called()
        t.assert_effects(0, 0)
        self.assertEqual(t.process_reads, 2)

    def test_wrong_pane_or_malformed_process_state_is_not_retried(self):
        t = self.transport
        original = t.processes[-1]
        for changes in (dict(pane_id="other"), dict(shell_pid="123"), dict(foreground_processes=[{}])):
            with self.subTest(changes=changes):
                t.processes = [dict(original, **changes)]
                with self.assertRaisesRegex(TaskError, "pane readiness.*last check"):
                    cli.start("DEV-7")
        t.keys.assert_not_called()
        t.assert_effects(0, 0)

    def test_session_timeout_never_reissues_launch_or_queues_task(self):
        t = self.transport
        t.session_delay = 100
        with self.assertRaisesRegex(TaskError, "readiness confirmation.*readiness turn not found"):
            cli.start("DEV-7")
        self.assertEqual(t.now, 30.75)
        t.assert_effects(1, 0)
        self.assertIsNone(self.registry.get("DEV-7-I1")["session_id"])

    def test_unexpected_runtime_fails_before_session_poll_or_prompt(self):
        t = self.transport
        t.start_changes = dict(agent="pi")
        with self.assertRaisesRegex(TaskError, "startup launch/runtime confirmation.*agent target mismatch"):
            cli.start("DEV-7")
        t.assert_effects(1, 0)
        t.rpc.request.assert_not_called()

    def test_contradictory_session_is_never_accepted(self):
        t = self.transport
        t.get_changes = dict(agent_session=dict(agent="codex", kind="id", value="different"))
        with self.assertRaisesRegex(TaskError, "session does not match"):
            cli.start("DEV-7")
        t.assert_effects(1, 0)
        self.assertEqual(self.registry.get("DEV-7-I1")["session_id"], t.thread_id)

    def test_prompt_timeout_keeps_identity_and_never_resubmits(self):
        t = self.transport
        t.receipt_delay = 100
        with self.assertRaisesRegex(TaskError, "prompt delivery/start.*Timed out"):
            cli.start("DEV-7")
        t.assert_effects(1, 1)
        row = self.registry.get("DEV-7-I1")
        self.assertEqual((row["state"], row["session_id"], row["resumability"]), ("uncertain", t.thread_id, "yes"))


class ShellReadinessTransportTests(unittest.TestCase):
    def test_remaining_budget_reaches_subprocess_and_transport_timeout_stops_input(self):
        workspace = Workspace("dev-7-test", Path("/checkout"), "w1", "t1", "p1", "ready")
        adapter = Codex(AgentOptions("codex"))
        now, budgets = [0], []

        def advance(seconds):
            now[0] += seconds

        def observe(args, **kwargs):
            self.assertEqual(args, ["herdr", "pane", "process-info", "--pane", "p1"])
            budgets.append(kwargs["timeout"])
            if len(budgets) == 1:
                advance(0.125)
                process = dict(pane_id="p1", shell_pid=123, foreground_process_group_id=123,
                               foreground_processes=[dict(pid=123), dict(pid=124)])
                payload = dict(result=dict(type="pane_process_info", process_info=process))
                return subprocess.CompletedProcess(args, 0, json.dumps(payload).encode(), b"")
            advance(kwargs["timeout"])
            raise subprocess.TimeoutExpired(args, kwargs["timeout"])

        clock = SimpleNamespace(monotonic=lambda: now[0], sleep=advance)
        with patch("task_start.agent.time", clock), patch.object(adapter, "SHELL_READY_TIMEOUT", 1), \
                patch("task_start.workspace.subprocess.run", side_effect=observe), \
                self.assertRaisesRegex(TaskError, r"Timed out.*foreground_pids=\[123, 124\]"):
            adapter.clear_shell_input(workspace)
        self.assertEqual(budgets, [1, 0.625])
        self.assertEqual(now[0], 1)
