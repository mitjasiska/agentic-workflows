using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgenticWorkflows.LocalControl;

namespace AgenticWorkflows.Tests;

public sealed class ProcessTests
{
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
