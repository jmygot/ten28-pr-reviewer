using System.Collections.Immutable;

namespace Ten28_PrReviewer.Reviews;

/// <summary>Identifies the developer's request to review a pull request.</summary>
/// <param name="RepositoryOwner">Owner of the repository receiving the review.</param>
/// <param name="RepositoryName">Name of the repository receiving the review.</param>
/// <param name="PullRequestNumber">Repository-local pull request number.</param>
/// <param name="Requester">Login of the requesting developer.</param>
/// <param name="CommentId">Identifier of the triggering conversation comment.</param>
public sealed record ReviewRequest(
    string RepositoryOwner,
    string RepositoryName,
    int PullRequestNumber,
    string Requester,
    long CommentId);

/// <summary>Captures the pull request and changed files at a particular head commit.</summary>
/// <param name="Request">Identity of the review request.</param>
/// <param name="Title">Pull request title, treated as untrusted data.</param>
/// <param name="Body">Optional description, treated as untrusted data.</param>
/// <param name="IsOpen">Whether the pull request is open.</param>
/// <param name="BaseSha">Commit from which the proposed changes are measured.</param>
/// <param name="HeadSha">Commit to which the review applies.</param>
/// <param name="ChangedFiles">Immutable collection of changed files.</param>
public sealed record PullRequestSnapshot(
    ReviewRequest Request,
    string Title,
    string? Body,
    bool IsOpen,
    string BaseSha,
    string HeadSha,
    ImmutableArray<ChangedFile> ChangedFiles)
{
    /// <summary>Selection coverage, or null on the original unfiltered snapshot.</summary>
    public ReviewCoverage? Coverage { get; init; }
}

/// <summary>Describes a file's change without requiring a local checkout.</summary>
/// <param name="Path">Current repository-relative path.</param>
/// <param name="PreviousPath">Original path for a renamed file, when available.</param>
/// <param name="Status">Kind of change.</param>
/// <param name="Additions">Number of added lines.</param>
/// <param name="Deletions">Number of deleted lines.</param>
/// <param name="Patch">Unified diff, or null when unavailable.</param>
public sealed record ChangedFile(
    string Path,
    string? PreviousPath,
    FileChangeStatus Status,
    int Additions,
    int Deletions,
    string? Patch);

/// <summary>Identifies the kind of file change.</summary>
public enum FileChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed
}

/// <summary>Describes a proposed finding; generation alone does not validate it.</summary>
/// <param name="Severity">Reported impact.</param>
/// <param name="Title">Short description of the defect.</param>
/// <param name="Path">Repository-relative affected file path.</param>
/// <param name="Line">Right-side changed line, or null for an unanchored observation.</param>
/// <param name="Explanation">Evidence and failure scenario.</param>
/// <param name="Recommendation">Suggested corrective action.</param>
public sealed record ReviewFinding(
    ReviewSeverity Severity,
    string Title,
    string Path,
    int? Line,
    string Explanation,
    string Recommendation);

/// <summary>Supported levels of finding impact.</summary>
public enum ReviewSeverity
{
    Critical,
    High,
    Medium,
    Low
}

/// <summary>Provider-reported usage; missing token counts remain unknown.</summary>
/// <param name="Provider">Provider identity, explicitly labeled for synthetic results.</param>
/// <param name="Model">Model identity, explicitly labeled for synthetic results.</param>
/// <param name="InputTokens">Reported input token count, or null when unavailable.</param>
/// <param name="OutputTokens">Reported output token count, or null when unavailable.</param>
public sealed record ReviewUsage(string Provider, string Model, long? InputTokens, long? OutputTokens);

/// <summary>Identifies an already submitted matching bot review.</summary>
/// <param name="Id">Repository review identifier supplied by GitHub.</param>
public sealed record PriorReview(long Id);

/// <summary>Trusted instructions used for a review.</summary>
/// <param name="Version">Identity of the policy revision.</param>
/// <param name="Instructions">Trusted review instructions.</param>
public sealed record ReviewPolicy(string Version, string Instructions)
{
    /// <summary>Effective limits; configuration may only decrease the central ceilings.</summary>
    public ReviewLimits Limits { get; init; } = new();

