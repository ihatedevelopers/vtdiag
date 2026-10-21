using System.Text.Json;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Integration.Tests;

/// <summary>End-to-end runs of the M1 chain on the real dev-PC snapshot and every fixture.</summary>
public sealed class EndToEndTests(ITestOutputHelper output)
{
    /// <summary>The 52 generated fixtures plus the three hand-made M0 fixtures.</summary>
    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Pipeline.GeneratedDir, "*.json").Order(StringComparer.Ordinal))
        {
            if (!string.Equals(Path.GetFileName(file), "labels.json", StringComparison.Ordinal))
            {
                data.Add("generated/" + Path.GetFileNameWithoutExtension(file));
            }
        }

        foreach (var name in Fixtures.Names)
        {
            data.Add("m0/" + name);
        }

        return data;
    }

    [Fact]
    public void Fixture_set_is_complete()
    {
        Assert.Equal(55, AllFixtures().Count);
    }

    [Theory]
    [InlineData(Languages.English)]
    [InlineData(Languages.Turkish)]
    public void Real_dev_pc_snapshot_runs_end_to_end_and_exports_cleanly(string language)
    {
        Assert.SkipUnless(File.Exists(Pipeline.RealSnapshotPath), "Private dev-PC snapshot not present (excluded from the public repo).");
        var snapshot = Pipeline.LoadReal();
        var analysis = Pipeline.Analyze(snapshot);
        Assert.NotEmpty(analysis.Health.Items);
        var exports = Pipeline.ExportAll(snapshot, analysis, language, KnownIdentifiers());
        Pipeline.AssertShareable("dev-pc", exports, KnownIdentifiers());
        output.WriteLine(string.Join("\n", exports.Select(e => $"{e.Kind}: {e.Texts.Sum(t => t.Text.Length)} chars")));
    }

    [Fact]
    public void Canaries_planted_in_the_real_snapshot_never_survive_an_export()
    {
        Assert.SkipUnless(File.Exists(Pipeline.RealSnapshotPath), "Private dev-PC snapshot not present (excluded from the public repo).");
        var snapshot = Pipeline.WithCanaries(Pipeline.LoadReal());
        var exports = Pipeline.ExportAll(snapshot, Pipeline.Analyze(snapshot), Languages.English, KnownIdentifiers());
        Pipeline.AssertShareable("dev-pc+canaries", exports, KnownIdentifiers());
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_runs_end_to_end_and_exports_cleanly(string fixture)
    {
        var (snapshot, intake) = Load(fixture);
        var analysis = Pipeline.Analyze(snapshot, intake);
        Assert.Equal(snapshot.Meta.SnapshotId, analysis.SnapshotId);
        Assert.InRange(analysis.UnknownCause.Probability, 0, 1);
        Assert.All(analysis.Hypotheses, h => Assert.InRange(h.Posterior, 0, 1));
        Pipeline.AssertShareable(fixture, Pipeline.ExportAll(snapshot, analysis, Languages.English));
    }

    [Fact]
    public void Real_dev_pc_analysis_is_plausible_for_a_pc_without_symptoms()
    {
        Assert.SkipUnless(File.Exists(Pipeline.RealSnapshotPath), "Private dev-PC snapshot not present (excluded from the public repo).");
        var snapshot = Pipeline.LoadReal();
        var analysis = Pipeline.Analyze(snapshot);
        var kb = Pipeline.Kb;
        string T(string key) => kb.Text(key, Languages.English);

        var items = analysis.Health.Items;
        output.WriteLine($"health: {items.Count} items: " + string.Join(", ", items.GroupBy(i => i.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        foreach (var i in items.Where(i => i.Status is HealthStatus.Fail or HealthStatus.Warn))
        {
            output.WriteLine($"  {i.Status,-5} {i.Group,-10} {T(i.TitleKey)}");
        }

        foreach (var i in items.Where(i => i.Status == HealthStatus.Unknown))
        {
            output.WriteLine($"  ?     {i.Group,-10} {T(i.TitleKey)} ({i.UnknownReason})");
        }

        output.WriteLine($"primary bucket {analysis.PrimaryBucket}; unknown cause {Pipeline.Num(analysis.UnknownCause.Probability)} leading={analysis.UnknownCause.Leading}; route {analysis.Route?.Kind.ToString() ?? "none"}");
        foreach (var h in analysis.Hypotheses.Take(5))
        {
            output.WriteLine($"  {h.Band,-12} {h.Bucket,-14} {Pipeline.Num(h.Posterior)} {h.Id} :: {string.Join("; ", h.Contributions.Where(c => c.Counted && c.DeltaLogOdds != 0).Select(c => $"{c.SignalId}={c.State}"))}");
        }

        output.WriteLine($"proposals: {string.Join(", ", analysis.Proposals.Select(p => p.FixId))}");
        output.WriteLine($"could not check: {analysis.CouldNotCheck.Count} ({string.Join(", ", analysis.CouldNotCheck.GroupBy(c => c.Reason).Select(g => $"{g.Key} {g.Count()}"))})");

        // A PC whose owner reported nothing: no verdict may claim "very likely", and no route may fire.
        Assert.DoesNotContain(analysis.Hypotheses, h => h.Band == ConfidenceBand.VeryLikely);
        Assert.Null(analysis.Route);

        // The scan ran unelevated: admin-only facts are "could not check", never a pass or a fail.
        var admin = items.Where(i => i.UnknownReason == UnknownReason.NeedsAdmin).ToList();
        Assert.NotEmpty(admin);
        Assert.All(admin, i => Assert.Equal(HealthStatus.Unknown, i.Status));
        // ...and the report says so in words: every NeedsAdmin health row renders the "needs administrator" reason.
        var exports = Pipeline.ExportAll(snapshot, analysis, Languages.English);
        var html = exports.Single(e => e.Kind == Contracts.Ipc.ExportKind.ReportHtml).Result.TextContent!;
        Assert.Contains("needs admin permission", html, StringComparison.Ordinal); // ReportStrings reason.needsAdmin
        foreach (var i in admin)
        {
            Assert.Contains(System.Net.WebUtility.HtmlEncode(T(i.TitleKey)), html, StringComparison.Ordinal);
        }

        // "Could not check" lines name the thing and then the reason, never the thing twice.
        var md = exports.Single(e => e.Kind == Contracts.Ipc.ExportKind.ReportMarkdown).Result.TextContent!;
        output.WriteLine(md);
        foreach (var c in analysis.CouldNotCheck.Where(c => c.TextKey is not null))
        {
            var what = T(c.TextKey!);
            Assert.DoesNotContain(what + ": " + what, md, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> KnownIdentifiers()
        => new[] { Environment.UserName, Environment.MachineName }.Where(s => s.Length >= 3);

    private static (ScanSnapshot Snapshot, IntakeAnswers Intake) Load(string fixture)
    {
        var name = fixture[(fixture.IndexOf('/', StringComparison.Ordinal) + 1)..];
        if (fixture.StartsWith("m0/", StringComparison.Ordinal))
        {
            return (Fixtures.Load(name), new IntakeAnswers { Symptoms = Fixtures.Symptoms[name] });
        }

        var snapshot = VtdJson.Deserialize<ScanSnapshot>(File.ReadAllText(Path.Combine(Pipeline.GeneratedDir, name + ".json")));
        using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(Pipeline.GeneratedDir, "labels.json")));
        var label = labels.RootElement.EnumerateArray().Single(l => string.Equals(l.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        static IReadOnlyList<string> Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
        return (snapshot, new IntakeAnswers
        {
            Symptoms = Strings(label.GetProperty("symptoms")),
            TypedCodes = Strings(label.GetProperty("typedCodes")),
            Answers = label.GetProperty("answers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
        });
    }
}
