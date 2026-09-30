"""Conservative PR history checks, including squash merges, without a gh dependency."""

import json
from dataclasses import dataclass
import os
import re
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen

from . import TaskError


@dataclass(frozen=True)
class MergedPull:
    repository: str
    number: int
    head_commit: str
    merge_commit: str


def repository_name(remote: str) -> str | None:
    if remote.startswith("git@github.com:"):
        path = remote.removeprefix("git@github.com:")
    else:
        try:
            url = urlsplit(remote)
            if url.hostname != "github.com" or url.scheme not in {"https", "ssh"}:
                return None
            path = url.path.lstrip("/")
        except ValueError:
            return None
    path = path.removesuffix(".git")
    return path if re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", path) else None


def request(path: str, branch: str, *, method: str = "GET", data: dict | None = None):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "agentic-workflows",
               "X-GitHub-Api-Version": "2022-11-28"}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = Request(f"https://api.github.com/repos/{path}", headers=headers, method=method,
                      data=json.dumps(data).encode() if data is not None else None)
    try:
        with urlopen(request, timeout=30) as response:
            return json.load(response)
    except (HTTPError, URLError, OSError, ValueError):
        if method != "GET":
            raise TaskError("GitHub PR write was not confirmed; check network/token access, then rerun task pr "
                            "to discover and reuse any matching PR before another write") from None
        raise TaskError(f"Cannot establish GitHub PR history for {branch}; check network/API access "
                        "(GH_TOKEN or GITHUB_TOKEN for private repositories or rate limits) "
                        "and inspect its PR/merge state before retrying. Workspace left intact.") from None


def publication_pull(repo: str, branch: str, base: str, heads: set[str]):
    """All history participates: a closed, different-base, or second PR conflicts."""
    pulls = list(pull_requests(repo, branch))
    if len(pulls) > 1:
        raise TaskError("Ambiguous PR history: multiple PRs use the task head; inspect them before retrying")
    if not pulls:
        return None
    listed = pulls[0]
    pull = request(f"{repo}/pulls/{listed['number']}", branch)
    for item in (listed, pull):
        validate_pull(item, repo, branch)
        try:
            url = re.fullmatch(r"https://github\.com/([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)/pull/([1-9][0-9]*)",
                               item["html_url"])
            if (item["base"]["ref"] != base or item["head"]["sha"] not in heads
                    or item["state"] != "open" or item["merged_at"] is not None
                    or item.get("merged", False) is not False
                    or item["number"] != listed["number"]
                    or url is None or url[1].casefold() != repo.casefold()
                    or url[2] != str(item["number"])):
                raise ValueError("conflicting PR")
        except (KeyError, TypeError, ValueError):
            raise TaskError("Conflicting PR repository/head/base/state; inspect it before retrying task pr") from None
    if pull["head"]["sha"] != listed["head"]["sha"]:
        raise TaskError("PR head changed during verification; retry after inspecting remote state")
    return pull


def publish_pull(repo: str, branch: str, base: str, head: str, title: str, body: str, *, verify_local, before_write):
    pull = publication_pull(repo, branch, base, {head})
    # Lifecycle owns the frozen local SHA and reviewed-state checks. Recheck
    # after network lookups, immediately before any PR write and URL reporting.
    verify_local()
    if pull is None:
        # An uncertain POST is never replayed here. The next invocation enumerates
        # authoritative history first and reuses the unique matching result.
        before_write()
        request(f"{repo}/pulls", branch, method="POST",
                data=dict(head=branch, base=base, title=title, body=body, draft=False))
    elif pull.get("title") != title or pull.get("body") != body:
        before_write()
        request(f"{repo}/pulls/{pull['number']}", branch, method="PATCH", data=dict(title=title, body=body))
    verified = publication_pull(repo, branch, base, {head})
    if verified is None or verified.get("title") != title or verified.get("body") != body:
        raise TaskError("GitHub has not confirmed the expected PR metadata; rerun task pr to verify/reuse it")
    verify_local()
    return verified["html_url"]


