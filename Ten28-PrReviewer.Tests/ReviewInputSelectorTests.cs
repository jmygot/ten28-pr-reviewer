using System.Collections.Immutable;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewInputSelectorTests
{
    /// <summary>Checks mandatory exclusions work at repository root and nested paths without case sensitivity.</summary>
    [Theory]
    [InlineData("bin/source.cs")]
    [InlineData("src/OBJ/source.cs")]
    [InlineData("dist/script.js")]
    [InlineData("src/lib.min.js")]
    [InlineData("package-lock.json")]
    [InlineData("src/Models.g.cs")]
    [InlineData("Models.g.i.cs")]
    [InlineData("src/models.generated.ts")]
    [InlineData("Form.Designer.cs")]
    public void GeneratedAndExcludedPathsAreRecorded(string path)
    {
        var prepared = Select([File(path)]);

        Assert.Empty(prepared.ChangedFiles);
        Assert.Equal(new SkippedReviewFile(path, ReviewSkipReason.ExcludedPath), Assert.Single(prepared.Coverage!.SkippedFiles));
        Assert.Equal(1, prepared.Coverage.TotalChangedFiles);
    }

    /// <summary>Checks distinct content exclusions preserve an accurate reason rather than guessing binary status.</summary>
    [Theory]
    [InlineData("image.png", "patch", ReviewSkipReason.UnsupportedExtension)]
    [InlineData("file.cs", null, ReviewSkipReason.PatchUnavailable)]
    [InlineData("file.cs", "", ReviewSkipReason.PatchUnavailable)]
    [InlineData("file.cs", " \r\n", ReviewSkipReason.PatchUnavailable)]
    [InlineData("file.cs", "GIT binary patch\nliteral 1", ReviewSkipReason.BinaryOrNonTextPatch)]
    [InlineData("file.cs", "Binary files old and new differ", ReviewSkipReason.BinaryOrNonTextPatch)]
    [InlineData("file.cs", "before\0after", ReviewSkipReason.BinaryOrNonTextPatch)]
    public void ContentExclusionsHaveDeterministicReasons(string path, string? patch, ReviewSkipReason reason)
    {
        var prepared = Select([File(path, patch)]);

        Assert.Equal(reason, Assert.Single(prepared.Coverage!.SkippedFiles).Reason);
    }

    /// <summary>Checks selection sorts current paths, retains whole files, and leaves the original snapshot intact.</summary>
    [Fact]
    public void SelectionIsDeterministicAndPreservesOriginal()
    {
        var renamed = File("a.CS", "@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file") with
        { Status = FileChangeStatus.Renamed, PreviousPath = "bin/old.cs" };
        var deleted = File("z.cs") with { Status = FileChangeStatus.Deleted };
        var original = DemoReviewFixture.Snapshot with { ChangedFiles = [deleted, File("skip.png"), renamed] };

        var prepared = ReviewInputSelector.Select(original, ReviewPolicySettings.Defaults);
        var reordered = Select(original.ChangedFiles.Reverse().ToImmutableArray());

        Assert.Equal(new[] { "a.CS", "z.cs" }, prepared.ChangedFiles.Select(file => file.Path));
        Assert.Equal(prepared.ChangedFiles.ToArray(), reordered.ChangedFiles.ToArray());
        Assert.Same(renamed, prepared.ChangedFiles[0]);
        Assert.Same(deleted, prepared.ChangedFiles[1]);
        Assert.Equal(3, original.ChangedFiles.Length);
        Assert.Null(original.Coverage);
        Assert.Equal(3, prepared.Coverage!.TotalChangedFiles);
        Assert.Equal(2, prepared.Coverage.SelectedFiles);
        Assert.Single(prepared.Coverage.SkippedFiles);
    }

    /// <summary>Checks below, exact, and above file-count limits with no file loss in coverage.</summary>
    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    public void FileCountBoundary(int count)
    {
        var prepared = Select(Enumerable.Range(0, count).Select(index => File($"{index:D2}.cs")).ToImmutableArray());

        Assert.Equal(Math.Min(count, 30), prepared.ChangedFiles.Length);
        Assert.Equal(Math.Max(count - 30, 0), prepared.Coverage!.SkippedFiles.Length);
        Assert.All(prepared.Coverage.SkippedFiles, file => Assert.Equal(ReviewSkipReason.FileCountLimit, file.Reason));
        Assert.Equal(count, prepared.ChangedFiles.Length + prepared.Coverage.SkippedFiles.Length);
    }

    /// <summary>Checks UTF-8 bytes determine the single-patch boundary, including multibyte characters.</summary>
    [Theory]
    [InlineData(39999, false)]
    [InlineData(40000, false)]
    [InlineData(40001, false)]
    [InlineData(39999, true)]
    [InlineData(40000, true)]
    [InlineData(40001, true)]
    public void SinglePatchByteBoundary(int bytes, bool unicode)
    {
        var patch = unicode ? new string('é', bytes / 2) + (bytes % 2 == 1 ? "a" : "") : new string('a', bytes);
        var prepared = Select([File("source.cs", patch)]);

        Assert.Equal(bytes <= 40000 ? 1 : 0, prepared.ChangedFiles.Length);
        if (bytes > 40000) Assert.Equal(ReviewSkipReason.SinglePatchLimit, Assert.Single(prepared.Coverage!.SkippedFiles).Reason);
        else Assert.Equal(patch, Assert.Single(prepared.ChangedFiles).Patch);
    }

    /// <summary>Checks the cumulative patch-byte boundary independently of the single-file limit.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void TotalPatchByteBoundary(int delta)
    {
        var files = Enumerable.Range(0, 6).Select(index => File($"{index}.cs", new string('a', 40000)))
            .Append(File("6.cs", new string('a', 10000 + delta))).ToImmutableArray();

        var prepared = Select(files);

        Assert.Equal(delta <= 0 ? 7 : 6, prepared.ChangedFiles.Length);
        if (delta > 0) Assert.Equal(ReviewSkipReason.TotalPatchLimit, Assert.Single(prepared.Coverage!.SkippedFiles).Reason);
    }

    /// <summary>Checks lower limits skip whole oversized patches and still consider later smaller files.</summary>
    [Fact]
    public void LaterSmallFilesCanFitAfterSkippedPatch()
    {
        var policy = ReviewPolicySettings.Defaults with { Limits = new ReviewLimits(3, 10, 8, 2) };
        var prepared = Select([File("c.cs", "cc"), File("a.cs", "aaaaaaa"), File("b.cs", "bbbb"), File("d.cs", "123456789")], policy);

        Assert.Equal(new[] { "a.cs", "c.cs" }, prepared.ChangedFiles.Select(file => file.Path));
        Assert.Equal(new[] { ReviewSkipReason.TotalPatchLimit, ReviewSkipReason.SinglePatchLimit }, prepared.Coverage!.SkippedFiles.Select(file => file.Reason));
        Assert.Equal(4, prepared.Coverage.TotalChangedFiles);
        Assert.Contains("4 changed; 2 selected; 2 skipped", ReviewInputSelector.CoverageStatement(prepared.Coverage));
        Assert.Contains("SinglePatchLimit: 1", ReviewInputSelector.CoverageStatement(prepared.Coverage));
        Assert.Contains("TotalPatchLimit: 1", ReviewInputSelector.CoverageStatement(prepared.Coverage));
    }

    /// <summary>Checks invalid typed budgets are rejected before selection.</summary>
    [Fact]
    public void InvalidTypedLimitsFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => Select([File("source.cs")], ReviewPolicySettings.Defaults with { Limits = new ReviewLimits(MaxFindings: 11) }));
    }

    private static ChangedFile File(string path, string? patch = "patch") => new(path, null, FileChangeStatus.Modified, 1, 1, patch);

    private static PullRequestSnapshot Select(ImmutableArray<ChangedFile> files, ReviewPolicy? policy = null) =>
        ReviewInputSelector.Select(DemoReviewFixture.Snapshot with { ChangedFiles = files }, policy ?? ReviewPolicySettings.Defaults);
}
