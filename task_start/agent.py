"""Agent-independent execution contract and the supported launch adapters."""

from dataclasses import dataclass, field, replace
import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
import subprocess
import time
from typing import Callable, Mapping, Protocol
from uuid import UUID, uuid4

from . import TaskError
from .codex_rpc import CodexRPC
from .config import AgentConfig
from .linear import Issue
from .sessions import SessionInvalid, merge_session, same_session, session_identity
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
    # Optional workflow-owned persistence hook; never includes the semantic handoff.
    runtime_observer: Callable[[dict], None] | None = field(default=None, compare=False, repr=False)

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
    session_kind: str | None = None
    resumability: str = "unknown"
    context_id: str | None = None


class AgentAdapter(Protocol):
    kind: str

    def check_available(self) -> None: ...

    def launch(self, execution: AgentExecution) -> LaunchResult: ...

    def verify_review_session(self, workspace: Workspace, reference: dict) -> dict: ...

    def resume_review(self, execution: AgentExecution, reference: dict, *, recreate: bool) -> LaunchResult: ...

    def review_status(self, execution: AgentExecution, terminal_id: str, reference: dict | None) -> str: ...


def resolve_agent_options(config: AgentConfig | None, overrides: AgentOverrides, *, section: str = "agent") -> AgentOptions:
    """Resolve independent CLI-over-config choices without mutating configuration."""
    kind = overrides.kind if overrides.kind is not None else (config.kind if config else None)
    if kind is None:
        raise TaskError(f"Configure [{section}] kind in ~/.agentic-workflows/config.toml or pass --agent")
    model = overrides.model if overrides.model is not None else (config.model if config else None)
    mode = overrides.mode if overrides.mode is not None else (config.mode if config else None)
    return AgentOptions(kind, model, mode)


