using VTD.Contracts.Scanning;

namespace VTD.Contracts.Snapshots;

/// <summary>
/// The result of one full PC scan: typed, three-valued facts grouped in sections (design_B.md section 4.1 with
/// design_A.md section 5 three-valued facts). Every section is always present; a fact that was not read is an explicit
/// <see cref="Fact{T}"/> Unknown with a reason, never omitted and never defaulted to false.
/// Schema: <c>kb/schema/snapshot.schema.json</c>. Serialize with <see cref="Serialization.VtdJson"/>.
/// </summary>
/// <remarks>
/// Locale rule: collectors match InstanceIds, numeric values, provider GUIDs and event ids, never localized display
/// names. Display names are kept for humans only. Privacy: secrets are never collected (no command lines, no Riot
/// tokens, no Vanguard log contents); identifiers carry <see cref="SensitivityAttribute"/> for the redactor.
/// </remarks>
public sealed record ScanSnapshot
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Snapshot schema version (<see cref="CurrentSchemaVersion"/>).</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Who, when, how (elevated or limited), which boot, redaction level.</summary>
    public required SnapshotMeta Meta { get; init; }

    /// <summary>One entry per collector: status, time, issue codes. The report's "what we couldn't read and why".</summary>
    public required IReadOnlyList<CollectorRun> Runs { get; init; }

    /// <summary>Windows build, pending reboot, Fast Startup, clock, hotfixes, modified-Windows markers.</summary>
    public required OsInfo Os { get; init; }

    /// <summary>CPU, memory, GPUs, board and BIOS.</summary>
    public required HardwareInfo Hardware { get; init; }

    /// <summary>Firmware type and boot capabilities.</summary>
    public required FirmwareInfo Firmware { get; init; }

    /// <summary>Vanguard readiness: Secure Boot, TPM, VBS/HVCI, virtualization, IOMMU, BCD flags, Defender, AV, IFEO.</summary>
    public required SecurityState Security { get; init; }

    /// <summary>Disks, partition style, volumes (free space, dirty bit), storage controllers (VMD).</summary>
    public required StorageInfo Storage { get; init; }

    /// <summary>PnP devices with problem codes and drivers.</summary>
    public required Fact<IReadOnlyList<DeviceInfo>> Devices { get; init; }

    /// <summary>Loaded kernel drivers (<c>EnumDeviceDrivers</c>).</summary>
    public required Fact<IReadOnlyList<LoadedDriver>> Drivers { get; init; }

    /// <summary>Services from the SCM.</summary>
    public required Fact<IReadOnlyList<ServiceInfo>> Services { get; init; }

    /// <summary>Running processes from a ToolHelp snapshot (names and PIDs only, never a handle).</summary>
    public required Fact<IReadOnlyList<ProcessInfo>> Processes { get; init; }

    /// <summary>Installed applications from the uninstall keys (HKLM, WOW6432Node, player HKCU).</summary>
    public required Fact<IReadOnlyList<InstalledApp>> Apps { get; init; }

    /// <summary>Startup entries (Run keys, Startup folders, logon tasks).</summary>
    public required Fact<IReadOnlyList<StartupItem>> Startup { get; init; }

    /// <summary>Enabled Windows optional features by invariant name (for example <c>VirtualMachinePlatform</c>).</summary>
    [Sensitivity(Sensitivity.None)]
    public required Fact<IReadOnlyList<string>> OptionalFeatures { get; init; }

    /// <summary>Adapters, connectivity, proxy, IPv6, Winsock LSPs, firewall rules for Riot executables, Wi-Fi, probes.</summary>
    public required NetworkState Network { get; init; }

    /// <summary>Power plan, USB selective suspend, processor limits, battery.</summary>
    public required PowerState Power { get; init; }

    /// <summary>Monitors, HAGS, MPO, Game Mode, Game DVR, GPU preferences for Riot executables.</summary>
    public required DisplayState Display { get; init; }

    /// <summary>Audio endpoints (Bluetooth Hands-Free detection is locale-free).</summary>
    public required AudioState Audio { get; init; }

    /// <summary>Mouse settings.</summary>
    public required InputState Input { get; init; }

    /// <summary>Riot Client, VALORANT, Vanguard (files, services, vgc log metadata), logs digests, crash metadata, detected codes.</summary>
    public required RiotState Riot { get; init; }

    /// <summary>Event-log digest over the last <see cref="EventDigest.WindowStartUtc"/>..end window (default 14 days).</summary>
    public required EventDigest Events { get; init; }

    /// <summary>Results of the KB's pack-declared read-only collectors, one per collector id.</summary>
    public required IReadOnlyList<PackFactResult> Pack { get; init; }
}

