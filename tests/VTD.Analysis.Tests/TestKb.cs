using VTD.Contracts.Knowledge;
using VTD.Knowledge;

namespace VTD.Analysis.Tests;

/// <summary>The real kb/ loaded through the production loader, shared by all analysis tests.</summary>
internal static class TestKb
{
    private static readonly Lazy<KnowledgeBase> Kb = new(Load);

    public static string Root { get; } = FindRoot();

    public static KnowledgeBase Real => Kb.Value;

    public static string GeneratedDir => Path.Combine(Root, "tests", "fixtures", "snapshots", "generated");

    /// <summary>Set <c>VTD_UPDATE_GENERATED=1</c> to rewrite tests/fixtures/snapshots/generated/ from <see cref="GeneratedFixtures"/>.</summary>
    public static bool UpdateGenerated => string.Equals(Environment.GetEnvironmentVariable("VTD_UPDATE_GENERATED"), "1", StringComparison.Ordinal);

    private static KnowledgeBase Load()
    {
        var result = new KnowledgeBaseLoader([]).LoadAsync(new KbLoadOptions
        {
            BundledPath = Path.Combine(Root, "kb"),
            NowUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
            AppVersion = "0.1.0",
        }, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Issues.Any(i => i.Severity == KbIssueSeverity.Error))
        {
            throw new InvalidOperationException("kb/ does not load: " + string.Join("; ", result.Issues.Where(i => i.Severity == KbIssueSeverity.Error).Select(i => i.Code + " " + i.Path)));
        }

        return (KnowledgeBase)result.Kb;
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
