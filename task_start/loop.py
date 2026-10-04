"""Bounded implementation/review routing composed with the single-pass reviewer."""

from dataclasses import asdict, dataclass, replace
import copy
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace
from uuid import uuid4

from . import TaskError
from .agent import AgentExecution, AgentOptions, AgentOverrides, adapter_for, resolve_agent_options
from .config import load_local, load_projects, repository_path, resolve_project
from .contexts import ContextRegistry, HerdrContexts, allocate_launch, pending_launch
from .implementation_pass import binding, implementation_pass, implementation_target, initial_execution
from .linear import Linear
from .loop_state import LoopStore, PauseRequested
from .publication_state import PublicationStore, prepare_review_continuation
from .preparation import prepare_task, require_agent_instructions
from .review import resolve_review_workspace, resolve_reviewer, review_pass
from .review_state import snapshot
from .workspace import Git, Herdr


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=True).encode()).hexdigest()


def environment(identifier, *, allow_bootstrap=False, pending_implementation=None):
    local = load_local()
    issue = Linear(local.api_key).get_issue(identifier)
    project = resolve_project(load_projects(), issue.project)
    repo = repository_path(local, project)
    registry, identities = ContextRegistry(), HerdrContexts()
    env = SimpleNamespace(local=local, issue=issue, project=project, repo=repo, registry=registry,
                          identities=identities, workspace=None)
    if allow_bootstrap and not any(c["role"] == "implementation" for c in registry.list(issue.identifier)):
        if registry.list(issue.identifier, include_retired=True):
            raise TaskError("Context history exists without an established implementation; inspect it before starting")
        return env
    env.workspace, env.anchor, env.base, env.endpoint = resolve_review_workspace(
        issue, project, repo, registry, identities, pending_implementation=pending_implementation)
    return env


def bootstrap(env, options):
    require_agent_instructions(env.issue)
    adapter_for(options).check_available()
    env.workspace = prepare_task(env.issue, env.project, Linear(env.local.api_key), Git(env.repo),
                                 lambda: Herdr(env.repo), default_only=True)
    env.endpoint = env.identities.endpoint()
    env.base = Git(env.repo).command("rev-parse", "--verify", f"refs/heads/{env.project.base_branch}^{{commit}}").strip()


def task_binding(env):
    return dict(issue=env.issue.identifier, repository=str(env.repo), worktree=str(env.workspace.path),
                branch=env.workspace.branch, workspace_id=env.workspace.workspace_id, endpoint=env.endpoint,
                base_branch=env.project.base_branch)


def requirements(env):
    return fingerprint(dict(title=env.issue.title, description=env.issue.description, project=env.issue.project))


def idle_reviewers(env, expected=None):
    """Do not start edits alongside another live/uncertain reviewer turn."""
    for item in env.registry.list(env.issue.identifier):
        if item["role"] != "review":
            continue
        context, pane = resolve_reviewer(item["context_id"], env.issue, env.workspace, env.repo,
                                         env.endpoint, env.registry, env.identities)
        if expected and item["context_id"] == expected["context_id"] and binding(context) != expected:
            raise TaskError("Exact reviewer session/settings changed; no replacement is allowed")
        options = AgentOptions(context["agent"], context["model"], context["mode"])
        adapter = adapter_for(options)
        reference = adapter.verify_review_session(env.workspace, binding(context)["session"])
        if pane is not None and pane.get("agent") is not None:
            workspace = replace(env.workspace, pane_id=pane["pane_id"], tab_id=pane["tab_id"])
            execution = AgentExecution(env.issue, env.repo, workspace, options, "Loop boundary identity check",
                                       purpose="review", policy=dict(read_only=True))
            if adapter.review_status(execution, pane["terminal_id"], reference) not in {"idle", "done"}:
                raise TaskError("Reviewer is not confirmed idle; no implementation handoff is safe")
    if expected and expected["context_id"] not in {c["context_id"] for c in env.registry.list(env.issue.identifier)}:
        raise TaskError("Saved reviewer context is missing; no replacement is allowed")


