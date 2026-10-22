# Task preparation

Use this operation to turn an authorized outcome into implementation-ready work.

## Establish current context

1. Read the root `AGENTS.md` and `README.md`, then follow their authority map to only the sources applicable to the requested outcome.
2. Read the applicable current Linear issue through Linear MCP. Discover its team, project, parent, labels, and workflow states from current data or repository-owned configuration; do not infer them from an identifier prefix.
3. Capture source freshness: the repository revision, dirty state, observation time, and every authoritative source used. If local and remote state differ materially, report that before task mutation.
4. Extract the requested outcome, scope, exclusions, acceptance criteria, dependencies, and relationships. Do not promote roadmap mentions into work.

If the repository does not define an authority map or the Linear target is missing or ambiguous, stop with `Architecture decision required` only when the gap could materially change the task. Otherwise make a reversible choice under Implementation Latitude and record it.

## Evaluate readiness

Classify every applicable statement using the repository's definitions:

- Product Invariant
- Design Direction
- Open Area
- Implementation Latitude

Return exactly one result:

- `Ready` when the outcome and acceptance boundary are clear, no Product Invariant conflicts, and no material Open Area could change scope, observable behavior, ownership, persistence, integration boundaries, sequencing, safety, or recovery.
- `Architecture decision required` when an invariant conflicts with the requested work or a material Open Area remains.

For a blocked result, identify one focused decision and its evidence. Do not create, update, relate, or transition an implementation issue. Use `grilling` to resolve the decision and `domain-modeling` when canonical terms or an ADR must change. Reconcile the accepted decision into authoritative documents, then rerun readiness.

## Validate the trace

Write the evaluation in the schema accepted by `../scripts/validate_traceability.py`. Include the current work item's identifier when one exists, but never require a particular identifier format. Validate the file before any task-authoring call.

## Delegate issue authoring

Only after a valid `Ready` result:

1. Search current Linear state for an existing issue that represents the same requested outcome, including a known identifier, parent children, and relevant open issues. Reuse or refine it instead of creating a duplicate.
2. Invoke `create-linear-task` with the outcome, exclusions, acceptance criteria, relevant architecture references, deliberate Implementation Latitude, and required parent or related relationships.
3. Let `create-linear-task` discover or validate the current Linear target and own rendering, classification, labels, mutation safety, and post-write verification. If its configured target conflicts with the current issue or repository, stop and report the configuration gap.
4. Reread the resulting issue through Linear MCP and verify its title, description, target, labels, state, and relationships.
5. Repeat the same search on rerun. A second run must refine the same issue or make no change, never create another issue for the same outcome.

Discussion and a readiness result do not themselves authorize a Linear write. Obtain the authorization required by the active tool and workflow before mutation.