/// <summary>Snapshot metadata.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record SnapshotMeta
{
    /// <summary>ULID of the snapshot.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>App version that produced the snapshot (SemVer).</summary>
    public required string AppVersion { get; init; }

    /// <summary>KB version that supplied pack collectors (CalVer).</summary>
    public required string KbVersion { get; init; }

    /// <summary>When the scan started (UTC).</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Total scan duration.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Full, quick or targeted scan.</summary>
    public required ScanMode Mode { get; init; }

    /// <summary>Elevated or limited engine. In limited mode admin-only facts are Unknown(NeedsAdmin).</summary>
    public required PrivilegeMode Privilege { get; init; }

    /// <summary>Where this snapshot came from.</summary>
    public required SnapshotOrigin Origin { get; init; }

    /// <summary>The boot this scan ran in.</summary>
    public required Fact<BootIdentity> Boot { get; init; }

    /// <summary>Windows UI culture name, for example <c>tr-TR</c> (informational; never used for matching).</summary>
    public required string OsUiCulture { get; init; }

    /// <summary>VTDiag UI language (<c>en</c> or <c>tr</c>).</summary>
    public required string AppLanguage { get; init; }

    /// <summary>Redaction applied to this copy of the snapshot.</summary>
    public required RedactionState Redaction { get; init; }

    /// <summary>HMAC of stable hardware ids with a per-install salt that is never exported. Lets a helper tell that two
    /// reports come from the same PC without learning any serial.</summary>
    public required string MachineKey { get; init; }

    /// <summary>Id of the raw capture recorded with <c>vtdiag scan --capture</c>, when one was recorded.</summary>
    public string? CaptureId { get; init; }
}

/// <summary>Where a snapshot came from.</summary>
public enum SnapshotOrigin
{
    /// <summary>A live scan on this PC.</summary>
    LiveScan = 0,

    /// <summary>A hand-made or generated test fixture.</summary>
    Fixture = 1,

    /// <summary>Replayed from a raw capture.</summary>
    Replay = 2,

    /// <summary>Imported from a <c>.vtdscan</c> (Helper mode).</summary>
    Imported = 3,
}

/// <summary>Redaction state of a snapshot copy.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RedactionState
{
    /// <summary>Redaction level.</summary>
    public required RedactionLevel Level { get; init; }

    /// <summary>Version of the redaction rule set.</summary>
    public required int RulesVersion { get; init; }

    /// <summary>Rule ids that changed something (for example <c>user.name</c>, <c>path.profile</c>, <c>net.mac</c>).</summary>
    public IReadOnlyList<string> AppliedRules { get; init; } = [];
}

/// <summary>Status of one collector in one scan.</summary>
public enum CollectorStatus
{
    /// <summary>Everything read.</summary>
    Ok = 0,

    /// <summary>Some facts read, some Unknown (see issue codes).</summary>
    Partial = 1,

    /// <summary>Skipped because the engine runs in limited mode.</summary>
    SkippedNoAdmin = 2,

    /// <summary>Stopped at the collector timeout; facts read so far are kept.</summary>
    TimedOut = 3,

    /// <summary>Failed; all its facts are Unknown(Failed).</summary>
    Failed = 4,

    /// <summary>Not applicable to this PC.</summary>
    NotApplicable = 5,

    /// <summary>Not requested in this scan mode.</summary>
    NotRequested = 6,
}

/// <summary>Per-collector run record.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CollectorRun
{
    /// <summary>Collector id (for example <c>security</c>, <c>network</c>, <c>riot</c>, <c>pack</c>).</summary>
    public required string CollectorId { get; init; }

    /// <summary>Outcome.</summary>
    public required CollectorStatus Status { get; init; }

    /// <summary>Elapsed time.</summary>
    public required long ElapsedMs { get; init; }

    /// <summary>Machine issue codes (for example <c>wmi.timeout:Win32_DeviceGuard</c>). Never PII.</summary>
    public IReadOnlyList<string> IssueCodes { get; init; } = [];
}
