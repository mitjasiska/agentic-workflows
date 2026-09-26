"""Agent-independent execution contract and the supported launch adapters."""

from dataclasses import dataclass, field
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import time
from typing import Mapping, Protocol
from uuid import UUID, uuid4

from . import TaskError
from .codex_rpc import CodexRPC
from .config import AgentConfig
from .linear import Issue
from .workspace import Workspace, run


@dataclass(frozen=True)
class AgentOptions:
    """Resolved workflow-level choices, before adapter-specific mapping."""

    kind: str
    model: str | None = None
    mode: str | None = None

    def __post_init__(self) -> None:
        if not re.fullmatch(r"[a-z][a-z0-9_-]*", self.kind):
            raise TaskError("agent must be a lowercase name such as codex or pi")
        if (self.model is not None
                and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/@*+-]*", self.model)):
            raise TaskError("model must not contain whitespace, control characters, or option syntax")
        if self.mode is not None and not re.fullmatch(r"[a-z][a-z0-9_-]*", self.mode):
            raise TaskError("mode must be a lowercase name without whitespace or control characters")


@dataclass(frozen=True)
class AgentOverrides:
    """Optional per-command choices reusable by future workflow commands."""

    kind: str | None = None
    model: str | None = None
    mode: str | None = None


@dataclass(frozen=True)
class AgentExecution:
    """The small workflow-owned request passed unchanged to an adapter."""

    issue: Issue
    repository: Path
    workspace: Workspace
    options: AgentOptions
    handoff: str
    purpose: str = "implementation"
    policy: Mapping[str, object] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not isinstance(self.handoff, str) or not self.handoff.strip():
            raise TaskError("Agent execution handoff must be non-empty text")
        if "\0" in self.handoff:
            raise TaskError("Agent execution handoff contains a NUL character")
        if not isinstance(self.purpose, str) or not self.purpose.strip():
            raise TaskError("Agent execution purpose must be non-empty text")


@dataclass(frozen=True)
class LaunchResult:
    """Adapter result consumed by deterministic workflow output."""

    kind: str
    pane_id: str
    summary: str
    session_id: str | None = None
    turn_id: str | None = None


class AgentAdapter(Protocol):
    kind: str

    def check_available(self) -> None: ...

    def launch(self, execution: AgentExecution) -> LaunchResult: ...


def resolve_agent_options(config: AgentConfig | None, overrides: AgentOverrides) -> AgentOptions:
    """Resolve independent CLI-over-config choices without mutating configuration."""
    kind = overrides.kind if overrides.kind is not None else (config.kind if config else None)
    if kind is None:
        raise TaskError("Configure [agent] kind in ~/.agentic-workflows/config.toml or pass --agent; "
                        "use --no-agent for workspace-only setup")
    model = overrides.model if overrides.model is not None else (config.model if config else None)
    mode = overrides.mode if overrides.mode is not None else (config.mode if config else None)
    return AgentOptions(kind, model, mode)


