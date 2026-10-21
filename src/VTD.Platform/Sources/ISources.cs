namespace VTD.Platform.Sources;

/// <summary>Read-only registry access (HKLM 64-bit view and HKU). Never writes, never opens a key for write.</summary>
public interface IRegistrySource
{
    /// <summary>Reads a key's values and subkey names.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="path">Key path under the hive.</param>
    RegKeyData ReadKey(RegHive hive, string path);
}

/// <summary>Read-only WMI queries with a hard timeout.</summary>
public interface IWmiSource
{
    /// <summary>Runs a WQL SELECT.</summary>
    /// <param name="wmiNamespace">Namespace, for example <c>root\cimv2</c>.</param>
    /// <param name="wql">WQL SELECT statement.</param>
    /// <param name="timeout">Hard timeout.</param>
    WmiResult Query(string wmiNamespace, string wql, TimeSpan timeout);
}

/// <summary>The allowlisted read-only WMI methods (BCD store reads, Win32_Tpm.IsReady). Admin only.</summary>
public interface IWmiMethodSource
{
    /// <summary>Reads BCD flags of the current boot entry and the firmware boot order.</summary>
    /// <param name="timeout">Hard timeout.</param>
    BcdRaw ReadBcd(TimeSpan timeout);

    /// <summary>Reads Win32_Tpm properties and IsReady.</summary>
    /// <param name="timeout">Hard timeout.</param>
    TpmWmiRaw ReadTpm(TimeSpan timeout);
}

/// <summary>Read-only file metadata and filtered text reads. Content leaves only through a <see cref="LineFilter"/>.</summary>
public interface IFileSource
{
    /// <summary>File or directory metadata.</summary>
    /// <param name="path">Absolute path.</param>
    FileStat Stat(string path);

    /// <summary>Lists a directory.</summary>
    /// <param name="path">Absolute directory path.</param>
    /// <param name="pattern">Search pattern, for example <c>*.log</c>.</param>
    /// <param name="recursive">Recurse.</param>
    /// <param name="max">Max entries.</param>
    DirListing List(string path, string pattern, bool recursive, int max);

    /// <summary>Reads text lines with shared access (files the game keeps open), applying <paramref name="filter"/> inside.</summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="filter">Line filter.</param>
    /// <param name="maxBytes">Read at most this many bytes from the start.</param>
    /// <param name="maxLines">Keep at most this many filtered lines.</param>
    LinesResult ReadLines(string path, LineFilter filter, long maxBytes, int maxLines);

    /// <summary>Reads the first bytes of a file (binary headers only).</summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="count">Bytes (at most 4096).</param>
    HeaderResult ReadHeader(string path, int count);

    /// <summary>Version resource.</summary>
    /// <param name="path">Absolute path.</param>
    FileVersionResult GetVersion(string path);

    /// <summary>Target of a .lnk file.</summary>
    /// <param name="path">Absolute path of the .lnk.</param>
    ShortcutResult ReadShortcut(string path);
}

/// <summary>Event-log queries (EventLogReader + XPath). Rendered (localized) messages are never read.</summary>
public interface IEventLogSource
{
    /// <summary>Runs a query.</summary>
    /// <param name="query">Query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    EventQueryResult Query(EventQuery query, CancellationToken cancellationToken);
}

/// <summary>Native, read-only system information. Nothing here opens a process or thread handle.</summary>
public interface ISystemInfoSource
{
    /// <summary>Environment facts.</summary>
    EnvRaw GetEnvironment();

    /// <summary>ToolHelp process snapshot (names and PIDs only).</summary>
    ProcessList GetProcesses();

    /// <summary>All Win32 services and drivers with their state.</summary>
    ServiceList GetServices();

    /// <summary>Loaded kernel modules.</summary>
    DriverList GetLoadedDrivers();

    /// <summary>PnP devices.</summary>
    /// <param name="includePhantomClasses">Class GUIDs (lowercase, braces) whose non-present devices are included too.</param>
    DeviceList GetDevices(IReadOnlyList<string> includePhantomClasses);

    /// <summary>Firmware type and (when <paramref name="privileged"/>) firmware variables.</summary>
    /// <param name="privileged">Try the SeSystemEnvironmentPrivilege reads (elevated engine only).</param>
    FirmwareRaw GetFirmware(bool privileged);

    /// <summary>Power scheme, overlay, values and AC/battery state.</summary>
    PowerRaw GetPower();

    /// <summary>Physical memory.</summary>
    MemoryRaw GetMemory();

    /// <summary>CPU topology and CPUID bits.</summary>
    CpuRaw GetCpu();

    /// <summary>Active display paths.</summary>
    DisplayRaw GetDisplays();

    /// <summary>Network interfaces, best route, WinHTTP proxy flag.</summary>
    NetRaw GetNetwork();

    /// <summary>Network List Manager connectivity.</summary>
    ConnectivityRaw GetConnectivity();

    /// <summary>WLAN interfaces.</summary>
    WlanRaw GetWlan();

    /// <summary>TBS device info.</summary>
    TpmRaw GetTpm();

    /// <summary>Windows Update Agent busy state.</summary>
    WuaRaw GetWindowsUpdate();

    /// <summary>Default audio endpoints of the calling user.</summary>
    AudioDefaultsRaw GetAudioDefaults();

    /// <summary>BitLocker protection of a volume via the shell property system.</summary>
    /// <param name="driveRoot">For example <c>C:\</c>.</param>
    BitLockerRaw GetBitLocker(string driveRoot);
}

/// <summary>Optional network probes (only when the request allows them).</summary>
public interface INetworkProbeSource
{
    /// <summary>Measures local clock skew against NTP (falls back to an HTTPS Date header).</summary>
    /// <param name="timeout">Timeout.</param>
    ClockRaw MeasureClock(TimeSpan timeout);

    /// <summary>ICMP echo.</summary>
    /// <param name="address">IP address.</param>
    /// <param name="count">Echoes.</param>
    /// <param name="timeout">Per-echo timeout.</param>
    PingRaw Ping(string address, int count, TimeSpan timeout);
}

/// <summary>Every source a scan reads from. Swap the whole bundle for capture or replay.</summary>
public sealed record SystemSources
{
    /// <summary>Registry.</summary>
    public required IRegistrySource Registry { get; init; }

    /// <summary>WMI queries.</summary>
    public required IWmiSource Wmi { get; init; }

    /// <summary>Allowlisted WMI methods.</summary>
    public required IWmiMethodSource WmiMethods { get; init; }

    /// <summary>Files.</summary>
    public required IFileSource Files { get; init; }

    /// <summary>Event logs.</summary>
    public required IEventLogSource Events { get; init; }

    /// <summary>Native system info.</summary>
    public required ISystemInfoSource System { get; init; }

    /// <summary>Network probes.</summary>
    public required INetworkProbeSource Probes { get; init; }
}
