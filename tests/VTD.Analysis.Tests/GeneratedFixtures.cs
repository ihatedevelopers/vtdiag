using VTD.Contracts;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Analysis.Tests;

/// <summary>One labelled fixture: what the player says and what the analysis should find.</summary>
public sealed record FixtureLabel
{
    public required string Name { get; init; }

    /// <summary>The expected top hypothesis, or null when the honest answer is "we couldn't pin this down".</summary>
    public string? ExpectedTop { get; init; }

    /// <summary>Other hypotheses that are acceptable as "very likely" (co-existing real causes).</summary>
    public IReadOnlyList<string> Accept { get; init; } = [];

    public IReadOnlyList<string> Symptoms { get; init; } = [];

    public IReadOnlyList<string> TypedCodes { get; init; } = [];

    public IReadOnlyDictionary<string, string> Answers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Expected route verdict (camelCase RouteKind), when the case is outside the PC.</summary>
    public string? ExpectedRoute { get; init; }

    public bool ExpectUnknownLeading { get; init; }
}

/// <summary>
/// Synthetic fixtures per major problem (DESIGN.md testing: "synthetic fixtures generated per problem"). Each one starts
/// from <see cref="FixtureSnapshots.HealthyDesktop"/> (which already has Discord, an NVIDIA driver, Fast Startup on,
/// HAGS on...) and changes only what the problem changes, so unrelated noise stays in. Written to
/// tests/fixtures/snapshots/generated/ with labels.json; the accuracy gate reads them from disk.
/// </summary>
public static class GeneratedFixtures
{
    public static readonly DateTimeOffset Created = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private const string RealtekId = FixtureSnapshots.RealtekAdapterInstanceId;
    private const string WifiId = FixtureSnapshots.WifiAdapterInstanceId;

    public static IReadOnlyList<(FixtureLabel Label, ScanSnapshot Snapshot)> All() => Build();

