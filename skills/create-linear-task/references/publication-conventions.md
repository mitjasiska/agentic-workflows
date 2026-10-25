# Fixed publication conventions

These instruction-level conventions are shared by `create-linear-task` and `external-orchestrator`. Keep both skills installed together when using the Orchestrator. There is no publication configuration file, host-specific lookup, or settings synchronization. The conventions travel with the versioned skill source if the skills are extracted later.

## Prototype mapping

| Category | Exact Linear label | Commit/PR type |
| --- | --- | --- |
| feature | Feature | feat |
| bug | Bug | fix |
| chore | Chore | chore |
| docs | Docs | docs |
| refactor | Refactor | refactor |

This preserves the prototype's [publication mapping](https://github.com/mitjasiska/agentic-workflows-prototype/blob/main/task_start/publish.py#L31) and [task-authoring categories](https://github.com/mitjasiska/agentic-workflows-prototype/blob/main/skills/create-linear-task/scripts/prepare_issue.py#L20). `Research` is an independent modifier and does not select a publication type. Unrelated labels are ignored for type selection and preserved.

## Apply the conventions

- **Task-linked publication:** Read the current issue through Linear MCP. Exactly one current issue label must match a primary label in the table. Use its mapped type. Zero or multiple matching primary labels require a focused classification decision before publication; do not guess from the issue title or relabel automatically.
- **Task authoring:** Choose the applicable semantic category and resolve its exact label against the actual current Linear team. Resolve IDs at runtime; do not hardcode them. If a required label is missing or ambiguous, report the gap rather than substituting a similar name or creating a label. Record the selected category, label, and change type in agent metadata. Research does not imply a primary category.
- **Taskless publication:** No Linear access or labels are needed. Suggest one of `feat`, `fix`, `chore`, `docs`, or `refactor` based on the actual diff and show it with the proposed message before commit approval. Honor an explicitly requested type from this set. Do not silently choose the first type or a fixed default; ask if the change cannot be classified clearly.

Review and branch permissions come from the human's chosen workflow and repository rules. Choosing a type does not authorize a commit, push, Workflow start, Linear update, or PR creation. Changing these conventions later is an explicit skill-source maintenance change, not an edit to installed copies or AW runtime configuration.
