using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Policy;

/// <summary>
/// ProtectedTargetPolicy (design_B.md section 13.1 S4/S5, critique section 4 v0.2): the targets no fix, KB pack, helper
/// plan or agent may touch, whatever they say. Every primitive calls it from <see cref="IOperation.CheckPolicyAsync"/>.
/// Facts it needs are read live through the ports; a fact that cannot be read DENIES (Unknown never allows).
/// The lists are code, reviewed with the code, and unit-tested as a deny matrix.
/// </summary>
public sealed class ProtectedTargetPolicy
{
    /// <summary>Net setup class.</summary>
    public const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Bluetooth setup class.</summary>
    public const string BluetoothClass = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";

    /// <summary>The VTDiag firewall rule group.</summary>
    public const string FirewallGroup = "VTDiag";

    private readonly SystemPorts _ports;
    private readonly IFixSessionContext _session;

    /// <summary>Creates the policy.</summary>
    /// <param name="ports">System ports.</param>
    /// <param name="session">Session context.</param>
    public ProtectedTargetPolicy(SystemPorts ports, IFixSessionContext session)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Services whose configuration or state no fix may change (vgc has one gated exception, see <see cref="CheckService"/>).</summary>
    public static IReadOnlyList<string> ProtectedServices { get; } =
    [
        "vgc", "vgk", "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "mpssvc",
        "mpsdrv", "BFE", "EventLog", "Winmgmt", "TBS", "Dnscache", "wuauserv", "UsoSvc", "WaaSMedicSvc", "TrustedInstaller", "Schedule",
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "PlugPlay", "Power", "ProfSvc", "CryptSvc", "Dhcp", "nsi", "NlaSvc",
        "netprofm", "BrokerInfrastructure", "SystemEventsBroker", "CoreMessagingRegistrar", "gpsvc", "Appinfo", "AudioSrv",
        "AudioEndpointBuilder", "W32Time", "KeyIso", "VaultSvc", "BITS", "msiserver", "AppIDSvc", "EFS", "Netlogon", "LanmanWorkstation",
    ];

    /// <summary>Remote-access services no fix may start.</summary>
    public static IReadOnlyList<string> RemoteAccessServices { get; } =
        ["RemoteRegistry", "TermService", "SessionEnv", "UmRdpService", "WinRM", "sshd", "TlntSvr", "RemoteAccess", "SNMP", "RasMan"];

    /// <summary>Setup classes a device fix never disables (boot/storage chain, display, input, platform, security devices).</summary>
    public static IReadOnlyList<string> ProtectedDeviceClasses { get; } =
    [
        "{4d36e97d-e325-11ce-bfc1-08002be10318}", // System
        "{4d36e966-e325-11ce-bfc1-08002be10318}", // Computer
        "{50127dc3-0f36-415e-a6cc-4cb3be910b65}", // Processor
        "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}", // Firmware
        "{4d36e97b-e325-11ce-bfc1-08002be10318}", // SCSIAdapter (NVMe, RAID, VMD)
        "{4d36e96a-e325-11ce-bfc1-08002be10318}", // HDC (SATA/AHCI)
        "{4d36e967-e325-11ce-bfc1-08002be10318}", // DiskDrive
        "{71a27cdd-812a-11d0-bec7-08002be2092f}", // Volume
        "{533c5b84-ec70-11d2-9505-00c04f79deaf}", // VolumeSnapshot
        "{36fc9e60-c465-11cf-8056-444553540000}", // USB controllers and hubs
        "{4d36e968-e325-11ce-bfc1-08002be10318}", // Display
        "{4d36e96e-e325-11ce-bfc1-08002be10318}", // Monitor
        "{4d36e96b-e325-11ce-bfc1-08002be10318}", // Keyboard
        "{4d36e96f-e325-11ce-bfc1-08002be10318}", // Mouse
        "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", // HIDClass
        "{72631e54-78a4-11d0-bcf7-00aa00b7b32a}", // Battery
        "{d94ee5d8-d189-4994-83d2-f68d7d41b0e6}", // SecurityDevices (TPM)
    ];

