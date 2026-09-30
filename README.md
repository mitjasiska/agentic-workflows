# Agentic Workflows

Agentic Workflows helps you take a development task from a Linear issue to an
agent implementation, independent review, PR publication, and workspace cleanup.
It combines a task-creation skill with a `task` command that coordinates your repositories,
Git worktrees, and interactive coding agents in Herdr.

You choose the task and scope, the workflow prepares the checkout and hands over
the current requirements, and you decide how to address findings and publish the
result. Codex and Pi are supported execution agents; Codex is the default
implementation.

## Core concepts

| Concept | What it means for you |
| --- | --- |
| Issue | The Linear task holds the requirements. Start and review use its latest contents. |
| Project repository | A Linear project maps to a permanent local Git checkout and a base branch. Keep that checkout clean and on its configured base. |
| Task workspace | A separate Git worktree and branch, opened in Herdr. Implementation and review share this checkout. |
| Slice | An explicitly named part of an issue, with its own branch and workspace. The default is one issue, one branch, one PR. |
| Context | An implementation or reviewer conversation, labeled in its pane, such as `DEV-7-I1` or `DEV-7-R1`. Several contexts can share a checkout. |
| Review pass | A review of the current checkout against the local base. A fresh pass starts an independent conversation; an explicit resume keeps a particular reviewer's history. |

The normal path is: prepare an issue, start implementation, review the result,
address any findings, publish with `task pr`, inspect and merge the PR, then clean
up. Implementation stops ready for review. Review reports findings without
applying fixes. You own scope, follow-up fixes, publication, and marking the
Linear issue complete; there is no automatic fix or merge loop.

## Setup

You need Python 3.12, Git, and Herdr on `PATH`, with a running Herdr session.
Agent execution also needs an authenticated Codex or Pi CLI on `PATH`.

1. Clone this repository and the project repositories you want to work on.
2. Check the mappings in [`config/projects.toml`](config/projects.toml).
   `linear_project` must match the Linear project name; `repo_name` and
   `base_branch` identify its local checkout and base. The base must track a
   same-named remote branch.
3. Copy [`config/local.example.toml`](config/local.example.toml) to
   `~/.agentic-workflows/config.toml`, creating the directory first. Set
   `projects_root`, your Linear personal API key, and your `[agent]` selection.
   Keep this populated file private. The key needs issue/project/team-status
   read access and permission to update issues.
4. Add this repository to `PATH`, then start a task:

   ```sh
   export PATH="/absolute/path/to/agentic-workflows:$PATH"
   task start DEV-7
   ```

No package installation is required. The command works from any directory or
through a symlink on `PATH`. On Windows, use
`py -3.12 C:/path/to/agentic-workflows/task start DEV-7`.

Configure `[reviewer]` separately before using review; fresh reviews require an
explicit model and mode in that section or as command flags. The
[configuration guide](docs/configuration.md) includes examples, option precedence,
agent compatibility, and optional repository-specific Codex permission profiles.

## Using the workflow

### Create or refine a task

Start with a Linear issue, or use the
[`create-linear-task` skill](skills/create-linear-task/SKILL.md) to turn a rough
idea into one. It supports implementation, research, and experiment tasks, keeping
the human specification visible and execution guidance in a collapsed
`Agent instructions` section. Task creation is separate from the `task` CLI.

The standalone skill is validated with Codex; other skill hosts have not been
verified. Use it from this repository, or install the whole directory:

```sh
skill_dest="${CODEX_HOME:-$HOME/.codex}/skills/create-linear-task"
mkdir -p "$skill_dest"
cp -R skills/create-linear-task/. "$skill_dest/"
```

