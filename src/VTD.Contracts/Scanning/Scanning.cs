using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Scanning;

/// <summary>Scan breadth.</summary>
public enum ScanMode
{
    /// <summary>Every collector (target 30-60 s).</summary>
    Full = 0,

    /// <summary>Health baseline only (Vanguard readiness, pending reboot, disk, clock, drivers, services).</summary>
    Quick = 1,

    /// <summary>Only the collectors named in <see cref="ScanRequest.Collectors"/>.</summary>
    Targeted = 2,
}

/// <summary>Friendly area shown on the Scanning screen.</summary>
public enum ScanArea
{
    /// <summary>Windows version and state.</summary>
    Windows = 0,

    /// <summary>Hardware.</summary>
    Hardware = 1,

    /// <summary>Security and Vanguard requirements.</summary>
    Security = 2,

    /// <summary>Devices and drivers.</summary>
    Drivers = 3,

    /// <summary>Apps, services, processes, startup.</summary>
    Apps = 4,

    /// <summary>Network.</summary>
    Network = 5,

    /// <summary>Riot Client, VALORANT, Vanguard files.</summary>
    Game = 6,

    /// <summary>Event logs and game logs.</summary>
    Logs = 7,
}

/// <summary>What a collector needs to give full results.</summary>
[Flags]
public enum CollectorNeeds
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>Elevation; in limited mode it returns Unknown(NeedsAdmin) facts and <c>SkippedNoAdmin</c>/<c>Partial</c>.</summary>
    Admin = 1,

    /// <summary>Network access (probes). Skipped when the request disallows network.</summary>
    Network = 2,
}

/// <summary>A scan request.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ScanRequest
{
    /// <summary>Scan breadth.</summary>
    public ScanMode Mode { get; init; } = ScanMode.Full;

    /// <summary>Collector ids to run when <see cref="Mode"/> is Targeted.</summary>
    public IReadOnlyList<string> Collectors { get; init; } = [];

    /// <summary>Pack-declared read-only collectors from the KB (<see cref="IKnowledgeBase.Collectors"/>).</summary>
    public IReadOnlyList<CollectorDef> PackCollectors { get; init; } = [];

    /// <summary>Allow indicative network probes.</summary>
    public bool AllowNetworkProbes { get; init; } = true;

    /// <summary>Region endpoints to ping (indicative only; game servers move). Filled by the engine from the KB table
    /// <see cref="ProbeTarget.KbTableId"/> when present; at most <see cref="ProbeTarget.MaxTargets"/> are used. The gateway
    /// and one public DNS resolver are always probed when <see cref="AllowNetworkProbes"/> is true.</summary>
    public IReadOnlyList<ProbeTarget> ProbeTargets { get; init; } = [];

    /// <summary>Event-log window in days (default 14, max 30).</summary>
    public int EventWindowDays { get; init; } = 14;

    /// <summary>KB version that supplied <see cref="PackCollectors"/> (recorded in the snapshot meta).</summary>
    public required string KbVersion { get; init; }

    /// <summary>The interactive player whose per-user data is read (from the pipe, set by A4).</summary>
    public required PlayerContext Player { get; init; }

    /// <summary>UI language to record in the snapshot meta.</summary>
    public string AppLanguage { get; init; } = Languages.English;

    /// <summary>Record a raw capture for replay tests (<c>vtdiag scan --capture</c>; A's raw-capture fakes).</summary>
    public bool CaptureRaw { get; init; }
}

/// <summary>A region endpoint for an indicative latency probe (<see cref="ScanRequest.ProbeTargets"/>).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ProbeTarget
{
    /// <summary>KB table that lists region endpoints (columns <c>label</c>, <c>host</c>).</summary>
    public const string KbTableId = "ping_targets";

    /// <summary>Most region targets one scan pings (the rest are ignored).</summary>
    public const int MaxTargets = 6;

    /// <summary>Label shown in the report and stored in <see cref="Snapshots.PingProbe.Target"/>, for example <c>eu-central</c>.</summary>
    public required string Label { get; init; }

    /// <summary>IPv4 or IPv6 literal. No DNS names (the scan never resolves KB-supplied names); anything else is skipped.</summary>
    public required string Host { get; init; }
}

/// <summary>Progress of a running scan.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ScanProgress
{
    /// <summary>Collector that changed state.</summary>
    public required string CollectorId { get; init; }

    /// <summary>Area for the UI.</summary>
    public required ScanArea Area { get; init; }

    /// <summary>Null while running; the final status when done.</summary>
    public CollectorStatus? Status { get; init; }

    /// <summary>Collectors finished.</summary>
    public required int Completed { get; init; }

    /// <summary>Collectors in this scan.</summary>
    public required int Total { get; init; }
}

/// <summary>Context handed to every collector. Not serialized.</summary>
public sealed record CollectContext
{
    /// <summary>The request (its <see cref="ScanRequest.Player"/> says whose per-user data to read).</summary>
    public required ScanRequest Request { get; init; }

    /// <summary>Elevated or limited.</summary>
    public required PrivilegeMode Privilege { get; init; }

    /// <summary>Scan start time (all "last N days" windows are relative to this, not the wall clock).</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Time source.</summary>
    public required TimeProvider Clock { get; init; }
}

