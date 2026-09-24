import copy
from dataclasses import replace
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch

from task_start import TaskError, cli
from task_start.agent import Codex, INSTRUCTIONS, task_prompt
from task_start.config import AgentConfig, agent_config, load_local
from task_start.linear import Linear
from task_start.workspace import Workspace, slice_slug
import test_task_start as baseline
from test_task_start import ISSUE, LOCAL, issue_data


class ConfigurationAndInputTests(unittest.TestCase):
    def test_arbitrary_models_and_reasoning(self):
        for model in ["gpt-6-astra", "gpt-6-sol", "custom/model:v2"]:
            for reasoning in ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"]:
                self.assertEqual(agent_config(dict(kind="codex", model=model, reasoning=reasoning)),
                                 AgentConfig("codex", model, reasoning))

    def test_invalid_agent_config(self):
        for data in [[], "codex", {}, dict(kind="other", model="a", reasoning="high"),
                     dict(kind="codex", model="a"), dict(kind="codex", model="a", reasoning="extreme"),
                     dict(kind="codex", model=3, reasoning="high"),
                     dict(kind="codex", model="-config", reasoning="high"),
                     dict(kind="codex", model="a\nb", reasoning="high")]:
            with self.subTest(data=data), self.assertRaises(TaskError):
                agent_config(data)

    def test_load_agent_and_skip_invalid_config_for_no_agent(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.toml"
            base = f'projects_root = {json.dumps(directory)}\n[linear]\napi_key = "placeholder"\n'
            path.write_text(base + '[agent]\nkind = "codex"\nmodel = "gpt-6-sol"\nreasoning = "max"\n')
            self.assertEqual(load_local(path).agent, AgentConfig("codex", "gpt-6-sol", "max"))
            path.write_text(base + '[agent]\nkind = "other"\n')
            self.assertIsNone(load_local(path, no_agent=True).agent)

    def test_slice_normalization_and_flags(self):
        for value in ["codex-handoff", "Codex Handoff", "  Codéx__Handoff  "]:
            self.assertEqual(slice_slug(value), "codex-handoff")
        args = cli.parser().parse_args(["start", "DEV-13", "--slice", "Codex Handoff", "--no-agent"])
        self.assertEqual(args.slice, "codex-handoff")
        self.assertTrue(args.no_agent)
        for value in ["", " ", "---", "../escape", "foo/bar", "a.lock", "a\nb", "a;ls", "a" * 101, "字"]:
            with self.subTest(value=value), patch("sys.stderr", new=io.StringIO()), self.assertRaises(SystemExit):
                cli.parser().parse_args(["start", "DEV-13", "--slice", value])


class PromptTests(unittest.TestCase):
    def test_linear_description_is_exact_and_nullable(self):
        for description in [None, "", "\n  Current **task**\r\n`$(echo bad)`\nΔ\n"]:
            data = issue_data()
            data["issue"]["description"] = description
            with patch.object(Linear, "request", return_value=data):
                issue = Linear("placeholder").get_issue("DEV-7")
            self.assertEqual(issue.description, description or "")
        for description in [42, [], {}]:
            data["issue"]["description"] = description
            with patch.object(Linear, "request", return_value=data), self.assertRaises(TaskError):
                Linear("placeholder").get_issue("DEV-7")
        del data["issue"]["description"]
        with patch.object(Linear, "request", return_value=data), self.assertRaises(TaskError):
            Linear("placeholder").get_issue("DEV-7")

    def test_fresh_snapshot_and_instructions(self):
        workspace = Workspace("dev-7-original-title", Path("/exact/checkout"), "w2", "tab", "pane", "reopened")
        issue = replace(ISSUE, title="Renamed issue", description="\nExact  whitespace\r\n**Unicode α**\n")
        prompt = task_prompt(issue, workspace)
        self.assertIn(f"Implement Linear issue {issue.identifier}.", prompt)
        self.assertIn(f"Title:\n{issue.title}\n", prompt)
        self.assertIn(f"Task:\n{issue.description}\n\nInstructions:", prompt)
        self.assertIn(INSTRUCTIONS, prompt)
        self.assertIn(str(workspace.path), prompt)
        self.assertIn(workspace.branch, prompt)
        self.assertNotIn(LOCAL.api_key, prompt)
        self.assertIn("Slice: handoff", task_prompt(issue, replace(workspace, slice="handoff")))
        with self.assertRaisesRegex(TaskError, "NUL"):
            task_prompt(replace(issue, description="\0"), workspace)


class AgentTests(unittest.TestCase):
    def setUp(self):
        self.message_id = "b67e58a0-3876-4e85-ab88-bc170e847327"
        self.bootstrap = (f"Handoff readiness {self.message_id}. Do not use tools or modify files. "
                          "Reply READY, then wait for the task prompt.")
        self.workspace = Workspace("dev-7-old", Path("/exact/checkout"), "w6", "w6:t8", "w6:p20", "ready")
        self.codex = Codex(AgentConfig("codex", "custom/model", "high"))
        self.rpc = MagicMock()
        for target, value in [("CodexRPC", None), ("uuid4", self.message_id)]:
            patcher = patch("task_start.agent." + target, return_value=value)
            mocked = patcher.start()
            self.addCleanup(patcher.stop)
            if target == "CodexRPC":
                self.factory = mocked
                self.factory.return_value = MagicMock()
                self.factory.return_value.__enter__.return_value = self.rpc
        def advance(seconds):
            self.now += seconds
        for target, effect in [("monotonic", lambda: self.now), ("sleep", advance)]:
            patcher = patch("task_start.agent.time." + target, side_effect=effect)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.reset_fixture()

    def reset_fixture(self):
        self.now = 0
        self.queued = False
        self.prompt = "Exact task\nα\r\n$(no shell)\n"
        self.thread_id = "01a0d314-bd68-7203-8b68-f2520f892afa"
        self.turn_id = "01a0d315-ded5-7051-ac63-e46e9aa818fe"
        self.thread = dict(id=self.thread_id, cwd=str(self.workspace.path), preview=self.bootstrap)
        self.sessions = [self.thread]
        self.bootstrap_item = dict(turnId="readiness-turn", item=dict(type="userMessage", clientId="native",
                                    content=[dict(type="text", text=self.bootstrap)]))
        self.receipt = dict(turnId=self.turn_id, item=dict(type="userMessage", clientId=self.message_id,
                           content=[dict(type="text", text=self.prompt)]))
        self.task_items = [self.receipt]
        self.agent = dict(agent="codex", agent_status="idle", workspace_id="w6", tab_id="w6:t8",
                          pane_id="w6:p20", terminal_id="term_opaque", cwd="/exact/checkout", foreground_cwd="/exact/checkout")
        self.args = ["codex", "--cd", "/exact/checkout", "--model", "custom/model", "--config",
                     'model_reasoning_effort="high"', "--", self.bootstrap]
        self.results = [dict(type="pane_list", panes=[dict(self.agent, agent=None)]),
                        dict(type="agent_started", agent=self.agent, argv=self.args),
                        dict(type="agent_info", agent=dict(self.agent)),
                        dict(type="agent_info", agent=dict(self.agent))]
        self.rpc.reset_mock()
        self.rpc.request.side_effect = self.request

    def request(self, method, params, **kwargs):
        if method == "thread/list":
            return dict(data=self.sessions, nextCursor=None)
        if method == "thread/read":
            return dict(thread=self.thread)
        if method == "thread/items/list":
            return dict(data=[self.bootstrap_item] + (self.task_items if self.queued else []), nextCursor=None)
        if method == "thread/queue/add":
            self.queued = True
            return dict(queuedSubmission=dict(id="queue-id", clientUserMessageId=params["clientUserMessageId"], input=params["input"]))
        self.fail("Unexpected session operation " + method)

    def run_launch(self):
        return self.codex.launch(self.workspace, self.prompt)

    def test_exact_workspace_model_reasoning_and_confirmed_task(self):
        with patch("task_start.agent.run", side_effect=[json.dumps(dict(result=r)) for r in self.results]) as run:
            result = self.run_launch()
        self.assertIn(self.turn_id, result)
        self.assertIn(self.thread_id, result)
        calls = [c.args[0] for c in run.call_args_list]
        self.assertEqual(calls[1][4:], ["--kind", "codex", "--pane", "w6:p20", "--timeout", "30000", "--", *self.args[1:]])
        self.assertEqual(calls[2:], [["herdr", "agent", "get", "w6:p20"]] * 2)
        self.factory.assert_called_once_with(self.workspace.path)
        self.rpc.request.assert_any_call("thread/queue/add", dict(threadId=self.thread_id,
            clientUserMessageId=self.message_id, input=[dict(type="text", text=self.prompt, text_elements=[])]))
        self.assertFalse(any(c.args[0] in {"thread/start", "thread/resume", "turn/start"} for c in self.rpc.request.call_args_list))

    def test_no_task_goes_through_terminal_arguments(self):
        with patch.object(self.codex, "command", side_effect=self.results) as command:
            self.run_launch()
        self.assertFalse(any(self.prompt in c.args or c.args[1] == "prompt" for c in command.call_args_list))

    def test_startup_failure_does_not_queue_or_retry(self):
        with patch.object(self.codex, "command", side_effect=[self.results[0], TaskError("failed")]) as command, self.assertRaisesRegex(TaskError, "startup"):
            self.run_launch()
        self.assertEqual(command.call_count, 2)
        self.assertFalse(self.queued)

    def test_existing_codex_in_any_pane_refuses_duplicate(self):
        self.results[0]["panes"].append(dict(self.agent, pane_id="w6:p99"))
        with patch.object(self.codex, "command", side_effect=self.results) as command, self.assertRaisesRegex(TaskError, "already occupies.*w6:p99"):
            self.run_launch()
        command.assert_called_once_with("pane", "list", "--workspace", "w6")
        self.factory.assert_not_called()

    def test_mismatched_start_response_prevents_queue(self):
        for field, value in [("workspace_id", "other"), ("pane_id", "other"), ("tab_id", "other"),
                             ("cwd", "/wrong"), ("foreground_cwd", "/wrong"), ("agent", "claude"), ("agent_status", "blocked")]:
            with self.subTest(field=field):
                self.reset_fixture()
                self.results[1]["agent"][field] = value
                with patch.object(self.codex, "command", side_effect=self.results), self.assertRaisesRegex(TaskError, "startup"):
                    self.run_launch()
                self.assertFalse(self.queued)

    def test_argv_mismatch_fails(self):
        self.results[1]["argv"] = ["codex"]
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
            self.run_launch()

    def test_working_without_task_receipt_fails_and_retry_cannot_duplicate(self):
        self.results[1]["agent"]["agent_status"] = "working"
        self.task_items = []
        with patch.object(self.codex, "command", side_effect=self.results) as command, self.assertRaisesRegex(TaskError, "did not confirm the exact task"):
            self.run_launch()
        self.assertEqual(command.call_count, 3)
        self.assertEqual(sum(c.args[0] == "thread/queue/add" for c in self.rpc.request.call_args_list), 1)
        with patch.object(self.codex, "command", return_value={"panes": [self.agent]}), self.assertRaisesRegex(TaskError, "already occupies"):
            self.run_launch()
        self.factory.assert_called_once()

    def test_first_use_trust_idle_is_not_readiness(self):
        self.results[1]["agent"].update(interactive_ready=True, agent_status="idle")
        self.sessions = []
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaisesRegex(TaskError, "trust"):
            self.run_launch()
        self.assertFalse(self.queued)

    def test_receipt_polling_never_repeats_queue_or_launch(self):
        pending = 2
        def request(method, params, **kwargs):
            nonlocal pending
            if self.queued and method == "thread/items/list" and pending:
                pending -= 1
                return dict(data=[self.bootstrap_item], nextCursor=None)
            return self.request(method, params, **kwargs)
        self.rpc.request.side_effect = request
        with patch.object(self.codex, "command", side_effect=self.results) as command:
            self.run_launch()
        self.assertEqual(self.now, 0.5)
        self.assertEqual(sum(c.args[1] == "start" for c in command.call_args_list), 1)
        self.assertEqual(sum(c.args[0] == "thread/queue/add" for c in self.rpc.request.call_args_list), 1)

    def test_quickly_completed_turn_is_confirmed(self):
        with patch.object(self.codex, "command", side_effect=self.results):
            self.assertIn("confirmed", self.run_launch())

    def test_different_input_fails(self):
        self.receipt["item"]["content"][0]["text"] = self.prompt[:-1]
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaisesRegex(TaskError, "prompt delivery/start"):
            self.run_launch()

    def test_turn_identifier_is_required(self):
        self.receipt["turnId"] = ""
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
            self.run_launch()

    def test_other_message_id_does_not_confirm_task(self):
        self.receipt["item"]["clientId"] = "other-message"
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
            self.run_launch()

    def test_ambiguous_session_identity_fails(self):
        self.sessions.append(dict(self.thread, id="01a0d314-bd68-7203-8b68-f2520f892afb"))
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
            self.run_launch()
        self.assertFalse(self.queued)

    def test_unrelated_recent_session_is_never_selected(self):
        self.sessions = [dict(self.thread, preview="unrelated task")]
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
            self.run_launch()
        self.assertFalse(self.queued)

    def test_invalid_session_identity_fails(self):
        for field, value in [("id", "--last"), ("cwd", "/wrong")]:
            with self.subTest(field=field):
                self.reset_fixture()
                self.thread[field] = value
                with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
                    self.run_launch()
                self.assertFalse(self.queued)

    def test_queue_failure_is_clear(self):
        def request(method, params, **kwargs):
            if method == "thread/queue/add":
                raise TaskError("queue rejected")
            return self.request(method, params, **kwargs)
        self.rpc.request.side_effect = request
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaisesRegex(TaskError, "prompt queue"):
            self.run_launch()

    def test_queue_acknowledgement_must_match_input(self):
        def request(method, params, **kwargs):
            result = self.request(method, params, **kwargs)
            if method == "thread/queue/add":
                result["queuedSubmission"]["input"] = []
            return result
        self.rpc.request.side_effect = request
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaisesRegex(TaskError, "prompt queue"):
            self.run_launch()

    def test_session_api_failure_is_not_assumed_success(self):
        self.rpc.request.side_effect = TaskError("unsupported session API")
        with patch.object(self.codex, "command", side_effect=self.results), self.assertRaisesRegex(TaskError, "session API"):
            self.run_launch()

    def test_replaced_terminal_or_agent_session_fails(self):
        for field, value in [("terminal_id", "replacement"), ("pane_id", "other"), ("agent_session", "replacement")]:
            for index in [2, 3]:
                with self.subTest(field=field, index=index):
                    self.reset_fixture()
                    if field == "agent_session":
                        for result in self.results[1:]:
                            result["agent"][field] = "original"
                    self.results[index]["agent"][field] = value
                    with patch.object(self.codex, "command", side_effect=self.results), self.assertRaises(TaskError):
                        self.run_launch()
                    if index == 2:
                        self.assertFalse(self.queued)

    def test_exact_linear_context_and_resolved_slice_reach_queue(self):
        self.workspace = replace(self.workspace, slice="importer")
        issue = replace(ISSUE, title="Current title", description="\nExact α\r\n  task\n")
        self.prompt = task_prompt(issue, self.workspace)
        self.receipt["item"]["content"][0]["text"] = self.prompt
        with patch.object(self.codex, "command", side_effect=self.results):
            self.run_launch()
        queued = [c.args[1]["input"][0]["text"] for c in self.rpc.request.call_args_list if c.args[0] == "thread/queue/add"]
        self.assertEqual(queued, [self.prompt])
        self.assertIn(f"Task:\n{issue.description}\n\nInstructions:", queued[0])
        self.assertIn("Slice: importer\nImplement only this slice", queued[0])

    def test_malformed_responses_fail_cleanly(self):
        for result in ["bad", "[]", "null", '{"result": {"type": "other"}}',
                       '{"result": {"type": "pane_list", "panes": null}}']:
            with patch("task_start.agent.run", return_value=result), self.assertRaises(TaskError):
                self.run_launch()

    def test_unavailable_codex(self):
        with patch("task_start.agent.shutil.which", return_value=None), self.assertRaisesRegex(TaskError, "not installed"):
            self.codex.check_available()


class HandoffOrchestrationTests(unittest.TestCase):
    setUp = baseline.OrchestrationTests.setUp

    def test_handoff_order_and_fresh_context(self):
        self.workspace = replace(self.workspace, slice="codex-handoff")
        order = []
        self.git.update_base.side_effect = lambda *a: order.append("base")
        self.herdr.prepare.side_effect = lambda *a: order.append("workspace") or self.workspace
        self.linear.start.side_effect = lambda *a: order.append("linear")
        self.agent.launch.side_effect = lambda *a: order.append("agent") or "working"
        self.linear.get_issue.return_value = replace(ISSUE, title="New title", description="Fresh task\n  exact\n")
        cli.start("DEV-7", slice="Codex Handoff")
        self.assertEqual(order, ["base", "workspace", "linear", "agent"])
        selected, prompt = self.agent.launch.call_args.args
        self.assertIs(selected, self.workspace)
        self.assertIn("New title", prompt)
        self.assertIn("Fresh task\n  exact\n", prompt)
        self.assertEqual(self.herdr.prepare.call_args.args[2:], ("dev-7-codex-handoff", "DEV-7", "codex-handoff"))
        self.assertIn("Slice: codex-handoff", prompt)

    def test_no_requested_slice_uses_resolved_slice_in_prompt(self):
        selected = replace(self.workspace, branch="dev-7-importer", slice="importer")
        self.herdr.prepare.return_value = selected
        cli.start("DEV-7")
        self.assertIsNone(self.herdr.prepare.call_args.args[-1])
        workspace, prompt = self.agent.launch.call_args.args
        self.assertEqual(workspace, selected)
        self.assertIn("Slice: importer\nImplement only this slice of the task.", prompt)

    def test_no_agent_still_prepares_and_updates_status(self):
        with patch("task_start.cli.Codex") as codex:
            output = cli.start("DEV-7", no_agent=True)
        self.herdr.prepare.assert_called_once()
        self.linear.start.assert_called_once_with(ISSUE)
        codex.assert_not_called()
        self.assertIn("skipped (--no-agent)", output)

    def test_preparation_or_status_failure_never_launches(self):
        for boundary in [self.git.update_base, self.herdr.prepare, self.linear.start]:
            with self.subTest(boundary=boundary):
                boundary.side_effect = TaskError("failed")
                with self.assertRaises(TaskError):
                    cli.start("DEV-7")
                self.agent.launch.assert_not_called()
                boundary.side_effect = None

    def test_agent_failure_is_reported_without_undoing_ready_workspace(self):
        self.agent.launch.side_effect = TaskError("Codex startup failed")
        with patch("sys.stderr", new=io.StringIO()) as stderr, patch("sys.stdout", new=io.StringIO()) as stdout:
            self.assertEqual(cli.main(["start", "DEV-7"]), 1)
        self.assertIn("Codex startup failed", stderr.getvalue())
        self.assertEqual(stdout.getvalue(), "")
        self.linear.start.assert_called_once_with(ISSUE)

    def test_missing_agent_config_before_mutations(self):
        with patch("task_start.cli.load_local", return_value=replace(LOCAL, agent=None)), self.assertRaisesRegex(TaskError, r"\[agent\]"):
            cli.start("DEV-7")
        self.git.update_base.assert_not_called()
        self.herdr.prepare.assert_not_called()
