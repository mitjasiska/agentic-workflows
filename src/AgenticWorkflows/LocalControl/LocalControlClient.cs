using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(exchangeTimeout);
        try
        {
            endpoint.ValidateForClient();
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath), deadline.Token);
            using var stream = new NetworkStream(socket);
            await LocalControlProtocol.WriteAsync(stream, new Request(protocolVersion, "status"), deadline.Token);
            var body = await LocalControlProtocol.ReadAsync(stream, deadline.Token);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("protocolVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int serverVersion))
                throw new JsonException("An integer protocolVersion is required.");
            if (serverVersion != protocolVersion)
                throw new LocalControlException(LocalControlProtocol.Mismatch(protocolVersion, serverVersion, endpoint.SocketPath));
            var response = LocalControlProtocol.Deserialize<Response>(body);
            if (response.Error is not null && response.Status is null)
                throw new LocalControlException(response.Error);
            if (response.Status is null || response.Error is not null ||
                string.IsNullOrWhiteSpace(response.Status.SoftwareVersion) || response.Status.State != "running")
                throw new JsonException("Expected a status result or a diagnostic.");
            return new(response.ProtocolVersion, response.Status);
        }
        catch (Exception error) when (error is IOException or SocketException or JsonException or InvalidDataException ||
            (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new LocalControlException(new("control_plane_unavailable",
                $"Cannot obtain control-plane status at '{endpoint.SocketPath}': {error.Message}",
                "From the repository root, start or check the control-plane terminal with: dotnet run --project src/AgenticWorkflows.ControlPlane --no-build. " +
                "Use the same OS account and XDG_RUNTIME_DIR in both terminals, then retry awctl status.", endpoint.SocketPath));
        }
    }
}
