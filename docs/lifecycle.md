# Task lifecycle reference

[Project overview](../README.md) · [Configuration](configuration.md) · [Architecture](architecture.md)

Command behavior, invariants, and refusal/retry cases. Start with the
[README](../README.md#using-the-workflow) for the human workflow and command choices.

- [Start and status transition](#task-start)
- [Workspace reuse and slices](#workspace-lifecycle-and-slices)
- [Failure and retry behavior](#failure-and-retry-behavior)
- [Fresh review and exact resume](#task-review)
- [Cleanup and partial failures](#task-cleanup)

## Task start

A repository is resolved as `projects_root / repo_name`. Each must be its permanent Git
checkout, already on the configured base branch, with a clean working tree (including
untracked files) and no unfinished Git operation. The base must track a same-named
branch on a remote. `task start` fetches that upstream into `FETCH_HEAD` and updates
using `merge --ff-only`; it refuses local-only commits or divergence. It does not
switch, stash, reset, or force-update branches.

The command loads the current issue through
[Linear's GraphQL API](https://linear.app/developers/graphql), resolves the project,
updates the base, and asks Herdr to create or focus a worktree. Herdr chooses its location. New default
branches start with the lowercase issue identifier and a title slug (up to 100
characters). The identifier is the stable identity: renaming a Linear title never
renames an existing branch or creates a replacement workspace.

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

`task start ISSUE --no-agent` performs the same workspace preparation and Linear
transition without starting an agent. It does not require `[agent]` or an installed
agent and cannot be combined with `--agent`, `--model`, or `--mode`.

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

### Remote branches and PR history

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

See [handoff transport](architecture.md#handoff-transport) for agent-specific
readiness checks, delivery guarantees, and timeouts.

## Task review

Review the existing task checkout in a new independent reviewer pane:

```sh
task review DEV-20
task review DEV-20 --agent pi --model <provider/model> --mode high
task review DEV-20 --resume DEV-20-R2
task review DEV-20 --resume DEV-20-R2 --json
```

Configure `[reviewer]` separately from implementation; see
[agent and reviewer selection](configuration.md#agent-and-reviewer-selection).

The fresh form always allocates the next review context (`DEV-20-R1`,
`DEV-20-R2`, etc.). It splits a pane in the existing task tab, preserving focus,
and labels it with that exact context ID. It never prepares another worktree or
inherits implementation or previous reviewer conversation. Codex and Pi use the
shared execution adapters and repository permission profiles. Those permission
capabilities do not authorize task writes during review.

### Workspace resolution and pinned state

Each pass fetches the latest Linear issue and resolves the configured repository,
local base branch, exact existing task branch/worktree, scope metadata, and
context registry mappings. No title-derived branch guesses, Git fetch, base
advancement, worktree repair, Linear mutation, PR operation, or automatic routing
of findings occurs.
Review requires exactly one active implementation context mapping with a verified
pane in the open task workspace. There is no review slice selector.
The permanent checkout must pass the existing base safety checks; the task checkout
may have staged, unstaged, and untracked work. Ambiguous slices/worktrees and
missing or inconsistent mappings stop before reviewer launch.

Review pins the current local base commit. A versioned SHA-256 fingerprint covers
that commit, task HEAD/branch, semantic index entries/flags, Git status, file names,
bytes, executable bits, symlink targets, and non-ignored untracked files. Initialized
submodule state is included recursively. Conflicted/unfinished Git operations and
unsupported file types (such as non-regular untracked files) fail closed. Index
stat-cache refreshes and mtimes do not affect the fingerprint. Ignored validation
artifacts are excluded. Two matching reads establish each snapshot. Checkpoints run
immediately after launch/resume returns, at result/status polls, and immediately
before accepting the final result. Any checkpoint that observes drift permanently
invalidates that pass, even if the original bytes are later restored. Final acceptance
also requires the ending fingerprint to match the pinned starting fingerprint.
A mutation perfectly restored between checkpoints is an accepted v1 limitation;
no worktree lock or filesystem watcher is installed.

### Exact resume and provider history

`--resume` selects only the exact registered review context. It checks role, issue,
repository, checkout, server, terminal, and persisted provider history. It retains
the actual conversation and the recorded agent/model/mode. Fresh review requires
explicit model and mode (config or flags) so provider defaults cannot drift between
passes. Resume rejects selection flags; use a fresh reviewer for different settings.
Unknown, retired, uncertain, busy, mismatched, or non-resumable IDs never fall back
to fresh review. A verified missing pane can be recreated in the task's currently verified tab
with the same context ID/session. A terminal uniquely relocated to another pane
within the task workspace retains its context and session; only the verified pane/tab
location is updated. Conflicting or ambiguous runtime/session evidence fails. Tab
rearrangement within the task workspace and manual pane renaming do not select a
different reviewer. Codex uses its exact thread queue and persisted prompt receipt;
Pi requires an exact persisted session-file reference from Herdr.
Pi review launches and restarts explicitly load the bundled session-reporting
extension, so discovery does not depend on a globally installed Herdr integration.
It reports Pi's native session path; a startup path may precede persisted history.
Pi resume rechecks the saved model/thinking mode through the same capability check
as fresh launch; unsupported or clamped settings fail before the follow-up prompt.
Pi verification retains the immutable conversation ID from the persisted header in
the existing context registry session reference. Resume checks that ID and actual history
again after startup, before delivering instructions; recreating an empty session at
the same path is rejected. Session references first reported during review polling
are recorded through the same registry observer as launch. Later conflicting
identities fail; a reported path alone never establishes resumability. Explicit
empty, blank, or malformed `--resume` selectors fail before allocation.

### Completion and review permissions

Herdr may infer `idle` while Pi is still working. Idle/done without a structured
result, startup gaps, and temporarily absent/unknown observations remain pending;
elapsed startup time or an earlier working observation does not establish completion.
Acceptance requires validated output and idle/done, plus final identity/state checks.
Without conclusive output or a verified failure, the existing workflow timeout bounds
the wait, including when a stopped process is only reported as absent/unknown.

The handoff separates authoritative read-only review instructions, resolved and
pinned metadata, and latest Linear requirements. Embedded implementation-agent
instructions remain task context. Reviewers may inspect and validate, but cannot
edit task files, fix, stage, commit, push, merge, modify Linear, or change PRs.
Their only output-file write is a private temporary JSON result outside the task
checkout. The workflow waits for the same terminal/session to settle, validates a
pass-specific nonce and strict result shape, then removes the temporary directory.
No durable semantic report is stored. A blocked pass, timeout, or interruption leaves
the pane intact. Context health and resumability are determined separately from the
pass verdict using live pane/terminal/session identity and provider history. A verified
reviewer remains resumable after a pause or tab move; release the pause before submitting
its next follow-up. If the agent exits back to its shell, explicit resume restarts it in
the same mapped pane and verifies the retained conversation before sending the handoff.
Closing a pane preserves verified session evidence for later exact resume and pane recreation.
Temporary verification failures retain last-known verified resumability; every resume
still rechecks provider history. Missing, ambiguous, or replaced identity remains uncertain and cannot
be resumed implicitly. `--timeout` defaults to 1800 seconds after prompt delivery.

### Structured output and exit status

`--json` emits a version-1 `ReviewResult`, independent of human CLI formatting:

- `state`: `clean`, `findings`, `blocked`, or `failed`; `invalidated` distinguishes
  implementation-state invalidation from other blocking conditions.
- `findings`: severity (`critical`, `high`, `medium`, `low`), explanation, concrete
  file/location or other evidence, and optional requirement/test linkage text.
- `checks`: name, result (`passed`, `failed`, `not_run`), and details.
- `context_id`, `pass_id`, `pass_kind` (`fresh`/`resumed`), `execution`
  (`kind`/`model`/`mode`), pinned `review_state`, and `post_fingerprint`.
- `summary`: a human-readable explanation, never an automation verdict source.

Exit status is 0 for clean, 2 for findings, 3 for blocked/invalidated, and 1 for
failure. Missing/malformed output cannot be clean; drift overrides any model verdict.
Preflight errors stop before a pass exists and are reported on stderr. Findings,
validation limitations, and stopped/blocked agents require human action; no fix loop
or implementation resume is included.

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

### Merge evidence

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

### Removal checks and Python caches

All removal preconditions are checked before deletion and checked again just
before removal. Cleanup normally refuses dirty task worktrees (including
untracked and ignored files), index flags that hide modifications, unfinished Git
operations, submodules, locked/prunable/detached or mismatched worktrees,
missing/invalid task scope metadata, and any target that could remove the
permanent checkout, Git metadata, or another registered worktree.

The sole dirty-state exception is ignored Python bytecode directly inside
`__pycache__` directories. Cleanup first inspects the complete tracked,
untracked, and ignored state. It removes those directories only when every dirty
entry is a regular `.pyc` or `.pyo` file in such a directory and every directory
entry was present in that Git snapshot. A source change, symlink, loose bytecode,
nested/unknown cache content, or any other ignored or untracked file preserves
everything and refuses cleanup. The complete target validation runs again after
cache removal.

### Git removal, Herdr retirement, and retries

On success, non-force Git commands remove the exact registered worktree and then
its local branch; the output names both. Ancestry-proven branches use `branch -d`.
For a verified squash/rebase PR, cleanup rechecks that no worktree uses the branch
and deletes its exact local ref with `update-ref --no-deref -d`, supplying the
verified old SHA so a changed branch cannot be deleted. It does not use `-D` or
rewrite the task branch to manufacture ancestry. Remote branches, PRs, and Linear
status are not changed.

After Git cleanup, the command closes the exact open Herdr workspace identified
by the stable ID returned for the validated worktree. It confirms the ID's
repository and checkout path before closing it and confirms the ID disappeared
afterward; labels are never used as retirement identity. The exact association is
saved in Git-private metadata before removal, so a retry can finish closing a
stale workspace after the worktree and branch are already gone. Conflicting,
missing, or ambiguous identity refuses retirement. A close failure reports Git's
completed portion without claiming Herdr success and retains the retry state.
Each retry first uses a no-follow filesystem check to prove the saved checkout
path has no object, confirms no Git worktree has reused it, and revalidates the
workspace's repository key and task identity. Reuse or inconsistency preserves
both the workspace and retry record.
For a legacy cleanup that predates this retry metadata, the command checks Herdr
before reporting that nothing remains. It accepts exactly one absent linked
checkout only when Herdr's stable workspace ID, exact repository root/key, exact
issue label, and issue-prefixed checkout basename all agree. A label or path match
alone is insufficient; partial or multiple matches are left untouched.

If worktree removal fails, branch deletion and Herdr retirement are not attempted.
If branch deletion fails afterward, cleanup reports the partial result; a
branch-only retry refuses and leaves that branch for manual inspection. Run
cleanup from outside the task workspace after exiting its agent/shell sessions,
and do not modify the repository, task worktree, or Herdr workspace concurrently
with cleanup.
