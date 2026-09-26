"""Purpose-specific semantic handoffs constructed by workflow commands."""

from . import TaskError
from .linear import Issue
from .workspace import Workspace


IMPLEMENTATION_INSTRUCTIONS = """- Treat the task above as the source of truth.
- Read AGENTS.md and other repository instructions first.
- Inspect the existing implementation before changing anything.
- Implement only the requested scope.
- Run relevant tests and practical validation.
- Do not commit, push, merge, or open a PR.
- Do not use sudo or destructive Git operations.
- Stop when the implementation is ready for independent review.
- Work in the prepared checkout below; do not create another branch or worktree.
- Do not connect to Linear, re-fetch this issue, or read Linear credentials or the local workflow config.
- Do not write the Linear task description into the repository.
"""


def implementation_handoff(issue: Issue, workspace: Workspace) -> str:
    """Build the task-start implementation handoff before adapter selection."""
    scope = (f"\nSlice: {workspace.slice}\n"
             "Implement only this slice of the task. Ask if its scope is unclear.\n"
             if workspace.slice is not None else "")
    handoff = (f"Implement Linear issue {issue.identifier}.\n\nTitle:\n{issue.title}\n\n"
               f"Task:\n{issue.description}\n\nInstructions:\n"
               f"{IMPLEMENTATION_INSTRUCTIONS}{scope}\n"
               f"Prepared checkout: {workspace.path}\nBranch: {workspace.branch}\n")
    if "\0" in handoff:
        raise TaskError("Linear context contains a NUL character and cannot be delivered to the execution agent")
    return handoff
