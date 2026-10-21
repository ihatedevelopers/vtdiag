using VTD.Contracts.Fixes;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Analysis;

/// <summary>Input of one analysis. Deterministic: same input, same result (the id and time come in; nothing is generated inside).</summary>
public sealed record AnalysisInput
{
    /// <summary>Id the result gets (ULID from the caller's <see cref="IIdGenerator"/>).</summary>
    public required string AnalysisId { get; init; }

    /// <summary>Snapshot.</summary>
    public required ScanSnapshot Snapshot { get; init; }

    /// <summary>Answers.</summary>
    public IntakeAnswers Intake { get; init; } = IntakeAnswers.Empty;

    /// <summary>Monitor summary of the latest watched match.</summary>
    public MonitorSummary? Monitor { get; init; }

    /// <summary>Fix attempts on this PC; a verified fix whose outcome is NotFixed is negative evidence.</summary>
    public IReadOnlyList<FixAttempt> History { get; init; } = [];

    /// <summary>Analysis time (becomes <see cref="AnalysisResult.CreatedUtc"/>; waves are evaluated on this date).</summary>
    public required DateTimeOffset AsOfUtc { get; init; }

    /// <summary>Questions already asked in this session.</summary>
    public int QuestionsAsked { get; init; }

    /// <summary>Fix ids the player skipped (not proposed again unless asked).</summary>
    public IReadOnlyList<string> SkippedFixIds { get; init; } = [];
}

/// <summary>Ranks hypotheses (A2).</summary>
public interface IAnalyzer
{
    /// <summary>Analyzes.</summary>
    /// <param name="input">Input.</param>
    AnalysisResult Analyze(AnalysisInput input);
}