class HerdrAgentAdapter:
    """Shared Herdr transport; subclasses own executable arguments and prompt injection."""

    kind = ""
    display_name = "Agent"

    def __init__(self, options: AgentOptions):
        if options.kind != self.kind:
            raise TaskError(f"{self.display_name} adapter cannot execute agent {options.kind!r}")
        self.options = options
        self.validate_options()

    def validate_options(self) -> None:
        pass

    def check_available(self) -> None:
        if shutil.which(self.kind) is None:
            raise TaskError(f"{self.kind} is not installed or not on PATH; install/login to "
                            f"{self.display_name} or use --no-agent")

    def validate_execution(self, execution: AgentExecution) -> None:
        if execution.options != self.options:
            raise TaskError(f"{self.display_name} execution options changed after adapter selection")
        if not execution.repository.is_absolute() or not execution.workspace.path.is_absolute():
            raise TaskError("Agent execution requires an absolute repository and worktree")

    def command(self, group: str, operation: str, *args: str) -> dict:
        output = run(["herdr", group, operation, *args])
        expected = {("pane", "list"): "pane_list", ("agent", "start"): "agent_started",
                    ("agent", "get"): "agent_info", ("agent", "prompt"): "agent_prompted"}
        try:
            payload = json.loads(output)
            result = payload["result"]
            if payload.get("error") or result["type"] != expected[group, operation]:
                raise ValueError("unexpected response")
            return result
        except (ValueError, KeyError, TypeError):
            raise TaskError(f"Unexpected Herdr {group} {operation} response") from None

    def validate_agent(self, agent: dict, workspace: Workspace) -> None:
        if (agent["agent"] != self.kind or agent["workspace_id"] != workspace.workspace_id
                or agent["tab_id"] != workspace.tab_id or agent["pane_id"] != workspace.pane_id
                or not Path(agent["cwd"]).is_absolute() or not Path(agent["foreground_cwd"]).is_absolute()
                or not isinstance(agent["terminal_id"], str) or not agent["terminal_id"]
                or Path(agent["cwd"]).resolve() != workspace.path
                or Path(agent["foreground_cwd"]).resolve() != workspace.path):
            raise ValueError("agent target mismatch")

    def check_target(self, workspace: Workspace) -> None:
        panes = self.command("pane", "list", "--workspace", workspace.workspace_id)["panes"]
        if not isinstance(panes, list):
            raise ValueError("invalid panes")
        for pane in panes:
            if pane["workspace_id"] != workspace.workspace_id:
                raise ValueError("pane workspace mismatch")
            if pane.get("agent") == self.kind:
                raise TaskError(f"{self.display_name} already occupies pane {pane['pane_id']}; "
                                "inspect/continue it or exit it before starting a fresh task session. "
                                "Use --no-agent to focus the workspace without launching a duplicate")
        targets = [pane for pane in panes if pane["pane_id"] == workspace.pane_id]
        if len(targets) != 1 or targets[0]["tab_id"] != workspace.tab_id:
            raise ValueError("confirmed pane is no longer present")

    def agent_name(self, workspace: Workspace) -> str:
        # A bounded unique name, independent of issue title and shell syntax.
        return "task-" + hashlib.sha256(
            f"{workspace.path}:{workspace.pane_id}".encode()).hexdigest()[:20]

    def start_agent(self, workspace: Workspace, args: list[str]) -> dict:
        result = self.command("agent", "start", self.agent_name(workspace),
                              "--kind", self.kind, "--pane", workspace.pane_id,
                              "--timeout", "30000", "--", *args)
        self.validate_agent(result["agent"], workspace)
        if (result["agent"]["agent_status"] not in {"idle", "done", "working"}
                or result["argv"] != [self.kind, *args]):
            raise ValueError(f"{self.display_name} launch/readiness not confirmed")
        return result["agent"]

    def confirm_target(self, workspace: Workspace, started: dict) -> None:
        agent = self.command("agent", "get", workspace.pane_id)["agent"]
        self.validate_agent(agent, workspace)
        if (agent["terminal_id"] != started["terminal_id"]
                or (started.get("agent_session") is not None
                    and agent.get("agent_session") != started["agent_session"])):
            raise ValueError("agent session changed during handoff")


