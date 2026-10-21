namespace VTD.Platform.Sources;

/// <summary>One process from a ToolHelp snapshot (no handle is ever opened).</summary>
public sealed record ProcessRec
{
    /// <summary>Image name.</summary>
    public required string Name { get; init; }

    /// <summary>PID.</summary>
    public required int Pid { get; init; }

    /// <summary>Parent PID.</summary>
    public int ParentPid { get; init; }

    /// <summary>Threads.</summary>
    public int Threads { get; init; }
}

/// <summary>Process snapshot.</summary>
public sealed record ProcessList
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Processes.</summary>
    public IReadOnlyList<ProcessRec> Items { get; init; } = [];
}

/// <summary>One service or driver from <c>EnumServicesStatusEx</c>.</summary>
public sealed record ServiceRec
{
    /// <summary>Service name.</summary>
    public required string Name { get; init; }

    /// <summary>Display name (localized).</summary>
    public string? DisplayName { get; init; }

    /// <summary>SERVICE_* type bits.</summary>
    public int Type { get; init; }

    /// <summary>SERVICE_* state (1 stopped .. 7 paused).</summary>
    public int State { get; init; }
}

/// <summary>Service list.</summary>
public sealed record ServiceList
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Services and drivers.</summary>
    public IReadOnlyList<ServiceRec> Items { get; init; } = [];
}

/// <summary>A loaded kernel module.</summary>
public sealed record DriverRec
{
    /// <summary>Base name.</summary>
    public required string Name { get; init; }

    /// <summary>Full path as the kernel reports it.</summary>
    public string? Path { get; init; }
}

/// <summary>Loaded kernel modules from <c>EnumDeviceDrivers</c> (no process handle involved).</summary>
public sealed record DriverList
{
    /// <summary>Status. AccessDenied when Windows hides module addresses from a non-elevated caller (24H2+).</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Modules.</summary>
    public IReadOnlyList<DriverRec> Items { get; init; } = [];
}

/// <summary>One PnP device node (CfgMgr32).</summary>
public sealed record DeviceRec
{
    /// <summary>Instance id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Setup class GUID (lowercase, braces).</summary>
    public string? ClassGuid { get; init; }

    /// <summary>Invariant class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Enumerator.</summary>
    public string? Enumerator { get; init; }

    /// <summary>Friendly name (or device description).</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Hardware ids.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Present now.</summary>
    public bool Present { get; init; }

    /// <summary>CM problem code (0 = none).</summary>
    public int ProblemCode { get; init; }

    /// <summary>DN_* status flags.</summary>
    public long StatusFlags { get; init; }

    /// <summary>Driver service.</summary>
    public string? Service { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Driver date (UTC midnight).</summary>
    public DateTimeOffset? DriverDate { get; init; }

    /// <summary>Driver provider.</summary>
    public string? DriverProvider { get; init; }

    /// <summary>INF name.</summary>
    public string? InfName { get; init; }

    /// <summary>Driver key under <c>Control\Class</c>, for example <c>{4d36e972-e325-11ce-bfc1-08002be10318}\0001</c>.</summary>
    public string? DriverKey { get; init; }

    /// <summary>Parent instance id.</summary>
    public string? Parent { get; init; }
}

/// <summary>PnP device list.</summary>
public sealed record DeviceList
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Devices.</summary>
    public IReadOnlyList<DeviceRec> Items { get; init; } = [];
}

/// <summary>Firmware facts that need kernel32 / firmware variables.</summary>
public sealed record FirmwareRaw
{
    /// <summary><c>GetFirmwareType</c>: 1 BIOS, 2 UEFI, 0 unknown.</summary>
    public int FirmwareType { get; init; }

    /// <summary>Status of the firmware variable reads (AccessDenied without SeSystemEnvironmentPrivilege).</summary>
    public ReadStatus VariablesStatus { get; init; } = ReadStatus.AccessDenied;

    /// <summary><c>SetupMode</c> byte.</summary>
    public int? SetupMode { get; init; }

