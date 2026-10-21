namespace VTD.Contracts.Snapshots;

/// <summary>
/// Riot Client, VALORANT and Vanguard (check ids: vgc_service, vgk_files, stale_dat, riot_elevation, riot_folders,
/// valorant_config, display_mode) plus the log signals DESIGN.md section 2 requires in M1: ShooterGame.log safe-extractor
/// patterns, Riot Client logs, <c>Crashes\UECC-*</c> metadata and vgc log existence/timing (metadata only; contents
/// are encrypted and never opened). All log text is redacted at parse time; only pattern ids, counts and whitelisted
/// captured groups survive.
/// </summary>
public sealed record RiotState
{
    /// <summary>Riot Client install (<c>RiotClientInstalls.json</c>).</summary>
    public required Fact<RiotInstall> Client { get; init; }

    /// <summary>VALORANT install (product settings).</summary>
    public required Fact<RiotInstall> Valorant { get; init; }

    /// <summary>Riot Vanguard (vgc/vgk services and files, vgc log metadata).</summary>
    public required Fact<VanguardInfo> Vanguard { get; init; }

    /// <summary>AppCompat layers on Riot executables (<c>RUNASADMIN</c>, <c>DISABLEDXMAXIMIZEDWINDOWEDMODE</c>...).</summary>
    public required Fact<IReadOnlyList<CompatLayerEntry>> CompatLayers { get; init; }

    /// <summary>Whitelisted keys from <c>GameUserSettings.ini</c>.</summary>
    public required Fact<GameConfigDigest> GameConfig { get; init; }

    /// <summary>ShooterGame.log digest (current log and rotated backups).</summary>
    public required Fact<ShooterLogDigest> ShooterLog { get; init; }

    /// <summary>Riot Client log digest (<c>%LOCALAPPDATA%\Riot Games\Riot Client\Logs</c>).</summary>
    public required Fact<RiotClientLogDigest> ClientLogs { get; init; }

    /// <summary>VALORANT crash folders (<c>Crashes\UECC-*</c>) metadata.</summary>
    public required Fact<IReadOnlyList<CrashReportMeta>> Crashes { get; init; }

    /// <summary>Error codes found in logs and crash reports, normalized (for example <c>VAN 9003</c>, <c>VAL 43</c>,
    /// <c>VAN -81</c>). Codes the player typed are in the intake answers, not here: logged codes weigh more.</summary>
    public required Fact<IReadOnlyList<DetectedCode>> DetectedCodes { get; init; }

    /// <summary>VALORANT is running (process name).</summary>
    public required Fact<bool> GameRunning { get; init; }

