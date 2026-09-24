import argparse
import re
import sys

from . import TaskError
from .agent import Codex, task_prompt
from .config import load_local, load_projects, repository_path, resolve_project
from .linear import Linear
from .workspace import Git, Herdr, branch_name, slice_slug


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
    start.add_argument("--slice", type=parse_slice, help="Select an explicit implementation slice (branch suffix)")
    start.add_argument("--no-agent", action="store_true", help="Prepare/focus the workspace without starting Codex")
    return result


def parse_slice(value: str) -> str:
    try:
        return slice_slug(value)
    except TaskError as error:
        raise argparse.ArgumentTypeError(str(error)) from None


def start(identifier: str, *, no_agent: bool = False, slice: str | None = None) -> str:
    if slice is not None:
        slice = slice_slug(slice)
    local = load_local(no_agent=no_agent)
    agent = None
    if not no_agent:
        if local.agent is None:
            raise TaskError("Configure [agent] kind, model and reasoning in ~/.agentic-workflows/config.toml "
                            "or use --no-agent")
        agent = Codex(local.agent)
        agent.check_available()
    projects = load_projects()
    linear = Linear(local.api_key)
    issue = linear.get_issue(identifier)
    project = resolve_project(projects, issue.project)
    repo = repository_path(local, project)
    git = Git(repo)
    git.update_base(project.base_branch)
    workspace = Herdr(repo).prepare(git, project.base_branch,
                                   branch_name(issue.identifier, slice or issue.title), issue.identifier, slice)
    try:
        linear.start(issue)
    except TaskError as error:
        raise TaskError(f"Workspace ready on {workspace.branch}, but status update failed: {error}") from None
    status = agent.launch(workspace, task_prompt(issue, workspace)) if agent else "skipped (--no-agent)"
    return (f"{issue.identifier}  {issue.title}\nRepo:   {repo}\nBranch: {workspace.branch}\n"
            f"Worktree: {workspace.path}\nHerdr:  {workspace.action}\nLinear: In Progress\nAgent:  {status}")


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        print(start(args.issue, no_agent=args.no_agent, slice=args.slice))
    except TaskError as error:
        print(f"task: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("task: interrupted; inspect workspace and issue state before retrying", file=sys.stderr)
        return 130
    return 0