    private static List<(FixtureLabel, ScanSnapshot)> Build()
    {
        var list = new List<(FixtureLabel, ScanSnapshot)>();
        var index = 0;
        void Add(FixtureLabel label, Func<ScanSnapshot, ScanSnapshot> mutate)
        {
            var bytes = new byte[10];
            bytes[0] = 0xA2;
            bytes[9] = (byte)++index;
            var id = Ulid.Format(Created, bytes);
            list.Add((label, mutate(Base(id))));
        }

        // ---------------------------------------------------------------- fps / stutter
        Add(L("realtek-unused-on-wifi", "realtek-2-5gbe-nic-stutter", ["stutter"]), s => WithRealtek(s, enabled: true, defaultRoute: false, errors: 0));
        Add(L("realtek-active-power-saving", "realtek-2-5gbe-nic-stutter", ["stutter"], answers: A(("ask.stutter_periodic", "yes"))),
            s => WithRealtek(s, enabled: true, defaultRoute: true, errors: 6, wifiUp: false));
        Add(L("overlay-rtss-overwolf", "third-party-hook-overlay-conflict", ["stutter"], accept: ["conflict-rtss", "conflict-overwolf_trackers"]),
            s => Procs(s, "RTSS.exe", "RTSSHooksLoader64.exe", "MSIAfterburner.exe", "Overwolf.exe", "OverwolfBrowser.exe")
                 with { Events = s.Events with { AppCrashes = K<IReadOnlyList<AppCrash>>([Crash("RTSSHooks64.dll", -2)]) } });
        Add(L("premier-opinion-hook", "third-party-hook-overlay-conflict", ["launch_black_screen"], accept: ["conflict-premier_opinion"]),
            s => Apps(s, ("Premier Opinion", "Premier Opinion"))
                 with { Events = s.Events with { AppCrashes = K<IReadOnlyList<AppCrash>>([Crash("pmls64.dll", -1), Crash("pmls64.dll", -3)]) } });
        Add(L("power-saver-laptop", "power-plan-throttling-low-fps", ["fps_low"]),
            s => Laptop(s) with
            {
                Power = s.Power with
                {
                    ActiveScheme = K(new PowerScheme { Guid = "a1841308-3541-4fab-bc81-f71556f20b4a", Kind = PowerSchemeKind.PowerSaver }),
                    Battery = K(new BatteryInfo { Present = true, OnAc = false, ChargePercent = 61 }),
                },
            });
        Add(L("kb5101650-microstutter", "windows-update-kb5101650-microstutter", ["stutter"]),
            s => s with { Os = s.Os with { Hotfixes = K<IReadOnlyList<HotfixInfo>>([new HotfixInfo { Id = "KB5101650", InstalledOn = new DateOnly(2026, 10, 15) }]) } });
        Add(L("hybrid-cpu-high-priority", "high-process-priority-van136-185", ["disconnect"], codes: ["VAN 136"], accept: ["intel-hybrid-cpu-ecore-stutter"]),
            s => s with { Security = s.Security with { Ifeo = K<IReadOnlyList<IfeoEntry>>([new IfeoEntry { ImageName = "VALORANT-Win64-Shipping.exe", CpuPriorityClass = 3, ValueNames = ["PerfOptions"] }]) } });

        // ---------------------------------------------------------------- launch / crash
        Add(L("nahimic-running", "nahimic-audio-service-launch-hang", ["launch_black_screen"]),
            s => Svc(Procs(Apps(s, ("Nahimic", "A-Volute")), "NahimicSvc64.exe"), Service("NahimicService", ServiceStartType.Automatic, ServiceState.Running)));
        Add(L("bonjour-val43", "mdns-bonjour-ni-val43-loading", ["launch_black_screen"], codes: ["VAL 43"], answers: A(("ask.other_account_works", "no"))),
            s => Svc(Procs(Apps(s, ("Bonjour", "Apple Inc.")), "mDNSResponder.exe"), Service("Bonjour Service", ServiceStartType.Automatic, ServiceState.Running))
                 with { Network = s.Network with { WinsockNamespaceProviders = K<IReadOnlyList<string>>(["mdnsnsp.dll"]) } });
        Add(L("stale-dat-fast-startup", "fast-startup-stale-dat-bsod", ["freeze_hard"], accept: ["vanguard-corrupt-install"]),
            s => VgFiles(s, ("392667600.dat", "windows", true))
                 with { Events = s.Events with { BugChecks = K<IReadOnlyList<BugCheck>>([new BugCheck { Code = "0x0000007e", Utc = Created.AddDays(-1) }]) } });
        Add(L("vanguard-corrupt", "vanguard-corrupt-install", ["wont_launch"], codes: ["VAN -1"], answers: A(("ask.league_also_broken", "yes"))),
            s => VgFiles(s, ("vgk.sys", "vanguard", false)) with
            {
                Riot = s.Riot with { Vanguard = K(s.Riot.Vanguard.Value with { Vgk = null, Files = [.. s.Riot.Vanguard.Value.Files.Where(f => f.Name != "vgk.sys"), new RiotFileInfo { Name = "vgk.sys", Location = "vanguard", Exists = false }] }) },
                Drivers = K<IReadOnlyList<LoadedDriver>>([.. s.Drivers.Value.Where(d => d.Name != "vgk.sys")]),
            });
        Add(L("vgc-disabled", "vgc-service-not-running", ["error_code"], codes: ["VAN -79"], answers: A(("ask.vanguard_precheck_on", "no"))),
            s => WithVgc(s, ServiceStartType.Disabled, ServiceState.Stopped));
        Add(L("vgk-not-loaded", "vgk-not-loaded-needs-reboot", ["error_code"], codes: ["VAN 57"]),
            s => s with
            {
                Os = s.Os with { PendingReboot = K(new PendingRebootInfo { ComponentServicing = false, WindowsUpdate = false, PendingFileRename = true }) },
                Riot = s.Riot with { Vanguard = K(s.Riot.Vanguard.Value with { Vgk = s.Riot.Vanguard.Value.Vgk! with { State = ServiceState.Stopped } }) },
                Drivers = K<IReadOnlyList<LoadedDriver>>([.. s.Drivers.Value.Where(d => d.Name != "vgk.sys")]),
            });
        Add(L("exploit-protection-van9002", "conflict-exploit_protection_override", ["wont_launch"], codes: ["VAN 9002"], accept: ["launch-fails-something-unusual"]),
            s => s with { Security = s.Security with { ExploitOverrides = K<IReadOnlyList<ExploitOverride>>([new ExploitOverride { ImageName = "vgc.exe", MitigationOptionsHex = "0x2000000000" }]) } });
        Add(L("env-vars-missing", "missing-environment-variables", ["wont_launch"]),
            s => s with { Os = s.Os with { EnvironmentIssues = K<IReadOnlyList<EnvVarFinding>>([new EnvVarFinding { Name = "TEMP", Issue = "missing", Scope = "user" }]) } });
        Add(L("vcredist-missing", "side-by-side-config-error", ["wont_launch"]),
            s => s with { Apps = K<IReadOnlyList<InstalledApp>>([.. s.Apps.Value.Where(a => !a.DisplayName.Contains("Visual C++", StringComparison.Ordinal))]) });

        // ---------------------------------------------------------------- whole-PC freezes, hardware
        Add(L("kernel-power-whea", "whole-pc-freeze-kernel-power-41", ["freeze_hard"], answers: A(("ask.whole_pc_freezes", "yes")), accept: ["usb-selective-suspend-freeze", "outdated-bios-reboot-vgk-bsod"]),
            s => Hits(s, Hit("kernel_power_41", "Microsoft-Windows-Kernel-Power", 41, 4), Hit("whea_errors", "Microsoft-Windows-WHEA-Logger", 18, 3)));
        Add(L("old-bios-vgk-bsod", "outdated-bios-reboot-vgk-bsod", ["freeze_hard"], accept: ["whole-pc-freeze-kernel-power-41"]),
            s => s with
            {
                Hardware = s.Hardware with { Board = K(s.Hardware.Board.Value with { BiosVersion = "1004", BiosReleaseDate = new DateOnly(2020, 6, 2) }) },
                Events = s.Events with { Minidumps = K<IReadOnlyList<MinidumpMeta>>([new MinidumpMeta { FileName = "102026-11234-01.dmp", ModifiedUtc = Created.AddDays(-2), SizeBytes = 1_200_000, BugCheckCode = "0x00000050", FaultingDriver = "vgk.sys" }]) },
            });
        Add(L("igpu-driverless", "igpu-disabled-or-driverless-freeze", ["freeze_hard"]),
            s => s with { Hardware = s.Hardware with { Gpus = K<IReadOnlyList<GpuInfo>>([.. s.Hardware.Gpus.Value.Select(g => g.IsIntegrated ? g with { IsBasicDisplayAdapter = true, DriverVersion = null } : g)]) } });
        Add(L("usb-optical-drive", "usb-optical-drive-restart", ["freeze_hard"], accept: ["whole-pc-freeze-kernel-power-41"]),
            s => s with { Devices = K<IReadOnlyList<DeviceInfo>>([.. s.Devices.Value, new DeviceInfo { InstanceId = "USBSTOR\\CDROM&VEN_ASUS&PROD_SDRW-08D2S-U\\7&1A2B3C&0", ClassGuid = "{4d36e965-e325-11ce-bfc1-08002be10318}", ClassName = "CDROM", Enumerator = "USBSTOR", Present = true, Enabled = true }]) });
        Add(L("amd-driver-tdr", "amd-adrenalin-driver-regression", ["crash_desktop"], accept: ["gpu-driver-corrupt-needs-clean-install"]),
            s => Hits(AmdGpu(s), Hit("display_tdr", "Display", 4101, 5)) with
            {
                Riot = s.Riot with { ShooterLog = K(s.Riot.ShooterLog.Value with { D3dDeviceLostCount = 2 }) },
            });

        // ---------------------------------------------------------------- Vanguard requirements (BIOS)
        Add(L("secureboot-off-mbr", "secure-boot-not-detected-van9003", ["error_code"], codes: ["VAN 9003"], accept: ["secure-boot-enable-boot-failure"]),
            s => s with
            {
                Firmware = s.Firmware with { Type = K(FirmwareType.Bios) },
                Security = s.Security with { SecureBootEnabled = K(false) },
                Storage = s.Storage with { SystemDiskPartitionStyle = K(PartitionStyle.Mbr) },
            });
        Add(L("secureboot-setup-mode", "secure-boot-not-detected-van9003", ["bios_requirement"], accept: ["secure-boot-enable-boot-failure"]),
            s => s with
            {
                Security = s.Security with { SecureBootEnabled = K(false), SecureBootSetupMode = K(true) },
                Riot = s.Riot with { DetectedCodes = K<IReadOnlyList<DetectedCode>>([new DetectedCode { Code = "VAN 9003", Source = CodeSource.RiotClientLog, Count = 2 }]) },
            });
        Add(L("tpm-off", "tpm2-missing-or-unmet", ["error_code"], codes: ["VAN 9001"]),
            s => s with { Security = s.Security with { Tpm = K(new TpmInfo { Present = false }) } });
        Add(L("hvci-off-virt-off", "hvci-memory-integrity-required", ["bios_requirement"], codes: ["VAN: RESTRICTION: 5"]),
            s => s with
            {
                Security = s.Security with
                {
                    Vbs = K(new VbsInfo { Status = VbsStatus.Off, HvciConfigured = false, HvciRunning = false, AvailableSecurityProperties = [1, 2, 3] }),
                    VirtualizationFirmwareEnabled = K(false),
                    HypervisorPresent = K(false),
                },
            });
        Add(L("iommu-off", "iommu-restriction", ["bios_requirement"], codes: ["VAN RESTRICTION"]),
            s => s with { Security = s.Security with { KernelDmaProtection = K(false), Vbs = K(s.Security.Vbs.Value with { AvailableSecurityProperties = [1, 2, 5] }) } });
        Add(L("windows-too-old", "windows-version-too-old", ["error_code"], codes: ["VAN 9004"]),
            s => s with { Os = s.Os with { Build = K(19044), DisplayVersion = K("21H2"), ProductName = K("Windows 10 Pro") } });
        Add(L("insider-vgk-bsod", "insider-build-vgk-bsod", ["freeze_hard"], accept: ["outdated-bios-reboot-vgk-bsod"]),
            s => s with
            {
                Os = s.Os with { InsiderBranch = K("CanaryChannel") },
                Events = s.Events with { Minidumps = K<IReadOnlyList<MinidumpMeta>>([new MinidumpMeta { FileName = "102126-9001-01.dmp", ModifiedUtc = Created.AddDays(-1), SizeBytes = 900_000, BugCheckCode = "0x0000007e", FaultingDriver = "vgk.sys" }]) },
            });
        Add(L("intel-vmd", "intel-vmd-status-vmd-9001", ["error_code"]),
            s => s with
            {
                Storage = s.Storage with { Controllers = K<IReadOnlyList<StorageControllerInfo>>([new StorageControllerInfo { InstanceId = "PCI\\VEN_8086&DEV_467F&SUBSYS_0B1A1028&REV_00\\3&11583659&0&E0", Name = "Intel RST VMD Controller 467F", DriverFile = "iaStorVD.sys", IsVmd = true, IsBootController = true }]) },
                Riot = s.Riot with { DetectedCodes = K<IReadOnlyList<DetectedCode>>([new DetectedCode { Code = "VAN: STATUS_VMD_9001", Source = CodeSource.RiotClientLog }]) },
            });
        Add(L("dual-boot-van1", "dual-boot-secure-boot-van1", ["error_code"], codes: ["VAN 1"]),
            s => s with { Firmware = s.Firmware with { OtherOsBootEntries = K(1) } });

        // ---------------------------------------------------------------- network
        Add(L("isp-routing", "isp-routing-high-ping", ["high_ping"]),
            s => Probes(s, new PingProbe { Target = "gateway", Kind = ProbeTargetKind.Gateway, Sent = 10, Received = 10, RttAvgMs = 2.0, JitterMs = 0.5 },
                           new PingProbe { Target = "eu-central", Kind = ProbeTargetKind.Region, Sent = 10, Received = 9, RttAvgMs = 142.0, JitterMs = 24.0 }));
        Add(L("weak-wifi-loss", "local-network-packet-loss-jitter", ["high_ping"]),
            s => s with
            {
                Network = s.Network with
                {
                    Probes = K<IReadOnlyList<PingProbe>>(
                    [
                        new PingProbe { Target = "gateway", Kind = ProbeTargetKind.Gateway, Sent = 10, Received = 8, RttAvgMs = 18.0, JitterMs = 22.0 },
                        new PingProbe { Target = "eu-central", Kind = ProbeTargetKind.Region, Sent = 10, Received = 8, RttAvgMs = 70.0, JitterMs = 25.0 },
                    ]),
                    Wlan = K(new WlanInfo { Connected = true, SignalQualityPercent = 34, RssiDbm = -78, Band = WifiBand.Ghz24, PhyType = "802.11n", LinkSpeedMbps = 72 }),
                },
            });
        Add(L("dual-nic-desync", "dual-network-adapter-desync", ["disconnect"], codes: ["VAN -72"]),
            s => WithRealtek(s, enabled: true, defaultRoute: true, errors: 0, wifiUp: true, wifiOnline: true, realtekModel: "Intel(R) Ethernet Controller I225-V", realtekDriver: "e2f68.sys"));
        Add(L("vpn-wireguard", "vpn-anticheat-network-filter-conflict", ["disconnect"], codes: ["VAN 289"], accept: ["conflict-vpn_wireguard", "conflict-vpn_generic"]),
            s => Procs(Apps(s, ("WireGuard", "WireGuard LLC")), "wireguard.exe") with
            {
                Network = s.Network with { Adapters = K<IReadOnlyList<NetAdapter>>([.. s.Network.Adapters.Value, new NetAdapter { InstanceId = "SWD\\WireGuard\\{5B8C3E1A-0000-4000-8000-000000000001}", Description = "WireGuard Tunnel", Kind = AdapterKind.Tunnel, Physical = false, Enabled = true, Status = AdapterOperStatus.Up, IsDefaultRoute = false, HasInternet = true }]) },
                Drivers = K<IReadOnlyList<LoadedDriver>>([.. s.Drivers.Value, new LoadedDriver { Name = "wireguard.sys" }]),
            });
        Add(L("firewall-blocks-riot", "van185-vanguard-disconnects", ["disconnect"], codes: ["VAN 185"], accept: ["conflict-defender_firewall_block", "network-exceptions-download-stuck"]),
            s => s with { Network = s.Network with { RiotFirewallRules = K<IReadOnlyList<FirewallRuleInfo>>([new FirewallRuleInfo { Name = "Block VALORANT", ProgramImageName = "VALORANT-Win64-Shipping.exe", Direction = FirewallDirection.Out, Action = FirewallAction.Block, Enabled = true }]) } });
        Add(L("clock-wrong", "wrong-system-clock-offline", ["login_issue"], codes: ["VAN -104"]),
            s => s with { Os = s.Os with { Clock = K(new ClockInfo { SkewSeconds = -5400, ReferenceSource = "https-date", TimeZoneId = "Turkey Standard Time", AutoTimeSync = false }) } });

        // ---------------------------------------------------------------- audio, input
        Add(L("bt-handsfree", "bluetooth-headset-hfp-voice-chat", ["voice_chat"], answers: A(("ask.uses_bluetooth_headset", "yes"))),
            s => s with
            {
                Audio = new AudioState
                {
                    Endpoints = K<IReadOnlyList<AudioEndpoint>>(
                    [
                        .. s.Audio.Endpoints.Value.Select(e => e with { IsDefaultCommunications = false }),
                        new AudioEndpoint { Id = "{0.0.0.00000000}.{aa11bb22-0000-4000-8000-000000000001}", Name = "Headset (WH-1000XM4 Hands-Free)", Flow = AudioFlow.Render, State = AudioEndpointState.Active, FormFactor = AudioFormFactor.Headset, IsDefaultCommunications = true, IsBluetoothHandsFree = true, DeviceInstanceId = "BTHHFENUM\\BTHHFPAUDIO\\8&2B1C3D4E&0&97" },
                        new AudioEndpoint { Id = "{0.0.1.00000000}.{aa11bb22-0000-4000-8000-000000000002}", Name = "Headset (WH-1000XM4 Hands-Free)", Flow = AudioFlow.Capture, State = AudioEndpointState.Active, FormFactor = AudioFormFactor.Headset, IsDefault = true, IsDefaultCommunications = true, IsBluetoothHandsFree = true, DeviceInstanceId = "BTHHFENUM\\BTHHFPAUDIO\\8&2B1C3D4E&0&97" },
                    ]),
                },
            });
        Add(L("mouse-acceleration", "mouse-acceleration-floaty-aim", ["mouse_input"]),
            s => s with { Input = new InputState { Mouse = K(new MouseSettings { EnhancePointerPrecision = true, Speed = 10 }) } });

        // ---------------------------------------------------------------- other
        Add(L("third-party-av", "third-party-antivirus-conflict", ["install_update"], accept: ["conflict-av_norton", "vanguard-install-loop"]),
            s => Procs(Apps(s, ("Norton 360", "Gen Digital Inc.")), "NortonSvc.exe", "NortonUI.exe") with
            {
                Security = s.Security with { AntivirusProducts = K<IReadOnlyList<AvProduct>>([new AvProduct { Name = "Norton 360", Enabled = true, UpToDate = true }, new AvProduct { Name = "Windows Defender", Enabled = false, IsDefender = true }]) },
            });
        Add(L("hyperv-van138", "conflict-hyperv_hypervisor", ["error_code"], codes: ["VAN 138"]),
            s => Svc(s, Service("vmms", ServiceStartType.Automatic, ServiceState.Running), Service("vmcompute", ServiceStartType.Manual, ServiceState.Running))
                 with { OptionalFeatures = K<IReadOnlyList<string>>(["Microsoft-Hyper-V-All", "Microsoft-Windows-Subsystem-Linux"]) });
        Add(L("monitor-on-igpu", "wrong-gpu-or-display-output", ["fps_low"], accept: ["low-gpu-usage-after-ue5-patch"]),
            s => s with { Display = s.Display with { Monitors = K<IReadOnlyList<MonitorInfo>>([s.Display.Monitors.Value[0] with { GpuInstanceId = s.Hardware.Gpus.Value.First(g => g.IsIntegrated).InstanceId }]) } });

        // ---------------------------------------------------------------- adversarial (independent review): noise, stale logs, mixes
        Add(L("stale-van152-secureboot-off", "secure-boot-not-detected-van9003", ["error_code"], codes: ["VAN 9003"], accept: ["secure-boot-enable-boot-failure"]),
            s => s with
            {
                Security = s.Security with { SecureBootEnabled = K(false) },
                Riot = s.Riot with { DetectedCodes = K<IReadOnlyList<DetectedCode>>([new DetectedCode { Code = "VAN 152", Source = CodeSource.RiotClientLog, Count = 1, FirstUtc = Created.AddDays(-121), LastUtc = Created.AddDays(-120) }]) },
            });
        Add(L("precheck-normal-state", null, ["wont_launch"], answers: A(("ask.vanguard_precheck_on", "no")), unknown: true),
            s => s with
            {
                Riot = s.Riot with
                {
                    Vanguard = K(s.Riot.Vanguard.Value with { Logs = s.Riot.Vanguard.Value.Logs! with { NewestUtc = Created.AddDays(-8), CountLast24Hours = 0, CountLastHour = 0 } }),
                    ClientLogs = K(new RiotClientLogDigest { FileCount = 6, NewestUtc = Created.AddDays(-8) }),
                },
            });
        Add(L("mixed-nahimic-rtss-vpn-faceit", "nahimic-audio-service-launch-hang", ["launch_black_screen"],
              accept: ["third-party-hook-overlay-conflict", "conflict-rtss", "conflict-ac_faceit", "conflict-vpn_nordvpn", "vpn-anticheat-network-filter-conflict"]),
            s => Svc(Procs(Apps(s, ("Nahimic", "A-Volute"), ("NordVPN", "Nord Security"), ("FACEIT", "FACEIT")), "NahimicSvc64.exe", "RTSS.exe", "NordVPN.exe"),
                     Service("NahimicService", ServiceStartType.Automatic, ServiceState.Running), Service("nordvpn-service", ServiceStartType.Automatic, ServiceState.Running),
                     Service("FACEITService", ServiceStartType.Automatic, ServiceState.Running))
                 with { Drivers = K<IReadOnlyList<LoadedDriver>>([.. s.Drivers.Value, new LoadedDriver { Name = "FACEIT.sys" }]) });
        Add(L("limited-admin-unknowns", null, ["error_code"], codes: ["VAN 57"], unknown: true),
            s => s with
            {
                Meta = s.Meta with { Privilege = PrivilegeMode.Limited },
                Drivers = Fact<IReadOnlyList<LoadedDriver>>.Unknown(UnknownReason.NeedsAdmin, "not elevated"),
                Services = Fact<IReadOnlyList<ServiceInfo>>.Unknown(UnknownReason.NeedsAdmin, "not elevated"),
                Riot = s.Riot with { Vanguard = K(s.Riot.Vanguard.Value with { Vgk = s.Riot.Vanguard.Value.Vgk! with { State = ServiceState.Stopped } }) },
            });
        Add(L("idle-realtek-no-errors-fps", "realtek-2-5gbe-nic-stutter", ["fps_low"]), s => WithRealtek(s, enabled: true, defaultRoute: false, errors: 0));

        // ---------------------------------------------------------------- routes and honest unknowns
        Add(L("vm-route", null, ["wont_launch"], route: "unsupportedPlatform"), s => s with { Os = s.Os with { IsVirtualMachine = K(true) } });
        Add(L("hwid-ban", null, ["error_code"], codes: ["VAN 152"], route: "hwidBanAppeal"), s => s);
        Add(L("account-side-val57", null, ["login_issue"], codes: ["VAL 57"], answers: A(("ask.other_account_works", "yes")), route: "tryAnotherAccount"), s => s);
        Add(L("healthy-no-symptoms", null, [], unknown: true), s => s);
        Add(L("healthy-but-stutters", null, ["stutter"], unknown: true), s => s);
        Add(L("healthy-crash-unknown-module", null, ["crash_desktop"], unknown: true),
            s => s with { Events = s.Events with { AppCrashes = K<IReadOnlyList<AppCrash>>([Crash("btmmhook.dll", -1)]) } });
        return list;
    }

