using System.Text.RegularExpressions;

namespace AgenticWorkflows.Workflows;

public sealed record PublicSubmission(string Title, string Message, string Body);

public static class SubmissionMetadata
{
    public static PublicSubmission Create(LinearTask task, PublicationMetadata? reviewed, Guid? passId, bool force)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["Feature"] = "feat", ["Bug"] = "fix", ["Chore"] = "chore", ["Docs"] = "docs", ["Refactor"] = "refactor" };
        var categories = (task.Labels ?? []).Where(types.ContainsKey).ToArray();
        Require(categories.Length == 1, "Exactly one primary Linear category (Feature, Bug, Chore, Docs, Refactor) is required; AW does not change labels.");
        Require(Regex.IsMatch(task.Identifier, @"^[A-Z][A-Z0-9]*-[1-9][0-9]*$"), "The Linear identifier is invalid.");
        Require(Uri.TryCreate(task.Url, UriKind.Absolute, out var link) && link.Scheme == "https" && link.Host == "linear.app" &&
            link.UserInfo.Length == 0 && link.Query.Length == 0 && link.Fragment.Length == 0 && link.IsDefaultPort &&
            Regex.IsMatch(link.AbsolutePath, @"^/[A-Za-z0-9_-]+/issue/" + Regex.Escape(task.Identifier) + @"(?:/[a-zA-Z0-9_-]+)?$"), "The canonical Linear link is invalid.");
        Require(force || reviewed is not null && passId is not null, "Current Reviewed Publication Metadata is required.");
        if (reviewed is not null) Validate(reviewed);
        string title = $"{types[categories[0]]}: {reviewed?.Summary ?? "complete requested change"} ({task.Identifier})";
        Require(title.Length <= 72, "The complete public subject must fit within 72 characters.");
        string marker = passId?.ToString() ?? "bypassed-by-force";
        string disclosure = passId is null ? "Independent review was bypassed with `awctl pr --force`." : "Independent review accepted the exact change.";
        if (force && passId is not null) disclosure += " Submission was explicitly authorized with `awctl pr --force`.";
        return new(title, $"{title}\n\nTask-Review: {marker}\n",
            $"## Summary\n\n{reviewed?.Description ?? "Completes the requested change."}\n\n## Tracking\n\n[{task.Identifier}]({task.Url})\n\nFixes {task.Identifier}\n\n## Validation\n\n{reviewed?.Validation ?? "No current independently reviewed validation statement is available."}\n\n{disclosure}\n");
    }

    public static void Validate(PublicationMetadata metadata)
    {
        string summary = metadata.Summary;
        Require(!string.IsNullOrWhiteSpace(summary) && summary == summary.Trim() && summary.Length <= 60 &&
            summary == summary.ToLowerInvariant() && !summary.Any(char.IsControl) &&
            !summary.Contains(':') && !summary.Contains('(') && !summary.Contains(')') && !".!?".Contains(summary[^1]), "Invalid reviewed action summary.");
        foreach (string text in new[] { summary, metadata.Description, metadata.Validation })
        {
            Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 2000 &&
                !text.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') &&
                !text.Split('\n').Any(l => l.TrimStart().StartsWith('#')), "Invalid public metadata shape or length.");
            Require(!Regex.IsMatch(text, @"(?i)(?:file://|(?:^|\s|[`(])/(?:home|tmp|root|Users|var|run|mnt|workspace)/|[A-Z]:\\|\b(?:gh[pousr]_|github_pat_|sk-)[A-Za-z0-9_\-]+|-----BEGIN .*PRIVATE KEY|\b(?:authorization|password|api[_ -]?key|access[_ -]?token)\s*[:=]|<\|(?:system|user|assistant)|agent instructions|conversation transcript)"),
                "Reviewed metadata contains private instructions, local paths, conversation markers, or credential-like text.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new WorkflowException("publication_metadata_invalid", message,
            "Correct classification in Linear or obtain a fresh independent review with valid public metadata, then run awctl pr. No publication was authorized.");
    }
}
