using System.Collections.Immutable;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewFindingsValidatorTests
{
    /// <summary>Checks evidence uses a complete, internally exact changed line and explanatory prose.</summary>
    /// <param name="explanation">Candidate evidence and prose.</param>
    /// <param name="accepted">Whether the evidence is sufficient for the changed line.</param>
    [Theory]
    [InlineData("When name is null, `return name.Trim();` throws.", true)]
    [InlineData("When name is null, `   return name.Trim();\t` throws.", true)]
    [InlineData("When name is null, ``return name.Trim();`` throws.", true)]
    [InlineData("The context `public static string DisplayName(string? name)` leads to `return name.Trim();` throwing for null.", true)]
    [InlineData("When null is passed:\r\n`return name.Trim();` throws.", true)]
    [InlineData("A null name now throws when Trim is called.", false)]
    [InlineData("`return name.Trim();`", false)]
    [InlineData("`return name.Trim();` !!!", false)]
    [InlineData("`return name.Trim();` `return name.Trim();`", false)]
    [InlineData("The call `name.Trim()` throws for null.", false)]
    [InlineData("The call `return  name.Trim();` throws for null.", false)]
    [InlineData("The context `public static string DisplayName(string? name)` is unsafe.", false)]
    [InlineData("The hunk `@@ -1,4 +1,4 @@` is unsafe.", false)]
    [InlineData("The source `return fabricated();` is unsafe.", false)]
    [InlineData("The changed line `return name.Trim(); has no closing delimiter.", false)]
    [InlineData("The changed line ```\nreturn name.Trim();\n``` throws.", false)]
    public async Task RequiresGroundedCompleteLineAndProse(string explanation, bool accepted)
    {
        var result = await ValidateAsync([Finding() with { Explanation = explanation }]);

        if (accepted) Assert.Equal(3, Assert.Single(result).Line);
        else Assert.Empty(result);
    }

    /// <summary>Checks longer delimiters can quote a source line containing template-literal backticks.</summary>
    [Fact]
    public async Task SupportsSourceContainingBackticks()
    {
        const string code = "return `Hello ${name.trim()}`;";
        var snapshot = Selected(new ChangedFile("file.ts", null, FileChangeStatus.Modified, 1, 1,
            "@@ -1 +1 @@\n-return name;\n+" + code));
        var finding = Finding() with { Path = "file.ts", Line = 1, Explanation = "For null names, ``" + code + "`` throws." };

        Assert.Equal(1, Assert.Single(await ValidateAsync([finding], snapshot)).Line);
    }

    /// <summary>Checks invalid anchors become summary observations without guessing a replacement line.</summary>
    /// <param name="line">Claimed line, including context, out-of-range, or absent locations.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public async Task GroundedUnanchorableFindingsKeepNullLocations(int? line)
    {
        var finding = Finding() with { Line = line };
        var actual = Assert.Single(await ValidateAsync([finding]));
        Assert.Null(actual.Line);
        Assert.Equal(finding with { Line = null }, actual);
    }

    /// <summary>Checks quoting a different added line cannot validate the claimed location.</summary>
    [Fact]
    public async Task RequiresEvidenceAtTheClaimedAddedLine()
    {
        var snapshot = Selected(new ChangedFile("file.cs", null, FileChangeStatus.Added, 2, 0,
            "@@ -0,0 +1,2 @@\n+var first = name.Trim();\n+var second = value.Trim();"));
        var finding = Finding() with
        {
            Path = "file.cs", Line = 1, Explanation = "For a null value, `var second = value.Trim();` throws."
        };

        Assert.Null(Assert.Single(await ValidateAsync([finding], snapshot)).Line);
        Assert.Equal(2, Assert.Single(await ValidateAsync([finding with { Line = 2 }], snapshot)).Line);
    }

    /// <summary>Checks removed-only evidence is retained in the summary, including complete file deletion.</summary>
    /// <param name="status">Modified or deleted file status.</param>
    [Theory]
    [InlineData(FileChangeStatus.Modified)]
    [InlineData(FileChangeStatus.Deleted)]
    public async Task DeletedEvidenceCannotBecomeRightSideInline(FileChangeStatus status)
    {
        var snapshot = Selected(new ChangedFile("guard.cs", null, status, 0, 1,
            "@@ -1 +0,0 @@\n-if (amount < 0) throw new ArgumentOutOfRangeException();"));
        var finding = Finding() with
        {
            Path = "guard.cs", Line = 1,
            Explanation = "Deleting `if (amount < 0) throw new ArgumentOutOfRangeException();` removes explicit rejection of negative input."
        };
        Assert.Null(Assert.Single(await ValidateAsync([finding], snapshot)).Line);
    }

    /// <summary>Checks punctuation-only changes cannot serve as checkable code evidence.</summary>
    [Fact]
    public async Task RejectsPunctuationOnlyEvidence()
    {
        var snapshot = Selected(new ChangedFile("file.cs", null, FileChangeStatus.Modified, 1, 1, "@@ -1 +1 @@\n-{\n+}"));
        Assert.Empty(await ValidateAsync([Finding() with { Path = "file.cs", Line = 1, Explanation = "The changed `}` causes a failure." }], snapshot));
    }

    /// <summary>Checks only exact current paths from selected files are accepted.</summary>
    /// <param name="path">A fabricated path or alias.</param>
    [Theory]
    [InlineData("fabricated.cs")]
    [InlineData("SRC/DisplayName.cs")]
    [InlineData("src/displayName.cs")]
    [InlineData("./src/DisplayName.cs")]
    [InlineData("src\\DisplayName.cs")]
    [InlineData("/src/DisplayName.cs")]
    [InlineData("C:\\src\\DisplayName.cs")]
    [InlineData("src/a/../DisplayName.cs")]
    [InlineData("src%2FDisplayName.cs")]
    [InlineData(" src/DisplayName.cs ")]
    [InlineData("src/Skipped.g.cs")]
    public async Task RejectsUnselectedOrAliasedPaths(string path)
    {
        var snapshot = Selected(DemoReviewFixture.Snapshot.ChangedFiles[0],
            DemoReviewFixture.Snapshot.ChangedFiles[0] with { Path = "src/Skipped.g.cs" });
        Assert.Empty(await ValidateAsync([Finding() with { Path = path }], snapshot));
    }

    /// <summary>Checks rename origins are not silently remapped to the current path.</summary>
    [Fact]
    public async Task AcceptsOnlyCurrentRenamePath()
    {
        var file = DemoReviewFixture.Snapshot.ChangedFiles[0] with
        { Path = "src/NewName.cs", PreviousPath = "src/OldName.cs", Status = FileChangeStatus.Renamed };
        var snapshot = Selected(file);
        Assert.Empty(await ValidateAsync([Finding() with { Path = file.PreviousPath! }], snapshot));
        Assert.Equal(file.Path, Assert.Single(await ValidateAsync([Finding() with { Path = file.Path }], snapshot)).Path);
    }

    /// <summary>Checks typed providers cannot bypass field and severity validation performed by the JSON reader.</summary>
    /// <param name="failure">Malformed typed value to reject.</param>
    [Theory]
    [InlineData("severity")]
    [InlineData("negative-severity")]
    [InlineData("zero-line")]
    [InlineData("negative-line")]
    [InlineData("title")]
    [InlineData("path")]
    [InlineData("explanation")]
    [InlineData("recommendation")]
    [InlineData("null-finding")]
    public async Task RejectsMalformedTypedFindings(string failure)
    {
        var finding = failure switch
        {
            "severity" => Finding() with { Severity = (ReviewSeverity)99 },
            "negative-severity" => Finding() with { Severity = (ReviewSeverity)(-1) },
            "zero-line" => Finding() with { Line = 0 },
            "negative-line" => Finding() with { Line = -1 },
            "title" => Finding() with { Title = " \t" },
            "path" => Finding() with { Path = null! },
            "explanation" => Finding() with { Explanation = null! },
            "recommendation" => Finding() with { Recommendation = " " },
            _ => null!
        };
        Assert.Empty(await ValidateAsync([finding]));
    }

    /// <summary>Checks malformed or unavailable patches suppress their findings without affecting valid files.</summary>
    /// <param name="patch">Invalid patch in a different selected file.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("patch")]
    [InlineData("@@ -1,2 +1,2 @@\n-old\n+return name.Trim();")]
    [InlineData("@@ -1 +1 @@\n-old\n+return name.Trim();\n@@ -5,2 +5,2 @@\n-truncated")]
    public async Task InvalidPatchIsIsolatedPerFile(string? patch)
    {
        var snapshot = Selected(DemoReviewFixture.Snapshot.ChangedFiles[0],
            new ChangedFile("bad.cs", null, FileChangeStatus.Modified, 1, 1, patch));
        var actual = await ValidateAsync([Finding() with { Path = "bad.cs" }, Finding()], snapshot);
        Assert.Equal(Finding(), Assert.Single(actual));
    }

    /// <summary>Checks duplicates use final location and normalized title while retaining the highest severity.</summary>
    [Fact]
    public async Task DeduplicatesAfterLocationValidation()
    {
        var low = Finding() with { Severity = ReviewSeverity.Low, Title = "  Nullable  NAME is dereferenced  " };
        var critical = Finding() with { Severity = ReviewSeverity.Critical, Title = "nullable name IS dereferenced" };
        var summaryLow = low with { Line = 999 };
        var summaryHigh = critical with { Line = 1, Severity = ReviewSeverity.High };
        var distinctTitle = Finding() with { Title = "A separate defect at the same line" };

        var result = await ValidateAsync([low, critical, summaryLow, summaryHigh, distinctTitle]);

        Assert.Equal(3, result.Length);
        Assert.Contains(critical, result);
        Assert.Contains(summaryHigh with { Line = null }, result);
        Assert.Contains(distinctTitle, result);
        Assert.DoesNotContain(low, result);
    }

    /// <summary>Checks duplicate keys do not merge different repository paths.</summary>
    [Fact]
    public async Task KeepsDistinctPaths()
    {
        var snapshot = Selected(DemoReviewFixture.Snapshot.ChangedFiles[0], DemoReviewFixture.Snapshot.ChangedFiles[0] with { Path = "other.cs" });
        Assert.Equal(2, (await ValidateAsync([Finding(), Finding() with { Path = "other.cs" }], snapshot)).Length);
    }

    /// <summary>Checks malformed selection metadata fails closed before processing findings.</summary>
    /// <param name="failure">Selection invariant to violate.</param>
    [Theory]
    [InlineData("coverage")]
    [InlineData("selected-count")]
    [InlineData("total-count")]
    [InlineData("default-skips")]
    [InlineData("duplicate-path")]
    [InlineData("empty-path")]
    [InlineData("default-files")]
    [InlineData("null-file")]
    public async Task RejectsInconsistentSelection(string failure)
    {
        var snapshot = ReviewPromptFixtures.Selected("defect");
        snapshot = failure switch
        {
            "coverage" => snapshot with { Coverage = null },
            "selected-count" => snapshot with { Coverage = snapshot.Coverage! with { SelectedFiles = 0 } },
            "total-count" => snapshot with { Coverage = snapshot.Coverage! with { TotalChangedFiles = 2 } },
            "default-skips" => snapshot with { Coverage = snapshot.Coverage! with { SkippedFiles = default } },
            "duplicate-path" => Selected(snapshot.ChangedFiles[0], snapshot.ChangedFiles[0]),
            "empty-path" => snapshot with { ChangedFiles = [snapshot.ChangedFiles[0] with { Path = " " }] },
            "default-files" => snapshot with { ChangedFiles = default },
            _ => snapshot with { ChangedFiles = [null!] }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync([Finding()], snapshot));
    }

    /// <summary>Checks empty results are valid and uninitialized typed collections fail closed.</summary>
    [Fact]
    public async Task HandlesEmptyAndInvalidCollections()
    {
        Assert.Empty(await ValidateAsync([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(default));
    }

    /// <summary>Checks cancellation is propagated before processing any findings.</summary>
    [Fact]
    public async Task HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReviewFindingsValidator().ValidateAsync(
            ReviewPromptFixtures.Selected("defect"), ReviewPolicySettings.Defaults, DemoReviewFixture.Result, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    internal static ReviewFinding Finding() => DemoReviewFixture.Result.Findings[0];

    internal static PullRequestSnapshot Selected(params ChangedFile[] files) => ReviewInputSelector.Select(
        DemoReviewFixture.Snapshot with { ChangedFiles = files.ToImmutableArray() }, ReviewPolicySettings.Defaults);

    private static Task<ImmutableArray<ReviewFinding>> ValidateAsync(
        ImmutableArray<ReviewFinding> findings, PullRequestSnapshot? snapshot = null) => new ReviewFindingsValidator().ValidateAsync(
            snapshot ?? ReviewPromptFixtures.Selected("defect"), ReviewPolicySettings.Defaults,
            DemoReviewFixture.Result with { Findings = findings }, CancellationToken.None);
}
