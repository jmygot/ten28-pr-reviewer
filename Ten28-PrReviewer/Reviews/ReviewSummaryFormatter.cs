using System.Collections.Immutable;
using System.Text;

namespace Ten28_PrReviewer.Reviews;

internal static class ReviewSummaryFormatter
{
    internal static string Format(PullRequestSnapshot snapshot, ReviewPolicy policy, LlmReviewResult result,
        ImmutableArray<ReviewFinding> findings)
    {
        var summary = findings.IsEmpty
            ? "No actionable findings in the analyzed diff. This does not replace human review or testing."
            : EscapeMarkdown(result.Summary);
        summary += "\n\nFindings: " + string.Join("; ", Enum.GetValues<ReviewSeverity>()
            .Select(severity => $"{severity.ToString().ToLowerInvariant()}: {findings.Count(finding => finding.Severity == severity)}")) + ".";
        var observations = findings.Where(finding => finding.Line is null).ToArray();
        if (observations.Length > 0)
            summary += "\n\n### Unanchored observations\n\n" + string.Join("\n\n", observations.Select(FormatFinding));
        summary += "\n\n" + ReviewInputSelector.CoverageStatement(snapshot.Coverage!);
        var version = typeof(ReviewSummaryFormatter).Assembly.GetName().Version?.ToString() ?? "unknown";
        return summary + $"\n\nReviewed commit: {EscapeMarkdown(snapshot.HeadSha)}; policy: {EscapeMarkdown(policy.Version)}; "
            + $"provider: {EscapeMarkdown(result.Usage.Provider)}; model: {EscapeMarkdown(result.Usage.Model)}; worker: {EscapeMarkdown(version)}.";
    }

    internal static string FormatFinding(ReviewFinding finding) =>
        $"[{finding.Severity.ToString().ToLowerInvariant()}] {EscapeMarkdown(finding.Title)} ({EscapeMarkdown(finding.Path)}): "
        + $"{EscapeMarkdown(finding.Explanation)} Recommendation: {EscapeMarkdown(finding.Recommendation)}";

    internal static string EscapeMarkdown(string text)
    {
        var escaped = new StringBuilder();
        foreach (var character in text)
        {
            switch (character)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '@': escaped.Append("&#64;"); break;
                default:
                    if (char.IsWhiteSpace(character) || char.IsControl(character)) escaped.Append(' ');
                    else
                    {
                        if ("\\`*_{}[]()#+-.!|~".Contains(character)) escaped.Append('\\');
                        escaped.Append(character);
                    }
                    break;
            }
        }
        return escaped.ToString();
    }
}
