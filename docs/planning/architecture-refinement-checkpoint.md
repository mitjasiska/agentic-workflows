# Architecture refinement checkpoint

Last updated: 2026-10-25

## Purpose and authority

This file is the durable continuation point for architecture grilling and implementation-readiness checks. It prevents a new chat from reconstructing session order from transcripts or guessing which open topic comes next.

It is not a source of product truth. Settled decisions belong in `docs/architecture/architecture.md`, `docs/architecture/workflow-specification.md`, `docs/architecture/GLOSSARY.md`, and the applicable ADR under `docs/architecture/adr/`. The broader unresolved inventory remains in `docs/architecture/architecture.md` under **Decisions still requiring refinement**. When this checkpoint conflicts with an authoritative document, fix the checkpoint.

Linear is the canonical work queue. The architecture foundation was completed in [DEV-115](https://linear.app/mitjasiska/issue/DEV-115/define-agentic-workflows-architecture-foundation), and the Initial E2E architecture was completed in [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture). This checkpoint records that completed readiness state but does not authorize further refinement or implementation.

## How to continue

From this repository, invoke the installed `grilling` skill or the `grill-with-docs` wrapper and ask:

> Continue the Agentic Workflows architecture refinement from `docs/planning/architecture-refinement-checkpoint.md`. Read the repository instructions and authoritative planning documents it identifies. Ask exactly one focused question at a time, give a candid recommendation, write each accepted decision into the authoritative documents, and keep this checkpoint current.

Identify the currently applicable Linear work item before continuing. Do not resume DEV-119 or start work merely because this checkpoint records prior questions.

At the end of every refinement session, update the date, last completed decision, exact next question, and readiness checklist below. Completion means that another repository-aware agent can resume with the next question without consulting chat history.

## Current objective

Refine the user-requested DEV-120 extension for instruction-based External Orchestrator publication, including task-linked and taskless contributions. Keep this work under DEV-120 rather than creating a separate publication task. The user's personal knowledge-base workflow remains deferred. The complete v1 architecture does not need to be finished first. An unanswered matter blocks implementation only when it could materially change the selected task's scope, observable behavior, acceptance criteria, domain ownership, persistence, integration boundaries, sequencing, safety, or recovery.

Current readiness for the instruction-only DEV-120 extension: **Ready.** Use fixed prototype publication conventions in the versioned skills; the external TOML, example, discovery, and synchronization design is removed. Keep both skills in AW for now, with no mandatory external independent review and no automatic execution/publication. Taskless types are proposed from the diff using the same five fixed types. This work remains limited to documentation, AGENTS.md, and skill resources; no `awctl`, service, or other application internals change. Validation of the simplified conventions, final diff review, and delivery remain pending under existing Git approvals. Prior source/service readiness remains settled; packaged installation remains deferred.

## Last completed decision

Use the prototype's fixed mappings directly in the skill source: Feature/feat, Bug/fix, Chore/chore, Docs/docs, and Refactor/refactor. Research remains a separate modifier. The user identified that AW and the external agent may run on different machines or operating systems and that these mappings rarely change; a later private skill can change its versioned source when needed. This supersedes the optional publication TOML, example, user-versus-project placement, and configuration-discovery design. No active publication settings were created, so there is nothing to migrate, synchronize, or newly ignore. DEV-120 remains documentation, AGENTS.md, and skill work only.

Keep the External Orchestrator and Create Linear Task skills together in AW for now so development can proceed. They may be extracted to a private repository later, but no extraction, new repository, or knowledge-base workflow is part of the current work. This resolves the ownership question raised during DEV-120 without changing the existing package layout.

The external skill adds no independent-review requirement for architecture changes or small routine contributions. The user's chosen workflow and applicable repository rules govern review, with truthful reporting of checks and reviews actually performed. This supersedes the proposed mandatory independent review for external task-linked architecture PRs and the pending taskless-review question. AW-managed review behavior is unchanged. Architecture section 18.3 records the distinction; an external review identifier is not an outstanding prerequisite for this skill.

DEV-120 keeps planning, execution, and publication separately authorized. Architecture discussion, accepted document edits, readiness evaluation, and creation of a Linear task for later `awctl` execution do not authorize starting execution, committing, pushing, or opening a PR. Permission for a route, including taskless direct-to-`main` work, is not authorization to take it. This preserves human control and the distinction between External Orchestrator work and Managed Agent Passes; the clarification is recorded in architecture section 18.3 and workflow specification section 2.1. DEV-120 is the requested home for the instruction-based publication work, with no personal knowledge-base workflow work now.

Source-checkout building, source setup, and control-plane service operation remain separate. The repository owns `build.sh` build, rebuild, and test operations; `setup-source.sh` safely configures and enables the user-scoped service plus PATH launcher without building or starting; and `awctl service start|stop|restart|status|enable|disable` manages only the configured local service. `awctl start TASK-ID` and `awctl status` retain their Workflow-start and application-health meanings. Lifecycle completion is verified, ordinary commands never start the service implicitly, and service stop or restart never becomes Workflow or Herdr-resource cleanup. [ADR 0039](../architecture/adr/0039-separate-source-build-setup-and-service-control.md) records the accepted boundary. Packaged installer and update mechanics remain open.

DEV-123 implementation readiness clarified where the already-settled Herdr gateway guarantees live. `IHerdrGateway` provides AW-semantic idempotency, Operation correlation, reconciliation, and Result Envelope acknowledgement by composing AW's durable pending intent and Operation-scoped result delivery with existing structured Herdr CLI calls and verified Herdr, Git, and filesystem facts. Herdr continues to own workspace, worktree, pane, process, and session mechanics but does not need native AW Workflow or Operation fields, a turn tracker, a result store, or an acknowledgement API. Harness-specific Herdr hooks are optional aids rather than core prerequisites. This preserves the existing ownership and recovery invariants and is recorded as a clarification in the architecture, workflow specification, ADR 0032, and ADR 0034.

DEV-122 refines the Local Control Interface Design Direction to JSON-RPC 2.0 using an established .NET implementation, replacing the custom envelope. The AW application protocol version is carried separately and validated before any application dispatch. Bounded exchanges, same-account local IPC, transport-independent application handlers, and control-plane ownership of accepted Operations remain unchanged. Notifications, batching, subscriptions, streaming, and remote administration are not introduced. The accepted decision is recorded in [ADR 0038](../architecture/adr/0038-adopt-json-rpc-for-local-control.md), the architecture, and the workflow specification; the implemented contract is in [Reference Documentation](../reference/local-control-interface.md). This issue does not reopen broader Initial E2E refinement or authorize other workflow implementation.

The post-DEV-119 local-control refinement separates the two control-plane presentation interfaces. A co-located `awctl` uses bounded, versioned, non-HTTP structured JSON request/reply exchanges over platform-local IPC: a Unix-domain socket on Linux and macOS or a named pipe on Windows, restricted to the operating-system account running the control plane. Mutating requests return after durable acceptance or completion of their immediate action; longer-running Operations remain owned by the control plane and survive CLI disconnection. Pre-stable CLI and service builds accept only their matching current protocol version. The control-plane HTTP API is reserved for the co-hosted React dashboard, may be exposed explicitly on a trusted private network, and is not a promised v1 third-party integration contract. Both adapters invoke the same application commands and queries. Remote Herdr execution remains independent of this local administration topology.

## Next question

None for the agreed instruction-only DEV-120 scope. Validate the fixed prototype conventions and taskless/publication boundaries, reconcile the Linear issue to remove the superseded TOML requirement, and present the final diff for review. Commit, push, PR creation, and installation refresh retain their separate authorization boundaries. Keep extraction, personal knowledge-base workflow, external configuration, custom templates, and AW runtime changes outside this task.

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
- [x] Define the Initial E2E acceptance boundary and coverage obligations and the minimal read-only API/dashboard projection, including coverage of both clean review and findings-to-repair routing. The detailed live-test procedure and test cases remain open, as recorded in [architecture section 8.1](../architecture/architecture.md#81-initial-e2e-milestone); this check does not mark them specified or milestone acceptance achieved.
- [x] Separate the co-located `awctl` Local Control Interface from the dashboard HTTP API, including cross-platform local IPC, single-operator access, bounded operation-independent exchanges, and pre-stable protocol compatibility.
- [x] Refine the Local Control Interface to JSON-RPC 2.0 with separate pre-dispatch application compatibility checks (DEV-122), preserving the local-only boundary and bounded exchanges.

## Explicitly deferred beyond the readiness gate

These matters remain important but do not block the first implementation task unless that task expands to include them:

- External-service E2E test-kit architecture under [DEV-124](https://linear.app/mitjasiska/issue/DEV-124/decide-the-external-service-e2e-test-kit-architecture), and the detailed live-test procedure and test cases requiring separate owner specification. DEV-125 clarifies the routine-pass boundary only; complete milestone acceptance still requires the separately scoped, operator-authorized live proof described in architecture section 8.1.
- Workflow Variant CLI, comparison, winner selection, submission, and cleanup behavior; Variant support is planned for v1 or early v2.
- Pi and additional harnesses beyond the initial Codex path.
- Markdown, pasted-text, and standard-input Task Sources, which follow the Initial E2E Milestone promptly.
- Existing-pull-request update after an additional reviewed batch; this is the immediate post-milestone submission expansion.
- Agent-assisted base-conflict integration through `awctl integrate` and draft pull-request submission.
- Backward-compatible Result Envelope and SQLite history across upgrades before an explicit stabilization milestone.
- Production backup cadence and retention, off-machine storage, and host-level disaster recovery beyond the provisional Initial E2E SQLite backups.
- GitLab, Jira, authentication/RBAC, PostgreSQL, dashboard mutation controls, scheduling, organization governance, and public branding.
- The full dashboard UI/UX design beyond the minimal read-only lifecycle projection needed by the milestone.
- Windows and macOS packaging, service hosting, and their platform adapters; the architecture is cross-platform, while the Initial E2E implementation and official v1 support remain Linux-first.
- Packaged Linux installation, updates, distribution, and migration beyond the settled build-from-source path.

## Session-size estimate

- Initial E2E implementation readiness: complete; no architecture question remains before implementation-task preparation.
- Full detailed v1 architecture after that gate: approximately 25–40 further decisions, or 3–6 hours across several focused sessions.

These are planning estimates, not completeness targets. Stop asking when the readiness checklist is resolved strongly enough to produce an implementation task with clear outcomes and acceptance boundaries; leave reversible code-level choices to Implementation Latitude.

The 2026-10-21 pre-implementation audit reopened the readiness gate for a small set of concrete consistency gaps. Questions 89 through 96 resolved general Workflow continuation, shared Task ownership, configuration authority and precedence, stable external identity behavior, deterministic metadata for safe local commits, provisional forced-submission phase behavior, the Initial E2E new-pull-request boundary, and complete local branch cleanup. A 2026-10-22 owner-directed refinement then replaced CLI-over-HTTP and remote CLI endpoint configuration with the platform-local Local Control Interface while retaining dashboard HTTP and remote Herdr execution. The gate is closed; implementation still begins only from a separate Linear implementation issue with a defined outcome and acceptance boundary.

The first DEV-123 readiness pass on 2026-10-24 exposed wording that could be read as requiring Herdr-native workflow idempotency, Operation correlation, result retention, acknowledgement, and Codex session-hook support. The clarified authority keeps those semantics in AW's gateway composition and durable result delivery without weakening conservative reconciliation or changing Herdr's ownership of execution resources.
