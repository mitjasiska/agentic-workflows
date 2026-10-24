using System.Collections.Concurrent;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Application;

public sealed record WorkflowCommand(string Action, string WorkingDirectory, Guid? WorkflowId = null,
    string? TaskIdentifier = null, string? MaxPasses = null, string? SourceDecision = null,
    string? CancellationDecision = null, string? DecisionResponse = null, AgentRole? Role = null,
    string? Model = null, string? Session = null, bool Force = false);
public sealed record WorkflowAcceptance(Guid? WorkflowId, Guid? OperationId, string Message, WorkflowState? Current,
    string[]? ComponentOutcomes = null);

/// <summary>Transport-independent commands. Durable intents own work after the caller disconnects.</summary>
public sealed class WorkflowApplication(IWorkflowStore store, IWorkflowConfiguration configuration, ILinearGateway linear,
    IGitHubGateway github, IHerdrGateway herdr, IWorkflowRepository repository, IPassDelivery delivery) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private readonly object runnerSync = new();
    private readonly HashSet<Guid> requestedRuns = [];
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private int disposed;
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public async Task<WorkflowAcceptance> ExecuteAsync(WorkflowCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Action == "setup") return await Setup(command.WorkingDirectory, cancellationToken);
        if (command.Action == "start") return await Start(command, cancellationToken);
        Guid id = await ResolveAsync(command.WorkflowId, command.WorkingDirectory, cancellationToken);
        var gate = gates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var data = Get(id); var state = data.State;
            switch (command.Action)
            {
                case "abandon":
                    Save(state with { Abandoned = true }, "WorkflowAbandoned", "Automatic routing stopped; all work and evidence retained.");
                    break;
                case "select":
                    if (command.Role is not AgentRole role) throw Error("role_required", "Select an explicit Implementer or Reviewer role.");
                    var models = new Dictionary<AgentRole, string>(state.ModelSelections);
                    var sessions = new Dictionary<AgentRole, string>(state.NextSessionChoices);
                    if (command.Model is not null)
                    {
                        if (command.Model == "clear") models.Remove(role); else models[role] = command.Model;
                    }
                    if (command.Session is not null)
                    {
                        WorkflowRules.ValidateChoice(command.Session, ["new", "reuse", "clear"], "next session choice");
                        if (command.Session == "clear") sessions.Remove(role); else sessions[role] = command.Session;
                    }
                    var selected = state with { ModelSelections = models, NextSessionChoices = sessions };
                    _ = ResolveConfiguration(selected, role);
                    configuration.ValidateContent(CredentialReferences(state), string.Join("\n", models.Values));
                    Save(selected, "PassSelectionChanged", "Role model and next-pass session selection recorded; active pass unchanged.");
                    break;
                case "interrupt":
                    await Interrupt(data, cancellationToken);
                    break;
                case "cleanup": return await Cleanup(data, command.Force, cancellationToken);
                case "continue":
                    if (!state.Attention.Any(a => a.Reason == AttentionReason.PassBudgetContinuation))
                        throw Error("budget_not_exhausted", "continue applies only to an exhausted pass budget.");
                    var project = configuration.ReadProject(state.Binding.Repository);
                    int? budget = WorkflowRules.Budget(command.MaxPasses ?? project.Defaults.MaxPasses ?? configuration.ReadGlobal().MaxPasses);
                    Save(state with { BatchLimit = budget, BatchStarted = 0,
                        Attention = state.Attention.Where(a => a.Reason != AttentionReason.PassBudgetContinuation).ToArray(), Recovery = null },
                        "PassBudgetGranted", budget is null ? "Explicit unlimited continuation granted." : $"Granted another batch of {budget} passes.");
                    break;
                case "resume":
                case "review":
                    await Resume(data, command, cancellationToken);
                    break;
                default: throw Error("unknown_command", "Unsupported workflow command.");
            }
            var current = Get(id).State;
            if (command.Action is "resume" or "continue" or "review") Kick(id);
            return new(id, current.CurrentOperationId, "Command durably accepted.", current);
        }
        finally { gate.Release(); }
    }

    private async Task<WorkflowAcceptance> Setup(string cwd, CancellationToken token)
    {
        var settings = configuration.ReadProject(cwd);
        var identity = await repository.InspectCanonicalAsync(cwd, token);
        var providers = await linear.ValidateProjectAsync(settings.LinearProjectId, settings.LinearTeamId, settings.InProgressStateId, settings.LinearCredentialReference, token);
        var forge = await github.ValidateRepositoryAsync(settings.GitHubRepositoryId, identity.RemoteUrl, settings.GitHubCredentialReference, token);
        configuration.ValidateContent([settings.LinearCredentialReference, settings.GitHubCredentialReference], providers.Project.Name + providers.Team.Name + providers.State.Name + forge.Name);
        await herdr.InspectTargetAsync(settings.HerdrTarget, token);
        var previous = store.FindProject(identity.Checkout);
        var project = new ProjectRegistration(previous?.Id ?? Guid.NewGuid(), identity.Checkout, identity.GitDirectory,
            configuration.ProjectFile(identity.Checkout), providers.Project, providers.Team, providers.State, forge,
            identity.RemoteUrl, settings.HerdrTarget, Now, identity.Stamp);
        store.RegisterProject(project);
        return new(null, null, $"Project {project.Id} registered. Resolved In Progress state: {providers.State.Id}. Save this ID in the Project Configuration.", null);
    }

    private async Task<WorkflowAcceptance> Start(WorkflowCommand command, CancellationToken token)
    {
        if (command.TaskIdentifier is null) throw Error("task_required", "start requires a Linear task identifier.");
        var settings = configuration.ReadProject(command.WorkingDirectory);
        var identity = await repository.InspectCanonicalAsync(command.WorkingDirectory, token);
        var project = store.FindProject(identity.Checkout) ?? throw Error("project_not_registered", "Run awctl setup from the canonical checkout first.");
        if (identity.GitDirectory != project.GitDirectory || identity.Stamp != project.RepositoryStamp) throw Error("repository_replaced", "The registered canonical repository was replaced.");
        if (settings.InProgressStateId is null) throw Error("in_progress_not_saved", "Save the resolved concrete In Progress state in Project Configuration after setup.");
        var task = await linear.ReadTaskAsync(command.TaskIdentifier, settings.LinearCredentialReference, token);
        if (task.StateType is "completed" or "canceled") throw Error("linear_terminal_task", "A Done or Canceled task must be deliberately reopened in Linear before start.");
        if (task.ProjectId != settings.LinearProjectId || task.TeamId != settings.LinearTeamId) throw Error("linear_task_outside_project", "The task does not belong to the configured Project and team.");
        await linear.ValidateProjectAsync(settings.LinearProjectId, settings.LinearTeamId, settings.InProgressStateId, settings.LinearCredentialReference, token);
        await github.ValidateRepositoryAsync(settings.GitHubRepositoryId, identity.RemoteUrl, settings.GitHubCredentialReference, token);
        await herdr.InspectTargetAsync(settings.HerdrTarget, token);
        var global = configuration.ReadGlobal();
        var snapshot = WorkflowRules.Capture(task, settings.Defaults.InstructionsPolicy ?? global.InstructionsPolicy ?? "warn", Now);
        configuration.ValidateContent([settings.LinearCredentialReference, settings.GitHubCredentialReference], snapshot.Title + "\n" + snapshot.Description);
        string branch = WorkflowRules.Branch(task.Identifier);
        Guid id = Guid.NewGuid();
        string path = Path.Combine(settings.ManagedRoot, id.ToString("N"));
        // Admission serializes only the final reference/reservation check, never agent execution.
        await admission.WaitAsync(token);
        try
        {
            var duplicate = store.FindDuplicate(project.Id, task.Id);
            if (duplicate is not null) throw new WorkflowException("duplicate_workflow", $"Task already has retained Workflow {duplicate.Id}, phase {duplicate.Phase}, workspace {duplicate.WorkspaceIntent.ManagedPath}.",
                $"Inspect awctl workflow {duplicate.Id}; use awctl resume {duplicate.Id} or explicit cleanup. start never reuses a workspace.");
            string baseline = await repository.AdmitAsync(identity, settings.BaseReference, branch, path, token);
            var workspaces = await herdr.InspectWorkspacesAsync(settings.HerdrTarget, identity.Checkout, token);
            if (workspaces.Workspaces.Any(w => w.Path == path || w.Branch == branch)) throw Error("workspace_collision", "A Herdr workspace already occupies the requested path or branch.");
            var binding = new IntegrationBinding(project.Id, identity.Checkout, identity.GitDirectory, identity.RemoteUrl, task.Id,
                settings.LinearProjectId, settings.LinearTeamId, settings.InProgressStateId, settings.GitHubRepositoryId,
                settings.HerdrTarget, settings.LinearCredentialReference, settings.GitHubCredentialReference, identity.Stamp);
            var intent = new WorkspaceIntent(id, identity.Checkout, identity.GitDirectory, baseline, branch, path, settings.HerdrTarget, workspaces.ExistingWorkspaceIds, Now, identity.Stamp);
            var operation = NewOperation("Startup", null);
            var state = new WorkflowState(id, Guid.NewGuid(), binding, intent, snapshot.Id, WorkflowPhase.WorkspacePreparation, null,
                operation.Id, false, false, WorkflowRules.Budget(command.MaxPasses ?? settings.Defaults.MaxPasses ?? global.MaxPasses), 0, 0,
                snapshot.Fingerprint, null, Observation(task), [], snapshot.HasAgentInstructions || (settings.Defaults.InstructionsPolicy ?? global.InstructionsPolicy) == "ignore" ? [] :
                    [new("missing_agent_instructions", "The complete captured description is authoritative because Agent instructions is absent.")],
                null, null, [], [], Now, 0);
            _ = ResolveConfiguration(state, AgentRole.Implementer); _ = ResolveConfiguration(state, AgentRole.Reviewer);
            store.Create(project, task.Id, new(state, [Event("WorkflowCreated", "Immutable requirements, integration binding, and workspace intent captured.", operation.Id)],
                [snapshot], [operation], [], [], []));
            Kick(id);
            return new(id, operation.Id, "Workflow durably created; preparation continues under control-plane ownership.", state);
        }
        finally { admission.Release(); }
    }

    public async Task<Guid> ResolveAsync(Guid? explicitId, string cwd, CancellationToken token = default)
    {
        var location = await repository.ResolveWorkflowAsync(cwd, token);
        Guid? embedded = location?.WorkflowId;
        if (embedded is not null && explicitId is not null && embedded != explicitId) throw Error("workflow_identity_conflict", "The supplied Workflow ID conflicts with this managed worktree.");
        Guid id = explicitId ?? embedded ?? throw Error("workflow_id_required", "Outside a proven managed worktree, supply the immutable Workflow ID.");
        var state = Get(id).State;
        if (embedded is not null)
        {
            if (state.Workspace is null) throw Error("workspace_unproven", "No proven workspace binding exists for this worktree identity.");
            if (location!.WorktreePath != state.Workspace.Path)
                throw Error("workspace_identity_conflict", "This checkout's metadata does not prove ownership of the retained Workflow workspace.");
            await repository.VerifyWorkspaceAsync(state.WorkspaceIntent, state.Workspace, token);
            await herdr.VerifyWorkspaceAsync(state.Workspace, token);
        }
        return id;
    }

    public WorkflowData Get(Guid id) => store.Find(id) ?? throw Error("workflow_not_found", "The immutable Workflow ID is not retained.");
    public WorkflowState[] List() => store.List();

    private void Kick(Guid id)
    {
        lock (runnerSync)
        {
            if (stopping.IsCancellationRequested) return;
            requestedRuns.Add(id);
            if (running.ContainsKey(id)) return;
            running[id] = Task.Run(async () =>
            {
                try
                {
                    while (!stopping.IsCancellationRequested)
                    {
                        lock (runnerSync) requestedRuns.Remove(id);
                        await Drive(id, false, stopping.Token);
                        lock (runnerSync)
                        {
                            if (requestedRuns.Contains(id)) continue;
                            running.TryRemove(id, out _);
                            return;
                        }
                    }
                }
                catch
                {
                    lock (runnerSync) running.TryRemove(id, out _);
                    throw;
                }
                lock (runnerSync) running.TryRemove(id, out _);
            });
        }
    }

    public async Task ReconcileStartupAsync(CancellationToken token = default)
    {
        foreach (var state in store.List())
        {
            var data = Get(state.Id);
            foreach (var result in data.Results) delivery.Retire(data.Passes.Single(p => p.Id == result.PassId));
            await Drive(state.Id, true, token);
        }
    }

    private async Task Drive(Guid id, bool reconciliationOnly, CancellationToken token)
    {
        var gate = gates.GetOrAdd(id, _ => new(1, 1));
        while (!token.IsCancellationRequested)
        {
            bool observeAgain = false;
            await gate.WaitAsync(token);
            try
            {
                var data = store.Find(id);
                if (data is null) return;
                bool historicalActive = false;
                foreach (var earlier in data.Operations.Where(o => o.Id != data.State.CurrentOperationId && o.PassId is not null && o.ExternalActionAttempted &&
                    o.Status is OperationStatus.Pending or OperationStatus.Running))
                {
                    if (data.Passes.Single(p => p.Id == earlier.PassId).Role != AgentRole.Reviewer) continue;
                    historicalActive |= await Observe(Get(id), earlier, token);
                }
                data = Get(id);
                var operation = Current(data);
                if (operation.Status is OperationStatus.Failed or OperationStatus.Interrupted or OperationStatus.Completed)
                {
                    if (!historicalActive) return;
                    observeAgain = true;
                }
                else if (operation.PassId is not null && !operation.ExternalActionAttempted)
                {
                    RetainUnattemptedLaunch(data, operation);
                    return;
                }
                else if (operation.PassId is not null)
                    observeAgain = await Observe(data, operation, token);
                else if (data.State.Abandoned || data.State.Attention.Length > 0) return;
                else if (reconciliationOnly)
                {
                    Save(data.State with { Recovery = Plan(data.State, "restart_continuation", "Restart preserved the pending intent. An explicit resume must revalidate it before another external action.") },
                        "RestartReconciled", "Pending action retained without replay.");
                    return;
                }
                else if (operation.Kind == "Startup") await Prepare(data, operation, token);
                else await Launch(data, operation, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (WorkflowException exception)
            {
                var data = store.Find(id);
                if (data is not null) StopWithDiagnostic(data, exception);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var data = store.Find(id);
                if (data is not null) StopWithDiagnostic(data, Error("workflow_action_unconfirmed", "The action could not establish its required facts; preserved evidence must be reconciled."));
                return;
            }
            finally { gate.Release(); }
            if (!observeAgain && store.Find(id)?.State.Recovery is not null) return;
            if (reconciliationOnly)
            {
                if (observeAgain) Kick(id);
                return;
            }
            if (observeAgain) await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private async Task Prepare(WorkflowData data, WorkflowOperation operation, CancellationToken token)
    {
        var state = data.State;
        await ValidateBinding(state, token);
        if (state.Workspace is null)
        {
            bool attempted = operation.ExternalActionAttempted || data.Operations.Any(o => o.Kind == "Startup" && o.ExternalActionAttempted);
            operation = operation with { ExternalActionAttempted = true };
            Save(state, "WorkspacePreparationRequested", "Durable reservation precedes Herdr workspace preparation.", [operation]);
            var workspace = await herdr.PrepareWorkspaceAsync(state.WorkspaceIntent, attempted, token);
            await repository.VerifyWorkspaceAsync(state.WorkspaceIntent, workspace, token);
            await repository.BindWorkflowAsync(workspace, state.Id, token);
            state = Get(state.Id).State;
            Save(state with { Workspace = workspace }, "WorkspacePrepared", "Exact Herdr and Git identities verified.");
        }
        state = Get(state.Id).State;
        if (!state.StartupLinearCompleted)
        {
            await linear.MoveToInProgressAsync(state.Binding, token);
            Save(state with { StartupLinearCompleted = true }, "LinearStartupCompleted", "Linear confirmed the configured In Progress state.");
        }
        state = Get(state.Id).State;
        var next = NewOperation("Handoff", operation.Id);
        Save(state with { Phase = WorkflowPhase.Implementation, CurrentOperationId = next.Id, Recovery = null },
            "ImplementationPending", "Startup completed; first Implementer handoff is pending.",
            [operation with { Status = OperationStatus.Completed, FinishedAt = Now }, next]);
    }

    private async Task Launch(WorkflowData data, WorkflowOperation operation, CancellationToken token)
    {
        var state = data.State;
        if (operation.ExternalActionAttempted) throw Error("session_preparation_uncertain", "Session preparation was attempted without a durable confirmed result. No second preparation or prompt was sent.");
        await ValidateBinding(state, token);
        if (!await CheckSource(data, token)) return;
        state = Get(state.Id).State;
        if (state.BatchLimit is int limit && state.BatchStarted >= limit)
        {
            AddAttention(state, new(AttentionReason.PassBudgetContinuation, state.TotalPasses.ToString(),
                "The managed pass batch is exhausted; the last pass completed normally.", $"awctl continue {state.Id}"));
            return;
        }
        AgentRole role = state.Phase == WorkflowPhase.Review ? AgentRole.Reviewer : AgentRole.Implementer;
        var effective = ResolveConfiguration(state, role);
        var snapshot = data.Snapshots.Single(s => s.Id == state.ActiveSnapshotId);
        var priorReview = data.Passes.Reverse().Where(p => p.Role == AgentRole.Reviewer && p.SnapshotId == snapshot.Id)
            .Select(p => data.ReviewReports.SingleOrDefault(r => r.PassId == p.Id && r.AuthorityValid)).FirstOrDefault(r => r is not null);
        var findings = data.Results.SingleOrDefault(r => r.PassId == priorReview?.PassId)?.Payload is ReviewResult review ? review.Findings : [];
        var implementation = data.Results.LastOrDefault(r => r.Role == AgentRole.Implementer);
        var checkpoint = role == AgentRole.Reviewer ? await repository.CaptureAsync(state.Workspace!, state.WorkspaceIntent.BaseCommit, token) : null;
        var guidance = await repository.ReadGuidanceAsync(state.Workspace!, token);
        var retained = effective.SessionPolicy == "reuse" ? data.Sessions.LastOrDefault(s => s.Role == role) : null;
        var predecessor = data.Operations.SingleOrDefault(o => o.Id == operation.PredecessorId);
        var preparedPass = predecessor is { PassId: not null, ExternalActionAttempted: false }
            ? data.Passes.Single(p => p.Id == predecessor.PassId) : null;
        var preparedSession = preparedPass is not null && preparedPass.Role == role &&
            !state.NextSessionChoices.ContainsKey(role) ? data.Sessions.Single(s => s.Id == preparedPass.SessionId) : null;
        if (preparedSession is not null && (preparedSession.Model != effective.Model || preparedSession.Reasoning != effective.Reasoning))
            throw Error("session_reuse_unavailable", "The prepared session configuration changed. Explicitly select a new session before resuming.");
        operation = operation with { ExternalActionAttempted = preparedSession is null && retained is null };
        Save(state, "SessionPreparationRequested", "Session preparation intent recorded before requesting owned execution resources.", [operation]);
        var session = preparedSession is not null ? new SessionPreparation(preparedSession, false)
            : await herdr.PrepareSessionAsync(state.Workspace!, role, effective, retained, token);
        state = Get(state.Id).State;
        Guid passId = Guid.NewGuid();
        string path = configuration.DeliveryPath(state.Id, session.Session.Id, operation.Id);
        string deliveryRoot = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
        if (session.NewSession) session = session with { Session = session.Session with { DeliveryRoot = deliveryRoot } };
        if (session.Session.DeliveryRoot != deliveryRoot)
            throw Error("session_delivery_mismatch", "The selected session does not authorize this delivery root. Select a new session explicitly.");
        if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(state.Workspace!.Path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw Error("unsafe_delivery_path", "Result delivery must be outside the worktree.");
        var handoff = delivery.Compose(state, snapshot, passId, operation.Id, role, effective, guidance, checkpoint, findings, implementation, path);
        configuration.ValidateContent(CredentialReferences(state), handoff.Text);
        var pass = new ManagedPass(passId, operation.Id, role, snapshot.Id, session.Session.Id, effective, handoff, path, checkpoint, findings, Now);
        var observed = await herdr.ObserveAsync(state.Workspace, session.Session, token);
        operation = operation with { PassId = pass.Id, ExternalActionAttempted = false, Observation = observed };
        Save(state, "HandoffRecorded", $"Complete {role} Handoff, session, and unique result delivery recorded.", [operation], passes: [pass], sessions: session.NewSession ? [session.Session] : []);
        await delivery.PrepareAsync(pass, token);
        state = Get(state.Id).State;
        operation = operation with { ExternalActionAttempted = true };
        var overrides = new Dictionary<AgentRole, string>(state.NextSessionChoices);
        overrides.Remove(role);
        Save(state with { BatchStarted = state.BatchStarted + 1, TotalPasses = state.TotalPasses + 1, NextSessionChoices = overrides },
            "PassLaunchRequested", $"{role} pass launch accepted; its prompt must never be resent ambiguously.", [operation]);
        await herdr.StartPassAsync(state.Workspace!, session.Session, pass, token);
        state = Get(state.Id).State;
        Save(state, "PassStarted", $"Herdr confirmed {role} prompt submission.", [operation with { Status = OperationStatus.Running }]);
    }

    private async Task<bool> Observe(WorkflowData data, WorkflowOperation operation, CancellationToken token)
    {
        var state = data.State;
        bool historical = operation.Id != state.CurrentOperationId;
        var pass = data.Passes.Single(p => p.Id == operation.PassId);
        var session = data.Sessions.Single(s => s.Id == pass.SessionId);
        ExecutionObservation observation;
        try { observation = await herdr.ObserveAsync(state.Workspace!, session, token); }
        catch (WorkflowException exception)
        {
            // Reachability changes never overwrite the last verified execution state or invent termination.
            var preserved = operation.Observation is null ? null : operation.Observation with { Reachable = false, Diagnostic = exception.Code };
            Save(historical ? state : state with { Recovery = Plan(state, exception.Code, exception.Message) }, "ExecutionUnreachable", "Last verified observation retained.",
                [operation with { Observation = preserved, Recovery = Plan(state, exception.Code, exception.Message) }]);
            return false;
        }
        if (session.NativeResumeReference is null && observation.NativeSessionReference is not null)
        {
            Save(state, "NativeSessionObserved", "Opaque native session reference captured from the exact owned execution.",
                sessions: [session with { NativeResumeReference = observation.NativeSessionReference }]);
            data = Get(state.Id); state = data.State;
        }
        ValidatedResult? result = null; WorkflowException? invalid = null;
        try
        {
            result = await delivery.ReadAsync(pass, token);
            if (result is not null) configuration.ValidateContent(CredentialReferences(state), WorkflowRules.ResultText(result.Payload));
        }
        catch (WorkflowException exception) when (exception.Code != "credential_unavailable") { result = null; invalid = exception; }
        bool complete = observation.State == ExecutionState.Completed || observation.State == ExecutionState.Idle &&
            (operation.Observation?.State == ExecutionState.Running ||
                observation.CompletionSequence is long sequence && sequence > (operation.Observation?.CompletionSequence ?? long.MaxValue));
        if (result is not null && observation.State is ExecutionState.Idle or ExecutionState.Completed or ExecutionState.Gone)
        {
            await Complete(data, operation, pass, result, observation, token);
            delivery.Retire(pass);
            return false;
        }
        if (complete)
        {
            invalid ??= Error("missing_result_envelope", "The correlated agent completed normally without its required Result Envelope.");
            Save(historical ? state : state with { Recovery = Plan(state, invalid.Code, invalid.Message) }, "PassResultRejected", invalid.Message,
                [operation with { Status = OperationStatus.Failed, FinishedAt = Now, Observation = observation, Recovery = Plan(state, invalid.Code, invalid.Message) }]);
            return false;
        }
        if (observation.State == ExecutionState.Gone)
        {
            Save(historical ? state : state with { Recovery = Plan(state, "execution_disappeared", "The exact execution is positively absent and no valid correlated result exists.") },
                "ExecutionDisappeared", "Operation interrupted after proven disappearance, with workspace and delivery preserved.",
                [operation with { Status = OperationStatus.Interrupted, FinishedAt = Now, Observation = observation }]);
            return false;
        }
        if (observation.State == ExecutionState.Running && operation.Status == OperationStatus.Pending)
        {
            Save(historical ? state : state with { Recovery = null }, "ExecutionReattached", "Proven active execution reattached without resending its prompt.",
                [operation with { Status = OperationStatus.Running, Observation = observation }]);
        }
        else if (operation.Observation?.State != observation.State || operation.Observation?.Reachable != true)
            Save(state, "ExecutionObserved", "Verified execution observation refreshed.", [operation with { Observation = observation }]);
        if (observation.State is ExecutionState.Unknown or ExecutionState.Idle && operation.Status == OperationStatus.Pending)
        {
            Save(historical ? Get(state.Id).State : Get(state.Id).State with { Recovery = Plan(state, "launch_ambiguous", "The prompt may have been delivered; no correlated completion or active execution proves its outcome.") },
                "LaunchAmbiguous", "No prompt was resent and no replacement pass was started.");
            return false;
        }
        return true;
    }

    private async Task Complete(WorkflowData data, WorkflowOperation operation, ManagedPass pass, ValidatedResult result,
        ExecutionObservation observation, CancellationToken token)
    {
        var state = data.State;
        if (operation.Id != state.CurrentOperationId)
        {
            var historicalReview = (ReviewResult)result.Payload;
            var invalidReport = new ReviewReport(pass.Id, pass.SnapshotId, pass.ReviewCheckpoint!.Target, historicalReview.Outcome, false,
                "Review authority was permanently revoked when another pass was selected before this Reviewer ended.", historicalReview.PublicMetadata);
            Save(state, "HistoricalReviewCompleted", "Earlier Reviewer result retained without current authority because another pass was selected before it ended.",
                [operation with { Status = OperationStatus.Completed, FinishedAt = Now, Observation = observation, Recovery = null }], results: [result], reports: [invalidReport]);
            return;
        }
        var warnings = state.Warnings.Concat(result.Payload.Report.Warnings).Concat(result.Payload.Report.Limitations).ToList();
        foreach (var check in result.Payload.Report.Checks.Where(c => c.Result != CheckResult.Passed))
            warnings.Add(new("validation_limitation", $"{check.Name}: {check.Result}. {check.Details}", check.ClassificationEvidence));
        var completed = operation with { Status = OperationStatus.Completed, FinishedAt = Now, Observation = observation, Recovery = null };
        WorkflowOperation? next = null;
        ReviewReport? report = null;
        if (result.Payload is ReviewResult evaluated)
        {
            RepositoryCheckpoint? current = null;
            string? captureFailure = null;
            try { current = await repository.CaptureAsync(state.Workspace!, state.WorkspaceIntent.BaseCommit, token); }
            catch (WorkflowException exception) { captureFailure = exception.Code + ": " + exception.Message; }
            bool valid = current is not null && pass.ReviewCheckpoint!.SameReviewState(current) && state.ActiveSnapshotId == pass.SnapshotId &&
                !data.Passes.Any(p => p.CreatedAt > pass.CreatedAt);
            report = new(pass.Id, pass.SnapshotId, pass.ReviewCheckpoint!.Target, evaluated.Outcome, valid,
                valid ? null : captureFailure ?? "HEAD, index, worktree, active snapshot, or subsequent pass changed during review.", evaluated.PublicMetadata);
            state = state with { Review = report };
        }
        if (result.Payload.Decision is Decision decision)
        {
            var attention = new Attention(AttentionReason.PassContinuationDecision, pass.Id.ToString(), decision.UnresolvedMatter + " " + decision.UnsafeContinuationRationale, decision.ProposedResolution);
            state = state with { Attention = [.. state.Attention, attention], Recovery = Plan(state, "pass_continuation_decision", attention.Explanation) };
        }
        else if (result.Payload is ImplementationResult)
        {
            next = NewOperation("Handoff", operation.Id);
            state = state with { Phase = WorkflowPhase.Review, CurrentOperationId = next.Id, Review = null, Recovery = null };
        }
        else if (result.Payload is ReviewResult review)
        {
            if (!report!.AuthorityValid)
                state = state with { Recovery = Plan(state, "review_invalidated", report.InvalidationReason!) };
            else if (review.Outcome == "Clean") state = state with { Phase = WorkflowPhase.Submission, Recovery = null };
            else
            {
                next = NewOperation("Handoff", operation.Id);
                state = state with { Phase = WorkflowPhase.Implementation, CurrentOperationId = next.Id, Recovery = null };
            }
        }
        Save(state with { Warnings = [.. warnings] }, "PassCompleted", result.Payload.Report.Summary,
            next is null ? [completed] : [completed, next], results: [result], reports: report is null ? [] : [report]);
    }

    private async Task<bool> CheckSource(WorkflowData data, CancellationToken token)
    {
        var state = data.State;
        var source = await linear.ReadTaskAsync(state.Binding.LinearTaskId, state.Binding.LinearCredentialReference, token);
        CheckTaskIdentity(state, source);
        var candidate = WorkflowRules.Capture(source, ResolveConfiguration(state, AgentRole.Implementer).InstructionsPolicy, Now);
        configuration.ValidateContent(CredentialReferences(state), candidate.Title + "\n" + candidate.Description);
        var attention = state.Attention.Where(a => a.Reason is not AttentionReason.TaskChangeDecision and not AttentionReason.LinearCancellationDecision).ToList();
        if (candidate.Fingerprint != state.HandledSourceFingerprint)
            attention.Add(new(AttentionReason.TaskChangeDecision, candidate.Fingerprint, "Authoritative Linear requirements changed before the next Handoff.", "Retain the active snapshot, adopt this exact revision, or stop."));
        if (source.StateType == "canceled" && state.AcceptedCancellationRevision != source.Revision)
            attention.Add(new(AttentionReason.LinearCancellationDecision, source.Revision, "Linear task is Canceled; the active pass may finish but another Handoff needs a decision.", "Explicitly continue the retained Workflow or abandon it; AW does not reopen Linear."));
        var warnings = state.Warnings;
        if (source.StateType == "completed" && !warnings.Any(w => w.Code == "linear_done")) warnings = [.. warnings, new("linear_done", "Linear is Done; AW continues without rewriting provider state.")];
        state = state with { LatestLinearObservation = Observation(source), Attention = [.. attention], Warnings = warnings };
        Save(state with { Recovery = attention.Count == 0 ? state.Recovery : Plan(state, "attention_required", string.Join(" ", attention.Select(a => a.Explanation))) },
            "LinearSourceObserved", "Task identity, requirements revision, and provider status rechecked at the Handoff boundary.");
        return attention.Count == 0;
    }

    private async Task Resume(WorkflowData data, WorkflowCommand command, CancellationToken token)
    {
        var state = data.State;
        await ValidateBinding(state, token);
        var current = Current(data);
        if (current.PassId is not null && !current.ExternalActionAttempted && current.Status == OperationStatus.Pending)
        {
            RetainUnattemptedLaunch(data, current);
            data = Get(state.Id); state = data.State; current = Current(data);
        }
        if (current.PassId is not null && current.ExternalActionAttempted && current.Status is OperationStatus.Pending or OperationStatus.Running)
        {
            await Observe(data, current, token);
            data = Get(state.Id); state = data.State; current = Current(data);
            if (current.Status is OperationStatus.Pending or OperationStatus.Running && current.PassId is not null)
            {
                var pass = data.Passes.Single(p => p.Id == current.PassId);
                if (pass.Role != AgentRole.Reviewer || command.Action != "review" || state.NextSessionChoices.GetValueOrDefault(AgentRole.Reviewer) != "new")
                {
                    if (current.Observation is { Reachable: true, State: ExecutionState.Running })
                        Save(state with { Abandoned = false }, "WorkflowReattached", "Explicit resume restored automatic routing for the proven running pass; no competing pass was launched.");
                    return;
                }
                Save(state, "ReviewAuthorityRevoked", "Explicit fresh review selected while an earlier Reviewer may still be active; the earlier pass permanently loses current authority.");
                data = Get(state.Id); state = data.State;
            }
        }
        await CheckSource(data, token);
        data = Get(state.Id); state = data.State;
        var requirements = state.Attention.ToList();
        TaskSnapshot[] snapshots = [];
        SourceRetention? sourceRetention = null;
        if (command.SourceDecision is not null)
        {
            WorkflowRules.ValidateChoice(command.SourceDecision, ["retain", "adopt", "stop"], "source decision");
            var changed = requirements.SingleOrDefault(a => a.Reason == AttentionReason.TaskChangeDecision) ?? throw Error("no_source_decision", "No source revision is awaiting a decision.");
            if (command.SourceDecision == "stop") { Save(state with { Abandoned = true }, "SourceDecisionStopped", "Workflow retained without another Handoff."); return; }
            var source = await linear.ReadTaskAsync(state.Binding.LinearTaskId, state.Binding.LinearCredentialReference, token);
            CheckTaskIdentity(state, source);
            var captured = WorkflowRules.Capture(source, ResolveConfiguration(state, AgentRole.Implementer).InstructionsPolicy, Now);
            configuration.ValidateContent(CredentialReferences(state), captured.Title + "\n" + captured.Description);
            if (captured.Fingerprint != changed.Revision) throw Error("source_changed_again", "Linear changed again while the decision was being applied.");
            state = state with { HandledSourceFingerprint = captured.Fingerprint };
            if (command.SourceDecision == "adopt")
            { snapshots = [captured]; state = state with { ActiveSnapshotId = captured.Id, Phase = WorkflowPhase.Implementation, Review = null }; }
            else sourceRetention = new(captured.SourceRevision, captured.Fingerprint, state.ActiveSnapshotId);
            requirements.Remove(changed);
        }
        if (command.CancellationDecision is not null)
        {
            WorkflowRules.ValidateChoice(command.CancellationDecision, ["continue", "stop"], "cancellation decision");
            var canceled = requirements.SingleOrDefault(a => a.Reason == AttentionReason.LinearCancellationDecision) ?? throw Error("no_cancellation_decision", "No cancellation is awaiting a decision.");
            if (command.CancellationDecision == "stop") { Save(state with { Abandoned = true }, "CancellationStopped", "Workflow abandoned; Linear unchanged.", snapshots: snapshots, sourceRetention: sourceRetention); return; }
            state = state with { AcceptedCancellationRevision = canceled.Revision };
            requirements.Remove(canceled);
        }
        if (command.DecisionResponse is not null)
        {
            if (string.IsNullOrWhiteSpace(command.DecisionResponse)) throw Error("empty_decision", "A concrete decision response is required.");
            configuration.ValidateContent(CredentialReferences(state), command.DecisionResponse);
            requirements.RemoveAll(a => a.Reason == AttentionReason.PassContinuationDecision);
            state = state with { Warnings = [.. state.Warnings, new("accepted_decision", command.DecisionResponse)] };
        }
        if (command.Action == "review") state = state with { Phase = WorkflowPhase.Review };
        state = state with { Attention = [.. requirements] };
        if (requirements.Count > 0)
        {
            Save(state, "ContinuationGated", "Applicable Attention Requirements remain; no continuation guessed.", snapshots: snapshots, sourceRetention: sourceRetention);
            return;
        }
        if (state.Phase == WorkflowPhase.Submission && state.Review?.AuthorityValid == true)
        { Save(state with { Abandoned = false }, "WorkflowRetained", "Reviewed work remains retained for the later submission slice.", snapshots: snapshots, sourceRetention: sourceRetention); return; }
        if (current.PassId is null && current.ExternalActionAttempted && current.Kind == "Handoff")
            throw Error("session_preparation_uncertain", "An earlier session preparation has no proven result; restore exact owned-resource evidence or use explicit cleanup.");
        var next = NewOperation(state.StartupLinearCompleted ? "Handoff" : "Startup", current.Id);
        Save(state with { Abandoned = false, Recovery = null, CurrentOperationId = next.Id }, "WorkflowResumed", "One revalidated continuation accepted as a new Operation.", [next], snapshots, sourceRetention: sourceRetention);
    }

    private void RetainUnattemptedLaunch(WorkflowData data, WorkflowOperation operation)
    {
        var recovery = Plan(data.State, "launch_not_attempted", "The Handoff was recorded, but launch was never attempted. Explicit resume revalidates the prepared resources before creating a successor Operation.");
        Save(data.State with { Recovery = recovery }, "UnattemptedLaunchRetained", "No external launch was attempted; no prompt replayed.",
            [operation with { Status = OperationStatus.Failed, FinishedAt = Now, Recovery = recovery }]);
    }

    private async Task Interrupt(WorkflowData data, CancellationToken token)
    {
        var operation = Current(data);
        if (operation.PassId is null || operation.Status is not (OperationStatus.Pending or OperationStatus.Running)) throw Error("no_active_pass", "No active managed pass can be interrupted.");
        await Observe(data, operation, token);
        data = Get(data.State.Id); operation = Current(data);
        if (operation.PassId is null || operation.Status is not (OperationStatus.Pending or OperationStatus.Running)) return;
        var pass = data.Passes.Single(p => p.Id == operation.PassId);
        await herdr.InterruptAsync(data.State.Workspace!, data.Sessions.Single(s => s.Id == pass.SessionId), token);
        var observed = await herdr.ObserveAsync(data.State.Workspace!, data.Sessions.Single(s => s.Id == pass.SessionId), token);
        if (observed.State is ExecutionState.Running or ExecutionState.Unknown) throw Error("interruption_unconfirmed", "Interruption was requested but the execution may still be active.");
        Save(data.State with { Recovery = Plan(data.State, "user_interrupted", "User-requested interruption confirmed; resume retains work and creates a new Operation.") },
            "PassInterrupted", "User-requested interruption confirmed.", [operation with { Status = OperationStatus.Interrupted, FinishedAt = Now, Observation = observed }]);
    }

    private async Task<WorkflowAcceptance> Cleanup(WorkflowData data, bool force, CancellationToken token)
    {
        var state = data.State;
        if (state.Workspace is null)
        {
            if (!force) throw Error("cleanup_workspace_unproven", "The workspace identity has not been proven. No external resource may be deleted by path alone.");
            var identity = await repository.InspectCanonicalAsync(state.Binding.Repository, token);
            if (identity.GitDirectory != state.Binding.GitDirectory || identity.Stamp != state.Binding.RepositoryStamp)
                throw Error("repository_replaced", "The bound repository was replaced; the deletion boundary cannot be verified.");
            // Removing known AW-owned records is safe. A similar external path/branch is never a deletion target.
            store.Delete(state.Id, state.Version);
            return new(state.Id, null, "cleanup_partial", null,
                [$"Removed AW-owned state. Herdr resources and branch were preserved because ownership is unproven. Target {state.WorkspaceIntent.HerdrTarget}; path {state.WorkspaceIntent.ManagedPath}; branch {state.WorkspaceIntent.Branch}. Inspect those exact resources manually; no Workflow retry remains."]);
        }
        var outcomes = new List<string>(); bool success = true;
        try { await repository.VerifyWorkspaceAsync(state.WorkspaceIntent, state.Workspace, token); }
        catch (WorkflowException exception) when (force && exception.Code == "worktree_absent")
        { success = false; outcomes.Add("The exact owned worktree is positively absent; remaining owned resources will be attempted independently."); }
        string? expectedTip;
        try
        {
            expectedTip = force ? await repository.ReadBranchTipAsync(state.WorkspaceIntent, token)
                : (await repository.CaptureAsync(state.Workspace, state.WorkspaceIntent.BaseCommit, token)).Head;
        }
        catch (WorkflowException exception) when (force && exception.Code == "branch_absent")
        {
            expectedTip = null; success = false;
            outcomes.Add("The exact local Workflow branch is positively absent; no branch deletion will be attempted.");
        }
        bool executionUnsafe = false;
        if (!force)
        {
            foreach (var session in data.Sessions)
            {
                try
                {
                    var observation = await herdr.ObserveAsync(state.Workspace, session, token);
                    executionUnsafe |= !observation.Reachable || observation.State is not (ExecutionState.Idle or ExecutionState.Completed or ExecutionState.Gone);
                }
                catch (WorkflowException) { executionUnsafe = true; }
            }
        }
        if (!force && (executionUnsafe || state.Attention.Length > 0 || Current(data).Status is OperationStatus.Pending or OperationStatus.Running ||
            data.Operations.Any(o => o.PassId is not null && o.ExternalActionAttempted && o.Status is OperationStatus.Pending or OperationStatus.Running) ||
            await repository.HasUniqueWorkAsync(state.Workspace, state.WorkspaceIntent.BaseCommit, state.Binding.RemoteUrl, token)))
        {
            Save(state with { Recovery = Plan(state, "cleanup_refused", "Ordinary cleanup would discard unique work or cannot prove execution and policy safety.") },
                "CleanupRefused", "Complete deletion boundary preflight refused; nothing deleted.");
            throw new WorkflowException("cleanup_refused", "Ordinary cleanup refused; the complete Workflow remains retained.", $"Inspect the retained work, or explicitly run awctl cleanup {state.Id} --force.");
        }
        try { await herdr.TeardownAsync(state.Workspace, data.Sessions, force, token); outcomes.Add("Herdr teardown confirmed."); }
        catch (WorkflowException exception)
        {
            if (!force)
            {
                Save(state with { Recovery = Plan(state, "cleanup_refused", "Herdr could not prove the complete deletion boundary safe: " + exception.Code) },
                    "CleanupRefused", "Herdr teardown unconfirmed; branch, delivery files, and Workflow retained.");
                throw;
            }
            success = false; outcomes.Add("Herdr teardown unconfirmed: " + exception.Code);
        }
        if (expectedTip is not null)
        {
            try { await repository.DeleteBranchAsync(state.WorkspaceIntent, expectedTip, token); outcomes.Add("Exact local branch deleted."); }
            catch (WorkflowException exception) { success = false; outcomes.Add("Local branch retained: " + exception.Code); }
        }
        try { delivery.Cleanup(data.Passes); outcomes.Add("Known result and Handoff transport files removed."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkflowException)
        { success = false; outcomes.Add("Some delivery transport files could not be removed; inspect the recorded Operation delivery locations."); }
        if (force || success) store.Delete(state.Id, state.Version);
        else Save(state with { Recovery = Plan(state, "cleanup_partial", "Ordinary cleanup did not confirm all components; Workflow retained.") }, "CleanupPartial", string.Join(" ", outcomes));
        return new(state.Id, null, success ? "cleanup_complete" : "cleanup_partial", null,
            [.. outcomes, $"Target {state.Workspace.Target}; workspace {state.Workspace.WorkspaceId}; path {state.Workspace.Path}; branch {state.Workspace.Branch}. No remote or Linear mutation."]);
    }

    private async Task ValidateBinding(WorkflowState state, CancellationToken token)
    {
        var identity = await repository.InspectCanonicalAsync(state.Binding.Repository, token);
        if (identity.GitDirectory != state.Binding.GitDirectory || identity.Stamp != state.Binding.RepositoryStamp) throw Error("repository_replaced", "The canonical repository identity no longer matches the Workflow binding.");
        var providers = await linear.ValidateProjectAsync(state.Binding.LinearProjectId, state.Binding.LinearTeamId, state.Binding.InProgressStateId, state.Binding.LinearCredentialReference, token);
        var forge = await github.ValidateRepositoryAsync(state.Binding.GitHubRepositoryId, identity.RemoteUrl, state.Binding.GitHubCredentialReference, token);
        var project = store.FindProject(state.Binding.Repository);
        if (project is not null && (project.LinearProject != providers.Project || project.LinearTeam != providers.Team || project.InProgressState != providers.State || project.GitHubRepository != forge))
            store.RegisterProject(project with { LinearProject = providers.Project, LinearTeam = providers.Team, InProgressState = providers.State, GitHubRepository = forge, ValidatedAt = Now });
        await herdr.InspectTargetAsync(state.Binding.HerdrTarget, token);
        if (state.Workspace is not null)
        {
            await repository.VerifyWorkspaceAsync(state.WorkspaceIntent, state.Workspace, token);
            await herdr.VerifyWorkspaceAsync(state.Workspace, token);
        }
    }

    private PassConfiguration ResolveConfiguration(WorkflowState state, AgentRole role) => WorkflowRules.Resolve(role, new(), state,
        configuration.ReadProject(state.Binding.Repository).Defaults, configuration.ReadGlobal());
    private static void CheckTaskIdentity(WorkflowState state, LinearTask task)
    {
        if (task.Id != state.Binding.LinearTaskId || task.ProjectId != state.Binding.LinearProjectId || task.TeamId != state.Binding.LinearTeamId)
            throw Error("linear_identity_mismatch", "The bound Linear Task or its Project/team relationship is missing or replaced.");
    }
    private void StopWithDiagnostic(WorkflowData data, WorkflowException exception)
    {
        var operation = Current(data);
        var plan = Plan(data.State, exception.Code, exception.Message);
        bool uncertain = operation.ExternalActionAttempted && operation.PassId is not null;
        var updated = operation.Status is OperationStatus.Pending or OperationStatus.Running
            ? new[] { operation with { Status = uncertain ? operation.Status : OperationStatus.Failed,
                FinishedAt = uncertain ? null : Now, Recovery = plan } } : [];
        Save(data.State with { Recovery = plan }, uncertain ? "ExecutionUncertain" : "OperationFailed", exception.Message, updated);
    }
    private void AddAttention(WorkflowState state, Attention attention)
    {
        state = state with { Attention = [.. state.Attention.Where(a => a.Reason != attention.Reason), attention] };
        Save(state with { Recovery = Plan(state, "attention_required", attention.Explanation) }, "AttentionRequired", attention.Explanation);
    }
    private void Save(WorkflowState state, string kind, string summary, WorkflowOperation[]? operations = null,
        TaskSnapshot[]? snapshots = null, ManagedPass[]? passes = null, AgentSession[]? sessions = null, ValidatedResult[]? results = null,
        ReviewReport[]? reports = null, SourceRetention? sourceRetention = null)
    {
        var updated = state with { Version = state.Version + 1, LastActivity = Now };
        var entry = Event(kind, summary, operations?.FirstOrDefault()?.Id ?? state.CurrentOperationId,
            passes?.FirstOrDefault()?.Id ?? operations?.FirstOrDefault()?.PassId);
        WorkflowEvent[] events = sourceRetention is null ? [entry] : [entry,
            Event("TaskSnapshotRetained", "Explicitly retained the active snapshot after examining changed source requirements.", entry.OperationId) with { SourceRetention = sourceRetention }];
        store.Save(state.Version, new(updated, events, snapshots ?? [], operations ?? [], passes ?? [], sessions ?? [], results ?? [], reports));
    }
    private static WorkflowOperation Current(WorkflowData data) => data.Operations.Single(o => o.Id == data.State.CurrentOperationId);
    private static WorkflowOperation NewOperation(string kind, Guid? predecessor) => new(Guid.NewGuid(), predecessor, kind, OperationStatus.Pending, null, false, null, null, Now, null);
    private static WorkflowEvent Event(string kind, string summary, Guid? operation = null, Guid? pass = null) => new(0, kind, summary, operation, pass, Now);
    private static LinearObservation Observation(LinearTask task) => new(task.Revision, task.StateId, task.StateType, Now);
    private static string[] CredentialReferences(WorkflowState state) => [state.Binding.LinearCredentialReference, state.Binding.GitHubCredentialReference];
    private static RecoveryPlan Plan(WorkflowState state, string code, string message)
    {
        var commands = new List<string>();
        foreach (var attention in state.Attention)
            switch (attention.Reason)
            {
                case AttentionReason.TaskChangeDecision: commands.AddRange([$"awctl resume {state.Id} --source retain", $"awctl resume {state.Id} --source adopt", $"awctl resume {state.Id} --source stop"]); break;
                case AttentionReason.PassBudgetContinuation: commands.Add($"awctl continue {state.Id}"); break;
                case AttentionReason.LinearCancellationDecision: commands.AddRange([$"awctl resume {state.Id} --cancellation continue", $"awctl resume {state.Id} --cancellation stop"]); break;
                case AttentionReason.PassContinuationDecision: commands.Add($"awctl resume {state.Id} --decision 'ACCEPTED RESOLUTION'"); break;
            }
        if (code == "session_reuse_unavailable") commands.Add($"awctl select {state.Id} --role {(state.Phase == WorkflowPhase.Review ? "reviewer" : "implementer")} --session new");
        if (code == "pass_continuation_decision" && commands.Count == 0) commands.Add($"awctl resume {state.Id} --decision 'ACCEPTED RESOLUTION'");
        if (code == "review_invalidated") commands.Add($"awctl review {state.Id}");
        if (commands.Count == 0 || code == "session_reuse_unavailable") commands.Add($"awctl resume {state.Id}");
        commands.Add($"awctl cleanup {state.Id} --force");
        return new(code, message, [.. commands], "Workspace, sessions, snapshots, pass reports, delivery evidence, and audit history are retained.");
    }
    private static WorkflowException Error(string code, string message) => new(code, message, "Inspect the Workflow projection and use the explicit continuation choices in its Recovery Plan.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await stopping.CancelAsync();
        try { await Task.WhenAll(running.Values); } catch (OperationCanceledException) { }
        stopping.Dispose();
    }
}
