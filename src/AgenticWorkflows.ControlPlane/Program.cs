using System.Runtime.Versioning;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("The initial Agentic Workflows control plane supports Linux.");
    return 1;
}

LocalControlEndpoint? endpoint = null;
try
{
    if (args.Length != 0)
        throw new ArgumentException("The control plane accepts no command-line options in this slice.");
    endpoint = LocalControlEndpoint.Discover();
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton(endpoint);
    builder.Services.AddSingleton<IControlPlaneStatusQuery, ControlPlaneStatusQuery>();
    builder.Services.AddSingleton(_ => CreateWorkflowRuntime());
    builder.Services.AddSingleton<IWorkflowRuntime>(provider => provider.GetRequiredService<WorkflowRuntime>());
    builder.Services.AddSingleton<LocalControlServer>();
    builder.Services.AddHostedService<LocalControlService>();
    builder.Services.AddHostedService<WorkflowRuntimeService>();
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new ControlDiagnostic("control_plane_failed",
        exception.Message, "Check the existing control-plane process and local runtime directory ownership and permissions, then restart: " +
        "dotnet run --project src/AgenticWorkflows.ControlPlane --no-build", endpoint?.SocketPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 1;
}

static WorkflowRuntime CreateWorkflowRuntime()
{
    string dataRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    string configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    string data = Path.Combine(dataRoot, "agentic-workflows");
    var process = new ArgumentProcess();
    var repository = new GitWorkflowRepository(process);
    var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
    return new(Path.Combine(data, "workflows.db"), new WorkflowConfiguration(Path.Combine(configRoot, "agentic-workflows", "global.toml"), data),
        repository, new LinearGateway(client), new GitHubGateway(client), new HerdrGateway(process, repository), new PassDelivery());
}

internal sealed class WorkflowRuntimeService(WorkflowRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("AW_DATABASE_MAINTENANCE") == "1") runtime.MaintenanceOnly(); else runtime.Start();
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken) => await runtime.DisposeAsync();
}
[SupportedOSPlatform("linux")]
internal sealed class LocalControlService(LocalControlServer server, LocalControlEndpoint endpoint,
    ILogger<LocalControlService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await server.StartAsync(cancellationToken);
        logger.LogInformation("Local Control Interface listening at {Endpoint}", endpoint.SocketPath);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await server.Completion.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await server.DisposeAsync(); }
    }
}
