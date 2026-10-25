using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.LocalControl;

namespace AgenticWorkflows.Source;

[SupportedOSPlatform("linux")]
public sealed record SourceEnvironment(string Home, string ConfigHome, string DataHome, string RuntimeDirectory, string Path, string Dotnet)
{
    [JsonIgnore] public string LauncherDirectory => System.IO.Path.Combine(Home, ".local", "bin");
    [JsonIgnore] public string Launcher => System.IO.Path.Combine(LauncherDirectory, "awctl");
    [JsonIgnore] public string Unit => System.IO.Path.Combine(ConfigHome, "systemd", "user", SourceSetup.UnitName);
    [JsonIgnore] public string EnabledLink => System.IO.Path.Combine(ConfigHome, "systemd", "user", "default.target.wants", SourceSetup.UnitName);
    [JsonIgnore] public string StateDirectory => System.IO.Path.Combine(ConfigHome, "agentic-workflows", "source-setup");
    [JsonIgnore] public string Receipt => System.IO.Path.Combine(StateDirectory, "installation.json");

    public static SourceEnvironment Current()
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Value(string name, string fallback) => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) ? fallback : Environment.GetEnvironmentVariable(name)!;
        return new(home, Value("XDG_CONFIG_HOME", System.IO.Path.Combine(home, ".config")),
            Value("XDG_DATA_HOME", System.IO.Path.Combine(home, ".local", "share")), Value("XDG_RUNTIME_DIR", ""),
            Value("PATH", "/usr/local/bin:/usr/bin:/bin"), Environment.ProcessPath ?? "/usr/bin/dotnet");
    }
}

