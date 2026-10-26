# Managed Linear workflows on Linux

AW retains a Linear task, prepares a Herdr workspace, and runs a Codex Implementer and an independent Reviewer. A Clean Review Report leaves the change available for inspection. Explicit `awctl pr` submits that exact change as one commit and the first non-draft GitHub PR. AW does not merge it or mark Linear Done. Use the [source installation guide](source-installation.md) to build, set up, and explicitly start the background control plane; closing `awctl` does not stop accepted work.

## One-time setup

Install the .NET 10 SDK, Git, the official Herdr CLI, and Codex for the same Linux account that runs the control plane. Prepare and authenticate Codex through Herdr first. Start the intended local Herdr session yourself. AW validates an already-running `local:SESSION` target and does not start a missing session. The adapter checks the installed structured command schema and running protocol; a version string alone does not establish compatibility. A harness-specific Herdr hook is not required for a fresh pass or result delivery.

Use the canonical repository checkout, with a credential-free `origin` URL for its GitHub repository. Copy the [safe Project Configuration example](../../examples/project.toml) to `.agentic-workflows.toml` in that checkout. Ensure the active file is ignored by Git. Managed worktrees find this original file through their durable Project registration.

Replace the example paths and identities. The Linear Project and team identifiers and numeric GitHub repository ID must be stable provider IDs, not display names. `managed_root` must be an absolute path accepted by Herdr's managed-worktree policy. For submission, `base` must identify an existing GitHub branch, such as `main`, `refs/heads/main`, or `origin/main`; its resolved starting commit and branch are retained. The first Whole Task branch is exactly `aw/TASK-ID/whole`; no title-derived suffix or random collision workaround is used.

Inject the variables named by `linear_credential` and `github_credential` into the control-plane process using your existing credential tooling. Do not put their values in TOML, command arguments, task descriptions, or reports. AW accepts only `env:VARIABLE` references and resolves their current values when accessing the provider. Restart the process after replacing environment-injected values. AW provides no secret vault. Codex authentication remains owned by its tooling.

Git must also have noninteractive read access to `origin` through its normal credential helper or SSH authentication. The GitHub credential reference authenticates API identity checks; AW never inserts that token into a Git URL or command argument. Admission verifies live remote branch collisions, and ordinary cleanup uses live remote evidence when local work is not already recoverable from the selected base.

Start the control plane using the [source installation guide](source-installation.md). From the canonical checkout, run:

```sh
awctl setup
```

Setup validates the canonical Git repository, one Linear Project and team, the concrete active-work state, the GitHub target, and local Herdr capabilities. If `in_progress_state_id` is omitted, setup accepts exactly one `started` state and reports its ID; save that ID in the Project Configuration before starting work. Ambiguous mappings require an explicit configured ID. Setup does not start a Workflow or change Linear state.

The optional Global Configuration is `$XDG_CONFIG_HOME/agentic-workflows/global.toml`, defaulting to `~/.config/agentic-workflows/global.toml`. It accepts only a `[defaults]` table with the fields shown in the example. Both files reject unknown fields and invalid scalar types. Model defaults are independent by role. `default` leaves model choice to Codex. Reasoning accepts `minimal`, `low`, `medium`, `high`, or `xhigh`; session policy accepts `new` or `reuse`; missing-section policy accepts `warn`, `require`, or `ignore`; pass budget is a quoted positive integer or `"unlimited"`.

For supported choices, invocation/next-pass selection wins over retained Workflow role selection, then Project defaults, Global defaults, and built-in defaults. An invalid selected value fails rather than falling back. Built-ins use the harness default model, medium reasoning, session reuse, a warning when Agent instructions is absent, and six total implementation/review passes. Identity bindings are outside this precedence: configuration changes select targets for new Workflows and do not redirect retained work.

## Start and inspect

```sh
awctl start DEV-123
awctl workflow WORKFLOW-ID
awctl history WORKFLOW-ID
awctl snapshot WORKFLOW-ID
awctl report WORKFLOW-ID --pass PASS-ID
awctl handoff WORKFLOW-ID --pass PASS-ID
awctl warnings WORKFLOW-ID
awctl attention WORKFLOW-ID
awctl workflows
```

The accepted response includes the immutable Workflow ID and Operation ID. Keep the Workflow ID. Outside its proven managed worktree, all continuation commands require that ID; a Linear task identifier or branch convention cannot select retained work. Inside the bound worktree, the ID may be omitted. Conflicting metadata stops the command.

