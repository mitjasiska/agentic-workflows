"""Non-destructive aborts with real Git/checkpoints and disposable processes."""

import copy
import fcntl
import io
import json
import multiprocessing
import os
from pathlib import Path
import pty
import select
import signal
import subprocess
import sys
import termios
import time
import unittest
from unittest.mock import patch

from task_start import TaskError, cli
from task_start.agent import Codex, Pi, AgentOptions
from task_start.contexts import ContextRegistry
from task_start.integration_process import stopped_loop_execution
from task_start.loop import loop, environment
from task_start.loop_state import LoopStore
from task_start.pass_delivery import PassDelivery, seal
from task_start.publication_state import PublicationStore
from task_start.review_state import snapshot
from task_start.review import accept_review
import test_loop_recovery as recovery
import test_loop_bootstrap as bootstrap
import test_canceled_cleanup as cleanup
import test_pass_delivery as provider


class AbortTests(unittest.TestCase):
    command = recovery.RecoveryTests.command
    pane_command = recovery.RecoveryTests.pane_command
    worktrees = recovery.RecoveryTests.worktrees
    select_adapter = recovery.RecoveryTests.select_adapter
    interrupt = recovery.RecoveryTests.interrupt

    def setUp(self):
        recovery.RecoveryTests.setUp(self)
        self.enterContext(patch("task_start.loop_abort.ContextRegistry", return_value=self.registry))
        self.enterContext(patch("task_start.loop_abort.HerdrContexts", return_value=self.identities))
        self.enterContext(patch("task_start.loop_abort.adapter_for", side_effect=lambda options:
                                self.implementer if options.kind == "pi" else self.adapter))
        self.implementer.verify_stopped_session = lambda workspace, reference, receipt=None: self.implementer.verify_session(workspace, reference)
        self.adapter.verify_stopped_session = lambda workspace, reference, receipt=None: self.adapter.verify_session(workspace, reference)
        self.proof = self.enterContext(patch("task_start.loop_abort.stopped_loop_execution", return_value={"100": 42}))
        original_command = self.pane_command
        def command(group, operation, *args):
            if operation == "process-info":
                pane = args[-1]
                pid = 100 + int(pane[1:])
                return dict(process_info=dict(pane_id=pane, shell_pid=pid, foreground_process_group_id=pid,
                            foreground_processes=[dict(pid=pid, argv=["bash"])]))
            return original_command(group, operation, *args)
        self.identity_command.side_effect = command

    def stop_agents(self):
        for pane in self.panes:
            pane.update(agent=None, agent_session=None, agent_status="unknown")

    def resume_implementation(self):
        self.panes[0].update(agent="pi", agent_session=dict(agent="pi", kind="path", value="/implementation.jsonl"),
                             agent_status="idle")

    def abort(self):
        return loop("DEV-7", action="abort")

    def content(self):
        return snapshot(self.path, self.base, self.branch).as_dict()

    def test_review_abort_preserves_staged_untracked_and_history_then_fresh_reviewer(self):
        (self.path / "tracked.txt").write_text("staged implementation")
        self.command(self.path, "add", "tracked.txt")
        (self.path / "tracked.txt").write_text("unstaged implementation")
        (self.path / "untracked.txt").write_bytes(b"precious\0work")
        original = self.interrupt("review")
        before, rows = self.content(), self.registry.list()
        self.stop_agents()
        # Abort/status controls must not load config or contact Linear.
        with patch("task_start.loop.load_local", side_effect=AssertionError("Local config accessed")):
            result = self.abort()
            self.assertEqual(result.state, "aborted", result.render())
            self.assertEqual(result.data["implementation_session"], original["implementation"]["session"])
            self.assertIn("/implementation.jsonl", result.render())
            self.assertEqual(self.abort().data, result.data)
            self.assertEqual(loop("DEV-7", action="status").data, result.data)
        self.assertEqual(self.content(), before)
        self.assertEqual(self.command(self.path, "branch", "--show-current"), self.branch)
        self.assertTrue(Path(original["delivery"]["directory"]).is_dir())
        archived = self.store.abort_journal(original["run_id"])
        self.assertEqual(archived["original"], original)
        self.assertEqual(archived["contexts"], rows)
        self.assertIsNone(self.store.read()[0]["active_pass"])
        self.assertIsNone(self.store.read()[0]["delivery"])
        retired = self.registry.get("DEV-7-R1")
        self.assertEqual(retired["state"], "abandoned")
        self.assertTrue(retired["retired_at"])
        for key in ("session_id", "herdr_session", "terminal_id", "pane_id"):
            self.assertEqual(retired[key], rows[-1][key])
        self.assertEqual(self.registry.get("DEV-7-I1"), rows[0])
        self.resume_implementation()
        result = loop("DEV-7", action="new", from_review=True, timeout=1)
        self.assertEqual(result.state, "clean", result.render())
        self.assertEqual(result.data["reviewer_context"], "DEV-7-R2")
        self.assertEqual(len(self.impl_prompts), 1)
        self.assertEqual(len(self.prompts), 2)
        self.assertEqual(self.content(), before)
        self.assertEqual(self.store.abort_journal(original["run_id"]), archived)

    def test_interrupted_implementation_and_fixes_require_explicit_continuation(self):
        for phase in ("implementation", "fixes"):
            with self.subTest(phase=phase):
                case = AbortTests(); case.setUp()
                try:
                    original = case.interrupt(phase)
                    (case.path / "partial.txt").write_text("unfinished work")
                    case.command(case.path, "add", "partial.txt")
                    (case.path / "new.txt").write_text("keep untracked")
                    before = case.content()
                    case.stop_agents()
                    result = case.abort()
                    case.assertFalse(result.data["abandonment"]["review_ready"])
                    case.assertEqual(case.content(), before)
                    case.assertEqual(loop("DEV-7").state, "aborted")
                    case.resume_implementation()
                    with case.assertRaisesRegex(TaskError, "Implementation was interrupted"):
                        loop("DEV-7", action="new", from_review=True)
                    count = len(case.impl_prompts)
                    result = loop("DEV-7", action="new", timeout=1)
                    case.assertEqual(result.state, "clean", result.render())
                    case.assertEqual(len(case.impl_prompts), count + 1)
                    case.assertNotEqual(case.store.read()[0]["run_id"], original["run_id"])
                finally:
                    case.doCleanups()

    def test_pre_delivery_claim_released_without_replaying_original_prompt(self):
        with patch.object(self.implementer, "resume", side_effect=KeyboardInterrupt):
            self.assertEqual(loop("DEV-7", timeout=1).state, "interrupted")
        original = self.store.read()[0]
        self.assertIsNone(original["delivery"]["receipt"])
        self.stop_agents()
        self.assertEqual(self.abort().state, "aborted")
        self.assertEqual(self.impl_prompts, [])
        self.resume_implementation()
        self.assertEqual(loop("DEV-7", action="new", timeout=1).state, "clean")
        self.assertEqual(len(self.impl_prompts), 1)
        self.assertNotIn(original["active_pass"]["pass_id"], self.impl_prompts[0])

    def test_repeated_new_abort_preserves_prior_journals_and_retired_runtime_history(self):
        first = self.interrupt("review")
        self.stop_agents()
        self.abort()
        self.resume_implementation()
        self.on_review = lambda execution: (_ for _ in ()).throw(KeyboardInterrupt())
        self.assertEqual(loop("DEV-7", action="new", from_review=True, timeout=1).state, "interrupted")
        second = self.store.read()[0]
        self.stop_agents()
        self.assertEqual(self.abort().state, "aborted")
        self.assertEqual(self.store.abort_journal(first["run_id"])["original"], first)
        self.assertEqual(self.store.abort_journal(second["run_id"])["original"], second)
        self.assertEqual([c["state"] for c in self.registry.list(include_retired=True)],
                         ["active", "abandoned", "abandoned"])

    def test_interrupted_rereview_preserves_fixes_and_starts_fresh_review(self):
        original = self.interrupt("rereview")
        before = self.content()
        self.stop_agents()
        self.assertEqual(self.abort().state, "aborted")
        self.assertEqual(self.store.read()[0]["records"], original["records"])
        self.assertTrue(self.store.read()[0]["abandonment"]["review_ready"])
        self.resume_implementation()
        count = len(self.impl_prompts)
        result = loop("DEV-7", action="new", from_review=True, timeout=1)
        self.assertEqual(result.state, "clean", result.render())
        self.assertEqual(result.data["reviewer_context"], "DEV-7-R2")
        self.assertEqual(len(self.impl_prompts), count)
        self.assertEqual(self.content(), before)

    def test_git_change_during_retry_keeps_revocation_and_refuses_reconciliation(self):
        self.interrupt("implementation")
        self.stop_agents()
        with patch.object(ContextRegistry, "reconcile_loop_abort", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                self.abort()
        (self.path / "after-abort.txt").write_text("new external edits")
        before = self.content()
        with self.assertRaisesRegex(TaskError, "changed during abort"):
            self.abort()
        self.assertEqual(self.content(), before)
        self.assertEqual(self.store.read()[0]["status"], "aborting")

    def test_abort_revokes_only_its_unconsumed_acceptance_and_refuses_conflicting_publication(self):
        def accepted_then_interrupted(*args, **kwargs):
            accept_review(*args, **kwargs)
            raise KeyboardInterrupt()
        with patch("task_start.review.accept_review", side_effect=accepted_then_interrupted):
            self.assertEqual(loop("DEV-7", timeout=1).state, "interrupted")
        state = self.store.read()[0]
        publication = PublicationStore(self.path)
        saved = publication.read()
        self.assertEqual(saved["acceptance"]["pass_id"], state["active_pass"]["pass_id"])
        self.stop_agents()
        conflict = copy.deepcopy(saved)
        conflict["acceptance"]["pass_id"] = "another-review"
        publication.write(conflict)
        with self.assertRaisesRegex(TaskError, "Publication state conflicts"):
            self.abort()
        self.assertEqual(publication.read(), conflict)
        self.assertIsNone(self.store.abort_journal(state["run_id"]))
        conflict = dict(saved, publication_history=dict(intervening="publication"))
        publication.write(conflict)
        with self.assertRaisesRegex(TaskError, "Publication provenance changed"):
            self.abort()
        self.assertEqual(publication.read(), conflict)
        self.assertIsNone(self.store.abort_journal(state["run_id"]))
        publication.write(saved)
        self.assertEqual(self.abort().state, "aborted")
        self.assertEqual(publication.read(), dict(saved, acceptance=None))

    def test_historical_claim_without_delivery_bookkeeping_is_not_assumed_undelivered(self):
        state = self.interrupt("implementation")
        del state["delivery"]
        self.store.save(state)
        self.stop_agents()
        with self.assertRaisesRegex(TaskError, "Historical claim"):
            self.abort()
        self.assertEqual(self.store.read()[0], state)

    def test_review_eligibility_cannot_survive_post_abort_drift_or_skip_implementation(self):
        self.interrupt("review")
        self.stop_agents()
        self.abort()
        self.resume_implementation()
        (self.path / "later.txt").write_text("new implementation work")
        count = len(self.impl_prompts), len(self.prompts)
        with self.assertRaisesRegex(TaskError, "boundary changed since abort"):
            loop("DEV-7", action="new", from_review=True)
        self.assertEqual((len(self.impl_prompts), len(self.prompts)), count)

    def test_new_after_abort_cannot_replace_original_implementation_identity(self):
        self.interrupt("review")
        self.stop_agents()
        self.abort()
        self.resume_implementation()
        self.registry.update("DEV-7-I1", session_id="/replacement.jsonl", herdr_session=None)
        self.panes[0]["agent_session"]["value"] = "/replacement.jsonl"
        count = len(self.impl_prompts), len(self.prompts)
        for from_review in (False, True):
            with self.assertRaisesRegex(TaskError, "Exact implementation context/session/settings changed"):
                loop("DEV-7", action="new", from_review=from_review)
        self.assertEqual((len(self.impl_prompts), len(self.prompts)), count)

    def test_live_idle_missing_moved_session_provider_git_and_stale_claims_refuse(self):
        for change in ("live", "missing", "moved", "session", "provider", "queued", "git", "head", "base", "context", "process", "receipt", "disposal", "integration"):
            with self.subTest(change=change):
                case = AbortTests(); case.setUp()
                try:
                    state = case.interrupt("review")
                    case.stop_agents()
                    if change == "live":
                        case.panes[-1].update(agent="codex", agent_status="idle")
                    elif change == "missing":
                        case.panes.pop()
                    elif change == "moved":
                        case.panes[-1]["terminal_id"] = "replaced"
                    elif change == "session":
                        case.registry.update("DEV-7-R1", session_id="replaced")
                    elif change == "provider":
                        case.adapter.verify_session = lambda *a: (_ for _ in ()).throw(TaskError("Unavailable provider"))
                    elif change == "queued":
                        case.adapter.verify_stopped_session = lambda *a: (_ for _ in ()).throw(TaskError("Pending input"))
                    elif change == "git":
                        (case.path / "tracked.txt").write_text("review drift")
                    elif change in {"head", "base"}:
                        case.command(case.path if change == "head" else case.repo, "commit", "--allow-empty", "-m", "drift")
                    elif change == "context":
                        case.registry.allocate("DEV-7", "review", agent="codex", model="x", mode="high",
                            repository=str(case.repo), worktree=str(case.path), endpoint="/server.sock",
                            workspace_id="w1", tab_id="t1", pane_id="extra", terminal_id="extra")
                    elif change == "receipt":
                        state["delivery"]["context"]["session"]["value"] = "conflicting"
                        case.store.save(state)
                    elif change == "disposal":
                        case.enterContext(patch("task_start.loop_abort.check_unreserved",
                                                side_effect=TaskError("Pending cleanup reserves this resource")))
                    elif change == "integration":
                        case.enterContext(patch("task_start.loop_abort.IntegrationStore.read",
                                                return_value=dict(state="preparing")))
                    else:
                        case.proof.side_effect = TaskError("Background task is live")
                    state = case.store.read()[0]
                    before, rows = case.content(), case.registry.list(include_retired=True)
                    count = len(case.impl_prompts), len(case.prompts)
                    with case.assertRaises(TaskError):
                        case.abort()
                    if change in {"git", "head", "base"}:
                        state["delivery"]["invalidated"] = True
                    case.assertEqual(case.store.read()[0], state)
                    case.assertEqual(case.registry.list(include_retired=True), rows)
                    case.assertEqual(case.content(), before)
                    case.assertEqual((len(case.impl_prompts), len(case.prompts)), count)
                    case.assertIsNone(case.store.abort_journal(state["run_id"]))
                finally:
                    case.doCleanups()

    def test_partial_abort_retries_and_late_results_cannot_be_consumed(self):
        for stage in ("before_journal", "contexts", "checkpoint"):
            with self.subTest(stage=stage):
                case = AbortTests(); case.setUp()
                try:
                    original = case.interrupt("review")
                    case.stop_agents()
                    target = {"before_journal": (LoopStore, "begin_abort"),
                              "contexts": (ContextRegistry, "reconcile_loop_abort"),
                              "checkpoint": (LoopStore, "finish_abort")}[stage]
                    with patch.object(*target, side_effect=KeyboardInterrupt):
                        with case.assertRaises(KeyboardInterrupt):
                            case.abort()
                    case.assertEqual(case.store.read()[0]["status"], "interrupted" if stage == "before_journal" else "aborting")
                    if stage != "before_journal":
                        with case.assertRaisesRegex(TaskError, "Abort reconciliation"):
                            loop("DEV-7")
                        with case.assertRaises(TaskError):
                            case.store.save(original)
                        with case.assertRaises(TaskError):
                            seal(original["delivery"]["directory"], original["run_id"],
                                 original["active_pass"]["pass_id"], str(case.path), case.base, case.branch)
                    case.assertEqual(case.abort().state, "aborted")
                    case.assertEqual(case.abort().state, "aborted")
                    env = environment("DEV-7")
                    delivery = PassDelivery(original, lambda *_: case.store.save(original), env)
                    with case.assertRaisesRegex(TaskError, "abandoned or superseded"):
                        delivery.verify(case.adapter, None)
                    case.assertIsNone(PublicationStore(case.path).read()["acceptance"])
                    case.resume_implementation()
                    case.assertEqual(loop("DEV-7", action="new", from_review=True, timeout=1).state, "clean")
                    saved = PublicationStore(case.path).read()
                    with case.assertRaises(TaskError):
                        delivery.runtime()
                    case.assertEqual(PublicationStore(case.path).read(), saved)
                finally:
                    case.doCleanups()

    def test_active_controller_lock_refuses_with_stop_sequence(self):
        self.interrupt("implementation")
        self.stop_agents()
        ready, finish = multiprocessing.Event(), multiprocessing.Event()
        def owner():
            with PublicationStore(self.path).locked():
                ready.set(); finish.wait(10)
        process = multiprocessing.get_context("fork").Process(target=owner)
        process.start()
        try:
            self.assertTrue(ready.wait(5))
            with self.assertRaisesRegex(TaskError, "Ctrl\\+C in its terminal first"):
                self.abort()
            self.assertEqual(self.store.read()[0]["status"], "interrupted")
        finally:
            finish.set(); process.join(5)

    def test_cli_abort_controls_and_help(self):
        self.interrupt("review")
        self.stop_agents()
        with patch("sys.stdout", io.StringIO()) as output:
            self.assertEqual(cli.main(["loop", "DEV-7", "--abort", "--json"]), 0)
        self.assertEqual(json.loads(output.getvalue())["state"], "aborted")
        for extra in (["--from-review"], ["--r-model", "different"], ["--timeout", "5"]):
            with patch("sys.stderr", io.StringIO()):
                self.assertEqual(cli.main(["loop", "DEV-7", "--abort", *extra]), 1)
        with patch("sys.stdout", io.StringIO()) as output, self.assertRaises(SystemExit):
            cli.parser().parse_args(["loop", "--help"])
        self.assertIn("Ctrl+C first", output.getvalue())
        with patch("task_start.cli.loop", side_effect=KeyboardInterrupt), patch("sys.stderr", io.StringIO()) as error:
            self.assertEqual(cli.main(["loop", "DEV-7", "--abort"]), 130)
        self.assertIn("--abort to finish reconciliation", error.getvalue())


class LoopProcessTests(unittest.TestCase):
    setUp = cleanup.DisposalProcessTests.setUp
    process = cleanup.DisposalProcessTests.process
    shells = cleanup.DisposalProcessTests.shells

    def test_ancestry_detached_checkout_users_unreadable_evidence_and_pid_reuse(self):
        self.process(123, 1, self.checkout)
        self.assertEqual(stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc), {"123": 7})
        for kind in ("child", "cwd", "argv", "fd", "missing", "replaced"):
            with self.subTest(kind=kind):
                root = self.proc / "124"
                self.process(124, 123 if kind == "child" else 1,
                             self.checkout if kind == "cwd" else self.proc,
                             argv=f"worker\0{self.checkout}\0" if kind == "argv" else "worker\0")
                if kind == "fd":
                    (root / "fd" / "3").symlink_to(self.checkout / "work.txt")
                if kind == "missing":
                    (root / "cwd").unlink()
                if kind == "replaced":
                    (self.proc / "123" / "exe").unlink()
                    (self.proc / "123" / "exe").symlink_to(sys.executable)
                with self.assertRaises(TaskError):
                    stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)
                import shutil
                shutil.rmtree(root)

    def nonterminal_process(self, pid, *, started=6, parent=1):
        self.process(pid, parent, self.proc, argv="service\0", started=started)
        stat = self.proc / str(pid) / "stat"
        fields = stat.read_text().rpartition(") ")[2].split()
        fields[4] = "0"
        stat.write_text(f"{pid} (service) " + " ".join(fields))

    def deny_private_references(self, pids):
        original_link, original_iter = os.readlink, Path.iterdir
        entries = {self.proc / str(pid) for pid in pids}
        def readlink(path, *args, **kwargs):
            if Path(path).parent in entries and Path(path).name == "cwd":
                raise PermissionError("private process cwd")
            return original_link(path, *args, **kwargs)
        def iterdir(path):
            if path.parent in entries and path.name == "fd":
                raise PermissionError("private process descriptors")
            return original_iter(path)
        self.enterContext(patch("os.readlink", side_effect=readlink))
        self.enterContext(patch.object(Path, "iterdir", iterdir))

    def test_older_services_require_readable_private_references(self):
        self.process(123, 1, self.checkout)
        for pid, name in enumerate(("systemd", "sd-pam", "ssh-agent", "sshd"), 20):
            self.nonterminal_process(pid)
            (self.proc / str(pid) / "cmdline").write_bytes(name.encode() + b"\0")
        self.assertEqual(stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc), {"123": 7})
        self.deny_private_references(range(20, 24))
        with self.assertRaisesRegex(TaskError, "Cannot prove task processes stopped"):
            stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)

    def test_older_live_worker_with_inaccessible_checkout_descriptor_refuses(self):
        self.process(123, 1, self.checkout)
        self.nonterminal_process(124)
        descriptor = self.proc / "124" / "fd" / "8"
        descriptor.symlink_to(self.checkout / "work.txt")
        original = os.readlink
        def denied(path, *args, **kwargs):
            if Path(path) == descriptor:
                raise PermissionError("live worker's checkout descriptor is private")
            return original(path, *args, **kwargs)
        with patch("os.readlink", side_effect=denied) as reads, \
                self.assertRaisesRegex(TaskError, "Cannot prove task processes stopped"):
            stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)
        self.assertIn(descriptor, [call.args[0] for call in reads.call_args_list])

    def test_inaccessible_newer_or_unknown_identity_is_never_assumed_unrelated(self):
        for kind in ("newer", "same_tick", "terminal", "argv", "stat", "child", "session", "unknown_session"):
            with self.subTest(kind=kind):
                case = LoopProcessTests(); case.setUp()
                try:
                    case.process(123, 1, case.checkout)
                    case.nonterminal_process(124, started=8 if kind == "newer" else 7 if kind == "same_tick" else 6,
                                             parent=123 if kind == "child" else 1)
                    if kind in {"terminal", "session", "unknown_session"}:
                        stat = case.proc / "124" / "stat"
                        fields = stat.read_text().rpartition(") ")[2].split()
                        fields[4 if kind == "terminal" else 3] = "0" if kind == "unknown_session" else "123"
                        stat.write_text("124 (service) " + " ".join(fields))
                    case.deny_private_references([124])
                    if kind in {"argv", "stat"}:
                        name = "cmdline" if kind == "argv" else "stat"
                        method = "read_bytes" if kind == "argv" else "read_text"
                        original = getattr(Path, method)
                        def denied(path, *args, **kwargs):
                            if path == case.proc / "124" / name:
                                raise PermissionError("identity denied")
                            return original(path, *args, **kwargs)
                        case.enterContext(patch.object(Path, method, denied))
                    with case.assertRaises(TaskError):
                        stopped_loop_execution(case.checkout, case.shells(123), proc=case.proc)
                finally:
                    case.doCleanups()

    def test_accessible_checkout_evidence_overrides_older_private_process(self):
        self.process(123, 1, self.checkout)
        self.nonterminal_process(124)
        (self.proc / "124" / "cmdline").write_bytes(os.fsencode(f"worker\0{self.checkout}\0"))
        self.deny_private_references([124])
        with self.assertRaisesRegex(TaskError, "Process 124"):
            stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)

    def test_descriptor_closing_during_scan_is_not_missing_process_identity(self):
        self.process(123, 1, self.checkout)
        self.process(124, 1, self.proc)
        descriptor = self.proc / "124" / "fd" / "8"
        descriptor.symlink_to(self.proc / "unrelated")
        original = os.readlink
        def closing(path, *args, **kwargs):
            if Path(path) == descriptor:
                descriptor.unlink()
                raise FileNotFoundError("descriptor closed")
            return original(path, *args, **kwargs)
        with patch("os.readlink", side_effect=closing):
            self.assertEqual(stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc), {"123": 7})

    def test_disappearing_process_is_tolerated_but_missing_live_cwd_is_bounded(self):
        self.process(123, 1, self.checkout)
        self.process(124, 1, self.proc)
        original = os.readlink
        def exited(path, *args, **kwargs):
            if Path(path) == self.proc / "124" / "cwd":
                import shutil
                shutil.rmtree(self.proc / "124")
                raise FileNotFoundError("process exited")
            return original(path, *args, **kwargs)
        with patch("os.readlink", side_effect=exited):
            self.assertEqual(stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc), {"123": 7})
        self.process(124, 1, self.proc)
        (self.proc / "124" / "cwd").unlink()
        with patch("os.readlink", wraps=original) as reads, self.assertRaises(TaskError):
            stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)
        self.assertEqual(sum(c.args[0] == self.proc / "124" / "cwd" for c in reads.call_args_list), 3)

    def test_private_process_pid_reuse_during_observation_refuses(self):
        self.process(123, 1, self.checkout)
        self.nonterminal_process(124)
        original = os.readlink
        def replaced(path, *args, **kwargs):
            if Path(path) == self.proc / "124" / "cwd":
                stat = self.proc / "124" / "stat"
                fields = stat.read_text().rpartition(") ")[2].split()
                fields[19] = "8"
                stat.write_text("124 (replacement) " + " ".join(fields))
                raise PermissionError("changed")
            return original(path, *args, **kwargs)
        with patch("os.readlink", side_effect=replaced), self.assertRaises(TaskError):
            stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)

    def test_zombie_transition_during_reference_inspection_is_verified(self):
        self.process(123, 1, self.checkout)
        self.nonterminal_process(124)
        entry = self.proc / "124"
        stat = entry / "stat"
        original_stat = stat.read_text()
        read_bytes, readlink, iterdir = Path.read_bytes, os.readlink, Path.iterdir
        for stage in ("cmdline", "final_cmdline", "cwd", "private_cwd", "fd"):
            with self.subTest(stage=stage):
                stat.write_text(original_stat)
                (entry / "cmdline").write_bytes(b"worker\0")
                cmdline_reads = 0
                def terminate():
                    fields = original_stat.rpartition(") ")[2].split()
                    fields[0] = "Z"
                    stat.write_text("124 (worker) " + " ".join(fields))
                    (entry / "cmdline").write_bytes(b"")
                def command(path, *args, **kwargs):
                    nonlocal cmdline_reads
                    if path == entry / "cmdline":
                        cmdline_reads += 1
                        if (stage == "cmdline" or stage == "final_cmdline" and cmdline_reads == 2):
                            terminate()
                    return read_bytes(path, *args, **kwargs)
                def link(path, *args, **kwargs):
                    if Path(path) == entry / "cwd" and stage in {"cwd", "private_cwd"}:
                        terminate()
                        if stage == "private_cwd":
                            raise PermissionError("process terminated during cwd inspection")
                        raise FileNotFoundError("zombie has no cwd")
                    return readlink(path, *args, **kwargs)
                def descriptors(path):
                    if path == entry / "fd" and stage == "fd":
                        terminate()
                        raise FileNotFoundError("process terminated during descriptor inspection")
                    return iterdir(path)
                with patch.object(Path, "read_bytes", command), patch("os.readlink", side_effect=link), \
                        patch.object(Path, "iterdir", descriptors):
                    self.assertEqual(stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc),
                                     {"123": 7})
                self.assertTrue(entry.is_dir())  # An unreaped zombie keeps its PID directory.
                self.assertEqual((entry / "cmdline").read_bytes(), b"")

    def test_failed_reference_scan_rejects_live_reused_or_unverifiable_process(self):
        self.process(123, 1, self.checkout)
        self.nonterminal_process(124)
        entry = self.proc / "124"
        stat = entry / "stat"
        original_stat, read_bytes = stat.read_text(), Path.read_bytes
        for fault in ("live", "reused_live", "reused_zombie", "changed_parent", "changed_session",
                      "malformed_stat", "missing_stat", "denied_stat"):
            with self.subTest(fault=fault):
                stat.write_text(original_stat)
                observed = False
                read_text = Path.read_text
                def command(path, *args, **kwargs):
                    nonlocal observed
                    if path == entry / "cmdline":
                        observed = True
                        fields = original_stat.rpartition(") ")[2].split()
                        fields[0] = "S" if fault in {"live", "reused_live"} else "Z"
                        if fault.startswith("reused"):
                            fields[19] = "8"
                        if fault in {"changed_parent", "changed_session"}:
                            fields[1 if fault == "changed_parent" else 3] = "999"
                        stat.write_text("bad stat" if fault == "malformed_stat"
                                        else "124 (worker) " + " ".join(fields))
                        if fault == "missing_stat":
                            stat.unlink()
                        return b""
                    return read_bytes(path, *args, **kwargs)
                def identity(path, *args, **kwargs):
                    if path == stat and observed and fault == "denied_stat":
                        raise PermissionError("cannot verify process termination")
                    return read_text(path, *args, **kwargs)
                with patch.object(Path, "read_bytes", command), patch.object(Path, "read_text", identity), \
                        self.assertRaisesRegex(TaskError, "Cannot prove task processes stopped"):
                    stopped_loop_execution(self.checkout, self.shells(123), proc=self.proc)
                self.assertTrue(observed)


