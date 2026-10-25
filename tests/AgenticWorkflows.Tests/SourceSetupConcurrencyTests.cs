using System.Diagnostics;
using AgenticWorkflows.LocalControl;

namespace AgenticWorkflows.Tests;

public sealed class SourceSetupConcurrencyTests
{
    [Fact]
    public async Task Sequential_setup_and_removal_retries_release_the_lock_during_subprocess_activity()
    {
        await using var activity = new ProcessActivity();
        await activity.Started.WaitAsync(TimeSpan.FromSeconds(10));
        using var fixture = new SourceSetupTests.SourceFixture();
        foreach (string stage in new[] { "daemon-reload", "enable", "remove-reload" })
        {
            bool remove = stage == "remove-reload";
            if (remove) await fixture.Setup();
            fixture.Process.Fail = remove ? "daemon-reload" : stage;
            var error = await Assert.ThrowsAsync<IOException>(() => fixture.Setup(remove: remove));
            Assert.Contains(fixture.Process.Fail, error.Message);
            Assert.Contains("Partial setup", error.Message);
            fixture.Process.Fail = null;
            await fixture.Setup();
            await fixture.Setup();
            await fixture.Setup(remove: true);
            await fixture.Setup(remove: true);
            Assert.Null(LinuxFileSystem.Inspect(fixture.Environment.Launcher));
            Assert.False(File.Exists(fixture.Environment.Unit));
            Assert.False(File.Exists(fixture.Environment.Receipt));
        }
        Assert.All(fixture.Sentinels, path => Assert.Equal("preserved", File.ReadAllText(path)));
    }

    [Fact]
    public async Task Concurrent_setup_is_excluded_until_the_owning_operation_completes()
    {
        using var fixture = new SourceSetupTests.SourceFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryCall = fixture.Process.OnCall;
        int hold = 1;
        fixture.Process.OnCall = arguments =>
        {
            ordinaryCall?.Invoke(arguments);
            if (!arguments.Contains("show") || Interlocked.Exchange(ref hold, 0) == 0) return;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var owning = Task.Run(() => fixture.Setup());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await Assert.ThrowsAsync<IOException>(() => fixture.Setup());
            Assert.Contains("holds runtime directory", error.Message);
            Assert.False(File.Exists(fixture.Environment.Receipt));
            Assert.False(File.Exists(fixture.Environment.Unit));
        }
        finally
        {
            release.TrySetResult();
            await owning.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await fixture.Setup();
        await fixture.Setup(remove: true);
        Assert.All(fixture.Sentinels, path => Assert.Equal("preserved", File.ReadAllText(path)));
    }

    private sealed class ProcessActivity : IAsyncDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly Task[] workers;
        internal Task Started { get; }

        internal ProcessActivity()
        {
            // Reproduce the reviewer's four concurrent fork/exec workers, using only
            // harmless children owned by this fixture and no shell or service manager.
            var ready = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            workers = ready.Select(signal => Task.Run(async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    signal.TrySetResult();
                    await Task.Yield();
                }
            })).ToArray();
            Started = Task.WhenAll(ready.Select(signal => signal.Task));
        }
        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            stopping.Dispose();
        }
    }
}
