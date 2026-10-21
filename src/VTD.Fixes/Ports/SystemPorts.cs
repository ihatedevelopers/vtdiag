using VTD.Contracts;

namespace VTD.Fixes.Ports;

// The system access layer under the primitives. Every primitive talks to Windows ONLY through these ports, so the
// primitives, the policy and the runner are tested against in-memory fakes, and the real Win32 implementations
// (VTD.Fixes.Interop) are exercised by the gated integration tests (VTD_ALLOW_SYSTEM_WRITES=1, CI runner / VM only).
// Ports throw on failure (UnauthorizedAccessException, IOException, Win32Exception, TimeoutException,
// PortRefusedException); "not found" is a null result, never an exception.

/// <summary>A port refused an operation for safety (reparse point, registry link, untrusted owner, not ours...).</summary>
public sealed class PortRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PortRefusedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Machine code, for example <c>file.reparse_point</c>.</param>
    public PortRefusedException(string code)
        : base(code)
    {
        Code = code;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Machine code.</param>
    /// <param name="innerException">Cause.</param>
    public PortRefusedException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    /// <summary>Machine code.</summary>
    public string Code { get; } = "port.refused";
}

// ------------------------------------------------------------------ services

/// <summary>Service start type as stored by the SCM.</summary>
public enum ServiceStart
{
    /// <summary>Boot (drivers).</summary>
    Boot = 0,

    /// <summary>System (drivers).</summary>
    System = 1,

    /// <summary>Automatic.</summary>
    Automatic = 2,

    /// <summary>Manual (demand).</summary>
    Manual = 3,

    /// <summary>Disabled.</summary>
    Disabled = 4,
}

/// <summary>Service run state.</summary>
public enum ServiceRun
{
    /// <summary>Stopped.</summary>
    Stopped = 1,

    /// <summary>Start pending.</summary>
    StartPending = 2,

    /// <summary>Stop pending.</summary>
    StopPending = 3,

    /// <summary>Running.</summary>
    Running = 4,

    /// <summary>Continue pending.</summary>
    ContinuePending = 5,

    /// <summary>Pause pending.</summary>
    PausePending = 6,

    /// <summary>Paused.</summary>
    Paused = 7,
}

/// <summary>A service's configuration and status.</summary>
public sealed record ServiceInfo
{
    /// <summary>Service (key) name.</summary>
    public required string Name { get; init; }

    /// <summary>Start type.</summary>
    public required ServiceStart StartType { get; init; }

    /// <summary>Delayed auto-start flag.</summary>
    public required bool Delayed { get; init; }

    /// <summary>Run state.</summary>
    public required ServiceRun State { get; init; }

    /// <summary>Kernel or file-system driver (not a user-mode service).</summary>
    public bool IsDriver { get; init; }

    /// <summary>Runs inside a shared svchost process (Windows' own services).</summary>
    public bool IsSharedProcess { get; init; }

    /// <summary>Launch-protected (anti-malware / Windows protected service).</summary>
    public bool LaunchProtected { get; init; }

    /// <summary>Binary path (raw, as configured).</summary>
    public string? ImagePath { get; init; }
}

/// <summary>Service Control Manager access.</summary>
public interface IServicePort
{
    /// <summary>Reads a service; null when it does not exist.</summary>
    /// <param name="name">Service name.</param>
    ServiceInfo? Query(string name);

    /// <summary>Sets the start type and the delayed-auto flag.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="startType">Start type (any value, so undo can restore Boot/System exactly).</param>
    /// <param name="delayed">Delayed auto-start.</param>
    void SetStartType(string name, ServiceStart startType, bool delayed);

    /// <summary>Stops the service and waits until it is stopped.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="timeout">Wait limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts the service and waits until it runs.</summary>
    /// <param name="name">Service name.</param>
    /// <param name="timeout">Wait limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);
}

// ------------------------------------------------------------------ devices

/// <summary>A PnP device node.</summary>
public sealed record DeviceInfo
{
    /// <summary>Instance id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Currently present.</summary>
    public required bool Present { get; init; }

    /// <summary>Not disabled (no CM_PROB_DISABLED, no CONFIGFLAG_DISABLED).</summary>
    public required bool Enabled { get; init; }

    /// <summary>Problem code (0 = none, 22 = disabled).</summary>
    public required int ProblemCode { get; init; }

    /// <summary>Setup class GUID (lowercase, braces), when known.</summary>
    public string? ClassGuid { get; init; }

