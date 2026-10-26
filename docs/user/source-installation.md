# Build and operate AW from source on Linux

This is the supported source-checkout path. It requires Linux, Bash, the stable .NET 10 SDK selected by `global.json`, and a working non-root systemd user session (`systemctl --user`). Use the account that owns your Git credentials, Herdr sessions, and workspaces. No root installation or packaged update mechanism is provided.

## Fresh checkout and first start

From the repository root:

```sh
./build.sh
./setup-source.sh
export PATH="$HOME/.local/bin:$PATH"
awctl service start
awctl status
```

`build.sh` restores as needed and incrementally builds the entire solution, including matching `awctl` and control-plane outputs. A successful build records their hashes in `artifacts/source-build.json`. Setup rejects missing or independently changed outputs; rerun `./build.sh` to refresh both together. Keep the checkout at its configured absolute path.

`setup-source.sh` checks the SDK and built pair, creates `~/.local/bin/awctl` as a symlink to this checkout's executable, writes `$XDG_CONFIG_HOME/systemd/user/agentic-workflows.service`, and enables it. `XDG_CONFIG_HOME` defaults to `~/.config`. Setup never builds or starts the service. The PATH command above affects your current shell. If your distribution does not already include `~/.local/bin`, add it to PATH using your normal shell configuration; setup changes no shell startup files. Open a new terminal or use `hash -r` if your shell cached an older `awctl`.

Setup records ownership under `$XDG_CONFIG_HOME/agentic-workflows/source-setup/installation.json`. Same-checkout reruns are safe. Conflicting launchers, units, or loaded installations are preserved and reported. A modified generated unit is also preserved; inspect the exact named file rather than forcing setup to overwrite it. Paths may contain spaces and shell metacharacters; control characters, unsafe writable directories, and symlinked setup parent directories are rejected.

The unit captures only HOME, PATH, and the XDG config/data/runtime locations used at setup. Run it from the intended account and environment. Keep the same XDG locations in shells running application commands. `XDG_RUNTIME_DIR` must be an absolute physical directory owned by that account with mode `0700`; normally the OS provides it. If it was absent during setup, both sides use the existing private `/tmp/agentic-workflows-UID` fallback. If you use a privately installed .NET runtime, ensure its apphost runtime discovery (for example, your existing `DOTNET_ROOT`) also works when invoking `awctl` from a new shell. The service itself uses the absolute `dotnet` path captured by setup.

AW health does not require provider credentials. Before starting Workflows, follow [managed workflow configuration](managed-workflows.md). `awctl setup` validates a Project through the running control plane; it is separate from `setup-source.sh`.

## Service commands and automatic startup

```sh
awctl service status
awctl service enable
awctl service disable
awctl service stop
awctl service start
awctl service restart
```

These commands manage only `agentic-workflows.service` in the current account's systemd user manager. `enable` and `disable` change automatic startup without changing the running process. Setup enables startup at the user manager's `default.target`, normally reached at login. Starting before login after a machine reboot requires your administrator's existing user-manager/linger policy; setup neither changes host policy nor requires root. Failures are supervised with `Restart=on-failure`.

`awctl service start` waits for an active unit and a compatible Local Control Interface health reply. Restart explicitly stops and verifies the old process/socket before starting and verifying the new process. Stop waits for an inactive unit and an absent endpoint. Service commands have a 60-second overall deadline, including manager actions and bounded status exchanges; restart shares that budget across stop and start. This allows headroom beyond an observed 32-second cold startup without waiting indefinitely. The cause of that observation is unproven; increasing the bound does not establish its cause. A failed unit and a protocol mismatch fail immediately when observed. A stale or unrelated socket path is preserved and reported; the CLI never deletes it to claim success.

`awctl service status` prints JSON with service-manager state, endpoint, and application health or its diagnostic. It exits 0 only when the unit is active and application health succeeds, otherwise 1. `awctl status` remains the application-health query through the Local Control Interface. `awctl start TASK-ID` creates a Workflow. Ordinary application and Workflow commands never start a service implicitly. Syntax errors return 2; service/setup failures return 1. Service warnings are JSON records on stderr, while successful service results appear on stdout.

