"""Merged orphan recovery: real disposable Git/SQLite state, fake service boundaries."""

import copy
from dataclasses import replace
import io
import json
import unittest
from unittest.mock import patch

from task_start import TaskError, cli, github
from task_start.cleanup import DisposalStore, never_published
from task_start.contexts import ContextRegistry
from task_start.workspace import Git
import test_canceled_cleanup as forced


HISTORY_REMOTE = Git.history_remote


class MergedOrphanCleanupTests(unittest.TestCase):
    # Reuse the disposable fixture, without inheriting its unpublished test cases.
    command = forced.ForcedCleanupTests.command
    workspace_entry = forced.ForcedCleanupTests.workspace_entry
    list_worktrees = forced.ForcedCleanupTests.list_worktrees
    workspace_commands = forced.ForcedCleanupTests.workspace_commands
    process_info = forced.ForcedCleanupTests.process_info
    context = forced.ForcedCleanupTests.context
    journal_path = forced.ForcedCleanupTests.journal_path
    journal = forced.ForcedCleanupTests.journal
    assert_preserved = forced.ForcedCleanupTests.assert_preserved
    assert_disposed = forced.ForcedCleanupTests.assert_disposed
    retained_integration = forced.ForcedCleanupTests.retained_integration
    __enter__ = forced.ForcedCleanupTests.__enter__
    __exit__ = forced.ForcedCleanupTests.__exit__

    def setUp(self):
        forced.ForcedCleanupTests.setUp(self)
        self.issue = replace(self.issue, state_name="Done", state_type="completed")
        self.linear.get_issue.return_value = self.issue
        # A manual worktree has neither workflow scope nor publication metadata.
        self.scope = self.git.scope_file(self.path)
        self.scope.unlink()
        self.publication.path.unlink()
        (self.path / "merged.txt").write_text("merged implementation\n")
        self.command(self.path, "add", "merged.txt")
        self.command(self.path, "commit", "-m", "orphan implementation")
        self.head = self.command(self.path, "rev-parse", "HEAD")
        self.command(self.repo, "merge", "--squash", self.branch)
        self.command(self.repo, "commit", "-m", "merged PR")
        self.base = self.command(self.repo, "rev-parse", "HEAD")
        self.pull = dict(number=33, state="closed", merged=True, merged_at="2026-10-01T12:00:00Z",
                         merge_commit_sha=self.base,
                         head=dict(ref=self.branch, sha=self.head, repo=dict(full_name="example/project")),
                         base=dict(ref="main", repo=dict(full_name="example/project")))
        self.detail = None
        self.remote_branches.return_value = [self.branch]
        self.enterContext(patch.object(Git, "history_remote", HISTORY_REMOTE))
        self.enterContext(patch("task_start.workspace.merged_pull", github.merged_pull))
        self.github = self.enterContext(patch("task_start.github.request", side_effect=self.github_request))
        self.never_published = self.enterContext(patch("task_start.cleanup.never_published",
                                                     wraps=never_published))

    def github_request(self, path, branch, **kwargs):
        self.assertEqual(branch, self.branch)
        self.assertEqual(kwargs, {})  # No remote writes, even during retry.
        if "?" in path:
            self.assertTrue(path.startswith("example/project/pulls?state=all&"))
            return [copy.deepcopy(self.pull)]
        self.assertEqual(path, "example/project/pulls/33")
        return copy.deepcopy(self.detail if self.detail is not None else self.pull)

    def assert_refused(self, pattern):
        refs = self.command(self.repo, "show-ref")
        contexts = self.registry.list(include_retired=True)
        with self.assertRaisesRegex(TaskError, pattern) as caught:
            cli.cleanup("DEV-7", force=True)
        self.assert_preserved()
        self.assertEqual(self.command(self.repo, "show-ref"), refs)
        self.assertEqual(self.registry.list(include_retired=True), contexts)
        self.assertTrue(self.workspace_active)
        self.assertFalse(any(c.args[0] == "close" for c in self.herdr_workspace.call_args_list))
        self.assertIn("task cleanup DEV-7 --force", str(caught.exception))
        return str(caught.exception)

    def test_exact_merged_orphan_discards_dirty_files_and_preserves_remote_and_main(self):
        remote_before = self.command(self.remote, "show-ref")
        with patch.object(Git, "save_scope", side_effect=AssertionError("Must not forge scope")):
            result = cli.cleanup("DEV-7", force=True)
        self.assertIn("discarded", result)
        self.assert_disposed()
        self.assertEqual(self.command(self.repo, "rev-parse", "HEAD"), self.base)
        self.assertEqual(self.command(self.remote, "show-ref"), remote_before)
        self.assertEqual([c[0] for c in self.linear.mock_calls], ["get_issue"])
        self.never_published.assert_not_called()
        retained = self.journal()["merged"]
        self.assertIsNone(retained["scope_sha256"])
        self.assertEqual(retained["proof"]["branch_commit"], self.head)
        self.assertEqual(retained["proof"]["pull"]["number"], 33)
        self.assertIn("already discarded", cli.cleanup("DEV-7", force=True))

    def test_dev77_manual_checkout_without_any_workflow_context(self):
        self.registry.retire("DEV-7", self.repo, self.path,
                             endpoint="/tmp/test-herdr.sock", workspace_id=self.workspace_id)
        self.command(self.path, "branch", "-m", "dev-77-herdr-shell-recovery")
        self.branch = "dev-77-herdr-shell-recovery"
        self.pull["head"]["ref"] = self.branch
        self.workspace_label = "A manually renamed workspace"
        self.linear.get_issue.return_value = replace(self.issue, identifier="DEV-77")
        self.assertIn("DEV-77: local execution discarded", cli.cleanup("DEV-77", force=True))
        self.assertFalse(self.path.exists())
        self.assertFalse(self.git.branches("DEV-77"))
        self.assertFalse(self.workspace_active)
        self.assertEqual(self.registry.get(self.unrelated), self.unrelated_before)

    def test_ancestry_proof_does_not_need_a_pr_or_linear_done(self):
        self.command(self.repo, "merge", "--no-edit", self.branch)
        self.linear.get_issue.return_value = replace(self.issue, state_name="In Progress", state_type="started")
        cli.cleanup("DEV-7", force=True)
        self.assert_disposed()
        self.github.assert_not_called()
        self.assertIsNone(self.journal()["merged"]["proof"]["pull"])

    def test_published_recorded_default_and_slice_also_use_merged_proof(self):
        for scope in (None, "original-title"):
            with self.subTest(scope=scope), MergedOrphanCleanupTests() as case:
                case.git.save_scope(case.path, case.branch, "DEV-7", scope)
                cli.cleanup("DEV-7", force=True)
                case.assert_disposed()
                self.assertIsNotNone(case.journal()["merged"]["scope_sha256"])

    def test_normal_cleanup_remains_conservative_without_scope(self):
        with self.assertRaisesRegex(TaskError, "metadata is missing.*--force"):
            cli.cleanup("DEV-7")
        self.assert_preserved()
        self.assertTrue(self.workspace_active)

    def test_done_open_closed_unmerged_and_mismatched_pr_proofs_refuse(self):
        original = copy.deepcopy(self.pull)
        variants = [dict(state="open", merged=False, merged_at=None),
                    dict(state="closed", merged=False, merged_at=None),
                    dict(merged=False), dict(merge_commit_sha="f" * 40)]
        for changes in variants:
            with self.subTest(changes=changes):
                self.pull = dict(copy.deepcopy(original), **changes)
                self.assert_refused("not confirmed merged|merge commit is not present")
        for side, field, value in (("head", "sha", self.base), ("head", "ref", "dev-8-foreign"),
                                   ("base", "ref", "other"), ("head", "repo", dict(full_name="foreign/repo")),
                                   ("base", "repo", dict(full_name="foreign/repo"))):
            with self.subTest(side=side, field=field):
                self.pull = copy.deepcopy(original)
                self.pull[side][field] = value
                self.assert_refused("exact task head|unexpected GitHub response")

    def test_listing_detail_disagreement_and_unavailable_evidence_refuse(self):
        self.detail = dict(self.pull, merge_commit_sha=self.head)
        self.assert_refused("exact task head")
        self.detail = None
        self.github.side_effect = TaskError("GitHub unavailable; retry when access is restored")
        self.assert_refused("GitHub unavailable")

    def test_missing_or_multiple_prs_cannot_prove_the_merge(self):
        for pulls in ([], [self.pull, dict(self.pull, number=34)]):
            with self.subTest(count=len(pulls)):
                self.github.side_effect = lambda *args, **kwargs: copy.deepcopy(pulls)
                self.assert_refused("Missing or ambiguous merge evidence")

    def test_stale_base_preserves_all_state_and_gives_update_command(self):
        # Present object, absent from base: a stale base cannot authorize disposal.
        self.pull["merge_commit_sha"] = self.head
        message = self.assert_refused("merge commit is not present")
        self.assertIn("git pull --ff-only", message)

    def test_additional_default_or_slice_or_branch_only_candidate_refuses(self):
        for branch, linked in (("dev-7-second-title", True), ("dev-7-api-slice", True), ("dev-7-old", False)):
            with self.subTest(branch=branch), MergedOrphanCleanupTests() as case:
                if linked:
                    case.command(case.repo, "worktree", "add", "-b", branch, str(case.repo.parent / branch))
                else:
                    case.command(case.repo, "branch", branch)
                message = case.assert_refused("Ambiguous workspaces")
                self.assertIn("task contexts DEV-7 --all", message)
                self.assertIn(branch, message)

    def add_merged_slice(self):
        self.slice_branch = "dev-7-api-slice"
        self.slice_path = self.repo.parent / "second merged slice"
        self.command(self.repo, "worktree", "add", "-b", self.slice_branch, str(self.slice_path))
        self.git.save_scope(self.slice_path, self.slice_branch, "DEV-7", "api-slice")
        self.slice_workspace = self.workspace_entry("w-slice", self.slice_path, "DEV-7 / api-slice")
        self.extra_workspaces.append(self.slice_workspace)
        pane = dict(workspace_id="w-slice", pane_id="slice:p1", terminal_id="slice-term",
                    tab_id="slice:t1", cwd=str(self.slice_path), agent=None, agent_status="idle")
        self.slice_context = self.registry.allocate("DEV-7", "implementation", agent="codex",
            repository=str(self.repo), worktree=str(self.slice_path), endpoint="/tmp/test-herdr.sock",
            **{key: pane[key] for key in ("workspace_id", "pane_id", "terminal_id", "tab_id")})
        self.registry.update(self.slice_context, state="active")
        self.panes.append(pane)
        original_trees = self.list_worktrees
        original_workspaces = self.workspace_commands
        def trees(*args):
            result = original_trees(*args)
            if self.slice_workspace in self.extra_workspaces:
                for tree in result["worktrees"]:
                    if tree["path"] == str(self.slice_path):
                        tree["open_workspace_id"] = "w-slice"
            return result
        def workspaces(operation, *args):
            if operation == "close" and args == ("w-slice",):
                self.extra_workspaces.remove(self.slice_workspace)
                self.panes[:] = [p for p in self.panes if p["workspace_id"] != "w-slice"]
                return dict(type="workspace_closed", workspace_id="w-slice", workspace=self.slice_workspace)
            return original_workspaces(operation, *args)
        self.herdr.side_effect = trees
        self.herdr_workspace.side_effect = workspaces

    def test_documented_selection_sequence_cleans_two_merged_executions(self):
        self.add_merged_slice()
        message = self.assert_refused("Ambiguous workspaces")
        self.assertIn("--force --branch <exact-local-branch>", message)
        with self.assertRaisesRegex(TaskError, "Ambiguous workspaces") as refused:
            cli.cleanup("DEV-7")
        self.assertIn("--force --branch <exact-local-branch>", str(refused.exception))
        original_contexts = [c for c in self.registry.list("DEV-7") if c["context_id"] != self.slice_context]
        with patch("sys.stdout", new=io.StringIO()):
            self.assertEqual(cli.main(["cleanup", "DEV-7", "--force", "--branch", self.slice_branch]), 0)
        self.assert_preserved()
        self.assertFalse(self.slice_path.exists())
        self.assertNotIn(self.slice_branch, self.git.branches("DEV-7"))
        self.assertNotIn(self.slice_workspace, self.extra_workspaces)
        self.assertEqual(self.registry.get(self.slice_context)["state"], "retired")
        self.assertEqual(self.registry.list("DEV-7"), original_contexts)
        self.assertEqual(self.journal()["selected_branch"], self.slice_branch)
        self.assertIn("already discarded", cli.cleanup("DEV-7", force=True, branch=self.slice_branch))
        self.assert_preserved()
        cli.cleanup("DEV-7", force=True)
        self.assert_disposed()

    def test_selecting_orphan_preserves_a_separate_live_slice(self):
        self.add_merged_slice()
        self.panes[-1].update(agent="codex", agent_status="working")
        context_before = self.registry.get(self.slice_context)
        cli.cleanup("DEV-7", force=True, branch=self.branch)
        self.assertFalse(self.path.exists())
        self.assertTrue(self.slice_path.exists())
        self.assertIn(self.slice_branch, self.git.branches("DEV-7"))
        self.assertIn(self.slice_workspace, self.extra_workspaces)
        self.assertEqual(self.registry.get(self.slice_context), context_before)
        self.assertFalse(any(c.args[-1] == "slice:p1" for c in self.identities.command.call_args_list))

    def test_selection_does_not_authorize_unmerged_foreign_or_unknown_branches(self):
        self.add_merged_slice()
        for branch in ("main", "dev-8-unrelated", "dev-7-missing"):
            with self.subTest(branch=branch), self.assertRaisesRegex(TaskError, "belonging to this issue|No registered local"):
                cli.cleanup("DEV-7", force=True, branch=branch)
        self.pull.update(merged=False)
        with self.assertRaisesRegex(TaskError, "not confirmed merged"):
            cli.cleanup("DEV-7", force=True, branch=self.branch)
        self.assert_preserved()
        self.assertTrue(self.slice_path.exists())
        self.assertTrue(self.workspace_active)

    def test_selection_needs_force_and_keeps_foreign_claim_protection(self):
        with patch("sys.stderr", new=io.StringIO()), patch("task_start.cli.load_local") as config:
            self.assertEqual(cli.main(["cleanup", "DEV-7", "--branch", self.branch]), 1)
            config.assert_not_called()
        self.add_merged_slice()
        self.registry.allocate("DEV-8", "review", agent="codex", repository=str(self.repo),
                               worktree=str(self.path), endpoint="/tmp/test-herdr.sock", workspace_id=self.workspace_id)
        with self.assertRaisesRegex(TaskError, "Another task context claims"):
            cli.cleanup("DEV-7", force=True, branch=self.branch)
        self.assert_preserved()
        self.assertTrue(self.slice_path.exists())

    def test_selector_cannot_hide_a_duplicate_herdr_registration(self):
        self.add_merged_slice()
        self.extra_workspaces.append(self.workspace_entry("duplicate", self.path, "DEV-7 / duplicate"))
        with self.assertRaisesRegex(TaskError, "Another Herdr workspace claims"):
            cli.cleanup("DEV-7", force=True, branch=self.branch)
        self.assert_preserved()
        self.assertTrue(self.workspace_active)

    def test_other_workspace_must_have_independent_checkout_metadata(self):
        for workspace in ("issue label", "renamed", "closed"):
            with self.subTest(workspace=workspace), MergedOrphanCleanupTests() as case:
                case.add_merged_slice()
                if workspace == "renamed":
                    case.slice_workspace["label"] = "An arbitrary title"
                    case.panes[-1].update(agent="codex", agent_status="working")
                elif workspace == "closed":
                    case.extra_workspaces.remove(case.slice_workspace)
                    case.panes[:] = [p for p in case.panes if p["workspace_id"] != "w-slice"]
                (case.slice_path / ".git").write_bytes((case.path / ".git").read_bytes())
                # This inconsistent sibling is still usable before cleanup.
                self.assertEqual(case.command(case.slice_path, "rev-parse", "HEAD"), case.head)
                status = case.command(case.slice_path, "status", "--porcelain")
                contexts = case.registry.list(include_retired=True)
                panes = copy.deepcopy(case.panes)
                refs = case.command(case.repo, "show-ref")
                with self.assertRaisesRegex(TaskError, "Cleanup checkout does not match|registered checkout.*Git metadata"):
                    cli.cleanup("DEV-7", force=True, branch=case.branch)
                case.assert_preserved()
                self.assertEqual(case.command(case.slice_path, "rev-parse", "HEAD"), case.head)
                self.assertEqual(case.command(case.slice_path, "status", "--porcelain"), status)
                self.assertTrue(case.publication.directory.is_dir())
                self.assertEqual(case.command(case.repo, "show-ref"), refs)
                self.assertEqual(case.registry.list(include_retired=True), contexts)
                self.assertEqual(case.panes, panes)
                self.assertTrue(case.workspace_active)
                self.assertFalse(any(c.args[0] == "close" for c in case.herdr_workspace.call_args_list))

    def test_foreign_closed_checkout_metadata_is_checked_without_a_selector(self):
        sibling = self.repo.parent / "closed foreign checkout"
        self.command(self.repo, "worktree", "add", "-b", "dev-9-foreign", str(sibling))
        (sibling / ".git").write_bytes((self.path / ".git").read_bytes())
        self.assertEqual(self.command(sibling, "rev-parse", "HEAD"), self.head)
        self.assert_refused("registered checkout.*Git metadata")
        self.assertEqual(self.command(sibling, "rev-parse", "HEAD"), self.head)
        self.assertTrue(self.publication.directory.is_dir())

    def test_unreadable_registered_checkout_metadata_refuses(self):
        sibling = self.repo.parent / "uncertain foreign checkout"
        self.command(self.repo, "worktree", "add", "-b", "dev-9-foreign", str(sibling))
        (sibling / ".git").write_text("gitdir: missing-metadata\n")
        message = self.assert_refused("Cannot verify Git metadata used by registered checkout")
        self.assertIn(str(sibling), message)
        self.assertTrue(sibling.exists())

    def test_final_guard_rechecks_sibling_metadata_before_removal_and_on_retry(self):
        for boundary in ("workspace", "worktree", "worktree removing"):
            for workspace in ("renamed", "closed"):
                with self.subTest(boundary=boundary, workspace=workspace), MergedOrphanCleanupTests() as case:
                    case.add_merged_slice()
                    case.slice_workspace["label"] = "An arbitrary title"
                    case.panes[-1].update(agent="codex", agent_status="working")
                    if workspace == "closed":
                        case.extra_workspaces.remove(case.slice_workspace)
                        case.panes[:] = [p for p in case.panes if p["workspace_id"] != "w-slice"]
                    original_gitfile = (case.slice_path / ".git").read_bytes()
                    contexts = case.registry.list(include_retired=True)
                    original = Git.cleanup_merge
                    changed = False
                    def proof(git, *args, **kwargs):
                        nonlocal changed
                        result = original(git, *args, **kwargs)
                        if not changed and kwargs.get("head"):
                            state = case.journal()
                            if (boundary == "workspace" and state["workspace_state"] == "closing"
                                    or boundary == "worktree" and state["workspace_state"] == "closed"
                                    or boundary == "worktree removing" and state["worktree_state"] == "removing"):
                                changed = True
                                (case.slice_path / ".git").write_bytes((case.path / ".git").read_bytes())
                        return result
                    with patch.object(Git, "cleanup_merge", proof), self.assertRaisesRegex(
                            TaskError, "registered checkout.*Git metadata"):
                        cli.cleanup("DEV-7", force=True, branch=case.branch)
                    self.assertTrue(changed)
                    case.assert_preserved()
                    self.assertEqual(case.command(case.slice_path, "rev-parse", "HEAD"), case.head)
                    self.assertEqual(case.registry.list(include_retired=True), contexts)
                    self.assertEqual(case.workspace_active, boundary == "workspace")
                    journal = case.journal()
                    self.assertEqual(journal["state"], "pending")
                    with self.assertRaisesRegex(TaskError, "registered checkout.*Git metadata"):
                        cli.cleanup("DEV-7", force=True)
                    self.assertEqual(case.journal(), journal)
                    case.assert_preserved()
                    # Restoring the fixture's independent binding lets the exact
                    # journal finish while preserving the sibling's execution.
                    (case.slice_path / ".git").write_bytes(original_gitfile)
                    cli.cleanup("DEV-7", force=True)
                    self.assertFalse(case.path.exists())
                    self.assertEqual(case.journal()["state"], "complete")
                    self.assertEqual(case.command(case.slice_path, "rev-parse", "HEAD"), case.base)
                    self.assertEqual(case.registry.get(case.slice_context),
                                     next(c for c in contexts if c["context_id"] == case.slice_context))
                    if workspace == "renamed":
                        self.assertIn(case.slice_workspace, case.extra_workspaces)
                        self.assertEqual(case.panes[-1]["agent_status"], "working")

    def test_pending_selection_cannot_switch_targets_and_resume_needs_no_selector(self):
        self.add_merged_slice()
        self.workspace_close_error = TaskError("close interrupted")
        with self.assertRaisesRegex(TaskError, "close interrupted"):
            cli.cleanup("DEV-7", force=True, branch=self.branch)
        before = self.journal()
        with self.assertRaisesRegex(TaskError, "Pending cleanup owns"):
            cli.cleanup("DEV-7", force=True, branch=self.slice_branch)
        self.assertEqual(self.journal(), before)
        self.workspace_close_error = None
        cli.cleanup("DEV-7", force=True)
        self.assertFalse(self.path.exists())
        self.assertTrue(self.slice_path.exists())
        self.assertEqual(self.registry.get(self.slice_context)["state"], "active")

    def test_explicit_same_branch_recovers_pending_merge_after_an_extra_candidate_appears(self):
        self.workspace_close_error = TaskError("close interrupted")
        with self.assertRaisesRegex(TaskError, "close interrupted"):
            cli.cleanup("DEV-7", force=True)
        self.add_merged_slice()
        self.workspace_close_error = None
        with self.assertRaisesRegex(TaskError, "Another task branch|Another Herdr workspace"):
            cli.cleanup("DEV-7", force=True)
        cli.cleanup("DEV-7", force=True, branch=self.branch)
        self.assertFalse(self.path.exists())
        self.assertTrue(self.slice_path.exists())
        self.assertEqual(self.journal()["selected_branch"], self.branch)

    def test_missing_open_workspace_requires_supported_herdr_open(self):
        self.workspace_id = None
        self.assert_refused("herdr worktree open.*--path")

    def test_foreign_herdr_identity_and_context_are_not_ownership_proof(self):
        original = self.workspace_entry
        for field, value in (("repo_root", str(self.other)), ("repo_key", str(self.other / ".git")),
                             ("checkout_path", str(self.other)), ("is_linked_worktree", False)):
            def wrong(*args, **kwargs):
                entry = original(*args, **kwargs)
                entry["worktree"][field] = value
                return entry
            with self.subTest(field=field), patch.object(self, "workspace_entry", side_effect=wrong):
                self.assert_refused("does not exactly match")
        self.registry.allocate("DEV-8", "review", agent="codex", repository=str(self.repo),
                               worktree=str(self.path), endpoint="/tmp/test-herdr.sock", workspace_id=self.workspace_id)
        self.assert_refused("Another task context claims")

    def test_other_slice_workspace_or_foreign_issue_label_refuses(self):
        self.extra_workspaces.append(self.workspace_entry("another-slice", self.other, "DEV-7 / api"))
        self.assert_refused("Another Herdr workspace claims")
        self.extra_workspaces.pop()
        self.workspace_label = "DEV-8"
        self.assert_refused("identifies another issue")

    def test_git_metadata_backlink_and_herdr_registration_must_agree(self):
        backlink = self.publication.directory / "gitdir"
        original = backlink.read_bytes()
        backlink.write_text(str(self.other / ".git") + "\n")
        self.assert_refused("metadata points to another worktree|single usable task worktree")
        backlink.write_bytes(original)
        get_trees = self.list_worktrees
        def wrong(*args):
            result = get_trees(*args)
            for tree in result["worktrees"]:
                if tree["path"] == str(self.path):
                    tree["path"] = str(self.other)
            return result
        self.herdr.side_effect = wrong
        self.assert_refused("single usable task worktree")

    def test_herdr_symlink_alias_is_not_a_cleanup_selector(self):
        alias = self.repo.parent / "alias"
        alias.symlink_to(self.path, target_is_directory=True)
        get_trees = self.list_worktrees
        def aliased(*args):
            result = get_trees(*args)
            for tree in result["worktrees"]:
                if tree["path"] == str(self.path):
                    tree["path"] = str(alias)
            return result
        self.herdr.side_effect = aliased
        self.assert_refused("paths are aliased")
        self.assertTrue(alias.is_symlink())

    def test_conflicting_existing_scope_and_private_review_identity_refuse(self):
        self.scope.write_text(json.dumps(dict(version=1, identifier="DEV-8", branch=self.branch, slice=None)))
        self.assert_refused("Invalid workspace scope")
        self.scope.unlink()
        saved = self.publication.read()
        saved["acceptance"] = dict(issue="DEV-8")
        self.publication.write(saved)
        self.assert_refused("Private review/integration identity")

    def published_record(self):
        saved = self.publication.read()
        binding = dict(issue="DEV-7", repository=str(self.repo), worktree=str(self.path),
                       branch=self.branch, base_branch="main")
        identity = dict(remote="origin", repository="example/project")
        saved["acceptance"] = dict(binding, pass_id="reviewed")
        saved["intent"] = dict(identity, pass_id="reviewed", publishing_head=self.head)
        saved["publication_history"] = dict(version=1, binding=binding, identity=identity,
                                            state="published", head=self.head)
        return saved

    def test_consistent_private_publication_is_retained_as_disposal_evidence(self):
        self.publication.write(self.published_record())
        cli.cleanup("DEV-7", force=True)
        self.assert_disposed()
        self.assertEqual(len(self.journal()["publication_sha256"]), 64)
        self.assertIsNotNone(self.journal()["merged"]["proof"]["pull"])

    def test_foreign_publication_binding_and_intent_refuse_even_with_merge_proof(self):
        for source in ("publication_history", "intent"):
            with self.subTest(source=source):
                saved = self.published_record()
                if source == "publication_history":
                    saved[source]["binding"]["issue"] = "DEV-8"
                else:
                    saved[source]["repository"] = "foreign/repo"
                self.publication.write(saved)
                self.assert_refused("Publication history conflicts|Private publication intent")

    def test_private_publication_of_another_head_refuses(self):
        for source, key in (("publication_history", "head"), ("intent", "publishing_head")):
            with self.subTest(source=source):
                saved = self.published_record()
                saved[source][key] = self.base
                self.publication.write(saved)
                self.assert_refused("Private publication head differs")

    def test_worktree_specific_wrong_remote_and_foreign_remote_refuse(self):
        self.command(self.repo, "config", "extensions.worktreeConfig", "true")
        self.command(self.path, "config", "--worktree", "remote.origin.url", "git@github.com:foreign/repo.git")
        self.assert_refused("differs from the approved|one fetch/push destination")
        self.command(self.path, "config", "--worktree", "--unset", "remote.origin.url")
        self.command(self.repo, "remote", "add", "foreign", "git@github.com:foreign/repo.git")
        self.assert_refused("different repositories")

    def test_live_or_uncertain_runtime_refuses_before_any_removal(self):
        self.identities.command.side_effect = None
        for info in ({}, dict(process_info=dict(pane_id="p1", shell_pid=101,
                        foreground_process_group_id=999, foreground_processes=[]))):
            with self.subTest(info=info):
                self.identities.command.return_value = info
                self.assert_refused("live or uncertain")

    def test_mount_and_nested_repository_protect_dirty_contents(self):
        self.mountinfo.write_bytes(forced.mount_table(self.path / "tracked.txt"))
        self.assert_refused("mount boundary")
        self.mountinfo.write_bytes(forced.mount_table())
        nested = self.path / "foreign"
        nested.mkdir()
        self.command(nested, "init", "--bare")
        self.assert_refused("Nested bare repository")

    def test_scope_and_git_marker_symlinks_are_not_followed(self):
        outside = self.repo.parent / "scope.json"
        outside.write_text(json.dumps(dict(version=1, identifier="DEV-7", branch=self.branch, slice=None)))
        self.scope.symlink_to(outside)
        self.assert_refused("scope metadata is not a regular file")
        self.scope.unlink()
        marker = self.path / ".git"
        outside.write_bytes(marker.read_bytes())
        marker.unlink()
        marker.symlink_to(outside)
        self.assert_refused("does not match its registered")
        self.assertTrue(outside.exists())

    def test_scope_created_after_proof_and_head_base_or_pr_drift_stop_before_close(self):
        for change in ("scope", "head", "base", "pr", "candidate", "runtime"):
            with self.subTest(change=change), MergedOrphanCleanupTests() as case:
                original = DisposalStore.write
                changed = False
                def write(store, value, **kwargs):
                    nonlocal changed
                    original(store, value, **kwargs)
                    if not changed and "merged" in value:
                        changed = True
                        if change == "scope":
                            case.git.save_scope(case.path, case.branch, "DEV-7", None)
                        elif change == "head":
                            case.command(case.path, "commit", "--allow-empty", "-m", "new work")
                        elif change == "base":
                            case.command(case.repo, "commit", "--allow-empty", "-m", "base advanced")
                        elif change == "pr":
                            case.pull.update(merged=False)
                        elif change == "candidate":
                            case.command(case.repo, "branch", "dev-7-concurrent-slice")
                        else:
                            case.process.side_effect = TaskError("live process appeared")
                with patch.object(DisposalStore, "write", write), self.assertRaisesRegex(TaskError, "changed|not confirmed merged|branch appeared|live process"):
                    cli.cleanup("DEV-7", force=True)
                case.assert_preserved()
                self.assertTrue(case.workspace_active)
                self.assertEqual(case.journal()["workspace_state"], "pending")

    def test_final_guard_detects_base_movement_after_merge_lookup(self):
        original = Git.cleanup_merge
        changed = False
        def proof(git, *args, **kwargs):
            nonlocal changed
            result = original(git, *args, **kwargs)
            # The first proof prepares the journal; later calls guard removal.
            if kwargs.get("head") and not changed:
                changed = True
                self.command(self.repo, "commit", "--allow-empty", "-m", "concurrent base")
            return result
        with patch.object(Git, "cleanup_merge", proof), self.assertRaisesRegex(TaskError, "Base branch changed"):
            cli.cleanup("DEV-7", force=True)
        self.assert_preserved()
        self.assertTrue(self.workspace_active)

    def test_corrupt_merged_journal_does_not_authorize_retry(self):
        self.workspace_close_error = TaskError("close interrupted")
        with self.assertRaisesRegex(TaskError, "close interrupted"):
            cli.cleanup("DEV-7", force=True)
        self.herdr_workspace.reset_mock()
        self.workspace_close_error = None
        path = self.journal_path()
        record = self.journal()
        for proof in (None, dict(proof={}, scope_sha256=None),
                      dict(proof=dict(record["merged"]["proof"], branch_commit=self.base), scope_sha256=None)):
            with self.subTest(proof=proof):
                path.write_text(json.dumps(dict(record, merged=proof)))
                self.assert_refused("journal identity is invalid")

    def test_interruption_before_and_after_every_removal_boundary_is_retryable(self):
        for boundary in ("workspace", "worktree", "branch", "contexts", "complete"):
            for after in (False, True):
                with self.subTest(boundary=boundary, after=after), MergedOrphanCleanupTests() as case:
                    original_git = Git.command
                    original_retire = ContextRegistry.retire_execution
                    original_write = DisposalStore.write
                    original_workspace = case.workspace_commands
                    def interrupt(action):
                        if after:
                            action()
                        raise TaskError("simulated interruption")
                    def git_command(git, *args):
                        if (boundary == "worktree" and args[:3] == ("worktree", "remove", "--force")
                                or boundary == "branch" and args[:3] == ("update-ref", "--no-deref", "-d")):
                            return interrupt(lambda: original_git(git, *args))
                        return original_git(git, *args)
                    def workspace(operation, *args):
                        if boundary == "workspace" and operation == "close":
                            return interrupt(lambda: original_workspace(operation, *args))
                        return original_workspace(operation, *args)
                    def retire(registry, *args):
                        if boundary == "contexts":
                            return interrupt(lambda: original_retire(registry, *args))
                        return original_retire(registry, *args)
                    def write(store, value, **kwargs):
                        if boundary == "complete" and value["state"] == "complete":
                            return interrupt(lambda: original_write(store, value, **kwargs))
                        return original_write(store, value, **kwargs)
                    case.herdr_workspace.side_effect = workspace
                    with patch.object(Git, "command", git_command), patch.object(ContextRegistry, "retire_execution", retire), \
                            patch.object(DisposalStore, "write", write), self.assertRaisesRegex(TaskError, "simulated interruption"):
                        cli.cleanup("DEV-7", force=True)
                    case.herdr_workspace.side_effect = original_workspace
                    cli.cleanup("DEV-7", force=True)
                    case.assert_disposed()

    def test_interrupted_integration_artifacts_retain_merge_proof_and_retry(self):
        self.retained_integration()
        original = forced.shutil.rmtree
        def remove(path, *args, **kwargs):
            original(path, *args, **kwargs)
            raise TaskError("artifact acknowledgement lost")
        with patch("task_start.cleanup.shutil.rmtree", remove), self.assertRaisesRegex(TaskError, "acknowledgement"):
            cli.cleanup("DEV-7", force=True)
        self.assertEqual(self.journal()["merged"]["proof"]["branch_commit"], self.head)
        cli.cleanup("DEV-7", force=True)
        self.assert_disposed()
        self.assertFalse(self.isolated.parent.exists())
        self.assertFalse(self.output.parent.exists())

    def test_retry_revalidates_merge_and_reused_paths(self):
        original = Git.command
        def fail_branch(git, *args):
            if args[:3] == ("update-ref", "--no-deref", "-d"):
                raise TaskError("branch deletion interrupted")
            return original(git, *args)
        with patch.object(Git, "command", fail_branch), self.assertRaisesRegex(TaskError, "interrupted"):
            cli.cleanup("DEV-7", force=True)
        self.assertFalse(self.path.exists())
        self.pull.update(merged=False)
        with self.assertRaisesRegex(TaskError, "not confirmed merged"):
            cli.cleanup("DEV-7", force=True)
        self.assertIn(self.branch, self.git.branches("DEV-7"))
        self.pull.update(merged=True)
        self.path.mkdir()
        (self.path / "valuable.txt").write_text("replacement execution")
        with self.assertRaisesRegex(TaskError, "reused|replaced"):
            cli.cleanup("DEV-7", force=True)
        self.assertEqual((self.path / "valuable.txt").read_text(), "replacement execution")

    def prune_head_after_interrupted_branch_removal(self):
        with patch.object(ContextRegistry, "retire_execution", side_effect=TaskError("retirement interrupted")):
            with self.assertRaisesRegex(TaskError, "retirement interrupted"):
                cli.cleanup("DEV-7", force=True)
        self.assertFalse(self.path.exists())
        self.assertFalse(self.git.branches("DEV-7"))
        self.assertEqual(self.journal()["branch_state"], "removed")
        self.assertEqual(self.command(self.repo, "for-each-ref", "--contains", self.head), "")
        # Actual object expiration is confined to this test's disposable repo.
        self.command(self.repo, "reflog", "expire", "--expire=now", "--all")
        self.command(self.repo, "gc", "--prune=now")
        with self.assertRaises(TaskError):
            self.git.command("cat-file", "-e", self.head)
        self.github.reset_mock()
        self.runner.reset_mock()

    def test_retry_after_squash_head_pruning_finishes_from_exact_pr_proof(self):
        self.prune_head_after_interrupted_branch_removal()
        cli.cleanup("DEV-7", force=True)
        self.assert_disposed()
        self.assertTrue(self.github.called)
        self.assertFalse(any(f"{self.base}..{self.head}" in c.args[0] for c in self.runner.call_args_list))
        self.assertEqual(self.journal()["merged"]["proof"]["pull"]["head_commit"], self.head)

    def test_pruned_head_retry_still_requires_matching_current_merge_evidence(self):
        self.prune_head_after_interrupted_branch_removal()
        original = copy.deepcopy(self.pull)
        for change in ("head", "state", "result", "repository"):
            self.pull = copy.deepcopy(original)
            if change == "head":
                self.pull["head"]["sha"] = self.base
            elif change == "state":
                self.pull.update(merged=False)
            elif change == "result":
                self.pull["merge_commit_sha"] = "f" * 40
            else:
                self.pull["head"]["repo"]["full_name"] = "foreign/repo"
            with self.subTest(change=change), self.assertRaises(TaskError):
                cli.cleanup("DEV-7", force=True)
            self.assertEqual(self.journal()["state"], "pending")
            self.assertEqual(len(self.registry.list("DEV-7")), 2)
        self.pull = original
        cli.cleanup("DEV-7", force=True)
        self.assert_disposed()


if __name__ == "__main__":
    unittest.main()
