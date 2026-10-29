using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Ten28_PrReviewer.Reviews;

internal static class ReviewPolicySettings
{
    internal static ReviewPolicy Defaults { get; } = new("central-v1", """
        Review the selected diff for correctness, security, regressions, validation defects,
        significant performance issues, and transaction safety. Report only actionable defects
        supported by evidence in the selected changes. Explain the failure scenario and a concrete
        correction. Exclude stylistic-only findings and speculative issues. Do not claim that the
        code is safe or that unselected files were reviewed. Respect the effective findings limit.
        """);

    internal static ReviewPolicy ReadCentral(IConfiguration configuration)
    {
        if (configuration.GetSection("Review").Value is not null) throw InvalidSettings();
        var settings = new JsonObject();
        foreach (var child in configuration.GetSection("Review").GetChildren())
        {
            var key = child.Key.ToLowerInvariant() switch
            {
                "maxchangedfilesanalyzed" => "maxChangedFilesAnalyzed",
                "maxtotalpatchbytes" => "maxTotalPatchBytes",
                "maxsinglepatchbytes" => "maxSinglePatchBytes",
                "maxfindings" => "maxFindings",
                "allowedextensions" => "allowedExtensions",
                "excludedglobs" => "excludedGlobs",
                _ => throw InvalidSettings()
            };
            if (key is "allowedExtensions" or "excludedGlobs")
            {
                if (child.Value is not null) throw InvalidSettings();
                var values = new JsonArray();
                foreach (var item in child.GetChildren())
                {
                    if (item.Key != values.Count.ToString(CultureInfo.InvariantCulture) || item.Value is null || item.GetChildren().Any())
                        throw InvalidSettings();
                    values.Add(item.Value);
                }
                settings[key] = values;
            }
            else
            {
                if (child.GetChildren().Any() || !int.TryParse(child.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw InvalidSettings();
                settings[key] = value;
            }
        }
        using var document = JsonDocument.Parse(settings.ToJsonString());
        return Apply(Defaults, document.RootElement);
    }

    internal static ReviewPolicy Apply(ReviewPolicy central, JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object) throw InvalidSettings();
        var policy = central;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in settings.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw InvalidSettings();
            var value = property.Value;
            policy = property.Name switch
            {
                "maxChangedFilesAnalyzed" => policy with { Limits = policy.Limits with { MaxChangedFilesAnalyzed = Limit(value, central.Limits.MaxChangedFilesAnalyzed) } },
                "maxTotalPatchBytes" => policy with { Limits = policy.Limits with { MaxTotalPatchBytes = Limit(value, central.Limits.MaxTotalPatchBytes) } },
                "maxSinglePatchBytes" => policy with { Limits = policy.Limits with { MaxSinglePatchBytes = Limit(value, central.Limits.MaxSinglePatchBytes) } },
                "maxFindings" => policy with { Limits = policy.Limits with { MaxFindings = Limit(value, central.Limits.MaxFindings) } },
                "allowedExtensions" => policy with { AllowedExtensions = Extensions(value, central.AllowedExtensions) },
                "excludedGlobs" => policy with { ExcludedGlobs = central.ExcludedGlobs.AddRange(Globs(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray() },
                _ => throw InvalidSettings()
            };
        }
        ValidateLimits(policy.Limits);
        return policy;
    }

    internal static void ValidateLimits(ReviewLimits limits)
    {
        if (limits.MaxChangedFilesAnalyzed is < 1 or > 30 || limits.MaxTotalPatchBytes is < 1 or > 250000
            || limits.MaxSinglePatchBytes is < 1 or > 40000 || limits.MaxFindings is < 1 or > 10)
            throw InvalidSettings();
    }

    private static int Limit(JsonElement element, int ceiling) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value > 0 && value <= ceiling
            ? value : throw InvalidSettings();

    private static ImmutableArray<string> Extensions(JsonElement element, ImmutableArray<string> allowed)
    {
        var extensions = Strings(element);
        if (extensions.Any(value => !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))) throw InvalidSettings();
        return extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static ImmutableArray<string> Globs(JsonElement element)
    {
        var globs = Strings(element);
        foreach (var glob in globs)
        {
            if (string.IsNullOrWhiteSpace(glob) || glob.Length > 256
                || glob.Any(character => char.IsControl(character) || "\\:?![]{}".Contains(character))
                || glob.Split('/').Any(segment => segment is "" or "." or ".." || (segment.Contains("**") && segment != "**")))
                throw InvalidSettings();
        }
        return globs;
    }

    private static ImmutableArray<string> Strings(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw InvalidSettings();
        var values = ImmutableArray.CreateBuilder<string>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidSettings();
            values.Add(value.GetString()!);
        }
        return values.ToImmutable();
    }

    private static InvalidOperationException InvalidSettings() => new("Invalid review policy configuration.");
}
