using System.Text.Json.Serialization;

namespace VTD.Contracts.Knowledge;

/// <summary>What a rule describes.</summary>
public enum RuleKind
{
    /// <summary>A cause that may be present (multi-label: several can be true at once).</summary>
    Hypothesis = 0,

    /// <summary>A "not fixable here" verdict (HWID ban, unsupported platform...). Short-circuits ranking.</summary>
    Route = 1,

    /// <summary>A low-prior hypothesis generated from a <see cref="ConflictDef"/> by <c>tools/kb_builder.py</c>.</summary>
    Generated = 2,
}

/// <summary>
/// A hypothesis rule: weighted references to signals (design_B.md section 4.2) evaluated with three-valued logic
/// (design_A.md section 6.3). File: <c>kb/rules/&lt;category&gt;/&lt;id&gt;.json</c>. Schema: <c>kb/schema/rule.schema.json</c>.
/// </summary>
/// <remarks>
/// Error-code strength is NOT declared here: it lives in the code table (<see cref="ErrorCodeDef.PointsTo"/>), so one
/// code can point at several causes with different strengths and typed codes weigh less than logged ones.
/// There is no expression language (DESIGN.md: not before v0.3); new raw facts come from pack-declared collectors.
/// </remarks>
[Sensitivity(Sensitivity.None)]
public sealed record RuleDef
{
    /// <summary>Stable kebab-case id (catalog id), never reused; deprecate instead.</summary>
    public required string Id { get; init; }

    /// <summary>Revision; bumped on every change (outcome cards record <c>id@rev</c>).</summary>
    public int Rev { get; init; } = 1;

    /// <summary>Kind.</summary>
    public RuleKind Kind { get; init; } = RuleKind.Hypothesis;

    /// <summary>Catalog category (sets the flat smoothed prior group).</summary>
    public required ProblemCategory Category { get; init; }

    /// <summary>String key of the title.</summary>
    public required string TitleKey { get; init; }

    /// <summary>String key of the one-paragraph summary.</summary>
    public string? SummaryKey { get; init; }

    /// <summary>The honesty bucket this cause falls into when it is the answer (DESIGN.md section 1).</summary>
    public HonestyBucket Bucket { get; init; } = HonestyBucket.FixableHere;

    /// <summary>For <see cref="RuleKind.Route"/>: the route.</summary>
    public RouteKind? Route { get; init; }

    /// <summary>Symptom ids (from <see cref="SymptomDef"/>) this cause explains; chips set priors, never veto.</summary>
    public IReadOnlyList<string> Symptoms { get; init; } = [];

    /// <summary>Prior inputs.</summary>
    public required PriorDef Prior { get; init; }

    /// <summary>Hard gates: signal ids that must not be definitely False. Unknown never fires a gate.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Exclusions: if any of these signals is definitely True, the hypothesis is ruled out.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>Weighted evidence.</summary>
    public IReadOnlyList<EvidenceDef> Evidence { get; init; } = [];

    /// <summary>Date-limited outbreak boost (Nahimic, Bonjour, hook waves).</summary>
    public WaveDef? Wave { get; init; }

    /// <summary>Candidate fix ids, KB-preferred order.</summary>
    public IReadOnlyList<string> Fixes { get; init; } = [];

    /// <summary>Guide ids.</summary>
    public IReadOnlyList<string> Guides { get; init; } = [];

    /// <summary>Question ids that may be asked to separate this hypothesis from a close rival.</summary>
    public IReadOnlyList<string> Questions { get; init; } = [];

    /// <summary>Rule ids this rule replaces when both fire (for example Nahimic supersedes <c>conflict-nahimic</c>).</summary>
    public IReadOnlyList<string> Supersedes { get; init; } = [];

    /// <summary>Related rule ids (UI "see also").</summary>
    public IReadOnlyList<string> Related { get; init; } = [];

    /// <summary>Provenance of the rule.</summary>
    public RuleSources? Sources { get; init; }

    /// <summary>Set when the rule is retired.</summary>
    public DeprecationDef? Deprecated { get; init; }
}

/// <summary>Prior inputs. Priors are a flat smoothed share per category plus the fixed unknown-cause floor.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PriorDef
{
    /// <summary>Solved threads (<c>thread_count</c> in <c>data/catalog.json</c>); 0 for generated rules.</summary>
    public required int Threads { get; init; }

    /// <summary>Explicit prior probability override (rare; needs <see cref="OverrideReason"/>).</summary>
    public double? Override { get; init; }

    /// <summary>Why the override exists.</summary>
    public string? OverrideReason { get; init; }
}

