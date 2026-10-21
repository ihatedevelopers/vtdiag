using System.Text.Json;
using Json.Schema;

namespace VTD.Contracts.Tests;

/// <summary>Validates JSON text against a checked-in schema with JsonSchema.Net.</summary>
internal static class SchemaValidation
{
    public static IReadOnlyList<string> Validate(string schemaName, string json)
    {
        var schemaText = File.ReadAllText(Path.Combine(RepoPaths.SchemaDir, schemaName + ".schema.json"));
        var schema = JsonSchema.FromText(schemaText, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        using var doc = JsonDocument.Parse(json);
        var results = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        Collect(results, errors);
        return errors.Count > 0 ? errors : ["invalid (no detail)"];
    }

    private static void Collect(EvaluationResults results, List<string> errors)
    {
        if (results.Errors is { Count: > 0 })
        {
            foreach (var (keyword, message) in results.Errors)
            {
                errors.Add($"{results.InstanceLocation} [{keyword}] {message}");
            }
        }

        if (results.Details is { Count: > 0 })
        {
            foreach (var detail in results.Details)
            {
                Collect(detail, errors);
            }
        }
    }
}
