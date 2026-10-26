using System.Net;
using System.Text;
using System.Text.Json;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Tests;

public sealed class LinearGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_classification_reads_complete_labels_without_relabeling(bool more)
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Issue("started")))!;
        value["issue"]!["labels"] = JsonSerializer.SerializeToNode(new
        { nodes = new[] { new { name = "Feature" }, new { name = "Research" }, new { name = "Unrelated" } }, pageInfo = new { hasNextPage = more } });
        using var fixture = new Transport(value);
        if (more)
            Assert.Equal("linear_labels_incomplete", (await Assert.ThrowsAsync<WorkflowException>(() => fixture.Gateway.ReadTaskAsync("issue", fixture.Reference, default))).Code);
        else
            Assert.Equal(new[] { "Feature", "Research", "Unrelated" }, (await fixture.Gateway.ReadTaskAsync("issue", fixture.Reference, default)).Labels);
        Assert.Single(fixture.Requests); Assert.DoesNotContain("mutation", fixture.Requests[0]);
        Assert.Contains("labels(first:100)", fixture.Requests[0]);
    }
    [Theory]
    [InlineData(null, 1, true)]
    [InlineData(null, 2, false)]
    [InlineData("started", 2, true)]
    [InlineData("missing", 1, false)]
    public async Task In_progress_mapping_requires_one_concrete_started_state(string? selected, int states, bool accepted)
    {
        using var fixture = new Transport(Project(states));
        if (accepted)
            Assert.Equal("started", (await fixture.Gateway.ValidateProjectAsync("project", "team", selected, fixture.Reference, default)).State.Id);
        else
            Assert.Equal("linear_in_progress_mapping", (await Assert.ThrowsAsync<WorkflowException>(() => fixture.Gateway.ValidateProjectAsync("project", "team", selected, fixture.Reference, default))).Code);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("completed")]
    [InlineData("canceled")]
    public async Task Already_started_or_terminal_task_does_not_receive_a_status_mutation(string state)
    {
        using var fixture = new Transport(Issue(state), Project(1));
        var binding = Binding(fixture.Reference);
        if (state == "started") await fixture.Gateway.MoveToInProgressAsync(binding, default);
        else Assert.Equal("linear_terminal_task", (await Assert.ThrowsAsync<WorkflowException>(() => fixture.Gateway.MoveToInProgressAsync(binding, default))).Code);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.All(fixture.Requests, request => Assert.DoesNotContain("mutation", request, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_transition_requires_exact_task_and_state_confirmation()
    {
        using var fixture = new Transport(Issue("unstarted"), Project(1),
            new { issueUpdate = new { success = true, issue = new { id = "different-task", state = new { id = "started" } } } });
        Assert.Equal("linear_transition_unconfirmed", (await Assert.ThrowsAsync<WorkflowException>(() => fixture.Gateway.MoveToInProgressAsync(Binding(fixture.Reference), default))).Code);
        Assert.Equal(3, fixture.Requests.Count);
        using var mutation = JsonDocument.Parse(fixture.Requests[^1]);
        Assert.Equal("issue", mutation.RootElement.GetProperty("variables").GetProperty("id").GetString());
        Assert.Equal("started", mutation.RootElement.GetProperty("variables").GetProperty("state").GetString());
    }

    private static object Issue(string type) => new { issue = new { id = "issue", identifier = "DEV-123", title = "Task", description = "Requirements", updatedAt = "r1", url = "https://linear.app/example/issue/DEV-123",
        project = new { id = "project" }, team = new { id = "team" }, state = new { id = type, type } } };
    private static object Project(int count) => new { project = new { id = "project", name = "Project", teams = new { nodes = new[] { new { id = "team" } } } },
        team = new { id = "team", name = "Team", states = new { nodes = Enumerable.Range(0, count).Select(n => new { id = n == 0 ? "started" : "other", name = "Active", type = "started" }).ToArray() } } };
    private static IntegrationBinding Binding(string reference) => new(Guid.NewGuid(), "/repo", "/repo/.git", "https://github.com/example/repo", "issue", "project", "team", "started", "1", "local:test", reference, "env:UNUSED");

    private sealed class Transport : HttpMessageHandler
    {
        private readonly string variable = "AW_LINEAR_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        private readonly Queue<object> replies;
        private readonly HttpClient client;
        internal string Reference => "env:" + variable;
        internal LinearGateway Gateway { get; }
        internal List<string> Requests { get; } = [];
        internal Transport(params object[] replies)
        {
            this.replies = new(replies);
            Environment.SetEnvironmentVariable(variable, "synthetic-linear-credential");
            client = new(this, disposeHandler: false); Gateway = new(client);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.linear.app/graphql", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-linear-credential", request.Headers.GetValues("Authorization").Single());
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = replies.Dequeue() }), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { client.Dispose(); Environment.SetEnvironmentVariable(variable, null); }
            base.Dispose(disposing);
        }
    }
}
