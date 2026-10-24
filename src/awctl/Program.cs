using System.Text.Json;
using AgenticWorkflows.LocalControl;

return await AwctlCommand.RunAsync(args, Console.Out, Console.Error);

internal static class AwctlCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        LocalControlClient? client = null)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync("Usage: awctl status");
            return 0;
        }
        if (args is not ["status"])
        {
            await WriteErrorAsync(error, new("usage", "Expected the local status command.", "Usage: awctl status"));
            return 2;
        }
        if (!OperatingSystem.IsLinux())
        {
            await WriteErrorAsync(error, new("unsupported_platform", "This initial awctl implementation supports Linux.", "Run awctl on the Linux machine hosting the control plane."));
            return 1;
        }
        try
        {
            client ??= new LocalControlClient(LocalControlEndpoint.Discover());
            var reply = await client.StatusAsync();
            await output.WriteLineAsync(JsonSerializer.Serialize(reply, Json));
            return 0;
        }
        catch (LocalControlException exception)
        {
            await WriteErrorAsync(error, exception.Diagnostic);
            return 1;
        }
        catch (IOException exception)
        {
            await WriteErrorAsync(error, new("invalid_local_endpoint", exception.Message,
                "Check XDG_RUNTIME_DIR and its ownership and permissions, then restart the control plane and retry awctl status."));
            return 1;
        }
    }

    private static Task WriteErrorAsync(TextWriter error, ControlDiagnostic diagnostic) =>
        error.WriteLineAsync(JsonSerializer.Serialize(diagnostic, Json));
}
