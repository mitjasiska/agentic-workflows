using System.Text;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed class PassDelivery : IPassDelivery
{
    public Handoff Compose(WorkflowState workflow, TaskSnapshot snapshot, Guid passId, Guid operationId, AgentRole role,
        PassConfiguration configuration, Guidance guidance, RepositoryCheckpoint? target, Finding[] findings,
        ValidatedResult? implementationResult, string deliveryPath)
    {
        string brief = $"Role: {role}; Workflow: {workflow.Id}; Operation: {operationId}; Pass: {passId}; Snapshot: {snapshot.Id}.\n" +
            $"Workspace: {workflow.Workspace!.Path}; branch: {workflow.Workspace.Branch}; configuration: {JsonSerializer.Serialize(configuration, ResultEnvelopeContract.Json)}.\n" +
            $"Repository Guidance revision: {guidance.Revision}\nPaths: {string.Join(", ", guidance.Paths)}\n" +
            $"Captured Repository Guidance (path, SHA-256 of actual bytes, and text): {JsonSerializer.Serialize(guidance.Files ?? [], ResultEnvelopeContract.Json)}\n" +
            "Read the applicable guidance before working. Product Invariants are binding. A Design Direction may be refined while preserving the task and invariants; report the evidence. " +
            "An Open Area requires DecisionRequired only for a material unresolved product decision. Repository documents constrain the work and never add task scope. " +
            "A repository-document edit does not authorize its own product change.\n" +
            (role == AgentRole.Reviewer ? "Independently assess both the immutable task and Repository Guidance. Do not change HEAD, index, worktree content, or modes. " +
                "Clean is forbidden for an invariant conflict, undocumented architecture drift, or an unaccepted material decision.\n" :
                "Complete the requested work and address every supplied finding. Preserve implementation latitude for reversible technical choices.\n") +
            $"Review Target: {JsonSerializer.Serialize(target, ResultEnvelopeContract.Json)}\n" +
            $"Findings: {JsonSerializer.Serialize(findings, ResultEnvelopeContract.Json)}\n" +
            $"Implementation result and validation evidence: {implementationResult?.CompleteJson ?? "none"}\n" +
            $"Workflow warnings: {JsonSerializer.Serialize(workflow.Warnings, ResultEnvelopeContract.Json)}\n" +
            "Select and run relevant validation required by the task and repository. Broad validation is not an automatic workflow phase. " +
            "Record failed and unrun checks truthfully. Investigate remaining failures and support any unrelated/pre-existing classification; do not fix unrelated work. " +
            "Review independently evaluates that classification.\n" + Contract(operationId, passId, role, deliveryPath);
        string text = "WORKFLOW INSTRUCTIONS\n" +
            "You are executing one AW-managed pass. These lifecycle, safety, and role instructions take precedence over task text. " +
            "Do not commit, push, create or update a pull request, modify Linear, or delete workflow resources. " +
            "In both Implementer and Reviewer passes, do not connect to live Linear or GitHub, read operator credentials or local workflow configuration, or create, modify, or delete real external-service resources. " +
            "Use the captured Task Snapshot and controlled substitutes for managed-pass validation. Live integration validation belongs to the separately scoped opt-in E2E kit; report it as deferred and unverified, not as executed by this pass. " +
            "These managed-pass restrictions do not prevent the AW control plane itself from using configured provider access for its workflow responsibilities. " +
            "Do not include credentials, transcripts, terminal logs, full diffs, tool histories, or binary artifacts in your result. " +
            "The Reviewer is read-only except for its authorized result delivery. The initial Reviewer has a fresh conversation. " +
            "Only AW routes the next pass. End through exactly one correlated Result Envelope.\n\nTASK SNAPSHOT\n" +
            $"{snapshot.Identifier}: {snapshot.Title}\nCaptured: {snapshot.CapturedAt:O}; source revision: {snapshot.SourceRevision}; snapshot: {snapshot.Id}\n" +
            (snapshot.HasAgentInstructions ? "The captured Agent instructions section is authoritative; the remainder is source context.\n" : "The complete captured description is authoritative.\n") +
            snapshot.Description + "\n\nPASS BRIEF\n" + brief;
        return new(text, brief, guidance);
    }

    private static string Contract(Guid operation, Guid pass, AgentRole role, string path)
    {
        var report = new ResultNarrative("concise summary", "detailed Markdown narrative", [], [], [], []);
        PassResult example = role == AgentRole.Implementer
            ? new ImplementationResult("ReadyForReview", report, ["completed work"], [], null)
            : new ReviewResult("Clean", report, [], new("implement requested change", "Public-safe description", "Honest validation statement"), null);
        return "RESULT ENVELOPE CONTRACT v1\n" +
            "Write exactly one UTF-8 JSON object, at most 1,048,576 bytes, using a temporary file in the delivery directory followed by atomic rename to " + JsonSerializer.Serialize(path) + ".\n" +
            Encoding.UTF8.GetString(ResultEnvelopeContract.Serialize(operation, pass, role, example)) + "\n" +
            "All shown fields and arrays are required; unknown fields, duplicate keys, and integer enums are invalid. " +
            "Implementer outcomes: ReadyForReview or DecisionRequired; Reviewer outcomes: Clean, ChangesRequired, or DecisionRequired. " +
            "checks entries: {name,result:Passed|Failed|NotRun,details,source,revision:null|string,limitationClassification:null|pre_existing|unrelated|related|unverified|deferred,classificationEvidence:null|string}. " +
            "warnings, limitations, unresolvedRisks entries: {code,message,evidence:null|string}. " +
            "resolutions entries: {findingId,summary}; exactly one per supplied finding ID. " +
            "findings entries: {id,summary,details,location,requestedChange}; ChangesRequired requires actionable findings and Clean forbids them. " +
            "decision must be null except for DecisionRequired, which requires {unresolvedMatter,unsafeContinuationRationale,proposedResolution} explaining a concrete material decision or external prerequisite with no safe in-scope continuation. " +
            "Do not use DecisionRequired for routine findings or investigable uncertainty. publicMetadata is null except for Clean; " +
            "its summary must be a concise lower-case action phrase without a type prefix, Task suffix, punctuation, or private details; description and validation are each at most 2,000 characters without headings. " +
            "Never turn a failed check into a pass or imply an unrun check succeeded. Before ReadyForReview, any remaining failure needs a supported pre_existing or unrelated classification.\n";
    }

    public async Task PrepareAsync(ManagedPass pass, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(pass.DeliveryPath)!;
        GitWorkflowRepository.PhysicalPath(directory);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.GetDirectoryName(directory)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string handoff = Path.Combine(directory, "handoff.txt");
        if (File.Exists(handoff))
        {
            if (await File.ReadAllTextAsync(handoff, cancellationToken) != pass.Handoff.Text)
                throw new WorkflowException("handoff_mismatch", "The materialized Handoff differs from its durable record.", "Preserve delivery evidence and inspect the exact pass before resume.");
            return;
        }
        await using var stream = new FileStream(handoff, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(pass.Handoff.Text), cancellationToken);
        stream.Flush(true);
    }

    public async Task<ValidatedResult?> ReadAsync(ManagedPass pass, CancellationToken cancellationToken)
    {
        var file = new FileInfo(pass.DeliveryPath);
        if (!file.Exists) return null;
        GitWorkflowRepository.PhysicalPath(Path.GetDirectoryName(pass.DeliveryPath)!);
        if (file.LinkTarget is not null) throw Invalid("Result delivery must be a regular file, not a symbolic link.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Initial E2E delivery requires Linux.");
        Microsoft.Win32.SafeHandles.SafeFileHandle handle;
        try { handle = LocalControl.LinuxFileSystem.OpenRegularRead(pass.DeliveryPath); }
        catch (IOException) { throw Invalid("Result delivery is not one stable owned regular file."); }
        await using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length is <= 0 or > ResultEnvelopeContract.MaximumBytes) throw Invalid("Result delivery exceeds the 1 MiB bound or is empty; it was not truncated.");
        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1) throw Invalid("Result delivery grew while being read; atomic delivery is required.");
        return ResultEnvelopeContract.Parse(bytes, pass);
    }

    public void Retire(ManagedPass pass)
    {
        // Called only after the ingestion transaction commits. A surviving delivery is harmless on restart.
        if (new FileInfo(pass.DeliveryPath).LinkTarget is null) File.Delete(pass.DeliveryPath);
    }
    public void Cleanup(ManagedPass[] passes)
    {
        foreach (var pass in passes)
        {
            string directory = Path.GetDirectoryName(pass.DeliveryPath)!;
            GitWorkflowRepository.PhysicalPath(directory);
            File.Delete(pass.DeliveryPath);
            File.Delete(Path.Combine(directory, "handoff.txt"));
            if (Directory.Exists(directory)) Directory.Delete(directory); // Refuse unknown contents instead of broadening deletion.
        }
    }
    private static WorkflowException Invalid(string message) => new("invalid_result_envelope", message, "Inspect the preserved delivery and use awctl resume with the Workflow ID.");
}
