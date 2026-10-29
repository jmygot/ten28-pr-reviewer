using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.LLM;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class DemoReviewTests
{
    /// <summary>Checks the real demo registrations complete one review without HTTP or credentials.</summary>
    [Fact]
    public async Task DemoPipelinePublishesOneSyntheticReviewWithoutHttp()
    {
        var builder = Program.CreateBuilder(["--demo"]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GITHUB_EVENT_NAME"] = "issue_comment",
            ["GITHUB_EVENT_PATH"] = "unused-demo-event.json"
        });
        var http = new ForbiddenHttpHandler();
        builder.Services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => http));
        using var host = builder.Build();

        var outcome = await host.Services.GetRequiredService<ReviewOrchestrator>().RunAsync();

        Assert.Equal(ReviewStatus.Completed, outcome.Status);
        var publisher = host.Services.GetRequiredService<DemoRecordingReviewPublisher>();
        Assert.Same(publisher, host.Services.GetRequiredService<IReviewPublisher>());
        var publication = Assert.Single(publisher.Publications);
        Assert.Equal(new ReviewRequest("demo-owner", "demo-repository", 42, "demo-developer", 1001),
            publication.Snapshot.Request);
        Assert.True(publication.Snapshot.IsOpen);
        Assert.Equal(new string('a', 40), publication.Snapshot.BaseSha);
        Assert.Equal(new string('b', 40), publication.Snapshot.HeadSha);
        Assert.Equal(publication.Snapshot.HeadSha, outcome.HeadSha);
        Assert.Equal(1, outcome.Coverage!.SelectedFiles);
        Assert.Empty(outcome.Coverage.SkippedFiles);
        Assert.Contains("Coverage: 1 changed; 1 selected; 0 skipped.", publication.Summary);
        var finding = Assert.Single(publication.Findings);
        Assert.Equal("src/DisplayName.cs", finding.Path);
        Assert.Equal(3, finding.Line);
        Assert.Equal(ReviewSeverity.High, finding.Severity);
        Assert.Equal("demo-deepseek-stub", outcome.Usage!.Provider);
        Assert.Equal("synthetic-model", outcome.Usage.Model);
        Assert.Null(outcome.Usage.InputTokens);
        Assert.Null(outcome.Usage.OutputTokens);
        Assert.Equal(0, http.Attempts);
    }

    /// <summary>Checks console host execution logs only simulated metadata and retains other command-line settings.</summary>
    [Fact]
    public async Task DemoHostRunsOfflineAndLogsSimulatedMetadataOnly()
    {
        var builder = Program.CreateBuilder(["--demo", "--Host:ShutdownTimeout=00:00:07"]);
        Assert.Equal("00:00:07", builder.Configuration["Host:ShutdownTimeout"]);
        var http = new ForbiddenHttpHandler();
        var logs = new CapturedLogs();
        builder.Services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => http));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        using var host = builder.Build();

        Assert.Equal(0, await Program.RunAsync(host));

        Assert.Single(host.Services.GetRequiredService<DemoRecordingReviewPublisher>().Publications);
        var output = string.Join('\n', logs.Messages);
        Assert.Contains("Simulated offline review Completed", output);
        Assert.Contains("No review was sent to GitHub", output);
        Assert.DoesNotContain(DemoReviewFixture.Result.Summary, output);
        Assert.DoesNotContain(DemoReviewFixture.Snapshot.ChangedFiles[0].Patch!, output);
        Assert.DoesNotContain(DemoReviewFixture.Policy.Instructions, output);
        Assert.Equal(0, http.Attempts);
    }

    /// <summary>Checks ordinary startup does not register or invoke the synthetic pipeline.</summary>
    [Fact]
    public async Task NormalStartupDoesNotRegisterDemoServices()
    {
        var builder = Program.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["GITHUB_EVENT_NAME"] = null });
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<ReviewOrchestrator>());
        Assert.IsType<GitHubReviewEventSource>(host.Services.GetRequiredService<IReviewEventSource>());
        Assert.IsType<GitHubRepository>(host.Services.GetRequiredService<IGitHubRepository>());
        Assert.IsType<GitHubReviewPolicyService>(host.Services.GetRequiredService<IReviewPolicyService>());
        Assert.IsType<DeepSeekProvider>(host.Services.GetRequiredService<ILLMProvider>());
        Assert.IsType<ReviewFindingsValidator>(host.Services.GetRequiredService<IReviewFindingsValidator>());
        Assert.IsType<GitHubReviewPublisher>(host.Services.GetRequiredService<IReviewPublisher>());
        Assert.Equal(0, await Program.RunAsync(host));
    }

    /// <summary>Checks pipeline failures retain the host's operational failure exit code.</summary>
    [Theory]
    [InlineData("llm")]
    [InlineData("publication")]
    public async Task DemoHostReturns1WhenReviewFails(string stage)
    {
        var builder = Program.CreateBuilder(["--demo"]);
        var pipeline = new ReviewOrchestratorTests.PipelineSpy { FailureStage = stage };
        builder.Services.AddSingleton(pipeline.CreateOrchestrator());
        using var host = builder.Build();

        Assert.Equal(1, await Program.RunAsync(host));
        Assert.Empty(pipeline.Publications);
    }

    /// <summary>Checks host shutdown cancels an in-progress pipeline before publication.</summary>
    [Fact]
    public async Task DemoHostReturns130WhenShutdownIsRequestedDuringReview()
    {
        var builder = Program.CreateBuilder(["--demo"]);
        IHost? runningHost = null;
        var pipeline = new ReviewOrchestratorTests.PipelineSpy
        {
            OnStage = stage =>
            {
                if (stage == "llm")
                {
                    runningHost!.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                }
            }
        };
        builder.Services.AddSingleton(pipeline.CreateOrchestrator());
        using var host = builder.Build();
        runningHost = host;

        Assert.Equal(130, await Program.RunAsync(host));
        Assert.Equal(["event", "repository", "policy", "history", "llm"], pipeline.Calls);
        Assert.Empty(pipeline.Publications);
    }

    private sealed class ForbiddenHttpHandler : HttpMessageHandler
    {
        internal int Attempts { get; private set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("HTTP must not be used by the demo pipeline.");
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);

        /// <inheritdoc />
        public void Dispose() { }

        private sealed class CapturedLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
