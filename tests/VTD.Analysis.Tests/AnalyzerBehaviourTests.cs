using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Analysis.Tests;

/// <summary>The ranking rules of DESIGN.md section 2, one behaviour per test.</summary>
public sealed class AnalyzerBehaviourTests
{
    private static readonly Analyzer Engine = new(TestKb.Real);

    private static AnalysisInput Input(ScanSnapshot s, IntakeAnswers? intake = null, IReadOnlyList<FixAttempt>? history = null, int asked = 0, DateTimeOffset? asOf = null) => new()
    {
        AnalysisId = "01JB6R7X3K9M2N4P5Q6R7S8TA1",
        Snapshot = s,
        Intake = intake ?? IntakeAnswers.Empty,
        History = history ?? [],
        QuestionsAsked = asked,
        AsOfUtc = asOf ?? s.Meta.CreatedUtc,
    };

    [Fact]
    public void Analysis_is_deterministic_and_serializable()
    {
        var input = Input(Fixtures.Load(Fixtures.RealtekStutter), new IntakeAnswers { Symptoms = ["stutter"] });
        var a = VtdJson.Serialize(Engine.Analyze(input));
        var b = VtdJson.Serialize(new Analyzer(TestKb.Real).Analyze(input));
        Assert.Equal(a, b);
        var result = VtdJson.Deserialize<AnalysisResult>(a);
        Assert.Equal(input.AnalysisId, result.AnalysisId);
        Assert.Equal(input.AsOfUtc, result.CreatedUtc);
        Assert.Equal(TestKb.Real.Version.Version, result.KbVersion);
    }

    [Fact]
    public void Realtek_fixture_binds_the_unused_adapter_and_keeps_wifi()
    {
        var r = Engine.Analyze(Input(Fixtures.Load(Fixtures.RealtekStutter), new IntakeAnswers { Symptoms = ["stutter"] }));
        var top = r.Hypotheses[0];
        Assert.Equal("realtek-2-5gbe-nic-stutter", top.Id);
        Assert.Equal(HonestyBucket.FixableHere, top.Bucket);
        var p = r.Proposals.First();
        Assert.Equal("fix.net.disable_unused_adapter", p.FixId);
        Assert.True(p.Available);
        var bound = Assert.Single(p.Binding.Entities["adapter"]);
        Assert.Equal(FixtureSnapshots.RealtekAdapterInstanceId, bound.Id);
        Assert.DoesNotContain(r.Proposals.SelectMany(x => x.Binding.Entities.Values.SelectMany(e => e)), e => e.Id == FixtureSnapshots.WifiAdapterInstanceId);
        Assert.Equal(HonestyBucket.FixableHere, r.PrimaryBucket);
        Assert.False(r.UnknownCause.Leading);
        Assert.True(r.UnknownCause.Probability >= RankingConstants.UnknownCauseFloor);
    }

    [Fact]
    public void Nahimic_fixture_disables_only_the_nahimic_service()
    {
        var r = Engine.Analyze(Input(Fixtures.Load(Fixtures.NahimicBlackScreen), new IntakeAnswers { Symptoms = ["launch_black_screen"] }));
        Assert.Equal("nahimic-audio-service-launch-hang", r.Hypotheses[0].Id);
        Assert.True(r.Hypotheses[0].WaveLogOdds > 0); // Sep-Dec 2026 wave
        var p = r.Proposals.First(x => x.FixId == "fix.svc.disable_conflict_service");
        Assert.Equal(["NahimicService"], p.Binding.Entities["svc"].Select(e => e.Id));
        Assert.Contains(r.RuledOut, o => o.HypothesisId == "conflict-nahimic" && o.Reason == RuledOutReason.Superseded);
        Assert.Contains(r.Guides, g => g.GuideId == "guide.apps.uninstall_nahimic");
    }