[SupportedOSPlatform("linux")]
public sealed class SourceSetup(SourceEnvironment environment, IArgumentProcess process)
{
    public const string UnitName = "agentic-workflows.service";
    public const string Logs = "journalctl --user -u agentic-workflows.service -n 100 --no-pager";
    private static readonly JsonSerializerOptions ReceiptJson = new()
    { AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    internal sealed record Installation(int Version, string Checkout, SourceEnvironment Environment, SourceEnvironment? PreviousEnvironment = null);

    public async Task RunAsync(string checkout, bool remove = false)
    {
        if (LinuxFileSystem.UserId == 0) throw new IOException("Source setup must run as the non-root service account.");
        checkout = Absolute(checkout);
        ValidateEnvironment(environment);
        // Validate the complete pair before touching the installation. Removal does not require it.
        if (!remove)
        {
            SourceBuild.Validate(checkout);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sdk = await process.RunAsync(environment.Dotnet, ["--version"], checkout, null, deadline.Token);
            if (sdk.ExitCode != 0 || !Version.TryParse(sdk.Output.Trim(), out var version) || version.Major != 10 || version.Minor != 0 || version.Build < 100)
                throw new IOException("The stable .NET 10 SDK selected by global.json is required; install it and run ./build.sh.");
        }
        EnsureDirectory(environment.StateDirectory, true);
        using var lease = LinuxFileSystem.LockDirectory(environment.StateDirectory);
        var installed = ReadInstallation(environment);
        if (installed is not null && installed.Checkout != checkout)
            throw new IOException("Source setup belongs to another checkout: " + installed.Checkout + ". Remove it from that checkout first.");
        ValidateArtifacts(environment, installed);
        var launcherBefore = Capture(environment.Launcher);
        var unitBefore = Capture(environment.Unit);
        var receiptBefore = Capture(environment.Receipt);
        var enabledBefore = Capture(environment.EnabledLink);
        await CheckManagerOwnership(installed);
        ValidateArtifacts(environment, installed);
        RequireUnchanged(environment.Receipt, receiptBefore);
        if (remove)
        {
            if (installed is null) return;
            // Remove only our known enable link; systemctl disable could remove unrelated aliases.
            DeleteOwned(environment.EnabledLink, enabledBefore);
            DeleteOwned(environment.Launcher, launcherBefore);
            DeleteOwned(environment.Unit, unitBefore);
            await Manager(["daemon-reload"]);
            DeleteOwned(environment.Receipt, receiptBefore);
            return;
        }
        EnsureDirectory(environment.LauncherDirectory);
        EnsureDirectory(System.IO.Path.GetDirectoryName(environment.Unit)!);
        SourceEnvironment? previous = installed?.Environment;
        if (installed?.PreviousEnvironment is not null && File.Exists(environment.Unit) &&
            File.ReadAllText(environment.Unit) != RenderUnit(installed)) previous = installed.PreviousEnvironment;
        var next = new Installation(1, checkout, environment, previous);
        // A receipt precedes the artifacts, so interrupted setup remains provable and repairable.
        AtomicFile(environment.Receipt, JsonSerializer.Serialize(next), receiptBefore);
        receiptBefore = Capture(environment.Receipt);
        AtomicLink(environment.Launcher, System.IO.Path.Combine(checkout, SourceBuild.Client), launcherBefore);
        AtomicFile(environment.Unit, RenderUnit(next), unitBefore);
        AtomicFile(environment.Receipt, JsonSerializer.Serialize(next with { PreviousEnvironment = null }), receiptBefore);
        await Manager(["daemon-reload"]);
        await Manager(["enable", UnitName]);
    }

    internal static Installation? ReadInstallation(SourceEnvironment environment)
    {
        CheckAncestors(environment.Receipt);
        if (LinuxFileSystem.Inspect(environment.Receipt) is null) return null;
        LinuxFileSystem.Require(environment.Receipt, LinuxFileSystem.RegularFileType, LinuxFileSystem.PrivateSocket);
        using var handle = LinuxFileSystem.OpenRegularRead(environment.Receipt);
        using var reader = new StreamReader(new FileStream(handle, FileAccess.Read));
        string text = ReadBounded(reader);
        Installation installation;
        try { installation = JsonSerializer.Deserialize<Installation>(text, ReceiptJson) ?? throw new IOException("Invalid source ownership receipt: " + environment.Receipt); }
        catch (JsonException error) { throw new IOException("Invalid source ownership receipt: " + environment.Receipt + ". Inspect it before retrying ./setup-source.sh.", error); }
        if (installation.Version != 1 || installation.Environment is null ||
            installation.Environment.Home != environment.Home || installation.Environment.ConfigHome != environment.ConfigHome)
            throw new IOException("Source ownership receipt does not match this account's setup paths: " + environment.Receipt);
        Absolute(installation.Checkout);
        ValidateEnvironment(installation.Environment);
        if (installation.PreviousEnvironment is { } previous)
        {
            ValidateEnvironment(previous);
            if (previous.Home != environment.Home || previous.ConfigHome != environment.ConfigHome)
                throw new IOException("Invalid previous source ownership paths.");
        }
        return installation;
    }

    internal static void ValidateArtifacts(SourceEnvironment environment, Installation? installation)
    {
        foreach (string path in new[] { environment.Launcher, environment.Unit }) CheckAncestors(path);
        if (LinuxFileSystem.Inspect(environment.Launcher) is { } link)
        {
            if (installation is null || link.UserId != LinuxFileSystem.UserId || (link.Mode & 0xf000) != 0xa000 ||
                new FileInfo(environment.Launcher).LinkTarget != System.IO.Path.Combine(installation.Checkout, SourceBuild.Client))
                throw new IOException("Conflicting launcher preserved: " + environment.Launcher);
        }
        if (LinuxFileSystem.Inspect(environment.Unit) is not null)
        {
            if (installation is null) throw new IOException("Unowned unit preserved: " + environment.Unit);
            using var handle = LinuxFileSystem.OpenRegularRead(environment.Unit);
            using var reader = new StreamReader(new FileStream(handle, FileAccess.Read));
            string text = ReadBounded(reader);
            if (text != RenderUnit(installation) && (installation.PreviousEnvironment is null ||
                text != RenderUnit(installation with { Environment = installation.PreviousEnvironment })))
                throw new IOException("Modified or conflicting unit preserved: " + environment.Unit);
        }
    }

    internal static string RenderUnit(Installation installation)
    {
        var e = installation.Environment;
        string Q(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";
        return "# AW source setup v1 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(installation.Checkout)) + "\n" +
            "[Unit]\nDescription=Agentic Workflows control plane (source)\n\n[Service]\nType=simple\n" +
            // All application paths and the host assembly are absolute; no cwd discovery is needed.
            "WorkingDirectory=/\n" +
            // ':' disables systemd environment substitution in command arguments; '%' is escaped separately.
            "ExecStart=:" + Q(e.Dotnet) + " " + Q(System.IO.Path.Combine(installation.Checkout, SourceBuild.Host)) + "\n" +
            string.Join("", new[] { "HOME=" + e.Home, "XDG_CONFIG_HOME=" + e.ConfigHome, "XDG_DATA_HOME=" + e.DataHome,
                "XDG_RUNTIME_DIR=" + e.RuntimeDirectory, "PATH=" + e.Path }.Select(value => "Environment=" + Q(value) + "\n")) +
            "Restart=on-failure\nRestartSec=2\nTimeoutStopSec=20\nKillMode=process\nUMask=0077\n\n[Install]\nWantedBy=default.target\n";
    }

    private async Task Manager(string[] arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await process.RunAsync("systemctl", ["--user", "--no-pager", .. arguments], null, null, deadline.Token);
            if (result.ExitCode != 0) throw new IOException("systemctl --user " + string.Join(' ', arguments) + " failed (exit " + result.ExitCode + ").");
        }
        catch (Exception error) when (error is IOException or AgenticWorkflows.Workflows.WorkflowException)
        {
            throw new IOException(error.Message + " Partial setup may remain at " + environment.Receipt +
                "; rerun ./setup-source.sh or ./setup-source.sh --remove from the same checkout. " +
                "Ensure a systemd user session is available. Inspect: " + Logs, error);
        }
    }

    private async Task CheckManagerOwnership(Installation? installation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await process.RunAsync("systemctl", ["--user", "--no-pager", "show", "--property=LoadState,FragmentPath", "--", UnitName],
            null, null, deadline.Token);
        var fields = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] fragments = fields.Where(line => line.StartsWith("FragmentPath=", StringComparison.Ordinal)).ToArray();
        string[] loads = fields.Where(line => line.StartsWith("LoadState=", StringComparison.Ordinal)).ToArray();
        string? fragment = fragments.Length == 1 ? fragments[0][13..] : null;
        bool absent = loads is ["LoadState=not-found"];
        if (fragment is null || loads.Length != 1 || fragment.Length > 4096 || fragment.Any(char.IsControl) || (!absent && result.ExitCode != 0))
            throw new IOException("Cannot inspect the fixed AW user unit. Ensure a systemd user session is available; inspect: systemctl --user status " + UnitName);
        if (!absent && (installation is null || fragment != environment.Unit))
            throw new IOException("A loaded AW unit from another installation is preserved: " + fragment);
        string enabled = environment.EnabledLink;
        CheckAncestors(enabled);
        if (LinuxFileSystem.Inspect(enabled) is { } info)
        {
            var link = new FileInfo(enabled);
            if (installation is null || info.UserId != LinuxFileSystem.UserId || (info.Mode & 0xf000) != 0xa000 ||
                link.LinkTarget is not string target || System.IO.Path.GetFullPath(target, link.DirectoryName!) != environment.Unit)
                throw new IOException("Conflicting automatic-start link preserved: " + enabled);
        }
    }

