using System.Text.Json.Nodes;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewResponseReaderTests
{
    private static readonly ReviewUsage Usage = new("synthetic-provider", "synthetic-model", null, null);

    /// <summary>Checks supported severities and nullable locations map without claiming findings are validated.</summary>
    /// <param name="severity">Supported wire severity.</param>
    /// <param name="line">Structurally valid location, including an unanchored observation.</param>
    [Theory]
    [InlineData("critical", 3)]
    [InlineData("high", null)]
    [InlineData("medium", 1)]
    [InlineData("low", int.MaxValue)]
    public void ReadsUnvalidatedFindings(string severity, int? line)
    {
        var response = JsonNode.Parse(ReviewOutputContract.Example)!;
        var finding = response["findings"]![0]!;
        finding["severity"] = severity;
        finding["line"] = line;
        finding["path"] = "not-in-diff.cs";

        var result = ReviewResponseReader.Read(response.ToJsonString(), 1, Usage);

        var actual = Assert.Single(result.Findings);
        Assert.Equal(Enum.Parse<ReviewSeverity>(severity, true), actual.Severity);
        Assert.Equal(line, actual.Line);
        Assert.Equal("not-in-diff.cs", actual.Path);
        Assert.Equal(finding["title"]!.GetValue<string>(), actual.Title);
        Assert.Equal(finding["explanation"]!.GetValue<string>(), actual.Explanation);
        Assert.Equal(finding["recommendation"]!.GetValue<string>(), actual.Recommendation);
        Assert.Equal(response["summary"]!.GetValue<string>(), result.Summary);
        Assert.Same(Usage, result.Usage);
    }

    /// <summary>Checks summary-only JSON and exact findings ceilings are accepted.</summary>
    [Fact]
    public void ReadsEmptyFindingsAndExactCeiling()
    {
        var result = ReviewResponseReader.Read("{\"summary\":\"No actionable findings in the supplied diff.\",\"findings\":[]}", 1, Usage);
        Assert.Empty(result.Findings);
        Assert.Single(ReviewResponseReader.Read(ReviewOutputContract.Example, 1, Usage).Findings);
    }

    /// <summary>Provides malformed or ambiguous responses without external fixtures.</summary>
    /// <returns>Invalid response JSON for the one-finding policy.</returns>
    public static IEnumerable<object[]> InvalidResponses()
    {
        foreach (var json in new[]
        {
            "", "{", "[]", "null", "```json\n{}\n```", "{}",
            "{\"summary\":\"SECRET_SENTINEL\",\"summary\":\"duplicate\",\"findings\":[]}",
            "{\"summary\":\"SECRET_SENTINEL\",\"findings\":[],\"findings\":[]}"
        }) yield return [json];
        foreach (var field in new[] { "summary", "findings" })
        {
            var response = JsonNode.Parse(ReviewOutputContract.Example)!.AsObject();
            response.Remove(field);
            yield return [response.ToJsonString()];
        }
        foreach (var field in new[] { "severity", "title", "path", "line", "side", "explanation", "recommendation" })
        {
            var response = JsonNode.Parse(ReviewOutputContract.Example)!;
            response["findings"]![0]!.AsObject().Remove(field);
            yield return [response.ToJsonString()];
        }
        foreach (var field in new[] { "summary", "title", "path", "explanation", "recommendation" })
        foreach (var badValue in new[] { "null", "\"\"", "\"   \"", "17", "true", "{}", "[]" })
        {
            var response = JsonNode.Parse(ReviewOutputContract.Example)!;
            var target = field == "summary" ? response : response["findings"]![0]!;
            target[field] = JsonNode.Parse(badValue);
            yield return [response.ToJsonString()];
        }
        foreach (var fieldAndValue in new[]
        {
            ("severity", "\"High\""), ("severity", "\"approved\""), ("severity", "0"),
            ("side", "\"LEFT\""), ("side", "\"right\""), ("side", "null"),
            ("line", "0"), ("line", "-1"), ("line", "1.5"), ("line", "2147483648"), ("line", "\"3\""), ("line", "true")
        })
        {
            var response = JsonNode.Parse(ReviewOutputContract.Example)!;
            response["findings"]![0]![fieldAndValue.Item1] = JsonNode.Parse(fieldAndValue.Item2);
            yield return [response.ToJsonString()];
        }
        foreach (var extraAtRoot in new[] { true, false })
        {
            var response = JsonNode.Parse(ReviewOutputContract.Example)!;
            (extraAtRoot ? response : response["findings"]![0]!)["instructions"] = "SECRET_SENTINEL";
            yield return [response.ToJsonString()];
        }
        foreach (var badFindings in new[] { "null", "{}", "\"SECRET_SENTINEL\"", "[null]", "[17]" })
        {
            var response = JsonNode.Parse(ReviewOutputContract.Example)!;
            response["findings"] = JsonNode.Parse(badFindings);
            yield return [response.ToJsonString()];
        }
        var tooMany = JsonNode.Parse(ReviewOutputContract.Example)!;
        tooMany["findings"]!.AsArray().Add(tooMany["findings"]![0]!.DeepClone());
        yield return [tooMany.ToJsonString()];
        yield return [ReviewOutputContract.Example.Replace("\"severity\": \"high\"", "\"severity\": \"high\", \"severity\": \"low\"")];
    }

    /// <summary>Checks malformed schema values fail with a fixed error that cannot expose model text.</summary>
    /// <param name="json">Invalid model-authored JSON.</param>
    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public void RejectsInvalidContractWithoutLeakingContent(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ReviewResponseReader.Read(json, 1, Usage));
        Assert.Equal("Invalid structured review response.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_SENTINEL", error.ToString());
    }
}
