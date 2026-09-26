import copy
from dataclasses import replace
import io
import json
import os
from pathlib import Path
import stat
import unittest
from unittest.mock import patch
from urllib.error import URLError
from urllib.parse import parse_qs, urlsplit

from task_start import TaskError, cli
from task_start.config import LocalConfig, Project
from task_start.linear import Linear
from task_start.workspace import Git, Herdr, run
import test_task_start as baseline


class CleanupTests(unittest.TestCase):
    """Real Git/ref/filesystem effects; only service and config boundaries are fake."""

    command = baseline.LocalGitIntegrationTests.command

    def setUp(self):
        baseline.LocalGitIntegrationTests.setUp(self)
        self.path = self.repo.parent / "arbitrary café checkout"
        self.branch = "dev-7-original-title"
        self.command(self.repo, "worktree", "add", "-b", self.branch, str(self.path))
        (self.path / "feature.txt").write_text("completed work\n")
        self.command(self.path, "add", "feature.txt")
        self.command(self.path, "commit", "-m", "task implementation")
        self.command(self.repo, "merge", "--ff-only", self.branch)
        self.git.save_scope(self.path, self.branch, "DEV-7", None)
        self.issue = replace(baseline.ISSUE, title="Renamed task", state_name="Shipped", state_type="completed")
        self.local = self.enterContext(patch("task_start.cli.load_local", return_value=LocalConfig(
            self.repo.parent, "placeholder")))
        self.enterContext(patch("task_start.cli.load_projects", return_value=[Project(
            self.issue.project, self.repo.name, "main")]))
        self.linear = self.enterContext(patch("task_start.cli.Linear")).return_value
        self.linear.get_issue.return_value = self.issue
        self.herdr = self.enterContext(patch.object(Herdr, "command", side_effect=self.list_worktrees))
        self.runner = self.enterContext(patch("task_start.workspace.run", wraps=run))
        for method in ("update_base", "remote_branches", "check_history"):
            self.enterContext(patch.object(Git, method, side_effect=AssertionError("Unexpected remote/base mutation")))
        self.enterContext(patch("task_start.cli.adapter_for", side_effect=AssertionError("Unexpected agent launch")))
        self.enterContext(patch("task_start.linear.urlopen", side_effect=AssertionError("Unexpected network access")))
        self.enterContext(patch("task_start.github.urlopen", side_effect=AssertionError("Unexpected network access")))

    def list_worktrees(self, operation, *args):
        self.assertEqual((operation, args), ("list", ()))
        entries = []
        for tree in self.git.worktrees():
            entries.append(dict(
                path=tree["worktree"], branch=tree.get("branch", "").removeprefix("refs/heads/") or None,
                label="Renamed display label", is_linked_worktree=Path(tree["worktree"]) != self.repo,
                is_bare="bare" in tree, is_detached="detached" in tree,
                is_prunable="prunable" in tree, open_workspace_id=None,
            ))
        return dict(source=dict(repo_root=str(self.repo)), worktrees=entries)

    def assert_refused(self, message):
        refs = self.command(self.repo, "show-ref")
        trees = self.git.worktrees()
        self.runner.reset_mock()
        with self.assertRaisesRegex(TaskError, message):
            cli.cleanup("DEV-7")
        self.assertEqual(self.command(self.repo, "show-ref"), refs)
        self.assertEqual(self.git.worktrees(), trees)
        self.assertTrue(self.path.is_dir())
        self.assertTrue(self.repo.is_dir())
        for call in self.runner.call_args_list:
            args = call.args[0][3:]
            self.assertNotIn("remove", args)
            self.assertNotIn("--delete", args)
        self.linear.start.assert_not_called()

    def test_success_removes_only_selected_local_state_and_repeat_is_noop(self):
        unrelated = self.repo.parent / "other-task"
        self.command(self.repo, "worktree", "add", "-b", "dev-70-unrelated", str(unrelated))
        self.command(self.remote, "branch", self.branch)
        remote_before = self.command(self.remote, "show-ref")
        self.command(self.repo, "fetch", "origin")
        # The task's real upstream is behind: deletion must use the expected
        # base, with an invocation-only override, without force or a config edit.
        self.command(self.repo, "branch", "--set-upstream-to=origin/" + self.branch, self.branch)
        base_before = self.command(self.repo, "rev-parse", "main")
        unrelated_before = self.command(unrelated, "rev-parse", "HEAD")
        with patch("sys.stdout", new=io.StringIO()) as output:
            self.assertEqual(cli.main(["cleanup", "dev-7"]), 0)
        self.assertIn(f"Removed worktree: {self.path}", output.getvalue())
        self.assertIn(f"Removed local branch: {self.branch}", output.getvalue())
        self.assertFalse(self.path.exists())
        self.assertEqual(self.git.branches("DEV-7"), [])
        self.assertEqual({Path(w["worktree"]) for w in self.git.worktrees()}, {self.repo, unrelated})
        self.assertEqual(self.command(self.repo, "rev-parse", "main"), base_before)
        self.assertEqual(self.command(unrelated, "rev-parse", "HEAD"), unrelated_before)
        self.assertEqual(self.command(self.remote, "show-ref"), remote_before)
        self.assertIn("Nothing to clean up", cli.cleanup("DEV-7"))
        self.local.assert_called_with(no_agent=True)
        self.linear.start.assert_not_called()
        commands = [call.args[0][3:] for call in self.runner.call_args_list]
        self.assertEqual([c for c in commands if c[:2] == ["worktree", "remove"]],
                         [["worktree", "remove", "--", str(self.path)]])
        self.assertTrue(any("--delete" in c and c[-1] == self.branch for c in commands))
        self.assertFalse(any(option in c for c in commands for option in
                             ("--force", "-D", "fetch", "push", "merge", "stash", "reset")))

    def test_dirty_tracked_staged_untracked_and_ignored_files_are_preserved(self):
        for state in ("tracked", "staged", "untracked", "ignored"):
            with self.subTest(state=state):
                name = {"tracked": "tracked.txt", "staged": "tracked.txt",
                        "untracked": "notes.txt", "ignored": "ignored.txt"}[state]
                file = self.path / name
                original = file.read_bytes() if file.exists() else None
                file.write_text("valuable local work\n")
                if state == "staged":
                    self.command(self.path, "add", name)
                self.assert_refused("dirty")
                self.assertEqual(file.read_text(), "valuable local work\n")
                if original is None:
                    file.unlink()
                else:
                    file.write_bytes(original)
                    if state == "staged":
                        self.command(self.path, "add", name)

    @unittest.skipUnless(os.name == "posix", "Requires POSIX executable-bit semantics")
    def test_filemode_config_cannot_hide_executable_bit_change(self):
        file = self.path / "tracked.txt"
        changed_mode = stat.S_IMODE(file.stat().st_mode) ^ stat.S_IXUSR
        file.chmod(changed_mode)
        if stat.S_IMODE(file.stat().st_mode) != changed_mode:
            self.skipTest("Filesystem does not preserve executable-bit changes")
        self.command(self.repo, "config", "core.fileMode", "false")
        self.assertEqual(self.command(self.path, "status", "--porcelain"), "")
        self.assert_refused("dirty")
        self.assertEqual(stat.S_IMODE(file.stat().st_mode), changed_mode)
        self.assertEqual(self.command(self.repo, "config", "--get", "core.fileMode"), "false")

    def test_symlinks_config_cannot_hide_symlink_replaced_with_regular_file(self):
        link = self.path / "tracked-link"
        try:
            link.symlink_to("tracked.txt")
        except (OSError, NotImplementedError):
            self.skipTest("Filesystem does not support creating symlinks")
        self.command(self.path, "-c", "core.symlinks=true", "add", "tracked-link")
        self.command(self.path, "commit", "-m", "tracked symlink")
        self.command(self.repo, "merge", "--ff-only", self.branch)
        self.command(self.repo, "config", "core.symlinks", "false")
        link.unlink()
        link.write_text("tracked.txt")
        self.assertEqual(self.command(self.path, "status", "--porcelain"), "")
        self.assert_refused("dirty")
        self.assertFalse(link.is_symlink())
        self.assertEqual(link.read_text(), "tracked.txt")
        self.assertEqual(self.command(self.repo, "config", "--get", "core.symlinks"), "false")

    def test_unmerged_task_refuses_even_if_merged_into_its_own_upstream(self):
        self.command(self.path, "commit", "--allow-empty", "-m", "unmerged work")
        self.command(self.repo, "branch", "other-base", self.branch)
        self.command(self.repo, "config", f"branch.{self.branch}.remote", ".")
        self.command(self.repo, "config", f"branch.{self.branch}.merge", "refs/heads/other-base")
        self.assert_refused("not fully merged into 'main'")

    def test_local_upstream_and_multiple_merge_values_cannot_override_expected_base(self):
        self.command(self.repo, "branch", "behind", self.before)
        self.command(self.repo, "config", f"branch.{self.branch}.remote", ".")
        self.command(self.repo, "config", "--add", f"branch.{self.branch}.merge", "refs/heads/behind")
        self.command(self.repo, "config", "--add", f"branch.{self.branch}.merge", "refs/heads/main")
        self.assertIn("cleanup complete", cli.cleanup("DEV-7"))
        self.assertEqual(self.command(self.repo, "rev-parse", "behind"), self.before)

    def test_index_flags_cannot_hide_local_modifications(self):
        for flag in ("assume-unchanged", "skip-worktree"):
            with self.subTest(flag=flag):
                self.command(self.path, "update-index", "--" + flag, "tracked.txt")
                (self.path / "tracked.txt").write_text("hidden local work\n")
                self.assert_refused("cleanliness cannot be verified")
                self.assertEqual((self.path / "tracked.txt").read_text(), "hidden local work\n")
                self.command(self.path, "update-index", "--no-" + flag, "tracked.txt")

    def test_squash_equivalence_does_not_allow_deleting_unmerged_commits(self):
        (self.path / "second-feature.txt").write_text("feature\n")
        self.command(self.path, "add", "second-feature.txt")
        self.command(self.path, "commit", "-m", "second feature")
        self.command(self.path, "commit", "--allow-empty", "-m", "task history")
        self.command(self.repo, "merge", "--squash", self.branch)
        self.command(self.repo, "commit", "-m", "squash merge")
        self.assert_refused("not fully merged")

    def test_incomplete_issue_is_refused_even_with_done_display_name(self):
        for state in ("started", "canceled", "", "unknown"):
            with self.subTest(state=state):
                self.linear.get_issue.return_value = replace(self.issue, state_name="Done", state_type=state)
                self.assert_refused("not completed")
        self.herdr.assert_not_called()

    def test_multiple_task_branches_refuse_before_any_removal(self):
        self.command(self.repo, "branch", "dev-7-second-slice")
        self.assert_refused("Ambiguous.*second-slice")

    def test_multiple_worktrees_refuse_before_any_removal(self):
        second = self.repo.parent / "second-task"
        self.command(self.repo, "worktree", "add", "-b", "dev-7-second", str(second))
        self.assert_refused("Ambiguous")
        self.assertTrue(second.is_dir())

    def test_herdr_mismatches_are_refused(self):
        original = self.list_worktrees("list")
        for field, value in (("path", str(self.repo)), ("branch", "dev-8-other"),
                             ("is_linked_worktree", False), ("is_prunable", True)):
            with self.subTest(field=field):
                listing = copy.deepcopy(original)
                listing["worktrees"][1][field] = value
                self.herdr.side_effect = None
                self.herdr.return_value = listing
                self.assert_refused("Git and Herdr|ambiguous or branch-only|permanent checkout")
        self.herdr.return_value = dict(original, source=dict(repo_root=str(self.remote)))
        self.assert_refused("Unexpected Herdr")
        self.herdr.return_value = dict(original, worktrees=original["worktrees"] + [original["worktrees"][1]])
        self.assert_refused("ambiguous or branch-only")

    def test_label_cannot_select_an_unrelated_branch(self):
        unrelated = self.repo.parent / "unrelated"
        self.command(self.repo, "worktree", "add", "-b", "dev-8-other", str(unrelated))
        listing = self.list_worktrees("list")
        listing["worktrees"][-1]["label"] = "DEV-7"
        self.herdr.side_effect = None
        self.herdr.return_value = listing
        self.assert_refused("Ambiguous")

    def test_locked_and_detached_worktrees_are_preserved(self):
        self.command(self.repo, "worktree", "lock", str(self.path))
        self.assert_refused("usable task worktree")
        self.command(self.repo, "worktree", "unlock", str(self.path))
        self.command(self.path, "checkout", "--detach")
        self.assert_refused("ambiguous or branch-only")

    def test_permanent_checkout_is_protected(self):
        # Put the only issue branch on the permanent checkout, as the base.
        self.command(self.path, "checkout", "--detach")
        self.command(self.repo, "checkout", self.branch)
        with patch("task_start.cli.load_projects", return_value=[Project(
                self.issue.project, self.repo.name, self.branch)]):
            self.assert_refused("permanent checkout")

    def test_linked_checkout_cannot_be_configured_as_permanent_repository(self):
        with patch("task_start.cli.repository_path", return_value=self.path):
            self.assert_refused("permanent checkout, not a linked worktree")

    def test_actual_checkout_must_belong_to_registered_repository(self):
        marker = self.path / ".git"
        original = marker.read_bytes()
        try:
            marker.write_text(f"gitdir: {self.remote / '.git'}\n")
            self.assert_refused("does not match its registered repository/branch")
        finally:
            marker.write_bytes(original)

    def test_nested_registered_worktree_is_protected(self):
        nested = self.path / "nested"
        self.command(self.repo, "worktree", "add", "-b", "dev-8-other", str(nested))
        self.assert_refused("Another registered worktree is inside")
        self.assertTrue(nested.is_dir())

    def test_scope_identity_must_be_valid(self):
        record = self.git.scope_file(self.path)
        data = json.loads(record.read_text())
        record.write_text(json.dumps(dict(data, identifier="DEV-8")))
        self.assert_refused("Invalid workspace scope")
        record.unlink()
        self.assert_refused("scope metadata is missing")

    def test_single_recorded_slice_can_be_cleaned_up(self):
        record = self.git.scope_file(self.path)
        data = json.loads(record.read_text())
        record.write_text(json.dumps(dict(data, slice="original-title")))
        self.assertIn("cleanup complete", cli.cleanup("DEV-7"))

    def test_clean_unfinished_operation_is_preserved(self):
        marker = self.git.scope_file(self.path).parent / "BISECT_LOG"
        marker.write_text("unfinished bisect\n")
        self.assert_refused("unfinished Git operation")
        self.assertEqual(marker.read_text(), "unfinished bisect\n")

    def test_submodule_is_refused_before_removal(self):
        self.command(self.path, "update-index", "--add", "--cacheinfo", "160000", self.before, "module")
        self.command(self.path, "commit", "-m", "gitlink")
        (self.path / "module").mkdir()
        self.command(self.repo, "merge", "--ff-only", self.branch)
        self.assert_refused("submodules")

    def test_target_change_during_validation_refuses_before_removal(self):
        listing = self.list_worktrees("list")
        changed = copy.deepcopy(listing)
        changed["worktrees"][1]["path"] = str(self.repo)
        self.herdr.side_effect = [listing, changed]
        self.assert_refused("permanent checkout")

    def test_new_work_during_validation_is_preserved(self):
        calls = 0

        def listing(operation, *args):
            nonlocal calls
            calls += 1
            if calls == 2:
                (self.path / "new-work.txt").write_text("new work\n")
            return self.list_worktrees(operation, *args)

        self.herdr.side_effect = listing
        self.assert_refused("dirty")
        self.assertEqual((self.path / "new-work.txt").read_text(), "new work\n")

    def test_removal_failure_keeps_branch_and_reports_failure(self):
        def fail_remove(args):
            if args[3:5] == ["worktree", "remove"]:
                raise TaskError("removal refused")
            return run(args)

        self.runner.side_effect = fail_remove
        with self.assertRaisesRegex(TaskError, "Worktree path is still present and registered.*Local branch.*was not deleted"):
            cli.cleanup("DEV-7")
        self.assertTrue(self.path.is_dir())
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertFalse(any("--delete" in c.args[0] for c in self.runner.call_args_list))

    def test_worktree_removed_but_confirmation_lost_keeps_branch(self):
        def uncertain_remove(args):
            output = run(args)
            if args[3:5] == ["worktree", "remove"]:
                raise TaskError("command confirmation lost")
            return output

        self.runner.side_effect = uncertain_remove
        with self.assertRaisesRegex(TaskError, "Confirmed removed worktree") as caught:
            cli.cleanup("DEV-7")
        self.assertIn(str(self.path), str(caught.exception))
        self.assertFalse(self.path.exists())
        self.assertNotIn(str(self.path), [tree["worktree"] for tree in self.git.worktrees()])
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertFalse(any("--delete" in c.args[0] for c in self.runner.call_args_list))

    def test_uncertain_removal_with_registration_only_reports_inconsistent_state(self):
        moved = self.repo.parent / "moved-task"

        def fail_remove(args):
            if args[3:5] == ["worktree", "remove"]:
                self.path.rename(moved)
                raise TaskError("removal result unknown")
            return run(args)

        self.runner.side_effect = fail_remove
        with self.assertRaisesRegex(TaskError, "Worktree removal state is unknown or inconsistent"):
            cli.cleanup("DEV-7")
        self.assertFalse(self.path.exists())
        self.assertIn(str(self.path), [tree["worktree"] for tree in self.git.worktrees()])
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertFalse(any("--delete" in c.args[0] for c in self.runner.call_args_list))

    def test_uncertain_removal_with_path_only_reports_inconsistent_state(self):
        def uncertain_remove(args):
            output = run(args)
            if args[3:5] == ["worktree", "remove"]:
                self.path.mkdir()
                raise TaskError("removal result unknown")
            return output

        self.runner.side_effect = uncertain_remove
        with self.assertRaisesRegex(TaskError, "Worktree removal state is unknown or inconsistent"):
            cli.cleanup("DEV-7")
        self.assertTrue(self.path.is_dir())
        self.assertNotIn(str(self.path), [tree["worktree"] for tree in self.git.worktrees()])
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertFalse(any("--delete" in c.args[0] for c in self.runner.call_args_list))

    def test_uncertain_removal_with_unreadable_post_state_reports_unknown(self):
        original_lstat = Path.lstat
        for failed_check in ("filesystem", "registration"):
            with self.subTest(failed_check=failed_check):
                removal_attempted = False
                inspected = set()

                def fail_remove(args):
                    nonlocal removal_attempted
                    if args[3:5] == ["worktree", "remove"]:
                        removal_attempted = True
                        raise TaskError("removal result unknown")
                    if removal_attempted and args[3:5] == ["worktree", "list"]:
                        inspected.add("registration")
                        if failed_check == "registration":
                            raise TaskError("cannot read Git registration")
                    return run(args)

                def inspect_path(path):
                    if removal_attempted and path == self.path:
                        inspected.add("filesystem")
                        if failed_check == "filesystem":
                            raise PermissionError("cannot inspect checkout")
                    return original_lstat(path)

                self.runner.side_effect = fail_remove
                with patch.object(Path, "lstat", inspect_path), self.assertRaisesRegex(
                        TaskError, "Worktree removal state is unknown or inconsistent"):
                    cli.cleanup("DEV-7")
                self.assertEqual(inspected, {"filesystem", "registration"})
                self.assertTrue(self.path.is_dir())
                self.assertEqual(self.git.branches("DEV-7"), [self.branch])
                self.assertFalse(any("--delete" in c.args[0] for c in self.runner.call_args_list))

    def test_branch_failure_reports_partial_cleanup_and_retry_preserves_branch(self):
        def fail_delete(args):
            if "--delete" in args:
                raise TaskError("branch is locked")
            return run(args)

        self.runner.side_effect = fail_delete
        config_before = (self.repo / ".git" / "config").read_bytes()
        with patch("sys.stderr", new=io.StringIO()) as output:
            self.assertEqual(cli.main(["cleanup", "DEV-7"]), 1)
        self.assertIn("cleanup is incomplete", output.getvalue())
        self.assertIn("Local branch", output.getvalue())
        self.assertFalse(self.path.exists())
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertEqual((self.repo / ".git" / "config").read_bytes(), config_before)
        with self.assertRaisesRegex(TaskError, "branch-only"):
            cli.cleanup("DEV-7")

    def test_uncertain_branch_result_does_not_claim_the_branch_was_preserved(self):
        def uncertain_delete(args):
            output = run(args)
            if "--delete" in args:
                raise TaskError("command did not confirm completion")
            return output

        self.runner.side_effect = uncertain_delete
        with self.assertRaisesRegex(TaskError, "Local branch deletion could not be confirmed") as caught:
            cli.cleanup("DEV-7")
        self.assertIn(f"Removed worktree: {self.path}", str(caught.exception))
        self.assertNotIn("was not deleted", str(caught.exception))
        self.assertEqual(self.git.branches("DEV-7"), [])
        self.assertIn("Nothing to clean up", cli.cleanup("DEV-7"))


