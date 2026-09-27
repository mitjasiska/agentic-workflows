---
name: create-linear-task
description: Create or refine a Linear development issue from a rough idea, with a concise human specification, separate implementation-agent guidance, and conservative configured classification. Use when a user wants an agent-ready Linear task rather than only prose or a local TODO.
---

# Create Linear Task

Create one independently understandable, implementable, testable, reviewable, and mergeable issue. Do not split small work merely to give each activity its own issue. Split only when outcomes can genuinely be implemented and reviewed independently; if that decision changes scope materially, ask first.

## Establish context and target

1. Inspect the relevant repository instructions and enough existing implementation to make the issue actionable. Do not turn investigation into implementation.
2. For a refinement, read the current issue including its exact team ID/name, project ID/name, description, and complete label IDs. Its current team and project are authoritative; do not include a team or project change in the mutation.
3. Use the generated `config.toml` packaged beside this `SKILL.md` (or the explicit `--config` override) to establish the expected workflow target:
   - An explicit user choice must match one configured project entry.
   - Otherwise use the single entry whose `repo_name` matches the current repository.
   - `linear_project` and `linear_team` are exact selectors; resolve each to exactly one existing Linear object.
   - For a new issue, create it in that configured target.
   - For a refinement, require the issue's current team/project names to exactly match that configured target, preserve the current IDs, and stop if they differ. A user-requested move has no automatic mechanism in this skill.
   - If the target is missing or ambiguous, ask. Never choose a similarly named team or project.

The packaged config is an installation snapshot, not a second configuration owner. Repository developers change project mappings only in `config/projects.toml`, run `python3.12 skills/create-linear-task/scripts/sync_config.py` from the repository root, and refresh the installed directory by copying `skills/create-linear-task/.` over the installed `create-linear-task/` directory. Verify the refresh by comparing the source and installed `config.toml` files. Do not edit mappings in an installed copy; if its snapshot is missing a target, request synchronization/reinstallation.

Do not create, rename, or delete teams, projects, or labels. Do not read credentials to compensate for a missing Linear integration; if Linear cannot be accessed, return a prepared draft and say it was not created.

## Write and classify the issue

Use a concise, human-readable title that can produce a descriptive branch name. Draft only the human sections that materially help:

- `Context / why` is optional; explain why the work exists, not its execution plan.
- `Scope / outcome` is required and must stand on its own.
- `Done when` is required and contains practical, verifiable completion conditions.
- `Boundaries` is optional; include it when preventing adjacent changes matters.

Classify these separate facts:

- `task_kind`: how the work is performed (`implementation`, `research`, or `experiment` by default).
- `primary_category`: at most one of `feature`, `bug`, `chore`, `docs`, or `refactor`, or `null` when none clearly applies.
- `research_modifier`: `true` when research is part of the work, otherwise `false`.

Task kind does not imply a primary category. A research task that only investigates a bug has no primary category; an experiment can be a feature, chore, or unclassified depending on its intended outcome. However, `task_kind = "research"` necessarily requires `research_modifier = true`; contradictory input must stop. If task kind or primary category is genuinely ambiguous, ask or explicitly use `null`; do not choose the nearest-looking value.

The canonical configuration is closed and maps primary categories as follows: `feature` → `Feature` / `feat`, `bug` → `Bug` / `fix`, `chore` → `Chore` / `chore`, `docs` → `Docs` / `docs`, and `refactor` → `Refactor` / `refactor`. Research independently adds the `Research` label. Pure research has no change type; primary + Research retains the primary change type. Do not add categories or substitute labels outside this taxonomy.

Classification alone does not justify another human-facing section. In particular, do not make the visible specification longer merely because the Research modifier is set; include research detail only where it helps explain the outcome or completion conditions.

## Keep agent guidance separate

Put only execution-relevant details in the collapsed `Agent instructions` block:

- repository or context to inspect;
- task-specific implementation, research, or experiment guidance;
- important constraints;
- validation expectations;
- the configured stop condition;
- task kind, primary category, Research modifier, intended workflow labels, and change type.

