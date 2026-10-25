using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;

namespace AgenticWorkflows.Source;

/// <summary>A successful complete solution build seals the client and host as one source artifact pair.</summary>
[SupportedOSPlatform("linux")]
public static class SourceBuild
{
    public const string Client = "src/awctl/bin/Debug/net10.0/awctl";
    public const string Host = "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.ControlPlane.dll";
    private const string Marker = "artifacts/source-build.json";
    private sealed record BuildPair(string Checkout, Dictionary<string, string> Files);

    public static void Seal(string checkout)
    {
        checkout = Path.GetFullPath(checkout);
        var files = Fingerprint(checkout);
        string clientLibrary = "src/awctl/bin/Debug/net10.0/AgenticWorkflows.dll";
        string hostLibrary = "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.dll";
        if (files[clientLibrary] != files[hostLibrary]) throw new IOException("Client and control-plane libraries differ. Run ./build.sh.");
        Directory.CreateDirectory(Path.Combine(checkout, "artifacts"));
        File.WriteAllText(Path.Combine(checkout, Marker), JsonSerializer.Serialize(new BuildPair(checkout, files)));
    }

    public static void Validate(string checkout)
    {
        var pair = JsonSerializer.Deserialize<BuildPair>(File.ReadAllText(Path.Combine(checkout, Marker)));
        var actual = Fingerprint(checkout);
        if (pair?.Checkout != checkout || pair.Files is null || pair.Files.Count != actual.Count ||
            actual.Any(p => !pair.Files.TryGetValue(p.Key, out var hash) || hash != p.Value))
            throw new IOException("Source artifacts do not match the last complete build. Run ./build.sh before ./setup-source.sh.");
    }

    private static Dictionary<string, string> Fingerprint(string checkout)
    {
        foreach (string required in new[] { Client, Client + ".dll", Host,
            "src/awctl/bin/Debug/net10.0/AgenticWorkflows.dll",
            "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.dll",
            "src/awctl/bin/Debug/net10.0/awctl.deps.json",
            "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.ControlPlane.deps.json",
            "src/awctl/bin/Debug/net10.0/awctl.runtimeconfig.json",
            "src/AgenticWorkflows.ControlPlane/bin/Debug/net10.0/AgenticWorkflows.ControlPlane.runtimeconfig.json" })
            if (!File.Exists(Path.Combine(checkout, required))) throw new IOException("Built artifact missing: " + required + ". Run ./build.sh.");
        if ((File.GetUnixFileMode(Path.Combine(checkout, Client)) & UnixFileMode.UserExecute) == 0)
            throw new IOException("The built awctl launcher is not executable. Run ./build.sh rebuild.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string assembly in new[] { Client, Host })
            foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(Path.Combine(checkout, assembly))!, "*", SearchOption.AllDirectories))
                files.Add(Path.GetRelativePath(checkout, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        return files;
    }
}
