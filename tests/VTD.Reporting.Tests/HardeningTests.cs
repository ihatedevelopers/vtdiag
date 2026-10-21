using System.IO.Compression;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>Regression tests for the independent security review (leaks, plan bypasses, hostile input).</summary>
public sealed class HardeningTests
{
    private static ScanSnapshot WithSample(ScanSnapshot s, IReadOnlyDictionary<string, string> sample)
    {
        var hits = s.Events.Hits.Value.Select((h, i) => i == 0 ? h with { Samples = [.. h.Samples, sample] } : h).ToList();
        return s with { Events = s.Events with { Hits = Fact.Known<IReadOnlyList<EventHit>>(hits) } };
    }

    private static string ShareJson(ScanSnapshot s, Redactor? redactor = null)
        => VtdJson.Serialize((redactor ?? TestSupport.Redactor()).Redact(s, RedactionPolicy.Share).Value);

    [Fact]
    public void Ssid_mac_and_ip_values_do_not_survive_in_free_text()
    {
        var s = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var sample = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["note1"] = "joined HomeNet-5G at 192.168.1.34 via D8F2CA4F8C46",
            ["SSID"] = "SomeOtherNet_Ayse",
            ["PeerMac"] = "AA-BB-CC-11-22-33",
            ["LocalIp"] = "10.20.30.40",
            ["free"] = "neighbour 3c:22:fb:01:02:03 seen",
        };
        var json = ShareJson(WithSample(s, sample));
        foreach (var leaked in (string[])["HomeNet-5G", "192.168.1.34", "D8F2CA4F8C46", "SomeOtherNet_Ayse", "AA-BB-CC-11-22-33", "10.20.30.40", "3c:22:fb:01:02:03"])
        {
            Assert.DoesNotContain(leaked, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(TestSupport.Redactor().Sweep(json));
        var raw = VtdJson.Serialize(sample.ToDictionary() as IReadOnlyDictionary<string, string>);
        var rules = TestSupport.Redactor().Sweep(raw).Select(h => h.Rule).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("mac", rules);
        Assert.Contains("network_id", rules);
    }

    [Theory]
    [InlineData("%SystemDrive%\\Users\\alice\\AppData\\Local\\x.exe")]
    [InlineData("\\\\localhost\\c$\\Users\\alice\\x")]
    [InlineData("\\\\NAS\\Users\\alice")]
    [InlineData("..\\Users\\alice\\x")]
    [InlineData("\\\\?\\C:\\Users\\alice\\x")]
    [InlineData("c:/users/alice/x")]
    [InlineData("C:\\Users\\bob;C:\\Users\\alice\\bin")]
    [InlineData("C:\\Users\\bob to C:\\Users\\alice")]
    [InlineData("\"C:\\Users\\alice\" --flag")]
    [InlineData("D:\\Documents and Settings\\alice\\x")]
    public void Profile_path_variants_are_scrubbed_and_detected(string path)
    {
        var r = TestSupport.Redactor();
        Assert.Contains("userprofile", RedactionSweep.Detect(path));
        var share = r.RedactText(path, RedactionPolicy.Share);
        Assert.DoesNotContain("alice", share, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bob", share, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(RedactionSweep.Detect(share));

        var s = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var startup = s.Startup.Value.Select(x => x with { TargetPath = path }).ToList();
        var json = ShareJson(s with { Startup = Fact.Known<IReadOnlyList<StartupItem>>(startup) });
        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_paths_with_a_users_key_are_not_profile_paths()
        => Assert.Empty(RedactionSweep.Detect("HKLM\\SAM\\SAM\\Domains\\Account\\Users\\000001F4"));

    [Theory]
    [InlineData("Çağrı", "ÇAĞRI kullanıcısı")]
    [InlineData("Işık", "IŞIK ve ışık")]
    [InlineData("ismail", "İSMAİL İsmail ISMAIL")]
    public void Turkish_case_variants_of_a_name_are_removed(string name, string text)
    {
        var r = new Redactor(TestSupport.Salt, [name]);
        var share = r.RedactText(text, RedactionPolicy.Share);
        foreach (var word in text.Split(' ').Where(w => w.Length >= 4 && !w.EndsWith("sı", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(word, share, StringComparison.Ordinal);
        }

        Assert.Contains("[id]", share, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token=abcdef123456", "abcdef123456")]
    [InlineData("secret: s3cr3tvalue", "s3cr3tvalue")]
    [InlineData("sessionid=0123456789", "0123456789")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("Riot Client:12345:54321:Abc123SecretPass:https", "Abc123SecretPass")]
    [InlineData("Password=My Secret Phrase", "Secret Phrase")]
    public void More_secret_shapes_are_scrubbed_at_local_and_share_level(string text, string secret)
    {
        var r = TestSupport.Redactor();
        Assert.Contains("auth_token", RedactionSweep.Detect(text));
        foreach (var level in (RedactionLevel[])[RedactionLevel.Local, RedactionLevel.Share])
        {
            var scrubbed = r.RedactText(text, new RedactionPolicy { Level = level });
            Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
            Assert.Empty(RedactionSweep.Detect(scrubbed));
        }
    }

    [Theory]
    [InlineData("Microsoft Basic Render Driver")]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("machine key mk1:7f3c2a9e41d05b6c")]
    [InlineData("1.17.9.5")]
    [InlineData("23.110.0.5")]
    [InlineData("2026-10-21T18:02:11+00:00")]
    [InlineData("{0.0.0.00000000}.{6a1f3b2e-9c47-4d1a-8f20-3b5e7c9d1a42}")]
    public void New_rules_do_not_hit_legit_data(string text)
        => Assert.Empty(RedactionSweep.Detect(text));

    [Fact]
    public void Escaped_placeholders_in_markdown_do_not_block_the_export()
    {
        var input = TestSupport.Input(Fixtures.RealtekStutter);
        var analysis = input.Analysis!;
        var h = analysis.Hypotheses[0];
        var contributions = h.Contributions.Select(c => c.EvidenceId == "resets"
            ? c with { Evidence = [c.Evidence[0] with { Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["count"] = "password: hunter2 and puuid=abc and CommandLine=x.exe -a" } }] }
            : c).ToList();
        input = input with { Analysis = analysis with { Hypotheses = [h with { Contributions = contributions }] } };
        foreach (var kind in (ExportKind[])[ExportKind.ReportMarkdown, ExportKind.ReportHtml, ExportKind.AiDigest])
        {
            var result = TestSupport.Exporter(TestSupport.Redactor()).Export(new ExportRequest { Kind = kind }, input);
            Assert.True(result.Status == ExportStatus.Ok, kind + ": " + string.Join(", ", result.SweepHits.Select(x => x.Rule + "@" + x.Path)));
            Assert.DoesNotContain("hunter2", result.TextContent!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Html_and_markdown_are_swept_as_a_reader_sees_them()
    {
        // A renderer bug that let a raw path through would be entity-encoded ("C&#58;\Users") in HTML; the sweep decodes first.
        var html = "<p>" + HtmlRenderer.E("C:\\Users\\alice\\x") + "</p>";
        Assert.Contains("&#58;", html, StringComparison.Ordinal);
        Assert.Contains("userprofile", RedactionSweep.Detect(html)); // the bare root alternative still matches
        Assert.Contains("userprofile", RedactionSweep.Detect(System.Net.WebUtility.HtmlDecode(html)));
        Assert.Empty(RedactionSweep.Detect(HtmlRenderer.E("access_token=[redacted]")));
        var md = MarkdownRenderer.Esc("access_token=abcdef123456");
        Assert.Contains("\\_", md, StringComparison.Ordinal);
        Assert.Contains("auth_token", RedactionSweep.Detect(MarkdownRenderer.Unescape(md)));
    }

    [Fact]
    public void A_model_that_is_not_share_level_is_blocked_by_the_builder()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var model = new ReportModel
        {
            SchemaVersion = 1,
            Meta = new ReportMeta { ReportId = "01JB6R7X3K9M2N4P5Q6R7S8TJ1", CreatedUtc = FakeClock.DefaultStart, AppVersion = "0.1.0", KbVersion = "2026.10.21.1", Language = "en", MachineKey = "mk1:x" },
            Snapshot = snapshot,
            Redaction = new RedactionManifest { Level = RedactionLevel.Local, RulesVersion = 1 },
        };
        var bundle = new ReportBuilder(FakeKnowledgeBase.Sample(), TestSupport.Redactor()).Build(model, new ReportOptions());
        Assert.True(bundle.Blocked);
        Assert.Contains(bundle.SweepHits, h => h.Rule == "not_share_level" && h.Section == "meta");
    }

    // ------------------------------------------------------------------ plans

    private static readonly DateTimeOffset Now = FakeClock.DefaultStart.AddHours(1);

    private static string PlanJson(params PlanStep[] steps) => VtdJson.Serialize(new FixPlan
    {
        Schema = 1,
        PlanId = "01JB7Z6Q4N8V2X3Y5W7T9R1M0K",
        ForSnapshotId = FixtureSnapshots.RealtekId,
        KbMin = "2026.10.1",
        CreatedUtc = FakeClock.DefaultStart,
        ExpiresUtc = FakeClock.DefaultStart.AddDays(2),
        Author = new PlanAuthor { Name = "helper" },
        Steps = steps,
    });

    private static PlanFixStep Bind(string fixId, string name, params string[] ids)
        => new() { FixId = fixId, Bind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [name] = ids } };

    [Theory]
    [InlineData("vgc")]
    [InlineData("vgk")]
    public void Template_fixes_cannot_target_vanguard(string service)
    {
        var v = new PlanValidator(TestSupport.Redactor()).Validate(PlanJson(Bind("fix.svc.disable_conflict_service", "svc", service)), TestSupport.Snapshot(Fixtures.RealtekStutter), FakeKnowledgeBase.Sample(), Now);
        Assert.Equal("plan.step.protected_target", v.Steps[0].ReasonKey);
    }

    [Fact]
    public void The_default_route_adapter_is_a_protected_target()
    {
        var wifi = TestSupport.Redactor().ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.WifiAdapterInstanceId });
        var v = new PlanValidator(TestSupport.Redactor()).Validate(PlanJson(Bind("fix.net.disable_unused_adapter", "adapter", wifi)), TestSupport.Snapshot(Fixtures.RealtekStutter), FakeKnowledgeBase.Sample(), Now);
        Assert.Equal("plan.step.protected_target", v.Steps[0].ReasonKey);
    }

    [Fact]
    public void With_the_fresh_analysis_a_non_template_fix_binds_only_what_the_analysis_offers()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var adapters = snapshot.Network.Adapters.Value.ToList();
        var spare = adapters[0] with { InstanceId = "PCI\\VEN_10EC&DEV_8168&SUBSYS_00000000&REV_15\\01000000FFFFFFFF00", IsDefaultRoute = false };
        snapshot = snapshot with { Network = snapshot.Network with { Adapters = Fact.Known<IReadOnlyList<NetAdapter>>([.. adapters, spare]) } };
        var r = TestSupport.Redactor();
        var spareShare = r.ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = spare.InstanceId });
        var realtekShare = r.ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId });
        var analysis = CannedAnalyses.For(snapshot);
        var validator = new PlanValidator(r);

