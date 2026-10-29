using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ten28_PrReviewer.Reviews;

internal sealed record ParsedReviewDiff(
    ImmutableDictionary<int, string> AddedLines, ImmutableHashSet<string> ChangedLines);

internal static class UnifiedDiffParser
{
    private static readonly Regex Header = new(
        @"^@@ -([0-9]+)(?:,([0-9]+))? \+([0-9]+)(?:,([0-9]+))? @@(?: .*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static ParsedReviewDiff? Parse(ChangedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(file.Patch) || file.Additions < 0 || file.Deletions < 0
            || !Enum.IsDefined(file.Status)
            || (file.Status == FileChangeStatus.Added && file.Deletions != 0)
            || (file.Status == FileChangeStatus.Deleted && file.Additions != 0)) return null;
        var added = ImmutableDictionary.CreateBuilder<int, string>();
        var changed = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        long oldLine = 0, newLine = 0, oldRemaining = 0, newRemaining = 0;
        long oldEnd = 0, newEnd = 0, additions = 0, deletions = 0;
        var hasHunk = false;
        var oldEnded = false;
        var newEnded = false;
        var lastSource = '\0';
        var lines = file.Patch.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            if (index == lines.Length - 1 && line.Length == 0) break;
            if (line.EndsWith('\r')) line = line[..^1];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (oldRemaining != 0 || newRemaining != 0) return null;
                var match = Header.Match(line);
                if (!match.Success || !Range(match.Groups[1], match.Groups[2], out var oldStart, out var oldCount)
                    || !Range(match.Groups[3], match.Groups[4], out var newStart, out var newCount)
                    || (oldCount == 0 && newCount == 0)) return null;
                var oldBoundary = oldCount == 0 ? oldStart + 1L : oldStart;
                var newBoundary = newCount == 0 ? newStart + 1L : newStart;
                if (oldBoundary < oldEnd || newBoundary < newEnd
                    || (oldEnded && (oldCount > 0 || oldBoundary != oldEnd))
                    || (newEnded && (newCount > 0 || newBoundary != newEnd))) return null;
                oldEnd = oldBoundary + oldCount;
                newEnd = newBoundary + newCount;
                oldLine = oldStart;
                newLine = newStart;
                oldRemaining = oldCount;
                newRemaining = newCount;
                lastSource = '\0';
                hasHunk = true;
                continue;
            }
            if (!hasHunk) return null;
            if (line == "\\ No newline at end of file")
            {
                if (lastSource == '\0' || (lastSource != '+' && oldRemaining != 0)
                    || (lastSource != '-' && newRemaining != 0)) return null;
                if (lastSource != '+') oldEnded = true;
                if (lastSource != '-') newEnded = true;
                lastSource = '\0';
                continue;
            }
            if (line.Length == 0) return null;
            var kind = line[0];
            switch (kind)
            {
                case ' ':
                    if (oldRemaining == 0 || newRemaining == 0 || oldEnded || newEnded) return null;
                    oldLine++;
                    newLine++;
                    oldRemaining--;
                    newRemaining--;
                    break;
                case '-':
                    if (oldRemaining == 0 || oldEnded) return null;
                    changed.Add(line[1..].Trim());
                    oldLine++;
                    oldRemaining--;
                    deletions++;
                    break;
                case '+':
                    if (newRemaining == 0 || newEnded) return null;
                    var source = line[1..].Trim();
                    if (!added.TryAdd((int)newLine, source)) return null;
                    changed.Add(source);
                    newLine++;
                    newRemaining--;
                    additions++;
                    break;
                default:
                    return null;
            }
            lastSource = kind;
        }
        return hasHunk && oldRemaining == 0 && newRemaining == 0
            && additions == file.Additions && deletions == file.Deletions
            ? new ParsedReviewDiff(added.ToImmutable(), changed.ToImmutable()) : null;
    }

    private static bool Range(Group startGroup, Group countGroup, out int start, out int count)
    {
        count = 1;
        return int.TryParse(startGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && (!countGroup.Success || int.TryParse(countGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
            && (count == 0 || start >= 1) && start + (long)count - 1 <= int.MaxValue;
    }
}
