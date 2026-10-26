using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Application;

public sealed record WorkflowQuery(string WorkingDirectory, Guid? WorkflowId = null, string View = "workflow",
    long After = 0, Guid? PassId = null, int Offset = 0);
public sealed record WorkflowSummary(Guid Id, string TaskIdentifier, Guid SnapshotId, WorkflowPhase Phase,
    Guid OperationId, OperationStatus OperationStatus, ExecutionObservation? Execution, Guid? PassId,
    string Branch, string Worktree, bool Abandoned, string[] Attention, string[] Warnings, int TotalPasses,
    int BatchStarted, string BatchRemaining, ReviewTarget? ReviewTarget, bool ReviewValid,
    string SubmissionReadiness, LinearObservation Linear, DateTimeOffset LastActivity, RecoveryPlan? Recovery,
    string Measurements, SubmissionProjection? Submission = null);
public sealed record SubmissionProjection(Guid IntentId, string Stage, string ExpectedCommit, string? Commit,
    string? PushedRevision, PullRequestIdentity? PullRequest, bool Forced, Guid? ReviewPassId);
public sealed record ReportPage(string Text, int Offset, int? NextOffset);
public sealed record WorkflowQueryReply(WorkflowSummary[] Workflows, WorkflowEvent[] History,
    long? NextPosition, ReportPage? Report);

public sealed class WorkflowQueries(WorkflowApplication application, IWorkflowRepository repository)
{
    public async Task<WorkflowQueryReply> ExecuteAsync(WorkflowQuery query, CancellationToken token = default)
    {
        if (query.Offset < 0 || query.After < 0) throw new WorkflowException("invalid_page", "Page positions must be nonnegative.", "Use the returned continuation position.");
        if (query.View == "list")
        {
            var states = application.List().OrderBy(s => s.Id).Skip(query.Offset).Take(1).ToArray();
            var projections = new List<WorkflowSummary>();
            foreach (var state in states) projections.Add(await Summary(state.Id, token));
            return new([.. projections], [], null, new("Use offset to page the retained workflow list.", query.Offset, states.Length == 1 ? query.Offset + 1 : null));
        }
        Guid id = await application.ResolveAsync(query.WorkflowId, query.WorkingDirectory, token);
        var data = application.Get(id);
        if (query.View == "workflow") return new([await Summary(id, token)], [], null, null);
        if (query.View == "history")
        {
            var events = data.Events.Where(e => e.Position > query.After).Take(10).Select(e => e with
            {
                Summary = Bound(e.Summary, 100),
                SourceRetention = e.SourceRetention is null ? null : e.SourceRetention with { SourceRevision = Bound(e.SourceRetention.SourceRevision, 100) }
            }).ToArray();
            return new([], events, events.Length == 10 ? events[^1].Position : null, null);
        }
        string text = query.View switch
        {
            "snapshot" => data.Snapshots.Single(s => s.Id == data.State.ActiveSnapshotId).Description,
            "warnings" => string.Join("\n\n", data.State.Warnings.Select(w => $"{w.Code}: {w.Message}\n{w.Evidence}")),
            "attention" => string.Join("\n\n", data.State.Attention.Select(a => $"{a.Reason}: {a.Explanation}\n{a.ProposedResolution}")),
            "handoff" => data.Passes.SingleOrDefault(p => p.Id == query.PassId)?.Handoff.Text ?? throw MissingPass(),
            "report" => query.PassId is null && (data.State.Submission is not null || data.Operations.Single(o => o.Id == data.State.CurrentOperationId).Kind == "Submission") ? SubmissionReport(data) : Report(data, query.PassId),
            _ => throw new WorkflowException("unknown_query", "Unsupported workflow query.", "Use workflow, history, snapshot, handoff, report, or list.")
        };
        int offset = Math.Min(query.Offset, text.Length);
        // A 1,000 UTF-16-character chunk fits the transport even with JSON escaping.
        int length = Math.Min(1000, text.Length - offset);
        if (length > 0 && char.IsHighSurrogate(text[offset + length - 1])) length--;
        return new([], [], null, new(text.Substring(offset, length), offset, offset + length < text.Length ? offset + length : null));
    }