@dataclass(frozen=True)
class LoopResult:
    """Versioned delivery seam for callers/future report persistence; no archive API."""
    data: dict

    @property
    def state(self):
        return self.data["state"]

    def as_dict(self):
        return self.data

    def render(self):
        value = self.data
        lines = [f"{value['issue']} loop: {value['state']} — {value['passes']} passes, {value['reviews']} reviews",
                 value["reason"],
                 f"Contexts: {value['implementation_context']} / {value['reviewer_context'] or 'reviewer not started'}"]
        for item in value["implementation"]:
            lines.append(f"Implementation ({item['phase']}): {item['summary']}")
        for item in value["review_iterations"]:
            lines.append(f"Review {item['iteration']}: {item['state']} — {item['summary']}")
            for finding in item["findings"]:
                lines.append(f"  {finding['id']} [{finding['category']}/{finding['severity']}]: "
                             f"{finding['explanation']} ({finding['evidence']})")
        for item in value["resolutions"]:
            lines.append(f"Fix {item['finding_id']}: {item['summary']}")
        for check in value["validation"]:
            lines.append(f"Check {check['name']}: {check['result']} — {check['details']}")
        lines.append(f"Final review: {value['final_review_state']}; human action required: "
                     + ("yes" if value["human_action_required"] else "no"))
        if value["next_phase"]:
            lines.append(f"Next boundary: {value['next_phase']}")
        if value["pause_requested"]:
            lines.append("Pause-after-current requested (sticky).")
        return "\n".join("".join(c if c.isprintable() else "?" for c in line) for line in lines)


def report(state, pause=False):
    implementations, reviews, resolutions, checks = [], [], [], []
    for record in state["records"]:
        if record["phase"] in {"review", "rereview"}:
            reviews.append(dict(iteration=len(reviews) + 1, state=record["state"], summary=record["summary"],
                                findings=record["findings"]))
        else:
            implementations.append(dict(phase=record["phase"], summary=record["summary"]))
        resolutions.extend(record.get("resolutions", []))
        for check in record["checks"]:
            if check not in checks:
                checks.append(check)
    return LoopResult(dict(version=1, run_id=state["run_id"], issue=state["binding"]["issue"],
        state=state["status"], reason=state["reason"], passes=state["pass_count"], reviews=state["review_count"],
        implementation_context=state["implementation"]["context_id"],
        reviewer_context=state["reviewer"]["context_id"] if state["reviewer"] else None,
        implementation=implementations, review_iterations=reviews, resolutions=resolutions, validation=checks,
        final_review_state=reviews[-1]["state"] if reviews else "not_run",
        human_action_required=state["status"] != "clean", next_phase=state["next_phase"],
        pause_requested=pause, active_pass=state["active_pass"]))


def new_state(env, reviewer_options, max_reviews, max_passes, timeout, *, from_review=False,
              initial_options=None):
    if initial_options is None:
        context, *_ = implementation_target(env)
        implementation = binding(context)
        idle_reviewers(env)
    else:
        # Reserve before checkpoint creation so all controls can locate the run.
        # An interruption in this gap leaves a launching context, never a retry.
        if env.registry.list(env.issue.identifier, include_retired=True):
            raise TaskError("Context history changed before initial allocation; inspect before starting")
        execution = initial_execution(env, initial_options)
        implementation = dict(context_id=allocate_launch(execution, env.registry, env.identities))
    state = dict(version=1, run_id=str(uuid4()), binding=task_binding(env), requirements=requirements(env),
        implementation=implementation, reviewer=None, reviewer_options=asdict(reviewer_options),
        status="ready", reason="Implementation completion pending", next_phase="implementation", active_pass=None,
        pass_count=0, review_count=0, max_reviews=max_reviews, max_passes=max_passes, timeout=timeout,
        snapshot=snapshot(env.workspace.path, env.base, env.workspace.branch).as_dict(),
        findings=[], seen=[], records=[])
    if initial_options is not None:
        state.update(version=3, initial_phase="initial_implementation", next_phase="initial_implementation",
                     implementation_options=asdict(initial_options), reason="Initial implementation pending")
    if from_review:
        # Keep the default checkpoint path intact. Review-first runs need an
        # explicit origin so validation never infers a missing completion pass.
        state.update(version=2, initial_phase="review", next_phase="review",
                     reason="Explicit review start; independent review pending")
    return state