    /// <summary>Classes whose devices (and every ancestor of them) are protected: disks, display, keyboard, mouse, HID.</summary>
    public static IReadOnlyList<string> AnchorDeviceClasses { get; } =
    [
        "{4d36e967-e325-11ce-bfc1-08002be10318}", "{4d36e968-e325-11ce-bfc1-08002be10318}", "{4d36e96b-e325-11ce-bfc1-08002be10318}",
        "{4d36e96f-e325-11ce-bfc1-08002be10318}", "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}",
    ];

    /// <summary>Windows processes no fix may close.</summary>
    public static IReadOnlyList<string> SystemProcesses { get; } =
    [
        "System", "Registry", "Idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "LsaIso.exe",
        "svchost.exe", "fontdrvhost.exe", "dwm.exe", "explorer.exe", "sihost.exe", "taskhostw.exe", "ctfmon.exe", "RuntimeBroker.exe",
        "SearchHost.exe", "SearchIndexer.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe", "TextInputHost.exe", "conhost.exe",
        "dllhost.exe", "audiodg.exe", "spoolsv.exe", "MsMpEng.exe", "NisSrv.exe", "SecurityHealthService.exe", "SecurityHealthSystray.exe",
        "smartscreen.exe", "MemCompression", "WUDFHost.exe", "dasHost.exe", "SgrmBroker.exe", "WmiPrvSE.exe", "TrustedInstaller.exe",
        "TiWorker.exe", "msiexec.exe", "ApplicationFrameHost.exe", "LockApp.exe", "LogonUI.exe", "userinit.exe", "consent.exe", "sppsvc.exe",
        "SystemSettings.exe", "MpDefenderCoreService.exe", "wlanext.exe", "unsecapp.exe", "dashost.exe", "Taskmgr.exe",
    ];

    /// <summary>AppCompat layer tokens a fix may ADD (removal is unrestricted).</summary>
    public static IReadOnlyList<string> AddableCompatTokens { get; } = ["~", "RUNASADMIN", "DISABLEDXMAXIMIZEDWINDOWEDMODE", "HIGHDPIAWARE", "DPIUNAWARE", "GDIDPISCALING"];

    /// <summary>Adapter advanced-property keywords a fix may set (power saving, wake, latency; never identity, VLAN or offload).</summary>
    public static IReadOnlyList<string> AdapterKeywords { get; } =
    [
        "*EEE", "EEE", "EnableGreenEthernet", "AdvancedEEE", "GigaLite", "PowerSavingMode", "EnablePME", "*SelectiveSuspend",
        "*DeviceSleepOnDisconnect", "AutoDisableGigabit", "S5WakeOnLan", "WakeOnLink", "*WakeOnMagicPacket", "*WakeOnPattern",
        "*InterruptModeration", "*FlowControl", "EnableSavePowerNow", "ULPMode", "*PMARPOffload", "*PMNSOffload", "ReduceSpeedOnPowerDown",
        "EnableDynamicPowerGating", "EnableConnectedPowerGating", "AutoPowerSaveModeEnabled", "*ModernStandbyWoLMagicPacket",
    ];

