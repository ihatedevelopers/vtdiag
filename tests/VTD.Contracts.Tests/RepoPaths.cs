namespace VTD.Contracts.Tests;

/// <summary>Locates the repository from the test output folder.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string SchemaDir => Path.Combine(Root, "kb", "schema");

    public static string FixtureDir => Path.Combine(Root, "tests", "fixtures", "snapshots");

    /// <summary>Set <c>VTD_UPDATE_SCHEMAS=1</c> to rewrite <c>kb/schema/*.schema.json</c> from the contracts.</summary>
    public static bool UpdateSchemas => string.Equals(Environment.GetEnvironmentVariable("VTD_UPDATE_SCHEMAS"), "1", StringComparison.Ordinal);

    /// <summary>Set <c>VTD_UPDATE_FIXTURES=1</c> to rewrite <c>tests/fixtures/snapshots/*.json</c> from <c>FixtureSnapshots</c>.</summary>
    public static bool UpdateFixtures => string.Equals(Environment.GetEnvironmentVariable("VTD_UPDATE_FIXTURES"), "1", StringComparison.Ordinal);

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

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
