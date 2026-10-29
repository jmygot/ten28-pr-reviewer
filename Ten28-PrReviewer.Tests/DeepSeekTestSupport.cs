using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.LLM;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

/// <summary>Serializes tests that temporarily replace the process API key with synthetic credentials.</summary>
[CollectionDefinition("DeepSeek process environment", DisableParallelization = true)]
public sealed class DeepSeekEnvironmentCollection;

internal sealed class DeepSeekFixture : IDisposable
{
    internal const string Key = "synthetic-DEEPSEEK-SECRET-SENTINEL";
    private readonly string? previousKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    internal DeepSeekOptions Settings { get; } = new();
    internal ManualReviewClock Clock { get; } = new();
    internal RecordingDeepSeekHandler Http { get; } = new();
    internal HttpClient Client { get; }
    internal DeepSeekProvider Provider { get; }

    internal DeepSeekFixture()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", Key);
        Client = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
        Provider = new DeepSeekProvider(Client, Options.Create(Settings), Clock);
    }

    internal Task<LlmReviewResult> ReviewAsync(CancellationToken token = default) =>
        Provider.ReviewAsync(ReviewPromptFixtures.Selected("defect"), ReviewPolicySettings.Defaults, token);

    internal static JsonObject Envelope(string? content = ReviewOutputContract.Example) => new()
    {
        ["model"] = "deepseek-flash-synthetic-revision",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["finish_reason"] = "stop",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant", ["content"] = content, ["reasoning_content"] = "REASONING_SENTINEL"
            }
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 13, ["completion_tokens"] = 7 }
    };

    internal static HttpResponseMessage Json(JsonObject? envelope = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent((envelope ?? Envelope()).ToJsonString(), Encoding.UTF8, "application/json")
    };

    /// <inheritdoc />
    public void Dispose()
    {
        Client.Dispose();
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previousKey);
    }
}

internal sealed class RecordingDeepSeekHandler : HttpMessageHandler
{
    internal List<HttpRequestMessage> Requests { get; } = [];
    internal List<string> Payloads { get; } = [];
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
        (_, _) => Task.FromResult(DeepSeekFixture.Json());

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        return await OnSend(request, cancellationToken);
    }
}

internal sealed class DeepSeekCapturedLogs : ILoggerProvider
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
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception) + exception);
    }
}

internal sealed class ManualReviewClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private readonly SemaphoreSlim timerCreated = new(0);
    private long ticks;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() { lock (gate) return ticks; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(GetTimestamp());

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal async Task<TimeSpan> WaitForTimerAsync(double minimumSeconds, double maximumSeconds)
    {
        while (true)
        {
            lock (gate)
            {
                var timer = timers.FirstOrDefault(timer => !timer.Disposed && timer.Due != long.MaxValue
                    && TimeSpan.FromTicks(timer.Due - ticks).TotalSeconds >= minimumSeconds
                    && TimeSpan.FromTicks(timer.Due - ticks).TotalSeconds <= maximumSeconds);
                if (timer is not null) return TimeSpan.FromTicks(timer.Due - ticks);
            }
            if (!await timerCreated.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("Expected fake timer was not scheduled.");
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        long target;
        lock (gate) target = ticks + elapsed.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => !timer.Disposed && timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null) { ticks = target; return; }
                ticks = next.Due;
                next.Due = next.Period > 0 ? next.Due + next.Period : long.MaxValue;
            }
            next.Callback(next.State);
        }
    }

    private sealed class ManualTimer(ManualReviewClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long Due { get; set; } = long.MaxValue;
        internal long Period { get; private set; }
        internal bool Disposed { get; private set; }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                Period = period.Ticks;
            }
            owner.timerCreated.Release();
            return true;
        }

        /// <inheritdoc />
        public void Dispose() { lock (owner.gate) Disposed = true; }

        /// <inheritdoc />
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
