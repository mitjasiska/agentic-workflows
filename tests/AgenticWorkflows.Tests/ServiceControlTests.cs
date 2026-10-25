using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Source;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class ServiceControlTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("status")]
    [InlineData("enable")]
    [InlineData("disable")]
    public void Service_namespace_preserves_application_commands(string action)
    {
        Assert.Equal(action, Assert.IsType<ServiceCommand>(AwctlCommand.Parse(["service", action])).Action);
        Assert.Null(AwctlCommand.Parse(["status"]));
        var start = Assert.IsType<WorkflowCommand>(AwctlCommand.Parse(["start", "DEV-126"]));
        Assert.Equal("start", start.Action);
        Assert.Equal("DEV-126", start.TaskIdentifier);
        Assert.IsType<WorkflowCommand>(AwctlCommand.Parse(["setup"]));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("service,kill")]
    [InlineData("service,start,--server,elsewhere")]
    [InlineData("service,status,another.service")]
    [InlineData("status,extra")]
    [InlineData("start")]
    public async Task Invalid_service_syntax_returns_usage_without_manager_invocation(string text)
    {
        await using var f = new Fixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await AwctlCommand.RunAsync(text.Split(','), output, error, service: f.Service()));
        Assert.Contains("usage", error.ToString()); Assert.Empty(output.ToString()); Assert.Empty(f.Manager.Calls);
    }

    [Fact]
    public async Task Start_requires_active_manager_and_compatible_health()
    {
        await using var f = new Fixture();
        f.Probe.UnavailableCalls = 2;
        var reply = await f.Service().ExecuteAsync(new("start"), TextWriter.Null);
        Assert.Equal("compatible", reply.EndpointState); Assert.Equal(3, f.Probe.StatusCalls);
        Assert.Equal(["start"], f.Manager.Actions);
        Assert.All(f.Manager.Calls, c => { Assert.Equal("systemctl", c.Executable); Assert.Contains("--user", c.Arguments); Assert.Contains(SourceSetup.UnitName, c.Arguments); });
    }

    [Fact]
    public async Task Active_unit_without_endpoint_times_out_with_observed_states_and_logs()
    {
        await using var f = new Fixture(); f.Probe.UnavailableCalls = int.MaxValue;
        var error = await Assert.ThrowsAsync<LocalControlException>(() => f.Service(TimeSpan.FromMilliseconds(70)).ExecuteAsync(new("start"), TextWriter.Null));
        Assert.Equal("service_timeout", error.Diagnostic.Code); Assert.Contains("active", error.Message);
        Assert.Contains(f.Socket.Endpoint.SocketPath, error.Message); Assert.Contains("control_plane_unavailable", error.Message);
        Assert.Contains(SourceSetup.Logs, error.Diagnostic.Guidance);
    }

    [Fact]
    public async Task Real_socket_readiness_accepts_matching_protocol()
    {
        await using var f = new Fixture(); await f.Socket.StartAsync();
        var service = f.Service(probe: new ServiceApplicationProbe(f.Socket.Client()));
        Assert.Equal("running", (await service.ExecuteAsync(new("start"), TextWriter.Null)).Application!.Status.State);
    }

    [Fact]
    public async Task Healthy_endpoint_does_not_complete_start_until_manager_is_active()
    {
        await using var f = new Fixture();
        int polls = 0;
        f.Manager.OnAction = _ => f.Manager.Active = "activating";
        f.Manager.OnShow = () =>
        {
            if (f.Manager.Active == "activating" && ++polls == 3) f.Manager.Active = "active";
            return Task.CompletedTask;
        };
        var reply = await f.Service().ExecuteAsync(new("start"), TextWriter.Null);
        Assert.Equal("active", reply.Service.ActiveState); Assert.Equal(3, polls); Assert.Equal(3, f.Probe.StatusCalls);
    }

    [Fact]
    public async Task Real_protocol_mismatch_is_immediate_and_keeps_both_versions()
    {
        await using var f = new Fixture(); await f.Socket.StartAsync(42);
        var service = f.Service(probe: new ServiceApplicationProbe(f.Socket.Client()));
        var error = await Assert.ThrowsAsync<LocalControlException>(() => service.ExecuteAsync(new("start"), TextWriter.Null));
        Assert.Equal("protocol_mismatch", error.Diagnostic.Code);
        Assert.Equal(1, error.Diagnostic.ClientProtocolVersion); Assert.Equal(42, error.Diagnostic.ServerProtocolVersion);
        Assert.Contains("./build.sh", error.Diagnostic.Guidance); Assert.Equal(0, f.Socket.Query.Calls);
        Assert.Equal(["start"], f.Manager.Actions);
    }

    [Fact]
    public async Task Restart_waits_for_old_socket_removal_before_starting()
    {
        await using var f = new Fixture(); await f.Socket.StartAsync();
        int stoppedProbes = 0;
        f.Manager.OnShow = async () =>
        {
            if (f.Manager.Active == "inactive" && ++stoppedProbes == 3) await f.Socket.Server!.DisposeAsync();
        };
        f.Manager.OnAction = action =>
        {
            if (action == "start") Assert.Null(LinuxFileSystem.Inspect(f.Socket.Endpoint.SocketPath));
        };
        await f.Service().ExecuteAsync(new("restart"), TextWriter.Null);
        Assert.Equal(["stop", "start"], f.Manager.Actions); Assert.Equal(3, stoppedProbes);
        Assert.Equal(1, f.Probe.QueryCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_preserves_lingering_socket_or_unrelated_file_and_never_reports_completion(bool socket)
    {
        await using var f = new Fixture();
        LinuxFileSystem.CreatePrivateDirectory(f.Socket.Endpoint.RuntimeDirectory);
        using var stale = socket ? LinuxFileSystem.Bind(f.Socket.Endpoint.SocketPath) : null;
        if (!socket) File.WriteAllText(f.Socket.Endpoint.SocketPath, "keep");
        var before = LinuxFileSystem.Inspect(f.Socket.Endpoint.SocketPath)!.Value;
        var error = await Assert.ThrowsAsync<LocalControlException>(() => f.Service(TimeSpan.FromMilliseconds(50)).ExecuteAsync(new("stop"), TextWriter.Null));
        Assert.Equal("service_timeout", error.Diagnostic.Code); Assert.Contains("inactive", error.Message);
        Assert.Contains(socket ? "socket-present" : "unsafe-path-present", error.Message);
        Assert.True(before.SameFile(LinuxFileSystem.Inspect(f.Socket.Endpoint.SocketPath)!.Value));
        Assert.Equal(["stop"], f.Manager.Actions);
    }

    [Fact]
    public async Task Stop_waits_until_manager_is_inactive_even_without_socket()
    {
        await using var f = new Fixture();
        f.Manager.OnAction = action => { if (action == "stop") f.Manager.Active = "deactivating"; };
        int polls = 0;
        f.Manager.OnShow = () => { if (f.Manager.Active == "deactivating" && ++polls == 3) f.Manager.Active = "inactive"; return Task.CompletedTask; };
        Assert.Equal("absent", (await f.Service().ExecuteAsync(new("stop"), TextWriter.Null)).EndpointState);
        Assert.Equal(3, polls);
    }

    [Theory]
    [InlineData("enable", "active")]
    [InlineData("disable", "active")]
    [InlineData("enable", "inactive")]
    [InlineData("disable", "inactive")]
    public async Task Policy_changes_leave_process_state_alone_and_do_not_probe_application(string action, string active)
    {
        await using var f = new Fixture(); f.Manager.Active = active;
        var reply = await f.Service().ExecuteAsync(new(action), TextWriter.Null);
        Assert.Equal(active, reply.Service.ActiveState); Assert.Equal(action == "enable" ? "enabled" : "disabled", reply.Service.UnitFileState);
        Assert.Equal([action], f.Manager.Actions); Assert.Equal(0, f.Probe.StatusCalls + f.Probe.QueryCalls);
        Assert.DoesNotContain(f.Manager.Calls, c => c.Arguments.Contains("--now"));
    }

    [Theory]
    [InlineData("active", false, 0)]
    [InlineData("inactive", true, 1)]
    [InlineData("active", true, 1)]
    [InlineData("failed", false, 1)]
    public async Task Service_status_prints_manager_and_available_health_with_truthful_exit(string active, bool unavailable, int exit)
    {
        await using var f = new Fixture(); f.Manager.Active = active; f.Probe.UnavailableCalls = unavailable ? 1 : 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(exit, await AwctlCommand.RunAsync(["service", "status"], output, error, service: f.Service()));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(active, json.RootElement.GetProperty("service").GetProperty("activeState").GetString());
        Assert.Equal(unavailable, json.RootElement.GetProperty("application").ValueKind == JsonValueKind.Null);
        Assert.Empty(f.Manager.Actions); Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("unavailable", "service_manager_unavailable")]
    [InlineData("missing", "source_setup_missing")]
    [InlineData("unsafe", "service_unit_unsafe")]
    public async Task Service_status_keeps_reachable_health_when_manager_state_cannot_be_used(string failure, string diagnostic)
    {
        await using var f = new Fixture(); f.Manager.Failure = failure;
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await AwctlCommand.RunAsync(["service", "status"], output, error, service: f.Service()));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("running", json.RootElement.GetProperty("application").GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(diagnostic, json.RootElement.GetProperty("serviceDiagnostic").GetProperty("code").GetString());
        Assert.Empty(f.Manager.Actions); Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("missing", "source_setup_missing")]
    [InlineData("unavailable", "service_manager_unavailable")]
    [InlineData("malformed", "service_manager_unavailable")]
    [InlineData("unsafe", "service_unit_unsafe")]
    [InlineData("failed", "service_failed")]
    [InlineData("action-failed", "service_command_failed")]
    public async Task Failed_or_unavailable_units_produce_actionable_cli_errors(string failure, string code)
    {
        await using var f = new Fixture(); f.Manager.Failure = failure;
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await AwctlCommand.RunAsync(["service", "start"], output, error, service: f.Service()));
        var diagnostic = JsonSerializer.Deserialize<ControlDiagnostic>(error.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(code, diagnostic.Code); Assert.Contains(SourceSetup.Logs, diagnostic.Guidance); Assert.Contains("endpoint", diagnostic.Message);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Missing_source_setup_fails_before_manager_access()
    {
        await using var f = new Fixture();
        var service = new ServiceControl(f.Manager, f.Probe, f.Socket.Endpoint, f.Manager.UnitPath,
            () => throw new IOException("Incomplete source setup. Run ./setup-source.sh."));
        var error = await Assert.ThrowsAsync<LocalControlException>(() => service.ExecuteAsync(new("start"), TextWriter.Null));
        Assert.Contains("./setup-source.sh", error.Diagnostic.Guidance); Assert.Empty(f.Manager.Calls);
    }

    [Fact]
    public async Task Stop_reports_active_and_uncertain_executions_before_manager_action_across_pages()
    {
        await using var workflow = new WorkflowFixture();
        Guid id = await workflow.Start();
        await workflow.Pass(id, 1);
        var summary = await new WorkflowQueries(workflow.Application, workflow.Providers).Summary(id);
        await using var f = new Fixture();
        Guid uncertainId = Guid.NewGuid();
        f.Probe.Pages = [summary, summary with { Id = uncertainId, Execution = null, OperationStatus = OperationStatus.Pending }];
        using var warnings = new StringWriter();
        f.Manager.OnAction = _ =>
        {
            Assert.Contains(id.ToString(), warnings.ToString());
            Assert.Contains(uncertainId.ToString(), warnings.ToString());
            Assert.Contains("execution uncertain", warnings.ToString());
        };
        await f.Service().ExecuteAsync(new("stop"), warnings);
        Assert.Equal(3, f.Probe.QueryCalls); Assert.Equal(["stop"], f.Manager.Actions);
        Assert.Contains("Herdr", warnings.ToString());
    }

    [Fact]
    public async Task Unreachable_application_is_reported_as_uncertain_before_stop()
    {
        await using var f = new Fixture(); f.Probe.QueryUnavailable = true;
        using var warnings = new StringWriter();
        f.Manager.OnAction = _ => Assert.Contains("external_executions_uncertain", warnings.ToString());
        await f.Service().ExecuteAsync(new("stop"), warnings);
        Assert.Equal(["stop"], f.Manager.Actions);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task Completed_replacement_reviewer_does_not_silence_uncertainty_about_earlier_execution(string action)
    {
        await using var workflow = new WorkflowFixture();
        Guid id = await workflow.Start(); await workflow.Pass(id, 1);
        await workflow.Deliver(id, WorkflowFixture.Ready());
        var reviewing = await workflow.Pass(id, 2);
        var earlier = reviewing.Passes[1];
        workflow.Providers.SessionObservations[earlier.SessionId] = ExecutionState.Unknown;
        await workflow.Application.ExecuteAsync(new("select", workflow.Root, id, Role: AgentRole.Reviewer, Session: "new"));
        await workflow.Application.ExecuteAsync(new("review", workflow.Root, id));
        var replacement = await workflow.Pass(id, 3);
        await workflow.Deliver(id, WorkflowFixture.Clean());
        await workflow.Until(id, d => d.State.Phase == WorkflowPhase.Submission);

        var data = workflow.Application.Get(id);
        var retained = data.Operations.Single(o => o.Id == earlier.OperationId);
        Assert.Equal(OperationStatus.Running, retained.Status);
        Assert.Equal(ExecutionState.Unknown, retained.Observation!.State);
        var probe = new WorkflowProbe(workflow);
        var summary = (await probe.WorkflowsAsync(0, default)).Query.Workflows.Single();
        Assert.Equal(OperationStatus.Completed, summary.OperationStatus);
        Assert.Equal(ExecutionState.Completed, summary.Execution!.State);

        await using var f = new Fixture(); using var warnings = new StringWriter();
        f.Manager.OnAction = _ =>
        {
            var warning = JsonSerializer.Deserialize<ControlDiagnostic>(warnings.ToString())!;
            Assert.Equal("external_executions_uncertain", warning.Code);
            Assert.Contains("only current operations", warning.Message);
            Assert.Contains("Earlier retained or replaced executions", warning.Message);
        };
        await f.Service(probe: probe).ExecuteAsync(new(action), warnings);
        Assert.Equal(action == "restart" ? ["stop", "start"] : new[] { "stop" }, f.Manager.Actions);
        Assert.Equal(3, workflow.Providers.Launches);
        Assert.Equal(retained, workflow.Application.Get(id).Operations.Single(o => o.Id == earlier.OperationId));
        Assert.Equal(replacement.Passes[2].Id, workflow.Application.Get(id).State.Review!.PassId);
        Assert.DoesNotContain("interrupt", workflow.Providers.Calls);
        Assert.DoesNotContain("teardown", workflow.Providers.Calls);
    }

    [Fact]
    public async Task Empty_workflow_inventory_needs_no_projection_uncertainty_warning()
    {
        await using var f = new Fixture(); using var warnings = new StringWriter();
        await f.Service().ExecuteAsync(new("stop"), warnings);
        Assert.Empty(warnings.ToString());
    }

    private sealed class WorkflowProbe(WorkflowFixture workflow) : IServiceApplicationProbe
    {
        public Task<StatusReply> StatusAsync(CancellationToken token) => Task.FromResult(new StatusReply(1, new("running", "test")));
        public async Task<WorkflowQueryResponse> WorkflowsAsync(int offset, CancellationToken token) =>
            new(1, await new WorkflowQueries(workflow.Application, workflow.Providers).ExecuteAsync(new("/", View: "list", Offset: offset), token));
    }

    internal sealed class Manager : IArgumentProcess
    {
        internal string UnitPath = "/tmp/owned unit/agentic-workflows.service";
        internal string Active = "active", Policy = "enabled", Failure = "";
        internal Action<string>? OnAction;
        internal Func<Task>? OnShow;
        internal List<(string Executable, string[] Arguments)> Calls = [];
        internal List<string> Actions = [];
        public async Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((executable, arguments.ToArray()));
            if (arguments.Contains("show"))
            {
                if (OnShow is not null) await OnShow();
                if (Failure == "unavailable") return new(1, "");
                if (Failure == "malformed") return new(0, "LoadState=loaded\nLoadState=evil");
                return new(0, $"LoadState={(Failure == "missing" ? "not-found" : "loaded")}\nActiveState={(Failure == "failed" ? "failed" : Active)}\nSubState=running\nUnitFileState={Policy}\nFragmentPath={UnitPath}\nKillMode={(Failure == "unsafe" ? "control-group" : "process")}\n");
            }
            string action = arguments.Single(a => a is "start" or "stop" or "enable" or "disable");
            Actions.Add(action);
            if (Failure == "action-failed") return new(1, "untrusted-secret-output");
            if (action == "start") Active = "active";
            if (action == "stop") Active = "inactive";
            if (action == "enable") Policy = "enabled";
            if (action == "disable") Policy = "disabled";
            OnAction?.Invoke(action);
            return new(0, "");
        }
    }
    private sealed class Probe : IServiceApplicationProbe
    {
        internal int StatusCalls, QueryCalls, UnavailableCalls;
        internal bool QueryUnavailable;
        internal WorkflowSummary[] Pages = [];
        public Task<StatusReply> StatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++StatusCalls <= UnavailableCalls) throw new LocalControlException(new("control_plane_unavailable", "unavailable", "start explicitly"));
            return Task.FromResult(new StatusReply(1, new("running", "test")));
        }
        public Task<WorkflowQueryResponse> WorkflowsAsync(int offset, CancellationToken token)
        {
            QueryCalls++;
            if (QueryUnavailable) throw new LocalControlException(new("control_plane_unavailable", "unavailable", "start explicitly"));
            return Task.FromResult(new WorkflowQueryResponse(1, new(Pages.Skip(offset).Take(1).ToArray(), [], null,
                new("", offset, offset < Pages.Length ? offset + 1 : null))));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal SocketFixture Socket = new(); internal Manager Manager = new(); internal Probe Probe = new();
        internal ServiceControl Service(TimeSpan? timeout = null, IServiceApplicationProbe? probe = null) =>
            new(Manager, probe ?? Probe, Socket.Endpoint, Manager.UnitPath, () => { }, timeout ?? TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(2));
        public ValueTask DisposeAsync() => Socket.DisposeAsync();
    }
}
