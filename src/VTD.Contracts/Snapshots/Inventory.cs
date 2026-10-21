namespace VTD.Contracts.Snapshots;

/// <summary>
/// One PnP device (check ids: pnp_errors, realtek_nic, vmd, intel_driver, usb_devices). Inside list items a null
/// nullable value means "not read" (Unknown), never false.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record DeviceInfo
{
    /// <summary>PnP instance id, for example <c>PCI\VEN_10EC&amp;DEV_8125&amp;SUBSYS_…\01000000684CE00000</c>.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public required string InstanceId { get; init; }

    /// <summary>Device setup class GUID.</summary>
    public required string ClassGuid { get; init; }

    /// <summary>Invariant class name from the registry (<c>Net</c>, <c>Display</c>, <c>MEDIA</c>, <c>HIDClass</c>...).</summary>
    public string? ClassName { get; init; }

    /// <summary>Enumerator: <c>PCI</c>, <c>USB</c>, <c>HID</c>, <c>BTHENUM</c>, <c>ACPI</c>, <c>SWD</c>...</summary>
    public string? Enumerator { get; init; }

    /// <summary>Friendly name (may be localized or user-renamed; display only).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? FriendlyName { get; init; }

    /// <summary>Hardware ids.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Currently present.</summary>
    public required bool Present { get; init; }

    /// <summary>Not disabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>CM problem code; 0 = working, 22 = disabled, 28 = no driver, 43 = stopped after reporting problems...</summary>
    public int ProblemCode { get; init; }

    /// <summary>Driver service name.</summary>
    public string? Service { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Driver date.</summary>
    public DateOnly? DriverDate { get; init; }

    /// <summary>Driver provider.</summary>
    public string? DriverProvider { get; init; }

    /// <summary>INF name, for example <c>oem42.inf</c>.</summary>
    public string? InfName { get; init; }
}

/// <summary>Authenticode signature status.</summary>
public enum SignatureStatus
{
    /// <summary>Not checked.</summary>
    Unknown = 0,

    /// <summary>Valid signature.</summary>
    Valid = 1,

    /// <summary>Not signed.</summary>
    Unsigned = 2,

    /// <summary>Signature present but invalid, revoked or untrusted.</summary>
    Invalid = 3,
}

/// <summary>Signer of a binary.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record SignerInfo
{
    /// <summary>Status.</summary>
    public required SignatureStatus Status { get; init; }

    /// <summary>Publisher (certificate subject CN).</summary>
    public string? Publisher { get; init; }

    /// <summary>Signed by Microsoft.</summary>
    public bool IsMicrosoft { get; init; }
}

/// <summary>A loaded kernel driver.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record LoadedDriver
{
    /// <summary>File name, for example <c>rt25cx21x64.sys</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Full path.</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? Path { get; init; }

    /// <summary>File version.</summary>
    public string? Version { get; init; }

    /// <summary>Signer.</summary>
    public SignerInfo? Signer { get; init; }
}

/// <summary>Service start type.</summary>
public enum ServiceStartType
{
    /// <summary>Boot.</summary>
    Boot = 0,

    /// <summary>System.</summary>
    System = 1,

    /// <summary>Automatic.</summary>
    Automatic = 2,

    /// <summary>Manual (vgc Manual + Stopped is normal with Vanguard Pre-Check).</summary>
    Manual = 3,

    /// <summary>Disabled.</summary>
    Disabled = 4,
}

/// <summary>Service state.</summary>
public enum ServiceState
{
    /// <summary>Stopped.</summary>
    Stopped = 0,

    /// <summary>Start pending.</summary>
    StartPending = 1,

    /// <summary>Stop pending.</summary>
    StopPending = 2,

    /// <summary>Running.</summary>
    Running = 3,

    /// <summary>Continue pending.</summary>
    ContinuePending = 4,

    /// <summary>Pause pending.</summary>
    PausePending = 5,

    /// <summary>Paused.</summary>
    Paused = 6,
}

/// <summary>Service kind.</summary>
public enum ServiceKind
{
    /// <summary>Win32 service (own or shared process).</summary>
    Win32 = 0,

    /// <summary>Kernel driver.</summary>
    KernelDriver = 1,

    /// <summary>File system driver.</summary>
    FileSystemDriver = 2,

    /// <summary>Per-user service template or instance.</summary>
    UserService = 3,
}

/// <summary>A service from the SCM. Match on <see cref="Name"/>, never on <see cref="DisplayName"/> (localized).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ServiceInfo
{
    /// <summary>Service name, for example <c>vgc</c>, <c>NahimicService</c>, <c>Bonjour Service</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Display name (localized for Microsoft services; display only).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Kind.</summary>
    public required ServiceKind Kind { get; init; }

    /// <summary>Start type.</summary>
    public required ServiceStartType StartType { get; init; }

    /// <summary>Delayed automatic start.</summary>
    public bool DelayedAutoStart { get; init; }

    /// <summary>Current state.</summary>
    public required ServiceState State { get; init; }

    /// <summary>Binary path without arguments.</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? ImagePath { get; init; }

    /// <summary>Signer of the image.</summary>
    public SignerInfo? Signer { get; init; }
}

/// <summary>A running process from a ToolHelp snapshot. Names and ids only: VTDiag never opens a process handle.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ProcessInfo
{
    /// <summary>Image name, for example <c>NahimicSvc64.exe</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Process id (volatile; fixes bind by name).</summary>
    public required int ProcessId { get; init; }

    /// <summary>Parent process id.</summary>
    public int? ParentProcessId { get; init; }

    /// <summary>Thread count from the snapshot.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>The name is in <see cref="RiotProcessNames.All"/>.</summary>
    public bool IsProtectedRiotProcess { get; init; }
}

/// <summary>Uninstall-key hive.</summary>
public enum AppHive
{
    /// <summary><c>HKLM\…\Uninstall</c>.</summary>
    Hklm = 0,

    /// <summary><c>HKLM\…\WOW6432Node\…\Uninstall</c>.</summary>
    Hklm32 = 1,

    /// <summary>The player's <c>HKCU\…\Uninstall</c>. Its UninstallString is never run elevated (critique section 1.3).</summary>
    Hkcu = 2,
}

/// <summary>
/// An installed application. The uninstall command line is deliberately NOT captured: VTD.Fixes re-reads it from HKLM
/// at apply time with a verified publisher, and never executes an HKCU uninstall string as admin.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record InstalledApp
{
    /// <summary>Display name (diagnostic, kept at share level, swept as free text).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public required string DisplayName { get; init; }

    /// <summary>Publisher.</summary>
    public string? Publisher { get; init; }

    /// <summary>Display version.</summary>
    public string? Version { get; init; }

    /// <summary>Install date.</summary>
    public DateOnly? InstallDate { get; init; }

    /// <summary>Hive.</summary>
    public required AppHive Hive { get; init; }

    /// <summary>Uninstall subkey name (often a GUID).</summary>
    public required string KeyName { get; init; }

    /// <summary>A <c>QuietUninstallString</c> exists.</summary>
    public bool HasQuietUninstall { get; init; }

    /// <summary><c>SystemComponent = 1</c>.</summary>
    public bool SystemComponent { get; init; }

    /// <summary>Install location.</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? InstallLocation { get; init; }
}

/// <summary>Startup entry location.</summary>
public enum StartupLocation
{
    /// <summary><c>HKLM\…\Run</c>.</summary>
    HklmRun = 0,

    /// <summary><c>HKCU\…\Run</c>.</summary>
    HkcuRun = 1,

    /// <summary><c>HKLM\…\RunOnce</c>.</summary>
    HklmRunOnce = 2,

    /// <summary><c>HKCU\…\RunOnce</c>.</summary>
    HkcuRunOnce = 3,

    /// <summary>The player's Startup folder.</summary>
    StartupFolderUser = 4,

    /// <summary>The all-users Startup folder.</summary>
    StartupFolderCommon = 5,

    /// <summary>A scheduled task with a logon trigger.</summary>
    LogonTask = 6,
}

/// <summary>A startup entry. Command lines are not collected (they can carry tokens); only the target image.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record StartupItem
{
    /// <summary>Entry name (free text chosen by the app or the user).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public required string Name { get; init; }

    /// <summary>Location.</summary>
    public required StartupLocation Location { get; init; }

    /// <summary>Target executable name, for example <c>Discord.exe</c>.</summary>
    public string? ImageName { get; init; }

    /// <summary>Target executable path (no arguments).</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? TargetPath { get; init; }

    /// <summary>Enabled (StartupApproved).</summary>
    public bool? Enabled { get; init; }
}
