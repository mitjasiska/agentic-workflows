using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed partial class GitHubGateway
{
    public async Task<PullRequestIdentity?> FindPullRequestAsync(PrIntent intent, CancellationToken cancellationToken)
    {
        var repository = await ValidateRepositoryAsync(intent.Binding.GitHubRepositoryId, intent.Binding.RemoteUrl,
            intent.Binding.GitHubCredentialReference, cancellationToken);
        string name = RepositoryName(repository.Name);
        string head = name.Split('/')[0] + ":" + intent.Branch;
        var list = await PullRequestRequest(intent, HttpMethod.Get, name + "/pulls?state=all&per_page=100&head=" + Uri.EscapeDataString(head), null, cancellationToken);
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() >= 100)
            throw PrError("github_pr_ambiguous", "GitHub PR lookup is malformed or exceeds the bounded complete lookup; no create will be attempted.");
        var candidates = list.EnumerateArray().ToArray();
        PullRequestIdentity? found = null;
        foreach (var candidate in candidates)
        {
            int number; long candidateId;
            try { number = candidate.GetProperty("number").GetInt32(); candidateId = candidate.GetProperty("id").GetInt64(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw PrError("github_pr_mismatch", "GitHub omitted a usable PR number."); }
            if (number <= 0) throw PrError("github_pr_mismatch", "GitHub returned an invalid PR number.");
            var detail = await PullRequestRequest(intent, HttpMethod.Get, name + "/pulls/" + number, null, cancellationToken);
            if (UnrelatedClosedRequest(intent, detail, candidateId, number)) continue;
            var verified = VerifyPullRequest(intent, detail, name);
            if (verified.Number != number || candidateId != verified.Id)
                throw PrError("github_pr_mismatch", "PR list and detail identities disagree.");
            if (found is not null) throw PrError("github_pr_ambiguous", "Multiple PRs match the frozen intent. No duplicate creation or guessed adoption is permitted.");
            found = verified;
        }
        return found;
    }

    public async Task<PullRequestIdentity> CreatePullRequestAsync(PrIntent intent, CancellationToken cancellationToken)
    {
        var repository = await ValidateRepositoryAsync(intent.Binding.GitHubRepositoryId, intent.Binding.RemoteUrl,
            intent.Binding.GitHubCredentialReference, cancellationToken);
        string name = RepositoryName(repository.Name);
        var value = await PullRequestRequest(intent, HttpMethod.Post, name + "/pulls",
            new { title = intent.Title, body = intent.Body, head = intent.Branch, @base = intent.BaseBranch, draft = false }, cancellationToken);
        return VerifyPullRequest(intent, value, name);
    }

    private async Task<JsonElement> PullRequestRequest(PrIntent intent, HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(method, "https://api.github.com/repos/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowConfiguration.ResolveCredential(intent.Binding.GitHubCredentialReference));
        request.Headers.UserAgent.ParseAdd("AgenticWorkflows/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw PrError(method == HttpMethod.Post && (int)response.StatusCode is 400 or 401 or 403 or 404 or 422 ? "github_pr_rejected" : "github_pr_unconfirmed",
                    $"GitHub returned HTTP {(int)response.StatusCode}. Rerun awctl pr to verify actual state after restoring provider access. Provider bodies and credentials are not logged.");
            return await ProviderJson.Read(response.Content, deadline.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        { throw PrError("github_pr_unconfirmed", "GitHub did not return a usable bounded response. Preserve the intent and rerun awctl pr to reconcile; an ambiguous create is never blindly repeated."); }
    }

    private static PullRequestIdentity VerifyPullRequest(PrIntent intent, JsonElement value, string repository)
    {
        try
        {
            long repositoryId = long.Parse(intent.Binding.GitHubRepositoryId, System.Globalization.CultureInfo.InvariantCulture);
            var head = value.GetProperty("head"); var target = value.GetProperty("base");
            long id = value.GetProperty("id").GetInt64(); int number = value.GetProperty("number").GetInt32();
            string url = value.GetProperty("html_url").GetString()!;
            if (id <= 0 || number <= 0 || url != $"https://github.com/{repository}/pull/{number}" ||
                head.GetProperty("repo").GetProperty("id").GetInt64() != repositoryId || target.GetProperty("repo").GetProperty("id").GetInt64() != repositoryId ||
                head.GetProperty("ref").GetString() != intent.Branch || head.GetProperty("sha").GetString() != intent.Commit ||
                target.GetProperty("ref").GetString() != intent.BaseBranch || value.GetProperty("draft").GetBoolean() ||
                value.GetProperty("title").GetString() != intent.Title || value.GetProperty("body").GetString() != intent.Body)
                throw PrError("github_pr_mismatch", "The PR does not match the frozen repository, head revision, base, public metadata, or non-draft intent. No update or duplicate creation is permitted.");
            return new(id, number, url);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw PrError("github_pr_mismatch", "GitHub omitted required immutable PR identity evidence; a URL alone is insufficient."); }
    }

    private static bool UnrelatedClosedRequest(PrIntent intent, JsonElement value, long id, int number)
    {
        // A cleaned earlier Workflow can reuse the same task branch. Its closed PR is not an update target.
        try
        {
            if (!value.TryGetProperty("state", out var state) || state.GetString() != "closed") return false;
            var head = value.GetProperty("head"); string sha = head.GetProperty("sha").GetString()!;
            string body = value.GetProperty("body").GetString() ?? "";
            long repository = long.Parse(intent.Binding.GitHubRepositoryId, System.Globalization.CultureInfo.InvariantCulture);
            return value.GetProperty("id").GetInt64() == id && value.GetProperty("number").GetInt32() == number &&
                head.GetProperty("repo").GetProperty("id").GetInt64() == repository &&
                value.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() == repository &&
                head.GetProperty("ref").GetString() == intent.Branch && sha is not null && Regex.IsMatch(sha, @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$") &&
                sha != intent.Commit && !body.Contains("AW-PR-Intent: " + intent.Id, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return false; } // Missing facts must still pass full verification, which refuses them.
    }

    private static string RepositoryName(string value) => value.Length <= 256 && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
        ? value : throw PrError("github_pr_mismatch", "GitHub did not return one safe repository name for the bound immutable ID.");
    private static WorkflowException PrError(string code, string message) => new(code, message,
        "Restore access to the bound GitHub repository and inspect the exact head, base, revision and PR metadata. Then rerun awctl pr with the Workflow ID. No database repair, duplicate create, or existing-PR update is performed.");
}