`awctl status` retains its control-plane health meaning. `workflow` shows phase, Operation status, execution observation, snapshot, branch, worktree, budget, warnings, attention, recovery, review validity, and derived submission eligibility. `history` returns ordered Workflow-local events; pass events identify the detailed report. Each report includes the pass configuration, timing, immutable review authority decision, and complete accepted envelope when available. Earlier invalidated reports remain available after a later clean review. Queries are bounded: use `--after` with the returned history position and `--offset` with the returned report/list offset. Snapshot, Handoff, and result pages retain their complete text across pages. Token and cost measurements are reported as unavailable when the runtime supplies none.

Read-only admission rejects terminal Linear tasks, invalid state mapping, duplicate retained Whole Task Workflows, unsafe branches, and local/remote/workspace collisions. It never reuses a worktree silently. AW records the snapshot and workspace intent before Herdr creation, verifies returned identities, moves Linear to In Progress, and then launches the Implementer. A failed Linear transition leaves the workspace retained without launching an agent. Later launch failure does not roll Linear back.

When a recognized Markdown heading or Linear expandable section named `Agent instructions` exists outside fenced code, its exact contents determine authoritative requirements and source changes. Otherwise the complete description is authoritative under `warn`, `require`, or `ignore`. The complete description, title, identifier, revision, and capture time remain in the immutable snapshot. Every pass records its exact snapshot and Handoff.

Every Implementer and Reviewer Handoff forbids the managed agent from connecting to live Linear or GitHub, reading operator credentials or local workflow configuration, or creating, modifying, or deleting real external-service resources. These lifecycle instructions take precedence over task text, including requests for live validation. Passes work from the captured snapshot and use controlled substitutes for provider validation. The AW control plane itself still uses configured provider access for identity checks, source observations, and the startup Linear transition described above.

Deterministic controlled validation is the blocking gate. Live integration validation is deferred and unverified, and belongs to the separately scoped [future opt-in E2E kit](../development/managed-workflow-smoke-test.md). Routine managed passes report that limitation; they do not execute the live smoke procedure.

The first Reviewer always has a separate fresh session. Review captures the baseline and proposed Git trees using a private temporary index and verifies HEAD, index, content, and modes again afterward. Unmanaged changes invalidate review authority. A Clean Review Report makes the exact unchanged tree eligible for explicit submission; no publication action follows automatically. Failed or unrun validation remains visible and is never relabeled as passing. Supported unrelated failures may proceed to independent review with a warning.

After Clean, status also compares current materialized bytes and modes with the accepted review checkpoint. A change hidden by Git normalization or a clean filter makes current review and submission eligibility invalid while preserving the historical report. Pure staging changes and a commit of the identical reviewed tree preserve eligibility.

## Submit the reviewed change

```sh
awctl pr WORKFLOW-ID
awctl workflow WORKFLOW-ID
awctl report WORKFLOW-ID
awctl history WORKFLOW-ID
```

The ID is optional only inside the proven Workflow worktree. The command returns a correlated Operation ID after durable acceptance; inspect `workflow` for completion or failure. Submission continues in the control plane if the CLI disconnects. The `submission` projection exposes the frozen intent, expected and verified commit, pushed revision, PR identity/link, force disclosure, and current stage. `report` without `--pass` pages the frozen submission metadata and recovery details; `report --pass PASS-ID` still returns the independent pass evidence.

The service account needs a configured public Git author/committer name and email, Git transport permission to create the bound task branch, and GitHub API access to read the repository/PRs and create a PR. Git transport authentication (SSH agent/key or a credential helper for credential-free HTTPS) is separate from the configured GitHub API credential reference. The origin fetch and push URLs must both equal the Workflow's bound repository. URL rewriting, shallow/grafted history, and ambiguous targets are refused. Git hooks are not a submission validation engine. No raw credentials belong in TOML, URLs, SQLite, reports, or public metadata.

Exactly one Linear label selects the public type: Feature → `feat`, Bug → `fix`, Chore → `chore`, Docs → `docs`, or Refactor → `refactor`. Research and unrelated labels do not select a type. Missing or ambiguous categories stop before publication; AW never relabels the task. The Reviewer supplies public-safe summary, description and validation text. Titles use `<type>: <reviewed action summary> (<TASK-ID>)` within 72 characters; commits include `Task-Review: <pass-id>`. PRs have Summary, Tracking and Validation sections, the canonical Linear link, and `Fixes TASK-ID`. A clean review that accepts an unrelated failed or deferred check permits ordinary submission, with that limitation disclosed. Invalid or stale metadata never falls back to the task title.