def codex_repository_policy(profiles: Mapping[str, str], repository: str) -> dict[str, str]:
    """Translate one explicit local repository override into adapter policy."""
    profile = profiles.get(repository)
    return {"codex_profile": profile} if profile is not None else {}


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

    def command(self, group: str, operation: str, *args: str, timeout: float = 120) -> dict:
        output = run(["herdr", group, operation, *args], timeout=timeout)
        expected = {("pane", "list"): "pane_list", ("agent", "start"): "agent_started",
                    ("pane", "process-info"): "pane_process_info",
                    ("agent", "get"): "agent_info", ("agent", "prompt"): "agent_prompted"}
        try:
            payload = json.loads(output)
            result = payload["result"]
            if payload.get("error") or result["type"] != expected[group, operation]:
                raise ValueError("unexpected response")
            return result
        except (ValueError, KeyError, TypeError):
            raise TaskError(f"Unexpected Herdr {group} {operation} response") from None

    def validate_agent(self, agent: dict, workspace: Workspace, *, review: bool = False,
                       allow_absent: bool = False) -> None:
        if ((agent["agent"] != self.kind and not (allow_absent and agent["agent"] is None))
                or agent["workspace_id"] != workspace.workspace_id
                or (not review and agent["tab_id"] != workspace.tab_id) or agent["pane_id"] != workspace.pane_id
                or not Path(agent["cwd"]).is_absolute() or not Path(agent["foreground_cwd"]).is_absolute()
                or not isinstance(agent["terminal_id"], str) or not agent["terminal_id"]
                or Path(agent["cwd"]).resolve() != workspace.path
                or Path(agent["foreground_cwd"]).resolve() != workspace.path):
            raise ValueError(f"agent target mismatch: agent={agent.get('agent')!r}, "
                             f"pane={agent.get('pane_id')!r}, terminal={agent.get('terminal_id')!r}, "
                             f"cwd={agent.get('cwd')!r}, foreground_cwd={agent.get('foreground_cwd')!r}")

    def check_target(self, workspace: Workspace, *, review: bool = False) -> None:
        panes = self.command("pane", "list", "--workspace", workspace.workspace_id)["panes"]
        if not isinstance(panes, list):
            raise ValueError("invalid panes")
        for pane in panes:
            if pane["workspace_id"] != workspace.workspace_id:
                raise ValueError("pane workspace mismatch")
            if pane.get("agent") == self.kind and not review:
                raise TaskError(f"{self.display_name} already occupies pane {pane['pane_id']}; "
                                "inspect/continue it or exit it before starting a fresh task session. "
                                "Use --no-agent to focus the workspace without launching a duplicate")
        targets = [pane for pane in panes if pane["pane_id"] == workspace.pane_id]
        if (len(targets) != 1 or (not review and targets[0]["tab_id"] != workspace.tab_id)
                or targets[0].get("agent")):
            raise ValueError("confirmed pane is no longer present")

    def agent_name(self, workspace: Workspace) -> str:
        # A bounded unique name, independent of issue title and shell syntax.
        return "task-" + hashlib.sha256(
            f"{workspace.path}:{workspace.pane_id}".encode()).hexdigest()[:20]

    def start_agent(self, workspace: Workspace, args: list[str], *, review: bool = False) -> dict:
        result = self.command("agent", "start", self.agent_name(workspace),
                              "--kind", self.kind, "--pane", workspace.pane_id,
                              "--timeout", "30000", "--", *args)
        self.validate_agent(result["agent"], workspace, review=review)
        if (result["agent"]["agent_status"] not in {"idle", "done", "working"}
                or result["argv"] != [self.kind, *args]):
            raise ValueError(f"{self.display_name} launch/readiness not confirmed: "
                             f"status={result['agent']['agent_status']!r}, argv_match={result['argv'] == [self.kind, *args]}")
        return result["agent"]

    def observe_agent(self, execution: AgentExecution, agent: dict,
                      previous: dict | None = None, *, expected_session=None, allow_absent=False) -> dict:
        """Validate every observation and persist a newly discovered reference immediately."""
        self.validate_agent(agent, execution.workspace, review=execution.purpose == "review", allow_absent=allow_absent)
        reference = agent.get("agent_session")
        identity = session_identity(reference, self.kind)
        established = None
        if previous is not None:
            if agent["terminal_id"] != previous["terminal_id"]:
                raise TaskError("agent terminal changed during handoff")
            established = previous.get("_session_reference", previous.get("agent_session"))
            if established is not None and not same_session(established, reference, self.kind):
                raise TaskError("agent session changed or is no longer reported during handoff")
        if reference is not None and expected_session is not None:
            if not same_session(expected_session, reference, self.kind):
                raise TaskError("agent session does not match the provider session during handoff")
        # Keep known identity fields even if a later legacy report omits the kind.
        # The raw report (including source) is persisted separately below.
        if identity is not None:
            established = merge_session(established or expected_session, reference, self.kind)
        if execution.runtime_observer:
            values = dict(terminal_id=agent["terminal_id"])
            if identity is not None:
                values.update(herdr_session=json.dumps(reference), session_id=established["value"],
                              session_kind=established["kind"])
            execution.runtime_observer(values)
        return dict(agent, _session_reference=established)

    def confirm_target(self, execution: AgentExecution, previous: dict, *, expected_session=None) -> dict:
        agent = self.command("agent", "get", execution.workspace.pane_id)["agent"]
        return self.observe_agent(execution, agent, previous, expected_session=expected_session)

    def verify_review_session(self, workspace: Workspace, reference: dict) -> dict:
        raise TaskError(f"{self.display_name} cannot safely resume this reviewer session")

    def resume_args(self, execution: AgentExecution, reference: dict) -> list[str]:
        raise TaskError(f"{self.display_name} does not support explicit reviewer resume")

    def review_status(self, execution: AgentExecution, terminal_id: str, reference: dict | None) -> str:
        try:
            agent = self.command("agent", "get", execution.workspace.pane_id)["agent"]
        except (TaskError, KeyError, TypeError):
            return "unknown"  # Unavailable observation is not proof of completion.
        if not isinstance(agent, dict):
            return "unknown"
        try:
            if agent["terminal_id"] != terminal_id:
                raise ValueError("terminal changed")
            # A known conflicting reference invalidates identity. Missing metadata
            # during fresh startup is allowed: the pass nonce still binds output.
            if reference and agent.get("agent_session") is not None and not same_session(
                    reference, agent["agent_session"], self.kind):
                raise ValueError("session changed")
            observed_execution = execution
            relocated = agent.get("pane_id") != execution.workspace.pane_id
            if relocated:
                if not isinstance(agent.get("pane_id"), str) or not agent["pane_id"]:
                    return "unknown"
                # A move can race the preceding registry snapshot. Validate all
                # other identity fields now; let the next snapshot prove unique
                # placement and persist it before accepting any result.
                observed_execution = replace(execution, workspace=replace(execution.workspace, pane_id=agent["pane_id"]))
            self.observe_agent(observed_execution, agent, expected_session=reference, allow_absent=True)
            status = agent.get("agent_status")
            if relocated or agent.get("agent") is None or agent.get("launch_pending"):
                return "unknown"
            if status not in {"idle", "done", "working", "blocked"}:
                return "unknown"
            return status
        except (KeyError, TypeError, ValueError):
            raise TaskError("Reviewer runtime identity/status changed or is unknown; inspect its pane") from None

    def resume_review(self, execution: AgentExecution, reference: dict, *, recreate: bool) -> LaunchResult:
        self.validate_execution(execution)
        reference = self.verify_review_session(execution.workspace, reference)
        if execution.runtime_observer:
            execution.runtime_observer(dict(herdr_session=json.dumps(reference)))
        try:
            if recreate:
                self.check_target(execution.workspace, review=True)
                self.start_agent(execution.workspace, self.resume_args(execution, reference), review=True)
            observed = self.command("agent", "get", execution.workspace.pane_id)["agent"]
            self.validate_agent(observed, execution.workspace, review=True)
            if (not same_session(reference, observed.get("agent_session"), self.kind)
                    or observed["agent_status"] not in {"idle", "done"}):
                raise TaskError("Exact reviewer session is not idle and confirmed; no prompt was submitted")
            observed = self.observe_agent(execution, observed, expected_session=reference)
            # Authenticate the observed provider conversation AFTER startup. A
            # path-based provider may have recreated an empty file during launch.
            self.verify_review_session(execution.workspace, reference)
            prompted = self.command("agent", "prompt", execution.workspace.pane_id, execution.handoff)["agent"]
            observed = self.observe_agent(execution, prompted, observed, expected_session=reference)
            self.confirm_target(execution, observed, expected_session=reference)
            return LaunchResult(self.kind, execution.workspace.pane_id, "Reviewer follow-up submitted",
                                reference["value"], session_kind=reference["kind"], resumability="yes")
        except (KeyError, TypeError, ValueError):
            raise TaskError("Reviewer resume identity or delivery was not confirmed; inspect its pane") from None


