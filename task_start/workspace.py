import json
from dataclasses import dataclass, replace
import os
from pathlib import Path
import re
import subprocess
import unicodedata

from . import TaskError
from .github import check_history, repository_name


def branch_name(identifier: str, title: str) -> str:
    ascii_title = unicodedata.normalize("NFKD", title).encode("ascii", "ignore").decode()
    slug = re.sub(r"[^a-z0-9]+", "-", ascii_title.lower()).strip("-")[:100].rstrip("-")
    return identifier.lower() + (f"-{slug}" if slug else "")


def slice_slug(value: str) -> str:
    # Normalize words and accents, but reject path/ref syntax and shell controls.
    if not value.strip() or any(not (c.isalnum() or c in " _-") for c in value):
        raise TaskError("slice must contain words, spaces, hyphens or underscores")
    ascii_value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode()
    untrimmed = re.sub(r"[^a-z0-9]+", "-", ascii_value.lower()).strip("-")
    if not untrimmed or len(untrimmed) > 100:
        raise TaskError("slice must normalize to 1–100 ASCII letters, digits or hyphens")
    return untrimmed


@dataclass(frozen=True)
class Workspace:
    branch: str
    path: Path
    workspace_id: str
    tab_id: str
    pane_id: str
    action: str
    slice: str | None = None


def run(args: list[str]) -> str:
    try:
        result = subprocess.run(args, capture_output=True, timeout=120, check=False)
    except FileNotFoundError:
        raise TaskError(f"{args[0]} is not installed or not on PATH") from None
    except (OSError, subprocess.TimeoutExpired):
        raise TaskError(f"{args[0]} could not complete; inspect its state before retrying") from None
    if result.returncode:
        # Commands can include authenticated remote URLs in stderr; don't echo them.
        operation = (args[5] if args[3] == "-c" else args[3]) if args[0] == "git" else " ".join(args[1:3])
        hint = "inspect it manually"
        if args[0] == "herdr":
            hint = "check the running Herdr session, repository trust, and worktree state"
            try:
                code = json.loads(result.stderr)["error"]["code"]
                if isinstance(code, str) and re.fullmatch(r"[a-z_]+", code):
                    hint = f"{code}; {hint}"
            except (ValueError, KeyError, TypeError):
                pass
        elif operation in {"fetch", "ls-remote"}:
            hint = "check remote access and whether the upstream base branch exists"
        elif operation == "merge":
            hint = "base could not be updated safely; inspect the checkout before retrying"
        raise TaskError(f"{args[0]} {operation} failed (exit {result.returncode}); {hint}")
    try:
        # Git emits filesystem bytes for unquoted paths. Preserve undecodable
        # bytes with the filesystem codec's error handler (surrogateescape on POSIX).
        return os.fsdecode(result.stdout) if args[0] == "git" else result.stdout.decode("utf-8")
    except UnicodeError:
        contract = "filesystem encoding" if args[0] == "git" else "UTF-8 JSON"
        raise TaskError(f"{args[0]} output could not be decoded as {contract}; inspect its state before retrying") from None


