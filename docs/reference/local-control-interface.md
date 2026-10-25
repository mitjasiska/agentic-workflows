# Local Control Interface contract

This reference describes the implemented Linux status, workflow, and database-maintenance methods. It is a pre-stable contract between co-packaged `awctl` and control-plane builds, not a stable third-party API or a promise of cross-version compatibility. [ADR 0038](../architecture/adr/0038-adopt-json-rpc-for-local-control.md) records the accepted design; the [development guide](../development/local-control-plane.md) covers running and recovering the processes.

## Transport and bounds

The endpoint is `$XDG_RUNTIME_DIR/agentic-workflows/control.sock`, or `/tmp/agentic-workflows-UID/control.sock` when XDG is unset or empty. It is a Linux Unix-domain stream socket, owned by the effective OS user, with mode `0600` in a physical owner-only `0700` runtime directory. A configured XDG parent must also be owned, physical, and `0700`. The UTF-8 socket pathname limit is 107 bytes. Endpoint discovery, the lifetime directory lock, conservative stale-socket recovery, and identity-checked cleanup are unchanged by JSON-RPC adoption. There is no network endpoint or remote selection option.

Each connection admits one request and sends at most one reply, then closes. Additional pipelined requests are not dispatched. Each frame is a four-byte signed positive big-endian length followed by exactly that many bytes of UTF-8 JSON. The body limit is 16,384 bytes in both directions; an invalid declared length is rejected before body allocation or reading. JSON depth is limited to 16. Duplicate properties, comments, and trailing commas are rejected. Fragmented headers and bodies are reassembled within the same deadline.

The client has a five-second deadline covering connection, request, and reply. The server has a five-second deadline from accepting each connection, at most 16 active exchanges, and a listen backlog of 16. A stalled peer is disconnected; a partial or disconnected exchange need not receive a reply. If even an error with the echoed ID cannot fit the output limit (for example, after escaping a large string ID), the exchange closes without sending an oversized frame. Caller cancellation closes the client exchange and preserves cancellation to its caller. No cancellation notification is sent. Closing a client connection does not confer ownership of an accepted Operation on that client or cancel it.

## Request and successful result

