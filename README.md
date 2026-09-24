# Agentic Workflows

Reusable tooling, skills, and automation for efficient agentic software development workflows.

The goal is to reduce repetitive development mechanics and make working with coding agents across multiple projects fast, consistent, and predictable.

## Setup and use

Requires Python 3.12, Git, and Herdr on `PATH`, with a running Herdr session.
Agent handoff also requires an installed, authenticated Codex CLI. The commands and
JSON responses were checked against Herdr 0.9.1 and Codex CLI 0.156.1.

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
reasoning = "high"
```

Model names are passed unchanged to Codex's `--model`; use the actual model ID
accepted by your CLI/account. There is no hard-coded model whitelist or workflow
alias expansion. Reasoning is passed as `--config 'model_reasoning_effort="high"'`.
The workflow validates the configuration's shape and reasoning values; support
for a particular model/effort combination and account access is determined by Codex.
See the [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).
Only `kind = "codex"` is currently supported. Login, repository trust, and Codex's
own permission settings remain under your control.

A repository is resolved as `projects_root / repo_name`. Each must be its permanent Git checkout, already on the configured base branch, with a clean working tree (including untracked files) and no unfinished Git operation. The base must track a same-named branch on a remote. `task start` fetches that upstream into `FETCH_HEAD` and updates using `merge --ff-only`; it refuses local-only commits or divergence. It does not switch, stash, reset, or force-update branches.

The command loads the current issue through [Linear's GraphQL API](https://linear.app/developers/graphql), resolves the project, updates the base, and asks Herdr to create or focus a worktree. Herdr chooses its location. New default branches start with the lowercase issue identifier and a title slug (up to 100 characters). The identifier is the stable identity: renaming a Linear title never renames an existing branch or creates a replacement workspace.

After Git and Herdr confirm the exact checkout and focus, the command sets the
issue to its team's exact `In Progress` status (unless it is already there). It
then starts Codex in the returned pane, with `--cd` set to the checkout. After a
harmless readiness exchange identifies that session, it queues the current
identifier, title, and exact description with standard instructions. It confirms the queued message became a
recorded Codex turn before reporting handoff success. Codex is told to read repository instructions,
inspect before editing, implement the requested scope, run relevant validation,
and stop for independent review without committing, pushing, merging, or opening
a PR. The Python workflow owns the Linear lookup; Codex is told not to contact
Linear or read its credentials. No task description file is written into the
worktree. The prompt is delivered over local stdio and remains visible in Codex's
session history.

To prepare/focus the workspace without Codex (also works without `[agent]` or a
Codex installation):

```sh
task start DEV-7 --no-agent
```

## Workspace lifecycle and slices

An existing workspace is reused only when Git and Herdr agree on one usable branch
and checkout. Dirty task worktrees are preserved; the permanent base checkout
must remain clean. Branch-only, locked, prunable, inconsistent, or ambiguous state
stops with an error. No branch, worktree, workspace, or session is deleted by this
workflow. Scope is recorded in `agentic-workflows-scope.json` in the worktree's
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
characters are rejected. The prompt names the slice and instructs Codex to ask if
its scope is unclear. Other slices remain untouched.

Without `--slice`, exactly one candidate can be reused, including one originally
created as a slice. The resolved workspace's recorded scope is used in the Codex
prompt, so an `importer` slice retains its restriction when reopened without
`--slice`, even after a Linear title or Herdr label changes.

Legacy workspaces without scope metadata are refused until an explicit `--slice`
matching their branch suffix establishes their scope. A branch name matching the
current title is not proof of default scope. Invalid/mismatched records and
explicit selectors conflicting with recorded scope are refused; existing scope
is never overwritten. If a metadata write fails, the workspace remains intact
and neither the Linear transition nor Codex startup proceeds.

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

If Git/Herdr preparation fails, Linear is not updated and Codex is not started.
If the Linear update fails, the valid workspace remains available and Codex is not
started. Check Linear and rerun.

Codex starts with a short native prompt containing a random readiness marker. It
asks only for `READY`, without tools or edits. The local stdio app-server API's
`thread/list` identifies the session by that exact marker and checkout;
`thread/read` and `thread/items/list` confirm the readiness turn. The workflow
never selects the latest session or infers its identity from a title.

`thread/queue/add` then delivers the unmodified task to that terminal-owned session
with a unique message ID. History polling must find the same session, checkout,
message ID, exact text, and a turn ID before success. The helper never creates,
resumes, or executes a model session and exits after confirmation. No shared daemon
or service is installed. These APIs (including the experimental queue endpoint) were validated
with Codex CLI 0.156.1; unsupported or malformed responses fail clearly.

Herdr's `interactive_ready` and `working` states alone do not prove receipt:
startup/trust dialogs can consume terminal input, and `agent prompt --wait` does
not track individual turns. Task text is therefore never pasted into the terminal.
Receipt polling has a 30-second deadline and never resends input. A blocked
startup, delivery failure, or timeout is an error, with pane and session IDs for
inspection. First-time repository trust or authentication may require action in
Codex; the workflow never approves those dialogs. A queued task may start after
you resolve a blocker, so inspect that session before retrying.

This confirms the handoff, not eventual implementation success; later model/API or
implementation failures remain visible in Codex. The workspace and Linear's
`In Progress` status remain intact on handoff failure.

The returned pane must be an available shell. An existing Codex anywhere in the
selected workspace prevents another launch. Continue that session, exit it before
requesting a fresh handoff, or use `--no-agent` to focus it. After a timeout, inspect
the pane before retrying: the process or prompt may already have started. There
is no automatic resubmission, fallback session, or killing of a potentially working
agent. Do not run simultaneous starts for the same repository or edit its base
checkout during a start.

## Tests

```sh
python3.12 -m unittest discover -s tests -v
```

Tests mock Linear, GitHub, Herdr and Codex boundaries and exercise Git safety and
authoritative remote lookup in temporary repositories with a local fake remote.
They cover handoff ordering/failures, exact context, mutable titles, ambiguity,
slices, squash-merge history and stale tracking refs. They need no API key,
network access, or real Herdr workspaces.
