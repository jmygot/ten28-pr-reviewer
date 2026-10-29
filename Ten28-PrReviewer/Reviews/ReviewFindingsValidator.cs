using System.Collections.Immutable;

namespace Ten28_PrReviewer.Reviews;

internal sealed class ReviewFindingsValidator : IReviewFindingsValidator
{
    /// <inheritdoc />
    public Task<ImmutableArray<ReviewFinding>> ValidateAsync(
        PullRequestSnapshot snapshot, ReviewPolicy policy, LlmReviewResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReviewPolicySettings.ValidateLimits(policy.Limits);
        var coverage = snapshot.Coverage;
        if (snapshot.ChangedFiles.IsDefault || coverage is null || coverage.SkippedFiles.IsDefault
            || coverage.SelectedFiles != snapshot.ChangedFiles.Length
            || coverage.TotalChangedFiles != (long)coverage.SelectedFiles + coverage.SkippedFiles.Length
            || snapshot.ChangedFiles.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path)))
            throw new InvalidOperationException("Finding validation requires selected input with consistent coverage.");
        if (result.Findings.IsDefault) throw new InvalidOperationException("Invalid review findings collection.");
        var diffs = new Dictionary<string, ParsedReviewDiff?>(StringComparer.Ordinal);
        foreach (var file in snapshot.ChangedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!diffs.TryAdd(file.Path, UnifiedDiffParser.Parse(file, cancellationToken)))
                throw new InvalidOperationException("Finding validation requires unique selected paths.");
        }
        var accepted = ImmutableArray.CreateBuilder<ReviewFinding>();
        var seen = new HashSet<(string Path, int? Line, string Title)>();
        foreach (var finding in result.Findings.Where(finding => finding is not null).OrderBy(finding => finding.Severity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(finding.Severity) || finding.Line is <= 0
                || string.IsNullOrWhiteSpace(finding.Title) || string.IsNullOrWhiteSpace(finding.Path)
                || string.IsNullOrWhiteSpace(finding.Explanation) || string.IsNullOrWhiteSpace(finding.Recommendation)
                || !diffs.TryGetValue(finding.Path, out var diff) || diff is null) continue;
            var (quotes, hasProse) = Evidence(finding.Explanation, cancellationToken);
            if (!hasProse) continue;
            var grounded = quotes.Where(quote => quote.Any(char.IsLetterOrDigit) && diff.ChangedLines.Contains(quote))
                .ToHashSet(StringComparer.Ordinal);
            if (grounded.Count == 0) continue;
            var validated = finding.Line is { } line && diff.AddedLines.TryGetValue(line, out var source) && grounded.Contains(source)
                ? finding : finding with { Line = null };
            var title = string.Join(' ', finding.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
            if (seen.Add((validated.Path, validated.Line, title))) accepted.Add(validated);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(accepted.ToImmutable());
    }

    private static (List<string> Quotes, bool HasProse) Evidence(string explanation, CancellationToken cancellationToken)
    {
        var quotes = new List<string>();
        var hasProse = false;
        foreach (var line in explanation.Split(['\r', '\n']))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runs = new List<(int Start, int Length)>();
            for (var index = 0; index < line.Length;)
            {
                if (line[index] != '`') { index++; continue; }
                var start = index;
                while (index < line.Length && line[index] == '`') index++;
                runs.Add((start, index - start));
            }
            var closingRuns = new int[runs.Count];
            var nextRun = new Dictionary<int, int>();
            for (var index = runs.Count - 1; index >= 0; index--)
            {
                closingRuns[index] = nextRun.GetValueOrDefault(runs[index].Length, -1);
                nextRun[runs[index].Length] = index;
            }
            var proseStart = 0;
            for (var index = 0; index < runs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var closing = closingRuns[index];
                if (closing < 0) continue;
                hasProse |= line[proseStart..runs[index].Start].Any(char.IsLetterOrDigit);
                quotes.Add(line[(runs[index].Start + runs[index].Length)..runs[closing].Start].Trim());
                proseStart = runs[closing].Start + runs[closing].Length;
                index = closing;
            }
            hasProse |= line[proseStart..].Any(char.IsLetterOrDigit);
        }
        return (quotes, hasProse);
    }
}
