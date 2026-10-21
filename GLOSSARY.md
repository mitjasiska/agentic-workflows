# Agentic Workflows

The canonical language for the human-directed workflow that coordinates deterministic control with non-deterministic agent work.

## Language

**Agentic Workflows (AW)**:
The product and deterministic control plane described by this domain model.
_Avoid_: AV

**Project**:
The AW configuration and workflow boundary anchored to one Git repository. It contains the Workflows started for Tasks in that repository and may eventually map to more than one Linear Project.
_Avoid_: Linear Project, Workflow, multi-repository product

**Global Configuration**:
The authoritative installation-level settings file whose defaults are shared by every Project registered with one AW installation.
_Avoid_: Workflow configuration, Project-specific settings, secret store

**Project Configuration**:
The authoritative local, repository-excluded TOML settings file in a Project's canonical registered checkout. Its validated integration identities and choices are reused when new Workflows begin; a committed example documents its shape but is not active configuration.
_Avoid_: Per-Workflow setup, committed active configuration, mutable Task settings, secret store

**Task**:
The Project-level, provider-neutral unit of requested work captured from a Task Source and delivered through one or more Workflows. Sibling Workflows reference the same Task identity but own independent Task Snapshots and mutable workflow state.
_Avoid_: Issue, ticket

**Task Slice**:
A stable, user-declared, agent-visible scope label on one Workflow for implementing a bounded part of a Task as its own branch, worktree, review cycle, and pull request. The complete Task Snapshot remains authoritative; the label has no separate requirements document, aggregate, or lifecycle, and AW does not enforce its semantic boundary mechanically. A Task Slice must not distinguish competing implementations of the same scope; that is a Workflow Variant.
_Avoid_: Milestone, implementation phase, additional commit, model experiment, competing implementation

**Workflow Variant**:
An optional label for one candidate implementation when several Workflows intentionally attempt the same Task Snapshot and Workflow Scope, for example with different models or approaches so the user can compare them and choose a candidate. Each Variant has an independent branch, worktree, agent passes, review evidence, and lifecycle. A Variant does not narrow the Task requirements and is therefore distinct from a Task Slice. Variant support is planned for v1 or early v2, not the Initial E2E Milestone; exact CLI syntax and candidate-selection behavior remain to be specified.
_Avoid_: Task Slice, scope, automatic winner

**Workflow**:
The durable end-to-end execution history and retention boundary created when AW starts one Task under one Workflow Scope, owning one workspace, review path, submission path, and their run-specific records. A Project normally contains many Workflows because each started Task or named slice receives its own.
_Avoid_: Task, multi-worktree task container, agent session

**Workflow Integration Binding**:
The stable external identities selected for one Workflow when it starts, including its repository, task-provider, forge, execution-target, and workspace relationships. Configuration changes do not silently redirect the binding; credential rotation through the same access reference does not change it.
_Avoid_: Current project defaults, credentials, mutable configuration snapshot

**Workflow Workspace**:
The Herdr-managed working area bound to exactly one Workflow, containing the worktree for that Workflow's branch and its related execution context. AW may reuse it only when its ownership by that Workflow is proven. The worktree is the editable checkout; it is not itself the branch.
_Avoid_: Arbitrary directory, guessed worktree, shared workflow workspace

**Unmanaged Herdr Resource**:
A Herdr pane, session, process, workspace, or worktree whose ownership by an AW Workflow is not proven. AW may observe it during reconciliation but has no authority to adopt, resume, interrupt, or delete it.
_Avoid_: AW orphan, available workspace, implicitly managed resource

**Workflow ID**:
The immutable generated identifier used to select one existing Workflow when current-worktree identity is unavailable. Task identifiers and slice names are not substitutes for it during post-creation command selection.
_Avoid_: mutable title, inferred sibling selection

**Workflow Scope**:
The value that identifies whether a Workflow addresses the Whole Task or one `NamedSlice(name)`. The original user-declared slice name is retained as semantic scope; a normalized branch slug is only a derived technical representation. Workflow Scope has no independent lifecycle or identity. A Task may retain Workflows with both kinds of scope; creating a later scoped Workflow does not supersede or repartition earlier Workflows.
_Avoid_: Task Slice aggregate, workspace selector, agent-inferred scope

**Submission**:
The deterministic umbrella that establishes the authorized commit, publishes the branch, and creates or updates the forge change request. A GitHub pull request and a GitLab merge request are provider forms of Submission; merging the request is a separate operation.
_Avoid_: Merge, deployment, pull request only

