"""Review regressions for drift and interrupted pre-submission boundaries."""

import copy
from dataclasses import replace
import io
import json
import unittest
from unittest.mock import patch

from task_start import TaskError
from task_start.agent import AgentOptions, Codex, Pi
from task_start.contexts import context_reference
from task_start.loop import loop
from task_start.loop_state import LoopStore
from task_start.pass_delivery import PassDelivery, seal
from task_start.publication_state import PublicationStore
from task_start.review import accept_review
from task_start.review_state import snapshot
from codex_startup_fixture import TRUST_SCREEN
import test_loop_abort as abort_fixture
import test_loop_bootstrap as bootstrap
import test_pass_delivery as provider


class DriftTests(unittest.TestCase):
    def test_abort_drift_is_durable_before_refusal_and_restoration_cannot_accept(self):
        for phase in ("review", "rereview"):
            for boundary in ("entry", "final", "accepted"):
                with self.subTest(phase=phase, boundary=boundary):
                    case = abort_fixture.AbortTests(); case.setUp()
                    try:
                        if boundary == "accepted":
                            def accepted(*args, **kwargs):
                                accept_review(*args, **kwargs)
                                raise KeyboardInterrupt()
                            before = case.interrupt(phase)
                            # Recovery owns a separate imported acceptance entrypoint.
                            with patch("task_start.loop.accept_review", side_effect=accepted):
                                self.assertEqual(loop("DEV-7").state, "interrupted")
                            self.assertIsNotNone(PublicationStore(case.path).read()["acceptance"])
                        else:
                            before = case.interrupt(phase)
                        rows = case.registry.list(include_retired=True)
                        panes = copy.deepcopy(case.panes)
                        counts = len(case.impl_prompts), len(case.prompts)
                        tracked = case.path / "tracked.txt"
                        content = tracked.read_bytes()
                        case.stop_agents()
                        if boundary == "final":
                            def drift(*args):
                                tracked.write_text("edit while verifying stopped runtime")
                                return {"100": 42}
                            case.proof.side_effect = drift
                        else:
                            tracked.write_text("edit before abort")
                        with self.assertRaises(TaskError):
                            case.abort()
                        saved = case.store.read()[0]
                        self.assertTrue(saved["delivery"]["invalidated"])
                        self.assertEqual(saved["active_pass"], before["active_pass"])
                        self.assertIsNone(PublicationStore(case.path).read()["acceptance"])
                        self.assertIsNone(case.store.abort_journal(before["run_id"]))
                        self.assertEqual(case.registry.list(include_retired=True), rows)
                        tracked.write_bytes(content)
                        with self.assertRaisesRegex(TaskError, "invalidated"):
                            case.abort()
                        case.panes[:] = panes
                        result = loop("DEV-7")
                        self.assertEqual(result.state, "escalated", result.render())
                        self.assertTrue(case.store.read()[0]["delivery"]["invalidated"])
                        self.assertIsNone(PublicationStore(case.path).read()["acceptance"])
                        self.assertEqual((len(case.impl_prompts), len(case.prompts)), counts)
                    finally:
                        case.doCleanups()