class StoppedProviderTests(unittest.TestCase):
    setUp = provider.ProviderCompletionTests.setUp

    def test_codex_requires_terminal_exact_turn_and_empty_queue(self):
        for status in ("completed", "interrupted", "failed"):
            self.turn["status"] = status
            self.adapter.verify_stopped_session(self.execution.workspace, self.reference, self.receipt)
        for change in ("working", "queue", "missing_queue", "partial", "prompt", "newer", "fork"):
            with self.subTest(change=change):
                turn, queue, thread = copy.deepcopy(self.turn), copy.deepcopy(self.queue), copy.deepcopy(self.thread)
                if change == "working":
                    self.turn["status"] = "inProgress"
                elif change == "queue":
                    self.queue["data"] = [dict(id="pending")]
                elif change == "missing_queue":
                    self.queue.pop("nextCursor")
                elif change == "partial":
                    self.turn["itemsView"] = "summary"
                elif change == "prompt":
                    self.turn["items"][0]["content"][0]["text"] = "different input"
                elif change == "newer":
                    self.turn["items"][0]["clientId"] = "different-turn"
                else:
                    self.thread["parentThreadId"] = "parent"
                with self.assertRaises(TaskError):
                    self.adapter.verify_stopped_session(self.execution.workspace, self.reference, self.receipt)
                self.turn, self.queue, self.thread = turn, queue, thread
        self.assertTrue(all(call.args[0] in {"thread/read", "thread/turns/list", "thread/queue/list"}
                            for call in self.rpc.request.call_args_list))

    def test_pi_accepts_interrupted_chain_but_refuses_replaced_or_newer_history(self):
        adapter = Pi(AgentOptions("pi", "model", "high"))
        path = self.path / "pi.jsonl"
        reference = dict(agent="pi", kind="path", value=str(path), conversation_id="original")
        entries = [dict(type="session", id="original", cwd=str(self.path)),
                   dict(type="message", id="input", parentId=None,
                        message=dict(role="user", content="exact nonce-bound prompt")),
                   dict(type="message", id="aborted", parentId="input",
                        message=dict(role="assistant", stopReason="aborted", content=[]))]
        def write(value):
            path.write_text("".join(json.dumps(v) + "\n" for v in value))
        write(entries)
        adapter.verify_stopped_session(self.execution.workspace, reference, self.receipt)
        self.assertFalse(adapter.observe_delivery(self.execution, reference, self.receipt, None))
        for change in ("replaced", "newer", "branched", "partial"):
            with self.subTest(change=change):
                bad = copy.deepcopy(entries)
                if change == "replaced":
                    bad[0]["id"] = "replacement"
                elif change == "newer":
                    bad.append(dict(type="message", id="next", parentId="aborted",
                                    message=dict(role="user", content="new work")))
                elif change == "branched":
                    bad[-1]["parentId"] = "unrelated"
                write(bad)
                if change == "partial":
                    with path.open("a") as output:
                        output.write('{"partial":')
                with self.assertRaises(TaskError):
                    adapter.verify_stopped_session(self.execution.workspace, reference, self.receipt)


