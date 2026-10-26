using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Workflows;
using StreamJsonRpc;

namespace AgenticWorkflows.Tests;

public sealed class WorkflowIntegrationTests
{
    [Fact]
    public async Task Pr_CLI_uses_versioned_protocol_and_disconnected_command_remains_owned_by_control_plane()
    {
        await using var fixture = new WorkflowFixture(); Guid id = await SubmissionPolicyTests.Clean(fixture);
        var parsed = Assert.IsType<WorkflowCommand>(AwctlCommand.Parse(["pr", id.ToString(), "--force"]));
        Assert.Equal("pr", parsed.Action); Assert.True(parsed.Force); Assert.Equal(id, parsed.WorkflowId);
        Assert.Null(Assert.IsType<WorkflowCommand>(AwctlCommand.Parse(["pr"])).WorkflowId);
        Assert.Throws<ArgumentException>(() => AwctlCommand.Parse(["pr", "--draft"]));
        Assert.Throws<ArgumentException>(() => AwctlCommand.Parse(["pr", "--max-passes", "2"]));
        await using var socket = new SocketFixture();
        var runtime = new ControlledRuntime(fixture) { HoldReply = true };
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(), socket.Endpoint, runtime);
        await server.StartAsync();
        using (var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket.Endpoint.SocketPath));
            using var stream = new NetworkStream(connection);
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0", id = 1, method = "workflow.command",
                @params = new { protocolVersion = LocalControlProtocol.CurrentVersion, command = new WorkflowCommand("pr", fixture.Root, id) }
            }, LocalControlProtocol.Json);
            await LocalControlProtocol.WriteAsync(stream, request, default);
            await runtime.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        runtime.Release.SetResult();
        var done = await fixture.Until(id, d => d.State.Submission?.PullRequest is not null);
        Assert.Equal(runtime.Last!.OperationId, done.State.CurrentOperationId);
        var query = await socket.Client().QueryAsync(new(fixture.Root, id));
        Assert.Equal(done.State.Submission!.PullRequest, query.Query.Workflows.Single().Submission!.PullRequest);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(query, LocalControlProtocol.Json).Length < LocalControlProtocol.MaximumFrameBytes);
    }
    [Fact]
    public async Task Unicode_history_summaries_remain_bounded_on_the_actual_local_transport()
    {
        await using var fixture = new WorkflowFixture();
        Guid id = await fixture.Start(); await fixture.Pass(id, 1);
        await fixture.Application.DisposeAsync();
        var state = fixture.Application.Get(id).State;
        long after = fixture.Application.Get(id).Events.Last().Position;
        string expanded = new('漢', 1000);
        fixture.Store.Save(state.Version, new(state with { Version = state.Version + 1 },
            Enumerable.Range(0, 10).Select(_ => new WorkflowEvent(0, "DecisionEvidence", expanded, state.CurrentOperationId, null, DateTimeOffset.UtcNow)).ToArray(), [], [], [], [], []));
        await using var socket = new SocketFixture();
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(), socket.Endpoint, new ControlledRuntime(fixture));
        await server.StartAsync();
        var page = await socket.Client().QueryAsync(new(fixture.Root, id, "history", After: after));
        Assert.Equal(10, page.Query.History.Length);
        Assert.All(page.Query.History, item => Assert.StartsWith(new string('漢', 100), item.Summary));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, LocalControlProtocol.Json).Length < LocalControlProtocol.MaximumFrameBytes);
    }

    [Fact]
    public async Task Project_configuration_must_be_ignored_and_untracked_in_the_canonical_repository()
    {
        string root = Path.Combine(Path.GetTempPath(), "aw-ignore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var process = new ArgumentProcess();
        async Task Git(params string[] arguments) => Assert.Equal(0, (await process.RunAsync("git", arguments, root, null, default)).ExitCode);
        try
        {
            await Git("init", "-b", "main");
            await Git("remote", "add", "origin", "https://github.com/example/isolated.git");
            await File.WriteAllTextAsync(Path.Combine(root, WorkflowConfiguration.ProjectFilename), "# ordinary settings");
            var repository = new GitWorkflowRepository(process);
            Assert.Equal("configuration_not_ignored", (await Assert.ThrowsAsync<WorkflowException>(() => repository.InspectCanonicalAsync(root, default))).Code);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), WorkflowConfiguration.ProjectFilename + "\n");
            Assert.Equal(root, (await repository.InspectCanonicalAsync(root, default)).Checkout);
            await Git("add", "--force", WorkflowConfiguration.ProjectFilename);
            Assert.Equal("configuration_not_ignored", (await Assert.ThrowsAsync<WorkflowException>(() => repository.InspectCanonicalAsync(root, default))).Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Git_tree_capture_preserves_index_and_tracks_modes_deletions_and_untracked_content()
    {
        string root = Path.Combine(Path.GetTempPath(), "aw-git-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var process = new ArgumentProcess();
        async Task<string> Git(params string[] args)
        {
            var result = await process.RunAsync("git", args, root, null, default);
            Assert.Equal(0, result.ExitCode); return result.Output.Trim();
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "AW test"); await Git("config", "user.email", "aw-test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "base");
            await File.WriteAllTextAsync(Path.Combine(root, "deleted.txt"), "delete me");
            await Git("add", "--all"); await Git("commit", "-m", "isolated test fixture");
            string baseline = await Git("rev-parse", "HEAD");
            string directory = await Git("rev-parse", "--absolute-git-dir");
            await Git("remote", "add", "origin", "https://github.com/example/isolated.git");
            var repository = new GitWorkflowRepository(process);
            var workspace = new WorkspaceIdentity("local:test", "workspace", root, "main", directory);
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "index"));
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "proposed");
            await File.WriteAllTextAsync(Path.Combine(root, "new file.txt"), "new file"); File.Delete(Path.Combine(root, "deleted.txt"));
            var first = await repository.CaptureAsync(workspace, baseline, default);
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "index")));
            Assert.NotEqual(first.Target.BaselineTree, first.Target.ProposedTree);
            Assert.Equal(new[] { "deleted.txt", "new file.txt", "tracked.txt" }, first.ChangedPaths);
            await Git("add", "--all");
            var staged = await repository.CaptureAsync(workspace, baseline, default);
            Assert.Equal(first.Target, staged.Target); Assert.NotEqual(first.IndexIdentity, staged.IndexIdentity);
            await Git("config", "core.filemode", "false");
            File.SetUnixFileMode(Path.Combine(root, "tracked.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var modeChanged = await repository.CaptureAsync(workspace, baseline, default);
            Assert.NotEqual(first.Target.ProposedTree, modeChanged.Target.ProposedTree);
            File.CreateSymbolicLink(Path.Combine(root, "link"), "tracked.txt");
            var symlinkChanged = await repository.CaptureAsync(workspace, baseline, default);
            Assert.NotEqual(modeChanged.Target.ProposedTree, symlinkChanged.Target.ProposedTree);
            Assert.Equal(baseline, await Git("rev-parse", "HEAD"));
            await Git("config", "core.filemode", "true");
            await Git("add", "--all"); await Git("commit", "-m", "isolated local work");
            string localTip = await Git("rev-parse", "HEAD");
            await Git("update-ref", "refs/remotes/origin/main", localTip);
            var missingRemote = new GitWorkflowRepository(new RemoteEvidenceProcess(process, ""));
            Assert.True(await missingRemote.HasUniqueWorkAsync(workspace, baseline, "https://github.com/example/isolated.git", default));
            var provenRemote = new GitWorkflowRepository(new RemoteEvidenceProcess(process, localTip + "\trefs/heads/main\n"));
            Assert.False(await provenRemote.HasUniqueWorkAsync(workspace, baseline, "https://github.com/example/isolated.git", default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Process_boundary_passes_provider_text_as_one_literal_argument()
    {
        string value = "provider value; $(printf injected) `printf injected`\nwith newline";
        var result = await new ArgumentProcess().RunAsync("printf", ["%s", value], Path.GetTempPath(), null, default);
        Assert.Equal(0, result.ExitCode); Assert.Equal(value, result.Output);
    }

    [Fact]
    public async Task Workflow_commands_queries_and_application_errors_use_real_JSON_RPC_socket()
    {
        await using var fixture = new WorkflowFixture();
        await using var socket = new SocketFixture();
        var runtime = new ControlledRuntime(fixture);
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(), socket.Endpoint, runtime);
        await server.StartAsync();
        var client = socket.Client();
        await client.CommandAsync(new("setup", fixture.Root));
        var accepted = await client.CommandAsync(new("start", fixture.Root, TaskIdentifier: "DEV-123"));
        Assert.NotNull(accepted.WorkflowId); Assert.NotNull(accepted.OperationId);
        Guid id = accepted.WorkflowId.Value;
        await fixture.Pass(id, 1);
        var query = await client.QueryAsync(new(fixture.Root, id));
        Assert.Equal(id, query.Query.Workflows.Single().Id);
        var error = await Assert.ThrowsAsync<LocalControlException>(() => client.CommandAsync(new("start", fixture.Root, TaskIdentifier: "DEV-123")));
        Assert.Equal("duplicate_workflow", error.Diagnostic.Code);
        Assert.Equal("running", (await client.StatusAsync()).Status.State);
    }

    [Fact]
    public async Task Client_disconnect_does_not_cancel_durable_accepted_work()
    {
        await using var fixture = new WorkflowFixture(); await fixture.Application.ExecuteAsync(new("setup", fixture.Root));
        await using var socket = new SocketFixture();
        var runtime = new ControlledRuntime(fixture) { HoldReply = true };
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(), socket.Endpoint, runtime);
        await server.StartAsync();
        using (var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket.Endpoint.SocketPath));
            using var stream = new NetworkStream(connection);
            await LocalControlProtocol.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0", id = 1, method = "workflow.command",
                @params = new { protocolVersion = LocalControlProtocol.CurrentVersion, command = new WorkflowCommand("start", fixture.Root, TaskIdentifier: "DEV-123") }
            }, LocalControlProtocol.Json), default);
            await runtime.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        runtime.Release.TrySetResult();
        Guid id = runtime.Last!.WorkflowId!.Value;
        await fixture.Pass(id, 1); await fixture.Deliver(id, WorkflowFixture.Ready()); await fixture.Pass(id, 2);
        Assert.Equal(2, fixture.Providers.Launches);
    }

    [Fact]
    public async Task Version_gate_precedes_new_workflow_dispatch()
    {
        await using var fixture = new WorkflowFixture(); await using var socket = new SocketFixture();
        var runtime = new ControlledRuntime(fixture);
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(), socket.Endpoint, runtime); await server.StartAsync();
        using var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket.Endpoint.SocketPath));
        using var stream = new NetworkStream(connection);
        using var handler = new LengthHeaderMessageHandler(stream, stream, new SystemTextJsonFormatter { JsonSerializerOptions = LocalControlProtocol.Json });
        using var rpc = new JsonRpc(handler); rpc.StartListening();
        var error = await Assert.ThrowsAsync<RemoteInvocationException>(() => rpc.InvokeWithParameterObjectAsync<object>("workflow.command", new { protocolVersion = 999, unknown = "future command" }));
        Assert.Equal(-32001, error.ErrorCode); Assert.Equal(0, runtime.Calls);
    }

    private sealed class RemoteEvidenceProcess(IArgumentProcess inner, string remote) : IArgumentProcess
    {
        public Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            executable == "git" && arguments[0] == "ls-remote" ? Task.FromResult(new ProcessOutput(0, remote)) :
            inner.RunAsync(executable, arguments, directory, environment, cancellationToken);
    }

    private sealed class ControlledRuntime(WorkflowFixture fixture) : IWorkflowRuntime
    {
        internal int Calls;
        internal bool HoldReply;
        internal WorkflowAcceptance? Last;
        internal TaskCompletionSource Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkflowAcceptance> ExecuteAsync(WorkflowCommand command)
        {
            Calls++; Last = await fixture.Application.ExecuteAsync(command); Accepted.TrySetResult();
            if (HoldReply) await Release.Task;
            return Last;
        }
        public Task<WorkflowQueryReply> QueryAsync(WorkflowQuery query) => new WorkflowQueries(fixture.Application, fixture.Providers).ExecuteAsync(query);
        public Task<DatabaseReply> DatabaseAsync(DatabaseCommand command) => Task.FromResult(new DatabaseReply("ready", "Controlled database"));
    }
}
