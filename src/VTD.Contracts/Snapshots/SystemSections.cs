namespace VTD.Contracts.Snapshots;

/// <summary>Windows state (check ids: win_build, pending_reboot, hotfix, env_vars, clock, modified Windows).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record OsInfo
{
    /// <summary><c>CurrentVersion\ProductName</c> (not localized by Windows; may say "Windows 10" on Windows 11).</summary>
    public required Fact<string> ProductName { get; init; }

    /// <summary><c>CurrentBuildNumber</c>, for example 26200.</summary>
    public required Fact<int> Build { get; init; }

    /// <summary>Update build revision (UBR).</summary>
    public required Fact<int> Ubr { get; init; }

    /// <summary><c>DisplayVersion</c>, for example <c>25H2</c>.</summary>
    public required Fact<string> DisplayVersion { get; init; }

    /// <summary><c>EditionID</c>, for example <c>Professional</c>, <c>CoreN</c>, <c>EnterpriseS</c> (LTSC).</summary>
    public required Fact<string> EditionId { get; init; }

    /// <summary>Insider branch name; NotApplicable when not an Insider build.</summary>
    public required Fact<string> InsiderBranch { get; init; }

    /// <summary>Windows install date (UTC).</summary>
    public required Fact<DateTimeOffset> InstallDateUtc { get; init; }

    /// <summary>Running inside a virtual machine (VALORANT does not support VMs: out-of-scope route).</summary>
    public required Fact<bool> IsVirtualMachine { get; init; }

    /// <summary>Pending reboot sources.</summary>
    public required Fact<PendingRebootInfo> PendingReboot { get; init; }

    /// <summary>A Windows Update installation is in progress.</summary>
    public required Fact<bool> WindowsUpdateInstalling { get; init; }

    /// <summary>Fast Startup (<c>HiberbootEnabled</c>). Also a fix target (fast-startup-stale-dat-bsod).</summary>
    public required Fact<bool> FastStartupEnabled { get; init; }

    /// <summary>Hibernation enabled.</summary>
    public required Fact<bool> HibernationEnabled { get; init; }

    /// <summary>Clock state.</summary>
    public required Fact<ClockInfo> Clock { get; init; }

    /// <summary>Installed hotfixes (KB ids).</summary>
    public required Fact<IReadOnlyList<HotfixInfo>> Hotfixes { get; init; }

    /// <summary>Markers of modified Windows images (Atlas, ReviOS, Tiny11...). Known-empty means "looks stock".</summary>
    public required Fact<IReadOnlyList<string>> ModifiedWindowsMarkers { get; init; }

    /// <summary>Environment variable problems (names and issue codes only, never values).</summary>
    public required Fact<IReadOnlyList<EnvVarFinding>> EnvironmentIssues { get; init; }

    /// <summary>Task Scheduler service usable (debloaters disable it; resume then degrades to RunOnce).</summary>
    public required Fact<bool> TaskSchedulerAvailable { get; init; }

    /// <summary>The interactive player is a member of Administrators. False means "standard user with a separate admin":
    /// resume falls back to an in-context RunOnce plus UAC (critique section 3.1).</summary>
    public required Fact<bool> InteractiveUserIsAdmin { get; init; }

    /// <summary>All facts Unknown with one reason (collector did not run or failed).</summary>
    /// <param name="reason">The reason.</param>
    public static OsInfo Unknown(UnknownReason reason) => new()
    {
        ProductName = Fact<string>.Unknown(reason),
        Build = Fact<int>.Unknown(reason),
        Ubr = Fact<int>.Unknown(reason),
        DisplayVersion = Fact<string>.Unknown(reason),
        EditionId = Fact<string>.Unknown(reason),
        InsiderBranch = Fact<string>.Unknown(reason),
        InstallDateUtc = Fact<DateTimeOffset>.Unknown(reason),
        IsVirtualMachine = Fact<bool>.Unknown(reason),
        PendingReboot = Fact<PendingRebootInfo>.Unknown(reason),
        WindowsUpdateInstalling = Fact<bool>.Unknown(reason),
        FastStartupEnabled = Fact<bool>.Unknown(reason),
        HibernationEnabled = Fact<bool>.Unknown(reason),
        Clock = Fact<ClockInfo>.Unknown(reason),
        Hotfixes = Fact<IReadOnlyList<HotfixInfo>>.Unknown(reason),
        ModifiedWindowsMarkers = Fact<IReadOnlyList<string>>.Unknown(reason),
        EnvironmentIssues = Fact<IReadOnlyList<EnvVarFinding>>.Unknown(reason),
        TaskSchedulerAvailable = Fact<bool>.Unknown(reason),
        InteractiveUserIsAdmin = Fact<bool>.Unknown(reason),
    };
}

