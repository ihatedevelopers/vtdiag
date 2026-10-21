using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Snapshots;

namespace VTD.Testing.Fakes;

/// <summary>
/// <see cref="IAnalyzer"/> returning canned, realistic results for the three fixtures (keyed by snapshot id) and an
/// "unknown cause leading" result for anything else. The canned results are what A2's real analyzer is expected to
/// produce in spirit (same top hypothesis, bucket, proposal and binding), and what A6 renders against.
/// </summary>
public sealed class FakeAnalyzer : IAnalyzer
{
    /// <summary>Inputs received.</summary>
    public List<AnalysisInput> Calls { get; } = [];

    /// <inheritdoc />
    public AnalysisResult Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Calls.Add(input);
        var result = CannedAnalyses.For(input.Snapshot);
        return result with { AnalysisId = input.AnalysisId, CreatedUtc = input.AsOfUtc, QuestionsAsked = input.QuestionsAsked };
    }
}

/// <summary>Canned analyses for the fixtures.</summary>
public static class CannedAnalyses
{
    /// <summary>The canned analysis for a snapshot (by id), or an "unknown cause" result.</summary>
    /// <param name="snapshot">Snapshot.</param>
    public static AnalysisResult For(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Meta.SnapshotId switch
        {
            FixtureSnapshots.RealtekId => Realtek(snapshot),
            FixtureSnapshots.NahimicId => Nahimic(snapshot),
            FixtureSnapshots.Van9003Id => Van9003(snapshot),
            _ => UnknownCause(snapshot),
        };
    }

