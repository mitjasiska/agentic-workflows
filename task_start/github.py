"""Conservative PR history checks, including squash merges, without a gh dependency."""

import json
import os
import re
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen

from . import TaskError


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


def check_history(remote: str, branch: str, *, existing: bool) -> None:
    repo = repository_name(remote)
    if repo is None:
        if existing:
            raise TaskError(f"Cannot establish PR history for {branch}: the base remote is not github.com. "
                            "Inspect its PR/merge state and retire historical local work manually, "
                            "or choose a new --slice. No workspace was opened or deleted.")
        return
    owner = repo.split("/")[0]
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "agentic-workflows",
               "X-GitHub-Api-Version": "2022-11-28"}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    # Query all states, not Git ancestry: squash merges do not preserve the head.
    # Generate each page URL ourselves rather than forwarding credentials to a Link URL.
    for page in range(1, 21):
        query = urlencode({"state": "all", "head": f"{owner}:{branch}",
                           "per_page": 100, "page": page})
        request = Request(f"https://api.github.com/repos/{repo}/pulls?{query}", headers=headers)
        try:
            with urlopen(request, timeout=30) as response:
                pulls = json.load(response)
        except (HTTPError, URLError, OSError, ValueError):
            raise TaskError(f"Cannot establish GitHub PR history for {branch}; check network/API access "
                            "(GH_TOKEN or GITHUB_TOKEN for private repositories or rate limits) "
                            "and inspect its PR/merge state before retrying. Workspace left intact.") from None
        try:
            if not isinstance(pulls, list) or len(pulls) > 100:
                raise ValueError("invalid PR list")
            for pull in pulls:
                if (pull["head"]["ref"] != branch
                        or pull["head"]["repo"]["full_name"].casefold() != repo.casefold()
                        or pull["base"]["repo"]["full_name"].casefold() != repo.casefold()
                        or pull["state"] not in {"open", "closed"}
                        or type(pull["number"]) is not int
                        or (pull["merged_at"] is not None and not isinstance(pull["merged_at"], str))):
                    raise ValueError("unexpected PR identity/state")
                if pull["merged_at"] is not None or pull["state"] == "closed":
                    state = "merged" if pull["merged_at"] is not None else "closed"
                    raise TaskError(f"Historical workspace {branch}: GitHub PR #{pull['number']} is {state}. "
                                    "Inspect/retire it manually or choose a new --slice. "
                                    "No branch or worktree was deleted.")
        except (KeyError, TypeError, ValueError, AttributeError):
            raise TaskError(f"Cannot establish PR history for {branch}: unexpected GitHub response; "
                            "inspect its PR/merge state manually") from None
        if len(pulls) < 100:
            return
    raise TaskError(f"Cannot establish complete PR history for {branch}; inspect it manually")
