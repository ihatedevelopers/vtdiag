using System.Diagnostics;
using System.Text.Json;
using Json.Schema;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Knowledge;
using VTD.Knowledge.Lint;
using VTD.Knowledge.Packs;

namespace VTD.Kb.Tests;

/// <summary>The real kb/: loads through the production loader, lints clean, covers the research data, and is in sync with the builder.</summary>
public sealed class KbContentTests
{
    [Fact]
    public void Real_kb_loads_through_the_loader_without_errors()
    {
        var result = Repo.RealKb;
        Assert.DoesNotContain(result.Issues, i => i.Severity == KbIssueSeverity.Error);
        Assert.False(result.FellBackToBundled);
        Assert.Equal(KbSource.Bundled, result.Kb.Version.Source);
        Assert.NotNull(result.Kb.Version.ManifestSha256);
    }

    [Fact]
    public void Lint_is_green()
    {
        var issues = KbLinter.Lint(Repo.Real);
        var errors = issues.Where(i => i.Severity == KbIssueSeverity.Error).Select(i => $"{i.Code} {i.Path}: {i.Message}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(40)));
    }

    [Fact]
    public void English_and_Turkish_are_complete()
    {
        var kb = Repo.Real;
        var en = kb.Keys(Languages.English).ToHashSet(StringComparer.Ordinal);
        var tr = kb.Keys(Languages.Turkish).ToHashSet(StringComparer.Ordinal);
        Assert.True(en.Count > 3000, $"only {en.Count} English strings");
        Assert.Empty(en.Except(tr, StringComparer.Ordinal));
        Assert.DoesNotContain(KbLinter.Lint(kb), i => i.Code.StartsWith("string.", StringComparison.Ordinal));
    }

    [Fact]
    public void Covers_the_research_data()
    {
        var kb = Repo.Real;
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "data", "catalog.json")));
        var catalogIds = catalog.RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();
        Assert.Equal(90, catalogIds.Count);
        var hypotheses = kb.Rules.Where(r => r.Kind == RuleKind.Hypothesis).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(90, hypotheses.Count);
        Assert.All(catalogIds, id => Assert.Contains(id, hypotheses));

        using var conflicts = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "research", "conflicts.json")));
        var conflictIds = conflicts.RootElement.EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
        Assert.Equal(113, conflictIds.Count);
        Assert.Equal(conflictIds.Order(StringComparer.Ordinal), kb.Conflicts.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.All(conflictIds, id => Assert.NotNull(kb.FindRule("conflict-" + id)));

        using var codes = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "research", "error_codes.json")));
        var rows = codes.RootElement.GetProperty("error_codes").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToList();
        Assert.Equal(57, rows.Count);
        foreach (var row in rows)
        {
            var first = row.Split('/')[0].Split('(')[0].Trim();
            if (first.Contains('-', StringComparison.Ordinal) && first.StartsWith("VAL 8", StringComparison.Ordinal))
            {
                first = "VAL 8";
            }

            Assert.True(kb.FindCode(first) is not null, $"research code row '{row}' ({first}) is not in the code table");
        }
    }

    [Fact]
    public void Every_kb_file_matches_its_schema_JsonSchemaNet()
    {
        var manifest = VTD.Contracts.Serialization.VtdJson.Deserialize<KbManifest>(File.ReadAllText(Path.Combine(Repo.Kb, KnowledgeBaseLoader.ManifestFile)));
        var failures = new List<string>();
        var cache = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var schemaName = KbPackReader.SchemaFor(file.Path);
            if (schemaName is null)
            {
                continue;
            }

            if (!cache.TryGetValue(schemaName, out var schema))
            {
                schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Repo.SchemaDir, schemaName + ".schema.json")), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
                cache[schemaName] = schema;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Kb, file.Path)));
            var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
            if (!result.IsValid)
            {
                failures.Add(file.Path);
            }
        }

        Assert.True(failures.Count == 0, "Schema failures: " + string.Join(", ", failures.Take(20)));
    }

    [Fact]
    public void Fixes_only_compose_audited_primitives_and_keep_firmware_in_guides()
    {
        var kb = Repo.Real;
        Assert.True(kb.Fixes.Count >= 25);
        foreach (var f in kb.Fixes)
        {
            Assert.NotEmpty(f.Steps);
            Assert.All(f.Steps, s => Assert.NotNull(OpCatalog.Find(s.Op)));
            Assert.DoesNotContain(f.Steps, s => s.Op == OpIds.BcdSetValue);
            Assert.NotEqual(RiskLevel.High, f.Risk);
            Assert.All(f.Steps.SelectMany(s => s.Args.Values), v =>
            {
                Assert.DoesNotContain("http", v, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".ps1", v, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("cmd.exe", v, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Network-cutting fixes carry a deadman (also enforced by lint).
        Assert.NotNull(kb.FindFix("fix.net.disable_unused_adapter")!.Deadman);
    }

    [Fact]
    public void Guides_cover_every_firmware_requirement()
    {
        var kb = Repo.Real;
        foreach (var id in new[] { "guide.secureboot.enable", "guide.tpm.enable", "guide.hvci.enable", "guide.iommu.enable", "guide.virtualization.enable", "guide.bios.update", "guide.bcd.restore_security", "guide.vanguard.reinstall" })
        {
            var g = kb.FindGuide(id);
            Assert.NotNull(g);
            Assert.Contains(g!.Branches, b => b.When.Count == 0);
        }

        var sb = kb.FindGuide("guide.secureboot.enable")!;
        Assert.Contains(sb.Preflight, p => p.Probe == GuardIds.BitLockerOn);
        Assert.Contains(sb.Branches.SelectMany(b => b.Steps), s => s.Action == GuideAction.RestartToFirmware);
        Assert.True(kb.Guides.Count >= 45);
    }

    [Fact]
    public void Questions_include_the_intake_league_question()
    {
        var q = Repo.Real.FindQuestion("ask.league_also_broken");
        Assert.NotNull(q);
        Assert.True(q!.Intake);
        Assert.NotNull(Repo.Real.FindQuestion("ask.other_account_works"));
        Assert.NotNull(Repo.Real.FindQuestion("ask.vanguard_precheck_on"));
    }

    [Fact]
    public void Route_codes_short_circuit()
    {
        var kb = Repo.Real;
        Assert.Equal(RouteKind.HwidBanAppeal, kb.FindCode("VAN 152")!.Def.Route);
        var val43 = kb.FindCode("val 43")!.Def;
        Assert.Equal(RouteKind.TryAnotherAccount, val43.Route);
        Assert.Equal("ask.other_account_works", val43.AskIfTyped);
        Assert.All(kb.ErrorCodes.SelectMany(c => c.PointsTo), p => Assert.True(p.Typed <= p.Logged));
    }

    [Fact]
    public void Kb_folder_is_in_sync_with_the_builder()
    {
        var python = FindPython();
        if (python is null)
        {
            Assert.Skip("python is not on PATH; CI runs tools/kb_builder.py check separately.");
        }

        var psi = new ProcessStartInfo(python!, ["-I", Path.Combine(Repo.Root, "tools", "kb_builder.py"), "check"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Repo.Root,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.True(p.ExitCode == 0, stdout + stderr);
    }

    private static string? FindPython()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var exe in new[] { "python.exe", "python3.exe" })
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
