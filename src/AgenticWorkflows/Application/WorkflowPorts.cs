using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Application;

public interface IWorkflowStore
{
    ProjectRegistration? FindProject(string canonicalCheckout);
    void RegisterProject(ProjectRegistration project);
    WorkflowData? Find(Guid workflowId);
    WorkflowState[] List(Guid? projectId = null);
    WorkflowState? FindDuplicate(Guid projectId, string taskProviderId);
    void Create(ProjectRegistration project, string taskProviderId, WorkflowChange change);
    void Save(long expectedVersion, WorkflowChange change);
    void Delete(Guid workflowId, long expectedVersion);
    void Backup(DateOnly day);
}

public interface ILinearGateway
{
    Task<LinearTask> ReadTaskAsync(string identifierOrId, string credentialReference, CancellationToken cancellationToken);
    Task<(ProviderProjection Project, ProviderProjection Team, ProviderProjection State)> ValidateProjectAsync(
        string projectId, string teamId, string? stateId, string credentialReference, CancellationToken cancellationToken);
    Task MoveToInProgressAsync(IntegrationBinding binding, CancellationToken cancellationToken);
}

public interface IGitHubGateway
{
    Task<ProviderProjection> ValidateRepositoryAsync(string repositoryId, string remoteUrl,
        string credentialReference, CancellationToken cancellationToken);
}

public sealed record HerdrCapabilities(string Target, string Version, int Protocol, string[] Operations);
public sealed record WorkspaceInspection(WorkspaceIdentity[] Workspaces, string[] ExistingWorkspaceIds);
public sealed record SessionPreparation(AgentSession Session, bool NewSession);
public interface IHerdrGateway
{
    Task<HerdrCapabilities> InspectTargetAsync(string target, CancellationToken cancellationToken);
    Task<WorkspaceInspection> InspectWorkspacesAsync(string target, string repository, CancellationToken cancellationToken);
    Task<WorkspaceIdentity> PrepareWorkspaceAsync(WorkspaceIntent intent, bool alreadyAttempted, CancellationToken cancellationToken);
    Task VerifyWorkspaceAsync(WorkspaceIdentity workspace, CancellationToken cancellationToken);
    Task<SessionPreparation> PrepareSessionAsync(WorkspaceIdentity workspace, AgentRole role,
        PassConfiguration configuration, AgentSession? retained, CancellationToken cancellationToken);
    Task StartPassAsync(WorkspaceIdentity workspace, AgentSession session, ManagedPass pass, CancellationToken cancellationToken);
    Task<ExecutionObservation> ObserveAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken);
    Task InterruptAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken);
    Task<string> ReadOutputAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken);
    Task TeardownAsync(WorkspaceIdentity workspace, AgentSession[] sessions, bool force, CancellationToken cancellationToken);
}

public sealed record RepositoryIdentity(string Checkout, string GitDirectory, string RemoteUrl, string? Stamp = null);
public sealed record ManagedLocation(Guid WorkflowId, string WorktreePath);
public interface IWorkflowRepository
{
    Task<RepositoryIdentity> InspectCanonicalAsync(string checkout, CancellationToken cancellationToken);
    Task<string> AdmitAsync(RepositoryIdentity repository, string baseReference, string branch, string managedPath,
        CancellationToken cancellationToken);
    Task VerifyWorkspaceAsync(WorkspaceIntent intent, WorkspaceIdentity workspace, CancellationToken cancellationToken);
    Task<RepositoryCheckpoint> CaptureAsync(WorkspaceIdentity workspace, string baselineCommit, CancellationToken cancellationToken);
    Task<ManagedLocation?> ResolveWorkflowAsync(string path, CancellationToken cancellationToken);
    Task BindWorkflowAsync(WorkspaceIdentity workspace, Guid workflowId, CancellationToken cancellationToken);
    Task<Guidance> ReadGuidanceAsync(WorkspaceIdentity workspace, CancellationToken cancellationToken);
    Task<bool> HasUniqueWorkAsync(WorkspaceIdentity workspace, string baseline, string boundRemoteUrl, CancellationToken cancellationToken);
    Task<string> ReadBranchTipAsync(WorkspaceIntent intent, CancellationToken cancellationToken);
    Task DeleteBranchAsync(WorkspaceIntent intent, string expectedTip, CancellationToken cancellationToken);
}

public sealed record WorkflowSettings(string? ImplementerModel = null, string? ReviewerModel = null,
    string? Reasoning = null, string? ImplementerSession = null, string? ReviewerSession = null,
    string? InstructionsPolicy = null, string? MaxPasses = null);
public sealed record ProjectSettings(string CanonicalCheckout, string BaseReference, string ManagedRoot,
    string LinearProjectId, string LinearTeamId, string? InProgressStateId, string GitHubRepositoryId,
    string HerdrTarget, string LinearCredentialReference, string GitHubCredentialReference,
    WorkflowSettings Defaults);
public interface IWorkflowConfiguration
{
    ProjectSettings ReadProject(string canonicalCheckout);
    WorkflowSettings ReadGlobal();
    string ProjectFile(string canonicalCheckout);
    string DeliveryPath(Guid workflowId, Guid sessionId, Guid operationId);
    void ValidateContent(string[] credentialReferences, string content);
}

public interface IPassDelivery
{
    Handoff Compose(WorkflowState workflow, TaskSnapshot snapshot, Guid passId, Guid operationId, AgentRole role,
        PassConfiguration configuration, Guidance guidance, RepositoryCheckpoint? target, Finding[] findings,
        ValidatedResult? implementationResult, string deliveryPath);
    Task PrepareAsync(ManagedPass pass, CancellationToken cancellationToken);
    Task<ValidatedResult?> ReadAsync(ManagedPass pass, CancellationToken cancellationToken);
    void Retire(ManagedPass pass);
    void Cleanup(ManagedPass[] passes);
}