class SquashCleanupTests(unittest.TestCase):
    """Actual squash/ancestry and removal checks with GitHub's HTTP boundary mocked."""

    command = CleanupTests.command
    list_worktrees = CleanupTests.list_worktrees
    assert_refused = CleanupTests.assert_refused

    def setUp(self):
        CleanupTests.setUp(self)
        (self.path / "squashed.txt").write_text("squashed feature\n")
        self.command(self.path, "add", "squashed.txt")
        self.command(self.path, "commit", "-m", "feature")
        self.command(self.path, "commit", "--allow-empty", "-m", "task history")
        self.head_commit = self.command(self.path, "rev-parse", "HEAD")
        self.command(self.repo, "merge", "--squash", self.branch)
        self.command(self.repo, "commit", "-m", "squashed PR")
        self.merge_commit = self.command(self.repo, "rev-parse", "HEAD")
        self.command(self.repo, "commit", "--allow-empty", "-m", "later base work")
        self.command(self.repo, "remote", "set-url", "origin", "git@github.com:owner/repo.git")
        self.pull = dict(number=22, state="closed", merged=True, merged_at="2026-09-26T12:00:00Z",
                         head=dict(ref=self.branch, sha=self.head_commit, repo=dict(full_name="owner/repo")),
                         base=dict(ref="main", repo=dict(full_name="owner/repo")),
                         merge_commit_sha=self.merge_commit)
        self.pulls, self.detail = [self.pull], self.pull
        self.api = self.enterContext(patch("task_start.github.urlopen", side_effect=self.respond))

    def respond(self, request, **kwargs):
        url = urlsplit(request.full_url)
        self.assertEqual(url.netloc, "api.github.com")
        self.assertEqual(request.get_method(), "GET")
        if url.path == "/repos/owner/repo/pulls":
            query = parse_qs(url.query)
            self.assertEqual(query["head"], ["owner:" + self.branch])
            self.assertEqual(query["state"], ["all"])
            payload = self.pulls
        else:
            self.assertEqual(url.path, "/repos/owner/repo/pulls/22")
            payload = self.detail
        return io.BytesIO(json.dumps(payload).encode())

    def test_verified_squash_merge_cleans_only_the_exact_local_task(self):
        self.assertNotEqual(self.command(self.repo, "rev-list", "--count", f"main..{self.branch}"), "0")
        self.command(self.repo, "branch", "dev-70-unrelated")
        refs_before = self.command(self.repo, "show-ref").splitlines()
        output = cli.cleanup("DEV-7")
        self.assertIn("cleanup complete", output)
        self.assertFalse(self.path.exists())
        self.assertEqual(self.git.branches("DEV-7"), [])
        self.assertEqual(self.command(self.repo, "show-ref").splitlines(),
                         [ref for ref in refs_before if not ref.endswith("refs/heads/" + self.branch)])
        self.assertEqual(self.api.call_count, 4)  # Complete evidence checked twice.
        commands = [call.args[0][3:] for call in self.runner.call_args_list]
        self.assertIn(["update-ref", "--no-deref", "-d", "refs/heads/" + self.branch, self.head_commit], commands)
        self.assertFalse(any(option in command for command in commands
                             for option in ("--force", "-D", "fetch", "push", "merge", "stash", "reset")))
        self.assertIn("Nothing to clean up", cli.cleanup("DEV-7"))

    def test_ancestor_merged_branch_needs_no_pr_evidence(self):
        self.command(self.repo, "merge", "--no-edit", self.branch)
        self.assertEqual(self.command(self.repo, "rev-list", "--count", f"main..{self.branch}"), "0")
        self.assertIn("cleanup complete", cli.cleanup("DEV-7"))
        self.api.assert_not_called()
        self.assertEqual(self.git.branches("DEV-7"), [])

    def test_unmerged_pr_refuses_even_if_its_merge_commit_field_is_in_base(self):
        for state in ("open", "closed"):
            with self.subTest(state=state):
                self.pull.update(state=state, merged=False, merged_at=None)
                self.assert_refused("not confirmed merged")

    def test_pr_for_different_head_branch_refuses(self):
        self.pull["head"]["ref"] = "dev-8-unrelated"
        self.assert_refused("unexpected GitHub response")

    def test_pr_head_must_equal_exact_local_tip(self):
        self.command(self.path, "commit", "--allow-empty", "-m", "unmerged work after PR")
        self.assert_refused("does not match the exact task head")

    def test_pr_base_and_repository_must_match(self):
        original = copy.deepcopy(self.pull)
        for side, field, value in (("base", "ref", "release"),
                                   ("base", "repo", dict(full_name="owner/other")),
                                   ("head", "repo", dict(full_name="fork/repo"))):
            with self.subTest(side=side, field=field):
                self.pull.clear()
                self.pull.update(copy.deepcopy(original))
                self.pull[side][field] = value
                self.assert_refused("does not match|unexpected GitHub response")

    def test_squash_merge_commit_must_be_present_in_expected_base(self):
        other = self.repo.parent / "other-base"
        self.command(self.repo, "worktree", "add", "-b", "other-base", str(other), self.before)
        self.command(other, "merge", "--squash", self.branch)
        self.command(other, "commit", "-m", "squashed elsewhere")
        self.pull["merge_commit_sha"] = self.command(other, "rev-parse", "HEAD")
        self.assert_refused("merge commit is not present in the expected base 'main'")
        self.assertTrue(other.is_dir())

    def test_missing_or_ambiguous_merge_evidence_refuses(self):
        for pulls in ([], [self.pull, dict(self.pull, number=23)]):
            with self.subTest(count=len(pulls)):
                self.pulls = pulls
                self.assert_refused("Missing or ambiguous merge evidence")

    def test_list_and_detail_must_agree(self):
        for changes in (dict(number=23), dict(merge_commit_sha=self.before)):
            with self.subTest(changes=changes):
                self.detail = dict(self.pull, **changes)
                self.assert_refused("does not match the exact task head")

    def test_unavailable_or_malformed_evidence_refuses(self):
        self.api.side_effect = URLError("unavailable")
        self.assert_refused("Cannot establish GitHub PR history")
        self.api.side_effect = self.respond
        for commit in (None, "", "main", "0" * 40):
            with self.subTest(commit=commit):
                self.pull["merge_commit_sha"] = commit
                self.assert_refused("could not be verified")

    def test_merge_evidence_is_rechecked_before_removal(self):
        requests = 0

        def changed_evidence(request, **kwargs):
            nonlocal requests
            requests += 1
            if requests == 3:
                self.pulls.append(dict(self.pull, number=23))
            return self.respond(request, **kwargs)

        self.api.side_effect = changed_evidence
        self.assert_refused("ambiguous merge evidence")

    def test_local_changes_during_final_api_lookup_are_preserved(self):
        requests = 0

        def changed_worktree(request, **kwargs):
            nonlocal requests
            requests += 1
            if requests == 4:
                (self.path / "ignored.txt").write_text("new local work\n")
            return self.respond(request, **kwargs)

        self.api.side_effect = changed_worktree
        self.assert_refused("dirty")
        self.assertEqual((self.path / "ignored.txt").read_text(), "new local work\n")

    def test_branch_switch_during_final_api_lookup_preserves_unrelated_checkout(self):
        unrelated = "dev-8-unrelated"
        self.command(self.repo, "branch", unrelated, self.head_commit)
        requests = 0

        def switch_branch(request, **kwargs):
            nonlocal requests
            requests += 1
            if requests == 4:
                self.command(self.path, "switch", unrelated)
                self.assertEqual(self.command(self.path, "status", "--porcelain"), "")
            return self.respond(request, **kwargs)

        self.api.side_effect = switch_branch
        with self.assertRaisesRegex(TaskError, "registration changed or is unusable; nothing was removed"):
            cli.cleanup("DEV-7")
        self.assertEqual(requests, 4)
        self.assertTrue(self.path.is_dir())
        self.assertEqual(self.command(self.path, "symbolic-ref", "HEAD"), "refs/heads/" + unrelated)
        self.assertEqual(self.command(self.path, "rev-parse", "HEAD"), self.head_commit)
        self.assertEqual(self.command(self.repo, "rev-parse", "refs/heads/" + self.branch), self.head_commit)
        self.assertEqual((self.path / "squashed.txt").read_text(), "squashed feature\n")
        trees = [tree for tree in self.git.worktrees() if tree["worktree"] == str(self.path)]
        self.assertEqual(len(trees), 1)
        self.assertEqual(trees[0]["branch"], "refs/heads/" + unrelated)
        self.assertFalse(any(operation in call.args[0] for call in self.runner.call_args_list
                             for operation in ("remove", "--delete", "update-ref")))

    def test_detach_during_final_api_lookup_preserves_detached_checkout(self):
        requests = 0
        detached_commit = None

        def detach_head(request, **kwargs):
            nonlocal requests, detached_commit
            requests += 1
            if requests == 4:
                self.command(self.path, "switch", "--detach")
                self.command(self.path, "commit", "--allow-empty", "-m", "detached local work")
                detached_commit = self.command(self.path, "rev-parse", "HEAD")
                self.assertEqual(self.command(self.path, "status", "--porcelain"), "")
            return self.respond(request, **kwargs)

        self.api.side_effect = detach_head
        with self.assertRaisesRegex(TaskError, "registration changed or is unusable; nothing was removed"):
            cli.cleanup("DEV-7")
        self.assertEqual(requests, 4)
        self.assertTrue(self.path.is_dir())
        self.assertIsNotNone(detached_commit)
        self.assertNotEqual(detached_commit, self.head_commit)
        self.assertEqual(self.command(self.path, "rev-parse", "--symbolic-full-name", "HEAD"), "HEAD")
        self.assertEqual(self.command(self.path, "rev-parse", "HEAD"), detached_commit)
        self.assertEqual(self.command(self.repo, "rev-parse", "refs/heads/" + self.branch), self.head_commit)
        self.assertEqual((self.path / "squashed.txt").read_text(), "squashed feature\n")
        trees = [tree for tree in self.git.worktrees() if tree["worktree"] == str(self.path)]
        self.assertEqual(len(trees), 1)
        self.assertIn("detached", trees[0])
        self.assertEqual(trees[0]["HEAD"], detached_commit)
        self.assertFalse(any(operation in call.args[0] for call in self.runner.call_args_list
                             for operation in ("remove", "--delete", "update-ref")))

    def test_squash_branch_checked_out_elsewhere_after_removal_is_preserved(self):
        other = self.repo.parent / "new-checkout"

        def reopen_after_remove(args):
            output = run(args)
            if args[3:5] == ["worktree", "remove"]:
                self.command(self.repo, "worktree", "add", str(other), self.branch)
            return output

        self.runner.side_effect = reopen_after_remove
        with self.assertRaisesRegex(TaskError, "still checked out in a registered worktree"):
            cli.cleanup("DEV-7")
        self.assertFalse(self.path.exists())
        self.assertTrue(other.is_dir())
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertFalse(any("update-ref" in call.args[0] for call in self.runner.call_args_list))

    def test_task_tip_change_during_final_api_lookup_refuses_before_removal(self):
        requests = 0

        def changed_head(request, **kwargs):
            nonlocal requests
            requests += 1
            if requests == 4:
                self.command(self.path, "commit", "--allow-empty", "-m", "new local work")
            return self.respond(request, **kwargs)

        self.api.side_effect = changed_head
        with self.assertRaisesRegex(TaskError, "changed during merge verification; nothing was removed"):
            cli.cleanup("DEV-7")
        self.assertTrue(self.path.is_dir())
        self.assertNotEqual(self.command(self.path, "rev-parse", "HEAD"), self.head_commit)
        self.assertEqual(self.git.branches("DEV-7"), [self.branch])
        self.assertFalse(any("remove" in call.args[0] for call in self.runner.call_args_list))

    def test_squash_branch_delete_checks_exact_tip_atomically(self):
        new_head = self.command(self.repo, "rev-parse", "main")

        def change_tip_before_delete(args):
            if args[3:6] == ["update-ref", "--no-deref", "-d"]:
                self.command(self.repo, "update-ref", "refs/heads/" + self.branch, new_head, self.head_commit)
            return run(args)

        self.runner.side_effect = change_tip_before_delete
        with self.assertRaisesRegex(TaskError, "Local branch deletion could not be confirmed"):
            cli.cleanup("DEV-7")
        self.assertFalse(self.path.exists())
        self.assertEqual(self.command(self.repo, "rev-parse", "refs/heads/" + self.branch), new_head)


class CleanupInputTests(unittest.TestCase):
    def test_cleanup_has_only_issue_argument(self):
        args = cli.parser().parse_args(["cleanup", "dev-7"])
        self.assertEqual(vars(args), dict(command="cleanup", issue="DEV-7"))
        for extra in ("--force", "--slice", "--no-agent", "--agent"):
            with patch("sys.stderr", new=io.StringIO()), self.assertRaises(SystemExit):
                cli.parser().parse_args(["cleanup", "DEV-7", extra])

    def test_linear_state_type_is_authoritative_and_required(self):
        data = baseline.issue_data()
        data["issue"]["state"].update(name="Shipped", type="completed")
        linear = Linear("placeholder")
        with patch.object(linear, "request", return_value=data) as request:
            self.assertEqual(linear.get_issue("DEV-7").state_type, "completed")
        self.assertIn("state { id name type }", request.call_args.args[0])
        for value in (None, "", 42):
            data["issue"]["state"]["type"] = value
            with patch.object(linear, "request", return_value=data), self.assertRaises(TaskError):
                linear.get_issue("DEV-7")


if __name__ == "__main__":
    unittest.main()
