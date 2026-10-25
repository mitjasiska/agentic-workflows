using System.Text.Json;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;
using AgenticWorkflows.Source;

return await AwctlCommand.RunAsync(args, Console.Out, Console.Error);

internal static class AwctlCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private const string Usage = """
        Usage: awctl status
               awctl service start|stop|restart|status|enable|disable
               awctl setup
               awctl start TASK-ID [--max-passes N|unlimited]
               awctl workflows [--offset N]
               awctl workflow|history|snapshot|warnings|attention [WORKFLOW-ID] [--after N] [--offset N]
               awctl report|handoff [WORKFLOW-ID] --pass PASS-ID [--offset N]
               awctl resume [WORKFLOW-ID] [--source retain|adopt|stop] [--cancellation continue|stop] [--decision TEXT]
               awctl continue [WORKFLOW-ID] [--max-passes N|unlimited]
               awctl review|interrupt|abandon [WORKFLOW-ID]
               awctl select [WORKFLOW-ID] --role implementer|reviewer [--model MODEL|clear] [--session new|reuse|clear]
               awctl cleanup [WORKFLOW-ID] [--force]
               awctl database status|reset|restore [--force] [--backup PATH]
        Existing workflows require their immutable ID outside a proven managed worktree.
        No command in this slice commits, pushes, creates a pull request, or marks Linear Done.
        """;

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        LocalControlClient? client = null, ServiceControl? service = null)
    {
        bool inspectService = client is null;
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        if (args.Length == 0)
        {
            await WriteErrorAsync(error, new("usage", "Expected a local command.", Usage));
            return 2;
        }
        if (!OperatingSystem.IsLinux())
        {
            await WriteErrorAsync(error, new("unsupported_platform", "This initial awctl implementation supports Linux.", "Run awctl on the Linux machine hosting the control plane."));
            return 1;
        }
        try
        {
            object? request = Parse(args);
            if (request is ServiceCommand serviceCommand)
            {
                var result = await (service ?? ServiceControl.Create()).ExecuteAsync(serviceCommand, error);
                await output.WriteLineAsync(JsonSerializer.Serialize(result, Json));
                return serviceCommand.Action == "status" && (result.Service.ActiveState != "active" || result.Application is null || result.ServiceDiagnostic is not null) ? 1 : 0;
            }
            client ??= new LocalControlClient(LocalControlEndpoint.Discover());
            object reply = request switch
            {
                WorkflowCommand command => await client.CommandAsync(command),
                WorkflowQuery query => await client.QueryAsync(query),
                DatabaseCommand database => await client.DatabaseAsync(database),
                _ => await client.StatusAsync()
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(reply, Json));
            if (reply is WorkflowCommandReply { Message: "cleanup_partial" }) return 3;
            return 0;
        }
        catch (ArgumentException)
        {
            await WriteErrorAsync(error, new("usage", "Invalid command arguments.", Usage));
            return 2;
        }
        catch (LocalControlException exception)
        {
            var diagnostic = exception.Diagnostic;
            if (inspectService && diagnostic.Code == "control_plane_unavailable")
            {
                try
                {
                    var observed = await ServiceControl.Create().InspectManagerAsync();
                    diagnostic = diagnostic with { Message = diagnostic.Message + " Observed user service: " + JsonSerializer.Serialize(observed, Json) };
                }
                catch (Exception unavailable) when (unavailable is IOException or LocalControlException or UnauthorizedAccessException)
                { diagnostic = diagnostic with { Message = diagnostic.Message + " User-service state unavailable; run awctl service status." }; }
            }
            await WriteErrorAsync(error, diagnostic);
            return 1;
        }
        catch (Exception exception) when (args[0] == "service" && exception is IOException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(error, new("source_setup_invalid", exception.Message,
                "Inspect the source ownership files; from the configured checkout rerun ./setup-source.sh. " +
                "Build missing support artifacts with ./build.sh. Logs: " + SourceSetup.Logs));
            return 1;
        }
        catch (IOException exception)
        {
            await WriteErrorAsync(error, new("invalid_local_endpoint", exception.Message,
                "Check XDG_RUNTIME_DIR and its ownership and permissions, then restart the control plane and retry awctl status."));
            return 1;
        }
    }

    internal static object? Parse(string[] args)
    {
        if (args is ["service", "start" or "stop" or "restart" or "status" or "enable" or "disable"])
            return new ServiceCommand(args[1]);
        if (args is ["status"]) return null;
        if (args.Length == 0 || args[0] == "status") throw new ArgumentException();
        string action = args[0];
        string? positional = null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index++)
        {
            string item = args[index];
            if (!item.StartsWith("--", StringComparison.Ordinal))
            { if (positional is not null) throw new ArgumentException(); positional = item; continue; }
            string value = item == "--force" ? "true" : ++index < args.Length ? args[index] : throw new ArgumentException();
            if (!options.TryAdd(item, value)) throw new ArgumentException();
        }
        string? Take(string name) => options.Remove(name, out var value) ? value : null;
        int Offset() => int.TryParse(Take("--offset") ?? "0", out int value) && value >= 0 ? value : throw new ArgumentException();
        Guid? Id() => positional is null ? null : Guid.TryParse(positional, out var id) ? id : throw new ArgumentException();
        object result;
        string cwd = Directory.GetCurrentDirectory();
        if (action == "database")
            result = new DatabaseCommand(positional ?? throw new ArgumentException(), Take("--force") == "true", Take("--backup"));
        else if (action is "workflows" or "workflow" or "history" or "snapshot" or "warnings" or "attention" or "report" or "handoff")
        {
            long after = long.TryParse(Take("--after") ?? "0", out long value) && value >= 0 ? value : throw new ArgumentException();
            Guid? pass = Take("--pass") is string passText ? Guid.TryParse(passText, out var parsed) ? parsed : throw new ArgumentException() : null;
            result = new WorkflowQuery(cwd, Id(), action == "workflows" ? "list" : action, after, pass, Offset());
        }
        else if (action is "setup" or "start" or "resume" or "continue" or "review" or "interrupt" or "abandon" or "cleanup" or "select")
        {
            if (action == "setup" && positional is not null || action == "start" && positional is null) throw new ArgumentException();
            AgentRole? role = Take("--role") switch { "implementer" => AgentRole.Implementer, "reviewer" => AgentRole.Reviewer, null => null, _ => throw new ArgumentException() };
            string? budget = Take("--max-passes"), source = Take("--source"), canceled = Take("--cancellation"), decision = Take("--decision"), model = Take("--model"), session = Take("--session");
            bool force = Take("--force") == "true";
            if (budget is not null && action is not ("start" or "continue") || force && action != "cleanup" ||
                (source is not null || canceled is not null || decision is not null) && action != "resume" ||
                (role is not null || model is not null || session is not null) && action != "select") throw new ArgumentException();
            result = new WorkflowCommand(action, cwd, action is "setup" or "start" ? null : Id(), action == "start" ? positional : null,
                budget, source, canceled, decision, role, model, session, force);
        }
        else throw new ArgumentException();
        if (options.Count != 0) throw new ArgumentException();
        return result;
    }

    private static Task WriteErrorAsync(TextWriter error, ControlDiagnostic diagnostic) =>
        error.WriteLineAsync(JsonSerializer.Serialize(diagnostic, Json));
}
