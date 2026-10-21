using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;

namespace VTD.Testing.Fakes;

/// <summary>
/// Hand-made fixture PCs (the source of <c>tests/fixtures/snapshots/*.json</c>). Each is a complete snapshot of a real
/// problem shape from <c>data/catalog.json</c>:
/// <list type="bullet">
/// <item><see cref="RealtekStutter"/>: AM5 desktop on Wi-Fi with an enabled, unused Realtek Gaming 2.5GbE adapter and
/// rt25cx21 reset events (catalog <c>realtek-2-5gbe-nic-stutter</c>, 76 threads). Turkish Windows.</item>
/// <item><see cref="NahimicBlackScreen"/>: MSI laptop with Nahimic running and a VALORANT crash in <c>ProductInfo.dll</c>
/// (catalog <c>nahimic-audio-service-launch-hang</c>, Sep-Oct 2026 wave).</item>
/// <item><see cref="Van9003SecureBootOff"/>: UEFI/GPT desktop with Secure Boot off and VAN 9003 in the Riot Client log,
/// scanned in LIMITED mode, so admin-only facts are Unknown(NeedsAdmin) (catalog <c>secure-boot-not-detected-van9003</c>).</item>
/// </list>
/// Agents derive near-miss variants with <c>with</c> expressions. To regenerate the JSON files run the contract tests
/// with <c>VTD_UPDATE_FIXTURES=1</c>.
/// </summary>
public static class FixtureSnapshots
{
    /// <summary>Snapshot id of <see cref="RealtekStutter"/>.</summary>
    public const string RealtekId = "01JB6R7X3K9M2N4P5Q6R7S8T9V";

    /// <summary>Snapshot id of <see cref="NahimicBlackScreen"/>.</summary>
    public const string NahimicId = "01JB6R7X3K9M2N4P5Q6R7S8T9W";

    /// <summary>Snapshot id of <see cref="Van9003SecureBootOff"/>.</summary>
    public const string Van9003Id = "01JB6R7X3K9M2N4P5Q6R7S8T9X";

    /// <summary>KB version recorded in the fixtures.</summary>
    public const string KbVersion = "2026.10.21.1";

    /// <summary>App version recorded in the fixtures.</summary>
    public const string AppVersion = "0.1.0";

    /// <summary>InstanceId of the unused Realtek adapter in <see cref="RealtekStutter"/> (the fix binds to it).</summary>
    public const string RealtekAdapterInstanceId = "PCI\\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\\01000000684CE00000";

    /// <summary>InstanceId of the Wi-Fi adapter in <see cref="RealtekStutter"/> (default route; protected).</summary>
    public const string WifiAdapterInstanceId = "PCI\\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\\D8F2CAFFFF4F8C4600";