def route(state, result, after):
    """Only structured categories/IDs and snapshots drive transitions; no prose inference."""
    phase = state["next_phase"]
    record = dict(phase=phase, **result)
    state["records"].append(record)
    state.update(active_pass=None, status="ready")
    before = state["snapshot"]
    state["snapshot"] = after
    if result["state"] in {"failed", "blocked"}:
        state.update(status="escalated", reason=result["summary"])
    elif phase in {"initial_implementation", "implementation", "fixes"}:
        if result["state"] != "completed":
            raise TaskError("Implementation outcome cannot be routed")
        if before["head"] != after["head"]:
            state.update(status="escalated", reason="Implementation changed Git history; inspect before proceeding")
        elif phase == "fixes" and before["content"] == after["content"]:
            state.update(status="escalated", reason="Fix pass made no Git-visible progress on the review findings")
        else:
            state.update(next_phase="rereview" if phase == "fixes" else "review",
                         reason="Implementation result collected; independent review pending")
    elif before != after:
        state.update(status="escalated", reason="Checkout changed across the review boundary; inspect the invalid result")
    elif result["state"] == "clean":
        state.update(status="clean", next_phase=None, findings=[], reason="Implementation and independent review completed cleanly")
    elif result["state"] == "findings":
        findings = result["findings"]
        if not findings or any(f.get("category") != "implementation" or not f.get("id") for f in findings):
            state.update(status="escalated", reason="Review requires a human decision or cannot be safely classified")
        else:
            ids = sorted(f["id"] for f in findings)
            # Also detect identical substantive output with renamed finding IDs.
            signature = fingerprint(sorted((dict((k, v) for k, v in f.items() if k != "id") for f in findings),
                                           key=lambda f: json.dumps(f, sort_keys=True)))
            previous = {f["id"] for f in state["findings"]}
            repeated = any(s["ids"] == ids or s["signature"] == signature for s in state["seen"])
            if repeated or previous and previous <= set(ids):
                state.update(status="escalated", reason="Review findings repeated or made no structured progress")
            else:
                state.update(next_phase="fixes", reason="Actionable implementation findings pending fixes")
            state["seen"].append(dict(ids=ids, signature=signature))
        state["findings"] = findings
    else:
        raise TaskError("Review outcome cannot be routed")
    if state["status"] == "escalated":
        state["next_phase"] = None