    /// <summary>Parent instance id, when known.</summary>
    public string? ParentInstanceId { get; init; }

    /// <summary>Driver key under <c>Control\Class</c> (<c>{class}\NNNN</c>), when known.</summary>
    public string? DriverKey { get; init; }
}

/// <summary>Result of a device state change.</summary>
/// <param name="RebootRequired">Windows asked for a restart to finish the change.</param>
public sealed record DeviceChangeResult(bool RebootRequired);

/// <summary>Configuration Manager / SetupAPI access.</summary>
public interface IDevicePort
{
    /// <summary>Reads one device (present or phantom); null when unknown.</summary>
    /// <param name="instanceId">Instance id.</param>
    DeviceInfo? Query(string instanceId);

    /// <summary>All present devices with class and parent (the policy computes protected ancestors from it).</summary>
    IReadOnlyList<DeviceInfo> EnumeratePresent();

    /// <summary>Disables persistently (global, survives reboot).</summary>
    /// <param name="instanceId">Instance id.</param>
    DeviceChangeResult Disable(string instanceId);

    /// <summary>Enables persistently.</summary>
    /// <param name="instanceId">Instance id.</param>
    DeviceChangeResult Enable(string instanceId);

    /// <summary>Restarts the device so it reloads its driver settings (DICS_PROPCHANGE).</summary>
    /// <param name="instanceId">Instance id.</param>
    DeviceChangeResult Restart(string instanceId);
}

// ------------------------------------------------------------------ network

/// <summary>IPv4 interface metric state.</summary>
/// <param name="Metric">Current metric.</param>
/// <param name="Automatic">Automatic metric on.</param>
public sealed record InterfaceMetric(int Metric, bool Automatic);

/// <summary>Network configuration access (interfaces are identified by the adapter's NetCfgInstanceId).</summary>
public interface INetworkPort
{
    /// <summary>The adapter's NetCfgInstanceId (from its driver key), or null when it is not a network adapter.</summary>
    /// <param name="instanceId">PnP instance id.</param>
    Guid? InterfaceGuidOf(string instanceId);

    /// <summary>True when the interface carries a default route (IPv4 or IPv6) in the routing table; Unknown on failure.</summary>
    /// <param name="interfaceGuid">Interface.</param>
    Tri CarriesDefaultRoute(Guid interfaceGuid);

    /// <summary>Reads the IPv4 metric; null when the interface has no IPv4.</summary>
    /// <param name="interfaceGuid">Interface.</param>
    InterfaceMetric? GetIpv4Metric(Guid interfaceGuid);

    /// <summary>Sets the IPv4 metric (automatic, or a fixed value).</summary>
    /// <param name="interfaceGuid">Interface.</param>
    /// <param name="automatic">Automatic metric.</param>
    /// <param name="metric">Fixed metric (ignored when automatic).</param>
    void SetIpv4Metric(Guid interfaceGuid, bool automatic, int metric);

    /// <summary>True when ms_tcpip6 is bound to the adapter; null when the binding is not found.</summary>
    /// <param name="interfaceGuid">Interface.</param>
    bool? GetIpv6Bound(Guid interfaceGuid);

    /// <summary>Binds or unbinds ms_tcpip6.</summary>
    /// <param name="interfaceGuid">Interface.</param>
    /// <param name="bound">Bound.</param>
    void SetIpv6Bound(Guid interfaceGuid, bool bound);
}

// ------------------------------------------------------------------ registry

/// <summary>Registry hive a primitive may touch. <see cref="User"/> is ALWAYS the player's hive (<c>HKU\&lt;player SID&gt;</c>).</summary>
public enum RegHive
{
    /// <summary>HKEY_LOCAL_MACHINE (64-bit view).</summary>
    Machine = 0,

    /// <summary>The interactive player's hive (never the engine's own HKCU).</summary>
    User = 1,
}

/// <summary>Raw registry value: type and exact bytes (so undo restores it bit for bit).</summary>
/// <param name="Type">REG_* type code.</param>
/// <param name="Data">Exact bytes.</param>
public sealed record RegValue(int Type, byte[] Data)
{
    /// <summary>REG_NONE.</summary>
    public const int None = 0;

    /// <summary>REG_SZ.</summary>
    public const int String = 1;

    /// <summary>REG_EXPAND_SZ.</summary>
    public const int ExpandString = 2;

    /// <summary>REG_BINARY.</summary>
    public const int Binary = 3;

    /// <summary>REG_DWORD.</summary>
    public const int DWord = 4;

