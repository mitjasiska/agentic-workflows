# Architecture and validation

[Project overview](../README.md) · [Lifecycle reference](lifecycle.md) · [Agent instructions](../AGENTS.md)

- [Execution contract and responsibilities](#agent-execution-architecture)
- [Handoff transport](#handoff-transport)
- [Context identity and registry](#workflow-context-identities)
- [Validation and historical live checks](#validation)

## Agent execution architecture

Agentic Workflows supports multiple agent adapters. Codex is the current default
and most mature adapter; Pi is the second supported implementation. Additional
agents can be implemented as adapters without rewriting task lifecycle logic.

The shared contract in [`task_start/agent.py`](../task_start/agent.py) is deliberately small:

- `AgentOptions` is the resolved agent, model, and workflow mode.
- `AgentExecution` carries the fresh issue snapshot, resolved repository and exact
  worktree, execution purpose, an already-constructed semantic handoff, and an
  extensible workflow-policy mapping. It does not invent purpose-specific wording.
- `LaunchResult` contains the pane/session/turn information deterministic workflow
  code may report. `AgentAdapter` is the launch boundary.

Responsibility is split as follows:

- Core owns fresh Linear retrieval, project/repository resolution, deterministic
  Git and Herdr workspace preparation, status transition, option precedence, and
  construction of the semantic implementation handoff. Implementation and review
  have separate purpose-specific builders in [`task_start/handoff.py`](../task_start/handoff.py).
- An adapter owns executable availability, supported values and mappings, CLI
  arguments, working-directory behavior, prompt transport/receipt mechanics, and
  agent-specific launch validation. Agent-specific capabilities are handled at this boundary.
- Machine-local configuration owns credentials, paths, the default agent/model/
  mode, repository-to-Codex-profile selections, and each CLI's authentication,
  trust, provider, and permission settings.
- Linear content owns task-specific intent and constraints. It is fetched fresh,
  transported in memory, and is never copied into tracked repository files.

The repository entry point is [`task`](../task), with command orchestration in
[`task_start/cli.py`](../task_start/cli.py). The main lifecycle components are
[`workspace.py`](../task_start/workspace.py) for Git/Herdr operations,
[`contexts.py`](../task_start/contexts.py) for context allocation and inspection,
and [`review.py`](../task_start/review.py) for review passes. Review snapshots and
result validation live in [`review_state.py`](../task_start/review_state.py) and
[`review_result.py`](../task_start/review_result.py). External API boundaries are
[`linear.py`](../task_start/linear.py) and [`github.py`](../task_start/github.py).

## Handoff transport

Codex is launched with `--cd` and uses its local app-server API for a readiness
exchange, durable queue submission, and exact recorded-turn confirmation. Pi has
no working-directory flag: Herdr starts it in the already-confirmed task pane and
the adapter validates both reported working directories before submitting the full
handoff through `herdr agent prompt`. Herdr confirms prompt submission, but does
not identify a Pi turn for that prompt. Both remain interactive sessions.

### Codex receipt confirmation

Fresh implementation and reviewer contexts share the same launch boundary.
Pane creation and context allocation/labeling do not prove the shell is ready:
shell startup children can still occupy its foreground process group. Before
sending input, the Codex adapter polls `pane process-info` for up to 30 seconds
(`CodexAdapter.SHELL_READY_TIMEOUT`). The exact pane must report a positive shell
PID, that PID as the foreground group, and only that shell in the group. A changed
shell identity or malformed/mismatched observation fails immediately. A timeout
reports the last shell PID, foreground group, and foreground PIDs, without sending
Ctrl+C or launching Codex.
Each process observation receives only the remaining readiness budget as its
subprocess timeout. The adapter rechecks the deadline after the observation and
rejects even a ready shell reported at or after expiry, before sending input.

Once ready, the adapter clears stale shell input once and initializes the receipt
API. It issues `agent start` once; Herdr owns the subsequent runtime-readiness wait
(`--timeout 30000`). The adapter validates the returned runtime target, status and
exact argv. It never retries `agent start` after a failure or uncertain response.
Session discovery and recorded-turn confirmation below poll observation only;
delayed visibility never repeats launch or queues another handoff. Errors identify
the failed phase; a session that has not been observed is not assumed absent.

Codex starts with a short native prompt containing a random readiness marker. It
asks only for `READY`, without tools or edits. The local stdio app-server API's
`thread/list` identifies the session by that exact marker and checkout;
`thread/read` and `thread/items/list` confirm the readiness turn. The workflow
never selects the latest session or infers its identity from a title.

`thread/queue/add` then delivers the unmodified task to that terminal-owned session
with a unique message ID. History polling must find the same session, checkout,
message ID, exact text, and a turn ID before success. The helper never creates,
resumes, or executes a model session and exits after confirmation. No shared daemon
or service is installed. These APIs (including the experimental queue endpoint)
were validated with Codex CLI 0.157.1; unsupported or malformed responses fail
clearly.

Herdr's `interactive_ready` and `working` states alone do not prove a Codex turn:
startup/trust dialogs can consume terminal input, and `agent prompt --wait` does
not track individual turns. Codex task text is therefore never pasted into the
terminal. Receipt polling has a 30-second deadline and never resends input. A blocked
startup, delivery failure, or timeout is an error, with pane and session IDs for
inspection. First-time repository trust or authentication may require action in
Codex; the workflow never approves those dialogs. A queued task may start after
you resolve a blocker, so inspect that session before retrying.

### Pi prompt submission

Pi starts with only supported `--model`/`--thinking` flags (or no arguments).
Although Pi CLI accepts an initial message, Herdr 0.9.1 rejects the multiline
handoff as an `agent start` argument (`invalid_agent_argument`). After confirming
the canonical `pi` process, exact argv, pane, and foreground/current directories,
the adapter submits the unchanged handoff once through `herdr agent prompt` and
validates the returned terminal, session, pane, and checkout. It deliberately does
not wait for Herdr's generic `working` state: unrelated Pi activity could satisfy
that state, and Herdr does not tie it to a particular prompt. Success therefore
means only that the prompt was submitted, not that a corresponding Pi turn started
or completed. A rejection, timeout, or target mismatch is not success; inspect the
pane before retrying because input may already have been sent. Authentication,
project trust, or model errors remain visible in the pane.

## Workflow context identities

Each new implementation launch receives a visible pane label such as `DEV-20-I1`.
The identifier is the canonical Linear issue ID, followed by `I` (implementation)
or `R` (review) and a monotonic ordinal. These identify agent contexts, not Git
worktrees: contexts can share a checkout. `task review` consumes the same allocation,
session identity, and pane-labeling primitives for independent reviewers.

Inspect contexts without loading workflow configuration or contacting Linear:

```sh
task contexts
task contexts DEV-20
task contexts DEV-20 --all
```

The default view includes non-retired contexts, including uncertain launches and
stale mappings. `--all` adds retired history. Output includes selected agent/model/
mode, recorded lifecycle status, live Herdr status, resumability, and the socket,
workspace, tab and pane needed to locate the context. Unspecified model/mode or
unavailable session evidence is `unknown`. A session reference does not by itself
establish resumability: Pi references remain `unknown` without persistence evidence,
while Codex's receipt API confirms a persisted readiness turn before reporting
`yes`. `task review --resume` rechecks provider history; registry evidence does not
guarantee that the provider will retain that history indefinitely.

### Registry allocation and lifecycle observations

The machine-local registry is `~/.agentic-workflows/contexts.sqlite3`, separate from
project files and Linear. Python's SQLite support supplies atomic transactions and
cross-process allocation locking without a service or dependency. The allocation
commits before labeling or launching; failed and interrupted attempts consume their
ordinal. Retired rows are small tombstones: they keep identity, allocation time,
retirement time and former location, but discard provider and terminal handles.
Cleanup never resets ordinals. Deleting this database is a destructive registry
reset that discards allocation history; normal operations never do that.

Context identity, runtime/session references, resumability, and lifecycle status
are independent fields. The registry records `launching`, `active`, `uncertain`,
and `retired` observations; it does not implement a state-machine framework or
semantic run reports. Adapter observers save real session handles as soon as they
become available in startup, confirmation, or prompt responses, including before
later handoff failures. Subsequent observations must match the established session;
reporting provenance such as `source` is retained separately and is not identity.
Review passes use `reviewing` while one caller owns that reviewer; an atomic registry
claim prevents concurrent follow-ups in the same conversation. Clean, findings,
blocked, and failed verdicts are transient results, not registry lifecycle states.
A failed fresh review launch retains the `uncertain` allocation and any identity
already observed by the adapter. It does not run reviewer finalization against a
possibly unestablished runtime or append a secondary missing-identity error to the
launch failure. Its ordinal remains consumed, even if no session was observed.
A launch interrupted before its result can be recorded remains `launching`;
another concurrent launch cannot claim that pane until the uncertain context is
inspected and cleaned up.

### Live identity inspection and cleanup

Herdr owns the live layout. Inspection never renames, focuses, resumes, repairs, or
rebinds anything. It checks socket, pane and terminal identity, reports manual
renames, absent/replaced agents, missing panes and session mismatches, and uses the
live tab when a pane is rearranged. A move to another workspace can change Herdr's
pane ID; inspection reports a matching terminal at its new location while retaining
the original binding. Contexts belonging to another Herdr socket remain visible
with unknown/stale live state; inspect from that server to reconcile them. Focusing,
typing directly to, or stopping an agent does not change its workflow identity.

`task start` refuses a duplicate agent, and `--no-agent` prepares/focuses the
workspace without an agent; neither allocates or relabels an existing context.
A fresh launch gets the next ordinal. Cleanup retires mappings
only after confirming removal, scoped to the issue, repository, checkout and Herdr
workspace/server, including absent older workspace instances for that same cleaned
checkout. Normal completion and cleanup retries perform the same reconciliation.
Partial cleanup retains mappings and allocation history for a retry. A terminal
moved outside the cleaned workspace also retains its mapping
until its closure can be confirmed. Existing Git/Linear cleanup safety checks
still apply.

## Validation

```sh
python3.12 -m unittest discover -s tests -v
```

Tests mock Linear, GitHub, Herdr, Codex RPC, and process boundaries and exercise Git
safety and authoritative remote lookup in temporary repositories with a local fake
remote. Controlled acceptance coverage drives the normal task-start semantics and
exact handoff construction through both Codex and Pi adapters, including Pi's
separate startup and prompt submission. It also covers pre-existing Pi activity,
model-specific thinking-level clamping, purpose-specific handoffs, option
precedence, unsupported combinations, handoff ordering/failures, exact context,
mutable titles, ambiguity, slices, squash-merge history and stale tracking refs.
Cleanup tests exercise actual worktree/branch removal in disposable repositories,
narrow Python-cache disposal, exact Herdr workspace retirement, preservation on
refusal, repeat runs, and partial-failure reporting.
Tests need no API key, network access, agent installation, or real Herdr workspaces.

For documentation changes, verify relative file links and heading anchors, compare
commands with CLI help, and run `git diff --check`. No dedicated Markdown/link
checker is provided in the repository.

### Recorded live-check limits

These are historical results, not checks rerun by a documentation update.

On 2026-09-29, a disposable Herdr workspace/split-pane probe observed `bash`,
`lesspipe`, and `basename` in a fresh pane's foreground group, followed 55 ms later
by only `bash`. Replaying that observed shape through the pre-fix `task start` and
`task review` paths reproduced the generic startup failure before `agent start`
or the receipt API was called, including review's secondary finalization error.
The probe workspace was closed. This establishes the pre-launch shell race; it
does not claim a live Codex launch or model review. A second disposable probe
exercised the corrected readiness check on one workspace pane and three split
panes. All converged to the shell-only predicate and cleared input once, including
two splits that needed multiple process observations; that workspace was also
closed. Sequenced offline regressions cover both callers, delayed session/receipt
visibility, bounded failures, conflicting identity, exactly-once side effects, and
retained allocation history.

Controlled fresh/resume acceptance tests use real disposable Git task worktrees and
the context SQLite registry, with deterministic Linear/Herdr/provider boundaries.
They verify distinct fresh sessions, exact resume, saved settings, stable state,
and mutation invalidation. The 2026-09-27 live check confirmed Herdr 0.9.1 pane split
identity and preserved task-tab placement, then closed that disposable pane. A live
model fresh/resume smoke could not run in the managed sandbox: Pi could not acquire
its settings/auth locks and Codex's receipt API could not initialize. No live review
verdict is claimed from that check.

The controlled Herdr 0.9.1 smoke test on 2026-09-27 used a temporary registry and
disposable Pi workspaces against the existing checkout, with a no-tools prompt.
It verified visible `DEV-41-I3` and then `DEV-41-I4` after closing the first workspace,
calling the cleanup retirement hook, and reopening the registry. Earlier failed
allocations `I1` and `I2` were also retained. The Git-removal part was simulated by
the retirement hook to preserve the implementation checkout; automated cleanup
tests exercise real disposable Git worktrees. Codex's local session API did not
initialize in the sandbox, so the successful live launch used Pi. All disposable
Herdr workspaces were closed.
