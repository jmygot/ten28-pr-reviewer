using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.GitHub;

namespace Ten28_PrReviewer.Reviews;

internal sealed class ReviewRunDiagnostics(TimeProvider clock)
{
    private readonly long started = clock.GetTimestamp();
    private long stageStarted = clock.GetTimestamp();
    internal string Stage { get; private set; } = "startup";
    internal string Outcome { get; private set; } = "Running";
    internal string Reason { get; set; } = "none";
    internal ReviewRequest? Request { get; set; }
    internal string? HeadSha { get; set; }
    internal string? ReviewKey { get; set; }
    internal string? ModelId { get; set; }
    internal ReviewCoverage? Coverage { get; set; }
    internal ReviewUsage? Usage { get; set; }
    internal bool GenerationStarted { get; set; }
    internal PriorReview? PriorReview { get; set; }
    internal Dictionary<ReviewSeverity, int> SeverityCounts { get; } = [];
    internal Dictionary<string, double> StageMilliseconds { get; } = [];
    internal double ElapsedMilliseconds { get; private set; }
    internal int? HttpStatus { get; private set; }
    internal DateTimeOffset? RetryAt { get; private set; }
    internal bool DeliveryMayHaveSucceeded { get; private set; }

    internal void BeginStage(string stage)
    {
        if (Outcome == "Running") StopStage();
        else Outcome = "Running";
        Stage = stage;
        stageStarted = clock.GetTimestamp();
    }

    internal void Finish(string outcome)
    {
        if (Outcome != "Running") return;
        StopStage();
        ElapsedMilliseconds = clock.GetElapsedTime(started).TotalMilliseconds;
        Outcome = outcome;
    }

