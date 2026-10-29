using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.LLM;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer;

internal sealed class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            using var host = CreateBuilder(args).Build();
            return await RunAsync(host);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Application startup failed.");
            return 1;
        }
    }

    internal static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args.Where(argument => argument != "--demo").ToArray(),
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Services.AddHttpClient();
        builder.Services.AddOptions<HostOptions>()
            .Bind(builder.Configuration.GetSection("Host"));

        if (args.Contains("--demo", StringComparer.Ordinal))
        {
            builder.Services.AddDemoReviewPipeline();
        }
        else
        {
            builder.Services.AddHttpClient<IReviewEventSource, GitHubReviewEventSource>().RemoveAllLoggers();
            builder.Services.AddHttpClient<IGitHubRepository, GitHubRepository>().RemoveAllLoggers();
            builder.Services.AddHttpClient<IReviewPolicyService, GitHubReviewPolicyService>().RemoveAllLoggers();
            builder.Services.AddHttpClient<IReviewHistory, GitHubReviewHistory>(client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
                .RemoveAllLoggers();
            builder.Services.AddHttpClient<IReviewPublisher, GitHubReviewPublisher>(client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
                .RemoveAllLoggers();
            builder.Services.AddSingleton<ReviewOrchestrator>();
            builder.Services.AddSingleton<IReviewFindingsValidator, ReviewFindingsValidator>();
            builder.Services.AddOptions<DeepSeekOptions>().Bind(builder.Configuration.GetSection("DeepSeek"));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHttpClient<ILLMProvider, DeepSeekProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
                .RemoveAllLoggers();
        }

        return builder;
    }

    internal static async Task<int> RunAsync(IHost host, CancellationToken cancellationToken = default)
    {
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.ApplicationStopping);
        var exitCode = 0;
        ReviewOrchestrator? orchestrator = null;
        var diagnostics = new ReviewRunDiagnostics(host.Services.GetService<TimeProvider>() ?? TimeProvider.System);

        try
        {
            await host.StartAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            orchestrator = host.Services.GetRequiredService<ReviewOrchestrator>();
            var outcome = await orchestrator.RunAsync(cancellation.Token);
            if (host.Services.GetService<DemoRecordingReviewPublisher>() is not null)
            {
                logger.LogInformation(
                    "Simulated offline review {Status}. Head SHA: {HeadSha}; findings: {FindingCount}. No review was sent to GitHub.",
                    outcome.Status, outcome.HeadSha, outcome.Findings.Length);
            }
            else
            {
                logger.LogInformation("Review {Status}. Head SHA: {HeadSha}; findings: {FindingCount}. {Coverage}",
                    outcome.Status, outcome.HeadSha, outcome.Findings.Length,
                    outcome.Coverage is null ? "" : ReviewInputSelector.CoverageStatement(outcome.Coverage));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            diagnostics.Fail(new OperationCanceledException(cancellation.Token), cancellation.Token);
            logger.LogInformation("Application execution cancelled.");
            exitCode = 130;
        }
        catch (GitHubReviewPublicationException error)
        {
            logger.LogError("Review publication failed: {Reason} HTTP: {StatusCode}; retry at UTC: {RetryAt}; delivery may have succeeded: {DeliveryMayHaveSucceeded}.",
                error.Message, error.StatusCode, error.RetryAt, error.DeliveryMayHaveSucceeded);
            exitCode = 1;
        }
        catch (Exception error)
        {
            diagnostics.Fail(error, cancellation.Token);
            logger.LogError("Application execution failed.");
            exitCode = 1;
        }
        finally
        {
            diagnostics = orchestrator?.Diagnostics ?? diagnostics;
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                logger.LogError("Application shutdown failed.");
                if (exitCode == 0)
                {
                    diagnostics.BeginStage("shutdown");
                    diagnostics.Fail(error, CancellationToken.None);
                    exitCode = 1;
                }
            }
            await diagnostics.WriteAsync(host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), logger,
                host.Services.GetService<DemoRecordingReviewPublisher>() is not null);
        }

        return exitCode;
    }
}