def validate_pull(pull: dict, repo: str, branch: str) -> None:
    try:
        if (pull["head"]["ref"] != branch
                or pull["head"]["repo"]["full_name"].casefold() != repo.casefold()
                or pull["base"]["repo"]["full_name"].casefold() != repo.casefold()
                or pull["state"] not in {"open", "closed"}
                or type(pull["number"]) is not int or pull["number"] < 1
                or (pull["merged_at"] is not None and not isinstance(pull["merged_at"], str))):
            raise ValueError("unexpected PR identity/state")
    except (KeyError, TypeError, ValueError, AttributeError):
        raise TaskError(f"Cannot establish PR history for {branch}: unexpected GitHub response; "
                        "inspect its PR/merge state manually") from None


def pull_requests(repo: str, branch: str):
    owner = repo.split("/")[0]
    # Query all states, not Git ancestry: squash merges do not preserve the head.
    # Generate each page URL ourselves rather than forwarding credentials to a Link URL.
    for page in range(1, 21):
        query = urlencode({"state": "all", "head": f"{owner}:{branch}",
                           "per_page": 100, "page": page})
        pulls = request(f"{repo}/pulls?{query}", branch)
        if not isinstance(pulls, list) or len(pulls) > 100:
            raise TaskError(f"Cannot establish PR history for {branch}: unexpected GitHub response; "
                            "inspect its PR/merge state manually")
        for pull in pulls:
            validate_pull(pull, repo, branch)
            yield pull
        if len(pulls) < 100:
            return
    raise TaskError(f"Cannot establish complete PR history for {branch}; inspect it manually")


def check_history(remote: str, branch: str, *, existing: bool) -> None:
    repo = repository_name(remote)
    if repo is None:
        if existing:
            raise TaskError(f"Cannot establish PR history for {branch}: the base remote is not github.com. "
                            "Inspect its PR/merge state and retire historical local work manually, "
                            "or choose a new --slice. No workspace was opened or deleted.")
        return
    for pull in pull_requests(repo, branch):
        if pull["merged_at"] is not None or pull["state"] == "closed":
            state = "merged" if pull["merged_at"] is not None else "closed"
            raise TaskError(f"Historical workspace {branch}: GitHub PR #{pull['number']} is {state}. "
                            "Inspect/retire it manually or choose a new --slice. "
                            "No branch or worktree was deleted.")


def merged_pull(remote: str, branch: str, base: str, head_commit: str) -> MergedPull:
    repo = repository_name(remote)
    if repo is None:
        raise TaskError("Cannot prove a squash/rebase merge: the base remote is not github.com")
    # Read all pages and all states: another PR for this head is ambiguous,
    # even if only one happens to claim a completed merge.
    pulls = list(pull_requests(repo, branch))
    if len(pulls) != 1:
        raise TaskError(f"Missing or ambiguous merge evidence for {branch}: expected exactly one GitHub PR, "
                        f"found {len(pulls)}")
    listed = pulls[0]
    pull = request(f"{repo}/pulls/{listed['number']}", branch)
    validate_pull(pull, repo, branch)
    try:
        if (pull["number"] != listed["number"] or pull["base"]["ref"] != base
                or pull["head"]["sha"] != head_commit
                or listed["base"]["ref"] != base or listed["head"]["sha"] != head_commit
                or any(pull[key] != listed[key] for key in ("state", "merged_at", "merge_commit_sha"))):
            raise ValueError("mismatched or changed PR")
        if pull["merged"] is not True or pull["state"] != "closed" or not pull["merged_at"]:
            raise TaskError(f"GitHub PR #{pull['number']} for {branch} is not confirmed merged")
        commit = pull["merge_commit_sha"]
        if not isinstance(commit, str) or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", commit):
            raise ValueError("invalid merge commit")
    except (KeyError, TypeError, ValueError):
        raise TaskError(f"GitHub merge evidence does not match the exact task head and base for {branch}; "
                        "workspace left intact") from None
    return MergedPull(repo, pull["number"], head_commit, commit)
