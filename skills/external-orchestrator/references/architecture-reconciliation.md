# Architecture reconciliation

Use this operation when accepted or proposed architecture changes may make authoritative documents or open tasks stale.

## Establish the change

1. Read the repository authority map and the Linear issue that authorized the architecture work.
2. Identify the comparison boundary: base and current revisions for repository changes, or explicit before-and-after source versions for remote documents.
3. Read the changed authoritative sources plus directly affected glossary entries and ADRs. Treat checkpoints, research, tasks, and chats as supporting evidence unless the repository assigns them authority.
4. Query Linear MCP for open tasks in the dynamically discovered target that refer to or depend on the changed behavior.

## Prepare reconciliation

For each change, record:

- the authoritative source and section;
- the previous and current meaning;
- whether this is a clarification, Design Direction refinement, Product Invariant change, or resolution of an Open Area;
- each affected open task, why it is affected, and the exact proposed update;
- any task that should remain unchanged and why.

Update authoritative documents when the active request authorizes it. Do not silently convert a task or chat decision into product authority.

Create and validate the common traceability JSON with `../scripts/validate_traceability.py`. Use `affected_tasks` for the proposed task changes and set readiness from the resulting current truth.

## Apply approved task changes

Show the proposed task updates concisely and obtain explicit approval before mutating any existing issue. After approval:

1. Invoke `create-linear-task` for each approved refinement so it remains the sole task-authoring module.
2. Reread each issue through Linear MCP and verify the exact change and relationships.
3. Rerun task readiness against the reconciled authoritative sources.
4. On repeat runs, compare the verified current issue to the proposal and make no write when it already matches.

If reconciliation exposes a Product Invariant conflict or a material Open Area, return `Architecture decision required` and make no task mutation until the decision is accepted and recorded in authoritative documentation.
