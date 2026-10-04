"""Shared task-start preparation, deliberately separate from agent delivery."""

import re

from . import TaskError
from .workspace import branch_name


def require_agent_instructions(issue):
    # Recognize the collapsed section header only; leave the description intact.
    if not re.search(r"(?m)^(?:\+\+\+|>>>)[ \t]*Agent instructions[ \t]*\r?$", issue.description):
        raise TaskError(f"Linear issue {issue.identifier} has no recognizable Agent instructions block. "
                        "Refine the issue in Linear before starting agent execution")


def prepare_task(issue, project, linear, git, herdr_factory, *, slice=None, default_only=False):
    git.update_base(project.base_branch)
    herdr = herdr_factory()
    selection = dict(default_only=True) if default_only else {}
    workspace = herdr.prepare(git, project.base_branch,
                              branch_name(issue.identifier, slice or issue.title), issue.identifier, slice,
                              **selection)
    try:
        linear.start(issue)
    except TaskError as error:
        raise TaskError(f"Workspace ready on {workspace.branch}, but status update failed: {error}") from None
    return workspace
