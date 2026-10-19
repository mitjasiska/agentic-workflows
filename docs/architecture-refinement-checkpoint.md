# Architecture refinement checkpoint

Last updated: 2026-10-19

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

Question 73 established Git's native tree representation as the canonical identity of reviewed content. A Review Target records the repository object format and the comparison-baseline and proposed-tree identities rather than an AW-specific per-file hash format. For uncommitted work, AW builds the proposed tree through a private temporary index that does not alter or depend on the user's real staging state. Git therefore defines path, content, deletion, mode, symlink, and supported-submodule semantics. Identical content remains reviewed across staging or commit changes.

## Next question

Question 74 — Stable snapshot capture:

> Should AW start a Reviewer only after it has captured one stable Review Target, automatically retry when relevant files or Git state change during capture, and then stop with a clear ordinary retry action if the workspace will not become stable?

Recommendation: **Yes.** AW should hold its own workspace mutation gate during capture, observe the relevant repository state, build the proposed Git tree in the private index, and observe the state again. A mismatch means the capture is discarded rather than handed to a Reviewer. AW should retry a small bounded number of times to absorb a brief race; if the workspace keeps changing, it should preserve everything, identify what kind of state changed, and tell the user to rerun the ordinary review command when the external activity settles. This cannot lock humans or independent processes out of the filesystem, so the same before-and-after verification still runs around the Review Pass and invalidates its authority if the target changes later.

## Initial E2E implementation-readiness checklist

Resolve these in roughly this order. Each item may require several one-at-a-time questions; avoid designing deferred v1 features while answering it.

- [x] Finish cleanup, abandonment, retry, resume, and unreachable-target recovery semantics for the initial lifecycle.
- [x] Finalize the minimal Workflow phases, durable outcomes, Operation statuses, and Attention Requirement reason set used by the initial milestone.
- [x] Specify the initial `IHerdrGateway` contract for local Codex execution, including create/resume/observe/interrupt/result/worktree-teardown behavior and minimum capability negotiation.
- [x] Specify the v1 universal Result Envelope fields and validation rules needed by Implementer and Reviewer passes.
- [x] Specify the minimal SQLite current-state, event, snapshot, operation, pass, report, evidence, attention, and submission records plus transaction and migration boundaries.
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
- Backward-compatible Result Envelope and SQLite history across upgrades before an explicit stabilization milestone.
- Production backup cadence and retention, off-machine storage, and host-level disaster recovery beyond the provisional Initial E2E SQLite backups.
- GitLab, Jira, authentication/RBAC, PostgreSQL, dashboard mutation controls, scheduling, organization governance, and public branding.
- The full dashboard UI/UX design beyond the minimal read-only lifecycle projection needed by the milestone.

## Session-size estimate

- Review Target, Worktree Fingerprint, and deterministic commit contract: approximately 1–3 focused questions, or 10–25 minutes.
- Initial E2E implementation readiness: approximately 15–20 focused questions in total, or 1.5–2.5 hours across one or two sessions.
- Full detailed v1 architecture: approximately 30–50 further decisions, or 4–7 hours across multiple focused sessions.

These are planning estimates, not completeness targets. Stop asking when the readiness checklist is resolved strongly enough to produce an implementation task with clear outcomes and acceptance boundaries; leave reversible code-level choices to Implementation Latitude.

The 2026-10-19 session accepted Question 73. Resume with Question 74 exactly as written above, keeping the distinction between AW's own mutation gate and external processes that AW can only detect.