    // ---------------------------------------------------------------- building blocks
    private static FixtureLabel L(string name, string? top, string[] symptoms, string[]? codes = null, Dictionary<string, string>? answers = null,
        string[]? accept = null, string? route = null, bool unknown = false)
        => new()
        {
            Name = name, ExpectedTop = top, Symptoms = symptoms, TypedCodes = codes ?? [], Answers = answers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Accept = accept ?? [], ExpectedRoute = route, ExpectUnknownLeading = unknown,
        };

    private static Dictionary<string, string> A(params (string Q, string O)[] answers) => answers.ToDictionary(a => a.Q, a => a.O, StringComparer.Ordinal);

    private static Fact<T> K<T>(T value) => Fact<T>.Known(value, "fixture/generated");

    private static ScanSnapshot Base(string id)
    {
        var s = FixtureSnapshots.HealthyDesktop(id, Created);
        return s with
        {
            Meta = s.Meta with { CaptureId = "generated" },
            Apps = K<IReadOnlyList<InstalledApp>>(
            [
                .. s.Apps.Value,
                new InstalledApp { DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", Publisher = "Microsoft Corporation", Version = "14.44.35211.0", Hive = AppHive.Hklm, KeyName = "{0d3e9e15-de7a-300b-96f1-b4af12b96488}" },
                new InstalledApp { DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", Publisher = "Microsoft Corporation", Version = "14.44.35211.0", Hive = AppHive.Hklm32, KeyName = "{1d3e9e15-de7a-300b-96f1-b4af12b96488}" },
            ]),
        };
    }

    private static ScanSnapshot WithRealtek(ScanSnapshot s, bool enabled, bool defaultRoute, int errors, bool wifiUp = true, bool wifiOnline = true,
        string realtekModel = "Realtek Gaming 2.5GbE Family Controller", string realtekDriver = "rt25cx21x64.sys")
    {
        var nic = new NetAdapter
        {
            InstanceId = RealtekId, Description = realtekModel, Kind = AdapterKind.Ethernet, Physical = true, Enabled = enabled,
            Status = defaultRoute ? AdapterOperStatus.Up : AdapterOperStatus.LowerLayerDown, LinkSpeedBps = defaultRoute ? 2_500_000_000 : 0,
            IsDefaultRoute = defaultRoute, HasInternet = defaultRoute, Ipv4Metric = 25, AutomaticMetric = true, Ipv6Bound = true, DriverFile = realtekDriver,
            DriverVersion = "10.68.815.2023", DriverDate = new DateOnly(2023, 8, 15), DriverProvider = "Realtek",
            AdvancedProperties = new Dictionary<string, string>(StringComparer.Ordinal) { ["*EEE"] = "1", ["EnableGreenEthernet"] = "1" },
        };
        var wifi = s.Network.Adapters.Value.First(a => a.InstanceId == WifiId);
        wifi = wifi with { IsDefaultRoute = !defaultRoute, Status = wifiUp ? AdapterOperStatus.Up : AdapterOperStatus.Down, HasInternet = wifiOnline && wifiUp };
        var result = s with
        {
            Network = s.Network with { Adapters = K<IReadOnlyList<NetAdapter>>([nic, wifi]) },
            Drivers = K<IReadOnlyList<LoadedDriver>>([.. s.Drivers.Value, new LoadedDriver { Name = realtekDriver }]),
        };
        return errors > 0 ? Hits(result, Hit("realtek_nic_errors", "rt25cx21", 5, errors)) : result;
    }

    private static ScanSnapshot Laptop(ScanSnapshot s) => s with
    {
        Hardware = s.Hardware with { Board = K(s.Hardware.Board.Value with { IsLaptop = true, SystemProduct = "Laptop 15" }) },
    };

    private static ScanSnapshot AmdGpu(ScanSnapshot s) => s with
    {
        Hardware = s.Hardware with
        {
            Gpus = K<IReadOnlyList<GpuInfo>>(
            [
                new GpuInfo { InstanceId = "PCI\\VEN_1002&DEV_7550&SUBSYS_24161002&REV_C0\\6&1A2B3C4D&0&00000019", Name = "AMD Radeon RX 9070 XT", Vendor = GpuVendor.Amd, PciVendorId = "1002", IsIntegrated = false, Enabled = true, DriverVersion = "32.0.21030.2001", DriverDate = new DateOnly(2026, 9, 30), DriverProvider = "Advanced Micro Devices, Inc." },
                .. s.Hardware.Gpus.Value.Where(g => g.IsIntegrated),
            ]),
        },
    };

    private static ScanSnapshot WithVgc(ScanSnapshot s, ServiceStartType start, ServiceState state)
    {
        var vg = s.Riot.Vanguard.Value;
        var vgc = vg.Vgc! with { StartType = start, State = state };
        return s with
        {
            Riot = s.Riot with { Vanguard = K(vg with { Vgc = vgc, Logs = vg.Logs! with { NewestUtc = Created.AddDays(-12), CountLast24Hours = 0, CountLastHour = 0 } }) },
            Services = K<IReadOnlyList<ServiceInfo>>([.. s.Services.Value.Select(x => x.Name == "vgc" ? vgc : x)]),
        };
    }

    private static ScanSnapshot VgFiles(ScanSnapshot s, params (string Name, string Location, bool Exists)[] files)
    {
        var vg = s.Riot.Vanguard.Value;
        var list = vg.Files.Where(f => !files.Any(x => x.Name == f.Name)).Concat(files.Select(f => new RiotFileInfo { Name = f.Name, Location = f.Location, Exists = f.Exists, SizeBytes = f.Exists ? 4096 : null })).ToList();
        return s with { Riot = s.Riot with { Vanguard = K(vg with { Files = list }) } };
    }

    private static ScanSnapshot Procs(ScanSnapshot s, params string[] names)
        => s with { Processes = K<IReadOnlyList<ProcessInfo>>([.. s.Processes.Value, .. names.Select((n, i) => new ProcessInfo { Name = n, ProcessId = 20_000 + (i * 4), ParentProcessId = 7412 })]) };

    private static ScanSnapshot Apps(ScanSnapshot s, params (string Name, string Publisher)[] apps)
        => s with { Apps = K<IReadOnlyList<InstalledApp>>([.. s.Apps.Value, .. apps.Select((a, i) => new InstalledApp { DisplayName = a.Name, Publisher = a.Publisher, Hive = AppHive.Hklm, KeyName = "{GEN-APP-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" })]) };

    private static ScanSnapshot Svc(ScanSnapshot s, params ServiceInfo[] services)
        => s with { Services = K<IReadOnlyList<ServiceInfo>>([.. s.Services.Value, .. services]) };

    private static ServiceInfo Service(string name, ServiceStartType start, ServiceState state)
        => new() { Name = name, DisplayName = name, Kind = ServiceKind.Win32, StartType = start, State = state };

    private static AppCrash Crash(string module, int daysAgo, string exe = "VALORANT-Win64-Shipping.exe")
        => new() { Exe = exe, Module = module, ExceptionCode = "0xc0000005", Kind = AppCrashKind.Crash, Utc = Created.AddDays(daysAgo) };

    private static EventHit Hit(string query, string provider, int id, int count)
        => new() { QueryId = query, Log = "System", Provider = provider, EventId = id, Count = count, FirstUtc = Created.AddDays(-10), LastUtc = Created.AddHours(-3) };

    private static ScanSnapshot Hits(ScanSnapshot s, params EventHit[] hits)
        => s with { Events = s.Events with { Hits = K<IReadOnlyList<EventHit>>([.. s.Events.Hits.Value, .. hits]) } };

    private static ScanSnapshot Probes(ScanSnapshot s, params PingProbe[] probes)
        => s with { Network = s.Network with { Probes = K<IReadOnlyList<PingProbe>>(probes) } };
}