    /// <summary>REG_MULTI_SZ.</summary>
    public const int MultiString = 7;

    /// <summary>REG_QWORD.</summary>
    public const int QWord = 11;
}

/// <summary>
/// Registry access. Implementations open every key component WITHOUT following registry symbolic links and refuse a
/// path through one (<see cref="PortRefusedException"/> <c>registry.link</c>), so an elevated write into the player's hive
/// cannot be redirected into HKLM (critique S-a).
/// </summary>
public interface IRegistryPort
{
    /// <summary>Reads a value; null when the key or the value is absent.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path relative to the hive.</param>
    /// <param name="name">Value name (empty = default value).</param>
    RegValue? GetValue(RegHive hive, string subKey, string name);

    /// <summary>True when the key exists.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    bool KeyExists(RegHive hive, string subKey);

    /// <summary>Subkey names (empty when the key is absent).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    IReadOnlyList<string> GetSubKeyNames(RegHive hive, string subKey);

    /// <summary>Value names (empty when the key is absent).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    IReadOnlyList<string> GetValueNames(RegHive hive, string subKey);

    /// <summary>Writes a value, creating missing keys (non-volatile).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    /// <param name="name">Value name.</param>
    /// <param name="value">Type and bytes.</param>
    void SetValue(RegHive hive, string subKey, string name, RegValue value);

    /// <summary>Deletes a value (no-op when absent). Keys are never deleted.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    /// <param name="name">Value name.</param>
    /// <returns>True when a value was deleted.</returns>
    bool DeleteValue(RegHive hive, string subKey, string name);
}

// ------------------------------------------------------------------ power

/// <summary>Power scheme access (powrprof).</summary>
public interface IPowerPort
{
    /// <summary>The active scheme.</summary>
    Guid GetActiveScheme();

    /// <summary>All installed schemes.</summary>
    IReadOnlyList<Guid> EnumerateSchemes();

    /// <summary>Reads an AC (or DC) value index; null when the setting does not exist in the scheme.</summary>
    /// <param name="scheme">Scheme.</param>
    /// <param name="subgroup">Subgroup.</param>
    /// <param name="setting">Setting.</param>
    /// <param name="ac">AC (true) or DC (false).</param>
    uint? ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac);

    /// <summary>Writes an AC (or DC) value index.</summary>
    /// <param name="scheme">Scheme.</param>
    /// <param name="subgroup">Subgroup.</param>
    /// <param name="setting">Setting.</param>
    /// <param name="ac">AC (true) or DC (false).</param>
    /// <param name="value">Value.</param>
    void WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value);

    /// <summary>Activates a scheme (also re-applies the active scheme after a value write).</summary>
    /// <param name="scheme">Scheme.</param>
    void SetActiveScheme(Guid scheme);
}

// ------------------------------------------------------------------ firewall and Defender

/// <summary>A Windows Firewall rule (the parts VTDiag creates and restores).</summary>
public sealed record FirewallRuleInfo
{
    /// <summary>Rule name.</summary>
    public required string Name { get; init; }

    /// <summary>Grouping (VTDiag rules use <c>VTDiag</c>).</summary>
    public string? Grouping { get; init; }

    /// <summary>Program path.</summary>
    public string? Program { get; init; }

    /// <summary><c>In</c> or <c>Out</c>.</summary>
    public required string Direction { get; init; }

    /// <summary><c>Allow</c> or <c>Block</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Profiles bit mask (0x7FFFFFFF = all).</summary>
    public int Profiles { get; init; } = 0x7FFFFFFF;

    /// <summary>Protocol number (256 = any).</summary>
    public int Protocol { get; init; } = 256;
}

/// <summary>Windows Firewall access (INetFwPolicy2).</summary>
public interface IFirewallPort
{
    /// <summary>Finds a rule by name; null when absent.</summary>
    /// <param name="name">Rule name.</param>
    FirewallRuleInfo? GetRule(string name);

    /// <summary>Adds a rule.</summary>
    /// <param name="rule">Rule.</param>
    void AddRule(FirewallRuleInfo rule);

    /// <summary>Removes a rule by name.</summary>
    /// <param name="name">Rule name.</param>
    void RemoveRule(string name);
}

/// <summary>Microsoft Defender exclusion access (MSFT_MpPreference).</summary>
public interface IDefenderPort
{
    /// <summary>True when the path is an exclusion; throws when exclusions cannot be read (needs admin, tamper protection).</summary>
    /// <param name="path">Folder path.</param>
    bool IsExcluded(string path);

