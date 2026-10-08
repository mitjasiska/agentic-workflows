from dataclasses import dataclass
import json
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from . import TaskError

ISSUE_QUERY = """
query TaskIssue($id: String!) {
  issue(id: $id) {
    id identifier title description url
    labels(first: 250) { nodes { name } pageInfo { hasNextPage } }
    project { id name }
    state { id name type }
    team { id states(first: 250) {
      nodes { id name }
      pageInfo { hasNextPage }
    } }
  }
}
"""
UPDATE_QUERY = """
mutation StartTask($id: String!, $state: String!) {
  issueUpdate(id: $id, input: {stateId: $state}) {
    success issue { id state { id name } }
  }
}
"""

READ_BACKOFF = (0.5, 1.0)
RETRYABLE_HTTP = {408, 429, 500, 502, 503, 504}


class LinearUnavailable(TaskError):
    """A read exhausted its bounded availability retries; no mutation was sent."""


@dataclass(frozen=True)
class Issue:
    id: str
    identifier: str
    title: str
    project: str
    state_id: str
    state_name: str
    in_progress_id: str
    description: str = ""
    state_type: str = ""
    url: str = ""
    labels: tuple[str, ...] = ()


def text_field(value: dict, key: str) -> str:
    result = value[key]
    if not isinstance(result, str) or not result.strip():
        raise ValueError(key)
    return result


class Linear:
    def __init__(self, api_key: str):
        self._api_key = api_key

    def request(self, query: str, variables: dict, *, retry_read: bool = False) -> dict:
        request = Request(
            "https://api.linear.app/graphql",
            data=json.dumps({"query": query, "variables": variables}).encode(),
            headers={"Authorization": self._api_key, "Content-Type": "application/json"},
            method="POST",
        )
        # Only get_issue opts in. GraphQL uses POST for reads and mutations alike;
        # neither HTTP method nor a transient response authorizes replaying writes.
        for attempt in range(len(READ_BACKOFF) + 1):
            transient = False
            try:
                with urlopen(request, timeout=30) as response:
                    payload = json.load(response)
                break
            except HTTPError as error:
                diagnostic = f"Linear HTTP error {error.code}"
                transient = error.code in RETRYABLE_HTTP
            except (URLError, TimeoutError, ConnectionError):
                diagnostic = "Linear connection failed or timed out"
                transient = True
            except (OSError, ValueError):
                diagnostic = "Linear request failed or returned invalid JSON"
            if not retry_read:
                raise TaskError(diagnostic + "; update outcome may be uncertain; retrieve current issue state before retrying") from None
            if not transient:
                raise TaskError(diagnostic + "; check issue data, credentials and access") from None
            if attempt == len(READ_BACKOFF):
                raise LinearUnavailable(diagnostic + f"; issue read unavailable after {attempt + 1} attempts") from None
            delay = READ_BACKOFF[attempt]
            print(f"{diagnostic}; retry {attempt + 1}/{len(READ_BACKOFF)} of read-only issue retrieval in {delay:g}s",
                  file=sys.stderr)
            time.sleep(delay)
        if not isinstance(payload, dict):
            raise TaskError("Unexpected Linear response")
        # Do not echo server error bodies, which may contain request information.
        if payload.get("errors"):
            raise TaskError("Linear API returned errors; check issue identifier and API access")
        if not isinstance(payload.get("data"), dict):
            raise TaskError("Unexpected Linear response: missing data")
        return payload["data"]

    def get_issue(self, identifier: str) -> Issue:
        data = self.request(ISSUE_QUERY, {"id": identifier}, retry_read=True)
        if data.get("issue", False) is None:
            raise TaskError(f"Linear issue {identifier} was not found")
        try:
            issue = data["issue"]
            if issue.get("project", False) is None:
                raise TaskError(f"Linear issue {identifier} has no project")
            if text_field(issue, "identifier") != identifier:
                raise ValueError("identifier mismatch")
            text_field(issue["team"], "id")
            states = issue["team"]["states"]
            if states["pageInfo"]["hasNextPage"] is not False:
                raise ValueError("incomplete states")
            if not isinstance(states["nodes"], list):
                raise ValueError("invalid states")
            progress = [text_field(s, "id") for s in states["nodes"]
                        if text_field(s, "name") == "In Progress"]
            state_name = text_field(issue["state"], "name")
            state_id = text_field(issue["state"], "id")
            if state_name == "In Progress":
                progress = [state_id]
            if len(progress) != 1:
                raise TaskError("Issue team must have exactly one status named In Progress")
            description = issue["description"]
            if description is None:
                description = ""
            if not isinstance(description, str):
                raise ValueError("invalid description")
            labels = issue.get("labels", {"nodes": [], "pageInfo": {"hasNextPage": False}})
            if labels["pageInfo"]["hasNextPage"] is not False or not isinstance(labels["nodes"], list):
                raise ValueError("incomplete labels")
            names = tuple(text_field(label, "name") for label in labels["nodes"])
            url = issue.get("url", "")
            if not isinstance(url, str):
                raise ValueError("invalid issue URL")
            return Issue(text_field(issue, "id"), identifier, text_field(issue, "title"),
                         text_field(issue["project"], "name"), state_id, state_name, progress[0], description,
                         text_field(issue["state"], "type"), url, names)
        except (KeyError, TypeError, ValueError, AttributeError):
            raise TaskError("Unexpected Linear issue response") from None

    def start(self, issue: Issue) -> None:
        if issue.state_name == "In Progress":
            return
        data = self.request(UPDATE_QUERY, {"id": issue.id, "state": issue.in_progress_id})
        try:
            update = data["issueUpdate"]
            if (update["success"] is not True or update["issue"]["id"] != issue.id
                    or update["issue"]["state"]["id"] != issue.in_progress_id
                    or update["issue"]["state"]["name"] != "In Progress"):
                raise ValueError("update not confirmed")
        except (KeyError, TypeError, ValueError):
            raise TaskError("Linear did not confirm the In Progress update; check the issue and retry") from None
