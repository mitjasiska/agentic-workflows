# Configuration

[Project overview](../README.md) · [Lifecycle reference](lifecycle.md)

Installation, project mapping, issue structure, task readiness, implementation
scope, agent selection, review validation, repository permission profiles, and
GitHub API credentials for the `task` CLI.

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
Linear project name and set `repo_name` and `base_branch`. Machine-specific paths
and the Linear API key live in `~/.agentic-workflows/config.toml`; shell-exported
GitHub credentials use the [private secrets file](#persistent-github-token-on-posix)
described below:

```toml
projects_root = "~/projects" # Or "C:/development/projects" on Windows

[linear]
api_key = "" # Fill in your personal API key locally

[agent]
kind = "codex"
model = "gpt-6-astra"
mode = "high"
```

## Linear issue structure

Linear descriptions are user-defined. The optional
[`create-linear-task` skill](../skills/create-linear-task/SKILL.md) recommends an
opinionated structure; using that skill or its template is not required by Lite.
Configure the single collapsed-block convention in the existing machine-local TOML:

```toml
[linear.issue_structure]
mode = "warn" # required | warn | ignore
block_name = "Agent instructions"
```

Both fields and the table are optional, with the defaults shown above.
`task start` with an agent and from-scratch `task loop` share this policy:

| Mode | Missing configured block |
| --- | --- |
| `warn` (default) | Print a non-blocking warning to stderr and continue. |
| `required` | Fail before Git updates, workspace preparation, Linear status changes, or context allocation/agent launch. |
| `ignore` | Skip the structural check entirely. |

The check recognizes only a line beginning with `+++` or `>>>`, followed by the
configured block name, matched literally and case-sensitively. Spaces/tabs around
the name in the marker and CRLF line endings are accepted. It checks marker
presence only, without parsing block contents or requiring any other heading,
closing delimiter, or template. For example, `block_name = "Implementation notes"`
recognizes `+++ Implementation notes` rather than the default name.

Unsupported modes, a non-table policy, unknown policy fields, and non-string,
empty, multiline, or nonprintable block names are configuration errors, including
in `ignore` mode. Surrounding spaces in configured names are trimmed.
Workspace-only preparation (`task start --no-agent`) and cleanup skip these
execution settings, just as they skip agent selection.

In every mode, the complete current description reaches the implementation agent
unchanged. Scope, acceptance criteria, constraints, and task-specific guidance may
appear anywhere; a collapsed block gives its contents no additional authority.
Separate workflow-owned instructions govern lifecycle and safety behavior.
The skill continues to generate its default `Agent instructions` block even if
you configure another name for runtime validation.

## Implementation task readiness

The implementation agent performs a lightweight semantic assessment before edits
by default. Configure it in the same machine-local TOML as agent selection:

```toml
[implementation.task_assessment]
enabled = true
```

Omitting the table or `enabled` keeps assessment enabled; set `enabled = false`
to skip the explicit assessment and retain ordinary implementation behavior.
The value must be a TOML boolean. Non-table settings and unknown fields are
configuration errors. Workspace-only preparation and cleanup skip this execution
setting. No new configuration format or project-registry mapping is required.

`task start` and loop-owned implementation use the same guidance and unchanged
complete issue description. The agent reads just enough repository context to
assess the outcome, scope and constraints, verification, feasibility, and blocking
ambiguity. It does not require a template, headings, explicit acceptance criteria,
or an `Agent instructions` block. It must not invent product decisions, expand
scope, edit Linear, rewrite the description, create a persistent plan, or run broad
validation just for readiness. A `ready` outcome continues in the same agent turn
without human approval or another model invocation. A `blocked` outcome stops
before implementation edits and contains specific questions.

This is independent of [issue structure](#linear-issue-structure): `required`
still refuses a missing configured block before preparation, even with assessment
disabled. `warn` and `ignore` permit ordinary issues; missing structure alone
cannot block readiness. Credentials, workspace, lifecycle, and exact session
checks remain mandatory in either setting. Assessment occurs after normal workspace
preparation and the workflow-owned Linear status transition.

For interactive start, answer a blocked assessment in the existing implementation
conversation. For a loop, inspect the stopped result and clarify the task, then
use `task loop ISSUE --new` to deliver current requirements to the same verified,
idle implementation session. `--continue` remains for graceful pauses, and an
uncertain delivery cannot be retried automatically. See the
[readiness result contract](lifecycle.md#implementation-readiness-outcomes) and
[loop recovery](lifecycle.md#checkpoint-and-interruption-recovery).

## Implementation scope discipline

Configure scope guidance in the machine-local TOML:

```toml
[implementation.scope]
policy = "strict" # strict | balanced
```

Omitting the table or `policy` defaults to `strict`. Only `strict` and `balanced`
are accepted, with surrounding whitespace trimmed. Unsupported values, non-table
settings, and unknown fields are configuration errors. Workspace-only preparation
(`task start --no-agent`) and cleanup skip this execution setting.

| Policy | Guidance |
| --- | --- |
| `strict` (default) | Prefer the smallest coherent change that satisfies the issue. Avoid unrelated refactors, formatting, renames, speculative documentation clarification, and opportunistic improvements. |
| `balanced` | Permit small, clearly relevant adjacent improvements when justified by the requested outcome. No unrelated feature work or broad cleanup. |

Both modes allow necessary supporting changes, regression tests, documentation,
and safety fixes for a complete, safe solution. `task start`, loop-owned
implementation (including completion and fixes), and independent review use the
same resolved policy. Fresh review and same-reviewer re-review examine each
substantive change against the complete issue and report unnecessary expansion
as actionable findings with concrete evidence and a justified correction, without
nitpicking necessary supporting work.

This is behavioral guidance, with no automatic edits, pruning, or rejection based
solely on diff size, file counts, or changed filenames. User-defined issue content
supplies task scope wherever it appears; the policy requires no template, heading,
or `Agent instructions` block. Workflow-owned lifecycle and safety instructions
remain authoritative. The separate issue-structure, task-readiness, and review
validation settings keep their existing behavior.

## Agent and reviewer selection

`kind` may be `codex` (the default implementation) or `pi`. For `task start`,
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

Configured loop use needs only `task loop DEV-7`. It resolves initial implementation
from `[agent]` and fresh review from `[reviewer]`, requiring explicit model and mode
for both roles. Optional `--i-agent`, `--i-model`, and `--i-mode` override implementation;
`--r-agent`, `--r-model`, and `--r-mode` override review. Each override resolves
independently over its role's configuration. Established implementation contexts
keep their recorded settings and reject `--i-*`. Generic `--agent`, `--model`, and
`--mode` remain available on `task start` and `task review`, but are not accepted by
`task loop`. See [loop lifecycle](lifecycle.md#task-loop) for startup and controls.

`task integrate DEV-7` also resolves its single execution role from `[agent]`.
Optional `--agent`, `--model`, and `--mode` override those fields independently;
model and mode must both be explicit before integration mutation or launch.
It always creates a fresh integration context; it does not inherit or change the
implementation context's settings. See [integration recovery](lifecycle.md#task-integrate).

## Review validation

The optional machine-local setting applies to standalone and loop-owned reviews,
including same-reviewer re-review:

```toml
[review.validation]
strategy = "focused_first" # focused_first | exhaustive
```

`focused_first` is the default: inspect the full diff, batch actionable findings,
and use targeted tests; defer expensive full-suite validation on findings passes.
`exhaustive` requests full validation on every review pass, even with findings.
Both preserve explicit Linear issue and repository test requirements and read-only
review safety. Invalid tables or strategy values are rejected before execution
mutation. Workspace-only start (`--no-agent`) and cleanup skip this execution setting.
The implementation handoff always prefers test-first work where practical and
focused local tests; there is no configurable methodology setting.
See [validation guidance](lifecycle.md#validation-guidance) for final checks and reporting.

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

Integration uses this same profile. Its agent only edits files in the isolated
checkout and writes structured completion in a retained private directory under
Python's temporary root (normally `/tmp`, or `TMPDIR`). The controller owns all
staging and Git metadata writes after verified completion. No additional sandbox
roots, approval override, or broader profile are required; see
[integration recovery](lifecycle.md#task-integrate).

This is deliberately a small Codex-only setup. Pi enforcement, cross-agent
capability mapping, stronger credential isolation, containers or external
sandboxing, a dedicated review profile, generalized command rules, and a broader
security framework remain out of scope.

## GitHub publishing

`task pr` requires `GH_TOKEN` or `GITHUB_TOKEN` in the invoking environment, with
access to the repository and permission to read/create/update pull requests.
For a fine-grained personal access token, select the resource owner and only the
repositories you use, then grant repository **Pull requests: Read and write**
(`Pull requests: write` in API diagnostics). PR-history checks during workspace
reuse and cleanup need **Pull requests: read**, included in that write access.
These permissions match GitHub's [pull request endpoints](https://docs.github.com/en/rest/pulls/pulls).
Leave other optional permissions unselected; keep GitHub's automatic Metadata
read access. The current workflow does not need repository **Contents: write**
on this API token: it pushes through Git and does not merge PRs through the API.

Git transport separately needs push access through the user's configured SSH key
or HTTPS credential helper. The workflow does not require `gh` or copy API tokens
into Git command arguments.

### Persistent GitHub token on POSIX

For Aquila-style Bash environments, keep shell-exported workflow secrets in
`~/.agentic-workflows/secrets.env`, outside every repository. The existing
`~/.agentic-workflows/config.toml` remains the home for the Linear key and workflow
settings; no credential migration is needed there. The CLI reads environment
variables and does not load `secrets.env` itself.

Create the private directory and file without truncating an existing file. The
subshell keeps the restrictive creation mask local to these commands:

```sh
(
    umask 077
    mkdir -p "$HOME/.agentic-workflows"
    chmod 700 "$HOME/.agentic-workflows"
    touch "$HOME/.agentic-workflows/secrets.env"
    chmod 600 "$HOME/.agentic-workflows/secrets.env"
)
```

In a trusted local editor, add or replace the `GH_TOKEN` export in that file,
using [`config/secrets.example.env`](../config/secrets.example.env) as the template.
This is placeholder-only file content; replace the placeholder privately in the
editor, never by entering a real token in a shell command or chat:

```sh
export GH_TOKEN='REPLACE_WITH_YOUR_FINE_GRAINED_TOKEN'
```

Remove old hard-coded `GH_TOKEN`/`GITHUB_TOKEN` assignments from `~/.bashrc` and
any other startup files that could overwrite this value. Keep a single
`GH_TOKEN` export in the secrets file. Add this stanza to `~/.bashrc`; it uses
POSIX shell syntax and tolerates a missing file:

```sh
set +vx  # Disable verbose input and command tracing before loading secrets.
if [ -r "$HOME/.agentic-workflows/secrets.env" ]; then
    . "$HOME/.agentic-workflows/secrets.env"
fi
```

Only source a file you own and trust: sourcing executes shell code. Mode `600`
allows only your account to read/write the file; retain it after editor saves or
replacement. Keep the populated file and editor backups out of version control,
synced dotfiles, logs, examples, and tests. Do not print it or dump the environment
for diagnosis, and keep shell tracing off while loading or using credentials.

Editing or replacing the file does **not** change an already-running shell's
environment. Reload it in each terminal that will invoke workflow commands:

```sh
set +vx
. "$HOME/.agentic-workflows/secrets.env"
```

Alternatively, source `~/.bashrc` again or open a fresh interactive Bash shell
that reads it. Login Bash shells must have their login startup file source
`~/.bashrc`; other POSIX shells need the stanza in their own startup file.
Child processes inherit the launching shell's environment. Existing agent,
terminal, or Herdr processes retain their earlier copy, so restart them from an
updated shell if they launch commands without reloading the file. Removing an
export or deleting the file also does not unset an inherited value; explicitly
unset the affected variable in existing shells when retiring a credential.

### Credential selection and safe verification

The REST client reads `GH_TOKEN` first, falling back to `GITHUB_TOKEN` only when
`GH_TOKEN` is unset or empty. A nonempty revoked or expired `GH_TOKEN` still wins
over a valid `GITHUB_TOKEN`; authentication failure does not trigger fallback.
It uses Python's HTTPS transport to `api.github.com`,
including the invoking environment's proxy and system certificate settings.
It does not obtain API credentials from `gh auth login`, SSH, Git credential
helpers, or the machine-local workflow configuration. Successful Git push or
public PR lookup does not establish that the selected API token can create a PR.
Whitespace/control characters in a selected token are refused without displaying
the value or silently selecting a different credential.

After loading the file, run this read-only check from the same shell as `task`.
It follows the same variable precedence, keeps the token out of command arguments,
and prints only the variable name and HTTP status, or a fixed error message:

```sh
python3.12 - <<'PY'
import os
from http.client import HTTPException
from urllib.error import HTTPError
from urllib.request import Request, urlopen

source = "GH_TOKEN" if os.environ.get("GH_TOKEN") else "GITHUB_TOKEN"
token = os.environ.get(source)
if not token:
    raise SystemExit("No GitHub API token is set.")
if any(not 33 <= ord(char) <= 126 for char in token):
    raise SystemExit("Selected token contains invalid characters; edit it privately.")
try:
    request = Request("https://api.github.com/user", headers={
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {token}",
        "User-Agent": "agentic-workflows-auth-check",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    with urlopen(request, timeout=30) as response:
        status = response.status
except HTTPError as error:
    status = error.code
    error.close()
except (OSError, HTTPException, ValueError):
    raise SystemExit("GitHub API connection failed; check network/proxy/TLS settings.") from None
print(f"{source}: HTTP {status}")
raise SystemExit(0 if status == 200 else 1)
PY
```

Expect `GH_TOKEN: HTTP 200` with the recommended setup. GitHub's
[authenticated-user endpoint](https://docs.github.com/en/rest/users/users#get-the-authenticated-user)
requires authentication but no additional fine-grained permissions. This confirms
the selected token is accepted, not repository access or PR write permission.
HTTP 401 means authentication was rejected: check validity/expiration/revocation
and reload the replacement token in the invoking shell. Do not debug by printing
the token, request headers, response body, or raw exception details.

An HTTP 403 diagnostic identifying insufficient token permissions means the
selected token needs repository access and **Pull requests: write**. For a
fine-grained token, check its selected repositories and permission settings;
for a classic token, check the appropriate `repo`/`public_repo` scope. Check
organization approval, SSO, or Actions restrictions when the diagnostic calls
for them. Tokens and raw API error bodies are never printed by the workflow.
See [GitHub's API troubleshooting guide](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api).

### Publishing prerequisites

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
