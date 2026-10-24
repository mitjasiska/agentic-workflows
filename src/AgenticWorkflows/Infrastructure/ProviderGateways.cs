using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed class LinearGateway(HttpClient client) : ILinearGateway
{
    public async Task<LinearTask> ReadTaskAsync(string identifierOrId, string credentialReference, CancellationToken cancellationToken)
    {
        var data = await Query("""
            query Task($id:String!) { issue(id:$id) { id identifier title description updatedAt url project { id } team { id } state { id type } } }
            """, new { id = identifierOrId }, credentialReference, cancellationToken);
        var issue = data.GetProperty("issue");
        if (issue.ValueKind != JsonValueKind.Object) throw Error("linear_task_missing", "The bound Linear task no longer exists or is inaccessible.");
        return new(Get(issue, "id"), Get(issue, "identifier"), Get(issue, "title"), Optional(issue, "description") ?? "",
            Get(issue, "updatedAt"), Get(issue.GetProperty("project"), "id"), Get(issue.GetProperty("team"), "id"),
            Get(issue.GetProperty("state"), "id"), Get(issue.GetProperty("state"), "type"), Get(issue, "url"));
    }

    public async Task<(ProviderProjection Project, ProviderProjection Team, ProviderProjection State)> ValidateProjectAsync(
        string projectId, string teamId, string? stateId, string credentialReference, CancellationToken cancellationToken)
    {
        var data = await Query("""
            query Project($project:String!,$team:String!) { project(id:$project) { id name teams { nodes { id } } } team(id:$team) { id name states { nodes { id name type } } } }
            """, new { project = projectId, team = teamId }, credentialReference, cancellationToken);
        var project = data.GetProperty("project"); var team = data.GetProperty("team");
        if (Get(project, "id") != projectId || Get(team, "id") != teamId ||
            !project.GetProperty("teams").GetProperty("nodes").EnumerateArray().Any(e => Get(e, "id") == teamId))
            throw Error("linear_identity_mismatch", "The configured Linear Project and team relationship is missing or replaced.");
        var states = team.GetProperty("states").GetProperty("nodes").EnumerateArray()
            .Where(e => Get(e, "type") == "started" && (stateId is null || Get(e, "id") == stateId)).ToArray();
        if (states.Length != 1) throw Error("linear_in_progress_mapping", "Select one concrete started state belonging to the bound Linear team; the mapping is missing or ambiguous.");
        return (new(projectId, Get(project, "name")), new(teamId, Get(team, "name")), new(Get(states[0], "id"), Get(states[0], "name")));
    }

    public async Task MoveToInProgressAsync(IntegrationBinding binding, CancellationToken cancellationToken)
    {
        var current = await ReadTaskAsync(binding.LinearTaskId, binding.LinearCredentialReference, cancellationToken);
        if (current.Id != binding.LinearTaskId || current.ProjectId != binding.LinearProjectId || current.TeamId != binding.LinearTeamId)
            throw Error("linear_identity_mismatch", "The task is no longer in the bound Project and team.");
        await ValidateProjectAsync(binding.LinearProjectId, binding.LinearTeamId, binding.InProgressStateId, binding.LinearCredentialReference, cancellationToken);
        if (current.StateId == binding.InProgressStateId) return;
        if (current.StateType is "completed" or "canceled") throw Error("linear_terminal_task", "The task became Done or Canceled before startup completed; no status write was performed.");
        var data = await Query("""
            mutation Start($id:String!,$state:String!) { issueUpdate(id:$id,input:{stateId:$state}) { success issue { id state { id } } } }
            """, new { id = binding.LinearTaskId, state = binding.InProgressStateId }, binding.LinearCredentialReference, cancellationToken);
        var update = data.GetProperty("issueUpdate");
        if (!update.GetProperty("success").GetBoolean() || Get(update.GetProperty("issue"), "id") != binding.LinearTaskId ||
            Get(update.GetProperty("issue").GetProperty("state"), "id") != binding.InProgressStateId)
            throw Error("linear_transition_unconfirmed", "Linear did not confirm the configured In Progress transition. No agent was launched.");
    }

    private async Task<JsonElement> Query(string query, object variables, string reference, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linear.app/graphql");
        request.Headers.TryAddWithoutValidation("Authorization", WorkflowConfiguration.ResolveCredential(reference));
        request.Content = JsonContent.Create(new { query, variables });
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw Error("linear_unavailable", $"Linear access failed with HTTP {(int)response.StatusCode}; credentials and response content are not logged.");
            var root = await ProviderJson.Read(response.Content, deadline.Token);
            if (root.TryGetProperty("errors", out _)) throw Error("linear_query_failed", "Linear could not establish the requested provider facts.");
            return root.GetProperty("data").Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw Error("linear_unavailable", "Linear did not return a usable bounded response; existing workflow evidence is preserved."); }
    }
    private static string Get(JsonElement element, string field) => Optional(element, field) ?? throw Error("linear_missing_identity", "Linear omitted a required identity or source field.");
    private static string? Optional(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static WorkflowException Error(string code, string message) => new(code, message, "Check the bound Linear target and credential reference, then use awctl resume; AW never changes the task to Done.");
}

