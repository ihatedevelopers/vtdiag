using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>The three renderings plus the AI digest.</summary>
public sealed partial class ReportBuilderTests
{
    public static TheoryData<string> FixtureNames => [.. Fixtures.Names];

    private static ReportModel Model(string fixture, Func<ShareExportInput, ShareExportInput>? change = null, string language = Languages.English)
    {
        var input = TestSupport.Input(fixture);
        input = change?.Invoke(input) ?? input;
        var redactor = TestSupport.Redactor();
        var raw = new ReportModel
        {
            SchemaVersion = ReportModel.CurrentSchemaVersion,
            Meta = new ReportMeta { ReportId = "01JB6R7X3K9M2N4P5Q6R7S8TF8", CreatedUtc = FakeClock.DefaultStart, AppVersion = "0.1.0", KbVersion = FixtureSnapshots.KbVersion, Language = language, MachineKey = input.Snapshot.Meta.MachineKey },
            Snapshot = input.Snapshot,
            Analysis = input.Analysis,
            Attempts = input.Attempts,
            Monitor = input.Monitor,
            Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = 1 },
        };
        return redactor.Redact(raw, RedactionPolicy.Share).Value;
    }

    private static ReportBundle Build(ReportModel model, string language = Languages.English, int max = 9000)
        => new ReportBuilder(FakeKnowledgeBase.Sample(), TestSupport.Redactor()).Build(
            model,
            new ReportOptions { Formats = [ReportFormat.Html, ReportFormat.Markdown, ReportFormat.Json, ReportFormat.AiDigest], Language = language, MarkdownMaxChars = max });

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventHandlerAttribute();

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Html_has_no_javascript_and_states_the_bucket_and_disclaimer(string fixture)
    {
        var model = Model(fixture);
        var bundle = Build(model);
        Assert.False(bundle.Blocked);
        var html = bundle.Html!;
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(EventHandlerAttribute(), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\"", html, StringComparison.Ordinal);
        Assert.Contains("VTDiag is not affiliated with Riot Games.", html, StringComparison.Ordinal);
        var bucket = model.Analysis!.PrimaryBucket;
        var sentence = bucket switch
        {
            HonestyBucket.FixableHere => "VTDiag found a likely cause it can fix on this PC.",
            HonestyBucket.GuidedManual => "The fix is a setting you change yourself (for example in the BIOS or for Secure Boot).",
            _ => "We couldn",
        };
        Assert.Contains(System.Net.WebUtility.HtmlEncode(sentence), html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
        Assert.Contains(bundle.JsonSha256, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_never_says_ok_for_unknown_and_shows_couldnt_check()
    {
        var html = Build(Model(Fixtures.Van9003SecureBootOff)).Html!;
        Assert.Contains("Couldn&#39;t check&#58; needs admin permission (the scan ran without it)", html, StringComparison.Ordinal);
        Assert.Contains("Secure Boot is off (VAN 9003)", html, StringComparison.Ordinal);
        Assert.Contains("Guided manual step", html, StringComparison.Ordinal);
        Assert.Contains("Very likely", html, StringComparison.Ordinal);
        Assert.DoesNotContain("0.98", ReportSectionOf(html, "suspects"), StringComparison.Ordinal); // no fake percentages for players
        Assert.Contains("posterior 0.98", html, StringComparison.Ordinal); // the helper section shows the math
    }

    [Fact]
    public void Html_contains_the_monitor_chart_as_inline_svg()
    {
        var html = Build(Model(Fixtures.RealtekStutter)).Html!;
        Assert.Contains("<svg class=\"chart\"", html, StringComparison.Ordinal);
        Assert.Contains("<polyline", html, StringComparison.Ordinal);
        Assert.Contains("average 229 FPS", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_planted_in_free_text_is_escaped()
    {
        const string evil = "<script>alert(1)</script><img src=x onerror=alert(2)> javascript:alert(3)";
        var model = Model(Fixtures.RealtekStutter, input =>
        {
            var analysis = input.Analysis!;
            var h = analysis.Hypotheses[0];
            var contributions = h.Contributions.Select(c => c.EvidenceId == "resets"
                ? c with { Evidence = [c.Evidence[0] with { Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["count"] = evil } }] }
                : c).ToList();
            return input with { Analysis = analysis with { Hypotheses = [h with { Contributions = contributions }] } };
        });
        var bundle = Build(model);
        var html = bundle.Html!;
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(EventHandlerAttribute(), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", bundle.Markdown!, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Markdown_has_the_essentials(string fixture)
    {
        var md = Build(Model(fixture)).Markdown!;
        Assert.True(md.Length <= 9000);
        Assert.Contains("**Verdict:**", md, StringComparison.Ordinal);
        Assert.Contains("**PC:**", md, StringComparison.Ordinal);
        Assert.Contains("**Vanguard readiness:**", md, StringComparison.Ordinal);
        Assert.Contains("Full report: attach the .html file (VTDiag-Report-20261021-", md, StringComparison.Ordinal);
        Assert.Contains("VTDiag is not affiliated with Riot Games.", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_stays_under_the_cap_with_a_huge_analysis()
    {
        var longText = string.Concat(Enumerable.Repeat("very long evidence text ", 200));
        var model = Model(Fixtures.RealtekStutter, input =>
        {
            var a = input.Analysis!;
            var template = a.Hypotheses[0];
            var hypotheses = Enumerable.Range(0, 50).Select(i => template with
            {
                Id = "hyp-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TitleKey = "rule.hyp" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".title",
                Posterior = 0.9 - (i * 0.01),
                Contributions = [.. Enumerable.Range(0, 10).Select(j => new EvidenceContribution { EvidenceId = "e" + j.ToString(System.Globalization.CultureInfo.InvariantCulture), SignalId = "sig." + j.ToString(System.Globalization.CultureInfo.InvariantCulture), State = Tri.True, DeltaLogOdds = 1 + j, Evidence = [new Evidence { TextKey = "x.y", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["t"] = longText } }] })],
            }).ToList();
            var couldNot = Enumerable.Range(0, 80).Select(i => new CouldNotCheck { SignalId = "signal.number_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Reason = UnknownReason.NeedsAdmin, AffectsHypotheses = [.. hypotheses.Take(20).Select(h => h.Id)] }).ToList();
            var attempts = Enumerable.Range(0, 40).Select(i => new AttemptSummary { AttemptId = "01JB6R7X3K9M2N4P5Q6R7S8TG" + (i % 10).ToString(System.Globalization.CultureInfo.InvariantCulture), SessionId = "01JB6R7X3K9M2N4P5Q6R7S8TH0", FixId = "fix.x" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), TitleKey = "fix.x" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".title", Status = AttemptStatus.Verified, Outcome = FixOutcome.NotFixed, Reversibility = Reversibility.Exact, AppliedUtc = FakeClock.DefaultStart, CanUndo = true }).ToList();
            return input with { Analysis = a with { Hypotheses = hypotheses, CouldNotCheck = couldNot }, Attempts = attempts };
        });
        foreach (var language in Languages.All)
        {
            foreach (var max in (int[])[9000, 3000, 800, 120])
            {
                var md = Build(model, language, max).Markdown!;
                Assert.True(md.Length <= max, $"{language}/{max}: {md.Length}");
            }

            var full = Build(model, language).Markdown!;
            Assert.Contains(language == Languages.Turkish ? "tane daha" : "more", full, StringComparison.Ordinal);
            Assert.Contains(language == Languages.Turkish ? "Riot Games ile bağlantılı değildir" : "not affiliated with Riot Games", full, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Turkish_rendering_is_turkish()
    {
        var model = Model(Fixtures.RealtekStutter, language: Languages.Turkish);
        var bundle = Build(model, Languages.Turkish);
        Assert.Contains("lang=\"tr\"", bundle.Html!, StringComparison.Ordinal);
        Assert.Contains("Burada düzeltilebilir", bundle.Html!, StringComparison.Ordinal);
        Assert.Contains("Kullanmadığın kablolu ağ girişi (Realtek 2.5G) takılmalara yol açıyor", bundle.Html!, StringComparison.Ordinal);
        Assert.Contains("Çok olası", bundle.Html!, StringComparison.Ordinal);
        Assert.Contains("Riot Games ile bağlantılı değildir", bundle.Html!, StringComparison.Ordinal);
        Assert.Contains("ortalama 229 FPS", bundle.Html!, StringComparison.Ordinal);
        Assert.Contains("lang=\"en\"", bundle.Html!, StringComparison.Ordinal); // helper section stays English
        Assert.Contains("**Sonuç:** Burada düzeltilebilir", bundle.Markdown!, StringComparison.Ordinal);
        Assert.Contains("Tam rapor: .html dosyasını ekle", bundle.Markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public void No_raw_dotted_keys_reach_the_player()
    {
        var html = Build(Model(Fixtures.Van9003SecureBootOff)).Html!;
        var player = html[..html.IndexOf("<section id=\"helper\"", StringComparison.Ordinal)];
        foreach (Match m in DottedKey().Matches(player))
        {
            Assert.Fail("Raw key in player section: " + m.Value);
        }
    }

    [GeneratedRegex(@"\b(?:health|rule|couldnotcheck|signal|fix|guide|bucket|band|reason|attempt|monitor|report|section)\.[a-z0-9_]+(?:\.[a-z0-9_]+)*\b", RegexOptions.CultureInvariant)]
    private static partial Regex DottedKey();

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Json_round_trips_and_the_hash_matches(string fixture)
    {
        var model = Model(fixture);
        var bundle = Build(model);
        var json = bundle.Json!;
        Assert.Equal(CanonicalHash.OfText(json), bundle.JsonSha256);
        var back = VtdJson.Deserialize<ReportModel>(json);
        Assert.Equal(json, VtdJson.Serialize(back));
        Assert.Equal(RedactionLevel.Share, back.Snapshot.Meta.Redaction.Level);
        Assert.Empty(TestSupport.Redactor().Sweep(json));
    }

    [Fact]
    public void Ai_digest_has_the_schema_the_ids_and_passes_the_sweep()
    {
        var model = Model(Fixtures.RealtekStutter);
        var digest = Build(model).AiDigest!;
        Assert.Contains(PlanSchema.Json.TrimEnd(), digest, StringComparison.Ordinal);
        Assert.Contains("\"$id\": \"urn:vtdiag:schema:plan:1\"", digest, StringComparison.Ordinal);
        Assert.Contains("`forSnapshotId`: `" + FixtureSnapshots.RealtekId + "`", digest, StringComparison.Ordinal);
        Assert.Contains("`kbMin`: `" + FixtureSnapshots.KbVersion + "`", digest, StringComparison.Ordinal);
        foreach (var id in (string[])["fix.net.disable_unused_adapter", "fix.svc.disable_conflict_service", "fix.net.dns_flush", "guide.secureboot.enable", "ask.only_valorant_affected", "ask.league_also_broken", "realtek-2-5gbe-nic-stutter", "net.realtek25.unused"])
        {
            Assert.Contains("`" + id + "`", digest, StringComparison.Ordinal);
        }

        var shareId = TestSupport.Redactor().ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId });
        Assert.Contains("bind `adapter`: adapter, exactly one id, candidates: `" + shareId + "` (Realtek Gaming 2.5GbE Family Controller", digest, StringComparison.Ordinal);
        Assert.Contains("carries the internet connection: never disable", digest, StringComparison.Ordinal);
        Assert.Contains("Never commands, scripts, file paths, registry keys or URLs as actions", digest, StringComparison.Ordinal);
        Assert.Contains("At most 10 steps", digest, StringComparison.Ordinal);
        Assert.DoesNotContain(FixtureSnapshots.RealtekAdapterInstanceId, digest, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TestSupport.Redactor().Sweep(VtdJson.Serialize(digest)));
    }

    [Fact]
    public void Ai_digest_example_plan_is_a_valid_plan_for_this_pc()
    {
        var model = Model(Fixtures.RealtekStutter);
        var digest = Build(model).AiDigest!;
        var start = digest.LastIndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length;
        var end = digest.IndexOf("\n```", start, StringComparison.Ordinal);
        var plan = digest[start..end];
        var validation = new PlanValidator(TestSupport.Redactor()).Validate(plan, TestSupport.Snapshot(Fixtures.RealtekStutter), FakeKnowledgeBase.Sample(), FakeClock.DefaultStart.AddHours(1));
        Assert.True(validation.Valid, string.Join(", ", validation.Issues.Select(i => i.Code + i.Path)));
        Assert.All(validation.Steps, s => Assert.Equal(Contracts.Assist.PlanStepStatus.Ok, s.Status));
    }

    [Fact]
    public void Kb_text_is_used_and_missing_keys_are_humanized()
    {
        var t = new ReportText(FakeKnowledgeBase.Sample(), Languages.English);
        Assert.Equal("Turn off the network port you're not using", t.Kb("fix.disable_adapter.title"));
        Assert.Equal("Secure Boot keys", t.Kb("health.secure_boot_keys"));
        Assert.Equal("Something brand new", t.Kb("health.something_brand_new"));
        Assert.Equal("Uninstall nahimic", t.Kb("guide.apps.uninstall_nahimic"));
        Assert.Equal("Realtek 2 5gbe nic stutter", t.Kb(null, null, "realtek-2-5gbe-nic-stutter"));
    }

    private static string ReportSectionOf(string html, string id)
    {
        var start = html.IndexOf("<section id=\"" + id + "\"", StringComparison.Ordinal);
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }
}
