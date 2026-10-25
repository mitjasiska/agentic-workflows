using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Source;

namespace AgenticWorkflows.Tests;

public sealed class SourceScriptTests
{
    [Theory]
    [InlineData("", "build,seal")]
    [InlineData("build", "build,seal")]
    [InlineData("rebuild", "clean,build,seal")]
    [InlineData("test", "build,test,seal")]
    public async Task Build_entry_runs_complete_solution_from_spaced_checkout_without_installing(string action, string expected)
    {
        using var f = new ScriptFixture();
        var result = await f.Run("build.sh", action.Length == 0 ? [] : [action]);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        var calls = f.Calls();
        Assert.Equal(expected.Split(','), calls.Select(c => c.Args[0].EndsWith(".dll", StringComparison.Ordinal) ? c.Args[1] : c.Args[0]));
        Assert.All(calls, c => { Assert.Equal(f.Source.Checkout, c.Directory); Assert.Equal(f.Source.Environment.Home, c.Home); });
        Assert.All(calls.Where(c => c.Args[0] is "build" or "clean" or "test"), c => Assert.Contains("AgenticWorkflows.slnx", c.Args));
        SourceBuild.Validate(f.Source.Checkout);
        Assert.False(File.Exists(f.Source.Environment.Unit)); Assert.Null(LinuxFileSystem.Inspect(f.Source.Environment.Launcher));
        Assert.All(f.Source.Sentinels, p => Assert.Equal("preserved", File.ReadAllText(p)));
    }

