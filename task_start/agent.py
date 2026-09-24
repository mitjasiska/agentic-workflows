"""Start Codex and deliver an in-memory Linear snapshot to the confirmed pane."""

import hashlib
import json
from pathlib import Path
import shutil
import time
from uuid import UUID, uuid4

from . import TaskError
from .config import AgentConfig
from .codex_rpc import CodexRPC
from .linear import Issue
from .workspace import Workspace, run

INSTRUCTIONS = """- Treat the task above as the source of truth.
- Read AGENTS.md and other repository instructions first.
- Inspect the existing implementation before changing anything.
- Implement only the requested scope.
- Run relevant tests and practical validation.
- Do not commit, push, merge, or open a PR.
- Stop when the implementation is ready for independent review.
- Work in the prepared checkout below; do not create another branch or worktree.
- Do not connect to Linear, re-fetch this issue, or read Linear credentials or the local workflow config.
- Do not write the Linear task description into the repository.
"""


def task_prompt(issue: Issue, workspace: Workspace) -> str:
    scope = (f"\nSlice: {workspace.slice}\nImplement only this slice of the task. Ask if its scope is unclear.\n"
             if workspace.slice is not None else "")
    prompt = (f"Implement Linear issue {issue.identifier}.\n\nTitle:\n{issue.title}\n\n"
              f"Task:\n{issue.description}\n\nInstructions:\n{INSTRUCTIONS}{scope}\n"
              f"Prepared checkout: {workspace.path}\nBranch: {workspace.branch}\n")
    if "\0" in prompt:
        raise TaskError("Linear context contains a NUL character and cannot be delivered to Codex")
    return prompt