/// <summary>
/// The sections a collector produced. Each section is owned by exactly one collector; the orchestrator merges patches
/// and fills every section nobody produced with <c>X.Unknown(reason)</c>, so the snapshot is always complete.
/// </summary>
public sealed record SnapshotPatch
{
    /// <summary>Windows.</summary>
    public OsInfo? Os { get; init; }

    /// <summary>Hardware.</summary>
    public HardwareInfo? Hardware { get; init; }

    /// <summary>Firmware.</summary>
    public FirmwareInfo? Firmware { get; init; }

    /// <summary>Security.</summary>
    public SecurityState? Security { get; init; }

    /// <summary>Storage.</summary>
    public StorageInfo? Storage { get; init; }

    /// <summary>Devices.</summary>
    public Fact<IReadOnlyList<DeviceInfo>>? Devices { get; init; }

    /// <summary>Loaded drivers.</summary>
    public Fact<IReadOnlyList<LoadedDriver>>? Drivers { get; init; }

    /// <summary>Services.</summary>
    public Fact<IReadOnlyList<ServiceInfo>>? Services { get; init; }

    /// <summary>Processes.</summary>
    public Fact<IReadOnlyList<ProcessInfo>>? Processes { get; init; }

    /// <summary>Installed apps.</summary>
    public Fact<IReadOnlyList<InstalledApp>>? Apps { get; init; }

    /// <summary>Startup items.</summary>
    public Fact<IReadOnlyList<StartupItem>>? Startup { get; init; }

    /// <summary>Optional features.</summary>
    public Fact<IReadOnlyList<string>>? OptionalFeatures { get; init; }

    /// <summary>Network.</summary>
    public NetworkState? Network { get; init; }

    /// <summary>Power.</summary>
    public PowerState? Power { get; init; }

    /// <summary>Display.</summary>
    public DisplayState? Display { get; init; }

    /// <summary>Audio.</summary>
    public AudioState? Audio { get; init; }

    /// <summary>Input.</summary>
    public InputState? Input { get; init; }

    /// <summary>Riot.</summary>
    public RiotState? Riot { get; init; }

    /// <summary>Events.</summary>
    public EventDigest? Events { get; init; }

    /// <summary>Pack collector results.</summary>
    public IReadOnlyList<PackFactResult> Pack { get; init; } = [];
}

/// <summary>Output of one collector run.</summary>
public sealed record CollectorOutput
{
    /// <summary>Run record (status, elapsed, issue codes).</summary>
    public required CollectorRun Run { get; init; }

    /// <summary>Sections produced.</summary>
    public required SnapshotPatch Patch { get; init; }

    /// <summary>Raw probe outputs, only when <see cref="ScanRequest.CaptureRaw"/> (returned as data; the engine persists them).</summary>
    public IReadOnlyList<RawCaptureEntry> Capture { get; init; } = [];
}

/// <summary>
/// One raw probe output for replay tests (A's raw-capture fakes: <c>vtdiag scan --capture</c>). The payload format per
/// source is owned by A1 (VTD.Platform replay); it is redacted at local level before it leaves the collector.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record RawCaptureEntry
{
    /// <summary>Collector id.</summary>
    public required string CollectorId { get; init; }

    /// <summary>Source key, for example <c>registry:HKLM\SYSTEM\…\SecureBoot\State</c> or <c>wmi:root\cimv2:Win32_DeviceGuard</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Raw result as JSON text.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public required string Payload { get; init; }
}

/// <summary>Result of a scan.</summary>
public sealed record ScanResult
{
    /// <summary>The complete snapshot (local level; the raw unredacted copy never leaves the engine).</summary>
    public required ScanSnapshot Snapshot { get; init; }

    /// <summary>Raw capture, when requested.</summary>
    public IReadOnlyList<RawCaptureEntry> Capture { get; init; } = [];
}

/// <summary>
/// One read-only collector (A1). Has a timeout and a no-admin path: in limited mode it must still return, with the
/// admin-only facts Unknown(NeedsAdmin). Never throws for data problems; returns Unknown(Failed) facts instead.
/// </summary>
public interface ICollector
{
    /// <summary>Stable id (for example <c>security</c>).</summary>
    string Id { get; }

    /// <summary>UI area.</summary>
    ScanArea Area { get; }

    /// <summary>What it needs.</summary>
    CollectorNeeds Needs { get; }

    /// <summary>Hard timeout (the slowest collector is at most 15 s).</summary>
    TimeSpan Timeout { get; }

    /// <summary>Collects.</summary>
    /// <param name="context">Context.</param>
    /// <param name="cancellationToken">Cancelled at the timeout or when the user cancels.</param>
    Task<CollectorOutput> CollectAsync(CollectContext context, CancellationToken cancellationToken);
}

/// <summary>Runs a full scan and returns a complete snapshot (A1's ScanOrchestrator).</summary>
public interface IScanService
{
    /// <summary>Scans the PC.</summary>
    /// <param name="request">Request.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Fresh, targeted live probes for guards (S3) and post-reboot verification (A1 implements, A3 and A4 consume).
/// Ids are <see cref="GuardIds"/> plus any signal id A1 can re-probe live; unsupported ids return Unknown.
/// </summary>
public interface IFactProbe
{
    /// <summary>Ids this probe can evaluate.</summary>
    IReadOnlyCollection<string> SupportedIds { get; }

    /// <summary>Probes one id now.</summary>
    /// <param name="probeId">Guard or signal id.</param>
    /// <param name="args">Resolved arguments (invariant strings).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SignalResult> ProbeAsync(string probeId, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken);
}
