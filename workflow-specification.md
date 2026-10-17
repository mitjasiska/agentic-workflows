# Agentic Workflows Workflow Specification

> Living specification created during the architecture refinement session on October 17, 2026.
>
> This document is the canonical source for detailed workflow mechanics. `architecture.md` owns current product boundaries and architectural shape, `GLOSSARY.md` owns canonical language, and ADRs preserve durable decision rationale. A conflict among them is a documentation defect to reconcile, not an implementation choice.

## 1. Product and reference conventions

- The canonical CLI executable is `awctl`.
- Most task definitions originate in Linear.
- A task definition may alternatively come from a Markdown file or pasted text.
- V1 integrates Linear and GitHub. Jira and GitLab are possible later adapters, not V1 implementations; the initial design preserves narrow provider boundaries without building a generalized multi-provider framework.
- [Agentic Workflows Prototype](https://github.com/mitjasiska/agentic-workflows-prototype) is the behavioral reference. The new product should preserve its proven workflow value while deliberately removing the friction discovered through its use; it is not a class-for-class or command-for-command port.

### 1.1 Immutable, versioned task snapshots

When a Workflow begins, AW captures the Task Definition and its provenance as its first immutable Task Snapshot. Every agent pass records the exact Task Snapshot it received.

For every textual Task Source, the authoritative requirements and source-change fingerprint are selected deterministically:

- When a recognized `Agent instructions` section exists, its captured contents are the authoritative agent-facing requirements. The remaining source content is retained as human-facing context and provenance.
- When `Agent instructions` is absent, the complete captured task content is authoritative.
- Changes outside the authoritative requirements content do not create a new Task Snapshot, pause the Workflow, or alter what later agents receive from the already selected snapshot.
- AW does not use an agent or semantic heuristic to decide whether a requirements change is material. Any changed authoritative content produces a different fingerprint and triggers the handoff-boundary decision.

Linear is expected to use `Agent instructions` for durable development work. Markdown files and pasted text are intended for quick work, such as researching a Git repository and producing a PDF report, so their complete contents will usually be authoritative.

The same missing-section policy applies to every textual source, including a README used as a Markdown task file:

- `warn` is the default. AW emits a prominent warning and continues automatically with the complete captured content as the authoritative requirements.
- `require` refuses to create or adopt the Task Snapshot and does not start the next agent pass until the section is supplied or the policy is explicitly overridden.
- `ignore` silently continues with the complete captured content as the authoritative requirements.

This uniform policy avoids source-specific workflow paths. For a Linear-backed Task, AW additionally captures the Issue identifier, title, complete description, extracted `Agent instructions` section when present, and available source provenance.

Linear and Markdown file sources can be checked again at handoff boundaries. Pasted text and standard input are immutable after capture because AW has no external source to re-read.

For a Task captured from a Linear Issue, later edits to the Issue do not silently alter or automatically interrupt the running pass. The user may still interrupt manually, including with Ctrl+C; recovery from manual interruption is specified separately. Before AW performs the next agent handoff, it compares the source with the latest source revision already handled by the Workflow. If the source changed, AW pauses and offers three choices:

1. Continue using the active Task Snapshot. This acknowledges that exact source revision, so AW does not ask about it again unless the source changes again.
2. Adopt the changed source as a new immutable Task Snapshot in the same Workflow. All earlier snapshots remain in history.
3. Stop without performing another handoff.

Adopting a new Task Snapshot invalidates prior conclusions that depended on the previous requirements. The next agent phase is always implementation, even when review would otherwise have followed, because the existing implementation has not yet addressed the new snapshot. The selected Implementer receives the complete new snapshot and relevant existing workspace and finding context. Review may resume only after implementation completes against that same snapshot.

This deliberately differs from the prototype behavior in which implementation and review may each read the latest Linear Issue. Versioned immutable snapshots prevent an Implementer from working against one set of requirements while a Reviewer judges the result against another, while still allowing the user to adopt changed requirements without discarding the Workflow and worktree.

## 2. Established execution model

Agentic Workflows combines two kinds of behavior:

- The **deterministic workflow** owns orchestration, authoritative state transitions, Git integration, policy enforcement, and verification.
- **Agent work** is non-deterministic. An agent performs a bounded role, such as implementation or review, inside the workspace prepared by the deterministic workflow.

The deterministic workflow is the glue. Agent output is input to that workflow; it is not authoritative merely because an agent reported success.

Conservative blocking must never create an operational dead end. Every blocked, invalidated, or interrupted operation preserves available work and evidence and offers a supported human recovery path without requiring database edits or a completely new Workflow.

Simplicity is a functional constraint. The design should expose only the distinctions needed by observed workflows, keep ordinary execution explainable, and treat proliferating states, flags, override scopes, or special recovery procedures as a reason to pause development and simplify the model. Extension points may be preserved without implementing speculative flexibility.

### 2.1 Agent participation

Two agent roles currently operate inside an Agentic Workflows-managed execution:

- the implementation role changes files;
- the review role assesses the implementation independently.

An agent execution is a Managed Agent Pass only when AW starts and tracks it with a recorded Handoff, role, session, lifecycle, and result. A human may instead open an agent independently in the retained worktree. That activity is Unmanaged Work: AW does not log the agent attempt or show it as a workflow pass, although it reconciles the resulting file and Git state at later deterministic checkpoints.

An External Orchestrator may supervise the broader process but is not currently a managed role inside Agentic Workflows. In the product owner's present topology, this is the ChatGPT/Codex application and its agents or chat running on a Windows machine, separate from the VPS where Agentic Workflows and its execution sessions run. Use **External Orchestrator** for that environment and **AW control plane** for the deterministic product authority; do not use “orchestrator” alone for both.

Execution reachability is not Operation outcome. If Herdr becomes unreachable after an agent was confirmed running, AW retains the timestamped last confirmed observation and shows current reachability as unavailable. It does not infer Failed, stopped, or Interrupted. Interrupted is recorded only after reconciliation positively establishes that the original execution disappeared without a valid Result Envelope.

### 2.2 Role, session, harness, model, and mode

Role and execution selection are separate concepts. The current defaults are policy choices that may change when research or operating evidence shows a better approach:

- Implementation fixes normally resume the original implementation session so it retains its task and code context.
- The initial formal review starts in a separate session without implementation conversation history.
- A later re-review may resume the same review session.
- Implementation and review have separately configurable harness, model, mode, and session-reuse defaults.
- The configured session-reuse policy determines whether a later pass normally starts a new session or resumes an eligible earlier session.
- The user may override session reuse for the next applicable role pass only. Once that pass starts, later passes return to the configured policy.
- Selecting a different model changes the workflow's model selection for that role and applies to all of its later passes until changed or cleared. Implementer and Reviewer model selections are independent.
- A harness selection will provisionally have the same role-scoped lifetime as a model selection, subject to later usage evidence. The lifetime of mode changes is deferred.
- If the expected session cannot be verified or resumed, Agentic Workflows stops for a user decision instead of silently launching a replacement.
- A new implementation session receives a complete bootstrap handoff, including the full task snapshot.

The architecture keeps role and session allocation independent so future policy may permit one session to fill both implementation and review roles if evidence supports that choice. This is not the current formal-review default.

The user may make or replace a Next-pass Session Override while the current agent pass is running. This is a future-intent change only: it selects session reuse for the next pass of a role but cannot change the current pass, Handoff, Task Snapshot, or worktree permissions. AW resolves the latest applicable override at the handoff boundary, revalidates it, and consumes it when the selected pass starts. Thus, while an Implementer is active, the user can choose whether the next review should use the previous Reviewer or a new Reviewer without interrupting implementation.

## 3. Git-backed implementation and review loop

The agreed high-level sequence is:

1. The deterministic workflow prepares a Git worktree at a known baseline.
2. An Implementer performs an implementation pass by editing files in that worktree.
3. The implementation pass leaves uncommitted worktree changes. The Implementer does not create a Git commit.
4. A Reviewer reviews the uncommitted worktree diff.
5. If the Review Pass returns findings, the deterministic workflow hands the findings and the same uncommitted worktree changes back to the Implementer for another pass.
6. Implementation and review repeat until the review succeeds or another workflow outcome ends the loop.
7. After successful review, AW stores the Review Report and the identity of the exact reviewed uncommitted state in durable workflow data, then enters a submission-ready state without committing.
8. The user may inspect the retained worktree and Review Report directly or with an external agent that is not part of the managed workflow.
9. The normal `awctl pr` wrapper verifies the worktree, creates a commit, pushes it, and creates or updates the pull request or merge request by composing three internal deterministic actions.
10. Optional PR-on-clean automation may invoke the same deterministic operation later; manual PR creation is the default.

Every automatically started Implementation Pass or Review Pass consumes one unit from the current Managed Agent Pass Budget. The default budget is six: the initial Implementation Pass and Review Pass, followed when necessary by two repair-and-review pairs. A finite budget is checked at pass boundaries: AW lets an active pass finish and records its result, but pauses before starting another Managed Agent Pass when none remain. The workflow requires human interaction rather than becoming Failed. Status includes the completed and remaining pass counts and the best available token and cost measurements. The user can explicitly continue with a new budget.

The budget may instead be set explicitly to Unlimited. In that mode, pass count never causes a pause, allowing intentional unattended execution. Unlimited does not suppress other stops and must be represented as a deliberate policy value rather than inferred from missing or invalid configuration. When a finite budget is exhausted, `awctl continue` starts a new automatic batch with the currently configured budget, which is six by default. `awctl continue --max-passes N` sets the size of that new batch, and `awctl continue --max-passes unlimited` makes that batch unlimited. These invocation-scoped values do not silently rewrite the saved configuration. A possible elapsed-agent-time budget is reserved but not specified. Provider usage-window detection, the meaning of provider exhaustion, and automatic recovery after a reset are intentionally deferred.

Task requests to commit, push, or create a pull or merge request do not create a conflict after implementation. They describe the desired result, while AW assigns those mechanics to the deterministic actions coordinated by `awctl pr`. AW-managed Implementers and Reviewers do not create commits as part of their Managed Agent Passes. This rule does not prevent a human or an independently opened agent from using Git outside the managed workflow.

Like the prototype, V1 exposes only `awctl pr`; commit, push, and forge submission are internal deterministic actions. The wrapper and its actions are durably resumable and idempotent across the chain. A retry reconciles real local and remote state before acting, reuses the exact commit already created for the action chain, does not push it again when the intended remote branch already contains it, and does not create a duplicate when the correlated pull or merge request already exists. If AW cannot prove that an observed effect belongs to the same action chain and target, it stops with a Recovery Plan rather than guessing or replaying everything.

Before any mutating action, `awctl pr` performs one deterministic preflight. Normal preflight requires a current clean Review Report, materializes the current proposed tree and verifies it against the Reviewed Tree, uses a Worktree Fingerprint when changes are uncommitted, verifies repository, branch, base, remote, and forge target identity, and stores a frozen PR Intent for that exact content and action chain. No commit, push, or forge write starts before the intent is durable.

`awctl pr --force` bypasses only the current clean-review requirement. It still freezes the exact current worktree content and performs all target, revision, concurrency, and destructive-scope checks. Neither normal retry nor forced retry may silently replace the frozen content with later worktree changes.

The explicit `--force` flag is sufficient human confirmation. AW records and displays the bypass but does not require an interactive confirmation, a typed phrase, or a second flag. Because the operation targets an isolated task branch and worktree and never force-pushes or rewrites the shared base, an erroneous forced submission is recoverable.

Force is invocation-scoped only. It cannot be persisted as configuration at any level, inherited by a retry that omits `--force`, or enabled for automatic PR-on-clean behavior. Each bypass requires an explicit `awctl pr --force` invocation.

The internal boundaries must be reusable so separate public `awctl commit` or `awctl push` commands can be added later if usage justifies them. V1 does not expose those commands merely to anticipate possible flexibility.

Remote branch divergence stops only the unsafe submission operation. AW never overwrites, resets, force-pushes, or automatically pulls, merges, or rebases an unexpectedly changed remote branch. It reports the expected and observed revisions and tells the user to fetch and reconcile the remote changes manually. The worktree and unrelated safe commands remain available. The user then reruns `awctl pr`, which revalidates and resumes the correlated action chain when safe. No separate mark-resolved command, recovery comment, or workflow-state repair is required. The `--force` option bypasses review policy only and cannot bypass remote-revision checks or enable Git force-push behavior.

After a pull or merge request exists, the worktree and branch may be retained for human-directed follow-up. Another implementation/review cycle followed by `awctl pr` may produce a later commit, push it to the same branch, and update the existing request. The one-commit rule applies to one submitted batch after any number of implementation/review passes; it does not impose one commit on the lifetime of the request.

The user may also perform follow-up work directly or through an external agent. AW does not manufacture history for that Unmanaged Work. It detects its observable effects at the next checkpoint, treats earlier dependent review evidence as stale, and requires either fresh applicable review evidence or an explicit Force Override before policy-dependent submission.

A fresh Review Pass may assess a local commit created through Unmanaged Work. AW binds the Review Report to the comparison baseline content and complete Reviewed Tree without claiming that AW created the commit. If a human or external agent merely commits an already reviewed tree, the changed commit SHA does not invalidate the content review. `awctl pr` may adopt a clean local `HEAD` whose complete tree equals the Reviewed Tree, treat the internal commit action as already satisfied, and continue with push and forge submission after separate ancestry and target checks. Any later content or mode change invalidates the review evidence. Without a clean review, submission still requires an invocation-scoped Force Override.

### 3.1 Implementation-to-review and review-to-implementation handoffs

Agentic Workflows constructs every handoff deterministically from authoritative durable workflow data. Agents do not pass authoritative task context directly to one another.

Every full or bootstrap Handoff uses one explicit three-layer order:

- **Workflow Instructions** are AW-owned lifecycle, safety, and role directions prepended before the Task Snapshot.
- The **Task Snapshot** is the immutable source-derived statement of the required outcome and context for the pass.
- The **Pass Brief** is AW-generated direction for the bounded pass, such as implementing without committing, reviewing without editing, addressing a specific findings batch, or returning a structured result. It is recorded with the pass and may vary without changing the Task Snapshot, but it must not silently change the Task requirements.

The prototype demonstrates all three concerns but does not compose them uniformly. Its review and integration handoffs begin with authoritative workflow instructions and place Linear requirements later, initial implementation places the Linear Issue before workflow-owned implementation instructions, and automated implementation passes append findings and result-delivery directions. The new design should make these layers explicit rather than inherit that incidental ordering.

Workflow Instructions govern AW lifecycle, safety, Git ownership, and role boundaries. The Task Snapshot governs the requested outcome. A Pass Brief may select or narrow the current step but must not redefine that outcome. Repository Guidance follows the status convention in `architecture.md`: Product Invariants are binding, Design Directions may be coherently refined, and Open Areas permit reversible local choices. The system does not ask agents to search broadly for textual contradictions.

The Handoff must preserve Implementation Latitude. It supplies required outcomes, constraints, applicable findings, evidence, and the result contract, but does not prescribe a mechanical edit sequence unless that sequence is itself an explicit requirement. A cohesive task may span several components when reasoning across them produces a better design. Deterministic orchestration bounds and observes Agent Work; it does not replace the Implementer's technical judgment with an unnecessarily detailed plan or force an ad hoc workaround to satisfy a Design Direction literally.

Applicable Repository Guidance constrains the implementation without expanding the Task Snapshot. An architecture document is not a backlog that the Implementer may implement opportunistically. A Task Snapshot conflict with a Product Invariant is blocking and the pass reports the exact conflict. A Design Direction may be refined when a coherent solution preserves the task and invariants; the result reports that choice and its rationale. An Open Area blocks only when proceeding would silently decide a material product question. AW's harness integration must ensure the guidance is available to the pass even when the selected harness does not automatically discover `AGENTS.md`; the pass record identifies the guidance paths and repository revision used.

- A new Reviewer receives the Task Snapshot, current worktree state or Review Target, available Validation Evidence, and the implementation result needed for review.
- When findings return to the original implementation session, Agentic Workflows may send an incremental handoff because that verified session already received the task snapshot.
- When work moves to a new implementation session, Agentic Workflows sends a bootstrap handoff containing the task snapshot, current workspace state, and applicable unresolved findings.
- Agent-provided summaries may supplement a handoff but never replace the authoritative task snapshot, workspace facts, findings, or validation evidence.
- Agentic Workflows decides which role and session should run and requests the action through its Herdr gateway. Herdr owns the actual session start or resume and instruction delivery.

Change detection for re-readable Task Sources, currently Linear and Markdown files, aligns with handoff boundaries. Optional periodic detection may inform the user during a long-running phase, but it does not automatically interrupt that phase. The exact warning mechanism remains open.

### 3.2 Base-conflict integration

AW retains the prototype's explicit `awctl integrate` command for one narrow case: an unpublished clean-reviewed result must be replayed onto an advanced base, and deterministic replay produces file conflicts. This is separate from an external agent creating a commit in the existing task worktree. A local commit changes Git history identity but, when it preserves the same Reviewed Tree, does not change the Review Target or create a merge conflict merely by existing in that worktree.

`awctl pr` reports a detected base conflict and stops the unsafe submission action; it never launches conflict-resolution work automatically. When the user explicitly runs `awctl integrate`, AW revalidates the task, repository, base, review, and publication state and reproduces the conflict in an isolated checkout. A bounded managed agent pass may resolve and validate files there, but AW retains exclusive ownership of staging, history construction, installation, push, and forge actions.

AW proves the isolated file result before installing it into the task branch through deterministic Git actions. Successful installation invalidates the old Review Report and requires a fresh independent Review Pass. `awctl integrate` never publishes. A conflict-free recheck sends the user back to `awctl pr` without launching an agent. V1 should preserve these safety properties while avoiding the prototype's highly specialized recovery states unless concrete failures require them.

## 4. Review Target and Worktree Fingerprint checkpoints

The Reviewer assesses one exact Review Target. AW identifies it by the comparison baseline content and the complete proposed Reviewed Tree. A point-in-time Worktree Fingerprint helps construct and verify that tree when changes are uncommitted; neither the current commit SHA nor a continuously running filesystem watcher defines review validity. In the ordinary managed path, the fingerprint identifies the uncommitted implementation. When Unmanaged Work has already committed the files, AW obtains the same proposed tree from clean `HEAD`.

The fingerprint covers the uncommitted change set inside the managed worktree relative to the workflow baseline:

- the path and change kind for every modified or deleted tracked file and every non-ignored untracked file;
- the content hash and relevant Git mode for each path that has content;
- the workflow baseline needed to interpret the change set.

Timestamps, staging versus unstaging of identical worktree content, ignored files, caches, and build output do not affect the fingerprint. They cannot change the content AW's normal PR operation is intended to commit.

AW recalculates the fingerprint only at deterministic boundaries that need it:

1. After the Implementer finishes, or after AW observes user-selected Unmanaged Work, and immediately before the review handoff.
2. After the Reviewer finishes. The baseline content and proposed tree must equal their pre-review values. AW also verifies that `HEAD`, the index, and worktree did not move during the pass so Reviewer mutation or concurrent interference invalidates that pass.
3. When recording a clean Review Report, which stores the accepted Review Target.
4. Immediately before `awctl pr`, which must match the accepted Review Target unless the user supplies `--force`.

Every later implementation repair that changes the proposed tree requires a new Review Pass. Staging, unstaging, or committing the identical tree does not change the reviewed content. The exact canonical encoding, race-free commit construction or adoption, and recovery behavior when a fingerprint cannot be established remain to be specified.

The prototype supplies a behavioral reference: it creates a content-based snapshot covering the baseline, HEAD, branch, index state, Git status, tracked files, non-ignored untracked files, modes, symlinks, and supported submodule state. It reads twice to reject a capture that changes during observation. During `pr`, it stages through a private temporary index, verifies that the resulting Git tree represents the reviewed bytes and modes, and refuses publication when the evidence differs. The new implementation may simplify the mechanism, but must preserve equivalent guarantees.

Both normal and forced PR creation require that no managed agent pass is active in the workspace. The deterministic PR operation holds the exclusive workspace mutation lock through verification, commit construction, push, and pull-request creation. Read-only commands, Next-pass Session Overrides, and Role Model Selections may still execute during an active agent pass because they do not mutate the active pass or worktree.

## 5. Validation evidence and routing

Prototype research is recorded in [docs/research/prototype-validation.md](docs/research/prototype-validation.md). It confirms that focused test selection and execution are agent-driven prompt behavior. The deterministic controller validates structured check evidence and chooses workflow routing; it does not select or execute the commands itself. TDD is optional, Reviewer validation defaults to focused-first, and linting is merely an agent-selected check unless a repository or task explicitly requires it.

The prototype rejects an Implementer completion containing any failed check. Even when the same failure is reproduced on the unchanged base and diagnosed as unrelated, the Implementer must report blocked, the loop escalates, and no Reviewer starts automatically. This is a known friction point rather than an inherited requirement.

The successor preserves every unresolved observed failed check as failed evidence but routes independently. A failing command does not interrupt an Implementation Pass: the Implementer can fix the implementation or test and rerun Focused Validation before completing the pass. Intermediate red/green TDD steps are internal agent work rather than AW state transitions. If a failure remains at completion, the Implementer records it and may still hand the work to the Reviewer unless a human decision or another genuine blocker is required.

The Reviewer receives all available Validation Evidence. A related failure may support findings and another implementation pass. An unrelated or deliberately deferred limitation may remain visible alongside a clean Review Report; clean review means the Reviewer found no actionable defect in the Review Target, not that every possible check passed. AW never changes a recorded failure into a pass or implies that an unrun check succeeded.

Broad Validation is user-scheduled by default. A suite containing thousands of tests may run manually, in CI, nightly, or at another chosen boundary without blocking ordinary implementation, review, or `awctl pr` merely because it has not run. The workflow records its known status and limitations. It does not automatically launch the full suite.

AW must not infer that an agent-selected command actually ran merely from a structured claim, and it must not add a deterministic test-selection engine, universal linter, or new protection mechanism without a demonstrated workflow need. In V1, linting and type checking use the same agent-selected Validation Evidence model as tests. They are not separate deterministic stages and do not receive stricter routing merely because of their check type.

A later milestone may add project-configured commands as a deterministic validation pipeline after the final Review Pass. This is a reserved extension seam rather than a V1 plugin framework. The command configuration, failure routing, and effect on review freshness require a separate decision before implementation.

### 5.1 Workflow audit trail

Every Managed Agent Pass and deterministic AW action appends its known inputs, effective execution configuration, lifecycle events, detailed structured report, evidence, measurements, warnings, decisions, overrides, and state transitions to the Workflow Audit Trail. Later events may supersede an earlier conclusion, but ordinary commands and recovery actions never rewrite history to make the earlier event disappear. AW does not store every agent message, terminal interaction, or tool call. The dashboard and `awctl` read the same durable record.

Each structured pass report must be detailed enough to explain what the role attempted, what changed or was assessed, what evidence supports the result, what remains uncertain, and what the next role or External Orchestrator needs to know. AW composes these reports with deterministic history and current state into a portable Workflow Report. Its intended consumer is often an external planning agent or chat rather than a human reading every field directly.

An agent delivers its pass report as exactly one UTF-8 JSON Result Envelope in an AW-authorized location outside the worktree. The envelope is correlated to the expected pass and validated against one versioned universal schema before AW accepts its outcome or performs another handoff. A role discriminator selects a role-specific payload and validation rules; implementation and review therefore share one envelope contract without turning role-specific fields into a loose collection of optionals. Narrative fields may contain long Markdown; machine-significant outcomes, findings, resolutions, checks, and correlation data remain typed fields rather than conventions embedded in prose. Missing, malformed, duplicate-key, mismatched, or internally inconsistent envelopes fail validation and cannot authorize a successful transition.

If Herdr positively confirms that the agent turn completed normally but the Result Envelope is missing or invalid, AW records the Operation as Failed with a precise result-contract diagnostic. It does not call the Operation Interrupted, because execution completion is known. The worktree, session, delivery artifact when present, and all other evidence remain available through the ordinary Recovery Plan.

The JSON file is a temporary cross-process delivery mechanism. After successful validation, AW persists the complete immutable Result Envelope and a typed relational projection of its query- and routing-relevant fields together with resulting workflow events, next current state, and any pending next-operation intent in one database transaction. The envelope and projection are created from the same validated object and cannot be edited independently. Later workflow decisions and dashboard reporting do not depend on the temporary file remaining present. Git, Herdr, and artifact stores remain external sources of their own state and are referenced and reconciled rather than copied wholesale into the workflow database.

The V1 Workflow Report does not embed the complete code diff. It includes a compact changed-file list derived deterministically from the exact Review Target, not trusted from an agent-authored summary. Optional diff export is deferred until usage demonstrates a need.

The dashboard presents a concise projection of this information: workflow state; implementation, review, and handoff counts; the latest or final review verdict; a short outcome; and whether escalation or human interaction is required. Detailed reports remain available for inspection and copying without dominating the primary view.

The workflow-history query presents durable activity as an ordered sequence of meaningful steps rather than only the latest status. Each step includes a stable workflow-local position, step type, applicable role and pass identity, timestamp, semantic outcome, short result, attention requirement, and references to its detailed report and evidence. Implementer and Reviewer steps are independently expandable. Later events may supersede a conclusion but do not remove its earlier step from the timeline. Live dashboard notifications are freshness hints only; the API's persisted history remains authoritative.

Presentation status must distinguish **Failed** from **Needs changes**. Failed means the pass or its execution failed. Needs changes means a Reviewer completed its role and returned actionable findings for another Implementation Pass. Required human interaction is a third condition and must not be conflated with either failure or routine findings.

At any point, AW can return one step's report, compose selected steps, or compose the complete portable Workflow Report deterministically from durable records. These are query projections rather than additional stored report variants. The report includes all agreed structured context but not raw transcripts, individual tool calls, hidden reasoning, or the complete code diff.

Unmanaged Work is not retroactively represented as a managed pass. AW records only the externally observed state change, the time and checkpoint at which it was detected, its effect on existing evidence, and the resulting user options. This visible provenance gap is acceptable in V1. AW does not prevent experienced users from working outside the managed workflow; possible organization-level enforcement is deferred as a separate policy capability.

## 6. Human recovery contract

Every blocked, invalidated, or interrupted operation stores a durable Recovery Plan. It identifies the blocker, preserved work and evidence, retry safety, currently valid recovery actions, the exact `awctl` command for each action, and the effect or bypass involved. `awctl status` and the dashboard display the same plan, and every selected action revalidates current state before mutating anything.

The recovery surface should remain small and composable. It reuses normal operations such as inspect/status, retry or resume, selecting an eligible agent/session for another pass, obtaining a new review, explicit force, and retain or stop. AW must not grow a separate bespoke repair workflow for each error or require manual database edits.

Recovery never edits historical records to make a blocker disappear. A successful ordinary action records a new checkpoint and new evidence that supersede the blocked decision for subsequent workflow progress while preserving the blocker and stale evidence in history. For example, another Implementation Pass and clean Review Pass can make the current worktree eligible for normal PR creation without changing the earlier invalid Review Pass into a valid one.

Humans and external agents remain free to inspect or change retained worktrees. AW detects resulting evidence drift at the next deterministic checkpoint, warns the user, and blocks only operations that would rely on stale evidence. It preserves the current worktree and never silently rolls external changes back.

The dashboard and audit history must remain truthful about provenance. They may record that externally caused file or Git state appeared and how AW responded, but they must not represent an external agent attempt as a Managed Agent Pass or imply that its unavailable conversation and actions were captured.

## 7. Open details

The next questions cover:

- the configuration scope of Workflow Instructions and the composition of incremental Handoffs sent to a verified existing session;
- the canonical Worktree Fingerprint encoding, race-free Review Target construction, and minimum ancestry rules for safely adopting an existing commit;
- whether conflict resolution uses a distinct agent role and session policy or a specialized Implementer pass;
- the exact versioned Result Envelope fields, validation rules, and role-specific payloads;
- the configuration and failure semantics of any later deterministic post-review validation pipeline;
- the Managed Agent Pass Budget's broader saved-configuration scope, a possible elapsed-agent-time budget, and provider usage-window behavior;
- retry, interruption, cancellation, and recovery behavior, including the mapping from blocker classes to the small recovery-action vocabulary;
- whether mode changes persist for later passes and whether usage confirms the provisional harness behavior, without adding arbitrary override-lifetime machinery;
- final commit construction and failure handling.

