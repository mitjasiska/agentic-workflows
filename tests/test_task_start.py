import copy
from dataclasses import replace
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError

from task_start import TaskError
from task_start import cli
from task_start.config import LocalConfig, Project, load_local, load_projects, repository_path, resolve_project
from task_start.linear import Issue, Linear
from task_start.workspace import Git, Herdr, branch_name, run


PROJECT = Project("KnowledgeBase", "knowledge-base", "main")
ISSUE = Issue("issue-id", "DEV-7", "Add ingestion CLI", "KnowledgeBase", "todo", "Todo", "started")
# Deliberately not an API key; no real credentials are used by these tests.
LOCAL = LocalConfig(Path("/projects"), "test-placeholder")


def issue_data():
    return {"issue": {
        "id": "issue-id", "identifier": "DEV-7", "title": "Add ingestion CLI",
        "project": {"id": "project-id", "name": "KnowledgeBase"},
        "state": {"id": "todo", "name": "Todo"},
        "team": {"id": "team-id", "states": {
            "nodes": [{"id": "todo", "name": "Todo"}, {"id": "started", "name": "In Progress"}],
            "pageInfo": {"hasNextPage": False},
        }},
    }}


class ParsingTests(unittest.TestCase):
    def test_start(self):
        args = cli.parser().parse_args(["start", "DEV-7"])
        self.assertEqual((args.command, args.issue), ("start", "DEV-7"))
        self.assertEqual(cli.parser().parse_args(["start", "dev-7"]).issue, "DEV-7")

    def test_invalid_identifiers_and_commands(self):
        for args in (["start", value] for value in ["DEV", "DEV-0", "DEV-07", "-7", "../DEV-7", "DEV-7;ls", "DEV-7\n"]):
            with self.subTest(args=args), patch("sys.stderr", new=io.StringIO()), self.assertRaises(SystemExit):
                cli.parser().parse_args(args)
        for command in ["review", "cleanup"]:
            with patch("sys.stderr", new=io.StringIO()), self.assertRaises(SystemExit):
                cli.parser().parse_args([command, "DEV-7"])

    def test_branch_names(self):
        for title, expected in [
            ("Add unified cross-platform source ingestion CLI", "add-unified-cross-platform-source-ingestion-cli"),
            ("  Fix: café / path..lock @{x} ", "fix-cafe-path-lock-x"),
            ("💡", ""), ("A" * 200, "a" * 100),
        ]:
            with self.subTest(title=title):
                self.assertEqual(branch_name("DEV-7", title), "dev-7" + ("-" + expected if expected else ""))


