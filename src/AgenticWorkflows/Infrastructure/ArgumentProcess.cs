using System.Diagnostics;
using System.Text;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed record ProcessOutput(int ExitCode, string Output);
public interface IArgumentProcess
{
    Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);
}

/// <summary>Infrastructure-only process boundary. No shell, terminal scraping, or retained process output.</summary>
public sealed class ArgumentProcess : IArgumentProcess
{
    public async Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (directory is not null) info.WorkingDirectory = directory;
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await error; // Drain stderr but never echo provider values, credentials, or prompts into diagnostics.
            return new(process.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new WorkflowException("external_action_uncertain", "The structured command did not complete within its deadline.", "Reconcile its durable intent before retrying; the external action may have succeeded.");
        }
        catch (System.ComponentModel.Win32Exception)
        { throw new WorkflowException("executable_unavailable", $"Required executable {executable} could not be started.", "Install the dependency for the control-plane service account and use awctl resume."); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) return builder.ToString();
            if (builder.Length + count > 4 * 1024 * 1024)
                throw new WorkflowException("provider_output_oversized", "Structured provider output exceeds 4 MiB.", "Inspect the external dependency; AW did not truncate or accept the response.");
            builder.Append(buffer, 0, count);
        }
    }
}
