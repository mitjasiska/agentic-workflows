# Local .NET development

Use the [Linux source setup and service guide](../user/source-installation.md) for first installation, automatic startup, service status, logs, safe removal, and recovery. See [managed workflow usage](../user/managed-workflows.md) and [validation/smoke testing](managed-workflow-smoke-test.md) for provider configuration and workflow validation. The dashboard and HTTP listener remain separate work.

## Prerequisites and build

Use Linux with Bash, systemd user services, and the .NET 10 SDK (10.0.100 or a later stable 10.0 feature band selected by `global.json`). Linux `statx` (kernel 4.11 or later with libc support) is required for ownership checks. Validation uses Ubuntu 24.04, Linux 6.8, and SDK 10.0.113. Git is needed for repository tests. Workflow execution additionally needs the operator's configured Herdr/Codex and provider access; automated tests use controlled substitutes.

From the repository root:

```sh
./build.sh
./build.sh rebuild
./build.sh test
```

The default is an incremental complete solution build with implicit restore. `rebuild` cleans the solution then builds it; `test` builds it then runs the complete automated suite. Failures propagate their exit code and invalidate the source-build seal. None of these operations installs files or invokes a service manager. The successful build seal covers all files under both application output directories and requires identical shared AW libraries. Setup checks the seal to prevent independently refreshed client/host outputs.

Manual equivalents for compilation and tests are:

```sh
dotnet restore AgenticWorkflows.slnx
dotnet build AgenticWorkflows.slnx --no-restore
dotnet clean AgenticWorkflows.slnx
dotnet build AgenticWorkflows.slnx
dotnet test AgenticWorkflows.slnx --no-build --no-restore
```

Use `./build.sh` after manual compilation to seal the matching outputs for source setup. Outputs live under each project's `bin/Debug/net10.0/`; source setup pins those paths and never builds. Packaged installation, release output layouts, migration, and updates are outside this source workflow.

Restore needs NuGet access and writable caches. For a restricted environment, set `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, and `NUGET_HTTP_CACHE_PATH` to writable disposable locations before build/test; do not change the real account's home or credentials. Tests need Unix sockets and child-process permissions. A sandbox that forbids socket binding cannot execute the suite.

## Edit, build, restart

After the one-time source setup:

```sh
./build.sh
awctl service restart
awctl status
```

Restart is explicit; building does not replace the running process. Repeating setup is only needed for missing setup artifacts or intentional non-secret environment changes. For a clean rebuild use `./build.sh rebuild`, then restart. Never independently rebuild one executable and treat it as a supported source installation update. Before changing pre-stable storage formats, follow the existing [database recovery contract](../user/managed-workflows.md); service setup never migrates or deletes data.

For debugging without systemd, first stop the configured service, then run the host in a terminal as the same account with the same XDG locations:

```sh
awctl service stop
dotnet run --project src/AgenticWorkflows.ControlPlane --no-build
```

Wait for `Local Control Interface listening at ...`. In another terminal:

```sh
dotnet run --project src/awctl --no-build -- status
```

Ctrl+C or SIGTERM stops the foreground host. Only after it has stopped, explicitly resume the configured service with `awctl service start`. Never run two hosts against the same runtime directory. Foreground hosting is a debugging equivalent, not an alternative installation.

## Endpoint and application protocol

Both applications discover `$XDG_RUNTIME_DIR/agentic-workflows/control.sock`, or `/tmp/agentic-workflows-UID/control.sock` when XDG runtime is absent or empty. The parent runtime directory must be physical, owned by the current user, and `0700`. The control plane atomically creates its own private directory and `0600` socket. It rejects insecure existing paths and symlinks. Socket paths must fit Linux's 107-byte UTF-8 limit.

A kernel lock on the runtime directory excludes competing hosts and is released after crashes. Startup recovers only an unchanged owned socket after positively refused connection. Normal shutdown removes only the socket created by that host; it preserves replacement paths. Service commands never unlink an endpoint to force a lifecycle success. Check the [source service guide](../user/source-installation.md) for status/log diagnostics and recovery.

`awctl status` prints JSON and returns 0 for a compatible running application, 1 for a diagnostic, and 2 for invalid syntax. It queries application health even when manually hosted. `awctl service status` separately reports user-manager state plus available application health. Neither query starts anything.

The socket adapter uses StreamJsonRpc, a four-byte big-endian length prefix, a 16 KiB body bound, a five-second exchange deadline, and at most 16 active exchanges. Every connection admits one request and at most one reply. Application protocol versioning is distinct from JSON-RPC's `"2.0"` marker and is checked before application dispatch. Mismatch retains both versions and explicit rebuild/restart guidance. Disconnecting a client never cancels accepted Workflow work. See the [Local Control Interface contract](../reference/local-control-interface.md).

## Implementation and tests

`src/AgenticWorkflows` contains application/workflow behavior, infrastructure adapters, local control, and tested source-setup/service modules. `src/AgenticWorkflows.ControlPlane` composes the Generic Host and runtime. `src/awctl` owns CLI parsing. `tools/AgenticWorkflows.SourceTools` exposes only the build seal and source-setup support for the two thin Bash entry scripts. Product validation and automated tests use .NET/xUnit.

Submission stays in the same application/runtime boundary: the CLI requests `pr`, the application persists one frozen intent and correlated Operations, and the Git/GitHub adapters reconcile external effects. SQLite schema 3 owns submission records beneath the Workflow; protocol 2 exposes bounded progress. Tests use temporary repositories, bare remotes and controlled HTTP/harness responses. See the [submission validation matrix](managed-workflow-smoke-test.md) for focused checks and the explicit separation from unverified live smoke testing.

Focused checks after a build:

```sh
dotnet test AgenticWorkflows.slnx --no-build --filter 'FullyQualifiedName~ServiceControlTests|FullyQualifiedName~SourceSetupTests|FullyQualifiedName~SourceScriptTests'
dotnet test AgenticWorkflows.slnx --no-build --filter 'FullyQualifiedName~LocalControlTests|FullyQualifiedName~EndpointTests|FullyQualifiedName~ProcessTests'
bash -n build.sh setup-source.sh
git diff --check
```

Source-script tests create disposable checkouts with spaces and metacharacters, homes, PATH launchers, XDG config/data/runtime locations, and service-manager substitutes. `tests/AgenticWorkflows.ScriptHost` is a test-only .NET executable that records arguments and injects failures; it never invokes the real service manager. Tests exercise root scripts, built-support behavior, generated-unit parsing with `systemd-analyze --user verify`, ownership conflicts, same-checkout reruns, partial failure, and safe removal. Unit parser tests require the systemd tools but do not start a user service. Repository documentation-link tests verify relative Markdown file targets.

All service mutations are controlled substitutions. Real-socket tests validate compatible readiness and protocol mismatch. Workflow/application tests isolate repositories/providers and preserve accepted work across host restart. Tests do not modify the developer's live PATH files, service, AW data, configuration, or credentials. Optional manual verification against a real user manager belongs to a disposable account after review; it is not part of automated validation.

Regression tests retain an uncertain earlier Reviewer after its replacement completes and verify the pre-stop/restart inventory warning. Directory leases explicitly unlock before closing their handles so a descriptor inherited during another thread's fork cannot extend a finished setup operation. Tests cover duplicate descriptors, setup/removal recovery under concurrent subprocess activity, and exclusion of genuinely concurrent setups.