/// <summary>
/// One weighted evidence item. Weights are log-odds added when the signal is definitely True or definitely False.
/// There is deliberately no weight for Unknown: evidence that rests on an Unknown fact contributes nothing.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record EvidenceDef
{
    /// <summary>Stable id within the rule.</summary>
    public required string Id { get; init; }

    /// <summary>Signal id.</summary>
    public required string Signal { get; init; }

    /// <summary>Log-odds added when the signal is True (scaled by the signal strength 0..1).</summary>
    [JsonPropertyName("true")]
    public double? WhenTrue { get; init; }

    /// <summary>Log-odds added when the signal is False.</summary>
    [JsonPropertyName("false")]
    public double? WhenFalse { get; init; }

    /// <summary>Rule out the hypothesis when the signal is definitely this value. Unknown never vetoes.</summary>
    public VetoWhen? Veto { get; init; }

    /// <summary>Correlated evidence group: only the item with the largest |delta| in a group counts.</summary>
    public string? Group { get; init; }

    /// <summary>Question id the planner may ask when this signal is Unknown.</summary>
    public string? Ask { get; init; }

    /// <summary>String key for the "why we think so" bullet; args come from the signal's evidence.</summary>
    public string? TextKey { get; init; }

    /// <summary>Count this evidence only when one of these symptoms was chosen.</summary>
    public IReadOnlyList<string> OnlyIfSymptoms { get; init; } = [];
}

/// <summary>The definite signal value that vetoes a hypothesis (there is deliberately no Unknown member).</summary>
public enum VetoWhen
{
    /// <summary>Veto when the signal is definitely True.</summary>
    True = 0,

    /// <summary>Veto when the signal is definitely False.</summary>
    False = 1,
}

/// <summary>A date-limited outbreak.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record WaveDef
{
    /// <summary>First day.</summary>
    public required DateOnly From { get; init; }

    /// <summary>Last day.</summary>
    public required DateOnly To { get; init; }

    /// <summary>Log-odds added while the analysis date is inside the window.</summary>
    public required double Boost { get; init; }
}

/// <summary>Where a rule comes from.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RuleSources
{
    /// <summary>Catalog id in <c>data/catalog.json</c>.</summary>
    public string? Catalog { get; init; }

    /// <summary>Solved threads.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Best evidence label (<c>op_confirmed</c>, <c>commenter_suggested</c>...).</summary>
    public string? Evidence { get; init; }

    /// <summary>Example Reddit thread ids.</summary>
    public IReadOnlyList<string> ThreadIds { get; init; } = [];

    /// <summary>Official or reference URLs.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];
}

/// <summary>Deprecation marker.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DeprecationDef
{
    /// <summary>KB version that deprecated it.</summary>
    public required string Since { get; init; }

    /// <summary>Replacement id.</summary>
    public string? ReplacedBy { get; init; }
}

/// <summary>Where a signal is computed.</summary>
public enum SignalSource
{
    /// <summary>A C# <c>ISignalProvider</c> in VTD.Analysis (one per catalog check id and more).</summary>
    Builtin = 0,

    /// <summary>Derived from a pack-declared collector result (<see cref="SignalDef.FromCollector"/>), no recompile.</summary>
    Collector = 1,

    /// <summary>Generated per <see cref="ConflictDef"/>: <c>conflict.&lt;id&gt;.installed|running|driver_loaded|adapter_present|crash_module|service_present</c>.</summary>
    Conflict = 2,

    /// <summary>From the live monitor summary (<c>mon.*</c>).</summary>
    Monitor = 3,

    /// <summary>From an intake answer (<c>ask.*</c>).</summary>
    Ask = 4,

    /// <summary>From the error-code table (<c>code.*</c>).</summary>
    Code = 5,
}

/// <summary>
/// Declares a signal id so the KB lint can check every reference, and (for collector signals) how it is derived.
/// File: <c>kb/signals/*.json</c>. Schema: <c>kb/schema/signal.schema.json</c>.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record SignalDef
{
    /// <summary>Dotted id, for example <c>events.realtek_nic_resets</c>, <c>sec.secureboot.on</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Where it is computed.</summary>
    public required SignalSource Source { get; init; }

    /// <summary>String key used for "could not check X" and the helper's fact browser.</summary>
    public string? TitleKey { get; init; }

    /// <summary>Entity kind the signal returns (used to bind fixes).</summary>
    public EntityKind? Entity { get; init; }

    /// <summary>Needs an elevated scan; Unknown(NeedsAdmin) in limited mode.</summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>For <see cref="SignalSource.Collector"/>: the derivation.</summary>
    public CollectorSignalSpec? FromCollector { get; init; }

    /// <summary>For <see cref="SignalSource.Ask"/>: the question id.</summary>
    public string? Question { get; init; }
}

