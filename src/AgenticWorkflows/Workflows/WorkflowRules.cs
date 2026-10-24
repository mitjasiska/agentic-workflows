using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgenticWorkflows.Application;

namespace AgenticWorkflows.Workflows;

public static partial class WorkflowRules
{
    public static string ResultText(PassResult result)
    {
        var values = new List<string?> { result.Report.Summary, result.Report.Narrative };
        foreach (var check in result.Report.Checks) values.AddRange([check.Name, check.Details, check.Source, check.Revision, check.ClassificationEvidence]);
        foreach (var notice in result.Report.Warnings.Concat(result.Report.Limitations).Concat(result.Report.UnresolvedRisks))
            values.AddRange([notice.Code, notice.Message, notice.Evidence]);
        if (result.Decision is Decision decision) values.AddRange([decision.UnresolvedMatter, decision.UnsafeContinuationRationale, decision.ProposedResolution]);
        if (result is ImplementationResult implementation)
        {
            values.AddRange(implementation.CompletedWork);
            foreach (var resolution in implementation.Resolutions) values.AddRange([resolution.FindingId, resolution.Summary]);
        }
        if (result is ReviewResult review)
        {
            foreach (var finding in review.Findings) values.AddRange([finding.Id, finding.Summary, finding.Details, finding.Location, finding.RequestedChange]);
            if (review.PublicMetadata is PublicationMetadata metadata) values.AddRange([metadata.Summary, metadata.Description, metadata.Validation]);
        }
        return string.Join("\n", values);
    }

    public static TaskSnapshot Capture(LinearTask task, string policy, DateTimeOffset now)
    {
        ValidateChoice(policy, ["warn", "require", "ignore"], "instructions policy");
        string? extracted = ExtractInstructions(task.Description);
        if (extracted is null && policy == "require")
            throw new WorkflowException("instructions_required", "The task has no recognized Agent instructions section.",
                "Supply the section in Linear or explicitly select warn or ignore before capture.");
        string requirements = extracted ?? task.Description;
        if (string.IsNullOrWhiteSpace(requirements))
            throw new WorkflowException("empty_requirements", "The authoritative requirements are empty.", "Supply nonempty task requirements.");
        return new(Guid.NewGuid(), task.Identifier, task.Title, task.Description, requirements,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(requirements))), task.Revision, now, extracted is not null);
    }

    // Recognize headings and Linear's expandable section syntax only outside fenced code.
    // Preserve the exact captured characters inside the section for change detection.
    internal static string? ExtractInstructions(string description)
    {
        var lines = Regex.Matches(description, @"[^\r\n]*(?:\r\n|\n|\r|$)");
        int? start = null;
        int level = 0;
        bool expandable = false;
        char? fence = null;
        int fenceLength = 0;
        foreach (Match line in lines)
        {
            string text = line.Value.TrimEnd('\r', '\n');
            var fenced = Fence().Match(text);
            if (fenced.Success)
            {
                char marker = fenced.Groups[1].Value[0];
                int length = fenced.Groups[1].Length;
                if (fence is null) { fence = marker; fenceLength = length; }
                else if (fence == marker && length >= fenceLength) fence = null;
                continue;
            }
            if (fence is not null) continue;
            var heading = Heading().Match(text);
            bool isExpandable = text.TrimStart().StartsWith("+++", StringComparison.Ordinal);
            string label = isExpandable ? text.Trim()[3..].Trim() : heading.Groups[2].Value.Trim().TrimEnd('#').Trim();
            if (start is not null && (expandable && isExpandable || !expandable && heading.Success && heading.Groups[1].Length <= level))
                return description[start.Value..line.Index];
            if ((isExpandable || heading.Success) && label.Equals("Agent instructions", StringComparison.OrdinalIgnoreCase))
            {
                start = line.Index + line.Length;
                level = heading.Groups[1].Length;
                expandable = isExpandable;
            }
        }
        return start is null ? null : description[start.Value..];
    }

    public static int? Budget(string? selected)
    {
        if (selected == "unlimited") return null;
        if (selected is null) return 6;
        if (int.TryParse(selected, out int count) && count > 0) return count;
        throw new WorkflowException("invalid_budget", "Pass budget must be a positive integer or unlimited.", "Correct the selected value; invalid values do not fall back.");
    }

    public static PassConfiguration Resolve(AgentRole role, WorkflowSettings invocation,
        WorkflowState state, WorkflowSettings project, WorkflowSettings global)
    {
        bool implementation = role == AgentRole.Implementer;
        string model = (implementation ? invocation.ImplementerModel : invocation.ReviewerModel)
            ?? state.ModelSelections.GetValueOrDefault(role)
            ?? (implementation ? project.ImplementerModel : project.ReviewerModel)
            ?? (implementation ? global.ImplementerModel : global.ReviewerModel) ?? "default";
        if (!Model().IsMatch(model))
            throw new WorkflowException("invalid_model", "The selected model must be a nonempty model identifier.", "Correct or clear the role model selection.");
        string reasoning = invocation.Reasoning ?? project.Reasoning ?? global.Reasoning ?? "medium";
        ValidateChoice(reasoning, ["minimal", "low", "medium", "high", "xhigh"], "reasoning");
        string session = (implementation ? invocation.ImplementerSession : invocation.ReviewerSession)
            ?? state.NextSessionChoices.GetValueOrDefault(role)
            ?? (implementation ? project.ImplementerSession : project.ReviewerSession)
            ?? (implementation ? global.ImplementerSession : global.ReviewerSession) ?? "reuse";
        ValidateChoice(session, ["new", "reuse"], "session policy");
        string policy = invocation.InstructionsPolicy ?? project.InstructionsPolicy ?? global.InstructionsPolicy ?? "warn";
        ValidateChoice(policy, ["warn", "require", "ignore"], "instructions policy");
        return new(model, reasoning, session, policy);
    }

    public static void ValidateChoice(string value, string[] allowed, string name)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new WorkflowException("invalid_setting", $"Invalid {name}.", $"Select one of: {string.Join(", ", allowed)}.");
    }

    public static string Branch(string identifier)
    {
        if (!TaskKey().IsMatch(identifier))
            throw new WorkflowException("unsafe_task_identifier", "Linear returned an unsafe task identifier.", "Verify the provider task identity before starting.");
        return $"aw/{identifier}/whole";
    }

    [GeneratedRegex(@"^ {0,3}([`]{3,}|[~]{3,})")]
    private static partial Regex Fence();
    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.+)$")]
    private static partial Regex Heading();
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{0,31}-[1-9][0-9]{0,15}$")]
    private static partial Regex TaskKey();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$")]
    private static partial Regex Model();
}