    /// <summary><c>db</c> contains the "Windows UEFI CA 2023" certificate subject.</summary>
    public bool? DbHasCa2023 { get; init; }

    /// <summary><c>OsIndicationsSupported</c>.</summary>
    public long? OsIndicationsSupported { get; init; }
}

/// <summary>Power state from powrprof and kernel32.</summary>
public sealed record PowerRaw
{
    /// <summary>Status of the scheme reads.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Active scheme GUID (lowercase).</summary>
    public string? ActiveScheme { get; init; }

    /// <summary>Friendly name (localized).</summary>
    public string? SchemeName { get; init; }

    /// <summary>Effective overlay scheme GUID (Windows 11 power mode), lowercase.</summary>
    public string? Overlay { get; init; }

    /// <summary>USB selective suspend AC index.</summary>
    public int? UsbSelectiveSuspendAc { get; init; }

    /// <summary>USB selective suspend DC index.</summary>
    public int? UsbSelectiveSuspendDc { get; init; }

    /// <summary>Minimum processor state AC.</summary>
    public int? ProcessorMinAc { get; init; }

    /// <summary>Maximum processor state AC.</summary>
    public int? ProcessorMaxAc { get; init; }

    /// <summary><c>GetSystemPowerStatus</c> read.</summary>
    public bool PowerStatusOk { get; init; }

    /// <summary>ACLineStatus (0 off, 1 on, 255 unknown).</summary>
    public int AcLine { get; init; }

    /// <summary>BatteryFlag (128 = no battery, 255 unknown).</summary>
    public int BatteryFlag { get; init; }

    /// <summary>Battery percent (255 unknown).</summary>
    public int BatteryPercent { get; init; }
}

/// <summary>Memory status.</summary>
public sealed record MemoryRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Total physical (bytes).</summary>
    public long TotalBytes { get; init; }

    /// <summary>Available physical (bytes).</summary>
    public long AvailableBytes { get; init; }

    /// <summary>Standby lists sum (bytes) from English PDH counters, when readable.</summary>
    public long? StandbyBytes { get; init; }
}

/// <summary>CPU topology and CPUID bits.</summary>
public sealed record CpuRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Physical cores.</summary>
    public int Cores { get; init; }

    /// <summary>Logical processors.</summary>
    public int Logical { get; init; }

    /// <summary>Cores per efficiency class (index = class).</summary>
    public IReadOnlyList<int> CoresByEfficiencyClass { get; init; } = [];

    /// <summary><c>x64</c>, <c>arm64</c>...</summary>
    public string? Architecture { get; init; }

    /// <summary>CPUID vendor string.</summary>
    public string? CpuidVendor { get; init; }

    /// <summary>CPUID was readable.</summary>
    public bool CpuidAvailable { get; init; }

    /// <summary>CPUID.1:ECX.VMX.</summary>
    public bool Vmx { get; init; }

    /// <summary>CPUID.80000001:ECX.SVM.</summary>
    public bool Svm { get; init; }

    /// <summary>CPUID.1:ECX bit 31 (a hypervisor is present).</summary>
    public bool HypervisorBit { get; init; }
}

/// <summary>One active display path.</summary>
public sealed record DisplayPathRec
{
    /// <summary>EDID friendly name.</summary>
    public string? Name { get; init; }

    /// <summary>Width.</summary>
    public int Width { get; init; }

    /// <summary>Height.</summary>
    public int Height { get; init; }

    /// <summary>Refresh (Hz, rounded).</summary>
    public int RefreshHz { get; init; }

    /// <summary>Source at (0,0).</summary>
    public bool Primary { get; init; }

    /// <summary>Advanced color (HDR) enabled, when known.</summary>
    public bool? Hdr { get; init; }

    /// <summary>Internal panel.</summary>
    public bool? Internal { get; init; }

    /// <summary>Adapter device interface path.</summary>
    public string? AdapterDevicePath { get; init; }
}

