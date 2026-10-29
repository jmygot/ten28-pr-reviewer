using System.Collections.Immutable;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewOrchestratorTests
{
    private static readonly string[] Stages = ["event", "repository", "policy", "history", "llm", "validation", "history", "publication"];

    /// <summary>Checks stage order, context propagation, and completion after publication.</summary>
    [Fact]
    public async Task RunPassesContextThroughThePipeline()
    {
        var pipeline = new PipelineSpy();
        using var cancellation = new CancellationTokenSource();

        var outcome = await pipeline.CreateOrchestrator().RunAsync(cancellation.Token);

        Assert.Equal(Stages, pipeline.Calls);
        Assert.All(pipeline.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(ReviewStatus.Completed, outcome.Status);
        Assert.Null(outcome.Reason);
        Assert.Equal(DemoReviewFixture.Snapshot.HeadSha, outcome.HeadSha);
        Assert.Contains("Synthetic review: the changed return expression can dereference a null name", outcome.Summary);
        Assert.Contains("Coverage: 1 changed; 1 selected; 0 skipped.", outcome.Summary);
        Assert.Same(pipeline.PreparedSnapshot!.Coverage, outcome.Coverage);
        Assert.Equal(DemoReviewFixture.Result.Findings.ToArray(), outcome.Findings.ToArray());
        Assert.Equal(DemoReviewFixture.Result.Usage, outcome.Usage);
        Assert.Single(pipeline.Publications);
    }

    /// <summary>Checks that no request avoids all downstream work.</summary>
    [Fact]
    public async Task RunSkipsWhenThereIsNoRequest()
    {
        var pipeline = new PipelineSpy { Request = null };

        var outcome = await pipeline.CreateOrchestrator().RunAsync();

        Assert.Equal(["event"], pipeline.Calls);
        Assert.Equal(ReviewStatus.Skipped, outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
        Assert.Null(outcome.HeadSha);
        Assert.Null(outcome.Summary);
        Assert.Null(outcome.Usage);
        Assert.Empty(outcome.Findings);
        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks that generated findings cannot bypass the validator, including summary-only output.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunPublishesOnlyValidatedFindings(bool summaryOnly)
    {
        var rejected = DemoReviewFixture.Result.Findings[0] with { Path = "fabricated.cs", Line = 999 };
        var pipeline = new PipelineSpy
        {
            Result = DemoReviewFixture.Result with { Findings = [rejected, DemoReviewFixture.Result.Findings[0]] },
            ValidatedFindings = summaryOnly ? [] : DemoReviewFixture.Result.Findings
        };

        var outcome = await pipeline.CreateOrchestrator().RunAsync();

        var publication = Assert.Single(pipeline.Publications);
        Assert.Equal(pipeline.ValidatedFindings.ToArray(), publication.Findings.ToArray());
        Assert.Equal(pipeline.ValidatedFindings.ToArray(), outcome.Findings.ToArray());
        Assert.DoesNotContain(rejected, publication.Findings);
        Assert.Contains(summaryOnly ? "No actionable findings in the analyzed diff" : "Synthetic review: the changed return expression can dereference a null name", publication.Summary);
        Assert.Contains("Coverage: 1 changed; 1 selected; 0 skipped.", publication.Summary);
        Assert.Equal(DemoReviewFixture.Snapshot.HeadSha, publication.Snapshot.HeadSha);
        Assert.Equal(ReviewStatus.Completed, outcome.Status);
    }

    /// <summary>Checks every boundary failure propagates without executing later stages or reporting completion.</summary>
    [Theory]
    [InlineData("event")]
    [InlineData("repository")]
    [InlineData("policy")]
    [InlineData("history")]
    [InlineData("llm")]
    [InlineData("validation")]
    [InlineData("publication")]
    public async Task RunStopsAtTheFailingDependency(string stage)
    {
        var pipeline = new PipelineSpy { FailureStage = stage };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CreateOrchestrator().RunAsync());

        Assert.Same(pipeline.Failure, error);
        Assert.Equal(Stages.Take(Array.IndexOf(Stages, stage) + 1), pipeline.Calls);
        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks an already cancelled request never reaches the event source.</summary>
    [Fact]
    public async Task RunStopsBeforeWorkWhenAlreadyCancelled()
    {
        var pipeline = new PipelineSpy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.CreateOrchestrator().RunAsync(cancellation.Token));

        Assert.Empty(pipeline.Calls);
        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks cancellation between stages prevents further work even if a dependency returns normally.</summary>
    [Theory]
    [InlineData("event")]
    [InlineData("repository")]
    [InlineData("policy")]
    [InlineData("history")]
    [InlineData("llm")]
    [InlineData("validation")]
    [InlineData("publication")]
    public async Task RunStopsWhenCancelledDuringProcessing(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var pipeline = new PipelineSpy
        {
            OnStage = current =>
            {
                if (current == stage)
                {
                    cancellation.Cancel();
                }
            }
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.CreateOrchestrator().RunAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(Stages.Take(Array.IndexOf(Stages, stage) + 1), pipeline.Calls);
        Assert.All(pipeline.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks all-ineligible input stops before generation, validation, and publication with retained coverage.</summary>
    [Fact]
    public async Task NoEligibleFilesSkipsDownstreamWork()
    {
        var pipeline = new PipelineSpy
        {
            Snapshot = DemoReviewFixture.Snapshot with { ChangedFiles = [new ChangedFile("image.png", null, FileChangeStatus.Added, 0, 0, null)] }
        };

        var outcome = await pipeline.CreateOrchestrator().RunAsync();

        Assert.Equal(ReviewStatus.Skipped, outcome.Status);
        Assert.Equal(new[] { "event", "repository", "policy" }, pipeline.Calls);
        Assert.Null(outcome.Usage);
        Assert.Empty(pipeline.Publications);
        Assert.Equal("image.png", Assert.Single(outcome.Coverage!.SkippedFiles).Path);
        Assert.Contains("1 changed; 0 selected; 1 skipped", outcome.Reason);
    }

    /// <summary>Checks only selected patches reach generation/validation and publication describes partial coverage.</summary>
    [Fact]
    public async Task SelectedSnapshotAndCoverageFlowThroughPipeline()
    {
        var pipeline = new PipelineSpy
        {
            Snapshot = DemoReviewFixture.Snapshot with
            {
                ChangedFiles = DemoReviewFixture.Snapshot.ChangedFiles.Add(new ChangedFile("generated.g.cs", null, FileChangeStatus.Added, 1, 0, "unselected patch"))
            }
        };

        var outcome = await pipeline.CreateOrchestrator().RunAsync();

        Assert.Single(pipeline.PreparedSnapshot!.ChangedFiles);
        Assert.Equal(2, pipeline.Snapshot.ChangedFiles.Length);
        Assert.Contains("2 changed; 1 selected; 1 skipped", outcome.Summary);
        Assert.Contains("ExcludedPath: 1", outcome.Summary);
        Assert.Same(outcome.Coverage, Assert.Single(pipeline.Publications).Snapshot.Coverage);
    }

    /// <summary>Checks validated findings are stably sorted by severity and capped at the effective limit.</summary>
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task FindingsAreOrderedAndLimited(int count)
    {
        var findings = Enumerable.Range(0, count).Select(index => DemoReviewFixture.Result.Findings[0] with
        {
            Title = $"finding-{index}", Severity = index % 2 == 0 ? ReviewSeverity.Low : ReviewSeverity.Critical
        }).ToImmutableArray();
        var pipeline = new PipelineSpy { ValidatedFindings = findings };

        var outcome = await pipeline.CreateOrchestrator().RunAsync();

        Assert.Equal(findings.OrderBy(finding => finding.Severity).Take(10).ToArray(), outcome.Findings.ToArray());
        Assert.Equal(Math.Min(count, 10), Assert.Single(pipeline.Publications).Findings.Length);
    }

    /// <summary>Checks lower findings settings are enforced after validation.</summary>
    [Fact]
    public async Task LowerFindingsLimitIsEnforced()
    {
        var pipeline = new PipelineSpy
        {
            Policy = DemoReviewFixture.Policy with { Limits = new ReviewLimits(MaxFindings: 1) },
            ValidatedFindings = [DemoReviewFixture.Result.Findings[0], DemoReviewFixture.Result.Findings[0] with { Severity = ReviewSeverity.Critical }]
        };

        Assert.Equal(ReviewSeverity.Critical, Assert.Single((await pipeline.CreateOrchestrator().RunAsync()).Findings).Severity);
    }

    internal sealed class PipelineSpy : IReviewEventSource, IGitHubRepository, IReviewPolicyService,
        ILLMProvider, IReviewFindingsValidator, IReviewPublisher, IReviewHistory
    {
        internal ReviewRequest? Request { get; init; } = DemoReviewFixture.Request;
        internal PullRequestSnapshot Snapshot { get; init; } = DemoReviewFixture.Snapshot;
        internal ReviewPolicy Policy { get; init; } = DemoReviewFixture.Policy;
        internal PullRequestSnapshot? PreparedSnapshot { get; private set; }
        internal LlmReviewResult Result { get; init; } = DemoReviewFixture.Result;
        internal ImmutableArray<ReviewFinding> ValidatedFindings { get; init; } = DemoReviewFixture.Result.Findings;
        internal string? FailureStage { get; init; }
        internal InvalidOperationException Failure { get; } = new("Synthetic boundary failure.");
        internal Action<string>? OnStage { get; init; }
        internal List<string> Calls { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        internal List<DemoPublication> Publications { get; } = [];

        internal ReviewOrchestrator CreateOrchestrator() => new(this, this, this, this, this, this, this);

        /// <inheritdoc />
        public string ModelId => "synthetic-model";

        /// <inheritdoc />
        public Task<PriorReview?> FindSuccessfulReviewAsync(PullRequestSnapshot snapshot, string reviewKey, CancellationToken cancellationToken)
        {
            Enter("history", cancellationToken);
            return Task.FromResult<PriorReview?>(null);
        }

        private void Enter(string stage, CancellationToken token)
        {
            Calls.Add(stage);
            Tokens.Add(token);
            token.ThrowIfCancellationRequested();
            if (FailureStage == stage)
            {
                throw Failure;
            }

            OnStage?.Invoke(stage);
        }

        /// <inheritdoc />
        public Task<ReviewRequest?> ReadAsync(CancellationToken cancellationToken)
        {
            Enter("event", cancellationToken);
            return Task.FromResult(Request);
        }

        /// <inheritdoc />
        public Task<PullRequestSnapshot> GetPullRequestAsync(ReviewRequest request, CancellationToken cancellationToken)
        {
            Enter("repository", cancellationToken);
            Assert.Same(Request, request);
            return Task.FromResult(Snapshot);
        }

        /// <inheritdoc />
        public Task<ReviewPolicy> GetPolicyAsync(PullRequestSnapshot snapshot, CancellationToken cancellationToken)
        {
            Enter("policy", cancellationToken);
            Assert.Same(Snapshot, snapshot);
            return Task.FromResult(Policy);
        }

        /// <inheritdoc />
        public Task<LlmReviewResult> ReviewAsync(
            PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken)
        {
            Enter("llm", cancellationToken);
            Assert.NotSame(Snapshot, snapshot);
            Assert.NotNull(snapshot.Coverage);
            PreparedSnapshot = snapshot;
            Assert.Same(Policy, policy);
            return Task.FromResult(Result);
        }

        /// <inheritdoc />
        public Task<ImmutableArray<ReviewFinding>> ValidateAsync(
            PullRequestSnapshot snapshot, ReviewPolicy policy, LlmReviewResult result,
            CancellationToken cancellationToken)
        {
            Enter("validation", cancellationToken);
            Assert.Same(PreparedSnapshot, snapshot);
            Assert.Same(Policy, policy);
            Assert.Same(Result, result);
            return Task.FromResult(ValidatedFindings);
        }

        /// <inheritdoc />
        public Task PublishAsync(
            PullRequestSnapshot snapshot, string summary, ImmutableArray<ReviewFinding> findings,
            CancellationToken cancellationToken)
        {
            Enter("publication", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Publications.Add(new DemoPublication(snapshot, summary, findings));
            return Task.CompletedTask;
        }
    }
}
