using System.Collections.Immutable;

namespace Ten28_PrReviewer.Reviews;

/// <summary>Reads a review request from the configured event source.</summary>
public interface IReviewEventSource
{
    /// <summary>Reads the event without publishing a review.</summary>
    /// <param name="cancellationToken">Cancels event reading.</param>
    /// <returns>The request, or null when there is no applicable event.</returns>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    /// <remarks>Source failures propagate to the caller.</remarks>
    Task<ReviewRequest?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Provides read-only access to pull request metadata and changes.</summary>
public interface IGitHubRepository
{
    /// <summary>Reads a snapshot without executing or changing repository content.</summary>
    /// <param name="request">Identifies the repository and pull request.</param>
    /// <param name="cancellationToken">Cancels snapshot retrieval.</param>
    /// <returns>A snapshot at the retrieved head commit.</returns>
    /// <exception cref="OperationCanceledException">Retrieval was cancelled.</exception>
    /// <remarks>Repository access failures propagate to the caller.</remarks>
    Task<PullRequestSnapshot> GetPullRequestAsync(ReviewRequest request, CancellationToken cancellationToken);
}

/// <summary>Supplies trusted policy independently of generated review content.</summary>
public interface IReviewPolicyService
{
    /// <summary>Obtains review instructions without publishing a review.</summary>
    /// <param name="snapshot">Snapshot whose trusted policy is needed.</param>
    /// <param name="cancellationToken">Cancels policy loading.</param>
    /// <returns>The policy to use for generation and validation.</returns>
    /// <exception cref="OperationCanceledException">Loading was cancelled.</exception>
    /// <remarks>Policy loading failures propagate to the caller.</remarks>
    Task<ReviewPolicy> GetPolicyAsync(PullRequestSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Generates review content without GitHub write access.</summary>
public interface ILLMProvider
{
    /// <summary>The requested model identity, available without making a billable call.</summary>
    string ModelId { get; }

    /// <summary>Generates an unvalidated review; a live implementation may incur model usage.</summary>
    /// <param name="snapshot">Untrusted pull request content to analyze.</param>
    /// <param name="policy">Trusted review instructions.</param>
    /// <param name="cancellationToken">Cancels generation.</param>
    /// <returns>Proposed review content and available usage.</returns>
    /// <exception cref="OperationCanceledException">Generation was cancelled.</exception>
    /// <remarks>Provider failures propagate to the caller; this operation never publishes.</remarks>
    Task<LlmReviewResult> ReviewAsync(
        PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken);
}

/// <summary>Reads successful reviews without generating or publishing content.</summary>
public interface IReviewHistory
{
    /// <summary>Finds a submitted bot review for the exact reviewed commit and application key.</summary>
    /// <param name="snapshot">Identifies the repository, PR, and reviewed commit.</param>
    /// <param name="reviewKey">Deterministic application review key.</param>
    /// <param name="cancellationToken">Cancels history retrieval.</param>
    /// <returns>The prior review identity, or null after a complete search without a match.</returns>
    /// <exception cref="OperationCanceledException">Retrieval was cancelled.</exception>
    /// <remarks>Lookup failures propagate and must prevent generation or publication.</remarks>
    Task<PriorReview?> FindSuccessfulReviewAsync(PullRequestSnapshot snapshot, string reviewKey,
        CancellationToken cancellationToken);
}

/// <summary>Determines which generated findings may be published.</summary>
public interface IReviewFindingsValidator
{
    /// <summary>Validates findings without publishing or modifying repository data.</summary>
    /// <param name="snapshot">Changed files and commit against which to validate.</param>
    /// <param name="policy">Trusted validation policy.</param>
    /// <param name="result">Unvalidated model output.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>Grounded findings with verified inline lines or null lines for summary-only observations; may be empty.</returns>
    /// <exception cref="OperationCanceledException">Validation was cancelled.</exception>
    /// <remarks>Validation failures propagate and must prevent publication.</remarks>
    Task<ImmutableArray<ReviewFinding>> ValidateAsync(
        PullRequestSnapshot snapshot, ReviewPolicy policy, LlmReviewResult result,
        CancellationToken cancellationToken);
}

/// <summary>Publishes a non-blocking comment review for the reviewed commit.</summary>
public interface IReviewPublisher
{
    /// <summary>Publishes the summary and validated findings against the snapshot's head SHA.</summary>
    /// <param name="snapshot">Identifies the target pull request and reviewed commit.</param>
    /// <param name="summary">Summary to publish.</param>
    /// <param name="findings">Validated inline findings with non-null changed RIGHT-side lines; may be empty for a summary-only review.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task that completes only when publication succeeds.</returns>
    /// <exception cref="OperationCanceledException">Publication was cancelled.</exception>
    /// <remarks>
    /// A live implementation writes a COMMENT review, never an approval or change request.
    /// Publication failures propagate; cancellation cannot undo an already completed write.
    /// </remarks>
    Task PublishAsync(
        PullRequestSnapshot snapshot, string summary, ImmutableArray<ReviewFinding> findings,
        CancellationToken cancellationToken);
}
