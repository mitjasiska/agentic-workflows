# Configuration

[Project overview](../README.md) · [Lifecycle reference](lifecycle.md)

Installation, project mapping, agent selection, and repository permission profiles
for the `task` CLI.

## Installation and project mapping

Requires Python 3.12, Git, and Herdr on `PATH`, with a running Herdr session.
Agent handoff also requires the selected, authenticated Codex or Pi CLI on `PATH`.
The commands and JSON responses were checked against Herdr 0.9.1 and Codex CLI
0.157.1. Pi transport was checked against Pi CLI 0.87.1 and Herdr 0.9.1;
run `pi --help` to verify the installed interface.

1. Clone `agentic-workflows` and your project repositories.
2. Copy [`config/local.example.toml`](../config/local.example.toml) to
   `~/.agentic-workflows/config.toml` (create the directory first).
3. Set `projects_root`, your Linear personal API key, and `[agent]` in that local
   file. The key needs read access to the issues, projects and team statuses, and
   permission to update issues. Keep it private; never commit the populated file.
4. Add the cloned repository directory to your `PATH`, then run:

   ```sh
   task start DEV-7
   ```

The executable `task` lives at the repository root. For example, in a POSIX shell:

```sh
export PATH="/absolute/path/to/agentic-workflows:$PATH"
task start DEV-7
```

It works from any directory; a symlink to `task` on your `PATH` works too. No package
installation is required. On Windows, invoke
`py -3.12 C:/path/to/agentic-workflows/task start DEV-7`; a global Windows launcher
is not included yet.

Portable project metadata is committed in
[`config/projects.toml`](../config/projects.toml). Match `linear_project` exactly to the
Linear project name and set `repo_name` and `base_branch`. Machine-specific paths and
credentials live only in `~/.agentic-workflows/config.toml`:

```toml
projects_root = "~/projects" # Or "C:/development/projects" on Windows

[linear]
api_key = "" # Fill in your personal API key locally

[agent]
kind = "codex"
model = "gpt-6-astra"
mode = "high"
```

## Agent and reviewer selection

`kind` may be `codex` (the default implementation) or `pi`. For implementation,
`model` and `mode` are optional; when omitted, the selected agent's own local
default is used. The legacy `reasoning = "high"` field remains accepted as an alias
for `mode` so
existing Codex installations continue to work. Do not set both fields.

Model names are passed unchanged to the selected CLI. Workflow `mode` means
reasoning/thinking effort, not Pi's unrelated `--mode text|json|rpc` output flag.
Codex receives it as `--config 'model_reasoning_effort="high"'`; Pi receives it
as `--thinking high`. `none` and `off` are mapped to the spelling used by each
agent. Codex supports `none`/`off`, `minimal`, `low`, `medium`, `high`, `xhigh`,
`max`, and `ultra`; Pi supports `off`/`none` through `max` but not `ultra`.
Unknown mode names fail before workspace or Linear mutation. When a Pi mode is
requested, the adapter uses Pi's local RPC mode in the exact worktree before task
submission to resolve the selected model, supported thinking levels, and effective
level. It rejects a model/mode pair if Pi would silently clamp it. Model/account
support is ultimately determined by the selected CLI. Login, repository trust,
and each agent's own permission settings remain under your control. Although Pi
itself accepts thinking suffixes such as `model:high`, Agentic Workflows rejects
that syntax because Pi may silently clamp it. Keep `model` plain and put every
thinking choice in workflow `mode`/`--mode` so it follows one validated path.

Per-command choices override machine-local configuration without modifying it:

```sh
task start DEV-7 --agent pi
task start DEV-7 --agent codex --model gpt-6-astra --mode high
```

`--agent`, `--model`, and `--mode` resolve independently. For example,
`--agent pi` retains the configured model and mode; it does not silently choose
Pi-specific values. Precedence is command override, then `[agent]`, then the
selected CLI's local default for an omitted model or mode. An unavailable or
unknown requested agent fails; there is no fallback to Codex.

Configure review separately from implementation in the machine-local config:

