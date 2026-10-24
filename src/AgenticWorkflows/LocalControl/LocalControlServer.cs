using System.Net.Sockets;
using System.Runtime.Versioning;
using StreamJsonRpc;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;

namespace AgenticWorkflows.LocalControl;

[SupportedOSPlatform("linux")]
public sealed class LocalControlServer : IAsyncDisposable
{
    private readonly IControlPlaneStatusQuery query;
    private readonly LocalControlEndpoint endpoint;
    private readonly int protocolVersion;
    private readonly TimeSpan exchangeTimeout;
    private readonly IWorkflowRuntime? workflows;
    private readonly CancellationTokenSource stopping = new();
    private LocalControlEndpoint.EndpointLease? lease;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public LocalControlServer(IControlPlaneStatusQuery query, LocalControlEndpoint endpoint)
        : this(query, endpoint, LocalControlProtocol.CurrentVersion, LocalControlProtocol.ExchangeTimeout) { }

    public LocalControlServer(IControlPlaneStatusQuery query, LocalControlEndpoint endpoint, IWorkflowRuntime workflows)
        : this(query, endpoint, LocalControlProtocol.CurrentVersion, LocalControlProtocol.ExchangeTimeout) { this.workflows = workflows; }

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
                await using var handler = new LocalControlMessageHandler(stream, true, protocolVersion, endpoint.SocketPath, deadline.Token);
                using var rpc = new LocalControlRpc(handler);
                rpc.AddLocalRpcMethod("status", (Func<int, StatusReply>)Status);
                if (workflows is not null)
                {
                    rpc.AddLocalRpcMethod("workflow.command", (Func<int, WorkflowCommand, Task<WorkflowCommandReply>>)Command);
                    rpc.AddLocalRpcMethod("workflow.query", (Func<int, WorkflowQuery, Task<WorkflowQueryResponse>>)Query);
                    rpc.AddLocalRpcMethod("database", (Func<int, DatabaseCommand, Task<DatabaseResponse>>)Database);
                }
                rpc.StartListening();
                await (await Task.WhenAny(handler.ResponseWritten, rpc.Completion)).WaitAsync(deadline.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ConnectionLostException or InvalidDataException)
            {
                // A disconnected or stalled client owns no service lifetime. Its deadline closes this exchange.
            }
        }
    }

    private StatusReply Status(int protocolVersion) => new(this.protocolVersion, query.Execute());

    private async Task<WorkflowCommandReply> Command(int protocolVersion, WorkflowCommand command)
    {
        var accepted = await workflows!.ExecuteAsync(command);
        WorkflowSummary? summary = null;
        if (accepted.Current is not null)
        {
            var projection = await workflows.QueryAsync(new(command.WorkingDirectory, accepted.WorkflowId));
            summary = projection.Workflows.Single();
        }
        return new(this.protocolVersion, accepted.WorkflowId, accepted.OperationId, accepted.Message, summary, accepted.ComponentOutcomes);
    }
    private async Task<WorkflowQueryResponse> Query(int protocolVersion, WorkflowQuery query) => new(this.protocolVersion, await workflows!.QueryAsync(query));
    private async Task<DatabaseResponse> Database(int protocolVersion, DatabaseCommand command) => new(this.protocolVersion, await workflows!.DatabaseAsync(command));

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
