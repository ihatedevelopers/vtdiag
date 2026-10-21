using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>
/// DESIGN.md: "Canary tests plant tokens in fixtures." Every canary is planted into the free-text-ish fields of a
/// fixture snapshot, its analysis and a monitor summary; no export kind may contain any of them. When something slips
/// past redaction the export is blocked, the hit names the section, and leaving that section out works.
/// </summary>
public sealed partial class CanaryTests
{
    private static readonly string All = string.Join(" ", CanaryTokens.All);

    public static TheoryData<string, ExportKind> Cases()
    {
        var data = new TheoryData<string, ExportKind>();
        foreach (var fixture in Fixtures.Names)
        {
            foreach (var kind in Enum.GetValues<ExportKind>().Where(k => k != ExportKind.DebugBundle))
            {
                data.Add(fixture, kind);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void No_canary_survives_any_share_export(string fixture, ExportKind kind)
    {
        var input = Planted(fixture);
        foreach (var language in Languages.All)
        {
            var result = TestSupport.Exporter(TestSupport.EngineRedactor()).Export(new ExportRequest { Kind = kind, Language = language }, input);
            Assert.True(result.Status == ExportStatus.Ok, $"{kind}/{language}: {result.Status} " + string.Join(", ", result.SweepHits.Select(h => h.Rule + "@" + h.Path)));
            Assert.NotNull(result.Sha256);
            Assert.NotNull(result.Redaction);
            Assert.Equal(RedactionLevel.Share, result.Redaction!.Level);
            foreach (var (name, text) in TestSupport.Outputs(result))
            {
                TestSupport.AssertNoCanary(kind + "/" + language + "/" + name, text);
            }
        }
    }

    [Theory]
    [InlineData(ExportKind.ReportHtml, "^VTDiag-Report-20261021-[0-9A-Z]{8}\\.html$")]
    [InlineData(ExportKind.ReportMarkdown, "^VTDiag-Report-20261021-[0-9A-Z]{8}\\.md$")]
    [InlineData(ExportKind.ReportJson, "^VTDiag-Report-20261021-[0-9A-Z]{8}\\.json$")]
    [InlineData(ExportKind.ScanBundle, "^VTDiag-[0-9A-Z]{8}\\.vtdscan$")]
    [InlineData(ExportKind.AiDigest, "^VTDiag-AI-20261021-[0-9A-Z]{8}\\.md$")]
    public void File_names_follow_the_pattern(ExportKind kind, string pattern)
    {
        var result = TestSupport.Exporter(TestSupport.Redactor()).Export(new ExportRequest { Kind = kind }, TestSupport.Input(Fixtures.RealtekStutter));
        Assert.Equal(ExportStatus.Ok, result.Status);
        Assert.Matches(new Regex(pattern, RegexOptions.CultureInvariant), result.FileName!);
    }

    [Theory]
    [InlineData(ExportKind.ReportHtml)]
    [InlineData(ExportKind.ReportMarkdown)]
    [InlineData(ExportKind.ReportJson)]
    [InlineData(ExportKind.ScanBundle)]
    [InlineData(ExportKind.AiDigest)]
    public void A_slip_in_a_snapshot_section_blocks_and_names_it_and_leaving_it_out_works(ExportKind kind)
    {
        // The plain redactor does not know the computer name, and service display names are Sensitivity.None.
        var input = TestSupport.Input(Fixtures.NahimicBlackScreen);
        var services = input.Snapshot.Services.Value.ToList();
        services[0] = services[0] with { DisplayName = "Helper on " + CanaryTokens.ComputerName };
        input = input with { Snapshot = input.Snapshot with { Services = Fact.Known<IReadOnlyList<ServiceInfo>>(services) } };

        var exporter = TestSupport.Exporter(TestSupport.Redactor());
        var blocked = exporter.Export(new ExportRequest { Kind = kind }, input);
        Assert.Equal(ExportStatus.BlockedByRedaction, blocked.Status);
        Assert.Null(blocked.TextContent);
        Assert.Null(blocked.Base64Content);
        var hit = Assert.Single(blocked.SweepHits);
        Assert.Equal("canary", hit.Rule);
        Assert.Equal("services", hit.Section);
        Assert.Equal("/snapshot/services/value/0/displayName", hit.Path);
        Assert.Contains(hit.Section, ShareExporter.ExcludableSections);

        var retry = exporter.Export(new ExportRequest { Kind = kind, ExcludedSections = [hit.Section!] }, input);
        Assert.Equal(ExportStatus.Ok, retry.Status);
        Assert.Contains("services", retry.Redaction!.ExcludedSections);
        foreach (var (name, text) in TestSupport.Outputs(retry))
        {
            TestSupport.AssertNoCanary(name, text);
        }
    }

    [Fact]
    public void A_slip_in_the_analysis_blocks_and_leaving_analysis_out_works()
    {
        var input = TestSupport.Input(Fixtures.RealtekStutter);
        var analysis = input.Analysis!;
        input = input with { Analysis = analysis with { Hypotheses = [analysis.Hypotheses[0] with { TitleKey = "rule." + CanaryTokens.ComputerName }] } };
        var exporter = TestSupport.Exporter(TestSupport.Redactor());
        var blocked = exporter.Export(new ExportRequest { Kind = ExportKind.ReportHtml }, input);
        Assert.Equal(ExportStatus.BlockedByRedaction, blocked.Status);
        Assert.All(blocked.SweepHits, h => Assert.Equal("analysis", h.Section));

        var retry = exporter.Export(new ExportRequest { Kind = ExportKind.ReportHtml, ExcludedSections = ["analysis"] }, input);
        Assert.Equal(ExportStatus.Ok, retry.Status);
        TestSupport.AssertNoCanary("html", retry.TextContent!);
    }

    [Fact]
    public void A_model_that_bypassed_the_redactor_is_blocked_by_the_builder()
    {
        var input = Planted(Fixtures.RealtekStutter);
        var model = new ReportModel
        {
            SchemaVersion = ReportModel.CurrentSchemaVersion,
            Meta = new ReportMeta { ReportId = "01JB6R7X3K9M2N4P5Q6R7S8TC2", CreatedUtc = FakeClock.DefaultStart, AppVersion = "0.1.0", KbVersion = FixtureSnapshots.KbVersion, Language = "en", MachineKey = input.Snapshot.Meta.MachineKey },
            Snapshot = input.Snapshot,
            Analysis = input.Analysis,
            Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = 1 },
        };
        var redactor = TestSupport.Redactor();
        var bundle = new ReportBuilder(FakeKnowledgeBase.Sample(), redactor).Build(model, new ReportOptions { Formats = [ReportFormat.Html, ReportFormat.Markdown, ReportFormat.Json, ReportFormat.AiDigest] });
        Assert.True(bundle.Blocked);
        Assert.Null(bundle.Html);
        Assert.Null(bundle.Markdown);
        Assert.Null(bundle.Json);
        Assert.Null(bundle.AiDigest);
        Assert.Contains(bundle.SweepHits, h => h.Section == "apps");
        Assert.Contains(bundle.SweepHits, h => h.Section == "analysis");
        Assert.Contains(bundle.SweepHits, h => h.Rule == "jwt");
        Assert.Contains(bundle.SweepHits, h => h.Rule == "sid");
        Assert.All(bundle.SweepHits, h => Assert.DoesNotContain("canary", h.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Raw_planted_json_hits_every_rule()
    {
        var json = Contracts.Serialization.VtdJson.Serialize(Planted(Fixtures.RealtekStutter).Snapshot);
        var rules = TestSupport.Redactor().Sweep(json).Select(h => h.Rule).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in (string[])["canary", "jwt", "email", "sid", "userprofile", "auth_token", "id_next_to_key"])
        {
            Assert.Contains(rule, rules);
        }
    }

    [Fact]
    public void The_codec_refuses_an_unredacted_snapshot_even_when_stamped_share()
    {
        var input = Planted(Fixtures.RealtekStutter);
        var stamped = input.Snapshot with { Meta = input.Snapshot.Meta with { Redaction = new RedactionState { Level = RedactionLevel.Share, RulesVersion = 1 } } };
        var codec = new ScanBundleCodec(TestSupport.Redactor());
        var ex = Assert.Throws<ScanBundleException>(() => codec.Write(new ScanBundle { Manifest = ScanBundleCodecTests.Manifest(stamped), Snapshot = stamped }));
        Assert.Equal("sweep_blocked", ex.Code);
        Assert.Contains(ex.SweepHits, h => h.Section == "apps");
    }

    /// <summary>A fixture input with every canary planted in free-text-ish fields.</summary>
    internal static ShareExportInput Planted(string fixture)
    {
        var input = TestSupport.Input(fixture);
        var s = input.Snapshot;
        var profile = CanaryTokens.UserProfilePath;

        var devices = s.Devices.Value.Select((d, i) => i == 0 ? d with { FriendlyName = d.FriendlyName + " " + CanaryTokens.Jwt + " " + CanaryTokens.UserName } : d).ToList();
        var apps = s.Apps.Value.Select((a, i) => i == 0 ? a with { DisplayName = a.DisplayName + " (" + CanaryTokens.UserName + ", " + CanaryTokens.Email + ")", InstallLocation = profile } : a).ToList();
        var startup = s.Startup.Value.Select(x => x with { Name = CanaryTokens.ComputerName + " helper " + CanaryTokens.Puuid, TargetPath = profile + "\\Update.exe" }).ToList();
        var drivers = s.Drivers.Value.Select((d, i) => i == 0 ? d with { Path = profile + "\\drv.sys" } : d).ToList();
        var services = s.Services.Value.Select((x, i) => i == 0 ? x with { ImagePath = profile + "\\svc.exe" } : x).ToList();
        var adapters = s.Network.Adapters.Value.Select((a, i) => i == 0 ? a with { Alias = All, InterfaceGuid = CanaryTokens.ComputerName } : a).ToList();
        IReadOnlyDictionary<string, string> sample = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["param1"] = CanaryTokens.RemotingAuthToken,
            ["user"] = CanaryTokens.Sid,
            ["note"] = CanaryTokens.Puuid,
            ["path"] = profile,
            ["who"] = CanaryTokens.UserName + "@" + CanaryTokens.ComputerName,
            [CanaryTokens.Email] = "key-is-an-email",
        };
        var hits = s.Events.Hits.TryGetValue(out var h) && h.Count > 0
            ? h.Select((x, i) => i == 0 ? x with { Samples = [.. x.Samples, sample] } : x).ToList()
            : [new EventHit { QueryId = "q", Log = "System", Provider = "p", EventId = 1, Count = 1, FirstUtc = s.Meta.CreatedUtc, LastUtc = s.Meta.CreatedUtc, Samples = [sample] }];
        var pack = s.Pack.Select(p => p.Data.TryGetValue(out var data) ? p with { Data = Fact.Known(data with { Items = [.. data.Items, sample] }) } : p).ToList();
        if (pack.Count == 0)
        {
            pack.Add(new PackFactResult { CollectorId = "planted", Data = Fact.Known(new PackFactData { Count = 1, Items = [sample] }) });
        }

        var endpoints = s.Audio.Endpoints.Value.Select(e => e with { Name = CanaryTokens.UserName + "'s AirPods" }).ToList();
        var riot = s.Riot with
        {
            Client = s.Riot.Client.TryGetValue(out var client) ? Fact.Known(client with { Path = profile + "\\Riot Client" }) : s.Riot.Client,
            CompatLayers = Fact.Known<IReadOnlyList<CompatLayerEntry>>([new CompatLayerEntry { ImageName = "VALORANT.exe", ExePath = profile + "\\VALORANT.exe", Hive = AppHive.Hkcu, Layers = ["RUNASADMIN"] }]),
        };
        var network = s.Network with
        {
            Adapters = Fact.Known<IReadOnlyList<NetAdapter>>(adapters),
            RiotFirewallRules = Fact.Known<IReadOnlyList<FirewallRuleInfo>>([new FirewallRuleInfo { Name = "VALORANT " + profile + " " + CanaryTokens.Jwt, ProgramImageName = "VALORANT-Win64-Shipping.exe", Direction = FirewallDirection.In, Action = FirewallAction.Allow, Enabled = true }]),
            Wlan = s.Network.Wlan.TryGetValue(out var wlan) ? Fact.Known(wlan with { Ssid = CanaryTokens.ComputerName }) : s.Network.Wlan,
        };
        var snapshot = s with
        {
            Devices = Fact.Known<IReadOnlyList<DeviceInfo>>(devices),
            Apps = Fact.Known<IReadOnlyList<InstalledApp>>(apps),
            Startup = Fact.Known<IReadOnlyList<StartupItem>>(startup),
            Drivers = Fact.Known<IReadOnlyList<LoadedDriver>>(drivers),
            Services = Fact.Known<IReadOnlyList<ServiceInfo>>(services),
            Network = network,
            Events = s.Events with { Hits = Fact.Known<IReadOnlyList<EventHit>>(hits) },
            Pack = pack,
            Audio = s.Audio with { Endpoints = Fact.Known<IReadOnlyList<AudioEndpoint>>(endpoints) },
            Riot = riot,
        };

        var evidence = new Evidence
        {
            TextKey = "rule.realtek25.ev.resets",
            Args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = "14",
                ["path"] = profile,
                ["token"] = CanaryTokens.Jwt,
                ["user"] = CanaryTokens.UserName,
                ["host"] = CanaryTokens.ComputerName,
                ["mail"] = CanaryTokens.Email,
                ["sid"] = CanaryTokens.Sid,
                ["id"] = CanaryTokens.Puuid,
                ["auth"] = CanaryTokens.RemotingAuthToken,
            },
        };
        var analysis = CannedAnalyses.For(snapshot);
        var file = new EntityRef { Kind = EntityKind.File, Id = profile + "\\x.exe", DisplayName = "x.exe of " + CanaryTokens.UserName + " on " + CanaryTokens.ComputerName };
        var hypotheses = analysis.Hypotheses.Count == 0
            ? [new RankedHypothesis { Id = "planted", Category = ProblemCategory.Other, TitleKey = "rule.planted.title", Posterior = 0.3, LogOdds = -0.8, PriorLogOdds = -2, Band = ConfidenceBand.Possible, Bucket = HonestyBucket.Unknown }]
            : analysis.Hypotheses;
        hypotheses = [.. hypotheses.Select((x, i) => i == 0
            ? x with
            {
                Contributions = [.. x.Contributions, new EvidenceContribution { EvidenceId = "planted", SignalId = "planted.signal", State = Tri.True, DeltaLogOdds = 5, TextKey = "rule.realtek25.ev.resets", Evidence = [evidence] }],
                Entities = [.. x.Entities, file],
            }
            : x)];
        analysis = analysis with
        {
            Hypotheses = hypotheses,
            Anomalies = [new AnomalyFinding { Kind = AnomalyKind.UnknownFaultingModule, Subject = "evil.dll " + CanaryTokens.Jwt, Evidence = [evidence] }],
            CouldNotCheck = [.. analysis.CouldNotCheck, new CouldNotCheck { SignalId = "planted.signal", Reason = UnknownReason.Failed, TextKey = "couldnotcheck.failed" }],
        };

        var monitor = input.Monitor! with
        {
            Detectors = [.. input.Monitor!.Detectors, new SignalResult { Id = "mon.planted", State = Tri.True, Evidence = [evidence] }],
            Correlations = [.. input.Monitor.Correlations, new CorrelationResult { SourceId = "process:" + profile + "\\Discord.exe", Hits = 3, HitRatio = 0.2 }],
        };

        return input with
        {
            Snapshot = snapshot,
            Analysis = analysis,
            Monitor = monitor,
            Attempts = [new Contracts.Fixes.AttemptSummary { AttemptId = "01JB6R7X3K9M2N4P5Q6R7S8TD3", SessionId = "01JB6R7X3K9M2N4P5Q6R7S8TD4", FixId = "fix.net.disable_unused_adapter", TitleKey = "fix.disable_adapter.title", Status = Contracts.Fixes.AttemptStatus.Verified, Outcome = Contracts.Fixes.FixOutcome.NotFixed, Reversibility = Contracts.Fixes.Reversibility.Exact, AppliedUtc = FakeClock.DefaultStart, CanUndo = true }],
        };
    }
}
