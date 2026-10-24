using System.Text.RegularExpressions;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;
using Tomlyn;
using Tomlyn.Model;

namespace AgenticWorkflows.Infrastructure;

public sealed partial class WorkflowConfiguration(string globalFile, string dataDirectory) : IWorkflowConfiguration
{
    public const string ProjectFilename = ".agentic-workflows.toml";
    public string ProjectFile(string canonicalCheckout) => Path.Combine(canonicalCheckout, ProjectFilename);
    public string DeliveryPath(Guid workflowId, Guid sessionId, Guid operationId) => Path.Combine(dataDirectory, "deliveries", workflowId.ToString("N"), sessionId.ToString("N"), operationId.ToString("N"), "result.json");
    public WorkflowSettings ReadGlobal() => File.Exists(globalFile) ? Settings(Read(globalFile), true) : new();
    public void ValidateContent(string[] credentialReferences, string content)
    {
        if (RawCredential().IsMatch(content) || credentialReferences.Distinct().Any(reference => content.Contains(ResolveCredential(reference), StringComparison.Ordinal)))
            throw new WorkflowException("raw_credential_rejected", "Input contains a raw provider credential or private key and was not persisted or included in a Handoff.",
                "Remove the secret from the source or delivery, rotate exposed credentials through the same reference, and retry the retained operation.");
    }

    public ProjectSettings ReadProject(string canonicalCheckout)
    {
        var table = Read(ProjectFile(canonicalCheckout));
        Allowed(table, ["base", "managed_root", "linear_project_id", "linear_team_id", "in_progress_state_id", "github_repository_id", "herdr_target", "linear_credential", "github_credential", "defaults"]);
        string root = Required(table, "managed_root");
        if (!Path.IsPathFullyQualified(root)) throw Error("managed_root must be an absolute path.");
        string linearCredential = Required(table, "linear_credential");
        string githubCredential = Required(table, "github_credential");
        ValidateReference(linearCredential); ValidateReference(githubCredential);
        if (table.TryGetValue("defaults", out var configuredDefaults) && configuredDefaults is not TomlTable)
            throw Error("defaults must be a table.");
        return new(Path.GetFullPath(canonicalCheckout), Required(table, "base"), Path.GetFullPath(root),
            Required(table, "linear_project_id"), Required(table, "linear_team_id"), Optional(table, "in_progress_state_id"),
            Required(table, "github_repository_id"), Required(table, "herdr_target"), linearCredential, githubCredential,
            table.TryGetValue("defaults", out var defaults) && defaults is TomlTable settings ? Settings(settings, false) : new());
    }

    private static TomlTable Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) throw Error("Configuration exceeds 64 KiB.");
            string content = File.ReadAllText(path);
            if (RawCredential().IsMatch(content)) throw Error("Configuration contains a raw credential or private key; use only credential references.");
            return TomlSerializer.Deserialize<TomlTable>(content) ?? throw Error("Configuration is empty.");
        }
        catch (Exception exception) when (exception is IOException or TomlException or UnauthorizedAccessException)
        { throw Error("Cannot read valid TOML configuration. Check the selected file and its syntax; configuration values are not echoed."); }
    }

    private static WorkflowSettings Settings(TomlTable table, bool global)
    {
        if (global)
        {
            Allowed(table, ["defaults"]);
            if (!table.TryGetValue("defaults", out var defaults)) return new();
            if (defaults is not TomlTable nested) throw Error("defaults must be a table.");
            table = nested;
        }
        Allowed(table, ["implementer_model", "reviewer_model", "reasoning", "implementer_session", "reviewer_session", "instructions_policy", "max_passes"]);
        var settings = new WorkflowSettings(Optional(table, "implementer_model"), Optional(table, "reviewer_model"),
            Optional(table, "reasoning"), Optional(table, "implementer_session"), Optional(table, "reviewer_session"),
            Optional(table, "instructions_policy"), Optional(table, "max_passes"));
        return settings;
    }
    private static void Allowed(TomlTable table, string[] names)
    {
        if (table.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal)))
            throw Error("Configuration contains an unsupported field. Raw credential values and identity overrides are not supported settings.");
    }
    private static string Required(TomlTable table, string key) => Optional(table, key) ?? throw Error($"Missing required configuration field: {key}.");
    private static string? Optional(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value)) return null;
        if (value is not string text || string.IsNullOrWhiteSpace(text)) throw Error($"{key} must be a nonempty string.");
        return text;
    }
    public static void ValidateReference(string reference)
    {
        if (!CredentialReference().IsMatch(reference)) throw Error("Credentials must use env:VARIABLE references.");
    }
    public static string ResolveCredential(string reference)
    {
        ValidateReference(reference);
        return Environment.GetEnvironmentVariable(reference[4..]) is { Length: > 0 } secret ? secret
            : throw new WorkflowException("credential_unavailable", $"Credential reference {reference} is unavailable to the control plane.", "Inject the referenced environment variable into the service account and retry.");
    }
    private static WorkflowException Error(string message) => new("invalid_configuration", message, "Correct the canonical Project or Global TOML file; invalid selected values never fall back.");
    [GeneratedRegex(@"^env:[A-Z_][A-Z0-9_]{0,127}$")]
    private static partial Regex CredentialReference();
    [GeneratedRegex(@"(?:lin_api_[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)")]
    private static partial Regex RawCredential();
}
