using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewRunDiagnosticsTests
{
    /// <summary>Checks deterministic stage durations and usage remain available when validation fails after a model response.</summary>
    [Fact]
    public async Task RetainsUsageAndTimingAfterValidationFailure()
    {
        using var scenario = new DuplicateReviewPipelineTests.Scenario { FailureStage = "validation" };
        var orchestrator = scenario.CreateOrchestrator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync());
        var diagnostics = orchestrator.Diagnostics;
        Assert.Equal("Failed", diagnostics.Outcome);
        Assert.Equal("validation", diagnostics.Stage);
        Assert.Equal(100, diagnostics.ElapsedMilliseconds);
        Assert.Equal(10, diagnostics.StageMilliseconds["event_authorization"]);
        Assert.Equal(20, diagnostics.StageMilliseconds["snapshot"]);
        Assert.Equal(30, diagnostics.StageMilliseconds["generation"]);
        Assert.Equal(40, diagnostics.StageMilliseconds["validation"]);
        Assert.Equal(13, diagnostics.Usage!.InputTokens);
        Assert.Equal(7, diagnostics.Usage.OutputTokens);
    }

    /// <summary>Checks host logs and appended job summaries describe each terminal outcome without source text or secrets.</summary>
    /// <param name="mode">Host outcome or summary output variation.</param>
    [Theory]
    [InlineData("success")]
    [InlineData("duplicate")]
    [InlineData("generation")]
    [InlineData("validation")]
    [InlineData("denied")]
    [InlineData("uncertain")]
    [InlineData("cancelled")]
    [InlineData("missing-usage")]
    [InlineData("no-summary")]
    [InlineData("unwritable-summary")]
    [InlineData("hostile-model")]
    public async Task HostReportsSafeTerminalOutcome(string mode)
    {
        using var scenario = new DuplicateReviewPipelineTests.Scenario();
        var directory = Path.Combine(Path.GetTempPath(), "Ten28-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "summary.md");
        var logs = new Logs();
        try
        {
            await File.WriteAllTextAsync(path, "Existing step summary\n");
            if (mode == "duplicate") await scenario.CreateOrchestrator().RunAsync();
            if (mode is "generation" or "validation") scenario.FailureStage = mode;
            if (mode is "denied" or "uncertain") scenario.Store.Mode = mode;
            if (mode == "missing-usage") scenario.ResultUsage = null;
            if (mode == "hostile-model") scenario.ResultUsage = new ReviewUsage("SECRET_PROVIDER", "synthetic-model<script>SECRET_EXCEPTION</script>", 13, 7);
            var builder = Program.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(scenario.Configuration.AsEnumerable());
            builder.Configuration["GITHUB_STEP_SUMMARY"] = mode switch
            {
                "no-summary" => null,
                "unwritable-summary" => directory,
                _ => path
            };
            builder.Services.AddSingleton<IReviewEventSource>(scenario);
            builder.Services.AddSingleton<IGitHubRepository>(scenario);
            builder.Services.AddSingleton<IReviewPolicyService>(scenario);
            builder.Services.AddSingleton<ILLMProvider>(scenario);
            builder.Services.AddSingleton<IReviewFindingsValidator>(scenario);
            builder.Services.AddSingleton<IReviewHistory>(scenario.History);
            builder.Services.AddSingleton<IReviewPublisher>(scenario.Publisher);
            builder.Services.AddSingleton<TimeProvider>(scenario.Clock);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            using var host = builder.Build();
            using var cancellation = new CancellationTokenSource();
            if (mode == "cancelled") cancellation.Cancel();
            var expectedCode = mode == "cancelled" ? 130 : mode is "generation" or "validation" or "denied" or "uncertain" ? 1 : 0;
            Assert.Equal(expectedCode, await Program.RunAsync(host, cancellation.Token));

            var output = string.Join('\n', logs.Messages);
            var summary = await File.ReadAllTextAsync(path);
            Assert.StartsWith("Existing step summary\n", summary);
            var expectedOutcome = mode == "duplicate" ? "Skipped" : expectedCode == 130 ? "Cancelled" : expectedCode == 1 ? "Failed" : "Completed";
            Assert.Contains("Review run " + expectedOutcome, output);
            if (mode is "no-summary" or "unwritable-summary") Assert.Equal("Existing step summary\n", summary);
            else
            {
                Assert.Contains("Company PR Reviewer", summary);
                Assert.Contains(expectedOutcome, summary);
                Assert.Contains("Generation started", summary);
                Assert.Contains("Elapsed ms", summary);
            }
            if (mode == "unwritable-summary") Assert.Contains("Could not write the GitHub step summary", output);
            if (mode is "validation" or "denied" or "uncertain") Assert.Contains("| Input tokens | 13 |", summary);
            if (mode == "missing-usage") Assert.Contains("| Input tokens | unknown |", summary);
            if (mode == "duplicate") { Assert.Contains("duplicate\\_review", summary); Assert.Contains("| Generation started | False |", summary); }
            if (mode == "uncertain") Assert.Contains("| Delivery may have succeeded | True |", summary);
            if (mode == "hostile-model") Assert.Contains("redacted", summary);
            if (expectedCode != 0) Assert.DoesNotContain("Review Completed", output);
            foreach (var text in new[] { output, summary })
            {
                Assert.DoesNotContain("SECRET_EXCEPTION", text);
                Assert.DoesNotContain("SECRET_RESPONSE", text);
                Assert.DoesNotContain("SECRET_PROVIDER", text);
                Assert.DoesNotContain("synthetic-pipeline-token", text);
                Assert.DoesNotContain(scenario.Snapshot.ChangedFiles[0].Patch!, text);
                Assert.DoesNotContain(scenario.Policy.Instructions, text);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Checks log and summary metadata cannot inject Markdown, workflow commands, or configured secrets.</summary>
    [Fact]
    public async Task RedactsHostileRequestAndCredentialMetadata()
    {
        using var scenario = new DuplicateReviewPipelineTests.Scenario();
        var diagnostics = new ReviewRunDiagnostics(scenario.Clock)
        {
            Request = scenario.Snapshot.Request with { RepositoryOwner = "::error::SECRET_EXCEPTION\n", Requester = "synthetic-pipeline-token" },
            ModelId = "synthetic-pipeline-token", HeadSha = "<script>SECRET_EXCEPTION</script>"
        };
        diagnostics.Finish("Skipped");
        var logs = new Logs();
        await diagnostics.WriteAsync(scenario.Configuration, logs.CreateLogger("test"), false);
        var output = string.Join('\n', logs.Messages);
        Assert.Contains("redacted", output);
        Assert.DoesNotContain("SECRET_EXCEPTION", output);
        Assert.DoesNotContain("synthetic-pipeline-token", output);
        Assert.DoesNotContain("::error::", output);
    }

    private sealed class Logs : ILoggerProvider
    {
        internal List<string> Messages { get; } = [];
        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        /// <inheritdoc />
        public void Dispose() { }
        private sealed class CapturedLogger(List<string> messages) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;
            /// <inheritdoc />
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }
}
