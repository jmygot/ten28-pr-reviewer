using System.Globalization;

namespace Ten28_PrReviewer.Reviews;

internal static class ReviewOutputContract
{
    internal static string Schema(int maxFindings) => $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "findings"],
          "properties": {
            "summary": { "type": "string", "minLength": 1 },
            "findings": {
              "type": "array",
              "maxItems": {{maxFindings.ToString(CultureInfo.InvariantCulture)}},
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["severity", "title", "path", "line", "side", "explanation", "recommendation"],
                "properties": {
                  "severity": { "type": "string", "enum": ["critical", "high", "medium", "low"] },
                  "title": { "type": "string", "minLength": 1 },
                  "path": { "type": "string", "minLength": 1 },
                  "line": { "type": ["integer", "null"], "minimum": 1 },
                  "side": { "type": "string", "const": "RIGHT" },
                  "explanation": { "type": "string", "minLength": 1 },
                  "recommendation": { "type": "string", "minLength": 1 }
                }
              }
            }
          }
        }
        """;

    internal const string Example = """
        {
          "summary": "The supplied diff introduces a null dereference. Review is limited to the supplied patches and context.",
          "findings": [
            {
              "severity": "high",
              "title": "Nullable name is dereferenced",
              "path": "src/DisplayName.cs",
              "line": 3,
              "side": "RIGHT",
              "explanation": "The changed `return name.Trim();` replaces the null fallback. Passing a null name now throws NullReferenceException.",
              "recommendation": "Preserve the null fallback before trimming the name."
            }
          ]
        }
        """;
}
