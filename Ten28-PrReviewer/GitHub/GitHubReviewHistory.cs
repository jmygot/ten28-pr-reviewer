using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.GitHub;

internal sealed class GitHubReviewHistory(HttpClient httpClient, IConfiguration configuration, TimeProvider timeProvider) : IReviewHistory
{
    internal const int MaxPages = 30;
    internal const int PageSize = 100;
    internal const int MaxResponseBytes = 10 * 1024 * 1024;
    internal const int MaxTotalResponseBytes = 32 * 1024 * 1024;

    /// <inheritdoc />
    public async Task<PriorReview?> FindSuccessfulReviewAsync(PullRequestSnapshot snapshot, string reviewKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReviewIdentity.IsHex(reviewKey, 64) || !ReviewIdentity.IsHex(snapshot.HeadSha, 40)
            || snapshot.Request.PullRequestNumber <= 0 || string.IsNullOrWhiteSpace(snapshot.Request.RepositoryOwner)
            || string.IsNullOrWhiteSpace(snapshot.Request.RepositoryName))
            throw new InvalidOperationException("Invalid review history input.");
        var token = configuration["GITHUB_TOKEN"];
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Missing required configuration: GITHUB_TOKEN.");
        if (!Uri.TryCreate((configuration["GITHUB_API_URL"] ?? "https://api.github.com").TrimEnd('/') + "/", UriKind.Absolute, out var apiBase)
            || apiBase.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Invalid GitHub API configuration.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;
        var totalBytes = 0;
        var seen = new HashSet<long>();
        var repository = snapshot.Request.RepositoryOwner.ToLowerInvariant() + "/" + snapshot.Request.RepositoryName.ToLowerInvariant();
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var path = $"repos/{Uri.EscapeDataString(snapshot.Request.RepositoryOwner)}/{Uri.EscapeDataString(snapshot.Request.RepositoryName)}/pulls/{snapshot.Request.PullRequestNumber}/reviews?per_page={PageSize}&page={page}";
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBase, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.UserAgent.ParseAdd("Ten28-PrReviewer/1.0");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var error = new HttpRequestException("GitHub review history request failed.", null, response.StatusCode);
                    DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
                    if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
                    {
                        try { retryAt = timeProvider.GetUtcNow().Add(delta); }
                        catch (ArgumentOutOfRangeException) { }
                    }
                    if (retryAt is null && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")
                        && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets)
                        && long.TryParse(resets.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
                        && reset is >= 0 and <= 253402300799) retryAt = DateTimeOffset.FromUnixTimeSeconds(reset);
                    if (retryAt is not null) error.Data["ReviewRetryAt"] = retryAt.Value;
                    throw error;
                }
                var limit = Math.Min(MaxResponseBytes, MaxTotalResponseBytes - totalBytes);
                if (response.Content.Headers.ContentLength > limit) throw InvalidHistory();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var body = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - body.Length + 1)), ct);
                    ct.ThrowIfCancellationRequested();
                    if (count == 0) break;
                    if (body.Length + count > limit) throw InvalidHistory();
                    body.Write(buffer, 0, count);
                }
                totalBytes += (int)body.Length;
                using var document = JsonDocument.Parse(body.ToArray());
                var entries = document.RootElement;
                if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > PageSize) throw InvalidHistory();
                PriorReview? match = null;
                foreach (var entry in entries.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    RequireObject(entry);
                    var id = entry.GetProperty("id").GetInt64();
                    if (id <= 0 || !seen.Add(id)) throw InvalidHistory();
                    var user = entry.GetProperty("user");
                    RequireObject(user);
                    var login = user.GetProperty("login").GetString();
                    var type = user.GetProperty("type").GetString();
                    var state = entry.GetProperty("state").GetString();
                    var commit = entry.GetProperty("commit_id").GetString();
                    var text = entry.GetProperty("body").GetString();
                    if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(type) || !ReviewIdentity.IsHex(commit, 40)
                        || state is not ("PENDING" or "COMMENTED" or "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")) throw InvalidHistory();
                    if (state != "PENDING" && (!entry.TryGetProperty("submitted_at", out var submitted)
                        || !submitted.TryGetDateTimeOffset(out _))) throw InvalidHistory();
                    if (state == "COMMENTED" && type == "Bot" && string.Equals(login, "github-actions[bot]", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(commit, snapshot.HeadSha, StringComparison.OrdinalIgnoreCase) && text is not null
                        && ReviewIdentity.TryRead(text, out var identity) && identity!.Key == reviewKey
                        && identity.HeadSha == snapshot.HeadSha.ToLowerInvariant() && identity.Repository == repository
                        && identity.PullRequest == snapshot.Request.PullRequestNumber)
                        match ??= new PriorReview(id);
                }
                if (match is not null) return match;
                if (entries.GetArrayLength() < PageSize)
                {
                    if (response.Headers.TryGetValues("Link", out var links) && links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal)))
                        throw InvalidHistory();
                    return null;
                }
            }
            throw new InvalidOperationException("Review history search exceeds its page limit.");
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new TimeoutException("Review history deadline exceeded.");
        }
        catch (HttpRequestException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sanitized = new HttpRequestException("GitHub review history request failed.", null, error.StatusCode);
            if (error.Data["ReviewRetryAt"] is DateTimeOffset retryAt) sanitized.Data["ReviewRetryAt"] = retryAt;
            throw sanitized;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw InvalidHistory();
        }
    }

    private static InvalidOperationException InvalidHistory() => new("GitHub returned incomplete, invalid, or oversized review history.");

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(property => property.Name)
            .Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count()) throw InvalidHistory();
    }
}
