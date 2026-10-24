using System.Runtime.Versioning;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.LocalControl;
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
    builder.Services.AddSingleton<LocalControlServer>();
    builder.Services.AddHostedService<LocalControlService>();
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
