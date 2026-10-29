using System.Text.Json;
using Ten28_PrReviewer.Reviews;

namespace Ten28_PrReviewer.Tests;

public class ReviewOutputContractTests
{
    /// <summary>Checks required keys, types, and constraints match the existing domain finding contract.</summary>
    [Fact]
    public void SchemaDefinesClosedObjectsAndSupportedFindingValues()
    {
        using var document = JsonDocument.Parse(ReviewOutputContract.Schema(10));
        var schema = document.RootElement;
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        AssertClosedObject(schema, ["summary", "findings"]);
        var properties = schema.GetProperty("properties");
        AssertNonemptyString(properties.GetProperty("summary"));
        var findings = properties.GetProperty("findings");
        Assert.Equal("array", findings.GetProperty("type").GetString());
        Assert.Equal(10, findings.GetProperty("maxItems").GetInt32());
        Assert.False(findings.TryGetProperty("minItems", out _));
        var finding = findings.GetProperty("items");
        var domainFields = typeof(ReviewFinding).GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Append("side").Order().ToArray();
        AssertClosedObject(finding, domainFields);
        var fields = finding.GetProperty("properties");
        Assert.Equal("string", fields.GetProperty("severity").GetProperty("type").GetString());
        Assert.Equal(Enum.GetNames<ReviewSeverity>().Select(name => name.ToLowerInvariant()),
            fields.GetProperty("severity").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        foreach (var name in new[] { "title", "path", "explanation", "recommendation" })
            AssertNonemptyString(fields.GetProperty(name));
        Assert.Equal(new[] { "integer", "null" }, fields.GetProperty("line").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, fields.GetProperty("line").GetProperty("minimum").GetInt32());
        Assert.Equal("string", fields.GetProperty("side").GetProperty("type").GetString());
        Assert.Equal("RIGHT", fields.GetProperty("side").GetProperty("const").GetString());
    }

    /// <summary>Checks positive, empty, and unanchored output fixtures fit the schema fields and current finding model.</summary>
    /// <param name="fixture">Synthetic response scenario to check.</param>
    [Theory]
    [InlineData("defect")]
    [InlineData("no-findings")]
    [InlineData("deleted")]
    public void ResponseFixturesAreCompatibleWithSchemaAndDomain(string fixture)
    {
        var response = fixture switch
        {
            "defect" => ReviewOutputContract.Example,
            "no-findings" => """
                {"summary":"No actionable findings in the analyzed diff. Only supplied patches and context were reviewed; this does not establish that the code is safe or tested.","findings":[]}
                """,
            _ => """
                {
                  "summary":"The diff deletes explicit negative-amount validation. Review is limited to the supplied deletion hunk.",
                  "findings":[{
                    "severity":"medium", "title":"Negative amounts are no longer rejected",
                    "path":"src/Validation.cs", "line":null, "side":"RIGHT",
                    "explanation":"The deletion removes the amount < 0 guard and its ArgumentOutOfRangeException. Negative input formerly rejected by this check no longer raises that exception here.",
                    "recommendation":"Preserve equivalent negative-amount validation before accepting the amount."
                  }]
                }
                """
        };
        using var schema = JsonDocument.Parse(ReviewOutputContract.Schema(1));
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(schema.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(),
            root.EnumerateObject().Select(property => property.Name).Order());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("summary").GetString()));
        Assert.InRange(root.GetProperty("findings").GetArrayLength(), 0, properties.GetProperty("findings").GetProperty("maxItems").GetInt32());
        var snapshot = ReviewPromptFixtures.Selected(fixture);
        foreach (var finding in root.GetProperty("findings").EnumerateArray())
        {
            var fields = properties.GetProperty("findings").GetProperty("items");
            Assert.Equal(fields.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(),
                finding.EnumerateObject().Select(property => property.Name).Order());
            foreach (var name in new[] { "title", "path", "explanation", "recommendation" })
                Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty(name).GetString()));
            Assert.Equal("RIGHT", finding.GetProperty("side").GetString());
            var severityText = finding.GetProperty("severity").GetString()!;
            Assert.Contains(severityText, fields.GetProperty("properties").GetProperty("severity").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
            int? line = finding.GetProperty("line").ValueKind == JsonValueKind.Null ? null : finding.GetProperty("line").GetInt32();
            if (line.HasValue) Assert.True(line.Value >= 1);
            var model = new ReviewFinding(Enum.Parse<ReviewSeverity>(severityText, ignoreCase: true),
                finding.GetProperty("title").GetString()!, finding.GetProperty("path").GetString()!, line,
                finding.GetProperty("explanation").GetString()!, finding.GetProperty("recommendation").GetString()!);
            Assert.Equal(Assert.Single(snapshot.ChangedFiles).Path, model.Path);
            Assert.Equal(fixture == "deleted" ? null : (int?)3, model.Line);
        }
    }

    private static void AssertClosedObject(JsonElement schema, string[] keys)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(keys.Order(), schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order());
        Assert.Equal(keys.Order(), schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
    }

    private static void AssertNonemptyString(JsonElement schema)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(1, schema.GetProperty("minLength").GetInt32());
    }
}
