using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Ten28_PrReviewer.Reviews;

internal static class ReviewInputSelector
{
    internal static PullRequestSnapshot Select(PullRequestSnapshot snapshot, ReviewPolicy policy)
    {
        ReviewPolicySettings.ValidateLimits(policy.Limits);
        var exclusions = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var glob in policy.ExcludedGlobs) exclusions.AddInclude(glob);
        var selected = ImmutableArray.CreateBuilder<ChangedFile>();
        var skipped = ImmutableArray.CreateBuilder<SkippedReviewFile>();
        var totalBytes = 0;
        foreach (var file in snapshot.ChangedFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            ReviewSkipReason? reason = null;
            if (exclusions.Match(file.Path).HasMatches) reason = ReviewSkipReason.ExcludedPath;
            else if (!policy.AllowedExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase)) reason = ReviewSkipReason.UnsupportedExtension;
            else if (string.IsNullOrWhiteSpace(file.Patch)) reason = ReviewSkipReason.PatchUnavailable;
            else if (file.Patch.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
                || file.Patch.StartsWith("GIT binary patch", StringComparison.Ordinal)
                || file.Patch.StartsWith("Binary files ", StringComparison.Ordinal)) reason = ReviewSkipReason.BinaryOrNonTextPatch;
            else
            {
                var bytes = Encoding.UTF8.GetByteCount(file.Patch);
                if (bytes > policy.Limits.MaxSinglePatchBytes) reason = ReviewSkipReason.SinglePatchLimit;
                else if (selected.Count >= policy.Limits.MaxChangedFilesAnalyzed) reason = ReviewSkipReason.FileCountLimit;
                else if (bytes > policy.Limits.MaxTotalPatchBytes - totalBytes) reason = ReviewSkipReason.TotalPatchLimit;
                else
                {
                    selected.Add(file);
                    totalBytes += bytes;
                }
            }
            if (reason is { } value) skipped.Add(new SkippedReviewFile(file.Path, value));
        }
        return snapshot with
        {
            ChangedFiles = selected.ToImmutable(),
            Coverage = new ReviewCoverage(snapshot.ChangedFiles.Length, selected.Count, skipped.ToImmutable())
        };
    }

    internal static string CoverageStatement(ReviewCoverage coverage)
    {
        var reasons = coverage.SkippedFiles.GroupBy(file => file.Reason).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}: {group.Count()}");
        return $"Coverage: {coverage.TotalChangedFiles} changed; {coverage.SelectedFiles} selected; {coverage.SkippedFiles.Length} skipped."
            + (coverage.SkippedFiles.IsEmpty ? "" : " Skip reasons: " + string.Join(", ", reasons) + ".");
    }
}
