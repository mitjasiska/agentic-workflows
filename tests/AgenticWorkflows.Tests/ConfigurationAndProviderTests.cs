using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class ConfigurationAndProviderTests
{
    [Fact]
    public void Safe_configuration_example_parses_and_rejects_raw_secret_fields()
    {
        string root = Path.Combine(Path.GetTempPath(), "aw-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "examples", "project.toml"));
            string path = Path.Combine(root, WorkflowConfiguration.ProjectFilename); File.WriteAllText(path, source);
            var configuration = new WorkflowConfiguration(Path.Combine(root, "global.toml"), Path.Combine(root, "data"));
            var project = configuration.ReadProject(root);
            Assert.Equal("env:AW_LINEAR_TOKEN", project.LinearCredentialReference);
            Assert.Equal("6", project.Defaults.MaxPasses);
            File.WriteAllText(path, "token = 'must-not-be-accepted'\n" + source);
            Assert.Equal("invalid_configuration", Assert.Throws<WorkflowException>(() => configuration.ReadProject(root)).Code);
            File.WriteAllText(path, source.Replace("linear_credential = \"env:AW_LINEAR_TOKEN\"", "linear_credential = \"raw-token-value\"", StringComparison.Ordinal));
            Assert.Throws<WorkflowException>(() => configuration.ReadProject(root));
            File.WriteAllText(path, source.Replace("[defaults]", "defaults = 'invalid'\n[unrecognized]", StringComparison.Ordinal));
            Assert.Throws<WorkflowException>(() => configuration.ReadProject(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Credential_rotation_is_live_and_rejections_do_not_echo_credentials()
    {
        string variable = "AW_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var configuration = new WorkflowConfiguration("/unused", "/unused");
        try
        {
            Environment.SetEnvironmentVariable(variable, "synthetic-first-credential");
            var error = Assert.Throws<WorkflowException>(() => configuration.ValidateContent(["env:" + variable], "Accidental synthetic-first-credential disclosure"));
            Assert.DoesNotContain("synthetic-first-credential", error.Message + error.Guidance);
            Environment.SetEnvironmentVariable(variable, "synthetic-rotated-credential");
            Assert.Equal("synthetic-rotated-credential", WorkflowConfiguration.ResolveCredential("env:" + variable));
            Assert.Throws<WorkflowException>(() => configuration.ValidateContent(["env:" + variable], "synthetic-rotated-credential"));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData(42, true)]
    [InlineData(99, false)]
    public async Task GitHub_rename_refreshes_metadata_but_replacement_identity_is_rejected(int originId, bool accepted)
    {
        string variable = "AW_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "synthetic-github-credential");
        try
        {
            using var handler = new Replies(
                new(HttpStatusCode.OK) { Content = Json("{\"id\":42,\"full_name\":\"owner/new-name\"}") },
                new(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://api.github.com/repositories/42") } },
                new(HttpStatusCode.OK) { Content = Json($"{{\"id\":{originId},\"full_name\":\"owner/new-name\"}}") });
            using var client = new HttpClient(handler);
            var gateway = new GitHubGateway(client);
            if (accepted)
                Assert.Equal("owner/new-name", (await gateway.ValidateRepositoryAsync("42", "https://github.com/owner/old-name.git", "env:" + variable, default)).Name);
            else
                Assert.Equal("github_identity_unavailable", (await Assert.ThrowsAsync<WorkflowException>(() => gateway.ValidateRepositoryAsync("42", "https://github.com/owner/old-name.git", "env:" + variable, default))).Code);
            Assert.Equal(3, handler.Calls);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void Changed_documentation_links_resolve_within_the_repository()
    {
        string root = RepositoryRoot();
        string[] documents = ["README.md", "docs/user/managed-workflows.md", "docs/development/managed-workflow-smoke-test.md",
            "docs/development/local-control-plane.md", "docs/reference/local-control-interface.md", "docs/reference/result-envelope.md"];
        foreach (string document in documents)
        {
            string full = Path.Combine(root, document);
            foreach (Match match in Regex.Matches(File.ReadAllText(full), @"\[[^\]]*\]\(([^)]+)\)"))
            {
                string link = match.Groups[1].Value.Split('#')[0];
                if (link.Length == 0 || Uri.TryCreate(link, UriKind.Absolute, out _)) continue;
                string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, link));
                Assert.True(File.Exists(target) || Directory.Exists(target), $"Missing documentation link in {document}: {link}");
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflows.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository fixture root not found.");
    }
    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
    private sealed class Replies(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("synthetic-github-credential", request.Headers.Authorization!.Parameter);
            return Task.FromResult(responses[Calls++]);
        }
    }
}
