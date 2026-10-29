using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ten28_PrReviewer.Demo;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewIdentityTests
{
    /// <summary>Checks canonical repository and policy sets have one stable key and a round-trippable marker.</summary>
    [Fact]
    public void CanonicalIdentityIsStable()
    {
        var snapshot = DemoReviewFixture.Snapshot;
        var policy = DemoReviewFixture.Policy;
        var identity = ReviewIdentity.Create(snapshot, policy, "model", "1.0");
        var normalized = ReviewIdentity.Create(snapshot with { Request = snapshot.Request with
            { RepositoryOwner = snapshot.Request.RepositoryOwner.ToUpperInvariant(), RepositoryName = snapshot.Request.RepositoryName.ToUpperInvariant() },
            HeadSha = snapshot.HeadSha.ToUpperInvariant() }, policy with
        {
            AllowedExtensions = policy.AllowedExtensions.Reverse().Select(value => value.ToUpperInvariant()).Concat(policy.AllowedExtensions).ToImmutableArray(),
            ExcludedGlobs = policy.ExcludedGlobs.Reverse().Select(value => value.ToUpperInvariant()).Concat(policy.ExcludedGlobs).ToImmutableArray()
        }, "model", "1.0");

        Assert.Equal(identity, normalized);
        Assert.Matches("^[0-9a-f]{64}$", identity.Key);
        Assert.True(ReviewIdentity.TryRead("Summary\n\n" + identity.Marker + "\nFallback findings", out var parsed));
        Assert.Equal(identity, parsed);
    }

    /// <summary>Checks each review identity input can permit a fresh review.</summary>
    /// <param name="change">Identity component that changes.</param>
    [Theory]
    [InlineData("repository")]
    [InlineData("pr")]
    [InlineData("head")]
    [InlineData("version")]
    [InlineData("instructions")]
    [InlineData("limits")]
    [InlineData("extensions")]
    [InlineData("exclusions")]
    [InlineData("model")]
    [InlineData("worker")]
    public void ChangedIdentityHasNewKey(string change)
    {
        var snapshot = DemoReviewFixture.Snapshot;
        var policy = DemoReviewFixture.Policy;
        var initial = ReviewIdentity.Create(snapshot, policy, "model", "1.0");
        snapshot = change switch
        {
            "repository" => snapshot with { Request = snapshot.Request with { RepositoryName = "other" } },
            "pr" => snapshot with { Request = snapshot.Request with { PullRequestNumber = 43 } },
            "head" => snapshot with { HeadSha = new string('c', 40) },
            _ => snapshot
        };
        policy = change switch
        {
            "version" => policy with { Version = "v2" },
            "instructions" => policy with { Instructions = "Different instructions" },
            "limits" => policy with { Limits = policy.Limits with { MaxFindings = 1 } },
            "extensions" => policy with { AllowedExtensions = [".cs"] },
            "exclusions" => policy with { ExcludedGlobs = policy.ExcludedGlobs.Add("**/extra/**") },
            _ => policy
        };
        Assert.NotEqual(initial.Key, ReviewIdentity.Create(snapshot, policy, change == "model" ? "other" : "model",
            change == "worker" ? "2.0" : "1.0").Key);
    }

    /// <summary>Checks marker parsing rejects alterations, duplicate markers, ambiguous fields, and unsupported versions.</summary>
    /// <param name="change">Malformed marker variation.</param>
    [Theory]
    [InlineData("legacy")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("base64")]
    [InlineData("json")]
    [InlineData("key")]
    [InlineData("field")]
    [InlineData("duplicate-field")]
    [InlineData("oversized")]
    public void RejectsMalformedMarkers(string change)
    {
        var identity = ReviewIdentity.Create(DemoReviewFixture.Snapshot, DemoReviewFixture.Policy, "model", "1.0");
        var marker = identity.Marker;
        var envelope = JsonSerializer.Serialize(new { key = identity.Key, identity });
        if (change == "legacy") marker = "No marker";
        if (change == "duplicate") marker += "\n" + marker;
        if (change == "version") marker = marker.Replace(":v1 ", ":v2 ");
        if (change == "base64") marker = "<!-- company-pr-reviewer:v1 ??? -->";
        if (change == "json") envelope = "{";
        if (change == "key") envelope = envelope.Replace(identity.Key, new string('0', 64));
        if (change == "field") envelope = JsonNode.Parse(envelope)!.AsObject().ToJsonString().Replace("\"identity\"", "\"other\"");
        if (change == "duplicate-field") envelope = envelope.Insert(1, "\"key\":\"duplicate\",");
        if (change is "json" or "key" or "field" or "duplicate-field")
            marker = "<!-- company-pr-reviewer:v1 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope)) + " -->";
        if (change == "oversized") marker = "<!-- company-pr-reviewer:v1 " + new string('A', 8192) + " -->";
        Assert.False(ReviewIdentity.TryRead(marker, out _));
    }
}
