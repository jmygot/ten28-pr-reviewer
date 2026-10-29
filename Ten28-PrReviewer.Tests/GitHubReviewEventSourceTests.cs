using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.GitHub;
using Ten28_PrReviewer.LLM;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class GitHubReviewEventSourceTests
{
    /// <summary>Checks the command, request identity, and authenticated read-only API contract.</summary>
    [Theory]
    [InlineData("@company-code-reviewer review", null, "https://api.github.com/repos/company/project/pulls/42")]
    [InlineData(" \t@company-code-reviewer review\r\n", "https://github.example/api/v3", "https://github.example/api/v3/repos/company/project/pulls/42")]
    public async Task ValidCommandReturnsVerifiedRequest(string body, string? apiUrl, string expectedUrl)
    {
        using var fixture = new EventFixture();
        fixture.Change("comment.body", JsonSerializer.Serialize(body));
        fixture.Settings["GITHUB_API_URL"] = apiUrl;
        fixture.Http.OnSend = fixture.Http.OnPermissionSend = (request, _) =>
        {
            var permissionCall = fixture.Http.Attempts == 2;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(permissionCall ? expectedUrl.Replace("pulls/42", "collaborators/developer/permission") : expectedUrl,
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(EventFixture.Token, request.Headers.Authorization.Parameter);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(permissionCall ? fixture.Http.PermissionBody : """{"state":"open","draft":true}""")
            });
        };

        var request = await fixture.ReadAsync();

        Assert.Equal(new ReviewRequest("company", "project", 42, "developer", 5000000001), request);
        Assert.Equal(2, fixture.Http.Attempts);
    }

    /// <summary>Checks malformed or nonmatching trigger fields stop before credentials or HTTP are needed.</summary>
    [Theory]
    [InlineData("action", null)]
    [InlineData("action", "\"edited\"")]
    [InlineData("action", "\"deleted\"")]
    [InlineData("action", "\"synchronize\"")]
    [InlineData("action", "\"Created\"")]
    [InlineData("issue", null)]
    [InlineData("issue.pull_request", null)]
    [InlineData("issue.pull_request", "null")]
    [InlineData("issue.pull_request", "[]")]
    [InlineData("issue.pull_request", "\"https://untrusted.example\"")]
    [InlineData("comment", null)]
    [InlineData("comment.body", null)]
    [InlineData("comment.body", "null")]
    [InlineData("comment.body", "42")]
    [InlineData("comment.body", "\"\"")]
    [InlineData("comment.body", "\"  \\n\"")]
    [InlineData("comment.body", "\"please @company-code-reviewer review\"")]
    [InlineData("comment.body", "\"@company-code-reviewer review please\"")]
    [InlineData("comment.body", "\"@company-code-reviewer Review\"")]
    [InlineData("comment.body", "\"@company-code-reviewer  review\"")]
    [InlineData("comment.body", "\"`@company-code-reviewer review`\"")]
    [InlineData("comment.body", "\"@company-code-reviewer review\\n@company-code-reviewer review\"")]
    [InlineData("comment.user", null)]
    [InlineData("comment.user", "null")]
    [InlineData("comment.user.type", null)]
    [InlineData("comment.user.type", "\"Bot\"")]
    [InlineData("comment.user.type", "\"Organization\"")]
    [InlineData("comment.user.type", "\"unknown\"")]
    [InlineData("comment.user.login", null)]
    [InlineData("comment.user.login", "\" \"")]
    [InlineData("repository", null)]
    [InlineData("repository.owner.login", null)]
    [InlineData("repository.owner.login", "\" \"")]
    [InlineData("repository.name", null)]
    [InlineData("repository.name", "\"\"")]
    [InlineData("issue.number", null)]
    [InlineData("issue.number", "0")]
    [InlineData("issue.number", "-1")]
    [InlineData("issue.number", "2147483648")]
    [InlineData("issue.number", "\"42\"")]
    [InlineData("comment.id", null)]
    [InlineData("comment.id", "0")]
    [InlineData("comment.id", "-1")]
    [InlineData("comment.id", "9223372036854775808")]
    [InlineData("comment.id", "\"123\"")]
    public async Task InvalidTriggerReturnsNullWithoutHttp(string path, string? value)
    {
        using var fixture = new EventFixture();
        fixture.Change(path, value);
        fixture.Settings["GITHUB_TOKEN"] = null;

        Assert.Null(await fixture.ReadAsync());
        Assert.Equal(0, fixture.Http.Attempts);
    }

    /// <summary>Checks only issue_comment events can read a payload or access the API.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Issue_Comment")]
    [InlineData("issue_comment.created")]
    [InlineData("pull_request")]
    [InlineData("pull_request_target")]
    [InlineData("push")]
    [InlineData("pull_request_review_comment")]
    [InlineData("pull_request_review")]
    [InlineData("issues")]
    [InlineData("workflow_dispatch")]
    [InlineData("workflow_call")]
    public async Task OtherEventsCannotReadOrAcceptValidLookingPayloads(string? eventName)
    {
        using var fixture = new EventFixture();
        fixture.Settings["GITHUB_EVENT_NAME"] = eventName;
        Assert.Null(await fixture.ReadAsync());

        fixture.Settings["GITHUB_EVENT_PATH"] = Path.Combine(fixture.DirectoryPath, "missing.json");
        Assert.Null(await fixture.ReadAsync());
        Assert.Equal(0, fixture.Http.Attempts);
    }

    /// <summary>Checks current API state overrides stale event state, including merged PRs.</summary>
    [Theory]
    [InlineData("open", "closed", true, false)]
    [InlineData("open", "closed", false, false)]
    [InlineData("closed", "open", false, true)]
    public async Task ApiStateDeterminesAcceptance(string payloadState, string apiState, bool merged, bool accepted)
    {
        using var fixture = new EventFixture();
        fixture.Change("issue.state", JsonSerializer.Serialize(payloadState));
        fixture.Http.Body = JsonSerializer.Serialize(new { state = apiState, merged });

        Assert.Equal(accepted, await fixture.ReadAsync() is not null);
        Assert.Equal(accepted ? 2 : 1, fixture.Http.Attempts);
    }

    /// <summary>Checks missing configuration cannot accept a qualifying request.</summary>
    [Theory]
    [InlineData("GITHUB_EVENT_PATH", null)]
    [InlineData("GITHUB_EVENT_PATH", " ")]
    [InlineData("GITHUB_TOKEN", null)]
    [InlineData("GITHUB_TOKEN", " ")]
    public async Task MissingRequiredConfigurationFails(string key, string? value)
    {
        using var fixture = new EventFixture();
        fixture.Settings[key] = value;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
        Assert.Equal(0, fixture.Http.Attempts);
    }

    /// <summary>Checks missing files and malformed JSON propagate without HTTP.</summary>
    [Fact]
    public async Task UnreadableAndMalformedPayloadsFail()
    {
        using var fixture = new EventFixture();
        fixture.Settings["GITHUB_EVENT_PATH"] = Path.Combine(fixture.DirectoryPath, "missing.json");
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.ReadAsync());

        fixture.Settings["GITHUB_EVENT_PATH"] = fixture.EventPath;
        await File.WriteAllTextAsync(fixture.EventPath, "{broken json");
        await Assert.ThrowsAnyAsync<JsonException>(() => fixture.ReadAsync());
        Assert.Equal(0, fixture.Http.Attempts);
    }

    /// <summary>Checks valid JSON with an invalid root is an inapplicable trigger.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task InvalidPayloadRootIsSkipped(string json)
    {
        using var fixture = new EventFixture();
        await File.WriteAllTextAsync(fixture.EventPath, json);

        Assert.Null(await fixture.ReadAsync());
        Assert.Equal(0, fixture.Http.Attempts);
    }

    /// <summary>Checks unsuccessful responses propagate instead of treating a PR as verified.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ApiFailuresPropagate(int status)
    {
        using var fixture = new EventFixture();
        fixture.Http.Status = (HttpStatusCode)status;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ReadAsync());

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, fixture.Http.Attempts);
    }

    /// <summary>Checks ambiguous successful responses and malformed API JSON cannot produce a request.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"state\":null}")]
    [InlineData("{\"state\":42}")]
    [InlineData("{\"state\":\"unknown\"}")]
    [InlineData("{\"state\":\"Open\"}")]
    public async Task InvalidApiStateFails(string json)
    {
        using var fixture = new EventFixture();
        fixture.Http.Body = json;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync());
    }

    /// <summary>Checks parsing and network errors propagate from the API boundary.</summary>
    [Fact]
    public async Task MalformedApiJsonAndNetworkFailuresPropagate()
    {
        using var fixture = new EventFixture();
        fixture.Http.Body = "{broken json";
        await Assert.ThrowsAnyAsync<JsonException>(() => fixture.ReadAsync());

        var failure = new HttpRequestException("Synthetic network failure.");
        fixture.Http.OnSend = (_, _) => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ReadAsync()));
    }

    /// <summary>Checks cancellation stops before work or reaches an in-progress HTTP request.</summary>
    [Fact]
    public async Task CancellationPreventsAcceptance()
    {
        using var fixture = new EventFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ReadAsync(cancelled.Token));
        Assert.Equal(0, fixture.Http.Attempts);

        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.OnSend = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancelled HTTP should not complete.");
        };
        var reading = fixture.ReadAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(1, fixture.Http.Attempts);
    }

    /// <summary>Checks normal startup skips closed PRs and authorized PRs with no eligible patches.</summary>
    [Theory]
    [InlineData("open", "Review Skipped")]
    [InlineData("closed", "Review Skipped")]
    public async Task NormalHostDetectsOrSkipsAndExits(string state, string message)
    {
        using var fixture = new EventFixture();
        fixture.Http.Body = JsonSerializer.Serialize(new { state });
        fixture.Settings["Review:MaxSinglePatchBytes"] = "1";
        using var host = fixture.CreateHost();

        Assert.Equal(0, await Program.RunAsync(host));
        Assert.NotNull(host.Services.GetService<ReviewOrchestrator>());
        Assert.IsType<DeepSeekProvider>(host.Services.GetRequiredService<ILLMProvider>());
        Assert.IsType<GitHubReviewPublisher>(host.Services.GetRequiredService<IReviewPublisher>());
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains(message, logs);
        if (state == "open")
        {
            Assert.Contains("SinglePatchLimit: 1", logs);
        }
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain("@company-code-reviewer review", logs);
        Assert.DoesNotContain("sensitive-title", logs);
        Assert.DoesNotContain("sensitive-description", logs);
        Assert.DoesNotContain("sensitive-patch", logs);
        Assert.Equal(state == "open" ? 6 : 1, fixture.Http.Attempts);
        if (state == "open")
        {
            Assert.Equal(new[]
            {
                "/repos/company/project/pulls/42", "/repos/company/project/collaborators/developer/permission",
                "/repos/company/project/pulls/42", "/repos/company/project/pulls/42/files", "/repos/company/project/pulls/42",
                "/repos/company/project/contents/.github/ai-review.json"
            }, fixture.Http.Paths);
            Assert.Contains(new string('b', 40), logs);
            Assert.Contains("1 changed; 0 selected; 1 skipped", logs);
        }
    }

    /// <summary>Checks event-free startup skips successfully without credentials, payloads, or HTTP.</summary>
    [Fact]
    public async Task NormalHostWithoutEventNeedsNoCredentials()
    {
        using var fixture = new EventFixture();
        fixture.Settings["GITHUB_EVENT_NAME"] = null;
        fixture.Settings["GITHUB_EVENT_PATH"] = null;
        fixture.Settings["GITHUB_TOKEN"] = null;
        using var host = fixture.CreateHost();

        Assert.Equal(0, await Program.RunAsync(host));
        Assert.Contains(fixture.Logs.Messages, message => message.Contains("Review Skipped"));
        Assert.Equal(0, fixture.Http.Attempts);
    }

    /// <summary>Checks detection failures produce operational failure without logging response or secret content.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalHostReturns1WhenDetectionFails(bool missingPath)
    {
        using var fixture = new EventFixture();
        if (missingPath)
        {
            fixture.Settings["GITHUB_EVENT_PATH"] = null;
        }
        fixture.Http.Status = HttpStatusCode.Forbidden;
        fixture.Http.Body = EventFixture.Token;
        using var host = fixture.CreateHost();

        Assert.Equal(1, await Program.RunAsync(host));
        Assert.DoesNotContain(EventFixture.Token, string.Join('\n', fixture.Logs.Messages));
        Assert.Equal(missingPath ? 0 : 1, fixture.Http.Attempts);
    }

    /// <summary>Checks host cancellation interrupts PR verification and preserves exit code 130.</summary>
    [Fact]
    public async Task NormalHostReturns130WhenDetectionIsCancelled()
    {
        using var fixture = new EventFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Http.OnSend = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancelled HTTP should not complete.");
        };
        using var host = fixture.CreateHost();

        Assert.Equal(130, await Program.RunAsync(host, cancellation.Token));
        Assert.Equal(1, fixture.Http.Attempts);
    }

    /// <summary>Checks effective base permissions authorize maintain and custom roles before generation.</summary>
    [Theory]
    [InlineData("write", "write")]
    [InlineData("admin", "admin")]
    [InlineData("write", "maintain")]
    [InlineData("write", "custom-writer")]
    [InlineData("admin", "custom-admin")]
    public async Task AuthorizedPermissionReachesLlmAfterLookup(string permission, string role)
    {
        using var fixture = new EventFixture();
        fixture.Http.PermissionBody = JsonSerializer.Serialize(new
        {
            permission, role_name = role, user = new { login = "DEVELOPER" }
        });
        var llm = new RecordingLlmProvider(() => Assert.Equal(2, fixture.Http.Attempts));
        var publisher = new DemoRecordingReviewPublisher();
        var orchestrator = new ReviewOrchestrator(fixture.Source, new DemoGitHubRepository(),
            new DemoReviewPolicyService(), llm, new ReviewFindingsValidator(), publisher, new DemoReviewHistory());

        var outcome = await orchestrator.RunAsync();

        Assert.Equal(ReviewStatus.Completed, outcome.Status);
        Assert.Equal(1, llm.Calls);
        Assert.Single(publisher.Publications);
        Assert.Equal(new[] { "/repos/company/project/pulls/42", "/repos/company/project/collaborators/developer/permission" },
            fixture.Http.Paths);
    }

    /// <summary>Checks elevated association, ownership, and role names cannot override denied base access.</summary>
    [Theory]
    [InlineData("read", "read", "MEMBER", "developer")]
    [InlineData("none", "none", "COLLABORATOR", "developer")]
    [InlineData("read", "custom-reader", "OWNER", "developer")]
    [InlineData("read", "admin", "OWNER", "developer")]
    [InlineData("none", "admin", "OWNER", "company")]
    public async Task InsufficientPermissionSkipsAllDownstreamWork(string permission, string role, string association, string login)
    {
        using var fixture = new EventFixture();
        fixture.Change("comment.author_association", JsonSerializer.Serialize(association));
        fixture.Change("comment.user.login", JsonSerializer.Serialize(login));
        fixture.Http.PermissionBody = JsonSerializer.Serialize(new { permission, role_name = role, user = new { login } });
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();

        var outcome = await fixture.RunPipelineAsync(pipeline);

        Assert.Equal(ReviewStatus.Skipped, outcome.Status);
        Assert.Null(outcome.Usage);
        Assert.Empty(pipeline.Calls);
        Assert.Equal(2, fixture.Http.Attempts);
    }

    /// <summary>Checks ambiguous responses fail closed without disclosing response contents.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{broken json")]
    [InlineData("{\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":null,\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":42,\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":true,\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":\"unknown\",\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":\"Write\",\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":\"maintain\",\"user\":{\"login\":\"developer\"}}")]
    [InlineData("{\"permission\":\"write\"}")]
    [InlineData("{\"permission\":\"write\",\"user\":null}")]
    [InlineData("{\"permission\":\"write\",\"user\":{}}")]
    [InlineData("{\"permission\":\"write\",\"user\":{\"login\":null}}")]
    [InlineData("{\"permission\":\"write\",\"user\":{\"login\":42}}")]
    [InlineData("{\"permission\":\"write\",\"user\":{\"login\":\" \"}}")]
    [InlineData("{\"permission\":\"write\",\"user\":{\"login\":\"synthetic-github-token\"}}")]
    public async Task InvalidPermissionResponseStopsAllDownstreamWork(string body)
    {
        using var fixture = new EventFixture();
        fixture.Http.PermissionBody = body;
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunPipelineAsync(pipeline));

        Assert.StartsWith("GitHub returned an invalid", error.Message);
        Assert.DoesNotContain(EventFixture.Token, error.ToString());
        Assert.Empty(pipeline.Calls);
        Assert.Equal(2, fixture.Http.Attempts);
    }

    /// <summary>Checks unsuccessful or unexpected HTTP statuses cannot authorize or generate a review.</summary>
    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task PermissionHttpFailureStopsAllDownstreamWork(int status)
    {
        using var fixture = new EventFixture();
        fixture.Http.PermissionStatus = (HttpStatusCode)status;
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();

        var error = await Record.ExceptionAsync(() => fixture.RunPipelineAsync(pipeline));

        if (status < 300)
            Assert.IsType<InvalidOperationException>(error);
        else
            Assert.Equal((HttpStatusCode)status, Assert.IsType<HttpRequestException>(error).StatusCode);
        Assert.Empty(pipeline.Calls);
        Assert.Equal(2, fixture.Http.Attempts);
    }

    /// <summary>Checks network errors and HTTP timeouts propagate without any downstream work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionTransportFailureStopsAllDownstreamWork(bool timeout)
    {
        using var fixture = new EventFixture();
        Exception failure = timeout
            ? new TaskCanceledException("Synthetic timeout.", new TimeoutException())
            : new HttpRequestException("Synthetic network failure.");
        fixture.Http.OnPermissionSend = (_, _) => throw failure;
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();

        var error = await Record.ExceptionAsync(() => fixture.RunPipelineAsync(pipeline));

        Assert.IsAssignableFrom<Exception>(error);
        if (timeout)
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        else
            Assert.Same(failure, error);
        Assert.Empty(pipeline.Calls);
        Assert.Equal(2, fixture.Http.Attempts);
    }

    /// <summary>Checks cancellation interrupts an in-progress permission lookup before downstream work.</summary>
    [Fact]
    public async Task PermissionCancellationStopsAllDownstreamWork()
    {
        using var fixture = new EventFixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.OnPermissionSend = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancelled HTTP should not complete.");
        };
        var pipeline = new ReviewOrchestratorTests.PipelineSpy();
        var running = fixture.RunPipelineAsync(pipeline, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(pipeline.Calls);
        Assert.Equal(2, fixture.Http.Attempts);
    }

    /// <summary>Checks each request observes current permissions after access is revoked.</summary>
    [Fact]
    public async Task PermissionIsNotCachedBetweenRequests()
    {
        using var fixture = new EventFixture();
        var source = fixture.Source;
        Assert.NotNull(await source.ReadAsync(CancellationToken.None));
        fixture.Http.PermissionBody = """{"permission":"none","user":{"login":"developer"}}""";

        Assert.Null(await source.ReadAsync(CancellationToken.None));
        Assert.Equal(4, fixture.Http.Attempts);
    }

    /// <summary>Checks host exit codes and sanitized logs for denied and unverifiable access.</summary>
    [Theory]
    [InlineData(200, "read", 0)]
    [InlineData(200, "synthetic-github-token", 1)]
    [InlineData(403, "write", 1)]
    public async Task HostHandlesPermissionDenialAndFailure(int status, string permission, int expectedExit)
    {
        using var fixture = new EventFixture();
        fixture.Http.PermissionStatus = (HttpStatusCode)status;
        fixture.Http.PermissionBody = JsonSerializer.Serialize(new { permission, user = new { login = "developer" } });
        using var host = fixture.CreateHost();

        Assert.Equal(expectedExit, await Program.RunAsync(host));
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains(expectedExit == 0 ? "Review Skipped" : "Application execution failed", logs);
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain("Manual review request detected", logs);
        Assert.DoesNotContain("Review input prepared", logs);
        Assert.Equal(2, fixture.Http.Attempts);
        Assert.DoesNotContain(fixture.Http.Paths, path => path.EndsWith("/files"));
    }

    /// <summary>Checks snapshot failures at any retrieval stage stop host execution without exposing source data.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task HostReportsSnapshotFailureWithoutSourceText(int attempt)
    {
        using var fixture = new EventFixture();
        fixture.Http.OnSnapshotSend = (request, _) => Task.FromResult(new HttpResponseMessage(
            fixture.Http.Attempts == attempt ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        {
            Content = new StringContent(fixture.Http.Attempts == attempt ? EventFixture.Token :
                request.RequestUri!.AbsolutePath.EndsWith("/files") ? fixture.Http.FilesBody : fixture.Http.SnapshotBody)
        });
        using var host = fixture.CreateHost();

        Assert.Equal(1, await Program.RunAsync(host));
        Assert.Equal(attempt, fixture.Http.Attempts);
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains("Application execution failed", logs);
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain("Review input prepared", logs);
        Assert.IsType<DeepSeekProvider>(host.Services.GetRequiredService<ILLMProvider>());
        Assert.IsType<GitHubReviewPublisher>(host.Services.GetRequiredService<IReviewPublisher>());
    }

    /// <summary>Checks host cancellation during snapshot retrieval preserves exit code 130.</summary>
    [Fact]
    public async Task HostReturns130WhenSnapshotRetrievalIsCancelled()
    {
        using var fixture = new EventFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Http.OnSnapshotSend = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancelled snapshot should not complete.");
        };
        using var host = fixture.CreateHost();

        Assert.Equal(130, await Program.RunAsync(host, cancellation.Token));
        Assert.Equal(3, fixture.Http.Attempts);
    }

    /// <summary>Checks invalid base configuration stops host processing without leaking repository settings.</summary>
    [Fact]
    public async Task HostFailsClosedOnInvalidPolicyConfiguration()
    {
        using var fixture = new EventFixture();
        var decoded = System.Text.Encoding.UTF8.GetBytes("{\"instructions\":\"" + EventFixture.Token + "\"}");
        fixture.Http.OnPolicySend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                type = "file", path = ".github/ai-review.json", encoding = "base64", size = decoded.Length,
                content = Convert.ToBase64String(decoded)
            }))
        });
        using var host = fixture.CreateHost();

        Assert.Equal(1, await Program.RunAsync(host));
        Assert.Equal(6, fixture.Http.Attempts);
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains("Application execution failed", logs);
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain("Review input prepared", logs);
        Assert.IsType<DeepSeekProvider>(host.Services.GetRequiredService<ILLMProvider>());
    }

    /// <summary>Checks startup reports excluded coverage when lower central budgets leave no eligible files.</summary>
    [Fact]
    public async Task HostReportsZeroSelectedFilesWithoutClaimingReview()
    {
        using var fixture = new EventFixture();
        fixture.Settings["Review:MaxSinglePatchBytes"] = "1";
        using var host = fixture.CreateHost();

        Assert.Equal(0, await Program.RunAsync(host));
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains("1 changed; 0 selected; 1 skipped", logs);
        Assert.Contains("SinglePatchLimit: 1", logs);
        Assert.DoesNotContain("sensitive-patch", logs);
        Assert.IsType<DeepSeekProvider>(host.Services.GetRequiredService<ILLMProvider>());
        Assert.IsType<GitHubReviewPublisher>(host.Services.GetRequiredService<IReviewPublisher>());
    }

    /// <summary>Checks normal startup authorizes, generates, validates, rechecks, and publishes through real GitHub services.</summary>
    /// <param name="failure">Optional publisher failure to verify sanitized host reporting.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("403")]
    [InlineData("network")]
    [InlineData("422")]
    [InlineData("primary-rate-limit")]
    [InlineData("secondary-rate-limit")]
    [InlineData("429")]
    public async Task NormalHostRunsCompleteMockedPipeline(string failure)
    {
        using var fixture = new EventFixture();
        var file = DemoReviewFixture.Snapshot.ChangedFiles[0];
        fixture.Http.FilesBody = JsonSerializer.Serialize(new[] { new
        { filename = file.Path, status = "modified", additions = file.Additions, deletions = file.Deletions, patch = file.Patch } });
        var provider = new RecordingLlmProvider(() => Assert.Equal(7, fixture.Http.Attempts));
        var posts = 0;
        fixture.Http.OnSnapshotSend = async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal(10, fixture.Http.Attempts);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var root = payload.RootElement;
                Assert.Equal("COMMENT", root.GetProperty("event").GetString());
                Assert.Equal(new string('b', 40), root.GetProperty("commit_id").GetString());
                var comment = Assert.Single(root.GetProperty("comments").EnumerateArray());
                Assert.Equal(file.Path, comment.GetProperty("path").GetString());
                Assert.Equal(3, comment.GetProperty("line").GetInt32());
                Assert.Equal("RIGHT", comment.GetProperty("side").GetString());
                if (failure == "network") throw new HttpRequestException(EventFixture.Token);
                if (failure == "none")
                    return GitHubReviewPublisherTests.Json(HttpStatusCode.OK,
                        JsonSerializer.Serialize(new { id = 123, state = "COMMENTED", commit_id = new string('b', 40) }));
                var status = failure switch
                {
                    "422" => HttpStatusCode.UnprocessableEntity,
                    "429" => HttpStatusCode.TooManyRequests,
                    _ => HttpStatusCode.Forbidden
                };
                var response = GitHubReviewPublisherTests.Json(status, JsonSerializer.Serialize(new
                {
                    message = failure == "secondary-rate-limit" ? "Secondary rate limit. " + EventFixture.Token : EventFixture.Token
                }));
                if (failure == "primary-rate-limit")
                {
                    response.Headers.Add("X-RateLimit-Remaining", "0");
                    response.Headers.Add("X-RateLimit-Reset", "1893456000");
                }
                if (failure == "secondary-rate-limit")
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return response;
            }
            Assert.True(fixture.Http.Attempts < 7 || provider.Calls == 1);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/files") ? fixture.Http.FilesBody : fixture.Http.SnapshotBody) };
        };
        using var host = fixture.CreateHost(builder => builder.Services.AddSingleton<ILLMProvider>(provider));

        Assert.Equal(failure == "none" ? 0 : 1, await Program.RunAsync(host));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, posts);
        Assert.Equal(10, fixture.Http.Attempts);
        Assert.Equal("/repos/company/project/collaborators/developer/permission", fixture.Http.Paths[1]);
        Assert.Equal("/repos/company/project/pulls/42", fixture.Http.Paths[^2]);
        Assert.Equal("/repos/company/project/pulls/42/reviews", fixture.Http.Paths[^1]);
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains(failure == "none" ? "Review Completed" : "Review publication failed", logs);
        if (failure == "network") Assert.Contains("delivery may have succeeded: True", logs);
        if (failure is "primary-rate-limit" or "secondary-rate-limit" or "429")
            Assert.Contains("rate limited; no automatic retry", logs);
        if (failure == "primary-rate-limit") Assert.Contains("2030", logs);
        if (failure == "422") Assert.Contains("GitHub rejected the review; no further publication attempt", logs);
        if (failure != "none") Assert.DoesNotContain("Review Completed", logs);
        Assert.DoesNotContain("Simulated offline", logs);
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain(file.Patch!, logs);
        Assert.DoesNotContain(DemoReviewFixture.Result.Summary, logs);
    }

    /// <summary>Checks PR changes or unverifiable metadata after generation prevent publication through normal startup.</summary>
    /// <param name="change">Metadata change observed immediately before publication.</param>
    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("closed")]
    [InlineData("missing-head")]
    public async Task NormalHostAbortsPublicationAfterGeneration(string change)
    {
        using var fixture = new EventFixture();
        var file = DemoReviewFixture.Snapshot.ChangedFiles[0];
        fixture.Http.FilesBody = JsonSerializer.Serialize(new[] { new
        { filename = file.Path, status = "modified", additions = file.Additions, deletions = file.Deletions, patch = file.Patch } });
        var provider = new RecordingLlmProvider(() => Assert.Equal(7, fixture.Http.Attempts));
        var posts = 0;
        fixture.Http.OnSnapshotSend = (request, _) =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            var body = request.RequestUri!.AbsolutePath.EndsWith("/files") ? fixture.Http.FilesBody : fixture.Http.SnapshotBody;
            if (provider.Calls > 0)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(9, fixture.Http.Attempts);
                var metadata = JsonNode.Parse(body)!;
                if (change is "head" or "base") metadata[change]!["sha"] = new string('c', 40);
                if (change == "closed") metadata["state"] = "closed";
                if (change == "missing-head") metadata.AsObject().Remove("head");
                body = metadata.ToJsonString();
            }
            return Task.FromResult(GitHubReviewPublisherTests.Json(HttpStatusCode.OK, body));
        };
        using var host = fixture.CreateHost(builder => builder.Services.AddSingleton<ILLMProvider>(provider));

        Assert.Equal(1, await Program.RunAsync(host));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, posts);
        Assert.Equal(9, fixture.Http.Attempts);
        Assert.Equal("/repos/company/project/pulls/42", fixture.Http.Paths[^1]);
        Assert.Equal(2, fixture.Http.Paths.Count(path => path == "/repos/company/project/pulls/42/reviews"));
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Contains("Review publication failed", logs);
        Assert.Contains(change == "missing-head" ? "invalid pre-publication metadata" : "publication aborted", logs);
        Assert.DoesNotContain("Review Completed", logs);
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain(file.Patch!, logs);
        Assert.DoesNotContain(DemoReviewFixture.Result.Summary, logs);
    }

    /// <summary>Checks a rejected inline review gets one summary-only attempt only after a fresh PR check.</summary>
    /// <param name="outcome">Success, PR change before fallback, or rejection of the fallback.</param>
    [Theory]
    [InlineData("success")]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("closed")]
    [InlineData("rejected")]
    public async Task NormalHostRechecksBeforeSummaryOnlyFallback(string outcome)
    {
        using var fixture = new EventFixture();
        var file = DemoReviewFixture.Snapshot.ChangedFiles[0];
        fixture.Http.FilesBody = JsonSerializer.Serialize(new[] { new
        { filename = file.Path, status = "modified", additions = file.Additions, deletions = file.Deletions, patch = file.Patch } });
        var provider = new RecordingLlmProvider(() => Assert.Equal(7, fixture.Http.Attempts));
        var posts = 0;
        string? initialSummary = null;
        fixture.Http.OnSnapshotSend = async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal(posts == 1 ? 10 : 12, fixture.Http.Attempts);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var root = payload.RootElement;
                Assert.Equal("COMMENT", root.GetProperty("event").GetString());
                Assert.Equal(new string('b', 40), root.GetProperty("commit_id").GetString());
                var summary = root.GetProperty("body").GetString()!;
                if (posts == 1)
                {
                    initialSummary = summary;
                    var comment = Assert.Single(root.GetProperty("comments").EnumerateArray());
                    Assert.Equal(file.Path, comment.GetProperty("path").GetString());
                    Assert.Equal(3, comment.GetProperty("line").GetInt32());
                    Assert.Equal("RIGHT", comment.GetProperty("side").GetString());
                    return GitHubReviewPublisherTests.Json(HttpStatusCode.UnprocessableEntity,
                        """{"message":"Validation Failed","errors":[{"resource":"PullRequestReviewComment","field":"line","code":"invalid"}]}""");
                }
                Assert.Equal(2, posts);
                Assert.False(root.TryGetProperty("comments", out _));
                Assert.NotNull(initialSummary);
                Assert.StartsWith(initialSummary, summary);
                Assert.Contains("Inline findings (summary-only fallback)", summary);
                Assert.Contains(ReviewSummaryFormatter.FormatFinding(DemoReviewFixture.Result.Findings[0]), summary);
                return outcome == "rejected"
                    ? GitHubReviewPublisherTests.Json(HttpStatusCode.UnprocessableEntity,
                        JsonSerializer.Serialize(new { message = EventFixture.Token }))
                    : GitHubReviewPublisherTests.Json(HttpStatusCode.Created,
                        JsonSerializer.Serialize(new { id = 123, state = "COMMENTED", commit_id = new string('b', 40) }));
            }
            var body = request.RequestUri!.AbsolutePath.EndsWith("/files") ? fixture.Http.FilesBody : fixture.Http.SnapshotBody;
            if (posts > 0)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(11, fixture.Http.Attempts);
                var metadata = JsonNode.Parse(body)!;
                if (outcome is "head" or "base") metadata[outcome]!["sha"] = new string('c', 40);
                if (outcome == "closed") metadata["state"] = "closed";
                body = metadata.ToJsonString();
            }
            return GitHubReviewPublisherTests.Json(HttpStatusCode.OK, body);
        };
        using var host = fixture.CreateHost(builder => builder.Services.AddSingleton<ILLMProvider>(provider));

        Assert.Equal(outcome == "success" ? 0 : 1, await Program.RunAsync(host));

        Assert.Equal(1, provider.Calls);
        var changed = outcome is "head" or "base" or "closed";
        Assert.Equal(changed ? 1 : 2, posts);
        Assert.Equal(changed ? 11 : 12, fixture.Http.Attempts);
        var expectedPaths = new[] { "/repos/company/project/pulls/42", "/repos/company/project/pulls/42/reviews",
            "/repos/company/project/pulls/42", "/repos/company/project/pulls/42/reviews" };
        Assert.Equal(expectedPaths.Take(changed ? 3 : 4), fixture.Http.Paths.Skip(8));
        var logs = string.Join('\n', fixture.Logs.Messages);
        Assert.Single(fixture.Logs.Messages, message => message.Contains("attempting one summary-only COMMENT review"));
        Assert.Contains(outcome == "success" ? "Review Completed" : "Review publication failed", logs);
        if (outcome != "success") Assert.DoesNotContain("Review Completed", logs);
        Assert.DoesNotContain(EventFixture.Token, logs);
        Assert.DoesNotContain(file.Patch!, logs);
        Assert.DoesNotContain(DemoReviewFixture.Result.Summary, logs);
    }

    private sealed class RecordingLlmProvider(Action onReview) : ILLMProvider
    {
        /// <inheritdoc />
        public string ModelId => "synthetic-model";

        internal int Calls { get; private set; }

        /// <inheritdoc />
        public Task<LlmReviewResult> ReviewAsync(PullRequestSnapshot snapshot, ReviewPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onReview();
            Calls++;
            return Task.FromResult(DemoReviewFixture.Result);
        }
    }

    private sealed class EventFixture : IDisposable
    {
        internal const string Token = "synthetic-github-token";
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Ten28-PrReviewer-tests-" + Guid.NewGuid().ToString("N"));
        internal string EventPath => Path.Combine(DirectoryPath, "event.json");
        internal ConfigurationManager Settings { get; } = new();
        internal FakeHttpHandler Http { get; } = new();
        internal CapturedLogs Logs { get; } = new();
        private readonly HttpClient client;

        internal EventFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(EventPath, """
                {
                  "action": "created",
                  "repository": {"owner": {"login": "company"}, "name": "project"},
                  "issue": {"number": 42, "state": "open", "pull_request": {"url": "https://untrusted.example/never-follow"}},
                  "comment": {"id": 5000000001, "body": "@company-code-reviewer review", "user": {"login": "developer", "type": "User"}}
                }
                """);
            Settings["GITHUB_EVENT_NAME"] = "issue_comment";
            Settings["GITHUB_EVENT_PATH"] = EventPath;
            Settings["GITHUB_TOKEN"] = Token;
            Settings["GITHUB_API_URL"] = null;
            client = new HttpClient(Http);
        }

        internal void Change(string path, string? json)
        {
            var root = JsonNode.Parse(File.ReadAllText(EventPath))!;
            var parts = path.Split('.');
            var parent = root;
            foreach (var part in parts[..^1])
            {
                parent = parent[part]!;
            }
            if (json is null)
            {
                parent.AsObject().Remove(parts[^1]);
            }
            else
            {
                parent[parts[^1]] = JsonNode.Parse(json);
            }
            File.WriteAllText(EventPath, root.ToJsonString());
        }

        internal IReviewEventSource Source => new GitHubReviewEventSource(client, Settings);

        internal Task<ReviewRequest?> ReadAsync(CancellationToken cancellationToken = default) =>
            Source.ReadAsync(cancellationToken);

        internal Task<ReviewOutcome> RunPipelineAsync(ReviewOrchestratorTests.PipelineSpy spy, CancellationToken cancellationToken = default) =>
            new ReviewOrchestrator(Source, spy, spy, spy, spy, spy, spy).RunAsync(cancellationToken);

        internal IHost CreateHost(Action<HostApplicationBuilder>? configure = null)
        {
            Http.SnapshotRequestsEnabled = true;
            var builder = Program.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(Settings.AsEnumerable());
            builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Http));
            builder.Services.AddHttpClient(nameof(IReviewPublisher)).ConfigurePrimaryHttpMessageHandler(() => Http);
            builder.Services.AddHttpClient(nameof(IReviewHistory)).ConfigurePrimaryHttpMessageHandler(() => Http);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Logs);
            configure?.Invoke(builder);
            return builder.Build();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            client.Dispose();
            Settings.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        internal int Attempts { get; private set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal string Body { get; set; } = """{"state":"open"}""";
        internal HttpStatusCode PermissionStatus { get; set; } = HttpStatusCode.OK;
        internal string PermissionBody { get; set; } = """{"permission":"write","user":{"login":"developer"}}""";
        internal List<string> Paths { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnPermissionSend { get; set; }
        internal bool SnapshotRequestsEnabled { get; set; }
        internal string SnapshotBody { get; } = JsonSerializer.Serialize(new
        {
            number = 42, state = "open", title = "sensitive-title", body = "sensitive-description",
            @base = new { sha = new string('a', 40) }, head = new { sha = new string('b', 40) }, changed_files = 1
        });
        internal string FilesBody { get; set; } = """[{"filename":"file.cs","status":"modified","additions":1,"deletions":1,"patch":"sensitive-patch"}]""";
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnSnapshotSend { get; set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnPolicySend { get; set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnHistorySend { get; set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (SnapshotRequestsEnabled && request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/reviews"))
                return OnHistorySend?.Invoke(request, cancellationToken)
                    ?? Task.FromResult(GitHubReviewPublisherTests.Json(HttpStatusCode.OK, "[]"));
            if (SnapshotRequestsEnabled && request.RequestUri.AbsolutePath.EndsWith("/contents/.github/ai-review.json"))
            {
                Assert.Equal("?ref=" + new string('a', 40), request.RequestUri.Query);
                return OnPolicySend?.Invoke(request, cancellationToken)
                    ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (SnapshotRequestsEnabled && Attempts > 2)
            {
                return OnSnapshotSend?.Invoke(request, cancellationToken)
                    ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/files") ? FilesBody : SnapshotBody)
                    });
            }
            var permission = request.RequestUri.AbsolutePath.EndsWith("/permission", StringComparison.Ordinal);
            return (permission ? OnPermissionSend : OnSend)?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new HttpResponseMessage(permission ? PermissionStatus : Status)
                {
                    Content = new StringContent(permission ? PermissionBody : Body)
                });
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);

        /// <inheritdoc />
        public void Dispose() { }

        private sealed class CapturedLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