**Submission Evidence**:
The AW record binding a retained Workflow and scope to a confirmed forge request, including its provider, repository, request number and URL, and head branch. It supports deterministic continuation while retained and may be removed by explicit cleanup; the forge remains authoritative for live request status.
_Avoid_: permanent project issue registry, cached PR status as authority

**Whole Task**:
The default Workflow Scope in which one Workflow implements the complete Task and produces one pull request, used when the user has not explicitly selected a named Task Slice.
_Avoid_: Default slice, unnamed slice, entire project

**Task Definition**:
The requirements a workflow is expected to satisfy, together with their source provenance.
_Avoid_: Prompt, pasted task, issue

**Task Source**:
The origin from which a task definition is captured, such as Linear, a Markdown file, or pasted text.
_Avoid_: Issue tracker

**Task Identifier**:
The stable value used to distinguish a Task for workspace and branch identity. Linear uses its normalized Issue key; Markdown and pasted-text Tasks receive a random 128-bit identifier at capture. A readable slug may accompany the identifier but is not identity-bearing.
_Avoid_: mutable Task title, content hash, human name as uniqueness proof

**Task Snapshot**:
One immutable version of a Task Definition and its provenance. Every agent pass is bound to exactly one Task Snapshot, while a Workflow may retain several explicitly adopted snapshots and sibling Workflows retain provenance to the same parent Task.
_Avoid_: Latest issue, live task, current requirements

**Active Task Snapshot**:
The Task Snapshot selected by the user as the requirements for the next agent pass.
_Avoid_: Latest source, mutable task

**Source Revision**:
A specific observed version of a Task Source, identified sufficiently to detect whether its task-defining content changed.
_Avoid_: Latest task

**Linear Issue**:
The Linear record from which a task definition may be captured.
_Avoid_: Task, workflow

**Agent Instructions**:
The designated section of a textual Task Source containing the authoritative agent-facing requirements when present. It is expected primarily in Linear Issues.
_Avoid_: Human description, live prompt

**Human Description**:
The captured task-source content outside Agent Instructions, retained as context and provenance rather than authoritative requirements when Agent Instructions is present.
_Avoid_: Agent Instructions, Task Definition

**Agent Instructions Policy**:
The project rule governing whether a Task Source without Agent Instructions is accepted silently, accepted with a warning, or rejected.
_Avoid_: Linear policy, source policy

**Handoff**:
The authoritative context assembled by Agentic Workflows for starting or resuming one bounded agent pass.
_Avoid_: Agent-to-agent message, prompt forwarding

**Workflow Instructions**:
AW-owned lifecycle, safety, and role directions prepended to a full Handoff before the Task Snapshot.
_Avoid_: Agent Instructions, Task requirements, prepended instructions

**Repository Guidance**:
Versioned workspace instructions and architectural context, classified as Product Invariants, Design Directions, or Open Areas, that constrain or inform how a Task may be satisfied without adding to its requested outcome or scope.
_Avoid_: Task Definition, roadmap authorization, live chat context

**Product Invariant**:
Settled product behavior or an architectural boundary that implementation must respect unless the authoritative architecture is changed explicitly.
_Avoid_: Preference, implementation suggestion, implicit requirement

**Design Direction**:
A preferred architectural shape or default that an implementation may refine coherently while preserving Product Invariants and the intended outcome.
_Avoid_: Mandatory edit recipe, Product Invariant, arbitrary deviation

**Open Area**:
An explicitly unresolved part of the product in which reversible local implementation choices are allowed but material product decisions still require focused architecture refinement.
_Avoid_: Missing task scope, unlimited discretion, settled decision

**Pass Brief**:
The workflow-generated direction for one bounded agent pass, kept separate from the source-derived Task Snapshot.
_Avoid_: Task Snapshot, Agent Instructions

**Agent Session**:
A retained harness conversation or execution context that may be idle or used by a Managed Agent Pass. Several sessions may belong to one Workflow without implying that their agents are executing concurrently.
_Avoid_: Agent role, Managed Agent Pass

**Session Reuse Policy**:
The configured rule that determines whether a later pass of a role normally resumes an eligible earlier session or starts a new session.
_Avoid_: Reviewer identity, model selection

**Next-pass Session Override**:
A replaceable one-pass user choice that overrides the Session Reuse Policy for the next pass of a specified role.
_Avoid_: Permanent session setting, active-pass reconfiguration

**Role Model Selection**:
The model chosen for all subsequent passes of one role in a Workflow until changed or cleared.
_Avoid_: Next-pass model, global model

