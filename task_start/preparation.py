"""Shared task-start preparation, deliberately separate from agent delivery."""

import re
import sys

from . import TaskError
from .config import IssueStructureConfig
from .ownership import ownership_operation
from .workspace import branch_name


def check_issue_structure(issue, policy: IssueStructureConfig):
    if policy.mode == "ignore":
        return
    # Recognize only the configured collapsed marker; leave all task text intact.
    marker = rf"(?m)^(?:\+\+\+|>>>)[ \t]*{re.escape(policy.block_name)}[ \t]*\r?$"
    if re.search(marker, issue.description):
        return
    message = f"Linear issue {issue.identifier} has no recognizable collapsed {policy.block_name!r} block. "
    if policy.mode == "required":
        raise TaskError(message + "Required by linear.issue_structure.mode=required; "
                        "add the configured block in Linear or change the issue_structure policy before starting agent execution")
    print("task: warning: " + message + "Continuing agent execution (linear.issue_structure.mode=warn).", file=sys.stderr)


@ownership_operation
def prepare_task(issue, project, linear, git, herdr_factory, *, slice=None, default_only=False):
    git.check_disposal(issue.identifier)
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
