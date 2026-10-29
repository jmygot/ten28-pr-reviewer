using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class GitHubRepositoryTests
{
    /// <summary>Checks complete pagination, bounded request counts, headers, and snapshot identity.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(3000)]
    public async Task RetrievesCompleteSnapshot(int count)
    {
        using var fixture = new Fixture(count);

        var snapshot = await fixture.ReadAsync();

        Assert.Same(Fixture.Request, snapshot.Request);
        Assert.Equal("Synthetic PR", snapshot.Title);
        Assert.Equal("Untrusted description", snapshot.Body);
        Assert.True(snapshot.IsOpen);
        Assert.Equal(Fixture.BaseSha, snapshot.BaseSha);
        Assert.Equal(Fixture.HeadSha, snapshot.HeadSha);
        Assert.Equal(count, snapshot.ChangedFiles.Length);
        Assert.Equal(count, snapshot.ChangedFiles.Select(file => file.Path).Distinct().Count());
        var pages = (count + 99) / 100;
        Assert.Equal(pages + 2, fixture.Http.Paths.Count);
        Assert.Equal("/repos/company/project/pulls/42", fixture.Http.Paths[0]);
        Assert.Equal(fixture.Http.Paths[0], fixture.Http.Paths[^1]);
        for (var page = 1; page <= pages; page++)
            Assert.Equal($"/repos/company/project/pulls/42/files?per_page=100&page={page}", fixture.Http.Paths[page]);
    }

    /// <summary>Checks configured API roots and escaped path segments are used instead of payload URLs.</summary>
    [Fact]
    public async Task UsesConfiguredApiAndEscapedRepositoryIdentity()
    {
        using var fixture = new Fixture(0);
        fixture.Configuration["GITHUB_API_URL"] = "https://github.example/api/v3/";
        var request = Fixture.Request with { RepositoryOwner = "company name", RepositoryName = "project#name" };
        fixture.Http.OnSend = (message, _) =>
        {
            Assert.Equal("https://github.example/api/v3/repos/company%20name/project%23name/pulls/42", message.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(fixture.Metadata));
        };

        Assert.Same(request, (await fixture.Repository.GetPullRequestAsync(request, CancellationToken.None)).Request);
        Assert.Equal(2, fixture.Http.Paths.Count);
    }

    /// <summary>Checks all supported statuses, rename origins, deleted patches, and exact multi-hunk text.</summary>
    [Fact]
    public async Task PreservesFileAndHunkMetadata()
    {
        const string patch = "@@ -1,2 +1,2 @@ title\r\n context\r\n-old\r\n+new\r\n@@ -8 +8,2 @@\n-old\n+new\n+extra\n\\ No newline at end of file\n";
        using var fixture = new Fixture(4);
        fixture.Files = JsonSerializer.Serialize(new[]
        {
            new { filename = "new.cs", status = "added", previous_filename = (string?)null, additions = 3, deletions = 0, patch },
            new { filename = "modify.bin", status = "modified", previous_filename = (string?)null, additions = 3, deletions = 2, patch },
            new { filename = "deleted.cs", status = "removed", previous_filename = (string?)null, additions = 0, deletions = 4, patch },
            new { filename = "renamed.cs", status = "renamed", previous_filename = (string?)"original.cs", additions = 3, deletions = 2, patch }
        });

        var snapshot = await fixture.ReadAsync();

        Assert.Equal(new[] { FileChangeStatus.Added, FileChangeStatus.Modified, FileChangeStatus.Deleted, FileChangeStatus.Renamed },
            snapshot.ChangedFiles.Select(file => file.Status));
        Assert.All(snapshot.ChangedFiles, file => Assert.Equal(patch, file.Patch));
        Assert.Equal("original.cs", snapshot.ChangedFiles[3].PreviousPath);
        Assert.Equal(0, snapshot.ChangedFiles[0].Deletions);
        Assert.Equal(4, snapshot.ChangedFiles[2].Deletions);
        Assert.Equal("modify.bin", snapshot.ChangedFiles[1].Path);
    }

    /// <summary>Checks unavailable patches retain metadata without extension-based binary inference.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task MissingAndEmptyPatchesBecomeNull(string? patch)
    {
        using var fixture = new Fixture(1);
        var file = FileNode("image.png");
        Set(file, "patch", patch);
        fixture.Files = new JsonArray(file).ToJsonString();

        var result = Assert.Single((await fixture.ReadAsync()).ChangedFiles);

        Assert.Equal("image.png", result.Path);
        Assert.Null(result.Patch);
        Assert.Equal(1, result.Additions);
    }

    /// <summary>Checks missing, malformed, mismatched, closed, and oversized metadata fails before file retrieval.</summary>
    [Theory]
    [InlineData("number", null)]
    [InlineData("number", "41")]
    [InlineData("number", "\"42\"")]
    [InlineData("state", null)]
    [InlineData("state", "\"closed\"")]
    [InlineData("state", "\"unknown\"")]
    [InlineData("title", null)]
    [InlineData("title", "\" \"")]
    [InlineData("body", "42")]
    [InlineData("base", "null")]
    [InlineData("base.sha", "\"short\"")]
    [InlineData("head.sha", "\"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\"")]
    [InlineData("changed_files", null)]
    [InlineData("changed_files", "-1")]
    [InlineData("changed_files", "1.5")]
    [InlineData("changed_files", "3001")]
    [InlineData("changed_files", "2147483648")]
    public async Task InvalidMetadataStopsRetrieval(string path, string? value)
    {
        using var fixture = new Fixture(1);
        var metadata = JsonNode.Parse(fixture.Metadata)!;
        Set(metadata, path, value);
        fixture.Metadata = metadata.ToJsonString();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Single(fixture.Http.Paths);
    }

    /// <summary>Checks invalid root shapes and JSON fail without returning a snapshot or leaking response data.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{synthetic-github-token")]
    public async Task InvalidMetadataJsonIsSanitized(string json)
    {
        using var fixture = new Fixture(1) { Metadata = json };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());

        Assert.DoesNotContain(Fixture.Token, error.ToString());
        Assert.Single(fixture.Http.Paths);
    }

    /// <summary>Checks invalid file fields cannot enter a snapshot.</summary>
    [Theory]
    [InlineData("filename", null)]
    [InlineData("filename", "\"\"")]
    [InlineData("status", "\"copied\"")]
    [InlineData("status", null)]
    [InlineData("additions", "-1")]
    [InlineData("additions", "\"1\"")]
    [InlineData("deletions", null)]
    [InlineData("deletions", "2147483648")]
    [InlineData("patch", "42")]
    [InlineData("previous_filename", "42")]
    public async Task InvalidFileMetadataStopsRetrieval(string path, string? value)
    {
        using var fixture = new Fixture(1);
        var file = FileNode("file.cs");
        Set(file, path, value);
        fixture.Files = new JsonArray(file).ToJsonString();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Equal(2, fixture.Http.Paths.Count);
    }

    /// <summary>Checks renamed files must identify their original path.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\" \"")]
    public async Task RenameRequiresOriginalPath(string? previousPath)
    {
        using var fixture = new Fixture(1);
        var file = FileNode("new.cs");
        file["status"] = "renamed";
        Set(file, "previous_filename", previousPath);
        fixture.Files = new JsonArray(file).ToJsonString();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
    }

    /// <summary>Checks invalid, short, and oversized file pages cannot be silently accepted.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("{broken json")]
    public async Task InvalidFilePageFails(string body)
    {
        using var fixture = new Fixture(1) { Files = body };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Equal(2, fixture.Http.Paths.Count);
    }

    /// <summary>Checks count mismatches and duplicate paths, including across page boundaries, fail closed.</summary>
    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(101, 100, false)]
    [InlineData(101, 101, true)]
    public async Task IncompleteAndDuplicateFileListsFail(int expected, int actual, bool duplicate)
    {
        using var fixture = new Fixture(expected);
        fixture.Http.OnSend = (message, _) =>
        {
            if (!message.RequestUri!.AbsolutePath.EndsWith("/files"))
                return Task.FromResult(Json(fixture.Metadata));
            var page = int.Parse(message.RequestUri.Query.Split("page=")[^1]);
            var start = (page - 1) * 100;
            var files = Enumerable.Range(start, Math.Max(0, Math.Min(100, actual - start)))
                .Select(index => FileNode(duplicate && index == actual - 1 ? "file-0.cs" : $"file-{index}.cs"));
            return Task.FromResult(Json(new JsonArray(files.ToArray()).ToJsonString()));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
    }

    /// <summary>Checks base/head changes, closure, and file-count changes invalidate collection before generation.</summary>
    [Theory]
    [InlineData("base.sha", "\"cccccccccccccccccccccccccccccccccccccccc\"")]
    [InlineData("head.sha", "\"cccccccccccccccccccccccccccccccccccccccc\"")]
    [InlineData("changed_files", "2")]
    [InlineData("state", "\"closed\"")]
    public async Task ChangedPullRequestNeverReachesLlm(string path, string value)
    {
        using var fixture = new Fixture(1);
        fixture.Http.OnSend = (message, _) =>
        {
            var body = message.RequestUri!.AbsolutePath.EndsWith("/files") ? fixture.Files : fixture.Metadata;
            if (fixture.Http.Paths.Count == 3)
            {
                var metadata = JsonNode.Parse(body)!;
                Set(metadata, path, value);
                body = metadata.ToJsonString();
            }
            return Task.FromResult(Json(body));
        };
        var downstream = new ReviewOrchestratorTests.PipelineSpy();
        var orchestrator = new ReviewOrchestrator(downstream, fixture.Repository, downstream, downstream, downstream, downstream, downstream);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync());

        Assert.Equal(new[] { "event" }, downstream.Calls);
        Assert.Equal(3, fixture.Http.Paths.Count);
    }

    /// <summary>Checks HTTP failures at metadata, pagination, or verification stop without retries.</summary>
    [Theory]
    [InlineData(1, 401)]
    [InlineData(2, 403)]
    [InlineData(2, 404)]
    [InlineData(2, 429)]
    [InlineData(2, 500)]
    [InlineData(3, 503)]
    [InlineData(2, 204)]
    public async Task ApiFailuresStopAtTheFailingRequest(int attempt, int status)
    {
        using var fixture = new Fixture(1);
        fixture.Http.OnSend = (message, _) => Task.FromResult(fixture.Http.Paths.Count == attempt
            ? Json(Fixture.Token, (HttpStatusCode)status)
            : Json(message.RequestUri!.AbsolutePath.EndsWith("/files") ? fixture.Files : fixture.Metadata));

        var error = await Record.ExceptionAsync(() => fixture.ReadAsync());

        if (status == 204) Assert.IsType<InvalidOperationException>(error);
        else Assert.Equal((HttpStatusCode)status, Assert.IsType<HttpRequestException>(error).StatusCode);
        Assert.Equal(attempt, fixture.Http.Paths.Count);
        Assert.DoesNotContain(Fixture.Token, error.ToString());
    }

    /// <summary>Checks network failures propagate unchanged without retrying the file request.</summary>
    [Fact]
    public async Task NetworkFailurePropagates()
    {
        using var fixture = new Fixture(1);
        var failure = new HttpRequestException("Synthetic transport failure.");
        fixture.Http.OnSend = (_, _) => fixture.Http.Paths.Count == 2 ? throw failure : Task.FromResult(Json(fixture.Metadata));

        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ReadAsync()));
        Assert.Equal(2, fixture.Http.Paths.Count);
    }

    /// <summary>Checks both declared and unknown response lengths enforce the exact response-byte limit.</summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task EnforcesResponseByteBoundary(int delta, bool unknownLength)
    {
        using var fixture = new Fixture(0);
        var body = Pad(fixture.Metadata, GitHubRepository.MaxResponseBytes + delta);
        fixture.Http.OnSend = (_, _) => Task.FromResult(BodyResponse(body, unknownLength));

        if (delta > 0) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        else Assert.Empty((await fixture.ReadAsync()).ChangedFiles);
    }

    /// <summary>Checks combined metadata and file bodies enforce the aggregate byte limit exactly.</summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task EnforcesTotalResponseByteBoundary(int delta, bool unknownLength)
    {
        using var fixture = new Fixture(101);
        fixture.Http.OnSend = (message, _) =>
        {
            var attempt = fixture.Http.Paths.Count;
            var files = message.RequestUri!.AbsolutePath.EndsWith("/files");
            var page = files ? int.Parse(message.RequestUri.Query.Split("page=")[^1]) : 0;
            var body = files ? fixture.Page(page) : fixture.Metadata;
            var size = attempt < 4 ? GitHubRepository.MaxResponseBytes
                : GitHubRepository.MaxTotalResponseBytes - 3 * GitHubRepository.MaxResponseBytes + delta;
            return Task.FromResult(BodyResponse(Pad(body, size), unknownLength));
        };

        if (delta > 0) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        else Assert.Equal(101, (await fixture.ReadAsync()).ChangedFiles.Length);
        Assert.Equal(4, fixture.Http.Paths.Count);
    }

    /// <summary>Checks oversized declared content is rejected before reading its body.</summary>
    [Fact]
    public async Task OversizedContentLengthDoesNotReadBody()
    {
        using var fixture = new Fixture(0);
        var stream = new TestReadStream([], false) { BeforeRead = _ => throw new InvalidOperationException("Body must not be read.") };
        fixture.Http.OnSend = (_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentLength = GitHubRepository.MaxResponseBytes + 1L;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());

        Assert.Contains("retrieval limits", error.Message);
        Assert.Equal(0, stream.ReadCalls);
    }

    /// <summary>Checks pre-cancellation and cancellation during headers/body reads stop subsequent requests.</summary>
    [Theory]
    [InlineData("before")]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task CancellationStopsRetrieval(string stage)
    {
        using var fixture = new Fixture(1);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stage == "before") cancellation.Cancel();
        fixture.Http.OnSend = async (_, token) =>
        {
            if (stage == "headers")
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            var stream = new TestReadStream(Encoding.UTF8.GetBytes(fixture.Metadata), false)
            {
                BeforeRead = async readToken =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, readToken);
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        var reading = fixture.ReadAsync(cancellation.Token);
        if (stage != "before")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(stage == "before" ? 0 : 1, fixture.Http.Paths.Count);
    }

    /// <summary>Checks repository credentials are required without making an HTTP request.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task MissingTokenFailsBeforeHttp(string? token)
    {
        using var fixture = new Fixture(1);
        fixture.Configuration["GITHUB_TOKEN"] = token;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Empty(fixture.Http.Paths);
    }

    private static JsonObject FileNode(string path) => new()
    {
        ["filename"] = path, ["status"] = "modified", ["additions"] = 1, ["deletions"] = 1,
        ["patch"] = "@@ -1 +1 @@\n-old\n+new", ["raw_url"] = "https://untrusted.example/never-follow"
    };

    private static void Set(JsonNode node, string path, string? json)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1]) node = node[part]!;
        if (json is null) node.AsObject().Remove(parts[^1]);
        else node[parts[^1]] = JsonNode.Parse(json);
    }

    private static string Pad(string json, int byteCount) => json + new string(' ', byteCount - Encoding.UTF8.GetByteCount(json));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage BodyResponse(string body, bool unknownLength) => unknownLength
        ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TestReadStream(Encoding.UTF8.GetBytes(body), false))
        }
        : Json(body);

    private sealed class Fixture : IDisposable
    {
        internal const string Token = "synthetic-github-token";
        internal static string BaseSha { get; } = new('a', 40);
        internal static string HeadSha { get; } = new('b', 40);
        internal static ReviewRequest Request { get; } = new("company", "project", 42, "developer", 1001);
        internal ConfigurationManager Configuration { get; } = new();
        internal FakeHttpHandler Http { get; } = new();
        internal string Metadata { get; set; }
        internal string Files { get; set; }
        internal GitHubRepository Repository { get; }
        private readonly HttpClient client;
        private readonly int fileCount;

        internal Fixture(int count)
        {
            fileCount = count;
            Configuration["GITHUB_TOKEN"] = Token;
            Configuration["GITHUB_API_URL"] = "https://api.github.com";
            Metadata = JsonSerializer.Serialize(new
            {
                number = 42, state = "open", title = "Synthetic PR", body = "Untrusted description",
                @base = new { sha = BaseSha }, head = new { sha = HeadSha }, changed_files = count
            });
            Files = Page(1);
            Http.DefaultBody = message => message.RequestUri!.AbsolutePath.EndsWith("/files")
                ? (fileCount <= 100 ? Files : Page(int.Parse(message.RequestUri.Query.Split("page=")[^1]))) : Metadata;
            client = new HttpClient(Http);
            Repository = new GitHubRepository(client, Configuration);
        }

        internal string Page(int page) => new JsonArray(Enumerable.Range((page - 1) * 100, Math.Min(100, Math.Max(0, fileCount - (page - 1) * 100)))
            .Select(index => (JsonNode)FileNode($"file-{index}.cs")).ToArray()).ToJsonString();

        internal Task<PullRequestSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Repository.GetPullRequestAsync(Request, cancellationToken);

        /// <inheritdoc />
        public void Dispose()
        {
            client.Dispose();
            Configuration.Dispose();
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal Func<HttpRequestMessage, string> DefaultBody { get; set; } = _ => "{}";
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Fixture.Token, request.Headers.Authorization.Parameter);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return OnSend?.Invoke(request, cancellationToken) ?? Task.FromResult(Json(DefaultBody(request)));
        }
    }

    private sealed class TestReadStream(byte[] bytes, bool canSeek) : MemoryStream(bytes)
    {
        internal int ReadCalls { get; private set; }
        internal Func<CancellationToken, Task>? BeforeRead { get; init; }

        /// <inheritdoc />
        public override bool CanSeek => canSeek;

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (BeforeRead is not null) await BeforeRead(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
