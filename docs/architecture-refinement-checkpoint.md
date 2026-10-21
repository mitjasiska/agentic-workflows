# Architecture refinement checkpoint

Last updated: 2026-10-21

## Purpose and authority

This file is the durable continuation point for architecture grilling and implementation-readiness checks. It prevents a new chat from reconstructing session order from transcripts or guessing which open topic comes next.

It is not a source of product truth. Settled decisions belong in `architecture.md`, `workflow-specification.md`, `GLOSSARY.md`, and the applicable ADR. The broader unresolved inventory remains in `architecture.md` under **Decisions still requiring refinement**. When this checkpoint conflicts with an authoritative document, fix the checkpoint.

Linear is the canonical work queue. The architecture foundation is completed in [DEV-115](https://linear.app/mitjasiska/issue/DEV-115/define-agentic-workflows-architecture-foundation). Continue the remaining readiness work under [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture-readiness); this checkpoint supplies that issue's exact local continuation point but does not replace it.

## How to continue

From this repository, invoke the installed `grilling` skill or the `grill-with-docs` wrapper and ask:

> Continue the Agentic Workflows architecture refinement from `docs/architecture-refinement-checkpoint.md`. Read the repository instructions and authoritative planning documents it identifies. Ask exactly one focused question at a time, give a candid recommendation, write each accepted decision into the authoritative documents, and keep this checkpoint current.

Confirm that DEV-119 is still the applicable Linear work item before continuing. If it has been completed, canceled, or superseded, follow the current Linear queue instead of starting work merely because this checkpoint contains another question.

At the end of every refinement session, update the date, last completed decision, exact next question, and readiness checklist below. Completion means that another repository-aware agent can resume with the next question without consulting chat history.

## Current objective

Reach a sufficiently specified Initial E2E Milestone to prepare and start its implementation task. The complete v1 architecture does not need to be finished first. An unanswered matter blocks implementation only when it could materially change the milestone's scope, observable behavior, acceptance criteria, domain ownership, persistence, integration boundaries, sequencing, safety, or recovery.

Current readiness: **Ready for Initial E2E implementation-task preparation.** The original readiness grilling and the targeted pre-implementation consistency audit are complete. Broader v1 decisions remain deferred and do not block this milestone.

## Last completed decision

Question 96 makes cleanup the complete local recovery path for reusing a Task and scope. Ordinary cleanup removes the exact local Workflow branch only when no unique local work would be lost; `awctl cleanup --force` deletes that exact verified branch even if local-only work is discarded. Neither form changes a remote branch, pull request, or Task, so a remaining live remote branch continues to block `awctl start`.

## Next question

None. The Initial E2E architecture is ready for implementation-task preparation. Per `README.md`, the next work-queue action is to create the separate Initial E2E implementation issue in Linear and only then complete DEV-119.

## Initial E2E implementation-readiness checklist

Resolve these in roughly this order. Each item may require several one-at-a-time questions; avoid designing deferred v1 features while answering it.

- [x] Finish cleanup, abandonment, retry, resume, and unreachable-target recovery semantics for the initial lifecycle.
- [x] Finalize the minimal Workflow phases, durable outcomes, Operation statuses, and Attention Requirement reason set used by the initial milestone.
- [x] Specify the initial `IHerdrGateway` contract for local Codex execution, including create/resume/observe/interrupt/result/worktree-teardown behavior and minimum capability negotiation.
- [x] Specify the v1 universal Result Envelope fields and validation rules needed by Implementer and Reviewer passes.
- [x] Specify the minimal SQLite current-state, event, snapshot, operation, pass, report, evidence, attention, and submission records plus transaction and migration boundaries.
- [x] Specify canonical Review Target and Worktree Fingerprint encoding, race-free capture, and deterministic commit construction for the initial Git path.
- [x] Finalize initial branch/worktree naming and safe path rules.
- [x] Finalize new-PR creation, retry reconciliation, Linear-label classification, reviewer-produced public metadata, deterministic commit/PR title and PR body generation, closing Linear reference, and minimum safe GitHub/ancestry checks. Preserve the prototype's proven metadata behavior unless an explicit decision below changes it. Updating an existing PR is explicitly the immediate post-milestone follow-up.
- [x] Specify the project configuration and secret boundaries required for Linear, GitHub, Herdr, repository identity, and the configured Linear In Progress state.
- [x] Define the Initial E2E acceptance suite and the minimal read-only API/dashboard projection, including how tests exercise both clean review and findings-to-repair routing.

## Explicitly deferred beyond the readiness gate

These matters remain important but do not block the first implementation task unless that task expands to include them:

- Workflow Variant CLI, comparison, winner selection, submission, and cleanup behavior; Variant support is planned for v1 or early v2.
- Pi and additional harnesses beyond the initial Codex path.
- Markdown, pasted-text, and standard-input Task Sources, which follow the Initial E2E Milestone promptly.
- Existing-pull-request update after an additional reviewed batch; this is the immediate post-milestone submission expansion.
- Agent-assisted base-conflict integration through `awctl integrate` and draft pull-request submission.
- Backward-compatible Result Envelope and SQLite history across upgrades before an explicit stabilization milestone.
- Production backup cadence and retention, off-machine storage, and host-level disaster recovery beyond the provisional Initial E2E SQLite backups.
- GitLab, Jira, authentication/RBAC, PostgreSQL, dashboard mutation controls, scheduling, organization governance, and public branding.
- The full dashboard UI/UX design beyond the minimal read-only lifecycle projection needed by the milestone.

## Session-size estimate

- Initial E2E implementation readiness: complete; no architecture question remains before implementation-task preparation.
- Full detailed v1 architecture after that gate: approximately 25–40 further decisions, or 3–6 hours across several focused sessions.

These are planning estimates, not completeness targets. Stop asking when the readiness checklist is resolved strongly enough to produce an implementation task with clear outcomes and acceptance boundaries; leave reversible code-level choices to Implementation Latitude.

The 2026-10-21 pre-implementation audit reopened the readiness gate for a small set of concrete consistency gaps. Questions 89 through 96 resolved general Workflow continuation, shared Task ownership, configuration authority and precedence, stable external identity behavior, deterministic metadata for safe local commits, provisional forced-submission phase behavior, the Initial E2E new-pull-request boundary, and complete local branch cleanup. The gate is closed again; implementation still begins only from the separate Linear implementation issue required by `README.md`.
