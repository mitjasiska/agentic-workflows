using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class SubmissionPolicyTests
{
    [Fact]
    public async Task Verified_repository_rename_refreshes_PR_link_without_replacing_identity_or_intent()
    {
        await using var f = new WorkflowFixture(); Guid id = await Clean(f);
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var first = await f.Until(id, d => d.State.Submission?.PullRequest is not null);
        f.Providers.PullRequest = f.Providers.PullRequest! with { Url = "https://github.com/owner/renamed/pull/1" };
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var next = await f.Until(id, d => d.State.CurrentOperationId != first.State.CurrentOperationId && d.Operations.Last().Status == OperationStatus.Completed);
        Assert.Equal(first.State.Submission!.Intent, next.State.Submission!.Intent);
        Assert.Equal(first.State.Submission.PullRequest!.Id, next.State.Submission.PullRequest!.Id);
        Assert.Equal(f.Providers.PullRequest.Url, next.State.Submission.PullRequest.Url);
        Assert.Single(f.Providers.Calls, c => c == "pr");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_intent_and_phase_are_atomic_across_checkpoint_failure(bool after)
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Application.ExecuteAsync(new("interrupt", f.Root, id));
        await f.Application.DisposeAsync(); var p = f.Providers;
        f.Application = new(new SubmissionFaultStore(f.Store, "PrIntentFrozen", after), p, p, p, p, p, f.Delivery);
        await f.Application.ExecuteAsync(new("pr", f.Root, id, Force: true));
        var failed = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Failed);
        Assert.Equal(after ? WorkflowPhase.Submission : WorkflowPhase.Implementation, failed.State.Phase);
        Assert.Equal(after, failed.State.Submission is not null);
        Assert.Equal(after ? 1 : 0, failed.Events.Count(e => e.Kind == "PrIntentFrozen"));
        Assert.DoesNotContain("commit", p.Calls);
    }

    [Fact]
    public async Task Retry_reuses_frozen_metadata_after_mutable_Linear_title_and_label_changes()
    {
        await using var f = new WorkflowFixture(); Guid id = await Clean(f); f.Providers.LosePrResponse = true;
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var failed = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Failed);
        var intent = failed.State.Submission!.Intent;
        f.Providers.Source = f.Providers.Source with { Title = "PRIVATE changed title", Labels = ["Bug"], Revision = "changed" };
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var done = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Completed);
        Assert.Equal(intent, done.State.Submission!.Intent); Assert.DoesNotContain("PRIVATE", intent.Body);
        Assert.StartsWith("feat:", intent.Title); Assert.Single(f.Providers.Calls, c => c == "pr");
    }
    [Theory]
    [InlineData(WorkflowPhase.Implementation)]
    [InlineData(WorkflowPhase.Review)]
    [InlineData(WorkflowPhase.Submission)]
    public async Task Force_from_permitted_phase_freezes_generic_metadata_and_requires_new_flag_on_retry(WorkflowPhase phase)
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        await f.Application.ExecuteAsync(new("interrupt", f.Root, id));
        Change(f, id, s => s with { Phase = phase });
        f.Providers.LosePrResponse = true;
        await f.Application.ExecuteAsync(new("pr", f.Root, id, Force: true));
        var failed = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Failed);
        var intent = failed.State.Submission!.Intent;
        Assert.Equal(phase, intent.PreviousPhase); Assert.Equal(WorkflowPhase.Submission, failed.State.Phase);
        Assert.Equal("feat: complete requested change (DEV-123)", intent.Title);
        Assert.Contains("Task-Review: bypassed-by-force", intent.CommitMessage);
        Assert.Contains("Independent review was bypassed", intent.Body);
        Assert.Contains(failed.State.Warnings, w => w.Code == "forced_submission");
        var operation = failed.Operations.Last();
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var refused = await f.Until(id, d => d.Operations.Last().Id != operation.Id && d.Operations.Last().Status == OperationStatus.Failed);
        Assert.Equal(intent, refused.State.Submission!.Intent);
        Assert.False(refused.Operations.Last().Force);
        await f.Application.ExecuteAsync(new("pr", f.Root, id, Force: true));
        var done = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Completed);
        Assert.NotNull(done.State.Submission!.PullRequest); Assert.Equal(intent, done.State.Submission.Intent);
        Assert.Single(f.Providers.Calls, c => c == "pr");
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("active")]
    [InlineData("unknown")]
    [InlineData("attention")]
    [InlineData("remote")]
    [InlineData("abandoned")]
    public async Task Force_refusal_preserves_phase_and_cannot_bypass_nonreview_safety(string reason)
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        if (reason != "active") await f.Application.ExecuteAsync(new("interrupt", f.Root, id));
        if (reason == "preparation") Change(f, id, s => s with { Phase = WorkflowPhase.WorkspacePreparation, StartupLinearCompleted = false });
        if (reason == "unknown") f.Providers.Execution = ExecutionState.Unknown;
        if (reason == "attention") Change(f, id, s => s with { Attention = [new(AttentionReason.PassContinuationDecision, "r", "Policy unresolved", "Resolve explicitly")] });
        if (reason == "remote") f.Providers.SubmissionFailure = "remote_revision_changed";
        if (reason == "abandoned") await f.Application.ExecuteAsync(new("abandon", f.Root, id));
        var before = f.Application.Get(id).State;
        if (reason == "active") Assert.Equal("active_pass", (await Assert.ThrowsAsync<WorkflowException>(() => f.Application.ExecuteAsync(new("pr", f.Root, id, Force: true)))).Code);
        else
        {
            await f.Application.ExecuteAsync(new("pr", f.Root, id, Force: true));
            await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Failed);
        }
        var after = f.Application.Get(id).State;
        Assert.Equal(before.Phase, after.Phase); Assert.Null(after.Submission);
        Assert.DoesNotContain("commit", f.Providers.Calls); Assert.DoesNotContain("push", f.Providers.Calls); Assert.DoesNotContain("pr", f.Providers.Calls);
    }

    [Theory]
    [InlineData(CheckResult.Failed, "Focused checks passed. An unrelated test failed on the unchanged base; the live smoke test was not run.")]
    [InlineData(CheckResult.Failed, "Focused checks passed. The unrelated baseline check remains broken on the unchanged base.")]
    [InlineData(CheckResult.NotRun, "Focused checks passed. Live integration testing awaits operator credentials.")]
    public async Task Accepted_validation_limitation_remains_ordinary_submission_evidence(CheckResult result, string validation)
    {
        await using var f = new WorkflowFixture(); Guid id = await f.Start(); await f.Pass(id, 1);
        var check = new ValidationEvidence("unrelated test", result,
            result == CheckResult.Failed ? "Fails on pinned base too." : "Live integration awaits credentials.", "implementer", "base", "unrelated",
            result == CheckResult.Failed ? "Same exact test failed on unchanged base." : "Live integration is outside this task's validation scope.");
        await f.Deliver(id, WorkflowFixture.Ready() with { Report = WorkflowFixture.Report([check]) });
        await f.Pass(id, 2);
        await f.Deliver(id, WorkflowFixture.Clean() with { Report = WorkflowFixture.Report([check]), PublicMetadata = new("implement durable workflows", "Implements the requested behavior.", validation) });
        await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission);
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var done = await f.Until(id, d => d.Operations.Last().Status is OperationStatus.Completed or OperationStatus.Failed);
        Assert.Null(done.State.Recovery); Assert.False(done.State.Submission!.Intent.Forced);
        Assert.Contains(validation, done.State.Submission.Intent.Body);
        Assert.All(done.Results, r => Assert.Equal(check, r.Payload.Report.Checks.Single()));
        var frozen = done.State.Submission.Intent;
        await f.Restart();
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var retried = await f.Until(id, d => d.Operations.Last().Status is OperationStatus.Completed or OperationStatus.Failed);
        Assert.Null(retried.State.Recovery); Assert.Equal(frozen, retried.State.Submission!.Intent);
        Assert.All(retried.Results, r => Assert.Equal(check, r.Payload.Report.Checks.Single()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("stale-snapshot")]
    [InlineData("stale-tree")]
    public async Task Missing_invalid_or_stale_review_never_falls_back_to_task_title(string reason)
    {
        await using var f = new WorkflowFixture(); Guid id = await Clean(f);
        Change(f, id, s => reason switch
        {
            "missing" => s with { Review = s.Review! with { PublicMetadata = null } },
            "invalid" => s with { Review = s.Review! with { PublicMetadata = new("feat: private title", "bad", "bad") } },
            "stale-snapshot" => s with { Review = s.Review! with { SnapshotId = Guid.NewGuid() } },
            _ => s
        });
        if (reason == "stale-tree") f.Providers.Drift = true;
        await f.Application.ExecuteAsync(new("pr", f.Root, id));
        var refused = await f.Until(id, d => d.Operations.Last().Status == OperationStatus.Failed);
        Assert.Null(refused.State.Submission); Assert.DoesNotContain("commit", f.Providers.Calls);
    }

    [Fact]
    public async Task Concurrent_commands_serialize_and_completed_submission_cannot_start_followup_batch()
    {
        await using var f = await SubmissionFixture.Create();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.Before = async args =>
        {
            if (args[0] != "update-ref") return;
            reached.TrySetResult(); await release.Task;
        };
        var first = await f.Workflow.Application.ExecuteAsync(new("pr", f.Canonical, f.Id));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = f.Workflow.Application.ExecuteAsync(new("pr", f.Canonical, f.Id));
        var review = f.Workflow.Application.ExecuteAsync(new("review", f.Canonical, f.Id));
        Assert.False(second.IsCompleted); Assert.False(review.IsCompleted);
        var projection = await new WorkflowQueries(f.Workflow.Application, f.Repository).Summary(f.Id);
        Assert.Equal(first.OperationId, projection.OperationId); Assert.NotNull(projection.Submission);
        release.SetResult(); await second;
        Assert.Equal("submission_frozen", (await Assert.ThrowsAsync<WorkflowException>(() => review)).Code);
        await f.Workflow.Until(f.Id, d => d.Operations.Last().Status == OperationStatus.Completed);
        Assert.Single(f.Transport.Effects, e => e == "ref"); Assert.Single(f.Transport.Effects, e => e == "push");
        Assert.Single(f.Workflow.Providers.Calls, c => c == "pr");
    }

    [Theory]
    [InlineData("Feature", "feat")]
    [InlineData("Bug", "fix")]
    [InlineData("Chore", "chore")]
    [InlineData("Docs", "docs")]
    [InlineData("Refactor", "refactor")]
    public void Metadata_uses_exact_primary_category_and_reviewed_prose(string category, string type)
    {
        var task = Task([category, "Research", "Unrelated"]);
        var id = Guid.NewGuid(); var result = SubmissionMetadata.Create(task, new("fix requested behavior", "Public change description", "Focused checks passed"), id, false);
        Assert.Equal($"{type}: fix requested behavior (DEV-128)", result.Title);
        Assert.Equal(result.Title + "\n\nTask-Review: " + id + "\n", result.Message);
        Assert.DoesNotContain(task.Title, result.Body + result.Message);
    }

    [Theory]
    [InlineData("Upper case", "safe", "safe")]
    [InlineData("feat: duplicate prefix", "safe", "safe")]
    [InlineData("change (dev-128)", "safe", "safe")]
    [InlineData("change.", "safe", "safe")]
    [InlineData("change\nmore", "safe", "safe")]
    [InlineData("change", "# heading", "safe")]
    [InlineData("change", "See /home/user/private", "safe")]
    [InlineData("change", "safe", "api_key=secret")]
    [InlineData("change", "Agent instructions: private", "safe")]
    public void Public_metadata_rejects_unsafe_shape_and_private_content(string summary, string description, string validation) =>
        Assert.Throws<WorkflowException>(() => SubmissionMetadata.Create(Task(["Feature"]), new(summary, description, validation), Guid.NewGuid(), false));

    [Fact]
    public void Metadata_bounds_include_identifier_and_do_not_truncate_or_guess()
    {
        Assert.Throws<WorkflowException>(() => SubmissionMetadata.Create(Task(["Refactor"]), new(new string('a', 60), "safe", "safe"), Guid.NewGuid(), false));
        Assert.Throws<WorkflowException>(() => SubmissionMetadata.Create(Task(["Feature"]), new("change", new string('a', 2001), "safe"), Guid.NewGuid(), false));
        Assert.Throws<WorkflowException>(() => SubmissionMetadata.Create(Task([]), null, null, true));
        Assert.Throws<WorkflowException>(() => SubmissionMetadata.Create(Task(["Feature", "Bug"]), null, null, true));
        Assert.Throws<WorkflowException>(() => SubmissionMetadata.Create(Task(["Feature"]) with { Url = "https://evil.example/DEV-128" }, null, null, true));
    }

    internal static LinearTask Task(string[] labels) => new("task", "DEV-128", "PRIVATE mutable task title", "Private instructions", "r1", "project", "team", "started", "started", "https://linear.app/example/issue/DEV-128", labels);
    internal static async Task<Guid> Clean(WorkflowFixture f)
    {
        Guid id = await f.Start(); await f.Pass(id, 1); await f.Deliver(id, WorkflowFixture.Ready());
        await f.Pass(id, 2); await f.Deliver(id, WorkflowFixture.Clean());
        await f.Until(id, d => d.State.Phase == WorkflowPhase.Submission); return id;
    }
    internal static void Change(WorkflowFixture f, Guid id, Func<WorkflowState, WorkflowState> mutate)
    {
        var state = f.Application.Get(id).State;
        f.Store.Save(state.Version, new(mutate(state) with { Version = state.Version + 1 },
            [new(0, "FixtureState", "Controlled policy fixture", state.CurrentOperationId, null, DateTimeOffset.UtcNow)], [], [], [], [], []));
    }
}
