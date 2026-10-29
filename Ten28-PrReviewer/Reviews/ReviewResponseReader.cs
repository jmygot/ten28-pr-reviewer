using System.Collections.Immutable;
using System.Text.Json;

namespace Ten28_PrReviewer.Reviews;

internal static class ReviewResponseReader
{
    internal static LlmReviewResult Read(string json, int maxFindings, ReviewUsage usage)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireProperties(root, ["summary", "findings"]);
            var summary = Text(root, "summary");
            var items = root.GetProperty("findings");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maxFindings) throw Invalid();
            var findings = ImmutableArray.CreateBuilder<ReviewFinding>();
            foreach (var item in items.EnumerateArray())
            {
                RequireProperties(item, ["severity", "title", "path", "line", "side", "explanation", "recommendation"]);
                var severity = Text(item, "severity") switch
                {
                    "critical" => ReviewSeverity.Critical,
                    "high" => ReviewSeverity.High,
                    "medium" => ReviewSeverity.Medium,
                    "low" => ReviewSeverity.Low,
                    _ => throw Invalid()
                };
                if (Text(item, "side") != "RIGHT") throw Invalid();
                var location = item.GetProperty("line");
                int? line = null;
                if (location.ValueKind != JsonValueKind.Null)
                {
                    if (location.ValueKind != JsonValueKind.Number || !location.TryGetInt32(out var number) || number < 1)
                        throw Invalid();
                    line = number;
                }
                findings.Add(new ReviewFinding(severity, Text(item, "title"), Text(item, "path"), line,
                    Text(item, "explanation"), Text(item, "recommendation")));
            }
            return new LlmReviewResult(summary, findings.ToImmutable(), usage);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw Invalid();
        }
    }

    private static void RequireProperties(JsonElement value, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != required.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Any(name => !required.Contains(name, StringComparer.Ordinal))) throw Invalid();
    }

    private static string Text(JsonElement value, string property)
    {
        var text = value.GetProperty(property);
        return text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()! : throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Invalid structured review response.");
}