    [Fact]
    public void Limited_mode_secure_boot_is_guided_and_unknown_is_not_false()
    {
        var r = Engine.Analyze(Input(Fixtures.Load(Fixtures.Van9003SecureBootOff), new IntakeAnswers { Symptoms = ["error_code"] }));
        var top = r.Hypotheses[0];
        Assert.Equal("secure-boot-not-detected-van9003", top.Id);
        Assert.Equal(HonestyBucket.GuidedManual, top.Bucket);
        Assert.True(top.CodeLogOdds > 0);
        var setup = top.Contributions.Single(c => c.SignalId == "sec.secureboot.setup_mode");
        Assert.Equal(Tri.Unknown, setup.State);
        Assert.Equal(0, setup.DeltaLogOdds);
        Assert.Contains(r.CouldNotCheck, c => c.SignalId == "sec.secureboot.setup_mode" && c.Reason == UnknownReason.NeedsAdmin);
        Assert.Contains(r.Codes, c => c.Code == "VAN 9003" && c.Origin == CodeOrigin.Logged && c.MatchedId == "van-9003");
        var guide = r.Guides.First(g => g.GuideId == "guide.secureboot.enable");
        Assert.Equal("uefi_sb_off", guide.BranchId);
        Assert.All(r.Proposals.Where(p => p.NeedsAdmin), p => Assert.Equal("proposal.unavailable.limited_mode", p.UnavailableReasonKey));
        Assert.Equal(HealthStatus.Fail, r.Health.Items.Single(i => i.Id == "vanguard.secure_boot").Status);
        Assert.Equal(HealthStatus.Unknown, r.Health.Items.Single(i => i.Id == "vanguard.secure_boot_keys").Status);
    }