    /// <summary>Power settings a fix may write, with the forward value range (subgroup, setting, min, max).</summary>
    public static IReadOnlyList<(Guid Subgroup, Guid Setting, uint Min, uint Max)> PowerSettings { get; } =
    [
        (new("54533251-82be-4824-96c1-47b60b740d00"), new("bc5038f7-23e0-4960-96da-33abaf5935ec"), 100, 100), // processor max state
        (new("54533251-82be-4824-96c1-47b60b740d00"), new("893dee8e-2bef-41e0-89c6-b55d0929964c"), 5, 100),   // processor min state
        (new("54533251-82be-4824-96c1-47b60b740d00"), new("0cc5b647-c1df-4637-891a-dec35c318583"), 0, 100),   // core parking min cores
        (new("2a737441-1930-4402-8d77-b2bebba308a3"), new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), 0, 1),     // USB selective suspend
        (new("501a4d13-42af-4429-9fd1-a8218c268e20"), new("ee12f906-d277-404b-b6da-e5fa1a576df5"), 0, 2),     // PCIe link state PM
    ];

    /// <summary>Built-in schemes a fix may activate (they must also exist on the PC).</summary>
    public static IReadOnlyList<Guid> ActivatableSchemes { get; } =
    [
        new("381b4222-f694-41f0-9685-ff5bb260df2e"), new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        new("e9a42b02-d5df-448d-aa00-03f14749eb61"), new("a1841308-3541-4fab-bc81-f71556f20b4a"),
    ];

    /// <summary>The only direction each BCD element may be set (towards the secure state).</summary>
    public static IReadOnlyDictionary<string, string> SecureBcdValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["testsigning"] = "off",
        ["nointegritychecks"] = "off",
        ["nx"] = "OptIn",
        ["hypervisorlaunchtype"] = "Auto",
    };

    /// <summary>Minimum age of <c>392667600.dat</c> before the current boot for it to count as stale.</summary>
    public static TimeSpan StaleDatMargin { get; } = TimeSpan.FromMinutes(2);

    /// <summary>An allow decision.</summary>
    public static PolicyDecision Allow => PolicyDecision.Allow;

    /// <summary>A deny decision.</summary>
    /// <param name="rule">Rule id suffix (for example <c>service.vgc</c>).</param>
    public static PolicyDecision Deny(string rule) => new() { Allowed = false, RuleId = "protected." + rule, ReasonKey = "policy." + rule.Replace('.', '_') };

    // ------------------------------------------------------------------ services

    /// <summary>Policy for <c>service.setStartType</c>, <c>service.stop</c> and <c>service.start</c>.</summary>
    /// <param name="op">Primitive id.</param>
    /// <param name="name">Service name.</param>
    /// <param name="startType">Requested start type for <c>service.setStartType</c>.</param>
    public PolicyDecision CheckService(string op, string name, string? startType)
    {
        var isVgc = string.Equals(name, "vgc", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(name, "vgk", StringComparison.OrdinalIgnoreCase))
        {
            return Deny("service.vgk");
        }

        if (isVgc)
        {
            return op switch
            {
                OpIds.ServiceStart => Allow,
                OpIds.ServiceSetStartType when string.Equals(startType, "Automatic", StringComparison.Ordinal)
                    => _session.VgcAutomaticConfirmed == Tri.True ? Allow : Deny("service.vgc_precheck"),
                _ => Deny("service.vgc"),
            };
        }

        if (op == OpIds.ServiceStart)
        {
            return RemoteAccessServices.Contains(name, StringComparer.OrdinalIgnoreCase) ? Deny("service.remote_access") : Allow;
        }

        if (ProtectedServices.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return Deny("service.protected");
        }

        ServiceInfo? info;
        try
        {
            info = _ports.Services.Query(name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("service.unverifiable");
        }

        if (info is null)
        {
            return Allow; // capture reports exists=false and the preview blocks with target.missing
        }

        if (info.StartType is ServiceStart.Boot or ServiceStart.System)
        {
            return Deny("service.boot_start");
        }

        if (info.IsSharedProcess)
        {
            return Deny("service.shared_host");
        }

        return info.LaunchProtected ? Deny("service.launch_protected") : Allow;
    }

    // ------------------------------------------------------------------ devices

    /// <summary>Policy for <c>device.disable</c>: protected classes, the default-route NIC, and every ancestor of a disk,
    /// display, keyboard, mouse, HID device or default-route NIC.</summary>
    /// <param name="instanceId">Instance id.</param>
    public PolicyDecision CheckDeviceDisable(string instanceId)
    {
        DeviceInfo? target;
        IReadOnlyList<DeviceInfo> all;
        try
        {
            target = _ports.Devices.Query(instanceId);
            all = _ports.Devices.EnumeratePresent();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("device.unverifiable");
        }

        if (target is null)
        {
            return Allow; // capture reports present=false and the preview blocks
        }

        var cls = target.ClassGuid;
        if (cls is null)
        {
            return Deny("device.unverifiable");
        }

        if (ProtectedDeviceClasses.Contains(cls, StringComparer.OrdinalIgnoreCase))
        {
            return Deny("device.class");
        }

        if (string.Equals(cls, BluetoothClass, StringComparison.OrdinalIgnoreCase)
            && !instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
        {
            return Deny("device.bluetooth_radio");
        }

        var anchors = new List<DeviceInfo>();
        foreach (var d in all)
        {
            if (d.ClassGuid is { } c && AnchorDeviceClasses.Contains(c, StringComparer.OrdinalIgnoreCase))
            {
                anchors.Add(d);
            }
            else if (d.ClassGuid is { } n && string.Equals(n, NetClass, StringComparison.OrdinalIgnoreCase))
            {
                var route = DefaultRoute(d.InstanceId);
                if (route != Tri.False)
                {
                    anchors.Add(d); // a default-route NIC, or one we cannot check, protects itself and its parents
                }
            }
        }

        var byId = all.Where(d => !string.IsNullOrEmpty(d.InstanceId)).GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in anchors)
        {
            var current = anchor;
            for (var depth = 0; current is not null && depth < 64; depth++)
            {
                if (string.Equals(current.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(cls, NetClass, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(current, anchor)
                        ? Deny("default_route_nic")
                        : Deny("device.ancestor");
                }

                current = current.ParentInstanceId is { } p && byId.TryGetValue(p, out var parent) ? parent : null;
            }
        }

        if (string.Equals(cls, NetClass, StringComparison.OrdinalIgnoreCase) && DefaultRoute(instanceId) != Tri.False)
        {
            return Deny("default_route_nic");
        }

        return Allow;
    }

    /// <summary>Policy for the adapter primitives: the target must be a network adapter (Net class).</summary>
    /// <param name="instanceId">Instance id.</param>
    public PolicyDecision CheckNetAdapter(string instanceId)
    {
        try
        {
            var d = _ports.Devices.Query(instanceId);
            if (d is null)
            {
                return Allow; // capture fails and the preview blocks
            }

            return d.ClassGuid is { } c && string.Equals(c, NetClass, StringComparison.OrdinalIgnoreCase) ? Allow : Deny("netadapter.not_network");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("netadapter.unverifiable");
        }
    }

    private Tri DefaultRoute(string instanceId)
    {
        try
        {
            var guid = _ports.Network.InterfaceGuidOf(instanceId);
            return guid is null ? Tri.Unknown : _ports.Network.CarriesDefaultRoute(guid.Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Tri.Unknown;
        }
    }

    // ------------------------------------------------------------------ registry

    /// <summary>Policy for <c>registry.setValue</c> (kind and data given) and <c>registry.deleteValue</c> (both null).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    /// <param name="name">Value name.</param>
    /// <param name="kind">Kind for a set.</param>
    /// <param name="data">Data for a set.</param>
    public PolicyDecision CheckRegistry(RegHive hive, string subKey, string name, string? kind, string? data)
    {
        if (hive == RegHive.User && _session.Player is null)
        {
            return Deny("player_unknown");
        }

        if (RegistryAllowlist.SecurityDenied(subKey))
        {
            return Deny("registry.security");
        }

        var entry = RegistryAllowlist.Find(hive, subKey, name);
        if (entry is null)
        {
            return Deny("registry.not_allowlisted");
        }

        if (kind is null)
        {
            return entry.AllowDelete ? Allow : Deny("registry.delete_not_allowed");
        }

        if (!entry.Kinds.Contains(kind, StringComparer.Ordinal) || data is null || !RegistryAllowlist.FullMatch(entry.DataPattern, data))
        {
            return Deny("registry.data");
        }

        return Allow;
    }

    // ------------------------------------------------------------------ power

    /// <summary>Policy for <c>powercfg.setValue</c>.</summary>
    /// <param name="subgroup">Subgroup.</param>
    /// <param name="setting">Setting.</param>
    /// <param name="values">AC/DC values requested.</param>
    public static PolicyDecision CheckPowerValue(Guid subgroup, Guid setting, IEnumerable<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var entry = PowerSettings.FirstOrDefault(p => p.Subgroup == subgroup && p.Setting == setting);
        if (entry.Subgroup == Guid.Empty)
        {
            return Deny("power.setting");
        }

        return values.All(v => v >= entry.Min && v <= entry.Max) ? Allow : Deny("power.value");
    }

    /// <summary>Policy for <c>powercfg.setActiveScheme</c>.</summary>
    /// <param name="scheme">Scheme.</param>
    public PolicyDecision CheckActiveScheme(Guid scheme)
    {
        if (!ActivatableSchemes.Contains(scheme))
        {
            return Deny("power.scheme");
        }

        try
        {
            return _ports.Power.EnumerateSchemes().Contains(scheme) ? Allow : Deny("power.scheme_missing");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("power.unverifiable");
        }
    }

    // ------------------------------------------------------------------ app compat, IFEO, firewall, Defender

    /// <summary>True when the file name of <paramref name="path"/> is a Riot/Vanguard/League executable.</summary>
    /// <param name="path">Full path.</param>
    public static bool IsRiotExecutable(string path)
        => ArgValidator.IsAbsoluteLocalPath(path) && RiotProcessNames.IsProtected(Path.GetFileName(path));

    /// <summary>Policy for <c>appcompat.setLayers</c>: Riot executables only, allowlisted tokens only.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="exePath">Executable.</param>
    /// <param name="tokens">Tokens to add.</param>
    public PolicyDecision CheckAddLayers(RegHive hive, string exePath, IEnumerable<string> tokens)
    {
        if (hive == RegHive.User && _session.Player is null)
        {
            return Deny("player_unknown");
        }

        if (!IsRiotExecutable(exePath))
        {
            return Deny("appcompat.not_riot");
        }

        return tokens.All(t => AddableCompatTokens.Contains(t, StringComparer.Ordinal)) ? Allow : Deny("appcompat.token");
    }

    /// <summary>Policy for <c>appcompat.removeLayers</c>.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="exePath">Executable.</param>
    public PolicyDecision CheckRemoveLayers(RegHive hive, string exePath)
    {
        if (hive == RegHive.User && _session.Player is null)
        {
            return Deny("player_unknown");
        }

        return exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Allow : Deny("appcompat.not_exe");
    }

    /// <summary>Policy for the IFEO primitives: Riot/Vanguard images only (removing someone else's hardening lowers security).</summary>
    /// <param name="imageName">Image name.</param>
    public static PolicyDecision CheckIfeo(string imageName)
        => RiotProcessNames.IsProtected(imageName) ? Allow : Deny("ifeo.not_riot");

    /// <summary>Policy for <c>firewall.addRule</c>: allow rules for Riot executables only.</summary>
    /// <param name="program">Program path.</param>
    public PolicyDecision CheckFirewallAdd(string program)
    {
        if (!IsRiotExecutable(program))
        {
            return Deny("firewall.program");
        }

        return Allow;
    }

    /// <summary>Policy for <c>firewall.removeRule</c>: rules of group VTDiag only.</summary>
    /// <param name="name">Rule name.</param>
    public PolicyDecision CheckFirewallRemove(string name)
    {
        try
        {
            var rule = _ports.Firewall.GetRule(name);
            return rule is null || string.Equals(rule.Grouping, FirewallGroup, StringComparison.Ordinal) ? Allow : Deny("firewall.not_ours");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("firewall.unverifiable");
        }
    }

    /// <summary>Policy for <c>defender.addExclusion</c>: an existing Riot Games or Riot Vanguard folder (or one inside them).</summary>
    /// <param name="path">Folder.</param>
    public PolicyDecision CheckDefenderExclusion(string path)
    {
        if (!ArgValidator.IsAbsoluteLocalPath(path) || path.Length <= 3)
        {
            return Deny("defender.path");
        }

        var parts = path[3..].Split('\\');
        var riotIndex = Array.FindIndex(parts, p => string.Equals(p, "Riot Games", StringComparison.OrdinalIgnoreCase) || string.Equals(p, "Riot Vanguard", StringComparison.OrdinalIgnoreCase));
        if (riotIndex < 0)
        {
            return Deny("defender.path");
        }

        var vanguard = string.Equals(parts[riotIndex], "Riot Vanguard", StringComparison.OrdinalIgnoreCase);
        if (vanguard && !path.StartsWith(Path.Combine(_ports.Files.Folders.ProgramFiles, "Riot Vanguard"), StringComparison.OrdinalIgnoreCase))
        {
            return Deny("defender.path");
        }

        try
        {
            return _ports.Files.DirectoryExists(path) ? Allow : Deny("defender.path_missing");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("defender.unverifiable");
        }
    }

    /// <summary>Policy for <c>bcd.setValue</c>: only towards the secure value.</summary>
    /// <param name="element">Element word.</param>
    /// <param name="value">Requested value.</param>
    public static PolicyDecision CheckBcd(string element, string value)
        => SecureBcdValues.TryGetValue(element, out var secure) && string.Equals(secure, value, StringComparison.Ordinal) ? Allow : Deny("bcd.direction");

    // ------------------------------------------------------------------ processes and startup

    /// <summary>Policy for <c>process.closeGraceful</c>.</summary>
    /// <param name="imageName">Image name.</param>
    public static PolicyDecision CheckProcessClose(string imageName)
    {
        if (RiotProcessNames.IsProtected(imageName))
        {
            return Deny("process.riot");
        }

        if (SystemProcesses.Contains(imageName, StringComparer.OrdinalIgnoreCase) || imageName.StartsWith("VTDiag", StringComparison.OrdinalIgnoreCase))
        {
            return Deny("process.system");
        }

        return Allow;
    }

    /// <summary>True when a startup entry (name or target image) belongs to Riot, Vanguard or Windows Security.</summary>
    /// <param name="name">Entry name.</param>
    /// <param name="targetImage">Target executable file name, when known.</param>
    public static bool IsProtectedStartup(string name, string? targetImage)
    {
        string[] words = ["Riot", "Vanguard", "VALORANT", "SecurityHealth", "Windows Defender", "WindowsDefender"];
        if (words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (targetImage is null)
        {
            return false;
        }

        string[] images = ["vgtray.exe", "SecurityHealthSystray.exe", "MsMpEng.exe", "MSASCuiL.exe"];
        return RiotProcessNames.IsProtected(targetImage) || images.Contains(targetImage, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ files

    /// <summary>The allowlisted quarantine targets (computed from OS folders).</summary>
    public IReadOnlyList<string> QuarantineTargets()
    {
        var f = _ports.Files.Folders;
        return
        [
            Path.Combine(f.Windows, "392667600.dat"),
            Path.Combine(f.ProgramFiles, "Riot Vanguard", "vgkbootstatus.dat"),
            Path.Combine(f.Windows, "vgkbootstatus.dat"),
        ];
    }

    /// <summary>
    /// Policy for <c>file.quarantine</c>: only the allowlisted Vanguard files; never while Vanguard's driver is running; and
    /// <c>392667600.dat</c> only when it is STALE (last written well before the running kernel started, so Vanguard did not
    /// refresh it this boot). On a working PC Vanguard rewrites it every boot, so a fresh file is left alone.
    /// </summary>
    /// <param name="fullPath">Expanded full path.</param>
    public PolicyDecision CheckQuarantine(string fullPath)
    {
        var target = QuarantineTargets().FirstOrDefault(t => string.Equals(t, fullPath, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return Deny("file.not_allowlisted");
        }

        try
        {
            var vgk = _ports.Services.Query("vgk");
            if (vgk is not null && vgk.State != ServiceRun.Stopped)
            {
                return Deny("file.vanguard_running");
            }

            if (string.Equals(Path.GetFileName(target), "392667600.dat", StringComparison.OrdinalIgnoreCase))
            {
                var facts = _ports.Files.Probe(target);
                if (!facts.Exists)
                {
                    return Allow; // nothing to move; capture reports exists=false and the preview blocks
                }

                var boot = _ports.Boot.LastBootUtc();
                if (boot is null || facts.LastWriteUtc is null)
                {
                    return Deny("file.vg_dat_staleness_unknown");
                }

                if (facts.LastWriteUtc.Value >= boot.Value - StaleDatMargin)
                {
                    return Deny("file.vg_dat_not_stale");
                }
            }

            return Allow;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Deny("file.unverifiable");
        }
    }

    /// <summary>Policy for <c>systemProtection.enable</c>: the system drive only.</summary>
    /// <param name="drive">Drive.</param>
    public PolicyDecision CheckSystemProtection(string drive)
        => string.Equals(drive, _ports.Files.Folders.SystemDrive, StringComparison.OrdinalIgnoreCase) ? Allow : Deny("systemprotection.drive");
}
