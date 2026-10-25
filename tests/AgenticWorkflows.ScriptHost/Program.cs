using System.Diagnostics;
using System.Text.Json;

// A controlled native apphost for script tests. Never invokes a real service manager.
if (!OperatingSystem.IsLinux()) return 2;
string? log = Environment.GetEnvironmentVariable("AW_TEST_PROCESS_LOG");
if (log is null) return 2;
File.AppendAllText(log, JsonSerializer.Serialize(new { args, directory = Directory.GetCurrentDirectory(), home = Environment.GetEnvironmentVariable("HOME") }) + "\n");
string? fail = Environment.GetEnvironmentVariable("AW_TEST_FAIL_ARGUMENT");
if (fail is not null && args.Contains(fail)) return 23;
if (args is ["--version"]) { Console.WriteLine("10.0.113"); return 0; }
if (args.Contains("show"))
{
    Console.WriteLine("LoadState=not-found\nFragmentPath=\nActiveState=inactive\nUnitFileState=disabled\nSubState=dead\nKillMode=process");
    return 0;
}
if (args.Contains("enable"))
{
    string directory = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "systemd", "user", "default.target.wants");
    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    string link = Path.Combine(directory, "agentic-workflows.service");
    if (!File.Exists(link)) File.CreateSymbolicLink(link, "../agentic-workflows.service");
}
if (args.Length >= 2 && args[0].EndsWith("AgenticWorkflows.SourceTools.dll", StringComparison.Ordinal))
{
    var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("AW_TEST_REAL_DOTNET")!) { UseShellExecute = false };
    foreach (string argument in args) info.ArgumentList.Add(argument);
    using var child = Process.Start(info)!;
    await child.WaitForExitAsync(); return child.ExitCode;
}
return 0;
