using System.Security.Cryptography;
using System.Text;
using AgenticWorkflows.Application;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class GitReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_clean_normalization_hidden_drift_revokes_current_readiness(bool cleanFilter)
    {
        using var repository = await Repository.Create();
        await File.WriteAllTextAsync(Path.Combine(repository.Root, ".gitattributes"), cleanFilter ? "*.txt filter=canonical\n" : "*.txt text eol=lf\n");
        if (cleanFilter) await repository.Git("config", "filter.canonical.clean", "tr -d '\\r'");
        string file = Path.Combine(repository.Root, "file.txt");
        await File.WriteAllTextAsync(file, "baseline\n");
        await repository.Git("add", "--all"); await repository.Git("commit", "-m", "fixture");
        string baseline = await repository.Git("rev-parse", "HEAD");
        await File.WriteAllTextAsync(file, "reviewed\n");
        await using var workflow = new WorkflowFixture();
        workflow.Providers.CaptureOverride = token => repository.Adapter.CaptureAsync(repository.Workspace, baseline, token);
        Guid id = await workflow.Start(); await workflow.Pass(id, 1);
        await workflow.Deliver(id, WorkflowFixture.Ready()); await workflow.Pass(id, 2);
        await workflow.Deliver(id, WorkflowFixture.Clean());
        var completed = await workflow.Until(id, data => data.State.Phase == WorkflowPhase.Submission);
        var queries = new WorkflowQueries(workflow.Application, workflow.Providers);
        Assert.Equal("eligible", (await queries.Summary(id)).SubmissionReadiness);
        await File.WriteAllTextAsync(file, "reviewed\r\n");
        var current = await repository.Adapter.CaptureAsync(repository.Workspace, baseline, default);
        Assert.Equal(completed.State.Review!.Target, current.Target);
        Assert.NotEqual(completed.Passes[1].ReviewCheckpoint!.WorktreeIdentity, current.WorktreeIdentity);
        var summary = await queries.Summary(id);
        Assert.False(summary.ReviewValid); Assert.StartsWith("ineligible:", summary.SubmissionReadiness);
        await workflow.Restart();
        summary = await new WorkflowQueries(workflow.Application, workflow.Providers).Summary(id);
        Assert.False(summary.ReviewValid); Assert.StartsWith("ineligible:", summary.SubmissionReadiness);
        Assert.True(workflow.Application.Get(id).ReviewReports.Single().AuthorityValid); // Historical evidence is unchanged.
    }

    [Fact]
    public async Task Post_clean_staging_and_identical_tree_commit_preserve_readiness_including_deletions()
    {
        using var repository = await Repository.Create();
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "deleted.txt"), "baseline\n");
        await repository.Git("add", "--all"); await repository.Git("commit", "-m", "fixture");
        string baseline = await repository.Git("rev-parse", "HEAD");
        File.Delete(Path.Combine(repository.Root, "deleted.txt"));
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "added.txt"), "reviewed\n");
        await using var workflow = new WorkflowFixture();
        workflow.Providers.CaptureOverride = token => repository.Adapter.CaptureAsync(repository.Workspace, baseline, token);
        Guid id = await workflow.Start(); await workflow.Pass(id, 1);
        await workflow.Deliver(id, WorkflowFixture.Ready()); await workflow.Pass(id, 2);
        await workflow.Deliver(id, WorkflowFixture.Clean());
        await workflow.Until(id, data => data.State.Phase == WorkflowPhase.Submission);
        var queries = new WorkflowQueries(workflow.Application, workflow.Providers);
        Assert.Equal("eligible", (await queries.Summary(id)).SubmissionReadiness);
        await repository.Git("add", "--all");
        Assert.Equal("eligible", (await queries.Summary(id)).SubmissionReadiness);
        await repository.Git("restore", "--staged", ".");
        Assert.Equal("eligible", (await queries.Summary(id)).SubmissionReadiness);
        await repository.Git("add", "--all"); await repository.Git("commit", "-m", "identical reviewed tree fixture");
        Assert.NotEqual(baseline, await repository.Git("rev-parse", "HEAD"));
        var summary = await queries.Summary(id);
        Assert.True(summary.ReviewValid); Assert.Equal("eligible", summary.SubmissionReadiness);
    }

    [Theory]
    [InlineData("modified", false)]
    [InlineData("staged", false)]
    [InlineData("untracked", false)]
    [InlineData("modified", true)]
    public async Task Dirty_proposed_nested_gitlinks_invalidate_review_authority(string mutation, bool stagedGitlink)
    {
        using var repository = await Repository.Create();
        await repository.Git("commit", "--allow-empty", "-m", "parent fixture");
        string baseline = await repository.Git("rev-parse", "HEAD");
        string nestedPath = stagedGitlink ? "nested" : "new nested repo";
        using var nested = await Repository.Create(Path.Combine(repository.Root, nestedPath));
        await File.WriteAllTextAsync(Path.Combine(nested.Root, "file.txt"), "nested baseline\n");
        await nested.Git("add", "--all"); await nested.Git("commit", "-m", "nested fixture");
        if (stagedGitlink) await repository.Git("add", "--", nestedPath);
        byte[] nestedIndex = await File.ReadAllBytesAsync(Path.Combine(nested.Root, ".git/index"));
        var target = await repository.Adapter.CaptureAsync(repository.Workspace, baseline, default);
        Assert.Equal(nestedIndex, await File.ReadAllBytesAsync(Path.Combine(nested.Root, ".git/index")));
        Assert.StartsWith("160000 commit", await repository.Git("ls-tree", target.Target.ProposedTree, "--", nestedPath));
        await using var workflow = new WorkflowFixture();
        workflow.Providers.CaptureOverride = token => repository.Adapter.CaptureAsync(repository.Workspace, baseline, token);
        Guid id = await workflow.Start(); await workflow.Pass(id, 1);
        await workflow.Deliver(id, WorkflowFixture.Ready()); await workflow.Pass(id, 2);
        await File.WriteAllTextAsync(Path.Combine(nested.Root, mutation == "untracked" ? "new.txt" : "file.txt"), "unreviewed nested work\n");
        if (mutation == "staged") await nested.Git("add", "--all");
        Assert.Equal("dirty_submodule", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.CaptureAsync(repository.Workspace, baseline, default))).Code);
        await workflow.Deliver(id, WorkflowFixture.Clean());
        var completed = await workflow.Until(id, data => data.State.Review is not null);
        Assert.False(completed.State.Review!.AuthorityValid);
        Assert.Contains("dirty_submodule", completed.State.Review.InvalidationReason);
        Assert.Equal(WorkflowPhase.Review, completed.State.Phase);
        Assert.Equal("Clean", completed.ReviewReports.Single().Outcome);
        await workflow.Restart();
        Assert.False(workflow.Application.Get(id).State.Review!.AuthorityValid);
    }

    [Fact]
    public async Task Git_hidden_nested_bytes_are_part_of_materialized_review_evidence()
    {
        using var repository = await Repository.Create();
        await repository.Git("commit", "--allow-empty", "-m", "parent fixture");
        string baseline = await repository.Git("rev-parse", "HEAD");
        using var nested = await Repository.Create(Path.Combine(repository.Root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(nested.Root, ".gitattributes"), "*.txt text eol=lf\n");
        await File.WriteAllTextAsync(Path.Combine(nested.Root, "file.txt"), "line\n");
        await nested.Git("add", "--all"); await nested.Git("commit", "-m", "nested fixture");
        await nested.Git("update-index", "--assume-unchanged", "file.txt");
        var before = await repository.Adapter.CaptureAsync(repository.Workspace, baseline, default);
        await File.WriteAllTextAsync(Path.Combine(nested.Root, "file.txt"), "line\r\n");
        Assert.Empty(await nested.Git("status", "--porcelain=v1", "--untracked-files=all"));
        var after = await repository.Adapter.CaptureAsync(repository.Workspace, baseline, default);
        Assert.Equal(before.Target, after.Target); Assert.Equal(before.IndexIdentity, after.IndexIdentity);
        Assert.NotEqual(before.WorktreeIdentity, after.WorktreeIdentity);
        Assert.False(before.SameReviewState(after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_cleanup_finishes_with_partial_report_when_worktree_and_branch_are_absent(bool teardownFails)
    {
        using var repository = await Repository.Create();
        await repository.Git("commit", "--allow-empty", "-m", "canonical fixture");
        string head = await repository.Git("rev-parse", "HEAD");
        var identity = await repository.Adapter.InspectCanonicalAsync(repository.Root, default);
        var intent = new WorkspaceIntent(Guid.NewGuid(), repository.Root, identity.GitDirectory, head, "aw/DEV-123/whole",
            Path.Combine(repository.Root, "missing-worktree"), "local:test", [], DateTimeOffset.UtcNow, identity.Stamp);
        var workspace = new WorkspaceIdentity(intent.HerdrTarget, "owned", intent.ManagedPath, intent.Branch, identity.GitDirectory);
        Assert.Equal("worktree_absent", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.VerifyWorkspaceAsync(intent, workspace, default))).Code);
        Assert.Equal("branch_absent", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.ReadBranchTipAsync(intent, default))).Code);
        await using var workflow = new WorkflowFixture();
        Guid id = await workflow.Start(); await workflow.Pass(id, 1);
        workflow.Providers.RepositoryFailure = "worktree_absent";
        workflow.Providers.BranchTipOverride = token => repository.Adapter.ReadBranchTipAsync(intent, token);
        if (teardownFails) workflow.Providers.TeardownFailure = "herdr_unreachable";
        var result = await workflow.Application.ExecuteAsync(new("cleanup", workflow.Root, id, Force: true));
        Assert.Equal("cleanup_partial", result.Message);
        Assert.Single(workflow.Providers.Calls, call => call == "teardown");
        Assert.DoesNotContain("delete-branch", workflow.Providers.Calls);
        Assert.Contains(result.ComponentOutcomes!, text => text.Contains("branch is positively absent", StringComparison.Ordinal));
        if (teardownFails) Assert.Contains(result.ComponentOutcomes!, text => text.Contains("herdr_unreachable", StringComparison.Ordinal));
        Assert.Null(workflow.Store.Find(id));
        Assert.Equal(head, await repository.Git("rev-parse", "main"));
        Assert.Equal("refs/heads/main", await repository.Git("for-each-ref", "--format=%(refname)", "refs/heads/"));
    }

    [Fact]
    public async Task Branch_inspection_rejects_symbolic_replacement_and_repository_identity_mismatch()
    {
        using var repository = await Repository.Create();
        await repository.Git("commit", "--allow-empty", "-m", "canonical fixture");
        var identity = await repository.Adapter.InspectCanonicalAsync(repository.Root, default);
        var intent = new WorkspaceIntent(Guid.NewGuid(), repository.Root, identity.GitDirectory, "base", "aw/DEV-123/whole",
            Path.Combine(repository.Root, "missing-worktree"), "local:test", [], DateTimeOffset.UtcNow, identity.Stamp);
        await repository.Git("branch", intent.Branch);
        Assert.Equal(await repository.Git("rev-parse", "HEAD"), await repository.Adapter.ReadBranchTipAsync(intent, default));
        await repository.Git("symbolic-ref", "refs/heads/" + intent.Branch, "refs/heads/main");
        Assert.Equal("branch_identity_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.ReadBranchTipAsync(intent, default))).Code);
        await repository.Git("symbolic-ref", "refs/heads/" + intent.Branch, "refs/heads/absent-target");
        Assert.Equal("branch_identity_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.ReadBranchTipAsync(intent, default))).Code);
        Assert.Equal("repository_identity_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.ReadBranchTipAsync(intent with { RepositoryStamp = "replaced" }, default))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_utf8_guidance_fails_durably_and_resume_works_after_correction(bool tracked)
    {
        using var repository = await Repository.Create();
        string path = Path.Combine(repository.Root, "AGENTS.md");
        await repository.Git("commit", "--allow-empty", "-m", "canonical fixture");
        await File.WriteAllBytesAsync(path, [0x61, 0xC3, 0x28]);
        if (tracked) await repository.Git("add", "AGENTS.md");
        Assert.Equal("guidance_invalid_utf8", (await Assert.ThrowsAsync<WorkflowException>(() => repository.Adapter.ReadGuidanceAsync(repository.Workspace, default))).Code);
        await using var workflow = new WorkflowFixture();
        workflow.Providers.GuidanceOverride = token => repository.Adapter.ReadGuidanceAsync(repository.Workspace, token);
        Guid id = await workflow.Start();
        var failed = await workflow.Until(id, data => data.State.Recovery?.Code == "guidance_invalid_utf8");
        var operation = failed.Operations.Single(item => item.Id == failed.State.CurrentOperationId);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("guidance_invalid_utf8", operation.Recovery!.Code);
        Assert.Contains("encoding", operation.Recovery.Explanation);
        Assert.Empty(failed.Passes); Assert.Equal(0, workflow.Providers.Launches);
        await workflow.Restart();
        Assert.Equal("guidance_invalid_utf8", workflow.Application.Get(id).State.Recovery!.Code);
        await File.WriteAllTextAsync(path, "Corrected UTF-8 guidance.\n");
        await workflow.Application.ExecuteAsync(new("resume", workflow.Root, id));
        var resumed = await workflow.Pass(id, 1);
        Assert.Equal(operation.Id, resumed.Operations.Last().PredecessorId);
        Assert.Equal(OperationStatus.Failed, resumed.Operations.Single(item => item.Id == operation.Id).Status);
        Assert.Contains("Corrected UTF-8 guidance.", resumed.Passes.Single().Handoff.Text);
        Assert.Null(resumed.State.Recovery); Assert.Equal(1, workflow.Providers.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialized_byte_mutation_invalidates_review_even_when_git_normalizes_it(bool cleanFilter)
    {
        using var f = await Repository.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Root, ".gitattributes"), cleanFilter ? "*.txt filter=canonical\n" : "*.txt text eol=lf\n");
        if (cleanFilter) await f.Git("config", "filter.canonical.clean", "tr -d '\\r'");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "file.txt"), "line\n");
        await f.Git("add", "--all"); await f.Git("commit", "-m", "fixture");
        string baseline = await f.Git("rev-parse", "HEAD");
        var before = await f.Adapter.CaptureAsync(f.Workspace, baseline, default);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "file.txt"), "line\r\n");
        var after = await f.Adapter.CaptureAsync(f.Workspace, baseline, default);
        Assert.Equal(before.Target, after.Target);
        Assert.Equal(before.Head, after.Head); Assert.Equal(before.IndexIdentity, after.IndexIdentity);
        Assert.NotEqual(before.WorktreeIdentity, after.WorktreeIdentity);
        Assert.False(before.SameReviewState(after));
    }

    [Fact]
    public async Task Force_staged_ignored_additions_are_reviewed_and_count_as_unique_work_without_index_mutation()
    {
        using var f = await Repository.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Root, ".gitignore"), "ignored.txt\n");
        await f.Git("add", "--all"); await f.Git("commit", "-m", "fixture");
        string baseline = await f.Git("rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "ignored.txt"), "unique work\n");
        await f.Git("add", "--force", "ignored.txt");
        byte[] index = await File.ReadAllBytesAsync(Path.Combine(f.Root, ".git/index"));
        var captured = await f.Adapter.CaptureAsync(f.Workspace, baseline, default);
        Assert.Contains("ignored.txt", captured.ChangedPaths);
        Assert.Equal("unique work", await f.Git("show", captured.Target.ProposedTree + ":ignored.txt"));
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(f.Root, ".git/index")));
        Assert.True(await f.Adapter.HasUniqueWorkAsync(f.Workspace, baseline, "https://github.com/example/isolated.git", default));
    }

    [Fact]
    public async Task Untracked_and_edited_guidance_records_actual_content_and_revision_in_handoff()
    {
        using var f = await Repository.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "README.md"), "baseline guidance\n");
        await f.Git("add", "--all"); await f.Git("commit", "-m", "fixture");
        Directory.CreateDirectory(Path.Combine(f.Root, "new-feature"));
        const string instruction = "Use the established transaction boundary.\n";
        await File.WriteAllTextAsync(Path.Combine(f.Root, "new-feature/AGENTS.md"), instruction);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "README.md"), "edited guidance\n");
        var guidance = await f.Adapter.ReadGuidanceAsync(f.Workspace, default);
        Assert.Equal(await f.Git("rev-parse", "HEAD"), guidance.Revision);
        Assert.Equal(new[] { "README.md", "new-feature/AGENTS.md" }, guidance.Paths);
        var nested = Assert.Single(guidance.Files!, file => file.Path == "new-feature/AGENTS.md");
        Assert.Equal(instruction, nested.Text);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instruction))), nested.ContentIdentity);
        await using var workflow = new WorkflowFixture();
        Guid id = await workflow.Start(); var data = await workflow.Pass(id, 1);
        var pass = data.Passes[0];
        var handoff = workflow.Delivery.Compose(data.State, data.Snapshots[0], pass.Id, pass.OperationId, pass.Role,
            pass.Configuration, guidance, null, [], null, pass.DeliveryPath);
        Assert.Contains(nested.ContentIdentity, handoff.Text); Assert.Contains("Use the established transaction boundary.", handoff.Text);
        Assert.Contains("edited guidance", handoff.Text);
    }

    [Fact]
    public async Task Missing_worktree_is_distinct_from_replaced_repository_or_path()
    {
        using var f = await Repository.Create();
        var canonical = await f.Adapter.InspectCanonicalAsync(f.Root, default);
        string path = Path.Combine(f.Root, "missing-worktree");
        var intent = new WorkspaceIntent(Guid.NewGuid(), f.Root, canonical.GitDirectory, "base", "aw/DEV-1/whole", path, "local:test", [], DateTimeOffset.UtcNow, canonical.Stamp);
        var workspace = new WorkspaceIdentity("local:test", "owned", path, intent.Branch, canonical.GitDirectory);
        Assert.Equal("worktree_absent", (await Assert.ThrowsAsync<WorkflowException>(() => f.Adapter.VerifyWorkspaceAsync(intent, workspace, default))).Code);
        await File.WriteAllTextAsync(path, "replacement");
        Assert.Equal("workspace_identity_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => f.Adapter.VerifyWorkspaceAsync(intent, workspace, default))).Code);
        File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(f.Root, "absent"));
        Assert.Equal("symlink_path", (await Assert.ThrowsAsync<WorkflowException>(() => f.Adapter.VerifyWorkspaceAsync(intent, workspace, default))).Code);
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; }
        private readonly ArgumentProcess process = new();
        internal GitWorkflowRepository Adapter { get; }
        internal WorkspaceIdentity Workspace => new("local:test", "owned", Root, "main", Path.Combine(Root, ".git"));
        private Repository(string? root) { Root = root ?? Path.Combine(Path.GetTempPath(), "aw-review-regression-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Adapter = new(process); }
        internal static async Task<Repository> Create(string? root = null)
        {
            var result = new Repository(root);
            await result.Git("init", "-b", "main"); await result.Git("config", "user.name", "AW test");
            await result.Git("config", "user.email", "test@example.invalid");
            await result.Git("remote", "add", "origin", "https://github.com/example/isolated.git");
            return result;
        }
        internal async Task<string> Git(params string[] arguments)
        {
            var result = await process.RunAsync("git", arguments, Root, null, default);
            Assert.Equal(0, result.ExitCode); return result.Output.Trim();
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