An active process, a compatible reachable Local Control Interface, and a ready Workflow database are separate observations. Compatible health can succeed in database maintenance mode or after database admission fails. Health includes `status.database` with supported/observed schema, active path, `databaseReady`, `workflowReady`, and diagnostic/guidance; `awctl database status` exposes the same readiness facts. Null observed schema means it could not be read. Use these fields before `awctl setup`; service success alone does not establish Workflow readiness.

Before stop or restart, the CLI reports known active or uncertain external executions. The Workflow list exposes current operations; earlier retained or replaced executions may remain uncertain even after a replacement completes. Whenever retained Workflows exist, the CLI explicitly warns that this projection cannot establish a complete execution inventory. Inspect retained Workflow history and pass reports for that earlier work. If the application cannot be queried, the CLI also reports uncertainty and continues the explicit service operation. Herdr work can survive. Service lifecycle never interrupts a Managed Agent Pass, abandons or cleans a Workflow, removes a worktree, or kills a Herdr resource. The unit uses `KillMode=process` to limit signals to the control-plane process; do not override it with group-wide termination. Surviving executions are reconciled from durable state after restart. Stop/restart is not a substitute for `awctl interrupt` or Workflow cleanup.

## Logs and recovery

```sh
systemctl --user status agentic-workflows.service
journalctl --user -u agentic-workflows.service -n 100 --no-pager
journalctl --user -u agentic-workflows.service -f
```

A missing setup diagnostic means: return to the intended checkout, run `./build.sh`, run `./setup-source.sh`, then explicitly run `awctl service start`. A user-manager connection failure means the current login/session cannot reach systemd's user manager; restore that session before retrying. Do not switch to a root service.

A partial setup failure names the failed systemctl operation and ownership receipt. Fix the session or filesystem problem, then rerun setup from the same checkout. Missing owned files can be recreated. Alternatively run `./setup-source.sh --remove` there. A conflict requires inspection of the named installation; remove it using its owning checkout/tool before setting up a different checkout. Do not delete a foreign launcher or unit blindly.

On `protocol_mismatch`, use `./build.sh` in the configured checkout and `awctl service restart`. The diagnostic retains both versions; startup does not hide mismatch behind repeated restarts. If a build or test run failed, fix it and rerun `./build.sh` before setup. If the checkout moved or disappeared, restore its original path before removal, then set up the new location explicitly.

If service startup fails or readiness times out, inspect the state and endpoint printed in the diagnostic and the journal above. Check the configured SDK/runtime path, XDG locations, directory permissions, and [database recovery instructions](managed-workflows.md). Do not delete configuration or SQLite data to repair a service-manager problem. With a crashed service and a stale owned socket, a later normal startup verifies ownership and positive connection refusal before recovering it. An active or foreign endpoint is never removed. A stop timeout with a remaining socket is a failure even if systemd reports inactive; inspect the process and endpoint before retrying start.

Credentials are not copied into the generated unit or receipt. Provider environment references must be supplied to the systemd user manager using your existing credential tooling before service start/restart; an interactive shell export alone does not update the manager. For an existing export, `systemctl --user import-environment VARIABLE_NAME` imports that named value without putting it in command arguments. Never import the entire environment or put secret values in the unit, repository, or log output. Preserve your existing agent/SSH socket access and tool PATH for that account. Rerun setup after intentional non-secret PATH or XDG changes, then explicitly restart. Do not customize generated unit contents; use your existing systemd environment/credential mechanism.

## Safe removal

From the owning checkout, stop the process deliberately before removing its setup:

```sh
awctl service stop
./setup-source.sh --remove
```

Removal disables automatic startup, removes only the proven launcher, generated unit, and ownership receipt, and reloads the user manager. It neither stops a running process nor builds. It leaves the empty setup directories and all AW configuration, SQLite databases/backups, Workflows, worktrees, branches, Herdr resources, credentials, and unrelated files intact. A modified or conflicting artifact blocks removal and is preserved. Removal can proceed without the original client/host artifacts or SDK validation in the helper, but the entry script still needs a usable .NET SDK and built source-support helper; rebuild the checkout if that helper is missing.