public sealed class GitHubGateway(HttpClient client) : IGitHubGateway
{
    public async Task<ProviderProjection> ValidateRepositoryAsync(string repositoryId, string remoteUrl, string credentialReference, CancellationToken cancellationToken)
    {
        if (!long.TryParse(repositoryId, out long id) || id <= 0 || !GitWorkflowRepository.SafeGitHubRemote(remoteUrl))
            throw Error("Select the immutable numeric GitHub repository ID and a credential-free GitHub origin.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repositories/" + id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowConfiguration.ResolveCredential(credentialReference));
        request.Headers.UserAgent.ParseAdd("AgenticWorkflows/0.1");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw Error("The bound GitHub repository is unavailable to the configured credential reference.");
            var value = await ProviderJson.Read(response.Content, deadline.Token);
            if (value.GetProperty("id").GetInt64() != id) throw Error("The GitHub repository identity changed.");
            string name = value.GetProperty("full_name").GetString()!;
            string normalized = remoteUrl.StartsWith("git@github.com:", StringComparison.Ordinal) ? remoteUrl[15..] : new Uri(remoteUrl).AbsolutePath.TrimStart('/');
            if (normalized.EndsWith(".git", StringComparison.Ordinal)) normalized = normalized[..^4];
            if (!normalized.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                    throw Error("The canonical origin does not identify one GitHub repository.");
                // Old names may remain valid aliases after a rename. Resolve the origin to its stable ID;
                // a replacement repository at the old name must not inherit the binding.
                var uri = new Uri("https://api.github.com/repos/" + normalized);
                bool verified = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    using var origin = new HttpRequestMessage(HttpMethod.Get, uri);
                    origin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowConfiguration.ResolveCredential(credentialReference));
                    origin.Headers.UserAgent.ParseAdd("AgenticWorkflows/0.1");
                    using var resolved = await client.SendAsync(origin, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if ((int)resolved.StatusCode is 301 or 302 or 307 or 308)
                    {
                        var location = resolved.Headers.Location;
                        if (location is null) throw Error("GitHub returned a redirect without an identity target.");
                        uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        if (uri.Scheme != "https" || uri.Host != "api.github.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
                            throw Error("GitHub origin resolution left the bound provider; credentials were not forwarded.");
                        continue;
                    }
                    if (!resolved.IsSuccessStatusCode) throw Error("The canonical origin is missing or inaccessible.");
                    var actual = await ProviderJson.Read(resolved.Content, deadline.Token);
                    if (actual.GetProperty("id").GetInt64() != id) throw Error("The canonical origin now points to a different GitHub repository.");
                    verified = true;
                    break;
                }
                if (!verified) throw Error("The canonical origin could not be resolved within the bounded redirect limit.");
            }
            return new(repositoryId, name);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        { throw Error("GitHub did not return a usable identity response."); }
    }
    private static WorkflowException Error(string message) => new("github_identity_unavailable", message, "Restore access to the immutable GitHub target; configuration changes apply only to new Workflows.");
}

internal static class ProviderJson
{
    internal static async Task<JsonElement> Read(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(bytes, token);
            if (count == 0) break;
            if (buffer.Length + count > 2 * 1024 * 1024) throw new JsonException("Provider response exceeded the bound.");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        return document.RootElement.Clone();
    }
}
