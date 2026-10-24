using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using AgenticWorkflows.Application;

namespace AgenticWorkflows.LocalControl;

[SupportedOSPlatform("linux")]
public sealed class LocalControlServer : IAsyncDisposable
{
    private readonly IControlPlaneStatusQuery query;
    private readonly LocalControlEndpoint endpoint;
    private readonly int protocolVersion;
    private readonly TimeSpan exchangeTimeout;
    private readonly CancellationTokenSource stopping = new();
    private LocalControlEndpoint.EndpointLease? lease;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public LocalControlServer(IControlPlaneStatusQuery query, LocalControlEndpoint endpoint)
        : this(query, endpoint, LocalControlProtocol.CurrentVersion, LocalControlProtocol.ExchangeTimeout) { }

    internal LocalControlServer(IControlPlaneStatusQuery query, LocalControlEndpoint endpoint,
        int protocolVersion, TimeSpan exchangeTimeout)
    {
        this.query = query;
        this.endpoint = endpoint;
        this.protocolVersion = protocolVersion;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (lease is not null || stopping.IsCancellationRequested)
            throw new InvalidOperationException("This local control server has already started or stopped.");
        lease = await endpoint.ListenAsync(cancellationToken);
        Completion = ServeAsync(lease.Listener, stopping.Token);
    }

    private async Task ServeAsync(Socket listener, CancellationToken cancellationToken)
    {
        var clients = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Bound both active exchanges and the number of retained tasks.
                if (clients.Count >= 16)
                    await (await Task.WhenAny(clients)).WaitAsync(cancellationToken);
                clients.RemoveAll(task => task.IsCompletedSuccessfully);
                var socket = await listener.AcceptAsync(cancellationToken);
                clients.Add(HandleAsync(socket, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(clients);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        using (var stream = new NetworkStream(socket))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(exchangeTimeout);
            try
            {
                Response response;
                try
                {
                    var body = await LocalControlProtocol.ReadAsync(stream, deadline.Token);
                    using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                        !version.TryGetInt32(out int requestedVersion))
                        throw new JsonException("An integer protocolVersion is required.");

                    // Check the envelope before interpreting or dispatching an application request.
                    if (requestedVersion != protocolVersion)
                        response = new(protocolVersion, Error: LocalControlProtocol.Mismatch(requestedVersion, protocolVersion, endpoint.SocketPath));
                    else
                    {
                        var request = LocalControlProtocol.Deserialize<Request>(body);
                        response = request.Command == "status"
                            ? new(protocolVersion, query.Execute())
                            : new(protocolVersion, Error: new("unknown_command", "The local command is not supported.", "Use awctl status."));
                    }
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or EndOfStreamException)
                {
                    response = new(protocolVersion, Error: new("invalid_request", "The Local Control Interface request is malformed or exceeds its bounds.",
                        "Update awctl and the control plane together, then retry awctl status."));
                }
                await LocalControlProtocol.WriteAsync(stream, response, deadline.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                // A disconnected or stalled client owns no service lifetime. Its deadline closes this exchange.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        try { await Completion; }
        finally
        {
            lease?.Dispose();
            lease = null;
        }
    }
}