The block must state that it addresses the implementation agent executing the issue. Do not repeat the human specification there. The default stop condition is to finish the requested work and validation, then stop ready for review without committing, pushing, or opening a PR unless another workflow explicitly requests it.

Render the collapsed block with Linear's API Markdown delimiters: `+++ Agent instructions` to open and `+++` to close. Preserve this API representation. `>>>` is only the interactive-editor shortcut and must not be emitted in an API description.

## Render deterministic fields

Prepare a JSON object with this shape, using empty lists for inapplicable agent subsections:

```json
{
  "title": "Concise title",
  "context": "Optional context or null",
  "outcome": "Independently understandable result",
  "done_when": ["Verifiable condition"],
  "boundaries": [],
  "task_kind": "implementation",
  "primary_category": "feature",
  "research_modifier": false,
  "agent": {
    "repository_context": [],
    "guidance": [],
    "constraints": [],
    "validation": ["Relevant validation"]
  },
  "existing_target": null,
  "existing_labels": [],
  "workspace_labels": [
    {"id": "existing-label-id", "name": "Feature"}
  ]
}
```

Use `null` for a genuinely unclassified task kind or primary category; all three classification fields must still be present. For a new issue, set `existing_target` to `null` and use an empty `existing_labels` list. For a refinement, set it to `{"team_id": "…", "team_name": "…", "project_id": "…", "project_name": "…"}` from the current issue. The renderer preserves those IDs and refuses a team/project name that conflicts with configuration; the returned issue update contains no target fields.

Populate `workspace_labels` with the complete active catalog of labels applicable to the selected team as `{id, name}` objects. Do not pass a broader unscoped catalog. Existing unrelated labels need not appear there: an issue may retain archived labels omitted from active-catalog queries. Treat those existing IDs as opaque and preserve them without resolving, recreating, renaming, or mutating them. Run `scripts/prepare_issue.py --repository <repo-root>`, optionally adding `--project-key <configured-key>` or `--config <path>`, and provide the JSON on standard input. With no override, the script loads the configuration from the installed skill directory and does not depend on the source repository.

Before any mutation, the renderer exact-matches all six canonical label names in that selected-team catalog, including canonical labels not intended for the current task. Any missing or ambiguous canonical label stops preparation; never substitute or create one. It emits only `label_changes.add` and `label_changes.remove` for canonical workflow label IDs; it never emits a replacement set containing unrelated labels. With `replace_workflow_labels = false`, stale or mutually exclusive workflow labels stop instead of producing contradictory labels. Replacement is allowed only when that project explicitly sets the option to `true`; even then, only the six canonical workflow labels can be removed.

For a refinement, the issue and label state read while drafting are not authoritative. Immediately before mutation, re-read the issue's current target and complete labels plus the complete selected-team label catalog. Replace `existing_target`, `existing_labels`, and `workspace_labels` in the draft, then rerun the renderer. Apply only title, description, and the resulting workflow-label additions/removals; do not submit team/project fields or all label IDs as a replacement. This preserves the existing target and unrelated labels added concurrently. If the Linear integration cannot perform label-scoped additions/removals, stop rather than falling back to a stale full-label update. For a new issue, use only `label_changes.add` as its initial workflow labels.

This fresh-read strategy is not an atomic lock against another Linear actor. After mutation, keep the original pre-mutation `existing_target` unchanged. Re-read the issue and put its target in a separate `current_target` object with the same four ID/name fields; replace only `existing_labels` and `workspace_labels` with the post-mutation state, then run the renderer with `--verify-labels`. Verification requires the reread team and project IDs to exactly equal the original IDs—names alone are insufficient—and requires the canonical workflow labels to exactly match `intended_workflow_labels` while ignoring unrelated labels. If it reports a concurrent Linear change, fail clearly and report the mismatch; do not automatically remove, overwrite, or otherwise repair the concurrent change.

Create or update the issue through the available Linear integration, then re-read it and verify the exact title, team, project, description, and label IDs. Report its identifier and link. If the renderer or exact Linear resolution fails, stop before mutation and surface the ambiguity or configuration gap.
