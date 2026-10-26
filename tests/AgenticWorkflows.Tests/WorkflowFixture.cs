using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

internal sealed class WorkflowFixture : IAsyncDisposable
{
    internal string Root { get; }
    internal IWorkflowRepository Repository { get; }
    internal SqliteWorkflowStore Store { get; }
    internal ControlledProviders Providers { get; }
    internal WorkflowApplication Application { get; set; }
    internal PassDelivery Delivery { get; } = new();
    internal WorkflowFixture(string? root = null, IWorkflowRepository? repository = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "aw-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Providers = new(Root);
        Repository = repository ?? Providers;
        Store = new(Path.Combine(Root, "workflows.db"));
        Application = CreateApplication();
    }
    internal WorkflowApplication CreateApplication() => new(Store, Providers, Providers, Providers, Providers, Repository, Delivery);
    internal async Task<Guid> Start()
    {
        await Application.ExecuteAsync(new("setup", Root));
        return (await Application.ExecuteAsync(new("start", Root, TaskIdentifier: "DEV-123"))).WorkflowId!.Value;
    }
    internal async Task<WorkflowData> Until(Guid id, Func<WorkflowData, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var data = Application.Get(id);
            if (predicate(data)) return data;
            try { await Task.Delay(20, deadline.Token); }
            catch (OperationCanceledException)
            { throw new Xunit.Sdk.XunitException($"Timed out. Phase={data.State.Phase}; recovery={data.State.Recovery}; events={string.Join(",", data.Events.Select(e => e.Kind))}"); }
        }
    }
    internal Task<WorkflowData> Pass(Guid id, int count) => Until(id, d => d.Passes.Length >= count && d.Operations.Last().Status == OperationStatus.Running);
    internal async Task Deliver(Guid id, PassResult result)
    {
        var pass = Application.Get(id).Passes.Last();
        byte[] bytes = ResultEnvelopeContract.Serialize(pass.OperationId, pass.Id, pass.Role, result);
        await Atomic(pass.DeliveryPath, bytes);
        Providers.Execution = ExecutionState.Completed;
    }
    internal static async Task Atomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes);
        File.Move(temporary, path);
    }
    internal async Task Restart()
    {
        await Application.DisposeAsync();
        Application = CreateApplication();
        await Application.ReconcileStartupAsync();
    }
    internal static ResultNarrative Report(ValidationEvidence[]? checks = null) => new("Completed requested work", "Detailed evidence and implementation assessment.", checks ?? [], [], [], []);
    internal static ImplementationResult Ready(FindingResolution[]? resolutions = null) => new("ReadyForReview", Report(), ["Requested behavior implemented"], resolutions ?? [], null);
    internal static ReviewResult Clean() => new("Clean", Report(), [], new("implement durable workflows", "Implements the requested behavior.", "Focused validation passed."), null);
    internal static Finding Finding() => new("F1", "Wrong routing", "The operation routes incorrectly.", "WorkflowApplication.cs", "Preserve the pending intent before external action.");
    public async ValueTask DisposeAsync()
    {
        await Application.DisposeAsync();
        Directory.Delete(Root, true);
    }
}

