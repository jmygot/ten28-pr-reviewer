using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.GitHub;

internal sealed class GitHubReviewEventSource(HttpClient httpClient, IConfiguration configuration) : IReviewEventSource
{
    /// <inheritdoc />
    public async Task<ReviewRequest?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(configuration["GITHUB_EVENT_NAME"], "issue_comment", StringComparison.Ordinal))
        {
            return null;
        }

        await using var payload = File.OpenRead(RequiredSetting("GITHUB_EVENT_PATH"));
        using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var root = document.RootElement;
        var issue = Property(root, "issue");
        var comment = Property(root, "comment");
        var author = Property(comment, "user");
        if (Text(root, "action") != "created"
            || Property(issue, "pull_request").ValueKind != JsonValueKind.Object
            || !string.Equals(Text(comment, "body")?.Trim(), "@company-code-reviewer review", StringComparison.Ordinal)
            || Text(author, "type") != "User")
        {
            return null;
        }

        var repository = Property(root, "repository");
        var owner = Text(Property(repository, "owner"), "login");
        var name = Text(repository, "name");
        var requester = Text(author, "login");
        var number = Property(issue, "number");
        var commentId = Property(comment, "id");
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(requester)
            || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var pullRequestNumber) || pullRequestNumber <= 0
            || commentId.ValueKind != JsonValueKind.Number || !commentId.TryGetInt64(out var id) || id <= 0)
        {
            return null;
        }

        var token = RequiredSetting("GITHUB_TOKEN");
        var apiUrl = configuration["GITHUB_API_URL"] ?? "https://api.github.com";
        var apiBase = new Uri(apiUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var repositoryPath = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using var request = CreateRequest(new Uri(apiBase, $"{repositoryPath}/pulls/{pullRequestNumber}"), token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var pullRequest = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        switch (Text(pullRequest.RootElement, "state"))
        {
            case "closed": return null;
            case "open": break;
            default: throw new InvalidOperationException("GitHub returned an invalid pull request state.");
        }

        using var permissionRequest = CreateRequest(new Uri(apiBase,
            $"{repositoryPath}/collaborators/{Uri.EscapeDataString(requester)}/permission"), token);
        using var permissionResponse = await httpClient.SendAsync(permissionRequest, cancellationToken);
        permissionResponse.EnsureSuccessStatusCode();
        if (permissionResponse.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException("GitHub returned an invalid permission response.");
        }

        await using var permissionContent = await permissionResponse.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument permissionDocument;
        try
        {
            permissionDocument = await JsonDocument.ParseAsync(permissionContent, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("GitHub returned an invalid permission response.");
        }

        using (permissionDocument)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var permission = permissionDocument.RootElement;
            if (!string.Equals(Text(Property(permission, "user"), "login"), requester, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GitHub returned an invalid permission identity.");
            }

            return Text(permission, "permission") switch
            {
                "write" or "admin" => new ReviewRequest(owner, name, pullRequestNumber, requester, id),
                "read" or "none" => null,
                _ => throw new InvalidOperationException("GitHub returned an invalid effective permission.")
            };
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Ten28-PrReviewer/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private string RequiredSetting(string key) =>
        !string.IsNullOrWhiteSpace(configuration[key])
            ? configuration[key]!
            : throw new InvalidOperationException($"Missing required configuration: {key}.");

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