    /// <summary>The Riot Client is running (process name).</summary>
    public required Fact<bool> ClientRunning { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static RiotState Unknown(UnknownReason reason) => new()
    {
        Client = Fact<RiotInstall>.Unknown(reason),
        Valorant = Fact<RiotInstall>.Unknown(reason),
        Vanguard = Fact<VanguardInfo>.Unknown(reason),
        CompatLayers = Fact<IReadOnlyList<CompatLayerEntry>>.Unknown(reason),
        GameConfig = Fact<GameConfigDigest>.Unknown(reason),
        ShooterLog = Fact<ShooterLogDigest>.Unknown(reason),
        ClientLogs = Fact<RiotClientLogDigest>.Unknown(reason),
        Crashes = Fact<IReadOnlyList<CrashReportMeta>>.Unknown(reason),
        DetectedCodes = Fact<IReadOnlyList<DetectedCode>>.Unknown(reason),
        GameRunning = Fact<bool>.Unknown(reason),
        ClientRunning = Fact<bool>.Unknown(reason),
    };
}

/// <summary>A Riot product install.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RiotInstall
{
    /// <summary>Installed (folder exists).</summary>
    public required bool Installed { get; init; }

    /// <summary>Install folder.</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? Path { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Drive letter with colon.</summary>
    public string? Drive { get; init; }

    /// <summary>Installed on the Windows system drive.</summary>
    public bool? OnSystemDrive { get; init; }

    /// <summary>The path contains non-ASCII characters (a known cause of client errors).</summary>
    public bool? PathHasNonAscii { get; init; }
}

/// <summary>Riot Vanguard state.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record VanguardInfo
{
    /// <summary>Vanguard is installed (service or folder present).</summary>
    public required bool Installed { get; init; }

    /// <summary>The <c>vgc</c> service, null when missing. Manual + Stopped is NORMAL with Pre-Check (never force Automatic
    /// unless the player confirms Pre-Check is off AND the vgc logs show a failure).</summary>
    public ServiceInfo? Vgc { get; init; }

    /// <summary>The <c>vgk</c> driver service, null when missing.</summary>
    public ServiceInfo? Vgk { get; init; }

    /// <summary>Install folder (normally <c>C:\Program Files\Riot Vanguard</c>).</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? InstallPath { get; init; }

    /// <summary>vgc.exe file version.</summary>
    public string? Version { get; init; }

    /// <summary>Known Vanguard files: <c>vgc.exe</c>, <c>vgk.sys</c>, <c>vgkbootstatus.dat</c>, <c>%WINDIR%\392667600.dat</c>
    /// (location <c>windows</c>).</summary>
    public IReadOnlyList<RiotFileInfo> Files { get; init; } = [];

    /// <summary>vgc log metadata (existence and timing only). Many tiny logs in a short window = crash-restart loop;
    /// no recent log when the player expected Vanguard = it never started (tells Pre-Check from a broken Vanguard).</summary>
    public VgcLogDigest? Logs { get; init; }
}

/// <summary>A known Riot/Vanguard file.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RiotFileInfo
{
    /// <summary>File name.</summary>
    public required string Name { get; init; }

    /// <summary>Where it lives: <c>vanguard</c> (Program Files\Riot Vanguard), <c>system32</c>, <c>windows</c> (the
    /// Windows folder itself, for example <c>392667600.dat</c>), <c>riotclient</c>, <c>valorant</c>.</summary>
    public required string Location { get; init; }

    /// <summary>Exists.</summary>
    public required bool Exists { get; init; }

    /// <summary>Size (bytes).</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Last write (UTC).</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>File version.</summary>
    public string? Version { get; init; }
}

/// <summary>vgc log folder metadata. File contents are encrypted and are never opened.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record VgcLogDigest
{
    /// <summary>Number of log files.</summary>
    public required int FileCount { get; init; }

    /// <summary>Newest log write time.</summary>
    public DateTimeOffset? NewestUtc { get; init; }

    /// <summary>Oldest log write time.</summary>
    public DateTimeOffset? OldestUtc { get; init; }

    /// <summary>Logs written in the last hour before the scan.</summary>
    public int CountLastHour { get; init; }

    /// <summary>Logs written in the last 24 hours before the scan.</summary>
    public int CountLast24Hours { get; init; }

    /// <summary>Total size (bytes).</summary>
    public long TotalBytes { get; init; }

    /// <summary>Up to 20 most recent files.</summary>
    public IReadOnlyList<LogFileMeta> Recent { get; init; } = [];
}

/// <summary>Log file metadata.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record LogFileMeta
{
    /// <summary>File name.</summary>
    public required string Name { get; init; }

    /// <summary>Size (bytes).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Last write (UTC).</summary>
    public required DateTimeOffset ModifiedUtc { get; init; }
}

/// <summary>AppCompat layers set on one executable.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CompatLayerEntry
{
    /// <summary>Executable image name.</summary>
    public required string ImageName { get; init; }

    /// <summary>Executable path (the registry value name).</summary>
    [Sensitivity(Sensitivity.Path)]
    public required string ExePath { get; init; }

    /// <summary>HKCU or HKLM.</summary>
    public required AppHive Hive { get; init; }

    /// <summary>Layer tokens, for example <c>~</c>, <c>RUNASADMIN</c>, <c>WIN8RTM</c>.</summary>
    public IReadOnlyList<string> Layers { get; init; } = [];
}

/// <summary>Whitelisted <c>GameUserSettings.ini</c> keys (window mode, resolution, frame caps), per-account files merged.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GameConfigDigest
{
    /// <summary>Number of per-account config folders.</summary>
    public required int AccountConfigCount { get; init; }

    /// <summary>Whitelisted settings, key to raw invariant value (for example <c>FullscreenMode</c> = <c>0</c>).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>The file failed to parse or looks truncated (config corruption marker).</summary>
    public bool CorruptMarkers { get; init; }
}

/// <summary>
/// ShooterGame.log digest. Built by a safe extractor: any line with <c>-remoting-auth-token</c>, <c>-subject</c>,
/// <c>CommandLine</c>, <c>MachineId</c>, <c>LoginId</c>, <c>UserName</c>, <c>puuid</c>, <c>Bearer</c>, <c>access_token</c> or
/// a JWT-shaped string is discarded before matching; raw lines never leave the extractor.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record ShooterLogDigest
{
    /// <summary>Last write of the current log.</summary>
    public DateTimeOffset? LastWriteUtc { get; init; }

    /// <summary>Rotated backup logs found.</summary>
    public int BackupLogCount { get; init; }

    /// <summary>Game version (branch / changelist) from the log header.</summary>
    public string? GameVersion { get; init; }

    /// <summary>The <c>LogInit</c> hardware line, parsed.</summary>
    public LogInitHardware? Hardware { get; init; }

    /// <summary><c>LogPlatformInitializerV2: Failed to initialize: &lt;Manager&gt; (error: X)</c> lines (map to VAL 55-70).</summary>
    public IReadOnlyList<PlatformInitFailure> PlatformInitFailures { get; init; } = [];

    /// <summary>"D3D device being lost" (GPU crash / TDR) occurrences.</summary>
    public int D3dDeviceLostCount { get; init; }

    /// <summary>"Fatal error" occurrences.</summary>
    public int FatalErrorCount { get; init; }

    /// <summary>LogNet disconnect occurrences.</summary>
    public int NetDisconnectCount { get; init; }

    /// <summary>Hit counts by allowlisted pattern id.</summary>
    public IReadOnlyDictionary<string, int> PatternCounts { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, int>.Empty;
}

/// <summary>The <c>LogInit</c> header line, parsed.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record LogInitHardware
{
    /// <summary>OS text, for example <c>Windows 11 (25H2) [10.0.26200.xxxx]</c>.</summary>
    public string? Os { get; init; }

    /// <summary>CPU text.</summary>
    public string? Cpu { get; init; }

    /// <summary>GPU text.</summary>
    public string? Gpu { get; init; }

    /// <summary>Disk type text (<c>SSD</c>/<c>HDD</c>).</summary>
    public string? Disk { get; init; }
}

/// <summary>A failed platform manager at game start.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PlatformInitFailure
{
    /// <summary>Manager name, for example <c>VanguardManager</c>, <c>ContractsManager</c>.</summary>
    public required string Manager { get; init; }

    /// <summary>Error token, for example <c>RecruitmentInitConfigurationFailure</c>.</summary>
    public string? Error { get; init; }

    /// <summary>Occurrences.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>Riot Client log digest.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RiotClientLogDigest
{
    /// <summary>Log files found.</summary>
    public required int FileCount { get; init; }

    /// <summary>Newest log write.</summary>
    public DateTimeOffset? NewestUtc { get; init; }

    /// <summary>Hit counts by allowlisted pattern id (login/RSO errors, VAN surfacing...).</summary>
    public IReadOnlyDictionary<string, int> PatternCounts { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, int>.Empty;
}

/// <summary>Metadata of one VALORANT crash folder.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CrashReportMeta
{
    /// <summary>Folder name (<c>UECC-Windows-…</c>).</summary>
    public required string FolderName { get; init; }

    /// <summary>Created (UTC).</summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>Crash type from the crash context (<c>Crash</c>, <c>Ensure</c>, <c>GPUCrash</c>, <c>Hang</c>).</summary>
    public string? CrashType { get; init; }

    /// <summary>Allowlisted pattern id of the error message (never the text).</summary>
    public string? ErrorPattern { get; init; }

    /// <summary>Drive the crash context reports the game ran from (VAN: RESTRICTION: 3 drive check).</summary>
    public string? InstallDrive { get; init; }
}

/// <summary>Where a code was found.</summary>
public enum CodeSource
{
    /// <summary>Riot Client logs.</summary>
    RiotClientLog = 0,

    /// <summary>ShooterGame.log.</summary>
    ShooterLog = 1,

    /// <summary>Crash report metadata.</summary>
    CrashReport = 2,

    /// <summary>Windows event log.</summary>
    EventLog = 3,
}

/// <summary>An error code found on the PC (normalized: <c>VAN 9003</c>, <c>VAN: RESTRICTION: 1</c>, <c>VAN -81</c>, <c>VAL 43</c>).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DetectedCode
{
    /// <summary>Normalized code.</summary>
    public required string Code { get; init; }

    /// <summary>Source.</summary>
    public required CodeSource Source { get; init; }

    /// <summary>Occurrences.</summary>
    public int Count { get; init; } = 1;

    /// <summary>First seen.</summary>
    public DateTimeOffset? FirstUtc { get; init; }

    /// <summary>Last seen.</summary>
    public DateTimeOffset? LastUtc { get; init; }
}
