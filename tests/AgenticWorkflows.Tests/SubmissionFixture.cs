using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

internal sealed class SubmissionFixture : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aw-submit-" + Guid.NewGuid().ToString("N"));
    internal string Canonical => Path.Combine(Root, "canonical");
    internal string Remote => Path.Combine(Root, "remote.git");
    internal LocalGitTransport Transport { get; }
    internal GitWorkflowRepository Repository { get; }
    internal WorkflowFixture Workflow { get; private set; } = null!;
    internal Guid Id;
    internal WorkflowData Data => Workflow.Application.Get(Id);
    internal string Worktree => Data.State.Workspace!.Path;
    private SubmissionFixture() { Transport = new(Remote); Repository = new(Transport); }
    internal static async Task<SubmissionFixture> Create(bool repair = false)
    {
        var f = new SubmissionFixture(); Directory.CreateDirectory(f.Canonical);
        await f.Git(f.Root, "init", "--bare", f.Remote);
        await f.Git(f.Canonical, "init", "-b", "main");
        await f.Git(f.Canonical, "config", "user.name", "AW fixture");
        await f.Git(f.Canonical, "config", "user.email", "aw@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(f.Canonical, ".gitignore"), "managed/\ndeliveries/\n*.db*\n");
        await File.WriteAllTextAsync(Path.Combine(f.Canonical, "file.txt"), "baseline\n");
        await f.Git(f.Canonical, "add", "."); await f.Git(f.Canonical, "commit", "-m", "baseline fixture");
        await f.Git(f.Canonical, "remote", "add", "origin", LocalGitTransport.Url);
        await f.Git(f.Canonical, "push", f.Remote, "main");
        f.Workflow = new(f.Canonical, f.Repository);
        f.Workflow.Providers.WorkspaceFactory = async intent =>
        {
            await f.Git(f.Canonical, "worktree", "add", "-b", intent.Branch, intent.ManagedPath, intent.BaseCommit);
            return new(intent.HerdrTarget, "owned-workspace", intent.ManagedPath, intent.Branch, intent.GitDirectory);
        };
        f.Id = await f.Workflow.Start(); await f.Workflow.Pass(f.Id, 1);
        await File.WriteAllTextAsync(Path.Combine(f.Worktree, "file.txt"), "reviewed change\n");
        await f.Workflow.Deliver(f.Id, WorkflowFixture.Ready()); await f.Workflow.Pass(f.Id, 2);
        if (repair)
        {
            await f.Workflow.Deliver(f.Id, new ReviewResult("ChangesRequired", WorkflowFixture.Report(), [WorkflowFixture.Finding()], null, null));
            await f.Workflow.Pass(f.Id, 3);
            await File.WriteAllTextAsync(Path.Combine(f.Worktree, "repair.txt"), "reviewed repair\n");
            await f.Workflow.Deliver(f.Id, WorkflowFixture.Ready([new("F1", "Fixed with regression proof.")]));
            await f.Workflow.Pass(f.Id, 4);
        }
        await f.Workflow.Deliver(f.Id, WorkflowFixture.Clean());
        await f.Workflow.Until(f.Id, d => d.State.Phase == WorkflowPhase.Submission);
        return f;
    }
    internal async Task<WorkflowData> Submit(bool force = false)
    {
        var accepted = await Workflow.Application.ExecuteAsync(new("pr", Canonical, Id, Force: force));
        return await Workflow.Until(Id, d => d.Operations.Single(o => o.Id == accepted.OperationId).Status is OperationStatus.Completed or OperationStatus.Failed);
    }
    internal async Task<string> Git(string directory, params string[] args)
    {
        var result = await new ArgumentProcess().RunAsync("git", args, directory, null, default);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)}: {result.ExitCode}");
        return result.Output.TrimEnd('\n', '\r');
    }
    public async ValueTask DisposeAsync()
    {
        if (Workflow is not null) await Workflow.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

// Only the exact synthetic URL is redirected, at the process boundary. No test reaches a service.
internal sealed class LocalGitTransport(string remote) : IArgumentProcess
{
    internal const string Url = "https://github.com/owner/repo.git";
    internal string? FailAfter;
    internal Func<string[], Task>? Before;
    internal Func<Task>? AfterPrepared;
    internal readonly List<string> Effects = [];
    public Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
        Run(executable, arguments, directory, environment, null, cancellationToken);
    public Task<ProcessOutput> RunPreparedAsync(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, PreparedProcessInput input, CancellationToken cancellationToken) =>
        Run(executable, arguments, directory, environment, input with { Verify = async token =>
        {
            if (AfterPrepared is not null) await AfterPrepared();
            await input.Verify(token);
        } }, cancellationToken);
    private async Task<ProcessOutput> Run(string executable, IReadOnlyList<string> arguments, string? directory,
        IReadOnlyDictionary<string, string?>? environment, PreparedProcessInput? input, CancellationToken cancellationToken)
    {
        string[] args = [.. arguments];
        if (Before is not null) await Before(args);
        if (args[0] is "ls-remote" or "fetch" || args.Contains("push", StringComparer.Ordinal))
            args = args.Select(a => a is Url or "origin" ? remote : a).ToArray();
        if (args.Any(a => a.StartsWith("https://", StringComparison.Ordinal)) && args[0] is "ls-remote" or "fetch" or "push")
            throw new InvalidOperationException("Live network access is prohibited by the fixture.");
        string? effect = args.Contains("push", StringComparer.Ordinal) ? "push" : args[0] == "update-ref" ? "ref" : args[0] == "hash-object" && args.Contains("-w") ? "commit" : null;
        var process = new ArgumentProcess();
        var result = input is null ? await process.RunAsync(executable, args, directory, environment, cancellationToken) :
            await process.RunPreparedAsync(executable, args, directory, environment, input, cancellationToken);
        if (effect is not null && result.ExitCode == 0)
        {
            Effects.Add(effect);
            if (FailAfter == effect) { FailAfter = null; throw new WorkflowException("lost_git_response", "Controlled success before lost response.", "Retry awctl pr."); }
        }
        return result;
    }
}

internal sealed class SubmissionFaultStore(IWorkflowStore inner, string eventKind, bool after) : IWorkflowStore
{
    private bool armed = true;
    public void Save(long expectedVersion, WorkflowChange change)
    {
        bool fail = armed && change.Events.Any(e => e.Kind == eventKind);
        if (fail) armed = false;
        if (fail && !after) throw new IOException("Controlled checkpoint failure before transaction.");
        inner.Save(expectedVersion, change);
        if (fail && after) throw new IOException("Controlled checkpoint acknowledgement loss.");
    }
    public ProjectRegistration? FindProject(string canonicalCheckout) => inner.FindProject(canonicalCheckout);
    public void RegisterProject(ProjectRegistration project) => inner.RegisterProject(project);
    public WorkflowData? Find(Guid id) => inner.Find(id);
    public WorkflowState[] List(Guid? projectId = null) => inner.List(projectId);
    public WorkflowState? FindDuplicate(Guid projectId, string taskProviderId) => inner.FindDuplicate(projectId, taskProviderId);
    public void Create(ProjectRegistration project, string taskProviderId, WorkflowChange change) => inner.Create(project, taskProviderId, change);
    public void Delete(Guid id, long version) => inner.Delete(id, version);
    public void Backup(DateOnly day) => inner.Backup(day);
}
