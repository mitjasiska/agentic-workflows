using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Source;

namespace AgenticWorkflows.Tests;

public sealed class SourceSetupTests
{
    [Fact]
    public async Task Setup_is_idempotent_enables_without_starting_and_removal_preserves_all_user_data()
    {
        using var f = new SourceFixture();
        await f.Setup();
        string unit = File.ReadAllText(f.Environment.Unit);
        Assert.Equal(Path.Combine(f.Checkout, SourceBuild.Client), new FileInfo(f.Environment.Launcher).LinkTarget);
        Assert.Contains("KillMode=process", unit); Assert.Contains("ExecStart=:", unit); Assert.Contains("Environment=\"XDG_DATA_HOME=", unit);
        Assert.Contains("%%", unit); Assert.Contains("WantedBy=default.target", unit); Assert.DoesNotContain("User=root", unit);
        Assert.NotNull(LinuxFileSystem.Inspect(f.Environment.EnabledLink));
        await f.Setup(); Assert.Equal(unit, File.ReadAllText(f.Environment.Unit));
        Assert.Equal(2, f.Process.Calls.Count(c => c.Contains("enable")));
        Assert.DoesNotContain(f.Process.Calls, c => c.Any(a => a is "start" or "stop" or "restart" or "--now"));
        await f.Setup(remove: true);
        Assert.Null(LinuxFileSystem.Inspect(f.Environment.Launcher)); Assert.False(File.Exists(f.Environment.Unit)); Assert.False(File.Exists(f.Environment.Receipt));
        Assert.Null(LinuxFileSystem.Inspect(f.Environment.EnabledLink));
        Assert.All(f.Sentinels, p => Assert.Equal("preserved", File.ReadAllText(p)));
        Assert.DoesNotContain(f.Process.Calls, c => c.Contains("disable"));
        await f.Setup(remove: true); // already removed is safe
    }

