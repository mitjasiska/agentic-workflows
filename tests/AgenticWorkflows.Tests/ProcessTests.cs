using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class ProcessTests
{
    [Fact]
    public async Task Retained_pending_intent_survives_process_restart_and_is_not_replayed()
    {
        await using var workflow = new WorkflowFixture();
        workflow.Providers.FailLinear = true;
        Guid id = await workflow.Start();
        var stopped = await workflow.Until(id, d => d.State.Recovery is not null);
        await workflow.Application.DisposeAsync();
        var pending = new WorkflowOperation(Guid.NewGuid(), stopped.State.CurrentOperationId, "Startup", OperationStatus.Pending,
            null, false, null, null, DateTimeOffset.UtcNow, null);
        // Simulate a crash immediately after the accepted continuation transaction, before its external action.
        workflow.Store.Save(stopped.State.Version, new(stopped.State with { CurrentOperationId = pending.Id, Recovery = null, Version = stopped.State.Version + 1 },
            [new(0, "WorkflowResumed", "Accepted continuation fixture", pending.Id, null, DateTimeOffset.UtcNow)], [], [pending], [], [], []));
        await using var socket = new SocketFixture();
        string database = Path.Combine(socket.Root, "data", "agentic-workflows", "workflows.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.Copy(Path.Combine(workflow.Root, "workflows.db"), database);
        var endpoint = LocalControlEndpoint.FromRuntimeDirectory(socket.Root);
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = new ChildProcess("AgenticWorkflows.ControlPlane.dll", socket.Root);
            await host.WaitForListeningAsync();
            var client = new LocalControlClient(endpoint);
            WorkflowSummary? summary = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (summary?.Recovery?.Code != "restart_continuation")
            {
                summary = (await client.QueryAsync(new(socket.Root, id))).Query.Workflows.Single();
                if (summary.Recovery?.Code != "restart_continuation") await Task.Delay(20, deadline.Token);
            }
            Assert.Equal(pending.Id, summary.OperationId);
            Assert.Equal(OperationStatus.Pending, summary.OperationStatus);
            Assert.Equal(0, summary.TotalPasses);
            Assert.Contains("awctl resume " + id, summary.Recovery!.Commands);
            await host.KillAsync();
        }
    }

    [Fact]
    public async Task Executables_discover_endpoint_exchange_status_and_shut_down_cleanly()
    {
        await using var fixture = new SocketFixture();
        var endpoint = LocalControlEndpoint.FromRuntimeDirectory(fixture.Root);
        await using (var host = new ChildProcess("AgenticWorkflows.ControlPlane.dll", fixture.Root))
        {
            await host.WaitForListeningAsync();
            Assert.True(File.Exists(endpoint.SocketPath));
            await using var cli = new ChildProcess("awctl.dll", fixture.Root, "status");
            await cli.WaitForExitAsync();
            Assert.Equal(0, cli.ExitCode);
            Assert.Empty(cli.Error);
            using var response = JsonDocument.Parse(cli.Output);
            Assert.Equal(1, response.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.StartsWith("0.1.0", response.RootElement.GetProperty("status").GetProperty("softwareVersion").GetString());
            Assert.Equal("running", response.RootElement.GetProperty("status").GetProperty("state").GetString());
            await host.StopAsync();
            Assert.True(host.ExitCode == 0, host.Output + host.Error);
        }
        Assert.Null(LinuxFileSystem.Inspect(endpoint.SocketPath));
        await using var unavailable = new ChildProcess("awctl.dll", fixture.Root, "status");
        await unavailable.WaitForExitAsync();
        Assert.Equal(1, unavailable.ExitCode);
        var diagnostic = JsonSerializer.Deserialize<ControlDiagnostic>(unavailable.Error, LocalControlProtocol.Json)!;
        Assert.Equal(endpoint.SocketPath, diagnostic.Endpoint);
        Assert.Equal("control_plane_unavailable", diagnostic.Code);
        Assert.Contains("dotnet run", diagnostic.Guidance);
    }

    [Fact]
    public async Task Crashed_host_leaves_recoverable_socket_and_releases_process_lock()
    {
        await using var fixture = new SocketFixture();
        var endpoint = LocalControlEndpoint.FromRuntimeDirectory(fixture.Root);
        await using (var crashed = new ChildProcess("AgenticWorkflows.ControlPlane.dll", fixture.Root))
        {
            await crashed.WaitForListeningAsync();
            await crashed.KillAsync();
        }
        Assert.NotNull(LinuxFileSystem.Inspect(endpoint.SocketPath));
        await using var restarted = new ChildProcess("AgenticWorkflows.ControlPlane.dll", fixture.Root);
        await restarted.WaitForListeningAsync();
        Assert.Equal("running", (await new LocalControlClient(endpoint).StatusAsync()).Status.State);
        await restarted.StopAsync();
        Assert.True(restarted.ExitCode == 0, restarted.Output + restarted.Error);
        Assert.Null(LinuxFileSystem.Inspect(endpoint.SocketPath));
    }

    [Fact]
    public async Task Competing_host_process_fails_without_disrupting_the_first_host()
    {
        await using var fixture = new SocketFixture();
        await using var first = new ChildProcess("AgenticWorkflows.ControlPlane.dll", fixture.Root);
        await first.WaitForListeningAsync();
        await using var second = new ChildProcess("AgenticWorkflows.ControlPlane.dll", fixture.Root);
        await second.WaitForExitAsync();
        Assert.Equal(1, second.ExitCode);
        Assert.Contains("Another control plane", second.Output + second.Error);
        Assert.Equal("running", (await new LocalControlClient(LocalControlEndpoint.FromRuntimeDirectory(fixture.Root)).StatusAsync()).Status.State);
        await first.StopAsync();
        Assert.True(first.ExitCode == 0, first.Output + first.Error);
    }

    private sealed class ChildProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new();
        private readonly StringBuilder error = new();
        private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ChildProcess(string assembly, string runtimeDirectory, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly));
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            start.Environment["XDG_RUNTIME_DIR"] = runtimeDirectory;
            start.Environment["XDG_DATA_HOME"] = Path.Combine(runtimeDirectory, "data");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(runtimeDirectory, "config");
            process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null) return;
                lock (output) output.AppendLine(args.Data);
                if (args.Data.Contains("Local Control Interface listening", StringComparison.Ordinal))
                    listening.TrySetResult();
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    lock (error) error.AppendLine(args.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        internal string Output { get { lock (output) return output.ToString(); } }
        internal string Error { get { lock (error) return error.ToString(); } }
        internal int ExitCode => process.ExitCode;

        internal async Task WaitForListeningAsync()
        {
            await Task.WhenAny(listening.Task, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(listening.Task.IsCompletedSuccessfully, Output + Error);
        }

        internal Task WaitForExitAsync() => process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        internal async Task StopAsync()
        {
            Assert.Equal(0, kill(process.Id, 15)); // SIGTERM exercises Generic Host shutdown.
            await WaitForExitAsync();
        }

        internal async Task KillAsync()
        {
            process.Kill(entireProcessTree: true);
            await WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
                await KillAsync();
            process.Dispose();
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}