/// <summary>Pending reboot sources.</summary>
public sealed record PendingRebootInfo
{
    /// <summary>Component Based Servicing <c>RebootPending</c>.</summary>
    public required bool ComponentServicing { get; init; }

    /// <summary>Windows Update <c>RebootRequired</c>.</summary>
    public required bool WindowsUpdate { get; init; }

    /// <summary><c>PendingFileRenameOperations</c> present.</summary>
    public required bool PendingFileRename { get; init; }
}

/// <summary>Clock state (check id: clock). Clock skew is resynced or tolerated BEFORE signature and expiry checks.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ClockInfo
{
    /// <summary>Local clock minus reference time, in seconds (positive = PC is ahead). Null when no reference was reachable.</summary>
    public long? SkewSeconds { get; init; }

    /// <summary>Where the reference came from (<c>ntp</c>, <c>https-date</c>), when measured.</summary>
    public string? ReferenceSource { get; init; }

    /// <summary>Windows time zone id (for example <c>Turkey Standard Time</c>).</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>"Set time automatically" is on.</summary>
    public bool? AutoTimeSync { get; init; }
}

/// <summary>One installed hotfix.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record HotfixInfo
{
    /// <summary>KB id, for example <c>KB5066835</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Install date when known.</summary>
    public DateOnly? InstalledOn { get; init; }
}

/// <summary>An environment variable problem (values are never collected).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record EnvVarFinding
{
    /// <summary>Variable name, for example <c>TEMP</c> or <c>__COMPAT_LAYER</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Issue code: <c>missing</c>, <c>points_to_missing_dir</c>, <c>too_long</c>, <c>unexpected_set</c>.</summary>
    public required string Issue { get; init; }

    /// <summary>Scope: <c>machine</c>, <c>user</c> or <c>process</c>.</summary>
    public required string Scope { get; init; }
}

