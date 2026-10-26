using System.Runtime.Versioning;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Source;

public sealed record ServiceCommand(string Action);
public sealed record ServiceState(string LoadState, string ActiveState, string SubState, string UnitFileState, string FragmentPath, string KillMode);
public sealed record ServiceReply(string Unit, ServiceState Service, string Endpoint, string EndpointState, StatusReply? Application,
    ControlDiagnostic? HealthDiagnostic, ControlDiagnostic? ServiceDiagnostic = null);

/// <summary>Narrow probe seam; all application information still uses the Local Control Interface.</summary>
public interface IServiceApplicationProbe
{
    Task<StatusReply> StatusAsync(CancellationToken token);
    Task<WorkflowQueryResponse> WorkflowsAsync(int offset, CancellationToken token);
}

[SupportedOSPlatform("linux")]
public sealed class ServiceApplicationProbe(LocalControlClient client) : IServiceApplicationProbe
{
    public Task<StatusReply> StatusAsync(CancellationToken token) => client.StatusAsync(token);
    public Task<WorkflowQueryResponse> WorkflowsAsync(int offset, CancellationToken token) =>
        client.QueryAsync(new("/", View: "list", Offset: offset), token);
}

[SupportedOSPlatform("linux")]
public sealed class ServiceControl
{
    // Source-host cold startup can exceed 30 seconds. Bound the complete command, including
    // manager/probe exchanges, while admitting the observed 32-second startup with headroom.
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly IArgumentProcess process;
    private readonly IServiceApplicationProbe probe;
    private readonly LocalControlEndpoint endpoint;
    private readonly string unitPath;
    private readonly Action validateSetup;
    private readonly TimeSpan timeout;
    private readonly TimeSpan interval;
    private readonly TimeProvider time;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private ServiceState state = new("unknown", "unknown", "unknown", "unknown", "", "unknown");
    private string endpointState = "unprobed";
    private ControlDiagnostic? healthDiagnostic;
    private StatusReply? health;

