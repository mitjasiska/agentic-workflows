# Move Linear Tasks to In Progress before agent launch

AW owns the automatic transition of a Linear-backed Task into In Progress when starting managed work, so the user does not need to update Linear manually. The GitHub–Linear integration separately owns the transition to Done after pull-request merge; `awctl pr` does not complete the Task. Provider status remains observable integration state rather than proof of AW review or submission correctness.

"In Progress" is a configured semantic role, not a display-name lookup performed during every start. Project setup resolves and persists the concrete Linear workflow-state identifier for the relevant team. AW automatically selects an unambiguous active-work state when possible; if several states could fill that role, setup asks for one explicit choice. Startup validates that the saved identifier still exists and belongs to the Task's team. A missing or invalid mapping is a configuration error, not permission to guess or launch an agent without updating Linear.

The deterministic startup order is:

1. Complete read-only admission and validation checks, including validation of the configured Linear state identifier. Stop without creating a Workflow or worktree if the Linear Task is already Done or Canceled; an existing configured In Progress status is an idempotent success.
2. Durably prepare the Workflow and worktree.
3. Move the Linear Task to In Progress when it is not already there.
4. Launch the first managed agent pass.

The user must deliberately reopen a terminal Task in Linear before retrying `awctl start`; AW does not reopen it or bypass the terminal-state check automatically. If the Linear update fails, AW stops before agent launch and retains the prepared Workflow for an explicit retry. If the Linear update succeeds but agent launch fails, AW leaves the Task In Progress and retains the prepared Workflow for retry. It does not attempt a compensating Linear status rollback because the durable Workflow represents started work even when its first execution attempt did not launch successfully.

This is AW's only automatic status write for that Workflow. After startup, AW observes and reports the current Linear status but does not reassert In Progress or write later lifecycle states. The GitHub–Linear integration owns PR-driven transitions so the two automation systems do not overwrite each other.

If the Task later becomes Canceled, AW does not automatically kill an active Managed Agent Pass. It allows that pass to finish and preserves its output, then pauses before another managed handoff for an explicit user decision. If the Workflow is idle, it pauses when the cancellation is observed. The user can still interrupt immediately with Ctrl+C or by exiting the agent; ordinary interruption recovery then applies. AW neither reopens the Task nor rolls its status back.

Done is handled differently for a Workflow that is already active. AW records and displays a warning but continues normally because another linked pull request may have triggered completion before the current Workflow created its own request. Done does not prove an intent to stop active work, and AW does not move the Task back to In Progress.
