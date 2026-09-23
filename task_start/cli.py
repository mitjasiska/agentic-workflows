import argparse
import re
import sys

from . import TaskError
from .config import load_local, load_projects, repository_path, resolve_project
from .linear import Linear
from .workspace import Git, Herdr, branch_name


def issue_identifier(value: str) -> str:
    value = value.upper()
    if not re.fullmatch(r"[A-Z][A-Z0-9]*-[1-9][0-9]*", value):
        raise argparse.ArgumentTypeError("expected an issue identifier such as DEV-7")
    return value


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="task", description="Prepare a Linear task workspace in Herdr")
    commands = result.add_subparsers(dest="command", required=True)
    start = commands.add_parser("start", help="Create or reopen a task workspace")
    start.add_argument("issue", type=issue_identifier)
    return result


def start(identifier: str) -> str:
    local = load_local()
    projects = load_projects()
    linear = Linear(local.api_key)
    issue = linear.get_issue(identifier)
    project = resolve_project(projects, issue.project)
    repo = repository_path(local, project)
    git = Git(repo)
    git.update_base(project.base_branch)
    branch, action = Herdr(repo).prepare(git, project.base_branch,
                                       branch_name(issue.identifier, issue.title), issue.identifier)
    try:
        linear.start(issue)
    except TaskError as error:
        raise TaskError(f"Workspace ready on {branch}, but status update failed: {error}") from None
    return (f"{issue.identifier}  {issue.title}\nRepo:   {repo}\nBranch: {branch}\n"
            f"Herdr:  {action}\nLinear: In Progress")


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        print(start(args.issue))
    except TaskError as error:
        print(f"task: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("task: interrupted; inspect workspace and issue state before retrying", file=sys.stderr)
        return 130
    return 0