    private static void ValidateEnvironment(SourceEnvironment e)
    {
        foreach (string value in new[] { e.Home, e.ConfigHome, e.DataHome, e.RuntimeDirectory, e.Path, e.Dotnet })
            if (value is null || value.Any(char.IsControl)) throw new IOException("Setup paths and PATH must not contain control characters.");
        Absolute(e.Home); Absolute(e.ConfigHome); Absolute(e.DataHome); Absolute(e.Dotnet);
        if (e.RuntimeDirectory.Length != 0) Absolute(e.RuntimeDirectory);
    }
    private static string Absolute(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || System.IO.Path.GetFullPath(path) != path)
            throw new IOException("Source setup requires a normalized absolute path: " + path);
        return path;
    }
    // Existing ancestors must be physical and cannot be writable by another account.
    internal static void CheckAncestors(string path)
    {
        for (string? parent = System.IO.Path.GetDirectoryName(path); parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
        {
            if (LinuxFileSystem.Inspect(parent) is not { } info) continue;
            bool sharedSticky = (info.Mode & 0x200) != 0;
            if ((info.Mode & 0xf000) != LinuxFileSystem.DirectoryType ||
                ((info.Mode & 0x12) != 0 && !sharedSticky))
                throw new IOException("Unsafe source setup parent directory: " + parent);
        }
    }
    private static void EnsureDirectory(string directory, bool privateMode = false)
    {
        CheckAncestors(System.IO.Path.Combine(directory, "entry"));
        var missing = new Stack<string>();
        for (string? current = directory; current is not null && LinuxFileSystem.Inspect(current) is null; current = System.IO.Path.GetDirectoryName(current))
            missing.Push(current);
        foreach (string current in missing)
        {
            Directory.CreateDirectory(current, LinuxFileSystem.PrivateDirectory);
            LinuxFileSystem.Require(current, LinuxFileSystem.DirectoryType, LinuxFileSystem.PrivateDirectory);
        }
        if (privateMode) LinuxFileSystem.Require(directory, LinuxFileSystem.DirectoryType, LinuxFileSystem.PrivateDirectory);
    }
    private static void AtomicFile(string path, string content, EntrySnapshot before)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = LinuxFileSystem.PrivateSocket }))
            {
                file.Write(Encoding.UTF8.GetBytes(content));
                file.Flush(true);
            }
            RequireUnchanged(path, before);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
    private static void AtomicLink(string path, string target, EntrySnapshot before)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.CreateSymbolicLink(temporary, target); RequireUnchanged(path, before); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private static void DeleteOwned(string path, EntrySnapshot before)
    {
        RequireUnchanged(path, before);
        if (LinuxFileSystem.Inspect(path) is not null) File.Delete(path);
    }
    private sealed record EntrySnapshot(LinuxFileSystem.Metadata? Metadata, string? Content);
    private static EntrySnapshot Capture(string path)
    {
        CheckAncestors(path);
        var info = LinuxFileSystem.Inspect(path);
        if (info is null) return new(null, null);
        if ((info.Value.Mode & 0xf000) == 0xa000) return new(info, new FileInfo(path).LinkTarget);
        using var handle = LinuxFileSystem.OpenRegularRead(path);
        using var reader = new StreamReader(new FileStream(handle, FileAccess.Read));
        return new(info, ReadBounded(reader));
    }
    private static void RequireUnchanged(string path, EntrySnapshot before)
    {
        var current = Capture(path);
        if (before.Content != current.Content ||
            (before.Metadata is { } expected ? current.Metadata is not { } actual || !expected.SameFile(actual) ||
                expected.Mode != actual.Mode || expected.UserId != actual.UserId : current.Metadata is not null))
            throw new IOException("Source setup artifact changed during the operation and was preserved: " + path + ". Inspect it before rerunning ./setup-source.sh.");
    }
    private static string ReadBounded(StreamReader reader)
    {
        char[] buffer = new char[65537];
        int length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > 65536) throw new IOException("Source setup ownership file exceeds 64 KiB; inspect it before retrying setup.");
        return new string(buffer, 0, length);
    }
}