class CodexAdapter(HerdrAgentAdapter):
    """Codex TUI adapter with durable app-server prompt receipt confirmation."""

    kind = "codex"
    display_name = "Codex"
    MODES = {"none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"}

    def validate_options(self) -> None:
        mode = "none" if self.options.mode == "off" else self.options.mode
        if mode is not None and mode not in self.MODES:
            raise TaskError("Codex mode must be none/off, minimal, low, medium, high, xhigh, max, or ultra")

    def launch_args(self, workspace: Workspace, bootstrap: str) -> list[str]:
        args = ["--cd", str(workspace.path)]
        if self.options.model is not None:
            args.extend(["--model", self.options.model])
        if self.options.mode is not None:
            mode = "none" if self.options.mode == "off" else self.options.mode
            args.extend(["--config", "model_reasoning_effort=" + json.dumps(mode)])
        return [*args, "--", bootstrap]

    def launch(self, execution: AgentExecution) -> LaunchResult:
        self.validate_execution(execution)
        workspace = execution.workspace
        prompt = execution.handoff
        phase = "startup"
        thread_id = None
        try:
            self.check_target(workspace)
            # A native, single-line readiness turn survives Codex startup dialogs.
            # Its nonce binds the returned Codex thread to this precise launch.
            message_id = str(uuid4())
            bootstrap = (f"Handoff readiness {message_id}. Do not use tools or modify files. "
                         "Reply READY, then wait for the task prompt.")
            args = self.launch_args(workspace, bootstrap)
            # Verify the receipt API can initialize before starting a terminal agent.
            with CodexRPC(workspace.path) as rpc:
                started = self.start_agent(workspace, args)
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
            summary = f"Codex turn {turn_id} confirmed in {workspace.pane_id} (session {thread_id})"
            return LaunchResult(self.kind, workspace.pane_id, summary, thread_id, turn_id)
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

    def find_session(self, rpc, workspace: Workspace, bootstrap: str) -> str:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            result = rpc.request("thread/list", {"cwd": str(workspace.path), "limit": 100},
                                 timeout=max(0, deadline - time.monotonic()))
            if not isinstance(result["data"], list):
                raise ValueError("invalid Codex thread list")
            matches = [thread for thread in result["data"] if thread["preview"] == bootstrap]
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

    def confirm_prompt(self, rpc, workspace: Workspace, thread_id: str,
                       message_id: str | None, prompt: str) -> str:
        # Poll persisted receipt, never resubmit. The terminal-owned TUI executes.
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


