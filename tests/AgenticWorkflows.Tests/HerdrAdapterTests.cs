using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class HerdrAdapterTests
{
    [Fact]
    public async Task Missing_target_is_rejected_without_creating_or_contacting_a_named_session()
    {
        var process = new StructuredHerdr { TargetRunning = false };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var error = await Assert.ThrowsAsync<WorkflowException>(() => gateway.InspectTargetAsync("local:test", default));
        Assert.Equal("herdr_target_unavailable", error.Code);
        Assert.Single(process.Commands); Assert.Equal(new[] { "session", "list", "--json" }, process.Commands[0]);
    }

    [Fact]
    public async Task Fresh_launch_uses_exact_resource_ids_and_needs_no_hook()
    {
        var process = new StructuredHerdr();
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var workspace = new WorkspaceIdentity("local:test", "workspace", "/tmp/owned-workspace", "aw/DEV-123/whole", "/tmp/repo/.git", [new("root", "root-terminal")]);
        var session = new AgentSession(Guid.NewGuid(), AgentRole.Implementer, "workspace", "pane", "terminal", null, "default", DateTimeOffset.UtcNow, DeliveryRoot: "/tmp/aw-delivery");
        var pass = new ManagedPass(Guid.NewGuid(), Guid.NewGuid(), AgentRole.Implementer, Guid.NewGuid(), session.Id,
            new("default", "medium", "new", "warn"), new("A complete durable handoff", "brief", new("head", ["AGENTS.md"])),
            "/tmp/aw-delivery/operation/result.json", null, [], DateTimeOffset.UtcNow);
        await gateway.StartPassAsync(workspace, session, pass, default);
        var launch = Assert.Single(process.Commands, c => c.Contains("start"));
        Assert.Contains("codex", launch); Assert.Contains("pane", launch);
        Assert.Contains("--add-dir", launch); Assert.Contains("/tmp/aw-delivery", launch);
        var prompt = Assert.Single(process.Commands, c => c.Contains("prompt"));
        Assert.Contains("handoff.txt", prompt[^1]);
        Assert.DoesNotContain(process.Commands.SelectMany(c => c), argument => argument.Contains("integration", StringComparison.Ordinal) || argument.Contains("hook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_pane_without_native_session_proof_does_not_authorize_reuse()
    {
        var process = new StructuredHerdr { AgentKind = "codex" };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var workspace = new WorkspaceIdentity("local:test", "workspace", "/tmp/owned-workspace", "aw/DEV-123/whole", "/tmp/repo/.git");
        var retained = new AgentSession(Guid.NewGuid(), AgentRole.Implementer, "workspace", "pane", "terminal", null, "default", DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<WorkflowException>(() => gateway.PrepareSessionAsync(workspace, AgentRole.Implementer, new("default", "medium", "reuse", "warn"), retained, default));
        Assert.Equal("session_reuse_unavailable", error.Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("prompt") || c.Contains("start") || c.Contains("split"));
    }

    [Fact]
    public async Task Mismatched_terminal_identity_blocks_prompt_and_interruption()
    {
        var process = new StructuredHerdr();
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var workspace = new WorkspaceIdentity("local:test", "workspace", "/tmp/owned-workspace", "aw/DEV-123/whole", "/tmp/repo/.git");
        var replaced = new AgentSession(Guid.NewGuid(), AgentRole.Implementer, "workspace", "pane", "different-terminal", null, "default", DateTimeOffset.UtcNow);
        Assert.Equal("session_identity_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.InterruptAsync(workspace, replaced, default))).Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("send-keys"));
    }

    [Fact]
    public async Task Changed_native_session_in_an_owned_pane_does_not_authorize_interruption()
    {
        var process = new StructuredHerdr { AgentKind = "codex" };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var workspace = new WorkspaceIdentity("local:test", "workspace", "/tmp/owned-workspace", "aw/DEV-123/whole", "/tmp/repo/.git");
        var retained = new AgentSession(Guid.NewGuid(), AgentRole.Implementer, "workspace", "pane", "terminal", "verified-native-session", "default", DateTimeOffset.UtcNow);
        Assert.Equal("execution_identity_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.InterruptAsync(workspace, retained, default))).Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("send-keys"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("replacement")]
    public async Task Native_session_is_rechecked_after_preparation_before_prompt(string? replacement)
    {
        var process = new StructuredHerdr { AgentKind = "codex", NativeValue = "expected" };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, pass) = LaunchFacts("native:codex:session:expected");
        await gateway.PrepareSessionAsync(workspace, session.Role, pass.Configuration, session, default);
        process.NativeValue = replacement;
        Assert.Equal("session_reuse_unavailable", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.StartPassAsync(workspace, session, pass, default))).Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("prompt") || c.Contains("start"));
    }

    [Fact]
    public async Task Fresh_selection_never_adopts_an_existing_idle_conversation()
    {
        var process = new StructuredHerdr { AgentKind = "codex", NativeValue = "unmanaged" };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, pass) = LaunchFacts(null);
        Assert.Equal("session_reuse_unavailable", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.StartPassAsync(workspace, session, pass, default))).Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("prompt") || c.Contains("start"));
    }

    [Fact]
    public async Task Reused_session_has_one_persisted_grant_covering_distinct_operation_directories()
    {
        var process = new StructuredHerdr();
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, first) = LaunchFacts(null);
        await gateway.StartPassAsync(workspace, session, first, default);
        process.NativeValue = "expected";
        session = session with { NativeResumeReference = "native:codex:session:expected" };
        var next = first with { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), DeliveryPath = "/tmp/aw-delivery/second/result.json", Configuration = first.Configuration with { SessionPolicy = "reuse" } };
        await gateway.PrepareSessionAsync(workspace, session.Role, next.Configuration, session, default);
        await gateway.StartPassAsync(workspace, session, next, default);
        var start = Assert.Single(process.Commands, c => c.Contains("start"));
        string grant = start[Array.IndexOf(start, "--add-dir") + 1];
        Assert.Equal(session.DeliveryRoot, grant);
        Assert.StartsWith(grant + "/", first.DeliveryPath); Assert.StartsWith(grant + "/", next.DeliveryPath);
        Assert.NotEqual(Path.GetDirectoryName(first.DeliveryPath), Path.GetDirectoryName(next.DeliveryPath));
        Assert.Equal(2, process.Commands.Count(c => c.Contains("prompt")));
        var escaped = next with { DeliveryPath = "/tmp/ungranted/result.json" };
        Assert.Equal("session_delivery_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.StartPassAsync(workspace, session, escaped, default))).Code);
        Assert.Equal(2, process.Commands.Count(c => c.Contains("prompt")));
    }

    [Fact]
    public async Task Changed_reasoning_requires_explicit_new_session_instead_of_false_effective_configuration()
    {
        var process = new StructuredHerdr { AgentKind = "codex", NativeValue = "expected" };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, pass) = LaunchFacts("native:codex:session:expected");
        pass = pass with { Configuration = pass.Configuration with { Reasoning = "high" } };
        Assert.Equal("session_reuse_unavailable", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.PrepareSessionAsync(workspace, session.Role, pass.Configuration, session, default))).Code);
        Assert.Equal("session_reuse_unavailable", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.StartPassAsync(workspace, session, pass, default))).Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("prompt") || c.Contains("start"));
    }

    [Theory]
    [InlineData("workspace get", "{}")]
    [InlineData("workspace get", "{\"workspace\":[]}")]
    [InlineData("api snapshot", "{\"snapshot\":{\"panes\":null}}")]
    [InlineData("agent get", "{\"agent\":null}")]
    public async Task Invalid_structured_shapes_are_recoverable_gateway_diagnostics(string command, string output)
    {
        var process = new StructuredHerdr { InvalidCommand = command, InvalidOutput = output };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, _) = LaunchFacts(null);
        Assert.Equal("herdr_invalid_output", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.ObserveAsync(workspace, session, default))).Code);
        Assert.DoesNotContain(process.Commands, c => c.Contains("prompt") || c.Contains("remove"));
    }

    [Theory]
    [InlineData("working", false)]
    [InlineData("unknown", false)]
    [InlineData("idle", true)]
    public async Task Ordinary_teardown_refuses_active_uncertain_or_extra_foreground_work(string status, bool extraProcess)
    {
        var process = new StructuredHerdr { AgentKind = "codex", NativeValue = "expected", AgentStatus = status, ExtraProcess = extraProcess };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, _) = LaunchFacts("native:codex:session:expected");
        await Assert.ThrowsAsync<WorkflowException>(() => gateway.TeardownAsync(workspace, [session], false, default));
        Assert.DoesNotContain(process.Commands, c => c.Contains("remove"));
    }

    [Fact]
    public async Task Ordinary_teardown_accepts_verified_idle_conversation_and_process_boundary()
    {
        var process = new StructuredHerdr { AgentKind = "codex", NativeValue = "expected" };
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, session, _) = LaunchFacts("native:codex:session:expected");
        await gateway.TeardownAsync(workspace, [session], false, default);
        var command = Assert.Single(process.Commands, c => c.Contains("process-info"));
        Assert.Equal(new[] { "--session", "test", "pane", "process-info", "--pane", "pane" }, command);
        Assert.Single(process.Commands, c => c.Contains("remove") && c.Contains("--workspace") && c.Contains("workspace"));
    }

    [Fact]
    public async Task Ordinary_teardown_preflights_original_shell_processes_too()
    {
        var process = new StructuredHerdr();
        var gateway = new HerdrGateway(process, new ControlledProviders("/tmp"));
        var (workspace, _, _) = LaunchFacts(null);
        workspace = workspace with { InitialPanes = [new("pane", "terminal")] };
        await Assert.ThrowsAsync<WorkflowException>(() => gateway.TeardownAsync(workspace, [], false, default));
        Assert.DoesNotContain(process.Commands, c => c.Contains("remove"));
    }

    private static (WorkspaceIdentity Workspace, AgentSession Session, ManagedPass Pass) LaunchFacts(string? native)
    {
        var workspace = new WorkspaceIdentity("local:test", "workspace", "/tmp/owned-workspace", "aw/DEV-123/whole", "/tmp/repo/.git");
        var session = new AgentSession(Guid.NewGuid(), AgentRole.Implementer, "workspace", "pane", "terminal", native, "default", DateTimeOffset.UtcNow, DeliveryRoot: "/tmp/aw-delivery");
        var pass = new ManagedPass(Guid.NewGuid(), Guid.NewGuid(), session.Role, Guid.NewGuid(), session.Id,
            new("default", "medium", "new", "warn"), new("handoff", "brief", new("head", [])), "/tmp/aw-delivery/first/result.json", null, [], DateTimeOffset.UtcNow);
        return (workspace, session, pass);
    }

    private sealed class StructuredHerdr : IArgumentProcess
    {
        internal bool TargetRunning = true;
        internal string? AgentKind;
        internal string? NativeValue;
        internal string AgentStatus = "idle";
        internal bool ExtraProcess;
        internal string? InvalidCommand;
        internal string? InvalidOutput;
        internal List<string[]> Commands = [];
        public Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Assert.Equal("herdr", executable); Commands.Add(arguments.ToArray());
            object result;
            if (arguments is ["session", "list", "--json"])
                return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new { sessions = new[] { new { name = "test", running = TargetRunning } } })));
            Assert.Equal("--session", arguments[0]); Assert.Equal("test", arguments[1]);
            if (string.Join(" ", arguments.Skip(2).Take(2)) == InvalidCommand)
                return Task.FromResult(new ProcessOutput(0, InvalidOutput!));
            if (arguments.Skip(2).Take(2).SequenceEqual(["workspace", "get"]))
                result = new { workspace = new { workspace_id = "workspace", worktree = new { checkout_path = "/tmp/owned-workspace" } } };
            else if (arguments.Skip(2).Take(2).SequenceEqual(["pane", "get"]))
                result = new { pane = new { workspace_id = "workspace", pane_id = "pane", terminal_id = "terminal" } };
            else if (arguments.Skip(2).Take(2).SequenceEqual(["agent", "get"]))
                result = new { agent = new { workspace_id = "workspace", pane_id = "pane", terminal_id = "terminal", agent = AgentKind, agent_status = AgentStatus,
                    agent_session = NativeValue is null ? null : new { source = "native", agent = "codex", kind = "session", value = NativeValue } } };
            else if (arguments.Skip(2).Take(2).SequenceEqual(["api", "snapshot"]))
                result = new { snapshot = new { panes = new[] { new { workspace_id = "workspace", pane_id = "pane", terminal_id = "terminal" } } } };
            else if (arguments.Skip(2).Take(2).SequenceEqual(["pane", "process-info"]))
            {
                Assert.Equal(new[] { "--pane", "pane" }, arguments.Skip(4));
                result = new { process_info = new { pane_id = "pane", shell_pid = 1, foreground_processes = ExtraProcess ? new[] { new { pid = 2, name = "codex" }, new { pid = 3, name = "other" } } : new[] { new { pid = 2, name = "codex" } } } };
            }
            else if (arguments.Skip(2).Take(2).SequenceEqual(["agent", "start"]))
            { AgentKind = "codex"; result = new { acknowledged = true }; }
            else result = new { acknowledged = true };
            return Task.FromResult(new ProcessOutput(0, JsonSerializer.Serialize(new { result })));
        }
    }
}