/// <summary>
/// Derives a Tri signal from a pack collector: True when at least <see cref="MinCount"/> items match (within
/// <see cref="WithinDays"/> and <see cref="FieldEquals"/>), False when the collector ran and fewer match, Unknown when
/// the collector result is Unknown. Strength = min(1, count / <see cref="SaturateAt"/>).
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record CollectorSignalSpec
{
    /// <summary>Collector id.</summary>
    public required string Collector { get; init; }

    /// <summary>Minimum matching items for True.</summary>
    public int MinCount { get; init; } = 1;

    /// <summary>Only count items newer than this many days before the scan.</summary>
    public int? WithinDays { get; init; }

    /// <summary>Only count items whose fields equal these values (ordinal, case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> FieldEquals { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Count at which strength reaches 1.0 (default: <see cref="MinCount"/>).</summary>
    public int? SaturateAt { get; init; }
}

/// <summary>Event level filter for event-log collectors.</summary>
public enum EventLevel
{
    /// <summary>Critical (1).</summary>
    Critical = 1,

    /// <summary>Error (2).</summary>
    Error = 2,

    /// <summary>Warning (3).</summary>
    Warning = 3,

    /// <summary>Information (4).</summary>
    Information = 4,

    /// <summary>Verbose (5).</summary>
    Verbose = 5,
}

/// <summary>Registry hive a pack collector may read.</summary>
public enum RegistryHiveScope
{
    /// <summary>HKLM.</summary>
    Hklm = 0,

    /// <summary>The interactive player's HKCU (via HKU\&lt;player SID&gt;, confirmed from the pipe, not claimed).</summary>
    HkcuPlayer = 1,
}

/// <summary>
/// A pack-declared READ-ONLY collector (design_A.md section 6.5, DESIGN.md section 2): lets mods ship a new conflict wave
/// as data with no release. Exactly six kinds; anything else needs a C# probe. File: <c>kb/collectors/*.json</c>.
/// Limits are in <see cref="PackCollectorLimits"/>; A1's executor and A2's lint both enforce them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EventLogCollectorDef), "eventLog")]
[JsonDerivedType(typeof(UninstallRegexCollectorDef), "uninstallRegex")]
[JsonDerivedType(typeof(LoadedDriverCollectorDef), "loadedDriver")]
[JsonDerivedType(typeof(FaultingModuleCollectorDef), "faultingModule")]
[JsonDerivedType(typeof(RegistryValueCollectorDef), "registryValue")]
[JsonDerivedType(typeof(FileExistsCollectorDef), "fileExists")]
[Sensitivity(Sensitivity.None)]
public abstract record CollectorDef
{
    /// <summary>Stable id (snake_case), referenced by <see cref="CollectorSignalSpec.Collector"/>.</summary>
    public required string Id { get; init; }

    /// <summary>String key describing what it looks for.</summary>
    public string? DescriptionKey { get; init; }
}

/// <summary>
/// Event-log provider/id query. Items: one per event with <c>utc</c>, <c>provider</c>, <c>eventId</c>, <c>level</c> and the
/// projected EventData fields. No rendered (localized) messages.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record EventLogCollectorDef : CollectorDef
{
    /// <summary>Log name, for example <c>System</c>, <c>Application</c>, <c>Microsoft-Windows-CodeIntegrity/Operational</c>.</summary>
    public required string Log { get; init; }

    /// <summary>Provider names (any).</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>Event ids (any). Empty = all ids of the providers.</summary>
    public IReadOnlyList<int> EventIds { get; init; } = [];

    /// <summary>Levels (any). Empty = all.</summary>
    public IReadOnlyList<EventLevel> Levels { get; init; } = [];

    /// <summary>Look-back window (days, at most <see cref="PackCollectorLimits.MaxSinceDays"/>).</summary>
    public int SinceDays { get; init; } = 14;

    /// <summary>Max events read (at most <see cref="PackCollectorLimits.MaxEvents"/>).</summary>
    public int Max { get; init; } = 200;

    /// <summary>EventData field names to keep.</summary>
    public IReadOnlyList<string> Project { get; init; } = [];
}

/// <summary>Uninstall-key regex (HKLM, WOW6432Node, player HKCU). Items: <c>displayName</c>, <c>publisher</c>, <c>version</c>, <c>hive</c>.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record UninstallRegexCollectorDef : CollectorDef
{
    /// <summary>.NET regex on DisplayName (case-insensitive, NonBacktracking, 50 ms timeout).</summary>
    public required string DisplayNameRegex { get; init; }

    /// <summary>Optional regex on Publisher.</summary>
    public string? PublisherRegex { get; init; }
}

/// <summary>Loaded kernel driver by name. Items: <c>name</c>, <c>version</c>, <c>signer</c>.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record LoadedDriverCollectorDef : CollectorDef
{
    /// <summary>Driver file names (ordinal, case-insensitive).</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>Optional regex on the file name.</summary>
    public string? NameRegex { get; init; }
}

