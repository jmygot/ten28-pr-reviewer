using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ten28_PrReviewer.Reviews;

internal sealed record ReviewIdentity(string Repository, int PullRequest, string HeadSha, string PolicyVersion,
    string PolicyHash, string ModelId, string WorkerVersion)
{
    private const string Prefix = "<!-- company-pr-reviewer:v1 ";
    private const string Suffix = " -->";
    internal static string CurrentWorkerVersion => typeof(ReviewIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReviewIdentity).Assembly.GetName().Version?.ToString() ?? "unknown";

    internal string Key => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
    internal string Marker => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { key = Key, identity = this })) + Suffix;

    internal static ReviewIdentity Create(PullRequestSnapshot snapshot, ReviewPolicy policy, string modelId,
        string? workerVersion = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new InvalidOperationException("Missing requested model identity.");
        var policyBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            policy.Version, policy.Instructions, policy.Limits,
            AllowedExtensions = Normalize(policy.AllowedExtensions), ExcludedGlobs = Normalize(policy.ExcludedGlobs)
        });
        return new ReviewIdentity(snapshot.Request.RepositoryOwner.ToLowerInvariant() + "/" + snapshot.Request.RepositoryName.ToLowerInvariant(),
            snapshot.Request.PullRequestNumber, snapshot.HeadSha.ToLowerInvariant(), policy.Version,
            Convert.ToHexStringLower(SHA256.HashData(policyBytes)), modelId, workerVersion ?? CurrentWorkerVersion);
    }

    internal static bool TryRead(string body, out ReviewIdentity? identity)
    {
        identity = null;
        var lines = body.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1 || !lines[0].EndsWith(Suffix, StringComparison.Ordinal) || lines[0].Length > 8192) return false;
        try
        {
            var encoded = lines[0][Prefix.Length..^Suffix.Length];
            var bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded) return false;
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!HasFields(root, ["key", "identity"])) return false;
            var fields = root.GetProperty("identity");
            if (!HasFields(fields, ["Repository", "PullRequest", "HeadSha", "PolicyVersion", "PolicyHash", "ModelId", "WorkerVersion"])) return false;
            var parsed = fields.Deserialize<ReviewIdentity>();
            if (parsed is null || parsed.PullRequest <= 0 || !IsHex(parsed.HeadSha, 40) || !IsHex(parsed.PolicyHash, 64)
                || string.IsNullOrWhiteSpace(parsed.Repository) || parsed.Repository != parsed.Repository.ToLowerInvariant()
                || string.IsNullOrWhiteSpace(parsed.PolicyVersion) || string.IsNullOrWhiteSpace(parsed.ModelId)
                || string.IsNullOrWhiteSpace(parsed.WorkerVersion) || root.GetProperty("key").GetString() != parsed.Key) return false;
            identity = parsed;
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);

    private static string[] Normalize(IEnumerable<string> values) => values.Select(value => value.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool HasFields(JsonElement element, string[] names) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal));
}
