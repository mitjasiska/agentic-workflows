using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

/// <summary>Versioned delivery boundary. Parsing never confers execution completion.</summary>
public static class ResultEnvelopeContract
{
    public const int Version = 1;
    public const int MaximumBytes = 1_048_576;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private sealed record WireEnvelope(int SchemaVersion, Guid OperationId, Guid PassId, AgentRole Role, JsonElement Payload);

    public static ValidatedResult Parse(ReadOnlySpan<byte> bytes, ManagedPass pass)
    {
        try
        {
            Require(bytes.Length is > 0 and <= MaximumBytes, "Result Envelope must contain 1–1,048,576 UTF-8 bytes; it was not truncated.");
            string raw = new UTF8Encoding(false, true).GetString(bytes);
            var wire = JsonSerializer.Deserialize<WireEnvelope>(raw, Json);
            Require(wire is not null, "Expected one JSON object.");
            Require(wire!.SchemaVersion == Version, $"Result schema must be {Version}.");
            Require(wire.OperationId == pass.OperationId && wire.PassId == pass.Id && wire.Role == pass.Role,
                "Operation, pass, or role does not match the authorized delivery.");
            PassResult payload = wire.Role switch
            {
                AgentRole.Implementer => wire.Payload.Deserialize<ImplementationResult>(Json)!,
                AgentRole.Reviewer => wire.Payload.Deserialize<ReviewResult>(Json)!,
                _ => throw Invalid("Unsupported role.")
            };
            Require(payload is not null && payload.Report is not null, "The role-specific report is required.");
            ValidateReport(payload!.Report);
            if (payload is ImplementationResult implementation)
            {
                Require(implementation.Outcome is "ReadyForReview" or "DecisionRequired", "Invalid Implementer outcome.");
                Require(implementation.CompletedWork is not null && implementation.Resolutions is not null, "Completed work and resolutions are required arrays.");
                foreach (string item in implementation.CompletedWork!) Nonempty(item, "Completed work");
                var expected = pass.SuppliedFindings.Select(f => f.Id).Order().ToArray();
                Require(implementation.Resolutions!.All(r => r is not null), "Null finding resolution.");
                Require(expected.SequenceEqual(implementation.Resolutions.Select(r => r.FindingId).Order()),
                    "Exactly one resolution is required for each supplied finding, with no extra IDs.");
                foreach (var resolution in implementation.Resolutions) Nonempty(resolution.Summary, "Resolution summary");
                ValidateDecision(implementation.Outcome, implementation.Decision);
                if (implementation.Outcome == "ReadyForReview")
                    foreach (var check in implementation.Report.Checks.Where(c => c.Result == CheckResult.Failed))
                        Require(check.LimitationClassification is "pre_existing" or "unrelated" &&
                            !string.IsNullOrWhiteSpace(check.ClassificationEvidence),
                            "A remaining failed check requires supported pre-existing or unrelated classification before ReadyForReview.");
            }
            else if (payload is ReviewResult review)
            {
                Require(review.Outcome is "Clean" or "ChangesRequired" or "DecisionRequired", "Invalid Reviewer outcome.");
                Require(review.Findings is not null && review.Findings.All(f => f is not null), "Findings must be an array.");
                Require(review.Findings!.Select(f => f.Id).Distinct().Count() == review.Findings.Length, "Duplicate finding identifiers.");
                foreach (var finding in review.Findings)
                {
                    Nonempty(finding.Id, "Finding ID"); Nonempty(finding.Summary, "Finding summary");
                    Nonempty(finding.Details, "Finding details"); Nonempty(finding.Location, "Finding location");
                    Nonempty(finding.RequestedChange, "Actionable requested change");
                }
                Require(review.Outcome != "ChangesRequired" || review.Findings.Length > 0, "ChangesRequired needs actionable findings.");
                Require(review.Outcome != "Clean" || review.Findings.Length == 0, "Clean forbids actionable findings.");
                ValidateDecision(review.Outcome, review.Decision);
                if (review.Outcome == "Clean")
                {
                    Require(review.PublicMetadata is not null, "Clean requires reviewed public metadata.");
                    ValidatePublicMetadata(review.PublicMetadata!);
                }
                else Require(review.PublicMetadata is null, "Only Clean may supply accepted public metadata.");
            }
            return new(wire.SchemaVersion, wire.OperationId, wire.PassId, wire.Role, payload, raw);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException)
        {
            // Parser diagnostics can include untrusted values. Retain a precise category without echoing content.
            throw Invalid("The Result Envelope is malformed UTF-8 JSON or violates the strict role-specific schema.");
        }
    }

    public static byte[] Serialize(Guid operationId, Guid passId, AgentRole role, PassResult payload) =>
        JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = Version, operationId, passId, role,
            payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), Json) }, Json);

    private static void ValidateReport(ResultNarrative report)
    {
        Nonempty(report.Summary, "Summary"); Nonempty(report.Narrative, "Detailed narrative");
        Require(report.Summary.Length <= 1000, "Summary exceeds 1,000 characters.");
        Require(report.Checks is not null && report.Warnings is not null && report.Limitations is not null && report.UnresolvedRisks is not null,
            "Checks, warnings, limitations, and unresolved risks are required arrays.");
        foreach (var check in report.Checks!)
        {
            Require(check is not null, "Null check."); Nonempty(check!.Name, "Check name");
            Nonempty(check.Details, "Check details"); Nonempty(check.Source, "Evidence source");
            Require(Enum.IsDefined(check.Result), "Invalid check result.");
            Require(check.LimitationClassification is null or "pre_existing" or "unrelated" or "related" or "unverified" or "deferred", "Invalid limitation classification.");
        }
        foreach (var notice in report.Warnings!.Concat(report.Limitations!).Concat(report.UnresolvedRisks!))
        {
            Require(notice is not null, "Null notice."); Nonempty(notice!.Code, "Notice code"); Nonempty(notice.Message, "Notice message");
        }
    }

    private static void ValidateDecision(string outcome, Decision? decision)
    {
        Require((outcome == "DecisionRequired") == (decision is not null), "Decision content must be present exactly for DecisionRequired.");
        if (decision is null) return;
        Nonempty(decision.UnresolvedMatter, "Unresolved matter");
        Nonempty(decision.UnsafeContinuationRationale, "Unsafe continuation rationale");
        Nonempty(decision.ProposedResolution, "Proposed resolution");
    }

    private static void ValidatePublicMetadata(PublicationMetadata metadata)
    {
        try { SubmissionMetadata.Validate(metadata); }
        catch (WorkflowException) { throw Invalid("Invalid public action summary, description, or validation."); }
    }

    private static void Nonempty(string? value, string field) => Require(!string.IsNullOrWhiteSpace(value), $"{field} is required.");
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw Invalid(message); }
    private static WorkflowException Invalid(string message) => new("invalid_result_envelope", message,
        "Inspect the retained pass report and delivery diagnostic, then use awctl resume with the exact Workflow ID.");
}
