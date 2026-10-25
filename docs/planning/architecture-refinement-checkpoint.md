# Architecture refinement checkpoint

Last updated: 2026-10-25

## Purpose and authority

This file is the durable continuation point for architecture grilling and implementation-readiness checks. It prevents a new chat from reconstructing session order from transcripts or guessing which open topic comes next.

It is not a source of product truth. Settled decisions belong in `docs/architecture/architecture.md`, `docs/architecture/workflow-specification.md`, `docs/architecture/e2e-test-kit.md`, `docs/architecture/GLOSSARY.md`, and the applicable ADR under `docs/architecture/adr/`. The broader unresolved inventory remains in `docs/architecture/architecture.md` under **Decisions still requiring refinement**; kit-specific open questions belong in the E2E test-kit architecture. When this checkpoint conflicts with an authoritative document, fix the checkpoint.

Linear is the canonical work queue. The architecture foundation was completed in [DEV-115](https://linear.app/mitjasiska/issue/DEV-115/define-agentic-workflows-architecture-foundation), and the Initial E2E architecture was completed in [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture). This checkpoint records that completed readiness state but does not authorize further refinement or implementation.

## How to continue

From this repository, invoke the installed `grilling` skill or the `grill-with-docs` wrapper and ask:

> Continue the Agentic Workflows architecture refinement from `docs/planning/architecture-refinement-checkpoint.md`. Read the repository instructions and authoritative planning documents it identifies. Ask exactly one focused question at a time, give a candid recommendation, write each accepted decision into the authoritative documents, and keep this checkpoint current.

Identify the currently applicable Linear work item before continuing. Do not resume DEV-119 or start work merely because this checkpoint records prior questions.

At the end of every refinement session, update the date, last completed decision, exact next question, and readiness checklist below. Completion means that another repository-aware agent can resume with the next question without consulting chat history.

## Current objective

Resume with an owner-driven first smoke attempt under [DEV-124](https://linear.app/mitjasiska/issue/DEV-124/decide-the-external-service-e2e-test-kit-architecture), rather than continuing an exhaustive automation interview. The owner wants to run the simple test personally in the next session and refine automation from experience afterward. Keep the agreed existing Aquila installation, dedicated external test targets, reusable manually prepared Linear task, `awctl` control with read-only SQLite/Herdr observations, unmerged PR, successful cleanup sequence, and failed-state retention. Future kit code remains in the AW monorepo, with its dedicated architecture document and shared ADR series. No unattended smoke-run publication authorization was accepted. Architecture refinement produced documentation only, with no kit implementation or live run; publication of these documents is a separately requested external action.

[DEV-125](https://linear.app/mitjasiska/issue/DEV-125/separate-routine-feature-tests-from-live-e2e-acceptance-testing) is delivered on `main` in commit `e2d458d1d2fe09fab52dc5db19dbb683fecd9001`. Its validation boundary is present in the architecture, workflow specification, and repository guidance and is preserved by this E2E companion design. Full Initial E2E acceptance still requires controlled automated coverage and a real-system test through Linear, Herdr/Codex, Git, and GitHub creating a real test pull request. The development smoke-test outline only covers the currently implemented path through review; it is explicitly unexecuted and does not establish full milestone acceptance.

Current readiness for the unattended automated kit: **Architecture decision required**, deliberately deferred. Do not treat those deferred automation decisions as a reason to restart grilling before the owner-driven trial. First check factual prerequisites: the installed AW build must support normal PR submission, the fixture repository/task and expected tiny change must be configured, and ordinary workflows must be idle. The current checkout implements only through review; missing PR capability must be delivered first, not bypassed or reported as a full passing smoke test. Existing product safety, review, explicit submission, cleanup, and Git contribution approvals remain applicable.

## Last completed decision

Stop the questions for this session. The owner will personally run the first minimal smoke test and revisit automation and unattended authorization later. Record what the practical attempt reveals and ask only the focused questions it actually needs. In particular, the proposed one-invocation authorization for test commit, push, PR, and cleanup remains deferred rather than accepted. The E2E architecture and ADRs 0041/0042 now distinguish the initial operator-driven procedure from a future automated runner.

Retain failed-smoke state until explicit operator cleanup: the existing Workflow/history, worktree, local and remote branches, any PR, and current Linear status. Do not reset the reusable task to ready or automatically erase diagnostic state. Runner failure does not imply a stopped agent; preserve accurate active/uncertain observations, and do not reuse a fixture whose retained resources or execution still block another run. ADR 0042 and the E2E architecture record the policy; timeout handling and detailed explicit-cleanup recovery remain open.

Keep the private test repository and accept accumulated closed-PR history. After a successful authorized run, record evidence, close the exact PR without merging, invoke ordinary `awctl cleanup` while the remote branch still preserves committed work, remove the exact remote branch, and finally reset the reusable Linear task to its configured ready state. [ADR 0042](../architecture/adr/0042-reuse-smoke-task-and-separate-external-fixture-cleanup.md) records the choice and the separation of local AW cleanup from external fixture teardown. Detailed partial-cleanup recovery remains open.

Reuse one manually created Linear smoke task instead of automatically creating a new task each run. The task persists; reset changes its status rather than deleting the task or its history. Each run still uses an ordinary AW Workflow and branch and produces a real PR. The earlier `123` filename was an example, not an accepted naming scheme; use AW's actual task-derived Whole Task branch naming.

Use ordinary `awctl` commands as the smoke runner's primary AW control and query surface. The owner confirmed that SQLite state was intended; supporting read-only SQLite observations and Herdr state/pane inspection are allowed. [ADR 0041](../architecture/adr/0041-use-awctl-for-live-e2e-control.md) records this boundary: observations can assert durable facts but cannot write or repair workflow state, mutate AW-owned execution resources behind AW, or replace normal publication. Keep the first smoke minimal and build on it after it works. Deliver AW's normal PR capability before running the complete scenario; that product prerequisite is separate from external publication of this architecture documentation.

Begin with one simple real Linear-to-GitHub-PR smoke test. AW owns the actual workflow, agent passes, independent review, and deterministic publication; the operator initially drives the procedure and checks actual evidence. A repeatable .NET runner remains the later direction, with unattended authorization and detailed behavior deferred. The current lack of `awctl pr` is an implementation dependency, not permission to publish directly or report review-only success as the full smoke result.

Use the existing AW installation on Aquila for initial live tests, including its Linux account, normal service, `awctl`, and SQLite database. Keep dedicated external test targets, require ordinary workflows to be idle during live runs, preserve unrelated records and resources, and explicitly select disruptive scenarios. This supersedes the tentative separate-service direction and the unaccepted separate-account recommendation. [ADR 0040](../architecture/adr/0040-use-existing-installation-for-initial-live-e2e-tests.md) records the accepted trade-off; detailed admission, automation, cleanup, and rerun behavior remain open. No runtime provisioning or live test has occurred during this refinement.

Under DEV-124, use a dedicated [E2E test-kit architecture](../architecture/e2e-test-kit.md), with E2E and product ADRs mixed in the existing `docs/architecture/adr/` directory and one continuous numbering sequence. The companion maintains an E2E-specific decision index; the main architecture retains shared product boundaries and milestone acceptance. Operational instructions belong in Development Documentation.

Keep the E2E-test source in the Agentic Workflows monorepo so tests evolve with the AW version they exercise. Configure a separate Linear test team/project and private GitHub testing repositories. The E2E test-kit architecture owns the accepted meaning. The initial shared installation is now settled above; a run procedure and scenario contract remain open.

Use the prototype's fixed mappings directly in the skill source: Feature/feat, Bug/fix, Chore/chore, Docs/docs, and Refactor/refactor. Research remains a separate modifier. The user identified that AW and the external agent may run on different machines or operating systems and that these mappings rarely change; a later private skill can change its versioned source when needed. This supersedes the optional publication TOML, example, user-versus-project placement, and configuration-discovery design. No active publication settings were created, so there is nothing to migrate, synchronize, or newly ignore. DEV-120 remains documentation, AGENTS.md, and skill work only.

Keep the External Orchestrator and Create Linear Task skills together in AW for now so development can proceed. They may be extracted to a private repository later, but no extraction, new repository, or knowledge-base workflow is part of the current work. This resolves the ownership question raised during DEV-120 without changing the existing package layout.

The external skill adds no independent-review requirement for architecture changes or small routine contributions. The user's chosen workflow and applicable repository rules govern review, with truthful reporting of checks and reviews actually performed. This supersedes the proposed mandatory independent review for external task-linked architecture PRs and the pending taskless-review question. AW-managed review behavior is unchanged. Architecture section 18.3 records the distinction; an external review identifier is not an outstanding prerequisite for this skill.

DEV-120 keeps planning, execution, and publication separately authorized. Architecture discussion, accepted document edits, readiness evaluation, and creation of a Linear task for later `awctl` execution do not authorize starting execution, committing, pushing, or opening a PR. Permission for a route, including taskless direct-to-`main` work, is not authorization to take it. This preserves human control and the distinction between External Orchestrator work and Managed Agent Passes; the clarification is recorded in architecture section 18.3 and workflow specification section 2.1. DEV-120 is the requested home for the instruction-based publication work, with no personal knowledge-base workflow work now.

Source-checkout building, source setup, and control-plane service operation remain separate. The repository owns `build.sh` build, rebuild, and test operations; `setup-source.sh` safely configures and enables the user-scoped service plus PATH launcher without building or starting; and `awctl service start|stop|restart|status|enable|disable` manages only the configured local service. `awctl start TASK-ID` and `awctl status` retain their Workflow-start and application-health meanings. Lifecycle completion is verified, ordinary commands never start the service implicitly, and service stop or restart never becomes Workflow or Herdr-resource cleanup. [ADR 0039](../architecture/adr/0039-separate-source-build-setup-and-service-control.md) records the accepted boundary. Packaged installer and update mechanics remain open.

DEV-123 implementation readiness clarified where the already-settled Herdr gateway guarantees live. `IHerdrGateway` provides AW-semantic idempotency, Operation correlation, reconciliation, and Result Envelope acknowledgement by composing AW's durable pending intent and Operation-scoped result delivery with existing structured Herdr CLI calls and verified Herdr, Git, and filesystem facts. Herdr continues to own workspace, worktree, pane, process, and session mechanics but does not need native AW Workflow or Operation fields, a turn tracker, a result store, or an acknowledgement API. Harness-specific Herdr hooks are optional aids rather than core prerequisites. This preserves the existing ownership and recovery invariants and is recorded as a clarification in the architecture, workflow specification, ADR 0032, and ADR 0034.

DEV-122 refines the Local Control Interface Design Direction to JSON-RPC 2.0 using an established .NET implementation, replacing the custom envelope. The AW application protocol version is carried separately and validated before any application dispatch. Bounded exchanges, same-account local IPC, transport-independent application handlers, and control-plane ownership of accepted Operations remain unchanged. Notifications, batching, subscriptions, streaming, and remote administration are not introduced. The accepted decision is recorded in [ADR 0038](../architecture/adr/0038-adopt-json-rpc-for-local-control.md), the architecture, and the workflow specification; the implemented contract is in [Reference Documentation](../reference/local-control-interface.md). This issue does not reopen broader Initial E2E refinement or authorize other workflow implementation.

The post-DEV-119 local-control refinement separates the two control-plane presentation interfaces. A co-located `awctl` uses bounded, versioned, non-HTTP structured JSON request/reply exchanges over platform-local IPC: a Unix-domain socket on Linux and macOS or a named pipe on Windows, restricted to the operating-system account running the control plane. Mutating requests return after durable acceptance or completion of their immediate action; longer-running Operations remain owned by the control plane and survive CLI disconnection. Pre-stable CLI and service builds accept only their matching current protocol version. The control-plane HTTP API is reserved for the co-hosted React dashboard, may be exposed explicitly on a trusted private network, and is not a promised v1 third-party integration contract. Both adapters invoke the same application commands and queries. Remote Herdr execution remains independent of this local administration topology.

## Next question

No further design question is required tonight. At the next session, first inspect the installed AW build and PR capability; confirm the dedicated private repository, existing Linear smoke task and its tiny expected file change, provider mappings, and idle ordinary workflows. Then help the owner start the task through `awctl`, observe implementation and clean review, explicitly submit using normal AW PR behavior when available, verify the resulting PR, and apply successful cleanup or preserve failed state. A missing prerequisite is an operational fact to resolve, not permission to improvise missing product behavior. Resume automation questions only as the practical attempt exposes a concrete need; one-invocation publication authorization, detailed runner/reporting, and recovery design remain deferred. Do not schedule or start an unattended run from this handoff.

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
