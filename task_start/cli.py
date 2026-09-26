import argparse
import re
import sys

from . import TaskError
from .agent import (AgentExecution, AgentOverrides, adapter_for,
                    codex_repository_policy, resolve_agent_options)
from .config import load_local, load_projects, repository_path, resolve_project
from .handoff import implementation_handoff
from .linear import Linear
from .workspace import Git, Herdr, branch_name, slice_slug


def issue_identifier(value: str) -> str:
    value = value.upper()
    if not re.fullmatch(r"[A-Z][A-Z0-9]*-[1-9][0-9]*", value):
        raise argparse.ArgumentTypeError("expected an issue identifier such as DEV-7")
    return value


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="task", description="Manage Linear task workspaces in Herdr")
    commands = result.add_subparsers(dest="command", required=True)
    start = commands.add_parser("start", help="Create or reopen a task workspace")
    start.add_argument("issue", type=issue_identifier)
    start.add_argument("--slice", type=parse_slice, help="Select an explicit implementation slice (branch suffix)")
    add_agent_options(start, include_no_agent=True)
    cleanup_command = commands.add_parser("cleanup", help="Remove a completed, merged task worktree and local branch safely")
    cleanup_command.add_argument("issue", type=issue_identifier)
    return result


def add_agent_options(command: argparse.ArgumentParser, *, include_no_agent: bool = False) -> None:
    """Add reusable execution selection flags to a workflow subcommand."""
    command.add_argument("--agent", dest="agent_kind", metavar="KIND",
                         help="Override the configured execution agent (codex or pi)")
    command.add_argument("--model", help="Override the configured model for this run")
    command.add_argument("--mode", help="Override reasoning/thinking mode for this run")
    if include_no_agent:
        command.add_argument("--no-agent", action="store_true",
                             help="Prepare/focus the workspace without starting an execution agent")


def parse_slice(value: str) -> str:
    try:
        return slice_slug(value)
    except TaskError as error:
        raise argparse.ArgumentTypeError(str(error)) from None


def start(identifier: str, *, no_agent: bool = False, slice: str | None = None,
          agent_kind: str | None = None, model: str | None = None,
          mode: str | None = None) -> str:
    if slice is not None:
        slice = slice_slug(slice)
    overrides = AgentOverrides(agent_kind, model, mode)
    if no_agent and any(value is not None for value in (agent_kind, model, mode)):
        raise TaskError("--no-agent conflicts with --agent, --model, and --mode")
    local = load_local(no_agent=no_agent)
    agent = None
    options = None
    if not no_agent:
        options = resolve_agent_options(local.agent, overrides)
        agent = adapter_for(options)
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
    if agent:
        policy = (codex_repository_policy(local.codex_repository_profiles, project.repo_name)
                  if options.kind == "codex" else {})
        execution = AgentExecution(issue, repo, workspace, options,
                                   implementation_handoff(issue, workspace), policy=policy)
        status = agent.launch(execution).summary
    else:
        status = "skipped (--no-agent)"
    return (f"{issue.identifier}  {issue.title}\nRepo:   {repo}\nBranch: {workspace.branch}\n"
            f"Worktree: {workspace.path}\nHerdr:  {workspace.action}\nLinear: In Progress\nAgent:  {status}")


def cleanup(identifier: str) -> str:
    local = load_local(no_agent=True)
    issue = Linear(local.api_key).get_issue(identifier)
    project = resolve_project(load_projects(), issue.project)
    repo = repository_path(local, project)
    if issue.state_type != "completed":
        raise TaskError(f"{identifier} is not completed (Linear status: {issue.state_name}); nothing was removed")
    git, herdr = Git(repo), Herdr(repo)
    # Check, but never fetch or advance the base during cleanup.
    git.check_base(project.base_branch)
    target = herdr.resolve_task(git, issue.identifier, include_remotes=False)
    if target is None:
        return (f"{issue.identifier}: no local task branch or registered Herdr worktree remains in {repo}. "
                "Nothing to clean up.")
    snapshot = git.check_cleanup(project.base_branch, target, issue.identifier)
    if herdr.resolve_task(git, issue.identifier, include_remotes=False) != target:
        raise TaskError("Git/Herdr cleanup target changed during validation; nothing was removed")
    git.remove_task(project.base_branch, target, issue.identifier, snapshot)
    return (f"{issue.identifier}: cleanup complete\nRepo: {repo}\n"
            f"Removed worktree: {target.path}\nRemoved local branch: {target.branch}")


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "cleanup":
            print(cleanup(args.issue))
        else:
            print(start(args.issue, no_agent=args.no_agent, slice=args.slice,
                        agent_kind=args.agent_kind, model=args.model, mode=args.mode))
    except TaskError as error:
        print(f"task: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("task: interrupted; inspect workspace and issue state before retrying", file=sys.stderr)
        return 130
    return 0