/// <summary>Faulting-module match on Application Error events. Items: <c>utc</c>, <c>exe</c>, <c>module</c>, <c>exceptionCode</c>.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FaultingModuleCollectorDef : CollectorDef
{
    /// <summary>Module file names (ordinal, case-insensitive).</summary>
    public required IReadOnlyList<string> Modules { get; init; }

    /// <summary>Crashing executables to consider; empty = <see cref="RiotProcessNames.All"/>.</summary>
    public IReadOnlyList<string> Exes { get; init; } = [];

    /// <summary>Look-back window (days).</summary>
    public int SinceDays { get; init; } = 30;
}

/// <summary>Registry value read. Items: <c>name</c>, <c>kind</c>, <c>data</c> (invariant string) per value; an absent key yields Count 0.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RegistryValueCollectorDef : CollectorDef
{
    /// <summary>Hive.</summary>
    public required RegistryHiveScope Hive { get; init; }

    /// <summary>Key path under the hive. Must start with one of <see cref="PackCollectorLimits.AllowedRegistryRoots"/>
    /// (after <c>ControlSetNNN</c> → <c>CurrentControlSet</c> normalization) and must not hit a denied prefix.</summary>
    public required string Key { get; init; }

    /// <summary>Value names; empty = all values (at most <see cref="PackCollectorLimits.MaxRegistryValues"/>).</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
}

/// <summary>File existence/metadata (never content). Items: <c>exists</c>, <c>size</c>, <c>mtimeUtc</c>, <c>version</c>, <c>signer</c>.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FileExistsCollectorDef : CollectorDef
{
    /// <summary>Path template starting with one of <see cref="PackCollectorLimits.AllowedPathTokens"/>; no <c>..</c>
    /// segments, no wildcards, no UNC or device paths.</summary>
    public required string Path { get; init; }
}

/// <summary>Limits on pack-declared collectors, shared by A1's executor and A2's KB lint.</summary>
public static class PackCollectorLimits
{
    /// <summary>Max look-back for event and crash collectors (days).</summary>
    public const int MaxSinceDays = 30;

    /// <summary>Max events read per event-log collector.</summary>
    public const int MaxEvents = 1000;

    /// <summary>Max items kept per collector result.</summary>
    public const int MaxItems = 50;

    /// <summary>Max values per registry collector.</summary>
    public const int MaxRegistryValues = 50;

    /// <summary>Max length of any string kept in a pack collector item (longer values are truncated and flagged).</summary>
    public const int MaxValueLength = 256;

    /// <summary>
    /// Registry roots a pack collector may read (ordinal, case-insensitive prefixes; HKLM unless noted). An allowlist:
    /// anything else needs a C# probe. <c>ControlSet001</c>..<c>ControlSet999</c> are normalized to <c>CurrentControlSet</c>
    /// before matching; <see cref="DeniedRegistryPrefixes"/> still applies inside these roots.
    /// </summary>
    public static IReadOnlyList<string> AllowedRegistryRoots { get; } =
    [
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
        "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags",
        "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
        "SOFTWARE\\Microsoft\\Windows Defender",
        "SOFTWARE\\Policies\\Microsoft",
        "SOFTWARE\\Microsoft\\WindowsSelfHost",
        "SOFTWARE\\Microsoft\\Windows\\Dwm",
        "SOFTWARE\\Riot Games",
        "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard",
        "SYSTEM\\CurrentControlSet\\Control\\CI",
        "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers",
        "SYSTEM\\CurrentControlSet\\Control\\SecureBoot",
        "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power",
        "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management",
        "SYSTEM\\CurrentControlSet\\Services",
        "HKCU:Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
        "HKCU:Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR",
        "HKCU:Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags",
        "HKCU:Software\\Microsoft\\DirectX",
        "HKCU:Software\\Microsoft\\GameBar",
        "HKCU:System\\GameConfigStore",
    ];

    /// <summary>Regex match timeout.</summary>
    public static TimeSpan RegexTimeout { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Path tokens a file collector may start with (expanded by A1 for the interactive player).</summary>
    public static IReadOnlyList<string> AllowedPathTokens { get; } =
    [
        "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%", "%SystemRoot%",
        "%PlayerLocalAppData%", "%PlayerAppData%", "%RiotClientDir%", "%ValorantDir%", "%VanguardDir%",
    ];

    /// <summary>Registry key prefixes a registry collector may never read, even inside an allowed root (ordinal,
    /// case-insensitive, after ControlSet normalization).</summary>
    public static IReadOnlyList<string> DeniedRegistryPrefixes { get; } =
    [
        "SAM", "SECURITY", "SYSTEM\\CurrentControlSet\\Control\\Lsa", "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
        "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList", "SOFTWARE\\Microsoft\\Cryptography",
        "SOFTWARE\\Riot Games\\Riot Vanguard\\Keys", "HKCU:Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\\Passport",
    ];
}
