# Agentic Workflows

Reusable tooling, skills, and automation for efficient agentic software development workflows.

The goal is to reduce repetitive development mechanics and make working with coding agents across multiple projects fast, consistent, and predictable.

## Setup and use

Requires Python 3.12, Git, and Herdr on `PATH`, with a running Herdr session.
The Herdr commands and JSON responses were checked against locally installed Herdr 0.9.1.

1. Clone `agentic-workflows` and your project repositories.
2. Copy `config/local.example.toml` to `~/.agentic-workflows/config.toml` (create the directory first).
3. Set `projects_root` and your Linear personal API key in that local file. The key needs read access to the issues, projects and team statuses, and permission to update issues. Keep it private; never commit the populated file.
4. Add the cloned repository directory to your `PATH`, then run:

   ```sh
   task start DEV-7
   ```

The executable `task` lives at the repository root. For example, in a POSIX shell:

```sh
export PATH="/absolute/path/to/agentic-workflows:$PATH"
task start DEV-7
```

It works from any directory; a symlink to `task` on your `PATH` works too. No package installation is required. On Windows, invoke `py -3.12 C:/path/to/agentic-workflows/task start DEV-7`; a global Windows launcher is not included yet.

Portable project metadata is committed in `config/projects.toml`. Match `linear_project` exactly to the Linear project name and set `repo_name` and `base_branch`. Machine-specific paths and credentials live only in `~/.agentic-workflows/config.toml`:

```toml
projects_root = "~/projects" # Or "C:/development/projects" on Windows

[linear]
api_key = "" # Fill in your personal API key locally
```

A repository is resolved as `projects_root / repo_name`. Each must be its permanent Git checkout, already on the configured base branch, with a clean working tree (including untracked files) and no unfinished Git operation. The base must track a same-named branch on a remote. `task start` fetches that upstream into `FETCH_HEAD` and updates using `merge --ff-only`; it refuses local-only commits or divergence. It does not switch, stash, reset, or force-update branches.

The command loads the issue through [Linear's GraphQL API](https://linear.app/developers/graphql), resolves the project, updates the base, and asks Herdr to create and focus a worktree. Herdr chooses the worktree location. Branch names start with the lowercase issue identifier and a deterministic title slug (up to 100 title characters). No AI agent is started.

An existing task worktree is reopened only when Git and Herdr agree on a single branch and checkout. Matching the issue prefix allows reuse after a title change. Branch-only, known remote-only, stale, locked, or ambiguous task work stops with an error; no separate worktree database is maintained. Remote-only detection uses locally known remote-tracking refs; the command only fetches the base, so it does not discover task branches created solely on another machine.

Only after Herdr confirms preparation and focus does the command set the issue to its team's exact `In Progress` status. An issue already in that status needs no update. If the status request fails, the workspace remains available; check Linear and rerun. If Herdr fails or times out, inspect its state before retrying; no Linear update is attempted. Do not run simultaneous starts for the same repository or edit its base checkout during a start.

## Tests

```sh
python3.12 -m unittest discover -s tests -v
```

Tests fake Linear and Herdr boundaries, test Git command construction, and exercise Git safety in temporary repositories with a local fake remote. They need no API key, network access, or real Herdr workspaces.