AW freezes one PR Intent before creating a commit, replacing a branch ref, pushing, or writing to GitHub. It commits only the frozen tree and checks the tree again immediately before advancing the branch. Staging changes alone do not invalidate review. An existing local commit is eligible only when it is clean checked-out Workflow HEAD, has exactly the expected base as its sole parent, contains the reviewed tree, and is proven unpublished with no descendant ref. An exact complete message is adopted unchanged. Otherwise AW may replace only that one unpublished commit with identical tree and parent and deterministic metadata, preserving the index/worktree and auditing the original SHA. Merges, multiple commits, pushed history, unexpected parents, wrong branches and races are refused.

Unpublished-history verification includes all advertised remote refs, including retained GitHub PR refs after a source branch is deleted. The final local branch update verifies the direct Workflow ref while Git holds its transaction lock; a competing symbolic ref is preserved without advancing its target. An unfinished merge, cherry-pick, revert, rebase, sequencer operation or bisect also stops submission, including retries and `--force`. Finish or abort that operation manually before rerunning `awctl pr`; AW preserves its Git state and does not finish or abort it for you.

An advanced base must descend from the frozen base. Non-overlapping changes can be verified with a private index. Overlapping paths are conservatively refused for manual reconciliation, including cases that might admit a textual merge; AW never merges or rebases during submission. An unexpected remote task revision is also refused with expected/observed IDs. `--force` does not bypass either check.

```sh
awctl pr WORKFLOW-ID --force
```

Force is the complete invocation-scoped confirmation to bypass current clean-review/freshness policy. It is available from Implementation, Review, or Submission after preparation, with no active or uncertain Managed Agent Pass. The exclusive mutation gate and all other attention, content, history, credential, repository and remote protections still apply. Without current usable reviewed metadata, the title is `<type>: complete requested change (<TASK-ID>)`, the trailer is `Task-Review: bypassed-by-force`, and the PR explicitly discloses bypassed independent review. Intent storage and advancing to Submission are one transaction; refused preflight leaves the phase unchanged. A retry that needs force must repeat the flag; there is no saved force setting.

To continue a stopped submission, rerun `awctl pr WORKFLOW-ID` (or explicitly include `--force` for a forced intent). Each accepted retry has a new Operation linked to the earlier attempt and reuses the frozen strings and content. AW verifies an existing exact commit, pushed revision and intended PR instead of duplicating them. It never publishes newer worktree content. Preserve later edits separately and restore the frozen content when recovering drift. Fetch and reconcile unexpected remote work manually, then rerun the command; no database repair or mark-resolved command is needed.

When a PR write might have succeeded but an exact PR cannot be found, AW stops without resending it. Restore provider visibility and inspect `report` for the frozen title/body and intent marker. If manual provider reconciliation is necessary, it must establish that exact head/base/revision and metadata; a similar branch or URL is insufficient. A positively rejected create can be retried after access is restored. A PR whose identity or public metadata changed is preserved and reported for reconciliation, not automatically updated.

A completed command verifies/reuses its first PR on repetition. Another reviewed batch, existing-PR updates, drafts, `awctl integrate`, merge, Linear Done, and automatic cleanup are outside this release. Worktrees, passes, reports and failure evidence remain available after success or refusal. Use explicit cleanup only when those retained resources are no longer needed. The live Linear-to-PR smoke test remains unverified; isolated regression results are not live milestone acceptance.

## Continue and recover

```sh
awctl resume WORKFLOW-ID
awctl review WORKFLOW-ID
awctl interrupt WORKFLOW-ID
awctl abandon WORKFLOW-ID
awctl continue WORKFLOW-ID
awctl continue WORKFLOW-ID --max-passes 4
awctl continue WORKFLOW-ID --max-passes unlimited
```

`resume` is the general continuation command after startup failure, failed/invalid result, interruption, or abandonment. It revalidates retained facts and creates a new Operation linked to its predecessor only when continuation is unambiguous. It does not rewrite a failed Operation. `review` requests independent review of the current target. `interrupt` requests interruption of the exact owned execution and requires a confirming observation. `abandon` stops automatic routing while preserving current work and accepting the running pass's eventual result.

The budget is checked between passes. `continue` grants another configured batch; its `--max-passes` override applies to that continuation and does not rewrite settings. A pass that was already launched may finish.

At a changed authoritative source revision:

