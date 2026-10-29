using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class DuplicateReviewPipelineTests
{
    /// <summary>Checks repeated manual requests for the same key generate and publish only once.</summary>
    [Fact]
    public async Task RepeatRequestSkipsBillableGeneration()
    {
        using var scenario = new Scenario();
        var first = await scenario.CreateOrchestrator().RunAsync();
        var second = await scenario.CreateOrchestrator().RunAsync();

        Assert.Equal(ReviewStatus.Completed, first.Status);
        Assert.Equal(ReviewStatus.Skipped, second.Status);
        Assert.Equal(first.ReviewKey, second.ReviewKey);
        Assert.Equal(new PriorReview(123), second.PriorReview);
        Assert.Null(second.Usage);
        Assert.Equal(1, scenario.GenerationCalls);
        Assert.Equal(1, scenario.Store.Posts);
        Assert.True(ReviewIdentity.TryRead(Assert.Single(scenario.Store.Reviews)["body"]!.GetValue<string>(), out var marker));
        Assert.Equal(first.ReviewKey, marker!.Key);
    }

    /// <summary>Checks new commit, model, policy, and worker identities allow a new manually requested review.</summary>
    /// <param name="change">Identity component changed after a successful review.</param>
    [Theory]
    [InlineData("head")]
    [InlineData("model")]
    [InlineData("policy")]
    [InlineData("limits")]
    [InlineData("worker")]
    public async Task ChangedIdentityAllowsFreshReview(string change)
    {
        using var scenario = new Scenario();
        var first = await scenario.CreateOrchestrator().RunAsync();
        if (change == "head") scenario.Snapshot = scenario.Snapshot with { HeadSha = new string('c', 40) };
        if (change == "model") scenario.ModelId = "other-model";
        if (change == "policy") scenario.Policy = scenario.Policy with { Version = "v2" };
        if (change == "limits") scenario.Policy = scenario.Policy with { Limits = scenario.Policy.Limits with { MaxFindings = 1 } };
        if (change == "worker")
        {
            var previous = ReviewIdentity.Create(scenario.Snapshot, scenario.Policy, scenario.ModelId, "previous-release");
            scenario.Store.Reviews[0]["body"] = previous.Marker;
        }
        var second = await scenario.CreateOrchestrator().RunAsync();

        Assert.Equal(ReviewStatus.Completed, second.Status);
        if (change != "worker") Assert.NotEqual(first.ReviewKey, second.ReviewKey);
        Assert.Equal(2, scenario.GenerationCalls);
        Assert.Equal(2, scenario.Store.Posts);
    }

    /// <summary>Checks a successful review appearing during generation prevents publication but retains incurred usage.</summary>
    [Fact]
    public async Task ConcurrentReviewBeforePublicationRetainsUsage()
    {
        using var scenario = new Scenario();
        scenario.OnGenerate = () => scenario.Store.Reviews.Add(GitHubReviewHistoryTests.Review(99,
            ReviewIdentity.Create(scenario.Snapshot, scenario.Policy, scenario.ModelId).Marker));
        var orchestrator = scenario.CreateOrchestrator();
        var outcome = await orchestrator.RunAsync();

        Assert.Equal(ReviewStatus.Skipped, outcome.Status);
        Assert.Equal(new PriorReview(99), outcome.PriorReview);
        Assert.Equal(13, outcome.Usage!.InputTokens);
        Assert.Equal(7, outcome.Usage.OutputTokens);
        Assert.Equal(1, scenario.GenerationCalls);
        Assert.Equal(0, scenario.Store.Posts);
        Assert.True(orchestrator.Diagnostics.GenerationStarted);
        Assert.Equal("duplicate_review", orchestrator.Diagnostics.Reason);
    }

    /// <summary>Checks history errors before generation or publication stop the pipeline without further writes.</summary>
    /// <param name="late">Whether history becomes unavailable after generation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryFailureStopsPipeline(bool late)
    {
        using var scenario = new Scenario();
        scenario.Store.HistoryStatus = late ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        if (late) scenario.OnGenerate = () => scenario.Store.HistoryStatus = HttpStatusCode.TooManyRequests;
        var orchestrator = scenario.CreateOrchestrator();
        await Assert.ThrowsAsync<HttpRequestException>(() => orchestrator.RunAsync());
        Assert.Equal(late ? 1 : 0, scenario.GenerationCalls);
        Assert.Equal(0, scenario.Store.Posts);
        Assert.Equal(late ? "history_before_publication" : "history_before_generation", orchestrator.Diagnostics.Stage);
        Assert.Equal(late ? 429 : 403, orchestrator.Diagnostics.HttpStatus);
    }

    /// <summary>Checks a marker is retained in fallback and an uncertain successful write is found on the next request.</summary>
    /// <param name="mode">Publisher fallback or uncertain-delivery mode.</param>
    [Theory]
    [InlineData("fallback")]
    [InlineData("uncertain")]
    public async Task PublishedMarkerPreventsRetryBilling(string mode)
    {
        using var scenario = new Scenario();
        scenario.Store.Mode = mode;
        if (mode == "uncertain")
        {
            var first = scenario.CreateOrchestrator();
            var error = await Assert.ThrowsAsync<GitHubReviewPublicationException>(() => first.RunAsync());
            Assert.True(error.DeliveryMayHaveSucceeded);
            Assert.True(first.Diagnostics.DeliveryMayHaveSucceeded);
        }
        else Assert.Equal(ReviewStatus.Completed, (await scenario.CreateOrchestrator().RunAsync()).Status);
        var second = await scenario.CreateOrchestrator().RunAsync();

        Assert.Equal(ReviewStatus.Skipped, second.Status);
        Assert.Equal(1, scenario.GenerationCalls);
        Assert.Equal(mode == "fallback" ? 2 : 1, scenario.Store.Posts);
        Assert.Single(scenario.Store.Reviews);
        if (mode == "fallback") Assert.Contains("summary-only fallback", scenario.Store.Reviews[0]["body"]!.GetValue<string>());
    }

    internal sealed class Scenario : IReviewEventSource, IGitHubRepository, IReviewPolicyService, ILLMProvider,
        IReviewFindingsValidator, IDisposable
    {
        internal PullRequestSnapshot Snapshot { get; set; } = ReviewInputSelector.Select(DemoReviewFixture.Snapshot, DemoReviewFixture.Policy);
        internal ReviewPolicy Policy { get; set; } = DemoReviewFixture.Policy;
        internal ManualReviewClock Clock { get; } = new();
        internal StoreHandler Store { get; }
        internal ConfigurationManager Configuration { get; } = new();
        internal int GenerationCalls { get; private set; }
        internal Action? OnGenerate { get; set; }
        internal string? FailureStage { get; set; }
        internal ReviewUsage? ResultUsage { get; set; } = new("synthetic-provider", "synthetic-model-revision", 13, 7);
        internal HttpClient Client { get; }
        internal Scenario()
        {
            Configuration["GITHUB_TOKEN"] = "synthetic-pipeline-token";
            Store = new StoreHandler(() => Snapshot);
            Client = new HttpClient(Store);
        }
        internal GitHubReviewHistory History => new(Client, Configuration, Clock);
        internal GitHubReviewPublisher Publisher => new(Client, Configuration, Clock, NullLogger<GitHubReviewPublisher>.Instance);
        internal ReviewOrchestrator CreateOrchestrator() => new(this, this, this, this, this, Publisher, History, Clock);
        /// <inheritdoc />
        public string ModelId { get; set; } = "synthetic-model";
        /// <inheritdoc />
        public Task<ReviewRequest?> ReadAsync(CancellationToken cancellationToken)
        { Clock.Advance(TimeSpan.FromMilliseconds(10)); return Task.FromResult<ReviewRequest?>(Snapshot.Request); }
        /// <inheritdoc />
        public Task<PullRequestSnapshot> GetPullRequestAsync(ReviewRequest request, CancellationToken cancellationToken)
        { Clock.Advance(TimeSpan.FromMilliseconds(20)); return Task.FromResult(Snapshot); }
        /// <inheritdoc />
        public Task<ReviewPolicy> GetPolicyAsync(PullRequestSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(Policy);
        /// <inheritdoc />
        public Task<LlmReviewResult> ReviewAsync(PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken)
        {
            GenerationCalls++;
            Clock.Advance(TimeSpan.FromMilliseconds(30));
            OnGenerate?.Invoke();
            if (FailureStage == "generation") throw new HttpRequestException("SECRET_EXCEPTION");
            return Task.FromResult(DemoReviewFixture.Result with { Usage = ResultUsage ?? new ReviewUsage("synthetic-provider", ModelId, null, null) });
        }
        /// <inheritdoc />
        public Task<ImmutableArray<ReviewFinding>> ValidateAsync(PullRequestSnapshot snapshot, ReviewPolicy policy, LlmReviewResult result, CancellationToken cancellationToken)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(40));
            if (FailureStage == "validation") throw new InvalidOperationException("SECRET_EXCEPTION");
            return new ReviewFindingsValidator().ValidateAsync(snapshot, policy, result, cancellationToken);
        }
        /// <inheritdoc />
        public void Dispose() { Client.Dispose(); Configuration.Dispose(); }
    }

    internal sealed class StoreHandler(Func<PullRequestSnapshot> snapshot) : HttpMessageHandler
    {
        internal List<JsonObject> Reviews { get; } = [];
        internal int Posts { get; private set; }
        internal string Mode { get; set; } = "normal";
        internal HttpStatusCode HistoryStatus { get; set; } = HttpStatusCode.OK;
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/reviews"))
                return GitHubReviewPublisherTests.Json(HistoryStatus,
                    new JsonArray(Reviews.Select(review => review.DeepClone()).ToArray()).ToJsonString());
            if (request.Method == HttpMethod.Get)
                return GitHubReviewPublisherTests.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                { number = snapshot().Request.PullRequestNumber, state = "open", head = new { sha = snapshot().HeadSha }, @base = new { sha = snapshot().BaseSha } }));
            Assert.Equal(HttpMethod.Post, request.Method);
            Posts++;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            Assert.Equal("COMMENT", root.GetProperty("event").GetString());
            Assert.Equal(snapshot().HeadSha, root.GetProperty("commit_id").GetString());
            if (Mode == "fallback" && Posts == 1)
                return GitHubReviewPublisherTests.Json(HttpStatusCode.UnprocessableEntity,
                    """{"message":"Validation Failed","errors":[{"resource":"PullRequestReviewComment","field":"line","code":"invalid"}]}""");
            if (Mode == "denied") return GitHubReviewPublisherTests.Json(HttpStatusCode.Forbidden, "SECRET_RESPONSE");
            var review = GitHubReviewHistoryTests.Review(123 + Reviews.Count, root.GetProperty("body").GetString());
            review["commit_id"] = snapshot().HeadSha;
            Reviews.Add(review);
            if (Mode == "uncertain") throw new HttpRequestException("SECRET_EXCEPTION");
            return GitHubReviewPublisherTests.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new
                { id = review["id"]!.GetValue<long>(), state = "COMMENTED", commit_id = snapshot().HeadSha }));
        }
    }
}
