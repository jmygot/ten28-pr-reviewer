using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class GitHubReviewPublisherTests
{
    /// <summary>Checks native review submission is one commit-pinned COMMENT with explicit current RIGHT-side anchors.</summary>
    /// <param name="renamed">Whether the current path replaces an original path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostsCommitPinnedCommentReview(bool renamed)
    {
        using var fixture = new Fixture();
        if (renamed)
        {
            fixture.Snapshot = fixture.Snapshot with { ChangedFiles = [fixture.Snapshot.ChangedFiles[0] with
                { Status = FileChangeStatus.Renamed, PreviousPath = "src/Original.cs" }] };
        }
        await fixture.PublishAsync();

        Assert.Equal(new[] { "GET", "POST" }, fixture.Http.Methods);
        Assert.Equal(new[] { "/repos/demo-owner/demo-repository/pulls/42", "/repos/demo-owner/demo-repository/pulls/42/reviews" }, fixture.Http.Paths);
        using var document = JsonDocument.Parse(Assert.Single(fixture.Http.Payloads));
        var root = document.RootElement;
        Assert.Equal(new[] { "body", "comments", "commit_id", "event" }, root.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("COMMENT", root.GetProperty("event").GetString());
        Assert.Equal(fixture.Snapshot.HeadSha, root.GetProperty("commit_id").GetString());
        Assert.Equal(fixture.Summary, root.GetProperty("body").GetString());
        var comment = Assert.Single(root.GetProperty("comments").EnumerateArray());
        Assert.Equal(new[] { "body", "line", "path", "side" }, comment.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("src/DisplayName.cs", comment.GetProperty("path").GetString());
        Assert.Equal(3, comment.GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", comment.GetProperty("side").GetString());
        Assert.Contains("[high] Nullable name is dereferenced", comment.GetProperty("body").GetString());
        Assert.Contains("Recommendation: Preserve a null fallback", comment.GetProperty("body").GetString());
    }

    /// <summary>Checks summary-only output omits comments rather than submitting null locations.</summary>
    [Fact]
    public async Task PostsSummaryOnlyWithoutComments()
    {
        using var fixture = new Fixture { Findings = [] };
        await fixture.PublishAsync();
        using var document = JsonDocument.Parse(Assert.Single(fixture.Http.Payloads));
        Assert.False(document.RootElement.TryGetProperty("comments", out _));
        Assert.Equal("COMMENT", document.RootElement.GetProperty("event").GetString());
    }

    /// <summary>Checks configured API roots and escaped repository segments are honored.</summary>
    [Fact]
    public async Task UsesConfiguredApiRoot()
    {
        using var fixture = new Fixture();
        fixture.Configuration["GITHUB_API_URL"] = "https://github.example/api/v3/";
        fixture.Snapshot = fixture.Snapshot with { Request = fixture.Snapshot.Request with
            { RepositoryOwner = "company name", RepositoryName = "project#name" } };
        fixture.Http.OnSend = (request, _, _) =>
        {
            Assert.StartsWith("https://github.example/api/v3/repos/company%20name/project%23name/pulls/42", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.Method == HttpMethod.Get ? fixture.MetadataResponse() : fixture.Success());
        };
        await fixture.PublishAsync();
        Assert.Equal(2, fixture.Http.Methods.Count);
    }

    /// <summary>Checks unsafe input fails before any HTTP boundary.</summary>
    /// <param name="kind">Invalid input variation.</param>
    [Theory]
    [InlineData("path")]
    [InlineData("old-path")]
    [InlineData("line-null")]
    [InlineData("line-zero")]
    [InlineData("line-context")]
    [InlineData("line-outside")]
    [InlineData("deleted")]
    [InlineData("patch")]
    [InlineData("severity")]
    [InlineData("title")]
    [InlineData("explanation")]
    [InlineData("recommendation")]
    [InlineData("default-findings")]
    [InlineData("too-many-findings")]
    [InlineData("duplicate-paths")]
    [InlineData("coverage")]
    [InlineData("closed")]
    [InlineData("sha")]
    [InlineData("summary")]
    [InlineData("summary-size")]
    [InlineData("comment-size")]
    [InlineData("token")]
    [InlineData("api")]
    public async Task RejectsInvalidInputBeforeHttp(string kind)
    {
        using var fixture = new Fixture();
        var finding = fixture.Findings[0];
        fixture.Findings = [kind switch
        {
            "path" => finding with { Path = "fabricated.cs" },
            "old-path" => finding with { Path = "src/Original.cs" },
            "line-null" => finding with { Line = null },
            "line-zero" => finding with { Line = 0 },
            "line-context" => finding with { Line = 2 },
            "line-outside" => finding with { Line = 999 },
            "severity" => finding with { Severity = (ReviewSeverity)99 },
            "title" => finding with { Title = " " },
            "explanation" => finding with { Explanation = " " },
            "recommendation" => finding with { Recommendation = " " },
            "comment-size" => finding with { Explanation = new string('x', 60001) },
            _ => finding
        }];
        if (kind is "patch" or "deleted" or "old-path")
            fixture.Snapshot = fixture.Snapshot with { ChangedFiles = [fixture.Snapshot.ChangedFiles[0] with
            {
                Patch = kind == "patch" ? "@@ truncated" : fixture.Snapshot.ChangedFiles[0].Patch,
                Status = kind == "deleted" ? FileChangeStatus.Deleted : FileChangeStatus.Renamed,
                PreviousPath = "src/Original.cs"
            }] };
        if (kind == "default-findings") fixture.Findings = default;
        if (kind == "too-many-findings") fixture.Findings = Enumerable.Repeat(finding, 11).ToImmutableArray();
        if (kind == "duplicate-paths") fixture.Snapshot = fixture.Snapshot with
        { ChangedFiles = [fixture.Snapshot.ChangedFiles[0], fixture.Snapshot.ChangedFiles[0]], Coverage = new(2, 2, []) };
        if (kind == "coverage") fixture.Snapshot = fixture.Snapshot with { Coverage = null };
        if (kind == "closed") fixture.Snapshot = fixture.Snapshot with { IsOpen = false };
        if (kind == "sha") fixture.Snapshot = fixture.Snapshot with { HeadSha = "invalid" };
        if (kind == "summary") fixture.Summary = " ";
        if (kind == "summary-size") fixture.Summary = new string('x', 60001);
        if (kind == "token") fixture.Configuration["GITHUB_TOKEN"] = null;
        if (kind == "api") fixture.Configuration["GITHUB_API_URL"] = "http://untrusted.example";

        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Empty(fixture.Http.Methods);
        Assert.False(error.DeliveryMayHaveSucceeded);
        Assert.Null(error.InnerException);
    }

    /// <summary>Checks changed, closed, and unverifiable PR metadata prevent all writes.</summary>
    /// <param name="kind">Metadata change or malformed response.</param>
    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("closed")]
    [InlineData("number")]
    [InlineData("missing")]
    [InlineData("state")]
    [InlineData("invalid-sha")]
    [InlineData("json")]
    [InlineData("null")]
    public async Task RecheckFailsClosed(string kind)
    {
        using var fixture = new Fixture();
        var metadata = fixture.Metadata();
        if (kind == "head") metadata["head"]!["sha"] = new string('c', 40);
        if (kind == "base") metadata["base"]!["sha"] = new string('c', 40);
        if (kind == "closed") metadata["state"] = "closed";
        if (kind == "number") metadata["number"] = 41;
        if (kind == "missing") metadata.Remove("head");
        if (kind == "state") metadata["state"] = "unknown";
        if (kind == "invalid-sha") metadata["head"]!["sha"] = "invalid";
        fixture.Http.OnSend = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK,
            kind == "json" ? "{" : kind == "null" ? "null" : metadata.ToJsonString()));

        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Equal(new[] { "GET" }, fixture.Http.Methods);
        Assert.False(error.DeliveryMayHaveSucceeded);
    }

    /// <summary>Checks definitive permission, validation, and API failures never retry or leak response text.</summary>
    /// <param name="status">Unsuccessful HTTP status.</param>
    /// <param name="duringPost">Whether the failure occurs during publication or metadata verification.</param>
    [Theory]
    [InlineData(403, false)]
    [InlineData(403, true)]
    [InlineData(422, false)]
    [InlineData(422, true)]
    [InlineData(401, true)]
    [InlineData(404, true)]
    [InlineData(500, false)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(302, true)]
    public async Task HttpFailureIsSanitizedAndNotRetried(int status, bool duringPost)
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (request, _, _) => Task.FromResult(request.Method == HttpMethod.Get && duringPost
            ? fixture.MetadataResponse() : Json((HttpStatusCode)status, "{\"message\":\"SECRET_RESPONSE_SENTINEL\"}"));

        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(duringPost ? 2 : 1, fixture.Http.Methods.Count);
        Assert.Equal(duringPost && status >= 500, error.DeliveryMayHaveSucceeded);
        Assert.DoesNotContain("SECRET_RESPONSE_SENTINEL", error.ToString());
        Assert.DoesNotContain(Fixture.Token, error.ToString());
        Assert.Null(error.InnerException);
    }

    /// <summary>Checks rate limits are identified separately from permission failures without retrying.</summary>
    /// <param name="kind">Rate-limit response and header combination.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("secondary")]
    [InlineData("retry-seconds")]
    [InlineData("retry-date")]
    [InlineData("429")]
    [InlineData("invalid-reset")]
    public async Task RateLimitReportsSafeRetryTiming(string kind)
    {
        using var fixture = new Fixture();
        var expected = fixture.Clock.GetUtcNow().AddSeconds(90);
        fixture.Http.OnSend = (request, _, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(fixture.MetadataResponse());
            var response = Json(kind == "429" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Forbidden,
                kind == "secondary" ? "{\"message\":\"You exceeded a secondary rate limit. SECRET\"}" : "{}");
            if (kind is "primary" or "invalid-reset")
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", kind == "invalid-reset" ? "999999999999999999" : expected.ToUnixTimeSeconds().ToString());
            }
            if (kind == "retry-seconds") response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            if (kind == "retry-date") response.Headers.RetryAfter = new RetryConditionHeaderValue(expected);
            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Contains("rate limited", error.Message);
        Assert.Equal(kind is "primary" or "retry-seconds" or "retry-date" ? expected : (DateTimeOffset?)null, error.RetryAt);
        Assert.False(error.DeliveryMayHaveSucceeded);
        Assert.Equal(2, fixture.Http.Methods.Count);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    /// <summary>Checks one explicit inline-location rejection produces a fresh recheck and summary-only fallback.</summary>
    [Fact]
    public async Task InlineRejectionFallsBackOnceWithFindings()
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (request, attempt, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? fixture.MetadataResponse() : attempt == 2 ? InlineRejection() : fixture.Success());

        await fixture.PublishAsync();

        Assert.Equal(new[] { "GET", "POST", "GET", "POST" }, fixture.Http.Methods);
        using var body = JsonDocument.Parse(fixture.Http.Payloads[1]);
        Assert.False(body.RootElement.TryGetProperty("comments", out _));
        Assert.Equal("COMMENT", body.RootElement.GetProperty("event").GetString());
        Assert.Equal(fixture.Snapshot.HeadSha, body.RootElement.GetProperty("commit_id").GetString());
        var summary = body.RootElement.GetProperty("body").GetString()!;
        Assert.StartsWith(fixture.Summary, summary);
        Assert.Contains("summary-only fallback", summary);
        Assert.Contains(ReviewSummaryFormatter.FormatFinding(fixture.Findings[0]), summary);
    }

    /// <summary>Checks ambiguous validation and spam responses are not treated as inline-location rejections.</summary>
    /// <param name="json">Untrusted validation response.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("{\"message\":\"Validation Failed\"}")]
    [InlineData("{\"message\":\"Validation Failed\",\"errors\":[\"line is invalid\"]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"PullRequestReviewComment\",\"field\":\"body\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"line\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"PullRequestReviewComment\",\"field\":\"line\",\"code\":\"custom\"}]}")]
    [InlineData("{\"message\":\"spam\",\"errors\":[{\"resource\":\"PullRequestReviewComment\",\"field\":\"line\",\"code\":\"invalid\"}]}")]
    public async Task Ambiguous422DoesNotFallBack(string json)
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (request, _, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? fixture.MetadataResponse() : Json(HttpStatusCode.UnprocessableEntity, json));
        await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Equal(new[] { "GET", "POST" }, fixture.Http.Methods);
    }

    /// <summary>Checks a second rejection, stale recheck, or failure during fallback cannot cause further attempts.</summary>
    /// <param name="kind">Fallback failure stage.</param>
    [Theory]
    [InlineData("stale")]
    [InlineData("closed")]
    [InlineData("recheck403")]
    [InlineData("422")]
    [InlineData("network")]
    [InlineData("deadline")]
    public async Task FallbackFailureStops(string kind)
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (request, attempt, token) =>
        {
            if (attempt == 2) return Task.FromResult(InlineRejection());
            if (attempt == 3)
            {
                if (kind == "recheck403") return Task.FromResult(Json(HttpStatusCode.Forbidden, "{}"));
                var metadata = fixture.Metadata();
                if (kind == "stale") metadata["head"]!["sha"] = new string('c', 40);
                if (kind == "closed") metadata["state"] = "closed";
                if (kind == "deadline") { fixture.Clock.Advance(TimeSpan.FromSeconds(30)); token.ThrowIfCancellationRequested(); }
                return Task.FromResult(Json(HttpStatusCode.OK, metadata.ToJsonString()));
            }
            if (attempt == 4 && kind == "network") throw new HttpRequestException("SECRET");
            return Task.FromResult(request.Method == HttpMethod.Get ? fixture.MetadataResponse() : InlineRejection());
        };

        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Equal(kind is "stale" or "closed" or "recheck403" or "deadline" ? 3 : 4, fixture.Http.Methods.Count);
        Assert.Equal(kind == "network", error.DeliveryMayHaveSucceeded);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    /// <summary>Checks summary-only reviews cannot enter the inline fallback path.</summary>
    [Fact]
    public async Task SummaryOnly422DoesNotFallBack()
    {
        using var fixture = new Fixture { Findings = [] };
        fixture.Http.OnSend = (request, _, _) => Task.FromResult(request.Method == HttpMethod.Get ? fixture.MetadataResponse() : InlineRejection());
        await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Equal(2, fixture.Http.Methods.Count);
    }

    /// <summary>Checks transport failures and deadlines are sanitized and distinguish preflight from uncertain writes.</summary>
    /// <param name="duringPost">Whether the request may already have written a review.</param>
    /// <param name="failure">Transport or timeout failure.</param>
    [Theory]
    [InlineData(false, "network")]
    [InlineData(true, "network")]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    public async Task TransportFailureAndDeadlineAreSafe(bool duringPost, string failure)
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (request, _, token) =>
        {
            if (request.Method == HttpMethod.Get && duringPost) return Task.FromResult(fixture.MetadataResponse());
            if (failure == "timeout") { fixture.Clock.Advance(TimeSpan.FromSeconds(30)); token.ThrowIfCancellationRequested(); }
            if (failure == "io") throw new IOException("SECRET");
            throw new HttpRequestException("SECRET", new Exception(Fixture.Token));
        };
        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Equal(duringPost, error.DeliveryMayHaveSucceeded);
        Assert.Equal(duringPost ? 2 : 1, fixture.Http.Methods.Count);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.DoesNotContain(Fixture.Token, error.ToString());
        Assert.Null(error.InnerException);
    }

    /// <summary>Checks caller cancellation is retained before or during either HTTP request.</summary>
    /// <param name="stage">Request index at which the caller cancels, or zero for pre-cancellation.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CallerCancellationIsPreserved(int stage)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (stage == 0) cancellation.Cancel();
        fixture.Http.OnSend = (request, attempt, token) =>
        {
            if (attempt == stage) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(request.Method == HttpMethod.Get ? fixture.MetadataResponse() : fixture.Success());
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PublishAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(stage, fixture.Http.Methods.Count);
        var report = new ReviewRunDiagnostics(fixture.Clock);
        report.Fail(error, cancellation.Token);
        Assert.Equal(stage == 2, report.DeliveryMayHaveSucceeded);
        Assert.Equal("Cancelled", report.Outcome);
    }

    /// <summary>Checks malformed or mismatched success confirmations cannot produce a successful outcome or a retry.</summary>
    /// <param name="kind">Invalid confirmation.</param>
    [Theory]
    [InlineData("json")]
    [InlineData("id")]
    [InlineData("state")]
    [InlineData("sha")]
    public async Task InvalidConfirmationIsUncertainWithoutRetry(string kind)
    {
        using var fixture = new Fixture();
        var confirmation = new JsonObject { ["id"] = 1, ["state"] = "COMMENTED", ["commit_id"] = fixture.Snapshot.HeadSha };
        if (kind == "id") confirmation["id"] = 0;
        if (kind == "state") confirmation["state"] = "PENDING";
        if (kind == "sha") confirmation["commit_id"] = new string('c', 40);
        fixture.Http.OnSend = (request, _, _) => Task.FromResult(request.Method == HttpMethod.Get ? fixture.MetadataResponse()
            : Json(HttpStatusCode.OK, kind == "json" ? "{SECRET" : confirmation.ToJsonString()));
        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.True(error.DeliveryMayHaveSucceeded);
        Assert.Equal(2, fixture.Http.Methods.Count);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    /// <summary>Checks response bounds at metadata and publication boundaries with known and unknown content lengths.</summary>
    /// <param name="duringPost">Whether the excessive body follows a POST.</param>
    /// <param name="knownLength">Whether Content-Length exposes the excessive size.</param>
    /// <param name="overLimit">Whether the response exceeds the permitted byte count.</param>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task ResponseReadsAreBounded(bool duringPost, bool knownLength, bool overLimit)
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (request, _, _) =>
        {
            if (request.Method == HttpMethod.Get && duringPost) return Task.FromResult(fixture.MetadataResponse());
            var text = request.Method == HttpMethod.Get ? fixture.Metadata().ToJsonString()
                : JsonSerializer.Serialize(new { id = 1, state = "COMMENTED", commit_id = fixture.Snapshot.HeadSha });
            var bytes = Encoding.UTF8.GetBytes(text.PadRight(GitHubReviewPublisher.MaxResponseBytes + (overLimit ? 1 : 0)));
            Stream stream = knownLength ? new MemoryStream(bytes) : new UnknownLengthStream(bytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        };
        if (!overLimit) await fixture.PublishAsync();
        else
        {
            var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
            Assert.Contains("size limit", error.Message);
            Assert.Equal(duringPost, error.DeliveryMayHaveSucceeded);
        }
        Assert.Equal(duringPost || !overLimit ? 2 : 1, fixture.Http.Methods.Count);
    }

    /// <summary>Checks model-controlled inline bodies cannot inject Markdown, HTML, or mentions.</summary>
    [Fact]
    public async Task InlineTextIsEscaped()
    {
        using var fixture = new Fixture();
        fixture.Findings = [fixture.Findings[0] with
        { Title = "# heading", Explanation = "<script>@developer</script>", Recommendation = "[click](javascript:evil)" }];
        await fixture.PublishAsync();
        using var body = JsonDocument.Parse(Assert.Single(fixture.Http.Payloads));
        var text = body.RootElement.GetProperty("comments")[0].GetProperty("body").GetString()!;
        Assert.Contains("\\# heading", text);
        Assert.Contains("&lt;script&gt;&#64;developer&lt;/script&gt;", text);
        Assert.Contains("\\[click\\]\\(javascript:evil\\)", text);
        Assert.DoesNotContain("<script>", text);
        Assert.DoesNotContain("@developer", text);
    }

    /// <summary>Checks cancellation, deadlines, and transport errors while streaming bodies retain safe failure semantics.</summary>
    /// <param name="duringPost">Whether a review may already have been written.</param>
    /// <param name="failure">Failure raised while reading the response body.</param>
    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "deadline")]
    [InlineData(true, "deadline")]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    public async Task BodyReadFailuresAreHandled(bool duringPost, string failure)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Http.OnSend = (request, _, _) =>
        {
            if (request.Method == HttpMethod.Get && duringPost) return Task.FromResult(fixture.MetadataResponse());
            var stream = new ControlledReadStream(token =>
            {
                if (failure == "cancel") cancellation.Cancel();
                if (failure == "deadline") fixture.Clock.Advance(TimeSpan.FromSeconds(30));
                token.ThrowIfCancellationRequested();
                throw new IOException("SECRET_BODY");
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        };

        if (failure == "cancel")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PublishAsync(cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync(cancellation.Token));
            Assert.Equal(duringPost, error.DeliveryMayHaveSucceeded);
            Assert.DoesNotContain("SECRET_BODY", error.ToString());
            Assert.Null(error.InnerException);
        }
        Assert.Equal(duringPost ? 2 : 1, fixture.Http.Methods.Count);
    }

    /// <summary>Checks a fallback that cannot fit safely stops without another recheck or POST.</summary>
    [Fact]
    public async Task OversizedFallbackDoesNotPost()
    {
        using var fixture = new Fixture { Summary = new string('x', 59999) };
        fixture.Http.OnSend = (request, _, _) => Task.FromResult(request.Method == HttpMethod.Get ? fixture.MetadataResponse() : InlineRejection());
        var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => fixture.PublishAsync());
        Assert.Contains("size limit", error.Message);
        Assert.Equal(new[] { "GET", "POST" }, fixture.Http.Methods);
        Assert.False(error.DeliveryMayHaveSucceeded);
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage InlineRejection() => Json(HttpStatusCode.UnprocessableEntity,
        """{"message":"Validation Failed","errors":[{"resource":"PullRequestReviewComment","field":"line","code":"invalid"}]}""");

    private sealed class Fixture : IDisposable
    {
        internal const string Token = "synthetic-publisher-token";
        internal ConfigurationManager Configuration { get; } = new();
        internal ManualReviewClock Clock { get; } = new();
        internal Handler Http { get; } = new();
        internal PullRequestSnapshot Snapshot { get; set; } = ReviewInputSelector.Select(DemoReviewFixture.Snapshot, DemoReviewFixture.Policy);
        internal ImmutableArray<ReviewFinding> Findings { get; set; } = DemoReviewFixture.Result.Findings;
        internal string Summary { get; set; } = "Synthetic summary";
        private readonly HttpClient client;
        private readonly GitHubReviewPublisher publisher;

        internal Fixture()
        {
            Configuration["GITHUB_TOKEN"] = Token;
            client = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
            publisher = new(client, Configuration, Clock, NullLogger<GitHubReviewPublisher>.Instance);
            Http.OnSend = (request, _, _) => Task.FromResult(request.Method == HttpMethod.Get ? MetadataResponse() : Success());
        }

        internal JsonObject Metadata() => new()
        {
            ["number"] = Snapshot.Request.PullRequestNumber, ["state"] = "open",
            ["head"] = new JsonObject { ["sha"] = Snapshot.HeadSha }, ["base"] = new JsonObject { ["sha"] = Snapshot.BaseSha }
        };
        internal HttpResponseMessage MetadataResponse() => Json(HttpStatusCode.OK, Metadata().ToJsonString());
        internal HttpResponseMessage Success() => Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { id = 123, state = "COMMENTED", commit_id = Snapshot.HeadSha }));
        internal Task PublishAsync(CancellationToken token = default) => publisher.PublishAsync(Snapshot, Summary, Findings, token);

        /// <inheritdoc />
        public void Dispose() { client.Dispose(); Configuration.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Methods { get; } = [];
        internal List<string> Paths { get; } = [];
        internal List<string> Payloads { get; } = [];
        internal Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = null!;

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            Paths.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Fixture.Token, request.Headers.Authorization?.Parameter);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("Ten28-PrReviewer/1.0", request.Headers.UserAgent.ToString());
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            if (request.Content is not null)
            {
                Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
                Payloads.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return await OnSend(request, Methods.Count, cancellationToken);
        }
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        /// <inheritdoc />
        public override bool CanSeek => false;
    }

    private sealed class ControlledReadStream(Func<CancellationToken, ValueTask<int>> read) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read(cancellationToken);
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