    /// <summary>Adds a path exclusion.</summary>
    /// <param name="path">Folder path.</param>
    void AddExclusion(string path);

    /// <summary>Removes a path exclusion.</summary>
    /// <param name="path">Folder path.</param>
    void RemoveExclusion(string path);
}

// ------------------------------------------------------------------ boot configuration

/// <summary>An allowlisted BCD element on <c>{current}</c>.</summary>
public enum BcdElement
{
    /// <summary>testsigning (BcdLibraryBoolean_AllowPrereleaseSignatures).</summary>
    TestSigning = 0,

    /// <summary>nointegritychecks (BcdLibraryBoolean_DisableIntegrityChecks).</summary>
    NoIntegrityChecks = 1,

    /// <summary>nx (BcdOSLoaderInteger_NxPolicy).</summary>
    Nx = 2,

    /// <summary>hypervisorlaunchtype (BcdOSLoaderInteger_HypervisorLaunchType).</summary>
    HypervisorLaunchType = 3,
}

/// <summary>BCD access (WMI BcdStore on the system store, {current} object).</summary>
public interface IBcdPort
{
    /// <summary>Reads an element as its bcdedit word (<c>on</c>/<c>off</c>, <c>OptIn</c>/<c>OptOut</c>/<c>AlwaysOn</c>/<c>AlwaysOff</c>,
    /// <c>Auto</c>/<c>Off</c>); null when the element is not set.</summary>
    /// <param name="element">Element.</param>
    string? Get(BcdElement element);

    /// <summary>Sets an element.</summary>
    /// <param name="element">Element.</param>
    /// <param name="value">bcdedit word.</param>
    void Set(BcdElement element, string value);

    /// <summary>Deletes an element (back to the default).</summary>
    /// <param name="element">Element.</param>
    void Delete(BcdElement element);
}

// ------------------------------------------------------------------ processes

/// <summary>One process from a ToolHelp snapshot (names and ids only; no handle is ever opened).</summary>
/// <param name="ProcessId">PID.</param>
/// <param name="ImageName">Executable file name.</param>
/// <param name="SessionId">Terminal session id (null when it could not be read).</param>
public sealed record ProcessEntry(int ProcessId, string ImageName, int? SessionId);

/// <summary>Process listing and graceful close (WM_CLOSE to top-level windows). Never kills, never opens a handle.</summary>
public interface IProcessPort
{
    /// <summary>Snapshot of running processes.</summary>
    IReadOnlyList<ProcessEntry> Snapshot();

    /// <summary>The interactive console session id.</summary>
    int ActiveConsoleSessionId();

    /// <summary>Posts WM_CLOSE to every top-level window of the process; returns how many windows got it.</summary>
    /// <param name="processId">PID.</param>
    int PostClose(int processId);
}

// ------------------------------------------------------------------ files

/// <summary>Metadata of a file.</summary>
public sealed record FileFacts
{
    /// <summary>Exists as a regular file.</summary>
    public required bool Exists { get; init; }

    /// <summary>Size.</summary>
    public long Size { get; init; }

    /// <summary>Last write time (UTC).</summary>
    public DateTimeOffset? LastWriteUtc { get; init; }
}

/// <summary>Well-known folders, from the OS (never from KB or plan text).</summary>
public sealed record KnownFolders
{
    /// <summary><c>%WINDIR%</c>.</summary>
    public required string Windows { get; init; }

    /// <summary><c>%ProgramFiles%</c> (64-bit).</summary>
    public required string ProgramFiles { get; init; }

    /// <summary><c>%ProgramFiles(x86)%</c>.</summary>
    public required string ProgramFilesX86 { get; init; }

    /// <summary><c>%ProgramData%</c>.</summary>
    public required string ProgramData { get; init; }

    /// <summary>The system drive (<c>C:</c>).</summary>
    public string SystemDrive => Windows[..2];
}

/// <summary>
/// File access for the quarantine. Moves are renames of the exact file object that was validated: opened without following
/// reparse points, every path component checked (no junction/symlink, trusted owner), never copied and deleted.
/// </summary>
public interface IFilePort
{
    /// <summary>Well-known folders.</summary>
    KnownFolders Folders { get; }

    /// <summary>The quarantine root (<c>%ProgramData%\VTDiag\quarantine</c>).</summary>
    string QuarantineRoot { get; }

