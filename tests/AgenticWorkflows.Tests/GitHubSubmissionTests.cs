using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class GitHubSubmissionTests
{
    [Fact]
    public async Task Unrelated_closed_PR_from_cleaned_workflow_does_not_block_new_intent()
    {
        using var f = new ProviderFixture(); f.Handler.Exists = true; f.Handler.Mismatch = "historical";
        Assert.Null(await f.Gateway.FindPullRequestAsync(f.Intent, default)); Assert.Equal(0, f.Handler.Creates);
    }
    [Fact]
    public async Task Create_and_lost_response_reconciliation_verify_complete_target_and_request()
    {
        using var f = new ProviderFixture();
        Assert.Null(await f.Gateway.FindPullRequestAsync(f.Intent, default));
        f.Handler.LoseResponse = true;
        Assert.Equal("github_pr_unconfirmed", (await Assert.ThrowsAsync<WorkflowException>(() => f.Gateway.CreatePullRequestAsync(f.Intent, default))).Code);
        var found = await f.Gateway.FindPullRequestAsync(f.Intent, default);
        Assert.Equal(new PullRequestIdentity(999, 7, "https://github.com/owner/repo/pull/7"), found);
        Assert.Equal(1, f.Handler.Creates);
        var request = JsonNode.Parse(f.Handler.CreatedBody!)!;
        Assert.Equal(f.Intent.Title, request["title"]!.GetValue<string>());
        Assert.Equal(f.Intent.Body, request["body"]!.GetValue<string>());
        Assert.Equal(f.Intent.Branch, request["head"]!.GetValue<string>());
        Assert.Equal("main", request["base"]!.GetValue<string>());
        Assert.False(request["draft"]!.GetValue<bool>());
        Assert.Contains(f.Handler.Uris, u => u.Contains("state=all", StringComparison.Ordinal) && u.Contains("head=owner%3Aaw%2FDEV-128%2Fwhole", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("head-repo")]
    [InlineData("base-repo")]
    [InlineData("head")]
    [InlineData("sha")]
    [InlineData("base")]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("url")]
    [InlineData("draft")]
    [InlineData("id")]
    [InlineData("multiple")]
    [InlineData("incomplete")]
    [InlineData("historical-null-sha")]
    public async Task PR_lookup_rejects_similar_or_ambiguous_results_without_writes(string mismatch)
    {
        using var f = new ProviderFixture(); f.Handler.Exists = true; f.Handler.Mismatch = mismatch;
        var error = await Assert.ThrowsAsync<WorkflowException>(() => f.Gateway.FindPullRequestAsync(f.Intent, default));
        Assert.Contains(error.Code, new[] { "github_pr_mismatch", "github_pr_ambiguous" });
        Assert.Equal(0, f.Handler.Creates);
        Assert.DoesNotContain("synthetic-private-credential", error.Message + error.Guidance);
    }

    [Theory]
    [InlineData(403, "github_pr_rejected")]
    [InlineData(422, "github_pr_rejected")]
    [InlineData(500, "github_pr_unconfirmed")]
    public async Task Definite_rejection_and_ambiguous_server_failure_are_distinct(int status, string code)
    {
        using var f = new ProviderFixture(); f.Handler.Status = status;
        var error = await Assert.ThrowsAsync<WorkflowException>(() => f.Gateway.CreatePullRequestAsync(f.Intent, default));
        Assert.Equal(code, error.Code); Assert.Equal(1, f.Handler.Creates);
        Assert.DoesNotContain("provider secret response", error.Message);
    }

    private sealed class ProviderFixture : IDisposable
    {
        private readonly string variable = "AW_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        internal PrIntent Intent { get; }
        internal Provider Handler { get; }
        private readonly HttpClient client;
        internal GitHubGateway Gateway { get; }
        internal ProviderFixture()
        {
            Environment.SetEnvironmentVariable(variable, "synthetic-private-credential");
            Guid id = Guid.NewGuid();
            var binding = new IntegrationBinding(Guid.NewGuid(), "/fixture", "/fixture/.git", LocalGitTransport.Url,
                "task", "project", "team", "started", "1", "local:test", "env:UNUSED", "env:" + variable);
            Intent = new(id, Guid.NewGuid(), Guid.NewGuid(), binding, new("sha1", new('a', 40), new('b', 40)), "bytes",
                new('c', 40), "main", "aw/DEV-128/whole", new('c', 40), new('d', 40), null,
                "feat: implement submission (DEV-128)\n\nTask-Review: test\n", "feat: implement submission (DEV-128)",
                "## Summary\n\nPublic change\n\n<!-- AW-PR-Intent: " + id + " -->\n", Guid.NewGuid(), false, WorkflowPhase.Submission, null, DateTimeOffset.UtcNow);
            Handler = new(Intent); client = new(Handler); Gateway = new(client);
        }
        public void Dispose() { client.Dispose(); Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class Provider(PrIntent intent) : HttpMessageHandler
    {
        internal bool Exists;
        internal bool LoseResponse;
        internal int Status = 201;
        internal int Creates;
        internal string? CreatedBody;
        internal string? Mismatch;
        internal readonly List<string> Uris = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Uris.Add(request.RequestUri.OriginalString);
            if (request.RequestUri.AbsolutePath == "/repositories/1") return Reply(200, new { id = 1, full_name = "owner/repo" });
            if (request.Method == HttpMethod.Post)
            {
                Creates++; CreatedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (Status != 201) return Reply(Status, new { message = "provider secret response" });
                Exists = true;
                if (LoseResponse) throw new HttpRequestException("response lost");
                return Reply(201, Detail());
            }
            if (request.RequestUri.Query.Length > 0)
                return Reply(200, !Exists ? Array.Empty<object>() : Mismatch == "multiple" ? new object[] { new { id = 999, number = 7 }, new { id = 1000, number = 8 } } :
                    Mismatch == "incomplete" ? Enumerable.Range(0, 100).Select(n => (object)new { id = 999 + n, number = 7 + n }).ToArray() : [new { id = 999, number = 7 }]);
            return Reply(200, Detail());
        }
        private JsonNode Detail()
        {
            var value = JsonSerializer.SerializeToNode(new
            {
                id = 999, number = 7, html_url = "https://github.com/owner/repo/pull/7", draft = false, state = "open",
                title = intent.Title, body = intent.Body,
                head = new { repo = new { id = 1 }, @ref = intent.Branch, sha = intent.Commit },
                @base = new { repo = new { id = 1 }, @ref = intent.BaseBranch, sha = intent.BaseCommit }
            })!;
            switch (Mismatch)
            {
                case "head-repo": value["head"]!["repo"]!["id"] = 2; break;
                case "base-repo": value["base"]!["repo"]!["id"] = 2; break;
                case "head": value["head"]!["ref"] = intent.Branch + "-similar"; break;
                case "sha": value["head"]!["sha"] = new string('e', 40); break;
                case "base": value["base"]!["ref"] = "other"; break;
                case "title": value["title"] = "different"; break;
                case "body": value["body"] = "different"; break;
                case "url": value["html_url"] = "https://evil.example/pull/7"; break;
                case "draft": value["draft"] = true; break;
                case "id": value["id"] = 1000; break;
                case "historical": value["state"] = "closed"; value["head"]!["sha"] = new string('e', 40); value["body"] = "A prior workflow."; break;
                case "historical-null-sha": value["state"] = "closed"; value["head"]!["sha"] = null; break;
            }
            return value;
        }
        private static HttpResponseMessage Reply(int status, object value) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    }
}
