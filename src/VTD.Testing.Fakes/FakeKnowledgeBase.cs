using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;

namespace VTD.Testing.Fakes;

/// <summary>
/// In-memory <see cref="IKnowledgeBase"/>. <see cref="Sample"/> holds a small, schema-valid KB covering the three
/// fixtures (rules from design_B.md section 4.2 examples, their fixes, the Secure Boot guide, codes, questions, a pack
/// collector and two conflict entries). A2 replaces it with the real loader; everyone else develops against it.
/// </summary>
public sealed class FakeKnowledgeBase : IKnowledgeBase
{
    private readonly Dictionary<string, Dictionary<string, string>> _strings;

    /// <summary>Creates a KB from parts. Unspecified parts are empty.</summary>
    /// <param name="version">Version.</param>
    public FakeKnowledgeBase(KbVersion? version = null)
    {
        Version = version ?? new KbVersion { Version = FixtureSnapshots.KbVersion, Format = KbManifest.CurrentFormat, Source = KbSource.Bundled };
        _strings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            [VTD.Contracts.Languages.English] = new(StringComparer.Ordinal),
            [VTD.Contracts.Languages.Turkish] = new(StringComparer.Ordinal),
        };
    }

    /// <inheritdoc />
    public KbVersion Version { get; }

    /// <inheritdoc />
    public IReadOnlyList<RuleDef> Rules { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<SignalDef> Signals { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<CollectorDef> Collectors { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<ConflictDef> Conflicts { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<ErrorCodeDef> ErrorCodes { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<FixDef> Fixes { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<GuideDef> Guides { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<QuestionDef> Questions { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<SymptomDef> Symptoms { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<TableDef> Tables { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<TrustedHelperKey> TrustedHelpers { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Languages => [.. _strings.Keys];

    /// <summary>Adds or replaces a string.</summary>
    /// <param name="language">Language.</param>
    /// <param name="key">Key.</param>
    /// <param name="text">Text with <c>{name}</c> placeholders.</param>
    public FakeKnowledgeBase WithText(string language, string key, string text)
    {
        if (!_strings.TryGetValue(language, out var table))
        {
            table = new Dictionary<string, string>(StringComparer.Ordinal);
            _strings[language] = table;
        }

        table[key] = text;
        return this;
    }

    /// <inheritdoc />
    public RuleDef? FindRule(string id) => Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    /// <inheritdoc />
    public FixDef? FindFix(string id) => Fixes.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    /// <inheritdoc />
    public GuideDef? FindGuide(string id) => Guides.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));

    /// <inheritdoc />
    public QuestionDef? FindQuestion(string id) => Questions.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.Ordinal));

    /// <inheritdoc />
    public ConflictDef? FindConflict(string id) => Conflicts.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>Simple normalization: upper invariant, separators collapsed, <c>VAN9003</c> → <c>VAN 9003</c>. A2 owns the real one.</summary>
    /// <param name="text">Raw text.</param>
    public ErrorCodeMatch? FindCode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = NormalizeCode(text);
        foreach (var def in ErrorCodes)
        {
            foreach (var code in def.Codes)
            {
                if (string.Equals(code, normalized, StringComparison.Ordinal))
                {
                    return new ErrorCodeMatch { Def = def, Normalized = code, Exact = true };
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string Text(string key, string language, IReadOnlyDictionary<string, object>? args = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var text = (_strings.TryGetValue(language, out var t) && t.TryGetValue(key, out var v)) ? v
            : (_strings[VTD.Contracts.Languages.English].TryGetValue(key, out var en) ? en : key);
        if (args is null)
        {
            return text;
        }

        var culture = CultureInfo.GetCultureInfo(language);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", Convert.ToString(value, culture), StringComparison.Ordinal);
        }

        return text;
    }

    /// <inheritdoc />
    public bool HasText(string key, string language) => _strings.TryGetValue(language, out var t) && t.ContainsKey(key);

    /// <summary>Fake normalization used by <see cref="FindCode"/>.</summary>
    /// <param name="text">Raw code.</param>
    public static string NormalizeCode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim().ToUpperInvariant().Replace('-', ' ').Replace(':', ' ');
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (s.Length > 3 && (s.StartsWith("VAN", StringComparison.Ordinal) || s.StartsWith("VAL", StringComparison.Ordinal)) && s[3] != ' ')
        {
            s = s[..3] + " " + s[3..];
        }

        return s;
    }

    /// <summary>The sample KB covering the three fixtures.</summary>
    public static FakeKnowledgeBase Sample()
    {
        var kb = new FakeKnowledgeBase
        {
            Symptoms =
            [
                new SymptomDef { Id = "stutter", TitleKey = "symptom.stutter", Categories = [ProblemCategory.FpsPerformance], Order = 1 },
                new SymptomDef { Id = "launch_black_screen", TitleKey = "symptom.launch_black_screen", Categories = [ProblemCategory.CrashFreezeBsod, ProblemCategory.Vanguard], Order = 2 },
                new SymptomDef { Id = "error_code", TitleKey = "symptom.error_code", Categories = [ProblemCategory.Vanguard, ProblemCategory.TpmSecureBootBios], Order = 3 },
            ],
            Collectors =
            [
                new EventLogCollectorDef { Id = "realtek_nic_errors", Log = "System", Providers = ["rt25cx21", "rtcx21", "rt640x64", "rt68cx21"], Levels = [EventLevel.Critical, EventLevel.Error, EventLevel.Warning], SinceDays = 14, Max = 500, Project = [] },
                new FaultingModuleCollectorDef { Id = "nahimic_crash_module", Modules = ["ProductInfo.dll"], SinceDays = 30 },
            ],
            Signals =
            [
                new SignalDef { Id = "conflict.realtek_2_5gbe.adapter_present", Source = SignalSource.Conflict, Entity = EntityKind.Adapter },
                new SignalDef { Id = "conflict.realtek_2_5gbe.adapter_enabled", Source = SignalSource.Conflict, Entity = EntityKind.Adapter },
                new SignalDef { Id = "net.realtek25.unused", Source = SignalSource.Builtin, Entity = EntityKind.Adapter, TitleKey = "signal.net.realtek25.unused" },
                new SignalDef { Id = "events.realtek_nic_resets", Source = SignalSource.Collector, FromCollector = new CollectorSignalSpec { Collector = "realtek_nic_errors", MinCount = 3, WithinDays = 14, SaturateAt = 10 } },
                new SignalDef { Id = "net.multi_nic_up", Source = SignalSource.Builtin },
                new SignalDef { Id = "conflict.nahimic.installed", Source = SignalSource.Conflict, Entity = EntityKind.App },
                new SignalDef { Id = "conflict.nahimic.running", Source = SignalSource.Conflict, Entity = EntityKind.Service },
                new SignalDef { Id = "conflict.nahimic.service_present", Source = SignalSource.Conflict, Entity = EntityKind.Service },
                new SignalDef { Id = "conflict.nahimic.crash_module", Source = SignalSource.Collector, FromCollector = new CollectorSignalSpec { Collector = "nahimic_crash_module", MinCount = 1, WithinDays = 30 } },
                new SignalDef { Id = "sec.secureboot.on", Source = SignalSource.Builtin, TitleKey = "signal.sec.secureboot.on" },
                new SignalDef { Id = "sec.secureboot.setup_mode", Source = SignalSource.Builtin, RequiresAdmin = true, TitleKey = "signal.sec.secureboot.setup_mode" },
                new SignalDef { Id = "fw.uefi", Source = SignalSource.Builtin },
                new SignalDef { Id = "disk.system_gpt", Source = SignalSource.Builtin },
                new SignalDef { Id = "ask.only_valorant_affected", Source = SignalSource.Ask, Question = "ask.only_valorant_affected" },
                new SignalDef { Id = "ask.league_also_broken", Source = SignalSource.Ask, Question = "ask.league_also_broken" },
            ],
            Rules =
            [
                new RuleDef
                {
                    Id = "realtek-2-5gbe-nic-stutter",
                    Category = ProblemCategory.FpsPerformance,
                    TitleKey = "rule.realtek25.title",
                    SummaryKey = "rule.realtek25.summary",
                    Bucket = HonestyBucket.FixableHere,
                    Symptoms = ["stutter", "fps_drop", "freeze_short", "ping_spikes"],
                    Prior = new PriorDef { Threads = 76 },
                    Requires = ["conflict.realtek_2_5gbe.adapter_present"],
                    Evidence =
                    [
                        new EvidenceDef { Id = "enabled", Signal = "conflict.realtek_2_5gbe.adapter_enabled", WhenTrue = 1.0, Veto = VetoWhen.False, Group = "nic_state" },
                        new EvidenceDef { Id = "unused", Signal = "net.realtek25.unused", WhenTrue = 1.0, Group = "nic_state", TextKey = "rule.realtek25.ev.unused" },
                        new EvidenceDef { Id = "resets", Signal = "events.realtek_nic_resets", WhenTrue = 2.0, TextKey = "rule.realtek25.ev.resets" },
                        new EvidenceDef { Id = "multi_nic", Signal = "net.multi_nic_up", WhenTrue = 0.5 },
                        new EvidenceDef { Id = "only_valorant", Signal = "ask.only_valorant_affected", WhenTrue = 0.5, Ask = "ask.only_valorant_affected" },
                    ],
                    Fixes = ["fix.net.disable_unused_adapter"],
                    Guides = [],
                    Questions = ["ask.only_valorant_affected"],
                    Sources = new RuleSources { Catalog = "realtek-2-5gbe-nic-stutter", ThreadCount = 76, Evidence = "op_confirmed", ThreadIds = ["1orgew9", "1osbhay"] },
                },
                new RuleDef
                {
                    Id = "nahimic-audio-service-launch-hang",
                    Category = ProblemCategory.CrashFreezeBsod,
                    TitleKey = "rule.nahimic.title",
                    Bucket = HonestyBucket.FixableHere,
                    Symptoms = ["launch_black_screen", "loading_stuck"],
                    Prior = new PriorDef { Threads = 29 },
                    Requires = ["conflict.nahimic.installed"],
                    Evidence =
                    [
                        new EvidenceDef { Id = "running", Signal = "conflict.nahimic.running", WhenTrue = 1.5, WhenFalse = -0.5 },
                        new EvidenceDef { Id = "crash_module", Signal = "conflict.nahimic.crash_module", WhenTrue = 3.0, TextKey = "rule.nahimic.ev.module" },
                    ],
                    Wave = new WaveDef { From = new DateOnly(2026, 9, 1), To = new DateOnly(2026, 12, 31), Boost = 0.7 },
                    Fixes = ["fix.svc.disable_conflict_service"],
                    Supersedes = ["conflict-nahimic"],
                    Sources = new RuleSources { Catalog = "nahimic-audio-service-launch-hang", ThreadCount = 29, Evidence = "op_confirmed" },
                },
                new RuleDef
                {
                    Id = "secure-boot-not-detected-van9003",
                    Category = ProblemCategory.TpmSecureBootBios,
                    TitleKey = "rule.van9003.title",
                    Bucket = HonestyBucket.GuidedManual,
                    Symptoms = ["error_code"],
                    Prior = new PriorDef { Threads = 19 },
                    Evidence =
                    [
                        new EvidenceDef { Id = "sb_off", Signal = "sec.secureboot.on", WhenFalse = 3.0, Veto = VetoWhen.True, TextKey = "rule.van9003.ev.sb_off" },
                        new EvidenceDef { Id = "legacy", Signal = "fw.uefi", WhenFalse = 1.0 },
                        new EvidenceDef { Id = "mbr", Signal = "disk.system_gpt", WhenFalse = 1.0 },
                        new EvidenceDef { Id = "setup_mode", Signal = "sec.secureboot.setup_mode", WhenTrue = 1.5 },
                    ],
                    Guides = ["guide.secureboot.enable"],
                    Sources = new RuleSources { Catalog = "secure-boot-not-detected-van9003", ThreadCount = 19, Evidence = "op_confirmed", Urls = ["https://support.riotgames.com/riot/client/error-van-9003"] },
                },
                new RuleDef
                {
                    Id = "route-hwid-ban",
                    Kind = RuleKind.Route,
                    Route = RouteKind.HwidBanAppeal,
                    Category = ProblemCategory.Vanguard,
                    TitleKey = "rule.route.hwid_ban.title",
                    Bucket = HonestyBucket.RiotOrAccount,
                    Prior = new PriorDef { Threads = 0 },
                },
            ],
            Conflicts =
            [
                new ConflictDef
                {
                    Id = "nahimic",
                    Name = "Nahimic / A-Volute audio (MSI, Lenovo, Dell, ASUS Sonic Studio OEM)",
                    Kind = ConflictKind.Audio,
                    Detect = new ConflictDetect { ProcessNames = ["NahimicSvc64.exe", "NahimicSvc32.exe"], ServiceNames = ["NahimicService"], UninstallDisplayNameRegex = "Nahimic|A-Volute", ModuleNames = ["ProductInfo.dll"] },
                    EffectKeys = ["conflict.nahimic.effect.0", "conflict.nahimic.effect.1"],
                    Fix = new ConflictFix { ActionKey = "conflict.nahimic.fix", Reversible = true, Risk = RiskLevel.Low, FixIds = ["fix.svc.disable_conflict_service"], GuideIds = ["guide.apps.uninstall_nahimic"] },
                    Confidence = ConfidenceLevel.High,
                    Category = ProblemCategory.CrashFreezeBsod,
                    Symptoms = ["launch_black_screen"],
                    Evidence = new ConflictEvidence { RedditMentions = 71, SolvedMentions = 27, ExampleThreadIds = ["1wciq6n"] },
                },
                new ConflictDef
                {
                    Id = "realtek_2_5gbe",
                    Name = "Realtek 2.5GbE / Gaming 2.5GbE Family Controller",
                    Kind = ConflictKind.Driver,
                    Detect = new ConflictDetect { DriverNames = ["rt25cx21x64.sys"], NetworkAdapterRegex = "Realtek.*2\\.5GbE|Realtek Gaming 2\\.5GbE" },
                    EffectKeys = ["conflict.realtek_2_5gbe.effect.0"],
                    Fix = new ConflictFix { ActionKey = "conflict.realtek_2_5gbe.fix", Reversible = true, Risk = RiskLevel.Low, FixIds = ["fix.net.disable_unused_adapter"] },
                    Confidence = ConfidenceLevel.High,
                    Category = ProblemCategory.FpsPerformance,
                    Symptoms = ["stutter"],
                    Evidence = new ConflictEvidence { RedditMentions = 174, SolvedMentions = 46 },
                },
            ],
            ErrorCodes =
            [
                new ErrorCodeDef
                {
                    Id = "van-9003",
                    Codes = ["VAN 9003"],
                    Category = "vanguard_requirement",
                    MeaningKey = "code.van9003.meaning",
                    PointsTo = [new CodeCause { Hypothesis = "secure-boot-not-detected-van9003", Logged = 4.0, Typed = 2.5 }],
                    OfficialUrls = ["https://support.riotgames.com/riot/client/error-van-9003"],
                    Confidence = ConfidenceLevel.High,
                },
                new ErrorCodeDef
                {
                    Id = "van-152",
                    Codes = ["VAN 152"],
                    Category = "ban",
                    MeaningKey = "code.van152.meaning",
                    Route = RouteKind.HwidBanAppeal,
                    OfficialUrls = ["https://support.riotgames.com/riot/performance/error-van-152"],
                    Confidence = ConfidenceLevel.High,
                },
                new ErrorCodeDef
                {
                    Id = "val-account-group",
                    Codes = ["VAL 43", "VAL 57", "VAL 59"],
                    Category = "val_account",
                    MeaningKey = "code.val_account.meaning",
                    Route = RouteKind.TryAnotherAccount,
                    AskIfTyped = "ask.other_account_works",
                },
            ],
            Fixes =
            [
                new FixDef
                {
                    Id = "fix.net.disable_unused_adapter",
                    TitleKey = "fix.disable_adapter.title",
                    ExplainKey = "fix.disable_adapter.explain",
                    UndoExplainKey = "fix.disable_adapter.undo",
                    NoticeKey = "fix.disable_adapter.notice",
                    ManualStepKeys = ["fix.disable_adapter.manual.0", "fix.disable_adapter.manual.1", "fix.disable_adapter.manual.2"],
                    Risk = RiskLevel.Low,
                    Reversibility = Reversibility.Exact,
                    Reboot = RebootNeed.None,
                    EstMinutes = 0.2,
                    AppliesTo = ["realtek-2-5gbe-nic-stutter"],
                    Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["adapter"] = new BindDef { Kind = EntityKind.Adapter, FromSignal = "net.realtek25.unused", Pick = BindPick.One } },
                    Guards =
                    [
                        new GuardDef { Probe = GuardIds.AdapterNotDefaultRoute, Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["instanceId"] = "$adapter.id" }, Must = Tri.True, FailKey = "guard.adapter_is_active" },
                        new GuardDef { Probe = GuardIds.OtherAdapterOnline, Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["exceptInstanceId"] = "$adapter.id" }, Must = Tri.True, FailKey = "guard.no_other_connection" },
                    ],
                    Steps = [new StepDef { Op = "device.disable", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["instanceId"] = "$adapter.id" } }],
                    Verify = [new VerifyDef { Probe = "device.disable", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["instanceId"] = "$adapter.id" }, Expect = new Dictionary<string, string>(StringComparer.Ordinal) { ["enabled"] = "false" } }],
                    Deadman = new DeadmanDef { Seconds = 60, Check = GuardIds.InternetReachable },
                    Outcome = new OutcomeDef { Kind = OutcomeKind.MonitorCompare, AskKey = "outcome.stutter_gone", MonitorSignals = ["mon.frametime.periodic_spikes"] },
                    Drift = new DriftDef { Watch = true, ReasonKey = "drift.adapter_reenabled" },
                    SuccessPrior = new SuccessPriorDef { Success = 33, Trials = 41 },
                },
                new FixDef
                {
                    Id = "fix.svc.disable_conflict_service",
                    TitleKey = "fix.disable_service.title",
                    ExplainKey = "fix.disable_service.explain",
                    UndoExplainKey = "fix.disable_service.undo",
                    Risk = RiskLevel.Low,
                    Reversibility = Reversibility.Exact,
                    Template = true,
                    AppliesTo = ["nahimic-audio-service-launch-hang"],
                    Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["svc"] = new BindDef { Kind = EntityKind.Service, FromSignal = "conflict.*.service_present", Pick = BindPick.All } },
                    Steps =
                    [
                        new StepDef { Op = "service.setStartType", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "$svc.id", ["startType"] = "Disabled" } },
                        new StepDef { Op = "service.stop", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "$svc.id", ["timeoutSec"] = "20" }, OnFail = StepFailure.Continue },
                    ],
                    Verify = [new VerifyDef { Probe = "service.setStartType", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "$svc.id" }, Expect = new Dictionary<string, string>(StringComparer.Ordinal) { ["startType"] = "Disabled" } }],
                    Outcome = new OutcomeDef { Kind = OutcomeKind.Relaunch, AskKey = "outcome.past_loading_screen" },
                    Drift = new DriftDef { Watch = true, ReasonKey = "drift.oem_reinstalled" },
                    SuccessPrior = new SuccessPriorDef { Success = 15, Trials = 19 },
                },
                new FixDef
                {
                    Id = "fix.net.dns_flush",
                    TitleKey = "fix.dns_flush.title",
                    ExplainKey = "fix.dns_flush.explain",
                    UndoExplainKey = "fix.dns_flush.undo",
                    Risk = RiskLevel.Low,
                    Reversibility = Reversibility.OneWay,
                    EstMinutes = 0.1,
                    Steps = [new StepDef { Op = OpIds.DnsFlush }],
                    Outcome = new OutcomeDef { Kind = OutcomeKind.Playtest, AskKey = "outcome.connects_now" },
                },
                new FixDef
                {
                    // A fix that only takes effect after a restart: drives AwaitingRestart, the restart screen and resume.
                    Id = "fix.display.hags_off",
                    TitleKey = "fix.hags_off.title",
                    ExplainKey = "fix.hags_off.explain",
                    UndoExplainKey = "fix.hags_off.undo",
                    Risk = RiskLevel.Low,
                    Reversibility = Reversibility.Exact,
                    Reboot = RebootNeed.Required,
                    EstMinutes = 0.5,
                    AppliesTo = ["realtek-2-5gbe-nic-stutter"],
                    Steps = [new StepDef { Op = "registry.setValue", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ["name"] = "HwSchMode", ["kind"] = "DWord", ["data"] = "1" } }],
                    Verify = [new VerifyDef { Probe = "registry.setValue", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ["name"] = "HwSchMode" }, Expect = new Dictionary<string, string>(StringComparer.Ordinal) { ["data"] = "1" } }],
                    Outcome = new OutcomeDef { Kind = OutcomeKind.Playtest, AskKey = "outcome.stutter_gone" },
                    Drift = new DriftDef { Watch = true, ReasonKey = "drift.setting_reverted" },
                },
            ],
            Guides =
            [
                new GuideDef
                {
                    Id = "guide.secureboot.enable",
                    TitleKey = "guide.secureboot.title",
                    Bucket = HonestyBucket.GuidedManual,
                    Risk = RiskLevel.High,
                    Reboot = RebootNeed.Required,
                    AppliesTo = ["secure-boot-not-detected-van9003"],
                    Preflight = [new GuardDef { Probe = GuardIds.BitLockerOn, Must = Tri.False, Severity = GuardSeverity.Warn, FailKey = "guide.secureboot.bitlocker_key_first" }],
                    Branches =
                    [
                        new GuideBranchDef
                        {
                            Id = "mbr",
                            When = [new ConditionDef { Signal = "disk.system_gpt", Is = Tri.False }],
                            TextKey = "guide.secureboot.mbr.text",
                            Steps = [new GuideStepDef { TextKey = "guide.secureboot.mbr.step1", Action = GuideAction.RunCheck, Target = "disk.mbr2gpt_validate" }],
                            NextOnFail = "csm",
                        },
                        new GuideBranchDef
                        {
                            Id = "csm",
                            When = [new ConditionDef { Signal = "fw.uefi", Is = Tri.False }],
                            Steps = [new GuideStepDef { TextKey = "guide.secureboot.csm.step1", VendorTable = "bios_secureboot_paths" }, new GuideStepDef { TextKey = "guide.secureboot.restart_bios", Action = GuideAction.RestartToFirmware }],
                            Verify = [new ConditionDef { Signal = "fw.uefi", Is = Tri.True }],
                        },
                        new GuideBranchDef
                        {
                            Id = "uefi_sb_off",
                            When = [new ConditionDef { Signal = "fw.uefi", Is = Tri.True }, new ConditionDef { Signal = "sec.secureboot.on", Is = Tri.False }],
                            Steps = [new GuideStepDef { TextKey = "guide.secureboot.enable.step1", VendorTable = "bios_secureboot_paths", DocsAnchor = "enable" }, new GuideStepDef { TextKey = "guide.secureboot.restart_bios", Action = GuideAction.RestartToFirmware }],
                            Verify = [new ConditionDef { Signal = "sec.secureboot.on", Is = Tri.True }],
                            NextOnFail = "setup_mode",
                        },
                        new GuideBranchDef
                        {
                            Id = "setup_mode",
                            When = [new ConditionDef { Signal = "sec.secureboot.setup_mode", Is = Tri.True }],
                            Steps = [new GuideStepDef { TextKey = "guide.secureboot.factory_keys.step1", VendorTable = "bios_secureboot_paths" }],
                            Verify = [new ConditionDef { Signal = "sec.secureboot.on", Is = Tri.True }],
                        },
                    ],
                    DocsPath = "docs/guides/secure-boot.md",
                    OutcomeAskKey = "outcome.game_launches",
                },
            ],
            Questions =
            [
                new QuestionDef
                {
                    Id = "ask.only_valorant_affected",
                    TextKey = "ask.only_valorant_affected.text",
                    Kind = QuestionKind.YesNo,
                    Options = [YesOption("ask.only_valorant_affected"), NoOption("ask.only_valorant_affected"), UnsureOption("ask.only_valorant_affected")],
                    Symptoms = ["stutter"],
                },
                new QuestionDef
                {
                    Id = "ask.league_also_broken",
                    TextKey = "ask.league_also_broken.text",
                    Kind = QuestionKind.YesNo,
                    Intake = true,
                    Options = [YesOption("ask.league_also_broken"), NoOption("ask.league_also_broken"), UnsureOption("ask.league_also_broken")],
                },
            ],
            Tables =
            [
                new TableDef
                {
                    Id = "bios_secureboot_paths",
                    KeyColumn = "vendor",
                    Rows =
                    [
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["vendor"] = "ASUSTeK COMPUTER INC.", ["key"] = "Del", ["path"] = "Advanced Mode > Boot > Secure Boot > OS Type: Windows UEFI mode" },
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["vendor"] = "Micro-Star International Co., Ltd.", ["key"] = "Del", ["path"] = "Settings > Security > Secure Boot > Enabled" },
                    ],
                },
            ],
        };

        kb.WithText(VTD.Contracts.Languages.English, "rule.realtek25.title", "Your unused wired network port (Realtek 2.5G) is causing the stutters")
          .WithText(VTD.Contracts.Languages.Turkish, "rule.realtek25.title", "Kullanmadığın kablolu ağ girişi (Realtek 2.5G) takılmalara yol açıyor")
          .WithText(VTD.Contracts.Languages.English, "rule.nahimic.title", "Nahimic audio software is blocking VALORANT at launch")
          .WithText(VTD.Contracts.Languages.English, "rule.van9003.title", "Secure Boot is off (VAN 9003)")
          .WithText(VTD.Contracts.Languages.English, "rule.realtek25.ev.resets", "{count} Realtek driver resets in 14 days")
          .WithText(VTD.Contracts.Languages.English, "fix.disable_adapter.title", "Turn off the network port you're not using")
          .WithText(VTD.Contracts.Languages.English, "fix.hags_off.title", "Turn off hardware-accelerated GPU scheduling")
          .WithText(VTD.Contracts.Languages.English, "fix.hags_off.explain", "Some drivers stutter with this Windows graphics setting on. It takes effect after a restart.")
          .WithText(VTD.Contracts.Languages.English, "fix.hags_off.undo", "Turns the setting back to how it was (after a restart).")
          .WithText(VTD.Contracts.Languages.Turkish, "fix.hags_off.title", "Donanım hızlandırmalı GPU zamanlamasını kapat")
          .WithText(VTD.Contracts.Languages.English, "fix.disable_adapter.explain", "We'll turn off the wired network port you're not using. You're on Wi-Fi, so your internet stays on. One click turns it back on.")
          .WithText(VTD.Contracts.Languages.Turkish, "fix.disable_adapter.explain", "Kullanmadığın kablolu ağ girişini kapatacağız. Wi-Fi'dasın, internetin kesilmez. Tek tıkla geri açabilirsin.")
          .WithText(VTD.Contracts.Languages.English, "fix.disable_adapter.manual.0", "Right-click the Start button and open \"Device Manager\".")
          .WithText(VTD.Contracts.Languages.Turkish, "fix.disable_adapter.manual.0", "Başlat düğmesine sağ tıkla ve \"Aygıt Yöneticisi\"ni aç.")
          .WithText(VTD.Contracts.Languages.English, "fix.disable_adapter.manual.1", "Open \"Network adapters\" and find the Realtek adapter VTDiag named in your report.")
          .WithText(VTD.Contracts.Languages.Turkish, "fix.disable_adapter.manual.1", "\"Ağ bağdaştırıcıları\"nı aç ve VTDiag raporunda adı geçen Realtek bağdaştırıcısını bul.")
          .WithText(VTD.Contracts.Languages.English, "fix.disable_adapter.manual.2", "Right-click it, choose \"Disable device\" and click \"Yes\".")
          .WithText(VTD.Contracts.Languages.Turkish, "fix.disable_adapter.manual.2", "Ona sağ tıkla, \"Cihazı devre dışı bırak\"ı seç ve \"Evet\"e bas.")
          .WithText(VTD.Contracts.Languages.English, "guide.secureboot.title", "Turn on Secure Boot in your BIOS")
          .WithText(VTD.Contracts.Languages.English, "report.disclaimer.not_affiliated", "VTDiag is not affiliated with Riot Games.");
        return kb;
    }

    private static QuestionOption YesOption(string q) => new() { Id = "yes", TextKey = q + ".yes", Sets = Tri.True };

    private static QuestionOption NoOption(string q) => new() { Id = "no", TextKey = q + ".no", Sets = Tri.False };

    private static QuestionOption UnsureOption(string q) => new() { Id = "unsure", TextKey = q + ".unsure", Sets = Tri.Unknown };
}
