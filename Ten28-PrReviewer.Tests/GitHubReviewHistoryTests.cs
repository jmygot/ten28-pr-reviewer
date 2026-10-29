using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class GitHubReviewHistoryTests
{
    /// <summary>Checks a matching submitted bot review is found on a later page with authenticated bounded requests.</summary>
    [Fact]
    public async Task FindsMatchOnSecondPage()
    {
        using var fixture = new Fixture();
        fixture.Handler.OnSend = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-history-token", request.Headers.Authorization.Parameter);
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.NotEmpty(request.Headers.UserAgent);
            var page = fixture.Handler.Calls;
            Assert.Equal($"https://api.github.com/repos/demo-owner/demo-repository/pulls/42/reviews?per_page=100&page={page}", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(page == 1 ? Page(1) : new JsonArray(Review(101, fixture.Identity.Marker))));
        };
        Assert.Equal(new PriorReview(101), await fixture.FindAsync());
        Assert.Equal(2, fixture.Handler.Calls);
    }

    /// <summary>Checks a complete exact-size page is followed by an empty page to establish no match.</summary>
    [Fact]
    public async Task FullPageRequiresContinuation()
    {
        using var fixture = new Fixture();
        fixture.Handler.OnSend = (_, _) => Task.FromResult(Json(fixture.Handler.Calls == 1 ? Page(1) : new JsonArray()));
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(2, fixture.Handler.Calls);
    }

    /// <summary>Checks untrusted authors, non-success states, and altered or legacy markers cannot suppress generation.</summary>
    /// <param name="kind">Nonmatching review variation.</param>
    [Theory]
    [InlineData("human")]
    [InlineData("other-bot")]
    [InlineData("wrong-type")]
    [InlineData("pending")]
    [InlineData("dismissed")]
    [InlineData("approved")]
    [InlineData("changes-requested")]
    [InlineData("commit")]
    [InlineData("marker-head")]
    [InlineData("marker-repository")]
    [InlineData("marker-pr")]
    [InlineData("marker-key")]
    [InlineData("legacy")]
    [InlineData("malformed-marker")]
    public async Task IgnoresNonmatchingReviews(string kind)
    {
        using var fixture = new Fixture();
        var identity = fixture.Identity;
        if (kind == "marker-head") identity = identity with { HeadSha = new string('c', 40) };
        if (kind == "marker-repository") identity = identity with { Repository = "other/repository" };
        if (kind == "marker-pr") identity = identity with { PullRequest = 43 };
        var review = Review(1, identity.Marker);
        if (kind == "human") review["user"]!["login"] = "developer";
        if (kind == "other-bot") review["user"]!["login"] = "other[bot]";
        if (kind == "wrong-type") review["user"]!["type"] = "User";
        if (kind == "pending") { review["state"] = "PENDING"; review.Remove("submitted_at"); }
        if (kind == "dismissed") review["state"] = "DISMISSED";
        if (kind == "approved") review["state"] = "APPROVED";
        if (kind == "changes-requested") review["state"] = "CHANGES_REQUESTED";
        if (kind == "commit") review["commit_id"] = new string('c', 40);
        if (kind == "marker-key") review["body"] = identity.Marker.Replace("v1", "v2");
        if (kind == "legacy") review["body"] = "Legacy unmarked review";
        if (kind == "malformed-marker") review["body"] = "<!-- company-pr-reviewer:v1 invalid -->";
        fixture.Handler.OnSend = (_, _) => Task.FromResult(Json(new JsonArray(review)));
        Assert.Null(await fixture.FindAsync());
    }

    /// <summary>Checks malformed responses and incomplete pagination fail closed with sanitized exceptions.</summary>
    /// <param name="kind">Invalid response variation.</param>
    [Theory]
    [InlineData("json")]
    [InlineData("root")]
    [InlineData("entry")]
    [InlineData("id")]
    [InlineData("user")]
    [InlineData("state")]
    [InlineData("commit")]
    [InlineData("submitted")]
    [InlineData("duplicate-id")]
    [InlineData("too-many")]
    [InlineData("incomplete")]
    public async Task InvalidHistoryFailsClosed(string kind)
    {
        using var fixture = new Fixture();
        var review = Review(1, fixture.Identity.Marker);
        if (kind == "id") review["id"] = 0;
        if (kind == "user") review["user"] = null;
        if (kind == "state") review["state"] = "unknown SECRET";
        if (kind == "commit") review["commit_id"] = "SECRET";
        if (kind == "submitted") review.Remove("submitted_at");
        if (kind == "incomplete") review["body"] = "Unmarked review";
        var body = new JsonArray(review).ToJsonString();
        if (kind == "json") body = "{SECRET";
        if (kind == "root") body = "{}";
        if (kind == "entry") body = "[null]";
        if (kind == "duplicate-id") body = new JsonArray(Review(), Review()).ToJsonString();
        if (kind == "too-many") body = new JsonArray(Enumerable.Range(1, 101).Select(id => (JsonNode)Review(id)).ToArray()).ToJsonString();
        fixture.Handler.OnSend = (_, _) =>
        {
            var response = GitHubReviewPublisherTests.Json(HttpStatusCode.OK, body);
            if (kind == "incomplete") response.Headers.Add("Link", "<https://evil.example/SECRET>; rel=\"next\"");
            return Task.FromResult(response);
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FindAsync());
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Null(error.InnerException);
    }

    /// <summary>Checks status and transport failures are sanitized and never retried.</summary>
    /// <param name="status">HTTP status or zero for transport failure.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task FailuresAreNotRetried(int status)
    {
        using var fixture = new Fixture();
        fixture.Handler.OnSend = (_, _) => status == 0 ? throw new HttpRequestException("SECRET")
            : Task.FromResult(GitHubReviewPublisherTests.Json((HttpStatusCode)status, "SECRET"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.FindAsync());
        Assert.Equal(status == 0 ? null : (HttpStatusCode?)status, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Equal(1, fixture.Handler.Calls);
    }

    /// <summary>Checks the page cap stops an incomplete search without treating it as absence.</summary>
    [Fact]
    public async Task EnforcesPageLimit()
    {
        using var fixture = new Fixture();
        fixture.Handler.OnSend = (_, _) => Task.FromResult(Json(Page((fixture.Handler.Calls - 1) * 100 + 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FindAsync());
        Assert.Equal(30, fixture.Handler.Calls);
    }

    /// <summary>Checks streamed and declared response sizes honor the exact response boundary.</summary>
    /// <param name="unknownLength">Whether the response omits Content-Length.</param>
    /// <param name="delta">Bytes beyond or at the response limit.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task EnforcesResponseSize(bool unknownLength, int delta)
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes("[]" + new string(' ', GitHubReviewHistory.MaxResponseBytes - 2 + delta));
        fixture.Handler.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = unknownLength ? new UnknownLengthContent(new MemoryStream(bytes)) : new ByteArrayContent(bytes) });
        if (delta == 0) Assert.Null(await fixture.FindAsync());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FindAsync());
    }

    /// <summary>Checks aggregate bytes are bounded across otherwise valid complete pages.</summary>
    [Fact]
    public async Task EnforcesAggregateSize()
    {
        using var fixture = new Fixture();
        fixture.Handler.OnSend = (_, _) =>
        {
            var body = Page((fixture.Handler.Calls - 1) * 100 + 1).ToJsonString();
            return Task.FromResult(GitHubReviewPublisherTests.Json(HttpStatusCode.OK,
                body + new string(' ', 9 * 1024 * 1024 - body.Length)));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FindAsync());
        Assert.Equal(4, fixture.Handler.Calls);
    }

    /// <summary>Checks caller cancellation and the shared deadline during headers and streamed body reads.</summary>
    /// <param name="duringBody">Whether cancellation occurs while reading the response body.</param>
    /// <param name="caller">Whether the caller cancels rather than the deadline expiring.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelsPendingReads(bool duringBody, bool caller)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        void Cancel() { if (caller) cancellation.Cancel(); else fixture.Clock.Advance(TimeSpan.FromSeconds(30)); }
        fixture.Handler.OnSend = (_, token) =>
        {
            if (!duringBody) { Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new UnknownLengthContent(new CancelStream(Cancel)) });
        };
        if (caller)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.FindAsync(cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => fixture.FindAsync());
    }

    /// <summary>Checks configured enterprise roots are used without following untrusted review links.</summary>
    [Fact]
    public async Task UsesConfiguredApiRoot()
    {
        using var fixture = new Fixture();
        fixture.Configuration["GITHUB_API_URL"] = "https://github.example/api/v3";
        fixture.Handler.OnSend = (request, _) =>
        {
            Assert.StartsWith("https://github.example/api/v3/repos/", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(new JsonArray()));
        };
        Assert.Null(await fixture.FindAsync());
    }

    /// <summary>Checks invalid trust-boundary inputs and credentials are rejected before HTTP access.</summary>
    /// <param name="kind">Invalid input or configuration.</param>
    [Theory]
    [InlineData("token")]
    [InlineData("api")]
    [InlineData("key")]
    [InlineData("sha")]
    [InlineData("number")]
    [InlineData("owner")]
    public async Task InvalidInputPreventsHttp(string kind)
    {
        using var fixture = new Fixture();
        var snapshot = DemoReviewFixture.Snapshot;
        if (kind == "token") fixture.Configuration["GITHUB_TOKEN"] = null;
        if (kind == "api") fixture.Configuration["GITHUB_API_URL"] = "http://untrusted.example";
        if (kind == "sha") snapshot = snapshot with { HeadSha = "invalid" };
        if (kind == "number") snapshot = snapshot with { Request = snapshot.Request with { PullRequestNumber = 0 } };
        if (kind == "owner") snapshot = snapshot with { Request = snapshot.Request with { RepositoryOwner = "" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FindAsync(default, snapshot, kind == "key" ? "invalid" : null));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    /// <summary>Checks retry timing survives sanitized history exceptions and is available to terminal diagnostics.</summary>
    /// <param name="kind">Rate-limit timing representation.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("delta")]
    [InlineData("date")]
    [InlineData("invalid-reset")]
    public async Task PreservesSafeRetryTiming(string kind)
    {
        using var fixture = new Fixture();
        var expected = fixture.Clock.GetUtcNow().AddSeconds(90);
        fixture.Handler.OnSend = (_, _) =>
        {
            var response = GitHubReviewPublisherTests.Json(HttpStatusCode.Forbidden, "SECRET");
            if (kind is "primary" or "invalid-reset")
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", kind == "primary" ? expected.ToUnixTimeSeconds().ToString() : "999999999999999999");
            }
            if (kind == "delta") response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            if (kind == "date") response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(expected);
            return Task.FromResult(response);
        };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.FindAsync());
        var report = new ReviewRunDiagnostics(fixture.Clock);
        report.Fail(error, CancellationToken.None);
        Assert.Equal(kind == "invalid-reset" ? null : (DateTimeOffset?)expected, report.RetryAt);
        Assert.Equal(403, report.HttpStatus);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    /// <summary>Checks all pages share one deadline rather than resetting the budget per page.</summary>
    [Fact]
    public async Task DeadlineSpansPages()
    {
        using var fixture = new Fixture();
        fixture.Handler.OnSend = (_, token) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(17));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Json(Page(1)));
        };
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.FindAsync());
        Assert.Equal(2, fixture.Handler.Calls);
    }

    internal static JsonObject Review(long id = 1, string? marker = null) => new()
    {
        ["id"] = id, ["user"] = new JsonObject { ["login"] = "github-actions[bot]", ["type"] = "Bot" },
        ["state"] = "COMMENTED", ["commit_id"] = new string('b', 40), ["body"] = marker ?? "Unmarked review",
        ["submitted_at"] = "2026-10-29T00:00:00Z"
    };

    private static JsonArray Page(int first) => new(Enumerable.Range(first, 100).Select(id => (JsonNode)Review(id)).ToArray());
    private static HttpResponseMessage Json(JsonArray array) => GitHubReviewPublisherTests.Json(HttpStatusCode.OK, array.ToJsonString());

    private sealed class Fixture : IDisposable
    {
        internal ConfigurationManager Configuration { get; } = new();
        internal Handler Handler { get; } = new();
        internal ManualReviewClock Clock { get; } = new();
        internal ReviewIdentity Identity { get; } = ReviewIdentity.Create(DemoReviewFixture.Snapshot, DemoReviewFixture.Policy, "model", "1.0");
        private readonly HttpClient client;
        internal Fixture() { Configuration["GITHUB_TOKEN"] = "synthetic-history-token"; client = new HttpClient(Handler); }
        internal Task<PriorReview?> FindAsync(CancellationToken token = default, PullRequestSnapshot? snapshot = null, string? key = null) =>
            new GitHubReviewHistory(client, Configuration, Clock).FindSuccessfulReviewAsync(snapshot ?? DemoReviewFixture.Snapshot, key ?? Identity.Key, token);
        /// <inheritdoc />
        public void Dispose() { client.Dispose(); Configuration.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = (_, _) => Task.FromResult(Json(new JsonArray()));
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return OnSend(request, cancellationToken); }
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CancelStream(Action cancel) : MemoryStream
    {
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancel(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(0); }
    }
}