class InitialAbortTests(unittest.TestCase):
    command = bootstrap.BootstrapLoopTests.command
    pane_command = bootstrap.BootstrapLoopTests.pane_command
    worktrees = bootstrap.BootstrapLoopTests.worktrees
    select_adapter = bootstrap.BootstrapLoopTests.select_adapter
    implementation_adapter = bootstrap.BootstrapLoopTests.implementation_adapter
    update_base = bootstrap.BootstrapLoopTests.update_base
    setUp = bootstrap.BootstrapLoopTests.setUp

    def test_sessionless_initial_reservation_aborts_and_reuses_only_undelivered_allocation(self):
        with patch.object(PassDelivery, "attach", side_effect=KeyboardInterrupt):
            self.assertEqual(loop("DEV-7", timeout=1).state, "interrupted")
        original = self.store.read()[0]
        self.assertIsNone(original["delivery"]["receipt"])
        self.assertEqual(self.launches, [])
        before = snapshot(self.path, self.base, self.branch)
        with patch("task_start.loop_abort.ContextRegistry", return_value=self.registry), \
                patch("task_start.loop_abort.HerdrContexts", return_value=self.identities), \
                patch("task_start.loop_abort.stopped_loop_execution", return_value={"123": 7}):
            self.assertEqual(loop("DEV-7", action="abort").state, "aborted")
        self.assertEqual(snapshot(self.path, self.base, self.branch), before)
        preparation = list(self.preparation)
        result = loop("DEV-7", action="new", timeout=1)
        self.assertEqual(result.state, "clean", result.render())
        self.assertEqual(self.preparation, preparation)
        self.assertEqual(len(self.launches), 1)
        self.assertEqual(self.store.read()[0]["implementation"]["context_id"], "DEV-7-I1")
        self.assertNotIn(original["active_pass"]["pass_id"], self.impl_prompts[0])


