namespace VTD.Contracts.Fixes;

/// <summary>
/// The declared shape of one audited primitive. A2 writes fix JSON against it (KB lint checks every <c>op</c>, argument
/// and verify <c>expect</c> key); A3's <see cref="IOperationRegistry"/> must implement exactly these, with matching
/// metadata (a test in A3's project compares them). Generated to <c>kb/schema/ops.json</c> for <c>tools/kb_builder.py</c>.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record OpSpec
{
    /// <summary>Primitive id.</summary>
    public required string Op { get; init; }

    /// <summary>What it does (maintainer English).</summary>
    public required string Summary { get; init; }

    /// <summary>Arguments.</summary>
    public required IReadOnlyList<OpArgSpec> Args { get; init; }

    /// <summary>Keys of the state map its capture/probe returns (the only keys a verify <c>expect</c> may use).</summary>
    public required IReadOnlyList<string> StateKeys { get; init; }

    /// <summary>Reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Risk floor.</summary>
    public required RiskLevel RiskFloor { get; init; }

    /// <summary>Needs elevation.</summary>
    public bool RequiresAdmin { get; init; } = true;

    /// <summary>Reboot need.</summary>
    public RebootNeed Reboot { get; init; } = RebootNeed.None;

    /// <summary>Can cut connectivity: a fix using it must declare a deadman (S15). <c>device.disable</c> is flagged per
    /// binding instead: the KB lint requires a deadman when it is bound to an <see cref="EntityKind.Adapter"/>.</summary>
    public bool AffectsNetwork { get; init; }

    /// <summary>One of the entity-bound generic template actions (DESIGN.md section 3.1).</summary>
    public bool TemplateAction { get; init; }

    /// <summary>Milestone that ships it (<c>M2</c> or later).</summary>
    public string Milestone { get; init; } = "M2";

    /// <summary>Policy notes that A3 enforces in ProtectedTargetPolicy (not overridable by the KB or a plan).</summary>
    public string? PolicyNotes { get; init; }
}

/// <summary>Primitive ids.</summary>
public static class OpIds
{
    /// <summary>Set a service start type.</summary>
    public const string ServiceSetStartType = "service.setStartType";

    /// <summary>Stop a service.</summary>
    public const string ServiceStop = "service.stop";

    /// <summary>Start a service.</summary>
    public const string ServiceStart = "service.start";

    /// <summary>Disable a PnP device (CM_Disable_DevNode with CM_DISABLE_PERSIST).</summary>
    public const string DeviceDisable = "device.disable";

    /// <summary>Enable a PnP device.</summary>
    public const string DeviceEnable = "device.enable";

    /// <summary>Set a network adapter advanced property (Class-key keyword + adapter restart).</summary>
    public const string NetAdapterSetAdvancedProperty = "netadapter.setAdvancedProperty";

    /// <summary>Set an adapter's IPv4 interface metric.</summary>
    public const string NetAdapterSetMetric = "netadapter.setMetric";

    /// <summary>Unbind IPv6 from one adapter.</summary>
    public const string NetAdapterUnbindIpv6 = "netadapter.unbindIpv6";

    /// <summary>Set a registry value under an allowlisted root.</summary>
    public const string RegistrySetValue = "registry.setValue";

    /// <summary>Delete a registry value under an allowlisted root.</summary>
    public const string RegistryDeleteValue = "registry.deleteValue";

    /// <summary>Set a power setting (PowerWriteAC/DCValueIndex).</summary>
    public const string PowerCfgSetValue = "powercfg.setValue";

    /// <summary>Set the active power scheme.</summary>
    public const string PowerCfgSetActiveScheme = "powercfg.setActiveScheme";

    /// <summary>Add AppCompat layers to an executable.</summary>
    public const string AppCompatSetLayers = "appcompat.setLayers";

    /// <summary>Remove AppCompat layers from an executable.</summary>
    public const string AppCompatRemoveLayers = "appcompat.removeLayers";

    /// <summary>Remove Exploit Protection overrides (IFEO MitigationOptions) from an image.</summary>
    public const string IfeoResetMitigations = "ifeo.resetMitigations";

    /// <summary>Remove IFEO PerfOptions (CPU/IO priority) from an image.</summary>
    public const string IfeoRemovePerfOptions = "ifeo.removePerfOptions";

    /// <summary>Move a file to the ACL'd quarantine (never delete).</summary>
    public const string FileQuarantine = "file.quarantine";