class PiAdapter(HerdrAgentAdapter):
    """Pi TUI adapter using Herdr's interactive agent prompt transport."""

    kind = "pi"
    display_name = "Pi"
    MODES = {"off", "minimal", "low", "medium", "high", "xhigh", "max"}

    def validate_options(self) -> None:
        mode = "off" if self.options.mode == "none" else self.options.mode
        if mode is not None and mode not in self.MODES:
            raise TaskError("Pi mode must be off/none, minimal, low, medium, high, xhigh, or max")
        if (self.options.model is not None and ":" in self.options.model
                and self.options.model.rsplit(":", 1)[-1] in self.MODES):
            raise TaskError("Pi model:mode syntax is not accepted by Agentic Workflows; "
                            "remove the thinking suffix from --model and select it with --mode")

    def launch_args(self) -> list[str]:
        args = []
        if self.options.model is not None:
            args.extend(["--model", self.options.model])
        if self.options.mode is not None:
            mode = "off" if self.options.mode == "none" else self.options.mode
            args.extend(["--thinking", mode])
        # Pi has no --cd flag. Herdr starts it in the confirmed pane cwd.
        # Herdr rejects multiline initial-message arguments; submit after startup.
        return args

    def model_mode_capabilities(self, workspace: Workspace) -> tuple[str, str, tuple[str, ...]]:
        """Ask the installed Pi to resolve the model and report its real mode support."""
        executable = shutil.which(self.kind)
        if executable is None:
            raise TaskError("pi is not installed or not on PATH; install/login to Pi or use --no-agent")
        requests = [
            {"id": "state", "type": "get_state"},
            {"id": "levels", "type": "get_available_thinking_levels"},
        ]
        rpc_input = "".join(json.dumps(request) + "\n" for request in requests).encode()
        args = [executable, *self.launch_args(), "--mode", "rpc", "--no-session", "--no-tools"]
        try:
            result = subprocess.run(args, input=rpc_input, capture_output=True, cwd=workspace.path,
                                    timeout=30, check=False)
        except (OSError, subprocess.TimeoutExpired):
            raise TaskError("Pi model/mode preflight could not complete; check Pi authentication and configuration") from None
        if result.returncode:
            raise TaskError("Pi model/mode preflight failed; check the requested model, authentication, and configuration")
        try:
            responses = {}
            for line in result.stdout.decode("utf-8").splitlines():
                payload = json.loads(line)
                if not isinstance(payload, dict):
                    raise ValueError("invalid response")
                response_id = payload.get("id")
                if response_id in {"state", "levels"}:
                    if response_id in responses:
                        raise ValueError("duplicate response")
                    responses[response_id] = payload
            state_response = responses["state"]
            levels_response = responses["levels"]
            if (state_response["type"] != "response" or state_response["command"] != "get_state"
                    or state_response["success"] is not True
                    or levels_response["type"] != "response"
                    or levels_response["command"] != "get_available_thinking_levels"
                    or levels_response["success"] is not True):
                raise ValueError("unexpected response")
            state = state_response["data"]
            model = state["model"]
            provider = model["provider"]
            model_id = model["id"]
            effective = state["thinkingLevel"]
            levels = levels_response["data"]["levels"]
            if (not isinstance(provider, str) or not provider or not isinstance(model_id, str) or not model_id
                    or effective not in self.MODES or not isinstance(levels, list) or not levels
                    or len(set(levels)) != len(levels)
                    or any(level not in self.MODES for level in levels)):
                raise ValueError("invalid capability data")
            return f"{provider}/{model_id}", effective, tuple(levels)
        except (KeyError, TypeError, ValueError, UnicodeError):
            raise TaskError("Pi model/mode preflight returned an unexpected response") from None

    def validate_model_mode(self, workspace: Workspace) -> None:
        if self.options.mode is None:
            return
        requested = "off" if self.options.mode == "none" else self.options.mode
        model, effective, supported = self.model_mode_capabilities(workspace)
        if requested not in supported or effective != requested:
            available = ", ".join(supported)
            raise TaskError(f"Pi model {model!r} does not support requested mode {requested!r}; "
                            f"supported modes: {available}. Pi would use {effective!r} instead")

    def launch(self, execution: AgentExecution) -> LaunchResult:
        self.validate_execution(execution)
        workspace = execution.workspace
        phase = "model/mode validation"
        try:
            self.validate_model_mode(workspace)
            phase = "startup"
            self.check_target(workspace)
            started = self.start_agent(workspace, self.launch_args())
            self.confirm_target(workspace, started)
            session = started.get("agent_session")
            if session is not None and (not isinstance(session, str) or not session):
                raise ValueError("invalid Pi session identifier")
            phase = "prompt submission"
            prompted = self.command("agent", "prompt", workspace.pane_id, execution.handoff)["agent"]
            self.validate_agent(prompted, workspace)
            if (prompted.get("agent_status") not in {"idle", "done", "working"}
                    or prompted["terminal_id"] != started["terminal_id"]
                    or (session is not None and prompted.get("agent_session") != session)):
                raise ValueError("Pi prompt submission target changed")
            self.confirm_target(workspace, started)
            summary = f"Pi prompt submitted in {workspace.pane_id}"
            if session:
                summary += f" (session {session})"
            return LaunchResult(self.kind, workspace.pane_id, summary, session)
        except (KeyError, TypeError, ValueError, OSError):
            raise TaskError(f"Pi {phase} was not confirmed in pane {workspace.pane_id}; "
                            "inspect it before retrying. Workspace left intact") from None
        except TaskError as error:
            raise TaskError(f"Pi {phase} failed in pane {workspace.pane_id}: {error}. "
                            "Workspace left intact; inspect the pane before retrying") from None


ADAPTERS: dict[str, type[HerdrAgentAdapter]] = {
    CodexAdapter.kind: CodexAdapter,
    PiAdapter.kind: PiAdapter,
}


def adapter_for(options: AgentOptions) -> AgentAdapter:
    """Select one adapter without falling back to another requested agent."""
    adapter = ADAPTERS.get(options.kind)
    if adapter is None:
        supported = ", ".join(sorted(ADAPTERS))
        raise TaskError(f"Unsupported agent {options.kind!r}; supported agents: {supported}")
    return adapter(options)


# Compatibility names for code importing the original concrete launcher.
Codex = CodexAdapter
Pi = PiAdapter
