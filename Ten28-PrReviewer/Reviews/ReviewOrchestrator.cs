using System.Collections.Immutable;

namespace Ten28_PrReviewer.Reviews;

/// <summary>Coordinates one review without owning provider-specific behavior.</summary>
/// <param name="eventSource">Source of the review request.</param>
/// <param name="repository">Read-only pull request access.</param>
/// <param name="policyService">Trusted policy source.</param>
/// <param name="llmProvider">Review generator.</param>
/// <param name="validator">Independent finding validator.</param>
/// <param name="publisher">Non-blocking review publisher.</param>
/// <param name="history">Read-only lookup of successful bot reviews.</param>
/// <param name="timeProvider">Clock for run measurements; defaults to the system clock.</param>
public sealed class ReviewOrchestrator(
    IReviewEventSource eventSource,
    IGitHubRepository repository,
    IReviewPolicyService policyService,
    ILLMProvider llmProvider,
    IReviewFindingsValidator validator,
    IReviewPublisher publisher,
    IReviewHistory history,
    TimeProvider? timeProvider = null)
{
    internal ReviewRunDiagnostics Diagnostics { get; private set; } = new(timeProvider ?? TimeProvider.System);

    /// <summary>Processes one request, publishing only after all preceding stages succeed.</summary>
    /// <param name="cancellationToken">Cancels processing and is passed to every boundary.</param>
    /// <returns>A skipped outcome without a request, or a completed outcome after publication.</returns>
    /// <exception cref="OperationCanceledException">Processing was cancelled.</exception>
    /// <remarks>Dependency failures propagate without retry. Generation may incur usage and publication may write a review.</remarks>
    public async Task<ReviewOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        Diagnostics = new(timeProvider ?? TimeProvider.System);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Diagnostics.BeginStage("event_authorization");
            var request = await eventSource.ReadAsync(cancellationToken);
            Diagnostics.Request = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (request is null)
            {
                Diagnostics.Reason = "no_applicable_request";
                Diagnostics.Finish("Skipped");
                return new ReviewOutcome(ReviewStatus.Skipped, "No applicable review request.", null, null, [], null);
            }

            Diagnostics.BeginStage("snapshot");
            var snapshot = await repository.GetPullRequestAsync(request, cancellationToken);
            Diagnostics.HeadSha = snapshot.HeadSha;
            cancellationToken.ThrowIfCancellationRequested();
            Diagnostics.BeginStage("policy");
            var policy = await policyService.GetPolicyAsync(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Diagnostics.BeginStage("selection");
            snapshot = ReviewInputSelector.Select(snapshot, policy);
            Diagnostics.Coverage = snapshot.Coverage;
            var coverageStatement = ReviewInputSelector.CoverageStatement(snapshot.Coverage!);
            if (snapshot.ChangedFiles.IsEmpty)
            {
                Diagnostics.Reason = "no_eligible_files";
                Diagnostics.Finish("Skipped");
                return new ReviewOutcome(ReviewStatus.Skipped, "No eligible files. " + coverageStatement,
                    snapshot.HeadSha, null, [], null) { Coverage = snapshot.Coverage };
            }
            Diagnostics.ModelId = llmProvider.ModelId;
            var identity = ReviewIdentity.Create(snapshot, policy, llmProvider.ModelId);
            Diagnostics.ReviewKey = identity.Key;
            Diagnostics.BeginStage("history_before_generation");
            var prior = await history.FindSuccessfulReviewAsync(snapshot, identity.Key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (prior is not null) return Duplicate(prior, null);
            Diagnostics.BeginStage("generation");
            Diagnostics.GenerationStarted = true;
            var result = await llmProvider.ReviewAsync(snapshot, policy, cancellationToken);
            Diagnostics.Usage = result.Usage;
            cancellationToken.ThrowIfCancellationRequested();
            Diagnostics.BeginStage("validation");
            var findings = await validator.ValidateAsync(snapshot, policy, result, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            findings = findings.OrderBy(finding => finding.Severity).Take(policy.Limits.MaxFindings).ToImmutableArray();
            foreach (var severity in Enum.GetValues<ReviewSeverity>()) Diagnostics.SeverityCounts[severity] = findings.Count(finding => finding.Severity == severity);
            var summary = ReviewSummaryFormatter.Format(snapshot, policy, result, findings) + "\n\n" + identity.Marker;
            var inlineFindings = findings.Where(finding => finding.Line is not null).ToImmutableArray();
            Diagnostics.BeginStage("history_before_publication");
            prior = await history.FindSuccessfulReviewAsync(snapshot, identity.Key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (prior is not null) return Duplicate(prior, result.Usage);
            Diagnostics.BeginStage("publication");
            await publisher.PublishAsync(snapshot, summary, inlineFindings, cancellationToken);

            Diagnostics.Finish("Completed");
            return new ReviewOutcome(ReviewStatus.Completed, null, snapshot.HeadSha, summary, findings, result.Usage)
            { Coverage = snapshot.Coverage, ReviewKey = identity.Key };

            ReviewOutcome Duplicate(PriorReview review, ReviewUsage? usage)
            {
                Diagnostics.PriorReview = review;
                Diagnostics.Reason = "duplicate_review";
                Diagnostics.Finish("Skipped");
                return new ReviewOutcome(ReviewStatus.Skipped, "A successful bot review already exists for this review key.",
                    snapshot.HeadSha, null, [], usage) { Coverage = snapshot.Coverage, ReviewKey = identity.Key, PriorReview = review };
            }
        }
        catch (Exception error)
        {
            Diagnostics.Fail(error, cancellationToken);
            throw;
        }
    }
}