    /// <summary>Add a firewall rule in group <c>VTDiag</c>.</summary>
    public const string FirewallAddRule = "firewall.addRule";

    /// <summary>Remove a firewall rule VTDiag created (group <c>VTDiag</c> only).</summary>
    public const string FirewallRemoveRule = "firewall.removeRule";

    /// <summary>Add a Defender exclusion (Riot paths only, off by default).</summary>
    public const string DefenderAddExclusion = "defender.addExclusion";

    /// <summary>Set an allowlisted BCD element.</summary>
    public const string BcdSetValue = "bcd.setValue";

    /// <summary>Resync the Windows clock.</summary>
    public const string TimeResync = "time.resync";

    /// <summary>Flush the DNS client cache.</summary>
    public const string DnsFlush = "dns.flush";

    /// <summary>Close a third-party process gracefully (WM_CLOSE / tray exit; never kill, never a Riot process).</summary>
    public const string ProcessCloseGraceful = "process.closeGraceful";

    /// <summary>Disable a startup entry (StartupApproved).</summary>
    public const string StartupDisable = "startup.disable";

    /// <summary>Turn on System Protection for a drive (prerequisite for restore points).</summary>
    public const string SystemProtectionEnable = "systemProtection.enable";

    /// <summary>Winsock reset (one-way, reboot).</summary>
    public const string OneWayWinsockReset = "oneway.winsockReset";

    /// <summary>TCP/IP reset (one-way, reboot).</summary>
    public const string OneWayIpReset = "oneway.ipReset";

    /// <summary>SFC + DISM repair, streamed (one-way, long; M4).</summary>
    public const string OneWaySfcDism = "oneway.sfcDism";
}

/// <summary>The catalog of audited primitives (design_B.md section 4.3, critique section 4 v0.2 list).</summary>
public static class OpCatalog
{
    private static OpArgSpec Arg(string name, OpArgType type, bool required = true, string? pattern = null, params string[] allowed)
        => new() { Name = name, Type = type, Required = required, Pattern = pattern, AllowedValues = allowed };