Messages use [JSON-RPC 2.0](https://www.jsonrpc.org/specification). The `jsonrpc` marker must be the exact string `"2.0"`. Method and parameter names are case-sensitive. Request `id` must be a string or a signed 64-bit integer; null, fractional, and other IDs are outside this profile. The server echoes the ID in its reply and the library correlates client responses by ID.

Named `params` are required and contain the signed 32-bit integer `protocolVersion`. The current AW application protocol version is **1**, distinct from the JSON-RPC marker. `status` accepts no additional parameters; positional arrays are not supported. Requests accept only the envelope members `jsonrpc`, `id`, `method`, and `params`; library extension metadata is outside this profile. Methods beginning with `$/` or `rpc.` are rejected after the application-version gate, before the library can interpret extensions.

Request body (the binary length prefix is omitted here):

```json
{"jsonrpc":"2.0","id":1,"method":"status","params":{"protocolVersion":1}}
```

Successful response body:

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"status":{"state":"running","softwareVersion":"0.1.0"}}}
```

`softwareVersion` is the application assembly's informational version and may include a revision suffix. The client checks `result.protocolVersion` before interpreting the remaining application result. `awctl status` prints the result projection itself, preserving its existing stdout shape and exit code 0:

```json
{"protocolVersion":1,"status":{"state":"running","softwareVersion":"0.1.0"}}
```

The old `{"protocolVersion":1,"command":"status"}` custom envelope is no longer accepted. The application version remains 1 for this initial status projection; it does not imply compatibility with the superseded envelope.

## Compatibility and errors

After validating the JSON-RPC request shape, the adapter checks `params.protocolVersion` before method lookup, binding the remaining method parameters, or invoking any application handler. An incompatible version wins over an unknown method or a future parameter schema. Missing or non-integer versions are invalid parameters. There is no negotiation or fallback.

A version mismatch response has the request ID, no `result`, and this shape:

```json
{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"awctl protocol version 42 does not match control-plane protocol version 1.","data":{"code":"protocol_mismatch","message":"awctl protocol version 42 does not match control-plane protocol version 1.","guidance":"Update awctl and the control plane together with ./build.sh from the configured source checkout, then run awctl service restart and retry awctl status.","endpoint":"/run/user/1001/agentic-workflows/control.sock","clientProtocolVersion":42,"serverProtocolVersion":1}}}
```

The endpoint and version values depend on the exchange. Error messages are diagnostic text, not identifiers to parse. A JSON-RPC response contains exactly one of `result` or `error`.

| JSON-RPC code | Meaning in this profile | Response ID |
| --- | --- | --- |
| `-32700` | JSON parsing failure, including duplicate properties or excessive depth | `null` |
| `-32600` | Invalid request/marker/ID, batch, or invalid/incomplete frame | `null` |
| `-32601` | Unknown method for a compatible application version | Request ID |
| `-32602` | Missing/invalid application version or incompatible method parameters | Request ID |
| `-32603` | Unexpected application failure | Request ID |
| `-32001` | Incompatible AW application protocol version | Request ID; version diagnostic in `error.data` |
| `-32002` | Application validation or recoverable workflow refusal | Request ID; code, message, and recovery guidance in `error.data` |

No notification is dispatched. A structurally valid notification (a request without `id`) is closed without a reply, including unknown methods and incompatible application versions. Batches are rejected as a whole with `-32600` and `id:null`; no element executes. There are no subscriptions, streaming results, or server callbacks. This intentionally restricted request/reply profile does not advertise the optional product behaviors of a general RPC endpoint.

Invalid frames/messages, disconnects, incorrect response IDs, unsupported protocol markers, unexpected responses, and deadlines produce an actionable `control_plane_unavailable` client diagnostic with the expected endpoint and explicit `awctl service status`/`awctl service start` recovery guidance. Wrong response IDs cannot satisfy the outstanding request and expire within the deadline. A valid application-version mismatch produces `protocol_mismatch` with both versions and update/restart guidance. Application-command diagnostics go to stderr with exit code 1 and no status on stdout; invalid CLI syntax returns 2. The client never starts a control plane implicitly.

## Implementation boundary

The adapter uses StreamJsonRpc 2.26.150 with `SystemTextJsonFormatter`. `LocalControlMessageHandler` implements bounded framing and admission policy through the library's message-handler extension point. StreamJsonRpc performs request correlation, parameter binding, method dispatch, and message serialization. `LocalControlRpc` disables cancellation notifications, maps application diagnostics to `-32002`, and maps unexpected exceptions to internal errors. Application handlers remain independent of RPC and sockets.

`awctl service start|stop|restart|status|enable|disable` is a separate fixed-unit Linux host-management adapter, not a JSON-RPC method. Its application-health and retained-execution queries use the existing methods here; lifecycle and startup-policy changes go directly to the systemd user manager. See [source setup and service operation](../user/source-installation.md).

## Workflow and database methods

| Method | Additional named parameter | Result projection |
| --- | --- | --- |
| `workflow.command` | `command` | `protocolVersion`, nullable `workflowId`/`operationId`, `message`, bounded current `workflow`, optional cleanup component outcomes |
| `workflow.query` | `query` | `protocolVersion`, `query` containing bounded workflows/history/report pages and continuation positions |
| `database` | `command` | `protocolVersion`, `database` containing state and diagnostic/maintenance result |

`workflow.command.command` has `action`, `workingDirectory`, and optional `workflowId`, `taskIdentifier`, `maxPasses`, `sourceDecision`, `cancellationDecision`, `decisionResponse`, `role`, `model`, `session`, and `force`. Actions are `setup`, `start`, `resume`, `continue`, `review`, `select`, `interrupt`, `abandon`, and `cleanup`. Only `start` uses a Task identifier. Existing work resolves from an explicit immutable Workflow ID or a proven managed worktree. `role` is `Implementer` or `Reviewer`; enum integers and unknown fields are rejected. See [usage](../user/managed-workflows.md) for the applicable option scopes.

`workflow.query.query` has `workingDirectory`, optional `workflowId`, `view` (default `workflow`), `after` (history position), optional `passId`, and `offset`. Views are `workflow`, `list`, `history`, `snapshot`, `handoff`, `report`, `warnings`, and `attention`. Text pages contain at most 1,000 UTF-16 characters without splitting a surrogate pair. History pages contain at most ten bounded summaries. List pages contain one bounded projection. `nextOffset` and `nextPosition` identify another page; null means no continuation. Full reports are assembled from immutable stored envelopes rather than editable sidecars. Readiness is derived from current Git and review evidence at query time.

`TaskSnapshotRetained` history entries carry `sourceRetention: {sourceRevision, sourceFingerprint, snapshotId}`; other entries use null. The revision is bounded to 100 characters in the history projection, with the complete value retained in immutable SQLite evidence. The fingerprint and snapshot identify the requirements examined and the snapshot explicitly retained. `reviewValid` and `submissionReadiness` compare accepted materialized content/mode evidence as well as the Git Review Target; post-review HEAD and staging changes alone do not revoke eligibility.

`database.command` has `action` (`status`, `reset`, or `restore`), optional invocation-scoped `force`, and optional `backup` path. A maintenance-only host can serve these methods when schema/integrity admission blocks ordinary workflows. Reset requires `force:true`; restore requires an explicit verified compatible backup. The old database and companion files are archived before replacement.

Mutation acceptance is durable. When it creates an Operation, the reply names that Operation and the current projection. Agent work continues after CLI exit or socket disconnection. A timeout never means cancellation or proves that an action did not happen; inspect retained Workflow state before retrying. The five-second transport deadline does not cancel an admitted application handler. No streaming, notification, batch, or subscription interface is added. Null facts are represented explicitly in workflow replies.

CLI exit codes remain 0 for success, 1 for application/transport errors, and 2 for usage errors. Partial cleanup returns 3 with structured component outcomes. Nothing in this interface authorizes commit, push, pull-request creation, or Linear completion.