class ConfigTests(unittest.TestCase):
    def test_portable_registry_and_resolution(self):
        projects = load_projects()
        self.assertEqual(resolve_project(projects, "KnowledgeBase"), PROJECT)
        self.assertEqual(repository_path(LOCAL, PROJECT), Path("/projects/knowledge-base").resolve())
        with self.assertRaisesRegex(TaskError, "exactly one"):
            resolve_project(projects, "Unknown")
        with self.assertRaises(TaskError):
            resolve_project([PROJECT, PROJECT], "KnowledgeBase")

    def test_local_config(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "config.toml"
            path.write_text(f'projects_root = {json.dumps(temp)}\n[linear]\napi_key = "test-placeholder"\n')
            self.assertEqual(load_local(path), LocalConfig(Path(temp).resolve(), "test-placeholder"))
            self.assertNotIn("test-placeholder", repr(load_local(path)))

    def test_missing_local_config(self):
        with tempfile.TemporaryDirectory() as temp:
            with patch("task_start.config.Path.home", return_value=Path(temp)), self.assertRaisesRegex(TaskError, "Config missing"):
                load_local()

    def test_tilde_root_and_invalid_utf8(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "config.toml"
            path.write_text('projects_root = "~/projects"\n[linear]\napi_key = "test-placeholder"')
            self.assertEqual(load_local(path).projects_root, (Path.home() / "projects").resolve())
            path.write_bytes(b"\xff")
            with self.assertRaisesRegex(TaskError, "valid TOML"):
                load_local(path)

    def test_invalid_local_config(self):
        for content, message in [
            ('projects_root = "/projects"', "api_key"),
            ('projects_root = "/projects"\n[linear]\napi_key = ""', "api_key"),
            ('projects_root = "relative"\n[linear]\napi_key = "test-placeholder"', "absolute"),
            ('projects_root = "/projects"\n[linear]\napi_key = 42', "api_key"),
            ('[linear]\napi_key = "test placeholder"', "whitespace"),
            ('[linear]\napi_key = "test-placeholder', "valid TOML"),
        ]:
            with self.subTest(content=content), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / "config.toml"
                path.write_text(content)
                with self.assertRaisesRegex(TaskError, message) as caught:
                    load_local(path)
                self.assertNotIn("test-placeholder", str(caught.exception))

    def test_invalid_registry(self):
        for name in ["../escape", "/absolute", "C:/projects", "a/b", "a\\b", ".."]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                path = Path(temp) / "projects.toml"
                path.write_text('[projects.test]\nlinear_project = "Test"\nbase_branch = "main"\nrepo_name = ' + json.dumps(name))
                with self.assertRaises(TaskError):
                    load_projects(path)


class LinearTests(unittest.TestCase):
    def setUp(self):
        self.linear = Linear("test-placeholder")

    def response(self, payload):
        return patch("task_start.linear.urlopen", return_value=io.BytesIO(json.dumps(payload).encode()))

    def test_issue_retrieval_http_boundary(self):
        with self.response({"data": issue_data()}) as mocked:
            self.assertEqual(self.linear.get_issue("DEV-7"), ISSUE)
        request = mocked.call_args.args[0]
        self.assertEqual(request.full_url, "https://api.linear.app/graphql")
        self.assertEqual(request.get_method(), "POST")
        self.assertEqual(request.get_header("Authorization"), "test-placeholder")
        self.assertEqual(json.loads(request.data)["variables"], {"id": "DEV-7"})
        self.assertEqual(mocked.call_args.kwargs["timeout"], 30)

    def test_invalid_response(self):
        for payload in [[], {}, {"data": None}, {"errors": [{"message": "test-placeholder"}]},
                        {"data": issue_data(), "errors": [{"message": "partial failure"}]},
                        {"data": {}}, {"data": {"issue": []}}, {"data": {"issue": None}}]:
            with self.subTest(payload=payload), self.response(payload), self.assertRaises(TaskError) as caught:
                self.linear.get_issue("DEV-7")
            self.assertNotIn("test-placeholder", str(caught.exception))
        with patch("task_start.linear.urlopen", return_value=io.BytesIO(b"not json")), self.assertRaises(TaskError):
            self.linear.get_issue("DEV-7")

    def test_transport_errors_are_sanitized(self):
        for error in [HTTPError("url", 401, "test-placeholder", {}, None), URLError("test-placeholder"), TimeoutError()]:
            with patch("task_start.linear.urlopen", side_effect=error), self.assertRaises(TaskError) as caught:
                self.linear.get_issue("DEV-7")
            self.assertNotIn("test-placeholder", str(caught.exception))

    def test_missing_or_malformed_issue_fields(self):
        for key in ["project", "state", "team", "title", "identifier"]:
            data = issue_data()
            data["issue"][key] = None
            with self.subTest(key=key), self.response({"data": data}), self.assertRaises(TaskError):
                self.linear.get_issue("DEV-7")
        for nodes in [[], [{"id": "a", "name": "In Progress"}, {"id": "b", "name": "In Progress"}]]:
            data = issue_data()
            data["issue"]["team"]["states"]["nodes"] = nodes
            with self.response({"data": data}), self.assertRaisesRegex(TaskError, "exactly one"):
                self.linear.get_issue("DEV-7")
        data = issue_data()
        data["issue"]["team"]["states"]["pageInfo"]["hasNextPage"] = True
        with self.response({"data": data}), self.assertRaises(TaskError):
            self.linear.get_issue("DEV-7")

    def test_status_update_only_changes_state(self):
        update = {"issueUpdate": {"success": True, "issue": {"id": ISSUE.id, "state": {"id": "started", "name": "In Progress"}}}}
        with self.response({"data": update}) as mocked:
            self.linear.start(ISSUE)
        request = json.loads(mocked.call_args.args[0].data)
        self.assertEqual(request["variables"], {"id": ISSUE.id, "state": "started"})
        self.assertIn("input: {stateId: $state}", request["query"])
        with patch.object(self.linear, "request") as mocked:
            self.linear.start(replace(ISSUE, state_name="In Progress", state_id="started"))
            mocked.assert_not_called()
        for data in [{}, {"issueUpdate": {"success": False}}, {"issueUpdate": None}]:
            with self.response({"data": data}), self.assertRaises(TaskError):
                self.linear.start(ISSUE)


class GitTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name).resolve()
        (self.repo / ".git").mkdir()
        self.git = Git(self.repo)
        self.answers = {
            ("rev-parse", "--is-inside-work-tree"): "true\n",
            ("rev-parse", "--show-toplevel"): str(self.repo) + "\n",
            ("rev-parse", "--absolute-git-dir"): str(self.repo / ".git"),
            ("rev-parse", "--path-format=absolute", "--git-common-dir"): str(self.repo / ".git"),
            ("for-each-ref", "--format=%(refname)", "refs/heads"): "refs/heads/main\n",
            ("rev-parse", "--symbolic-full-name", "HEAD"): "refs/heads/main\n",
            ("config", "--default", "", "--get", "branch.main.remote"): "origin\n",
            ("config", "--default", "", "--get", "branch.main.merge"): "refs/heads/main\n",
            ("rev-parse", "--verify", "FETCH_HEAD^{commit}"): "abc123\n",
            ("rev-parse", "--verify", "HEAD^{commit}"): "abc123\n",
            ("rev-list", "--left-right", "--count", "HEAD...abc123"): "0\t2\n",
        }
        def fake(args):
            self.assertEqual(args[:3], ["git", "-C", str(self.repo)])
            return self.answers.get(tuple(args[3:]), "")
        self.runner = self.enterContext(patch("task_start.workspace.run", side_effect=fake))

    def test_fast_forward_update(self):
        self.git.update_base("main")
        calls = [call.args[0][3:] for call in self.runner.call_args_list]
        fetch = ["fetch", "--no-tags", "--no-recurse-submodules", "--refmap=", "origin", "refs/heads/main"]
        self.assertIn(fetch, calls)
        merge = ["-c", "branch.main.mergeOptions=", "merge", "--ff-only", "--no-squash",
                 "--no-autostash", "--no-overwrite-ignore", "abc123"]
        self.assertIn(merge, calls)
        self.assertLess(calls.index(fetch), calls.index(merge))
        self.assertGreater(calls.index(["rev-parse", "--verify", "HEAD^{commit}"]), calls.index(merge))
        self.assertEqual(calls[-1], ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"])

    def test_unsafe_base_stops(self):
        cases = [
            (("for-each-ref", "--format=%(refname)", "refs/heads"), "refs/heads/other\n"),
            (("rev-parse", "--symbolic-full-name", "HEAD"), "refs/heads/other\n"),
            (("rev-parse", "--symbolic-full-name", "HEAD"), "HEAD\n"),
            (("status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"), "?? notes\n"),
            (("rev-parse", "--path-format=absolute", "--git-common-dir"), str(self.repo / "other")),
            (("config", "--default", "", "--get", "branch.main.remote"), ""),
            (("config", "--default", "", "--get", "branch.main.remote"), "."),
            (("config", "--default", "", "--get", "branch.main.merge"), "refs/heads/other"),
            (("rev-list", "--left-right", "--count", "HEAD...abc123"), "1\t2"),
            (("rev-list", "--left-right", "--count", "HEAD...abc123"), "1\t0"),
        ]
        for key, answer in cases:
            with self.subTest(key=key, answer=answer), patch.dict(self.answers, {key: answer}), self.assertRaises(TaskError):
                self.git.update_base("main")
        self.assertFalse(any("merge" in call.args[0][3:] for call in self.runner.call_args_list))

    def test_wrong_head_after_merge_stops_before_herdr(self):
        self.answers[("rev-parse", "--verify", "HEAD^{commit}")] = "old-head\n"
        with patch("task_start.cli.load_local", return_value=LOCAL), \
                patch("task_start.cli.load_projects", return_value=[PROJECT]), \
                patch("task_start.cli.Linear") as linear, \
                patch("task_start.cli.Git", return_value=self.git), \
                patch("task_start.cli.Herdr") as herdr:
            linear.return_value.get_issue.return_value = ISSUE
            with self.assertRaisesRegex(TaskError, "did not reach"):
                cli.start("DEV-7")
            herdr.assert_not_called()
            linear.return_value.start.assert_not_called()

    def test_dirty_checkout_after_merge_is_rejected(self):
        fake = self.runner.side_effect

        def dirty_merge(args):
            if "merge" in args[3:]:
                self.answers[("status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none")] = "M  tracked.txt\n"
            return fake(args)

        self.runner.side_effect = dirty_merge
        with self.assertRaisesRegex(TaskError, "local modifications"):
            self.git.update_base("main")

    def test_unfinished_operation(self):
        (self.repo / ".git" / "MERGE_HEAD").touch()
        with self.assertRaisesRegex(TaskError, "unfinished"):
            self.git.update_base("main")

    def test_branch_identity_and_worktree_parsing(self):
        self.answers[("for-each-ref", "--format=%(refname:strip=2)", "refs/heads")] = "main\ndev-7-old-title\ndev-70-other\n"
        self.assertEqual(self.git.branches("DEV-7"), ["dev-7-old-title"])
        self.answers[("worktree", "list", "--porcelain", "-z")] = "worktree /path with spaces\0HEAD abc\0branch refs/heads/dev-7-old-title\0\0"
        self.assertEqual(self.git.worktrees()[0]["worktree"], "/path with spaces")


class HerdrTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name).resolve()
        self.tree_path = self.repo / "existing"
        self.tree_path.mkdir()
        self.herdr = Herdr(self.repo)
        self.git = Mock()
        self.git.branches.return_value = []
        self.git.remote_branches.return_value = []
        self.git.worktrees.return_value = []
        self.branch = "dev-7-add-ingestion-cli"
        self.tree = {"branch": self.branch, "path": str(self.tree_path), "label": "DEV-7",
                     "is_linked_worktree": True, "is_bare": False, "is_detached": False,
                     "is_prunable": False, "open_workspace_id": "w1"}
        self.listing = {"type": "worktree_list", "source": {"repo_root": str(self.repo)}, "worktrees": []}

    def result(self, operation):
        return {"type": "worktree_" + operation, "worktree": copy.deepcopy(self.tree), "workspace": {"focused": True}}

    def prepare(self):
        return self.herdr.prepare(self.git, "main", self.branch, "DEV-7")

    def existing(self):
        self.listing["worktrees"] = [self.tree]
        self.git.branches.return_value = [self.tree["branch"]]
        self.git.worktrees.return_value = [{"worktree": str(self.tree_path), "branch": "refs/heads/" + self.tree["branch"]}]

    def test_create_command(self):
        with patch("task_start.workspace.run", side_effect=[json.dumps({"result": self.listing}), json.dumps({"result": self.result("created")})]) as runner:
            self.assertEqual(self.prepare(), (self.branch, "workspace created and focused"))
        self.assertEqual(runner.call_args_list[0].args[0], ["herdr", "worktree", "list", "--cwd", str(self.repo)])
        self.assertEqual(runner.call_args_list[1].args[0], ["herdr", "worktree", "create", "--cwd", str(self.repo), "--base", "main", "--branch", self.branch, "--label", "DEV-7", "--focus"])

    def test_reopen_even_after_title_changed(self):
        self.tree["branch"] = "dev-7-previous-title"
        self.existing()
        with patch("task_start.workspace.run", side_effect=[json.dumps({"result": self.listing}), json.dumps({"result": self.result("opened")})]) as runner:
            self.assertEqual(self.prepare()[0], "dev-7-previous-title")
        self.assertEqual(runner.call_args.args[0], ["herdr", "worktree", "open", "--cwd", str(self.repo), "--path", str(self.tree_path), "--label", "DEV-7", "--focus"])

    def test_branch_only_and_remote_only_refused(self):
        for local, remote in [([self.branch], []), ([], ["refs/remotes/origin/" + self.branch])]:
            self.git.branches.return_value = local
            self.git.remote_branches.return_value = remote
            with patch.object(self.herdr, "command", return_value=self.listing) as command, self.assertRaises(TaskError):
                self.prepare()
            command.assert_called_once_with("list")

    def test_ambiguous_or_unusable_worktrees_refused(self):
        for field, value in [("is_prunable", True), ("is_detached", True), ("is_linked_worktree", False), ("branch", "dev-8-other")]:
            self.existing()
            with patch.dict(self.tree, {field: value}), patch.object(self.herdr, "command", return_value=self.listing) as command, self.assertRaises(TaskError):
                self.prepare()
            command.assert_called_once_with("list")
        self.existing()
        self.listing["worktrees"].append(copy.deepcopy(self.tree))
        with patch.object(self.herdr, "command", return_value=self.listing), self.assertRaises(TaskError):
            self.prepare()

    def test_malformed_or_unfocused_herdr_response(self):
        for output in ["not json", "[]", '{"error": {}}', '{"result": {"type": "other"}}']:
            with patch("task_start.workspace.run", return_value=output), self.assertRaises(TaskError):
                self.prepare()
        result = self.result("created")
        result["workspace"]["focused"] = False
        with patch.object(self.herdr, "command", side_effect=[self.listing, result]), self.assertRaises(TaskError):
            self.prepare()

    def test_mismatched_or_locked_git_worktree_refused(self):
        for changes in [{"worktree": str(self.repo / "another")}, {"locked": "maintenance"}]:
            self.existing()
            self.git.worktrees.return_value[0].update(changes)
            with patch.object(self.herdr, "command", return_value=self.listing) as command, self.assertRaises(TaskError):
                self.prepare()
            command.assert_called_once_with("list")


class OrchestrationTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(patch("task_start.cli.load_local", return_value=LOCAL))
        self.enterContext(patch("task_start.cli.load_projects", return_value=[PROJECT]))
        self.linear = self.enterContext(patch("task_start.cli.Linear")).return_value
        self.linear.get_issue.return_value = ISSUE
        self.git = self.enterContext(patch("task_start.cli.Git")).return_value
        self.herdr = self.enterContext(patch("task_start.cli.Herdr")).return_value
        self.herdr.prepare.return_value = ("dev-7-add-ingestion-cli", "workspace created and focused")

    def test_order_and_concise_output(self):
        operations = Mock()
        operations.attach_mock(self.git.update_base, "base")
        operations.attach_mock(self.herdr.prepare, "workspace")
        operations.attach_mock(self.linear.start, "status")
        output = cli.start("DEV-7")
        self.assertEqual([call[0] for call in operations.mock_calls], ["base", "workspace", "status"])
        self.assertEqual(len(output.splitlines()), 5)
        self.assertIn("Linear: In Progress", output)
        self.assertNotIn("test-placeholder", output)

    def test_herdr_failure_does_not_update_linear(self):
        self.herdr.prepare.side_effect = TaskError("Herdr failed")
        with self.assertRaises(TaskError):
            cli.start("DEV-7")
        self.linear.start.assert_not_called()

    def test_git_failure_does_not_update_linear_or_call_herdr(self):
        self.git.update_base.side_effect = TaskError("dirty checkout")
        with self.assertRaises(TaskError):
            cli.start("DEV-7")
        self.linear.start.assert_not_called()
        self.herdr.prepare.assert_not_called()

    def test_status_failure_reports_workspace_ready(self):
        self.linear.start.side_effect = TaskError("Linear request failed")
        with self.assertRaisesRegex(TaskError, "Workspace ready"):
            cli.start("DEV-7")

    def test_expected_error_has_no_traceback(self):
        self.herdr.prepare.side_effect = TaskError("Herdr failed")
        with patch("sys.stderr", new=io.StringIO()) as stderr:
            self.assertEqual(cli.main(["start", "DEV-7"]), 1)
        self.assertEqual(stderr.getvalue(), "task: Herdr failed\n")


class ProcessTests(unittest.TestCase):
    def test_errors_do_not_expose_command_output(self):
        with patch("task_start.workspace.subprocess.run", return_value=subprocess.CompletedProcess([], 1, "", "credential-bearing URL")), self.assertRaises(TaskError) as caught:
            run(["git", "-C", "/repo", "fetch", "origin"])
        self.assertNotIn("credential-bearing", str(caught.exception))

    @unittest.skipUnless(os.name == "posix", "POSIX filesystem byte paths")
    def test_git_path_bytes_round_trip(self):
        raw_path = b"/repos/caf\xc3\xa9/undecodable-\xff\n"
        with patch("task_start.workspace.subprocess.run", return_value=subprocess.CompletedProcess([], 0, raw_path, b"")):
            self.assertEqual(os.fsencode(run(["git", "-C", "/repo", "rev-parse", "--show-toplevel"])), raw_path)

    def test_herdr_json_uses_utf8(self):
        payload = {"result": {"type": "worktree_list", "path": "/repos/café/naïve"}}
        encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        with patch("task_start.workspace.subprocess.run", return_value=subprocess.CompletedProcess([], 0, encoded, b"\xff")):
            self.assertEqual(Herdr(Path("/repo")).command("list"), payload["result"])

    def test_invalid_herdr_utf8_is_task_error(self):
        with patch("task_start.workspace.subprocess.run", return_value=subprocess.CompletedProcess([], 0, b"\xff", b"")):
            with self.assertRaisesRegex(TaskError, "UTF-8 JSON"):
                Herdr(Path("/repo")).command("list")

    def test_git_decoding_failure_is_task_error(self):
        with patch("task_start.workspace.subprocess.run", return_value=subprocess.CompletedProcess([], 0, b"\xff", b"")), \
                patch("task_start.workspace.os.fsdecode", side_effect=UnicodeDecodeError("ascii", b"\xff", 0, 1, "invalid byte")):
            with self.assertRaisesRegex(TaskError, "filesystem encoding"):
                run(["git", "-C", "/repo", "rev-parse", "--show-toplevel"])


class LocalGitIntegrationTests(unittest.TestCase):
    """Real Git in disposable directories, using a local fake remote; no network."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name).resolve()
        self.repo, self.remote = root / "checkout", root / "upstream"
        # Isolate tests from user Git configuration, hooks, signing and identity.
        self.enterContext(patch.dict(os.environ, {
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_AUTHOR_NAME": "Test", "GIT_AUTHOR_EMAIL": "test@example.invalid",
            "GIT_COMMITTER_NAME": "Test", "GIT_COMMITTER_EMAIL": "test@example.invalid",
        }))
        self.command(root, "init", "--initial-branch=main", str(self.remote))
        (self.remote / ".gitignore").write_text("ignored.txt\n")
        (self.remote / "tracked.txt").write_text("initial\n")
        self.command(self.remote, "add", ".")
        self.command(self.remote, "commit", "-m", "initial")
        self.command(root, "clone", str(self.remote), str(self.repo))
        self.git = Git(self.repo)
        self.before = self.command(self.repo, "rev-parse", "HEAD")

    def command(self, repo, *args):
        return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True).stdout.strip()

    def advance_remote(self, filename="tracked.txt"):
        (self.remote / filename).write_text("upstream change\n")
        self.command(self.remote, "add", "--force", filename)
        self.command(self.remote, "commit", "-m", "advance")

    def test_fast_forward_and_up_to_date(self):
        self.advance_remote()
        self.git.update_base("main")
        self.assertEqual(self.command(self.repo, "rev-parse", "HEAD"), self.command(self.remote, "rev-parse", "HEAD"))
        self.assertEqual((self.repo / "tracked.txt").read_text(), "upstream change\n")
        self.git.update_base("main")

    def test_branch_squash_options_cannot_change_fast_forward(self):
        self.command(self.repo, "config", "branch.main.mergeOptions", "--squash")
        self.advance_remote()
        self.git.update_base("main")
        fetched = self.command(self.repo, "rev-parse", "FETCH_HEAD")
        self.assertNotEqual(fetched, self.before)
        self.assertEqual(self.command(self.repo, "rev-parse", "HEAD"), fetched)
        self.assertEqual(self.command(self.repo, "status", "--porcelain"), "")
        self.assertEqual((self.repo / "tracked.txt").read_text(), "upstream change\n")
        self.assertEqual(self.command(self.repo, "config", "branch.main.mergeOptions"), "--squash")
        self.git.update_base("main")
        self.assertEqual(self.command(self.repo, "status", "--porcelain"), "")

    @unittest.skipUnless(sys.platform.startswith("linux"), "Uses Linux C.UTF-8 startup and C/ASCII runtime locales")
    def test_non_ascii_paths_under_ascii_text_locale(self):
        repo = self.repo.parent / "café"
        tree = self.repo.parent / "naïve-worktree"
        self.command(self.repo.parent, "clone", str(self.remote), str(repo))
        self.command(repo, "worktree", "add", "-b", "dev-7-example", str(tree))
        self.advance_remote()
        # Python's filesystem codec is fixed at startup. Switching the runtime
        # locale reproduces ASCII subprocess text decoding with UTF-8 path bytes.
        script = """
import codecs
import json
import locale
from pathlib import Path
import subprocess
import sys
from unittest.mock import patch

sys.path.insert(0, sys.argv[1])
from task_start.workspace import Git, Herdr

locale.setlocale(locale.LC_CTYPE, "C")
assert sys.flags.utf8_mode == 0
assert codecs.lookup(locale.getencoding()).name == "ascii"
repo, tree = Path(sys.argv[2]), Path(sys.argv[3])
git = Git(repo)
git.update_base("main")
assert {Path(w["worktree"]) for w in git.worktrees()} == {repo, tree}
payload = {"result": {"type": "worktree_list", "path": str(tree)}}
encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
with patch("task_start.workspace.subprocess.run", return_value=subprocess.CompletedProcess([], 0, encoded, b"")):
    assert Herdr(repo).command("list")["path"] == str(tree)
"""
        env = dict(os.environ, LC_ALL="C.UTF-8", PYTHONUTF8="0", PYTHONCOERCECLOCALE="0")
        result = subprocess.run(
            [sys.executable, "-X", "utf8=0", "-c", script,
             str(Path(__file__).resolve().parent.parent), str(repo), str(tree)],
            capture_output=True, encoding="utf-8", errors="replace", env=env,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.command(repo, "rev-parse", "HEAD"), self.command(self.remote, "rev-parse", "HEAD"))
        self.assertEqual(self.command(repo, "status", "--porcelain"), "")

    def test_dirty_checkout_is_preserved(self):
        self.advance_remote()
        (self.repo / "tracked.txt").write_text("local work\n")
        with self.assertRaisesRegex(TaskError, "local modifications"):
            self.git.update_base("main")
        self.assertEqual(self.command(self.repo, "rev-parse", "HEAD"), self.before)
        self.assertEqual((self.repo / "tracked.txt").read_text(), "local work\n")

    def test_local_commits_and_divergence_are_preserved(self):
        self.command(self.repo, "commit", "--allow-empty", "-m", "local work")
        local = self.command(self.repo, "rev-parse", "HEAD")
        with self.assertRaisesRegex(TaskError, "local-only"):
            self.git.update_base("main")
        self.advance_remote()
        with self.assertRaisesRegex(TaskError, "diverges"):
            self.git.update_base("main")
        self.assertEqual(self.command(self.repo, "rev-parse", "HEAD"), local)

    def test_ignored_file_is_not_overwritten(self):
        (self.repo / "ignored.txt").write_text("local ignored work\n")
        self.advance_remote("ignored.txt")
        with self.assertRaises(TaskError):
            self.git.update_base("main")
        self.assertEqual(self.command(self.repo, "rev-parse", "HEAD"), self.before)
        self.assertEqual((self.repo / "ignored.txt").read_text(), "local ignored work\n")

    def test_entry_point_from_another_directory_and_symlink(self):
        entry = Path(__file__).resolve().parent.parent / "task"
        result = subprocess.run([sys.executable, str(entry), "--help"], cwd=self.repo, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        if os.name != "nt":
            link = self.repo.parent / "task-link"
            link.symlink_to(entry)
            result = subprocess.run([str(link), "--help"], cwd=self.repo, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
