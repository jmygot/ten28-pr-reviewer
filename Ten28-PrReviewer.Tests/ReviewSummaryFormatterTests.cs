using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewSummaryFormatterTests
{
    /// <summary>Checks accepted inline and unanchored findings contribute to counts and coverage is explicit.</summary>
    [Fact]
    public void FormatsAcceptedCountsCoverageAndMetadata()
    {
        var snapshot = ReviewInputSelector.Select(DemoReviewFixture.Snapshot with
        {
            ChangedFiles = DemoReviewFixture.Snapshot.ChangedFiles.Add(new ChangedFile("image.png", null, FileChangeStatus.Added, 0, 0, null))
        }, DemoReviewFixture.Policy);
        var finding = DemoReviewFixture.Result.Findings[0];
        var summary = ReviewSummaryFormatter.Format(snapshot, DemoReviewFixture.Policy, DemoReviewFixture.Result,
            [finding, finding with { Severity = ReviewSeverity.Critical, Line = null }]);

        Assert.Contains("Findings: critical: 1; high: 1; medium: 0; low: 0.", summary);
        Assert.Contains("### Unanchored observations", summary);
        Assert.Contains("2 changed; 1 selected; 1 skipped", summary);
        Assert.Contains("UnsupportedExtension: 1", summary);
        Assert.Contains("Reviewed commit: " + snapshot.HeadSha, summary);
        Assert.Contains("policy: demo\\-v1", summary);
        Assert.Contains("provider: demo\\-deepseek\\-stub", summary);
        Assert.Contains("model: synthetic\\-model", summary);
        Assert.Contains("worker: " + ReviewSummaryFormatter.EscapeMarkdown(typeof(ReviewOrchestrator).Assembly.GetName().Version!.ToString()), summary);
    }

    /// <summary>Checks empty accepted output does not repeat an unvalidated model claim.</summary>
    [Fact]
    public void EmptyFindingsHaveQualifiedResultAndZeroCounts()
    {
        var snapshot = ReviewInputSelector.Select(DemoReviewFixture.Snapshot, DemoReviewFixture.Policy);
        var summary = ReviewSummaryFormatter.Format(snapshot, DemoReviewFixture.Policy,
            DemoReviewFixture.Result with { Summary = "APPROVED: safe to merge" }, []);

        Assert.StartsWith("No actionable findings in the analyzed diff. This does not replace human review or testing.", summary);
        Assert.Contains("critical: 0; high: 0; medium: 0; low: 0", summary);
        Assert.DoesNotContain("APPROVED", summary);
        Assert.DoesNotContain("### Unanchored observations", summary);
    }

    /// <summary>Checks all interpolated text is rendered literally, including model identity and policy metadata.</summary>
    [Fact]
    public void EscapesSummaryFindingsAndMetadata()
    {
        const string hostile = "# heading\n<script>@developer</script> [link](javascript:evil)";
        var finding = DemoReviewFixture.Result.Findings[0] with
        { Title = hostile, Explanation = hostile, Recommendation = hostile, Path = hostile, Line = null };
        var snapshot = ReviewInputSelector.Select(DemoReviewFixture.Snapshot, DemoReviewFixture.Policy);
        var result = DemoReviewFixture.Result with { Summary = hostile, Usage = new ReviewUsage(hostile, hostile, null, null) };

        var summary = ReviewSummaryFormatter.Format(snapshot, DemoReviewFixture.Policy with { Version = hostile }, result, [finding]);
        var inline = ReviewSummaryFormatter.FormatFinding(finding);

        foreach (var text in new[] { summary, inline })
        {
            Assert.Contains("\\# heading &lt;script&gt;&#64;developer&lt;/script&gt; \\[link\\]\\(javascript:evil\\)", text);
            Assert.DoesNotContain("<script>", text);
            Assert.DoesNotContain("@developer", text);
            Assert.DoesNotContain("\n# heading", text);
        }
    }
}
