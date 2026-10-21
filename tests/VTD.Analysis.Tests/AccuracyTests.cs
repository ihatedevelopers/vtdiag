using System.Globalization;
using System.Text.Json;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Analysis.Tests;

/// <summary>
/// The CI accuracy gate (DESIGN.md testing): top-3 hit rate on labelled fixtures, and the rate of false "very likely"
/// verdicts (a hypothesis shown as Very likely that is neither the label nor an accepted co-existing cause).
/// </summary>
public sealed class AccuracyTests(ITestOutputHelper output)
{
    /// <summary>Minimum top-3 hit rate (DESIGN.md M1 gate: 90 %).</summary>
    private const double MinTop3 = 0.90;

    /// <summary>Maximum share of fixtures with a false "very likely".</summary>
    private const double MaxFalseVeryLikely = 0.05;

    private static readonly JsonSerializerOptions LabelJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void Generated_fixtures_are_up_to_date()
    {
        var all = GeneratedFixtures.All();
        Directory.CreateDirectory(TestKb.GeneratedDir);
        var labels = JsonSerializer.Serialize(all.Select(a => a.Label).ToList(), LabelJson).ReplaceLineEndings("\n") + "\n";
        var stale = new List<string>();
        foreach (var (label, snapshot) in all)
        {
            var path = Path.Combine(TestKb.GeneratedDir, label.Name + ".json");
            var json = VtdJson.Serialize(snapshot, indented: true).ReplaceLineEndings("\n") + "\n";
            if (TestKb.UpdateGenerated)
            {
                File.WriteAllText(path, json);
            }
            else if (!File.Exists(path) || !string.Equals(File.ReadAllText(path).ReplaceLineEndings("\n"), json, StringComparison.Ordinal))
            {
                stale.Add(label.Name);
            }
        }

        var labelsPath = Path.Combine(TestKb.GeneratedDir, "labels.json");
        if (TestKb.UpdateGenerated)
        {
            File.WriteAllText(labelsPath, labels);
        }
        else if (!File.Exists(labelsPath) || !string.Equals(File.ReadAllText(labelsPath).ReplaceLineEndings("\n"), labels, StringComparison.Ordinal))
        {
            stale.Add("labels.json");
        }

        Assert.True(stale.Count == 0, "Generated fixtures are stale; re-run with VTD_UPDATE_GENERATED=1: " + string.Join(", ", stale));
    }

