using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflows.Tests;

public sealed class SubmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_creation_race_preserves_even_an_unexpected_fast_forwardable_revision(bool competingChange)
    {
        await using var f = await SubmissionFixture.Create();
        string? competing = null;
        f.Transport.Before = async args =>
        {
            if (!args.Contains("push", StringComparer.Ordinal)) return;
            f.Transport.Before = null;
            var intent = f.Data.State.Submission!.Intent;
            Assert.Contains("--force-with-lease=refs/heads/" + intent.Branch + ":", args);
            Assert.DoesNotContain("--force", args); Assert.DoesNotContain(args, a => a.StartsWith('+'));
            competing = competingChange ? await f.Git(f.Canonical, "commit-tree", intent.Target.BaselineTree, "-p", intent.BaseCommit, "-m", "another writer") : intent.BaseCommit;
            await f.Git(f.Canonical, "push", f.Remote, competing + ":refs/heads/" + intent.Branch);
        };
        var refused = await f.Submit(); Assert.Equal("push_unconfirmed", refused.State.Recovery!.Code);
        Assert.Contains(competing!, refused.State.Recovery.Explanation);
        Assert.Equal(competing + "\trefs/heads/" + refused.State.WorkspaceIntent.Branch,
            await f.Git(f.Canonical, "ls-remote", f.Remote, "refs/heads/" + refused.State.WorkspaceIntent.Branch));
        Assert.DoesNotContain("push", f.Transport.Effects); Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
    }
    [Fact]
    public async Task Completed_submission_refuses_deleted_remote_branch_and_preserves_first_PR()
    {
        await using var f = await SubmissionFixture.Create(); var done = await f.Submit(); Assert.Null(done.State.Recovery);
        var submitted = done.State.Submission!;
        await f.Git(f.Remote, "update-ref", "-d", "refs/heads/" + submitted.Intent.Branch, submitted.Intent.Commit);
        var refused = await f.Submit(); Assert.Equal("remote_revision_changed", refused.State.Recovery!.Code);
        Assert.Equal(submitted, refused.State.Submission);
        Assert.Single(f.Transport.Effects, e => e == "push"); Assert.Single(f.Workflow.Providers.Calls, c => c == "pr");
        Assert.Equal("", await f.Git(f.Canonical, "ls-remote", f.Remote, "refs/heads/" + submitted.Intent.Branch));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Advanced_base_is_checked_without_rewriting_reviewed_content(bool overlap)
    {
        await using var f = await SubmissionFixture.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Canonical, overlap ? "file.txt" : "independent.txt"), "base advanced\n");
        await f.Git(f.Canonical, "add", "."); await f.Git(f.Canonical, "commit", "-m", "advance base");
        await f.Git(f.Canonical, "push", f.Remote, "main");
        var result = await f.Submit();
        if (overlap)
        {
            Assert.Equal("base_conflict", result.State.Recovery!.Code); Assert.Null(result.State.Submission);
            Assert.Empty(f.Transport.Effects);
        }
        else
        {
            Assert.Null(result.State.Recovery);
            Assert.Equal(result.State.Review!.Target.ProposedTree, await f.Git(f.Worktree, "rev-parse", "HEAD^{tree}"));
            Assert.Equal(result.State.WorkspaceIntent.BaseCommit, await f.Git(f.Worktree, "show", "-s", "--format=%P", "HEAD"));
        }
    }

    [Theory]
    [InlineData("PullRequestWriteStarted", false, false)]
    [InlineData("PullRequestWriteStarted", true, true)]
    public async Task PR_write_checkpoint_uncertainty_never_causes_blind_create(string point, bool after, bool ambiguous)
    {
        await using var f = await SubmissionFixture.Create(); await f.Workflow.Application.DisposeAsync();
        var p = f.Workflow.Providers;
        f.Workflow.Application = new(new SubmissionFaultStore(f.Workflow.Store, point, after), p, p, p, p, f.Repository, f.Workflow.Delivery);
        var failed = await f.Submit(); Assert.NotNull(failed.State.Recovery); Assert.DoesNotContain("pr", p.Calls);
        await f.Workflow.Restart(); var retry = await f.Submit();
        if (ambiguous)
        {
            Assert.Equal("forge_write_ambiguous", retry.State.Recovery!.Code); Assert.DoesNotContain("pr", p.Calls);
            // Exact provider evidence restored outside AW, then an ordinary retry reconciles it.
            p.PullRequest = new(42, 1, "https://github.com/owner/repo/pull/1");
            retry = await f.Submit();
        }
        Assert.Null(retry.State.Recovery); Assert.NotNull(retry.State.Submission!.PullRequest);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_vertical_path_creates_exact_commit_push_and_first_PR_then_reuses_it(bool repair)
    {
        await using var f = await SubmissionFixture.Create(repair);
        Assert.Empty(f.Transport.Effects);
        var reviewed = f.Data.State.Review!;
        string indexPath = await f.Git(f.Worktree, "rev-parse", "--git-path", "index");
        byte[] index = await File.ReadAllBytesAsync(indexPath);
        var done = await f.Submit(); Assert.Null(done.State.Recovery);
        var submitted = done.State.Submission!; var intent = submitted.Intent;
        Assert.Equal(reviewed.Target.ProposedTree, await f.Git(f.Worktree, "rev-parse", "HEAD^{tree}"));
        Assert.Equal(intent.BaseCommit, await f.Git(f.Worktree, "show", "-s", "--format=%P", "HEAD"));
        Assert.Equal(intent.Commit, await f.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.Equal(intent.Commit + "\trefs/heads/" + intent.Branch, await f.Git(f.Canonical, "ls-remote", f.Remote, "refs/heads/" + intent.Branch));
        Assert.Contains("Task-Review: " + reviewed.PassId, await f.Git(f.Worktree, "show", "-s", "--format=%B", "HEAD"));
        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(new[] { "commit", "ref", "push" }, f.Transport.Effects);
        Assert.Equal(WorkflowPhase.Submission, done.State.Phase);
        Assert.NotNull(submitted.PullRequest);
        Assert.Contains("## Summary", intent.Body); Assert.Contains("## Tracking", intent.Body); Assert.Contains("## Validation", intent.Body);
        Assert.Contains("Fixes DEV-123", intent.Body); Assert.Contains("https://linear.app/example/issue/DEV-123", intent.Body);
        Assert.Contains("Independent review accepted the exact change", intent.Body);
        Assert.Equal("feat: implement durable workflows (DEV-123)", intent.Title);
        await f.Workflow.Restart(); var again = await f.Submit(); Assert.Null(again.State.Recovery);
        Assert.Equal(intent, again.State.Submission!.Intent);
        Assert.Equal(submitted.PullRequest, again.State.Submission.PullRequest);
        Assert.Single(f.Workflow.Providers.Calls, c => c == "pr");
        Assert.Equal(3, f.Transport.Effects.Count);
        var attempts = again.Operations.Where(o => o.Kind == "Submission").ToArray();
        Assert.Equal(2, attempts.Length); Assert.Equal(attempts[0].Id, attempts[1].PredecessorId);
        Assert.All(attempts, a => Assert.Equal(OperationStatus.Completed, a.Status));
        var summary = await new WorkflowQueries(f.Workflow.Application, f.Repository).Summary(f.Id);
        Assert.Equal(submitted.PullRequest, summary.Submission!.PullRequest);
        Assert.Equal("submitted", summary.Submission.Stage);
        Assert.Equal(repair ? 4 : 2, again.Passes.Length);
        Assert.Equal(Enumerable.Range(1, again.Events.Length).Select(i => (long)i), again.Events.Select(e => e.Position));
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("ref")]
    [InlineData("push")]
    [InlineData("pr")]
    public async Task Lost_effect_response_restarts_and_reconciles_without_duplicates(string point)
    {
        await using var f = await SubmissionFixture.Create();
        if (point == "pr") f.Workflow.Providers.LosePrResponse = true; else f.Transport.FailAfter = point;
        var failed = await f.Submit(); Assert.NotNull(failed.State.Recovery);
        var intent = failed.State.Submission!.Intent;
        var failedOperation = failed.Operations.Last(); Assert.Equal(OperationStatus.Failed, failedOperation.Status);
        await f.Workflow.Restart(); var done = await f.Submit(); Assert.Null(done.State.Recovery);
        Assert.Equal(intent, done.State.Submission!.Intent);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(failedOperation), System.Text.Json.JsonSerializer.Serialize(done.Operations.Single(o => o.Id == failedOperation.Id)));
        Assert.Single(f.Transport.Effects, e => e == "ref"); Assert.Single(f.Transport.Effects, e => e == "push");
        Assert.Single(f.Workflow.Providers.Calls, c => c == "pr");
        Assert.Equal("1", await f.Git(f.Worktree, "rev-list", "--count", intent.BaseCommit + "..HEAD"));
    }

    [Theory]
    [InlineData("PrIntentFrozen", false)]
    [InlineData("PrIntentFrozen", true)]
    [InlineData("SubmissionCommitVerified", false)]
    [InlineData("SubmissionCommitVerified", true)]
    [InlineData("SubmissionPushVerified", false)]
    [InlineData("SubmissionCompleted", false)]
    public async Task SQLite_checkpoint_failure_retries_same_authorized_chain(string point, bool after)
    {
        await using var f = await SubmissionFixture.Create();
        await f.Workflow.Application.DisposeAsync();
        var providers = f.Workflow.Providers;
        f.Workflow.Application = new(new SubmissionFaultStore(f.Workflow.Store, point, after), providers, providers, providers, providers, f.Repository, f.Workflow.Delivery);
        var failed = await f.Submit(); Assert.NotNull(failed.State.Recovery);
        if (point == "PrIntentFrozen" && !after) { Assert.Null(failed.State.Submission); Assert.Empty(f.Transport.Effects); }
        var intent = failed.State.Submission?.Intent;
        await f.Workflow.Restart(); var done = await f.Submit(); Assert.Null(done.State.Recovery);
        if (intent is not null) Assert.Equal(intent, done.State.Submission!.Intent);
        Assert.Single(f.Transport.Effects, e => e == "ref"); Assert.Single(f.Transport.Effects, e => e == "push");
        Assert.Single(providers.Calls, c => c == "pr");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safe_local_commit_adopts_exact_message_or_replaces_only_metadata(bool exactMessage)
    {
        await using var f = await SubmissionFixture.Create();
        string message = SubmissionMetadata.Create(f.Workflow.Providers.Source, f.Data.State.Review!.PublicMetadata, f.Data.State.Review.PassId, false).Message;
        await f.Git(f.Worktree, "add", ".");
        await f.Git(f.Worktree, "commit", "-m", exactMessage ? message : "private arbitrary message");
        string original = await f.Git(f.Worktree, "rev-parse", "HEAD");
        string indexPath = await f.Git(f.Worktree, "rev-parse", "--git-path", "index");
        byte[] index = await File.ReadAllBytesAsync(indexPath);
        var done = await f.Submit(); Assert.Null(done.State.Recovery);
        var intent = done.State.Submission!.Intent;
        Assert.Equal(original, intent.OriginalHead);
        Assert.Equal(exactMessage, original == intent.Commit);
        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal("", await f.Git(f.Worktree, "status", "--porcelain=v1"));
        Assert.Equal(await f.Git(f.Worktree, "rev-parse", original + "^{tree}"), await f.Git(f.Worktree, "rev-parse", "HEAD^{tree}"));
        Assert.Equal(await f.Git(f.Worktree, "show", "-s", "--format=%P", original), await f.Git(f.Worktree, "show", "-s", "--format=%P", "HEAD"));
        if (!exactMessage) Assert.Contains(done.Events, e => e.Summary.Contains("Replaced unpublished " + original, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("mode")]
    [InlineData("labels-none")]
    [InlineData("labels-many")]
    [InlineData("branch")]
    [InlineData("remote")]
    [InlineData("two-commits")]
    [InlineData("published")]
    [InlineData("descendant")]
    [InlineData("dirty-local-commit")]
    [InlineData("merge")]
    public async Task Unsafe_preflight_never_performs_publication(string mutation)
    {
        await using var f = await SubmissionFixture.Create();
        string baseline = f.Data.State.WorkspaceIntent.BaseCommit;
        switch (mutation)
        {
            case "content": await File.WriteAllTextAsync(Path.Combine(f.Worktree, "file.txt"), "unreviewed"); break;
            case "mode": File.SetUnixFileMode(Path.Combine(f.Worktree, "file.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); break;
            case "labels-none": f.Workflow.Providers.Source = f.Workflow.Providers.Source with { Labels = ["Research"] }; break;
            case "labels-many": f.Workflow.Providers.Source = f.Workflow.Providers.Source with { Labels = ["Feature", "Bug"] }; break;
            case "branch": await f.Git(f.Worktree, "switch", "-c", "unexpected"); break;
            case "remote": await f.Git(f.Canonical, "remote", "set-url", "origin", "https://github.com/owner/wrong.git"); break;
            default:
                await f.Git(f.Worktree, "add", "."); await f.Git(f.Worktree, "commit", "-m", "local work");
                if (mutation == "two-commits") await f.Git(f.Worktree, "commit", "--allow-empty", "-m", "another commit");
                if (mutation == "published") await f.Git(f.Worktree, "push", f.Remote, "HEAD:refs/heads/published-elsewhere");
                if (mutation == "descendant")
                {
                    await f.Git(f.Worktree, "switch", "-c", "descendant"); await f.Git(f.Worktree, "commit", "--allow-empty", "-m", "descendant");
                    await f.Git(f.Worktree, "switch", f.Data.State.WorkspaceIntent.Branch);
                }
                if (mutation == "dirty-local-commit") await f.Git(f.Worktree, "read-tree", baseline);
                if (mutation == "merge")
                {
                    string tree = await f.Git(f.Worktree, "rev-parse", "HEAD^{tree}"); string head = await f.Git(f.Worktree, "rev-parse", "HEAD");
                    string merge = await f.Git(f.Worktree, "commit-tree", tree, "-p", head, "-p", baseline, "-m", "merge");
                    await f.Git(f.Worktree, "update-ref", "refs/heads/" + f.Data.State.WorkspaceIntent.Branch, merge, head);
                }
                break;
        }
        string observed = await f.Git(f.Worktree, "rev-parse", "HEAD");
        var failed = await f.Submit(); Assert.NotNull(failed.State.Recovery); Assert.Null(failed.State.Submission);
        Assert.Empty(f.Transport.Effects); Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
        Assert.Equal(observed, await f.Git(f.Worktree, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Frozen_content_drift_and_remote_divergence_recover_through_ordinary_pr()
    {
        await using var f = await SubmissionFixture.Create(); f.Transport.FailAfter = "ref";
        var failed = await f.Submit(); var intent = failed.State.Submission!.Intent;
        await File.WriteAllTextAsync(Path.Combine(f.Worktree, "file.txt"), "later work\n");
        var drift = await f.Submit(); Assert.Equal("frozen_intent_drift", drift.State.Recovery!.Code);
        Assert.DoesNotContain("push", f.Transport.Effects);
        await File.WriteAllTextAsync(Path.Combine(f.Worktree, "file.txt"), "reviewed change\n");
        await f.Git(f.Remote, "update-ref", "refs/heads/" + intent.Branch, intent.BaseCommit);
        var diverged = await f.Submit(true); Assert.Equal("remote_revision_changed", diverged.State.Recovery!.Code);
        Assert.Contains(intent.BaseCommit, diverged.State.Recovery.Explanation);
        await f.Git(f.Remote, "update-ref", "-d", "refs/heads/" + intent.Branch, intent.BaseCommit);
        var done = await f.Submit(); Assert.Null(done.State.Recovery); Assert.Equal(intent, done.State.Submission!.Intent);
    }

    [Fact]
    public async Task Branch_compare_and_swap_preserves_competing_commit()
    {
        await using var f = await SubmissionFixture.Create(); string? competing = null;
        f.Transport.Before = async args =>
        {
            if (args[0] != "update-ref") return;
            f.Transport.Before = null;
            string old = await f.Git(f.Worktree, "rev-parse", "HEAD");
            string tree = await f.Git(f.Worktree, "rev-parse", "HEAD^{tree}");
            competing = await f.Git(f.Worktree, "commit-tree", tree, "-p", old, "-m", "competing writer");
            await f.Git(f.Worktree, "update-ref", "refs/heads/" + f.Data.State.WorkspaceIntent.Branch, competing, old);
        };
        var failed = await f.Submit(); Assert.Equal("branch_update_race", failed.State.Recovery!.Code);
        Assert.Equal(competing, await f.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.DoesNotContain("push", f.Transport.Effects);
    }

    [Fact]
    public async Task Submission_intent_is_immutable_and_cleanup_cascades_its_records()
    {
        await using var f = await SubmissionFixture.Create(); var done = await f.Submit(); Assert.Null(done.State.Recovery);
        var state = done.State;
        Assert.Throws<InvalidOperationException>(() => f.Workflow.Store.Save(state.Version, new(state with
        { Version = state.Version + 1, Submission = state.Submission! with { Intent = state.Submission.Intent with { Title = "changed" } } },
            [new(0, "InvalidChange", "invalid", state.CurrentOperationId, null, DateTimeOffset.UtcNow)], [], [], [], [], [])));
        Assert.Equal(state.Version, f.Workflow.Application.Get(f.Id).State.Version);
        Assert.Equal(state.Submission, f.Workflow.Application.Get(f.Id).State.Submission);
        f.Workflow.Store.Delete(f.Id, state.Version);
        using var db = new SqliteConnection("Data Source=" + Path.Combine(f.Canonical, "workflows.db")); db.Open();
        using var query = db.CreateCommand(); query.CommandText = "SELECT count(*) FROM submissions";
        Assert.Equal(0L, query.ExecuteScalar());
    }
}