**Deterministic Workflow**:
The rule-driven part of Agentic Workflows that makes authoritative decisions and coordinates bounded work.
_Avoid_: Glue, agent workflow

**Workflow Phase**:
The kind of work through which a Workflow is currently progressing: `WorkspacePreparation`, `Implementation`, `Review`, or `Submission` in the Initial E2E Milestone. Eligibility, durable outcomes, retention, and cleanup are not phases.
_Avoid_: Agent process status, operation status, blocker reason

**Attention Requirement**:
One current, typed alert derived when safe continuation requires a human policy choice or manual/external action. It identifies the concrete unresolved matter and presents a proposed resolution rather than merely announcing that the Workflow is blocked. Users act on the real situation rather than marking alerts resolved; AW revalidates facts and audit history records when each alert appeared and ceased to apply.
_Avoid_: Workflow phase, failure, generic blocked state

**Attention Required**:
The derived Workflow gate that is active while one or more Attention Requirements currently apply. It prevents automatic continuation while preserving the current Workflow Phase and disappears when revalidation finds that no requirement remains applicable.
_Avoid_: Stored duplicate status, primary blocker only, workflow phase

**Abandoned Workflow**:
A Workflow on which the user has explicitly stopped all automatic routing while retaining its worktree, sessions, evidence, and history. Abandonment is resumable through an explicit command and is not cleanup or failure.
_Avoid_: Failed Workflow, deleted Workflow, implicit pause

**Forced Cleanup**:
An invocation-scoped destructive escape hatch that asks Herdr to stop and remove every managed execution resource owned by one exactly identified Workflow, deletes that Workflow's exact verified local branch even when local-only work is lost, and then deletes all of its AW-owned state. It never changes a remote branch, pull request, or Task; an unconfirmed teardown or local-branch deletion produces an immediate partial result and no retained Workflow record.
_Avoid_: Force Override for submission, arbitrary directory deletion, persistent force setting

**Operation**:
One immutable, bounded attempt to perform a workflow action. Retrying or resuming creates another Operation linked to its predecessor; that Operation may reuse an eligible agent session without reusing the predecessor's identity.
_Avoid_: Mutable retry record, agent session

**Operation Status**:
The durable lifecycle status of one bounded Operation: `Pending`, `Running`, `Completed`, `Failed`, or `Interrupted`. User-requested stops and confirmed vanished executions are both `Interrupted` with distinct typed reasons; `Canceled` is a Task-provider state, not an Operation Status.
_Avoid_: Workflow phase, terminal pane status, failed Workflow

**Execution Observation**:
AW's timestamped last verified view of an Operation's Herdr execution and the execution target's reachability at that observation.
_Avoid_: Operation Status, inferred process state

**Agent Work**:
A non-deterministic contribution produced by an agent for a bounded role in a workflow.
_Avoid_: Workflow logic, authoritative result

**Implementation Latitude**:
The bounded freedom an Implementer has to choose a coherent technical solution, refine a Design Direction, or make a reversible local choice in an Open Area while respecting the Task Snapshot, Workflow Instructions, and Product Invariants.
_Avoid_: Unspecified requirements, unrestricted agent behavior, mechanical edit plan

**Managed Agent Pass**:
One bounded Implementer or Reviewer execution started and tracked by AW with a recorded Handoff, session identity, and result. An idle retained Agent Session is not an active pass.
_Avoid_: Any agent activity, external agent run, idle Agent Session

**Unmanaged Work**:
Human or agent activity in a retained workspace that AW did not start or track. AW may observe its resulting file or Git state without claiming knowledge of how the work was performed.
_Avoid_: Managed Agent Pass, missing workflow pass

**Workflow Audit Trail**:
The durable, chronologically ordered, append-only record of managed workflow inputs, actions, events, results, evidence, measurements, decisions, and overrides. Later entries may supersede earlier conclusions but do not rewrite them.
_Avoid_: Activity feed, agent transcript, mutable status history

**Workflow Report**:
A detailed, portable report composed from Task Snapshots, structured pass reports, deterministic workflow events, evidence, measurements, and current state for inspection or transfer to an External Orchestrator.
_Avoid_: Raw transcript, dashboard summary, agent reasoning trace

**External Orchestrator**:
An agent or chat outside the AW-managed workflow that helps a human plan, inspect results, or decide next actions without becoming the authoritative control plane or a Managed Agent Pass.
_Avoid_: Orchestrator, AW worker, managed role