    /// <summary>Reads metadata (does not follow a reparse point; a reparse point reports Exists=false).</summary>
    /// <param name="path">Full path.</param>
    FileFacts Probe(string path);

    /// <summary>SHA-256 (lowercase hex) of the file, read through a no-follow handle.</summary>
    /// <param name="path">Full path.</param>
    string Sha256(string path);

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/> safely (see the interface remarks); the
    /// destination folder is created inside the quarantine root with the admin-only ACL when needed. Refuses when the
    /// content hash differs from <paramref name="expectedSha256"/> or the destination exists.</summary>
    /// <param name="source">Full source path.</param>
    /// <param name="destination">Full destination path.</param>
    /// <param name="expectedSha256">Hash the moved file must have.</param>
    void SafeMove(string source, string destination, string expectedSha256);

    /// <summary>Writes the quarantine manifest next to a quarantined file.</summary>
    /// <param name="folder">Quarantine folder.</param>
    /// <param name="manifest">Manifest values.</param>
    void WriteManifest(string folder, IReadOnlyDictionary<string, string> manifest);

    /// <summary>True when the folder exists as a real directory.</summary>
    /// <param name="path">Full path.</param>
    bool DirectoryExists(string path);

    /// <summary>Real subdirectories of a folder (junctions and symlinks are left out); empty when it does not exist.</summary>
    /// <param name="path">Full path.</param>
    IReadOnlyList<string> ListDirectories(string path);
}

// ------------------------------------------------------------------ fixed Windows tools

/// <summary>The only external programs a primitive may start: fixed System32 executables with fixed arguments.</summary>
public enum SystemTool
{
    /// <summary><c>ipconfig.exe /flushdns</c>.</summary>
    FlushDns = 0,

    /// <summary><c>netsh.exe winsock reset</c>.</summary>
    WinsockReset = 1,

    /// <summary><c>netsh.exe int ip reset</c>.</summary>
    IpReset = 2,

    /// <summary><c>w32tm.exe /resync /force</c>.</summary>
    TimeResync = 3,
}

/// <summary>Runs a <see cref="SystemTool"/> (no shell, no window, bounded time).</summary>
public interface IToolRunner
{
    /// <summary>Runs the tool; returns the exit code.</summary>
    /// <param name="tool">Tool.</param>
    /// <param name="timeout">Limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<int> RunAsync(SystemTool tool, TimeSpan timeout, CancellationToken cancellationToken);
}

// ------------------------------------------------------------------ system protection and boot time

/// <summary>System Protection (restore points) configuration.</summary>
public interface ISystemProtectionPort
{
    /// <summary>True when System Protection is on for the drive; throws when it cannot be read.</summary>
    /// <param name="drive">Drive (<c>C:</c>).</param>
    bool IsEnabled(string drive);

    /// <summary>Configured maximum shadow storage, percent; null when unknown.</summary>
    int? MaxSizePercent();

    /// <summary>Turns System Protection on for the drive.</summary>
    /// <param name="drive">Drive.</param>
    void Enable(string drive);
}

/// <summary>Boot time of the running kernel.</summary>
public interface IBootClock
{
    /// <summary>When the running kernel started (UTC); null when unknown.</summary>
    DateTimeOffset? LastBootUtc();
}

/// <summary>Every port the primitives use.</summary>
public sealed record SystemPorts
{
    /// <summary>Services.</summary>
    public required IServicePort Services { get; init; }

    /// <summary>Devices.</summary>
    public required IDevicePort Devices { get; init; }

    /// <summary>Network.</summary>
    public required INetworkPort Network { get; init; }

    /// <summary>Registry.</summary>
    public required IRegistryPort Registry { get; init; }

    /// <summary>Power.</summary>
    public required IPowerPort Power { get; init; }

    /// <summary>Firewall.</summary>
    public required IFirewallPort Firewall { get; init; }

    /// <summary>Defender.</summary>
    public required IDefenderPort Defender { get; init; }

    /// <summary>BCD.</summary>
    public required IBcdPort Bcd { get; init; }

    /// <summary>Processes.</summary>
    public required IProcessPort Processes { get; init; }

    /// <summary>Files.</summary>
    public required IFilePort Files { get; init; }

    /// <summary>Tools.</summary>
    public required IToolRunner Tools { get; init; }

    /// <summary>System Protection.</summary>
    public required ISystemProtectionPort SystemProtection { get; init; }

    /// <summary>Boot clock.</summary>
    public required IBootClock Boot { get; init; }
}
