# Agentic Workflows

The canonical language for the human-directed workflow that coordinates deterministic control with non-deterministic agent work.

## Language

**Agentic Workflows (AW)**:
The product and deterministic control plane described by this domain model.
_Avoid_: AV

**Task**:
The provider-neutral unit of work carried through an Agentic Workflows workflow.
_Avoid_: Issue, ticket

**Task Definition**:
The requirements a workflow is expected to satisfy, together with their source provenance.
_Avoid_: Prompt, pasted task, issue

**Task Source**:
The origin from which a task definition is captured, such as Linear, a Markdown file, or pasted text.
_Avoid_: Issue tracker

**Task Snapshot**:
One immutable version of a Task Definition and its provenance. Every agent pass is bound to exactly one Task Snapshot, while a Workflow may retain several explicitly adopted snapshots.
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
The kind of work through which a Workflow is currently progressing, kept separate from the lifecycle of any concrete Operation or external execution.
_Avoid_: Agent process status, operation status

**Operation Status**:
The durable lifecycle status of one bounded Operation, kept separate from the Workflow Phase and from AW's last observation of the external execution.
_Avoid_: Workflow phase, terminal pane status

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
One bounded Implementer or Reviewer execution started and tracked by AW with a recorded Handoff, session identity, and result.
_Avoid_: Any agent activity, external agent run

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
The single JSON object through which a Managed Agent Pass reports its correlated outcome, detailed narrative, and typed evidence to the Deterministic Workflow. All roles use one versioned schema whose discriminator selects the required role-specific payload.
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
A deterministic identity for the paths, change kinds, modes, and contents of the Uncommitted Worktree Changes at one checkpoint.
_Avoid_: File watcher, timestamp, repository revision

**Reviewed Tree**:
The exact complete file content and modes accepted by a clean review, independent of whether that content is uncommitted or stored in a Git commit.
_Avoid_: Commit SHA, branch head, latest files

**Review Target**:
The exact proposed content and comparison baseline assessed by a Review Pass. Committing the same Reviewed Tree does not change the Review Target.
_Avoid_: Commit SHA, latest worktree, moving branch

**Review Report**:
The durable structured result of a Review Pass, bound to the exact Review Target that was reviewed.
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

**Force Override**:
An explicit bypass of PR review policy that never authorizes a Git force-push or bypasses target-identity and revision protections.
_Avoid_: Force-push, state repair

**PR Intent**:
The durable authorization for one `awctl pr` action chain, binding its exact content and repository, branch, remote, and forge targets before mutation begins.
_Avoid_: Latest worktree, mutable submission plan