```sh
awctl resume WORKFLOW-ID --source retain
awctl resume WORKFLOW-ID --source adopt
awctl resume WORKFLOW-ID --source stop
```

Retain acknowledges that exact observed revision. Adopt creates another immutable snapshot and returns to Implementation. Stop retains the Workflow without another Handoff. Later Linear cancellation similarly accepts `--cancellation continue` or `--cancellation stop`; AW does not reopen the task. A later Done state is a warning and does not pause routing or cause a status rewrite.

Each retain decision appends a `TaskSnapshotRetained` history event with the examined source revision, authoritative-requirements fingerprint, and retained snapshot ID, atomically with the current-state change. Later observations and retain decisions do not overwrite this evidence; it remains available after restart, including when another Attention Requirement still prevents continuation.

For a material decision, record the accepted resolution with `awctl resume WORKFLOW-ID --decision 'accepted resolution'`. The response is retained in history and later Handoffs. This is explicit authorization, not an automatic acceptance of agent prose.

Role selections apply independently:

```sh
awctl select WORKFLOW-ID --role implementer --model MODEL
awctl select WORKFLOW-ID --role reviewer --model clear
awctl select WORKFLOW-ID --role implementer --session new
awctl resume WORKFLOW-ID
```

Model selection persists until changed or cleared. A session override is consumed by the next selected pass. It may be recorded while another pass runs. Repairs normally reuse the original eligible Implementer, and re-review may reuse the retained Reviewer. The CLI adapter requires matching pane/terminal/workspace identities and an observed native session reference before reuse. If Herdr cannot expose that reference, AW preserves the session and asks for the existing user choice; select `new` explicitly to proceed. Fresh execution and result delivery still need no hook. AW never guesses that a replacement Codex process in the same pane is the original conversation.

The native conversation is checked again immediately before a reused prompt. Model and reasoning must match the session's recorded launch configuration; changing either requires explicitly selecting a new session when the retained process cannot apply it. Each session receives a stable writable delivery root outside the worktree, containing a separate directory for each Operation. Repair and re-review can therefore deliver new results with the original sandbox grant.

An unreachable target retains the last verified execution observation. It does not manufacture failure or interruption. A possibly active Implementer prevents another mutating pass. An ambiguous launch is never resent. Restore the target and run `resume`; if correlation cannot be proven, inspect the Recovery Plan and preserve or explicitly discard the Workflow. Do not edit SQLite.

If a restart finds a complete Handoff whose launch was never attempted, it records `launch_not_attempted` and waits for `resume`. Resume revalidates the prepared session and creates a successor Operation with a new correlated result location. No pass budget is consumed by the unattempted launch. Resuming an abandoned, proven running pass restores automatic routing after that pass finishes, without starting a competing pass.

## Retention and cleanup

```sh
awctl cleanup WORKFLOW-ID
awctl cleanup WORKFLOW-ID --force
```

Ordinary cleanup preflights execution, attention, workspace identity, uncommitted changes, and local-only commits. A refusal preserves the Workflow and proposes the exact forced command. Forced cleanup is invocation-scoped. It attempts only the proven owned Herdr resources and exact local branch, then removes AW-owned Workflow records. Unproven resources remain untouched. Unconfirmed teardown or branch deletion returns `cleanup_partial` and exit code 3, with exact last-known targets for manual inspection; no Workflow retry remains after forced state removal. Cleanup never changes remote Git, GitHub, or Linear.

Ordinary cleanup rereads retained session and foreground-process facts even after a Clean review. A reactivated or uncertain execution prevents teardown. During forced cleanup, a positively absent owned worktree permits bounded attempts on the remaining verified resources and produces a partial report; a replaced repository, contradictory path, or unproven ownership still prevents deletion.

## Database recovery

SQLite is at `$XDG_DATA_HOME/agentic-workflows/workflows.db`, defaulting to `~/.local/share/agentic-workflows/workflows.db`. AW keeps current state and append-only events transactionally, with Workflow-owned records removed together only on explicit cleanup. The temporary result delivery is outside the worktree and is retired only after durable ingestion. A surviving delivery after restart does not duplicate history.