    public async Task<WorkflowSummary> Summary(Guid id, CancellationToken token = default)
    {
        var data = application.Get(id); var state = data.State;
        var operation = data.Operations.Single(o => o.Id == state.CurrentOperationId);
        bool valid = false;
        string readiness = "unavailable: no current Clean Review Report";
        if (state.Review is { AuthorityValid: true, Outcome: "Clean" } report && state.Workspace is not null)
        {
            try
            {
                var checkpoint = await repository.CaptureAsync(state.Workspace, state.WorkspaceIntent.BaseCommit, token);
                var accepted = data.Passes.SingleOrDefault(pass => pass.Id == report.PassId)?.ReviewCheckpoint;
                // After Clean, HEAD and staging may change while the reviewed materialized content stays identical.
                valid = accepted?.WorktreeIdentity is not null && checkpoint.WorktreeIdentity == accepted.WorktreeIdentity &&
                    checkpoint.Target == report.Target && accepted.Target == report.Target && report.SnapshotId == state.ActiveSnapshotId;
                readiness = valid && state.Phase == WorkflowPhase.Submission && !state.Abandoned && state.Attention.Length == 0 && operation.Status == OperationStatus.Completed
                    ? "eligible" : "ineligible: review drift, abandonment, attention, or active operation";
            }
            catch (WorkflowException) { readiness = "unavailable: repository evidence could not be verified"; }
        }
        var recovery = state.Recovery is null ? null : state.Recovery with
        { Explanation = Bound(state.Recovery.Explanation, 200), PreservedEvidence = Bound(state.Recovery.PreservedEvidence, 100), Commands = state.Recovery.Commands.Take(6).ToArray() };
        if (state.Submission is { } retained)
            readiness = retained.PullRequest is not null ? "submitted: retained first PR; no follow-up batch" : "frozen submission: inspect operation and rerun awctl pr to continue";
        return new(id, data.Snapshots.Single(s => s.Id == state.ActiveSnapshotId).Identifier, state.ActiveSnapshotId,
            state.Phase, operation.Id, operation.Status, operation.Observation is null ? null : operation.Observation with { NativeSessionReference = null, Diagnostic = operation.Observation.Diagnostic is null ? null : Bound(operation.Observation.Diagnostic, 100) }, operation.PassId,
            state.WorkspaceIntent.Branch, Bound(state.WorkspaceIntent.ManagedPath, 256), state.Abandoned,
            state.Attention.Select(a => a.Reason + ": " + Bound(a.Explanation, 80)).Take(5).ToArray(),
            state.Warnings.TakeLast(3).Select(w => Bound(w.Message, 120)).ToArray(), state.TotalPasses, state.BatchStarted,
            state.BatchLimit is int limit ? Math.Max(0, limit - state.BatchStarted).ToString() : "unlimited", state.Review?.Target, valid,
            readiness, state.LatestLinearObservation with { Revision = Bound(state.LatestLinearObservation.Revision, 100),
                StateId = Bound(state.LatestLinearObservation.StateId, 100), StateType = Bound(state.LatestLinearObservation.StateType, 32) }, state.LastActivity, recovery,
            "Token and cost measurements: unavailable. Recorded operation creation and completion times are available in history.",
            state.Submission is { } submission ? new(submission.Intent.Id,
                submission.PullRequest is not null ? "submitted" : submission.ForgeWriteAttempted ? "PR write attempted; reconcile" : submission.PushedRevision is not null ? "pushed" : submission.Commit is not null ? "committed" : "intent frozen",
                submission.Intent.Commit, submission.Commit, submission.PushedRevision, submission.PullRequest,
                submission.Intent.Forced, submission.Intent.ReviewPassId) : null);
    }
    private static string SubmissionReport(WorkflowData data)
    {
        var operation = data.Operations.Single(o => o.Id == data.State.CurrentOperationId);
        string outcome = $"Operation {operation.Id}; kind {operation.Kind}; status {operation.Status}; predecessor {operation.PredecessorId}.\n" +
            $"Recovery: {data.State.Recovery?.Explanation ?? "none"}\n" + string.Join('\n', data.State.Recovery?.Commands ?? []) + "\n\n";
        if (data.State.Submission is null) return outcome + "No PR Intent was authorized. Worktree, passes and evidence remain retained.";
        var submission = data.State.Submission!; var intent = submission.Intent;
        return outcome + $"Submission intent {intent.Id}; Workflow {intent.WorkflowId}; snapshot {intent.SnapshotId}.\n" +
            $"Tree {intent.Target.ProposedTree}; base {intent.BaseCommit}; branch {intent.Branch}; base branch {intent.BaseBranch}.\n" +
            $"Original HEAD {intent.OriginalHead}; expected commit {intent.Commit}; verified commit {submission.Commit ?? "unconfirmed"}; pushed revision {submission.PushedRevision ?? "unconfirmed"}.\n" +
            $"PR {submission.PullRequest?.Number}: {submission.PullRequest?.Url ?? "unconfirmed"}.\n" +
            $"Force: {intent.Forced}; previous phase {intent.PreviousPhase}; {intent.BypassReason}\n" +
            $"Recovery: {data.State.Recovery?.Explanation ?? "none"}\n\nCommit message:\n{intent.CommitMessage}\nPR body:\n{intent.Body}";
    }
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "… (see full report)";
    private static string Report(WorkflowData data, Guid? passId)
    {
        var pass = data.Passes.SingleOrDefault(p => p.Id == passId) ?? throw MissingPass();
        var operation = data.Operations.Single(o => o.Id == pass.OperationId);
        var review = data.ReviewReports.SingleOrDefault(r => r.PassId == passId);
        string authority = review is null ? "No completed Review Report." :
            $"Review authority: {(review.AuthorityValid ? "valid at completion" : "invalid")}. {review.InvalidationReason}\n" +
            $"Review Target: {review.Target.ObjectFormat}; baseline {review.Target.BaselineTree}; proposed {review.Target.ProposedTree}.";
        return $"Pass {pass.Id}; Operation {operation.Id}; role {pass.Role}; status {operation.Status}.\n" +
            $"Snapshot {pass.SnapshotId}; session {pass.SessionId}.\nConfiguration: {pass.Configuration}.\n" +
            $"Operation created: {operation.CreatedAt:O}; completed: {operation.FinishedAt?.ToString("O") ?? "unavailable"}.\n" +
            "Token and cost measurements: unavailable.\n" + authority + "\n" +
            $"Recovery: {operation.Recovery?.Explanation ?? "none"}\n\nComplete Result Envelope:\n" +
            (data.Results.SingleOrDefault(r => r.PassId == passId)?.CompleteJson ?? "Unavailable; retained operation and delivery diagnostics describe the current evidence.");
    }
    private static WorkflowException MissingPass() => new("pass_not_found", "The selected pass or its result is not retained.", "Select a pass ID from workflow history.");
}