class LoopRuntime:
    def __init__(self, identifier, publication):
        self.identifier, self.publication = identifier, publication
        self.env = None

    def prepare(self, state):
        initial = state["next_phase"] == "initial_implementation"
        env = environment(self.identifier, pending_implementation=state["implementation"]["context_id"] if initial else None)
        if task_binding(env) != state["binding"] or requirements(env) != state["requirements"]:
            raise TaskError("Task requirements or exact checkout binding changed; human inspection is required")
        if snapshot(env.workspace.path, env.base, env.workspace.branch).as_dict() != state["snapshot"]:
            raise TaskError("Checkout/base changed since the saved boundary; no completed pass will be replayed")
        if initial:
            options = AgentOptions(**state["implementation_options"])
            adapter_for(options).check_available()
            pending_launch(initial_execution(env, options), state["implementation"]["context_id"],
                           env.registry, env.identities)
        else:
            implementation_target(env, state["implementation"])
        idle_reviewers(env, state["reviewer"])
        self.env = env

    def execute(self, state, before_handoff, pass_observer):
        env, phase = self.env, state["next_phase"]
        if phase in {"initial_implementation", "implementation", "fixes"}:
            saved = self.publication.read()
            if saved["rebase"] is not None and saved["rebase"].get("result") is None:
                raise TaskError("Unpublished rebase is pending; inspect publication before implementation")
            prepare_review_continuation(saved, Git(env.workspace.path),
                {k: state["binding"][k] for k in ("issue", "repository", "worktree", "branch", "base_branch")}, env.base)
            saved.update(acceptance=None, intent=None)
            self.publication.write(saved)
            return implementation_pass(env, state["implementation"], state["findings"], state["timeout"],
                                       before_handoff, pass_observer,
                                       initial_options=AgentOptions(**state["implementation_options"])
                                       if phase == "initial_implementation" else None)
        options = state["reviewer_options"]
        feedback = ({} if phase == "review" else
                    dict(findings=state["findings"], resolutions=state["records"][-1]["resolutions"]))
        result = review_pass(env.issue, env.project, env.repo, env.registry, env.identities,
            env.workspace, env.anchor, env.base, env.endpoint, env.local,
            state["reviewer"]["context_id"] if state["reviewer"] else None,
            options["kind"] if phase == "review" else None,
            options["model"] if phase == "review" else None,
            options["mode"] if phase == "review" else None,
            state["timeout"], self.publication, loop_feedback=feedback,
            before_handoff=before_handoff, pass_observer=pass_observer)
        if result.context_id and result.state in {"clean", "findings"}:
            state["reviewer"] = binding(env.registry.get(result.context_id))
        return dict(pass_id=result.pass_id, context_id=result.context_id, state=result.state,
                    summary=result.summary, findings=result.findings, checks=result.checks, resolutions=[])

    def snapshot(self):
        env = self.env
        return snapshot(env.workspace.path, env.base, env.workspace.branch).as_dict()


def drive(store, state, runtime, *, on_pass_result=None):
    """Single controller, deterministic bounds, no retries of uncertain delivery.

    on_pass_result is an optional future persistence seam, invoked only after the
    completed result and next boundary are checkpointed. It never owns routing.
    """
    try:
        while state["status"] == "ready":
            _, pause = store.read()
            if pause:
                raise PauseRequested()
            if state["pass_count"] >= state["max_passes"] or (
                    state["next_phase"] in {"review", "fixes", "rereview"}
                    and state["review_count"] >= state["max_reviews"]):
                state.update(status="escalated", reason="Autonomous pass/review limit reached; human inspection required")
                break
            runtime.prepare(state)

            def observe(context_id, pass_id, implementation=None):
                state["active_pass"].update(context_id=context_id, pass_id=pass_id)
                if implementation is not None:
                    if state["implementation"]["context_id"] != implementation["context_id"]:
                        raise TaskError("Implementation context changed during launch")
                    state["implementation"] = implementation
                if state["next_phase"] == "review":
                    state["reviewer"] = dict(context_id=context_id)
                store.save(state)

            def begin():
                # Recheck after potentially slow provider/session preflight.
                if runtime.snapshot() != state["snapshot"]:
                    raise TaskError("Checkout changed before the handoff claim; inspect the saved boundary")
                store.begin(state)

            result = runtime.execute(state, begin, observe)
            if state["status"] != "running":
                raise TaskError("Agent pass returned without a recorded handoff claim")
            route(state, result, runtime.snapshot())
            store.save(state)
            if on_pass_result:
                on_pass_result(copy.deepcopy(result))
    except PauseRequested:
        state.update(status="paused", reason="Paused after collecting the current pass; use --continue explicitly")
    except (KeyboardInterrupt, SystemExit):
        state.update(status="interrupted", reason="Controller interrupted; handoff completion is uncertain. Inspect contexts; --continue is refused")
    except Exception as error:
        state.update(status="escalated", reason=f"{error}. Inspect contexts before recovery; no handoff was retried")
    store.save(state)
    return report(*store.read())


