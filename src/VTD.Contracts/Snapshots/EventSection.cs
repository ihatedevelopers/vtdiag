namespace VTD.Contracts.Snapshots;

/// <summary>
/// Event-log digest (check ids: kp41_lke, realtek_events, app_crash_module, minidump, tpm_events, thermals). Queried
/// with EventLogReader + XPath by provider and event id, capped per query; only provider, id, timestamps and whitelisted
/// EventData fields are kept, never the rendered (localized) message.
/// </summary>
public sealed record EventDigest
{
    /// <summary>Start of the query window (default scan time minus 14 days).</summary>
    public required DateTimeOffset WindowStartUtc { get; init; }

    /// <summary>End of the query window (scan time).</summary>
    public required DateTimeOffset WindowEndUtc { get; init; }

    /// <summary>Hits of the built-in queries, grouped by log/provider/id.</summary>
    public required Fact<IReadOnlyList<EventHit>> Hits { get; init; }

    /// <summary>Application Error 1000/1001/1002 for Riot/Vanguard executables (faulting module matcher input).</summary>
    public required Fact<IReadOnlyList<AppCrash>> AppCrashes { get; init; }

    /// <summary>BugCheck 1001 events.</summary>
    public required Fact<IReadOnlyList<BugCheck>> BugChecks { get; init; }

    /// <summary>Minidump file headers (<c>%SystemRoot%\Minidump</c>), header only.</summary>
    public required Fact<IReadOnlyList<MinidumpMeta>> Minidumps { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <param name="windowStartUtc">Window start.</param>
    /// <param name="windowEndUtc">Window end.</param>
    public static EventDigest Unknown(UnknownReason reason, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc) => new()
    {
        WindowStartUtc = windowStartUtc,
        WindowEndUtc = windowEndUtc,
        Hits = Fact<IReadOnlyList<EventHit>>.Unknown(reason),
        AppCrashes = Fact<IReadOnlyList<AppCrash>>.Unknown(reason),
        BugChecks = Fact<IReadOnlyList<BugCheck>>.Unknown(reason),
        Minidumps = Fact<IReadOnlyList<MinidumpMeta>>.Unknown(reason),
    };
}

/// <summary>A group of matching events.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record EventHit
{
    /// <summary>Built-in query id that produced the hit (for example <c>realtek_nic_errors</c>, <c>kernel_power_41</c>).</summary>
    public required string QueryId { get; init; }

    /// <summary>Log name, for example <c>System</c>.</summary>
    public required string Log { get; init; }

    /// <summary>Provider name, for example <c>rt25cx21</c> or <c>Microsoft-Windows-Kernel-Power</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>Event id.</summary>
    public required int EventId { get; init; }

    /// <summary>Events in the window.</summary>
    public required int Count { get; init; }

    /// <summary>First occurrence.</summary>
    public required DateTimeOffset FirstUtc { get; init; }

    /// <summary>Last occurrence.</summary>
    public required DateTimeOffset LastUtc { get; init; }

    /// <summary>Up to 5 samples of whitelisted EventData fields (name to invariant value).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Samples { get; init; } = [];
}

/// <summary>Application event kind.</summary>
public enum AppCrashKind
{
    /// <summary>Application Error 1000.</summary>
    Crash = 0,

    /// <summary>Windows Error Reporting 1001.</summary>
    WerReport = 1,

    /// <summary>Application Hang 1002.</summary>
    Hang = 2,
}

/// <summary>An application crash or hang of a Riot/Vanguard executable.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record AppCrash
{
    /// <summary>Executable, for example <c>VALORANT-Win64-Shipping.exe</c>.</summary>
    public required string Exe { get; init; }

    /// <summary>Executable version.</summary>
    public string? ExeVersion { get; init; }

    /// <summary>Faulting module, for example <c>ProductInfo.dll</c> (Nahimic) or <c>n64hooks.dll</c>.</summary>
    public string? Module { get; init; }

    /// <summary>Faulting module version.</summary>
    public string? ModuleVersion { get; init; }

    /// <summary>Exception code, for example <c>0xc0000005</c>.</summary>
    public string? ExceptionCode { get; init; }

    /// <summary>Kind.</summary>
    public required AppCrashKind Kind { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset Utc { get; init; }
}

/// <summary>A BugCheck 1001 event.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record BugCheck
{
    /// <summary>Bug check code, for example <c>0x00000133</c>.</summary>
    public required string Code { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>First parameter.</summary>
    public string? Parameter1 { get; init; }

    /// <summary>Dump file name.</summary>
    public string? DumpFile { get; init; }
}

/// <summary>Minidump header metadata.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MinidumpMeta
{
    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>Last write.</summary>
    public required DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Size (bytes).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Bug check code from the header.</summary>
    public string? BugCheckCode { get; init; }

    /// <summary>Faulting driver from the header, when present (for example <c>vgk.sys</c>).</summary>
    public string? FaultingDriver { get; init; }
}

/// <summary>The result of one pack-declared read-only collector (<see cref="Knowledge.CollectorDef"/>).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PackFactResult
{
    /// <summary>The collector id from the KB.</summary>
    public required string CollectorId { get; init; }

    /// <summary>The data, or Unknown with a reason (for example NeedsAdmin for an HKLM read that needs elevation).</summary>
    public required Fact<PackFactData> Data { get; init; }
}

/// <summary>Uniform output of a pack-declared collector.</summary>
public sealed record PackFactData
{
    /// <summary>Number of matches (events, apps, drivers, crashes, values, files).</summary>
    public required int Count { get; init; }

    /// <summary>First match time (event and crash collectors).</summary>
    public DateTimeOffset? FirstUtc { get; init; }

    /// <summary>Last match time.</summary>
    public DateTimeOffset? LastUtc { get; init; }

    /// <summary>Matches as field maps, capped (projected EventData fields; app name/publisher/version; value name/kind/data;
    /// file exists/size/mtime/version/signer). Field names per kind are documented on each collector definition.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Items { get; init; } = [];

    /// <summary>Entities the matches point at (an app, a driver, a file, a registry value), so derived signals can bind
    /// entity-bound fix templates.</summary>
    public IReadOnlyList<EntityRef> Entities { get; init; } = [];

    /// <summary>More matches existed than were kept.</summary>
    public bool Truncated { get; init; }
}
