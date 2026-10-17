# Repository instructions

## Start from the repository sources

Read `README.md` before planning or changing this repository. It maps the authoritative planning documents and defines the task-readiness checkpoint.

For architecture, workflow, domain-model, or implementation-task preparation, read the applicable parts of:

- `architecture.md` for product boundaries and architectural constraints;
- `workflow-specification.md` for detailed workflow behavior;
- `GLOSSARY.md` for canonical terminology;
- `docs/adr/` for relevant settled decisions and rationale;
- `docs/research/` only when a decision depends on its evidence.

Do not assume that merely mentioning a future capability in these documents makes it part of the current task.

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

## Keep planning and implementation distinct

Architecture refinement and task preparation do not authorize application implementation. Begin implementation only from an explicit implementation request with a defined outcome and acceptance boundary.
