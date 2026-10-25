namespace AgenticWorkflows.Workflows;

public enum WorkflowPhase { WorkspacePreparation, Implementation, Review, Submission }
public enum OperationStatus { Pending, Running, Completed, Failed, Interrupted }
public enum AgentRole { Implementer, Reviewer }
public enum AttentionReason { TaskChangeDecision, PassContinuationDecision, PassBudgetContinuation, LinearCancellationDecision, RepositoryReconciliation }
public enum ExecutionState { Unknown, Idle, Running, Completed, Gone }
public enum CheckResult { Passed, Failed, NotRun }

public sealed class WorkflowException(string code, string message, string guidance) : Exception(message)
{
    public string Code { get; } = code;
    public string Guidance { get; } = guidance;
}

public sealed record RecoveryPlan(string Code, string Explanation, string[] Commands, string PreservedEvidence);
public sealed record Attention(AttentionReason Reason, string Revision, string Explanation, string ProposedResolution);
public sealed record Notice(string Code, string Message, string? Evidence = null);
public sealed record ValidationEvidence(string Name, CheckResult Result, string Details, string Source,
    string? Revision, string? LimitationClassification, string? ClassificationEvidence);
public sealed record Finding(string Id, string Summary, string Details, string Location, string RequestedChange);
public sealed record FindingResolution(string FindingId, string Summary);
public sealed record Decision(string UnresolvedMatter, string UnsafeContinuationRationale, string ProposedResolution);
public sealed record PublicationMetadata(string Summary, string Description, string Validation);
public sealed record Measurements(TimeSpan? Elapsed, long? InputTokens, long? OutputTokens, decimal? Cost, string? Currency);
public sealed record ResultNarrative(string Summary, string Narrative, ValidationEvidence[] Checks,
    Notice[] Warnings, Notice[] Limitations, Notice[] UnresolvedRisks);
public abstract record PassResult(ResultNarrative Report, Decision? Decision);
public sealed record ImplementationResult(string Outcome, ResultNarrative Report, string[] CompletedWork,
    FindingResolution[] Resolutions, Decision? Decision) : PassResult(Report, Decision);
public sealed record ReviewResult(string Outcome, ResultNarrative Report, Finding[] Findings,
    PublicationMetadata? PublicMetadata, Decision? Decision) : PassResult(Report, Decision);
public sealed record ValidatedResult(int SchemaVersion, Guid OperationId, Guid PassId, AgentRole Role,
    PassResult Payload, string CompleteJson);

public sealed record LinearTask(string Id, string Identifier, string Title, string Description, string Revision,
    string ProjectId, string TeamId, string StateId, string StateType, string Url);
public sealed record LinearObservation(string Revision, string StateId, string StateType, DateTimeOffset ObservedAt);
public sealed record TaskSnapshot(Guid Id, string Identifier, string Title, string Description,
    string Requirements, string Fingerprint, string SourceRevision, DateTimeOffset CapturedAt, bool HasAgentInstructions);
public sealed record ProviderProjection(string Id, string Name);
public sealed record ProjectRegistration(Guid Id, string CanonicalCheckout, string GitDirectory, string ConfigurationPath,
    ProviderProjection LinearProject, ProviderProjection LinearTeam, ProviderProjection InProgressState,
    ProviderProjection GitHubRepository, string RemoteUrl, string HerdrTarget, DateTimeOffset ValidatedAt, string? RepositoryStamp = null);
public sealed record IntegrationBinding(Guid ProjectId, string Repository, string GitDirectory, string RemoteUrl,
    string LinearTaskId, string LinearProjectId, string LinearTeamId, string InProgressStateId,
    string GitHubRepositoryId, string HerdrTarget, string LinearCredentialReference, string GitHubCredentialReference, string? RepositoryStamp = null);
public sealed record WorkspaceIntent(Guid WorkflowId, string Repository, string GitDirectory, string BaseCommit,
    string Branch, string ManagedPath, string HerdrTarget, string[] ExistingWorkspaceIds, DateTimeOffset AdmittedAt, string? RepositoryStamp = null);
