# Agentic Workflows

Reusable tooling, skills, and automation for efficient agentic software development workflows.

The goal is to reduce repetitive development mechanics and make working with coding agents across multiple projects fast, consistent, and predictable.

## Linear task creation skill

[`skills/create-linear-task`](skills/create-linear-task/SKILL.md) turns a rough development idea into a concise Linear issue that can be handed directly to an implementation agent. It keeps the human specification visible, puts execution-only guidance and workflow metadata in a collapsed `Agent instructions` block, and supports implementation, research, and experiment tasks.

Use the skill from this repository or install the entire directory into Codex's skill location. The directory includes a generated [`config.toml`](skills/create-linear-task/config.toml), which the renderer finds relative to the installed skill—not the source checkout. Repository [`config/projects.toml`](config/projects.toml) is the single human-edited source for project mappings; do not edit project mappings in the packaged or installed snapshot.

From the repository root, an initial installation or full refresh is:

```sh
skill_dest="${CODEX_HOME:-$HOME/.codex}/skills/create-linear-task"
mkdir -p "$skill_dest"
cp -R skills/create-linear-task/. "$skill_dest/"
```

After changing canonical project mappings, regenerate and check the packaged snapshot, then refresh that same installed directory:

```sh
python3.12 skills/create-linear-task/scripts/sync_config.py
python3.12 skills/create-linear-task/scripts/sync_config.py --check
skill_dest="${CODEX_HOME:-$HOME/.codex}/skills/create-linear-task"
mkdir -p "$skill_dest"
cp -R skills/create-linear-task/. "$skill_dest/"
cmp skills/create-linear-task/config.toml "$skill_dest/config.toml"
```

`cmp` exits successfully only when the installed configuration matches the generated package. The drift check is also covered by the repository test suite. Pass `--config /path/to/generated-config.toml` only to use an intentionally prepared alternative package configuration. Missing exact targets or `linear_team` values fail rather than being guessed.
Start a new Codex session after installation or refresh so skill discovery uses the updated copy.

The deterministic renderer uses that bundled configuration to select an exact Linear project/team and apply a closed workflow taxonomy. A task has at most one primary category (`feature`, `bug`, `chore`, `docs`, or `refactor`) plus an independent Research modifier. Task kind remains separate.

The taxonomy maps those categories to the exact canonical labels `Feature`, `Bug`, `Chore`, `Docs`, and `Refactor`, and to the matching Conventional Commit types. The modifier maps to the exact `Research` label. Before mutation, the skill resolves all six names exactly once against the complete catalog applicable to the selected team—even labels unused by the current task. A missing or ambiguous canonical label stops instead of being guessed or created.

The packaged configuration contains the known `agentic-workflows` team selector. New issues use the configured team/project. Refinements carry their current team/project IDs explicitly, require their names to match the configured target, and never include a target change in the issue mutation; a mismatch stops rather than moving the issue. Post-mutation verification retains those original IDs and compares them with a separate reread target, so unchanged names cannot hide a move. No relationship is inferred between an issue prefix such as `DEV-` and team selection. Existing unrelated labels—including archived labels such as `Improvement` that may be absent from the active catalog—are preserved as opaque IDs. For refinements, the skill re-reads current labels immediately before mutation and applies only canonical workflow-label additions/removals; it never replaces all labels from the earlier drafting snapshot. It then re-reads and verifies the target and workflow labels, reporting concurrent changes without automatically repairing them. Workflow-label replacement is disabled unless a project explicitly enables `replace_workflow_labels`, and the skill never creates, renames, or deletes label objects.

Descriptions use Linear's API Markdown form `+++ Section title … +++` for collapsed sections. The similar `>>>` syntax is an interactive-editor shortcut and is intentionally not emitted by this API-oriented skill.

## Setup and use

Requires Python 3.12, Git, and Herdr on `PATH`, with a running Herdr session.
Agent handoff also requires the selected, authenticated Codex or Pi CLI on `PATH`.
The commands and JSON responses were checked against Herdr 0.9.1 and Codex CLI
0.157.1. Pi transport was checked against Pi CLI 0.87.1 and Herdr 0.9.1;
run `pi --help` to verify the installed interface.

