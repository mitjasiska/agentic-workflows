# Next action

Use this operation when the human asks what to work on next or what an existing result means for the work queue.

1. Read the repository's `README.md` and applicable instructions. Resolve the Linear project from the user's choice, repository configuration, or one unambiguous current project match. An issue identifier is optional at this stage.
2. Read the relevant current queue through Linear MCP, including started work, work awaiting review, and plausible next candidates. Follow pagination where needed to support the recommendation. Read candidate details and dependencies before selecting one; distinguish a dependency's actual current state from an old comment about it.
3. Compare the user's priorities, blockers, dependencies, and available completion evidence. Prefer finishing existing work when that advances the requested outcome. An In Progress state alone does not prove an agent is running or authorize starting another execution.
4. Use repository architecture to interpret candidate scope and constraints. A roadmap mention or checkpoint entry does not create work. If no existing issue covers an explicitly requested outcome, recommend task preparation; otherwise explain the queue gap without inventing a task.
5. Recommend one immediate action, cite the issue and evidence, and state any uncertainty that could change the choice. Offer alternatives only when the tradeoff matters. If implementation readiness must be decided, continue with [Task preparation](task-preparation.md); do not imply that queue selection alone established readiness.

When interpreting an execution report, identify its task, revision, reported outcome, validation, unresolved findings, and proposed next action. Treat missing or stale facts as unknown. A pass report, clean review, merged change, and completed project outcome are distinct evidence; use the level relevant to the user's question. Recommend a current check when the evidence cannot establish the next action.

This operation is read-only. Starting or interrupting execution, assigning work, changing an issue, publishing Git changes, and marking work complete require authorization for those actions. Delegate an authorized issue update to `create-linear-task`; leave execution to the user's selected workflow and its supported controls.
