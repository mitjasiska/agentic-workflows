using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class SubmissionSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Symbolic_ref_substitution_preserves_shared_base_and_competing_ref(bool replaceLocalCommit)
    {
        await using var f = await SubmissionFixture.Create();
        string baseline = f.Data.State.WorkspaceIntent.BaseCommit;
        if (replaceLocalCommit)
        {
            await f.Git(f.Worktree, "add", ".");
            await f.Git(f.Worktree, "commit", "-m", "local message");
        }
        string original = await f.Git(f.Worktree, "rev-parse", "HEAD");
        string reference = "refs/heads/" + f.Data.State.WorkspaceIntent.Branch;
        string indexPath = await f.Git(f.Worktree, "rev-parse", "--git-path", "index");
        byte[] index = await File.ReadAllBytesAsync(indexPath);
        f.Transport.Before = async args =>
        {
            if (args[0] != "update-ref") return;
            f.Transport.Before = null;
            await f.Git(f.Worktree, "symbolic-ref", reference, "refs/heads/main");
        };
        var refused = await f.Submit();
        Assert.Equal("branch_update_race", refused.State.Recovery!.Code);
        Assert.Equal(baseline, await f.Git(f.Canonical, "rev-parse", "main"));
        Assert.Equal("refs/heads/main", await f.Git(f.Worktree, "symbolic-ref", reference));
        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal("reviewed change\n", await File.ReadAllTextAsync(Path.Combine(f.Worktree, "file.txt")));
        Assert.DoesNotContain("ref", f.Transport.Effects);
        Assert.DoesNotContain("push", f.Transport.Effects);
        Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
        string lockPath = Path.Combine(refused.State.Binding.GitDirectory, reference + ".lock");
        Assert.False(File.Exists(lockPath));

        // Manual reconciliation restores the direct ref without changing the shared base.
        await f.Git(f.Worktree, "update-ref", "--no-deref", reference, original);
        var done = await f.Submit();
        Assert.Null(done.State.Recovery);
        Assert.Equal(refused.State.Submission!.Intent, done.State.Submission!.Intent);
        Assert.Equal(baseline, await f.Git(f.Canonical, "rev-parse", "main"));
    }

    [Fact]
    public async Task Prepared_transaction_holds_ref_lock_during_final_verification()
    {
        await using var f = await SubmissionFixture.Create();
        string reference = "refs/heads/" + f.Data.State.WorkspaceIntent.Branch;
        bool attempted = false;
        f.Transport.AfterPrepared = async () =>
        {
            attempted = true;
            var competing = await new ArgumentProcess().RunAsync("git", ["symbolic-ref", reference, "refs/heads/main"], f.Worktree, null, default);
            Assert.NotEqual(0, competing.ExitCode);
        };
        var done = await f.Submit();
        Assert.True(attempted); Assert.Null(done.State.Recovery);
        Assert.Equal(done.State.Submission!.Intent.Commit, await f.Git(f.Worktree, "rev-parse", reference));
        Assert.Equal(done.State.WorkspaceIntent.BaseCommit, await f.Git(f.Canonical, "rev-parse", "main"));
    }

    [Theory]
    [InlineData("cancel", "external_action_uncertain")]
    [InlineData("verify", "controlled_refusal")]
    [InlineData("acknowledgement", "process_prepare_failed")]
    public async Task Prepared_process_failure_aborts_without_ref_mutation_or_abandoned_lock(string failure, string code)
    {
        await using var f = await SubmissionFixture.Create();
        using var canceled = new CancellationTokenSource();
        string baseline = f.Data.State.WorkspaceIntent.BaseCommit;
        string reference = "refs/heads/" + f.Data.State.WorkspaceIntent.Branch;
        string next = await f.Git(f.Worktree, "commit-tree", f.Data.State.Review!.Target.ProposedTree, "-p", baseline, "-m", "transaction fixture");
        var input = new PreparedProcessInput($"start\nupdate {reference} {next} {baseline}\nprepare\n",
            failure == "acknowledgement" ? ["start: ok\n", "unexpected\n"] : ["start: ok\n", "prepare: ok\n"], token =>
            {
                if (failure == "cancel") { canceled.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new WorkflowException("controlled_refusal", "Verification refused.", "Retry after reconciliation.");
            }, "commit\n");
        var error = await Assert.ThrowsAsync<WorkflowException>(() => new ArgumentProcess().RunPreparedAsync("git",
            ["update-ref", "--no-deref", "--stdin"], f.Worktree, null, input, canceled.Token));
        Assert.Equal(code, error.Code);
        Assert.Equal(baseline, await f.Git(f.Worktree, "rev-parse", reference));
        Assert.False(File.Exists(Path.Combine(f.Data.State.Binding.GitDirectory, reference + ".lock")));
        Assert.Null((await f.Submit()).State.Recovery);
    }

    [Theory]
    [InlineData("refs/pull/17/head", false, false)]
    [InlineData("refs/pull/17/head", true, false)]
    [InlineData("refs/pull/17/merge", false, true)]
    [InlineData("refs/archive/retained", true, true)]
    public async Task Advertised_non_branch_remote_refs_prevent_local_commit_adoption_or_replacement(string reference, bool exactMessage, bool descendant)
    {
        await using var f = await SubmissionFixture.Create();
        string message = SubmissionMetadata.Create(f.Workflow.Providers.Source, f.Data.State.Review!.PublicMetadata,
            f.Data.State.Review.PassId, false).Message;
        await f.Git(f.Worktree, "add", ".");
        await f.Git(f.Worktree, "commit", "-m", exactMessage ? message : "previously published message");
        string original = await f.Git(f.Worktree, "rev-parse", "HEAD");
        await f.Git(f.Worktree, "push", f.Remote, "HEAD:" + reference);
        string remoteTip = original;
        if (descendant)
        {
            string tree = await f.Git(f.Remote, "rev-parse", original + "^{tree}");
            remoteTip = await f.Git(f.Remote, "-c", "user.name=Remote fixture", "-c", "user.email=remote@example.invalid",
                "commit-tree", tree, "-p", original, "-m", "retained remote descendant");
            await f.Git(f.Remote, "update-ref", reference, remoteTip, original);
        }
        string indexPath = await f.Git(f.Worktree, "rev-parse", "--git-path", "index");
        byte[] index = await File.ReadAllBytesAsync(indexPath);
        foreach (bool force in new[] { false, true })
        {
            var refused = await f.Submit(force);
            Assert.Equal("local_commit_published", refused.State.Recovery!.Code);
            Assert.Null(refused.State.Submission);
            Assert.Empty(f.Transport.Effects); Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
            Assert.Equal(original, await f.Git(f.Worktree, "rev-parse", "HEAD"));
            Assert.Equal(remoteTip, await f.Git(f.Remote, "rev-parse", reference));
            Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal("", await f.Git(f.Worktree, "status", "--porcelain=v1"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_merge_with_exact_reviewed_tree_is_preserved_until_manually_aborted(bool force)
    {
        await using var f = await SubmissionFixture.Create();
        var before = f.Data.State;
        string side = await f.Git(f.Worktree, "commit-tree", before.Review!.Target.ProposedTree,
            "-p", before.WorkspaceIntent.BaseCommit, "-m", "side change");
        await File.WriteAllTextAsync(Path.Combine(f.Worktree, "file.txt"), "baseline\n");
        await f.Git(f.Worktree, "merge", "--no-commit", "--no-ff", side);
        string mergePath = await f.Git(f.Worktree, "rev-parse", "--git-path", "MERGE_HEAD");
        string indexPath = await f.Git(f.Worktree, "rev-parse", "--git-path", "index");
        byte[] merge = await File.ReadAllBytesAsync(mergePath), index = await File.ReadAllBytesAsync(indexPath);
        var refused = await f.Submit(force);
        Assert.Equal("git_operation_in_progress", refused.State.Recovery!.Code);
        Assert.Contains("Finish or abort it manually", refused.State.Recovery.Explanation);
        Assert.Null(refused.State.Submission); Assert.Equal(before.Phase, refused.State.Phase);
        Assert.Empty(f.Transport.Effects); Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
        Assert.Equal(before.WorkspaceIntent.BaseCommit, await f.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.Equal(merge, await File.ReadAllBytesAsync(mergePath));
        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal("reviewed change\n", await File.ReadAllTextAsync(Path.Combine(f.Worktree, "file.txt")));
        // Git's own abort semantics still work; restoring the accepted change allows ordinary retry.
        await f.Git(f.Worktree, "merge", "--abort");
        await File.WriteAllTextAsync(Path.Combine(f.Worktree, "file.txt"), "reviewed change\n");
        Assert.Null((await f.Submit()).State.Recovery);
    }

    [Theory]
    [InlineData("CHERRY_PICK_HEAD", false)]
    [InlineData("REVERT_HEAD", false)]
    [InlineData("REBASE_HEAD", false)]
    [InlineData("sequencer", true)]
    [InlineData("rebase-merge", true)]
    [InlineData("rebase-apply", true)]
    [InlineData("BISECT_START", false)]
    public async Task Unfinished_operation_markers_refuse_normal_and_forced_preflight(string marker, bool directory)
    {
        await using var f = await SubmissionFixture.Create();
        string path = await f.Git(f.Worktree, "rev-parse", "--git-path", marker);
        if (directory) Directory.CreateDirectory(path);
        string evidence = directory ? Path.Combine(path, "retained-state") : path;
        string content = f.Data.State.WorkspaceIntent.BaseCommit + "\n";
        await File.WriteAllTextAsync(evidence, content);
        foreach (bool force in new[] { false, true })
        {
            var refused = await f.Submit(force);
            Assert.Equal("git_operation_in_progress", refused.State.Recovery!.Code);
            Assert.Contains(marker, refused.State.Recovery.Explanation);
            Assert.Null(refused.State.Submission);
            Assert.Empty(f.Transport.Effects); Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
            Assert.Equal(content, await File.ReadAllTextAsync(evidence));
            Assert.Equal(refused.State.WorkspaceIntent.BaseCommit, await f.Git(f.Worktree, "rev-parse", "HEAD"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unfinished_operation_after_intent_freeze_blocks_execution_and_releases_transaction(bool duringTransaction)
    {
        await using var f = await SubmissionFixture.Create();
        string marker = await f.Git(f.Worktree, "rev-parse", "--git-path", "CHERRY_PICK_HEAD");
        string baseline = f.Data.State.WorkspaceIntent.BaseCommit;
        if (duringTransaction)
            f.Transport.AfterPrepared = () => File.WriteAllTextAsync(marker, baseline + "\n");
        else
        {
            f.Transport.FailAfter = "commit";
            Assert.NotNull((await f.Submit()).State.Submission);
            await File.WriteAllTextAsync(marker, baseline + "\n");
        }
        var refused = await f.Submit(true);
        Assert.Equal("git_operation_in_progress", refused.State.Recovery!.Code);
        Assert.NotNull(refused.State.Submission);
        Assert.Equal(baseline, await f.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.DoesNotContain("ref", f.Transport.Effects); Assert.DoesNotContain("push", f.Transport.Effects);
        Assert.DoesNotContain("pr", f.Workflow.Providers.Calls);
        Assert.Equal(baseline + "\n", await File.ReadAllTextAsync(marker));
        f.Transport.AfterPrepared = null;
        File.Delete(marker); // Simulated operation completed by its owner.
        var done = await f.Submit(duringTransaction);
        Assert.Null(done.State.Recovery); Assert.Equal(refused.State.Submission.Intent, done.State.Submission!.Intent);
    }
}
