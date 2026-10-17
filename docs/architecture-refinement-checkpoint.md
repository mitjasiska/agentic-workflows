# Architecture refinement checkpoint

Last updated: 2026-10-17

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

Current readiness: **Not ready for Initial E2E implementation.** Core product and workflow boundaries are well established, but several integration and persistence contracts still materially affect the first implementation.

## Last completed decision

Question 54 established operation-specific Herdr capability checks. A structured target response identifies the integration version and named capabilities. AW validates only the capabilities mandatory for the requested mutation; a missing capability rejects that Operation with a precise diagnostic and Recovery Plan while unrelated supported operations remain available. Compatibility is not an exact Herdr-version allowlist. Cache timing and wire-level fields remain adapter details unless they affect workflow correctness.

The supplemental publication decision established that exactly one primary Linear category selects the Conventional Commit-style type while `Research` and unrelated labels remain orthogonal. A Reviewer supplies public-safe summary, description, and validation fields from the exact reviewed result. AW then deterministically freezes the shared commit/PR subject, `Task-Review` trailer, and sectioned PR body, retaining both the canonical Linear link and the previously agreed `Fixes TASK-ID` reference. See ADR 0031.

## Next question

Question 55 — Workspace preparation through the gateway:

> Should `PrepareWorkflowWorkspace` be an idempotent, identity-bound request that either creates the intended managed workspace or returns the one already proven to belong to that exact Workflow, while rejecting missing, duplicate, or mismatched ownership evidence instead of guessing or silently adopting an unrelated worktree?

Recommendation: **Yes.** AW should supply the immutable Workflow identity, resolved repository/base/branch intent, and requested managed location. The gateway returns stable target/workspace identifiers plus the canonical path and observed ownership facts. Repeating the same request after an uncertain response may return the same verified workspace without creating another one. Ambiguity or contradictory ownership stops only preparation and exposes recovery; it must not cause silent reuse, deletion, or repair. This preserves safe retry while avoiding the prototype's need for users to repair AW state manually.

## Initial E2E implementation-readiness checklist

Resolve these in roughly this order. Each item may require several one-at-a-time questions; avoid designing deferred v1 features while answering it.

- [x] Finish cleanup, abandonment, retry, resume, and unreachable-target recovery semantics for the initial lifecycle.
- [x] Finalize the minimal Workflow phases, durable outcomes, Operation statuses, and Attention Requirement reason set used by the initial milestone.
- [ ] Specify the initial `IHerdrGateway` contract for local Codex execution, including create/resume/observe/interrupt/result/worktree-teardown behavior and minimum capability negotiation.
- [ ] Specify the v1 universal Result Envelope fields and validation rules needed by Implementer and Reviewer passes.
- [ ] Specify the minimal SQLite current-state, event, snapshot, operation, pass, report, evidence, attention, and submission records plus transaction and migration boundaries.
- [ ] Specify canonical Review Target and Worktree Fingerprint encoding, race-free capture, and deterministic commit construction for the initial Git path.
- [ ] Finalize initial branch/worktree naming and safe path rules.
- [ ] Finalize new-PR creation, retry reconciliation, Linear-label classification, reviewer-produced public metadata, deterministic commit/PR title and PR body generation, closing Linear reference, and minimum safe GitHub/ancestry checks. Preserve the prototype's proven metadata behavior unless an explicit decision below changes it. Updating an existing PR remains the immediate follow-up unless pulled into the milestone deliberately.
- [ ] Specify the project configuration and secret boundaries required for Linear, GitHub, Herdr, repository identity, and the configured Linear In Progress state.
- [ ] Define the Initial E2E acceptance suite and the minimal read-only API/dashboard projection, including how tests exercise both clean review and findings-to-repair routing.

## Explicitly deferred beyond the readiness gate

These matters remain important but do not block the first implementation task unless that task expands to include them:

- Workflow Variant CLI, comparison, winner selection, submission, and cleanup behavior; Variant support is planned for v1 or early v2.
- Pi and additional harnesses beyond the initial Codex path.
- Markdown, pasted-text, and standard-input Task Sources, which follow the Initial E2E Milestone promptly.
- Existing-pull-request update after an additional reviewed batch, unless implementation sequencing pulls it forward.
- GitLab, Jira, authentication/RBAC, PostgreSQL, dashboard mutation controls, scheduling, organization governance, and public branding.
- The full dashboard UI/UX design beyond the minimal read-only lifecycle projection needed by the milestone.

## Session-size estimate

- Remaining initial Herdr gateway contract: approximately 4–6 focused questions, or 20–40 minutes.
- Initial E2E implementation readiness: approximately 15–20 focused questions in total, or 1.5–2.5 hours across one or two sessions.
- Full detailed v1 architecture: approximately 30–50 further decisions, or 4–7 hours across multiple focused sessions.

These are planning estimates, not completeness targets. Stop asking when the readiness checklist is resolved strongly enough to produce an implementation task with clear outcomes and acceptance boundaries; leave reversible code-level choices to Implementation Latitude.

The 2026-10-17 session paused before Question 55 was answered. Question 54 is the last accepted decision; resume with Question 55 exactly as written above.

Before stopping, the user added and settled one required publication-design follow-up based on the prototype. Prototype research is recorded in `docs/research/prototype-validation.md` under **Linear labels and generated publication metadata**. The accepted successor behavior preserves the category mapping, orthogonal `Research` label, reviewed public metadata, deterministic shared commit/PR subject, `Task-Review` trailer, and sectioned PR body while retaining the successor's already-agreed `Fixes TASK-ID` reference. Question 55 remains the next unanswered question.
