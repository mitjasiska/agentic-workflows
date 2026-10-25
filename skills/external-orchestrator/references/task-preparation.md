# Task preparation

Use this operation to turn an authorized outcome into implementation-ready work.

## Establish current context

1. Read the root `AGENTS.md` and `README.md`, then follow their authority map to only the sources applicable to the requested outcome.
2. Read the applicable current Linear issue through Linear MCP. When preparing an explicitly requested new outcome without an issue, resolve the current project and search its queue for matching work instead. Discover relevant target identities from current data or repository-owned configuration; do not infer them from an identifier prefix.
3. Capture source freshness: the repository revision, dirty state, observation time, and every authoritative source used. If local and remote state differ materially, report that before task mutation.
4. Extract the requested outcome, scope, exclusions, acceptance criteria, dependencies, and relationships. Do not promote roadmap mentions into work.

Resolve missing or ambiguous repository and Linear context with a focused request for that information. An unavailable tool or ambiguous target is a context blocker, not an architecture verdict. Reserve `Architecture decision required` for a material product question or constraint conflict found in the assessment.

## Evaluate readiness

Record guidance that affects the outcome using the repository's definitions. If it has no named categories, use these meanings without requiring changes to its documentation:

- **Product Invariant:** settled behavior or a boundary that must be preserved.
- **Design Direction:** a preferred shape that may be refined while preserving the outcome and invariants.
- **Open Area:** an unresolved product question; it blocks only when material to this task.
- **Implementation Latitude:** a reversible technical choice the implementer may make.

Return one readiness value with concise evidence and the next action:

- `Ready` when the outcome and acceptance boundary are clear, no Product Invariant conflicts, and no material Open Area could change scope, observable behavior, ownership, persistence, integration boundaries, sequencing, safety, or recovery.
- `Architecture decision required` when an invariant conflicts with the requested work or a material Open Area remains.

For a blocked result, identify one focused decision and its evidence. Make no related implementation-task mutation. Use the available architecture-refinement skills when helpful. Wait for the human's acceptance of a material product decision, reconcile it into authoritative documents, then rerun readiness. A document edit alone is not acceptance.

## Validate the trace

Write the evaluation using [the traceability contract](traceability.md). Include the current work item's identifier when one exists, but never require a particular identifier format. Check every required field and readiness invariant before any task-authoring call.

## Delegate issue authoring

Only after a valid `Ready` result:

1. Invoke `create-linear-task` with the current issue or resolved project, outcome, exclusions, acceptance criteria, architecture references, deliberate Implementation Latitude, checked readiness trace, and the user's authorization or proposal-only boundary.
2. Let it own duplicate detection, rendering, classification, relationships, mutation safety, and fresh post-write verification. A target conflict requires resolution before mutation.
3. Report its verified result or remaining blocker. A repeat run must reuse the existing issue and make no write when the requested state already matches.

Discussion and a readiness result do not themselves authorize a Linear write. Obtain the authorization required by the active tool and workflow before mutation.
