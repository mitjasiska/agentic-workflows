using System.Security.Cryptography;
using System.Text;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed class GitWorkflowRepository(IArgumentProcess process) : IWorkflowRepository
{
    private static readonly IReadOnlyDictionary<string, string?> ReadEnvironment = new Dictionary<string, string?>
    { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_INDEX_FILE"] = null, ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null };

    public async Task<RepositoryIdentity> InspectCanonicalAsync(string checkout, CancellationToken cancellationToken)
    {
        string path = PhysicalPath(checkout);
        string top = await Git(path, ["rev-parse", "--show-toplevel"], cancellationToken);
        string git = await Git(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken);
        string individual = await Git(path, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        if (path != PhysicalPath(top) || PhysicalPath(git) != PhysicalPath(individual))
            throw Error("canonical_checkout_required", "Project setup and start require the canonical checkout, not a linked worktree or subdirectory.");
        string remote = await Git(path, ["remote", "get-url", "origin"], cancellationToken);
        if (!SafeGitHubRemote(remote)) throw Error("invalid_remote", "The origin must be a credential-free GitHub HTTPS or SSH URL.");
        if (File.Exists(Path.Combine(path, WorkflowConfiguration.ProjectFilename)))
        {
            var ignored = await Run(path, ["check-ignore", "--quiet", "--", WorkflowConfiguration.ProjectFilename], cancellationToken);
            string tracked = await Git(path, ["ls-files", "--", WorkflowConfiguration.ProjectFilename], cancellationToken);
            if (ignored.ExitCode != 0 || tracked.Length != 0)
                throw Error("configuration_not_ignored", "The active Project Configuration must be untracked and Git-ignored. Add its filename to the repository ignore rules before setup or start.");
        }
        return new(path, PhysicalPath(git), remote, Stamp(git));
    }

    public async Task<string> AdmitAsync(RepositoryIdentity repository, string baseReference, string branch, string managedPath,
        CancellationToken cancellationToken)
    {
        _ = PhysicalPath(managedPath);
        if ((await Run(repository.Checkout, ["check-ref-format", "--branch", branch], cancellationToken)).ExitCode != 0)
            throw Error("unsafe_branch", "The selected Workflow branch is not Git-safe.");
        if (Path.Exists(managedPath) || new FileInfo(managedPath).LinkTarget is not null)
            throw Error("workspace_collision", "The reserved managed location already exists.");
        string baseline = await Git(repository.Checkout, ["rev-parse", "--verify", "--end-of-options", baseReference + "^{commit}"], cancellationToken);
        var local = await Run(repository.Checkout, ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], cancellationToken);
        if (local.ExitCode != 0) throw Error("git_admission_failed", "Could not inspect local branch references.");
        string localRef = "refs/heads/" + branch;
        if (local.Output.Split('\n').Any(line => RefCollision(line.Trim(), localRef) || line.EndsWith("/" + branch, StringComparison.Ordinal)))
            throw Error("branch_collision", "The deterministic Workflow branch collides with a retained local reference.");
        var remote = await Run(repository.Checkout, ["ls-remote", "--heads", "origin"], cancellationToken);
        if (remote.ExitCode != 0) throw Error("remote_unavailable", "Could not verify live remote branch collisions.");
        if (remote.Output.Split('\n').Any(line => RefCollision(line.Split('\t').Last().Trim(), localRef)))
            throw Error("branch_collision", "The deterministic Workflow branch collides with a live remote branch.");
        return baseline;
    }

    private static bool RefCollision(string first, string second) => first == second || first.StartsWith(second + "/", StringComparison.Ordinal) || second.StartsWith(first + "/", StringComparison.Ordinal);

    public async Task VerifyWorkspaceAsync(WorkspaceIntent intent, WorkspaceIdentity workspace, CancellationToken cancellationToken)
    {
        if (workspace.Target != intent.HerdrTarget || workspace.Branch != intent.Branch ||
            PhysicalPath(workspace.Path) != Path.GetFullPath(intent.ManagedPath) || workspace.GitDirectory != intent.GitDirectory)
            throw Error("workspace_identity_mismatch", "Herdr workspace identity does not match the durable reservation.");
        var canonical = await InspectCanonicalAsync(intent.Repository, cancellationToken);
        if (canonical.GitDirectory != intent.GitDirectory || canonical.Stamp != intent.RepositoryStamp)
            throw Error("repository_identity_mismatch", "The canonical repository identity no longer matches the reservation.");
        if (!Path.Exists(workspace.Path))
            throw Error("worktree_absent", "The exact reserved worktree path is positively absent; the canonical repository identity remains verified.");
        if (!Directory.Exists(workspace.Path))
            throw Error("workspace_identity_mismatch", "The reserved worktree path was replaced by a non-directory.");
        string common = await Git(workspace.Path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken);
        string branch = await Git(workspace.Path, ["symbolic-ref", "--short", "HEAD"], cancellationToken);
        if (PhysicalPath(common) != intent.GitDirectory || branch != intent.Branch || intent.RepositoryStamp is not null && Stamp(common) != intent.RepositoryStamp)
            throw Error("repository_identity_mismatch", "The Git worktree does not belong to the bound repository and branch.");
        string listed = await Git(intent.Repository, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        if (listed.Split('\0').Count(part => part == "worktree " + workspace.Path) != 1)
            throw Error("worktree_not_registered", "Git does not report exactly one registered worktree at the bound path.");
    }

    public async Task<RepositoryCheckpoint> CaptureAsync(WorkspaceIdentity workspace, string baselineCommit, CancellationToken cancellationToken)
    {
        // The caller holds AW's per-workspace mutation gate. Two complete observations reject external races.
        var first = await CaptureOnce(workspace, baselineCommit, cancellationToken);
        var second = await CaptureOnce(workspace, baselineCommit, cancellationToken);
        if (!first.SameReviewState(second)) throw Error("moving_review_target", "HEAD, index, or worktree changed during Review Target capture; no Reviewer was launched.");
        return second;
    }

    private async Task<RepositoryCheckpoint> CaptureOnce(WorkspaceIdentity workspace, string baseline, CancellationToken token)
    {
        string cwd = workspace.Path;
        string common = await Git(cwd, ["rev-parse", "--path-format=absolute", "--git-common-dir"], token);
        string branch = await Git(cwd, ["symbolic-ref", "--short", "HEAD"], token);
        if (PhysicalPath(common) != workspace.GitDirectory || branch != workspace.Branch)
            throw Error("repository_identity_mismatch", "The bound repository or checked-out branch changed.");
        string head = await Git(cwd, ["rev-parse", "HEAD"], token);
        string format = await Git(cwd, ["rev-parse", "--show-object-format"], token);
        string baselineTree = await Git(cwd, ["rev-parse", "--verify", "--end-of-options", baseline + "^{tree}"], token);
        string indexPath = await Git(cwd, ["rev-parse", "--path-format=absolute", "--git-path", "index"], token);
        string beforeIndex = IndexHash(indexPath);
        string index = Path.Combine(Path.GetTempPath(), "aw-index-" + Guid.NewGuid().ToString("N"));
        string inventoryFile = index + ".paths";
        var environment = new Dictionary<string, string?>(ReadEnvironment) { ["GIT_INDEX_FILE"] = index };
        try
        {
            // The actual index may track additions that ignore rules now exclude. Preserve that inventory,
            // but materialize current worktree bytes without inheriting assume-unchanged/skip-worktree flags.
            string[] headPaths = SplitPaths(await Git(cwd, ["ls-tree", "-r", "--name-only", "-z", head], token));
            string[] paths = headPaths.Concat(SplitPaths(await Git(cwd, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string fingerprint = await Fingerprint(cwd, paths, token);
            await Git(cwd, ["read-tree", head], token, environment);
            string[] materialized = paths.Where(p => headPaths.Contains(p, StringComparer.Ordinal) || Path.Exists(Path.Combine(cwd, p)) || new FileInfo(Path.Combine(cwd, p)).LinkTarget is not null).ToArray();
            if (materialized.Length > 0)
            {
                await File.WriteAllBytesAsync(inventoryFile, Encoding.UTF8.GetBytes(string.Join('\0', materialized) + '\0'), token);
                await Git(cwd, ["--literal-pathspecs", "-c", "core.filemode=true", "add", "--all", "--force", "--pathspec-from-file=" + inventoryFile, "--pathspec-file-nul"], token, environment);
            }
            string proposed = await Git(cwd, ["write-tree"], token, environment);
            string changes = await Git(cwd, ["diff-tree", "--no-commit-id", "--name-only", "-r", "-z", baselineTree, proposed, "--"], token);
            string afterHead = await Git(cwd, ["rev-parse", "HEAD"], token);
            if (head != afterHead || beforeIndex != IndexHash(indexPath) || fingerprint != await Fingerprint(cwd, paths, token))
                throw Error("moving_review_target", "HEAD, index, or materialized bytes/modes changed during Review Target capture.");
            // Fingerprint verifies nested working state for the full proposed inventory, including
            // new gitlinks absent from the real index; parent status is not nested-content evidence.
            return new(new(format, baselineTree, proposed), head, branch, beforeIndex,
                changes.Split('\0', StringSplitOptions.RemoveEmptyEntries), DateTimeOffset.UtcNow, fingerprint);
        }
        finally { File.Delete(index); File.Delete(index + ".lock"); File.Delete(inventoryFile); }
    }

    private static string[] SplitPaths(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private async Task<string> Fingerprint(string cwd, string[] paths, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            digest.AppendData(BitConverter.GetBytes(bytes.Length)); digest.AppendData(bytes);
        }
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            string full = Path.Combine(cwd, path);
            PhysicalPath(Path.GetDirectoryName(full)!);
            // Deleted paths belong to the Git tree comparison, not the materialized inventory.
            // Otherwise committing the identical proposed tree would change this fingerprint.
            if (!Path.Exists(full) && new FileInfo(full).LinkTarget is null) continue;
            Append(path);
            if (new FileInfo(full).LinkTarget is string link) { Append("120000"); Append(link); }
            else if (Directory.Exists(full))
            {
                if (PhysicalPath(full) != PhysicalPath(await Git(full, ["rev-parse", "--show-toplevel"], token)))
                    throw Error("nested_repository_unproven", "An included directory is not the root of a verifiable nested repository.");
                if ((await Git(full, ["status", "--porcelain=v1", "-z", "--ignore-submodules=none", "--untracked-files=all"], token)).Length != 0)
                    throw Error("dirty_submodule", "A proposed gitlink contains uncommitted or untracked work; reconcile the nested repository before review.");
                Append("160000"); Append(await Git(full, ["rev-parse", "HEAD"], token));
                // Even a Git-clean nested repository can have materialized changes hidden by filters.
                // Include its actual bytes, modes, and index rather than treating HEAD as working-state proof.
                string nestedIndex = await Git(full, ["rev-parse", "--path-format=absolute", "--git-path", "index"], token);
                Append(IndexHash(nestedIndex));
                string[] nestedPaths = SplitPaths(await Git(full, ["ls-tree", "-r", "--name-only", "-z", "HEAD"], token))
                    .Concat(SplitPaths(await Git(full, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token)))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                Append(await Fingerprint(full, nestedPaths, token));
            }
            else if (File.Exists(full))
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                using var handle = LocalControl.LinuxFileSystem.OpenRegularRead(full);
                using var stream = new FileStream(handle, FileAccess.Read);
                Append((File.GetUnixFileMode(full) & UnixFileMode.UserExecute) != 0 ? "100755" : "100644");
                Append(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)));
            }
            else Append("absent");
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    public async Task<ManagedLocation?> ResolveWorkflowAsync(string path, CancellationToken cancellationToken)
    {
        var result = await Run(path, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        if (result.ExitCode != 0) return null;
        string metadata = Path.Combine(result.Output.Trim(), "aw-workflow-id");
        if (!File.Exists(metadata)) return null;
        if (new FileInfo(metadata).LinkTarget is not null || !Guid.TryParse(File.ReadAllText(metadata).Trim(), out var id))
            throw Error("invalid_worktree_identity", "The worktree identity metadata is missing, conflicting, or invalid.");
        return new(id, PhysicalPath(await Git(path, ["rev-parse", "--show-toplevel"], cancellationToken)));
    }

    public async Task BindWorkflowAsync(WorkspaceIdentity workspace, Guid workflowId, CancellationToken cancellationToken)
    {
        string git = await Git(workspace.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        string metadata = Path.Combine(git, "aw-workflow-id");
        if (File.Exists(metadata))
        {
            if ((await ResolveWorkflowAsync(workspace.Path, cancellationToken))?.WorkflowId != workflowId)
                throw Error("conflicting_worktree_identity", "The managed worktree is already bound to another Workflow.");
            return;
        }
        using var stream = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(workflowId.ToString()), cancellationToken);
        stream.Flush(true);
    }

    public async Task<Guidance> ReadGuidanceAsync(WorkspaceIdentity workspace, CancellationToken cancellationToken)
    {
        string revision = await Git(workspace.Path, ["rev-parse", "HEAD"], cancellationToken);
        string listed = await Git(workspace.Path, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken);
        var files = new List<GuidanceFile>();
        foreach (string path in SplitPaths(listed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (path is not ("AGENTS.md" or "README.md") && !path.EndsWith("/AGENTS.md", StringComparison.Ordinal) &&
                !(path.StartsWith("docs/architecture/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal))) continue;
            string full = PhysicalPath(Path.Combine(workspace.Path, path));
            if (!File.Exists(full)) continue;
            if (new FileInfo(full).Length > 1024 * 1024) throw Error("guidance_oversized", "One Repository Guidance file exceeds the 1 MiB handoff bound.");
            byte[] bytes = await File.ReadAllBytesAsync(full, cancellationToken);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                throw new WorkflowException("guidance_invalid_utf8", "Repository Guidance contains invalid UTF-8 encoding; no Handoff was accepted.",
                    "Correct the encoding of the applicable Repository Guidance, then run awctl resume with the retained Workflow ID.");
            }
            files.Add(new(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), text));
        }
        return new(revision, files.Select(f => f.Path).ToArray(), [.. files]);
    }

    public async Task<bool> HasUniqueWorkAsync(WorkspaceIdentity workspace, string baseline, string boundRemoteUrl, CancellationToken cancellationToken)
    {
        var checkpoint = await CaptureAsync(workspace, baseline, cancellationToken);
        string headTree = await Git(workspace.Path, ["rev-parse", "HEAD^{tree}"], cancellationToken);
        if (headTree != checkpoint.Target.ProposedTree) return true;
        var ancestry = await Run(workspace.Path, ["merge-base", "--is-ancestor", checkpoint.Head, baseline], cancellationToken);
        if (ancestry.ExitCode == 0) return false;
        if (await Git(workspace.Path, ["remote", "get-url", "origin"], cancellationToken) != boundRemoteUrl) return true;
        // A stale remote-tracking ref does not prove the commit is recoverable from the live bound remote.
        var remote = await Run(workspace.Path, ["ls-remote", "--heads", "origin"], cancellationToken);
        if (remote.ExitCode != 0) return true;
        foreach (string line in remote.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string tip = line.Split('\t')[0];
            if (!System.Text.RegularExpressions.Regex.IsMatch(tip, @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")) continue;
            if (tip == checkpoint.Head || (await Run(workspace.Path, ["merge-base", "--is-ancestor", checkpoint.Head, tip], cancellationToken)).ExitCode == 0)
                return false;
        }
        return true;
    }

    public async Task DeleteBranchAsync(WorkspaceIntent intent, string expectedTip, CancellationToken cancellationToken)
    {
        var identity = await InspectCanonicalAsync(intent.Repository, cancellationToken);
        if (identity.GitDirectory != intent.GitDirectory || identity.Stamp != intent.RepositoryStamp) throw Error("repository_identity_mismatch", "The repository was replaced; branch deletion refused.");
        string worktrees = await Git(intent.Repository, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        if (worktrees.Split('\0').Contains("branch refs/heads/" + intent.Branch)) throw Error("branch_checked_out", "The Workflow branch remains checked out; branch deletion refused.");
        await Git(intent.Repository, ["update-ref", "-d", "refs/heads/" + intent.Branch, expectedTip], cancellationToken);
    }

    public async Task<string> ReadBranchTipAsync(WorkspaceIntent intent, CancellationToken cancellationToken)
    {
        var identity = await InspectCanonicalAsync(intent.Repository, cancellationToken);
        if (identity.GitDirectory != intent.GitDirectory || identity.Stamp != intent.RepositoryStamp)
            throw Error("repository_identity_mismatch", "The bound repository was replaced; branch inspection refused.");
        string reference = "refs/heads/" + intent.Branch;
        var symbolic = await Run(intent.Repository, ["symbolic-ref", "--quiet", reference], cancellationToken);
        if (symbolic.ExitCode == 0)
            throw Error("branch_identity_mismatch", "The exact local Workflow branch was replaced by a symbolic reference; deletion refused.");
        if (symbolic.ExitCode != 1)
            throw Error("git_check_failed", "Git could not establish the local branch reference type.");
        var exists = await Run(intent.Repository, ["show-ref", "--verify", "--quiet", reference], cancellationToken);
        if (exists.ExitCode == 1)
            throw Error("branch_absent", "The exact local Workflow branch is positively absent from the verified canonical repository.");
        if (exists.ExitCode != 0)
            throw Error("git_check_failed", "Git could not verify the exact local Workflow branch.");
        string listed = await Git(intent.Repository, ["for-each-ref", "--format=%(objectname)%09%(refname)%09%(symref)", reference], cancellationToken);
        var matches = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))
            .Where(fields => fields.Length == 3 && fields[1] == reference).ToArray();
        if (matches.Length != 1 || matches[0][2].Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(matches[0][0], @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$"))
            throw Error("branch_identity_mismatch", "Git did not prove one direct local Workflow branch; no broader branch target was selected.");
        return matches[0][0];
    }

    private async Task<string> Git(string directory, string[] arguments, CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await Run(directory, arguments, token, environment);
        if (result.ExitCode != 0) throw Error("git_check_failed", $"Git {arguments[0]} could not establish the required repository fact.");
        return result.Output.TrimEnd('\r', '\n');
    }
    private Task<ProcessOutput> Run(string directory, string[] arguments, CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null) => process.RunAsync("git", arguments, directory, environment ?? ReadEnvironment, token);
    private static string IndexHash(string path) => File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static string Stamp(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Initial E2E repository identity requires Linux.");
        var metadata = LocalControl.LinuxFileSystem.Require(path, LocalControl.LinuxFileSystem.DirectoryType);
        return $"{metadata.DeviceMajor}:{metadata.DeviceMinor}:{metadata.Inode}";
    }
    public static string PhysicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).LinkTarget is not null)
                throw Error("symlink_path", "The repository or managed path contains a symbolic link; identity cannot be proven.");
        }
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }
    public static bool SafeGitHubRemote(string value) =>
        value.StartsWith("git@github.com:", StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private static WorkflowException Error(string code, string message) => new(code, message, "Preserve the workspace, inspect the exact repository facts, and use awctl resume or awctl cleanup --force with the Workflow ID.");
}
