using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class UnifiedDiffParserTests
{
    /// <summary>Provides valid API patches with the expected new-file anchor lines.</summary>
    /// <returns>Patches, metadata counts/status, and expected anchor numbers.</returns>
    public static IEnumerable<object[]> ValidPatches()
    {
        yield return [DemoReviewFixture.Snapshot.ChangedFiles[0].Patch!, 1, 1, FileChangeStatus.Modified, "3"];
        yield return ["@@ -10,2 +10,3 @@ heading\n before\n-old\n+first\n+second\n@@ -30 +31 @@ other\n-last\n+third", 3, 2, FileChangeStatus.Renamed, "11,12,31"];
        yield return ["@@ -0,0 +1,2 @@\n+first\n+second\n", 2, 0, FileChangeStatus.Added, "1,2"];
        yield return ["@@ -1,2 +0,0 @@\n-first\n-second", 0, 2, FileChangeStatus.Deleted, ""];
        yield return ["@@ -3,0 +4 @@\n+inserted", 1, 0, FileChangeStatus.Modified, "4"];
        yield return ["@@ -4 +3,0 @@\n-deleted", 0, 1, FileChangeStatus.Modified, ""];
        yield return ["@@ -1,2 +1,3 @@\n \n-old\n+\n+value", 2, 1, FileChangeStatus.Modified, "2,3"];
        yield return ["@@ -1,2 +1,2 @@ heading\n---- old header\n++++ new header\n-@@ old code\n+@@ new code", 2, 2, FileChangeStatus.Modified, "1,2"];
        yield return ["@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file", 1, 1, FileChangeStatus.Modified, "1"];
        yield return ["@@ -1,2 +1,2 @@\r\n-old\r\n+new\r\n context\r\n\\ No newline at end of file\r\n", 1, 1, FileChangeStatus.Modified, "1"];
        yield return ["@@ -2147483647 +2147483647 @@\n-old\n+new", 1, 1, FileChangeStatus.Modified, "2147483647"];
        yield return ["@@ -3,0 +4 @@\n+inserted\n@@ -4 +5 @@\n-old\n+replaced", 2, 1, FileChangeStatus.Modified, "4,5"];
        yield return ["@@ -1 +1,2 @@\n-old\n\\ No newline at end of file\n+new1\n+new2\n\\ No newline at end of file", 2, 1, FileChangeStatus.Modified, "1,2"];
        yield return ["@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+first\n@@ -1,0 +2 @@\n+second", 2, 1, FileChangeStatus.Modified, "1,2"];
        yield return ["@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file\n@@ -2 +1,0 @@\n-old2", 1, 2, FileChangeStatus.Modified, "1"];
    }

    /// <summary>Checks unified-diff coordinates and all changed source are preserved without context anchors.</summary>
    /// <param name="patch">Valid per-file API patch.</param>
    /// <param name="additions">Expected metadata additions.</param>
    /// <param name="deletions">Expected metadata deletions.</param>
    /// <param name="status">File change status.</param>
    /// <param name="anchors">Comma-separated new-file anchor lines.</param>
    [Theory]
    [MemberData(nameof(ValidPatches))]
    public void ParsesValidHunks(string patch, int additions, int deletions, FileChangeStatus status, string anchors)
    {
        var file = new ChangedFile("src/Current.cs", "src/Previous.cs", status, additions, deletions, patch);

        var parsed = UnifiedDiffParser.Parse(file, CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal(anchors, string.Join(',', parsed.AddedLines.Keys.Order()));
        var expectedChanged = patch.Split('\n').Where(line => line.StartsWith('+') || line.StartsWith('-'))
            .Select(line => line[1..].Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedChanged.SetEquals(parsed.ChangedLines));
        foreach (var source in parsed.AddedLines.Values) Assert.Contains(source, parsed.ChangedLines);
    }

    /// <summary>Checks only outer whitespace is normalized for evidence matching.</summary>
    [Fact]
    public void PreservesInternalSourceWhitespace()
    {
        var file = new ChangedFile("file.cs", null, FileChangeStatus.Modified, 1, 1,
            "@@ -1 +1 @@ title is not evidence\n-\treturn  old; \n+    return  value;\t\r\n");

        var parsed = UnifiedDiffParser.Parse(file, CancellationToken.None)!;

        Assert.Equal("return  value;", parsed.AddedLines[1]);
        Assert.Contains("return  old;", parsed.ChangedLines);
        Assert.DoesNotContain("title is not evidence", parsed.ChangedLines);
    }

    /// <summary>Provides malformed, ambiguous, unsupported, or truncated patches.</summary>
    /// <returns>Invalid patch strings with nominal one-line replacement metadata.</returns>
    public static IEnumerable<object?[]> InvalidPatches()
    {
        foreach (var patch in new[]
        {
            null, "", " ", "patch", "diff --git a/file.cs b/file.cs\n@@ -1 +1 @@\n-old\n+new",
            "@@@ -1 -1 +1 @@@\n-old\n+new", "@@ -1 +1 @@suffix\n-old\n+new",
            "@@ -0 +1 @@\n-old\n+new", "@@ -1 +0 @@\n-old\n+new",
            "@@ --1 +1 @@\n-old\n+new", "@@ -1,-1 +1 @@\n-old\n+new",
            "@@ -1,2147483648 +1 @@\n-old\n+new", "@@ -1 +2147483648 @@\n-old\n+new",
            "@@ -2147483647,2 +1 @@\n-old\n+new", "@@ -1 +2147483647,2 @@\n-old\n+new",
            "@@ -1,0 +1,0 @@", "@@ -1 +1 @@\n-old", "@@ -1,2 +1,2 @@\n-old\n+new",
            "@@ -1 +1 @@\n-old\n+new\n+extra", "@@ -1 +1 @@\n-old\n+new\n-removed",
            "@@ -1 +1 @@\n?unknown\n-old\n+new", "@@ -1 +1 @@\n\n-old\n+new",
            "@@ -1 +1 @@\n\\ No newline at end of file\n-old\n+new",
            "@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file\n\\ No newline at end of file",
            "@@ -1,2 +1,2 @@\n-old\n\\ No newline at end of file\n+new\n context",
            "@@ -1 +1 @@\n-old\n+new\n\\ unexpected marker",
            "@@ -1 +1 @@\n-old\n+new\n@@ -5,2 +5,2 @@\n-truncated\n+partial",
            "@@ -1 +1 @@\n-old\n+new\n\n"
        }) yield return [patch];
    }

    /// <summary>Checks an invalid later hunk cannot leave a usable partial map behind.</summary>
    /// <param name="patch">Invalid or missing API patch.</param>
    [Theory]
    [MemberData(nameof(InvalidPatches))]
    public void RejectsInvalidPatchCompletely(string? patch)
    {
        Assert.Null(UnifiedDiffParser.Parse(new ChangedFile("file.cs", null, FileChangeStatus.Modified, 1, 1, patch), CancellationToken.None));
    }

    /// <summary>Checks hunk ordering, overlap, and metadata totals independently of header syntax.</summary>
    /// <param name="nextHeader">Second hunk with overlapping or out-of-order coordinates.</param>
    [Theory]
    [InlineData("@@ -10 +10 @@")]
    [InlineData("@@ -9 +20 @@")]
    [InlineData("@@ -20 +9 @@")]
    public void RejectsOverlappingOrUnorderedRanges(string nextHeader)
    {
        var patch = "@@ -10 +10 @@\n-old\n+new\n" + nextHeader + "\n-again\n+other";
        Assert.Null(UnifiedDiffParser.Parse(new ChangedFile("file.cs", null, FileChangeStatus.Modified, 2, 2, patch), CancellationToken.None));
    }

    /// <summary>Checks metadata totals and status rules cannot contradict the parsed patch.</summary>
    /// <param name="additions">Reported additions.</param>
    /// <param name="deletions">Reported deletions.</param>
    /// <param name="status">Reported file status.</param>
    [Theory]
    [InlineData(0, 1, FileChangeStatus.Modified)]
    [InlineData(1, 0, FileChangeStatus.Modified)]
    [InlineData(2, 1, FileChangeStatus.Modified)]
    [InlineData(-1, 1, FileChangeStatus.Modified)]
    [InlineData(1, -1, FileChangeStatus.Modified)]
    [InlineData(1, 1, FileChangeStatus.Added)]
    [InlineData(1, 1, FileChangeStatus.Deleted)]
    [InlineData(1, 1, (FileChangeStatus)99)]
    public void RejectsContradictoryMetadata(int additions, int deletions, FileChangeStatus status)
    {
        Assert.Null(UnifiedDiffParser.Parse(new ChangedFile("file.cs", null, status, additions, deletions,
            "@@ -1 +1 @@\n-old\n+new"), CancellationToken.None));
    }

    /// <summary>Checks a newline marker prevents later source on the side already ended.</summary>
    [Fact]
    public void RejectsSourceAfterItsNoNewlineMarker()
    {
        var patch = "@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file\n@@ -2 +2 @@\n-old2\n+new2";
        Assert.Null(UnifiedDiffParser.Parse(new ChangedFile("file.cs", null, FileChangeStatus.Modified, 2, 2, patch), CancellationToken.None));
    }

    /// <summary>Checks empty ranges cannot move beyond an already established end of file.</summary>
    /// <param name="oldSide">Whether the old side or new side has ended.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsRangesBeyondKnownEndOfFile(bool oldSide)
    {
        var patch = oldSide
            ? "@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n@@ -5,0 +2 @@\n+extra"
            : "@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file\n@@ -2 +5,0 @@\n-extra";
        Assert.Null(UnifiedDiffParser.Parse(new ChangedFile("file.cs", null, FileChangeStatus.Modified,
            oldSide ? 2 : 1, oldSide ? 1 : 2, patch), CancellationToken.None));
    }

    /// <summary>Checks parser work honors cancellation before inspecting untrusted text.</summary>
    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() =>
            UnifiedDiffParser.Parse(DemoReviewFixture.Snapshot.ChangedFiles[0], cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }
}
