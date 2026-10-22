# Repository instructions

## Use Linear as the work queue

Linear is the canonical source for what work should be performed next. Repository documents are authoritative for product decisions, constraints, and detailed design, but they do not create work by themselves. Begin architecture refinement or implementation from the applicable Linear issue, and use the repository sources to interpret and execute that issue. Do not turn an open item in an architecture document into an unrequested task.

For architecture refinement, task preparation, work-queue coordination, or deciding next actions outside an AW-managed Implementer or Reviewer pass, use the `external-orchestrator` skill.

The current architecture-refinement continuation is tracked by [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture-readiness). The local checkpoint records the exact question at which that issue should resume.

## Start from the repository sources

Read `README.md` before planning or changing this repository. It maps the authoritative planning documents and defines the task-readiness checkpoint.

For architecture, workflow, domain-model, or implementation-task preparation, read the applicable parts of:

- `docs/architecture/architecture.md` for product boundaries and architectural constraints;
- `docs/architecture/workflow-specification.md` for detailed workflow behavior;
- `docs/architecture/GLOSSARY.md` for canonical terminology;
- `docs/planning/architecture-refinement-checkpoint.md` for the current architecture-grilling position, next question, and implementation-readiness gate;
- `docs/architecture/adr/` for relevant settled decisions and rationale;
- `docs/research/` only when a decision depends on its evidence.

For architecture grilling, continuation in a new chat, or deciding whether implementation may begin, read the checkpoint completely after the authoritative documents it names. Treat it as a routing record rather than a decision source: settled behavior remains authoritative only in the architecture, workflow specification, glossary, and ADRs. Update the checkpoint before ending a refinement session so its next question and readiness state remain current.

Do not assume that merely mentioning a future capability in these documents makes it part of the current task.

## Maintain documentation by audience and authority

`README.md` is the concise human landing page and the repository's single documentation index. Keep detailed material under `docs/` and do not create a second documentation README.

Use these documentation categories:

- **Architecture Documentation** belongs under `docs/architecture/` and contains the complete architecture, workflow specification, glossary, and ADRs. It is authoritative for product decisions and must remain human-readable.
- **Product Documentation** belongs under `docs/user/` and explains installation, configuration, usage, and recovery for users.
- **Development Documentation** belongs under `docs/development/` and explains local setup, building, testing, and contribution workflows.
- **Reference Documentation** belongs under `docs/reference/` and contains schemas, contracts, and other precise lookup material.
- **Planning and research material** belongs under `docs/planning/` and `docs/research/`. It supports decisions but is not authoritative product truth by itself.

Create product, development, and reference sections only when real content exists. Do not leave product decisions solely in `AGENTS.md`, a checkpoint, a task, a research note, or a chat transcript; reconcile them into the applicable authoritative Architecture Documentation.

## Interpret repository guidance by status

The current task instructions own the immediate outcome, scope, and acceptance criteria. Repository architecture owns the constraints within which that outcome must fit. Architecture documents provide context and boundaries; they are not authorization to implement unrelated roadmap items.

Classify applicable Repository Guidance as follows:

- A **Product Invariant** is settled behavior or a boundary. Respect it. If the task conflicts with it, stop and identify the exact conflict; do not invent a compromise.
- A **Design Direction** is a preferred shape or default. Refine it when the actual code reveals a more coherent solution, provided the result preserves the task's intent and Product Invariants. Report the refinement and its evidence.
- An **Open Area** is explicitly undecided or deferred. Make reversible local choices using Implementation Latitude, but stop for a focused architecture decision before silently settling a material product question.

When a statement's status is ambiguous, infer it from explicit language and context only when the consequence is reversible. If the ambiguity could materially change the task, treat it as a documentation defect and resolve it first.

Trace every implementation change to the current task. Use the architecture to select a compatible solution, not to expand the requested outcome. Preserve **Implementation Latitude** for reversible technical choices that the task and architecture intentionally leave open.

## Enforce task readiness proportionately

Before implementation, check whether an unanswered question could materially change scope, observable behavior, acceptance criteria, domain ownership, persistence, integration boundaries, sequencing dependencies, safety, or recovery.

- When such a material decision is missing, stop task preparation or implementation and request a focused architecture decision. Write the resulting decision into the appropriate authoritative document before proceeding.
- When the open choice is a reversible implementation detail, proceed using Implementation Latitude and report the choice and relevant evidence.

Do not demand exhaustive design before work begins. Prefer cohesive, outcome-oriented tasks, and avoid turning a capable Implementer into an executor of a mechanical edit script.

Do not implement an ad hoc workaround merely to follow a Design Direction literally. Choose the coherent solution that best preserves the documented intent and Product Invariants. If that solution would change an invariant or settle a material Open Area, propose the smallest architecture change and wait for it to be decided.

## Keep architecture and implementation synchronized

Implementation may reveal evidence that the documentation should change, but editing a document does not by itself authorize a product decision. Handle documentation impact according to the status of the affected guidance:

- **Implementation Latitude:** Make the reversible technical choice and report it with relevant evidence. Do not elevate ordinary table, class, API, algorithm, or tooling choices into product architecture unless they expose a durable boundary or constraint.
- **Clarification:** An Implementer may correct or sharpen an authoritative document in the same change when the edit preserves the already-settled meaning. Identify the clarification explicitly in the implementation report.
- **Design Direction refinement:** An Implementer may update the applicable authoritative document when code evidence supports a more coherent solution that preserves the task intent and every Product Invariant. Explain the evidence, the refinement, and its consequences for review.
- **Product Invariant or material Open Area:** Stop implementation, identify the exact decision, and propose the smallest architecture change. Wait for explicit acceptance, write the accepted decision into the appropriate authoritative document, and only then continue. Editing the architecture alongside conflicting code is not approval and cannot retroactively legitimize the implementation.

Every implementation handoff or final report must include an **Architecture impact** statement using one of these outcomes:

- `No architecture change`
- `Architecture clarified` — name the authoritative files and the preserved meaning
- `Design direction refined` — name the files and summarize the implementation evidence
- `Architecture decision required` — state the unresolved decision and stop the affected work

Every implementation handoff or final report must also include a **Product/development documentation impact** statement. Name the human-facing or developer-facing files updated, or state `No product/development documentation change`. A task is incomplete while relevant usage, configuration, operation, recovery, setup, testing, or contribution documentation is knowingly stale.

Reviewers must assess both the current task and the applicable Repository Guidance. They must verify that reported clarifications and refinements preserve Product Invariants, identify undocumented architectural drift, and reject material product changes that lack an explicit accepted decision. A documentation edit from the Implementer is evidence to review, not authority to change the product.

Linear issues should contain task-specific outcomes, scope, exclusions, acceptance criteria, and relevant architecture references. Do not duplicate this standing protocol in every issue; record an exception only when the task deliberately requires different handling.

## Keep planning and implementation distinct

Architecture refinement and task preparation do not authorize application implementation. Begin implementation only from an explicit implementation request with a defined outcome and acceptance boundary.