1. Clone `agentic-workflows` and your project repositories.
2. Copy `config/local.example.toml` to `~/.agentic-workflows/config.toml` (create the directory first).
3. Set `projects_root`, your Linear personal API key, and `[agent]` in that local file. The key needs read access to the issues, projects and team statuses, and permission to update issues. Keep it private; never commit the populated file.
4. Add the cloned repository directory to your `PATH`, then run:

   ```sh
   task start DEV-7
   ```

The executable `task` lives at the repository root. For example, in a POSIX shell:

```sh
export PATH="/absolute/path/to/agentic-workflows:$PATH"
task start DEV-7
```

It works from any directory; a symlink to `task` on your `PATH` works too. No package installation is required. On Windows, invoke `py -3.12 C:/path/to/agentic-workflows/task start DEV-7`; a global Windows launcher is not included yet.

Portable project metadata is committed in `config/projects.toml`. Match `linear_project` exactly to the Linear project name and set `repo_name` and `base_branch`. Machine-specific paths and credentials live only in `~/.agentic-workflows/config.toml`:

```toml
projects_root = "~/projects" # Or "C:/development/projects" on Windows

[linear]
api_key = "" # Fill in your personal API key locally

[agent]
kind = "codex"
model = "gpt-6-astra"
mode = "high"
```

`kind` may be `codex` (the default implementation) or `pi`. `model` and `mode`
are optional; when omitted, the selected agent's own local default is used. The
legacy `reasoning = "high"` field remains accepted as an alias for `mode` so
existing Codex installations continue to work. Do not set both fields.

Model names are passed unchanged to the selected CLI. Workflow `mode` means
reasoning/thinking effort, not Pi's unrelated `--mode text|json|rpc` output flag.
Codex receives it as `--config 'model_reasoning_effort="high"'`; Pi receives it
as `--thinking high`. `none` and `off` are mapped to the spelling used by each
agent. Codex supports `none`/`off`, `minimal`, `low`, `medium`, `high`, `xhigh`,
`max`, and `ultra`; Pi supports `off`/`none` through `max` but not `ultra`.
Unknown mode names fail before workspace or Linear mutation. When a Pi mode is
requested, the adapter uses Pi's local RPC mode in the exact worktree before task
submission to resolve the selected model, supported thinking levels, and effective
level. It rejects a model/mode pair if Pi would silently clamp it. Model/account
support is ultimately determined by the selected CLI. Login, repository trust,
and each agent's own permission settings remain under your control. Although Pi
itself accepts thinking suffixes such as `model:high`, Agentic Workflows rejects
that syntax because Pi may silently clamp it. Keep `model` plain and put every
thinking choice in workflow `mode`/`--mode` so it follows one validated path.

Per-command choices override machine-local configuration without modifying it:

```sh
task start DEV-7 --agent pi
task start DEV-7 --agent codex --model gpt-6-astra --mode high
```

`--agent`, `--model`, and `--mode` resolve independently. For example,
`--agent pi` retains the configured model and mode; it does not silently choose
Pi-specific values. Precedence is command override, then `[agent]`, then the
selected CLI's local default for an omitted model or mode. An unavailable or
unknown requested agent fails; there is no fallback to Codex.

## Codex permissions for trusted repositories

Agentic Workflows does not relax Codex permissions globally. With no repository
override, the adapter passes no profile, sandbox, approval, or network option, so
Codex's active defaults and machine configuration continue to apply. Model and
reasoning choices are independent of permissions.

A repository that genuinely needs unattended implementation can opt into one
named Codex configuration profile in the machine-local
`~/.agentic-workflows/config.toml`. The key is the exact `repo_name` from
`config/projects.toml`, not a checkout or worktree path. For this repository:

```toml
[codex.repositories."agentic-workflows"]
profile = "agentic-workflows-trusted"
```

Create the selected profile beside Codex's user config. With the usual
`CODEX_HOME`, the example above names
`~/.codex/agentic-workflows-trusted.config.toml`:

```toml
approval_policy = "never"
sandbox_mode = "workspace-write"

[sandbox_workspace_write]
network_access = true
```

