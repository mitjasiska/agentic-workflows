# Local .NET development

This first application slice supports Linux and provides a control-plane process and the local `awctl status` command. It does not require an HTTP listener, database, integration credentials, or a service installation.

## Prerequisites and build

Use Linux with the .NET 10 SDK (10.0.100 or a later stable 10.0 feature band, as selected by `global.json`). The implementation uses Linux `statx` (kernel 4.11 or later with libc support) for filesystem ownership checks. Validation for this slice uses Ubuntu 24.04, Linux 6.8, and SDK 10.0.113. Windows and macOS are not supported by this implementation.

From the repository root:

```sh
dotnet restore AgenticWorkflows.slnx
dotnet build AgenticWorkflows.slnx --no-restore
dotnet test AgenticWorkflows.slnx --no-build
```

Restore needs access to NuGet. Tests need permission to create local Unix sockets and start child processes; a sandbox that denies socket binding cannot run this suite. Tests use private temporary directories under `/tmp` and do not contact a developer's running control plane. No test requires root or external services.

## Run in two terminals

Run both terminals as the same OS user, with the same `XDG_RUNTIME_DIR`, from the repository root. After building, start the control plane in terminal one:

```sh
dotnet run --project src/AgenticWorkflows.ControlPlane --no-build
```

Wait for `Local Control Interface listening at ...`. The process stays running until Ctrl+C or SIGTERM.

In terminal two:

```sh
dotnet run --project src/awctl --no-build -- status
```

The built executable can also be invoked directly:

```sh
./src/awctl/bin/Debug/net10.0/awctl status
```

Successful output is JSON on stdout, with exit code 0:

```json
{"protocolVersion":1,"status":{"state":"running","softwareVersion":"0.1.0"}}
```

The software version comes from the application assembly and may include a build revision suffix. The protocol version belongs to the Local Control Interface envelope. Failures produce a JSON diagnostic on stderr and exit code 1; invalid command syntax returns 2. `awctl --help` shows the command syntax.

## Endpoint discovery and recovery

Both programs automatically select `$XDG_RUNTIME_DIR/agentic-workflows/control.sock`. If `XDG_RUNTIME_DIR` is unset or empty, they use `/tmp/agentic-workflows-UID/control.sock`, where `UID` is the effective Unix user ID. `XDG_RUNTIME_DIR` is an OS runtime-directory convention, not a server URL. There is no remote CLI configuration or transport selection.

The runtime directory must be a physical directory owned by the current user with mode `0700`; when XDG is set, its directory must meet the same requirements. The control plane creates its own private subdirectory atomically and creates a `0600` socket inside it. Existing insecure directories or symlinks are refused, not repaired silently. The socket pathname must fit Linux's 107-byte UTF-8 limit.

The control plane holds an exclusive kernel lock on its runtime directory for its lifetime. It preserves active sockets, ordinary files, directories, and symlinks at the expected socket path. A stale, owned socket is removed only after a connection is positively refused and its filesystem identity is rechecked. Normal shutdown removes only the socket created by that process; the private directory remains for later starts. After a crash, the kernel releases the lock and the next startup safely recovers the stale socket.

If `awctl status` reports `control_plane_unavailable`, its diagnostic includes the expected endpoint and the exact development start command. Check the control-plane terminal for startup failures, start it with the command above, and confirm that both terminals use the same account and runtime directory. The CLI never starts the service implicitly.

If it reports `protocol_mismatch`, the diagnostic includes the client and server protocol versions. Stop the old process, restore and rebuild both executables together, then start the rebuilt control plane and retry the status command. Only an exact protocol-version match is accepted, before the application query runs.

If startup reports an occupied or unsafe path, inspect that exact path and the existing process. Stop the active control plane before restarting it. Do not delete an active endpoint or an unrelated file to force startup. This slice supplies no systemd unit or lifecycle commands; service packaging is later work.

## Implementation and tests

The solution has four projects with executable behavior:

- `src/AgenticWorkflows` contains the transport-independent `IControlPlaneStatusQuery` and its implementation under `Application/`, and the Local Control Interface adapter under `LocalControl/`. A later HTTP adapter can invoke the same query; the query has no socket, serialization, endpoint, or protocol dependency.
- `src/AgenticWorkflows.ControlPlane` composes the query and socket server through a .NET Generic Host and dependency injection.
- `src/awctl` is the local command-line executable.
- `tests/AgenticWorkflows.Tests` uses xUnit and the .NET test SDK for real-socket and separate-process tests.

Each connection carries one request and one reply. Frames have a four-byte signed positive big-endian byte length, followed by UTF-8 JSON, with a 16 KiB body limit and a five-second exchange deadline. The server allows at most 16 active exchanges and a bounded listen backlog. A request is `{"protocolVersion":1,"command":"status"}`. A reply carries `protocolVersion` and either `status` or `error`; diagnostics contain `code`, `message`, and `guidance`, plus endpoint and version details when applicable. Malformed, duplicate, and unsupported properties are rejected for the current version. Mismatches are rejected before interpreting the command schema. These framing and envelope details are provisional implementation choices.

For focused execution:

```sh
dotnet test AgenticWorkflows.slnx --no-build --filter FullyQualifiedName~LocalControlTests
dotnet test AgenticWorkflows.slnx --no-build --filter FullyQualifiedName~EndpointTests
dotnet test AgenticWorkflows.slnx --no-build --filter FullyQualifiedName~ProcessTests
```

Protocol tests inject endpoint and protocol settings in code, without exposing test configuration as CLI flags. Coverage includes successful version exchange, unavailable and incompatible services with no dispatch, malformed and fragmented frames, timeouts, private permissions, conservative stale recovery, preservation of active or unrelated paths, and real process shutdown/restart behavior. Run the complete suite for this initial slice using the command in the build section.
