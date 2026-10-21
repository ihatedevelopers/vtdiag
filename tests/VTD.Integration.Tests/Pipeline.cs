using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VTD.Analysis;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Ipc;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Knowledge;
using VTD.Reporting;
using VTD.Testing.Fakes;

namespace VTD.Integration.Tests;

/// <summary>
/// The M1 chain the real engine will run, with production types only: snapshot → bundled KB (production loader) →
/// <see cref="Analyzer"/> → <see cref="ShareExporter"/> (HTML, Markdown, JSON, Copy for AI, .vtdscan) → sweep.
/// </summary>
internal static partial class Pipeline
{
    /// <summary>Markdown limit (DESIGN.md: Copy for Reddit ≤ 9 000 characters).</summary>
    public const int MarkdownMax = 9000;

    private static readonly Lazy<IKnowledgeBase> Bundled = new(LoadKb);

    private static readonly byte[] Salt = [.. Enumerable.Range(1, 32).Select(i => (byte)(i * 11))];

    public static string Root { get; } = FindRoot();

    public static IKnowledgeBase Kb => Bundled.Value;

    public static string RealSnapshotPath => Path.Combine(Root, "tests", "fixtures", "snapshots", "real", "dev-pc.json");

    public static string GeneratedDir => Path.Combine(Root, "tests", "fixtures", "snapshots", "generated");

    /// <summary>Every export kind the player can make.</summary>
    public static IReadOnlyList<ExportKind> Kinds { get; } = [ExportKind.ReportHtml, ExportKind.ReportMarkdown, ExportKind.ReportJson, ExportKind.AiDigest, ExportKind.ScanBundle];

    /// <summary>Canary shapes and real-PII shapes that must never survive a share export.</summary>
    public static IReadOnlyList<string> Forbidden { get; } =
    [
        RedactionConstants.CanaryMarker, CanaryTokens.UserName, CanaryTokens.Sid, "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
        "q8Zr2pX7uLmN4kTs", "eyJhbGciOiJIUzI1NiJ9", CanaryTokens.Email,
    ];

    public static ScanSnapshot LoadReal() => VtdJson.Deserialize<ScanSnapshot>(File.ReadAllText(RealSnapshotPath));

    public static AnalysisResult Analyze(ScanSnapshot snapshot, IntakeAnswers? intake = null) => new Analyzer(Kb).Analyze(new AnalysisInput
    {
        AnalysisId = "01JB6R7X3K9M2N4P5Q6R7S8TA0",
        Snapshot = snapshot,
        Intake = intake ?? IntakeAnswers.Empty,
        AsOfUtc = snapshot.Meta.CreatedUtc.AddMinutes(5),
    });

    /// <summary>Exports every kind at Share level, in the given language.</summary>
    public static IReadOnlyList<(ExportKind Kind, ExportResult Result, IReadOnlyList<(string Name, string Text)> Texts)> ExportAll(
        ScanSnapshot snapshot, AnalysisResult analysis, string language, IEnumerable<string>? knownIdentifiers = null)
    {
        var redactor = new Redactor(Salt, [CanaryTokens.ComputerName, CanaryTokens.UserName, .. knownIdentifiers ?? []]);
        var clock = new FakeClock(snapshot.Meta.CreatedUtc.AddMinutes(6));
        var exporter = new ShareExporter(redactor, new ReportBuilder(Kb, redactor), new ScanBundleCodec(redactor), new SequentialIdGenerator(clock), clock);
        var input = new ShareExportInput
        {
            Snapshot = snapshot,
            Analysis = analysis,
            AppVersion = FixtureSnapshots.AppVersion,
            KbVersion = Kb.Version.Version,
        };
        return [.. Kinds.Select(k =>
        {
            var r = exporter.Export(new ExportRequest { Kind = k, Language = language }, input);
            return (k, r, Texts(r));
        })];
    }

