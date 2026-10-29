using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Demo;

internal static class DemoReviewServices
{
    internal static void AddDemoReviewPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IReviewEventSource, DemoReviewEventSource>();
        services.AddSingleton<IGitHubRepository, DemoGitHubRepository>();
        services.AddSingleton<IReviewPolicyService, DemoReviewPolicyService>();
        services.AddSingleton<ILLMProvider, DemoDeepSeekProvider>();
        services.AddSingleton<IReviewFindingsValidator, ReviewFindingsValidator>();
        services.AddSingleton<IReviewHistory, DemoReviewHistory>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DemoRecordingReviewPublisher>();
        services.AddSingleton<IReviewPublisher>(provider => provider.GetRequiredService<DemoRecordingReviewPublisher>());
        services.AddSingleton<ReviewOrchestrator>();
    }
}

internal static class DemoReviewFixture
{
    internal static ReviewRequest Request { get; } = new("demo-owner", "demo-repository", 42, "demo-developer", 1001);

    internal static PullRequestSnapshot Snapshot { get; } = new(
        Request, "Synthetic display name change", "Offline fixture; no real repository is accessed.", true,
        new string('a', 40), new string('b', 40),
        [new ChangedFile("src/DisplayName.cs", null, FileChangeStatus.Modified, 1, 1,
            """
            @@ -1,4 +1,4 @@
             public static string DisplayName(string? name)
             {
            -    return name ?? "Unknown";
            +    return name.Trim();
             }
            """)]);

    internal static ReviewPolicy Policy { get; } = ReviewPolicySettings.Defaults with { Version = "demo-v1" };

    internal static LlmReviewResult Result { get; } = new(
        "Synthetic review: the changed return expression can dereference a null name.",
        [new ReviewFinding(ReviewSeverity.High, "Nullable name is dereferenced", "src/DisplayName.cs", 3,
            "The changed `return name.Trim();` throws when name is null.", "Preserve a null fallback before trimming.")],
        new ReviewUsage("demo-deepseek-stub", "synthetic-model", null, null));
}

internal sealed class DemoReviewEventSource : IReviewEventSource
{
    /// <inheritdoc />
    public Task<ReviewRequest?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReviewRequest?>(DemoReviewFixture.Request);
    }
}

internal sealed class DemoGitHubRepository : IGitHubRepository
{
    /// <inheritdoc />
    public Task<PullRequestSnapshot> GetPullRequestAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DemoReviewFixture.Snapshot with { Request = request });
    }
}

internal sealed class DemoReviewPolicyService(IConfiguration? configuration = null) : IReviewPolicyService
{
    /// <inheritdoc />
    public Task<ReviewPolicy> GetPolicyAsync(PullRequestSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(configuration is null ? DemoReviewFixture.Policy
            : ReviewPolicySettings.ReadCentral(configuration) with { Version = "demo-v1" });
    }
}

internal sealed class DemoDeepSeekProvider : ILLMProvider
{
    /// <inheritdoc />
    public string ModelId => "synthetic-model";

    /// <inheritdoc />
    public Task<LlmReviewResult> ReviewAsync(
        PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DemoReviewFixture.Result);
    }
}

internal sealed class DemoReviewHistory : IReviewHistory
{
    /// <inheritdoc />
    public Task<PriorReview?> FindSuccessfulReviewAsync(PullRequestSnapshot snapshot, string reviewKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PriorReview?>(null);
    }
}

internal sealed record DemoPublication(
    PullRequestSnapshot Snapshot, string Summary, ImmutableArray<ReviewFinding> Findings);

internal sealed class DemoRecordingReviewPublisher : IReviewPublisher
{
    private readonly List<DemoPublication> publications = [];

    internal IReadOnlyList<DemoPublication> Publications => publications.AsReadOnly();

    /// <inheritdoc />
    public Task PublishAsync(
        PullRequestSnapshot snapshot, string summary, ImmutableArray<ReviewFinding> findings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        publications.Add(new DemoPublication(snapshot, summary, findings));
        return Task.CompletedTask;
    }
}