/// <summary>
/// The analysis: independent multi-label posteriors (two real problems can co-exist; no cross-category penalty), the
/// fixed unknown-cause floor, the honesty buckets, a health checklist that has Unknown items, at most one next question,
/// and fix proposals ordered by utility. Schema: part of <c>kb/schema/report.schema.json</c>.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record AnalysisResult
{
    /// <summary>ULID.</summary>
    public required string AnalysisId { get; init; }

    /// <summary>Snapshot analyzed.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>KB version used.</summary>
    public required string KbVersion { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Surviving hypotheses, highest posterior first. Posteriors are independent and do not sum to 1.</summary>
    public required IReadOnlyList<RankedHypothesis> Hypotheses { get; init; }

    /// <summary>The "unknown cause" share.</summary>
    public required UnknownCause UnknownCause { get; init; }

    /// <summary>A short-circuit verdict (VAN 152, unsupported platform, account-side...). When set, no fix is proposed.</summary>
    public RouteVerdict? Route { get; init; }

    /// <summary>The bucket the report leads with.</summary>
    public required HonestyBucket PrimaryBucket { get; init; }

    /// <summary>Every bucket that has at least one likely hypothesis.</summary>
    public IReadOnlyList<BucketSummary> Buckets { get; init; } = [];

    /// <summary>Health baseline, computed regardless of the complaint.</summary>
    public required HealthChecklist Health { get; init; }

    /// <summary>The next question, when the top two are within the margin and a question separates them.</summary>
    public QuestionPrompt? NextQuestion { get; init; }

    /// <summary>Questions asked so far in the session.</summary>
    public int QuestionsAsked { get; init; }

    /// <summary>Fix proposals by utility = P(h) × P(fix works | h) / cost.</summary>
    public IReadOnlyList<FixProposal> Proposals { get; init; } = [];

    /// <summary>Guides worth showing.</summary>
    public IReadOnlyList<GuideProposal> Guides { get; init; } = [];

    /// <summary>Hypotheses ruled out, with the reason (shown under "ruled out because…").</summary>
    public IReadOnlyList<RuledOut> RuledOut { get; init; } = [];

    /// <summary>"We could not check X": Unknown signals that mattered to some hypothesis.</summary>
    public IReadOnlyList<CouldNotCheck> CouldNotCheck { get; init; } = [];

    /// <summary>Unexplained findings ("help the KB grow"): never raise a hypothesis.</summary>
    public IReadOnlyList<AnomalyFinding> Anomalies { get; init; } = [];

    /// <summary>Codes considered (typed and logged) and what they matched.</summary>
    public IReadOnlyList<CodeUse> Codes { get; init; } = [];
}

/// <summary>One ranked hypothesis with its explanation.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RankedHypothesis
{
    /// <summary>Rule id.</summary>
    public required string Id { get; init; }

    /// <summary>Rule revision.</summary>
    public int Rev { get; init; } = 1;

    /// <summary>Category.</summary>
    public required ProblemCategory Category { get; init; }

    /// <summary>Title key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Independent posterior probability 0..1.</summary>
    public required double Posterior { get; init; }

    /// <summary>Final log-odds.</summary>
    public required double LogOdds { get; init; }

    /// <summary>Prior log-odds.</summary>
    public required double PriorLogOdds { get; init; }

    /// <summary>Display band.</summary>
    public required ConfidenceBand Band { get; init; }

    /// <summary>Honesty bucket (may be downgraded from FixableHere when no fix applies, for example in limited mode).</summary>
    public required HonestyBucket Bucket { get; init; }

    /// <summary>Per-evidence contributions (helpers see the math; players see the top 3 bullets).</summary>
    public IReadOnlyList<EvidenceContribution> Contributions { get; init; } = [];

    /// <summary>Log-odds from the code table.</summary>
    public double CodeLogOdds { get; init; }

    /// <summary>Log-odds from an active wave.</summary>
    public double WaveLogOdds { get; init; }

    /// <summary>Negative evidence from failed fixes.</summary>
    public double FailedFixLogOdds { get; init; }

    /// <summary>The evidence cap was applied.</summary>
    public bool CapApplied { get; init; }

    /// <summary>Entities involved (bind fixes).</summary>
    public IReadOnlyList<EntityRef> Entities { get; init; } = [];

    /// <summary>Applicable fix ids.</summary>
    public IReadOnlyList<string> FixIds { get; init; } = [];

    /// <summary>Applicable guide ids.</summary>
    public IReadOnlyList<string> GuideIds { get; init; } = [];
}

/// <summary>One evidence item's effect on a hypothesis.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record EvidenceContribution
{
    /// <summary>Evidence id in the rule.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Signal id.</summary>
    public required string SignalId { get; init; }

    /// <summary>Signal state. Unknown and NotApplicable always have <see cref="DeltaLogOdds"/> 0.</summary>
    public required Tri State { get; init; }

    /// <summary>Signal strength.</summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>Log-odds added.</summary>
    public required double DeltaLogOdds { get; init; }

    /// <summary>Correlation group.</summary>
    public string? Group { get; init; }

    /// <summary>False when a stronger item of the same group took precedence.</summary>
    public bool Counted { get; init; } = true;

    /// <summary>The "why" text key.</summary>
    public string? TextKey { get; init; }

    /// <summary>Evidence from the signal.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

/// <summary>The unknown-cause pseudo-hypothesis (formula on <see cref="RankingConstants.UnknownCauseFloor"/>).</summary>
public sealed record UnknownCause
{
    /// <summary>The fixed floor (<see cref="RankingConstants.UnknownCauseFloor"/>).</summary>
    public required double Floor { get; init; }

    /// <summary><see cref="RankingConstants.UnknownCauseProbability"/> of the surviving posteriors.</summary>
    public required double Probability { get; init; }

    /// <summary>It ranks first, or the top hypothesis is below <see cref="RankingConstants.PinDownThreshold"/>:
    /// the UI says "We couldn't pin this down" and offers the escalation ladder.</summary>
    public required bool Leading { get; init; }
}

/// <summary>A route verdict.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RouteVerdict
{
    /// <summary>Route.</summary>
    public required RouteKind Kind { get; init; }

    /// <summary>Code id or rule id that triggered it.</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>Text key ("This needs Riot's appeal, there's nothing to fix on your PC").</summary>
    public required string TextKey { get; init; }

    /// <summary>Official links.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>Bucket.</summary>
    public required HonestyBucket Bucket { get; init; }
}

/// <summary>Hypotheses grouped by honesty bucket.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record BucketSummary
{
    /// <summary>Bucket.</summary>
    public required HonestyBucket Bucket { get; init; }

    /// <summary>Hypothesis ids in it, highest first.</summary>
    public required IReadOnlyList<string> HypothesisIds { get; init; }

    /// <summary>Highest posterior in it.</summary>
    public required double MaxPosterior { get; init; }
}

/// <summary>Health checklist group.</summary>
public enum HealthGroup
{
    /// <summary>TPM 2.0, Secure Boot, UEFI/GPT, HVCI/VBS, IOMMU, virtualization/NX, build, test-signing.</summary>
    VanguardReadiness = 0,

    /// <summary>Windows build, pending reboot, clock, modified image.</summary>
    Windows = 1,

    /// <summary>Driver problem codes, GPU driver.</summary>
    Drivers = 2,

    /// <summary>Connectivity, adapters.</summary>
    Network = 3,

    /// <summary>Known conflicting background apps.</summary>
    BackgroundApps = 4,

    /// <summary>Disk space, dirty bit.</summary>
    Storage = 5,

    /// <summary>Required services (vgc/vgk configured, TBS, Winmgmt...).</summary>
    Services = 6,
}

/// <summary>Health item status. Unknown is shown as "could not check", never as OK.</summary>
public enum HealthStatus
{
    /// <summary>Healthy.</summary>
    Ok = 0,

    /// <summary>Worth attention.</summary>
    Warn = 1,

    /// <summary>Fails a requirement.</summary>
    Fail = 2,

    /// <summary>Could not check (reason in <see cref="HealthItem.UnknownReason"/>).</summary>
    Unknown = 3,

    /// <summary>Not applicable.</summary>
    NotApplicable = 4,
}

/// <summary>The health baseline. The UI counts statuses; "Good news: 14 things look healthy."</summary>
public sealed record HealthChecklist
{
    /// <summary>Items.</summary>
    public required IReadOnlyList<HealthItem> Items { get; init; }
}

/// <summary>One health item.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record HealthItem
{
    /// <summary>Stable id, for example <c>vanguard.tpm2</c>, <c>vanguard.secure_boot</c>, <c>windows.pending_reboot</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Group.</summary>
    public required HealthGroup Group { get; init; }

    /// <summary>Status.</summary>
    public required HealthStatus Status { get; init; }

    /// <summary>Title key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Detail key.</summary>
    public string? DetailKey { get; init; }

    /// <summary>Placeholder values.</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Why it is Unknown.</summary>
    public UnknownReason? UnknownReason { get; init; }

    /// <summary>Signal it rests on.</summary>
    public string? SignalId { get; init; }

    /// <summary>Fix that addresses it.</summary>
    public string? FixId { get; init; }

    /// <summary>Guide that addresses it.</summary>
    public string? GuideId { get; init; }
}

/// <summary>A question the planner wants to ask.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record QuestionPrompt
{
    /// <summary>Question id.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Hypotheses it separates.</summary>
    public required IReadOnlyList<string> Separates { get; init; }

    /// <summary>Current log-odds margin between them.</summary>
    public required double Margin { get; init; }
}

/// <summary>A proposed fix with a concrete binding.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixProposal
{
    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Hypotheses it addresses.</summary>
    public required IReadOnlyList<string> HypothesisIds { get; init; }

    /// <summary>Entities it would act on.</summary>
    public required FixBinding Binding { get; init; }

    /// <summary>Utility.</summary>
    public required double Utility { get; init; }

    /// <summary>Expected benefit Σ P(h) × P(fix works | h).</summary>
    public required double ExpectedBenefit { get; init; }

    /// <summary>Cost = 1 + 2·reboot + 3·medium risk + 5·one-way + minutes/5.</summary>
    public required double Cost { get; init; }

    /// <summary>Reason key ("It's quick, harmless and undoable, so we'll try it before the bigger stuff").</summary>
    public required string ReasonKey { get; init; }

    /// <summary>Effective risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>Effective reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Reboot need.</summary>
    public required RebootNeed Reboot { get; init; }

    /// <summary>Needs the elevated engine.</summary>
    public required bool NeedsAdmin { get; init; }

    /// <summary>Can be applied now (false in limited mode, when a guard is known to fail, or when the route forbids it).</summary>
    public required bool Available { get; init; }

    /// <summary>Why it is not available.</summary>
    public string? UnavailableReasonKey { get; init; }
}

/// <summary>A guide worth showing.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuideProposal
{
    /// <summary>Guide id.</summary>
    public required string GuideId { get; init; }

    /// <summary>Hypotheses it addresses.</summary>
    public IReadOnlyList<string> HypothesisIds { get; init; } = [];

    /// <summary>The branch whose conditions hold, when one does.</summary>
    public string? BranchId { get; init; }
}

/// <summary>Why a hypothesis was ruled out.</summary>
public enum RuledOutReason
{
    /// <summary>A <c>requires</c> signal was definitely False.</summary>
    RequiresFalse = 0,

    /// <summary>An evidence veto fired.</summary>
    Vetoed = 1,

    /// <summary>An <c>excludes</c> signal was definitely True.</summary>
    Excluded = 2,

    /// <summary>Superseded by a more specific rule.</summary>
    Superseded = 3,
}

/// <summary>A ruled-out hypothesis.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RuledOut
{
    /// <summary>Rule id.</summary>
    public required string HypothesisId { get; init; }

    /// <summary>Reason.</summary>
    public required RuledOutReason Reason { get; init; }

    /// <summary>Signal or rule that caused it.</summary>
    public string? By { get; init; }
}

/// <summary>Something we could not check that mattered.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CouldNotCheck
{
    /// <summary>Signal id.</summary>
    public required string SignalId { get; init; }

    /// <summary>Reason.</summary>
    public required UnknownReason Reason { get; init; }

    /// <summary>Text key of WHAT could not be checked (the signal's title, for example "Secure Boot keys"). The reason
    /// sentence comes from <see cref="Reason"/> (<c>reason.&lt;camelCase&gt;</c>), never from this key.</summary>
    public string? TextKey { get; init; }

    /// <summary>Hypotheses whose ranking this leaves open.</summary>
    public IReadOnlyList<string> AffectsHypotheses { get; init; } = [];
}

/// <summary>Kind of unexplained finding.</summary>
public enum AnomalyKind
{
    /// <summary>A faulting module in a Riot/Vanguard crash that no conflict entry names.</summary>
    UnknownFaultingModule = 0,

    /// <summary>A driver CodeIntegrity blocked or that is HVCI-incompatible and unknown to the KB.</summary>
    BlockedDriver = 1,

    /// <summary>A non-Microsoft Winsock LSP.</summary>
    NonMicrosoftLsp = 2,

    /// <summary>A service crash-looping (SCM 7031/7034 more than 3 times in 7 days).</summary>
    CrashLoopingService = 3,

    /// <summary>A PnP device with a problem code.</summary>
    DeviceProblemCode = 4,

    /// <summary>An unsigned kernel driver loaded.</summary>
    UnsignedDriver = 5,
}

/// <summary>An unexplained finding for the report's "help the KB grow" section.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record AnomalyFinding
{
    /// <summary>Kind.</summary>
    public required AnomalyKind Kind { get; init; }

    /// <summary>Subject (module, driver, service or LSP file name; device class).</summary>
    public required string Subject { get; init; }

    /// <summary>Evidence.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

/// <summary>How an error code entered the analysis.</summary>
public enum CodeOrigin
{
    /// <summary>Typed by the player (weighs less).</summary>
    Typed = 0,

    /// <summary>Found in logs or crash reports.</summary>
    Logged = 1,
}

/// <summary>A code the analysis considered.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CodeUse
{
    /// <summary>Normalized code.</summary>
    public required string Code { get; init; }

    /// <summary>Origin.</summary>
    public required CodeOrigin Origin { get; init; }

    /// <summary>Matched table row id, null when unknown to the KB.</summary>
    public string? MatchedId { get; init; }
}