    /// <summary>The share-level checks every export must pass.</summary>
    public static void AssertShareable(string name, IReadOnlyList<(ExportKind Kind, ExportResult Result, IReadOnlyList<(string Name, string Text)> Texts)> exports, IEnumerable<string>? alsoForbidden = null)
    {
        var forbidden = Forbidden.Concat(alsoForbidden ?? []).Where(f => f.Length >= 3).ToList();
        foreach (var (kind, result, texts) in exports)
        {
            var where = name + "/" + kind;
            Assert.True(result.Status == ExportStatus.Ok, $"{where}: {result.Status} {string.Join(", ", result.SweepHits.Select(h => h.Path + ":" + h.Rule))}");
            Assert.NotEmpty(texts);
            foreach (var (part, text) in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{where}/{part} is empty");
                foreach (var f in forbidden)
                {
                    Assert.False(text.Contains(f, StringComparison.OrdinalIgnoreCase), $"'{f}' survived in {where}/{part}");
                }

                Assert.False(UserSid().IsMatch(text), $"a user SID survived in {where}/{part}");
                Assert.False(ProfilePath().IsMatch(text), $"a user profile path survived in {where}/{part}: {ProfilePath().Match(text).Value}");
                if (part.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || kind == ExportKind.ReportHtml)
                {
                    Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("javascript:", text, StringComparison.OrdinalIgnoreCase);
                }
            }

            switch (kind)
            {
                case ExportKind.ReportMarkdown:
                    Assert.True(result.TextContent!.Length <= MarkdownMax, $"{where}: {result.TextContent.Length} chars > {MarkdownMax}");
                    break;
                case ExportKind.ReportJson:
                    using (var doc = JsonDocument.Parse(result.TextContent!))
                    {
                        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
                    }

                    _ = VtdJson.Deserialize<ReportModel>(result.TextContent!); // the JSON report is the contract, strictly
                    break;
                case ExportKind.ScanBundle:
                    Assert.Contains(texts, t => t.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>A copy with canaries planted in free-text fields a real scan carries (app names, paths, process paths).</summary>
    public static ScanSnapshot WithCanaries(ScanSnapshot s)
    {
        var apps = s.Apps.TryGetValue(out var a) ? a : [];
        var startup = s.Startup.TryGetValue(out var st) ? st : [];
        return s with
        {
            Apps = Fact.Known<IReadOnlyList<InstalledApp>>([.. apps, new InstalledApp
            {
                DisplayName = "Helper for " + CanaryTokens.Email,
                Publisher = CanaryTokens.ComputerName,
                Hive = AppHive.Hkcu,
                KeyName = "canary-app",
                InstallLocation = CanaryTokens.UserProfilePath,
            }], "software/uninstall"),
            Startup = Fact.Known<IReadOnlyList<StartupItem>>([.. startup, new StartupItem
            {
                Name = "Canary " + CanaryTokens.UserName,
                Location = StartupLocation.HkcuRun,
                ImageName = "canary.exe",
                TargetPath = CanaryTokens.UserProfilePath + "\\canary.exe",
                Enabled = true,
            }], "software/startup"),
        };
    }

    private static IReadOnlyList<(string Name, string Text)> Texts(ExportResult result)
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

        return result.TextContent is null ? [] : [("text", result.TextContent)];
    }

    private static IKnowledgeBase LoadKb()
    {
        var result = new KnowledgeBaseLoader([]).LoadAsync(new KbLoadOptions
        {
            BundledPath = Path.Combine(Root, "kb"),
            NowUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
            AppVersion = "0.1.0",
        }, CancellationToken.None).GetAwaiter().GetResult();
        var errors = result.Issues.Where(i => i.Severity == KbIssueSeverity.Error).ToList();
        if (errors.Count > 0 || result.Kb.Rules.Count == 0)
        {
            throw new InvalidOperationException("the bundled kb/ does not load: " + string.Join("; ", errors.Select(i => i.Code + " " + i.Path)));
        }

        return result.Kb;
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

    public static string Num(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // S-1-5-21-<3 sub-authorities>-<RID>: a real account SID.
    [GeneratedRegex(@"S-1-5-21-\d{6,}-\d{6,}-\d{6,}-\d+", RegexOptions.CultureInvariant)]
    private static partial Regex UserSid();

    // C:\Users\<name>\ where <name> is not a placeholder or a shared profile (escaped once in JSON, or not at all).
    [GeneratedRegex(@"[A-Za-z]:\\{1,2}Users\\{1,2}(?!Public\\|Default\\|All Users\\|%|\{|\[|<|\*|…|\.\.\.)[^\\""<>\s]{2,}\\", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePath();
}
