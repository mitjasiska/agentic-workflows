using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflows.Tests;

public sealed class WorkflowRecoveryRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_retained_revisions_remain_exact_immutable_history_after_restart(bool cancellationGates)
    {
        await using var f = new WorkflowFixture();
        Guid id = await Clean(f);
        Guid snapshot = f.Application.Get(id).State.ActiveSnapshotId;
        var expected = new List<SourceRetention>();
        foreach (string revision in new[] { "examined-r2", "examined-r3" })
        {
            f.Providers.Source = f.Providers.Source with
            {
                Revision = revision, Description = "# Agent instructions\nRequirements at " + revision,
                StateType = cancellationGates ? "canceled" : "started"
            };
            string fingerprint = WorkflowRules.Capture(f.Providers.Source, "warn", DateTimeOffset.UtcNow).Fingerprint;
            expected.Add(new(revision, fingerprint, snapshot));
            await f.Application.ExecuteAsync(new("resume", f.Root, id, SourceDecision: "retain"));
            Assert.Equal(fingerprint, f.Application.Get(id).State.HandledSourceFingerprint);
        }
        await f.Restart();
        var retained = new SqliteWorkflowStore(Path.Combine(f.Root, "workflows.db")).Find(id)!;
        var decisions = retained.Events.Where(item => item.SourceRetention is not null).ToArray();
        Assert.Equal(expected, decisions.Select(item => item.SourceRetention!));
        Assert.All(decisions, item => Assert.Equal("TaskSnapshotRetained", item.Kind));
        Assert.True(decisions[0].Position < decisions[1].Position);
        Assert.Equal("examined-r3", retained.State.LatestLinearObservation.Revision);
        Assert.Single(retained.Snapshots); Assert.Equal(snapshot, retained.State.ActiveSnapshotId);
        Assert.Equal(2, f.Providers.Launches);
        if (cancellationGates) Assert.Contains(retained.State.Attention, a => a.Reason == AttentionReason.LinearCancellationDecision);
        var history = await new WorkflowQueries(f.Application, f.Providers).ExecuteAsync(new(f.Root, id, "history", decisions[0].Position - 1));
        Assert.Equal(expected[0], history.History[0].SourceRetention);
        using var db = new SqliteConnection($"Data Source={Path.Combine(f.Root, "workflows.db")};Pooling=False"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE events SET json='{}' WHERE kind='TaskSnapshotRetained'";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "DELETE FROM events WHERE kind='TaskSnapshotRetained'";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public async Task Retention_evidence_failure_rolls_back_the_decision_state_and_continuation()
    {
        await using var f = new WorkflowFixture();
        Guid id = await Clean(f);
        f.Providers.Source = f.Providers.Source with { Revision = "r2", Description = "# Agent instructions\nNew requirements" };
        using var db = new SqliteConnection($"Data Source={Path.Combine(f.Root, "workflows.db")};Pooling=False"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_retention BEFORE INSERT ON events WHEN NEW.kind='TaskSnapshotRetained' BEGIN SELECT RAISE(ABORT,'injected evidence failure'); END;";
        command.ExecuteNonQuery();
        var before = f.Application.Get(id);
        await Assert.ThrowsAsync<SqliteException>(() => f.Application.ExecuteAsync(new("resume", f.Root, id, SourceDecision: "retain")));
        var after = f.Application.Get(id);
        Assert.Equal(before.State.HandledSourceFingerprint, after.State.HandledSourceFingerprint);
        Assert.Equal(before.State.ActiveSnapshotId, after.State.ActiveSnapshotId);
        Assert.Equal(before.Operations, after.Operations);
        Assert.DoesNotContain(after.Events, item => item.Kind is "TaskSnapshotRetained" or "WorkflowRetained");
        Assert.Contains(after.State.Attention, item => item.Reason == AttentionReason.TaskChangeDecision);
        command.CommandText = "DROP TRIGGER reject_retention"; command.ExecuteNonQuery();
        await f.Application.ExecuteAsync(new("resume", f.Root, id, SourceDecision: "retain"));
        Assert.Single(f.Application.Get(id).Events, item => item.Kind == "TaskSnapshotRetained");
    }

    [Fact]
    public async Task Same_git_tree_with_changed_materialized_bytes_cannot_authorize_clean_review()
    {
        await using var f = new WorkflowFixture();
        Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Deliver(id, WorkflowFixture.Ready()); var reviewing = await f.Pass(id, 2);
        var checkpoint = reviewing.Passes.Last().ReviewCheckpoint!;
        f.Providers.RawWorktreeDrift = true;
        var current = await f.Providers.CaptureAsync(reviewing.State.Workspace!, reviewing.State.WorkspaceIntent.BaseCommit, default);
        Assert.Equal(checkpoint.Target, current.Target); Assert.Equal(checkpoint.IndexIdentity, current.IndexIdentity);
        await f.Deliver(id, WorkflowFixture.Clean());
        var rejected = await f.Until(id, d => d.State.Review is not null);
        Assert.False(rejected.State.Review!.AuthorityValid);
        Assert.Equal(WorkflowPhase.Review, rejected.State.Phase);
        Assert.Equal("Clean", rejected.ReviewReports.Single().Outcome);
        Assert.NotEqual("eligible", (await new WorkflowQueries(f.Application, f.Providers).Summary(id)).SubmissionReadiness);
        await f.Restart();
        Assert.False(f.Application.Get(id).State.Review!.AuthorityValid);
    }

    [Fact]
    public async Task Restart_retains_unattempted_handoff_and_resume_creates_one_revalidated_successor()
    {
        await using var f = new WorkflowFixture();
        await f.Application.DisposeAsync();
        var delivery = new PausedDelivery(f.Delivery);
        f.Application = new(f.Store, f.Providers, f.Providers, f.Providers, f.Providers, f.Providers, delivery);
        Guid id = await f.Start();
        await delivery.Recorded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var recorded = f.Application.Get(id);
        var original = Assert.Single(recorded.Passes);
        Assert.Equal(0, f.Providers.Launches); Assert.Equal(0, recorded.State.TotalPasses);
        Assert.False(recorded.Operations.Last().ExternalActionAttempted);
        await f.Restart();
        var reconciled = f.Application.Get(id);
        Assert.Equal("launch_not_attempted", reconciled.State.Recovery!.Code);
        var predecessor = reconciled.Operations.Single(o => o.Id == original.OperationId);
        Assert.Equal(OperationStatus.Failed, predecessor.Status);
        Assert.Equal(0, f.Providers.Launches);
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        var resumed = await f.Pass(id, 2);
        Assert.Equal(1, f.Providers.Launches); Assert.Equal(1, resumed.State.TotalPasses);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(predecessor),
            System.Text.Json.JsonSerializer.Serialize(resumed.Operations.Single(o => o.Id == predecessor.Id)));
        Assert.Equal(predecessor.Id, resumed.Operations.Last().PredecessorId);
        Assert.Equal(original.SessionId, resumed.Passes.Last().SessionId);
        Assert.NotEqual(original.DeliveryPath, resumed.Passes.Last().DeliveryPath);
        Assert.NotEqual(original.Id, resumed.Passes.Last().Id);
        Assert.Single(resumed.Sessions);
        Assert.DoesNotContain(resumed.Events, e => e.Kind == "ExecutionUncertain");
        await f.Deliver(id, WorkflowFixture.Ready());
        var review = await f.Pass(id, 3);
        Assert.Equal(AgentRole.Reviewer, review.Passes.Last().Role);
        Assert.Equal(2, review.State.TotalPasses);
    }

    [Fact]
    public async Task Resume_reattaches_running_abandoned_implementation_and_restores_routing()
    {
        await using var f = new WorkflowFixture();
        Guid id = await f.Start(); var running = await f.Pass(id, 1);
        await f.Application.ExecuteAsync(new("abandon", f.Root, id));
        Assert.True(f.Application.Get(id).State.Abandoned);
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        var resumed = f.Application.Get(id);
        Assert.False(resumed.State.Abandoned);
        Assert.Equal(running.State.CurrentOperationId, resumed.State.CurrentOperationId);
        Assert.Equal(1, f.Providers.Launches);
        Assert.Single(resumed.Events, e => e.Kind == "WorkflowReattached");
        await f.Deliver(id, WorkflowFixture.Ready());
        Assert.Equal(AgentRole.Reviewer, (await f.Pass(id, 2)).Passes.Last().Role);
    }

    [Theory]
    [InlineData(ExecutionState.Running, false)]
    [InlineData(ExecutionState.Unknown, false)]
    [InlineData(ExecutionState.Idle, true)]
    public async Task Ordinary_cleanup_rechecks_every_retained_session_after_clean(ExecutionState execution, bool unreachable)
    {
        await using var f = new WorkflowFixture();
        Guid id = await Clean(f);
        var data = f.Application.Get(id);
        f.Providers.SessionObservations[data.Sessions[0].Id] = execution;
        f.Providers.Unreachable = unreachable;
        var error = await Assert.ThrowsAsync<WorkflowException>(() => f.Application.ExecuteAsync(new("cleanup", f.Root, id)));
        Assert.Equal("cleanup_refused", error.Code);
        Assert.NotNull(f.Store.Find(id));
        Assert.DoesNotContain("teardown", f.Providers.Calls);
        Assert.DoesNotContain("delete-branch", f.Providers.Calls);
        Assert.Equal("cleanup_refused", f.Application.Get(id).State.Recovery!.Code);
    }

    [Fact]
    public async Task Forced_cleanup_attempts_remaining_owned_resources_after_positive_worktree_absence()
    {
        await using var f = new WorkflowFixture();
        Guid id = await Clean(f);
        f.Providers.RepositoryFailure = "worktree_absent";
        var result = await f.Application.ExecuteAsync(new("cleanup", f.Root, id, Force: true));
        Assert.Equal("cleanup_partial", result.Message);
        Assert.Contains("teardown", f.Providers.Calls); Assert.Contains("delete-branch", f.Providers.Calls);
        Assert.Contains(result.ComponentOutcomes!, message => message.Contains("positively absent", StringComparison.Ordinal));
        Assert.Null(f.Store.Find(id));
    }

    [Theory]
    [InlineData("workspace_identity_mismatch")]
    [InlineData("repository_identity_mismatch")]
    [InlineData("symlink_path")]
    public async Task Forced_cleanup_still_refuses_contradictory_deletion_identity(string diagnostic)
    {
        await using var f = new WorkflowFixture();
        Guid id = await Clean(f);
        f.Providers.RepositoryFailure = diagnostic;
        Assert.Equal(diagnostic, (await Assert.ThrowsAsync<WorkflowException>(() => f.Application.ExecuteAsync(new("cleanup", f.Root, id, Force: true)))).Code);
        Assert.DoesNotContain("teardown", f.Providers.Calls); Assert.DoesNotContain("delete-branch", f.Providers.Calls);
        Assert.NotNull(f.Store.Find(id));
    }

    [Fact]
    public async Task Invalid_herdr_output_preserves_verified_execution_and_durable_recovery_across_restart()
    {
        await using var f = new WorkflowFixture();
        Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Until(id, d => d.Operations.Last().Observation?.State == ExecutionState.Running);
        await f.Application.DisposeAsync();
        var verified = f.Application.Get(id).Operations.Last().Observation!;
        f.Providers.ObservationFailure = "herdr_invalid_output";
        f.Application = f.CreateApplication();
        await f.Application.ReconcileStartupAsync();
        var retained = f.Application.Get(id);
        Assert.Equal(OperationStatus.Running, retained.Operations.Last().Status);
        Assert.Equal(verified.State, retained.Operations.Last().Observation!.State);
        Assert.Equal(verified.VerifiedAt, retained.Operations.Last().Observation!.VerifiedAt);
        Assert.False(retained.Operations.Last().Observation!.Reachable);
        Assert.Equal("herdr_invalid_output", retained.State.Recovery!.Code);
        Assert.Equal(1, f.Providers.Launches);
        f.Providers.ObservationFailure = null;
        await f.Application.ExecuteAsync(new("resume", f.Root, id));
        await f.Deliver(id, WorkflowFixture.Ready());
        await f.Pass(id, 2);
        Assert.Equal(2, f.Providers.Launches);
    }

    private static async Task<Guid> Clean(WorkflowFixture f)
    {
        Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Deliver(id, WorkflowFixture.Ready()); await f.Pass(id, 2);
        await f.Deliver(id, WorkflowFixture.Clean());
        await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission);
        return id;
    }

    private sealed class PausedDelivery(IPassDelivery inner) : IPassDelivery
    {
        internal TaskCompletionSource Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Handoff Compose(WorkflowState state, TaskSnapshot snapshot, Guid passId, Guid operationId, AgentRole role, PassConfiguration configuration,
            Guidance guidance, RepositoryCheckpoint? target, Finding[] findings, ValidatedResult? result, string path) =>
            inner.Compose(state, snapshot, passId, operationId, role, configuration, guidance, target, findings, result, path);
        public async Task PrepareAsync(ManagedPass pass, CancellationToken token)
        { Recorded.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        public Task<ValidatedResult?> ReadAsync(ManagedPass pass, CancellationToken token) => inner.ReadAsync(pass, token);
        public void Retire(ManagedPass pass) => inner.Retire(pass);
        public void Cleanup(ManagedPass[] passes) => inner.Cleanup(passes);
    }
}