def control_store(identifier):
    """Pause/status/continue locate exact private state without Linear or local config."""
    contexts = ContextRegistry().list(identifier)
    paths = {c["worktree"] for c in contexts if c["worktree"]}
    if len(paths) != 1:
        raise TaskError("Loop control requires exactly one registered task checkout")
    path = Path(paths.pop())
    store = LoopStore(path)
    state, _ = store.read()
    if state["binding"]["issue"] != identifier or state["binding"]["worktree"] != str(path):
        raise TaskError("Loop checkpoint does not match the exact issue/checkout")
    return store, path


def loop(identifier, *, action="run", agent_kind=None, model=None, mode=None,
         max_reviews=None, max_passes=None, timeout=None, from_review=False, on_pass_result=None,
         impl_agent_kind=None, impl_model=None, impl_mode=None):
    implementation_overrides = (impl_agent_kind, impl_model, impl_mode)
    overrides = (agent_kind, model, mode, max_reviews, max_passes, timeout, *implementation_overrides)
    if action not in {"run", "new", "continue", "pause", "status"}:
        raise TaskError("Unknown loop control action")
    if from_review and action not in {"run", "new"}:
        raise TaskError("--from-review only starts a new loop; controls preserve the saved boundary")
    if action not in {"run", "new"} and any(v is not None for v in overrides):
        raise TaskError("Loop controls preserve recorded settings/limits and do not accept selection overrides")
    if action in {"pause", "status", "continue"}:
        store, path = control_store(identifier)
        if action == "pause":
            return report(*store.pause())
        if action == "status":
            return report(*store.read())
        with PublicationStore(path).locked() as publication:
            state = store.continue_paused()
            return drive(store, state, LoopRuntime(identifier, publication), on_pass_result=on_pass_result)
    max_reviews = 3 if max_reviews is None else max_reviews
    max_passes = 6 if max_passes is None else max_passes
    timeout = 1800 if timeout is None else timeout
    if (type(max_reviews) is not int or not 1 <= max_reviews <= 20
            or type(max_passes) is not int or not 1 <= max_passes <= 40
            or not isinstance(timeout, (int, float)) or not 0 < timeout <= 86400):
        raise TaskError("Loop requires 1..20 reviews, 1..40 passes, and a positive timeout up to 86400 seconds")
    if from_review and any(v is not None for v in implementation_overrides):
        raise TaskError("--i-* options require a from-scratch loop, not --from-review")
    env = environment(identifier, allow_bootstrap=not from_review)
    options = resolve_agent_options(env.local.reviewer, AgentOverrides(agent_kind, model, mode),
                                    section="reviewer", agent_flag="--r-agent")
    if options.model is None or options.mode is None:
        raise TaskError("Loop reviewer requires explicit model and mode ([reviewer] or --r-model/--r-mode)")
    adapter_for(options).check_available()
    initial_options = None
    if env.workspace is None:
        initial_options = resolve_agent_options(env.local.agent, AgentOverrides(*implementation_overrides),
                                                agent_flag="--i-agent")
        if initial_options.model is None or initial_options.mode is None:
            raise TaskError("Loop implementation requires explicit model and mode ([agent] or --i-model/--i-mode)")
        bootstrap(env, initial_options)
    elif any(v is not None for v in implementation_overrides):
        raise TaskError("--i-* options require a from-scratch loop; existing implementation settings are preserved")
    with PublicationStore(env.workspace.path).locked() as publication:
        store = LoopStore(env.workspace.path)
        state = new_state(env, options, max_reviews, max_passes, timeout, from_review=from_review,
                          initial_options=initial_options)
        store.create(state, replace=action == "new")
        return drive(store, state, LoopRuntime(identifier, publication), on_pass_result=on_pass_result)