The installed build accepts exactly one integer database schema version, matched against SQLite's `PRAGMA user_version`. Integrity, foreign keys, and expected AW tables, indexes, and triggers must also pass; changing the number cannot convert an old database. Incompatibility or detected corruption blocks ordinary workflow commands before reconciliation. `awctl status`, `awctl service status`, and `awctl database status` distinguish the running process/reachable interface from database and Workflow readiness, with the active path, supported schema, observed active schema (null when unreadable), and recovery guidance. These inspections report an observed incompatible schema immediately with both versions and compatible-build/reset guidance; no failed Workflow command is needed first. Explicit maintenance restrictions retain their continuation guidance. An active-store verification failure detected by a command or query is reflected in both readiness flags and its diagnostic, even when its schema number still matches. A later successful active-store admission clears a transient access/locking diagnostic; inspecting the schema number alone does not. Provider failures and current-backup failures are distinguished from active-store failures. A backup diagnostic identifies that backup's path separately from the active schema. Clean retained Workflows before a breaking upgrade whenever possible.

A hot SQLite rollback journal after process death is a recoverable interrupted transaction. Startup first checks native recovery on an isolated copy to establish committed-schema compatibility and integrity, then permits SQLite to recover the unchanged retained store. An incompatible store and its companions remain untouched. Access and locking failures have separate diagnostics and require correcting permissions or releasing the lock, not resetting or restoring the database. Actual corruption still requires explicit restore.

This build uses integer database schema 3, including immutable Workflow-owned PR intents and submission progress. Older stores require the explicit archive-and-reset path below; missing evidence is never inferred or silently migrated. The Result Envelope remains version 1; the Local Control Interface is version 3, independently versioned for the readiness projections. Upgrade CLI and service together, and restart explicitly. An old database is preserved until an operator chooses reset or a compatible restore.

```sh
awctl database status
awctl database reset --force
awctl database restore --backup /absolute/path/to/verified-daily-backup.db
```

If an ordinary compatible runtime is already active, stop the host and restart it with `AW_DATABASE_MAINTENANCE=1` before reset or restore. An admission failure also leaves maintenance commands available. Reset archives the complete old database, `-wal`, `-shm`, and `-journal` companions, and the entire `workflows.db.daily` directory into `workflows.db.archive-TIMESTAMP-ID/`. The archive preserves the original basenames, plus a small maintenance receipt. Reset creates a compatible empty active store and a fresh current daily backup, including when the old set contained today's backup.

Restore checks the selected file's actual schema, integrity, foreign keys, and AW structure, then stages a consistent copy before archiving the current complete set and installing the replacement with a fresh daily set. Selecting a file inside the current daily set works because staging precedes archiving. An incompatible selection is refused and preserved with both schema versions and compatible-build guidance. Dates/names never override metadata. To use an older historical backup, run a build supporting its schema or select an actually compatible file; AW never converts it or rewrites `user_version`. Restart without maintenance mode afterward. Neither operation adopts or tears down external resources automatically; configuration, credentials, repositories, worktrees, and Herdr resources are preserved.

For the source service, enter maintenance with `awctl service stop`, `systemctl --user set-environment AW_DATABASE_MAINTENANCE=1`, then `awctl service start`. Perform the explicit database operation above. Afterwards run `systemctl --user unset-environment AW_DATABASE_MAINTENANCE` and `awctl service restart` to resume ordinary reconciliation. An interactive shell export alone does not update a systemd user service's environment.

AW uses SQLite's online backup API, verifies the current set, and retains the seven most recent daily backups under `workflows.db.daily`. Historical archives are outside startup verification and automatic retention, so an old or corrupt historical backup cannot block the new active store. These same-host backups do not protect against losing the host. Corruption is never repaired in place and restore is never automatic.

If a reset/restore exchange times out or the host stops mid-command, inspect `awctl database status` before retrying. A `workflows.db.maintenance` checkpoint blocks ordinary admission until the pending command finishes. Repeat `awctl database reset --force` for a pending reset, or `awctl database restore --backup /original/selected/path` for a pending restore; the latter uses its already verified staged copy even if the source has moved into the archive. Restore continuation requires the original normalized absolute selection; an equivalent path spelling is accepted, while a different backup selection or maintenance action is refused with the pending command's continuation guidance. Finish that operation before selecting another backup. Then restart normally. A completed reset may safely be repeated, producing another separate archive. An interrupted preparation before the checkpoint leaves the active set untouched; repeating the command prepares again. Any unreferenced `.prepare-ID` material is preserved outside ordinary admission. Do not rename backups, remove companions, or edit SQLite to continue.

After reset and restart, run `awctl setup` from the configured canonical Project checkout to register the preserved Project configuration again. Live provider validation uses the operator's normal configured integrations; it is separate from isolated development tests.