    [Theory]
    [InlineData("launcher-file")]
    [InlineData("launcher-link")]
    [InlineData("unit-file")]
    [InlineData("unit-link")]
    [InlineData("unit-dangling-link")]
    public async Task Unowned_artifacts_are_preserved_before_any_service_mutation(string kind)
    {
        using var f = new SourceFixture();
        Directory.CreateDirectory(f.Environment.LauncherDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Environment.Unit)!);
        string target = kind.StartsWith("launcher", StringComparison.Ordinal) ? f.Environment.Launcher : f.Environment.Unit;
        if (kind.EndsWith("link", StringComparison.Ordinal)) File.CreateSymbolicLink(target, f.Sentinels[0] + (kind.Contains("dangling", StringComparison.Ordinal) ? ".absent" : ""));
        else File.WriteAllText(target, "another installation");
        var before = LinuxFileSystem.Inspect(target)!.Value;
        await Assert.ThrowsAnyAsync<IOException>(() => f.Setup());
        await Assert.ThrowsAsync<IOException>(() => f.Setup(remove: true));
        Assert.True(before.SameFile(LinuxFileSystem.Inspect(target)!.Value));
        Assert.DoesNotContain(f.Process.Calls, c => c.Contains("enable") || c.Contains("disable") || c.Contains("daemon-reload"));
        Assert.All(f.Sentinels, p => Assert.Equal("preserved", File.ReadAllText(p)));
    }

    [Fact]
    public async Task Another_checkout_cannot_refresh_or_remove_this_setup()
    {
        using var f = new SourceFixture(); await f.Setup();
        string other = Path.Combine(f.Root, "other checkout"); f.BuildArtifacts(other);
        string receipt = File.ReadAllText(f.Environment.Receipt), unit = File.ReadAllText(f.Environment.Unit);
        await Assert.ThrowsAsync<IOException>(() => f.Setup(checkout: other));
        await Assert.ThrowsAsync<IOException>(() => f.Setup(checkout: other, remove: true));
        Assert.Equal(receipt, File.ReadAllText(f.Environment.Receipt)); Assert.Equal(unit, File.ReadAllText(f.Environment.Unit));
    }

    [Fact]
    public async Task Modified_owned_unit_is_not_replaced_or_removed()
    {
        using var f = new SourceFixture(); await f.Setup();
        File.AppendAllText(f.Environment.Unit, "# local modification\n");
        await Assert.ThrowsAsync<IOException>(() => f.Setup()); await Assert.ThrowsAsync<IOException>(() => f.Setup(remove: true));
        Assert.Contains("local modification", File.ReadAllText(f.Environment.Unit)); Assert.NotNull(LinuxFileSystem.Inspect(f.Environment.Launcher));
    }

    [Theory]
    [InlineData("daemon-reload")]
    [InlineData("enable")]
    [InlineData("remove-reload")]
    public async Task Partial_manager_failure_reports_exact_stage_and_supports_retry(string stage)
    {
        using var f = new SourceFixture();
        bool remove = stage == "remove-reload";
        if (remove) await f.Setup();
        f.Process.Fail = remove ? "daemon-reload" : stage;
        var error = await Assert.ThrowsAsync<IOException>(() => f.Setup(remove: remove));
        Assert.Contains(f.Process.Fail, error.Message); Assert.Contains("Partial setup", error.Message); Assert.Contains("--remove", error.Message); Assert.Contains(SourceSetup.Logs, error.Message);
        Assert.True(File.Exists(f.Environment.Receipt));
        f.Process.Fail = null; await f.Setup(); await f.Setup(remove: true);
        Assert.All(f.Sentinels, p => Assert.Equal("preserved", File.ReadAllText(p)));
    }

    [Fact]
    public async Task Partial_missing_artifacts_and_changed_environment_can_be_repaired()
    {
        using var f = new SourceFixture(); await f.Setup();
        File.Delete(f.Environment.Launcher);
        var changed = f.Environment with { DataHome = Path.Combine(f.Root, "new data"), Path = "/opt/other tools:/usr/bin" };
        await new SourceSetup(changed, f.Process).RunAsync(f.Checkout);
        Assert.Contains("new data", File.ReadAllText(f.Environment.Unit));
        File.Delete(f.Environment.Unit); await new SourceSetup(changed, f.Process).RunAsync(f.Checkout);
        await new SourceSetup(changed, f.Process).RunAsync(f.Checkout, true);
    }

    [Fact]
    public async Task Removal_does_not_require_build_artifacts_or_the_original_sdk()
    {
        using var f = new SourceFixture(); await f.Setup();
        Directory.Delete(Path.Combine(f.Checkout, "src"), true); f.Process.Fail = "--version";
        await f.Setup(remove: true); Assert.False(File.Exists(f.Environment.Unit));
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("marker")]
    [InlineData("sdk")]
    public async Task Missing_or_mismatched_build_and_sdk_are_rejected_before_installing(string failure)
    {
        using var f = new SourceFixture();
        if (failure == "artifact") File.AppendAllText(Path.Combine(f.Checkout, SourceBuild.Client + ".dll"), "independent refresh");
        if (failure == "marker") File.Delete(Path.Combine(f.Checkout, "artifacts/source-build.json"));
        if (failure == "sdk") f.Process.Sdk = "9.0.100";
        await Assert.ThrowsAnyAsync<IOException>(() => f.Setup());
        Assert.Null(LinuxFileSystem.Inspect(f.Environment.Launcher)); Assert.False(File.Exists(f.Environment.Unit));
        Assert.DoesNotContain(f.Process.Calls, c => c.Contains("enable"));
    }

    [Fact]
    public async Task Setup_rejects_symlink_parent_and_control_characters_without_changing_target()
    {
        using var f = new SourceFixture();
        string target = Path.Combine(f.Root, "foreign"); Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(f.Environment.LauncherDirectory, target);
        await Assert.ThrowsAsync<IOException>(() => f.Setup()); Assert.Empty(Directory.GetFileSystemEntries(target));
        await Assert.ThrowsAsync<IOException>(() => new SourceSetup(f.Environment with { Path = "evil\nExecStart=/bin/false" }, f.Process).RunAsync(f.Checkout));
    }

    [Fact]
    public void Build_seal_rejects_independently_built_shared_protocol_libraries()
    {
        using var f = new SourceFixture();
        File.WriteAllText(Path.Combine(f.Checkout, Path.GetDirectoryName(SourceBuild.Host)!, "AgenticWorkflows.dll"), "different");
        Assert.Throws<IOException>(() => SourceBuild.Seal(f.Checkout));
    }

    [Fact]
    public async Task Loaded_foreign_unit_or_foreign_enable_link_is_preserved()
    {
        using var f = new SourceFixture();
        f.Process.Show = "LoadState=loaded\nFragmentPath=/usr/lib/systemd/user/agentic-workflows.service\n";
        var error = await Assert.ThrowsAsync<IOException>(() => f.Setup());
        Assert.Contains("another installation", error.Message);
        Assert.False(File.Exists(f.Environment.Unit));
        f.Process.Show = "LoadState=not-found\nFragmentPath=\n";
        Directory.CreateDirectory(Path.GetDirectoryName(f.Environment.EnabledLink)!, LinuxFileSystem.PrivateDirectory);
        File.CreateSymbolicLink(f.Environment.EnabledLink, f.Sentinels[0]);
        await Assert.ThrowsAsync<IOException>(() => f.Setup());
        Assert.Equal(f.Sentinels[0], new FileInfo(f.Environment.EnabledLink).LinkTarget);
    }

    [Fact]
    public async Task Removal_preserves_unrelated_unit_aliases_and_default_shell_files()
    {
        using var f = new SourceFixture(); await f.Setup();
        string alias = Path.Combine(Path.GetDirectoryName(f.Environment.Unit)!, "custom.target.wants", SourceSetup.UnitName);
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!, LinuxFileSystem.PrivateDirectory);
        File.CreateSymbolicLink(alias, "../agentic-workflows.service");
        string profile = Path.Combine(f.Environment.Home, ".profile"); File.WriteAllText(profile, "keep PATH setup");
        await f.Setup(remove: true);
        Assert.Equal("../agentic-workflows.service", new FileInfo(alias).LinkTarget);
        Assert.Equal("keep PATH setup", File.ReadAllText(profile));
    }

    [Theory]
    [InlineData("LoadState=not-found\nFragmentPath=\nFragmentPath=evil\n")]
    [InlineData("not property output")]
    public async Task Untrusted_manager_output_is_rejected_without_artifact_changes(string state)
    {
        using var f = new SourceFixture(); f.Process.Show = state;
        await Assert.ThrowsAsync<IOException>(() => f.Setup()); Assert.False(File.Exists(f.Environment.Unit));
    }

    [Fact]
    public async Task Interrupted_environment_refresh_recognizes_the_previous_generated_unit()
    {
        using var f = new SourceFixture(); await f.Setup();
        var old = SourceSetup.ReadInstallation(f.Environment)!;
        var next = old with { Environment = f.Environment with { Path = "/opt/changed:/usr/bin" }, PreviousEnvironment = f.Environment };
        File.WriteAllText(f.Environment.Receipt, JsonSerializer.Serialize(next));
        await f.Setup();
        Assert.Null(SourceSetup.ReadInstallation(f.Environment)!.PreviousEnvironment);
        Assert.Equal(SourceSetup.RenderUnit(old), File.ReadAllText(f.Environment.Unit));
    }

    [Fact]
    public async Task Replacement_during_setup_or_removal_is_preserved()
    {
        using var f = new SourceFixture(); await f.Setup();
        f.Process.OnCall = arguments =>
        {
            if (!arguments.Contains("show")) return;
            File.Delete(f.Environment.Launcher); File.WriteAllText(f.Environment.Launcher, "foreign replacement");
        };
        await Assert.ThrowsAsync<IOException>(() => f.Setup());
        Assert.Equal("foreign replacement", File.ReadAllText(f.Environment.Launcher));
        File.Delete(f.Environment.Launcher); File.CreateSymbolicLink(f.Environment.Launcher, Path.Combine(f.Checkout, SourceBuild.Client));
        f.Process.OnCall = arguments =>
        {
            if (!arguments.Contains("daemon-reload")) return;
            File.Delete(f.Environment.Receipt); File.WriteAllText(f.Environment.Receipt, "foreign receipt");
        };
        await Assert.ThrowsAsync<IOException>(() => f.Setup(remove: true));
        Assert.Equal("foreign receipt", File.ReadAllText(f.Environment.Receipt));
    }

    [Theory]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Version\":2}")]
    [InlineData("not json")]
    public async Task Invalid_receipt_never_authorizes_artifact_replacement(string receipt)
    {
        using var f = new SourceFixture(); await f.Setup();
        File.WriteAllText(f.Environment.Receipt, receipt);
        string unit = File.ReadAllText(f.Environment.Unit);
        await Assert.ThrowsAsync<IOException>(() => f.Setup());
        Assert.Equal(receipt, File.ReadAllText(f.Environment.Receipt)); Assert.Equal(unit, File.ReadAllText(f.Environment.Unit));
    }

    internal sealed class SetupProcess : IArgumentProcess
    {
        internal List<string[]> Calls = []; internal string? Fail; internal string Sdk = "10.0.113";
        internal string Show = "LoadState=not-found\nFragmentPath=\n";
        internal Action<string[]>? OnCall;
        public Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory, IReadOnlyDictionary<string, string?>? environment, CancellationToken token)
        {
            Calls.Add(arguments.ToArray());
            OnCall?.Invoke(arguments.ToArray());
            if (arguments.Contains("--version")) return Task.FromResult(new ProcessOutput(0, Sdk));
            Assert.Equal("systemctl", executable); Assert.Contains("--user", arguments);
            if (arguments.Contains("show")) return Task.FromResult(new ProcessOutput(0, Show));
            return Task.FromResult(new ProcessOutput(Fail is not null && arguments.Contains(Fail) ? 17 : 0, ""));
        }
    }

    internal sealed class SourceFixture : IDisposable
    {
        internal string Root = Path.Combine("/tmp", "aw-source-" + Guid.NewGuid().ToString("N"));
        internal string Checkout; internal SourceEnvironment Environment; internal SetupProcess Process = new(); internal string[] Sentinels;
        internal SourceFixture()
        {
            Directory.CreateDirectory(Root, LinuxFileSystem.PrivateDirectory);
            Checkout = Path.Combine(Root, "checkout with spaces % $() ' \""); Directory.CreateDirectory(Checkout);
            string home = Path.Combine(Root, "home with spaces"); Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(home, ".local"));
            Environment = new(home, Path.Combine(home, "config"), Path.Combine(home, "data"), Path.Combine(Root, "runtime"), "/opt/tools with spaces:/usr/bin", "/usr/bin/dotnet");
            Directory.CreateDirectory(Environment.RuntimeDirectory, LinuxFileSystem.PrivateDirectory);
            Sentinels = [Path.Combine(Environment.ConfigHome, "agentic-workflows", "global.toml"), Path.Combine(Environment.DataHome, "agentic-workflows", "workflows.db"),
                Path.Combine(Root, "worktree", "work"), Path.Combine(Root, "herdr", "resource"), Path.Combine(Root, "credentials")];
            foreach (string path in Sentinels) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "preserved"); }
            File.SetUnixFileMode(home, LinuxFileSystem.PrivateDirectory);
            foreach (string directory in Directory.EnumerateDirectories(home, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(directory, LinuxFileSystem.PrivateDirectory);
            BuildArtifacts(Checkout);
            Process.OnCall = arguments =>
            {
                if (!arguments.Contains("enable")) return;
                Directory.CreateDirectory(Path.GetDirectoryName(Environment.EnabledLink)!, LinuxFileSystem.PrivateDirectory);
                if (LinuxFileSystem.Inspect(Environment.EnabledLink) is null) File.CreateSymbolicLink(Environment.EnabledLink, "../agentic-workflows.service");
            };
        }
        internal void BuildArtifacts(string root)
        {
            foreach (string file in new[] { SourceBuild.Client, SourceBuild.Client + ".dll", SourceBuild.Host,
                "src/awctl/bin/Debug/net10.0/awctl.runtimeconfig.json", "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.ControlPlane.runtimeconfig.json",
                "src/awctl/bin/Debug/net10.0/awctl.deps.json", "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.ControlPlane.deps.json",
                "src/awctl/bin/Debug/net10.0/AgenticWorkflows.dll", "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.dll" })
            { string path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "same"); }
            File.SetUnixFileMode(Path.Combine(root, SourceBuild.Client), LinuxFileSystem.PrivateDirectory);
            SourceBuild.Seal(root);
        }
        internal Task Setup(bool remove = false, string? checkout = null) => new SourceSetup(Environment, Process).RunAsync(checkout ?? Checkout, remove);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
