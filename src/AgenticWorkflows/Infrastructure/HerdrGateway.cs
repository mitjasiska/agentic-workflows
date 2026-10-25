using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

/// <summary>AW semantics composed from structured official CLI commands and durable caller-owned intent.</summary>
public sealed class HerdrGateway(IArgumentProcess process, IWorkflowRepository repository) : IHerdrGateway
{
    public Task<HerdrCapabilities> InspectTargetAsync(string target, CancellationToken token) => Structured(() => InspectTargetCore(target, token));
    public Task<WorkspaceInspection> InspectWorkspacesAsync(string target, string repository, CancellationToken token) => Structured(() => InspectWorkspacesCore(target, repository, token));
    public Task<WorkspaceIdentity> PrepareWorkspaceAsync(WorkspaceIntent intent, bool attempted, CancellationToken token) => Structured(() => PrepareWorkspaceCore(intent, attempted, token));
    public Task VerifyWorkspaceAsync(WorkspaceIdentity workspace, CancellationToken token) => Structured(() => VerifyWorkspaceCore(workspace, token));
    public Task<SessionPreparation> PrepareSessionAsync(WorkspaceIdentity workspace, AgentRole role, PassConfiguration configuration, AgentSession? retained, CancellationToken token) => Structured(() => PrepareSessionCore(workspace, role, configuration, retained, token));
    public Task StartPassAsync(WorkspaceIdentity workspace, AgentSession session, ManagedPass pass, CancellationToken token) => Structured(() => StartPassCore(workspace, session, pass, token));
    public Task<ExecutionObservation> ObserveAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken token) => Structured(() => ObserveCore(workspace, session, token));
    public Task InterruptAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken token) => Structured(() => InterruptCore(workspace, session, token));
    public Task<string> ReadOutputAsync(WorkspaceIdentity workspace, AgentSession session, CancellationToken token) => Structured(() => ReadOutputCore(workspace, session, token));
    public Task TeardownAsync(WorkspaceIdentity workspace, AgentSession[] sessions, bool force, CancellationToken token) => Structured(() => TeardownCore(workspace, sessions, force, token));

    private static async Task<T> Structured<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Error("herdr_invalid_output", "Herdr returned an incomplete or invalid structured response; the requested action remains unconfirmed."); }
    }
    private static Task Structured(Func<Task> action) => Structured(async () => { await action(); return true; });
    private static readonly JsonDocumentOptions StrictJson = new() { MaxDepth = 32, AllowDuplicateProperties = false };
    private async Task<HerdrCapabilities> InspectTargetCore(string target, CancellationToken cancellationToken)
    {
        var snapshot = await Snapshot(target, cancellationToken);
        string version = Text(snapshot, "version");
        int protocol = snapshot.GetProperty("protocol").GetInt32();
        var schema = await process.RunAsync("herdr", ["api", "schema", "--json"], null, null, cancellationToken);
        if (schema.ExitCode != 0) throw Error("herdr_capabilities_unavailable", "Herdr cannot expose its structured command schema.");
        using var document = JsonDocument.Parse(schema.Output, StrictJson);
        var methods = document.RootElement.GetProperty("schemas").GetProperty("request").GetProperty("oneOf")
            .EnumerateArray().Select(e => e.GetProperty("properties").GetProperty("method").GetProperty("const").GetString()).ToArray();
        string[] required = ["worktree.create", "worktree.list", "worktree.remove", "agent.start", "agent.prompt", "agent.get", "pane.get", "pane.split", "pane.process_info"];
        if (required.Any(r => !methods.Contains(r))) throw Error("herdr_capability_missing", "Herdr lacks an underlying structured workspace, process, or agent command required by this adapter.");
        if (document.RootElement.GetProperty("protocol").GetInt32() != protocol)
            throw Error("herdr_cli_server_mismatch", "Herdr CLI and server schemas differ.");
        return new(target, version, protocol, required);
    }

    private async Task<WorkspaceInspection> InspectWorkspacesCore(string target, string canonicalRepository, CancellationToken cancellationToken)
    {
        var snapshot = await Snapshot(target, cancellationToken);
        var list = await Call(target, ["worktree", "list", "--cwd", canonicalRepository, "--trust-repository"], cancellationToken);
        string root = Text(list.GetProperty("source"), "repo_root");
        var identity = await repository.InspectCanonicalAsync(root, cancellationToken);
        var matches = list.GetProperty("worktrees").EnumerateArray()
            .Where(e => OptionalText(e, "open_workspace_id") is not null && OptionalText(e, "branch") is not null)
            .Select(e => new WorkspaceIdentity(target, Text(e, "open_workspace_id"), Text(e, "path"),
                Text(e, "branch").Replace("refs/heads/", "", StringComparison.Ordinal), identity.GitDirectory,
                snapshot.GetProperty("panes").EnumerateArray().Where(p => Text(p, "workspace_id") == Text(e, "open_workspace_id"))
                    .Select(p => new OwnedPane(Text(p, "pane_id"), Text(p, "terminal_id"))).ToArray())).ToArray();
        return new(matches, snapshot.GetProperty("workspaces").EnumerateArray().Select(e => Text(e, "workspace_id")).ToArray());
    }

    private async Task<WorkspaceIdentity> PrepareWorkspaceCore(WorkspaceIntent intent, bool alreadyAttempted, CancellationToken cancellationToken)
    {
        var inspection = await InspectWorkspacesAsync(intent.HerdrTarget, intent.Repository, cancellationToken);
        var candidates = inspection.Workspaces.Where(w => w.Path == intent.ManagedPath || w.Branch == intent.Branch).ToArray();
        if (alreadyAttempted)
        {
            if (candidates.Length != 1 || intent.ExistingWorkspaceIds.Contains(candidates[0].WorkspaceId) || candidates[0].InitialPanes?.Length != 1)
                throw Error("workspace_preparation_uncertain", "The earlier preparation cannot be correlated to exactly one newly created Herdr resource. No second creation was attempted.");
            var candidate = candidates[0];
            await repository.VerifyWorkspaceAsync(intent, candidate, cancellationToken);
            await VerifyPreparedContent(intent, candidate, cancellationToken);
            await VerifyWorkspaceAsync(candidate, cancellationToken);
            return candidate;
        }
        if (candidates.Length != 0 || Path.Exists(intent.ManagedPath)) throw Error("workspace_collision", "The reserved branch or managed path is occupied.");
        var result = await Call(intent.HerdrTarget, ["worktree", "create", "--cwd", intent.Repository, "--base", intent.BaseCommit,
            "--branch", intent.Branch, "--path", intent.ManagedPath, "--label", "AW " + intent.WorkflowId,
            "--no-focus", "--trust-repository"], cancellationToken);
        var identity = new WorkspaceIdentity(intent.HerdrTarget, Text(result.GetProperty("workspace"), "workspace_id"),
            Text(result.GetProperty("worktree"), "path"), intent.Branch, intent.GitDirectory,
            [new(Text(result.GetProperty("root_pane"), "pane_id"), Text(result.GetProperty("root_pane"), "terminal_id"))]);
        if (intent.ExistingWorkspaceIds.Contains(identity.WorkspaceId)) throw Error("workspace_identity_mismatch", "Herdr returned a previously existing workspace.");
        await repository.VerifyWorkspaceAsync(intent, identity, cancellationToken);
        await VerifyPreparedContent(intent, identity, cancellationToken);
        await VerifyWorkspaceAsync(identity, cancellationToken);
        return identity;
    }

    private async Task VerifyPreparedContent(WorkspaceIntent intent, WorkspaceIdentity workspace, CancellationToken token)
    {
        var checkpoint = await repository.CaptureAsync(workspace, intent.BaseCommit, token);
        if (checkpoint.Head != intent.BaseCommit || checkpoint.Target.ProposedTree != checkpoint.Target.BaselineTree)
            throw Error("workspace_preparation_mismatch", "The newly prepared workspace does not match the admitted base and clean initial tree. It was not adopted or recreated.");
    }

    private async Task VerifyWorkspaceCore(WorkspaceIdentity workspace, CancellationToken cancellationToken)
    {
        var value = (await Call(workspace.Target, ["workspace", "get", workspace.WorkspaceId], cancellationToken)).GetProperty("workspace");
        if (Text(value, "workspace_id") != workspace.WorkspaceId ||
            value.GetProperty("worktree").ValueKind != JsonValueKind.Object ||
            Text(value.GetProperty("worktree"), "checkout_path") != workspace.Path)
            throw Error("herdr_identity_mismatch", "The exact Herdr workspace is missing or no longer owns the bound worktree.");
    }

    private async Task<SessionPreparation> PrepareSessionCore(WorkspaceIdentity workspace, AgentRole role,
        PassConfiguration configuration, AgentSession? retained, CancellationToken cancellationToken)
    {
        await VerifyWorkspaceAsync(workspace, cancellationToken);
        if (retained is not null)
        {
            await VerifyPane(workspace, retained, cancellationToken);
            var agent = await Agent(workspace, retained, cancellationToken);
            if (retained.Role != role || retained.Model != configuration.Model || retained.Reasoning != configuration.Reasoning ||
                retained.NativeResumeReference is null || retained.NativeResumeReference != NativeSession(agent) ||
                OptionalText(agent, "agent") != "codex" || Text(agent, "agent_status") != "idle")
                throw Error("session_reuse_unavailable", "The expected role session cannot be verified idle with the selected model and reasoning. Explicitly select a new session to change launch configuration.");
            return new(retained, false);
        }
        var snapshot = await Snapshot(workspace.Target, cancellationToken);
        var panes = snapshot.GetProperty("panes").EnumerateArray().Where(e => Text(e, "workspace_id") == workspace.WorkspaceId).ToArray();
        var anchor = panes.FirstOrDefault(p => workspace.InitialPanes?.Any(o => o.PaneId == Text(p, "pane_id") && o.TerminalId == Text(p, "terminal_id")) == true);
        if (anchor.ValueKind != JsonValueKind.Object) throw Error("owned_pane_missing", "The original owned pane cannot be verified for session preparation.");
        // This mutation is called only after the application records a preparation attempt. Ambiguous preparation stops;
        // it is never repeated automatically after restart or a lost response.
        var created = await Call(workspace.Target, ["pane", "split", "--pane", Text(anchor, "pane_id"),
            "--direction", "right", "--cwd", workspace.Path, "--no-focus"], cancellationToken);
        var pane = created.GetProperty("pane");
        if (Text(pane, "workspace_id") != workspace.WorkspaceId) throw Error("pane_identity_mismatch", "Herdr prepared a pane outside the owned workspace.");
        return new(new(Guid.NewGuid(), role, workspace.WorkspaceId, Text(pane, "pane_id"), Text(pane, "terminal_id"),
            null, configuration.Model, DateTimeOffset.UtcNow, configuration.Reasoning), true);
    }

    private async Task StartPassCore(WorkspaceIdentity workspace, AgentSession session, ManagedPass pass, CancellationToken cancellationToken)
    {
        await VerifyPane(workspace, session, cancellationToken);
        if (session.Id != pass.SessionId || session.Role != pass.Role || session.Model != pass.Configuration.Model || session.Reasoning != pass.Configuration.Reasoning)
            throw Error("session_reuse_unavailable", "The selected pass configuration differs from the recorded session launch configuration; no prompt was sent.");
        string deliveryRoot = session.DeliveryRoot ?? throw Error("session_delivery_mismatch", "The selected session has no recorded delivery grant.");
        if (GitWorkflowRepository.PhysicalPath(Path.GetDirectoryName(Path.GetDirectoryName(pass.DeliveryPath)!)!) != GitWorkflowRepository.PhysicalPath(deliveryRoot) ||
            deliveryRoot == workspace.Path || deliveryRoot.StartsWith(workspace.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw Error("session_delivery_mismatch", "The Operation delivery is outside the selected session grant or inside the worktree.");
        var agent = await Agent(workspace, session, cancellationToken);
        if (session.NativeResumeReference is not null)
        {
            if (session.NativeResumeReference != NativeSession(agent) || OptionalText(agent, "agent") != "codex" || Text(agent, "agent_status") != "idle")
                throw Error("session_reuse_unavailable", "The expected native conversation is missing, replaced, or not idle at the prompt boundary; no prompt was sent.");
        }
        else if (OptionalText(agent, "agent") is null)
        {
            var args = new List<string> { "agent", "start", "AW-" + session.Id.ToString("N"), "--kind", "codex", "--pane", session.PaneId, "--timeout", "30000", "--" };
            if (pass.Configuration.Model != "default") args.AddRange(["--model", pass.Configuration.Model]);
            args.AddRange(["--config", "model_reasoning_effort=\"" + pass.Configuration.Reasoning + "\"", "--sandbox", "workspace-write",
                "--add-dir", deliveryRoot, "--ask-for-approval", "never"]);
            // Review is read-only by contract but needs its authorized delivery directory writable.
            await Call(workspace.Target, args, cancellationToken);
            await VerifyPane(workspace, session, cancellationToken);
            agent = await Agent(workspace, session, cancellationToken);
            if (OptionalText(agent, "agent") != "codex" || Text(agent, "agent_status") != "idle")
                throw Error("session_not_idle", "The fresh Codex start did not establish an idle selected execution; no prompt was sent.");
        }
        else throw Error("session_reuse_unavailable", "A process already occupies the fresh selected pane without native conversation proof; no prompt was sent.");
        await Call(workspace.Target, ["agent", "prompt", session.PaneId,
            "Read the complete AW Handoff at " + JsonSerializer.Serialize(Path.Combine(Path.GetDirectoryName(pass.DeliveryPath)!, "handoff.txt")) +
            ". Perform that bounded pass and atomically write its one correlated Result Envelope to the authorized location. Do not commit, push, or create a pull request."], cancellationToken);
    }

    private async Task<ExecutionObservation> ObserveCore(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken)
    {
        await VerifyWorkspaceAsync(workspace, cancellationToken);
        var snapshot = await Snapshot(workspace.Target, cancellationToken);
        var panes = snapshot.GetProperty("panes").EnumerateArray().Where(e => Text(e, "pane_id") == session.PaneId).ToArray();
        if (panes.Length == 0) return new(ExecutionState.Gone, DateTimeOffset.UtcNow, true, "The exact owned pane is absent from the reachable target.", null);
        await VerifyPane(workspace, session, cancellationToken);
        var agent = await Agent(workspace, session, cancellationToken);
        string status = Text(agent, "agent_status");
        if (session.NativeResumeReference is not null && session.NativeResumeReference != NativeSession(agent))
            throw Error("execution_identity_mismatch", "The expected native execution is missing or replaced; its last verified observation remains authoritative.");
        ExecutionState state = status switch { "working" => ExecutionState.Running, "idle" => ExecutionState.Idle, "done" => ExecutionState.Completed, _ => ExecutionState.Unknown };
        long? sequence = agent.TryGetProperty("completion_seq", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
        return new(state, DateTimeOffset.UtcNow, true, null, sequence, NativeSession(agent));
    }

    private async Task InterruptCore(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken)
    {
        await VerifyPane(workspace, session, cancellationToken);
        var agent = await Agent(workspace, session, cancellationToken);
        if (OptionalText(agent, "agent") != "codex" || session.NativeResumeReference is not null && session.NativeResumeReference != NativeSession(agent))
            throw Error("execution_identity_mismatch", "The selected pane contains a different or unverified agent execution; interruption refused.");
        await Call(workspace.Target, ["agent", "send-keys", session.PaneId, "Ctrl+c"], cancellationToken);
    }

    private async Task<string> ReadOutputCore(WorkspaceIdentity workspace, AgentSession session, CancellationToken cancellationToken)
    {
        await VerifyPane(workspace, session, cancellationToken);
        var output = await Call(workspace.Target, ["agent", "read", session.PaneId], cancellationToken);
        return output.GetRawText();
    }

    private async Task TeardownCore(WorkspaceIdentity workspace, AgentSession[] sessions, bool force, CancellationToken cancellationToken)
    {
        await VerifyWorkspaceAsync(workspace, cancellationToken);
        var snapshot = await Snapshot(workspace.Target, cancellationToken);
        var owned = snapshot.GetProperty("panes").EnumerateArray().Where(e => Text(e, "workspace_id") == workspace.WorkspaceId).ToArray();
        // A new unmanaged pane in a retained workspace is not covered by a similar path or workspace label.
        if (owned.Any(p => !sessions.Any(s => s.PaneId == Text(p, "pane_id") && s.TerminalId == Text(p, "terminal_id")) &&
            workspace.InitialPanes?.Any(s => s.PaneId == Text(p, "pane_id") && s.TerminalId == Text(p, "terminal_id")) != true))
            throw Error("unproven_teardown_boundary", "The workspace contains a pane not recorded as AW-owned; teardown refused.");
        if (!force)
        {
            foreach (var pane in owned)
            {
                string paneId = Text(pane, "pane_id");
                var retained = sessions.SingleOrDefault(s => s.PaneId == paneId);
                if (retained is not null)
                {
                    var observed = await ObserveAsync(workspace, retained, cancellationToken);
                    if (!observed.Reachable || observed.State is not (ExecutionState.Idle or ExecutionState.Completed or ExecutionState.Gone))
                        throw Error("cleanup_execution_unproven", "An owned execution is active or uncertain; teardown refused.");
                }
                var info = (await Call(workspace.Target, ["pane", "process-info", "--pane", paneId], cancellationToken)).GetProperty("process_info");
                if (Text(info, "pane_id") != paneId) throw Error("cleanup_execution_unproven", "Process facts do not identify the owned pane.");
                var processes = info.GetProperty("foreground_processes").EnumerateArray().ToArray();
                // An idle retained Codex may occupy the foreground. Other foreground work is not
                // covered by an old completed Operation; an owned shell must itself be idle.
                if (retained is null && (info.GetProperty("shell_pid").ValueKind != JsonValueKind.Number || processes.Length != 1 ||
                    processes[0].GetProperty("pid").GetInt64() != info.GetProperty("shell_pid").GetInt64()))
                    throw Error("cleanup_execution_unproven", "The owned shell has active or unobservable foreground work; teardown refused.");
                if (retained is not null && (processes.Length != 1 || Text(processes[0], "name") != "codex"))
                    throw Error("cleanup_execution_unproven", "The retained session has active or unobservable foreground processes; teardown refused.");
            }
        }
        var args = new List<string> { "worktree", "remove", "--workspace", workspace.WorkspaceId, "--trust-repository" };
        if (force) args.Add("--force");
        await Call(workspace.Target, args, cancellationToken);
    }

    private async Task VerifyPane(WorkspaceIdentity workspace, AgentSession session, CancellationToken token)
    {
        await VerifyWorkspaceAsync(workspace, token);
        var pane = (await Call(workspace.Target, ["pane", "get", session.PaneId], token)).GetProperty("pane");
        if (Text(pane, "pane_id") != session.PaneId || Text(pane, "terminal_id") != session.TerminalId || Text(pane, "workspace_id") != workspace.WorkspaceId)
            throw Error("session_identity_mismatch", "The selected pane, terminal, or workspace no longer matches the recorded session.");
    }
    private async Task<JsonElement> Agent(WorkspaceIdentity workspace, AgentSession session, CancellationToken token)
    {
        var agent = (await Call(workspace.Target, ["agent", "get", session.PaneId], token)).GetProperty("agent");
        if (Text(agent, "pane_id") != session.PaneId || Text(agent, "terminal_id") != session.TerminalId || Text(agent, "workspace_id") != workspace.WorkspaceId)
            throw Error("session_identity_mismatch", "The selected execution does not match the durable session identifiers.");
        return agent;
    }
    private async Task<JsonElement> Snapshot(string target, CancellationToken token) => (await Call(target, ["api", "snapshot"], token)).GetProperty("snapshot");
    private async Task<JsonElement> Call(string target, IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(target, @"^local:[A-Za-z0-9_-]{1,64}$"))
            throw Error("invalid_herdr_target", "Initial E2E requires an explicit local:SESSION Herdr target.");
        // --session can create a session in interactive CLI modes. Prove this exact named target already exists
        // and is running before any structured call, including read-only admission. Never auto-start a replacement.
        var listed = await process.RunAsync("herdr", ["session", "list", "--json"], null, null, token);
        try
        {
            using var sessions = JsonDocument.Parse(listed.Output, StrictJson);
            var selected = sessions.RootElement.GetProperty("sessions").EnumerateArray().Where(s => Text(s, "name") == target[6..]).ToArray();
            if (listed.ExitCode != 0 || selected.Length != 1 || !selected[0].GetProperty("running").GetBoolean())
                throw Error("herdr_target_unavailable", "The exact configured local Herdr session is missing or stopped. AW did not create or start one.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw Error("herdr_target_unavailable", "Herdr did not provide an unambiguous running local target."); }
        var result = await process.RunAsync("herdr", ["--session", target[6..], .. arguments], null, null, token);
        if (result.ExitCode != 0) throw Error("herdr_command_failed", "Herdr did not confirm the requested structured operation; its side effects must be reconciled before retry.");
        try
        {
            using var document = JsonDocument.Parse(result.Output, StrictJson);
            var root = document.RootElement;
            return (root.TryGetProperty("result", out var response) ? response : root).Clone();
        }
        catch (JsonException) { throw Error("herdr_invalid_output", "Herdr returned malformed structured output; the requested action remains unconfirmed."); }
    }
    private static string Text(JsonElement element, string property) => OptionalText(element, property) ?? throw Error("herdr_missing_fact", $"Herdr did not supply required field {property}.");
    private static string? NativeSession(JsonElement agent) => agent.TryGetProperty("agent_session", out var session) && session.ValueKind == JsonValueKind.Object
        ? Text(session, "source") + ":" + Text(session, "agent") + ":" + Text(session, "kind") + ":" + Text(session, "value") : null;
    private static string? OptionalText(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static WorkflowException Error(string code, string message) => new(code, message,
        "Inspect the exact retained Workflow. Restore the selected Herdr target or explicitly select a new session before awctl resume; never resend an ambiguous prompt.");
}