    internal ServiceControl(IArgumentProcess process, IServiceApplicationProbe probe, LocalControlEndpoint endpoint,
        string unitPath, Action validateSetup, TimeSpan? timeout = null, TimeSpan? interval = null,
        TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.process = process; this.probe = probe; this.endpoint = endpoint; this.unitPath = unitPath; this.validateSetup = validateSetup;
        this.timeout = timeout ?? DefaultTimeout; this.interval = interval ?? TimeSpan.FromMilliseconds(200);
        this.time = time ?? TimeProvider.System;
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, this.time, token));
    }

    public static ServiceControl Create()
    {
        if (LinuxFileSystem.UserId == 0) throw new LocalControlException(new("unsupported_service_account",
            "AW service management requires the non-root service account.", "Run awctl as the account that ran ./setup-source.sh."));
        var environment = SourceEnvironment.Current();
        var installation = SourceSetup.ReadInstallation(environment);
        if (installation is null) throw new LocalControlException(new("source_setup_missing", "The AW user service has not been configured.",
            "From the source checkout run ./build.sh, then ./setup-source.sh, then awctl service start."));
        var endpoint = LocalControlEndpoint.FromRuntimeDirectory(installation.Environment.RuntimeDirectory);
        return new(new ArgumentProcess(), new ServiceApplicationProbe(new(endpoint)), endpoint, environment.Unit, () =>
        {
            SourceSetup.ValidateArtifacts(environment, installation);
            if (LinuxFileSystem.Inspect(environment.Unit) is null || LinuxFileSystem.Inspect(environment.Launcher) is null)
                throw new IOException("Source setup is incomplete. From " + installation.Checkout + " run ./setup-source.sh.");
        });
    }

    public async Task<ServiceReply> ExecuteAsync(ServiceCommand command, TextWriter warnings)
    {
        if (command.Action is not ("start" or "stop" or "restart" or "status" or "enable" or "disable")) throw new ArgumentException();
        using var deadline = new CancellationTokenSource(timeout, time);
        try
        {
            validateSetup();
            if (command.Action is "status")
            {
                ControlDiagnostic? managerDiagnostic = null;
                using var managerDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                managerDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try { await ObserveManager(managerDeadline.Token); RequireUnit(); }
                catch (LocalControlException error) { managerDiagnostic = error.Diagnostic; }
                catch (Exception error) when (error is WorkflowException or OperationCanceledException)
                { managerDiagnostic = Failure("service_manager_unavailable", "Cannot obtain user-service state within its deadline.").Diagnostic; }
                await ObserveHealth(deadline.Token);
                return Reply() with { ServiceDiagnostic = managerDiagnostic };
            }
            await ObserveManager(deadline.Token);
            RequireUnit();
            if (command.Action is "enable" or "disable")
            {
                await Manager([command.Action, SourceSetup.UnitName], deadline.Token);
                await ObserveManager(deadline.Token);
                if (command.Action == "enable" && state.UnitFileState is not ("enabled" or "enabled-runtime") ||
                    command.Action == "disable" && state.UnitFileState is not "disabled")
                    throw Failure("service_policy_failed", "The observed automatic-start policy did not match the requested change.");
                return Reply();
            }
            if (command.Action is "stop" or "restart")
            {
                await ReportExecutions(warnings, deadline.Token);
                await Manager(["--no-block", "stop", SourceSetup.UnitName], deadline.Token);
                await WaitStopped(deadline.Token);
                if (command.Action == "stop") return Reply();
            }
            await Manager(["--no-block", "start", SourceSetup.UnitName], deadline.Token);
            while (true)
            {
                await ObserveManager(deadline.Token);
                RequireUnit();
                if (state.ActiveState == "failed") throw Failure("service_failed", "The user service entered the failed state.");
                await ObserveHealth(deadline.Token);
                if (healthDiagnostic?.Code == "protocol_mismatch") throw Failure("protocol_mismatch", healthDiagnostic.Message);
                if (state.ActiveState == "active" && health is not null) return Reply();
                await delay(interval, deadline.Token);
            }
        }
        catch (LocalControlException) { throw; }
        catch (Exception error) when (error is OperationCanceledException or WorkflowException or IOException or UnauthorizedAccessException)
        {
            if (healthDiagnostic is null) endpointState = DescribeEndpoint();
            throw Failure(error is OperationCanceledException ? "service_timeout" : "service_operation_failed", error.Message);
        }
    }

    public async Task<ServiceState> InspectManagerAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { validateSetup(); await ObserveManager(deadline.Token); return state; }
        catch (Exception error) when (error is OperationCanceledException or WorkflowException)
        { throw Failure("service_manager_unavailable", "The user-service state could not be inspected within its deadline."); }
    }

    private async Task WaitStopped(CancellationToken token)
    {
        health = null; healthDiagnostic = null;
        while (true)
        {
            await ObserveManager(token);
            RequireUnit();
            endpointState = DescribeEndpoint();
            if (state.ActiveState == "inactive" && endpointState == "absent") return;
            await delay(interval, token);
        }
    }
    private string DescribeEndpoint()
    {
        try
        {
            var info = LinuxFileSystem.Inspect(endpoint.SocketPath);
            if (info is null) return "absent";
            return (info.Value.Mode & 0xf000) == LinuxFileSystem.SocketType && info.Value.UserId == LinuxFileSystem.UserId ? "socket-present" : "unsafe-path-present";
        }
        catch (IOException) { return "unverifiable"; }
    }
    private async Task ObserveHealth(CancellationToken token)
    {
        health = null;
        try { health = await probe.StatusAsync(token); healthDiagnostic = null; endpointState = "compatible"; }
        catch (LocalControlException error) { healthDiagnostic = error.Diagnostic; endpointState = error.Diagnostic.Code; }
    }
    private async Task ObserveManager(CancellationToken token)
    {
        var result = await process.RunAsync("systemctl", ["--user", "--no-pager", "show",
            "--property=LoadState,ActiveState,SubState,UnitFileState,FragmentPath,KillMode", "--", SourceSetup.UnitName], null, null, token);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf('=');
            if (split <= 0 || !values.TryAdd(line[..split], line[(split + 1)..]))
                throw Failure("service_manager_unavailable", "The user service manager returned invalid state.");
        }
        string Field(string name) => values.TryGetValue(name, out var value) && value.Length <= 4096 && !value.Any(char.IsControl)
            ? value : throw Failure("service_manager_unavailable", "Cannot read " + name + " from the user service manager.");
        state = new(Field("LoadState"), Field("ActiveState"), Field("SubState"), Field("UnitFileState"), Field("FragmentPath"), Field("KillMode"));
        if (state.LoadState == "not-found") throw Failure("source_setup_missing", "The fixed AW user unit is not loaded. Run ./setup-source.sh from the source checkout.");
        if (result.ExitCode != 0) throw Failure("service_manager_unavailable", "systemctl --user show failed with exit " + result.ExitCode + ". Ensure a systemd user session is available.");
    }
    private void RequireUnit()
    {
        if (state.LoadState != "loaded" || state.FragmentPath != unitPath || state.KillMode != "process")
            throw Failure("service_unit_unsafe", "Expected the configured AW user unit with KillMode=process. Inspect unit overrides before retrying ./setup-source.sh.");
    }
    private async Task Manager(string[] arguments, CancellationToken token)
    {
        var result = await process.RunAsync("systemctl", ["--user", "--no-pager", .. arguments], null, null, token);
        if (result.ExitCode != 0)
        {
            await ObserveManager(token);
            await ObserveHealth(token);
            throw Failure("service_command_failed", "systemctl --user " + string.Join(' ', arguments) + " failed with exit " + result.ExitCode + ".");
        }
    }
    private async Task ReportExecutions(TextWriter warnings, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            int offset = 0;
            bool reportedProjectionLimit = false;
            while (true)
            {
                var response = await probe.WorkflowsAsync(offset, deadline.Token);
                if (!reportedProjectionLimit && response.Query.Workflows.Length != 0)
                {
                    reportedProjectionLimit = true;
                    await warnings.WriteLineAsync(JsonSerializer.Serialize(new ControlDiagnostic("external_executions_uncertain",
                        "The Workflow list projects only current operations. Earlier retained or replaced executions may still be active or uncertain, even when the current operation is completed.",
                        "Inspect retained Workflow history and pass reports. Herdr work is preserved and reconciled after restart; service lifecycle requests no interruption or cleanup.")));
                }
                foreach (var workflow in response.Query.Workflows)
                    if (workflow.Execution is { State: ExecutionState.Running or ExecutionState.Unknown } or { Reachable: false } ||
                        workflow.PassId is not null && workflow.OperationStatus != OperationStatus.Completed)
                        await warnings.WriteLineAsync(JsonSerializer.Serialize(new ControlDiagnostic("external_execution_retained",
                            $"Workflow {workflow.Id}; pass {workflow.PassId}; operation {workflow.OperationStatus}; execution {workflow.Execution?.State.ToString() ?? "uncertain"}.",
                            "Herdr executions may survive service stop and are reconciled after restart. Service lifecycle does not interrupt or clean up Workflows.")));
                if (response.Query.Report?.NextOffset is not int next) break;
                if (next <= offset) throw new IOException("Invalid workflow continuation offset.");
                offset = next;
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception error) when (error is LocalControlException or OperationCanceledException or IOException)
        {
            await warnings.WriteLineAsync(JsonSerializer.Serialize(new ControlDiagnostic("external_executions_uncertain",
                "Active external executions could not be fully inspected before service lifecycle.",
                "Work is preserved and may continue under Herdr. Inspect awctl workflows after restart; no interruption or cleanup is requested.")));
        }
        await warnings.FlushAsync();
    }
    private ServiceReply Reply() => new(SourceSetup.UnitName, state, endpoint.SocketPath, endpointState, health, healthDiagnostic);
    private LocalControlException Failure(string code, string message) => new(new(code,
        message + " Observed service=" + JsonSerializer.Serialize(state) + "; endpoint=" + endpoint.SocketPath + "; endpoint state=" +
        (endpointState == "unprobed" ? DescribeEndpoint() : endpointState) + ".",
        "Inspect: systemctl --user status agentic-workflows.service; " + SourceSetup.Logs +
        ". For missing or partial setup, run ./setup-source.sh from the configured checkout. " +
        "For protocol mismatch run ./build.sh there, then awctl service restart. Never delete an occupied socket to force startup.",
        endpoint.SocketPath, healthDiagnostic?.ClientProtocolVersion, healthDiagnostic?.ServerProtocolVersion));
}
