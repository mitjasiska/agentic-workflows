using System.Text;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class WorkflowVerticalTests
{
    [Fact]
    public async Task Missing_credential_reference_preserves_completed_delivery_until_ingestion_can_be_verified()
    {
        await using var fixture = new WorkflowFixture();
        Guid id = await fixture.Start(); var active = await fixture.Pass(id, 1);
        fixture.Providers.CredentialUnavailable = true;
        await fixture.Deliver(id, WorkflowFixture.Ready());
        var waiting = await fixture.Until(id, d => d.State.Recovery?.Code == "credential_unavailable");
        Assert.Empty(waiting.Results);
        Assert.Equal(OperationStatus.Running, waiting.Operations.Last().Status);
        Assert.True(File.Exists(active.Passes[0].DeliveryPath));
        fixture.Providers.CredentialUnavailable = false;
        await fixture.Application.ExecuteAsync(new("resume", fixture.Root, id));
        var reviewed = await fixture.Pass(id, 2);
        Assert.Single(reviewed.Results);
        Assert.Equal(OperationStatus.Completed, reviewed.Operations.Single(o => o.Id == active.Passes[0].OperationId).Status);
        Assert.False(File.Exists(active.Passes[0].DeliveryPath));
    }

    [Fact]
    public async Task Copied_metadata_cannot_resolve_another_checkout_as_the_managed_worktree()
    {
        await using var fixture = new WorkflowFixture();
        Guid id = await fixture.Start();
        var active = await fixture.Pass(id, 1);
        Assert.Equal(id, await fixture.Application.ResolveAsync(null, active.State.Workspace!.Path));
        fixture.Providers.CopiedLocation = new(id, Path.Combine(fixture.Root, "copied-checkout"));
        var error = await Assert.ThrowsAsync<WorkflowException>(() => fixture.Application.ExecuteAsync(new("abandon", fixture.Root, id)));
        Assert.Equal("workspace_identity_conflict", error.Code);
        Assert.False(fixture.Application.Get(id).State.Abandoned);
    }

    [Fact]
    public async Task Clean_first_review_is_durable_independent_and_readiness_is_derived()
    {
        await using var f = new WorkflowFixture();
        Guid id = await f.Start();
        var first = await f.Pass(id, 1);
        Assert.Equal(new[] { "workspace", "linear", "launch" }, f.Providers.Calls.Take(3));
        Assert.Equal(AgentRole.Implementer, first.Passes[0].Role);
        await f.Deliver(id, WorkflowFixture.Ready());
        var review = await f.Pass(id, 2);
        Assert.Equal(AgentRole.Reviewer, review.Passes[1].Role);
        Assert.NotEqual(review.Passes[0].SessionId, review.Passes[1].SessionId);
        Assert.Equal(review.Passes[0].SnapshotId, review.Passes[1].SnapshotId);
        Assert.Contains("AGENTS.md", review.Passes[1].Handoff.Text);
        Assert.Contains("Repository Guidance revision: head", review.Passes[1].Handoff.Text);
        Assert.Contains("Product Invariants", review.Passes[1].Handoff.Text);
        await f.Deliver(id, WorkflowFixture.Clean());
        var clean = await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission);
        Assert.Equal(2, clean.Results.Length);
        Assert.True(clean.State.Review!.AuthorityValid);
        Assert.Equal(Enumerable.Range(1, clean.Events.Length).Select(n => (long)n), clean.Events.Select(e => e.Position));
        var queries = new WorkflowQueries(f.Application, f.Providers);
        Assert.Equal("eligible", (await queries.Summary(id)).SubmissionReadiness);
        f.Providers.Drift = true;
        Assert.False((await queries.Summary(id)).ReviewValid);
        Assert.True(f.Application.Get(id).State.Review!.AuthorityValid); // Query does not rewrite history.
        await f.Restart();
        Assert.Equal(2, f.Application.Get(id).Results.Length);
        Assert.Equal(2, f.Providers.Launches);
    }

    [Fact]
    public async Task Findings_route_to_original_implementer_and_retained_reviewer()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start();
        await f.Pass(id, 1); await f.Deliver(id, WorkflowFixture.Ready());
        await f.Pass(id, 2);
        await f.Deliver(id, new ReviewResult("ChangesRequired", WorkflowFixture.Report(), [WorkflowFixture.Finding()], null, null));
        var repair = await f.Pass(id, 3);
        Assert.Equal(repair.Passes[0].SessionId, repair.Passes[2].SessionId);
        Assert.Single(repair.Passes[2].SuppliedFindings);
        await f.Deliver(id, WorkflowFixture.Ready([new("F1", "Corrected routing with regression coverage")]));
        var rereview = await f.Pass(id, 4);
        Assert.Equal(rereview.Passes[1].SessionId, rereview.Passes[3].SessionId);
        Assert.Equal(4, rereview.Passes.Select(p => p.DeliveryPath).Distinct().Count());
        Assert.All(rereview.Passes, pass => Assert.StartsWith(
            rereview.Sessions.Single(session => session.Id == pass.SessionId).DeliveryRoot + Path.DirectorySeparatorChar, pass.DeliveryPath));
        Assert.NotEqual(rereview.Sessions[0].DeliveryRoot, rereview.Sessions[1].DeliveryRoot);
        await f.Deliver(id, WorkflowFixture.Clean());
        await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission);
        Assert.Equal(4, f.Providers.Launches);
    }

    [Fact]
    public async Task DecisionRequired_is_completed_operation_with_attention_not_failure()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Deliver(id, new ImplementationResult("DecisionRequired", WorkflowFixture.Report(), [], [], new("Ownership undecided", "Changing ownership would violate an invariant", "Accept the focused ownership decision")));
        var data = await f.Until(id, d => d.State.Attention.Any(a => a.Reason == AttentionReason.PassContinuationDecision));
        Assert.Equal(OperationStatus.Completed, data.Operations.Last().Status);
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        Assert.Equal(1, f.Providers.Launches);
        await f.Application.ExecuteAsync(new("resume", f.Root, id, DecisionResponse: "Preserve the existing ownership boundary."));
        var continued = await f.Pass(id, 2);
        Assert.Contains("Preserve the existing ownership", continued.Passes[1].Handoff.Text);
    }

    [Fact]
    public async Task Pass_budget_pauses_between_passes_and_continue_does_not_change_saved_configuration()
    {
        await using var f = new WorkflowFixture(); f.Providers.Settings = new(MaxPasses: "1");
        Guid id = await f.Start(); await f.Pass(id, 1); await f.Deliver(id, WorkflowFixture.Ready());
        await f.Until(id, d => d.State.Attention.Any(a => a.Reason == AttentionReason.PassBudgetContinuation));
        Assert.Equal(1, f.Providers.Launches);
        await f.Application.ExecuteAsync(new("continue", f.Root, id, MaxPasses: "unlimited"));
        var data = await f.Pass(id, 2);
        Assert.Null(data.State.BatchLimit); Assert.Equal("1", f.Providers.Settings.MaxPasses);
    }

    [Theory]
    [InlineData("retain", 1, AgentRole.Reviewer)]
    [InlineData("adopt", 2, AgentRole.Implementer)]
    public async Task Source_decision_is_exact_and_snapshots_are_immutable(string choice, int snapshots, AgentRole nextRole)
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); var original = await f.Pass(id, 1);
        f.Providers.Source = f.Providers.Source with { Description = "# Agent instructions\nChanged requirements\n", Revision = "r2" };
        await f.Deliver(id, WorkflowFixture.Ready());
        await f.Until(id, d => d.State.Attention.Any(a => a.Reason == AttentionReason.TaskChangeDecision));
        await f.Application.ExecuteAsync(new("resume", f.Root, id, SourceDecision: choice));
        var data = await f.Pass(id, 2);
        Assert.Equal(snapshots, data.Snapshots.Length); Assert.Equal(nextRole, data.Passes[1].Role);
        Assert.Equal(original.Snapshots[0], data.Snapshots[0]);
        Assert.Empty(data.State.Attention);
    }

    [Fact]
    public async Task Linear_cancellation_preserves_active_result_and_gates_next_handoff()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        f.Providers.Source = f.Providers.Source with { StateType = "canceled", StateId = "canceled", Revision = "canceled-r1" };
        await f.Deliver(id, WorkflowFixture.Ready());
        var data = await f.Until(id, d => d.State.Attention.Any(a => a.Reason == AttentionReason.LinearCancellationDecision));
        Assert.Single(data.Results); Assert.Equal(1, f.Providers.Launches);
        await f.Application.ExecuteAsync(new("resume", f.Root, id, CancellationDecision: "continue"));
        await f.Pass(id, 2);
        Assert.Equal("canceled", f.Providers.Source.StateType);
        Assert.Single(f.Providers.Calls, c => c == "linear");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_and_oversized_delivery_fail_only_confirmed_completed_operation(bool oversized)
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); var data = await f.Pass(id, 1);
        await WorkflowFixture.Atomic(data.Passes[0].DeliveryPath, oversized ? new byte[1_048_577] : Encoding.UTF8.GetBytes("{bad}"));
        f.Providers.Execution = ExecutionState.Completed;
        var failed = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Failed);
        Assert.Empty(failed.Results); Assert.True(File.Exists(data.Passes[0].DeliveryPath));
        Assert.Equal(WorkflowPhase.Implementation, failed.State.Phase);
        Assert.Equal("invalid_result_envelope", failed.State.Recovery!.Code);
    }

    [Fact]
    public async Task Duplicate_admission_and_terminal_task_admission_launch_nothing_new()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        var duplicate = await Assert.ThrowsAsync<WorkflowException>(() => f.Application.ExecuteAsync(new("start", f.Root, TaskIdentifier: "DEV-123")));
        Assert.Equal("duplicate_workflow", duplicate.Code); Assert.Contains(id.ToString(), duplicate.Message);
        Assert.Equal(1, f.Providers.Creations);
        f.Providers.Source = f.Providers.Source with { StateType = "completed" };
        var terminal = await Assert.ThrowsAsync<WorkflowException>(() => f.Application.ExecuteAsync(new("start", f.Root, TaskIdentifier: "DEV-123")));
        Assert.Equal("linear_terminal_task", terminal.Code); Assert.Equal(1, f.Providers.Launches);
    }

    [Fact]
    public async Task Uncertain_launch_is_not_resent_after_resume_or_restart()
    {
        await using var f = new WorkflowFixture(); f.Providers.LoseLaunchResponse = true;
        Guid id = await f.Start(); await f.Until(id, d => d.State.Recovery is not null);
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        await f.Restart();
        Assert.Equal(1, f.Providers.Launches);
        Assert.NotEqual(OperationStatus.Interrupted, f.Application.Get(id).Operations.Last().Status);
    }

    [Fact]
    public async Task Restart_ingests_delivery_once_without_relaunching()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); var data = await f.Pass(id, 1);
        await f.Application.DisposeAsync();
        await f.Deliver(id, WorkflowFixture.Ready());
        f.Application = f.CreateApplication();
        await f.Application.ReconcileStartupAsync();
        var once = f.Application.Get(id);
        Assert.Single(once.Results); Assert.Equal(1, f.Providers.Launches);
        Assert.False(File.Exists(data.Passes[0].DeliveryPath));
        await f.Restart();
        Assert.Single(f.Application.Get(id).Events, e => e.Kind == "PassCompleted");
    }

    [Fact]
    public async Task Drift_during_review_retains_report_but_invalidates_authority()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Deliver(id, WorkflowFixture.Ready()); await f.Pass(id, 2);
        f.Providers.Drift = true; await f.Deliver(id, WorkflowFixture.Clean());
        var data = await f.Until(id, d => d.State.Review is not null);
        Assert.False(data.State.Review!.AuthorityValid); Assert.Equal(WorkflowPhase.Review, data.State.Phase);
        Assert.Equal(2, data.Results.Length);
        Guid invalidPass = data.Passes[1].Id;
        await f.Application.ExecuteAsync(new("review", f.Root, id));
        await f.Pass(id, 3); await f.Deliver(id, WorkflowFixture.Clean());
        var clean = await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission);
        Assert.Equal(2, clean.ReviewReports.Length);
        Assert.False(clean.ReviewReports.Single(r => r.PassId == invalidPass).AuthorityValid);
        var page = await new WorkflowQueries(f.Application, f.Providers).ExecuteAsync(new(f.Root, id, "report", PassId: invalidPass));
        Assert.Contains("Review authority: invalid", page.Report!.Text);
        await f.Restart();
        Assert.False(f.Application.Get(id).ReviewReports.Single(r => r.PassId == invalidPass).AuthorityValid);
    }

    [Fact]
    public async Task Abandonment_retains_result_and_ordinary_cleanup_refuses_unique_work()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Application.ExecuteAsync(new("abandon", f.Root, id));
        await f.Deliver(id, WorkflowFixture.Ready());
        await f.Until(id, d => d.Results.Length == 1);
        Assert.Equal(1, f.Providers.Launches);
        f.Providers.UniqueWork = true;
        var error = await Assert.ThrowsAsync<WorkflowException>(() => f.Application.ExecuteAsync(new("cleanup", f.Root, id)));
        Assert.Equal("cleanup_refused", error.Code); Assert.DoesNotContain("teardown", f.Providers.Calls);
        var cleanup = await f.Application.ExecuteAsync(new("cleanup", f.Root, id, Force: true));
        Assert.Equal("cleanup_complete", cleanup.Message); Assert.Null(f.Store.Find(id));
        Assert.NotNull(f.Store.FindProject(f.Root));
    }

    [Fact]
    public async Task Failed_Linear_startup_preserves_workspace_and_never_launches_agent()
    {
        await using var f = new WorkflowFixture(); f.Providers.FailLinear = true;
        Guid id = await f.Start(); var failed = await f.Until(id, d => d.State.Recovery is not null);
        Assert.NotNull(failed.State.Workspace); Assert.Equal(0, f.Providers.Launches);
        f.Providers.FailLinear = false;
        await f.Application.ExecuteAsync(new("resume", f.Root, id)); await f.Pass(id, 1);
        Assert.Equal(1, f.Providers.Creations);
        Assert.Equal(OperationStatus.Failed, f.Application.Get(id).Operations[0].Status);
    }

    [Fact]
    public async Task Lost_workspace_response_reconciles_before_retry_without_second_creation()
    {
        await using var f = new WorkflowFixture(); f.Providers.WorkspaceResponseLost = true;
        Guid id = await f.Start(); await f.Until(id, d => d.State.Recovery is not null);
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        await f.Pass(id, 1); Assert.Equal(1, f.Providers.Creations);
    }

    [Fact]
    public async Task Unreachable_execution_preserves_last_verified_observation()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Until(id, d => d.Operations.Last().Observation?.State == ExecutionState.Running);
        f.Providers.Unreachable = true;
        var data = await f.Until(id, d => d.Operations.Last().Observation?.Reachable == false);
        Assert.Equal(OperationStatus.Running, data.Operations.Last().Status);
        Assert.Equal(ExecutionState.Running, data.Operations.Last().Observation!.State);
        Assert.Equal(1, f.Providers.Launches);
    }

    [Fact]
    public async Task Session_loss_needs_explicit_new_session_choice()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Deliver(id, WorkflowFixture.Ready()); await f.Pass(id, 2);
        f.Providers.SessionLost = true;
        await f.Deliver(id, new ReviewResult("ChangesRequired", WorkflowFixture.Report(), [WorkflowFixture.Finding()], null, null));
        await f.Until(id, d => d.State.Recovery?.Code == "session_reuse_unavailable");
        Assert.Equal(2, f.Providers.Launches);
        await f.Application.ExecuteAsync(new("select", f.Root, id, Role: AgentRole.Implementer, Session: "new"));
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        var data = await f.Pass(id, 3);
        Assert.NotEqual(data.Passes[0].SessionId, data.Passes[2].SessionId);
        Assert.Empty(data.State.NextSessionChoices);
    }

    [Fact]
    public async Task Explicit_replacement_of_uncertain_reviewer_revokes_earlier_authority_and_retains_late_result()
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Deliver(id, WorkflowFixture.Ready()); var data = await f.Pass(id, 2);
        var earlier = data.Passes[1];
        f.Providers.SessionObservations[earlier.SessionId] = ExecutionState.Unknown;
        await f.Application.ExecuteAsync(new("select", f.Root, id, Role: AgentRole.Reviewer, Session: "new"));
        await f.Application.ExecuteAsync(new("review", f.Root, id));
        var replacement = await f.Pass(id, 3);
        Assert.NotEqual(earlier.SessionId, replacement.Passes[2].SessionId);
        await WorkflowFixture.Atomic(earlier.DeliveryPath, AgenticWorkflows.Infrastructure.ResultEnvelopeContract.Serialize(earlier.OperationId, earlier.Id, earlier.Role, WorkflowFixture.Clean()));
        f.Providers.SessionObservations[earlier.SessionId] = ExecutionState.Completed;
        var historical = await f.Until(id, d => d.Results.Any(r => r.PassId == earlier.Id));
        Assert.NotEqual(WorkflowPhase.Submission, historical.State.Phase);
        Assert.Null(historical.State.Review);
        Assert.Contains(historical.Events, e => e.Kind == "HistoricalReviewCompleted");
        await f.Deliver(id, WorkflowFixture.Clean());
        var clean = await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission);
        Assert.Equal(replacement.Passes[2].Id, clean.State.Review!.PassId);
    }

    [Fact]
    public async Task Force_cleanup_of_unproven_preparation_deletes_only_AW_records()
    {
        await using var f = new WorkflowFixture(); f.Providers.WorkspaceResponseLost = true;
        Guid id = await f.Start(); await f.Until(id, d => d.State.Recovery is not null);
        var result = await f.Application.ExecuteAsync(new("cleanup", f.Root, id, Force: true));
        Assert.Equal("cleanup_partial", result.Message); Assert.Null(f.Store.Find(id));
        Assert.DoesNotContain("teardown", f.Providers.Calls); Assert.DoesNotContain("delete-branch", f.Providers.Calls);
    }
}