    [Fact]
    public void Healthy_pc_has_no_confident_verdict_and_mostly_green_health()
    {
        var s = FixtureSnapshots.HealthyDesktop("01JB6R7X3K9M2N4P5Q6R7S8TA2", new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var r = Engine.Analyze(Input(s));
        Assert.True(r.UnknownCause.Leading);
        Assert.Equal(HonestyBucket.Unknown, r.PrimaryBucket);
        Assert.DoesNotContain(r.Hypotheses, h => h.Band is ConfidenceBand.VeryLikely or ConfidenceBand.Likely);
        Assert.True(r.Health.Items.Count(i => i.Status == HealthStatus.Ok) >= 15);
        Assert.DoesNotContain(r.Health.Items, i => i.Status == HealthStatus.Fail);
    }

    [Fact]
    public void Hwid_ban_routes_and_proposes_nothing()
    {
        var s = FixtureSnapshots.HealthyDesktop("01JB6R7X3K9M2N4P5Q6R7S8TA3", new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var r = Engine.Analyze(Input(s, new IntakeAnswers { Symptoms = ["error_code"], TypedCodes = ["van152"] }));
        Assert.NotNull(r.Route);
        Assert.Equal(RouteKind.HwidBanAppeal, r.Route!.Kind);
        Assert.Equal(HonestyBucket.RiotOrAccount, r.PrimaryBucket);
        Assert.Empty(r.Proposals);
        Assert.Null(r.NextQuestion);
        Assert.Contains(r.Route.Urls, u => u.StartsWith("https://support.riotgames.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_account_code_asks_first_then_routes_only_on_yes()
    {
        var s = FixtureSnapshots.HealthyDesktop("01JB6R7X3K9M2N4P5Q6R7S8TA4", new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var first = Engine.Analyze(Input(s, new IntakeAnswers { Symptoms = ["login_issue"], TypedCodes = ["VAL 59"] }));
        Assert.Null(first.Route);
        Assert.Equal("ask.other_account_works", first.NextQuestion?.QuestionId);

        var no = Engine.Analyze(Input(s, new IntakeAnswers { Symptoms = ["login_issue"], TypedCodes = ["VAL 59"], Answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["ask.other_account_works"] = "no" } }, asked: 1));
        Assert.Null(no.Route);

        var yes = Engine.Analyze(Input(s, new IntakeAnswers { Symptoms = ["login_issue"], TypedCodes = ["VAL 59"], Answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["ask.other_account_works"] = "yes" } }, asked: 1));
        Assert.Equal(RouteKind.TryAnotherAccount, yes.Route?.Kind);
    }

    [Fact]
    public void Logged_codes_weigh_more_than_typed_codes()
    {
        var s = Fixtures.Load(Fixtures.Van9003SecureBootOff);
        var logged = Engine.Analyze(Input(s, new IntakeAnswers { Symptoms = ["error_code"] }));
        var typedOnly = Engine.Analyze(Input(s with { Riot = s.Riot with { DetectedCodes = Fact<IReadOnlyList<DetectedCode>>.Known([]) } },
            new IntakeAnswers { Symptoms = ["error_code"], TypedCodes = ["VAN 9003"] }));
        var a = logged.Hypotheses.Single(h => h.Id == "secure-boot-not-detected-van9003").CodeLogOdds;
        var b = typedOnly.Hypotheses.Single(h => h.Id == "secure-boot-not-detected-van9003").CodeLogOdds;
        Assert.True(a > b, $"logged {a} should exceed typed {b}");
        Assert.Contains(typedOnly.Codes, c => c.Origin == CodeOrigin.Typed);
    }

    [Fact]
    public void Failed_fix_is_negative_evidence_and_not_offered_again()
    {
        var s = Fixtures.Load(Fixtures.RealtekStutter);
        var intake = new IntakeAnswers { Symptoms = ["stutter"] };
        var before = Engine.Analyze(Input(s, intake)).Hypotheses.Single(h => h.Id == "realtek-2-5gbe-nic-stutter");
        var attempt = new FixAttempt
        {
            AttemptId = "01JB6R7X3K9M2N4P5Q6R7S8TA5", FixId = "fix.net.disable_unused_adapter", BindingHash = "x", Status = AttemptStatus.Verified,
            Outcome = FixOutcome.NotFixed, HypothesisIds = ["realtek-2-5gbe-nic-stutter"], Reversibility = Reversibility.Exact,
        };
        var after = Engine.Analyze(Input(s, intake, [attempt]));
        var h = after.Hypotheses.Single(x => x.Id == "realtek-2-5gbe-nic-stutter");
        Assert.True(h.FailedFixLogOdds < 0);
        Assert.True(h.Posterior < before.Posterior);
        var p = after.Proposals.Single(x => x.FixId == "fix.net.disable_unused_adapter");
        Assert.False(p.Available);
        Assert.Equal("proposal.unavailable.failed_before", p.UnavailableReasonKey);
    }

    [Fact]
    public void Requires_gate_fires_only_on_definite_false()
    {
        var s = Fixtures.Load(Fixtures.RealtekStutter);
        var noNic = s with { Network = s.Network with { Adapters = Fact<IReadOnlyList<NetAdapter>>.Known([.. s.Network.Adapters.Value.Where(a => a.InstanceId != FixtureSnapshots.RealtekAdapterInstanceId)]) } };
        var unknownNic = s with { Network = s.Network with { Adapters = Fact<IReadOnlyList<NetAdapter>>.Unknown(UnknownReason.TimedOut) } };
        var gone = Engine.Analyze(Input(noNic, new IntakeAnswers { Symptoms = ["stutter"] }));
        Assert.Contains(gone.RuledOut, o => o.HypothesisId == "realtek-2-5gbe-nic-stutter" && o.Reason == RuledOutReason.RequiresFalse);
        var open = Engine.Analyze(Input(unknownNic, new IntakeAnswers { Symptoms = ["stutter"] }));
        Assert.DoesNotContain(open.RuledOut, o => o.HypothesisId == "realtek-2-5gbe-nic-stutter");
        Assert.Contains(open.CouldNotCheck, c => c.SignalId == "net.realtek25.present" && c.Reason == UnknownReason.TimedOut);
    }

    [Fact]
    public void Waves_only_count_inside_their_window()
    {
        var s = Fixtures.Load(Fixtures.NahimicBlackScreen);
        var intake = new IntakeAnswers { Symptoms = ["launch_black_screen"] };
        var inside = Engine.Analyze(Input(s, intake)).Hypotheses.Single(h => h.Id == "nahimic-audio-service-launch-hang");
        var after = Engine.Analyze(Input(s, intake, asOf: new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero))).Hypotheses.Single(h => h.Id == "nahimic-audio-service-launch-hang");
        Assert.True(inside.WaveLogOdds > 0);
        Assert.Equal(0, after.WaveLogOdds);
    }

    [Fact]
    public void Question_planner_respects_the_three_question_limit()
    {
        var s = FixtureSnapshots.HealthyDesktop("01JB6R7X3K9M2N4P5Q6R7S8TA6", new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var intake = new IntakeAnswers { Symptoms = ["stutter"] };
        Assert.Null(Engine.Analyze(Input(s, intake, asked: RankingConstants.MaxQuestions)).NextQuestion);
        var q = Engine.Analyze(Input(s, intake)).NextQuestion;
        if (q is not null)
        {
            Assert.Equal(2, q.Separates.Count);
            Assert.True(q.Margin <= RankingConstants.QuestionMarginLogOdds);
            Assert.NotNull(TestKb.Real.FindQuestion(q.QuestionId));
        }
    }

    [Fact]
    public void Vgc_automatic_needs_the_precheck_answer()
    {
        var label = AccuracyTests.LoadCases().Single(c => c.Label.Name == "vgc-disabled");
        var noAnswer = Engine.Analyze(AccuracyTests.Input(label.Label with { Answers = new Dictionary<string, string>(StringComparer.Ordinal) }, label.Snapshot));
        Assert.DoesNotContain(noAnswer.Proposals, p => p.FixId == "fix.vg.vgc_automatic");
        var precheckOn = Engine.Analyze(AccuracyTests.Input(label.Label with { Answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["ask.vanguard_precheck_on"] = "yes" } }, label.Snapshot));
        Assert.DoesNotContain(precheckOn.Proposals, p => p.FixId == "fix.vg.vgc_automatic");
        var off = Engine.Analyze(AccuracyTests.Input(label.Label, label.Snapshot));
        Assert.Contains(off.Proposals, p => p.FixId == "fix.vg.vgc_automatic" && p.Binding.Entities["svc"].Single().Id == "vgc");
    }

    [Fact]
    public void Normal_precheck_state_never_offers_vgc_automatic()
    {
        var c = AccuracyTests.LoadCases().Single(x => x.Label.Name == "precheck-normal-state");
        var engine = new Analyzer(TestKb.Real);
        var signals = engine.EvaluateSignals(AccuracyTests.Input(c.Label, c.Snapshot));
        Assert.Equal(Tri.False, signals["vg.vgc_never_started"].State); // not playing for a week is not a failure
        Assert.Equal(Tri.False, signals["vg.vgc_automatic_needed"].State);
        var r = engine.Analyze(AccuracyTests.Input(c.Label with { TypedCodes = ["VAN -81"] }, c.Snapshot)); // a typed code alone is not log evidence
        Assert.DoesNotContain(r.Proposals, p => p.FixId == "fix.vg.vgc_automatic");
    }

    [Fact]
    public void Mixed_causes_get_separate_proposals_and_never_touch_vpn_or_anticheat_services()
    {
        var c = AccuracyTests.LoadCases().Single(x => x.Label.Name == "mixed-nahimic-rtss-vpn-faceit");
        var r = Engine.Analyze(AccuracyTests.Input(c.Label, c.Snapshot));
        var bound = r.Proposals.SelectMany(p => p.Binding.Entities.Values.SelectMany(e => e)).Select(e => e.Id).ToList();
        Assert.DoesNotContain("nordvpn-service", bound);
        Assert.DoesNotContain("FACEITService", bound);
        Assert.DoesNotContain(r.Proposals, p => p.FixId == "fix.riot.client_runasadmin");
        var svc = r.Proposals.Where(p => p.FixId == "fix.svc.disable_conflict_service").ToList();
        Assert.All(svc, p => Assert.Single(p.Binding.Entities["svc"]));
        Assert.Contains(svc, p => p.Binding.Entities["svc"][0].Id == "NahimicService");
        Assert.Contains(r.Proposals, p => p.FixId == "fix.proc.close_overlays" && p.Binding.Entities["proc"].Any(e => e.Id == "RTSS.exe"));
    }

    [Fact]
    public void Stale_logged_codes_do_not_route()
    {
        var c = AccuracyTests.LoadCases().Single(x => x.Label.Name == "stale-van152-secureboot-off");
        var r = Engine.Analyze(AccuracyTests.Input(c.Label, c.Snapshot));
        Assert.Null(r.Route);
        Assert.NotEmpty(r.Guides);
        var fresh = c.Snapshot with { Riot = c.Snapshot.Riot with { DetectedCodes = Fact<IReadOnlyList<DetectedCode>>.Known([new DetectedCode { Code = "VAN 152", Source = CodeSource.RiotClientLog, LastUtc = c.Snapshot.Meta.CreatedUtc.AddHours(-1) }]) } };
        Assert.Equal(RouteKind.HwidBanAppeal, Engine.Analyze(AccuracyTests.Input(c.Label, fresh)).Route?.Kind);
    }

    [Fact]
    public void Unknown_drivers_keep_vanguard_driver_state_unknown()
    {
        var c = AccuracyTests.LoadCases().Single(x => x.Label.Name == "limited-admin-unknowns");
        var signals = Engine.EvaluateSignals(AccuracyTests.Input(c.Label, c.Snapshot));
        Assert.Equal(Tri.Unknown, signals["vg.vgk_running"].State);
        Assert.Equal(Tri.Unknown, signals["vg.vgk_not_loaded"].State);
        var r = Engine.Analyze(AccuracyTests.Input(c.Label, c.Snapshot));
        Assert.True(r.UnknownCause.Leading);
        Assert.Contains(r.CouldNotCheck, x => x.SignalId == "vg.vgk_not_loaded" && x.Reason == UnknownReason.NeedsAdmin);
    }

    [Fact]
    public void One_way_or_medium_fixes_need_real_findings()
    {
        var s = FixtureSnapshots.HealthyDesktop("01JB6R7X3K9M2N4P5Q6R7S8TA7", new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        foreach (var symptoms in new[] { new[] { "high_ping" }, ["stutter"], ["disconnect"] })
        {
            var r = Engine.Analyze(Input(s, new IntakeAnswers { Symptoms = symptoms }));
            Assert.DoesNotContain(r.Proposals, p => p.Reversibility == Reversibility.OneWay && p.Risk >= RiskLevel.Medium);
        }
    }

    [Fact]
    public void Dev_pack_offers_no_fixes()
    {
        var real = TestKb.Real;
        var dev = new VTD.Knowledge.KnowledgeBase(real.Version with { Source = KbSource.Dev }, new VTD.Knowledge.KbContent
        {
            Rules = real.Rules, Signals = real.Signals, Collectors = real.Collectors, Conflicts = real.Conflicts, ErrorCodes = real.ErrorCodes,
            Fixes = real.Fixes, Guides = real.Guides, Questions = real.Questions, Symptoms = real.Symptoms, Tables = real.Tables,
        });
        var r = new Analyzer(dev).Analyze(Input(Fixtures.Load(Fixtures.RealtekStutter), new IntakeAnswers { Symptoms = ["stutter"] }));
        Assert.NotEmpty(r.Proposals);
        Assert.All(r.Proposals, p => Assert.False(p.Available));
    }

    [Fact]
    public void Unknown_faulting_modules_become_anomalies()
    {
        var label = AccuracyTests.LoadCases().Single(c => c.Label.Name == "healthy-crash-unknown-module");
        var r = Engine.Analyze(AccuracyTests.Input(label.Label, label.Snapshot));
        Assert.Contains(r.Anomalies, a => a.Kind == AnomalyKind.UnknownFaultingModule && a.Subject == "btmmhook.dll");
    }

    [Fact]
    public void A_throwing_provider_degrades_to_unknown()
    {
        var bad = new CheckSignalProvider("test", ["sec.secureboot.on"], _ => throw new InvalidOperationException("boom"));
        var analyzer = new Analyzer(TestKb.Real, [bad, .. SignalEngine.DefaultProviders()]);
        var signals = analyzer.EvaluateSignals(Input(Fixtures.Load(Fixtures.Van9003SecureBootOff)));
        Assert.Equal(Tri.Unknown, signals["sec.secureboot.on"].State);
        Assert.Equal(UnknownReason.Failed, signals["sec.secureboot.on"].UnknownReason);
    }

    [Fact]
    public void Ranked_output_respects_bands_and_floor()
    {
        foreach (var (label, snapshot) in AccuracyTests.LoadCases())
        {
            var r = Engine.Analyze(AccuracyTests.Input(label, snapshot));
            Assert.True(r.UnknownCause.Probability >= RankingConstants.UnknownCauseFloor);
            Assert.All(r.Hypotheses, h => Assert.Equal(RankingConstants.BandOf(h.Posterior), h.Band));
            Assert.Equal(r.Hypotheses.Select(h => h.Posterior).OrderByDescending(p => p), r.Hypotheses.Select(h => h.Posterior));
            Assert.All(r.Proposals, p => Assert.NotNull(TestKb.Real.FindFix(p.FixId)));
            Assert.True(r.Proposals.Where(p => p.Available).Select(p => p.Utility).SequenceEqual(r.Proposals.Where(p => p.Available).Select(p => p.Utility).OrderByDescending(u => u)));
        }
    }
}