        var offered = validator.Validate(PlanJson(Bind("fix.net.disable_unused_adapter", "adapter", realtekShare)), snapshot, FakeKnowledgeBase.Sample(), Now, analysis);
        Assert.Equal(PlanStepStatus.Ok, offered.Steps[0].Status);
        var other = validator.Validate(PlanJson(Bind("fix.net.disable_unused_adapter", "adapter", spareShare)), snapshot, FakeKnowledgeBase.Sample(), Now, analysis);
        Assert.Equal("plan.step.binding_not_suggested", other.Steps[0].ReasonKey);
        // Without an analysis the interface call cannot check FromSignal; the step resolves (the engine's preview re-checks).
        Assert.Equal(PlanStepStatus.Ok, validator.Validate(PlanJson(Bind("fix.net.disable_unused_adapter", "adapter", spareShare)), snapshot, FakeKnowledgeBase.Sample(), Now).Steps[0].Status);
    }

    [Theory]
    [InlineData("\"bind\":{\"svc\":null}")]
    [InlineData("\"bind\":{\"svc\":[null]}")]
    [InlineData("\"bind\":null")]
    public void Null_bindings_never_throw(string bind)
    {
        var json = PlanJson(Bind("fix.svc.disable_conflict_service", "svc", "NahimicService"));
        json = json.Replace("\"bind\":{\"svc\":[\"NahimicService\"]}", bind, StringComparison.Ordinal);
        Assert.Contains(bind, json, StringComparison.Ordinal);
        var v = new PlanValidator(TestSupport.Redactor()).Validate(json, TestSupport.Snapshot(Fixtures.NahimicBlackScreen), FakeKnowledgeBase.Sample(), Now);
        Assert.True(!v.Valid || v.Steps[0].Status == PlanStepStatus.Greyed);
    }

    [Fact]
    public void Null_steps_never_throw()
    {
        var json = PlanJson(new PlanMonitorStep { Minutes = 5 }).Replace("[{\"kind\":\"monitor\",\"minutes\":5}]", "[null]", StringComparison.Ordinal);
        var v = new PlanValidator(TestSupport.Redactor()).Validate(json, TestSupport.Snapshot(Fixtures.RealtekStutter), FakeKnowledgeBase.Sample(), Now);
        Assert.False(v.Valid);
    }

    [Fact]
    public void Too_many_ids_in_one_binding_greys_the_step()
    {
        var ids = Enumerable.Range(0, PlanValidator.MaxIdsPerBinding + 1).Select(i => "Svc" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var v = new PlanValidator(TestSupport.Redactor()).Validate(PlanJson(Bind("fix.svc.disable_conflict_service", "svc", ids)), TestSupport.Snapshot(Fixtures.NahimicBlackScreen), FakeKnowledgeBase.Sample(), Now);
        Assert.Equal("plan.step.binding_count", v.Steps[0].ReasonKey);
    }

    [Fact]
    public void Entity_ids_that_contain_an_identifier_still_resolve()
    {
        // A registry value under the adapter's interface GUID (an Identifier): the share id hashes the GUID in place, and
        // the validator recomputes the same hash from the fresh snapshot.
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var guid = snapshot.Network.Adapters.Value[0].InterfaceGuid!;
        var entity = new EntityRef { Kind = EntityKind.RegistryValue, Id = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + guid + "|TcpAckFrequency" };
        var pack = snapshot.Pack.Select((p, i) => i == 0 ? p with { Data = Fact.Known(p.Data.Value with { Entities = [entity] }) } : p).ToList();
        snapshot = snapshot with { Pack = pack };
        var r = TestSupport.Redactor();
        var shared = r.Redact(snapshot, RedactionPolicy.Share).Value;
        var shareId = shared.Pack[0].Data.Value.Entities.Single().Id;
        Assert.DoesNotContain(guid, shareId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\Interfaces\\h:", shareId, StringComparison.Ordinal);

        var fix = new FixDef
        {
            Id = "fix.reg.template_value",
            TitleKey = "x",
            ExplainKey = "x",
            UndoExplainKey = "x",
            Risk = RiskLevel.Low,
            Reversibility = Reversibility.Exact,
            Template = true,
            Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["value"] = new BindDef { Kind = EntityKind.RegistryValue } },
        };
        var kb = new FakeKnowledgeBase(FakeKnowledgeBase.Sample().Version) { Fixes = [fix] };
        var v = new PlanValidator(r).Validate(PlanJson(Bind(fix.Id, "value", shareId)), snapshot, kb, Now);
        Assert.Equal(PlanStepStatus.Ok, v.Steps[0].Status);
        Assert.Equal(entity.Id, v.Steps[0].ResolvedBinding!.Entities["value"].Single().Id);
    }

    // ------------------------------------------------------------------ bundles

    [Fact]
    public void A_corrupt_central_directory_is_a_bundle_exception()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("manifest.json").Open();
            s.Write(Encoding.UTF8.GetBytes("{}"));
        }

        var bytes = ms.ToArray();
        var eocd = bytes.Length - 22;
        bytes[eocd + 16] ^= 0x55; // central directory offset now points into garbage
        var ex = Assert.Throws<ScanBundleException>(() => new ScanBundleCodec(TestSupport.Redactor()).Read(new MemoryStream(bytes)));
        Assert.Contains(ex.Code, (string[])["format", "bad_entry"]);
    }

    [Fact]
    public void A_declared_entry_count_over_the_cap_is_refused_before_unzipping()
    {
        var bytes = new byte[64];
        var eocd = bytes.Length - 22;
        bytes[eocd] = 0x50;
        bytes[eocd + 1] = 0x4B;
        bytes[eocd + 2] = 0x05;
        bytes[eocd + 3] = 0x06;
        bytes[eocd + 10] = 0x40; // 200,000 declared entries would not fit in two bytes; 0x0D40 = 3392 > 64
        bytes[eocd + 11] = 0x0D;
        var ex = Assert.Throws<ScanBundleException>(() => new ScanBundleCodec(TestSupport.Redactor()).Read(new MemoryStream(bytes)));
        Assert.Equal("too_large", ex.Code);
    }
}
