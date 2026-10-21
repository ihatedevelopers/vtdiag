using System.IO.Compression;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>Shared builders for the reporting tests. Everything is deterministic: fixed salt, FakeClock, sequential ids.</summary>
internal static class TestSupport
{
    /// <summary>A fixed 32-byte install salt.</summary>
    public static byte[] Salt { get; } = [.. Enumerable.Range(1, 32).Select(i => (byte)(i * 7))];

    /// <summary>Another salt (hashes must differ).</summary>
    public static byte[] OtherSalt { get; } = [.. Enumerable.Range(1, 32).Select(i => (byte)(255 - i))];

    public static Redactor Redactor() => new(Salt);

    /// <summary>The redactor the engine builds: it also knows the computer name and the player's user name.</summary>
    public static Redactor EngineRedactor() => new(Salt, [CanaryTokens.ComputerName, CanaryTokens.UserName]);

    public static ScanSnapshot Snapshot(string fixture) => Fixtures.Load(fixture);

    public static AnalysisResult Analysis(ScanSnapshot snapshot) => CannedAnalyses.For(snapshot);

    public static ShareExporter Exporter(Redactor redactor, FakeKnowledgeBase? kb = null, FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        return new ShareExporter(redactor, new ReportBuilder(kb ?? FakeKnowledgeBase.Sample(), redactor), new ScanBundleCodec(redactor), new SequentialIdGenerator(clock), clock);
    }

    public static ShareExportInput Input(string fixture, bool withMonitor = true)
    {
        var snapshot = Snapshot(fixture);
        return new ShareExportInput
        {
            Snapshot = snapshot,
            Analysis = Analysis(snapshot),
            Monitor = withMonitor ? FakeMonitorSummary() : null,
            AppVersion = FixtureSnapshots.AppVersion,
            KbVersion = FixtureSnapshots.KbVersion,
            Attempts = [],
        };
    }

    /// <summary>A monitor summary from the fake monitor (Realtek periodic spikes) with a per-minute series.</summary>
    public static Contracts.Monitoring.MonitorSummary FakeMonitorSummary()
    {
        var clock = new FakeClock();
        var monitor = new FakeMonitorService(clock, new SequentialIdGenerator(clock));
        monitor.StartAsync(new Contracts.Monitoring.MonitorOptions(), CancellationToken.None).GetAwaiter().GetResult();
        clock.Advance(TimeSpan.FromMinutes(30));
        var summary = monitor.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        var start = summary.StartUtc;
        return summary with
        {
            Network = summary.Network! with { EndpointIp = "203.0.113.77" },
            Series = [.. Enumerable.Range(0, 30).Select(i => new Contracts.Monitoring.MinuteSample { Utc = start.AddMinutes(i), Fps = i % 4 == 3 ? 180 : 230, P1LowFps = i % 4 == 3 ? 40 : 70, Spikes = i % 4 == 3 ? 20 : 8 })],
        };
    }

    /// <summary>Every text a canary must not survive in: decoded zip entries for bundles, the text otherwise.</summary>
    public static IReadOnlyList<(string Name, string Text)> Outputs(ExportResult result)
    {
        if (result.Base64Content is { } b64)
        {
            using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(b64)), ZipArchiveMode.Read);
            return [.. zip.Entries.Select(e =>
            {
                using var s = e.Open();
                using var r = new StreamReader(s, Encoding.UTF8);
                return (e.FullName, r.ReadToEnd());
            })];
        }

        return [("text", result.TextContent ?? string.Empty)];
    }

    /// <summary>Canary shapes that must never appear (ordinal ignore case), including the SID and PUUID values themselves.</summary>
    public static IReadOnlyList<string> ForbiddenFragments { get; } =
    [
        RedactionConstants.CanaryMarker,
        "VtdCanaryUser",
        "S-1-5-21-1111111111-2222222222-3333333333-1001",
        "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
        "q8Zr2pX7uLmN4kTs",
        "eyJhbGciOiJIUzI1NiJ9",
        "vtdcanary@example.invalid",
    ];

    public static void AssertNoCanary(string name, string text)
    {
        foreach (var fragment in ForbiddenFragments)
        {
            Assert.False(text.Contains(fragment, StringComparison.OrdinalIgnoreCase), $"'{fragment}' survived in {name}");
        }

        Assert.DoesNotContain("Users\\\\VtdCanary", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Repository root (for golden files).</summary>
    public static string RepoRoot { get; } = FindRoot();

    public static string GoldenDir => Path.Combine(RepoRoot, "tests", "VTD.Reporting.Tests", "Golden");

    /// <summary>Set <c>VTD_UPDATE_GOLDEN=1</c> to rewrite the golden files.</summary>
    public static bool UpdateGolden => string.Equals(Environment.GetEnvironmentVariable("VTD_UPDATE_GOLDEN"), "1", StringComparison.Ordinal);

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