internal sealed class ControlledProviders(string root) : IWorkflowConfiguration, ILinearGateway, IGitHubGateway, IHerdrGateway, IWorkflowRepository
{
    internal LinearTask Source = new("issue-123", "DEV-123", "Durable task", "# Agent instructions\nImplement durable work.\n", "r1", "project", "team", "todo", "unstarted", "https://linear.app/example/issue/DEV-123", ["Feature"]);
    internal readonly List<string> Calls = [];
    internal int Launches;
    internal int Creations;
    internal bool FailLinear;
    internal bool LoseLaunchResponse;
    internal bool Unreachable;
    internal bool SessionLost;
    internal bool UniqueWork;
    internal bool Drift;
    internal bool RawWorktreeDrift;
    internal bool WorkspaceResponseLost;
    internal bool CredentialUnavailable;
    internal string? RepositoryFailure;
    internal string? ObservationFailure;
    internal Func<CancellationToken, Task<RepositoryCheckpoint>>? CaptureOverride;
    internal Func<CancellationToken, Task<Guidance>>? GuidanceOverride;
    internal Func<CancellationToken, Task<string>>? BranchTipOverride;
    internal Func<WorkspaceIntent, Task<WorkspaceIdentity>>? WorkspaceFactory;
    internal string? TeardownFailure;
    internal ManagedLocation? CopiedLocation;
    internal ExecutionState Execution = ExecutionState.Idle;
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ExecutionState> SessionObservations = new();
    internal WorkflowSettings Settings = new();
    internal PullRequestIdentity? PullRequest;
    internal string? SubmissionFailure;
    internal bool LosePrResponse;
    public Task<SubmissionCommit> PrepareSubmissionAsync(WorkflowState state, RepositoryCheckpoint checkpoint, string message, CancellationToken token)
    { Calls.Add("preflight"); if (SubmissionFailure is not null) throw Failure(SubmissionFailure); return Task.FromResult(new SubmissionCommit("main", "frozen-commit", "frozen-object")); }
    public Task VerifySubmissionAsync(WorkflowState state, PrIntent intent, CancellationToken token)
    { if (SubmissionFailure is not null) throw Failure(SubmissionFailure); return Task.CompletedTask; }
    public Task CommitSubmissionAsync(WorkflowState state, PrIntent intent, CancellationToken token)
    { Calls.Add("commit"); return Task.CompletedTask; }
    public Task PushSubmissionAsync(WorkflowState state, PrIntent intent, CancellationToken token)
    { Calls.Add("push"); return Task.CompletedTask; }
    public Task<PullRequestIdentity?> FindPullRequestAsync(PrIntent intent, CancellationToken token) => Task.FromResult(PullRequest);
    public Task<PullRequestIdentity> CreatePullRequestAsync(PrIntent intent, CancellationToken token)
    { Calls.Add("pr"); PullRequest = new(42, 1, "https://github.com/owner/repo/pull/1"); if (LosePrResponse) throw Failure("github_pr_unconfirmed"); return Task.FromResult(PullRequest); }
    private WorkspaceIdentity? workspace;
    private Guid? bound;
    public ProjectSettings ReadProject(string canonicalCheckout) => new(root, "main", Path.Combine(root, "managed"), "project", "team", "started", "1", "local:test", "env:AW_TEST_LINEAR", "env:AW_TEST_GITHUB", Settings);
    public WorkflowSettings ReadGlobal() => new();
    public void ValidateContent(string[] credentialReferences, string content)
    { if (CredentialUnavailable) throw Failure("credential_unavailable"); }
    public string ProjectFile(string canonicalCheckout) => Path.Combine(root, ".agentic-workflows.toml");
    public string DeliveryPath(Guid workflowId, Guid sessionId, Guid operationId) => Path.Combine(root, "deliveries", workflowId.ToString(), sessionId.ToString(), operationId.ToString(), "result.json");
    public Task<LinearTask> ReadTaskAsync(string identifierOrId, string credentialReference, CancellationToken cancellationToken) => Task.FromResult(Source);
    public Task<(ProviderProjection Project, ProviderProjection Team, ProviderProjection State)> ValidateProjectAsync(string projectId, string teamId, string? stateId, string credentialReference, CancellationToken cancellationToken) =>
        Task.FromResult((new ProviderProjection(projectId, "Project"), new ProviderProjection(teamId, "Team"), new ProviderProjection(stateId ?? "started", "In Progress")));
    public Task MoveToInProgressAsync(IntegrationBinding binding, CancellationToken cancellationToken)
    {
        Calls.Add("linear");
        if (FailLinear) throw Failure("linear_unavailable");
        Source = Source with { StateId = "started", StateType = "started" }; return Task.CompletedTask;
    }
    public Task<ProviderProjection> ValidateRepositoryAsync(string repositoryId, string remoteUrl, string credentialReference, CancellationToken cancellationToken) => Task.FromResult(new ProviderProjection(repositoryId, "owner/repo"));
    public Task<HerdrCapabilities> InspectTargetAsync(string target, CancellationToken cancellationToken) => Task.FromResult(new HerdrCapabilities(target, "test", 22, ["workspace", "pass"]));
    public Task<WorkspaceInspection> InspectWorkspacesAsync(string target, string repository, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceInspection(workspace is null ? [] : [workspace], workspace is null ? [] : [workspace.WorkspaceId]));
    public async Task<WorkspaceIdentity> PrepareWorkspaceAsync(WorkspaceIntent intent, bool alreadyAttempted, CancellationToken cancellationToken)
    {
        Calls.Add("workspace");
        if (workspace is not null && alreadyAttempted) return workspace;
        if (alreadyAttempted) throw Failure("workspace_uncertain");
        Creations++;
        if (WorkspaceFactory is not null) workspace = await WorkspaceFactory(intent);
        else
        {
            Directory.CreateDirectory(intent.ManagedPath);
            workspace = new(intent.HerdrTarget, Guid.NewGuid().ToString(), intent.ManagedPath, intent.Branch, intent.GitDirectory);
        }
        if (WorkspaceResponseLost) throw Failure("workspace_response_lost");
        return workspace;
    }
    public Task VerifyWorkspaceAsync(WorkspaceIdentity value, CancellationToken cancellationToken)
    {
        if (value != workspace) throw Failure("workspace_identity_mismatch");
        return Task.CompletedTask;
    }
    public Task<SessionPreparation> PrepareSessionAsync(WorkspaceIdentity workspace, AgentRole role, PassConfiguration configuration, AgentSession? retained, CancellationToken cancellationToken)
    {
        if (retained is not null && SessionLost) throw Failure("session_reuse_unavailable");
        Execution = ExecutionState.Idle;
        return Task.FromResult(retained is not null ? new SessionPreparation(retained, false) :
            new(new(Guid.NewGuid(), role, workspace.WorkspaceId, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), null, configuration.Model, DateTimeOffset.UtcNow, configuration.Reasoning), true));
    }
    public Task StartPassAsync(WorkspaceIdentity workspace, AgentSession session, ManagedPass pass, CancellationToken cancellationToken)
    {
        Calls.Add("launch"); Launches++;
        if (LoseLaunchResponse) { Execution = ExecutionState.Unknown; throw Failure("external_action_uncertain"); }
        Execution = ExecutionState.Running; return Task.CompletedTask;
    }
    public Task<ExecutionObservation> ObserveAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken)
    {
        if (Unreachable) throw Failure("herdr_unreachable");
        if (ObservationFailure is not null) throw Failure(ObservationFailure);
        var state = SessionObservations.GetValueOrDefault(session.Id, Execution);
        return Task.FromResult(new ExecutionObservation(state, DateTimeOffset.UtcNow, true, null, state == ExecutionState.Completed ? 1 : 0));
    }
    public Task InterruptAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken) { Execution = ExecutionState.Idle; return Task.CompletedTask; }
    public Task<string> ReadOutputAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken) => Task.FromResult("controlled output");
    public Task TeardownAsync(WorkspaceIdentity value, AgentSession[] sessions, bool force, CancellationToken cancellationToken)
    { Calls.Add("teardown"); if (value != workspace) throw Failure("identity_mismatch"); if (TeardownFailure is not null) throw Failure(TeardownFailure); return Task.CompletedTask; }
    public Task<RepositoryIdentity> InspectCanonicalAsync(string checkout, CancellationToken cancellationToken) => Task.FromResult(new RepositoryIdentity(root, Path.Combine(root, ".git"), "https://github.com/owner/repo.git"));
    public Task<string> AdmitAsync(RepositoryIdentity repository, string baseReference, string branch, string managedPath, CancellationToken cancellationToken) => Task.FromResult("baseline-commit");
    public Task VerifyWorkspaceAsync(WorkspaceIntent intent, WorkspaceIdentity value, CancellationToken cancellationToken) =>
        RepositoryFailure is not null ? throw Failure(RepositoryFailure) : VerifyWorkspaceAsync(value, cancellationToken);
    public Task<RepositoryCheckpoint> CaptureAsync(WorkspaceIdentity workspace, string baselineCommit, CancellationToken cancellationToken) =>
        CaptureOverride?.Invoke(cancellationToken) ?? Task.FromResult(new RepositoryCheckpoint(new("sha1", "baseline-tree", Drift ? "changed-tree" : "proposed-tree"), "head", workspace.Branch, "index", ["file.cs"], DateTimeOffset.UtcNow,
            RawWorktreeDrift ? "changed-materialized-bytes" : "original-materialized-bytes"));
    public Task<ManagedLocation?> ResolveWorkflowAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(CopiedLocation ?? (path == workspace?.Path && bound is Guid id ? new ManagedLocation(id, workspace.Path) : null));
    public Task BindWorkflowAsync(WorkspaceIdentity workspace, Guid workflowId, CancellationToken cancellationToken) { bound = workflowId; return Task.CompletedTask; }
    public Task<Guidance> ReadGuidanceAsync(WorkspaceIdentity workspace, CancellationToken cancellationToken) =>
        GuidanceOverride?.Invoke(cancellationToken) ?? Task.FromResult(new Guidance("head", ["AGENTS.md", "README.md"]));
    public Task<bool> HasUniqueWorkAsync(WorkspaceIdentity workspace, string baseline, string boundRemoteUrl, CancellationToken cancellationToken) => Task.FromResult(UniqueWork);
    public Task DeleteBranchAsync(WorkspaceIntent intent, string expectedTip, CancellationToken cancellationToken) { Calls.Add("delete-branch"); return Task.CompletedTask; }
    public Task<string> ReadBranchTipAsync(WorkspaceIntent intent, CancellationToken cancellationToken) => BranchTipOverride?.Invoke(cancellationToken) ?? Task.FromResult("head");
    private static WorkflowException Failure(string code) => new(code, code, "Restore the controlled dependency.");
}