class CodexAdapter(HerdrAgentAdapter):
    """Codex TUI adapter with durable app-server prompt receipt confirmation."""

    kind = "codex"
    display_name = "Codex"
    MODES = {"none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"}
    SHELL_READY_TIMEOUT = 30

    def validate_options(self) -> None:
        mode = "none" if self.options.mode == "off" else self.options.mode
        if mode is not None and mode not in self.MODES:
            raise TaskError("Codex mode must be none/off, minimal, low, medium, high, xhigh, max, or ultra")

    def validate_execution(self, execution: AgentExecution) -> None:
        super().validate_execution(execution)
        profile = execution.policy.get("codex_profile")
        if (profile is not None
                and (not isinstance(profile, str)
                     or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", profile))):
            raise TaskError("Codex profile must be a portable name without path or option syntax")

    def launch_args(self, execution: AgentExecution, bootstrap: str) -> list[str]:
        workspace = execution.workspace
        args = ["--cd", str(workspace.path)]
        profile = execution.policy.get("codex_profile")
        if profile is not None:
            args.extend(["--profile", profile])
        if self.options.model is not None:
            args.extend(["--model", self.options.model])
        if self.options.mode is not None:
            mode = "none" if self.options.mode == "off" else self.options.mode
            args.extend(["--config", "model_reasoning_effort=" + json.dumps(mode)])
        return [*args, "--", bootstrap]

    def verify_review_session(self, workspace: Workspace, reference: dict) -> dict:
        try:
            if reference["kind"] != "id" or str(UUID(reference["value"])) != reference["value"]:
                raise ValueError("not an exact Codex ID")
        except (KeyError, ValueError, TypeError, AttributeError):
            raise SessionInvalid("Codex reviewer history is missing or mismatched; cannot resume") from None
        try:
            with CodexRPC(workspace.path) as rpc:
                thread = rpc.request("thread/read", {"threadId": reference["value"]})["thread"]
            if (not isinstance(thread["id"], str) or not thread["id"]
                    or not isinstance(thread["cwd"], str) or not Path(thread["cwd"]).is_absolute()):
                raise ValueError("unusable provider response")
            cwd = Path(thread["cwd"]).resolve()
        except (KeyError, ValueError, TypeError, OSError):
            # Missing/malformed API responses and transport failures do not prove
            # deletion. They still stop explicit resume before any handoff.
            raise TaskError("Codex reviewer history verification is temporarily unavailable; retry before resuming") from None
        if thread["id"] != reference["value"] or cwd != workspace.path:
            raise SessionInvalid("Codex reviewer history is missing or mismatched; cannot resume")
        return dict(reference)

    def resume_args(self, execution: AgentExecution, reference: dict) -> list[str]:
        return ["resume", *self.launch_args(execution, "")[:-2], "--", reference["value"]]

    def resume_review(self, execution: AgentExecution, reference: dict, *, recreate: bool) -> LaunchResult:
        """Queue to the exact persisted thread, including when Herdr omits its ID.

        The durable input receipt establishes the recipient; no terminal keystroke
        can accidentally deliver this follow-up to a different conversation.
        """
        self.validate_execution(execution)
        self.verify_review_session(execution.workspace, reference)
        thread_id = reference["value"]
        try:
            if recreate:
                self.check_target(execution.workspace, review=True)
                self.clear_shell_input(execution.workspace)
                self.start_agent(execution.workspace, self.resume_args(execution, reference), review=True)
            observed = self.command("agent", "get", execution.workspace.pane_id)["agent"]
            observed = self.observe_agent(execution, observed, expected_session=reference)
            if observed["agent_status"] not in {"idle", "done"}:
                raise TaskError("Reviewer is not idle; no follow-up was queued")
            message_id = str(uuid4())
            inputs = [{"type": "text", "text": execution.handoff, "text_elements": []}]
            with CodexRPC(execution.workspace.path) as rpc:
                queued = rpc.request("thread/queue/add", {"threadId": thread_id,
                    "clientUserMessageId": message_id, "input": inputs})["queuedSubmission"]
                if (queued["clientUserMessageId"] != message_id or queued["input"] != inputs
                        or not isinstance(queued["id"], str) or not queued["id"]):
                    raise ValueError("queue acknowledgement mismatch")
                turn = self.confirm_prompt(rpc, execution.workspace, thread_id, message_id, execution.handoff)
            self.confirm_target(execution, observed, expected_session=reference)
            return LaunchResult(self.kind, execution.workspace.pane_id, "Exact reviewer follow-up confirmed",
                                thread_id, turn, "id", "yes")
        except (KeyError, ValueError, TypeError, OSError):
            raise TaskError("Codex reviewer continuation was not confirmed; inspect the mapped pane/session") from None

    def clear_shell_input(self, workspace: Workspace) -> None:
        # A prior interrupted launch can leave an executable fragment in the
        # reusable shell's input buffer. Cancel it so Herdr's canonical `codex`
        # command cannot be appended to stale text (for example, `pi` + `codex`).
        # Pane creation/labeling can finish while shell startup children (e.g.
        # lesspipe) still occupy its foreground group. Wait for the same strict
        # shell-only predicate before sending any input. Never retry input/start.
        deadline = time.monotonic() + self.SHELL_READY_TIMEOUT
        established_shell = None
        last = "no completed process observation"

        def timeout_error():
            return TaskError(f"Timed out waiting for an interactive shell prompt before launch; last observed {last}")

        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise timeout_error()
            try:
                process = self.command("pane", "process-info", "--pane", workspace.pane_id,
                                       timeout=remaining)["process_info"]
            except TaskError:
                if time.monotonic() >= deadline:
                    raise timeout_error() from None
                raise
            foreground = process["foreground_processes"]
            shell_pid = process["shell_pid"]
            group = process["foreground_process_group_id"]
            if (process["pane_id"] != workspace.pane_id
                    or any(value is not None and (type(value) is not int or value <= 0)
                           for value in (shell_pid, group))
                    or not isinstance(foreground, list)
                    or any(not isinstance(p, dict) or type(p.get("pid")) is not int or p["pid"] <= 0
                           for p in foreground)):
                raise ValueError("invalid or mismatched shell process observation")
            last = (f"shell_pid={shell_pid}, foreground_group={group}, "
                    f"foreground_pids={[p['pid'] for p in foreground]}")
            # Transport/process scheduling can return even a ready observation
            # after its timeout. Never accept it or send input beyond our bound.
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise timeout_error()
            if established_shell is not None and shell_pid != established_shell:
                raise TaskError(f"Shell identity changed during pane readiness: {last}")
            established_shell = shell_pid
            if shell_pid is not None and group == shell_pid and [p["pid"] for p in foreground] == [shell_pid]:
                break
            time.sleep(min(0.25, remaining))
        run(["herdr", "pane", "send-keys", workspace.pane_id, "ctrl+c"])

    def launch(self, execution: AgentExecution) -> LaunchResult:
        self.validate_execution(execution)
        workspace = execution.workspace
        prompt = execution.handoff
        phase = "pane readiness (before launch)"
        thread_id = None
        try:
            self.check_target(workspace, review=execution.purpose == "review")
            self.clear_shell_input(workspace)
            # A native, single-line readiness turn survives Codex startup dialogs.
            # Its nonce binds the returned Codex thread to this precise launch.
            message_id = str(uuid4())
            bootstrap = (f"Handoff readiness {message_id}. Do not use tools or modify files. "
                         "Reply READY, then wait for the task prompt.")
            args = self.launch_args(execution, bootstrap)
            # Verify the receipt API can initialize before starting a terminal agent.
            phase = "receipt API initialization (before launch)"
            with CodexRPC(workspace.path) as rpc:
                phase = "startup launch/runtime confirmation"
                observed = self.observe_agent(execution, self.start_agent(workspace, args, review=execution.purpose == "review"))
                phase = "readiness confirmation"
                thread_id = self.find_session(rpc, workspace, bootstrap)
                provider_session = dict(agent=self.kind, kind="id", value=thread_id)
                if observed.get("agent_session") is not None and not same_session(
                        observed["agent_session"], provider_session, self.kind):
                    raise TaskError("agent session does not match the provider session during handoff")
                if execution.runtime_observer:
                    execution.runtime_observer(dict(session_id=thread_id, session_kind="id"))
                self.confirm_prompt(rpc, workspace, thread_id, None, bootstrap)
                # Unlike Herdr metadata, the separate receipt API has now read
                # this exact session's persisted readiness turn.
                if execution.runtime_observer:
                    execution.runtime_observer(dict(resumability="yes"))
                observed = self.confirm_target(execution, observed, expected_session=provider_session)
                phase = "prompt queue"
                inputs = [{"type": "text", "text": prompt, "text_elements": []}]
                queued = rpc.request("thread/queue/add", {"threadId": thread_id,
                                     "clientUserMessageId": message_id, "input": inputs})["queuedSubmission"]
                if (queued["clientUserMessageId"] != message_id or queued["input"] != inputs
                        or not isinstance(queued["id"], str) or not queued["id"]):
                    raise ValueError("Codex queue acknowledgement mismatch")
                phase = "prompt delivery/start"
                turn_id = self.confirm_prompt(rpc, workspace, thread_id, message_id, prompt)
            self.confirm_target(execution, observed, expected_session=provider_session)
            summary = f"Codex turn {turn_id} confirmed in {workspace.pane_id} (session {thread_id})"
            return LaunchResult(self.kind, workspace.pane_id, summary, thread_id, turn_id, "id", "yes")
        except (KeyError, TypeError, ValueError, OSError) as error:
            raise TaskError(f"Codex {phase} was not confirmed in pane {workspace.pane_id}; "
                            f"last check: {error}. Inspect it before retrying. Session: {thread_id or 'not yet observed'}. "
                            "Workspace left intact") from None
        except TaskError as error:
            # A timeout may follow successful delivery. Never auto-resubmit or
            # kill a possibly working agent, and never create a fallback session.
            raise TaskError(f"Codex {phase} failed in pane {workspace.pane_id}: {error}. "
                            f"Session: {thread_id or 'not yet observed'}. "
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
        raise TaskError("Codex readiness turn not found (last observed: no session matching the readiness marker); "
                        "inspect the pane for trust or startup dialogs")

    def confirm_prompt(self, rpc, workspace: Workspace, thread_id: str,
                       message_id: str | None, prompt: str) -> str:
        # Poll persisted receipt, never resubmit. The terminal-owned TUI executes.
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            thread = rpc.request("thread/read", {"threadId": thread_id},
                                 timeout=max(0, deadline - time.monotonic()))["thread"]
            if thread["id"] != thread_id or Path(thread["cwd"]).resolve() != workspace.path:
                raise ValueError("Codex receipt workspace/session mismatch")
            cursor, seen = None, set()
            while True:
                params = {"threadId": thread_id, "sortDirection": "asc", "limit": 100}
                if cursor is not None:
                    params["cursor"] = cursor
                page = rpc.request("thread/items/list", params, timeout=max(0, deadline - time.monotonic()))
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
                cursor = page.get("nextCursor")
                if cursor is None:
                    break
                if not isinstance(cursor, str) or not cursor or cursor in seen or len(seen) >= 1000:
                    raise ValueError("ambiguous Codex history pagination")
                seen.add(cursor)
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

    def review_args(self) -> list[str]:
        # Herdr process detection alone reports no Pi session identity. Load a
        # session-only reporter for this process; no global integration install.
        extension = Path(__file__).with_name("pi_session.mjs").resolve()
        if not extension.is_file():
            raise TaskError("Pi review session reporter is missing from this workflow installation")
        return [*self.launch_args(), "--extension", str(extension)]

    def verify_review_session(self, workspace: Workspace, reference: dict) -> dict:
        try:
            path = Path(reference["value"])
            if (reference["kind"] != "path" or not path.is_absolute()
                    or not stat.S_ISREG(path.stat().st_mode)):
                raise ValueError("an exact persisted Pi session path is required")
            with path.open(encoding="utf-8") as source:
                header = json.loads(source.readline())
                if (header["type"] != "session" or not isinstance(header["id"], str) or not header["id"].strip()
                        or ("conversation_id" in reference and header["id"] != reference["conversation_id"])
                        or Path(header["cwd"]).resolve() != workspace.path):
                    raise ValueError("session header mismatch")
                has_history = False
                for line in source:
                    if not line.strip():
                        continue
                    entry = json.loads(line)
                    if not isinstance(entry, dict):
                        raise ValueError("invalid session history")
                    if entry.get("type") == "message":
                        if not isinstance(entry.get("message"), dict):
                            raise ValueError("invalid persisted message")
                        has_history = True
                        break
                if not has_history:
                    raise ValueError("no persisted conversation")
            return dict(reference, conversation_id=header["id"])
        except (KeyError, ValueError, TypeError, FileNotFoundError, NotADirectoryError, IsADirectoryError):
            raise SessionInvalid("Pi reviewer needs an exact persisted session with matching conversation identity, cwd/history") from None
        except OSError:
            raise TaskError("Pi reviewer history verification is temporarily unavailable; retry before resuming") from None

    def resume_args(self, execution: AgentExecution, reference: dict) -> list[str]:
        return [*self.review_args(), "--session", reference["value"]]

    def resume_review(self, execution: AgentExecution, reference: dict, *, recreate: bool) -> LaunchResult:
        self.validate_execution(execution)
        self.validate_model_mode(execution.workspace)
        return super().resume_review(execution, reference, recreate=recreate)

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
            self.check_target(workspace, review=execution.purpose == "review")
            args = self.review_args() if execution.purpose == "review" else self.launch_args()
            observed = self.observe_agent(execution, self.start_agent(workspace, args, review=execution.purpose == "review"))
            observed = self.confirm_target(execution, observed)
            phase = "prompt submission"
            prompted = self.command("agent", "prompt", workspace.pane_id, execution.handoff)["agent"]
            observed = self.observe_agent(execution, prompted, observed)
            if prompted.get("agent_status") not in {"idle", "done", "working"}:
                raise ValueError("Pi prompt submission target changed")
            observed = self.confirm_target(execution, observed)
            identity = session_identity(observed["_session_reference"], self.kind)
            session_id, session_kind = (identity[2], identity[1]) if identity else (None, None)
            summary = f"Pi prompt submitted in {workspace.pane_id}"
            if session_id:
                summary += f" (session {session_id})"
            return LaunchResult(self.kind, workspace.pane_id, summary, session_id,
                                session_kind=session_kind)  # Herdr metadata alone cannot prove resumability.
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
