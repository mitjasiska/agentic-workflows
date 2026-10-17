# Agentic Workflows

This repository contains the architecture and, later, the implementation of Agentic Workflows. The current directory name is historical; the product is not named “Lite.”

## Authoritative planning documents

- [`AGENTS.md`](AGENTS.md) is the repository instruction router and task-readiness guardrail; Codex loads it automatically, while other planning and agent workflows must load it explicitly. It points agents to the applicable authoritative sources without duplicating them.
- [`architecture.md`](architecture.md) defines the product boundary, architectural shape, major behavior, and open decisions.
- [`workflow-specification.md`](workflow-specification.md) defines detailed workflow mechanics and edge cases.
- [`GLOSSARY.md`](GLOSSARY.md) defines canonical domain language.
- [`docs/architecture-refinement-checkpoint.md`](docs/architecture-refinement-checkpoint.md) records where architecture grilling should resume and which decisions still block implementation. It is a navigation checkpoint, not an additional source of product truth.
- [`docs/adr/`](docs/adr/) records durable architectural decisions and their rationale.
- [`docs/research/`](docs/research/) contains evidence used to inform decisions, including findings from the prototype.

Chat transcripts are working conversations, not authoritative project state. Decisions that must survive the conversation belong in the applicable repository document.

Linear is the canonical work queue: it determines what should be worked on next. The repository documents remain authoritative for the product decisions and constraints used to execute that work. The completed architecture-foundation task is [DEV-115](https://linear.app/mitjasiska/issue/DEV-115/define-agentic-workflows-architecture-foundation); the current continuation is [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture-readiness).

The planning documents distinguish three kinds of guidance:

- **Product invariants** are settled product behavior and boundaries. Implementation must respect them unless the architecture is changed explicitly.
- **Design directions** express the preferred shape or default, while leaving room for a coherent implementation to refine the approach and record why.
- **Open areas** are deliberately unresolved. Implementers may make reversible local choices, but a material product decision requires focused architecture refinement before implementation continues.

When a statement's category is unclear and the distinction could materially change the task, treat that ambiguity as a documentation defect to resolve rather than guessing. These categories do not expand a task's scope: the task still owns the requested outcome.

## Architecture-document workflow

The product owner may update and commit architecture documentation directly to the main branch without a pull request. This is a deliberate low-friction workflow for planning artifacts; it does not establish the Git policy for application implementation.

Architecture work may use Codex against the local repository or ChatGPT against the current GitHub repository. The selected environment must be able to read the current authoritative documents. Local uncommitted decisions must be committed and pushed, attached explicitly, or otherwise synchronized before a GitHub-connected chat can rely on them.

### Continuing the current architecture refinement

[DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture-readiness) remains open across as many focused sessions as are needed to reach Initial E2E implementation readiness. Do not create a new refinement issue for each chat. At the start of a session:

1. Open this repository at its latest architecture commit and confirm that DEV-119 is still the applicable Linear issue.
2. In Codex, invoke the installed `grilling` skill or `grill-with-docs` wrapper and say: “Continue DEV-119 from `docs/architecture-refinement-checkpoint.md`. Ask one question at a time.”
3. In a GitHub-connected ChatGPT chat, use the same request only after the latest local documentation changes have been committed and pushed so that the chat can read them.

During the session, write each accepted decision into the applicable authoritative document and update the checkpoint's last completed decision and exact next question. When stopping before readiness, leave DEV-119 open and leave the repository changes ready for the user's direct-to-main documentation commit. When the readiness checklist is complete, mark the checkpoint ready, create the separate Initial E2E implementation issue in Linear, and only then complete DEV-119.

## Task-readiness checkpoint

Before an implementation task is finalized or started, a repository-aware planning process must check it against the authoritative planning documents. If an unresolved architectural or product decision prevents the task from being specified safely, task preparation stops and a focused grilling session resolves only the blocking questions. The resulting decisions are written back to the authoritative documents before the task becomes ready.

The checkpoint blocks only when an unanswered question could materially change task scope, observable behavior, acceptance criteria, domain ownership, persistence, integration boundaries, sequencing dependencies, safety, or recovery. It does not require the architecture or task to prescribe reversible local code structure, ordinary naming, internal algorithms, or other implementation details that a capable Implementer can decide within the established constraints.

This checkpoint will be encoded in a reusable architecture-to-task skill that can operate in ChatGPT with GitHub and Linear access or in Codex with local repository access. The skill will compose the existing grilling, domain-modeling, and Linear-task workflows instead of duplicating them. It must distinguish a missing product decision from deliberate Implementation Latitude and must not begin implementation while task preparation is incomplete.

## Task shape and agent latitude

Tasks should specify a cohesive outcome, boundaries, acceptance criteria, relevant architecture, and required evidence while leaving the implementation approach open where the architecture does not require one. Prefer a larger coherent task when it lets an agent reason about the affected design as a whole. Do not split work into small tickets merely to prescribe a sequence of mechanical edits or to constrain the agent unnecessarily. The term **Task Slice** is reserved for an explicitly scoped part of one Task that produces its own branch, worktree, review cycle, and pull request. It must not be used to label competing implementations of the same scope; those are planned **Workflow Variants**.

Architecture must not force an Implementer into an ad hoc workaround. If a Design Direction becomes awkward when applied to the actual code, the Implementer may refine it into a coherent solution that preserves Product Invariants and task intent, and must report the choice. If the coherent solution would change a Product Invariant or make a material product decision in an Open Area, implementation pauses for an explicit architecture decision.

Task boundaries still need a reason. Split work when units are independently valuable, have materially different dependencies or risks, require a human decision or review boundary, can proceed independently, or would otherwise exceed a practical context or verification scope. “Larger tasks work better” is a working hypothesis to evaluate from observed completion quality, review findings, rework, token cost, and elapsed time rather than a permanent rule.