class InterruptedControllerTests(unittest.TestCase):
    """Independent original agent process and signalled controller processes."""
    command = recovery.ProcessRecoveryTests.command
    pane_command = recovery.ProcessRecoveryTests.pane_command
    worktrees = recovery.ProcessRecoveryTests.worktrees
    select_adapter = recovery.ProcessRecoveryTests.select_adapter
    implementation_adapter = recovery.ProcessRecoveryTests.implementation_adapter
    update_base = recovery.ProcessRecoveryTests.update_base
    read_provider = recovery.ProcessRecoveryTests.read_provider
    save_provider = recovery.ProcessRecoveryTests.save_provider
    pane_snapshot = recovery.ProcessRecoveryTests.pane_snapshot
    herdr = recovery.ProcessRecoveryTests.herdr
    controller = recovery.ProcessRecoveryTests.controller
    wait_until = recovery.ProcessRecoveryTests.wait_until
    stop_children = recovery.ProcessRecoveryTests.stop_children

    def setUp(self):
        recovery.ProcessRecoveryTests.setUp(self)
        self.enterContext(patch("task_start.loop_abort.ContextRegistry", return_value=self.registry))
        self.enterContext(patch("task_start.loop_abort.HerdrContexts", return_value=self.identities))
        self.enterContext(patch("task_start.loop_abort.stopped_loop_execution", return_value={"123": 7}))
        self.enterContext(patch("task_start.loop_abort.adapter_for", side_effect=Codex))

    def request(self, method, params, **kwargs):
        response = recovery.ProcessRecoveryTests.request(self, method, params, **kwargs)
        if method == "thread/turns/list" and self.read_provider().get("interrupted"):
            response["data"][0]["status"] = "interrupted"
        return response

    def scenario(self, interruption):
        first, _ = self.controller("original")
        self.wait_until(lambda: self.read_provider() and self.read_provider()["prompts"])
        self.wait_until(lambda: self.registry.get("DEV-7-I1")["state"] == "active")
        self.wait_until(lambda: (self.root / "worker.pid").exists())
        self.store = LoopStore(self.path)
        original = self.store.read()[0]
        import shutil
        self.addCleanup(shutil.rmtree, original["delivery"]["directory"], True)
        if interruption in {"controller", "both"}:
            os.kill(first.pid, signal.SIGINT)
            first.join(5)
        if interruption == "controller":
            with self.assertRaises(TaskError):
                loop("DEV-7", action="abort")
        worker = int((self.root / "worker.pid").read_text())
        os.kill(worker, signal.SIGTERM)
        value = self.read_provider()
        value["interrupted"] = True
        value["pane"]["agent_status"] = "idle"  # Session remains open after interrupt.
        self.save_provider(value)
        if interruption == "agent":
            self.assertTrue(first.is_alive())
            with self.assertRaisesRegex(TaskError, "Ctrl\\+C"):
                loop("DEV-7", action="abort")
            os.kill(first.pid, signal.SIGINT)
            first.join(5)
        self.assertFalse(first.is_alive())
        with self.assertRaisesRegex(TaskError, "Quit its agent|Quit|live, missing, or uncertain"):
            loop("DEV-7", action="abort")
        # The fixture's provider history locator survives the runtime exit.
        value["pane"].update(agent=None, agent_status="unknown")
        self.save_provider(value)
        (self.path / "partial.txt").write_text("interrupted implementation")
        self.command(self.path, "add", "partial.txt")
        (self.path / "untracked.txt").write_text("preserve")
        before = snapshot(self.path, self.base, self.branch)
        self.assertEqual(loop("DEV-7", action="abort").state, "aborted")
        self.assertEqual(snapshot(self.path, self.base, self.branch), before)
        self.assertEqual(len(self.read_provider()["prompts"]), 1)
        self.assertEqual(self.store.abort_journal(original["run_id"])["original"]["active_pass"], original["active_pass"])
        self.assertFalse(self.store.read()[0]["abandonment"]["review_ready"])
        self.assertEqual(loop("DEV-7").state, "aborted")

    def test_controller_only_interrupt_does_not_authorize_abort_of_live_agent(self):
        self.scenario("controller")

    def test_agent_only_interrupt_requires_stopping_waiting_controller_then_quitting_session(self):
        self.scenario("agent")

    def test_both_interrupted_preserve_partial_implementation(self):
        self.scenario("both")