public sealed record OwnedPane(string PaneId, string TerminalId);
public sealed record WorkspaceIdentity(string Target, string WorkspaceId, string Path, string Branch, string GitDirectory, OwnedPane[]? InitialPanes = null);
public sealed record AgentSession(Guid Id, AgentRole Role, string WorkspaceId, string PaneId, string TerminalId,
    string? NativeResumeReference, string Model, DateTimeOffset CreatedAt, string Reasoning = "medium", string? DeliveryRoot = null);
public sealed record ExecutionObservation(ExecutionState State, DateTimeOffset VerifiedAt, bool Reachable,
    string? Diagnostic, long? CompletionSequence, string? NativeSessionReference = null);
public sealed record ReviewTarget(string ObjectFormat, string BaselineTree, string ProposedTree);
public sealed record RepositoryCheckpoint(ReviewTarget Target, string Head, string Branch, string IndexIdentity,
    string[] ChangedPaths, DateTimeOffset CapturedAt, string? WorktreeIdentity = null)
{
    public bool SameContent(RepositoryCheckpoint other) => Target == other.Target;
    public bool SameReviewState(RepositoryCheckpoint other) => SameContent(other) && Head == other.Head &&
        Branch == other.Branch && IndexIdentity == other.IndexIdentity && WorktreeIdentity == other.WorktreeIdentity;
}
public sealed record PassConfiguration(string Model, string Reasoning, string SessionPolicy, string InstructionsPolicy);
public sealed record GuidanceFile(string Path, string ContentIdentity, string Text);
public sealed record Guidance(string Revision, string[] Paths, GuidanceFile[]? Files = null);
public sealed record Handoff(string Text, string PassBrief, Guidance Guidance);
public sealed record ManagedPass(Guid Id, Guid OperationId, AgentRole Role, Guid SnapshotId, Guid SessionId,
    PassConfiguration Configuration, Handoff Handoff, string DeliveryPath, RepositoryCheckpoint? ReviewCheckpoint,
    Finding[] SuppliedFindings, DateTimeOffset CreatedAt);
public sealed record WorkflowOperation(Guid Id, Guid? PredecessorId, string Kind, OperationStatus Status,
    Guid? PassId, bool ExternalActionAttempted, ExecutionObservation? Observation, RecoveryPlan? Recovery,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);
public sealed record ReviewReport(Guid PassId, Guid SnapshotId, ReviewTarget Target, string Outcome,
    bool AuthorityValid, string? InvalidationReason, PublicationMetadata? PublicMetadata);
public sealed record SourceRetention(string SourceRevision, string SourceFingerprint, Guid SnapshotId);
public sealed record WorkflowEvent(long Position, string Kind, string Summary, Guid? OperationId,
    Guid? PassId, DateTimeOffset At, SourceRetention? SourceRetention = null);

/// <summary>Current facts only. Immutable evidence is retained separately by the workflow store.</summary>
public sealed record WorkflowState(Guid Id, Guid TaskId, IntegrationBinding Binding, WorkspaceIntent WorkspaceIntent,
    Guid ActiveSnapshotId, WorkflowPhase Phase, WorkspaceIdentity? Workspace, Guid CurrentOperationId,
    bool Abandoned, bool StartupLinearCompleted, int? BatchLimit, int BatchStarted, int TotalPasses,
    string HandledSourceFingerprint, string? AcceptedCancellationRevision, LinearObservation LatestLinearObservation,
    Attention[] Attention, Notice[] Warnings, ReviewReport? Review, RecoveryPlan? Recovery,
    Dictionary<AgentRole, string> ModelSelections, Dictionary<AgentRole, string> NextSessionChoices,
    DateTimeOffset LastActivity, long Version);

public sealed record WorkflowData(WorkflowState State, TaskSnapshot[] Snapshots, WorkflowOperation[] Operations,
    ManagedPass[] Passes, AgentSession[] Sessions, ValidatedResult[] Results, WorkflowEvent[] Events, ReviewReport[] ReviewReports);
public sealed record WorkflowChange(WorkflowState State, WorkflowEvent[] Events,
    TaskSnapshot[] Snapshots, WorkflowOperation[] Operations, ManagedPass[] Passes, AgentSession[] Sessions,
    ValidatedResult[] Results, ReviewReport[]? ReviewReports = null);
