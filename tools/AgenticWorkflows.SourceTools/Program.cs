using AgenticWorkflows.Source;
using AgenticWorkflows.Infrastructure;

if (!OperatingSystem.IsLinux()) return 1;
try
{
    if (args is ["seal", var checkout]) SourceBuild.Seal(checkout);
    else if (args is ["setup", _] or ["setup", _, "--remove"])
    {
        var setup = new SourceSetup(SourceEnvironment.Current(), new ArgumentProcess());
        await setup.RunAsync(args[1], args.Length == 3);
        Console.WriteLine(args.Length == 3 ? "Owned source setup removed; AW data and configuration preserved." :
            "Source setup enabled; service has not been started. Run: awctl service start");
        if (args.Length == 2)
            Console.WriteLine("If needed, add the following directory to this shell's PATH (no shell files were changed): " +
                SourceEnvironment.Current().LauncherDirectory);
    }
    else return 2;
    return 0;
}
catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or AgenticWorkflows.Workflows.WorkflowException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("Source setup/build failed: " + error.Message +
        " Inspect the named paths; retry ./build.sh then ./setup-source.sh. " +
        "For partial setup, rerun ./setup-source.sh or ./setup-source.sh --remove from the same checkout. " +
        "Check: systemctl --user status agentic-workflows.service; journalctl --user -u agentic-workflows.service -n 100 --no-pager");
    return 1;
}