    internal void Fail(Exception error, CancellationToken cancellationToken)
    {
        if (Outcome != "Running")
        {
            Outcome = "Running";
            stageStarted = clock.GetTimestamp();
        }
        HttpStatus = error switch
        {
            GitHubReviewPublicationException publication => publication.StatusCode,
            HttpRequestException request => (int?)request.StatusCode,
            _ => error.Data["ReviewHttpStatus"] is int status && status is >= 100 and <= 599 ? status : null
        };
        RetryAt = error.Data["ReviewRetryAt"] is DateTimeOffset retryAt ? retryAt : null;
        DeliveryMayHaveSucceeded = error.Data["ReviewDeliveryMayHaveSucceeded"] is true;
        if (error is GitHubReviewPublicationException published)
        {
            RetryAt = published.RetryAt;
            DeliveryMayHaveSucceeded = published.DeliveryMayHaveSucceeded;
        }
        Reason = error switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => "cancelled",
            TimeoutException => "deadline_exceeded",
            HttpRequestException when HttpStatus is null => "transport_failure",
            IOException => "transport_failure",
            _ when HttpStatus == (int)HttpStatusCode.Forbidden => "permission_or_rate_limit",
            _ when HttpStatus == 429 => "rate_limited",
            _ when HttpStatus is not null => "http_failure",
            InvalidOperationException => "invalid_or_unavailable",
            _ => "operation_failed"
        };
        Finish(Reason == "cancelled" ? "Cancelled" : "Failed");
    }

    internal static InvalidOperationException HttpFailure(string message, int status)
    {
        var error = new InvalidOperationException(message);
        error.Data["ReviewHttpStatus"] = status;
        return error;
    }

    internal async Task WriteAsync(IConfiguration configuration, ILogger logger, bool demo)
    {
        var repository = Safe(Request is null ? null : Request.RepositoryOwner + "/" + Request.RepositoryName, configuration);
        var model = Safe(ModelId, configuration);
        var reportedModel = Usage?.Model is { } reported && ModelId is { } requested
            && reported.StartsWith(requested, StringComparison.Ordinal) ? Safe(reported, configuration) : "unknown";
        logger.LogInformation("Review run {Outcome}; stage: {Stage}; reason: {Reason}; repository: {Repository}; PR: {PullRequest}; comment: {CommentId}; requester: {Requester}; head: {HeadSha}; review key: {ReviewKey}; requested model: {Model}; reported model: {ReportedModel}; generation started: {GenerationStarted}; input tokens: {InputTokens}; output tokens: {OutputTokens}; elapsed ms: {ElapsedMilliseconds}; HTTP: {HttpStatus}; retry at UTC: {RetryAt}; delivery may have succeeded: {DeliveryMayHaveSucceeded}; prior review: {PriorReviewId}; {Coverage}; severities: {SeverityCounts}; stage ms: {StageMilliseconds}.",
            Outcome, Stage, Reason, repository, Request?.PullRequestNumber, Request?.CommentId, Safe(Request?.Requester, configuration),
            Safe(HeadSha, configuration), Safe(ReviewKey, configuration), model, reportedModel, GenerationStarted,
            Usage?.InputTokens, Usage?.OutputTokens, ElapsedMilliseconds, HttpStatus, RetryAt, DeliveryMayHaveSucceeded, PriorReview?.Id,
            Coverage is null ? "Coverage unknown" : ReviewInputSelector.CoverageStatement(Coverage),
            string.Join(", ", SeverityCounts.Select(pair => $"{pair.Key}: {pair.Value}")),
            string.Join(", ", StageMilliseconds.Select(pair => $"{pair.Key}: {pair.Value.ToString("F1", CultureInfo.InvariantCulture)}")));
        var path = configuration["GITHUB_STEP_SUMMARY"];
        if (string.IsNullOrWhiteSpace(path)) return;
        var text = new StringBuilder("\n## Company PR Reviewer" + (demo ? " (offline simulation)" : "") + "\n\n| Field | Value |\n| --- | --- |\n");
        void Row(string name, object? value) => text.Append("| ").Append(name).Append(" | ")
            .Append(ReviewSummaryFormatter.EscapeMarkdown(value is null ? "unknown" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unknown")).Append(" |\n");
        Row("Outcome", Outcome); Row("Stage", Stage); Row("Reason", Reason); Row("Repository", repository);
        Row("PR", Request?.PullRequestNumber); Row("Comment", Request?.CommentId); Row("Requester", Safe(Request?.Requester, configuration));
        Row("Head SHA", Safe(HeadSha, configuration)); Row("Review key", Safe(ReviewKey, configuration));
        Row("Requested model", model); Row("Reported model", reportedModel); Row("Generation started", GenerationStarted);
        Row("Input tokens", Usage?.InputTokens); Row("Output tokens", Usage?.OutputTokens); Row("Elapsed ms", ElapsedMilliseconds);
        Row("HTTP status", HttpStatus); Row("Retry at UTC", RetryAt?.ToString("O"));
        Row("Delivery may have succeeded", DeliveryMayHaveSucceeded); Row("Prior review", PriorReview?.Id);
        Row("Coverage", Coverage is null ? "unknown" : ReviewInputSelector.CoverageStatement(Coverage));
        foreach (var pair in SeverityCounts) Row(pair.Key + " findings", pair.Value);
        foreach (var pair in StageMilliseconds) Row(pair.Key + " ms", pair.Value);
        try { await File.AppendAllTextAsync(path, text.ToString(), Encoding.UTF8, CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            logger.LogWarning("Could not write the GitHub step summary; the review exit status is unchanged.");
        }
    }

    private void StopStage() => StageMilliseconds[Stage] = StageMilliseconds.GetValueOrDefault(Stage)
        + clock.GetElapsedTime(stageStarted).TotalMilliseconds;

    private static string Safe(string? value, IConfiguration configuration)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var secrets = new[] { configuration["GITHUB_TOKEN"], configuration["DEEPSEEK_API_KEY"],
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"), Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") };
        if (value.Length > 256 || secrets.Any(secret => !string.IsNullOrEmpty(secret) && value.Contains(secret, StringComparison.Ordinal))
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && !"-_.+/[]@".Contains(character))) return "redacted";
        return value;
    }
}
