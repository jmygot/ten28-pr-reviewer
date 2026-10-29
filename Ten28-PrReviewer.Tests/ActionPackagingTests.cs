using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Ten28_PrReviewer.Tests;

public class ActionPackagingTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, ".."));
    private const string CallerPath = ".github/workflows/ai-review.yml.example";
    private const string RepositoryPattern = @"[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+";
    private const string ReleasePattern = @"(?:v[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?|[0-9a-fA-F]{40})";

    /// <summary>Checks automatic CI only builds, tests, and simulates reviews with read-only access and no secret handoff.</summary>
    [Fact]
    public void CiWorkflowHasCredentialFreeVerificationBoundary()
    {
        var workflow = Read(".github/workflows/ci.yml");
        Assert.Equal(new[] { "pull_request", "push" }, Keys(Map(workflow, "on")));
        var permissions = Map(workflow, "permissions");
        Assert.Equal(new[] { "contents" }, Keys(permissions));
        Assert.Equal("read", Text(permissions, "contents"));
        var jobs = Map(workflow, "jobs");
        var job = Assert.IsType<YamlMappingNode>(Assert.Single(jobs.Children).Value);
        Assert.Equal(new[] { "runs-on", "steps", "timeout-minutes" }, Keys(job));
        Assert.Equal("ubuntu-latest", Text(job, "runs-on"));
        Assert.Equal("15", Text(job, "timeout-minutes"));
        var steps = Sequence(job, "steps");
        Assert.Equal(5, steps.Children.Count);
        Assert.Equal("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", Text(steps[0], "uses"));
        Assert.Equal(new[] { "persist-credentials" }, Keys(Map(steps[0], "with")));
        Assert.Equal("false", Text(Map(steps[0], "with"), "persist-credentials"));
        Assert.Equal("actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68", Text(steps[1], "uses"));
        Assert.Equal("10.0.x", Text(Map(steps[1], "with"), "dotnet-version"));
        Assert.Equal("dotnet build Ten28-PrReviewer.slnx --configuration Release --artifacts-path \"$RUNNER_TEMP/ten28-verification\"", Text(steps[2], "run"));
        Assert.Equal("dotnet test Ten28-PrReviewer.slnx --configuration Release --artifacts-path \"$RUNNER_TEMP/ten28-verification\" --no-build --no-restore", Text(steps[3], "run"));
        Assert.Equal("dotnet \"$RUNNER_TEMP/ten28-verification/bin/Ten28-PrReviewer/release/Ten28-PrReviewer.dll\" --demo", Text(steps[4], "run"));
        foreach (var step in steps.Children.Skip(2))
        {
            Assert.Equal(new[] { "name", "run", "shell" }, Keys(Assert.IsType<YamlMappingNode>(step)));
            Assert.Equal("bash", Text(step, "shell"));
        }
        var source = File.ReadAllText(Path.Combine(Root, ".github/workflows/ci.yml"));
        foreach (var forbidden in new[] { "secrets.", "GITHUB_TOKEN", "DEEPSEEK_API_KEY", "pull_request_target", "write", "workflow_call", "review.yml" })
            Assert.DoesNotContain(forbidden, source);
    }

    /// <summary>Checks the action installs stable .NET 10 and builds only trusted source before exposing runtime credentials.</summary>
    [Fact]
    public void CompositeActionHasTrustedBuildAndRuntimeBoundaries()
    {
        var action = Read("action.yml");
        var inputs = Map(action, "inputs");
        Assert.Equal(new[] { "deepseek-api-key", "github-token" }, Keys(inputs));
        foreach (var input in inputs.Children.Values)
        {
            Assert.Equal("true", Text(input, "required"));
            Assert.False(string.IsNullOrWhiteSpace(Text(input, "description")));
        }
        var runs = Map(action, "runs");
        Assert.Equal("composite", Text(runs, "using"));
        var steps = Sequence(runs, "steps");
        Assert.Equal(3, steps.Children.Count);
        Assert.Equal("actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68", Text(steps[0], "uses"));
        Assert.Equal("10.0.x", Text(Map(steps[0], "with"), "dotnet-version"));
        Assert.Equal("publish", Text(steps[1], "id"));
        Assert.Equal(new[] { "REVIEWER_ACTION_PATH", "REVIEWER_RELEASE_REF" }, Keys(Map(steps[1], "env")));
        Assert.Equal("${{ github.action_path }}", Text(Map(steps[1], "env"), "REVIEWER_ACTION_PATH"));
        Assert.Equal("${{ github.action_ref }}", Text(Map(steps[1], "env"), "REVIEWER_RELEASE_REF"));
        var runtime = Map(steps[2], "env");
        Assert.Equal(new[] { "DEEPSEEK_API_KEY", "GITHUB_TOKEN", "REVIEWER_WORKER_PATH" }, Keys(runtime));
        Assert.Equal("${{ inputs.github-token }}", Text(runtime, "GITHUB_TOKEN"));
        Assert.Equal("${{ inputs.deepseek-api-key }}", Text(runtime, "DEEPSEEK_API_KEY"));
        Assert.Equal("${{ steps.publish.outputs.worker-path }}", Text(runtime, "REVIEWER_WORKER_PATH"));
        foreach (var step in steps.Children.Skip(1))
        {
            Assert.Equal("pwsh", Text(step, "shell"));
            Assert.Equal("${{ github.action_path }}", Text(step, "working-directory"));
            Assert.DoesNotContain("${{", Text(step, "run"));
        }
        using var sdk = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "global.json")));
        Assert.Equal("10.0.100", sdk.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        Assert.Equal("latestFeature", sdk.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString());
        Assert.False(sdk.RootElement.GetProperty("sdk").GetProperty("allowPrerelease").GetBoolean());
    }

    /// <summary>Checks reusable workflow permissions, trigger, secret contract, and pinned central action reference.</summary>
    [Fact]
    public void ReusableWorkflowHasExplicitMinimalContract()
    {
        var workflow = Read(".github/workflows/review.yml");
        Assert.Equal(new[] { "workflow_call" }, Keys(Map(workflow, "on")));
        Assert.Empty(Map(workflow, "permissions").Children);
        var secrets = Map(Map(Map(workflow, "on"), "workflow_call"), "secrets");
        Assert.Equal(new[] { "deepseek_api_key" }, Keys(secrets));
        Assert.Equal("true", Text(Map(secrets, "deepseek_api_key"), "required"));
        var jobs = Map(workflow, "jobs");
        Assert.Equal(new[] { "review" }, Keys(jobs));
        var job = Map(jobs, "review");
        Assert.Equal("ubuntu-latest", Text(job, "runs-on"));
        Assert.Equal("10", Text(job, "timeout-minutes"));
        AssertReviewPermissions(job);
        var step = Assert.Single(Sequence(job, "steps").Children);
        Assert.Matches(@"\A" + RepositoryPattern + "@" + ReleasePattern + @"\z", Text(step, "uses"));
        var arguments = Map(step, "with");
        Assert.Equal(new[] { "deepseek-api-key", "github-token" }, Keys(arguments));
        Assert.Equal("${{ github.token }}", Text(arguments, "github-token"));
        Assert.Equal("${{ secrets.deepseek_api_key }}", Text(arguments, "deepseek-api-key"));
        Assert.DoesNotContain("concurrency", File.ReadAllText(Path.Combine(Root, ".github/workflows/review.yml")));
    }

    /// <summary>Checks only accepted created PR comments enter the caller-only concurrency group and receive the named secret.</summary>
    [Fact]
    public void CallerTemplateOwnsGuardAndConcurrency()
    {
        var workflow = Read(CallerPath);
        Assert.Equal(new[] { "issue_comment" }, Keys(Map(workflow, "on")));
        Assert.Equal("created", Assert.IsType<YamlScalarNode>(Assert.Single(Sequence(Map(Map(workflow, "on"), "issue_comment"), "types").Children)).Value);
        Assert.Empty(Map(workflow, "permissions").Children);
        Assert.False(File.Exists(Path.Combine(Root, ".github/workflows/ai-review.yml")));
        var jobs = Map(workflow, "jobs");
        Assert.Equal(new[] { "check-command", "review" }, Keys(jobs));
        var guard = Map(jobs, "check-command");
        Assert.Empty(Map(guard, "permissions").Children);
        Assert.Equal("${{ steps.command.outputs.accepted }}", Text(Map(guard, "outputs"), "accepted"));
        Assert.DoesNotContain(new YamlScalarNode("secrets"), guard.Children.Keys);
        var step = Assert.Single(Sequence(guard, "steps").Children);
        Assert.Equal("command", Text(step, "id"));
        Assert.Equal("pwsh", Text(step, "shell"));
        Assert.DoesNotContain("${{", Text(step, "run"));
        var review = Map(jobs, "review");
        Assert.Equal("check-command", Text(review, "needs"));
        Assert.Equal("needs.check-command.outputs.accepted == 'true'", Text(review, "if"));
        AssertReviewPermissions(review);
        var concurrency = Map(review, "concurrency");
        Assert.Equal("ai-review-${{ github.repository }}-${{ github.event.issue.number }}", Text(concurrency, "group"));
        Assert.Equal("false", Text(concurrency, "cancel-in-progress"));
        Assert.DoesNotContain(new YamlScalarNode("concurrency"), workflow.Children.Keys);
        var callerReference = Text(review, "uses");
        Assert.Matches(@"\A" + RepositoryPattern + @"/\.github/workflows/review\.yml@" + ReleasePattern + @"\z", callerReference);
        var centralJob = Map(Map(Read(".github/workflows/review.yml"), "jobs"), "review");
        var centralReference = Text(Assert.Single(Sequence(centralJob, "steps").Children), "uses");
        Assert.Equal(centralReference.Split('@')[0], callerReference[..callerReference.IndexOf("/.github/workflows/", StringComparison.Ordinal)]);
        Assert.Equal(new[] { "deepseek_api_key" }, Keys(Map(review, "secrets")));
        Assert.Equal("${{ secrets.DEEPSEEK_API_KEY }}", Text(Map(review, "secrets"), "deepseek_api_key"));
        foreach (var file in new[] { "action.yml", ".github/workflows/review.yml", CallerPath })
        {
            var source = File.ReadAllText(Path.Combine(Root, file));
            Assert.DoesNotContain("actions/checkout", source);
            Assert.DoesNotContain("secrets: inherit", source);
        }
    }

    /// <summary>Executes the shipped command guard with synthetic valid, invalid, and adversarial payloads.</summary>
    /// <param name="kind">Synthetic event variation.</param>
    /// <param name="accepted">Whether the central review job should become eligible.</param>
    [Theory]
    [InlineData("valid", true)]
    [InlineData("whitespace", true)]
    [InlineData("unicode-whitespace", true)]
    [InlineData("wrong-case", false)]
    [InlineData("extra-text", false)]
    [InlineData("empty", false)]
    [InlineData("missing", false)]
    [InlineData("non-string", false)]
    [InlineData("bot", false)]
    [InlineData("ordinary-issue", false)]
    [InlineData("edited", false)]
    [InlineData("other-event", false)]
    [InlineData("inline-comment", false)]
    [InlineData("hostile", false)]
    [InlineData("hostile-action", false)]
    public async Task GuardMatchesWorkerCommandSemantics(string kind, bool accepted)
    {
        using var files = new TemporaryFiles();
        var marker = Path.Combine(files.Path, "must-not-execute.txt");
        var payload = JsonNode.Parse("""{"action":"created","issue":{"pull_request":{}},"comment":{"body":"@company-code-reviewer review","user":{"type":"User"}}}""")!;
        if (kind == "whitespace") payload["comment"]!["body"] = " \t@company-code-reviewer review\r\n";
        if (kind == "unicode-whitespace") payload["comment"]!["body"] = "\u00a0@company-code-reviewer review\u2003";
        if (kind == "wrong-case") payload["comment"]!["body"] = "@Company-code-reviewer review";
        if (kind == "extra-text") payload["comment"]!["body"] = "@company-code-reviewer review please";
        if (kind == "empty") payload["comment"]!["body"] = "";
        if (kind == "missing") payload["comment"]!.AsObject().Remove("body");
        if (kind == "non-string") payload["comment"]!["body"] = new JsonArray("@company-code-reviewer review");
        if (kind == "bot") payload["comment"]!["user"]!["type"] = "Bot";
        if (kind == "ordinary-issue") payload["issue"]!.AsObject().Remove("pull_request");
        if (kind == "edited") payload["action"] = "edited";
        if (kind == "hostile") payload["comment"]!["body"] = "@company-code-reviewer review\n$(New-Item -Path '" + marker + "')";
        if (kind == "hostile-action") payload["action"] = "created'; New-Item '" + marker + "'; '";
        var eventPath = Path.Combine(files.Path, "event.json");
        await File.WriteAllTextAsync(eventPath, payload.ToJsonString());
        var outputPath = Path.Combine(files.Path, "outputs.txt");
        var guard = Assert.Single(Sequence(Map(Map(Read(CallerPath), "jobs"), "check-command"), "steps").Children);
        var result = await RunScriptAsync(Text(guard, "run"), files.Path, new()
        {
            ["GITHUB_EVENT_PATH"] = eventPath, ["GITHUB_OUTPUT"] = outputPath,
            ["GITHUB_EVENT_NAME"] = kind switch { "other-event" => "push", "inline-comment" => "pull_request_review_comment", _ => "issue_comment" }
        });
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("accepted=" + accepted.ToString().ToLowerInvariant() + "\n", await File.ReadAllTextAsync(outputPath));
        Assert.False(File.Exists(marker));
        Assert.DoesNotContain("@company-code-reviewer", result.Output);
    }

    /// <summary>Executes the actual publish/run scripts through a temporary fake dotnet, checking arguments and native exits.</summary>
    /// <param name="publishExit">Simulated publish exit code.</param>
    /// <param name="workerExit">Simulated worker exit code.</param>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 130)]
    public async Task ActionScriptsUseTrustedPathsAndPreserveExits(int publishExit, int workerExit)
    {
        using var files = new TemporaryFiles();
        var actionPath = Path.Combine(files.Path, "central action with spaces");
        Directory.CreateDirectory(actionPath);
        var shimPath = Path.Combine(files.Path, "fake commands");
        Directory.CreateDirectory(shimPath);
        var trace = Path.Combine(files.Path, "trace.json");
        var shimScript = Path.Combine(shimPath, "fake-dotnet.ps1");
        await File.WriteAllTextAsync(shimScript, """
            $record = @{ Arguments = @([IO.File]::ReadAllLines($env:FAKE_ARGUMENTS_PATH)); Directory = (Get-Location).Path; HasToken = -not [string]::IsNullOrEmpty($env:GITHUB_TOKEN); HasKey = -not [string]::IsNullOrEmpty($env:DEEPSEEK_API_KEY); Event = $env:GITHUB_EVENT_PATH; Summary = $env:GITHUB_STEP_SUMMARY }
            [IO.File]::WriteAllText($env:FAKE_TRACE, ($record | ConvertTo-Json -Depth 5))
            exit ([int]$env:FAKE_EXIT_CODE)
            """);
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(Path.Combine(shimPath, "dotnet.cmd"), """
                @echo off
                set "FAKE_SHIM_SCRIPT=%~dp0fake-dotnet.ps1"
                :capture
                if "%~1"=="" goto run
                >>"%FAKE_ARGUMENTS_PATH%" echo(%~1
                shift
                goto capture
                :run
                powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%FAKE_SHIM_SCRIPT%"
                exit /b %errorlevel%
                """.ReplaceLineEndings("\r\n") + "\r\n");
        else
        {
            var shim = Path.Combine(shimPath, "dotnet");
            await File.WriteAllTextAsync(shim, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$FAKE_ARGUMENTS_PATH\"\nexec pwsh -NoProfile -NonInteractive -File \"$(dirname \"$0\")/fake-dotnet.ps1\"\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = shimPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_TRACE"] = trace, ["FAKE_EXIT_CODE"] = publishExit.ToString(),
            ["FAKE_ARGUMENTS_PATH"] = Path.Combine(files.Path, "publish-arguments.txt"),
            ["REVIEWER_ACTION_PATH"] = actionPath, ["REVIEWER_RELEASE_REF"] = "v0.1.0",
            ["RUNNER_TEMP"] = files.Path, ["GITHUB_OUTPUT"] = Path.Combine(files.Path, "outputs.txt"),
            ["GITHUB_EVENT_PATH"] = Path.Combine(files.Path, "original-event.json"),
            ["GITHUB_STEP_SUMMARY"] = Path.Combine(files.Path, "original-summary.md")
        };
        var steps = Sequence(Map(Read("action.yml"), "runs"), "steps");
        var published = await RunScriptAsync(Text(steps[1], "run"), actionPath, environment);
        Assert.True(published.ExitCode == publishExit, published.Output);
        using (var record = JsonDocument.Parse(await File.ReadAllTextAsync(trace)))
        {
            var arguments = record.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.Equal("publish", arguments[0]);
            Assert.Equal(Path.Combine(actionPath, "Ten28-PrReviewer", "Ten28-PrReviewer.csproj"), Path.GetFullPath(arguments[1]));
            // Windows batch parameter shifting splits '=' inside MSBuild properties.
            Assert.Contains("-p:InformationalVersion=v0.1.0", string.Join("=", arguments));
            Assert.Contains("-p:IncludeSourceRevisionInInformationalVersion=false", string.Join("=", arguments));
            Assert.Contains("Release", arguments);
            var output = arguments[Array.IndexOf(arguments, "--output") + 1];
            Assert.StartsWith(files.Path + Path.DirectorySeparatorChar + "company-pr-reviewer-", output);
            Assert.Contains("--artifacts-path", arguments);
            Assert.Equal(actionPath, record.RootElement.GetProperty("Directory").GetString());
            Assert.False(record.RootElement.GetProperty("HasToken").GetBoolean());
            Assert.False(record.RootElement.GetProperty("HasKey").GetBoolean());
        }
        if (publishExit != 0) { Assert.False(File.Exists(environment["GITHUB_OUTPUT"])); return; }
        var outputLine = await File.ReadAllTextAsync(environment["GITHUB_OUTPUT"]!);
        var worker = outputLine["worker-path=".Length..].TrimEnd('\n');
        environment["REVIEWER_WORKER_PATH"] = worker;
        environment["GITHUB_TOKEN"] = "synthetic-action-token";
        environment["DEEPSEEK_API_KEY"] = "synthetic-action-key";
        environment["FAKE_EXIT_CODE"] = workerExit.ToString();
        environment["FAKE_ARGUMENTS_PATH"] = Path.Combine(files.Path, "runtime-arguments.txt");
        var executed = await RunScriptAsync(Text(steps[2], "run"), actionPath, environment);
        Assert.True(executed.ExitCode == workerExit, executed.Output);
        using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(trace));
        Assert.Equal(worker, Assert.Single(runtime.RootElement.GetProperty("Arguments").EnumerateArray()).GetString());
        Assert.EndsWith("Ten28-PrReviewer.dll", worker);
        Assert.True(runtime.RootElement.GetProperty("HasToken").GetBoolean());
        Assert.True(runtime.RootElement.GetProperty("HasKey").GetBoolean());
        Assert.Equal(environment["GITHUB_EVENT_PATH"], runtime.RootElement.GetProperty("Event").GetString());
        Assert.Equal(environment["GITHUB_STEP_SUMMARY"], runtime.RootElement.GetProperty("Summary").GetString());
        Assert.DoesNotContain("synthetic-action", executed.Output);
    }

    /// <summary>Checks a missing remote action ref stops publishing before invoking dotnet or creating outputs.</summary>
    [Fact]
    public async Task MissingReleaseIdentityFailsBeforePublish()
    {
        using var files = new TemporaryFiles();
        var steps = Sequence(Map(Read("action.yml"), "runs"), "steps");
        var output = Path.Combine(files.Path, "outputs.txt");
        var result = await RunScriptAsync(Text(steps[1], "run"), files.Path,
            new() { ["REVIEWER_RELEASE_REF"] = "", ["GITHUB_OUTPUT"] = output });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requires a release identity", result.Output);
        Assert.False(File.Exists(output));
    }

    /// <summary>Publishes the actual trusted project, checks its release identity, and starts the packaged DLL without credentials or an event.</summary>
    [Fact]
    public async Task PublishedWorkerRunsWithoutCallerCheckoutOrCredentials()
    {
        using var files = new TemporaryFiles();
        var steps = Sequence(Map(Read("action.yml"), "runs"), "steps");
        var outputPath = Path.Combine(files.Path, "publish-outputs.txt");
        var environment = new Dictionary<string, string?>
        {
            ["REVIEWER_ACTION_PATH"] = Root, ["REVIEWER_RELEASE_REF"] = "v0.1.0-packaging-test",
            ["RUNNER_TEMP"] = files.Path, ["GITHUB_OUTPUT"] = outputPath, ["GITHUB_EVENT_NAME"] = "",
            ["NuGetAudit"] = "false", ["RestoreIgnoreFailedSources"] = "true"
        };
        var publish = await RunScriptAsync(Text(steps[1], "run"), Root, environment, files.Path);
        Assert.True(publish.ExitCode == 0, publish.Output);
        var output = await File.ReadAllTextAsync(outputPath);
        var worker = output["worker-path=".Length..].TrimEnd('\n');
        Assert.True(File.Exists(worker));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(worker)!, "appsettings.json")));
        var context = new AssemblyLoadContext("published-worker-check", isCollectible: true);
        try
        {
            using var stream = File.OpenRead(worker);
            var assembly = context.LoadFromStream(stream);
            Assert.Equal("v0.1.0-packaging-test", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        }
        finally { context.Unload(); }
        environment["REVIEWER_WORKER_PATH"] = worker;
        environment["GITHUB_STEP_SUMMARY"] = Path.Combine(files.Path, "summary.md");
        var executed = await RunScriptAsync(Text(steps[2], "run"), Root, environment, files.Path);
        Assert.True(executed.ExitCode == 0, executed.Output);
        Assert.Contains("Review Skipped", executed.Output);
        Assert.DoesNotContain("Simulated offline", executed.Output);
        Assert.Contains("no\\_applicable\\_request", await File.ReadAllTextAsync(environment["GITHUB_STEP_SUMMARY"]!));
    }

    private static void AssertReviewPermissions(YamlNode job)
    {
        var permissions = Map(job, "permissions");
        Assert.Equal(new[] { "contents", "pull-requests" }, Keys(permissions));
        Assert.Equal("read", Text(permissions, "contents"));
        Assert.Equal("write", Text(permissions, "pull-requests"));
    }

    private static YamlMappingNode Read(string path)
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(Root, path));
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlNode Get(YamlNode node, string key) => Assert.IsType<YamlMappingNode>(node).Children[new YamlScalarNode(key)];
    private static YamlMappingNode Map(YamlNode node, string key) => Assert.IsType<YamlMappingNode>(Get(node, key));
    private static YamlSequenceNode Sequence(YamlNode node, string key) => Assert.IsType<YamlSequenceNode>(Get(node, key));
    private static string Text(YamlNode node, string key) => Assert.IsType<YamlScalarNode>(Get(node, key)).Value!;
    private static string[] Keys(YamlMappingNode node) => node.Children.Keys.Select(key => Assert.IsType<YamlScalarNode>(key).Value!).Order(StringComparer.Ordinal).ToArray();
    private static string SourcePath([CallerFilePath] string path = "") => path;

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(string script, string directory,
        Dictionary<string, string?> environment, string? scriptDirectory = null)
    {
        var path = Path.Combine(scriptDirectory ?? directory, "script-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "pwsh")
        { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "GITHUB_TOKEN", "DEEPSEEK_API_KEY", "REVIEWER_RELEASE_REF", "GITHUB_STEP_SUMMARY" }) start.Environment.Remove(name);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class TemporaryFiles : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ten28 packaging tests " + Guid.NewGuid().ToString("N"));
        internal TemporaryFiles() => Directory.CreateDirectory(Path);
        /// <inheritdoc />
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected temporary path.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
