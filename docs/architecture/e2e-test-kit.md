# External-service E2E test-kit architecture

> Architecture refinement under [DEV-124](https://linear.app/mitjasiska/issue/DEV-124/decide-the-external-service-e2e-test-kit-architecture). The owner will personally run the first minimal smoke test, learn from it, and revisit automation afterward. The agreed environment, `awctl` control and read-only observations, reusable task, successful cleanup, and failed-run retention remain in force. Unattended authorization and detailed runner design are deferred.

## Purpose and authority

This document owns the accepted architecture of the external-service E2E test kit. It describes how the kit is organized and, as further decisions are accepted, how it controls live execution, exercises AW, records evidence, and manages test resources within the selected shared installation.

The [main architecture](architecture.md) owns shared product boundaries and Initial E2E milestone acceptance. The [workflow specification](workflow-specification.md) owns the behavior the kit exercises. This companion applies those rules without redefining them. Statements use the same [Product Invariant, Design Direction, and Open Area classifications](architecture.md#how-to-read-this-document).

Practical setup and running instructions belong in Development Documentation. The existing [managed-workflow smoke-test outline](../development/managed-workflow-smoke-test.md) describes an unexecuted procedure for the implemented path through independent review; it is not a completed procedure for full milestone acceptance.

## Accepted design

### Source ownership

The E2E test source lives in the Agentic Workflows monorepo and evolves with the AW version it tests. A change to an AW command or contract can therefore update the corresponding tests in the same repository change. The exact project and directory layout remains Implementation Latitude within the repository's established .NET stack.

### External test targets

Live scenarios use a separately configured Linear test team and project and private GitHub testing repositories. Keeping test code in the monorepo does not place these external resources or raw credentials in the repository. The existing credential and stable-provider-identity rules apply; target selection and access do not become another source of AW workflow authority.

The first scenario uses one manually created Linear smoke task that remains in the configured test team/project for reuse. The runner does not automatically create or delete this task. Its concrete provider identity is configured; the task supplies the tiny fixed file change for each run. Exact file names and contents belong to that fixture rather than an arbitrary example run-number convention.

The exact resource identities, repository provisioning procedure, configuration surface, credential references, and fixture-ready Linear status remain open. A status such as To Do is selected by its concrete team-valid identity, not by assuming every team uses that English display name.

### Documentation and decision records

Kit-specific current architecture is maintained in this document, with links from the main architecture and the root README. The root README remains the repository's single documentation index.

E2E-related ADRs and product ADRs share the existing `docs/architecture/adr/` directory and one continuous numbering sequence. There is no separate E2E ADR directory or numbering scheme. Each record has a descriptive title; the decision index below provides an E2E reading path through the shared series.

ADRs explain consequential accepted choices, the alternatives considered, and their rationale. This document describes the resulting current design. A new ADR is created when a decision warrants a durable trade-off record, rather than for every interview answer or editorial choice.

## Relevant decision index

This index includes shared product constraints and accepted kit-specific decisions from the same ADR series.

| Decision | Relevance to the E2E kit |
| --- | --- |
| [ADR 0030: AW cleanup scope](adr/0030-make-abandonment-resumable-and-support-forced-cleanup.md) | AW cleans its exact local Workflow resources; external fixture teardown remains a runner responsibility. |
| [ADR 0032: identity-bound workspace preparation](adr/0032-make-workspace-preparation-idempotent-and-identity-bound.md) | Workspace ownership and idempotent preparation remain required. |
| [ADR 0034: result recovery and resource ownership](adr/0034-recover-completed-results-without-adopting-external-herdr-resources.md) | Correlated results survive restart, and unproven external resources remain untouched. |
| [ADR 0035: secret boundaries](adr/0035-keep-raw-secrets-out-of-aw-configuration-and-state.md) | Configuration and evidence must not contain raw credentials. |
| [ADR 0036: configuration and stable identities](adr/0036-keep-settings-files-authoritative-and-bind-stable-provider-identities.md) | Separate test targets still follow ordinary AW configuration and binding rules. |
| [ADR 0039: source setup and service control](adr/0039-separate-source-build-setup-and-service-control.md) | Runtime and lifecycle testing must respect the existing service-management boundary. |
| [ADR 0040: reuse the existing installation](adr/0040-use-existing-installation-for-initial-live-e2e-tests.md) | Initial live tests share the Aquila installation and run while ordinary workflows are idle. |
| [ADR 0041: CLI control and read-only observations](adr/0041-use-awctl-for-live-e2e-control.md) | Drive AW through `awctl`; inspect SQLite and Herdr only as supporting read-only evidence. |
| [ADR 0042: reusable fixture and cleanup ownership](adr/0042-reuse-smoke-task-and-separate-external-fixture-cleanup.md) | Keep the task and repository; close test PRs, clean exact test branches, and reset Linear after successful cleanup. |

## Initial live environment

Initial live runs use the existing AW installation on Aquila: the same Linux account, user service, ordinary `awctl` launcher, configuration environment, and SQLite database used for everyday work. The separate Linear test team/project and private GitHub test repositories remain the test targets. This replaces the earlier proposal for a second test service or dedicated account.

Ordinary workflows must be idle before a live run and remain idle throughout it. Retained ordinary workflows and historical evidence may coexist with test workflows in the database; the idle requirement does not authorize their deletion or abandonment. Exact admission checks and how the operator maintains this idle period remain part of the open run-control design.

Tests share the service and application state rather than an isolated test installation. A restart or crash scenario therefore affects the everyday service. Disruptive scenarios require explicit selection; unselected or skipped scenarios remain visibly unverified. Test cleanup must identify exact test-owned workflows and resources and preserve unrelated records, workspaces, and external resources. The detailed retention, cleanup, and rerun rules remain open.

[ADR 0040](adr/0040-use-existing-installation-for-initial-live-e2e-tests.md) records the simplicity-versus-isolation trade-off. Separate environments remain a possible later extension when concurrent ordinary work or broader disruptive scenarios justify them; they are not part of the initial runtime setup.

## First smoke scenario and automation direction

The owner will personally run the first minimal smoke test using the existing AW installation and `awctl`, exercising the agreed path before refining a full automation design. An unattended runner and blanket advance authorization are not prerequisites for this initial trial. Submission and cleanup are explicit operator actions under the ordinary product and repository rules.

A repeatable automated .NET runner remains the intended later direction. It will coordinate scenario actions, verify outcomes, and record actual results while AW remains the workflow authority. Its exact invocation, unattended authorization, report format, and detailed recovery behavior will be refined from the initial run rather than exhaustively specified first. Required human decisions and unavailable dependencies remain visible stops rather than guessed success.

The first scenario is one simple smoke test from a real Linear task to a real GitHub pull request in the configured private test repository. It exercises actual AW task capture, Herdr/Codex implementation, independent review, and the ordinary deterministic commit, push, and PR path. The PR remains unmerged. No deliberate defect, forced submission, crash, or restart is needed to define this first scenario; those are separate test scenarios.

AW continues to own workspace preparation, managed passes, review eligibility, and publication. Both the operator and future runner use ordinary `awctl` commands to drive and query AW; direct provider access serves fixture validation, reset, and independent verification of external results. Neither direct database writes, synthetic success results, nor substitute Git/forge publication may stand in for the AW behavior being tested. The initial Linear task is manually provisioned and reused; concrete fixture setup is checked before the trial.

The CLI control boundary allows additional read-only observations of SQLite state and Herdr state or panes. These may supply assertions or diagnostics where needed, while AW's correlated results and durable workflow records retain their existing meaning. Database observation must use read-only access and must not repair data or change schema; version-coupled test queries do not become a public SQLite integration contract. Supporting observations do not permit the runner to mutate Herdr resources behind AW, manufacture a successful pass, or bypass normal submission. [ADR 0041](adr/0041-use-awctl-for-live-e2e-control.md) records this boundary. The first smoke test stays minimal; broader inspection and additional scenarios can be added from concrete evidence after that path works.

The expected proof is a correlated Linear task, real implementation and clean independent review, and a real PR containing the expected reviewed change in the correct repository and branch target. A command exit code or a PR URL alone is not sufficient evidence. The operator checks the tiny change described by the reusable fixture task and records the Workflow identity, PR URL, observed result, and any limitation. Detailed automated assertions can follow the trial.

Submission remains explicitly authorized by the operator through the normal product path. The question of whether one future automated-run invocation grants advance authorization for commit, push, PR creation, and cleanup is deferred; it was not accepted during this session. Managed Implementers and Reviewers do not acquire publication authority through the test.

The current implementation described by the [repository status](../../README.md#current-status) stops at independent review and does not yet implement `awctl pr`. Complete the product's normal PR-submission capability before running the first complete smoke scenario. Reaching clean review can supply partial evidence but cannot be reported as a passing Linear-to-PR run, and the kit must not bypass missing product functionality. This one smoke scenario does not replace the broader controlled coverage or dashboard requirements of the full Initial E2E Milestone.

## Reusable fixture and cleanup

Each run still creates an ordinary AW Workflow, its own worktree and branch, and a real PR. The reusable Linear issue is not the branch or the Workflow. For the initial Whole Task path, AW chooses `aw/<TASK-ID>/whole` from the actual configured issue key under the existing workflow rules. No additional run-number branch naming scheme is required. A later run can reuse that branch name only after the preceding Workflow and its exact local and remote branch have been cleared.

Cleanup after a successful authorized smoke run removes the test branch and restores the existing Linear task to a configured ready state, initially intended to be To Do. This resets the fixture for another run; it does not delete the task or erase its provider history. The exact team-valid state identity remains open.

AW's existing cleanup boundary is unchanged: `awctl cleanup` owns cleanup of the exact AW Workflow, its owned Herdr resources and worktree, the verified local branch, and Workflow-owned database records. It does not close GitHub PRs, delete remote Git branches, or reset Linear. The operator initially performs external fixture teardown; a future runner may automate it after its authorization is specified. Those GitHub and Linear actions remain separate and use the exact identities observed for the test run. [ADR 0030](adr/0030-make-abandonment-resumable-and-support-forced-cleanup.md) remains authoritative for ordinary and forced AW cleanup.

The accepted PR end state follows GitHub's normal procedure: [close the PR without merging](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/closing-a-pull-request) and [delete its branch](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-branches-in-your-repository/deleting-and-restoring-branches-in-a-pull-request). The closed PR remains in history. The initial kit keeps the private test repository rather than deleting or replacing it to remove PR history.

The successful-run sequence is:

1. Record the smoke result and necessary evidence outside the Workflow-owned state that cleanup will remove.
2. Close the exact test PR without merging it.
3. Perform ordinary `awctl cleanup` for the exact test Workflow while the published branch still makes its committed work recoverable. This removes its local resources and branch under normal AW ownership checks.
4. Delete the exact remote test branch after verifying its identity and expected revision.
5. Reset the same Linear task to its configured ready status only after preceding cleanup is confirmed.

Clearing the remote branch before ordinary AW cleanup could leave unique local commits and require forced disposal; forced cleanup is not a default. Partial cleanup is reported truthfully and does not make the fixture ready for another run. Detailed recovery after partial cleanup remains open. [ADR 0042](adr/0042-reuse-smoke-task-and-separate-external-fixture-cleanup.md) records the accepted lifecycle and ownership boundary.

### Failed smoke runs

If the smoke test fails, retain its existing AW Workflow and history, worktree, local and remote branches, any created PR, and the Linear task's current state for inspection. Record the failure and available evidence; do not automatically apply the successful-run cleanup sequence or reset Linear to the ready state. Cleanup of a failed run requires an explicit operator action under the same exact-resource ownership rules.

A failed or exited runner does not prove that an AW-managed agent stopped. Preserve the last verified execution observations and identify any active or uncertain execution in the diagnostic result rather than inventing a stopped state. Do not start another smoke run on the same fixture while the retained Workflow, branches, or unresolved execution still prevent safe reuse. Detailed timeout, cancellation, explicit-cleanup invocation, and recovery behavior remain open.

## Starting the first trial

On the next session, check the installed AW build and normal PR-submission capability first. Then verify the dedicated test repository, manually prepared Linear task and its tiny expected change, configured provider mappings, and idle ordinary workflows. Start the task through `awctl`, observe real implementation and independent review, explicitly submit through the normal AW PR path when eligible, and verify the resulting PR. Apply the agreed successful cleanup or retain failed state for diagnosis. Missing prerequisites or new observations determine any further focused questions.

## Deferred automation decisions

The following matters remain open for the future automated kit. They do not require a further design interview before checking the prerequisites for the owner-driven initial trial:

- Runner invocation, unattended submission authorization, automated idle-workflow admission, target validation, and behavior if ordinary work starts during a run.
- Exact fixture configuration, status mapping, automated assertions, and selection of supporting read-only observations.
- Build provenance, dirty-source-checkout handling, and generated run artifacts.
- Run identity, report persistence, timeout, cancellation, explicit cleanup, and recovery after test failure or partial cleanup.

Resume these questions after the first practical smoke attempt as concrete needs arise. The current product submission prerequisite and the full milestone acceptance requirements are unchanged.