class RealShellTests(unittest.TestCase):
    """PTY shells, live background jobs and detached workers; no provider binaries."""
    def setUp(self):
        import tempfile
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name)
        self.master, slave = pty.openpty()
        def terminal():
            os.setsid()
            fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
        self.argv = ["/bin/bash", "--noprofile", "--norc", "-i"]
        self.shell = subprocess.Popen(self.argv, cwd=self.path, stdin=slave, stdout=slave, stderr=slave,
                                      preexec_fn=terminal)
        os.close(slave)
        self.addCleanup(self.stop_shell)
        self.read_prompt()
        # Sandboxes may stack /proc mounts. Exercise real PID/TTY/argv/ancestry
        # here with a controlled visibility observation; separate proc fixtures
        # exercise refusal for hidden/unreadable mount evidence.
        self.enterContext(patch("task_start.integration_process._process_visibility", return_value=b"controlled mounts"))

    def stop_shell(self):
        self.shell.terminate()
        try:
            self.shell.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self.shell.kill(); self.shell.wait()
        os.close(self.master)

    def read_prompt(self):
        deadline = time.monotonic() + 5
        data = b""
        while time.monotonic() < deadline:
            if select.select([self.master], [], [], 0.05)[0]:
                data += os.read(self.master, 65536)
                if data.endswith((b"# ", b"$ ")):
                    return
        self.fail("Disposable shell did not reach prompt")

    def proof(self):
        return stopped_loop_execution(self.path, {self.shell.pid: dict(cwd=self.path, argv=self.argv)})

    def test_idle_shell_does_not_hide_background_or_detached_checkout_process(self):
        self.assertIn(str(self.shell.pid), self.proof())
        os.write(self.master, b"sleep 30 &\n")
        self.read_prompt()
        with self.assertRaises(TaskError):
            self.proof()
        os.write(self.master, b"kill %1; wait %1\n")
        self.read_prompt()
        self.assertIn(str(self.shell.pid), self.proof())
        worker = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"], cwd=self.path,
                                  start_new_session=True)
        try:
            with self.assertRaisesRegex(TaskError, "may still use the task checkout"):
                self.proof()
        finally:
            worker.terminate(); worker.wait()
        self.assertIn(str(self.shell.pid), self.proof())