**Result Envelope**:
The single JSON object through which a Managed Agent Pass reports its correlated outcome, detailed narrative, and typed evidence to the Deterministic Workflow. It is the self-contained durable pass result used for routing, later handoffs, reporting, and human inspection without requiring the agent transcript.
_Avoid_: Status message, free-form completion claim, agent transcript

**Implementation Pass**:
One attempt by an Implementer to change the worktree so that it satisfies the captured task and unresolved review findings.
_Avoid_: Commit, implementation workflow

**Review Pass**:
One independent assessment of an exact Review Target before submission.
_Avoid_: Conversation review, latest-branch review

**Managed Agent Pass Budget**:
The number of Implementation Passes and Review Passes AW may start automatically in the current batch before requiring human continuation, or an explicit Unlimited value. The default is six passes. Exhaustion pauses before another pass and does not make the Workflow failed.
_Avoid_: Review failure, provider usage limit, hard-coded cycle count

**Implementer**:
The agent role responsible for changing files to satisfy the task and applicable review findings.
_Avoid_: Implementor, coder, implementation worker

**Reviewer**:
The agent role responsible for independently assessing one exact Review Target.
_Avoid_: Review worker, approver

**Uncommitted Worktree Changes**:
The tracked and relevant untracked content in a managed Git worktree that differs from its baseline and has not been committed.
_Avoid_: Dirty files, dirty worktree

**Worktree Fingerprint**:
A deterministic content identity for the complete proposed tree relative to its comparison baseline at one checkpoint. Changes to included paths, contents, deletions, or relevant modes change the identity; timestamps and staging state do not.
_Avoid_: File watcher, timestamp, repository revision

**Reviewed Tree**:
The exact complete file content and modes accepted by a clean review, independent of whether that content is uncommitted or stored in a Git commit.
_Avoid_: Commit SHA, branch head, latest files

**Review Target**:
The exact proposed content and comparison baseline assessed by a Review Pass. Committing the same Reviewed Tree does not change the Review Target.
_Avoid_: Commit SHA, latest worktree, moving branch

**Review Report**:
The durable structured result of a Review Pass, bound to the exact Review Target that was reviewed. It can provide current clean-review evidence only while that target remains unchanged.
_Avoid_: Agent message, approval

**Validation Evidence**:
A recorded observation of a check that passed, failed, or was not run, including enough context to understand its relevance and limitations.
_Avoid_: Proof of correctness, automatic workflow verdict

**Focused Validation**:
Checks selected for their direct relevance to the current change, findings, or concrete risk.
_Avoid_: Full suite, arbitrary small test set

**Broad Validation**:
A large repository-wide or system-wide check set whose execution timing is selected separately from ordinary implementation and review passes.
_Avoid_: Focused Validation, automatic pass gate

**Integration Operation**:
An explicit recovery operation that reconciles an unpublished task result with an advanced base when deterministic replay conflicts, producing a new state that requires fresh review.
_Avoid_: External integration, automatic rebase, unmanaged commit adoption

**Recovery Plan**:
The durable explanation of a blocker and the currently valid human actions for resolving, bypassing, or retaining it.
_Avoid_: Manual database repair, hidden recovery state

**Workflow Resume**:
The general continuation action for a retained Workflow that stopped before its next safe action. It revalidates current facts, reconciles the preceding Operation, and proceeds only when one safe continuation is unambiguous; partial Submission instead continues through its own `awctl pr` command.
_Avoid_: Generic retry, reopening an Operation, bypassing an Attention Requirement

**Force Override**:
An explicit bypass of PR review policy that never authorizes a Git force-push or bypasses target-identity and revision protections.
_Avoid_: Force-push, state repair

**PR Intent**:
The durable authorization for one `awctl pr` action chain, binding its exact content and repository, branch, remote, and forge targets before mutation begins.
_Avoid_: Latest worktree, mutable submission plan

**Primary Publication Category**:
The one Linear label whose controlled mapping selects the Conventional Commit-style type for a Linear-backed submission: `Feature`, `Bug`, `Chore`, `Docs`, or `Refactor`. Orthogonal labels such as `Research` may coexist but do not select or alter the publication type.
_Avoid_: Task kind, Research modifier, arbitrary Linear label

**Reviewed Publication Metadata**:
The public-safe action summary, description, and validation statement produced and accepted by a Reviewer for one exact Review Target, from which AW deterministically constructs the commit message and forge change request metadata.
_Avoid_: Linear Task title, private review report, agent-authored PR
