using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ValidatedReviewPipelineTests
{
    /// <summary>Checks real JSON decoding and validation allow only manually verified RIGHT anchors into publication.</summary>
    [Fact]
    public async Task PublishesOnlyGroundedAddedLinesAndSummarizesUsefulObservations()
    {
        var renamed = new ChangedFile("src/Renamed.cs", "src/Original.cs", FileChangeStatus.Renamed, 1, 1,
            "@@ -10 +10 @@\n-return input;\n+var value = input.Trim();");
        var deleted = new ChangedFile("src/Guard.cs", null, FileChangeStatus.Deleted, 0, 1,
            "@@ -1 +0,0 @@\n-if (amount < 0) throw new ArgumentOutOfRangeException();");
        var invalid = new ChangedFile("src/Truncated.cs", null, FileChangeStatus.Modified, 2, 2,
            "@@ -1,2 +1,2 @@\n-old\n+return name.Trim();");
        var pipeline = new ReviewOrchestratorTests.PipelineSpy
        {
            Snapshot = DemoReviewFixture.Snapshot with
            {
                ChangedFiles = [DemoReviewFixture.Snapshot.ChangedFiles[0], renamed, deleted, invalid,
                    DemoReviewFixture.Snapshot.ChangedFiles[0] with { Path = "src/Skipped.g.cs" }]
            }
        };
        var findings = new[]
        {
            Finding(),
            Finding() with { Path = renamed.Path, Line = 10, Title = "Renamed null dereference", Explanation = "For null input, `var value = input.Trim();` throws." },
            Finding() with { Path = deleted.Path, Line = 1, Title = "Deleted validation", Explanation = "Deleting `if (amount < 0) throw new ArgumentOutOfRangeException();` removes explicit rejection of negative input." },
            Finding() with { Line = 999, Title = "Unanchored observation" },
            Finding() with { Path = renamed.PreviousPath!, Title = "Invalid old path" },
            Finding() with { Path = "fabricated.cs", Title = "Invalid fabricated path" },
            Finding() with { Explanation = "This seems risky; fix it.", Title = "Invalid missing evidence" },
            Finding() with { Path = invalid.Path, Line = 1, Title = "Invalid partial map" },
            Finding() with { Path = "src/Skipped.g.cs", Title = "Invalid skipped path" }
        };
        var orchestrator = Create(pipeline, new JsonReviewProvider(Response(findings)));

        var outcome = await orchestrator.RunAsync();

        var publication = Assert.Single(pipeline.Publications);
        Assert.Equal(2, publication.Findings.Length);
        var expectedAnchors = new Dictionary<string, (int Line, string Source)>(StringComparer.Ordinal)
        {
            ["src/DisplayName.cs"] = (3, "`return name.Trim();`"),
            ["src/Renamed.cs"] = (10, "`var value = input.Trim();`")
        };
        foreach (var inline in publication.Findings)
        {
            Assert.True(expectedAnchors.TryGetValue(inline.Path, out var expected));
            Assert.Equal(expected.Line, inline.Line);
            Assert.Contains(expected.Source, inline.Explanation);
        }
        Assert.Equal(4, outcome.Findings.Length);
        Assert.Equal(2, outcome.Findings.Count(finding => finding.Line is null));
        Assert.Contains("### Unanchored observations", publication.Summary);
        Assert.Contains("Deleted validation", publication.Summary);
        Assert.Contains("Unanchored observation", publication.Summary);
        Assert.Contains("5 changed; 4 selected; 1 skipped", publication.Summary);
        Assert.DoesNotContain("Invalid", publication.Summary);
        Assert.DoesNotContain(outcome.Findings, finding => finding.Title.StartsWith("Invalid", StringComparison.Ordinal));
        Assert.Equal(outcome.Summary, publication.Summary);
        Assert.Equal(pipeline.Snapshot.HeadSha, publication.Snapshot.HeadSha);
    }

    /// <summary>Checks null-line findings are summary-only and empty reviews have no inline payloads.</summary>
    /// <param name="summaryOnly">Whether the response contains an evidenced null-line observation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesSummaryWithoutUnanchoredInlinePayloads(bool summaryOnly)
    {
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();
        var response = Response(summaryOnly ? [Finding() with { Line = null }] : []);

        var outcome = await Create(pipeline, new JsonReviewProvider(response)).RunAsync();

        var publication = Assert.Single(pipeline.Publications);
        Assert.Empty(publication.Findings);
        Assert.Equal(summaryOnly ? 1 : 0, outcome.Findings.Length);
        Assert.Equal(summaryOnly, publication.Summary.Contains("### Unanchored observations", StringComparison.Ordinal));
        Assert.Contains("1 changed; 1 selected; 0 skipped", publication.Summary);
    }

    /// <summary>Checks severity ordering and the cap apply across both output categories.</summary>
    [Fact]
    public async Task LimitsAnchoredAndUnanchoredFindingsTogether()
    {
        var pipeline = new ReviewOrchestratorTests.PipelineSpy
        {
            Policy = DemoReviewFixture.Policy with { Limits = new ReviewLimits(MaxFindings: 2) },
            Result = DemoReviewFixture.Result with
            {
                Findings = [Finding() with { Severity = ReviewSeverity.Low, Title = "Dropped low" },
                    Finding() with { Severity = ReviewSeverity.Medium, Title = "Retained inline" },
                    Finding() with { Severity = ReviewSeverity.Critical, Title = "Retained observation", Line = 999 }]
            }
        };

        var outcome = await Create(pipeline, pipeline).RunAsync();

        Assert.Equal(new[] { ReviewSeverity.Critical, ReviewSeverity.Medium }, outcome.Findings.Select(finding => finding.Severity));
        var publication = Assert.Single(pipeline.Publications);
        Assert.Equal("Retained inline", Assert.Single(publication.Findings).Title);
        Assert.Contains("Retained observation", publication.Summary);
        Assert.DoesNotContain("Dropped low", publication.Summary);
    }

    /// <summary>Checks rejected duplicate findings cannot produce duplicate inline comments.</summary>
    [Fact]
    public async Task DeduplicatesBeforePublisherBoundary()
    {
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();
        var provider = new JsonReviewProvider(Response([Finding(), Finding() with
        { Title = "NULLABLE  name IS dereferenced", Severity = ReviewSeverity.Critical }]));

        var outcome = await Create(pipeline, provider).RunAsync();

        Assert.Equal(ReviewSeverity.Critical, Assert.Single(Assert.Single(pipeline.Publications).Findings).Severity);
        Assert.Single(outcome.Findings);
    }

    /// <summary>Checks summaries render hostile Markdown and HTML as literal model-controlled text.</summary>
    [Fact]
    public async Task EscapesModelControlledSummaryAndObservationText()
    {
        var file = DemoReviewFixture.Snapshot.ChangedFiles[0] with { Path = "src/[unsafe]*.cs" };
        var observation = Finding() with
        {
            Path = file.Path, Line = null, Title = "**Do not format**\n# fake heading",
            Explanation = "For null, `return name.Trim();` throws. <script>alert(1)</script>",
            Recommendation = "Use [fix](javascript:evil) and @developer & verify."
        };
        var pipeline = new ReviewOrchestratorTests.PipelineSpy
        { Snapshot = DemoReviewFixture.Snapshot with { ChangedFiles = [file] } };
        var provider = new JsonReviewProvider(Response([observation], "# Raw summary\n<script>@developer</script>"));

        var outcome = await Create(pipeline, provider).RunAsync();

        var summary = Assert.Single(pipeline.Publications).Summary;
        Assert.StartsWith("\\# Raw summary &lt;script&gt;&#64;developer&lt;/script&gt;", summary);
        Assert.Contains("\\*\\*Do not format\\*\\* \\# fake heading", summary);
        Assert.Contains("src/\\[unsafe\\]\\*\\.cs", summary);
        Assert.Contains("\\`return name\\.Trim\\(\\);\\`", summary);
        Assert.Contains("&lt;script&gt;alert\\(1\\)&lt;/script&gt;", summary);
        Assert.Contains("\\[fix\\]\\(javascript:evil\\)", summary);
        Assert.Contains("&#64;developer &amp; verify", summary);
        Assert.DoesNotContain("<script>", summary);
        Assert.DoesNotContain("\n# fake heading", summary);
        Assert.DoesNotContain("@developer", summary);
        Assert.Equal(observation, Assert.Single(outcome.Findings));
        Assert.Empty(Assert.Single(pipeline.Publications).Findings);
    }

    /// <summary>Checks strict wire parsing failures prevent all publication.</summary>
    /// <param name="failure">Wire contract violation.</param>
    [Theory]
    [InlineData("severity")]
    [InlineData("side")]
    [InlineData("json")]
    public async Task ReaderFailurePreventsPublication(string failure)
    {
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();
        var response = JsonNode.Parse(Response([Finding()]))!;
        if (failure != "json") response["findings"]![0]![failure] = "unsupported";
        var provider = new JsonReviewProvider(failure == "json" ? "{" : response.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(pipeline, provider).RunAsync());

        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks inconsistent selected paths fail validation without reaching the publisher.</summary>
    [Fact]
    public async Task ValidatorFailurePreventsPublication()
    {
        var file = DemoReviewFixture.Snapshot.ChangedFiles[0];
        var pipeline = new ReviewOrchestratorTests.PipelineSpy
        { Snapshot = DemoReviewFixture.Snapshot with { ChangedFiles = [file, file] } };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(pipeline, new JsonReviewProvider(Response([Finding()]))).RunAsync());

        Assert.Equal("Finding validation requires unique selected paths.", error.Message);
        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks cancellation after real validation prevents otherwise valid findings from publishing.</summary>
    [Fact]
    public async Task CancellationAfterValidationPreventsPublication()
    {
        using var cancellation = new CancellationTokenSource();
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();
        var validator = new CancellingValidator(cancellation);
        var orchestrator = new ReviewOrchestrator(pipeline, pipeline, pipeline,
            new JsonReviewProvider(Response([Finding()])), validator, pipeline, pipeline);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.RunAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(pipeline.Publications);
    }

    private static ReviewFinding Finding() => ReviewFindingsValidatorTests.Finding();

    private static ReviewOrchestrator Create(ReviewOrchestratorTests.PipelineSpy pipeline, ILLMProvider provider) =>
        new(pipeline, pipeline, pipeline, provider, new ReviewFindingsValidator(), pipeline, pipeline);

    private static string Response(IEnumerable<ReviewFinding> findings, string summary = "Fixture review") => JsonSerializer.Serialize(new
    {
        summary,
        findings = findings.Select(finding => new
        {
            severity = finding.Severity.ToString().ToLowerInvariant(), title = finding.Title,
            path = finding.Path, line = finding.Line, side = "RIGHT", explanation = finding.Explanation,
            recommendation = finding.Recommendation
        })
    });

    private sealed class JsonReviewProvider(string response) : ILLMProvider
    {
        /// <inheritdoc />
        public string ModelId => "synthetic-model";

        /// <inheritdoc />
        public Task<LlmReviewResult> ReviewAsync(PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReviewResponseReader.Read(response, policy.Limits.MaxFindings,
                new ReviewUsage("synthetic-provider", "synthetic-model", null, null)));
        }
    }

    private sealed class CancellingValidator(CancellationTokenSource cancellation) : IReviewFindingsValidator
    {
        /// <inheritdoc />
        public async Task<ImmutableArray<ReviewFinding>> ValidateAsync(PullRequestSnapshot snapshot, ReviewPolicy policy,
            LlmReviewResult result, CancellationToken cancellationToken)
        {
            var findings = await new ReviewFindingsValidator().ValidateAsync(snapshot, policy, result, cancellationToken);
            cancellation.Cancel();
            return findings;
        }
    }
}
