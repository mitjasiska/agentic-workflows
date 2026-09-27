"""Read-only, content-based snapshots of the Git-visible implementation."""

from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path
import stat

from . import TaskError
from .workspace import Git


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


@dataclass(frozen=True)
class ReviewState:
    base_commit: str
    head: str
    branch: str
    index: str
    fingerprint: str
    version: int = 1

    def as_dict(self) -> dict:
        return asdict(self)


def snapshot(path: Path, base_commit: str, branch: str) -> ReviewState:
    """Hash names, bytes and Git modes, never mtimes or index stat-cache bytes.

    Git supplies the file inventory and ignore rules. Hashing tracked files directly
    also covers assume-unchanged/skip-worktree entries and binary modifications.
    Two identical reads are required so a visibly changing capture is rejected.
    """
    try:
        first = _snapshot(path, base_commit, branch)
        second = _snapshot(path, base_commit, branch)
        if first != second:
            raise TaskError("Implementation changed while capturing review state; a new pass is required")
        return first
    except (OSError, ValueError) as error:
        raise TaskError(f"Cannot capture Git-visible review state: {type(error).__name__}") from None


def _snapshot(path: Path, base_commit: str, branch: str) -> ReviewState:
    git = Git(path)

    def command(*args):
        return git.command("--no-optional-locks", *args)

    actual_branch = command("rev-parse", "--symbolic-full-name", "HEAD").strip()
    if (Path(command("rev-parse", "--show-toplevel").strip()).resolve() != path
            or (branch and actual_branch != f"refs/heads/{branch}")):
        raise TaskError("Review checkout/branch identity changed; a new pass is required")
    head = command("rev-parse", "--verify", "HEAD^{commit}").strip()
    command("cat-file", "-e", f"{base_commit}^{{commit}}")
    git_dir = Path(command("rev-parse", "--absolute-git-dir").strip())
    if any((git_dir / marker).exists() for marker in
           ("MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer", "BISECT_LOG")):
        raise TaskError("Task worktree has an unfinished Git operation; review cannot establish stable state")
    index = command("ls-files", "--stage", "-z")
    flags = command("ls-files", "-v", "-z")
    tracked = {}
    for entry in index.split("\0"):
        if not entry:
            continue
        metadata, name = entry.split("\t", 1)
        mode, oid, stage = metadata.split()
        if stage != "0":
            raise TaskError("Task index contains unresolved conflicts")
        tracked[name] = (mode, oid)
    # Staged deletions are still part of the reviewed HEAD, even if ignore rules
    # now hide a remaining working copy of the deleted path.
    head_files = set(command("ls-tree", "-r", "--name-only", "-z", head).split("\0")) - {""}
    untracked = command("ls-files", "--others", "--exclude-standard", "-z").split("\0")
    files = []
    for name in sorted(set(tracked) | head_files | (set(untracked) - {""})):
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts:
            raise TaskError("Git returned an unsafe review path")
        for parent in relative.parents:
            if (path / parent).is_symlink():
                raise TaskError("A task path has a symlink parent; cannot establish review state")
        item = path / relative
        try:
            mode = item.lstat().st_mode
        except FileNotFoundError:
            files.append([name, "missing"])
            continue
        if stat.S_ISLNK(mode):
            files.append([name, "symlink", os.readlink(item)])
        elif stat.S_ISREG(mode):
            value = hashlib.sha256()
            with item.open("rb") as source:
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    value.update(chunk)
            files.append([name, "executable" if mode & 0o111 else "file", value.hexdigest()])
        elif stat.S_ISDIR(mode) and tracked.get(name, (None, None))[0] == "160000":
            # Include nested Git-visible content, not only the gitlink commit.
            if not (item / ".git").exists():
                if any(item.iterdir()):
                    raise TaskError("Uninitialized submodule contains files; inspect it before review")
                files.append([name, "uninitialized submodule"])
            else:
                nested = _snapshot(item, tracked[name][1], "")
                files.append([name, "submodule", nested.as_dict()])
        elif stat.S_ISDIR(mode) and name in head_files and name not in tracked:
            # A deleted file replaced by a directory: Git enumerates its visible
            # children separately. The replacement itself is meaningful state.
            files.append([name, "directory replacing deleted file"])
        else:
            raise TaskError(f"Unsupported Git-visible file type during review: {name!r}")
    status = command("status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none")
    value = dict(version=1, base_commit=base_commit, head=head, branch=actual_branch,
                 index=index, flags=flags, status=status, files=files)
    fingerprint = digest(json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":")).encode())
    return ReviewState(base_commit, head, branch, digest(os.fsencode(index)), fingerprint)