    /// <summary>A healthy, fully elevated AM5 desktop on Windows 11 25H2: the base every fixture modifies.</summary>
    /// <param name="snapshotId">Snapshot id.</param>
    /// <param name="createdUtc">Scan time.</param>
    public static ScanSnapshot HealthyDesktop(string snapshotId, DateTimeOffset createdUtc)
    {
        var bootTime = createdUtc.AddHours(-3);
        return new ScanSnapshot
        {
            SchemaVersion = ScanSnapshot.CurrentSchemaVersion,
            Meta = new SnapshotMeta
            {
                SnapshotId = snapshotId,
                AppVersion = AppVersion,
                KbVersion = KbVersion,
                CreatedUtc = createdUtc,
                DurationMs = 31_480,
                Mode = ScanMode.Full,
                Privilege = PrivilegeMode.Elevated,
                Origin = SnapshotOrigin.Fixture,
                Boot = K(new BootIdentity { BootId = 412, LastBootUpTimeUtc = bootTime, Kind = BootKind.Cold }, "os/registry:PrefetchParameters\\BootId"),
                OsUiCulture = "en-US",
                AppLanguage = Languages.English,
                Redaction = new RedactionState { Level = RedactionLevel.Local, RulesVersion = 1, AppliedRules = ["user.name", "host.name", "path.profile"] },
                MachineKey = "mk1:7f3c2a9e41d05b6c",
            },
            Runs = StandardRuns(),
            Os = new OsInfo
            {
                ProductName = K("Windows 10 Pro", "os/registry:CurrentVersion"),
                Build = K(26200, "os/registry:CurrentVersion"),
                Ubr = K(6899, "os/registry:CurrentVersion"),
                DisplayVersion = K("25H2", "os/registry:CurrentVersion"),
                EditionId = K("Professional", "os/registry:CurrentVersion"),
                InsiderBranch = Fact<string>.NotApplicable("not an insider build", "os/registry:WindowsSelfHost"),
                InstallDateUtc = K(new DateTimeOffset(2025, 11, 2, 14, 20, 0, TimeSpan.Zero), "os/registry:CurrentVersion"),
                IsVirtualMachine = K(false, "os/wmi:Win32_ComputerSystem"),
                PendingReboot = K(new PendingRebootInfo { ComponentServicing = false, WindowsUpdate = false, PendingFileRename = false }, "os/registry"),
                WindowsUpdateInstalling = K(false, "os/wua"),
                FastStartupEnabled = K(true, "os/registry:HiberbootEnabled"),
                HibernationEnabled = K(true, "os/registry:HibernateEnabled"),
                Clock = K(new ClockInfo { SkewSeconds = 1, ReferenceSource = "https-date", TimeZoneId = "Turkey Standard Time", AutoTimeSync = true }, "os/clock"),
                Hotfixes = K<IReadOnlyList<HotfixInfo>>([new HotfixInfo { Id = "KB5066835", InstalledOn = new DateOnly(2026, 10, 15) }], "os/wua:history"),
                ModifiedWindowsMarkers = K<IReadOnlyList<string>>([], "os/markers"),
                EnvironmentIssues = K<IReadOnlyList<EnvVarFinding>>([], "os/env"),
                TaskSchedulerAvailable = K(true, "os/scm:Schedule"),
                InteractiveUserIsAdmin = K(true, "os/token"),
            },
            Hardware = new HardwareInfo
            {
                Cpu = K(new CpuInfo
                {
                    Vendor = CpuVendor.Amd,
                    Model = "AMD Ryzen 7 9800X3D 8-Core Processor",
                    Architecture = "x64",
                    Cores = 8,
                    LogicalProcessors = 16,
                    Hybrid = false,
                    VirtualizationSupported = true,
                }, "hardware/cpuid"),
                Memory = K(new MemoryInfo
                {
                    TotalMb = 32_768,
                    AvailableMb = 21_400,
                    StandbyMb = 6_100,
                    Modules =
                    [
                        new MemoryModule { CapacityMb = 16_384, RatedMts = 6000, ConfiguredMts = 6000, Slot = "DIMM_A2", Manufacturer = "G Skill Intl" },
                        new MemoryModule { CapacityMb = 16_384, RatedMts = 6000, ConfiguredMts = 6000, Slot = "DIMM_B2", Manufacturer = "G Skill Intl" },
                    ],
                }, "hardware/wmi:Win32_PhysicalMemory"),
                Gpus = K<IReadOnlyList<GpuInfo>>(
                [
                    new GpuInfo
                    {
                        InstanceId = "PCI\\VEN_10DE&DEV_2783&SUBSYS_51831462&REV_A1\\4&2E3F5C1&0&0019",
                        Name = "NVIDIA GeForce RTX 4070 SUPER",
                        Vendor = GpuVendor.Nvidia,
                        PciVendorId = "10DE",
                        PciDeviceId = "2783",
                        IsIntegrated = false,
                        Enabled = true,
                        DriverVersion = "32.0.15.8157",
                        DriverDate = new DateOnly(2026, 9, 24),
                        DriverProvider = "NVIDIA",
                        DedicatedVideoMb = 12_282,
                    },
                    new GpuInfo
                    {
                        InstanceId = "PCI\\VEN_1002&DEV_13C0&SUBSYS_88771043&REV_C9\\4&1A2B3C4D&0&0041",
                        Name = "AMD Radeon(TM) Graphics",
                        Vendor = GpuVendor.Amd,
                        PciVendorId = "1002",
                        PciDeviceId = "13C0",
                        IsIntegrated = true,
                        Enabled = true,
                        DriverVersion = "32.0.21025.1024",
                        DriverDate = new DateOnly(2026, 8, 12),
                        DriverProvider = "Advanced Micro Devices, Inc.",
                        DedicatedVideoMb = 512,
                    },
                ], "hardware/dxgi+pnp"),
                Board = K(new BoardInfo
                {
                    BoardManufacturer = "ASUSTeK COMPUTER INC.",
                    BoardProduct = "ROG STRIX B650E-F GAMING WIFI",
                    SystemManufacturer = "ASUS",
                    SystemProduct = "System Product Name",
                    BiosVendor = "American Megatrends Inc.",
                    BiosVersion = "3201",
                    BiosReleaseDate = new DateOnly(2025, 3, 7),
                    IsLaptop = false,
                }, "hardware/wmi:Win32_BaseBoard+Win32_BIOS"),
            },
            Firmware = new FirmwareInfo
            {
                Type = K(FirmwareType.Uefi, "firmware/GetFirmwareType"),
                BootToFirmwareSupported = K(true, "firmware/OsIndicationsSupported"),
                OtherOsBootEntries = K(0, "firmware/bcd"),
            },
            Security = new SecurityState
            {
                SecureBootEnabled = K(true, "security/registry:SecureBoot\\State"),
                SecureBootSetupMode = K(false, "security/firmware:SetupMode"),
                SecureBootDbHasCa2023 = K(true, "security/firmware:db"),
                Tpm = K(new TpmInfo { Present = true, SpecVersion = "2.0", Kind = TpmKind.Firmware, Ready = true, Enabled = true, Activated = true, ManufacturerId = "AMD", InstanceId = "ACPI\\MSFT0101\\1" }, "security/tbs+wmi:Win32_Tpm"),
                Vbs = K(new VbsInfo { Status = VbsStatus.Running, HvciConfigured = true, HvciRunning = true, AvailableSecurityProperties = [1, 2, 3, 5, 6, 7], RequiredSecurityProperties = [], SecurityServicesRunning = [2] }, "security/wmi:Win32_DeviceGuard"),
                HvciIncompatibleDrivers = K<IReadOnlyList<string>>([], "security/codeintegrity"),
                VirtualizationFirmwareEnabled = K(true, "security/wmi:Win32_Processor"),
                HypervisorPresent = K(true, "security/cpuid"),
                KernelDmaProtection = K(true, "security/wmi:Win32_DeviceGuard"),
                Bcd = K(new BcdFlags { TestSigning = false, NoIntegrityChecks = false, Nx = "OptIn", HypervisorLaunchType = "Auto", SafeBoot = false, Debug = false }, "security/wmi:BcdStore"),
                SystemVolumeBitLocker = K(BitLockerStatus.Off, "security/shell:BitLockerProtection"),
                Defender = K(new DefenderInfo { RealTimeProtection = true, TamperProtection = true, ControlledFolderAccess = false, SmartAppControl = "off", DisabledByPolicy = false }, "security/wmi:MSFT_MpComputerStatus"),
                AntivirusProducts = K<IReadOnlyList<AvProduct>>([new AvProduct { Name = "Windows Defender", Enabled = true, UpToDate = true, IsDefender = true }], "security/wmi:SecurityCenter2"),
                ExploitOverrides = K<IReadOnlyList<ExploitOverride>>([], "security/registry:IFEO"),
                Ifeo = K<IReadOnlyList<IfeoEntry>>([], "security/registry:IFEO"),
            },
            Storage = new StorageInfo
            {
                Disks = K<IReadOnlyList<DiskInfo>>(
                [
                    new DiskInfo { Number = 0, Model = "Samsung SSD 990 PRO 2TB", Media = DiskMediaType.Ssd, Bus = DiskBusType.Nvme, SizeGb = 1863, Style = PartitionStyle.Gpt, IsSystemDisk = true },
                ], "storage/wmi:MSFT_PhysicalDisk"),
                SystemDiskPartitionStyle = K(PartitionStyle.Gpt, "storage/wmi:MSFT_Disk"),
                Volumes = K<IReadOnlyList<VolumeInfo>>(
                [
                    new VolumeInfo { DriveLetter = "C:", FileSystem = "NTFS", SizeMb = 1_906_000, FreeMb = 1_240_000, Dirty = false, IsSystem = true, HostsRiotGames = true },
                ], "storage/volumes"),
                Controllers = K<IReadOnlyList<StorageControllerInfo>>(
                [
                    new StorageControllerInfo { InstanceId = "PCI\\VEN_144D&DEV_A80C&SUBSYS_A801144D&REV_00\\4&3A1B2C3D&0&0009", Name = "Standard NVM Express Controller", DriverFile = "stornvme.sys", DriverVersion = "10.0.26100.6899", IsVmd = false, IsBootController = true },
                ], "storage/pnp"),
            },
            Devices = K<IReadOnlyList<DeviceInfo>>(
            [
                new DeviceInfo { InstanceId = "PCI\\VEN_10DE&DEV_2783&SUBSYS_51831462&REV_A1\\4&2E3F5C1&0&0019", ClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}", ClassName = "Display", Enumerator = "PCI", FriendlyName = "NVIDIA GeForce RTX 4070 SUPER", Present = true, Enabled = true, ProblemCode = 0, Service = "nvlddmkm", DriverVersion = "32.0.15.8157", DriverDate = new DateOnly(2026, 9, 24), DriverProvider = "NVIDIA" },
                new DeviceInfo { InstanceId = WifiAdapterInstanceId, ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}", ClassName = "Net", Enumerator = "PCI", FriendlyName = "Intel(R) Wi-Fi 6E AX210 160MHz", Present = true, Enabled = true, ProblemCode = 0, Service = "Netwtw10", DriverVersion = "23.110.0.5", DriverDate = new DateOnly(2025, 2, 10), DriverProvider = "Intel" },
            ], "devices/cfgmgr32"),
            Drivers = K<IReadOnlyList<LoadedDriver>>(
            [
                new LoadedDriver { Name = "nvlddmkm.sys", Path = "\\SystemRoot\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_0a1b2c3d4e5f6a7b\\nvlddmkm.sys", Version = "32.0.15.8157", Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows Hardware Compatibility Publisher", IsMicrosoft = true } },
                new LoadedDriver { Name = "vgk.sys", Path = "\\??\\C:\\Program Files\\Riot Vanguard\\vgk.sys", Version = "1.17.9.5", Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "Riot Games, Inc.", IsMicrosoft = false } },
                new LoadedDriver { Name = "Netwtw10.sys", Path = "\\SystemRoot\\System32\\drivers\\Netwtw10.sys", Version = "23.110.0.5", Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows Hardware Compatibility Publisher", IsMicrosoft = true } },
            ], "drivers/EnumDeviceDrivers"),
            Services = K<IReadOnlyList<ServiceInfo>>(
            [
                VgcService(ServiceStartType.Manual, ServiceState.Stopped),
                VgkService(),
                new ServiceInfo { Name = "TBS", DisplayName = "TPM Base Services", Kind = ServiceKind.Win32, StartType = ServiceStartType.Manual, State = ServiceState.Running, ImagePath = "C:\\Windows\\System32\\svchost.exe" },
                new ServiceInfo { Name = "Winmgmt", DisplayName = "Windows Management Instrumentation", Kind = ServiceKind.Win32, StartType = ServiceStartType.Automatic, State = ServiceState.Running, ImagePath = "C:\\Windows\\System32\\svchost.exe" },
            ], "software/scm"),
            Processes = K<IReadOnlyList<ProcessInfo>>(
            [
                new ProcessInfo { Name = "explorer.exe", ProcessId = 7412, ParentProcessId = 7380, ThreadCount = 96 },
                new ProcessInfo { Name = "Discord.exe", ProcessId = 11208, ParentProcessId = 7412, ThreadCount = 54 },
                new ProcessInfo { Name = "RiotClientServices.exe", ProcessId = 13020, ParentProcessId = 7412, ThreadCount = 61, IsProtectedRiotProcess = true },
            ], "software/toolhelp"),
            Apps = K<IReadOnlyList<InstalledApp>>(
            [
                new InstalledApp { DisplayName = "Riot Vanguard", Publisher = "Riot Games, Inc.", Version = "1.17.9.5", Hive = AppHive.Hklm, KeyName = "Riot Vanguard", HasQuietUninstall = false },
                new InstalledApp { DisplayName = "Discord", Publisher = "Discord Inc.", Version = "1.0.9210", Hive = AppHive.Hkcu, KeyName = "Discord", InstallLocation = "C:\\Users\\<user>\\AppData\\Local\\Discord" },
                new InstalledApp { DisplayName = "NVIDIA Graphics Driver 581.57", Publisher = "NVIDIA Corporation", Version = "581.57", Hive = AppHive.Hklm, KeyName = "{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver" },
            ], "software/uninstall"),
            Startup = K<IReadOnlyList<StartupItem>>(
            [
                new StartupItem { Name = "Discord", Location = StartupLocation.HkcuRun, ImageName = "Update.exe", TargetPath = "C:\\Users\\<user>\\AppData\\Local\\Discord\\Update.exe", Enabled = true },
            ], "software/startup"),
            OptionalFeatures = K<IReadOnlyList<string>>(["Microsoft-Windows-Subsystem-Linux"], "software/features"),
            Network = new NetworkState
            {
                Adapters = K<IReadOnlyList<NetAdapter>>([WifiAdapter()], "network/iphlpapi+pnp"),
                Connectivity = K(new ConnectivityInfo { Ipv4Internet = true, Ipv6Internet = false, LocalOnly = false }, "network/nlm"),
                Proxy = K(new ProxyInfo { WinHttpProxy = false, UserProxyEnabled = false, AutoConfigUrl = false }, "network/winhttp+wininet"),
                Ipv6 = K(new Ipv6Info { DisabledComponents = 0, TeredoEnabled = false }, "network/registry:Tcpip6"),
                WinsockProviders = K<IReadOnlyList<string>>([], "network/registry:WinSock2"),
                WinsockNamespaceProviders = K<IReadOnlyList<string>>([], "network/registry:WinSock2-NameSpace"),
                RiotFirewallRules = K<IReadOnlyList<FirewallRuleInfo>>([], "network/INetFwPolicy2"),
                Firewall = K(new FirewallProfiles { DomainOn = true, PrivateOn = true, PublicOn = true }, "network/INetFwPolicy2"),
                Wlan = K(new WlanInfo { Connected = true, SignalQualityPercent = 88, RssiDbm = -52, Band = WifiBand.Ghz5, PhyType = "802.11ax", LinkSpeedMbps = 1201, Ssid = "HomeNet-5G" }, "network/wlanapi"),
                Probes = K<IReadOnlyList<PingProbe>>(
                [
                    new PingProbe { Target = "gateway", Kind = ProbeTargetKind.Gateway, Sent = 10, Received = 10, RttAvgMs = 2.1, RttP95Ms = 3.4, JitterMs = 0.6 },
                    new PingProbe { Target = "eu-central", Kind = ProbeTargetKind.Region, Sent = 10, Received = 10, RttAvgMs = 41.8, RttP95Ms = 44.0, JitterMs = 1.2 },
                ], "network/icmp"),
            },
            Power = new PowerState
            {
                ActiveScheme = K(new PowerScheme { Guid = "381b4222-f694-41f0-9685-ff5bb260df2e", Kind = PowerSchemeKind.Balanced, Name = "Balanced" }, "power/PowerGetActiveScheme"),
                Mode = K(PowerMode.BestPerformance, "power/overlay"),
                UsbSelectiveSuspendAc = K(true, "power/PowerReadACValueIndex"),
                UsbSelectiveSuspendDc = K(true, "power/PowerReadDCValueIndex"),
                ProcessorMinPercentAc = K(5, "power/PowerReadACValueIndex"),
                ProcessorMaxPercentAc = K(100, "power/PowerReadACValueIndex"),
                Battery = K(new BatteryInfo { Present = false, OnAc = true }, "power/GetSystemPowerStatus"),
            },
            Display = new DisplayState
            {
                Monitors = K<IReadOnlyList<MonitorInfo>>([new MonitorInfo { Name = "VG27AQ3A", Primary = true, Width = 2560, Height = 1440, RefreshHz = 180, HdrEnabled = false, Internal = false, GpuInstanceId = "PCI\\VEN_10DE&DEV_2783&SUBSYS_51831462&REV_A1\\4&2E3F5C1&0&0019" }], "display/QueryDisplayConfig"),
                HagsEnabled = K(true, "display/registry:HwSchMode"),
                MpoDisabled = K(false, "display/registry:OverlayTestMode"),
                GameModeEnabled = K(true, "display/registry:AutoGameModeEnabled"),
                GameDvrEnabled = K(false, "display/registry:GameDVR_Enabled"),
                WindowedOptimizations = K(true, "display/registry:DirectXUserGlobalSettings"),
                GpuPreferences = K<IReadOnlyList<GpuPreference>>([], "display/registry:UserGpuPreferences"),
            },
            Audio = new AudioState
            {
                Endpoints = K<IReadOnlyList<AudioEndpoint>>(
                [
                    new AudioEndpoint { Id = "{0.0.0.00000000}.{6a1f3b2e-9c47-4d1a-8f20-3b5e7c9d1a42}", Name = "Speakers (Realtek(R) Audio)", Flow = AudioFlow.Render, State = AudioEndpointState.Active, FormFactor = AudioFormFactor.Speakers, IsDefault = true, IsDefaultCommunications = true },
                ], "audio/mmdevice"),
            },
            Input = new InputState
            {
                Mouse = K(new MouseSettings { EnhancePointerPrecision = false, Speed = 10 }, "input/SystemParametersInfo"),
            },
            Riot = new RiotState
            {
                Client = K(new RiotInstall { Installed = true, Path = "C:\\Riot Games\\Riot Client", Version = "113.0.2.2271", Drive = "C:", OnSystemDrive = true, PathHasNonAscii = false }, "riot/RiotClientInstalls.json"),
                Valorant = K(new RiotInstall { Installed = true, Path = "C:\\Riot Games\\VALORANT\\live", Version = "release-11.08-shipping-14-3988147", Drive = "C:", OnSystemDrive = true, PathHasNonAscii = false }, "riot/product_settings"),
                Vanguard = K(new VanguardInfo
                {
                    Installed = true,
                    Vgc = VgcService(ServiceStartType.Manual, ServiceState.Stopped),
                    Vgk = VgkService(),
                    InstallPath = "C:\\Program Files\\Riot Vanguard",
                    Version = "1.17.9.5",
                    Files =
                    [
                        new RiotFileInfo { Name = "vgc.exe", Location = "vanguard", Exists = true, SizeBytes = 18_431_256, ModifiedUtc = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), Version = "1.17.9.5" },
                        new RiotFileInfo { Name = "vgk.sys", Location = "vanguard", Exists = true, SizeBytes = 25_102_920, ModifiedUtc = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), Version = "1.17.9.5" },
                        new RiotFileInfo { Name = "vgkbootstatus.dat", Location = "vanguard", Exists = false },
                        new RiotFileInfo { Name = "392667600.dat", Location = "system32", Exists = false },
                    ],
                    Logs = new VgcLogDigest { FileCount = 6, NewestUtc = createdUtc.AddHours(-2), OldestUtc = createdUtc.AddDays(-6), CountLastHour = 0, CountLast24Hours = 2, TotalBytes = 61_440 },
                }, "riot/vanguard"),
                CompatLayers = K<IReadOnlyList<CompatLayerEntry>>([], "riot/registry:AppCompatFlags\\Layers"),
                GameConfig = K(new GameConfigDigest
                {
                    AccountConfigCount = 1,
                    Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["FullscreenMode"] = "0", ["ResolutionSizeX"] = "2560", ["ResolutionSizeY"] = "1440", ["FrameRateLimit"] = "0" },
                    CorruptMarkers = false,
                }, "riot/GameUserSettings.ini"),
                ShooterLog = K(new ShooterLogDigest
                {
                    LastWriteUtc = createdUtc.AddHours(-1),
                    BackupLogCount = 10,
                    GameVersion = "release-11.08-shipping-14-3988147",
                    Hardware = new LogInitHardware { Os = "Windows 11 (25H2) [10.0.26200.6899]", Cpu = "AMD Ryzen 7 9800X3D 8-Core Processor", Gpu = "NVIDIA GeForce RTX 4070 SUPER", Disk = "SSD" },
                }, "riot/ShooterGame.log"),
                ClientLogs = K(new RiotClientLogDigest { FileCount = 8, NewestUtc = createdUtc.AddHours(-1) }, "riot/RiotClientLogs"),
                Crashes = K<IReadOnlyList<CrashReportMeta>>([], "riot/Crashes"),
                DetectedCodes = K<IReadOnlyList<DetectedCode>>([], "riot/logs"),
                GameRunning = K(false, "riot/toolhelp"),
                ClientRunning = K(true, "riot/toolhelp"),
            },
            Events = new EventDigest
            {
                WindowStartUtc = createdUtc.AddDays(-14),
                WindowEndUtc = createdUtc,
                Hits = K<IReadOnlyList<EventHit>>([], "events/EventLogReader"),
                AppCrashes = K<IReadOnlyList<AppCrash>>([], "events/EventLogReader:Application"),
                BugChecks = K<IReadOnlyList<BugCheck>>([], "events/EventLogReader:System"),
                Minidumps = K<IReadOnlyList<MinidumpMeta>>([], "events/minidump"),
            },
            Pack = [],
        };
    }

    /// <summary>
    /// Realtek Gaming 2.5GbE enabled but unused (player is on Wi-Fi), 14 rt25cx21 resets in 14 days, AM5 board,
    /// Turkish Windows (fact names never depend on it).
    /// </summary>
    public static ScanSnapshot RealtekStutter()
    {
        var created = new DateTimeOffset(2026, 10, 21, 18, 2, 11, TimeSpan.Zero);
        var s = HealthyDesktop(RealtekId, created);
        var realtekDevice = new DeviceInfo
        {
            InstanceId = RealtekAdapterInstanceId,
            ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}",
            ClassName = "Net",
            Enumerator = "PCI",
            FriendlyName = "Realtek Gaming 2.5GbE Family Controller",
            Present = true,
            Enabled = true,
            ProblemCode = 0,
            Service = "rt25cx21",
            DriverVersion = "10.68.815.2023",
            DriverDate = new DateOnly(2023, 8, 15),
            DriverProvider = "Realtek",
            InfName = "oem37.inf",
        };
        var realtekAdapter = new NetAdapter
        {
            InstanceId = RealtekAdapterInstanceId,
            InterfaceGuid = "{8B2E1D4C-3F5A-4E6B-9C7D-1A2B3C4D5E6F}",
            Description = "Realtek Gaming 2.5GbE Family Controller",
            Alias = "Ethernet",
            Kind = AdapterKind.Ethernet,
            Physical = true,
            Enabled = true,
            Status = AdapterOperStatus.LowerLayerDown,
            LinkSpeedBps = 0,
            IsDefaultRoute = false,
            HasInternet = false,
            Ipv4Metric = 25,
            AutomaticMetric = true,
            Ipv6Bound = true,
            DriverFile = "rt25cx21x64.sys",
            DriverVersion = "10.68.815.2023",
            DriverDate = new DateOnly(2023, 8, 15),
            DriverProvider = "Realtek",
            AdvancedProperties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["*EEE"] = "1",
                ["*GreenEthernet"] = "1",
                ["AdvancedEEE"] = "1",
                ["*InterruptModeration"] = "1",
                ["EnableGreenEthernet"] = "1",
            },
            AllowPowerOff = true,
            Mac = "04:42:1A:5C:E0:7F",
        };

        return s with
        {
            Meta = s.Meta with { OsUiCulture = "tr-TR", AppLanguage = Languages.Turkish },
            Devices = K<IReadOnlyList<DeviceInfo>>([.. s.Devices.Value, realtekDevice], "devices/cfgmgr32"),
            Drivers = K<IReadOnlyList<LoadedDriver>>(
            [
                .. s.Drivers.Value,
                new LoadedDriver { Name = "rt25cx21x64.sys", Path = "\\SystemRoot\\System32\\drivers\\rt25cx21x64.sys", Version = "10.68.815.2023", Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "Microsoft Windows Hardware Compatibility Publisher", IsMicrosoft = true } },
            ], "drivers/EnumDeviceDrivers"),
            Network = s.Network with
            {
                Adapters = K<IReadOnlyList<NetAdapter>>([realtekAdapter, WifiAdapter()], "network/iphlpapi+pnp"),
            },
            Events = s.Events with
            {
                Hits = K<IReadOnlyList<EventHit>>(
                [
                    new EventHit
                    {
                        QueryId = "realtek_nic_errors",
                        Log = "System",
                        Provider = "rt25cx21",
                        EventId = 5,
                        Count = 14,
                        FirstUtc = created.AddDays(-13),
                        LastUtc = created.AddHours(-2),
                        Samples = [new Dictionary<string, string>(StringComparer.Ordinal) { ["level"] = "error", ["adapter"] = "Realtek Gaming 2.5GbE Family Controller" }],
                    },
                ], "events/EventLogReader:System"),
            },
            Pack =
            [
                new PackFactResult
                {
                    CollectorId = "realtek_nic_errors",
                    Data = K(new PackFactData
                    {
                        Count = 14,
                        FirstUtc = created.AddDays(-13),
                        LastUtc = created.AddHours(-2),
                        Items =
                        [
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["utc"] = "2026-10-21T16:01:40Z", ["provider"] = "rt25cx21", ["eventId"] = "5", ["level"] = "2" },
                        ],
                        Truncated = true,
                    }, "pack/eventLog"),
                },
            ],
        };
    }

    /// <summary>
    /// MSI laptop with Nahimic (service and process running, A-Volute app installed, Nahimic APO on the speakers) and
    /// two recent VALORANT crashes with faulting module <c>ProductInfo.dll</c> 0xc0000005. Black screen at launch.
    /// </summary>
    public static ScanSnapshot NahimicBlackScreen()
    {
        var created = new DateTimeOffset(2026, 10, 18, 20, 15, 0, TimeSpan.Zero);
        var s = HealthyDesktop(NahimicId, created);
        var nahimicService = new ServiceInfo
        {
            Name = "NahimicService",
            DisplayName = "Nahimic service",
            Kind = ServiceKind.Win32,
            StartType = ServiceStartType.Automatic,
            State = ServiceState.Running,
            ImagePath = "C:\\Windows\\System32\\NahimicSvc64.exe",
            Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "A-Volute", IsMicrosoft = false },
        };

        return s with
        {
            Hardware = s.Hardware with
            {
                Cpu = K(new CpuInfo { Vendor = CpuVendor.Intel, Model = "13th Gen Intel(R) Core(TM) i7-13700H", Architecture = "x64", Cores = 14, LogicalProcessors = 20, Hybrid = true, PerformanceCores = 6, EfficiencyCores = 8, VirtualizationSupported = true }, "hardware/cpuid"),
                Gpus = K<IReadOnlyList<GpuInfo>>(
                [
                    new GpuInfo { InstanceId = "PCI\\VEN_10DE&DEV_28E0&SUBSYS_13A51462&REV_A1\\4&1F2E3D4C&0&0008", Name = "NVIDIA GeForce RTX 4060 Laptop GPU", Vendor = GpuVendor.Nvidia, PciVendorId = "10DE", PciDeviceId = "28E0", IsIntegrated = false, Enabled = true, DriverVersion = "32.0.15.8157", DriverDate = new DateOnly(2026, 9, 24), DriverProvider = "NVIDIA", DedicatedVideoMb = 8_188 },
                    new GpuInfo { InstanceId = "PCI\\VEN_8086&DEV_A7A0&SUBSYS_13A51462&REV_04\\3&11583659&0&10", Name = "Intel(R) Iris(R) Xe Graphics", Vendor = GpuVendor.Intel, PciVendorId = "8086", PciDeviceId = "A7A0", IsIntegrated = true, Enabled = true, DriverVersion = "32.0.101.6913", DriverDate = new DateOnly(2026, 6, 3), DriverProvider = "Intel Corporation", DedicatedVideoMb = 128 },
                ], "hardware/dxgi+pnp"),
                Board = K(new BoardInfo { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardProduct = "MS-15M2", SystemManufacturer = "Micro-Star International Co., Ltd.", SystemProduct = "Katana 15 B13VFK", BiosVendor = "American Megatrends International, LLC.", BiosVersion = "E15M2IMS.31A", BiosReleaseDate = new DateOnly(2025, 1, 14), IsLaptop = true }, "hardware/wmi:Win32_BaseBoard+Win32_BIOS"),
            },
            Services = K<IReadOnlyList<ServiceInfo>>([.. s.Services.Value, nahimicService], "software/scm"),
            Processes = K<IReadOnlyList<ProcessInfo>>(
            [
                .. s.Processes.Value,
                new ProcessInfo { Name = "NahimicSvc64.exe", ProcessId = 4120, ParentProcessId = 1032, ThreadCount = 12 },
                new ProcessInfo { Name = "NahimicSvc32.exe", ProcessId = 9384, ParentProcessId = 4120, ThreadCount = 6 },
            ], "software/toolhelp"),
            Apps = K<IReadOnlyList<InstalledApp>>(
            [
                .. s.Apps.Value,
                new InstalledApp { DisplayName = "Nahimic", Publisher = "A-Volute", Version = "1.10.6.0", Hive = AppHive.Hklm, KeyName = "{2F1A6B3C-7E8D-4A9B-B0C1-D2E3F4A5B6C7}", SystemComponent = false },
            ], "software/uninstall"),
            Audio = new AudioState
            {
                Endpoints = K<IReadOnlyList<AudioEndpoint>>(
                [
                    new AudioEndpoint { Id = "{0.0.0.00000000}.{9e2b4c1d-5a7f-4b3e-8d6c-2f1a0b9c8d7e}", Name = "Speakers (Realtek(R) Audio)", Flow = AudioFlow.Render, State = AudioEndpointState.Active, FormFactor = AudioFormFactor.Speakers, IsDefault = true, IsDefaultCommunications = true, ApoClsids = ["{0A8B5F2C-1D3E-4F6A-9B7C-8D9E0F1A2B3C}"] },
                ], "audio/mmdevice"),
            },
            Power = s.Power with { Battery = K(new BatteryInfo { Present = true, OnAc = true, ChargePercent = 94 }, "power/GetSystemPowerStatus") },
            Riot = s.Riot with
            {
                ShooterLog = K(new ShooterLogDigest
                {
                    LastWriteUtc = created.AddMinutes(-20),
                    BackupLogCount = 10,
                    GameVersion = "release-11.08-shipping-14-3988147",
                    Hardware = new LogInitHardware { Os = "Windows 11 (25H2) [10.0.26200.6899]", Cpu = "13th Gen Intel(R) Core(TM) i7-13700H", Gpu = "NVIDIA GeForce RTX 4060 Laptop GPU", Disk = "SSD" },
                    FatalErrorCount = 2,
                    PatternCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["log.fatal_error"] = 2 },
                }, "riot/ShooterGame.log"),
                Crashes = K<IReadOnlyList<CrashReportMeta>>(
                [
                    new CrashReportMeta { FolderName = "UECC-Windows-4F1D2C3B4A5E6F708192A3B4C5D6E7F8_0000", CreatedUtc = created.AddMinutes(-21), CrashType = "Crash", ErrorPattern = "crash.access_violation", InstallDrive = "C:" },
                ], "riot/Crashes"),
            },
            Events = s.Events with
            {
                AppCrashes = K<IReadOnlyList<AppCrash>>(
                [
                    new AppCrash { Exe = "VALORANT-Win64-Shipping.exe", ExeVersion = "11.8.0.3988147", Module = "ProductInfo.dll", ModuleVersion = "1.10.6.0", ExceptionCode = "0xc0000005", Kind = AppCrashKind.Crash, Utc = created.AddMinutes(-21) },
                    new AppCrash { Exe = "VALORANT-Win64-Shipping.exe", ExeVersion = "11.8.0.3988147", Module = "ProductInfo.dll", ModuleVersion = "1.10.6.0", ExceptionCode = "0xc0000005", Kind = AppCrashKind.Crash, Utc = created.AddDays(-1) },
                ], "events/EventLogReader:Application"),
            },
        };
    }

    /// <summary>
    /// VAN 9003: UEFI + GPT, Secure Boot off (registry read, works without admin), VAN 9003 in the Riot Client log.
    /// Scanned in LIMITED mode: Secure Boot setup mode, the db, BCD, TPM readiness and Defender details are
    /// Unknown(NeedsAdmin), and the report must say "could not check" for them instead of clearing them.
    /// </summary>
    public static ScanSnapshot Van9003SecureBootOff()
    {
        var created = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);
        var s = HealthyDesktop(Van9003Id, created);
        const string NeedsAdmin = "not elevated";

        return s with
        {
            Meta = s.Meta with { Privilege = PrivilegeMode.Limited, Redaction = s.Meta.Redaction with { Level = RedactionLevel.Local } },
            Runs =
            [
                new CollectorRun { CollectorId = "os", Status = CollectorStatus.Ok, ElapsedMs = 410 },
                new CollectorRun { CollectorId = "hardware", Status = CollectorStatus.Ok, ElapsedMs = 2_210 },
                new CollectorRun { CollectorId = "firmware", Status = CollectorStatus.Ok, ElapsedMs = 35 },
                new CollectorRun { CollectorId = "security", Status = CollectorStatus.Partial, ElapsedMs = 1_540, IssueCodes = ["needs_admin:firmware_vars", "needs_admin:Win32_Tpm", "needs_admin:BcdStore", "needs_admin:MSFT_MpComputerStatus"] },
                new CollectorRun { CollectorId = "storage", Status = CollectorStatus.Ok, ElapsedMs = 1_020 },
                new CollectorRun { CollectorId = "devices", Status = CollectorStatus.Ok, ElapsedMs = 880 },
                new CollectorRun { CollectorId = "software", Status = CollectorStatus.Ok, ElapsedMs = 3_300 },
                new CollectorRun { CollectorId = "network", Status = CollectorStatus.Ok, ElapsedMs = 5_900 },
                new CollectorRun { CollectorId = "power", Status = CollectorStatus.Ok, ElapsedMs = 60 },
                new CollectorRun { CollectorId = "display", Status = CollectorStatus.Ok, ElapsedMs = 140 },
                new CollectorRun { CollectorId = "audio", Status = CollectorStatus.Ok, ElapsedMs = 230 },
                new CollectorRun { CollectorId = "input", Status = CollectorStatus.Ok, ElapsedMs = 5 },
                new CollectorRun { CollectorId = "riot", Status = CollectorStatus.Ok, ElapsedMs = 1_800 },
                new CollectorRun { CollectorId = "events", Status = CollectorStatus.Ok, ElapsedMs = 6_400 },
                new CollectorRun { CollectorId = "pack", Status = CollectorStatus.Ok, ElapsedMs = 20 },
            ],
            Os = s.Os with
            {
                Build = K(26100, "os/registry:CurrentVersion"),
                Ubr = K(6899, "os/registry:CurrentVersion"),
                DisplayVersion = K("24H2", "os/registry:CurrentVersion"),
                EditionId = K("Core", "os/registry:CurrentVersion"),
            },
            Hardware = s.Hardware with
            {
                Cpu = K(new CpuInfo { Vendor = CpuVendor.Intel, Model = "12th Gen Intel(R) Core(TM) i5-12400F", Architecture = "x64", Cores = 6, LogicalProcessors = 12, Hybrid = false, VirtualizationSupported = true }, "hardware/cpuid"),
                Gpus = K<IReadOnlyList<GpuInfo>>(
                [
                    new GpuInfo { InstanceId = "PCI\\VEN_10DE&DEV_2504&SUBSYS_39761462&REV_A1\\4&2A3B4C5D&0&0008", Name = "NVIDIA GeForce RTX 3060", Vendor = GpuVendor.Nvidia, PciVendorId = "10DE", PciDeviceId = "2504", IsIntegrated = false, Enabled = true, DriverVersion = "32.0.15.8157", DriverDate = new DateOnly(2026, 9, 24), DriverProvider = "NVIDIA", DedicatedVideoMb = 12_288 },
                ], "hardware/dxgi+pnp"),
                Board = K(new BoardInfo { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardProduct = "PRO B660M-A WIFI DDR4 (MS-7D43)", SystemManufacturer = "Micro-Star International Co., Ltd.", SystemProduct = "MS-7D43", BiosVendor = "American Megatrends International, LLC.", BiosVersion = "1.B0", BiosReleaseDate = new DateOnly(2022, 9, 29), IsLaptop = false }, "hardware/wmi:Win32_BaseBoard+Win32_BIOS"),
            },
            Security = s.Security with
            {
                SecureBootEnabled = K(false, "security/registry:SecureBoot\\State"),
                SecureBootSetupMode = Fact<bool>.Unknown(UnknownReason.NeedsAdmin, NeedsAdmin, "security/firmware:SetupMode"),
                SecureBootDbHasCa2023 = Fact<bool>.Unknown(UnknownReason.NeedsAdmin, NeedsAdmin, "security/firmware:db"),
                Tpm = K(new TpmInfo { Present = true, SpecVersion = "2.0", Kind = TpmKind.Firmware, InstanceId = "ACPI\\MSFT0101\\1" }, "security/tbs"),
                Vbs = K(new VbsInfo { Status = VbsStatus.Off, HvciConfigured = false, HvciRunning = false, AvailableSecurityProperties = [1, 2, 3, 5], RequiredSecurityProperties = [], SecurityServicesRunning = [] }, "security/wmi:Win32_DeviceGuard"),
                HypervisorPresent = K(false, "security/cpuid"),
                Bcd = Fact<BcdFlags>.Unknown(UnknownReason.NeedsAdmin, NeedsAdmin, "security/wmi:BcdStore"),
                Defender = Fact<DefenderInfo>.Unknown(UnknownReason.NeedsAdmin, NeedsAdmin, "security/wmi:MSFT_MpComputerStatus"),
                SystemVolumeBitLocker = K(BitLockerStatus.Off, "security/shell:BitLockerProtection"),
            },
            Storage = s.Storage with
            {
                Volumes = K<IReadOnlyList<VolumeInfo>>(
                [
                    new VolumeInfo { DriveLetter = "C:", FileSystem = "NTFS", SizeMb = 952_000, FreeMb = 301_000, Dirty = null, IsSystem = true, HostsRiotGames = true },
                ], "storage/volumes"),
            },
            Drivers = K<IReadOnlyList<LoadedDriver>>(s.Drivers.Value.Where(d => !string.Equals(d.Name, "vgk.sys", StringComparison.OrdinalIgnoreCase)).ToList(), "drivers/EnumDeviceDrivers"),
            Services = K<IReadOnlyList<ServiceInfo>>(
                s.Services.Value.Select(v => string.Equals(v.Name, "vgk", StringComparison.Ordinal) ? v with { State = ServiceState.Stopped } : v).ToList(),
                "software/scm"),
            Riot = s.Riot with
            {
                Vanguard = K(s.Riot.Vanguard.Value with { Vgk = VgkService() with { State = ServiceState.Stopped } }, "riot/vanguard"),
                ClientLogs = K(new RiotClientLogDigest
                {
                    FileCount = 9,
                    NewestUtc = created.AddMinutes(-12),
                    PatternCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["client.van_error_dialog"] = 3 },
                }, "riot/RiotClientLogs"),
                DetectedCodes = K<IReadOnlyList<DetectedCode>>(
                [
                    new DetectedCode { Code = "VAN 9003", Source = CodeSource.RiotClientLog, Count = 3, FirstUtc = created.AddDays(-2), LastUtc = created.AddMinutes(-12) },
                ], "riot/logs"),
            },
        };
    }

    /// <summary>All three problem fixtures by file name (without extension).</summary>
    public static IReadOnlyDictionary<string, Func<ScanSnapshot>> All { get; } = new Dictionary<string, Func<ScanSnapshot>>(StringComparer.Ordinal)
    {
        [Fixtures.RealtekStutter] = RealtekStutter,
        [Fixtures.NahimicBlackScreen] = NahimicBlackScreen,
        [Fixtures.Van9003SecureBootOff] = Van9003SecureBootOff,
    };

    private static ServiceInfo VgcService(ServiceStartType startType, ServiceState state) => new()
    {
        Name = "vgc",
        DisplayName = "vgc",
        Kind = ServiceKind.Win32,
        StartType = startType,
        State = state,
        ImagePath = "C:\\Program Files\\Riot Vanguard\\vgc.exe",
        Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "Riot Games, Inc.", IsMicrosoft = false },
    };

    private static ServiceInfo VgkService() => new()
    {
        Name = "vgk",
        DisplayName = "vgk",
        Kind = ServiceKind.KernelDriver,
        StartType = ServiceStartType.System,
        State = ServiceState.Running,
        ImagePath = "C:\\Program Files\\Riot Vanguard\\vgk.sys",
        Signer = new SignerInfo { Status = SignatureStatus.Valid, Publisher = "Riot Games, Inc.", IsMicrosoft = false },
    };

    private static NetAdapter WifiAdapter() => new()
    {
        InstanceId = WifiAdapterInstanceId,
        InterfaceGuid = "{1C2D3E4F-5A6B-4C7D-8E9F-0A1B2C3D4E5F}",
        Description = "Intel(R) Wi-Fi 6E AX210 160MHz",
        Alias = "Wi-Fi",
        Kind = AdapterKind.WiFi,
        Physical = true,
        Enabled = true,
        Status = AdapterOperStatus.Up,
        LinkSpeedBps = 1_201_000_000,
        IsDefaultRoute = true,
        HasInternet = true,
        Ipv4Metric = 35,
        AutomaticMetric = true,
        Ipv6Bound = true,
        DriverFile = "Netwtw10.sys",
        DriverVersion = "23.110.0.5",
        DriverDate = new DateOnly(2025, 2, 10),
        DriverProvider = "Intel",
        AllowPowerOff = false,
        Mac = "D8:F2:CA:4F:8C:46",
        Ipv4Addresses = ["192.168.1.34"],
        DnsServers = ["192.168.1.1"],
    };

    private static IReadOnlyList<CollectorRun> StandardRuns() =>
    [
        new CollectorRun { CollectorId = "os", Status = CollectorStatus.Ok, ElapsedMs = 420 },
        new CollectorRun { CollectorId = "hardware", Status = CollectorStatus.Ok, ElapsedMs = 2_150 },
        new CollectorRun { CollectorId = "firmware", Status = CollectorStatus.Ok, ElapsedMs = 40 },
        new CollectorRun { CollectorId = "security", Status = CollectorStatus.Ok, ElapsedMs = 3_900 },
        new CollectorRun { CollectorId = "storage", Status = CollectorStatus.Ok, ElapsedMs = 1_100 },
        new CollectorRun { CollectorId = "devices", Status = CollectorStatus.Ok, ElapsedMs = 900 },
        new CollectorRun { CollectorId = "software", Status = CollectorStatus.Ok, ElapsedMs = 3_400 },
        new CollectorRun { CollectorId = "network", Status = CollectorStatus.Ok, ElapsedMs = 6_100 },
        new CollectorRun { CollectorId = "power", Status = CollectorStatus.Ok, ElapsedMs = 60 },
        new CollectorRun { CollectorId = "display", Status = CollectorStatus.Ok, ElapsedMs = 150 },
        new CollectorRun { CollectorId = "audio", Status = CollectorStatus.Ok, ElapsedMs = 240 },
        new CollectorRun { CollectorId = "input", Status = CollectorStatus.Ok, ElapsedMs = 5 },
        new CollectorRun { CollectorId = "riot", Status = CollectorStatus.Ok, ElapsedMs = 1_900 },
        new CollectorRun { CollectorId = "events", Status = CollectorStatus.Ok, ElapsedMs = 6_800 },
        new CollectorRun { CollectorId = "pack", Status = CollectorStatus.Ok, ElapsedMs = 25 },
    ];

    private static Fact<T> K<T>(T value, string source) => Fact<T>.Known(value, source);
}
