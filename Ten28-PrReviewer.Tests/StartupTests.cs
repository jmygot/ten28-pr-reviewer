using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ten28_PrReviewer.Tests;

public class StartupTests
{
    /// <summary>Checks that the application registers its infrastructure and loads its settings.</summary>
    [Fact]
    public void BuilderRegistersInfrastructureAndLoadsSettings()
    {
        using var host = Program.CreateBuilder([]).Build();

        Assert.NotNull(host.Services.GetRequiredService<ILogger<StartupTests>>());
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(5),
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    /// <summary>Checks that startup completes and the host stops without API credentials.</summary>
    [Fact]
    public async Task RunStartsAndStopsHostSuccessfully()
    {
        var service = new TestHostedService();
        using var host = CreateHost(service);

        Assert.Equal(0, await Program.RunAsync(host));
        Assert.True(service.Started);
        Assert.True(service.Stopped);
    }

    /// <summary>Checks that a cancelled execution returns the documented exit code.</summary>
    [Fact]
    public async Task RunReturns130WhenCancelled()
    {
        using var host = CreateHost(new TestHostedService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(130, await Program.RunAsync(host, cancellation.Token));
    }

    /// <summary>Checks that host shutdown signals cancel application work.</summary>
    [Fact]
    public async Task RunReturns130WhenHostRequestsShutdown()
    {
        var service = new TestHostedService();
        using var host = CreateHost(service);
        service.OnStart = () => host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        Assert.Equal(130, await Program.RunAsync(host));
        Assert.True(service.Stopped);
    }

    /// <summary>Checks that a failed startup returns an operational failure and stops the host.</summary>
    [Fact]
    public async Task RunReturns1WhenStartupFails()
    {
        var service = new TestHostedService { OnStart = () => throw new InvalidOperationException() };
        using var host = CreateHost(service);

        Assert.Equal(1, await Program.RunAsync(host));
        Assert.True(service.Stopped);
    }

    /// <summary>Checks that failure to stop the host is reported as an operational failure.</summary>
    [Fact]
    public async Task RunReturns1WhenShutdownFails()
    {
        using var host = CreateHost(new TestHostedService { FailOnStop = true });

        Assert.Equal(1, await Program.RunAsync(host));
    }

    private static IHost CreateHost(TestHostedService service)
    {
        var builder = Program.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["GITHUB_EVENT_NAME"] = null });
        builder.Services.AddSingleton<IHostedService>(service);
        return builder.Build();
    }

    private sealed class TestHostedService : IHostedService
    {
        internal Action? OnStart { get; set; }
        internal bool FailOnStop { get; init; }
        internal bool Started { get; private set; }
        internal bool Stopped { get; private set; }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnStart?.Invoke();
            Started = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return FailOnStop ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
        }
    }
}
