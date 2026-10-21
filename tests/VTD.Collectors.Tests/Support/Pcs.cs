using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Platform.Sources;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests.Support;

/// <summary>Synthetic PCs for collector tests.</summary>
internal static class Pcs
{
    public static readonly DateTimeOffset Start = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    public const string RealtekId = @"PCI\VEN_10EC&DEV_8125&SUBSYS_86771043&REV_05\01000000684CE00000";
    public const string GpuId = @"PCI\VEN_10DE&DEV_2783&SUBSYS_51831462&REV_A1\4&2E3F5C1&0&0019";
    public const string NicGuid = "{11111111-2222-3333-4444-555555555555}";

    public static PlayerContext Player => FakePlayer.Default;

    public static string Hku(string path) => @"HKU\" + Player.Sid + "\\" + path;

    public static string LocalAppData => Player.LocalAppData;

    /// <summary>A healthy AM5 desktop with VALORANT, Vanguard (Pre-Check: vgc Manual + Stopped), Turkish Windows.</summary>
    public static FakeSources Healthy()
    {
        var f = new FakeSources();
        const string S = "String";
        const string D = "DWord";
        f.Reg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", ("ProductName", S, "Windows 10 Pro"), ("CurrentBuildNumber", S, "26200"), ("UBR", D, "6899"), ("DisplayVersion", S, "25H2"), ("EditionID", S, "Professional"), ("InstallDate", D, "1762093200"), ("RegisteredOwner", S, CanaryTokens.UserName), ("DigitalProductId", "Binary", "00ff"));
        f.Reg(@"HKLM\HARDWARE\DESCRIPTION\System\BIOS", ("SystemManufacturer", S, "ASUS"), ("SystemProductName", S, "System Product Name"), ("BIOSVendor", S, "American Megatrends Inc."), ("BIOSVersion", S, "3201"), ("BIOSReleaseDate", S, "03/07/2025"), ("BaseBoardManufacturer", S, "ASUSTeK COMPUTER INC."), ("BaseBoardProduct", S, "ROG STRIX B650E-F GAMING WIFI"));
        f.Reg(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", ("ProcessorNameString", S, "AMD Ryzen 7 9800X3D 8-Core Processor"), ("VendorIdentifier", S, "AuthenticAMD"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", ("BootExecute", "MultiString", "autocheck autochk *"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ("HiberbootEnabled", D, "1"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", ("HibernateEnabled", D, "1"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", ("BootId", D, "412"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ("TEMP", "ExpandString", @"%SystemRoot%\TEMP"), ("TMP", "ExpandString", @"%SystemRoot%\TEMP"), ("ComSpec", "ExpandString", @"%SystemRoot%\system32\cmd.exe"), ("Path", "ExpandString", @"%SystemRoot%\system32"), ("windir", "ExpandString", "%SystemRoot%"), ("PATHEXT", S, ".COM;.EXE"));
        f.Reg(Hku("Environment"), ("TEMP", "ExpandString", @"%USERPROFILE%\AppData\Local\Temp"), ("TMP", "ExpandString", @"%USERPROFILE%\AppData\Local\Temp"));
        f.Folder(Player.ProfilePath + @"\AppData\Local\Temp", Start);
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Parameters", ("Type", S, "NTP"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", ("UEFISecureBootEnabled", D, "1"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ("HwSchMode", D, "2"));
        f.Reg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", ("x", S, null));
        f.Reg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\vgc.exe", ("MitigationOptions", "Binary", "0001000000000000"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", ("DisabledComponents", D, "0"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", ("{rule-1}", S, @"v2.31|Action=Block|Active=TRUE|Dir=Out|App=C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe|Name=Block VALORANT|"), ("{rule-2}", S, @"v2.31|Action=Allow|Active=TRUE|Dir=In|App=%SystemRoot%\system32\svchost.exe|Name=@FirewallAPI.dll,-1|"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile", ("EnableFirewall", D, "1"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", ("EnableFirewall", D, "1"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile", ("EnableFirewall", D, "1"));
        f.Reg(Hku(@"Control Panel\Mouse"), ("MouseSpeed", S, "0"), ("MouseSensitivity", S, "10"));
        f.Reg(Hku(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"), ("ProxyEnable", D, "0"));
        f.Reg(Hku(@"Software\Microsoft\DirectX\UserGpuPreferences"), (@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", S, "GpuPreference=2;"), ("DirectXUserGlobalSettings", S, "SwapEffectUpgradeEnable=1;"));
        f.Reg(Hku(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"), (@"C:\Riot Games\Riot Client\RiotClientServices.exe", S, "~ RUNASADMIN"));

        // Uninstall keys: HKLM, WOW6432Node, the player's hive.
        f.Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Riot Vanguard", ("DisplayName", S, "Riot Vanguard"), ("Publisher", S, "Riot Games, Inc."), ("DisplayVersion", S, "1.17.9.5"), ("UninstallString", S, "\"C:\\Program Files\\Riot Vanguard\\installer.exe\" --uninstall " + CanaryTokens.RemotingAuthToken));
        f.Reg(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{NAHIMIC}", ("DisplayName", S, "Nahimic"), ("Publisher", S, "A-Volute"), ("DisplayVersion", S, "1.10"));
        f.Reg(Hku(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Discord"), ("DisplayName", S, "Discord"), ("Publisher", S, "Discord Inc."), ("InstallLocation", S, Player.LocalAppData + @"\Discord"));
        f.Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ("SecurityHealth", "ExpandString", @"%windir%\system32\SecurityHealthSystray.exe"));
        f.Reg(Hku(@"Software\Microsoft\Windows\CurrentVersion\Run"), ("Discord", S, "\"" + Player.LocalAppData + "\\Discord\\Update.exe\" --processStart Discord.exe " + CanaryTokens.Jwt));

        // Services (vgc Manual + Stopped is normal with Pre-Check).
        f.Services = new ServiceList
        {
            Status = ReadStatus.Ok,
            Items =
            [
                new ServiceRec { Name = "vgc", DisplayName = "vgc", Type = 0x10, State = 1 },
                new ServiceRec { Name = "vgk", DisplayName = "vgk", Type = 0x1, State = 4 },
                new ServiceRec { Name = "Schedule", DisplayName = "Görev Zamanlayıcı", Type = 0x20, State = 4 },
                new ServiceRec { Name = "rt25cx21", DisplayName = "Realtek", Type = 0x1, State = 4 },
                new ServiceRec { Name = "TrustedInstaller", DisplayName = "Windows Modül Yükleyicisi", Type = 0x10, State = 1 },
            ],
        };
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\vgc", ("Start", D, "3"), ("ImagePath", "ExpandString", "\"C:\\Program Files\\Riot Vanguard\\vgc.exe\""), ("Type", D, "16"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\vgk", ("Start", D, "1"), ("ImagePath", "ExpandString", @"\??\C:\Program Files\Riot Vanguard\vgk.sys"), ("Type", D, "1"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\Schedule", ("Start", D, "2"), ("ImagePath", "ExpandString", @"%systemroot%\system32\svchost.exe -k netsvcs -p"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Services\rt25cx21", ("Start", D, "3"), ("ImagePath", "ExpandString", @"\SystemRoot\System32\drivers\rt25cx21x64.sys"));
        f.Processes = new ProcessList
        {
            Status = ReadStatus.Ok,
            Items = [new ProcessRec { Name = "explorer.exe", Pid = 7412, ParentPid = 7380, Threads = 96 }, new ProcessRec { Name = "RiotClientServices.exe", Pid = 13020, ParentPid = 7412, Threads = 61 }],
        };
        f.Drivers = new DriverList { Status = ReadStatus.Ok, Items = [new DriverRec { Name = "vgk.sys", Path = @"\??\C:\Program Files\Riot Vanguard\vgk.sys" }, new DriverRec { Name = "rt25cx21x64.sys", Path = @"\SystemRoot\System32\drivers\rt25cx21x64.sys" }] };

        // Devices: GPU, Realtek NIC (unused; player is on Wi-Fi in other tests), TPM.
        f.Devices = new DeviceList
        {
            Status = ReadStatus.Ok,
            Items =
            [
                new DeviceRec { InstanceId = GpuId, ClassGuid = DeviceClasses.Display, ClassName = "Display", Enumerator = "PCI", FriendlyName = "NVIDIA GeForce RTX 4070 SUPER", Present = true, Service = "nvlddmkm", DriverVersion = "32.0.15.8157", DriverDate = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero), DriverProvider = "NVIDIA", DriverKey = DeviceClasses.Display + @"\0000" },
                new DeviceRec { InstanceId = RealtekId, ClassGuid = DeviceClasses.Net, ClassName = "Net", Enumerator = "PCI", FriendlyName = "Realtek Gaming 2.5GbE Family Controller", Present = true, Service = "rt25cx21", DriverVersion = "10.68.1003.2023", DriverKey = DeviceClasses.Net + @"\0001" },
                new DeviceRec { InstanceId = @"ACPI\MSFT0101\1", ClassGuid = DeviceClasses.SecurityDevices, ClassName = "SecurityDevices", Enumerator = "ACPI", FriendlyName = "Güvenilir Platform Modülü 2.0", Present = true, Service = "TPM" },
            ],
        };
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\" + DeviceClasses.Display + @"\0000", ("HardwareInformation.qwMemorySize", "QWord", "12878610432"));
        f.Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\" + DeviceClasses.Net + @"\0001", ("NetCfgInstanceId", S, NicGuid), ("DriverDesc", S, "Realtek Gaming 2.5GbE Family Controller"), ("*IfType", D, "6"), ("*PhysicalMediaType", D, "14"), ("Characteristics", D, "132"), ("*EEE", S, "1"), ("NetworkAddress", S, "AABBCCDDEEFF"));
        f.Network = new NetRaw
        {
            Status = ReadStatus.Ok,
            BestIpv4Index = 7,
            WinHttpProxy = false,
            Interfaces = [new NetIfRec { Guid = NicGuid, Description = "Realtek Gaming 2.5GbE Family Controller", Alias = "Ethernet", IfType = 6, OperStatus = 1, SpeedBps = 2_500_000_000, Mac = "D8:F2:CA:4F:8C:46", Ipv4 = ["192.168.1.34"], Dns = ["192.168.1.1"], Gateways = ["192.168.1.1"], Ipv4Index = 7, Ipv4Metric = 25, AutomaticMetric = true }],
        };
        f.Displays = new DisplayRaw { Status = ReadStatus.Ok, Paths = [new DisplayPathRec { Name = "VG27AQ3A", Width = 2560, Height = 1440, RefreshHz = 180, Primary = true, Hdr = false, Internal = false, AdapterDevicePath = @"\\?\PCI#VEN_10DE&DEV_2783&SUBSYS_51831462&REV_A1#4&2e3f5c1&0&0019#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}" }] };

        // WMI.
        f.Wmi("Win32_DeviceGuard", Row(("VirtualizationBasedSecurityStatus", "2"), ("SecurityServicesConfigured", "2"), ("SecurityServicesRunning", "2"), ("AvailableSecurityProperties", "1,2,3,5,6,7"), ("RequiredSecurityProperties", "")));
        f.Wmi("Win32_Processor", Row(("VirtualizationFirmwareEnabled", "false")));
        f.Wmi("Win32_QuickFixEngineering", Row(("HotFixID", "KB5066835"), ("InstalledOn", "10/15/2026")));
        f.Wmi("Win32_PhysicalMemory", Row(("Capacity", "17179869184"), ("Speed", "6000"), ("ConfiguredClockSpeed", "4800"), ("DeviceLocator", "DIMM_A2")), Row(("Capacity", "17179869184"), ("Speed", "6000"), ("ConfiguredClockSpeed", "4800"), ("DeviceLocator", "DIMM_B2")));
        f.Wmi("MSFT_Disk", Row(("Number", "0"), ("PartitionStyle", "2"), ("IsBoot", "true"), ("IsSystem", "true"), ("BusType", "17"), ("Size", "2000398934016"), ("FriendlyName", "Samsung SSD 990 PRO 2TB")));
        f.Wmi("MSFT_PhysicalDisk", Row(("DeviceId", "0"), ("MediaType", "4"), ("BusType", "17")));
        f.Wmi("Win32_Volume", Row(("DriveLetter", "C:"), ("FileSystem", "NTFS"), ("Capacity", "1998597734400"), ("FreeSpace", "1300234567680"), ("DirtyBitSet", "false"), ("DriveType", "3")));
        f.Wmi("AntiVirusProduct", Row(("displayName", "Windows Defender"), ("productState", "397568"), ("pathToSignedProductExe", "windowsdefender://")));
        f.Wmi("MSFT_MpComputerStatus", Row(("RealTimeProtectionEnabled", "true"), ("IsTamperProtected", "true")));
        f.Wmi("Win32_OptionalFeature", Row(("Name", "Microsoft-Windows-Subsystem-Linux")), Row(("Name", "VirtualMachinePlatform")));
        f.Wmi("Win32_SystemEnclosure", Row(("ChassisTypes", "3")));

        // Riot.
        f.File(@"C:\ProgramData\Riot Games\RiotClientInstalls.json", 300, Start, "{", "  \"associated_client\": {", "    \"C:/Riot Games/VALORANT/live/\": \"C:/Riot Games/Riot Client/RiotClientServices.exe\"", "  },", "  \"rc_default\": \"C:/Riot Games/Riot Client/RiotClientServices.exe\"", "}");
        f.File(@"C:\ProgramData\Riot Games\Metadata\valorant.live\valorant.live.product_settings.yaml", 200, Start, "product_install_full_path: \"C:/Riot Games/VALORANT/live\"", "product_install_root: \"C:/Riot Games\"");
        f.File(@"C:\Riot Games\Riot Client\RiotClientServices.exe", 1000, Start);
        f.Versions[@"C:\Riot Games\Riot Client\RiotClientServices.exe"] = new FileVersionResult { Status = ReadStatus.Ok, FileVersion = "113.0.2.2271", ProductVersion = "113.0.2.2271" };
        f.File(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", 1000, Start);
        f.Folder(@"C:\Program Files\Riot Vanguard", Start);
        f.File(@"C:\Program Files\Riot Vanguard\vgc.exe", 18_431_256, Start.AddDays(-13));
        f.Versions[@"C:\Program Files\Riot Vanguard\vgc.exe"] = new FileVersionResult { Status = ReadStatus.Ok, FileVersion = "1.17.9.5" };
        f.File(@"C:\Program Files\Riot Vanguard\vgk.sys", 25_102_920, Start.AddDays(-13));
        f.File(@"C:\Program Files\Riot Vanguard\Logs\vgc_2026-10-21_15-00-00.log", 7000, Start.AddHours(-2));
        f.File(@"C:\Program Files\Riot Vanguard\Logs\vgc_2026-10-20_15-00-00.log", 9000, Start.AddDays(-1).AddHours(-2));
        var logs = LocalAppData + @"\VALORANT\Saved\Logs";
        f.File(logs + @"\ShooterGame.log", 13_000_000, Start.AddHours(-1), ShooterLines());
        f.File(logs + @"\ShooterGame-backup-2026.10.20-00.13.47.log", 17_000_000, Start.AddDays(-1), "[2026.10.20-00.10.00:000][  0]LogPlatformInitializerV2: Error: Failed to initialize: VanguardManager (error: VanguardNotRunning) after 0.1s");
        var client = LocalAppData + @"\Riot Games\Riot Client\Logs\Riot Client Logs";
        f.File(client + @"\2026-10-21T17-00-00_1234_Riot Client.log", 3000, Start.AddMinutes(-12), ClientLines());
        var crash = LocalAppData + @"\VALORANT\Saved\Crashes\UECC-Windows-0123456789ABCDEF0123456789ABCDEF_0000";
        f.Folder(crash, Start.AddDays(-3));
        f.File(crash + @"\CrashContext.runtime-xml", 5000, Start.AddDays(-3), CrashLines());
        var config = LocalAppData + @"\VALORANT\Saved\Config";
        f.Folder(config + @"\0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-eu", Start);
        f.Folder(config + @"\" + CanaryTokens.ComputerName + "-0123", Start);
        f.File(config + @"\0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-eu\Windows\GameUserSettings.ini", 2000, Start, "[/Script/ShooterGame.ShooterGameUserSettings]", "LastConfirmedDefaultMonitorDeviceID=\"MONITOR\\\\MSI3CB5\\\\{a0b1c2d3}\\\\0001\"", "FullscreenMode=0", "ResolutionSizeX=2560", "ResolutionSizeY=1440", "FrameRateLimit=0.000000", "UserName=" + CanaryTokens.UserName);

        // Events.
        f.Event("System", "rt25cx21", 27, Start.AddDays(-2));
        f.Event("System", "rt25cx21", 27, Start.AddDays(-1));
        f.Event("Application", "Application Error", 1000, Start.AddDays(-3), ("AppName", "VALORANT-Win64-Shipping.exe"), ("AppVersion", "4.27.2.0"), ("ModuleName", "ProductInfo.dll"), ("ModuleVersion", "1.0.0.1"), ("ExceptionCode", "c0000005"));
        f.Event("Application", "Application Error", 1000, Start.AddDays(-3), ("AppName", "notepad.exe"), ("ModuleName", "ntdll.dll"), ("ExceptionCode", "c0000005"));
        f.Event("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, Start.AddDays(-5), ("param1", "0x00000133 (0x0000000000000000, 0x0000000000000501, 0x0000000000000500, 0xfffff8045e2fb320)"), ("param2", @"C:\Windows\Minidump\102026-12345-01.dmp"));
        return f;
    }

    public static string[] ShooterLines() =>
    [
        "Log file open, 10/21/26 17:00:00",
        "LogInit: OS: Windows 11 (25H2) [10.0.26200.6899] (), CPU: AMD Ryzen 7 9800X3D 8-Core Processor, GPU: NVIDIA GeForce RTX 4070 SUPER, Disk: SSD",
        "LogInit: Command Line: " + CanaryTokens.RemotingAuthToken + " -subject=" + CanaryTokens.Puuid,
        "[2026.10.21-17.00.01:000][  0]LogShooter: Display: CI server version: release-11.08-shipping-14-3988147",
        "[2026.10.21-17.00.02:000][  0]LogPlatformInitializerV2: Error: Failed to initialize: ContractsManager (error: RecruitmentInitConfigurationFailure) after 0.2s",
        "[2026.10.21-17.00.03:000][  0]LogPlatformInitializerV2: Error: Failed to initialize: ContractsManager (error: RecruitmentInitConfigurationFailure) after 0.2s",
        "[2026.10.21-17.05.00:000][  0]LogD3D11RHI: Error: Unreal Engine is exiting due to D3D device being lost. (Error: 0x887A0005 - 'REMOVED')",
        "[2026.10.21-17.06.00:000][  0]LogNet: Warning: Network Failure: GameNetDriver[ConnectionLost]: NetworkFailure ConnectionLost",
        "[2026.10.21-17.07.00:000][  0]LogShooter: Error: VAN 9003 reported " + CanaryTokens.Email,
        "[2026.10.21-17.08.00:000][  0]LogShooter: Error: Vanguard error VAN 9003 received",
        "[2026.10.21-17.09.00:000][  0]LogUser: UserName=" + CanaryTokens.UserName + " MachineId=abc",
        "[2026.10.21-17.10.00:000][  0]LogHttp: Bearer " + CanaryTokens.Jwt,
        "[2026.10.21-17.11.00:000][  0]LogShooter: path " + CanaryTokens.UserProfilePath + " Fatal error",
    ];

    public static string[] ClientLines() =>
    [
        "001.000|   OKAY| vanguard: Per-product vanguard check for valorant: restartRequired=false",
        "002.000|  ERROR| vanguard: VAN 9003 Secure Boot violation dialog shown",
        "003.000|  ERROR| vanguard: VAN 9003 Secure Boot violation dialog shown",
        "004.000|  ERROR| SDK: rso-authenticator: RSO Authenticator x-rso-error-id 40101002",
        "005.000|  ERROR| riot-client-lifecycle: Failed to start patching for patchline valorant.live",
        "006.000|   OKAY| login: access_token=" + CanaryTokens.Jwt + " VAN 9003",
        "007.000|   OKAY| \"enable_crashpad\": true",
    ];

    public static string[] CrashLines() =>
    [
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<FGenericCrashContext>",
        "\t\t<CrashType>GPUCrash</CrashType>",
        "\t\t<ErrorMessage>Fatal error: [File:Unknown] Unreal Engine is exiting due to D3D device being lost. " + CanaryTokens.UserProfilePath + "</ErrorMessage>",
        "\t\t<CommandLine>VALORANT.exe " + CanaryTokens.RemotingAuthToken + " -subject=" + CanaryTokens.Puuid + "</CommandLine>",
        "\t\t<UserName>" + CanaryTokens.UserName + "</UserName>",
        "\t\t<MachineId>ABCDEF</MachineId>",
        "\t\t<BaseDir>C:/Riot Games/VALORANT/live/ShooterGame/Binaries/Win64/</BaseDir>",
        "</FGenericCrashContext>",
    ];

    public static ScanRequest Request(ScanMode mode = ScanMode.Full, IReadOnlyList<string>? collectors = null, bool capture = false)
        => new() { KbVersion = "2026.10.21", Player = Player, Mode = mode, Collectors = collectors ?? [], CaptureRaw = capture };

    public static ScanOrchestrator Orchestrator(FakeSources f, PrivilegeMode privilege = PrivilegeMode.Elevated, IReadOnlyList<CollectorBase>? collectors = null)
    {
        var clock = new FakeClock(Start);
        return new ScanOrchestrator(f.ToSources(), clock, new SequentialIdGenerator(clock), new ScanOrchestratorOptions { Privilege = privilege, MachineKeySalt = ReplayHarness.Salt, AppVersion = "0.0.1" }, collectors);
    }

    private static Dictionary<string, string> Row(params (string Key, string Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
}
