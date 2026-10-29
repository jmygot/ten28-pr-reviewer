using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.GitHub;

internal sealed class GitHubRepository(HttpClient httpClient, IConfiguration configuration) : IGitHubRepository
{
    internal const int MaxFiles = 3000;
    internal const int FilesPerPage = 100;
    internal const int MaxResponseBytes = 10 * 1024 * 1024;
    internal const int MaxTotalResponseBytes = 32 * 1024 * 1024;

    /// <inheritdoc />
    public async Task<PullRequestSnapshot> GetPullRequestAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = configuration["GITHUB_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Missing required configuration: GITHUB_TOKEN.");

        var apiBase = new Uri((configuration["GITHUB_API_URL"] ?? "https://api.github.com").TrimEnd('/') + "/", UriKind.Absolute);
        var path = $"repos/{Uri.EscapeDataString(request.RepositoryOwner)}/{Uri.EscapeDataString(request.RepositoryName)}/pulls/{request.PullRequestNumber}";
        var totalBytes = 0;
        using var initialDocument = await ReadJsonAsync(path);
        var initial = ReadMetadata(initialDocument.RootElement, request.PullRequestNumber);
        var files = ImmutableArray.CreateBuilder<ChangedFile>(initial.FileCount);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; files.Count < initial.FileCount; page++)
        {
            using var document = await ReadJsonAsync($"{path}/files?per_page={FilesPerPage}&page={page}");
            var entries = document.RootElement;
            var expectedCount = Math.Min(FilesPerPage, initial.FileCount - files.Count);
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != expectedCount)
                throw new InvalidOperationException("GitHub returned an incomplete file list.");

            foreach (var entry in entries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = ReadFile(entry);
                if (!paths.Add(file.Path))
                    throw new InvalidOperationException("GitHub returned duplicate file paths.");
                files.Add(file);
            }
        }

        using var finalDocument = await ReadJsonAsync(path);
        var final = ReadMetadata(finalDocument.RootElement, request.PullRequestNumber);
        if (initial.BaseSha != final.BaseSha || initial.HeadSha != final.HeadSha || initial.FileCount != final.FileCount)
            throw new InvalidOperationException("The pull request changed during diff retrieval.");

        cancellationToken.ThrowIfCancellationRequested();
        return new PullRequestSnapshot(request, final.Title, final.Body, true, final.BaseSha, final.HeadSha, files.ToImmutable());

        async Task<JsonDocument> ReadJsonAsync(string relativePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBase, relativePath));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.UserAgent.ParseAdd("Ten28-PrReviewer/1.0");
            message.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("GitHub returned an invalid snapshot response.");

            var limit = Math.Min(MaxResponseBytes, MaxTotalResponseBytes - totalBytes);
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidOperationException("GitHub snapshot response exceeds retrieval limits.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - body.Length + 1)), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) break;
                if (body.Length + count > limit)
                    throw new InvalidOperationException("GitHub snapshot response exceeds retrieval limits.");
                body.Write(buffer, 0, count);
            }

            totalBytes += (int)body.Length;
            body.Position = 0;
            try
            {
                return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("GitHub returned invalid snapshot JSON.");
            }
        }
    }

    private static PullRequestMetadata ReadMetadata(JsonElement element, int number)
    {
        if (ReadNonnegativeInteger(element, "number") != number)
            throw new InvalidOperationException("GitHub returned an unexpected pull request number.");
        if (RequiredText(element, "state") != "open")
            throw new InvalidOperationException("The pull request is not open.");

        var count = ReadNonnegativeInteger(element, "changed_files");
        if (count > MaxFiles)
            throw new InvalidOperationException("The pull request exceeds the file retrieval limit.");

        var baseSha = RequiredText(Property(element, "base"), "sha");
        var headSha = RequiredText(Property(element, "head"), "sha");
        if (baseSha.Length != 40 || !baseSha.All(Uri.IsHexDigit) || headSha.Length != 40 || !headSha.All(Uri.IsHexDigit))
            throw new InvalidOperationException("GitHub returned invalid commit identities.");

        return new PullRequestMetadata(RequiredText(element, "title"), OptionalText(element, "body"), baseSha, headSha, count);
    }

    private static ChangedFile ReadFile(JsonElement element)
    {
        var status = RequiredText(element, "status") switch
        {
            "added" => FileChangeStatus.Added,
            "modified" => FileChangeStatus.Modified,
            "removed" => FileChangeStatus.Deleted,
            "renamed" => FileChangeStatus.Renamed,
            _ => throw new InvalidOperationException("GitHub returned an unsupported file status.")
        };
        var previousPath = status == FileChangeStatus.Renamed
            ? RequiredText(element, "previous_filename")
            : OptionalText(element, "previous_filename");
        var patch = OptionalText(element, "patch");
        return new ChangedFile(RequiredText(element, "filename"), previousPath, status,
            ReadNonnegativeInteger(element, "additions"), ReadNonnegativeInteger(element, "deletions"),
            string.IsNullOrEmpty(patch) ? null : patch);
    }

    private static int ReadNonnegativeInteger(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0
            ? number : throw new InvalidOperationException("GitHub returned invalid numeric metadata.");
    }

    private static string RequiredText(JsonElement element, string name) =>
        OptionalText(element, name) is { } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidOperationException("GitHub omitted required snapshot metadata.");

    private static string? OptionalText(JsonElement element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw new InvalidOperationException("GitHub returned invalid text metadata.")
    };

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private sealed record PullRequestMetadata(string Title, string? Body, string BaseSha, string HeadSha, int FileCount);
}
