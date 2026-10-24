using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using StreamJsonRpc;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;

namespace AgenticWorkflows.LocalControl;

[SupportedOSPlatform("linux")]
public sealed class LocalControlClient
{
    private readonly LocalControlEndpoint endpoint;
    private readonly int protocolVersion;
    private readonly TimeSpan exchangeTimeout;

    public LocalControlClient(LocalControlEndpoint endpoint)
        : this(endpoint, LocalControlProtocol.CurrentVersion, LocalControlProtocol.ExchangeTimeout) { }

    internal LocalControlClient(LocalControlEndpoint endpoint, int protocolVersion, TimeSpan exchangeTimeout)
    {
        this.endpoint = endpoint;
        this.protocolVersion = protocolVersion;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<StatusReply> StatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync<StatusReply>("status", new { protocolVersion }, cancellationToken);
        if (response.Status is null || string.IsNullOrWhiteSpace(response.Status.SoftwareVersion) || response.Status.State != "running")
            throw new LocalControlException(new("control_plane_unavailable", "Expected a running status result.",
                "Start or check the control-plane terminal with: dotnet run --project src/AgenticWorkflows.ControlPlane --no-build. Use the same OS account and XDG_RUNTIME_DIR, then retry awctl status.", endpoint.SocketPath));
        return response;
    }

    public Task<WorkflowCommandReply> CommandAsync(WorkflowCommand command, CancellationToken token = default) =>
        ExchangeAsync<WorkflowCommandReply>("workflow.command", new { protocolVersion, command }, token);
    public Task<WorkflowQueryResponse> QueryAsync(WorkflowQuery query, CancellationToken token = default) =>
        ExchangeAsync<WorkflowQueryResponse>("workflow.query", new { protocolVersion, query }, token);
    public Task<DatabaseResponse> DatabaseAsync(DatabaseCommand command, CancellationToken token = default) =>
        ExchangeAsync<DatabaseResponse>("database", new { protocolVersion, command }, token);

    private async Task<T> ExchangeAsync<T>(string method, object parameters, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(exchangeTimeout);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            endpoint.ValidateForClient();
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath), deadline.Token);
            using var stream = new NetworkStream(socket);
            await using var handler = new LocalControlMessageHandler(stream, false, protocolVersion, endpoint.SocketPath, deadline.Token);
            using var rpc = new LocalControlRpc(handler);
            rpc.StartListening();
            JsonElement result;
            try
            {
                // Cancellation closes this local exchange; it does not introduce a notification protocol.
                result = await rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters)
                    .WaitAsync(deadline.Token);
            }
            catch (RemoteInvocationException error) when (error.ErrorCode == -32002)
            {
                if (error.ErrorData is not JsonElement data) throw new JsonException("Missing workflow diagnostic.");
                throw new LocalControlException(data.Deserialize<ControlDiagnostic>(LocalControlProtocol.Json) ?? throw new JsonException("Invalid workflow diagnostic."));
            }
            catch (RemoteInvocationException error) when (error.ErrorCode == LocalControlProtocol.ProtocolMismatch)
            {
                if (error.ErrorData is not JsonElement data)
                    throw new JsonException("Missing application protocol mismatch details.");
                var diagnostic = data.Deserialize<ControlDiagnostic>(LocalControlProtocol.Json);
                if (diagnostic is null || diagnostic.Code != "protocol_mismatch" ||
                    diagnostic.ClientProtocolVersion != protocolVersion || diagnostic.ServerProtocolVersion is not int serverVersion)
                    throw new JsonException("Invalid application protocol mismatch details.");
                throw new LocalControlException(LocalControlProtocol.Mismatch(protocolVersion, serverVersion, endpoint.SocketPath));
            }
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("protocolVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int returnedVersion))
                throw new JsonException("An integer application protocolVersion is required.");
            if (returnedVersion != protocolVersion)
                throw new LocalControlException(LocalControlProtocol.Mismatch(protocolVersion, returnedVersion, endpoint.SocketPath));
            return result.Deserialize<T>(LocalControlProtocol.Json) ?? throw new JsonException("Expected an application result.");
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested &&
            error is OperationCanceledException or ConnectionLostException)
        {
            throw new OperationCanceledException("The local status exchange was canceled.", error, cancellationToken);
        }
        catch (Exception error) when (error is IOException or SocketException or JsonException or InvalidDataException or RemoteRpcException or ConnectionLostException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LocalControlException(new("control_plane_unavailable",
                $"Cannot obtain control-plane status at '{endpoint.SocketPath}': {error.Message}",
                "From the repository root, start or check the control-plane terminal with: dotnet run --project src/AgenticWorkflows.ControlPlane --no-build. " +
                "Use the same OS account and XDG_RUNTIME_DIR in both terminals, then retry awctl status.", endpoint.SocketPath));
        }
    }
}
