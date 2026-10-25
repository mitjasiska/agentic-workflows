using System.Text.Json;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class HerdrWorkspaceRecoveryTests
{
    [Fact]
    public async Task Lost_workspace_response_recovers_exact_listed_resources_and_never_creates_a_second_worktree()
    {
        string root = Path.Combine(Path.GetTempPath(), "aw-herdr-" + Guid.NewGuid().ToString("N"));
        string checkout = Path.Combine(root, "canonical"); Directory.CreateDirectory(checkout);
        var process = new WorkspaceProcess(checkout, Path.Combine(root, "managed"));
        async Task<string> Git(params string[] args)
        {
            var output = await process.RunAsync("git", args, checkout, null, default);
            Assert.Equal(0, output.ExitCode); return output.Output.Trim();
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "AW test"); await Git("config", "user.email", "aw-test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(checkout, "file.txt"), "initial");
            await Git("add", "."); await Git("commit", "-m", "isolated workspace fixture");
            await Git("remote", "add", "origin", "https://github.com/example/isolated.git");
            string baseline = await Git("rev-parse", "HEAD");
            var repository = new GitWorkflowRepository(process);
            var identity = await repository.InspectCanonicalAsync(checkout, default);
            var intent = new WorkspaceIntent(Guid.NewGuid(), checkout, identity.GitDirectory, baseline,
                "aw/DEV-123/whole", process.Managed, "local:test", [], DateTimeOffset.UtcNow, identity.Stamp);
            var gateway = new HerdrGateway(process, repository);
            Assert.Equal("lost_workspace_response", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.PrepareWorkspaceAsync(intent, false, default))).Code);
            var recovered = await gateway.PrepareWorkspaceAsync(intent, true, default);
            Assert.Equal("new-workspace-id", recovered.WorkspaceId);
            Assert.Equal(process.Managed, recovered.Path);
            Assert.Equal(new OwnedPane("root-pane", "root-terminal"), Assert.Single(recovered.InitialPanes!));
            Assert.Equal(1, process.Creations);
            await File.WriteAllTextAsync(Path.Combine(process.Managed, "file.txt"), "unmanaged modification after uncertain creation");
            Assert.Equal("workspace_preparation_mismatch", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.PrepareWorkspaceAsync(intent, true, default))).Code);
            Assert.Equal(1, process.Creations);
            Assert.Contains("unmanaged modification", await File.ReadAllTextAsync(Path.Combine(process.Managed, "file.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class WorkspaceProcess(string canonical, string managed) : IArgumentProcess
    {
        private readonly ArgumentProcess git = new();
        internal string Managed => managed;
        internal int Creations;
        public async Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? directory,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            if (executable == "git") return await git.RunAsync(executable, arguments, directory, environment, cancellationToken);
            Assert.Equal("herdr", executable);
            if (arguments is ["session", "list", "--json"])
                return Json(new { sessions = new[] { new { name = "test", running = true } } });
            Assert.Equal("--session", arguments[0]); Assert.Equal("test", arguments[1]);
            var command = arguments.Skip(2).Take(2).ToArray();
            if (command.SequenceEqual(["worktree", "create"]))
            {
                Creations++;
                int baseIndex = arguments.ToList().IndexOf("--base");
                var created = await git.RunAsync("git", ["worktree", "add", "-b", "aw/DEV-123/whole", managed, arguments[baseIndex + 1]], canonical, null, cancellationToken);
                Assert.Equal(0, created.ExitCode);
                throw new WorkflowException("lost_workspace_response", "Creation response was lost after Herdr's Git action.", "Reconcile durable intent.");
            }
            bool exists = Directory.Exists(managed);
            if (command.SequenceEqual(["api", "snapshot"]))
                return Json(new { snapshot = new { version = "test", protocol = 22,
                    workspaces = exists ? new[] { new { workspace_id = "new-workspace-id" } } : [],
                    panes = exists ? new[] { new { workspace_id = "new-workspace-id", pane_id = "root-pane", terminal_id = "root-terminal" } } : [] } });
            if (command.SequenceEqual(["worktree", "list"]))
                return Json(new { source = new { repo_root = canonical }, worktrees = exists ? new[] { new { open_workspace_id = "new-workspace-id", path = managed, branch = "aw/DEV-123/whole" } } : [] });
            Assert.Equal(new[] { "workspace", "get" }, command);
            return Json(new { workspace = new { workspace_id = "new-workspace-id", worktree = new { checkout_path = managed } } });
        }
        private static ProcessOutput Json(object value) => new(0, JsonSerializer.Serialize(value));
    }
}