/// <summary>Display topology.</summary>
public sealed record DisplayRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Active paths.</summary>
    public IReadOnlyList<DisplayPathRec> Paths { get; init; } = [];
}

/// <summary>One network interface (System.Net + iphlpapi).</summary>
public sealed record NetIfRec
{
    /// <summary>Interface GUID with braces, uppercase.</summary>
    public required string Guid { get; init; }

    /// <summary>Description.</summary>
    public string? Description { get; init; }

    /// <summary>Alias.</summary>
    public string? Alias { get; init; }

    /// <summary>NetworkInterfaceType numeric value.</summary>
    public int IfType { get; init; }

    /// <summary>OperationalStatus numeric value.</summary>
    public int OperStatus { get; init; }

    /// <summary>Speed (bits/s), -1 unknown.</summary>
    public long SpeedBps { get; init; }

    /// <summary>MAC (AA:BB:...).</summary>
    public string? Mac { get; init; }

    /// <summary>IPv4 addresses.</summary>
    public IReadOnlyList<string> Ipv4 { get; init; } = [];

    /// <summary>DNS servers.</summary>
    public IReadOnlyList<string> Dns { get; init; } = [];

    /// <summary>IPv4 gateways.</summary>
    public IReadOnlyList<string> Gateways { get; init; } = [];

    /// <summary>IPv4 interface index (0 = no IPv4).</summary>
    public int Ipv4Index { get; init; }

    /// <summary>Effective IPv4 metric.</summary>
    public int? Ipv4Metric { get; init; }

    /// <summary>Automatic metric.</summary>
    public bool? AutomaticMetric { get; init; }
}

/// <summary>Network interfaces and routing.</summary>
public sealed record NetRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Interfaces.</summary>
    public IReadOnlyList<NetIfRec> Interfaces { get; init; } = [];

    /// <summary>IPv4 interface index of the best route to the internet (no packet sent), 0 = none.</summary>
    public int BestIpv4Index { get; init; }

    /// <summary>WinHTTP default proxy is set (<c>WinHttpGetDefaultProxyConfiguration</c>).</summary>
    public bool? WinHttpProxy { get; init; }
}

/// <summary>Network List Manager connectivity.</summary>
public sealed record ConnectivityRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>NLM_CONNECTIVITY flags.</summary>
    public int Flags { get; init; }
}

/// <summary>One WLAN interface.</summary>
public sealed record WlanIfRec
{
    /// <summary>Interface GUID.</summary>
    public required string Guid { get; init; }

    /// <summary>Connected.</summary>
    public bool Connected { get; init; }

    /// <summary>SSID (null when Windows withholds it without location permission).</summary>
    public string? Ssid { get; init; }

    /// <summary>Signal quality 0-100.</summary>
    public int? SignalQuality { get; init; }

    /// <summary>RSSI (dBm).</summary>
    public int? Rssi { get; init; }

    /// <summary>DOT11_PHY_TYPE.</summary>
    public int? PhyType { get; init; }

    /// <summary>Receive rate (kbps).</summary>
    public int? RxRateKbps { get; init; }

    /// <summary>Channel center frequency (kHz) of the associated BSS.</summary>
    public int? FrequencyKhz { get; init; }
}

/// <summary>WLAN state.</summary>
public sealed record WlanRaw
{
    /// <summary>Status (Unsupported when WLAN AutoConfig is not running).</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Interfaces.</summary>
    public IReadOnlyList<WlanIfRec> Interfaces { get; init; } = [];
}

/// <summary>TBS device info (no admin).</summary>
public sealed record TpmRaw
{
    /// <summary>Status (NotFound = no TPM).</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>TPM_VERSION_12 = 1, TPM_VERSION_20 = 2.</summary>
    public int Version { get; init; }

    /// <summary>TPM interface type.</summary>
    public int InterfaceType { get; init; }
}

/// <summary>A clock reference measurement.</summary>
public sealed record ClockRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Local minus reference (seconds).</summary>
    public long? SkewSeconds { get; init; }

    /// <summary><c>ntp</c> or <c>https-date</c>.</summary>
    public string? Source { get; init; }
}

