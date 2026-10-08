import copy
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from uuid import uuid4

from task_start import TaskError
from task_start.agent import AgentOptions, Codex, Pi
from task_start.pass_delivery import read_private


class ProviderCompletionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.execution = SimpleNamespace(workspace=SimpleNamespace(path=self.path))
        self.reference = dict(agent="codex", kind="id", value=str(uuid4()))
        self.receipt = dict(message_id=str(uuid4()), prompt_sha256=hashlib.sha256(b"exact nonce-bound prompt").hexdigest())
        self.completion = "TASK_PASS_COMPLETE " + str(uuid4()) + " " + "f" * 64
        self.thread = dict(id=self.reference["value"], cwd=str(self.path), forkedFromId=None, parentThreadId=None)
        self.turn = dict(id="one", status="completed", error=None, itemsView="full", items=[
            dict(type="userMessage", clientId=self.receipt["message_id"], content=[dict(type="text", text="exact nonce-bound prompt")]),
            dict(type="agentMessage", phase="final_answer", text=self.completion)])
        self.queue = dict(data=[], nextCursor=None)
        self.adapter = Codex(AgentOptions("codex", "model", "high"))
        factory = self.enterContext(patch("task_start.agent.CodexRPC"))
        self.rpc = factory.return_value.__enter__.return_value
        self.rpc.request.side_effect = lambda method, params: {
            "thread/read": dict(thread=self.thread), "thread/turns/list": dict(data=[self.turn], nextCursor="older"),
            "thread/queue/list": self.queue}[method]

    def observed(self):
        return self.adapter.observe_delivery(self.execution, self.reference, self.receipt, self.completion)

    def test_exact_codex_completed_turn_is_observed_without_delivery(self):
        self.assertTrue(self.observed())
        self.assertEqual([call.args[0] for call in self.rpc.request.call_args_list],
                         ["thread/read", "thread/turns/list", "thread/queue/list"])

    def test_idle_or_json_cannot_replace_completed_provider_receipt(self):
        baseline = copy.deepcopy(self.turn)
        for change in ("nonce", "prompt", "prose", "forged_seal", "partial", "error", "interrupted", "extra_input", "after_final"):
            with self.subTest(change=change):
                self.turn = copy.deepcopy(baseline)
                if change == "nonce":
                    self.turn["items"][0]["clientId"] = str(uuid4())
                elif change == "prompt":
                    self.turn["items"][0]["content"][0]["text"] = "stale prompt"
                elif change == "prose":
                    self.turn["items"][-1]["text"] = "I completed the implementation"
                elif change == "forged_seal":
                    self.turn["items"][-1]["text"] = self.completion.replace("f" * 64, "a" * 64)
                elif change == "partial":
                    self.turn["itemsView"] = "summary"
                elif change == "error":
                    self.turn["error"] = dict(message="failed")
                elif change == "interrupted":
                    self.turn["status"] = "interrupted"
                elif change == "extra_input":
                    self.turn["items"].append(copy.deepcopy(self.turn["items"][0]))
                else:
                    self.turn["items"].append(dict(type="commandExecution"))
                with self.assertRaises(TaskError):
                    self.observed()

    def test_running_turn_remains_pending_and_pending_input_or_forks_refuse(self):
        self.turn["status"] = "inProgress"
        self.assertFalse(self.observed())
        self.queue["data"] = [dict(id="another prompt")]
        with self.assertRaisesRegex(TaskError, "pending input"):
            self.observed()
        self.queue["data"] = []
        self.thread["forkedFromId"] = "fork"
        with self.assertRaises(TaskError):
            self.observed()

    def test_pi_exact_chain_and_replaced_branch_or_session(self):
        adapter = Pi(AgentOptions("pi", "model", "high"))
        path = self.path / "pi.jsonl"
        reference = dict(agent="pi", kind="path", value=str(path), conversation_id="original")
        entries = [dict(type="session", id="original", cwd=str(self.path)),
                   dict(type="message", id="input", parentId=None,
                        message=dict(role="user", content="exact nonce-bound prompt")),
                   dict(type="message", id="final", parentId="input",
                        message=dict(role="assistant", stopReason="stop", content=[dict(type="text", text=self.completion)]))]
        def write(value):
            path.write_text("".join(json.dumps(v) + "\n" for v in value))
        write(entries)
        self.assertTrue(adapter.observe_delivery(self.execution, reference, self.receipt, self.completion))
        for change in ("session", "parent", "input", "compaction"):
            with self.subTest(change=change):
                bad = copy.deepcopy(entries)
                if change == "session":
                    bad[0]["id"] = "replacement"
                elif change == "parent":
                    bad[-1]["parentId"] = "unrelated"
                elif change == "input":
                    bad.append(dict(type="message", id="another", parentId="final", message=dict(role="user", content="extra")))
                else:
                    bad.append(dict(type="compaction"))
                write(bad)
                with self.assertRaises(TaskError):
                    adapter.observe_delivery(self.execution, reference, self.receipt, self.completion)

    def test_result_read_is_bounded_and_refuses_symlinks(self):
        output = self.path / "result.json"
        output.write_bytes(b"x" * (1024 * 1024 + 1))
        with self.assertRaises(TaskError):
            read_private(output)
        output.unlink(); output.symlink_to(self.path / "missing")
        with self.assertRaises(TaskError):
            read_private(output)
