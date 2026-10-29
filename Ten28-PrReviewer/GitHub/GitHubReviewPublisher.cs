using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.GitHub;

internal sealed class GitHubReviewPublicationException(
    string message, int? statusCode = null, DateTimeOffset? retryAt = null, bool deliveryMayHaveSucceeded = false)
    : Exception(message)
{
    internal int? StatusCode { get; } = statusCode;
    internal DateTimeOffset? RetryAt { get; } = retryAt;
    internal bool DeliveryMayHaveSucceeded { get; } = deliveryMayHaveSucceeded;
}

internal sealed class GitHubReviewPublisher(
    HttpClient httpClient, IConfiguration configuration, TimeProvider timeProvider, ILogger<GitHubReviewPublisher> logger)
    : IReviewPublisher
{
    internal const int MaxResponseBytes = 1024 * 1024;
    private const int MaxBodyCharacters = 60000;

    /// <inheritdoc />
    public async Task PublishAsync(PullRequestSnapshot snapshot, string summary, ImmutableArray<ReviewFinding> findings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(snapshot, summary, findings, cancellationToken);
        var token = configuration["GITHUB_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubReviewPublicationException("Missing required configuration: GITHUB_TOKEN.");
        if (!Uri.TryCreate((configuration["GITHUB_API_URL"] ?? "https://api.github.com").TrimEnd('/') + "/",
            UriKind.Absolute, out var apiBase) || apiBase.Scheme != Uri.UriSchemeHttps)
            throw new GitHubReviewPublicationException("Invalid GitHub API configuration.");
        var path = $"repos/{Uri.EscapeDataString(snapshot.Request.RepositoryOwner)}/{Uri.EscapeDataString(snapshot.Request.RepositoryName)}/pulls/{snapshot.Request.PullRequestNumber}";
        var comments = findings.Select(finding => new
        {
            path = finding.Path, line = finding.Line!.Value, side = "RIGHT", body = ReviewSummaryFormatter.FormatFinding(finding)
        }).ToArray();
        if (comments.Any(comment => comment.body.Length > MaxBodyCharacters))
            throw new GitHubReviewPublicationException("Review comment exceeds the publication size limit.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;
        var deliveryUncertain = false;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var check = CreateRequest(HttpMethod.Get, path))
                using (var response = await httpClient.SendAsync(check, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    using var metadata = await ReadJsonAsync(response, ct);
                    if (response.StatusCode != HttpStatusCode.OK) throw Failure(response, metadata);
                    var root = metadata?.RootElement ?? default;
                    var number = Property(root, "number");
                    var state = Text(root, "state");
                    var head = Text(Property(root, "head"), "sha");
                    var baseSha = Text(Property(root, "base"), "sha");
                    if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var prNumber)
                        || prNumber != snapshot.Request.PullRequestNumber || state is not ("open" or "closed")
                        || !IsSha(head) || !IsSha(baseSha))
                        throw new GitHubReviewPublicationException("GitHub returned invalid pre-publication metadata.");
                    if (state != "open" || head != snapshot.HeadSha || baseSha != snapshot.BaseSha)
                        throw new GitHubReviewPublicationException("The pull request changed or closed during review; publication aborted.");
                }

                ct.ThrowIfCancellationRequested();
                using var post = CreateRequest(HttpMethod.Post, path + "/reviews");
                post.Content = attempt == 0 && comments.Length > 0
                    ? JsonContent.Create(new { commit_id = snapshot.HeadSha, body = summary, @event = "COMMENT", comments })
                    : JsonContent.Create(new { commit_id = snapshot.HeadSha, body = summary, @event = "COMMENT" });
                deliveryUncertain = true;
                using var posted = await httpClient.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, ct);
                deliveryUncertain = posted.IsSuccessStatusCode || (int)posted.StatusCode >= 500;
                using var result = await ReadJsonAsync(posted, ct);
                if (posted.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    var root = result?.RootElement ?? default;
                    var id = Property(root, "id");
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var reviewId) || reviewId <= 0
                        || Text(root, "state") != "COMMENTED" || Text(root, "commit_id") != snapshot.HeadSha)
                        throw new GitHubReviewPublicationException("GitHub returned an invalid publication confirmation.",
                            (int)posted.StatusCode, deliveryMayHaveSucceeded: true);
                    ct.ThrowIfCancellationRequested();
                    return;
                }
                if (attempt == 0 && comments.Length > 0 && posted.StatusCode == HttpStatusCode.UnprocessableEntity
                    && IsInlineLocationRejection(result))
                {
                    summary += "\n\n### Inline findings (summary-only fallback)\n\n"
                        + "GitHub rejected inline locations; validated findings are included here.\n\n"
                        + string.Join("\n\n", findings.Select(ReviewSummaryFormatter.FormatFinding));
                    if (summary.Length > MaxBodyCharacters)
                        throw new GitHubReviewPublicationException("Summary-only fallback exceeds the publication size limit.");
                    logger.LogWarning("GitHub rejected inline locations (HTTP 422); attempting one summary-only COMMENT review after rechecking the pull request.");
                    continue;
                }
                throw Failure(posted, result, deliveryUncertain);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = new OperationCanceledException(cancellationToken);
                cancelled.Data["ReviewDeliveryMayHaveSucceeded"] = deliveryUncertain;
                throw cancelled;
            }
            throw new GitHubReviewPublicationException("GitHub publication deadline exceeded.", deliveryMayHaveSucceeded: deliveryUncertain);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new GitHubReviewPublicationException("GitHub publication transport failed.", deliveryMayHaveSucceeded: deliveryUncertain);
        }
        catch (GitHubReviewPublicationException error) when (deliveryUncertain && !error.DeliveryMayHaveSucceeded)
        {
            throw new GitHubReviewPublicationException(error.Message, error.StatusCode, error.RetryAt, true);
        }

        HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, new Uri(apiBase, relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("Ten28-PrReviewer/1.0");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            return request;
        }
    }

    private static void Validate(PullRequestSnapshot snapshot, string summary, ImmutableArray<ReviewFinding> findings,
        CancellationToken cancellationToken)
    {
        var coverage = snapshot.Coverage;
        if (!snapshot.IsOpen || !IsSha(snapshot.HeadSha) || !IsSha(snapshot.BaseSha)
            || snapshot.Request.PullRequestNumber <= 0 || string.IsNullOrWhiteSpace(snapshot.Request.RepositoryOwner)
            || string.IsNullOrWhiteSpace(snapshot.Request.RepositoryName) || string.IsNullOrWhiteSpace(summary)
            || summary.Length > MaxBodyCharacters || findings.IsDefault || findings.Length > 10
            || snapshot.ChangedFiles.IsDefault || coverage is null || coverage.SkippedFiles.IsDefault
            || coverage.SelectedFiles != snapshot.ChangedFiles.Length
            || coverage.TotalChangedFiles != (long)coverage.SelectedFiles + coverage.SkippedFiles.Length)
            throw new GitHubReviewPublicationException("Invalid review publication input.");
        var files = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);
        foreach (var file in snapshot.ChangedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || !files.TryAdd(file.Path, file))
                throw new GitHubReviewPublicationException("Invalid selected review paths.");
        }
        var diffs = new Dictionary<string, ParsedReviewDiff?>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (finding is null || !Enum.IsDefined(finding.Severity) || finding.Line is null or <= 0
                || string.IsNullOrWhiteSpace(finding.Path) || string.IsNullOrWhiteSpace(finding.Title)
                || string.IsNullOrWhiteSpace(finding.Explanation) || string.IsNullOrWhiteSpace(finding.Recommendation)
                || !files.TryGetValue(finding.Path, out var file) || file.Status == FileChangeStatus.Deleted)
                throw new GitHubReviewPublicationException("Invalid inline review finding.");
            if (!diffs.TryGetValue(file.Path, out var diff))
                diffs.Add(file.Path, diff = UnifiedDiffParser.Parse(file, cancellationToken));
            if (diff is null || !diff.AddedLines.ContainsKey(finding.Line.Value))
                throw new GitHubReviewPublicationException("Invalid inline review location.");
        }
    }

    private GitHubReviewPublicationException Failure(HttpResponseMessage response, JsonDocument? document,
        bool deliveryUncertain = false)
    {
        var status = (int)response.StatusCode;
        var message = Text(document?.RootElement ?? default, "message") ?? "";
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0");
        if (status == 429 || (status == 403 && (remaining || response.Headers.RetryAfter is not null
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))))
        {
            DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
            if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            {
                try { retryAt = timeProvider.GetUtcNow().Add(delta); }
                catch (ArgumentOutOfRangeException) { }
            }
            if (retryAt is null && remaining && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                && long.TryParse(resetValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
                && reset is >= 0 and <= 253402300799)
                retryAt = DateTimeOffset.FromUnixTimeSeconds(reset);
            return new GitHubReviewPublicationException("GitHub publication rate limited; no automatic retry.", status, retryAt);
        }
        return new GitHubReviewPublicationException(status switch
        {
            403 => "GitHub publication permission denied.",
            422 => "GitHub rejected the review; no further publication attempt.",
            _ => "GitHub publication request failed."
        }, status, deliveryMayHaveSucceeded: deliveryUncertain);
    }

    private static bool IsInlineLocationRejection(JsonDocument? document)
    {
        var root = document?.RootElement ?? default;
        if (Text(root, "message") != "Validation Failed") return false;
        var errors = Property(root, "errors");
        if (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return false;
        foreach (var error in errors.EnumerateArray())
        {
            if (Text(error, "resource") != "PullRequestReviewComment" || Text(error, "code") != "invalid"
                || Text(error, "field") is not ("path" or "line" or "side" or "position")) return false;
        }
        return true;
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new GitHubReviewPublicationException("GitHub publication response exceeds its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxResponseBytes - body.Length + 1)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            if (body.Length + count > MaxResponseBytes)
                throw new GitHubReviewPublicationException("GitHub publication response exceeds its size limit.");
            body.Write(buffer, 0, count);
        }
        try { return JsonDocument.Parse(body.ToArray()); }
        catch (JsonException) { return null; }
    }

    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
