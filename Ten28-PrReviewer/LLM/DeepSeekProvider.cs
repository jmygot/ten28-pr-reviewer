using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.LLM;

internal sealed class DeepSeekProvider(
    HttpClient httpClient, IOptions<DeepSeekOptions> options, TimeProvider timeProvider) : ILLMProvider
{
    internal const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <inheritdoc />
    public string ModelId => "deepseek-flash";

    /// <inheritdoc />
    public async Task<LlmReviewResult> ReviewAsync(
        PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        settings.Validate();
        var prompt = ReviewPromptBuilder.Build(snapshot, policy);
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Missing required environment variable: DEEPSEEK_API_KEY.");
        var payload = JsonSerializer.Serialize(new
        {
            model = ModelId,
            messages = new[]
            {
                new { role = "system", content = prompt.SystemMessage },
                new { role = "user", content = prompt.UserMessage }
            },
            stream = false,
            thinking = new { type = "enabled" },
            reasoning_effort = "high",
            response_format = new { type = "json_object" },
            max_tokens = settings.MaxOutputTokens
        });
        var started = timeProvider.GetTimestamp();
        using var overallDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(settings.OverallTimeoutSeconds), timeProvider);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overallDeadline.Token);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                overall.Token.ThrowIfCancellationRequested();
                TimeSpan retryDelay;
                using (var attemptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), timeProvider))
                using (var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(overall.Token, attemptDeadline.Token))
                {
                    var token = attemptCancellation.Token;
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    token.ThrowIfCancellationRequested();
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var body = await ReadBodyAsync(response.Content, token);
                        var result = ReadEnvelope(body, policy.Limits.MaxFindings);
                        token.ThrowIfCancellationRequested();
                        return result;
                    }
                    var status = (int)response.StatusCode;
                    if (attempt >= settings.MaxRetries || (status != 429 && status is not (>= 500 and <= 599)))
                        throw ReviewRunDiagnostics.HttpFailure($"DeepSeek request failed (HTTP {status}).", status);
                    retryDelay = RetryDelay(response, attempt);
                }
                var remaining = TimeSpan.FromSeconds(settings.OverallTimeoutSeconds) - timeProvider.GetElapsedTime(started);
                if (retryDelay > TimeSpan.FromSeconds(30) || retryDelay >= remaining)
                    throw new InvalidOperationException("DeepSeek retry wait exceeds the remaining limits.");
                await Task.Delay(retryDelay, timeProvider, overall.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new TimeoutException("DeepSeek request deadline exceeded.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("DeepSeek transport failed.");
        }
    }

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var delay = TimeSpan.FromSeconds(2 << attempt) + TimeSpan.FromMilliseconds(Random.Shared.Next(251));
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta ?? (retryAfter?.Date - timeProvider.GetUtcNow());
        return requested is { } wait && wait > delay ? wait : delay;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidOperationException("DeepSeek response exceeds its size limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxResponseBytes - body.Length + 1)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) return body.ToArray();
            if (body.Length + count > MaxResponseBytes)
                throw new InvalidOperationException("DeepSeek response exceeds its size limit.");
            body.Write(buffer, 0, count);
        }
    }

    private static LlmReviewResult ReadEnvelope(byte[] body, int maxFindings)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            RequireObject(root);
            var model = Text(root, "model");
            var choices = root.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw InvalidResponse();
            var choice = choices[0];
            RequireObject(choice);
            if (Text(choice, "finish_reason") != "stop" || choice.GetProperty("index").GetInt32() != 0) throw InvalidResponse();
            var message = choice.GetProperty("message");
            RequireObject(message);
            if (Text(message, "role") != "assistant") throw InvalidResponse();
            if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null
                && (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0)) throw InvalidResponse();
            var content = Text(message, "content");
            long? inputTokens = null;
            long? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            {
                RequireObject(usage);
                inputTokens = TokenCount(usage, "prompt_tokens");
                outputTokens = TokenCount(usage, "completion_tokens");
            }
            return ReviewResponseReader.Read(content, maxFindings, new ReviewUsage("deepseek", model, inputTokens, outputTokens));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidResponse();
        }
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw InvalidResponse();
    }

    private static string Text(JsonElement value, string name)
    {
        var text = value.GetProperty(name);
        return text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()! : throw InvalidResponse();
    }

    private static long? TokenCount(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var count)) return null;
        return count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) && number >= 0
            ? number : throw InvalidResponse();
    }

    private static InvalidOperationException InvalidResponse() => new("Invalid DeepSeek response.");
}