class Git:
    def __init__(self, repo: Path):
        self.repo = repo

    def command(self, *args: str) -> str:
        return run(["git", "-C", str(self.repo), *args])

    def check_base(self, base: str) -> None:
        if not self.repo.is_dir():
            raise TaskError(f"Repository does not exist: {self.repo}")
        if not (self.repo / ".git").exists():
            raise TaskError(f"Configured path is not a Git checkout root: {self.repo}")
        if (self.command("rev-parse", "--is-inside-work-tree").strip() != "true"
                or Path(self.command("rev-parse", "--show-toplevel").strip()).resolve() != self.repo):
            raise TaskError("Configured repository must be a Git checkout root")
        git_dir = self.command("rev-parse", "--absolute-git-dir").strip()
        common = self.command("rev-parse", "--path-format=absolute", "--git-common-dir").strip()
        if Path(git_dir).resolve() != Path(common).resolve():
            raise TaskError("Configured repository must be the permanent checkout, not a linked worktree")
        self.command("check-ref-format", f"refs/heads/{base}")
        refs = self.command("for-each-ref", "--format=%(refname)", "refs/heads").splitlines()
        if f"refs/heads/{base}" not in refs:
            raise TaskError(f"Configured base branch {base!r} does not exist")
        if self.command("rev-parse", "--symbolic-full-name", "HEAD").strip() != f"refs/heads/{base}":
            raise TaskError(f"Permanent checkout must already be on {base!r}; switch it manually")
        if self.command("status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none").strip():
            raise TaskError("Permanent checkout has local modifications or untracked files; resolve them first")
        for marker in ("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer", "BISECT_LOG"):
            if (Path(git_dir) / marker).exists():
                raise TaskError("Permanent checkout has an unfinished Git operation; finish it first")

    def update_base(self, base: str) -> None:
        self.check_base(base)
        remote = self.command("config", "--default", "", "--get", f"branch.{base}.remote").strip()
        upstream = self.command("config", "--default", "", "--get", f"branch.{base}.merge").strip()
        if remote == "." or remote.startswith("-") or not remote:
            raise TaskError("Base branch must track a remote branch")
        if upstream != f"refs/heads/{base}":
            raise TaskError("Base upstream must have the same branch name as the configured base")
        # Fetch only into FETCH_HEAD; configured forced refspecs cannot move branches.
        self.command("fetch", "--no-tags", "--no-recurse-submodules", "--refmap=", remote, upstream)
        fetched = self.command("rev-parse", "--verify", "FETCH_HEAD^{commit}").strip()
        counts = self.command("rev-list", "--left-right", "--count", f"HEAD...{fetched}").split()
        if len(counts) != 2 or counts[0] != "0":
            raise TaskError("Base branch has local-only commits or diverges from its upstream; resolve it manually")
        self.check_base(base)
        # Override branch-local merge options only for this invocation. In
        # particular, --squash can otherwise report success without moving HEAD.
        self.command("-c", f"branch.{base}.mergeOptions=", "merge", "--ff-only",
                     "--no-squash", "--no-autostash", "--no-overwrite-ignore", fetched)
        if self.command("rev-parse", "--verify", "HEAD^{commit}").strip() != fetched:
            raise TaskError("Base update did not reach the fetched upstream commit; inspect the checkout before retrying")
        self.check_base(base)

    def branches(self, identifier: str) -> list[str]:
        branches = self.command("for-each-ref", "--format=%(refname:strip=2)", "refs/heads").splitlines()
        return [branch for branch in branches if belongs_to_issue(branch, identifier)]

    def remote_branches(self, identifier: str) -> list[str]:
        branches = set()
        for remote in self.command("remote").splitlines():
            if not remote or remote.startswith("-"):
                raise TaskError("Invalid Git remote name; inspect repository configuration")
            # Read the server's refs. Cached refs/remotes/* (and forced fetch
            # refspecs) never participate in workspace selection.
            output = self.command("ls-remote", "--heads", "--", remote)
            for line in output.splitlines():
                parts = line.split("\t")
                if (len(parts) != 2 or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", parts[0])
                        or not parts[1].startswith("refs/heads/")):
                    raise TaskError("Unexpected Git remote branch response")
                branch = parts[1].removeprefix("refs/heads/")
                if belongs_to_issue(branch, identifier):
                    branches.add(branch)
        return sorted(branches)

    def check_history(self, base: str, branch: str, *, existing: bool) -> None:
        remote = self.command("config", "--default", "", "--get", f"branch.{base}.remote").strip()
        url = self.command("remote", "get-url", remote).strip()
        if existing:
            repo_name = repository_name(url)
            for other in self.command("remote").splitlines():
                other_url = self.command("remote", "get-url", other).strip()
                other_repo = repository_name(other_url)
                if other_url != url and (repo_name is None or other_repo is None
                                         or other_repo.casefold() != repo_name.casefold()):
                    raise TaskError(f"Cannot establish complete PR history for {branch}: remotes point to "
                                    "different repositories. Inspect cross-repository PR/merge state manually "
                                    "or choose a new --slice; no workspace was opened or deleted")
        check_history(url, branch, existing=existing)

    def worktrees(self) -> list[dict]:
        output = self.command("worktree", "list", "--porcelain", "-z")
        entries = []
        for record in output.split("\0\0"):
            fields = dict(field.partition(" ")[::2] for field in record.split("\0") if field)
            if fields:
                if (not fields.get("worktree") or not Path(fields["worktree"]).is_absolute()
                        or ("branch" in fields and not fields["branch"].startswith("refs/heads/"))):
                    raise TaskError("Unexpected Git worktree response; inspect workspace state")
                entries.append(fields)
        return entries

    def scope_file(self, path: Path) -> Path:
        directory = Git(path).command("rev-parse", "--absolute-git-dir").strip()
        if not directory or not Path(directory).is_absolute():
            raise TaskError("Git did not identify the worktree metadata directory")
        return Path(directory) / "agentic-workflows-scope.json"

    def resolve_scope(self, path: Path, branch: str, identifier: str,
                      requested: str | None) -> str | None:
        record = self.scope_file(path)
        try:
            data = json.loads(record.read_text(encoding="utf-8"))
        except FileNotFoundError:
            if requested is None:
                raise TaskError(f"Workspace scope is unknown for {branch}; rerun with explicit --slice "
                                "<branch suffix> to establish its scope. Branch names and titles cannot "
                                "distinguish legacy default workspaces from slices") from None
            # Selection already matched the explicitly requested slice branch.
            return requested
        except (OSError, ValueError):
            raise TaskError(f"Cannot read workspace scope metadata: {record}; inspect it before retrying") from None
        try:
            scope = data["slice"]
            if (data != {"version": 1, "identifier": identifier, "branch": branch, "slice": scope}
                    or type(data["version"]) is not int
                    or (scope is not None and (not isinstance(scope, str) or slice_slug(scope) != scope
                                              or branch != branch_name(identifier, scope)))):
                raise ValueError("scope identity mismatch")
        except (KeyError, TypeError, ValueError, TaskError):
            raise TaskError(f"Invalid workspace scope metadata: {record}; inspect it before retrying") from None
        if requested is not None and scope != requested:
            raise TaskError(f"Requested slice {requested!r} conflicts with the recorded scope for {branch}; "
                            "choose a different slice or inspect the workspace metadata")
        return scope

    def save_scope(self, path: Path, branch: str, identifier: str, scope: str | None) -> None:
        record = self.scope_file(path)
        data = {"version": 1, "identifier": identifier, "branch": branch, "slice": scope}
        try:
            # Never overwrite existing identity. Metadata belongs to the Git
            # worktree lifetime, outside tracked files and Herdr display labels.
            with record.open("x", encoding="utf-8") as output:
                json.dump(data, output)
                output.write("\n")
        except FileExistsError:
            if self.resolve_scope(path, branch, identifier, scope) != scope:
                raise TaskError(f"Workspace scope changed for {branch}; inspect it before retrying") from None
        except OSError:
            raise TaskError(f"Workspace ready, but scope metadata could not be saved: {record}; "
                            "inspect it before retrying. Workspace left intact") from None