    /// <summary>Permitted file extensions, compared without case sensitivity.</summary>
    public ImmutableArray<string> AllowedExtensions { get; init; } = [".cs", ".csproj", ".json", ".sql", ".ts", ".tsx", ".js"];

    /// <summary>Mandatory and additional exclusions applied to current repository-relative paths.</summary>
    public ImmutableArray<string> ExcludedGlobs { get; init; } =
        ["**/bin/**", "**/obj/**", "**/dist/**", "**/*.min.js", "**/*lock*", "**/*.g.cs", "**/*.g.i.cs", "**/*.generated.*", "**/*.Designer.cs"];
}

/// <summary>Positive review budgets, bounded by the initial central defaults.</summary>
/// <param name="MaxChangedFilesAnalyzed">Maximum number of selected files.</param>
/// <param name="MaxTotalPatchBytes">Maximum selected patch bytes in UTF-8.</param>
/// <param name="MaxSinglePatchBytes">Maximum UTF-8 bytes in one selected patch.</param>
/// <param name="MaxFindings">Maximum validated findings published.</param>
public sealed record ReviewLimits(int MaxChangedFilesAnalyzed = 30, int MaxTotalPatchBytes = 250000,
    int MaxSinglePatchBytes = 40000, int MaxFindings = 10);

/// <summary>Accounts for every file in the original snapshot.</summary>
/// <param name="TotalChangedFiles">Number of original changed files.</param>
/// <param name="SelectedFiles">Number selected within the effective budgets.</param>
/// <param name="SkippedFiles">Paths and one reason for each excluded file.</param>
public sealed record ReviewCoverage(int TotalChangedFiles, int SelectedFiles, ImmutableArray<SkippedReviewFile> SkippedFiles);

/// <summary>Records why a changed file was not selected.</summary>
/// <param name="Path">Current repository-relative path.</param>
/// <param name="Reason">Deterministic exclusion reason.</param>
public sealed record SkippedReviewFile(string Path, ReviewSkipReason Reason);

/// <summary>Reasons for excluding complete patches from review input.</summary>
public enum ReviewSkipReason
{
    ExcludedPath,
    UnsupportedExtension,
    PatchUnavailable,
    BinaryOrNonTextPatch,
    SinglePatchLimit,
    FileCountLimit,
    TotalPatchLimit
}

/// <summary>Contains unvalidated generated review content and reported usage.</summary>
/// <param name="Summary">Generated summary.</param>
/// <param name="Findings">Proposed findings requiring independent validation.</param>
/// <param name="Usage">Provider identity and available token counts.</param>
public sealed record LlmReviewResult(
    string Summary,
    ImmutableArray<ReviewFinding> Findings,
    ReviewUsage Usage);

/// <summary>Reports completion after publication, or an intentional skip.</summary>
/// <param name="Status">Whether the pipeline completed or skipped work.</param>
/// <param name="Reason">Explanation for a skip; null for completion.</param>
/// <param name="HeadSha">Reviewed commit; null when no request was processed.</param>
/// <param name="Summary">Review summary; null when skipped.</param>
/// <param name="Findings">Accepted inline findings and null-line observations included in the summary.</param>
/// <param name="Usage">Usage when generation occurred; null when skipped.</param>
public sealed record ReviewOutcome(
    ReviewStatus Status,
    string? Reason,
    string? HeadSha,
    string? Summary,
    ImmutableArray<ReviewFinding> Findings,
    ReviewUsage? Usage)
{
    /// <summary>Application review key, when eligible input was prepared.</summary>
    public string? ReviewKey { get; init; }

    /// <summary>Existing bot review responsible for a duplicate skip.</summary>
    public PriorReview? PriorReview { get; init; }

    /// <summary>Coverage retained even when no eligible files remain and generation is skipped.</summary>
    public ReviewCoverage? Coverage { get; init; }
}

/// <summary>Successful terminal states; operational failures propagate as exceptions.</summary>
public enum ReviewStatus
{
    Completed,
    Skipped
}
