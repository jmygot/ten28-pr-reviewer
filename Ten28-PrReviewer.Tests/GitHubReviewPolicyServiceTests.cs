using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class GitHubReviewPolicyServiceTests
{
    /// <summary>Checks defaults are versioned and an absent file uses central rules without reading an error body.</summary>
    [Fact]
    public async Task AbsentConfigurationUsesCentralDefaults()
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var policy = await fixture.ReadAsync();

        Assert.Equal("central-v1", policy.Version);
        Assert.Equal(new ReviewLimits(30, 250000, 40000, 10), policy.Limits);
        foreach (var rule in new[] { "correctness", "security", "regressions", "validation", "performance", "transaction safety", "actionable", "evidence", "stylistic" })
            Assert.Contains(rule, policy.Instructions);
        Assert.Equal(new[] { ".cs", ".csproj", ".json", ".sql", ".ts", ".tsx", ".js" }, policy.AllowedExtensions);
        Assert.Equal(1, fixture.Http.Calls);
    }

    /// <summary>Checks repository settings use only the receiving repository and immutable base SHA with authenticated reads.</summary>
    [Fact]
    public async Task ConfigurationIsPinnedToTrustedBaseSha()
    {
        using var fixture = new Fixture();
        fixture.Configuration["GITHUB_API_URL"] = "https://github.example/api/v3";
        fixture.Http.OnSend = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://github.example/api/v3/repos/demo-owner/demo-repository/contents/.github/ai-review.json?ref=" + DemoReviewFixture.Snapshot.BaseSha,
                request.RequestUri!.AbsoluteUri);
            Assert.DoesNotContain(DemoReviewFixture.Snapshot.HeadSha, request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Fixture.Token, request.Headers.Authorization.Parameter);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return Task.FromResult(Response(Envelope("{}")));
        };

        await fixture.ReadAsync();
        Assert.Equal(1, fixture.Http.Calls);
    }

    /// <summary>Checks repository settings tighten central values, narrow extensions, and retain mandatory exclusions.</summary>
    [Fact]
    public async Task OverridesOnlyTightenCentralPolicy()
    {
        using var fixture = new Fixture();
        fixture.Configuration["Review:MaxFindings"] = "5";
        fixture.Configuration["Review:MaxChangedFilesAnalyzed"] = "20";
        fixture.Configuration["Review:ExcludedGlobs:0"] = "**/central/**";
        fixture.Http.Body = Envelope("""{"maxChangedFilesAnalyzed":10,"maxTotalPatchBytes":10000,"maxSinglePatchBytes":1000,"maxFindings":2,"allowedExtensions":[".CS",".sql"],"excludedGlobs":["**/vendor/**"]}""");

        var policy = await fixture.ReadAsync();

        Assert.Equal(new ReviewLimits(10, 10000, 1000, 2), policy.Limits);
        Assert.Equal(new[] { ".CS", ".sql" }, policy.AllowedExtensions);
        Assert.Contains("**/bin/**", policy.ExcludedGlobs);
        Assert.Contains("**/central/**", policy.ExcludedGlobs);
        Assert.Contains("**/vendor/**", policy.ExcludedGlobs);
        Assert.Equal(ReviewPolicySettings.Defaults.Instructions, policy.Instructions);
    }

    /// <summary>Checks invalid repository settings fail before generation instead of falling back or disclosing values.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken json")]
    [InlineData("{\"maxFindings\":0}")]
    [InlineData("{\"maxFindings\":-1}")]
    [InlineData("{\"maxFindings\":11}")]
    [InlineData("{\"maxFindings\":1.5}")]
    [InlineData("{\"maxFindings\":\"2\"}")]
    [InlineData("{\"maxFindings\":null}")]
    [InlineData("{\"maxChangedFilesAnalyzed\":31}")]
    [InlineData("{\"maxTotalPatchBytes\":250001}")]
    [InlineData("{\"maxSinglePatchBytes\":40001}")]
    [InlineData("{\"maxFindings\":1,\"maxFindings\":2}")]
    [InlineData("{\"instructions\":\"synthetic-github-token\"}")]
    [InlineData("{\"MaxFindings\":1}")]
    [InlineData("{\"allowedExtensions\":[\".exe\"]}")]
    [InlineData("{\"allowedExtensions\":[42]}")]
    [InlineData("{\"allowedExtensions\":\".cs\"}")]
    [InlineData("{\"excludedGlobs\":[\"!**/bin/**\"]}")]
    [InlineData("{\"excludedGlobs\":[\"../source/**\"]}")]
    [InlineData("{\"excludedGlobs\":[\"/source/**\"]}")]
    [InlineData("{\"excludedGlobs\":[\"**/*.c?\"]}")]
    [InlineData("{\"excludedGlobs\":[\"a**b.cs\"]}")]
    [InlineData("{\"excludedGlobs\":[\"\"]}")]
    public async Task InvalidRepositoryConfigurationStopsPipeline(string settings)
    {
        using var fixture = new Fixture();
        fixture.Http.Body = Envelope(settings);
        var downstream = new ReviewOrchestratorTests.PipelineSpy();
        var orchestrator = new ReviewOrchestrator(downstream, downstream, fixture.Service, downstream, downstream, downstream, downstream);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync());

        Assert.DoesNotContain(Fixture.Token, error.ToString());
        Assert.Equal(new[] { "event", "repository" }, downstream.Calls);
        Assert.Empty(downstream.Publications);
    }

    /// <summary>Checks repository values cannot undo tighter effective central settings.</summary>
    [Theory]
    [InlineData("{\"maxFindings\":3}")]
    [InlineData("{\"allowedExtensions\":[\".sql\"]}")]
    public async Task RepositoryCannotIncreaseCentralAllowances(string settings)
    {
        using var fixture = new Fixture();
        fixture.Configuration["Review:MaxFindings"] = "2";
        fixture.Configuration["Review:AllowedExtensions:0"] = ".cs";
        fixture.Http.Body = Envelope(settings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
    }

    /// <summary>Checks malformed central settings stop before network access.</summary>
    [Theory]
    [InlineData("MaxFindings", "0")]
    [InlineData("MaxFindings", "11")]
    [InlineData("MaxFindings", "")]
    [InlineData("MaxFindings", "x")]
    [InlineData("MaxTotalPatchBytes", "250001")]
    [InlineData("MaxSinglePatchBytes", "40001")]
    [InlineData("MaxChangedFilesAnalyzed", "31")]
    [InlineData("Instructions", "synthetic-github-token")]
    [InlineData("AllowedExtensions:0", ".exe")]
    [InlineData("ExcludedGlobs:0", "../unsafe/**")]
    [InlineData("AllowedExtensions", ".cs")]
    public async Task InvalidCentralConfigurationStopsBeforeHttp(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Configuration["Review:" + key] = value;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.DoesNotContain(Fixture.Token, error.ToString());
        Assert.Equal(0, fixture.Http.Calls);
    }

    /// <summary>Checks malformed content envelopes and decoded data fail closed.</summary>
    [Theory]
    [InlineData("type", "\"dir\"")]
    [InlineData("type", "null")]
    [InlineData("path", "\"other.json\"")]
    [InlineData("encoding", "\"none\"")]
    [InlineData("size", "-1")]
    [InlineData("size", "3")]
    [InlineData("size", "65537")]
    [InlineData("content", "42")]
    [InlineData("content", "\"not base64!\"")]
    public async Task InvalidEnvelopeFails(string property, string value)
    {
        using var fixture = new Fixture();
        var envelope = JsonNode.Parse(Envelope("{}"))!;
        envelope[property] = JsonNode.Parse(value);
        fixture.Http.Body = envelope.ToJsonString();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
    }

    /// <summary>Checks API failures do not silently select defaults or retry.</summary>
    [Theory]
    [InlineData(204)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ApiFailureIsSanitized(int status)
    {
        using var fixture = new Fixture();
        fixture.Http.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Fixture.Token) });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.DoesNotContain(Fixture.Token, error.ToString());
        Assert.Equal(1, fixture.Http.Calls);
    }

    /// <summary>Checks streamed envelope limits at and around the boundary with and without Content-Length.</summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task EnvelopeSizeBoundary(int delta, bool unknownLength)
    {
        using var fixture = new Fixture();
        var envelope = Envelope("{}");
        envelope += new string(' ', GitHubReviewPolicyService.MaxEnvelopeBytes + delta - Encoding.UTF8.GetByteCount(envelope));
        fixture.Http.OnSend = (_, _) => Task.FromResult(unknownLength
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ReadStream(Encoding.UTF8.GetBytes(envelope))) }
            : Response(envelope));

        if (delta > 0) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        else Assert.Equal(new ReviewLimits(), (await fixture.ReadAsync()).Limits);
    }

    /// <summary>Checks decoded settings enforce their own byte limit independently of the response envelope.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DecodedConfigurationSizeBoundary(int delta)
    {
        using var fixture = new Fixture();
        fixture.Http.Body = Envelope("{}" + new string(' ', GitHubReviewPolicyService.MaxConfigurationBytes + delta - 2));

        if (delta > 0) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        else Assert.Equal(new ReviewLimits(), (await fixture.ReadAsync()).Limits);
    }

    /// <summary>Checks lookup transport failures propagate without retries or fallback.</summary>
    [Fact]
    public async Task NetworkFailurePropagates()
    {
        using var fixture = new Fixture();
        var failure = new HttpRequestException("Synthetic transport failure.");
        fixture.Http.OnSend = (_, _) => throw failure;

        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ReadAsync()));
        Assert.Equal(1, fixture.Http.Calls);
    }

    /// <summary>Checks ambiguous envelopes, invalid UTF-8, and scalar central configuration are rejected.</summary>
    [Fact]
    public async Task AmbiguousOrMalformedConfigurationFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Http.Body = Envelope("{}").Replace("\"size\":2", "\"size\":1,\"size\":2", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        fixture.Http.Body = """{"type":"file","path":".github/ai-review.json","encoding":"base64","size":1,"content":"/w=="}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        fixture.Configuration["Review"] = "invalid";
        var calls = fixture.Http.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Equal(calls, fixture.Http.Calls);
    }

    /// <summary>Checks cancellation before work and during headers or body reads prevents a policy from being returned.</summary>
    [Theory]
    [InlineData("before")]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task CancellationStopsLookup(string stage)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stage == "before") cancellation.Cancel();
        fixture.Http.OnSend = async (_, token) =>
        {
            if (stage == "headers")
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ReadStream(Encoding.UTF8.GetBytes(Envelope("{}")))
                {
                    BeforeRead = async readToken =>
                    {
                        entered.SetResult();
                        await Task.Delay(Timeout.Infinite, readToken);
                    }
                })
            };
        };
        var reading = fixture.ReadAsync(cancellation.Token);
        if (stage != "before")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(stage == "before" ? 0 : 1, fixture.Http.Calls);
    }

    private static string Envelope(string settings) => JsonSerializer.Serialize(new
    {
        type = "file", path = ".github/ai-review.json", encoding = "base64", size = Encoding.UTF8.GetByteCount(settings),
        content = Convert.ToBase64String(Encoding.UTF8.GetBytes(settings)), download_url = "https://untrusted.example/never-follow"
    });

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Fixture : IDisposable
    {
        internal const string Token = "synthetic-github-token";
        internal ConfigurationManager Configuration { get; } = new();
        internal FakeHttpHandler Http { get; } = new();
        internal GitHubReviewPolicyService Service { get; }
        private readonly HttpClient client;

        internal Fixture()
        {
            Configuration["GITHUB_TOKEN"] = Token;
            Configuration["GITHUB_API_URL"] = "https://api.github.com";
            client = new HttpClient(Http);
            Service = new GitHubReviewPolicyService(client, Configuration);
        }

        internal Task<ReviewPolicy> ReadAsync(CancellationToken cancellationToken = default) =>
            Service.GetPolicyAsync(DemoReviewFixture.Snapshot, cancellationToken);

        /// <inheritdoc />
        public void Dispose()
        {
            client.Dispose();
            Configuration.Dispose();
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string Body { get; set; } = Envelope("{}");
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return OnSend?.Invoke(request, cancellationToken) ?? Task.FromResult(Response(Body));
        }
    }

    private sealed class ReadStream(byte[] content) : MemoryStream(content)
    {
        internal Func<CancellationToken, Task>? BeforeRead { get; init; }

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BeforeRead is not null) await BeforeRead(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