    /// <summary>All primitives.</summary>
    public static IReadOnlyList<OpSpec> All { get; } =
    [
        new() { Op = OpIds.ServiceSetStartType, Summary = "Set a service's start type (and delayed-auto flag).", Args = [Arg("name", OpArgType.ServiceName), Arg("startType", OpArgType.Enum, true, null, "Automatic", "Manual", "Disabled"), Arg("delayed", OpArgType.Bool, false)], StateKeys = ["exists", "startType", "delayed"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, TemplateAction = true, PolicyNotes = "Protected services (vgc, vgk except the official Vanguard flows, WinDefend, BFE, mpssvc, EventLog, Winmgmt, TBS, Dnscache, wuauserv, Schedule...) are refused. vgc → Automatic only when the player confirmed Pre-Check is off AND vgc logs show a failure." },
        new() { Op = OpIds.ServiceStop, Summary = "Stop a service.", Args = [Arg("name", OpArgType.ServiceName), Arg("timeoutSec", OpArgType.Int, false)], StateKeys = ["exists", "state"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, PolicyNotes = "Same protected list as service.setStartType." },
        new() { Op = OpIds.ServiceStart, Summary = "Start a service.", Args = [Arg("name", OpArgType.ServiceName), Arg("timeoutSec", OpArgType.Int, false)], StateKeys = ["exists", "state"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.DeviceDisable, Summary = "Disable a PnP device persistently.", Args = [Arg("instanceId", OpArgType.InstanceId)], StateKeys = ["present", "enabled", "problemCode"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, TemplateAction = true, PolicyNotes = "Refused for the default-route NIC, the boot/system disk controller, display adapters and input HID." },
        new() { Op = OpIds.DeviceEnable, Summary = "Enable a PnP device.", Args = [Arg("instanceId", OpArgType.InstanceId)], StateKeys = ["present", "enabled", "problemCode"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.NetAdapterSetAdvancedProperty, Summary = "Set an adapter advanced property by registry keyword, then restart the adapter.", Args = [Arg("instanceId", OpArgType.InstanceId), Arg("keyword", OpArgType.String, true, "^\\*?[A-Za-z0-9]{1,64}$"), Arg("value", OpArgType.String, true, "^[A-Za-z0-9._-]{0,64}$")], StateKeys = ["exists", "value"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, AffectsNetwork = true },
        new() { Op = OpIds.NetAdapterSetMetric, Summary = "Set an adapter's IPv4 metric (or back to automatic).", Args = [Arg("instanceId", OpArgType.InstanceId), Arg("metric", OpArgType.String, true, "^(automatic|[0-9]{1,4})$")], StateKeys = ["metric", "automaticMetric"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, AffectsNetwork = true },
        new() { Op = OpIds.NetAdapterUnbindIpv6, Summary = "Unbind IPv6 (ms_tcpip6) from one adapter.", Args = [Arg("instanceId", OpArgType.InstanceId)], StateKeys = ["ipv6Bound"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, AffectsNetwork = true, TemplateAction = true },
        new() { Op = OpIds.RegistrySetValue, Summary = "Set a registry value under a primitive allowlisted root. HKCU means the player's hive (HKU\\<player SID>).", Args = [Arg("path", OpArgType.RegistryPath), Arg("name", OpArgType.String), Arg("kind", OpArgType.Enum, true, null, "String", "ExpandString", "DWord", "QWord", "MultiString"), Arg("data", OpArgType.String)], StateKeys = ["exists", "kind", "data"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, PolicyNotes = "Allowlisted roots live in A3's code, not in the KB. Never lowers security (S5): Secure Boot, TPM, HVCI, Defender, firewall, UAC values cannot be set to off." },
        new() { Op = OpIds.RegistryDeleteValue, Summary = "Delete a registry value under an allowlisted root.", Args = [Arg("path", OpArgType.RegistryPath), Arg("name", OpArgType.String)], StateKeys = ["exists", "kind", "data"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.PowerCfgSetValue, Summary = "Set a power setting on AC and/or DC for a scheme.", Args = [Arg("scheme", OpArgType.String, true, "^(active|[0-9a-fA-F-]{36})$"), Arg("subgroup", OpArgType.Guid), Arg("setting", OpArgType.Guid), Arg("ac", OpArgType.Int, false), Arg("dc", OpArgType.Int, false)], StateKeys = ["ac", "dc"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.PowerCfgSetActiveScheme, Summary = "Set the active power scheme.", Args = [Arg("scheme", OpArgType.Guid)], StateKeys = ["activeScheme"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.AppCompatSetLayers, Summary = "Add AppCompat layer tokens to an executable (for example RUNASADMIN on the Riot Client).", Args = [Arg("exePath", OpArgType.FilePath), Arg("hive", OpArgType.Enum, true, null, "HKLM", "HKCU"), Arg("layers", OpArgType.String, true, "^[A-Z0-9_~ ]{1,256}$")], StateKeys = ["exists", "layers"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.AppCompatRemoveLayers, Summary = "Remove AppCompat layer tokens from an executable.", Args = [Arg("exePath", OpArgType.FilePath), Arg("hive", OpArgType.Enum, true, null, "HKLM", "HKCU"), Arg("layers", OpArgType.String, true, "^[A-Z0-9_~ ]{1,256}$")], StateKeys = ["exists", "layers"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, TemplateAction = true },
        new() { Op = OpIds.IfeoResetMitigations, Summary = "Remove Exploit Protection overrides (MitigationOptions/AuditOptions) from an image (VAN 9002).", Args = [Arg("imageName", OpArgType.ImageName)], StateKeys = ["mitigationOptions", "mitigationAuditOptions"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.IfeoRemovePerfOptions, Summary = "Remove IFEO PerfOptions (CpuPriorityClass, IoPriority) from an image.", Args = [Arg("imageName", OpArgType.ImageName)], StateKeys = ["cpuPriorityClass", "ioPriority"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.FileQuarantine, Summary = "Move a file to %ProgramData%\\VTDiag\\quarantine\\<attempt> with a manifest (never delete; reparse-safe).", Args = [Arg("path", OpArgType.FilePath)], StateKeys = ["exists", "sha256", "quarantinePath"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, PolicyNotes = "Windows folders only for the allowlisted set (%WINDIR%\\392667600.dat; vgkbootstatus.dat in the Vanguard folder; ene.sys via guide only). Elevated moves open with FILE_FLAG_OPEN_REPARSE_POINT and refuse player-controlled path components." },
        new() { Op = OpIds.FirewallAddRule, Summary = "Add an allow rule for a Riot executable in group VTDiag.", Args = [Arg("name", OpArgType.String, true, "^VTDiag [A-Za-z0-9 ._-]{1,64}$"), Arg("program", OpArgType.FilePath), Arg("direction", OpArgType.Enum, true, null, "In", "Out")], StateKeys = ["exists", "enabled"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, PolicyNotes = "Allow rules only; program must be a Riot executable." },
        new() { Op = OpIds.FirewallRemoveRule, Summary = "Remove a rule VTDiag created (group VTDiag only).", Args = [Arg("name", OpArgType.String, true, "^VTDiag [A-Za-z0-9 ._-]{1,64}$")], StateKeys = ["exists", "enabled"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.DefenderAddExclusion, Summary = "Add a Defender path exclusion for a Riot folder (off by default, warned, listed in the report's security changes).", Args = [Arg("path", OpArgType.FilePath)], StateKeys = ["excluded"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Medium, PolicyNotes = "Riot Games and Riot Vanguard folders only." },
        new() { Op = OpIds.BcdSetValue, Summary = "Set an allowlisted BCD element on {current} (backup via bcdedit /export).", Args = [Arg("element", OpArgType.Enum, true, null, "testsigning", "nointegritychecks", "nx", "hypervisorlaunchtype"), Arg("value", OpArgType.String, true, "^(on|off|OptIn|OptOut|AlwaysOn|Auto|Off)$")], StateKeys = ["value"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Medium, Reboot = RebootNeed.Required, PolicyNotes = "Only towards the secure state (testsigning off, nointegritychecks off, nx OptIn, hypervisorlaunchtype Auto). HVCI enable stays guide-only in MVP (critique A7)." },
        new() { Op = OpIds.TimeResync, Summary = "Resync the Windows clock (w32time). Undo does not restore a wrong clock.", Args = [], StateKeys = ["skewSeconds"], Reversibility = Reversibility.BestEffort, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.DnsFlush, Summary = "Flush the DNS client cache (harmless, one-way).", Args = [], StateKeys = [], Reversibility = Reversibility.OneWay, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.ProcessCloseGraceful, Summary = "Close a third-party process gracefully (WM_CLOSE to its windows / tray exit); never kill, never a handle.", Args = [Arg("imageName", OpArgType.ImageName)], StateKeys = ["running"], Reversibility = Reversibility.SessionOnly, RiskFloor = RiskLevel.Low, RequiresAdmin = false, TemplateAction = true, PolicyNotes = "Refused for RiotProcessNames and Windows system processes." },
        new() { Op = OpIds.StartupDisable, Summary = "Disable a startup entry via StartupApproved (the entry is kept).", Args = [Arg("location", OpArgType.Enum, true, null, "HklmRun", "HkcuRun", "HklmRunOnce", "HkcuRunOnce", "StartupFolderUser", "StartupFolderCommon"), Arg("name", OpArgType.String)], StateKeys = ["exists", "enabled"], Reversibility = Reversibility.Exact, RiskFloor = RiskLevel.Low, TemplateAction = true },
        new() { Op = OpIds.SystemProtectionEnable, Summary = "Turn on System Protection for the system drive so a restore point can be made.", Args = [Arg("drive", OpArgType.String, true, "^[A-Z]:$")], StateKeys = ["enabled", "maxSizePercent"], Reversibility = Reversibility.BestEffort, RiskFloor = RiskLevel.Low },
        new() { Op = OpIds.OneWayWinsockReset, Summary = "netsh winsock reset equivalent (one-way, needs reboot).", Args = [], StateKeys = [], Reversibility = Reversibility.OneWay, RiskFloor = RiskLevel.Medium, Reboot = RebootNeed.Required, AffectsNetwork = true },
        new() { Op = OpIds.OneWayIpReset, Summary = "TCP/IP stack reset (one-way, needs reboot).", Args = [], StateKeys = [], Reversibility = Reversibility.OneWay, RiskFloor = RiskLevel.Medium, Reboot = RebootNeed.Required, AffectsNetwork = true },
        new() { Op = OpIds.OneWaySfcDism, Summary = "SFC /scannow + DISM RestoreHealth, streamed (one-way, long; AC power or > 40% battery).", Args = [], StateKeys = [], Reversibility = Reversibility.OneWay, RiskFloor = RiskLevel.Medium, Reboot = RebootNeed.Recommended, Milestone = "M4" },
    ];

    /// <summary>Finds a primitive spec.</summary>
    /// <param name="op">Primitive id.</param>
    public static OpSpec? Find(string op) => All.FirstOrDefault(o => string.Equals(o.Op, op, StringComparison.Ordinal));
}
