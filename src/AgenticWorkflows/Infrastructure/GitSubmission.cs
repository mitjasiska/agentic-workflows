using System.Text;
using System.Text.RegularExpressions;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed partial class GitWorkflowRepository
{
    public async Task<SubmissionCommit> PrepareSubmissionAsync(WorkflowState state, RepositoryCheckpoint checkpoint,
        string message, CancellationToken cancellationToken)
    {
        string cwd = state.Workspace!.Path;
        string baseBranch = BaseBranch(state.WorkspaceIntent.BaseReference);
        await SubmissionIdentity(state, cancellationToken);
        var refs = await RemoteRefs(state, cancellationToken);
        if (refs.TryGetValue("refs/heads/" + state.WorkspaceIntent.Branch, out var remoteHead))
            throw SubmissionError("remote_revision_changed", $"Expected absent Workflow remote branch; observed {remoteHead}. Reconcile the remote branch manually.");
        await CheckBase(state, baseBranch, checkpoint.Target.ProposedTree, refs, cancellationToken);
        string? content = null;
        string commit;
        if (checkpoint.Head != state.WorkspaceIntent.BaseCommit)
        {
            await SafeLocalCommit(state, checkpoint, refs, cancellationToken);
            string existing = await CommitMessage(cwd, checkpoint.Head, cancellationToken);
            if (existing == message)
            {
                if (!checkpoint.SameReviewState(await CaptureAsync(state.Workspace, state.WorkspaceIntent.BaseCommit, cancellationToken)))
                    throw SubmissionError("moving_submission_target", "Local commit state moved during preflight; no intent was frozen.");
                return new(baseBranch, checkpoint.Head, null);
            }
        }
        string author = await Git(cwd, ["var", "GIT_AUTHOR_IDENT"], cancellationToken);
        string committer = await Git(cwd, ["var", "GIT_COMMITTER_IDENT"], cancellationToken);
        if (!ValidIdent(author) || !ValidIdent(committer))
            throw SubmissionError("git_identity_invalid", "Configure a valid Git author and committer identity for the service account.");
        content = $"tree {checkpoint.Target.ProposedTree}\nparent {state.WorkspaceIntent.BaseCommit}\nauthor {author}\ncommitter {committer}\n\n{message}";
        // hash-object without -w computes identity only. The frozen bytes make a lost commit response reconcilable.
        commit = await HashCommit(cwd, content, false, cancellationToken);
        var again = await CaptureAsync(state.Workspace, state.WorkspaceIntent.BaseCommit, cancellationToken);
        if (!checkpoint.SameReviewState(again)) throw SubmissionError("moving_submission_target", "The proposed Git state moved during preflight; no intent was frozen.");
        return new(baseBranch, commit, content);
    }

    public async Task VerifySubmissionAsync(WorkflowState state, PrIntent intent, CancellationToken cancellationToken)
    {
        await SubmissionIdentity(state, cancellationToken);
        if (intent.Binding != state.Binding || intent.WorkflowId != state.Id || intent.Branch != state.WorkspaceIntent.Branch ||
            intent.BaseCommit != state.WorkspaceIntent.BaseCommit || intent.BaseBranch != BaseBranch(state.WorkspaceIntent.BaseReference))
            throw SubmissionError("submission_identity_mismatch", "The frozen intent differs from the bound Workflow repository, branch, or base.");
        var checkpoint = await CaptureAsync(state.Workspace!, intent.BaseCommit, cancellationToken);
        if (checkpoint.Target != intent.Target || checkpoint.WorktreeIdentity != intent.WorktreeIdentity)
            throw SubmissionError("frozen_intent_drift", $"Expected tree {intent.Target.ProposedTree}; observed {checkpoint.Target.ProposedTree}, or materialized bytes/modes changed. Preserve later work separately and restore the frozen content.");
        if (checkpoint.Head != intent.OriginalHead && checkpoint.Head != intent.Commit)
            throw SubmissionError("local_revision_changed", $"Expected local HEAD {intent.OriginalHead} or {intent.Commit}; observed {checkpoint.Head}. Preserve and reconcile the unexpected history manually.");
        var refs = await RemoteRefs(state, cancellationToken);
        refs.TryGetValue("refs/heads/" + intent.Branch, out string? remote);
        if (state.Submission?.PushedRevision is not null && remote is null)
            throw SubmissionError("remote_revision_changed", $"Expected previously verified pushed revision {intent.Commit}; observed absent branch. Restore or reconcile the remote manually; completed submission does not recreate deleted branches.");
        if (remote is not null && remote != intent.Commit)
            throw SubmissionError("remote_revision_changed", $"Expected absent remote branch or {intent.Commit}; observed {remote}. Fetch and reconcile the divergence manually; AW never force-pushes.");
        await CheckBase(state, intent.BaseBranch, intent.Target.ProposedTree, refs, cancellationToken);
        if (checkpoint.Head == intent.Commit)
        {
            await VerifyCommit(state.Workspace!.Path, intent, cancellationToken);
            return;
        }
        if (checkpoint.Head != intent.BaseCommit)
            await SafeLocalCommit(state, checkpoint, refs, cancellationToken);
    }

    public async Task CommitSubmissionAsync(WorkflowState state, PrIntent intent, CancellationToken cancellationToken)
    {
        await VerifySubmissionAsync(state, intent, cancellationToken);
        string cwd = state.Workspace!.Path;
        string head = await ReadBranchTipAsync(state.WorkspaceIntent, cancellationToken);
        if (head == intent.Commit) { await VerifyCommit(cwd, intent, cancellationToken); return; }
        if (head != intent.OriginalHead || intent.CommitObject is null)
            throw SubmissionError("local_revision_changed", $"Expected original branch {intent.OriginalHead}; observed {head}.");
        string created = await HashCommit(cwd, intent.CommitObject, true, cancellationToken);
        if (created != intent.Commit) throw SubmissionError("commit_identity_mismatch", "Frozen commit bytes did not produce the authorized object identity.");
        await VerifyCommit(cwd, intent, cancellationToken);
        // Recheck after object creation and immediately before ref replacement. No index/worktree writes.
        await VerifySubmissionAsync(state, intent, cancellationToken);
        string reference = "refs/heads/" + intent.Branch;
        ProcessOutput result;
        try
        {
            // --no-deref confines writes to the Workflow ref. It alone would still replace a
            // racing symref, so verify the direct ref while Git holds its prepared transaction lock.
            result = await process.RunPreparedAsync("git", ["update-ref", "--no-deref", "-m", "AW submission " + intent.Id, "--stdin"],
                cwd, ReadEnvironment, new($"start\nupdate {reference} {intent.Commit} {intent.OriginalHead}\nprepare\n",
                    ["start: ok\n", "prepare: ok\n"], async token =>
                    {
                        if ((await Run(cwd, ["symbolic-ref", "--quiet", reference], token)).ExitCode != 1 ||
                            await Git(cwd, ["rev-parse", reference], token) != intent.OriginalHead)
                            throw SubmissionError("branch_update_race", "The locked Workflow ref is no longer the expected direct ref. The competing ref and shared base were preserved.");
                        await VerifySubmissionAsync(state, intent, token);
                    }, "commit\n"), cancellationToken);
        }
        catch (WorkflowException e) when (e.Code == "process_prepare_failed")
        { throw SubmissionError("branch_update_race", $"The branch transaction could not lock and verify expected revision {intent.OriginalHead}. All competing work is preserved."); }
        if (result.ExitCode != 0) throw SubmissionError("branch_update_race", $"Atomic branch update expected {intent.OriginalHead}; another writer changed the ref. All competing work is preserved.");
        await VerifySubmissionAsync(state, intent, cancellationToken);
    }

    public async Task PushSubmissionAsync(WorkflowState state, PrIntent intent, CancellationToken cancellationToken)
    {
        await VerifySubmissionAsync(state, intent, cancellationToken);
        if (await ReadBranchTipAsync(state.WorkspaceIntent, cancellationToken) != intent.Commit)
            throw SubmissionError("commit_not_installed", "The exact frozen commit is not the Workflow branch tip.");
        var refs = await RemoteRefs(state, cancellationToken);
        string reference = "refs/heads/" + intent.Branch;
        if (refs.GetValueOrDefault(reference) == intent.Commit) return;
        // An empty expected value is a create-only lease: the remote ref MUST be absent.
        // This cannot replace any existing revision, even a fast-forwardable ancestor that races
        // the preflight. It is independent of the operator's review-policy --force flag.
        // Never use a nonempty lease, a '+' refspec, --force, or a default push mapping here.
        var result = await Run(state.Workspace!.Path, ["-c", "push.followTags=false", "push", "--porcelain", "--no-verify", "--recurse-submodules=no", "--signed=false",
            "--force-with-lease=" + reference + ":", intent.Binding.RemoteUrl, intent.Commit + ":" + reference], cancellationToken);
        var observed = (await RemoteRefs(state, cancellationToken)).GetValueOrDefault(reference);
        if (observed != intent.Commit)
            throw SubmissionError("push_unconfirmed", $"Expected pushed revision {intent.Commit}; observed {observed ?? "absent"}. Push exit status {result.ExitCode}; reconcile before retry.");
        await VerifySubmissionAsync(state, intent, cancellationToken);
    }

    private async Task SubmissionIdentity(WorkflowState state, CancellationToken token)
    {
        await VerifyWorkspaceAsync(state.WorkspaceIntent, state.Workspace!, token);
        string cwd = state.Workspace!.Path;
        string gitDirectory = await Git(cwd, ["rev-parse", "--absolute-git-dir"], token);
        foreach (string marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "sequencer", "rebase-merge", "rebase-apply", "BISECT_START" })
            if (Path.Exists(Path.Combine(gitDirectory, marker)))
                throw SubmissionError("git_operation_in_progress", $"Unfinished Git operation ({marker}) in the Workflow worktree. Finish or abort it manually, then rerun awctl pr; force cannot bypass this protection.");
        string fetch = await Git(cwd, ["remote", "get-url", "--all", "origin"], token);
        string push = await Git(cwd, ["remote", "get-url", "--push", "--all", "origin"], token);
        if (fetch != state.Binding.RemoteUrl || push != state.Binding.RemoteUrl)
            throw SubmissionError("remote_identity_changed", "The origin fetch/push URLs no longer equal the immutable credential-free remote binding.");
        // URL rewrites can send an explicit URL to another repository. Refuse them rather than infer intent.
        var rewrites = await Run(cwd, ["config", "--get-regexp", @"^url\..*\.(insteadof|pushinsteadof)$"], token);
        if (rewrites.ExitCode != 1) throw SubmissionError("remote_rewrite_unproven", "Git URL rewriting is configured or could not be inspected. Use an unambiguous bound Git transport.");
        if (await Git(cwd, ["rev-parse", "--is-shallow-repository"], token) != "false" || File.Exists(Path.Combine(state.Binding.GitDirectory, "info", "grafts")))
            throw SubmissionError("history_unproven", "Shallow or grafted history cannot prove safe publication ancestry.");
        if (await ReadBranchTipAsync(state.WorkspaceIntent, token) != await Git(cwd, ["rev-parse", "HEAD"], token))
            throw SubmissionError("branch_identity_mismatch", "The checked-out HEAD differs from the exact direct Workflow branch ref.");
    }

    private async Task<Dictionary<string, string>> RemoteRefs(WorkflowState state, CancellationToken token)
    {
        // Include all advertised refs, notably GitHub refs/pull/* after their source branch was deleted.
        var output = await Git(state.Workspace!.Path, ["ls-remote", state.Binding.RemoteUrl], token);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length != 2 || !Regex.IsMatch(fields[0], @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$") || !refs.TryAdd(fields[1], fields[0]))
                throw SubmissionError("remote_state_ambiguous", "Remote refs did not provide unique exact object identities.");
        }
        return refs;
    }

    private async Task CheckBase(WorkflowState state, string branch, string proposed, Dictionary<string, string> refs, CancellationToken token)
    {
        if (!refs.TryGetValue("refs/heads/" + branch, out string? tip))
            throw SubmissionError("base_missing", "The bound GitHub base branch is absent from the remote.");
        string cwd = state.Workspace!.Path, baseline = state.WorkspaceIntent.BaseCommit;
        if (tip == baseline) return;
        await FetchObject(state, tip, token);
        if ((await Run(cwd, ["merge-base", "--is-ancestor", baseline, tip], token)).ExitCode != 0)
            throw SubmissionError("base_ancestry_changed", $"Expected base descended from {baseline}; observed {tip}. Reconcile the base manually.");
        // A private index proves non-overlapping advanced-base changes without committing or touching files.
        string index = Path.Combine(Path.GetTempPath(), "aw-base-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            var environment = new Dictionary<string, string?>(ReadEnvironment) { ["GIT_INDEX_FILE"] = index };
            var merged = await Run(cwd, ["read-tree", "-m", baseline, tip, proposed], token, environment);
            if (merged.ExitCode != 0 || (await Git(cwd, ["ls-files", "--unmerged"], token, environment)).Length != 0)
                throw SubmissionError("base_conflict", $"Advanced base {tip} overlaps the frozen change from {baseline}. Reconcile manually; AW does not merge, rebase, or launch integration.");
        }
        finally { File.Delete(index); File.Delete(index + ".lock"); }
    }

    private async Task SafeLocalCommit(WorkflowState state, RepositoryCheckpoint checkpoint, Dictionary<string, string> refs, CancellationToken token)
    {
        string cwd = state.Workspace!.Path;
        string parents = await Git(cwd, ["show", "-s", "--format=%P", checkpoint.Head], token);
        string tree = await Git(cwd, ["rev-parse", checkpoint.Head + "^{tree}"], token);
        if (parents != state.WorkspaceIntent.BaseCommit || tree != checkpoint.Target.ProposedTree ||
            (await Git(cwd, ["status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none"], token)).Length != 0)
            throw SubmissionError("unsafe_local_history", "Local HEAD must be clean, non-merge, exactly one direct child of the expected base, and contain the frozen tree.");
        string containing = await Git(cwd, ["for-each-ref", "--contains=" + checkpoint.Head, "--format=%(refname)"], token);
        if (containing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(r => r != "refs/heads/" + state.WorkspaceIntent.Branch))
            throw SubmissionError("local_descendant", "Another local ref contains the local commit; adoption or replacement cannot prove the narrow unpublished history boundary.");
        foreach (string tip in refs.Values.Distinct())
        {
            await FetchObject(state, tip, token);
            var ancestry = await Run(cwd, ["merge-base", "--is-ancestor", checkpoint.Head, tip], token);
            if (ancestry.ExitCode != 1)
                throw SubmissionError("local_commit_published", "The local commit is reachable from the remote, or its unpublished status cannot be proven. No replacement or adoption occurred.");
        }
    }

    private async Task FetchObject(WorkflowState state, string tip, CancellationToken token)
    {
        if ((await Run(state.Workspace!.Path, ["cat-file", "-e", tip + "^{commit}"], token)).ExitCode == 0) return;
        await Git(state.Workspace.Path, ["fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", state.Binding.RemoteUrl, tip], token);
    }
    private async Task VerifyCommit(string cwd, PrIntent intent, CancellationToken token)
    {
        if (await Git(cwd, ["show", "-s", "--format=%P", intent.Commit], token) != intent.BaseCommit ||
            await Git(cwd, ["rev-parse", intent.Commit + "^{tree}"], token) != intent.Target.ProposedTree ||
            await CommitMessage(cwd, intent.Commit, token) != intent.CommitMessage)
            throw SubmissionError("commit_mismatch", "The observed commit differs from the frozen tree, sole parent, or complete public message.");
    }
    private async Task<string> CommitMessage(string cwd, string commit, CancellationToken token)
    {
        var raw = await Run(cwd, ["cat-file", "commit", commit], token);
        int split = raw.Output.IndexOf("\n\n", StringComparison.Ordinal);
        if (raw.ExitCode != 0 || split < 0)
            throw SubmissionError("commit_unreadable", "Git could not read the complete commit object.");
        return raw.Output[(split + 2)..];
    }
    private async Task<string> HashCommit(string cwd, string content, bool write, CancellationToken token)
    {
        string file = Path.Combine(Path.GetTempPath(), "aw-commit-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(file, content, new UTF8Encoding(false), token);
            return await Git(cwd, write ? ["hash-object", "-t", "commit", "-w", "--", file] : ["hash-object", "-t", "commit", "--", file], token);
        }
        finally { File.Delete(file); }
    }
    private static bool ValidIdent(string value) => Regex.IsMatch(value, @"^[^\r\n<>]+ <[^\r\n<>]+> [0-9]+ [+-][0-9]{4}$");
    private static string BaseBranch(string reference)
    {
        string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference[11..] :
            reference.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) ? reference[20..] :
            reference.StartsWith("origin/", StringComparison.Ordinal) ? reference[7..] : reference;
        if (!Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9_./-]*$") || branch.Contains("..", StringComparison.Ordinal) || branch.EndsWith('/') || branch.EndsWith(".lock", StringComparison.Ordinal))
            throw SubmissionError("base_branch_invalid", "The retained base reference must identify one GitHub branch.");
        return branch;
    }
    private static WorkflowException SubmissionError(string code, string message) => new(code, message,
        "Preserve the retained worktree and frozen intent. Inspect and reconcile the expected versus observed Git facts manually, then rerun awctl pr with the Workflow ID; force does not bypass this protection.");
}