class StartupAbortTests(unittest.TestCase):
    command = bootstrap.BootstrapLoopTests.command
    pane_command = bootstrap.BootstrapLoopTests.pane_command
    worktrees = bootstrap.BootstrapLoopTests.worktrees
    select_adapter = bootstrap.BootstrapLoopTests.select_adapter
    implementation_adapter = bootstrap.BootstrapLoopTests.implementation_adapter
    update_base = bootstrap.BootstrapLoopTests.update_base
    codex_transport = bootstrap.BootstrapLoopTests.codex_transport

    def setUp(self):
        bootstrap.BootstrapLoopTests.setUp(self)
        self.enterContext(patch("task_start.loop_abort.ContextRegistry", return_value=self.registry))
        self.enterContext(patch("task_start.loop_abort.HerdrContexts", return_value=self.identities))
        self.enterContext(patch("task_start.loop_abort.stopped_loop_execution", return_value={"123": 7}))
        self.enterContext(patch("task_start.loop_abort.adapter_for", side_effect=lambda options:
                               Pi(options) if options.kind == "pi" else Codex(options)))

    def stop(self):
        self.panes[0].update(agent=None, agent_session=None, agent_status="unknown", launch_pending=False)

    def preserve_work(self):
        (self.path / "tracked.txt").write_text("staged valuable edits")
        self.command(self.path, "add", "tracked.txt")
        (self.path / "tracked.txt").write_text("unstaged valuable edits")
        (self.path / "untracked.txt").write_bytes(b"valuable\0untracked")
        return snapshot(self.path, self.base, self.branch)

    def abort_preserving(self, before):
        result = loop("DEV-7", action="abort")
        self.assertEqual(result.state, "aborted", result.render())
        self.assertFalse(result.data["abandonment"]["review_ready"])
        self.assertEqual(snapshot(self.path, self.base, self.branch), before)
        self.assertEqual(loop("DEV-7", action="abort").data, result.data)
        self.assertIsNone(PublicationStore(self.path).read()["acceptance"])
        self.assertEqual(self.prompts, [])
        return result

    def test_codex_trust_interruption_releases_only_exact_stopped_reservation(self):
        transport = self.codex_transport()
        transport.blocker = TRUST_SCREEN
        with patch("task_start.agent.sys.stdin") as stdin, patch("task_start.agent.sys.stderr", new_callable=io.StringIO):
            stdin.isatty.return_value = True
            stdin.readline.side_effect = KeyboardInterrupt
            self.assertEqual(loop("DEV-7", timeout=1).state, "interrupted")
        original = self.store.read()[0]
        self.assertIsNone(original["delivery"]["receipt"])
        self.assertEqual(self.registry.get("DEV-7-I1")["state"], "awaiting_user")
        transport.assert_effects(1, 0)
        with self.assertRaisesRegex(TaskError, "live, missing, or uncertain"):
            loop("DEV-7", action="abort")
        self.assertEqual(self.store.read()[0], original)
        self.stop()
        before = self.preserve_work()
        self.abort_preserving(before)
        self.assertEqual(self.registry.get("DEV-7-I1")["state"], "launching")
        self.assertEqual(self.store.abort_journal(original["run_id"])["original"], original)
        transport.assert_effects(1, 0)
        # A replacement may reach the same reserved shell, with a new pass ID;
        # stop before any new provider launch (the original transport is one-shot).
        with patch.object(PassDelivery, "attach", side_effect=KeyboardInterrupt):
            self.assertEqual(loop("DEV-7", action="new", timeout=1).state, "interrupted")
        replacement = self.store.read()[0]
        self.assertNotEqual(replacement["run_id"], original["run_id"])
        self.assertEqual(replacement["implementation"], original["implementation"])
        self.assertNotEqual(replacement["active_pass"]["pass_id"], original["active_pass"]["pass_id"])
        transport.assert_effects(1, 0)

    def pi_submission_interruption(self):
        transport = bootstrap.PiInitialTransport(self, history_at="prompt")
        def interrupted(group, operation, *args):
            result = transport.herdr(group, operation, *args)
            if (group, operation) == ("agent", "prompt"):
                raise KeyboardInterrupt()
            return result
        transport.command.side_effect = interrupted
        self.assertEqual(loop("DEV-7", timeout=1).state, "interrupted")
        original = self.store.read()[0]
        self.assertNotIn("conversation_id", context_reference(self.registry.get("DEV-7-I1")))
        self.assertEqual(original["delivery"]["context"]["session"]["conversation_id"], "original-implementation")
        self.stop()
        return transport, original

    def test_pi_pinned_receipt_enriches_registry_across_partial_abort_retry(self):
        transport, original = self.pi_submission_interruption()
        before = self.preserve_work()
        with patch.object(LoopStore, "finish_abort", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                loop("DEV-7", action="abort")
        self.assertEqual(self.store.read()[0]["status"], "aborting")
        retained = context_reference(self.registry.get("DEV-7-I1"))
        self.assertEqual(retained["conversation_id"], "original-implementation")
        self.assertEqual(retained["source"], "hook")
        self.abort_preserving(before)
        self.assertEqual(self.store.read()[0]["implementation"]["session"], original["delivery"]["context"]["session"])
        self.assertEqual(self.registry.get("DEV-7-I1")["state"], "active")
        self.assertEqual(self.registry.get("DEV-7-I1")["resumability"], "yes")
        self.assertEqual(len(self.impl_prompts), 1)
        self.assertEqual(transport.prompt, self.impl_prompts[0])
        with self.assertRaises(TaskError):
            seal(original["delivery"]["directory"], original["run_id"], original["active_pass"]["pass_id"],
                 str(self.path), self.base, self.branch)

    def test_pi_pinned_receipt_does_not_allow_replaced_history_or_conflicting_registry(self):
        transport, original = self.pi_submission_interruption()
        transport.history("replacement")
        with self.assertRaises(TaskError):
            loop("DEV-7", action="abort")
        self.assertEqual(self.store.read()[0], original)
        self.assertIsNone(self.store.abort_journal(original["run_id"]))
        transport.history()
        self.registry.update("DEV-7-I1", herdr_session=json.dumps(dict(transport.reference, conversation_id="conflicting")))
        with self.assertRaisesRegex(TaskError, "Saved pass session/settings changed"):
            loop("DEV-7", action="abort")
        self.assertEqual(self.store.read()[0], original)
        self.assertEqual(len(self.impl_prompts), 1)

    def interrupt_after_receipt(self):
        attach = PassDelivery.attach
        def interrupted(delivery, execution):
            execution = attach(delivery, execution)
            def receipt(*args):
                execution.delivery_observer(*args)
                raise KeyboardInterrupt()
            return replace(execution, delivery_observer=receipt)
        with patch.object(PassDelivery, "attach", interrupted):
            self.assertEqual(loop("DEV-7", timeout=1).state, "interrupted")
        original = self.store.read()[0]
        self.assertIsNotNone(original["delivery"]["receipt"])
        return original

    def test_codex_receipt_before_queue_submission_aborts_with_complete_readiness_history(self):
        transport = self.codex_transport()
        original = self.interrupt_after_receipt()
        self.assertIsNone(transport.queued_at)
        def request(method, params, **kwargs):
            if method == "thread/turns/list":
                return dict(data=[dict(id="readiness", status="completed", itemsView="full", items=[
                    dict(type="userMessage", clientId="native", content=[dict(type="text", text=transport.bootstrap)]),
                    dict(type="agentMessage", phase="final_answer", text="READY")])], nextCursor=None)
            return transport.request(method, params, **kwargs)
        transport.rpc.request.side_effect = request
        self.stop()
        before = self.preserve_work()
        self.abort_preserving(before)
        transport.assert_effects(1, 0)
        self.assertEqual(self.store.read()[0]["implementation"]["session"], original["delivery"]["context"]["session"])
        self.assertEqual(self.impl_prompts, [])

    def test_pi_receipt_before_submission_aborts_preserving_header_only_conversation(self):
        transport = bootstrap.PiInitialTransport(self, history_at="prompt")
        original = self.interrupt_after_receipt()
        self.assertIsNone(transport.prompt)
        retained = transport.path.read_bytes()
        self.assertEqual(len(retained.splitlines()), 1)
        self.stop()
        before = self.preserve_work()
        result = self.abort_preserving(before)
        self.assertIn("Pi session has no messages yet", result.data["reason"])
        self.assertEqual(transport.path.read_bytes(), retained)
        self.assertEqual(result.data["implementation_session"], original["delivery"]["context"]["session"])
        self.assertEqual(self.impl_prompts, [])
        self.assertFalse(any(c.args[:2] == ("agent", "prompt") for c in transport.command.call_args_list))
        # No completed history is invented. Explicit native implementation input
        # makes this same empty session reusable for a new implementation pass.
        self.panes[0].update(agent="pi", agent_status="idle", agent_session=transport.reference)
        with self.assertRaises(TaskError):
            loop("DEV-7", action="new", timeout=1)
        transport.history()
        transport.gets = transport.polls = 10  # The explicit native turn has finished.
        with patch.object(PassDelivery, "attach", side_effect=KeyboardInterrupt):
            self.assertEqual(loop("DEV-7", action="new", timeout=1).state, "interrupted")
        self.assertEqual(self.store.read()[0]["implementation"]["session"], original["delivery"]["context"]["session"])
        self.assertNotEqual(self.store.read()[0]["active_pass"]["pass_id"], original["active_pass"]["pass_id"])
        self.assertEqual(self.impl_prompts, [])


class NonDeliveryEvidenceTests(unittest.TestCase):
    setUp = provider.ProviderCompletionTests.setUp

    def test_codex_requires_complete_stable_history_and_empty_queue(self):
        claimed = copy.deepcopy(self.turn)
        latest = dict(id="ready", status="completed", error=None, itemsView="full", items=[
            dict(type="userMessage", clientId="native", content=[dict(type="text", text="readiness")])])
        older = dict(id="earlier", status="completed", error=None, itemsView="full", items=[
            dict(type="userMessage", clientId="earlier", content=[dict(type="text", text="old input")])])
        for change in (None, "hidden_prompt", "hidden_nonce", "partial", "running", "pagination", "missing_cursor",
                       "compacted", "unknown_input", "duplicate", "changed", "queued_during_scan"):
            with self.subTest(change=change):
                tail = copy.deepcopy(older)
                pages, queues = [], []
                if change in {"hidden_prompt", "hidden_nonce"}:
                    tail = copy.deepcopy(claimed)
                    if change == "hidden_prompt":
                        tail["items"][0]["clientId"] = "different"
                    else:
                        tail["items"][0]["content"][0]["text"] = "different"
                elif change == "partial": tail["itemsView"] = "summary"
                elif change == "running": tail["status"] = "inProgress"
                elif change == "compacted": tail["items"].append(dict(type="contextCompaction"))
                elif change == "unknown_input": tail["items"][0]["content"].append(dict(type="image"))
                elif change == "duplicate": tail["id"] = latest["id"]
                def request(method, params):
                    if method == "thread/read": return dict(thread=self.thread)
                    if method == "thread/queue/list":
                        queues.append(True)
                        return dict(data=[dict(id="late")] if change == "queued_during_scan" and len(queues) > 1 else [], nextCursor=None)
                    self.assertEqual(method, "thread/turns/list")
                    pages.append(params)
                    if params.get("cursor"):
                        value = dict(data=[copy.deepcopy(tail)], nextCursor="older" if change == "pagination" else None)
                        if change == "missing_cursor": value.pop("nextCursor")
                        if change == "changed" and len(pages) > 2: value["data"][0]["id"] = "replacement"
                        return value
                    return dict(data=[copy.deepcopy(latest)], nextCursor="older")
                self.rpc.request.side_effect = request
                if change is None:
                    self.assertEqual(self.adapter.verify_stopped_session(self.execution.workspace, self.reference, self.receipt), self.reference)
                    self.assertEqual(len(pages), 4)
                    with self.assertRaises(TaskError):  # Absence is never completion.
                        self.adapter.observe_delivery(self.execution, self.reference, self.receipt, self.completion)
                else:
                    with self.assertRaises(TaskError):
                        self.adapter.verify_stopped_session(self.execution.workspace, self.reference, self.receipt)
        self.assertTrue(all(c.args[0] in {"thread/read", "thread/turns/list", "thread/queue/list"} for c in self.rpc.request.call_args_list))

    def test_pi_non_delivery_requires_intact_complete_linear_history(self):
        adapter = Pi(AgentOptions("pi", "model", "high"))
        path = self.path / "pi.jsonl"
        reference = dict(agent="pi", kind="path", value=str(path), conversation_id="original")
        original = [dict(type="session", id="original", cwd=str(self.path)),
                    dict(type="message", id="old", parentId=None, message=dict(role="user", content="old input")),
                    dict(type="message", id="done", parentId="old", message=dict(role="assistant", content="done"))]
        for change in (None, "header_only", "compacted", "unknown_record", "branch", "duplicate", "truncated", "unterminated",
                       "multimodal", "replaced", "duplicate_key", "changed"):
            with self.subTest(change=change):
                entries = copy.deepcopy(original)
                if change == "header_only": entries = entries[:1]
                elif change == "compacted": entries.append(dict(type="compaction"))
                elif change == "unknown_record": entries.append(dict(type="future_record"))
                elif change == "branch": entries[-1]["parentId"] = "lost"
                elif change == "duplicate": entries[-1]["id"] = "old"
                elif change == "multimodal": entries[1]["message"]["content"] = [dict(type="image")]
                elif change == "replaced": entries[0]["id"] = "replaced"
                raw = "".join(json.dumps(e) + "\n" for e in entries)
                if change == "truncated": raw += '{"type":'
                elif change == "unterminated": raw = raw.rstrip("\n")
                elif change == "duplicate_key": raw = raw.replace('"content": "old input"', '"content": "exact nonce-bound prompt", "content": "old input"')
                path.write_text(raw)
                proof = adapter.prove_not_delivered
                def changed(*args):
                    result = proof(*args)
                    path.write_text(raw.replace('"done"', '"different"'))
                    return result
                with patch.object(adapter, "prove_not_delivered", side_effect=changed if change == "changed" else proof):
                    if change in {None, "header_only"}:
                        self.assertEqual(adapter.verify_stopped_session(self.execution.workspace, reference, self.receipt), reference)
                        with self.assertRaises(TaskError):
                            adapter.observe_delivery(self.execution, reference, self.receipt, self.completion)
                    else:
                        with self.assertRaises(TaskError):
                            adapter.verify_stopped_session(self.execution.workspace, reference, self.receipt)


if __name__ == "__main__":
    unittest.main()