Start a new Codex session after installation or refresh. When project mappings
change, follow the skill's [configuration refresh instructions](skills/create-linear-task/SKILL.md#establish-context-and-target).
Its own instructions also cover [classification](skills/create-linear-task/SKILL.md#write-and-classify-the-issue)
and [safe issue refinement](skills/create-linear-task/SKILL.md#render-deterministic-fields).

A separate [ChatGPT plugin package](plugins/create-linear-task/README.md) distributes
this same skill for web/mobile. See its README for distribution options, packaging,
and acceptance requirements. The standalone installation above remains independent.

### Start, reopen, or prepare a workspace

```sh
task start DEV-7
task start DEV-7 --no-agent
task start DEV-7 --agent pi --model <provider/model> --mode high
```

`start` updates the clean permanent base, creates or focuses the task workspace,
sets Linear to the team's `In Progress` status, and starts an implementation
agent with the current issue and checkout. Renaming an issue does not change its
existing workspace's identity. Uncommitted work in a reused task checkout is
preserved.

Use `--no-agent` to prepare or focus the workspace without launching an agent.
It still updates Linear and works without an agent installation or `[agent]`
configuration. It cannot be combined with agent selection flags. If an agent is
already running, continue in its pane, use `--no-agent` to focus the workspace,
or exit that session before requesting a fresh implementation handoff.

`--agent`, `--model`, and `--mode` override configuration independently for one
command. Changing only `--agent` retains the configured model and mode, so choose
values supported by that agent. There is no automatic fallback agent.

For separate implementation slices, select a stable scope name:

```sh
task start DEV-7 --slice importer
task start DEV-7 --slice exporter
```

Each slice has its own branch and workspace. Without `--slice`, start can reuse
exactly one candidate and retains its recorded scope. Multiple candidates require
an explicit slice; ambiguous or historical workspaces are refused. Review and
cleanup have no slice selector and refuse multiple task worktrees/slices. See
[workspace reuse and slices](docs/lifecycle.md#workspace-lifecycle-and-slices)
for legacy workspaces, remote branches, and PR-history restrictions.

### Review and re-review

When implementation and validation are ready, leave the task workspace open and
run an independent review:

```sh
task review DEV-7
task review DEV-7 --agent codex --model gpt-6-astra --mode high
task review DEV-7 --resume DEV-7-R1
```

Fresh review opens a new reviewer pane in the task tab. It examines committed,
staged, unstaged, and untracked task changes against the local base, using the
latest Linear requirements. It needs a verified implementation context in that
workspace; a workspace prepared only with `--no-agent` is not sufficient. Review
does not advance the base or change Linear. Keep the checkout stable during a
pass: observed changes invalidate the result.

After addressing findings yourself or in the implementation session, choose a
fresh review for a new independent assessment, or `--resume` with an exact review
context ID for a follow-up in that reviewer's conversation. Resume retains the
original agent/model/mode and cannot be combined with selection flags. It stops
if the recorded conversation cannot be verified.

Results are `clean`, `findings`, `blocked`, or `failed`; a blocked result can report
that the implementation changed during review. Findings and limitations need
human follow-up. Use `--json` for structured output or `--timeout SECONDS` to
change the default 1800-second wait after delivery. See the
[review reference](docs/lifecycle.md#task-review) for guarantees, exit codes, and
resume requirements.

### Find your conversations

```sh
task contexts
task contexts DEV-7
task contexts DEV-7 --all
```

This read-only view shows context IDs, locations, selected agents, status, and
whether a conversation can be resumed. `--all` includes retired history. It does
not contact Linear or load workflow configuration. An `unknown` or stale entry
needs inspection; listing it does not repair or resume it.

### Publish the reviewed result

```sh
task pr DEV-7
```

This command authorizes committing the exact clean-reviewed task state, pushing
its branch, and creating or updating the matching GitHub PR. It prints the PR URL
and stops for your inspection. It never merges, approves, or completes the issue.
Keep the task workspace open and unchanged after review. Run review again if the
files, index, or task history have changed. If the remote base advances before
first publication, `pr` checks the rebase in disposable Git state. A conflict
leaves your task unchanged; a clean rebase updates it and stops for a fresh
independent review of the rebased result and preserved PR title/body. After that
review, rerun `pr` to publish the same rebased commit with that metadata.
Already published branches are never automatically rebased.

The commit subject and PR title use `<type>: <summary> (DEV-7)`. The type comes
from one canonical Linear category label; the reviewer supplies a public-safe
summary of the actual implemented result and observed validation. Titles use a
concise lower-case action summary, preserving names and acronyms, for example
`feat: add reviewed task PR publishing (DEV-18)`. Configure
`GH_TOKEN` or `GITHUB_TOKEN` with pull-request access, and keep your normal Git
push authentication/signing setup. Native passphrase prompts stay in your terminal.

Rerun the same command after a failure: it verifies and reuses completed commits,
pushes, and PRs. Conflicting state stops for inspection. See the
[publishing reference](docs/lifecycle.md#task-pr) for the exact review contract,
credentials, supported states, and recovery guidance.

### Finish and clean up

After inspecting and merging the PR, mark the Linear issue completed and
ensure the merge is present in the configured local base. Exit the task's
agent/shell sessions and run cleanup from outside its workspace:

```sh
task cleanup DEV-7
```

Cleanup verifies completion and merge evidence, then removes the local task
worktree and branch, closes its Herdr workspace, and retires the associated
contexts. It does not update the base, remove remote branches, or change PRs or
Linear. Dirty, ambiguous, or unproven state is preserved. GitHub squash/rebase
merges are supported when their evidence matches the local task and base. See
[cleanup requirements and retries](docs/lifecycle.md#task-cleanup).

### Handle a stopped or failed command

Read the reported state and inspect the named pane before retrying: an agent or
prompt may already have started. A handoff failure leaves the workspace and
Linear's `In Progress` status intact. Authentication or repository trust may need
action in the agent's pane. Do not run simultaneous starts for the same repository
or modify a workspace during review, publication, or cleanup. The
[failure and retry reference](docs/lifecycle.md#failure-and-retry-behavior)
explains which portions may already have completed.

## Project documentation

- [Configuration](docs/configuration.md): installation, project mapping, agent/reviewer settings, and permissions.
- [Task lifecycle reference](docs/lifecycle.md): workspace safety, review guarantees, cleanup, and retry cases.
- [Architecture and validation](docs/architecture.md): execution adapters, prompt delivery, context registry, tests, and recorded live-check limits.
- [Repository instructions for agents](AGENTS.md): maintenance guidance and documentation ownership.

Use `task --help` or `task <command> --help` for the available flags.
