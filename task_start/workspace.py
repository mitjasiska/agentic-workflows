import json
import os
from pathlib import Path
import re
import subprocess
import unicodedata

from . import TaskError


def branch_name(identifier: str, title: str) -> str:
    ascii_title = unicodedata.normalize("NFKD", title).encode("ascii", "ignore").decode()
    slug = re.sub(r"[^a-z0-9]+", "-", ascii_title.lower()).strip("-")[:100].rstrip("-")
    return identifier.lower() + (f"-{slug}" if slug else "")


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
        elif operation == "fetch":
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
        refs = self.command("for-each-ref", "--format=%(refname)", "refs/remotes").splitlines()
        return [ref for ref in refs if any(belongs_to_issue(part, identifier)
                                         for part in ref.split("/")[3:])]

    def worktrees(self) -> list[dict]:
        output = self.command("worktree", "list", "--porcelain", "-z")
        entries = []
        for record in output.split("\0\0"):
            fields = dict(field.partition(" ")[::2] for field in record.split("\0") if field)
            if fields:
                entries.append(fields)
        return entries


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

    def prepare(self, git: Git, base: str, branch: str, identifier: str) -> tuple[str, str]:
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
            matches = [w for w in entries if belongs_to_issue(w.get("branch") or "", identifier)
                       or w["label"].casefold() == identifier.casefold()]
        except (KeyError, TypeError, ValueError, AttributeError):
            raise TaskError("Unexpected Herdr worktree list") from None
        branches = git.branches(identifier)
        git_trees = git.worktrees()
        candidates = [w for w in git_trees if belongs_to_issue(
            w.get("branch", "").removeprefix("refs/heads/"), identifier)]
        if not branches and not matches and not candidates:
            if git.remote_branches(identifier):
                raise TaskError(f"A remote-tracking branch for {identifier} already exists; inspect it manually")
            created = self.command("create", "--base", base, "--branch", branch,
                                   "--label", identifier, "--focus")
            self.confirm(created, branch)
            return branch, "workspace created and focused"
        if len(branches) != 1 or len(matches) != 1 or len(candidates) != 1:
            raise TaskError(f"Existing branch/worktree state for {identifier} is ambiguous or branch-only; inspect Git and Herdr")
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
        opened = self.command("open", "--path", str(path), "--label", identifier, "--focus")
        self.confirm(opened, branch, path)
        return branch, "workspace reopened and focused"

    def confirm(self, result: dict, branch: str, path: Path | None = None) -> None:
        try:
            tree = result["worktree"]
            if (tree["branch"] != branch or result["workspace"]["focused"] is not True
                    or not isinstance(tree["path"], str) or not Path(tree["path"]).is_absolute()
                    or (path is not None and Path(tree["path"]).resolve() != path)):
                raise ValueError("workspace not confirmed")
        except (KeyError, TypeError, ValueError):
            raise TaskError("Herdr did not confirm the requested workspace and focus; inspect it before retrying") from None
