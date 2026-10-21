using System.Text.Json;
using Json.Schema;

namespace VTD.Collectors.Tests.Support;

/// <summary>Repository paths and switches for the collector tests.</summary>
internal static class TestPaths
{
    public static string Root { get; } = FindRoot();

    public static string CaptureDir => Path.Combine(Root, "tests", "fixtures", "captures");

    public static string RealSnapshotDir => Path.Combine(Root, "tests", "fixtures", "snapshots", "real");

    public static string SchemaDir => Path.Combine(Root, "kb", "schema");

    /// <summary><c>VTD_LIVE_SCAN=1</c>: run the real (read-only, unelevated) scan of this PC.</summary>
    public static bool LiveScan => string.Equals(Environment.GetEnvironmentVariable("VTD_LIVE_SCAN"), "1", StringComparison.Ordinal);

    /// <summary><c>VTD_UPDATE_CAPTURE=1</c>: with <see cref="LiveScan"/>, rewrite the checked-in redacted capture and real snapshot.</summary>
    public static bool UpdateCapture => string.Equals(Environment.GetEnvironmentVariable("VTD_UPDATE_CAPTURE"), "1", StringComparison.Ordinal);

    /// <summary>Optional folder for unredacted live-scan output (never inside the repository).</summary>
    public static string? LiveOut => Environment.GetEnvironmentVariable("VTD_LIVE_OUT");

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    public static IReadOnlyList<string> ValidateSnapshot(string json)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(SchemaDir, "snapshot.schema.json")), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
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

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VTDiag.sln not found above " + AppContext.BaseDirectory);
    }
}