    [Theory]
    [InlineData("rebuild", "clean")]
    [InlineData("build", "build")]
    [InlineData("test", "test")]
    [InlineData("build", "seal")]
    public async Task Build_returns_exact_failure_and_never_leaves_valid_pair_or_runs_later_steps(string action, string failure)
    {
        using var f = new ScriptFixture(); f.Fail = failure;
        var result = await f.Run("build.sh", [action]);
        Assert.Equal(23, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(f.Source.Checkout, "artifacts/source-build.json")));
        Assert.DoesNotContain(f.Calls(), c => c.Args.Contains("enable") || c.Args.Contains("start"));
        Assert.False(File.Exists(f.Source.Environment.Unit));
    }

    [Theory]
    [InlineData("build.sh", "invalid")]
    [InlineData("setup-source.sh", "--start")]
    public async Task Script_argument_errors_are_usage_failures_without_processes(string script, string argument)
    {
        using var f = new ScriptFixture();
        var result = await f.Run(script, [argument]);
        Assert.Equal(2, result.ExitCode); Assert.Contains("Usage:", result.Error); Assert.Empty(f.Calls());
    }

    [Fact]
    public async Task Setup_entry_runs_without_building_then_reruns_and_removes_safely()
    {
        using var f = new ScriptFixture();
        foreach (string[] args in new[] { Array.Empty<string>(), Array.Empty<string>(), new[] { "--remove" } })
        {
            var result = await f.Run("setup-source.sh", args);
            Assert.True(result.ExitCode == 0, result.Error + result.Output);
        }
        Assert.DoesNotContain(f.Calls(), c => c.Args.Any(a => a is "build" or "test" or "start" or "stop" or "restart"));
        Assert.Contains(f.Calls(), c => c.Args.Contains("enable")); Assert.DoesNotContain(f.Calls(), c => c.Args.Contains("disable"));
        Assert.False(File.Exists(f.Source.Environment.Unit)); Assert.Null(LinuxFileSystem.Inspect(f.Source.Environment.Launcher));
        Assert.All(f.Source.Sentinels, p => Assert.Equal("preserved", File.ReadAllText(p)));
    }

    [Fact]
    public async Task Setup_entry_reports_missing_artifacts_and_partial_enable_failure_without_starting()
    {
        using var f = new ScriptFixture();
        File.Delete(Path.Combine(f.Source.Checkout, "artifacts/source-build.json"));
        var missing = await f.Run("setup-source.sh", []);
        Assert.Equal(1, missing.ExitCode); Assert.Contains("./build.sh", missing.Error);
        SourceBuild.Seal(f.Source.Checkout); f.Fail = "enable";
        var partial = await f.Run("setup-source.sh", []);
        Assert.Equal(1, partial.ExitCode); Assert.Contains("Partial setup", partial.Error); Assert.Contains("--remove", partial.Error);
        Assert.True(File.Exists(f.Source.Environment.Receipt));
        f.Fail = null;
        Assert.Equal(0, (await f.Run("setup-source.sh", ["--remove"])).ExitCode);
        Assert.DoesNotContain(f.Calls(), c => c.Args.Contains("start"));
    }

    [Fact]
    public async Task Source_launcher_runs_status_from_an_unrelated_directory_without_starting_anything()
    {
        using var f = new ScriptFixture();
        foreach (string relative in new[] { Path.GetDirectoryName(SourceBuild.Client)!, Path.GetDirectoryName(SourceBuild.Host)! })
            CopyTree(Path.Combine(RepositoryRoot(), relative), Path.Combine(f.Source.Checkout, relative));
        SourceBuild.Seal(f.Source.Checkout);
        Assert.Equal(0, (await f.Run("setup-source.sh", [])).ExitCode);
        var result = await f.Run("awctl", ["status"], f.Source.Root);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("control_plane_unavailable", result.Error); Assert.Contains("awctl service start", result.Error);
        Assert.DoesNotContain(f.Calls(), c => c.Args.Any(a => a is "start" or "restart"));
    }

    [Fact]
    public async Task Generated_unit_passes_systemd_parser_with_literal_special_paths()
    {
        using var f = new ScriptFixture();
        var installed = await f.Run("setup-source.sh", []); Assert.True(installed.ExitCode == 0, installed.Error);
        var result = await f.Run("/usr/bin/systemd-analyze", ["--user", "verify", f.Source.Environment.Unit]);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
    }

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AgenticWorkflows.slnx"))) return dir.FullName;
        throw new IOException("Cannot find test source checkout.");
    }
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string output = Path.Combine(target, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(path, output, true);
        }
    }
    private sealed record Invocation(string[] Args, string Directory, string Home);
    private sealed record RunResult(int ExitCode, string Output, string Error);
    private sealed class ScriptFixture : IDisposable
    {
        internal SourceSetupTests.SourceFixture Source = new(); internal string? Fail;
        private readonly string fakeBin, log;
        internal ScriptFixture()
        {
            string root = RepositoryRoot(); fakeBin = Path.Combine(Source.Root, "substitute bin"); Directory.CreateDirectory(fakeBin);
            string host = Path.Combine(root, "tests/AgenticWorkflows.ScriptHost/bin/Debug/net10.0/AgenticWorkflows.ScriptHost");
            foreach (string name in new[] { "dotnet", "systemctl" }) File.CreateSymbolicLink(Path.Combine(fakeBin, name), host);
            foreach (string name in new[] { "build.sh", "setup-source.sh", "global.json" }) File.Copy(Path.Combine(root, name), Path.Combine(Source.Checkout, name));
            CopyTree(Path.Combine(root, "tools/AgenticWorkflows.SourceTools/bin/Debug/net10.0"), Path.Combine(Source.Checkout, "tools/AgenticWorkflows.SourceTools/bin/Debug/net10.0"));
            log = Path.Combine(Source.Root, "processes.jsonl");
        }
        internal Invocation[] Calls() => File.Exists(log) ? File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<Invocation>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray() : [];
        internal async Task<RunResult> Run(string program, string[] args, string? directory = null)
        {
            string executable = program.EndsWith(".sh", StringComparison.Ordinal) ? Path.Combine(Source.Checkout, program) : program;
            var start = new ProcessStartInfo(Path.IsPathFullyQualified(executable) ? executable : "/usr/bin/env")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory ?? Source.Root };
            if (!Path.IsPathFullyQualified(executable)) start.ArgumentList.Add(executable);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            start.Environment["HOME"] = Source.Environment.Home; start.Environment["XDG_CONFIG_HOME"] = Source.Environment.ConfigHome;
            start.Environment["XDG_DATA_HOME"] = Source.Environment.DataHome; start.Environment["XDG_RUNTIME_DIR"] = Source.Environment.RuntimeDirectory;
            start.Environment["PATH"] = Source.Environment.LauncherDirectory + ":" + fakeBin + ":/usr/bin:/bin";
            start.Environment["AW_TEST_PROCESS_LOG"] = log; start.Environment["AW_TEST_FAIL_ARGUMENT"] = Fail;
            start.Environment["AW_TEST_REAL_DOTNET"] = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/bin/dotnet";
            start.Environment["DOTNET_CLI_HOME"] = Path.Combine(Source.Root, "dotnet-home");
            start.Environment["SYSTEMD_UNIT_PATH"] = Path.GetDirectoryName(Source.Environment.Unit) + ":/usr/lib/systemd/user";
            using var child = Process.Start(start)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            return new(child.ExitCode, await output, await error);
        }
        public void Dispose() => Source.Dispose();
    }
}