def belongs_to_issue(branch: str, identifier: str) -> bool:
    return branch.lower() == identifier.lower() or branch.lower().startswith(identifier.lower() + "-")


class Herdr:
    def __init__(self, repo: Path):
        self.repo = repo

    def command(self, operation: str, *args: str) -> dict:
        output = run(["herdr", "worktree", operation, "--cwd", str(self.repo), *args])
        try:
            payload = json.loads(output)
            result = payload["result"]
            expected = {"list": "worktree_list", "create": "worktree_created", "open": "worktree_opened"}
            if payload.get("error") or result["type"] != expected[operation]:
                raise ValueError("unexpected result")
            return result
        except (ValueError, KeyError, TypeError):
            raise TaskError(f"Unexpected Herdr {operation} response; inspect workspace state before retrying") from None

    def prepare(self, git: Git, base: str, branch: str, identifier: str,
                slice: str | None = None) -> Workspace:
        label = identifier if slice is None else f"{identifier} / {slice}"

        def selected(name: str) -> bool:
            return name == branch if slice is not None else belongs_to_issue(name, identifier)

        result = self.command("list")
        try:
            if Path(result["source"]["repo_root"]).resolve() != self.repo:
                raise ValueError("repository mismatch")
            entries = result["worktrees"]
            if not isinstance(entries, list):
                raise ValueError("invalid worktrees")
            for entry in entries:
                if not isinstance(entry["path"], str) or not Path(entry["path"]).is_absolute():
                    raise ValueError("invalid path")
                if entry.get("branch") is not None and not isinstance(entry["branch"], str):
                    raise ValueError("invalid branch")
                if not isinstance(entry["label"], str):
                    raise ValueError("invalid label")
            matches = [w for w in entries if selected(w.get("branch") or "")
                       or w["label"].casefold() == label.casefold()]
        except (KeyError, TypeError, ValueError, AttributeError):
            raise TaskError("Unexpected Herdr worktree list") from None
        branches = [b for b in git.branches(identifier) if selected(b)]
        remotes = [b for b in git.remote_branches(identifier) if selected(b)]
        git_trees = git.worktrees()
        candidates = [w for w in git_trees if selected(w.get("branch", "").removeprefix("refs/heads/"))]
        names = set(branches + remotes + [w.get("branch") or "(detached)" for w in matches]
                    + [w["branch"].removeprefix("refs/heads/") for w in candidates])
        details = "; ".join(sorted(names))
        paths = "; ".join(w["path"] for w in matches)
        if len(names) > 1:
            raise TaskError(f"Ambiguous workspaces for {identifier}: {details}. Paths: {paths or '(none)'}. "
                            "Use --slice <branch suffix after the issue ID> to select one, "
                            "or inspect/retire historical work manually. Nothing was deleted.")
        if not branches and not matches and not candidates:
            if remotes:
                raise TaskError(f"A live remote branch for {identifier} already exists: {details}; "
                                "inspect it manually or choose another --slice")
            git.check_history(base, branch, existing=False)
            created = self.command("create", "--base", base, "--branch", branch,
                                   "--label", label, "--focus")
            workspace = self.confirm(created, branch, "workspace created and focused")
            self.confirm_git(git, workspace)
            git.save_scope(workspace.path, branch, identifier, slice)
            return replace(workspace, slice=slice)
        if len(branches) != 1 or len(matches) != 1 or len(candidates) != 1:
            raise TaskError(f"Existing branch/worktree state for {identifier} is ambiguous or branch-only: "
                            f"{details}. Paths: {paths or '(none)'}. Inspect Git and Herdr; nothing was deleted")
        branch = branches[0]
        match, tree = matches[0], candidates[0]
        path = Path(match["path"]).resolve()
        if (match.get("branch") != branch or tree.get("branch") != f"refs/heads/{branch}"
                or path != Path(tree["worktree"]).resolve() or not path.is_dir()
                or match.get("is_linked_worktree") is not True
                or match.get("is_bare") is not False or match.get("is_detached") is not False
                or match.get("is_prunable") is not False or "prunable" in tree or "locked" in tree
                or path == self.repo):
            raise TaskError("Git and Herdr do not identify a single usable task worktree; inspect them manually")
        git.check_history(base, branch, existing=True)
        scope = git.resolve_scope(path, branch, identifier, slice)
        label = identifier if scope is None else f"{identifier} / {scope}"
        opened = self.command("open", "--path", str(path), "--label", label, "--focus")
        workspace = self.confirm(opened, branch, "workspace reopened and focused", path)
        if match.get("open_workspace_id") is not None and workspace.workspace_id != match["open_workspace_id"]:
            raise TaskError("Herdr opened a different workspace ID; inspect it before retrying")
        self.confirm_git(git, workspace)
        git.save_scope(path, branch, identifier, scope)
        return replace(workspace, slice=scope)

    def confirm(self, result: dict, branch: str, action: str, path: Path | None = None) -> Workspace:
        try:
            tree = result["worktree"]
            workspace, tab, pane = result["workspace"], result["tab"], result["root_pane"]
            wid, tid, pid = workspace["workspace_id"], tab["tab_id"], pane["pane_id"]
            if (not all(isinstance(x, str) and x.strip() for x in (wid, tid, pid))
                    or tab["workspace_id"] != wid or pane["workspace_id"] != wid or pane["tab_id"] != tid
                    or tree["branch"] != branch or workspace["focused"] is not True
                    or not isinstance(tree["path"], str) or not Path(tree["path"]).is_absolute()
                    or not Path(tree["path"]).is_dir() or Path(tree["path"]).resolve() == self.repo
                    or tree.get("is_linked_worktree") is not True or tree.get("is_bare") is not False
                    or tree.get("is_detached") is not False or tree.get("is_prunable") is not False
                    or (path is not None and Path(tree["path"]).resolve() != path)):
                raise ValueError("workspace not confirmed")
            location = workspace["worktree"]
            if (not Path(location["repo_root"]).is_absolute() or not Path(location["checkout_path"]).is_absolute()
                    or Path(location["repo_root"]).resolve() != self.repo
                    or Path(location["checkout_path"]).resolve() != Path(tree["path"]).resolve()):
                raise ValueError("workspace checkout mismatch")
            return Workspace(branch, Path(tree["path"]).resolve(), wid, tid, pid, action)
        except (KeyError, TypeError, ValueError):
            raise TaskError("Herdr did not confirm the requested workspace and focus; inspect it before retrying") from None

    def confirm_git(self, git: Git, workspace: Workspace) -> None:
        trees = [w for w in git.worktrees() if w.get("branch") == f"refs/heads/{workspace.branch}"]
        if (len(trees) != 1 or Path(trees[0]["worktree"]).resolve() != workspace.path
                or "locked" in trees[0] or "prunable" in trees[0]):
            raise TaskError("Git did not confirm the prepared Herdr worktree; inspect it before retrying")
