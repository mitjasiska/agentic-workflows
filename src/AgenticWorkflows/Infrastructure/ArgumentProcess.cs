using System.Diagnostics;
using System.Text;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed record ProcessOutput(int ExitCode, string Output);
public sealed record PreparedProcessInput(string Prepare, string[] Acknowledgements,
    Func<CancellationToken, Task> Verify, string Commit);
public interface IArgumentProcess
{
    Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);
    Task<ProcessOutput> RunPreparedAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, PreparedProcessInput input, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This process adapter does not support prepared transactions.");
}

/// <summary>Infrastructure-only process boundary. No shell, terminal scraping, or retained process output.</summary>
public sealed class ArgumentProcess : IArgumentProcess
{
    public Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
        Run(executable, arguments, directory, environment, null, cancellationToken);

    public Task<ProcessOutput> RunPreparedAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, PreparedProcessInput input, CancellationToken cancellationToken) =>
        Run(executable, arguments, directory, environment, input, cancellationToken);

    private static async Task<ProcessOutput> Run(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, PreparedProcessInput? input, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input is not null };
        if (directory is not null) info.WorkingDirectory = directory;
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            if (input is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(input.Prepare.AsMemory(), deadline.Token);
                    await process.StandardInput.FlushAsync(deadline.Token);
                    foreach (string acknowledgement in input.Acknowledgements)
                    {
                        // Read only the expected bytes, never an unbounded provider line.
                        char[] response = new char[acknowledgement.Length];
                        int read = await process.StandardOutput.ReadBlockAsync(response, deadline.Token);
                        if (read != response.Length || !response.AsSpan().SequenceEqual(acknowledgement))
                            throw new WorkflowException("process_prepare_failed", "The command did not acknowledge its prepared transaction.", "Inspect the retained operation and reconcile before retrying.");
                    }
                    await input.Verify(deadline.Token);
                    await process.StandardInput.WriteAsync(input.Commit.AsMemory(), deadline.Token);
                    await process.StandardInput.FlushAsync(deadline.Token);
                }
                finally { process.StandardInput.Close(); } // EOF aborts an uncommitted Git transaction.
            }
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await error; // Drain stderr but never echo provider values, credentials, or prompts into diagnostics.
            return new(process.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            if (input is null && !process.HasExited) process.Kill(entireProcessTree: true);
            throw new WorkflowException("external_action_uncertain", "The structured command did not complete within its deadline.", "Reconcile its durable intent before retrying; the external action may have succeeded.");
        }
        catch (System.ComponentModel.Win32Exception)
        { throw new WorkflowException("executable_unavailable", $"Required executable {executable} could not be started.", "Install the dependency for the control-plane service account and use awctl resume."); }
        finally
        {
            // In particular, wait for an aborted prepared transaction to release its locks.
            if (input is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.StandardInput.Close();
                        // The request deadline may already be canceled. Give EOF a separate bounded
                        // grace period so Git can abort and remove its locks before resorting to kill.
                        using var abortDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(abortDeadline.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException) { } // Starting the executable failed.
            }
        }
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
