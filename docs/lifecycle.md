# Task lifecycle reference

[Project overview](../README.md) · [Configuration](configuration.md) · [Architecture](architecture.md)

Command behavior, invariants, and refusal/retry cases. Start with the
[README](../README.md#using-the-workflow) for the human workflow and command choices.

- [Start and status transition](#task-start)
- [Workspace reuse and slices](#workspace-lifecycle-and-slices)
- [Failure and retry behavior](#failure-and-retry-behavior)
- [Fresh review and exact resume](#task-review)
- [Reviewed PR publishing](#task-pr)
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
No general durable semantic report is stored; only the clean acceptance and
bounded public publishing metadata described under [task pr](#task-pr) are retained.
A blocked pass, timeout, or interruption leaves
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

## Task pr

```sh
task pr DEV-20
```

Running this command authorizes commit, push, and PR creation/update without a
confirmation prompt. It prints the verified PR URL and stops. Human inspection,
approval, merge, and Linear completion remain separate. There are no commit-only
or push-only commands. A failed stage leaves its completed predecessors available
for verification and continuation on the next invocation.

### Review acceptance and public metadata

Publishing uses the same exact repository/worktree/branch/scope and open Herdr
workspace/context resolver as review. It requires the active implementation
mapping and the accepted reviewer's unchanged registry identity. The task must
contain the pinned reviewed base. Publication requires the local and live remote
base to match that review; an unpublished task can first take the rebase path
below when the remote base has advanced.

Review stores one current clean acceptance in
`agentic-workflows-publication.json` in the task worktree's private Git directory,
outside tracked files. It contains issue/repository/worktree/branch/base and
Herdr identities, review context/pass, verdict, timestamp, reviewer settings and
available session identity, the reviewed fingerprint, and bounded public
publication prose (or explicit approval of the frozen publication metadata after
rebase). It contains no full issue description, review findings, chat,
credentials, or general run archive. Failed/blocked/findings results never create
acceptance. Beginning another review explicitly revokes prior acceptance and
publishing intent, including if that new pass is interrupted. Completed rebase
provenance survives a new review only while its exact resulting Git state is
unchanged. It authorizes commit reuse, never publication without fresh clean
acceptance. Review and publish hold the same process lock, released automatically
on process exit.

The same private record keeps one branch publication fact, independently of
review acceptance and intent. It binds the issue, checkout/branch, configured
base and remote repository to an observed published SHA. A new review does not
erase this evidence; it prevents automatic rebasing even if the remote branch
and PR are later absent. This is a bounded safety record, not a run archive.

The reviewer supplies public-safe summary, description, and validation prose from
the actual reviewed implementation. This is metadata generation within the
existing read-only review handoff; it cannot select Git/GitHub identities or
perform publishing actions. After automatic rebase, the handoff instead asks the
reviewer to validate the already-frozen title/body, as described below.
`task pr` has no second model invocation and never
falls back to copying a possibly stale task title or private review checks.
Initial clean verdicts without publishing metadata require a new review.

Exactly one canonical primary Linear category label resolves the type:
`Feature` → `feat`, `Bug` → `fix`, `Chore` → `chore`, `Docs` → `docs`,
`Refactor` → `refactor`. Other labels, including `Research`, do not select a type.
Missing or conflicting categories refuse publication. Both commit subject and PR
title use `<type>: <concise actual change> (DEV-20)`. The body contains `Summary`,
`Tracking`, and `Validation`, with a stable linked Linear issue reference in
`Tracking`. Only approved public prose is copied into it; private reviewer
findings and task instructions are not used as a fallback. Public-safety judgment
is supplied by the reviewer, not a deterministic secret-content classifier.

### Mapping the reviewed state to a commit

Before any commit, the full current fingerprint must equal the acceptance. This
includes the exact original HEAD, index, flags, file contents/modes and untracked
inventory. Publication includes final materialized tracked files and non-ignored
new files, with deletions, executable modes and symlinks. For partially staged
files, final working-copy content wins; staged-only intermediate versions are not
separate changes to publish. All intended files must be present during review.
Ignored untracked files remain excluded. Submodules and skip-worktree or
assume-unchanged entries are refused in v1.

A temporary index prepares Git's resulting tree. The tree's actual blob bytes,
paths and modes must exactly represent the reviewed materialized files. Content-
transforming clean filters (including LFS and line-ending conversion), ignored
remnants of staged deletions, and modes that Git ignores are refused in v1. The
real index stays untouched during preparation. The workflow atomically saves
and fsyncs an intent containing the accepted pass, repository/remote identity,
expected tree/index, title/body and exact commit message before invoking commit.
The message includes a `Task-Review` pass-ID trailer. A separate materialized
content digest, excluding absent deleted paths, connects pre-commit bytes/modes to
post-commit bytes/modes. The resulting commit must have exactly the reviewed HEAD
as its sole parent, the intended tree, and the intended message. The real index
is then advanced only from its accepted entries to that proven tree. As soon as
the workflow creates or selects a proven publication commit, it durably freezes
that exact SHA in the intent before any further transport. All later checks,
including retries, require local HEAD to equal this SHA; equivalent parent,
tree and message are insufficient.

An already committed reviewed change receives one publication commit (possibly
an empty tree delta) to establish the generated subject and pass provenance. The exception is
a proven rebased publishing commit: fresh review authorizes reuse of that exact
commit. Its original trailer remains provenance; the new durable acceptance
authorizes publication. A tree identical to the base is refused.

The workflow rechecks content, exact commit SHA, and index before push,
immediately after push (including failures), before PR writes, and before
reporting the URL. Hooks retain their normal behavior, but a hook that changes
the message/tree/content causes refusal before push. A pre-push hook that amends
HEAD also causes refusal, even if only author or committer metadata changes:
the remote may already contain the original frozen SHA while local HEAD differs.
The command reports both SHAs and stops before creating or reporting a PR; retries
continue to refuse the changed HEAD. Inspect local/remote history and the hook
before deciding how to recover; the workflow never resets or force-pushes it.
The lock serializes workflow commands, not editors or unrelated Git processes.
Keep the checkout stable throughout publication; changes perfectly restored
between observations cannot be detected.

### Base advancement before first publication

If the live base differs from the accepted base, any remote task branch or PR
history prevents automatic rebasing, even if its SHA otherwise matches. Durable
publication evidence also forbids rebasing after the remote ref is deleted;
an uncertain prior push cannot be treated as proof of no publication. For an
unpublished task, `pr` fetches only the configured base into `FETCH_HEAD`, using
the verified task-worktree remote and native terminal authentication. Both the
reviewed base and permanent checkout must be ancestors of the fetched base.
The clean permanent checkout advances with a guarded fast-forward; divergent or
local-only base history refuses the operation.

Integration runs in a disposable repository sharing only Git objects, with its
own refs, index and files. It replays linear implementation commits followed by
the intended publication commit onto the new base. Probe hooks, signing and
rerere are disabled; the probe never touches the real task branch/index/files.
Checkout preparation also uses a private index: it loads the reviewed source
tree, refreshes file metadata, and dry-runs the two-tree checkout against the
actual files. Refreshing is necessary when upstream changes an existing tracked
file; loading a tree alone does not establish that file's current stat data.
Setup errors, checkout preflight failures, or rebase conflicts stop with the
exact reviewed task index, files and HEAD unchanged. The permanent
base may already have advanced. Task histories containing merge commits require
manual rebasing in v1. Ignored files that would obstruct upstream paths also
cause refusal; move them aside before retrying.

After a clean probe, the workflow saves a bounded rebase journal and revokes the
old acceptance before installing anything. The journal records source state,
target base, expected commit trees/messages/authors, created commit IDs, the
completed fingerprint, and the frozen publication title/body with their metadata
fingerprint. This is one current continuation record, not a run
archive. Real replacement commits preserve implementation authors and honor the
task worktree's normal signing settings. No hooks can modify this precomputed
chain. Before installation, remote refs/PR history and permanent/task identities
are checked again. Checkout preparation is repeated in a private index, then the
real Git index is locked and task state rechecked. Checkout updates files and the
private index; only a successful checkout allows atomic replacement of the real
index. The reviewed source tree is never staged into the real index as a setup
step. A compare-and-swap branch update completes installation without running a
rebase sequencer in the real task worktree.

The command then stops with an explicit requirement for a new independent
`task review`. It cannot push or create a PR in that invocation. A pending
installation must finish through `task pr` before review; retries verify saved
commit IDs and the allowed source/target checkout states. If files reached the
planned target but index replacement was interrupted, the original index and
HEAD must still match the source; retry installs the proven target index and
reuses the same commits. Partial file updates or other drift that do not match a
proven state require manual inspection.

The fresh review explicitly receives that exact title/body and their fingerprint.
It must validate their accuracy against the rebased result, including the linked
issue, validation claims and public safety. A clean verdict must include a
`publication_approval` matching that metadata fingerprint. Missing or mismatched
approval cannot create clean acceptance; inaccurate or unsafe metadata calls for
findings or a blocked review. Metadata drift observed during the pass invalidates
it just as Git-visible drift does.

The durable acceptance binds this approval to the new pass and reviewed Git
fingerprint. `pr` checks it alongside the rebased commit provenance and reuses the
frozen title/body on every continuation, including after commit/push/PR failures.
It never regenerates them from the fresh reviewer's summary. The reviewer's own
conclusion or optional replacement publication prose may use different wording;
neither changes the frozen metadata. Older rebase records that did not retain the
title/body cannot be reconstructed safely and require manual inspection.

### Remote, PR and retry checks

The configured base must track a same-named branch on one GitHub remote. Fetch
and push destinations must each resolve to a single URL for the same repository;
conflicting repositories or multiple destinations are refused. The effective
remote is resolved with the task worktree's Git configuration, including worktree
settings and conditional includes, and must match the permanent checkout's
approved remote/repository identity. It is rechecked before each ref lookup,
fetch and push. Transport uses that remote's name so Git expands URL rewrites
once; an already-expanded URL is never passed back through Git's rewrite rules. Mirror
remotes and remote groups are refused. Push uses an explicit commit SHA and task
branch refspec, without force, tags or submodule pushes. Live `ls-remote` refs are
authoritative; cached tracking refs and task branch push defaults are not used.

Before the first push attempt, a pending marker is saved and fsynced. Observing
the remote task ref records positive publication evidence; a verified successful
push must save that evidence before continuing to GitHub. The first positive
record is retained permanently for that branch's workflow state, including
across fresh/failed reviews. Later commits or pushes do not downgrade it. If the
push acknowledgement or confirmation write fails, the pending marker survives:
retry can verify or push the same commit, but cannot automatically rebase after
the remote ref disappears. Failed evidence writes stop publication. Legacy
records with an existing intent but no push history are treated as uncertain,
rather than assumed never published.

Before pushing, the task branch may be absent, at the accepted pre-publication
HEAD, or already at the proven publication commit. Anything else conflicts.
After push it must equal the publication commit. All PR history for this exact
repository/head participates: there must be no PR, or exactly one open, unmerged
PR with the configured base and expected head SHA. A fork, another base, closed
or merged history, multiple PRs, or changing responses refuse publication. The
matching PR is reused and its title/body updated if necessary. The confirmed PR
and remote SHA are checked before reporting its URL. Immediately before every
PR POST/PATCH, after the preceding GitHub lookups, the workflow rechecks the
effective destination and authoritative remote base/head SHAs against the frozen
publishing state. Drift refuses the write. Local HEAD and reviewed contents are
checked again after that native ref lookup. These checks cannot lock GitHub
against unrelated changes between the final observation and the API write.
GitHub owner/repository
casing is equivalent, including in the reported PR URL. URL scheme, host, path,
PR number, head/base branch names and head SHA remain strictly validated.

| Interruption | Next `task pr` invocation |
| --- | --- |
| Before commit | Recheck acceptance and saved intent, then commit once. |
| Commit completed, including a lost command acknowledgement | Require the frozen SHA if present; otherwise prove parent/tree/message/content and freeze the recovered SHA. Finish the index update if needed and reuse that commit. |
| Push failed or its acknowledgement was lost | Inspect live refs; push only if the exact commit is absent. |
| Published ref was deleted, or an uncertain push can no longer be observed | Retain publication evidence and forbid automatic rebase; inspect history manually. |
| PR creation failed or its acknowledgement was lost | Enumerate all matching history first; reuse the unique matching PR or create if absent. |
| Metadata update/reporting failed | Revalidate and reconcile the same PR, then report its URL. |
| Unpublished base advanced | Probe integration, install only a conflict-free rebase, invalidate review and stop for a fresh independent pass. |
| Rebase installation interrupted | Verify and reuse the saved replacement commits, finish only a provable checkout/ref transition, then require review. |
| Fresh review after rebase | Require explicit approval of the frozen title/body for the reviewed state; reuse the exact rebased commit and metadata through push/PR. |
| Files/index/history/identity conflicts, or base change after publication | Stop without force or repair; inspect the reported state. An explicit new review establishes a new contract after intentional changes. |

Do not delete or edit acceptance/intent records to bypass refusal. Rerunning `pr`
continues a frozen contract, while explicitly running `review` starts a new one.
Neither a fresh review nor deletion of a remote ref clears publication history.

### Authentication and signing

The REST API uses `GH_TOKEN` or `GITHUB_TOKEN`; see
[GitHub publishing configuration](configuration.md#github-publishing).
Commit/push/fetch inherit stdin, stdout and stderr. Ref lookup and rebased commit
creation read only protocol stdout (refs or an object ID) and inherit stdin/stderr.
Git's SSH/GPG/credential tools retain the caller's console and controlling
terminal; the workflow never reads passphrase
input, captures prompts, or writes secrets into artifacts.
Native failures and a five-minute operation timeout produce an actionable error
without echoing captured command output. Normal Git signing and authentication
configuration stays in effect. Non-interactive callers must provide already
usable credentials/signing access or handle a native failure; the workflow never
supplies credentials to a prompt or weakens authentication.

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
