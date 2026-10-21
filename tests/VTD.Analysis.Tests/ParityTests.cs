using System.Text.Json;
using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;

namespace VTD.Analysis.Tests;

/// <summary>The C# providers and the KB declarations stay one-to-one, and everything the engine shows has text.</summary>
public sealed class ParityTests
{
    [Fact]
    public void Builtin_providers_and_kb_declarations_match()
    {
        var declared = TestKb.Real.Signals.Where(s => s.Source == SignalSource.Builtin).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var provided = BuiltinSignals.All.SelectMany(p => p.Provides).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(declared.Except(provided, StringComparer.Ordinal));
        Assert.Empty(provided.Except(declared, StringComparer.Ordinal));
        Assert.Equal(provided.Count, BuiltinSignals.All.Sum(p => p.Provides.Count)); // no id provided twice
    }

    [Fact]
    public void Every_catalog_check_id_has_a_provider()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestKb.Root, "data", "catalog.json")));
        var checks = catalog.RootElement.EnumerateArray()
            .SelectMany(p => p.GetProperty("diagnostic_checks").EnumerateArray().Select(c => c.GetProperty("check_id").GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(72, checks.Count);
        var providerChecks = BuiltinSignals.All.Select(p => p.CheckId).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(checks.Except(providerChecks, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_declared_signal_gets_a_result_and_every_result_is_declared()
    {
        var declared = TestKb.Real.Signals.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var analyzer = new Analyzer(TestKb.Real);
        foreach (var (label, snapshot) in AccuracyTests.LoadCases())
        {
            var signals = analyzer.EvaluateSignals(AccuracyTests.Input(label, snapshot));
            Assert.Empty(declared.Except(signals.Keys, StringComparer.Ordinal));
            var undeclared = signals.Keys.Where(k => !declared.Contains(k) && !k.StartsWith("mon.", StringComparison.Ordinal)).ToList();
            Assert.True(undeclared.Count == 0, label.Name + ": " + string.Join(", ", undeclared));
        }
    }

    [Fact]
    public void Every_text_key_the_engine_emits_exists_in_english_and_turkish()
    {
        var kb = TestKb.Real;
        var analyzer = new Analyzer(kb);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        void Check(string? key)
        {
            if (key is not null && (!kb.HasText(key, Languages.English) || !kb.HasText(key, Languages.Turkish)))
            {
                missing.Add(key);
            }
        }

        foreach (var (label, snapshot) in AccuracyTests.LoadCases())
        {
            var input = AccuracyTests.Input(label, snapshot);
            var r = analyzer.Analyze(input);
            foreach (var h in r.Hypotheses)
            {
                Check(h.TitleKey);
                foreach (var c in h.Contributions)
                {
                    Check(c.TextKey);
                    foreach (var e in c.Evidence)
                    {
                        Check(e.TextKey);
                    }
                }
            }

            foreach (var item in r.Health.Items)
            {
                Check(item.TitleKey);
                Check(item.DetailKey);
                if (item.Args.TryGetValue("reason", out var reasonKey))
                {
                    Check(reasonKey);
                }
            }

            foreach (var p in r.Proposals)
            {
                Check(p.ReasonKey);
                Check(p.UnavailableReasonKey);
            }

            foreach (var c in r.CouldNotCheck)
            {
                Check(c.TextKey);
            }

            foreach (var a in r.Anomalies.SelectMany(a => a.Evidence))
            {
                Check(a.TextKey);
            }

            Check(r.Route?.TextKey);
            foreach (var s in analyzer.EvaluateSignals(input).Values.SelectMany(s => s.Evidence))
            {
                Check(s.TextKey);
            }
        }

        Assert.True(missing.Count == 0, "Missing strings: " + string.Join(", ", missing));
    }

    [Fact]
    public void Evidence_placeholders_are_filled_by_the_providers()
    {
        var kb = TestKb.Real;
        var analyzer = new Analyzer(kb);
        foreach (var (label, snapshot) in AccuracyTests.LoadCases())
        {
            foreach (var ev in analyzer.EvaluateSignals(AccuracyTests.Input(label, snapshot)).Values.SelectMany(s => s.Evidence))
            {
                var args = ev.Args.ToDictionary(a => a.Key, a => (object)a.Value, StringComparer.Ordinal);
                var text = kb.Text(ev.TextKey, Languages.English, args);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }
        }
    }
}
