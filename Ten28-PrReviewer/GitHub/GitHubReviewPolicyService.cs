using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.GitHub;

internal sealed class GitHubReviewPolicyService(HttpClient httpClient, IConfiguration configuration) : IReviewPolicyService
{
    internal const int MaxEnvelopeBytes = 128 * 1024;
    internal const int MaxConfigurationBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task<ReviewPolicy> GetPolicyAsync(PullRequestSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = ReviewPolicySettings.ReadCentral(configuration);
        if (snapshot.BaseSha.Length != 40 || !snapshot.BaseSha.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid trusted base commit for review configuration.");
        var token = configuration["GITHUB_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Missing required configuration: GITHUB_TOKEN.");

        var apiBase = new Uri((configuration["GITHUB_API_URL"] ?? "https://api.github.com").TrimEnd('/') + "/", UriKind.Absolute);
        var request = snapshot.Request;
        var path = $"repos/{Uri.EscapeDataString(request.RepositoryOwner)}/{Uri.EscapeDataString(request.RepositoryName)}/contents/.github/ai-review.json?ref={Uri.EscapeDataString(snapshot.BaseSha)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBase, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        message.Headers.UserAgent.ParseAdd("Ten28-PrReviewer/1.0");
        message.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.StatusCode == HttpStatusCode.NotFound) return policy;
        if (response.StatusCode != HttpStatusCode.OK)
            throw ReviewRunDiagnostics.HttpFailure("GitHub review configuration lookup failed.", (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxEnvelopeBytes)
            throw new InvalidOperationException("Review configuration response exceeds its size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxEnvelopeBytes - body.Length + 1)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            if (body.Length + count > MaxEnvelopeBytes)
                throw new InvalidOperationException("Review configuration response exceeds its size limit.");
            body.Write(buffer, 0, count);
        }

        try
        {
            body.Position = 0;
            using var envelope = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "file"
                || !root.TryGetProperty("path", out var filePath) || filePath.ValueKind != JsonValueKind.String || filePath.GetString() != ".github/ai-review.json"
                || !root.TryGetProperty("encoding", out var encoding) || encoding.ValueKind != JsonValueKind.String || encoding.GetString() != "base64"
                || !root.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out var bytes) || bytes < 0 || bytes > MaxConfigurationBytes
                || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Invalid review configuration file response.");

            if (root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                throw new InvalidOperationException("Ambiguous review configuration file response.");

            var decoded = Convert.FromBase64String(content.GetString()!);
            if (decoded.Length > MaxConfigurationBytes || decoded.Length != bytes)
                throw new InvalidOperationException("Invalid review configuration file size.");
            using var settings = JsonDocument.Parse(decoded);
            cancellationToken.ThrowIfCancellationRequested();
            return ReviewPolicySettings.Apply(policy, settings.RootElement);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new InvalidOperationException("Invalid review configuration file.");
        }
    }
}