/// <summary>ICMP probe result.</summary>
public sealed record PingRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Sent.</summary>
    public int Sent { get; init; }

    /// <summary>Round trips of successful replies (ms).</summary>
    public IReadOnlyList<long> RttMs { get; init; } = [];
}

/// <summary>Process environment facts.</summary>
public sealed record EnvRaw
{
    /// <summary>Windows time zone id.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>Windows UI culture name.</summary>
    public required string UiCulture { get; init; }

    /// <summary>Milliseconds since boot.</summary>
    public long TickCount64 { get; init; }

    /// <summary>Windows directory.</summary>
    public required string WindowsDir { get; init; }

    /// <summary>Program Files.</summary>
    public required string ProgramFiles { get; init; }

    /// <summary>Program Files (x86).</summary>
    public required string ProgramFilesX86 { get; init; }

    /// <summary>ProgramData.</summary>
    public required string ProgramData { get; init; }

    /// <summary>This process is elevated.</summary>
    public bool Elevated { get; init; }

    /// <summary>OS architecture.</summary>
    public string? OsArchitecture { get; init; }

    /// <summary>SID of the account this process runs as (per-user COM reads such as default audio endpoints are only valid when it is the player).</summary>
    public string? UserSid { get; init; }
}

/// <summary>BitLocker protection of a volume from the shell property system (no admin).</summary>
public sealed record BitLockerRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary><c>System.Volume.BitLockerProtection</c> value.</summary>
    public int? Protection { get; init; }
}

/// <summary>BCD facts read through the allowlisted WMI methods (admin).</summary>
public sealed record BcdRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary><c>testsigning</c>.</summary>
    public bool? TestSigning { get; init; }

    /// <summary><c>nointegritychecks</c>.</summary>
    public bool? NoIntegrityChecks { get; init; }

    /// <summary><c>nx</c> integer.</summary>
    public long? Nx { get; init; }

    /// <summary><c>hypervisorlaunchtype</c> integer.</summary>
    public long? HypervisorLaunchType { get; init; }

    /// <summary><c>safeboot</c> integer (present = set).</summary>
    public long? SafeBoot { get; init; }

    /// <summary><c>debug</c> (kernel debugger).</summary>
    public bool? Debug { get; init; }

    /// <summary>Firmware boot manager display-order entry paths (lowercase), null entries for device entries.</summary>
    public IReadOnlyList<string> FirmwareEntryPaths { get; init; } = [];

    /// <summary>Firmware entries were readable.</summary>
    public bool FirmwareEntriesRead { get; init; }
}

/// <summary>Win32_Tpm facts (admin).</summary>
public sealed record TpmWmiRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Spec version (first token of <c>SpecVersion</c>).</summary>
    public string? SpecVersion { get; init; }

    /// <summary>Manufacturer id text.</summary>
    public string? ManufacturerIdTxt { get; init; }

    /// <summary>Enabled.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Activated.</summary>
    public bool? Activated { get; init; }

    /// <summary>IsReady method result.</summary>
    public bool? Ready { get; init; }
}

/// <summary>Windows Update Agent state (read-only properties of the WUA installer object).</summary>
public sealed record WuaRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary><c>IUpdateInstaller.IsBusy</c>: an installation or uninstallation is in progress.</summary>
    public bool? IsBusy { get; init; }
}

/// <summary>Default audio endpoints of the calling user (MMDevice API).</summary>
public sealed record AudioDefaultsRaw
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Default render endpoint id (console role).</summary>
    public string? RenderConsole { get; init; }

    /// <summary>Default render endpoint id (communications role).</summary>
    public string? RenderCommunications { get; init; }

    /// <summary>Default capture endpoint id (console role).</summary>
    public string? CaptureConsole { get; init; }

    /// <summary>Default capture endpoint id (communications role).</summary>
    public string? CaptureCommunications { get; init; }
}