    [Fact]
    public void Accuracy_gate()
    {
        var cases = LoadCases();
        Assert.True(cases.Count >= 40, $"only {cases.Count} labelled fixtures");
        var analyzer = new Analyzer(TestKb.Real);
        int top1 = 0, top3 = 0, scored = 0, falseVeryLikely = 0, routeOk = 0, routes = 0, unknownOk = 0, unknowns = 0;
        foreach (var (label, snapshot) in cases)
        {
            var result = analyzer.Analyze(Input(label, snapshot));
            var ids = result.Hypotheses.Select(h => h.Id).ToList();
            var accepted = new HashSet<string>(label.Accept, StringComparer.Ordinal);
            if (label.ExpectedTop is not null)
            {
                accepted.Add(label.ExpectedTop);
            }

            var wrongVeryLikely = result.Hypotheses.Where(h => h.Band == ConfidenceBand.VeryLikely && !accepted.Contains(h.Id)).Select(h => h.Id).ToList();
            if (wrongVeryLikely.Count > 0)
            {
                falseVeryLikely++;
            }

            string verdict;
            if (label.ExpectedRoute is not null)
            {
                routes++;
                var ok = result.Route is not null && string.Equals(Camel(result.Route.Kind.ToString()), label.ExpectedRoute, StringComparison.Ordinal) && result.Proposals.Count == 0;
                routeOk += ok ? 1 : 0;
                verdict = ok ? "route ok" : $"ROUTE MISS ({result.Route?.Kind.ToString() ?? "none"})";
            }
            else if (label.ExpectedTop is null)
            {
                unknowns++;
                var ok = result.UnknownCause.Leading && !result.Hypotheses.Any(h => h.Band == ConfidenceBand.VeryLikely);
                unknownOk += ok ? 1 : 0;
                verdict = ok ? "unknown ok" : "UNKNOWN MISS";
            }
            else
            {
                scored++;
                var rank = ids.IndexOf(label.ExpectedTop);
                top1 += rank == 0 ? 1 : 0;
                top3 += rank is >= 0 and < 3 ? 1 : 0;
                verdict = rank switch { 0 => "top1", 1 or 2 => "top3", -1 => "MISS", _ => $"MISS rank {rank + 1}" };
            }

            var head = string.Join(" | ", result.Hypotheses.Take(3).Select(h => string.Create(CultureInfo.InvariantCulture, $"{h.Id} {h.Posterior:0.00}")));
            output.WriteLine($"{label.Name,-32} {verdict,-14} unknown={result.UnknownCause.Probability:0.00} {head}{(wrongVeryLikely.Count > 0 ? "  FALSE-VL: " + string.Join(",", wrongVeryLikely) : string.Empty)}");
        }

        var top1Rate = top1 / (double)scored;
        var top3Rate = (top3 + routeOk + unknownOk) / (double)(scored + routes + unknowns);
        var fvlRate = falseVeryLikely / (double)cases.Count;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cases {cases.Count}: top1 {top1}/{scored} ({top1Rate:P0}), top3 incl. routes+unknowns {top3 + routeOk + unknownOk}/{scored + routes + unknowns} ({top3Rate:P0}), false very-likely {falseVeryLikely} ({fvlRate:P1})"));
        Assert.True(top3Rate >= MinTop3, string.Create(CultureInfo.InvariantCulture, $"top-3 {top3Rate:P1} < {MinTop3:P0}"));
        Assert.True(fvlRate <= MaxFalseVeryLikely, string.Create(CultureInfo.InvariantCulture, $"false very-likely {fvlRate:P1} > {MaxFalseVeryLikely:P0}"));
        Assert.Equal(routes, routeOk);
    }

    [Fact]
    public void M0_fixtures_rank_their_expected_cause_first()
    {
        var analyzer = new Analyzer(TestKb.Real);
        foreach (var name in Fixtures.Names)
        {
            var snapshot = Fixtures.Load(name);
            var result = analyzer.Analyze(new AnalysisInput
            {
                AnalysisId = "01JB6R7X3K9M2N4P5Q6R7S8TA0",
                Snapshot = snapshot,
                Intake = new IntakeAnswers { Symptoms = Fixtures.Symptoms[name] },
                AsOfUtc = snapshot.Meta.CreatedUtc,
            });
            output.WriteLine($"{name}: " + string.Join(" | ", result.Hypotheses.Take(4).Select(h => string.Create(CultureInfo.InvariantCulture, $"{h.Id} {h.Posterior:0.00} {h.Bucket}"))));
            Assert.Equal(Fixtures.ExpectedTopHypothesis[name], result.Hypotheses[0].Id);
        }
    }

    internal static List<(FixtureLabel Label, ScanSnapshot Snapshot)> LoadCases()
    {
        var labels = JsonSerializer.Deserialize<List<FixtureLabel>>(File.ReadAllText(Path.Combine(TestKb.GeneratedDir, "labels.json")), LabelJson)!;
        var cases = labels.Select(l => (l, VtdJson.Deserialize<ScanSnapshot>(File.ReadAllText(Path.Combine(TestKb.GeneratedDir, l.Name + ".json"))))).ToList();
        foreach (var name in Fixtures.Names)
        {
            cases.Add((new FixtureLabel { Name = "m0-" + name, ExpectedTop = Fixtures.ExpectedTopHypothesis[name], Symptoms = Fixtures.Symptoms[name] }, Fixtures.Load(name)));
        }

        return cases;
    }

    internal static AnalysisInput Input(FixtureLabel label, ScanSnapshot snapshot) => new()
    {
        AnalysisId = "01JB6R7X3K9M2N4P5Q6R7S8TA0",
        Snapshot = snapshot,
        Intake = new IntakeAnswers { Symptoms = label.Symptoms, TypedCodes = label.TypedCodes, Answers = label.Answers },
        AsOfUtc = snapshot.Meta.CreatedUtc.AddMinutes(5),
    };

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}