```toml
[reviewer]
kind = "codex"
model = "gpt-6-astra"
mode = "high"
```

Fresh review requires an explicit model and mode in `[reviewer]` or command flags.
Resuming a review retains its saved agent/model/mode and rejects selection flags.
See [review lifecycle](lifecycle.md#task-review) for exact resume requirements.

## Codex permissions for trusted repositories

Agentic Workflows does not relax Codex permissions globally. With no repository
override, the adapter passes no profile, sandbox, approval, or network option, so
Codex's active defaults and machine configuration continue to apply. Model and
reasoning choices are independent of permissions.

A repository that needs unattended implementation can opt into one
named Codex configuration profile in the machine-local
`~/.agentic-workflows/config.toml`. The key is the exact `repo_name` from
`config/projects.toml`, not a checkout or worktree path. For this repository:

```toml
[codex.repositories."agentic-workflows"]
profile = "agentic-workflows-trusted"
```

Create the selected profile beside Codex's user config. With the usual
`CODEX_HOME`, the example above names
`~/.codex/agentic-workflows-trusted.config.toml`:

```toml
approval_policy = "never"
sandbox_mode = "workspace-write"

[sandbox_workspace_write]
network_access = true
```

This exact combination was verified with Codex CLI 0.157.1. Agentic Workflows
launches it as `codex --cd WORKTREE --profile agentic-workflows-trusted ...`;
Codex loads and enforces the profile. `workspace-write` limits writes to the task
workspace and Codex's temporary roots while keeping protected paths such as
`.git` and `.codex` read-only. Network access permits commands inside that sandbox
to use outbound networking. `approval_policy = "never"` suppresses approval
prompts; an operation outside the sandbox fails and is returned to the agent
instead of escaping the sandbox. This setup does not use `danger-full-access` or
the bypass flag. See Codex's focused documentation for
[configuration profiles](https://developers.openai.com/codex/config-basic) and
[sandbox/approval behavior](https://developers.openai.com/codex/security).

Treat the override as a trust decision: the selected profile is layered on the
machine's Codex configuration and can expose repository content to processes and
network destinations used by the task. Keep the selection and profile file local;
do not commit populated local configuration, credentials, or approval state. An
unlisted repository receives no override, even if it uses the same agent or model.

Codex enforces filesystem, network, and approval boundaries. The task handoff's
rules—no commit, push, merge, pull request, `sudo`, or destructive Git
operations—are behavioral instructions, not hard sandbox rules. Implementation
and review commands may use the same trusted-repository profile; they stay
separate through fresh sessions, `AgentExecution.purpose`, and purpose-specific
handoffs, not through separate permission profiles.

This is deliberately a small Codex-only setup. Pi enforcement, cross-agent
capability mapping, stronger credential isolation, containers or external
sandboxing, a dedicated review profile, generalized command rules, and a broader
security framework remain out of scope.

## GitHub publishing

`task pr` requires `GH_TOKEN` or `GITHUB_TOKEN` in the invoking environment, with
access to the repository and permission to read/create/update pull requests.
Fine-grained tokens need repository **Pull requests: write** (which includes
read); Git transport separately needs push access through the user's configured
SSH key or HTTPS credential helper. The workflow does not require `gh` or copy
API tokens into Git command arguments. Keep tokens out of tracked configuration.

The configured base must track a same-named remote branch on `github.com`. Fetch
and push URLs must each identify a single destination in the same repository.
The task worktree's effective Git configuration must resolve to the permanent
checkout's approved remote/repository. Worktree-specific URL rewrites or
conditional includes that redirect it are refused, as are mirror remotes and
remote groups. Cross-repository/fork publication is not supported. Existing Git
hooks, commit signing, credential helpers, SSH configuration, and terminal
environment (including
`GPG_TTY` when required by the user's signing setup) remain under user control.
Run from your normal terminal for interactive passphrases. `task pr` does not
choose an execution agent or need agent CLI authentication; its public prose was
already generated during the accepted independent review.

See [publishing lifecycle and retries](lifecycle.md#task-pr) for the frozen review
contract and conservative refusal rules.