    private static AnalysisResult Realtek(ScanSnapshot s)
    {
        var adapter = new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId, DisplayName = "Realtek Gaming 2.5GbE Family Controller" };
        return Base(s) with
        {
            Hypotheses =
            [
                new RankedHypothesis
                {
                    Id = "realtek-2-5gbe-nic-stutter",
                    Category = ProblemCategory.FpsPerformance,
                    TitleKey = "rule.realtek25.title",
                    Posterior = 0.93,
                    LogOdds = 2.59,
                    PriorLogOdds = -1.52,
                    Band = ConfidenceBand.VeryLikely,
                    Bucket = HonestyBucket.FixableHere,
                    Contributions =
                    [
                        new EvidenceContribution { EvidenceId = "enabled", SignalId = "conflict.realtek_2_5gbe.adapter_enabled", State = Tri.True, DeltaLogOdds = 0, Group = "nic_state", Counted = false },
                        new EvidenceContribution { EvidenceId = "unused", SignalId = "net.realtek25.unused", State = Tri.True, DeltaLogOdds = 1.0, Group = "nic_state", TextKey = "rule.realtek25.ev.unused" },
                        new EvidenceContribution { EvidenceId = "resets", SignalId = "events.realtek_nic_resets", State = Tri.True, DeltaLogOdds = 2.0, TextKey = "rule.realtek25.ev.resets", Evidence = [new Evidence { TextKey = "rule.realtek25.ev.resets", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["count"] = "14" }, FactPath = "events.hits[0].count", Source = EvidenceSource.Scan }] },
                        new EvidenceContribution { EvidenceId = "multi_nic", SignalId = "net.multi_nic_up", State = Tri.False, DeltaLogOdds = 0 },
                        new EvidenceContribution { EvidenceId = "only_valorant", SignalId = "ask.only_valorant_affected", State = Tri.Unknown, DeltaLogOdds = 0 },
                    ],
                    Entities = [adapter],
                    FixIds = ["fix.net.disable_unused_adapter"],
                },
            ],
            UnknownCause = new UnknownCause { Floor = RankingConstants.UnknownCauseFloor, Probability = 0.06, Leading = false },
            PrimaryBucket = HonestyBucket.FixableHere,
            Buckets = [new BucketSummary { Bucket = HonestyBucket.FixableHere, HypothesisIds = ["realtek-2-5gbe-nic-stutter"], MaxPosterior = 0.93 }],
            Proposals =
            [
                new FixProposal
                {
                    FixId = "fix.net.disable_unused_adapter",
                    HypothesisIds = ["realtek-2-5gbe-nic-stutter"],
                    Binding = new FixBinding { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { ["adapter"] = [adapter] } },
                    Utility = 0.78,
                    ExpectedBenefit = 0.75,
                    Cost = 1.04,
                    ReasonKey = "proposal.reason.quick_harmless_undoable",
                    Risk = RiskLevel.Low,
                    Reversibility = Reversibility.Exact,
                    Reboot = RebootNeed.None,
                    NeedsAdmin = true,
                    Available = true,
                },
            ],
        };
    }

    private static AnalysisResult Nahimic(ScanSnapshot s)
    {
        var service = new EntityRef { Kind = EntityKind.Service, Id = "NahimicService", DisplayName = "Nahimic service" };
        return Base(s) with
        {
            Hypotheses =
            [
                new RankedHypothesis
                {
                    Id = "nahimic-audio-service-launch-hang",
                    Category = ProblemCategory.CrashFreezeBsod,
                    TitleKey = "rule.nahimic.title",
                    Posterior = 0.95,
                    LogOdds = 2.94,
                    PriorLogOdds = -2.26,
                    Band = ConfidenceBand.VeryLikely,
                    Bucket = HonestyBucket.FixableHere,
                    WaveLogOdds = 0.7,
                    Contributions =
                    [
                        new EvidenceContribution { EvidenceId = "running", SignalId = "conflict.nahimic.running", State = Tri.True, DeltaLogOdds = 1.5 },
                        new EvidenceContribution { EvidenceId = "crash_module", SignalId = "conflict.nahimic.crash_module", State = Tri.True, DeltaLogOdds = 3.0, TextKey = "rule.nahimic.ev.module", Evidence = [new Evidence { TextKey = "rule.nahimic.ev.module", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "ProductInfo.dll", ["count"] = "2" }, FactPath = "events.appCrashes[0].module" }] },
                    ],
                    Entities = [service],
                    FixIds = ["fix.svc.disable_conflict_service"],
                    GuideIds = ["guide.apps.uninstall_nahimic"],
                },
            ],
            UnknownCause = new UnknownCause { Floor = RankingConstants.UnknownCauseFloor, Probability = 0.05, Leading = false },
            PrimaryBucket = HonestyBucket.FixableHere,
            Buckets = [new BucketSummary { Bucket = HonestyBucket.FixableHere, HypothesisIds = ["nahimic-audio-service-launch-hang"], MaxPosterior = 0.95 }],
            Proposals =
            [
                new FixProposal
                {
                    FixId = "fix.svc.disable_conflict_service",
                    HypothesisIds = ["nahimic-audio-service-launch-hang"],
                    Binding = new FixBinding { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { ["svc"] = [service] } },
                    Utility = 0.76,
                    ExpectedBenefit = 0.75,
                    Cost = 1.0,
                    ReasonKey = "proposal.reason.quick_harmless_undoable",
                    Risk = RiskLevel.Low,
                    Reversibility = Reversibility.Exact,
                    Reboot = RebootNeed.None,
                    NeedsAdmin = true,
                    Available = true,
                },
            ],
            Guides = [new GuideProposal { GuideId = "guide.apps.uninstall_nahimic", HypothesisIds = ["nahimic-audio-service-launch-hang"] }],
        };
    }

    private static AnalysisResult Van9003(ScanSnapshot s) => Base(s) with
    {
        Hypotheses =
        [
            new RankedHypothesis
            {
                Id = "secure-boot-not-detected-van9003",
                Category = ProblemCategory.TpmSecureBootBios,
                TitleKey = "rule.van9003.title",
                Posterior = 0.98,
                LogOdds = 3.9,
                PriorLogOdds = -2.6,
                Band = ConfidenceBand.VeryLikely,
                Bucket = HonestyBucket.GuidedManual,
                CodeLogOdds = 4.0,
                Contributions =
                [
                    new EvidenceContribution { EvidenceId = "sb_off", SignalId = "sec.secureboot.on", State = Tri.False, DeltaLogOdds = 3.0, TextKey = "rule.van9003.ev.sb_off" },
                    new EvidenceContribution { EvidenceId = "legacy", SignalId = "fw.uefi", State = Tri.True, DeltaLogOdds = 0 },
                    new EvidenceContribution { EvidenceId = "mbr", SignalId = "disk.system_gpt", State = Tri.True, DeltaLogOdds = 0 },
                    new EvidenceContribution { EvidenceId = "setup_mode", SignalId = "sec.secureboot.setup_mode", State = Tri.Unknown, DeltaLogOdds = 0 },
                ],
                GuideIds = ["guide.secureboot.enable"],
            },
        ],
        UnknownCause = new UnknownCause { Floor = RankingConstants.UnknownCauseFloor, Probability = 0.03, Leading = false },
        PrimaryBucket = HonestyBucket.GuidedManual,
        Buckets = [new BucketSummary { Bucket = HonestyBucket.GuidedManual, HypothesisIds = ["secure-boot-not-detected-van9003"], MaxPosterior = 0.98 }],
        Guides = [new GuideProposal { GuideId = "guide.secureboot.enable", HypothesisIds = ["secure-boot-not-detected-van9003"], BranchId = "uefi_sb_off" }],
        CouldNotCheck =
        [
            new CouldNotCheck { SignalId = "sec.secureboot.setup_mode", Reason = UnknownReason.NeedsAdmin, TextKey = "couldnotcheck.needs_admin", AffectsHypotheses = ["secure-boot-not-detected-van9003"] },
        ],
        Codes = [new CodeUse { Code = "VAN 9003", Origin = CodeOrigin.Logged, MatchedId = "van-9003" }],
        Health = Health(s, limited: true),
    };

    private static AnalysisResult UnknownCause(ScanSnapshot s) => Base(s) with
    {
        UnknownCause = new UnknownCause { Floor = RankingConstants.UnknownCauseFloor, Probability = 0.72, Leading = true },
        PrimaryBucket = HonestyBucket.Unknown,
    };

    private static AnalysisResult Base(ScanSnapshot s) => new()
    {
        AnalysisId = "01JB6R7X3K9M2N4P5Q6R7S8TA0",
        SnapshotId = s.Meta.SnapshotId,
        KbVersion = s.Meta.KbVersion,
        CreatedUtc = s.Meta.CreatedUtc,
        Hypotheses = [],
        UnknownCause = new UnknownCause { Floor = RankingConstants.UnknownCauseFloor, Probability = RankingConstants.UnknownCauseFloor, Leading = false },
        PrimaryBucket = HonestyBucket.Unknown,
        Health = Health(s, limited: false),
    };

    private static HealthChecklist Health(ScanSnapshot s, bool limited)
    {
        var sbOn = s.Security.SecureBootEnabled.ToTri();
        return new HealthChecklist
        {
            Items =
            [
                new HealthItem { Id = "vanguard.tpm2", Group = HealthGroup.VanguardReadiness, Status = HealthStatus.Ok, TitleKey = "health.tpm2", SignalId = "sec.tpm2" },
                new HealthItem { Id = "vanguard.secure_boot", Group = HealthGroup.VanguardReadiness, Status = sbOn == Tri.True ? HealthStatus.Ok : sbOn == Tri.False ? HealthStatus.Fail : HealthStatus.Unknown, TitleKey = "health.secure_boot", SignalId = "sec.secureboot.on", GuideId = sbOn == Tri.False ? "guide.secureboot.enable" : null },
                new HealthItem
                {
                    Id = "vanguard.secure_boot_keys",
                    Group = HealthGroup.VanguardReadiness,
                    Status = limited ? HealthStatus.Unknown : HealthStatus.Ok,
                    UnknownReason = limited ? UnknownReason.NeedsAdmin : null,
                    TitleKey = "health.secure_boot_keys",
                    SignalId = "sec.secureboot.setup_mode",
                },
                new HealthItem { Id = "vanguard.uefi_gpt", Group = HealthGroup.VanguardReadiness, Status = HealthStatus.Ok, TitleKey = "health.uefi_gpt", SignalId = "fw.uefi" },
                new HealthItem
                {
                    Id = "vanguard.test_signing_off",
                    Group = HealthGroup.VanguardReadiness,
                    Status = limited ? HealthStatus.Unknown : HealthStatus.Ok,
                    UnknownReason = limited ? UnknownReason.NeedsAdmin : null,
                    TitleKey = "health.test_signing_off",
                    SignalId = "sec.bcd.testsigning",
                },
                new HealthItem { Id = "windows.pending_reboot", Group = HealthGroup.Windows, Status = HealthStatus.Ok, TitleKey = "health.pending_reboot", SignalId = "os.pending_reboot" },
                new HealthItem { Id = "storage.free_space", Group = HealthGroup.Storage, Status = HealthStatus.Ok, TitleKey = "health.free_space", SignalId = "disk.free_ok" },
            ],
        };
    }
}