/// <summary>Hardware (check ids: hybrid_cpu, ram_config, igpu_state, gpu_driver, bios_age, standby_mem).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record HardwareInfo
{
    /// <summary>CPU.</summary>
    public required Fact<CpuInfo> Cpu { get; init; }

    /// <summary>Memory.</summary>
    public required Fact<MemoryInfo> Memory { get; init; }

    /// <summary>Display adapters (DXGI + PnP).</summary>
    public required Fact<IReadOnlyList<GpuInfo>> Gpus { get; init; }

    /// <summary>Board, system and BIOS (no serials: they are never collected).</summary>
    public required Fact<BoardInfo> Board { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static HardwareInfo Unknown(UnknownReason reason) => new()
    {
        Cpu = Fact<CpuInfo>.Unknown(reason),
        Memory = Fact<MemoryInfo>.Unknown(reason),
        Gpus = Fact<IReadOnlyList<GpuInfo>>.Unknown(reason),
        Board = Fact<BoardInfo>.Unknown(reason),
    };
}

/// <summary>CPU vendor.</summary>
public enum CpuVendor
{
    /// <summary>Other or unknown vendor.</summary>
    Other = 0,

    /// <summary>Intel.</summary>
    Intel = 1,

    /// <summary>AMD.</summary>
    Amd = 2,

    /// <summary>Qualcomm or other ARM vendor (VALORANT does not support ARM).</summary>
    Arm = 3,
}

/// <summary>CPU facts.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CpuInfo
{
    /// <summary>Vendor.</summary>
    public required CpuVendor Vendor { get; init; }

    /// <summary>Brand string, for example <c>AMD Ryzen 7 9800X3D 8-Core Processor</c>.</summary>
    public required string Model { get; init; }

    /// <summary>Processor architecture: <c>x64</c> or <c>arm64</c>.</summary>
    public required string Architecture { get; init; }

    /// <summary>Physical cores.</summary>
    public required int Cores { get; init; }

    /// <summary>Logical processors.</summary>
    public required int LogicalProcessors { get; init; }

    /// <summary>Hybrid (P-cores + E-cores) by <c>GetLogicalProcessorInformationEx</c> efficiency classes.</summary>
    public required bool Hybrid { get; init; }

    /// <summary>Performance cores on hybrid CPUs.</summary>
    public int? PerformanceCores { get; init; }

    /// <summary>Efficiency cores on hybrid CPUs.</summary>
    public int? EfficiencyCores { get; init; }

    /// <summary>VMX/SVM supported by the CPU.</summary>
    public bool? VirtualizationSupported { get; init; }
}

/// <summary>Memory facts (XMP/EXPO hint = configured speed far below rated speed).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MemoryInfo
{
    /// <summary>Installed physical memory (MB).</summary>
    public required long TotalMb { get; init; }

    /// <summary>Available memory at scan time (MB).</summary>
    public long? AvailableMb { get; init; }

    /// <summary>Standby list size at scan time (MB).</summary>
    public long? StandbyMb { get; init; }

    /// <summary>Memory modules.</summary>
    public IReadOnlyList<MemoryModule> Modules { get; init; } = [];
}

/// <summary>One memory module (no serial number).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MemoryModule
{
    /// <summary>Capacity (MB).</summary>
    public required long CapacityMb { get; init; }

    /// <summary>Rated speed (MT/s).</summary>
    public int? RatedMts { get; init; }

    /// <summary>Configured speed (MT/s).</summary>
    public int? ConfiguredMts { get; init; }

    /// <summary>Slot label, for example <c>DIMM_A2</c>.</summary>
    public string? Slot { get; init; }

    /// <summary>Manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Part number.</summary>
    public string? PartNumber { get; init; }
}

/// <summary>GPU vendor by PCI vendor id.</summary>
public enum GpuVendor
{
    /// <summary>Other.</summary>
    Other = 0,

    /// <summary>NVIDIA (10DE).</summary>
    Nvidia = 1,

    /// <summary>AMD (1002).</summary>
    Amd = 2,

    /// <summary>Intel (8086).</summary>
    Intel = 3,

    /// <summary>Microsoft Basic Display / Render driver.</summary>
    Microsoft = 4,
}

/// <summary>One display adapter.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GpuInfo
{
    /// <summary>PnP instance id.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public required string InstanceId { get; init; }

    /// <summary>Adapter name (driver-provided).</summary>
    public required string Name { get; init; }

    /// <summary>Vendor.</summary>
    public required GpuVendor Vendor { get; init; }

    /// <summary>PCI vendor id, hex, for example <c>10DE</c>.</summary>
    public string? PciVendorId { get; init; }

    /// <summary>PCI device id, hex.</summary>
    public string? PciDeviceId { get; init; }

    /// <summary>Integrated GPU.</summary>
    public required bool IsIntegrated { get; init; }

    /// <summary>Device enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>PnP problem code (0 = working).</summary>
    public int ProblemCode { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Driver date.</summary>
    public DateOnly? DriverDate { get; init; }

    /// <summary>Driver provider.</summary>
    public string? DriverProvider { get; init; }

    /// <summary>Dedicated video memory (MB).</summary>
    public long? DedicatedVideoMb { get; init; }

    /// <summary>Microsoft Basic Display Adapter is in use (driver missing or broken).</summary>
    public bool IsBasicDisplayAdapter { get; init; }
}

/// <summary>Board, system and BIOS identity (no serial numbers).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record BoardInfo
{
    /// <summary>Baseboard manufacturer, for example <c>ASUSTeK COMPUTER INC.</c>.</summary>
    public string? BoardManufacturer { get; init; }

    /// <summary>Baseboard product, for example <c>ROG STRIX B650E-F GAMING WIFI</c>.</summary>
    public string? BoardProduct { get; init; }

    /// <summary>System manufacturer (OEM).</summary>
    public string? SystemManufacturer { get; init; }

    /// <summary>System product name.</summary>
    public string? SystemProduct { get; init; }

    /// <summary>BIOS vendor.</summary>
    public string? BiosVendor { get; init; }

    /// <summary>BIOS version string.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>BIOS release date.</summary>
    public DateOnly? BiosReleaseDate { get; init; }

    /// <summary>Portable chassis or a battery present.</summary>
    public bool? IsLaptop { get; init; }
}

/// <summary>Firmware boot mode.</summary>
public enum FirmwareType
{
    /// <summary>UEFI.</summary>
    Uefi = 0,

    /// <summary>Legacy BIOS / CSM.</summary>
    Bios = 1,
}

/// <summary>Firmware facts (check ids: uefi_gpt, dualboot).</summary>
public sealed record FirmwareInfo
{
    /// <summary><c>GetFirmwareType</c>.</summary>
    public required Fact<FirmwareType> Type { get; init; }

    /// <summary>"Restart into firmware settings" is supported (OsIndications support).</summary>
    public required Fact<bool> BootToFirmwareSupported { get; init; }

    /// <summary>Number of non-Windows boot entries (dual boot hint).</summary>
    public required Fact<int> OtherOsBootEntries { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static FirmwareInfo Unknown(UnknownReason reason) => new()
    {
        Type = Fact<FirmwareType>.Unknown(reason),
        BootToFirmwareSupported = Fact<bool>.Unknown(reason),
        OtherOsBootEntries = Fact<int>.Unknown(reason),
    };
}

/// <summary>
/// Vanguard readiness and security state (check ids: secure_boot, sb_keys, tpm, hvci, virtualization,
/// hvci_incompat_drivers, iommu_dma, ifeo, av_products). Admin-only reads are Unknown(NeedsAdmin) in limited mode; the
/// non-admin <c>SecureBoot\State</c> registry read and the admin firmware-variable read are reconciled, not collapsed.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record SecurityState
{
    /// <summary>Secure Boot on (<c>SecureBoot\State\UEFISecureBootEnabled</c>, no admin needed).</summary>
    public required Fact<bool> SecureBootEnabled { get; init; }

    /// <summary>Secure Boot in setup mode (no keys installed; admin firmware variable).</summary>
    public required Fact<bool> SecureBootSetupMode { get; init; }

    /// <summary>The <c>db</c> contains the Windows UEFI CA 2023 certificate (admin).</summary>
    public required Fact<bool> SecureBootDbHasCa2023 { get; init; }

    /// <summary>TPM.</summary>
    public required Fact<TpmInfo> Tpm { get; init; }

    /// <summary>VBS / HVCI (Memory Integrity) from Win32_DeviceGuard.</summary>
    public required Fact<VbsInfo> Vbs { get; init; }

    /// <summary>Driver file names that block or are incompatible with HVCI (CodeIntegrity events, compatibility scan).</summary>
    public required Fact<IReadOnlyList<string>> HvciIncompatibleDrivers { get; init; }

    /// <summary>Virtualization enabled in firmware (VT-x / SVM).</summary>
    public required Fact<bool> VirtualizationFirmwareEnabled { get; init; }

    /// <summary>A hypervisor is running.</summary>
    public required Fact<bool> HypervisorPresent { get; init; }

    /// <summary>Kernel DMA Protection (IOMMU) is on.</summary>
    public required Fact<bool> KernelDmaProtection { get; init; }

    /// <summary>BCD flags of the current boot entry (admin).</summary>
    public required Fact<BcdFlags> Bcd { get; init; }

    /// <summary>BitLocker on the system volume. Firmware/boot changes need the recovery key first (S11).</summary>
    public required Fact<BitLockerStatus> SystemVolumeBitLocker { get; init; }

    /// <summary>Microsoft Defender state.</summary>
    public required Fact<DefenderInfo> Defender { get; init; }

    /// <summary>Antivirus products registered with Security Center.</summary>
    public required Fact<IReadOnlyList<AvProduct>> AntivirusProducts { get; init; }

    /// <summary>Exploit Protection overrides (IFEO <c>MitigationOptions</c>) on Riot and Vanguard executables (VAN 9002).</summary>
    public required Fact<IReadOnlyList<ExploitOverride>> ExploitOverrides { get; init; }

    /// <summary>IFEO entries for Riot/Vanguard executables and well-known tools (debugger hijacks, PerfOptions).</summary>
    public required Fact<IReadOnlyList<IfeoEntry>> Ifeo { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static SecurityState Unknown(UnknownReason reason) => new()
    {
        SecureBootEnabled = Fact<bool>.Unknown(reason),
        SecureBootSetupMode = Fact<bool>.Unknown(reason),
        SecureBootDbHasCa2023 = Fact<bool>.Unknown(reason),
        Tpm = Fact<TpmInfo>.Unknown(reason),
        Vbs = Fact<VbsInfo>.Unknown(reason),
        HvciIncompatibleDrivers = Fact<IReadOnlyList<string>>.Unknown(reason),
        VirtualizationFirmwareEnabled = Fact<bool>.Unknown(reason),
        HypervisorPresent = Fact<bool>.Unknown(reason),
        KernelDmaProtection = Fact<bool>.Unknown(reason),
        Bcd = Fact<BcdFlags>.Unknown(reason),
        SystemVolumeBitLocker = Fact<BitLockerStatus>.Unknown(reason),
        Defender = Fact<DefenderInfo>.Unknown(reason),
        AntivirusProducts = Fact<IReadOnlyList<AvProduct>>.Unknown(reason),
        ExploitOverrides = Fact<IReadOnlyList<ExploitOverride>>.Unknown(reason),
        Ifeo = Fact<IReadOnlyList<IfeoEntry>>.Unknown(reason),
    };
}

/// <summary>TPM kind.</summary>
public enum TpmKind
{
    /// <summary>Not determined.</summary>
    Unknown = 0,

    /// <summary>Firmware TPM (Intel PTT / AMD fTPM).</summary>
    Firmware = 1,

    /// <summary>Discrete TPM chip.</summary>
    Discrete = 2,
}

/// <summary>TPM facts. Match on InstanceId (<c>ACPI\MSFT0101</c>), never on the localized friendly name.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record TpmInfo
{
    /// <summary>A TPM device is present.</summary>
    public required bool Present { get; init; }

    /// <summary>Specification version, for example <c>2.0</c>.</summary>
    public string? SpecVersion { get; init; }

    /// <summary>Firmware or discrete.</summary>
    public TpmKind Kind { get; init; }

    /// <summary>Ready for use (admin).</summary>
    public bool? Ready { get; init; }

    /// <summary>Enabled (admin).</summary>
    public bool? Enabled { get; init; }

    /// <summary>Activated (admin).</summary>
    public bool? Activated { get; init; }

    /// <summary>Manufacturer id (for example <c>AMD</c>, <c>INTC</c>, <c>IFX</c>).</summary>
    public string? ManufacturerId { get; init; }

    /// <summary>PnP instance id of the TPM device.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public string? InstanceId { get; init; }
}

/// <summary>VBS state.</summary>
public enum VbsStatus
{
    /// <summary>VBS off.</summary>
    Off = 0,

    /// <summary>Enabled but not running (needs reboot or blocked).</summary>
    Configured = 1,

    /// <summary>Running.</summary>
    Running = 2,
}

/// <summary>VBS / HVCI facts from Win32_DeviceGuard.</summary>
public sealed record VbsInfo
{
    /// <summary>VBS status.</summary>
    public required VbsStatus Status { get; init; }

    /// <summary>HVCI (Memory Integrity) configured.</summary>
    public required bool HvciConfigured { get; init; }

    /// <summary>HVCI running.</summary>
    public required bool HvciRunning { get; init; }

    /// <summary><c>AvailableSecurityProperties</c> (3 = DMA protection, 5 = NX...).</summary>
    public IReadOnlyList<int> AvailableSecurityProperties { get; init; } = [];

    /// <summary><c>RequiredSecurityProperties</c>.</summary>
    public IReadOnlyList<int> RequiredSecurityProperties { get; init; } = [];

    /// <summary><c>SecurityServicesRunning</c> (2 = HVCI).</summary>
    public IReadOnlyList<int> SecurityServicesRunning { get; init; } = [];
}

/// <summary>BCD flags of the current boot entry.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record BcdFlags
{
    /// <summary><c>testsigning</c>.</summary>
    public bool? TestSigning { get; init; }

    /// <summary><c>nointegritychecks</c>.</summary>
    public bool? NoIntegrityChecks { get; init; }

    /// <summary><c>nx</c> policy, for example <c>OptIn</c>.</summary>
    public string? Nx { get; init; }

    /// <summary><c>hypervisorlaunchtype</c>, for example <c>Auto</c> or <c>Off</c>.</summary>
    public string? HypervisorLaunchType { get; init; }

    /// <summary><c>safeboot</c> is set.</summary>
    public bool? SafeBoot { get; init; }

    /// <summary>Kernel debugging enabled.</summary>
    public bool? Debug { get; init; }
}

/// <summary>BitLocker protection status of the system volume.</summary>
public enum BitLockerStatus
{
    /// <summary>Not protected.</summary>
    Off = 0,

    /// <summary>Protected.</summary>
    On = 1,

    /// <summary>Protection suspended.</summary>
    Suspended = 2,

    /// <summary>Encryption or decryption in progress.</summary>
    InProgress = 3,
}

/// <summary>Microsoft Defender state.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DefenderInfo
{
    /// <summary>Real-time protection on.</summary>
    public bool? RealTimeProtection { get; init; }

    /// <summary>Tamper Protection on (registry changes to Defender settings will be blocked).</summary>
    public bool? TamperProtection { get; init; }

    /// <summary>Controlled Folder Access on (can block Riot writes).</summary>
    public bool? ControlledFolderAccess { get; init; }

    /// <summary>Smart App Control state: <c>off</c>, <c>evaluation</c>, <c>on</c>.</summary>
    public string? SmartAppControl { get; init; }

    /// <summary>Defender is disabled by policy (for example dControl / <c>DisableAntiSpyware</c>).</summary>
    public bool? DisabledByPolicy { get; init; }
}

/// <summary>An antivirus product from Security Center (SecurityCenter2).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record AvProduct
{
    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Real-time protection enabled.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Signatures up to date.</summary>
    public bool? UpToDate { get; init; }

    /// <summary>This is Microsoft Defender.</summary>
    public bool IsDefender { get; init; }
}

/// <summary>An Exploit Protection override on a Riot/Vanguard executable.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ExploitOverride
{
    /// <summary>Executable image name, for example <c>vgc.exe</c>.</summary>
    public required string ImageName { get; init; }

    /// <summary><c>MitigationOptions</c> as hex.</summary>
    public string? MitigationOptionsHex { get; init; }

    /// <summary><c>MitigationAuditOptions</c> as hex.</summary>
    public string? MitigationAuditOptionsHex { get; init; }
}

/// <summary>An Image File Execution Options entry (value names only; Debugger targets are not collected).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record IfeoEntry
{
    /// <summary>Executable image name.</summary>
    public required string ImageName { get; init; }

    /// <summary>A <c>Debugger</c> value is set (hijack).</summary>
    public bool HasDebugger { get; init; }

    /// <summary><c>PerfOptions\CpuPriorityClass</c>, when set.</summary>
    public int? CpuPriorityClass { get; init; }

    /// <summary>Value names present under the key.</summary>
    public IReadOnlyList<string> ValueNames { get; init; } = [];
}

/// <summary>Storage (check ids: uefi_gpt, disk_type, disk_dirty, vmd).</summary>
public sealed record StorageInfo
{
    /// <summary>Physical disks.</summary>
    public required Fact<IReadOnlyList<DiskInfo>> Disks { get; init; }

    /// <summary>Partition style of the disk Windows boots from.</summary>
    public required Fact<PartitionStyle> SystemDiskPartitionStyle { get; init; }

    /// <summary>Volumes with drive letters.</summary>
    public required Fact<IReadOnlyList<VolumeInfo>> Volumes { get; init; }

    /// <summary>Storage controllers (Intel VMD/RST, third-party NVMe/AHCI drivers named by "VAN: Incompatible OEM Driver").</summary>
    public required Fact<IReadOnlyList<StorageControllerInfo>> Controllers { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static StorageInfo Unknown(UnknownReason reason) => new()
    {
        Disks = Fact<IReadOnlyList<DiskInfo>>.Unknown(reason),
        SystemDiskPartitionStyle = Fact<PartitionStyle>.Unknown(reason),
        Volumes = Fact<IReadOnlyList<VolumeInfo>>.Unknown(reason),
        Controllers = Fact<IReadOnlyList<StorageControllerInfo>>.Unknown(reason),
    };
}

/// <summary>Disk partition style.</summary>
public enum PartitionStyle
{
    /// <summary>Not initialized.</summary>
    Raw = 0,

    /// <summary>MBR (Secure Boot needs GPT; mbr2gpt is one-way).</summary>
    Mbr = 1,

    /// <summary>GPT.</summary>
    Gpt = 2,
}

/// <summary>Disk media type (MSFT_PhysicalDisk.MediaType).</summary>
public enum DiskMediaType
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,

    /// <summary>Hard disk.</summary>
    Hdd = 1,

    /// <summary>Solid state.</summary>
    Ssd = 2,

    /// <summary>Storage class memory.</summary>
    Scm = 3,
}

/// <summary>Disk bus type.</summary>
public enum DiskBusType
{
    /// <summary>Other or unknown.</summary>
    Other = 0,

    /// <summary>SATA.</summary>
    Sata = 1,

    /// <summary>NVMe.</summary>
    Nvme = 2,

    /// <summary>USB.</summary>
    Usb = 3,

    /// <summary>RAID (VMD/RST).</summary>
    Raid = 4,

    /// <summary>SAS.</summary>
    Sas = 5,

    /// <summary>Virtual disk.</summary>
    Virtual = 6,
}

/// <summary>One physical disk (no serial number).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DiskInfo
{
    /// <summary>Disk number.</summary>
    public required int Number { get; init; }

    /// <summary>Model.</summary>
    public string? Model { get; init; }

    /// <summary>Media type.</summary>
    public required DiskMediaType Media { get; init; }

    /// <summary>Bus type.</summary>
    public required DiskBusType Bus { get; init; }

    /// <summary>Size (GB).</summary>
    public required long SizeGb { get; init; }

    /// <summary>Partition style.</summary>
    public required PartitionStyle Style { get; init; }

    /// <summary>Holds the Windows system volume.</summary>
    public required bool IsSystemDisk { get; init; }
}

/// <summary>One volume with a drive letter.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record VolumeInfo
{
    /// <summary>Drive letter with colon, for example <c>C:</c>.</summary>
    public required string DriveLetter { get; init; }

    /// <summary>File system, for example <c>NTFS</c>.</summary>
    public string? FileSystem { get; init; }

    /// <summary>Size (MB).</summary>
    public required long SizeMb { get; init; }

    /// <summary>Free space (MB). At least 200 MB is required before any apply.</summary>
    public required long FreeMb { get; init; }

    /// <summary>Dirty bit set (<c>FSCTL_IS_VOLUME_DIRTY</c>).</summary>
    public bool? Dirty { get; init; }

    /// <summary>Windows system volume.</summary>
    public required bool IsSystem { get; init; }

    /// <summary>Hosts the Riot Games install.</summary>
    public bool HostsRiotGames { get; init; }
}

/// <summary>A storage controller and its driver.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record StorageControllerInfo
{
    /// <summary>PnP instance id.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public required string InstanceId { get; init; }

    /// <summary>Controller name (driver-provided).</summary>
    public string? Name { get; init; }

    /// <summary>Driver file, for example <c>iastorvd.sys</c>, <c>stornvme.sys</c>, <c>secnvme.sys</c>.</summary>
    public string? DriverFile { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Intel VMD controller.</summary>
    public bool IsVmd { get; init; }

    /// <summary>Controls the boot disk (a protected target, S4).</summary>
    public bool IsBootController { get; init; }
}
