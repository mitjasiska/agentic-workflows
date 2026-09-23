from dataclasses import dataclass
import json
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from . import TaskError

ISSUE_QUERY = """
query TaskIssue($id: String!) {
  issue(id: $id) {
    id identifier title
    project { id name }
    state { id name }
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


@dataclass(frozen=True)
class Issue:
    id: str
    identifier: str
    title: str
    project: str
    state_id: str
    state_name: str
    in_progress_id: str


def text_field(value: dict, key: str) -> str:
    result = value[key]
    if not isinstance(result, str) or not result.strip():
        raise ValueError(key)
    return result


class Linear:
    def __init__(self, api_key: str):
        self._api_key = api_key

    def request(self, query: str, variables: dict) -> dict:
        request = Request(
            "https://api.linear.app/graphql",
            data=json.dumps({"query": query, "variables": variables}).encode(),
            headers={"Authorization": self._api_key, "Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlopen(request, timeout=30) as response:
                payload = json.load(response)
        except HTTPError as error:
            raise TaskError(f"Linear HTTP error {error.code}; check credentials/access or retry") from None
        except (URLError, OSError, ValueError):
            raise TaskError("Linear request failed or returned invalid JSON") from None
        if not isinstance(payload, dict):
            raise TaskError("Unexpected Linear response")
        # Do not echo server error bodies, which may contain request information.
        if payload.get("errors"):
            raise TaskError("Linear API returned errors; check issue identifier and API access")
        if not isinstance(payload.get("data"), dict):
            raise TaskError("Unexpected Linear response: missing data")
        return payload["data"]

    def get_issue(self, identifier: str) -> Issue:
        data = self.request(ISSUE_QUERY, {"id": identifier})
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
            return Issue(text_field(issue, "id"), identifier, text_field(issue, "title"),
                         text_field(issue["project"], "name"), state_id, state_name, progress[0])
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