This exact combination was verified with Codex CLI 0.157.1. Agentic Workflows
launches it as `codex --cd WORKTREE --profile agentic-workflows-trusted ...`;
Codex loads and enforces the profile. `workspace-write` limits writes to the task
workspace and Codex's temporary roots while keeping protected paths such as
`.git` and `.codex` read-only. Network access permits commands inside that sandbox
to use outbound networking. `approval_policy = "never"` suppresses approval
prompts; an operation outside the sandbox fails and is returned to the agent
instead of escaping the sandbox. This setup does not use `danger-full-access` or
the bypass flag. See Codex's focused documentation for
[configuration profiles](https://developers.openai.com/codex/config-basic) and
[sandbox/approval behavior](https://developers.openai.com/codex/security).

Treat the override as a trust decision: the selected profile is layered on the
machine's Codex configuration and can expose repository content to processes and
network destinations used by the task. Keep the selection and profile file local;
do not commit populated local configuration, credentials, or approval state. An
unlisted repository receives no override, even if it uses the same agent or model.

Codex enforces filesystem, network, and approval boundaries. The task handoff's
rules—no commit, push, merge, pull request, `sudo`, or destructive Git
operations—are behavioral instructions, not hard sandbox rules. Implementation
and future review commands may use the same trusted-repository profile; they stay
separate through fresh sessions, `AgentExecution.purpose`, and purpose-specific
handoffs, not through separate permission profiles.

This is deliberately a small Codex-only setup. Pi enforcement, cross-agent
capability mapping, stronger credential isolation, containers or external
sandboxing, a dedicated review profile, generalized command rules, and a broader
security framework remain out of scope.

A repository is resolved as `projects_root / repo_name`. Each must be its permanent Git checkout, already on the configured base branch, with a clean working tree (including untracked files) and no unfinished Git operation. The base must track a same-named branch on a remote. `task start` fetches that upstream into `FETCH_HEAD` and updates using `merge --ff-only`; it refuses local-only commits or divergence. It does not switch, stash, reset, or force-update branches.

The command loads the current issue through [Linear's GraphQL API](https://linear.app/developers/graphql), resolves the project, updates the base, and asks Herdr to create or focus a worktree. Herdr chooses its location. New default branches start with the lowercase issue identifier and a title slug (up to 100 characters). The identifier is the stable identity: renaming a Linear title never renames an existing branch or creates a replacement workspace.

After Git and Herdr confirm the exact checkout and focus, the command sets the
issue to its team's exact `In Progress` status (unless it is already there). It
then starts the selected adapter in the returned pane and sends the same workflow-
owned handoff: current identifier, title, exact description, resolved checkout,
branch, optional slice, and standard implementation instructions. Both agents
are told to read repository instructions, inspect before editing, implement the
requested scope, run relevant validation, and stop for independent review without
committing, pushing, merging, or opening a PR. The Python workflow owns the Linear
lookup; the execution agent is told not to contact Linear or read its credentials.
No task description file is written into the worktree.

Codex is launched with `--cd` and uses its local app-server API for a readiness
exchange, durable queue submission, and exact recorded-turn confirmation. Pi has
no working-directory flag: Herdr starts it in the already-confirmed task pane and
the adapter validates both reported working directories before submitting the full
handoff through `herdr agent prompt`. Herdr confirms prompt submission, but does
not identify a Pi turn for that prompt. Both remain interactive sessions.

To prepare/focus the workspace without an agent (also works without `[agent]` or
an agent installation):

```sh
task start DEV-7 --no-agent
```

## Agent execution architecture

Agentic Workflows is multi-agent by design. Codex is the current default and most
mature adapter, not the architecture; Pi is the second supported implementation
used to keep the boundary portable. Future agents such as OpenCode should be added
as adapters without rewriting task lifecycle logic.

The shared contract in `task_start/agent.py` is deliberately small:

- `AgentOptions` is the resolved agent, model, and workflow mode.
- `AgentExecution` carries the fresh issue snapshot, resolved repository and exact
  worktree, execution purpose, an already-constructed semantic handoff, and an
  extensible workflow-policy mapping. It does not invent purpose-specific wording.
- `LaunchResult` contains the pane/session/turn information deterministic workflow
  code may report. `AgentAdapter` is the launch boundary.

Responsibility is split as follows:

- Core owns fresh Linear retrieval, project/repository resolution, deterministic
  Git and Herdr workspace preparation, status transition, option precedence, and
  construction of the semantic implementation handoff. The task-start-specific
  builder lives in `task_start/handoff.py`; future review logic must provide its
  own review handoff rather than inheriting implementation framing.
- An adapter owns executable availability, supported values and mappings, CLI
  arguments, working-directory behavior, prompt transport/receipt mechanics, and
  agent-specific launch validation. Agent-specific capability should stay there
  instead of being forced into the shared contract.
- Machine-local configuration owns credentials, paths, the default agent/model/
  mode, repository-to-Codex-profile selections, and each CLI's authentication,
  trust, provider, and permission settings.
- Linear content owns task-specific intent and constraints. It is fetched fresh,
  transported in memory, and is never copied into tracked repository files.

New workflow commands such as review should reuse `add_agent_options`,
`resolve_agent_options`, `AgentExecution`, and the adapter registry. They should
target the shared boundary unless behavior genuinely belongs to one agent.

## Workspace lifecycle and slices

An existing workspace is reused only when Git and Herdr agree on one usable branch
and checkout. Dirty task worktrees are preserved; the permanent base checkout
must remain clean. Branch-only, locked, prunable, inconsistent, or ambiguous state
stops with an error. `task start` never deletes a branch, worktree, workspace, or
session. Scope is recorded in `agentic-workflows-scope.json` in the worktree's
private Git directory (`git rev-parse --absolute-git-dir`), outside tracked files.
The record contains only its version, issue identifier, branch and slice name
(or explicit `null` for a default workspace), never the Linear description.

The default remains one issue, one branch, one PR. For multiple implementation
slices, select a stable name explicitly:

```sh
task start DEV-13 --slice codex-handoff
task start DEV-13 --slice workspace-lifecycle
```

These create/reuse `dev-13-codex-handoff` and `dev-13-workspace-lifecycle`.
Slice names normalize accents, case, spaces and underscores to an ASCII branch
suffix; empty names, path/ref syntax, control characters and suffixes over 100
characters are rejected. The prompt names the slice and instructs the selected
agent to ask if its scope is unclear. Other slices remain untouched.

Without `--slice`, exactly one candidate can be reused, including one originally
created as a slice. The resolved workspace's recorded scope is used in the agent
prompt, so an `importer` slice retains its restriction when reopened without
`--slice`, even after a Linear title or Herdr label changes.

Legacy workspaces without scope metadata are refused until an explicit `--slice`
matching their branch suffix establishes their scope. A branch name matching the
current title is not proof of default scope. Invalid/mismatched records and
explicit selectors conflicting with recorded scope are refused; existing scope
is never overwritten. If a metadata write fails, the workspace remains intact
and neither the Linear transition nor agent startup proceeds.

Multiple candidate branches/worktrees (including live remote
branches) are listed and refused. Use `--slice` with the desired branch's suffix
after the issue ID to disambiguate a slice. For a legacy title-derived branch,
this explicitly adopts that suffix as its scope. Historical candidates are not
silently filtered out to guess an active one.

Remote branch discovery uses `git ls-remote --heads` on every configured remote.
Cached `refs/remotes/*` are never evidence of a live branch, so stale tracking refs
do not require manual pruning. A live remote-only branch stops creation for that
selection. Remote lookup failure stops safely; no fetch refspecs are added or
force-applied.

For a `github.com` base upstream, the workflow checks the
[GitHub pull request API](https://docs.github.com/en/rest/pulls/pulls#list-pull-requests)
for all PR states and pages for the selected branch in that repository. Any merged
or closed PR makes the branch historical, even after a squash merge and remote
branch deletion. This check also runs before creating a new branch, to avoid
recycling a historical name. A successful empty result or only open PRs permits
the selection. Git ancestry and `git branch --merged` are never used as a proxy.

Public repositories need no GitHub credentials within unauthenticated API limits.
Private repositories/rate limits require an existing `GH_TOKEN` or `GITHUB_TOKEN`
with pull-request read access; `gh` is not required. API/network errors or malformed
responses refuse the selection. PR history is checked in the configured base's
upstream repository; if configured remotes point to different repositories, reuse
is refused because cross-repository PR history is not supported. Hosts other
than `github.com` permit fresh workspaces but refuse existing-workspace reuse
because merge history cannot be established. Inspect PR state and retire old local
work manually, or choose a new slice; the command never cleans it up for you.

## Failure and retry behavior

Agent selection, option validation, and executable availability are checked before
Git, Herdr, or Linear mutation. If Git/Herdr preparation fails, Linear is not
updated and no agent is started. If the Linear update fails, the valid workspace
remains available and no agent is started. Check Linear and rerun.

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

Launch confirmation does not prove eventual implementation success; later model,
provider, or implementation failures remain visible in the selected agent. The
workspace and Linear's `In Progress` status remain intact on handoff failure.

The returned pane must be an available shell. An existing instance of the selected
agent anywhere in the workspace prevents a duplicate launch. Continue that session,
exit it before requesting a fresh handoff, or use `--no-agent` to focus it. After a
timeout, inspect the pane before retrying: the process or prompt may already have
started. There is no automatic resubmission, fallback agent/session, or killing of
a potentially working agent. Do not run simultaneous starts for the same repository
or edit its base checkout during a start.

## Task cleanup

After a task is completed and its branch is merged into the configured local base:

```sh
task cleanup DEV-7
```

Cleanup uses the same Linear project/repository mapping and Git/Herdr workspace
resolver as `task start`. It requires Linear's `completed` state type, regardless
of the status display name. The issue identifier, registered branch, checkout,
and recorded task scope must agree; the current title and filesystem naming are
not used to locate the worktree. Exactly one local task branch and one matching
registered Herdr worktree are required. Multiple slices are refused.

The permanent checkout must be clean and on the configured base. Cleanup checks
the local base without fetching or updating it; update it separately if needed.
If the task tip is reachable from that base, Git ancestry proves the merge.
Otherwise cleanup uses the existing GitHub PR-history lookup for the base's
upstream repository. It requires exactly one PR for the task branch, confirmed
merged into the configured base, with a head SHA identical to the local task tip.
The PR's resulting merge/squash commit must also be reachable from the local base.
The complete PR listing and individual PR response must agree. Extra local commits,
missing/ambiguous evidence, another head/base/repository, unavailable API access,
or a merge commit absent from the local base all cause refusal. Linear completion,
matching titles/messages, and patch similarity alone never prove a merge.

Squash verification supports `github.com` and uses the same optional `GH_TOKEN` or
`GITHUB_TOKEN` as the existing history checks. It reads PR evidence without changing
GitHub state. Non-ancestor cleanup on other hosts is refused.

All removal preconditions are checked before deletion and checked again just
before removal. Cleanup refuses dirty task worktrees (including untracked and
ignored files), index flags that hide modifications, unfinished Git operations,
submodules, locked/prunable/detached or mismatched worktrees, missing/invalid task
scope metadata, and any target that could remove the permanent checkout, Git
metadata, or another registered worktree. Resolve the reported condition manually.

On success, non-force Git commands remove the exact registered worktree and then
its local branch; the output names both. Ancestry-proven branches use `branch -d`.
For a verified squash/rebase PR, cleanup rechecks that no worktree uses the branch
and deletes its exact local ref with `update-ref --no-deref -d`, supplying the
verified old SHA so a changed branch cannot be deleted. It does not use `-D` or
rewrite the task branch to manufacture ancestry. Remote branches, PRs, Linear
status, and agent sessions are not changed. A repeat with no remaining local task state reports
that there is nothing to clean up. If worktree removal fails, branch deletion is
not attempted. If branch deletion fails afterward, cleanup reports the partial
result; a branch-only retry refuses and leaves that branch for manual inspection.
Run cleanup after exiting the task's agent/shell sessions, and do not modify the
repository or task worktree concurrently with cleanup.

## Tests

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
preservation on refusal, repeat runs, and partial-failure reporting.
Tests need no API key, network access, agent installation, or real Herdr workspaces.