class Codex:
    def __init__(self, config: AgentConfig):
        self.config = config

    def check_available(self) -> None:
        if shutil.which("codex") is None:
            raise TaskError("codex is not installed or not on PATH; install/login to Codex or use --no-agent")

    def command(self, group: str, operation: str, *args: str) -> dict:
        output = run(["herdr", group, operation, *args])
        expected = {("pane", "list"): "pane_list", ("agent", "start"): "agent_started",
                    ("agent", "get"): "agent_info"}
        try:
            payload = json.loads(output)
            result = payload["result"]
            if payload.get("error") or result["type"] != expected[group, operation]:
                raise ValueError("unexpected response")
            return result
        except (ValueError, KeyError, TypeError):
            raise TaskError(f"Unexpected Herdr {group} {operation} response") from None

    def validate_agent(self, agent: dict, workspace: Workspace) -> None:
        if (agent["agent"] != "codex" or agent["workspace_id"] != workspace.workspace_id
                or agent["tab_id"] != workspace.tab_id or agent["pane_id"] != workspace.pane_id
                or not Path(agent["cwd"]).is_absolute() or not Path(agent["foreground_cwd"]).is_absolute()
                or not isinstance(agent["terminal_id"], str) or not agent["terminal_id"]
                or Path(agent["cwd"]).resolve() != workspace.path
                or Path(agent["foreground_cwd"]).resolve() != workspace.path):
            raise ValueError("agent target mismatch")

    def launch(self, workspace: Workspace, prompt: str) -> str:
        phase = "startup"
        thread_id = None
        try:
            panes = self.command("pane", "list", "--workspace", workspace.workspace_id)["panes"]
            if not isinstance(panes, list):
                raise ValueError("invalid panes")
            for pane in panes:
                if pane["workspace_id"] != workspace.workspace_id:
                    raise ValueError("pane workspace mismatch")
                if pane.get("agent") == "codex":
                    raise TaskError(f"Codex already occupies pane {pane['pane_id']}; inspect/continue it "
                                    "or exit it before starting a fresh task session. "
                                    "Use --no-agent to focus the workspace without launching a duplicate")
            targets = [p for p in panes if p["pane_id"] == workspace.pane_id]
            if len(targets) != 1 or targets[0]["tab_id"] != workspace.tab_id:
                raise ValueError("confirmed pane is no longer present")
            # A bounded unique name, independent of issue title and user-controlled shell syntax.
            name = "task-" + hashlib.sha256(
                f"{workspace.path}:{workspace.pane_id}".encode()).hexdigest()[:20]
            # A native, single-line readiness turn survives Codex startup dialogs.
            # Its nonce binds the returned Codex thread to this precise launch;
            # never select a session by recency, branch name, or title.
            message_id = str(uuid4())
            bootstrap = (f"Handoff readiness {message_id}. Do not use tools or modify files. "
                         "Reply READY, then wait for the task prompt.")
            args = ["--cd", str(workspace.path), "--model", self.config.model,
                    "--config", "model_reasoning_effort=" + json.dumps(self.config.reasoning),
                    "--", bootstrap]
            # Verify the receipt API can initialize before starting a terminal agent.
            with CodexRPC(workspace.path) as rpc:
                result = self.command("agent", "start", name, "--kind", "codex", "--pane", workspace.pane_id,
                                      "--timeout", "30000", "--", *args)
                self.validate_agent(result["agent"], workspace)
                started = result["agent"]
                if (result["agent"]["agent_status"] not in {"idle", "done", "working"}
                        or result["argv"] != ["codex", *args]):
                    raise ValueError("Codex launch/readiness not confirmed")
                phase = "readiness confirmation"
                thread_id = self.find_session(rpc, workspace, bootstrap)
                self.confirm_prompt(rpc, workspace, thread_id, None, bootstrap)
                self.confirm_target(workspace, started)
                phase = "prompt queue"
                inputs = [{"type": "text", "text": prompt, "text_elements": []}]
                queued = rpc.request("thread/queue/add", {"threadId": thread_id,
                                     "clientUserMessageId": message_id, "input": inputs})["queuedSubmission"]
                if (queued["clientUserMessageId"] != message_id or queued["input"] != inputs
                        or not isinstance(queued["id"], str) or not queued["id"]):
                    raise ValueError("Codex queue acknowledgement mismatch")
                phase = "prompt delivery/start"
                turn_id = self.confirm_prompt(rpc, workspace, thread_id, message_id, prompt)
            self.confirm_target(workspace, started)
            return f"Codex turn {turn_id} confirmed in {workspace.pane_id} (session {thread_id})"
        except (KeyError, TypeError, ValueError, OSError):
            raise TaskError(f"Codex {phase} was not confirmed in pane {workspace.pane_id}; "
                            f"inspect it before retrying. Session: {thread_id or 'not created'}. "
                            "Workspace left intact") from None
        except TaskError as error:
            # A timeout may follow successful delivery. Never auto-resubmit or
            # kill a possibly working agent, and never create a fallback session.
            raise TaskError(f"Codex {phase} failed in pane {workspace.pane_id}: {error}. "
                            f"Session: {thread_id or 'not created'}. "
                            "Workspace left intact; inspect the pane before retrying") from None

    def confirm_target(self, workspace, started):
        agent = self.command("agent", "get", workspace.pane_id)["agent"]
        self.validate_agent(agent, workspace)
        if (agent["terminal_id"] != started["terminal_id"]
                or (started.get("agent_session") is not None
                    and agent.get("agent_session") != started["agent_session"])):
            raise ValueError("agent session changed during handoff")

    def find_session(self, rpc, workspace, bootstrap):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            result = rpc.request("thread/list", {"cwd": str(workspace.path), "limit": 100},
                                 timeout=max(0, deadline - time.monotonic()))
            if not isinstance(result["data"], list):
                raise ValueError("invalid Codex thread list")
            matches = [t for t in result["data"] if t["preview"] == bootstrap]
            if len(matches) > 1:
                raise ValueError("ambiguous Codex readiness sessions")
            if matches:
                thread = matches[0]
                if (str(UUID(thread["id"])) != thread["id"]
                        or Path(thread["cwd"]).resolve() != workspace.path):
                    raise ValueError("Codex readiness session mismatch")
                return thread["id"]
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))
        raise TaskError("Codex readiness turn not found; inspect the pane for trust or startup dialogs")

    def confirm_prompt(self, rpc, workspace, thread_id, message_id, prompt):
        # Poll persisted receipt, never resubmit. This client never starts/resumes
        # a thread: the Herdr TUI owns execution and consumes the durable queue.
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            thread = rpc.request("thread/read", {"threadId": thread_id},
                                 timeout=max(0, deadline - time.monotonic()))["thread"]
            if thread["id"] != thread_id or Path(thread["cwd"]).resolve() != workspace.path:
                raise ValueError("Codex receipt workspace/session mismatch")
            page = rpc.request("thread/items/list", {"threadId": thread_id,
                               "sortDirection": "asc", "limit": 100},
                               timeout=max(0, deadline - time.monotonic()))
            if not isinstance(page["data"], list):
                raise ValueError("invalid Codex history")
            for entry in page["data"]:
                item = entry["item"]
                if item["type"] != "userMessage" or (message_id is not None and item["clientId"] != message_id):
                    continue
                content = item["content"]
                if (len(content) != 1 or content[0]["type"] != "text"
                        or content[0]["text"] != prompt
                        or not isinstance(entry["turnId"], str) or not entry["turnId"]):
                    raise ValueError("Codex recorded different input")
                return entry["turnId"]
            if page.get("nextCursor") is not None:
                raise ValueError("unexpected history before task input")
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))
        raise TaskError("Timed out: Codex did not confirm the exact task prompt in a recorded turn; "
                        "check the pane for startup, trust, authentication, or model errors")
