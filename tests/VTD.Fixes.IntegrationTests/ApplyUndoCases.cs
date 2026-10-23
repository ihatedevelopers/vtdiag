using System.Collections.ObjectModel;
using Microsoft.Win32;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Fixes.Interop;
using VTD.Fixes.Ops;
using VTD.Fixes.Policy;

namespace VTD.Fixes.IntegrationTests;

/// <summary>What the undo of an attempt must do.</summary>
internal enum UndoExpectation
{
    /// <summary>Undone, and the before-state is back exactly.</summary>
    Undone,

    /// <summary>Refused as one-way (<c>undo.one_way</c>).</summary>
    OneWay,

    /// <summary>Refused as session-only (<c>undo.session_only</c>).</summary>
    SessionOnly,
}

/// <summary>How to resolve an armed deadman.</summary>
internal enum DeadmanChoice
{
    /// <summary>Keep the change (<c>ConfirmDeadmanAsync(keep: true)</c>), then undo it.</summary>
    Keep,

    /// <summary>Let it revert (<c>ConfirmDeadmanAsync(keep: false)</c>): the deadman itself must restore the before-state.</summary>
    Revert,
}

/// <summary>One fix arranged on the real machine: its binding, how to read the state back, and what must happen.</summary>
internal sealed class FixCase
{
    private static readonly IReadOnlyDictionary<string, string> None = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>What the fix acts on (for the log and the report).</summary>
    public required string Target { get; init; }

    /// <summary>Binding.</summary>
    public FixBinding Binding { get; init; } = FixBinding.Empty;

    /// <summary>Reads the real state the fix changes (independent of the product's ports).</summary>
    public Func<IReadOnlyDictionary<string, string>> Read { get; init; } = () => None;

    /// <summary>Keys of <see cref="Read"/> and the values they must have right after apply.</summary>
    public IReadOnlyDictionary<string, string> ExpectAfterApply { get; init; } = None;

    /// <summary>Undo expectation.</summary>
    public UndoExpectation Undo { get; init; } = UndoExpectation.Undone;

    /// <summary>Deadman choice when one is armed.</summary>
    public DeadmanChoice Deadman { get; init; } = DeadmanChoice.Keep;

    /// <summary>Step indexes allowed to fail (<c>onFail: continue</c> steps that cannot succeed on a dummy target).</summary>
    public IReadOnlyCollection<int> MayFailSteps { get; init; } = [];

    /// <summary>Block reasons that mean "this machine cannot host a valid target" (the row is skipped with them).</summary>
    public IReadOnlyCollection<string> SkipIfBlockedBy { get; init; } = [];
}

/// <summary>
/// The arrangement of every KB fix on a throwaway elevated Windows machine (GitHub Actions <c>windows-latest</c>). Each
/// arrangement seeds the "bad" state the fix repairs through <see cref="Lab"/>, which registers the undo of every seed
/// before making it. Adapter and device fixes act ONLY on the KM-TEST loopback adapter the fixture installed.
/// </summary>
internal static class ApplyUndoCases
{
    private const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string DummyGameDir = @"C:\Riot Games\VTDiagIT\VALORANT\live";
    private const string DisabledStartupBytes = "Binary:030000000000000000000000";

    private static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static readonly RegistryKey Hkcu = Registry.CurrentUser;
    private static readonly RegistryKey Hklm = Registry.LocalMachine;

    /// <summary>Arrangements by fix id.</summary>
    public static IReadOnlyDictionary<string, Func<Lab, FixCase>> All { get; } = new Dictionary<string, Func<Lab, FixCase>>(StringComparer.Ordinal)
    {
        ["fix.audio.bt_handsfree_off"] = lab => DeviceDisable(lab, "dev", EntityKind.Device),
        ["fix.generic.disable_device"] = lab => DeviceDisable(lab, "dev", EntityKind.Device),
        ["fix.net.disable_second_adapter"] = lab => DeviceDisable(lab, "adapter", EntityKind.Adapter),
        ["fix.net.disable_unused_adapter"] = lab => DeviceDisable(lab, "adapter", EntityKind.Adapter),
        ["fix.generic.unbind_ipv6"] = lab => UnbindIpv6(lab, DeadmanChoice.Revert),
        ["fix.net.unbind_ipv6_active"] = lab => UnbindIpv6(lab, DeadmanChoice.Keep),
        ["fix.net.realtek_power_saving_off"] = RealtekEee,

        ["fix.display.disable_fso"] = lab => CompatAdd(lab, Path.Combine(DummyGameDir, @"ShooterGame\Binaries\Win64", RiotProcessNames.Game), null, "String:~ DISABLEDXMAXIMIZEDWINDOWEDMODE"),
        ["fix.riot.client_runasadmin"] = lab => CompatAdd(lab, @"C:\Riot Games\VTDiagIT\Riot Client\RiotClientServices.exe", "~ HIGHDPIAWARE", "String:~ HIGHDPIAWARE RUNASADMIN"),
        ["fix.generic.remove_compat_layer"] = CompatRemove,
        ["fix.display.gpu_pref_high"] = GpuPreference,

        ["fix.display.game_dvr_off"] = lab => RegistrySeeds(lab, "HKCU Game DVR values set to 1",
            (Hkcu, @"System\GameConfigStore", "GameDVR_Enabled", 1, RegistryValueKind.DWord, "DWord:0"),
            (Hkcu, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, RegistryValueKind.DWord, "DWord:0")),
        ["fix.display.hags_off"] = lab => RegistrySeeds(lab, "HKLM HwSchMode set to 2 (HAGS on)",
            (Hklm, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord, "DWord:1")),
        ["fix.display.mpo_off"] = lab => RegistrySeeds(lab, "HKLM Dwm OverlayTestMode removed (MPO on)",
            (Hklm, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", null, RegistryValueKind.DWord, "DWord:5")),
        ["fix.input.mouse_accel_off"] = lab => RegistrySeeds(lab, "HKCU mouse acceleration on (1/6/10)",
            (Hkcu, @"Control Panel\Mouse", "MouseSpeed", "1", RegistryValueKind.String, "String:0"),
            (Hkcu, @"Control Panel\Mouse", "MouseThreshold1", "6", RegistryValueKind.String, "String:0"),
            (Hkcu, @"Control Panel\Mouse", "MouseThreshold2", "10", RegistryValueKind.String, "String:0")),
        ["fix.os.fast_startup_off"] = lab => RegistrySeeds(lab, "HKLM HiberbootEnabled set to 1",
            (Hklm, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, RegistryValueKind.DWord, "DWord:0")),

        ["fix.sec.remove_riot_perfoptions"] = lab => IfeoSeeds(lab,
            (@"PerfOptions", "CpuPriorityClass", 3, RegistryValueKind.DWord),
            (@"PerfOptions", "IoPriority", 3, RegistryValueKind.DWord)),
        ["fix.sec.reset_exploit_overrides"] = lab => IfeoSeeds(lab,
            (string.Empty, "MitigationOptions", new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10, 0, 0 }, RegistryValueKind.Binary),
            (string.Empty, "MitigationAuditOptions", new byte[16], RegistryValueKind.Binary)),

        ["fix.generic.disable_startup_hkcu"] = lab => Startup(lab, Hkcu, "HkcuRun", "VTDiagITStartupHkcu"),
        ["fix.generic.disable_startup_hklm"] = lab => Startup(lab, Hklm, "HklmRun", "VTDiagITStartupHklm"),

        ["fix.svc.disable_conflict_service"] = lab => DummyService(lab, "VTDiagITConflictSvc", "Start=DWord:4;Delayed=" + State.Absent, mayFail: []),
        ["fix.vg.vgc_automatic"] = lab =>
        {
            lab.Session.VgcAutomaticConfirmed = Tri.True;
            return DummyService(lab, "vgc", "Start=DWord:2;Delayed=" + State.Absent, mayFail: [1]);
        },

        ["fix.generic.close_process"] = lab => CloseProcess(lab, "MSIAfterburner.exe"),
        ["fix.proc.close_overlays"] = lab => CloseProcess(lab, "Overwolf.exe"),

        ["fix.vg.quarantine_bootstatus"] = lab => Quarantine(lab, @"%ProgramFiles%\Riot Vanguard\vgkbootstatus.dat", stale: false),
        ["fix.vg.quarantine_stale_dat"] = lab => Quarantine(lab, @"%SystemRoot%\392667600.dat", stale: true),

        ["fix.power.cpu_max_100"] = lab => PowerValue(lab, Processor, ProcessorMax, ac: 90, dc: null, expectAc: "100", expectDc: null),
        ["fix.power.usb_selective_suspend_off"] = lab => PowerValue(lab, UsbSubgroup, UsbSuspend, ac: 1, dc: 1, expectAc: "0", expectDc: "0"),
        ["fix.power.high_performance"] = ActiveScheme,

        ["fix.net.dns_flush"] = _ => new FixCase { Target = "none (ipconfig /flushdns)", Undo = UndoExpectation.OneWay },
        ["fix.net.winsock_reset"] = _ => new FixCase { Target = "none (netsh winsock reset; no reboot is done)", Undo = UndoExpectation.OneWay },
        ["fix.os.time_resync"] = TimeResync,
    };

    private static FixBinding Bind(string name, EntityKind kind, string id, string? displayName = null)
        => new() { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { [name] = [new EntityRef { Kind = kind, Id = id, DisplayName = displayName ?? id }] } };

    private static Dictionary<string, string> Map(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // ------------------------------------------------------------------ devices and adapters (loopback only)

    private static FixCase DeviceDisable(Lab lab, string bindName, EntityKind kind)
    {
        var lb = lab.Loopback();
        return new FixCase
        {
            Target = "KM-TEST loopback adapter " + lb.InstanceId + " (bound as " + kind.ToString() + ")",
            Binding = Bind(bindName, kind, lb.InstanceId),
            Read = () => Map(("device", State.Device(lb.InstanceId))),
            ExpectAfterApply = Map(("device", "problem=22")),
        };
    }

    private static FixCase UnbindIpv6(Lab lab, DeadmanChoice deadman)
    {
        var lb = lab.Loopback();
        if (State.Ipv6Binding(lb.InterfaceGuid) == "enabled=false")
        {
            lab.Run("WindowsPowerShell\\v1.0\\powershell.exe", "-NoProfile", "-Command",
                "Get-NetAdapterBinding -ComponentID ms_tcpip6 | Where-Object { $_.InterfaceDescription -like '*Loopback*' } | Enable-NetAdapterBinding -ComponentID ms_tcpip6");
        }

        return new FixCase
        {
            Target = "ms_tcpip6 binding of the loopback adapter " + lb.InstanceId,
            Binding = Bind("adapter", EntityKind.Adapter, lb.InstanceId),
            Read = () => Map(("ipv6", State.Ipv6Binding(lb.InterfaceGuid))),
            ExpectAfterApply = Map(("ipv6", "enabled=false")),
            Deadman = deadman,
        };
    }

    private static FixCase RealtekEee(Lab lab)
    {
        var lb = lab.Loopback();
        var key = lb.ClassKeyPath;
        var param = key + @"\Ndi\params\*EEE";

        // netloop.inf declares no *EEE keyword: declare one (enum 0/1) under the loopback's own driver key, as a Realtek
        // driver does, so the primitive has a keyword it accepts. Removed with the value in cleanup.
        lab.PreserveKey(Hklm, param);
        using (var p = Hklm.CreateSubKey(param, writable: true))
        {
            p.SetValue("ParamDesc", "Energy-Efficient Ethernet", RegistryValueKind.String);
            p.SetValue("type", "enum", RegistryValueKind.String);
            p.SetValue("default", "1", RegistryValueKind.String);
            using var e = p.CreateSubKey("enum", writable: true);
            e.SetValue("0", "Disabled", RegistryValueKind.String);
            e.SetValue("1", "Enabled", RegistryValueKind.String);
        }

        lab.Seed(Hklm, key, "*EEE", "1", RegistryValueKind.String);
        return new FixCase
        {
            Target = "*EEE=1 under the loopback adapter's driver key " + lb.DriverKey + " (Ndi\\params\\*EEE declared by the test)",
            Binding = Bind("adapter", EntityKind.Adapter, lb.InstanceId),
            Read = () => Map(("*EEE", State.Reg(Hklm, key, "*EEE")), ("EnableGreenEthernet", State.Reg(Hklm, key, "EnableGreenEthernet")), ("device", State.Device(lb.InstanceId))),
            ExpectAfterApply = Map(("*EEE", "String:0"), ("EnableGreenEthernet", State.Absent), ("device", "problem=0")),
        };
    }

    // ------------------------------------------------------------------ registry

    private static FixCase RegistrySeeds(Lab lab, string target, params (RegistryKey Root, string SubKey, string Name, object? Seed, RegistryValueKind Kind, string Expect)[] values)
    {
        foreach (var v in values)
        {
            if (v.Seed is null)
            {
                lab.SeedAbsent(v.Root, v.SubKey, v.Name);
            }
            else
            {
                lab.Seed(v.Root, v.SubKey, v.Name, v.Seed, v.Kind);
            }
        }

        return new FixCase
        {
            Target = target,
            Read = () => values.ToDictionary(v => v.Name, v => State.Reg(v.Root, v.SubKey, v.Name), StringComparer.Ordinal),
            ExpectAfterApply = values.ToDictionary(v => v.Name, v => v.Expect, StringComparer.Ordinal),
        };
    }

    private static FixCase CompatAdd(Lab lab, string exe, string? seed, string expect)
    {
        if (seed is null)
        {
            lab.SeedAbsent(Hkcu, Layers, exe);
        }
        else
        {
            lab.Seed(Hkcu, Layers, exe, seed, RegistryValueKind.String);
        }

        return new FixCase
        {
            Target = "HKCU AppCompat Layers value for " + exe + " (" + (seed ?? "absent") + ")",
            Binding = Bind("exe", EntityKind.File, exe),
            Read = () => Map(("layers", State.Reg(Hkcu, Layers, exe))),
            ExpectAfterApply = Map(("layers", expect)),
        };
    }

    private static FixCase CompatRemove(Lab lab)
    {
        var exe = Path.Combine(DummyGameDir, RiotProcessNames.GameLauncher);
        lab.Seed(Hkcu, Layers, exe, "~ WIN7RTM", RegistryValueKind.String);
        return new FixCase
        {
            Target = "HKCU AppCompat Layers '~ WIN7RTM' for " + exe,
            Binding = new FixBinding { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { ["layer"] = [new EntityRef { Kind = EntityKind.CompatLayer, Id = exe, DisplayName = "WIN7RTM" }] } },
            Read = () => Map(("layers", State.Reg(Hkcu, Layers, exe))),
            ExpectAfterApply = Map(("layers", State.Absent)),
        };
    }

    private static FixCase GpuPreference(Lab lab)
    {
        const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
        var exe = Path.Combine(DummyGameDir, @"ShooterGame\Binaries\Win64", RiotProcessNames.Game);
        lab.Seed(Hkcu, Key, exe, "GpuPreference=1;", RegistryValueKind.String);
        return new FixCase
        {
            Target = "HKCU UserGpuPreferences 'GpuPreference=1;' for " + exe,
            Binding = Bind("exe", EntityKind.File, exe),
            Read = () => Map(("pref", State.Reg(Hkcu, Key, exe))),
            ExpectAfterApply = Map(("pref", "String:GpuPreference=2;")),
        };
    }

    private static FixCase IfeoSeeds(Lab lab, params (string SubKey, string Name, object Value, RegistryValueKind Kind)[] values)
    {
        var image = RiotProcessNames.Game;
        var root = Ifeo + "\\" + image;
        lab.PreserveKey(Hklm, root);
        foreach (var v in values)
        {
            var key = v.SubKey.Length == 0 ? root : root + "\\" + v.SubKey;
            lab.Seed(Hklm, key, v.Name, v.Value, v.Kind);
        }

        string KeyOf(string subKey) => subKey.Length == 0 ? root : root + "\\" + subKey;
        return new FixCase
        {
            Target = "IFEO " + image + ": " + string.Join(", ", values.Select(v => v.Name)),
            Binding = Bind("img", EntityKind.Process, image),
            Read = () => values.ToDictionary(v => v.Name, v => State.Reg(Hklm, KeyOf(v.SubKey), v.Name), StringComparer.Ordinal),
            ExpectAfterApply = values.ToDictionary(v => v.Name, _ => State.Absent, StringComparer.Ordinal),
        };
    }

    private static FixCase Startup(Lab lab, RegistryKey root, string location, string name)
    {
        lab.Seed(root, RunKey, name, "\"" + System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe") + "\" /vtdiag-it", RegistryValueKind.String);
        lab.SeedAbsent(root, ApprovedRun, name);
        return new FixCase
        {
            Target = location + " entry '" + name + "' (notepad.exe), StartupApproved absent",
            Binding = new FixBinding { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { ["item"] = [new EntityRef { Kind = EntityKind.StartupItem, Id = location + "|" + name, DisplayName = name }] } },
            Read = () => Map(("run", State.Reg(root, RunKey, name)), ("approved", State.Reg(root, ApprovedRun, name))),
            ExpectAfterApply = Map(("approved", DisabledStartupBytes)),
        };
    }

    // ------------------------------------------------------------------ services, processes, files

    private static FixCase DummyService(Lab lab, string name, string expect, IReadOnlyCollection<int> mayFail)
    {
        lab.CreateDummyService(name);
        return new FixCase
        {
            Target = "dummy demand-start own-process service '" + name + "' (binary does not exist)",
            Binding = Bind("svc", EntityKind.Service, name),
            Read = () => Map(("service", State.Service(name))),
            ExpectAfterApply = Map(("service", expect)),
            MayFailSteps = mayFail,
        };
    }

    private static FixCase CloseProcess(Lab lab, string imageName)
    {
        var process = lab.StartWindowedDummy(imageName);
        return new FixCase
        {
            Target = "notepad.exe copy running as " + imageName,
            Binding = Bind("proc", EntityKind.Process, imageName),
            Read = () => Map(("running", State.Bool(!process.HasExited))),
            ExpectAfterApply = Map(("running", "false")),
            Undo = UndoExpectation.SessionOnly,
        };
    }

    private static FixCase Quarantine(Lab lab, string pathArg, bool stale)
    {
        var folders = ReparseSafeFilePort.SystemFolders();
        var full = ArgValidator.ExpandPath(pathArg, folders) ?? throw new InvalidOperationException(pathArg);
        var content = System.Text.Encoding.ASCII.GetBytes("VTDiag integration test dummy " + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        DateTime? lastWrite = null;
        if (stale)
        {
            var boot = new TickBootClock(TimeProvider.System).LastBootUtc() ?? throw new InvalidOperationException("boot time unknown");
            lastWrite = boot.UtcDateTime.AddDays(-1);
        }

        lab.CreateAdminFile(full, content, lastWrite);
        var sha = State.File(full)["sha256:".Length..];
        var quarantineRoot = new ReparseSafeFilePort(folders, MachineFixture.DataRoot, adminOnly: true).QuarantineRoot;
        var quarantined = FileQuarantineOp.QuarantinePathFor(quarantineRoot, full, sha);
        lab.DeleteLater(Path.GetDirectoryName(quarantined)!);
        return new FixCase
        {
            Target = "dummy " + full + (stale ? " (last write one day before boot)" : string.Empty) + ", owner Administrators",
            Binding = Bind("file", EntityKind.File, pathArg),
            Read = () => Map(("file", State.File(full)), ("quarantined", State.File(quarantined))),
            ExpectAfterApply = Map(("file", State.Absent), ("quarantined", "sha256:" + sha)),
        };
    }

    // ------------------------------------------------------------------ power

    private static FixCase PowerValue(Lab lab, Guid subgroup, Guid setting, uint ac, uint? dc, string expectAc, string? expectDc)
    {
        var scheme = State.ActiveScheme();
        var oldAc = State.PowerValue(scheme, subgroup, setting, ac: true);
        var oldDc = State.PowerValue(scheme, subgroup, setting, ac: false);
        if (oldAc is null || oldDc is null)
        {
            Assert.Skip("Power setting " + setting.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + " is not readable in the active scheme of this machine.");
        }

        lab.Defer("restore power setting " + setting.ToString("D", System.Globalization.CultureInfo.InvariantCulture), () =>
        {
            State.WritePowerValue(scheme, subgroup, setting, ac: true, oldAc.Value);
            State.WritePowerValue(scheme, subgroup, setting, ac: false, oldDc.Value);
        });
        State.WritePowerValue(scheme, subgroup, setting, ac: true, ac);
        if (dc is { } d)
        {
            State.WritePowerValue(scheme, subgroup, setting, ac: false, d);
        }

        string Word(uint? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? State.Absent;
        var expect = Map(("ac", expectAc));
        if (expectDc is not null)
        {
            expect["dc"] = expectDc;
        }

        return new FixCase
        {
            Target = "power setting " + setting.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + " of the active scheme seeded to AC " + Word(ac) + (dc is null ? string.Empty : " / DC " + Word(dc)),
            Read = () => Map(("ac", Word(State.PowerValue(scheme, subgroup, setting, ac: true))), ("dc", Word(State.PowerValue(scheme, subgroup, setting, ac: false)))),
            ExpectAfterApply = expect,
            SkipIfBlockedBy = ["target.power_setting_missing"],
        };
    }

    private static FixCase ActiveScheme(Lab lab)
    {
        var original = State.ActiveScheme();
        lab.Defer("re-activate power scheme " + original.ToString("D", System.Globalization.CultureInfo.InvariantCulture), () => State.SetActiveScheme(original));
        if (original == HighPerformance)
        {
            State.SetActiveScheme(Balanced);
        }

        return new FixCase
        {
            Target = "active power scheme " + State.ActiveScheme().ToString("D", System.Globalization.CultureInfo.InvariantCulture) + " (not High performance)",
            Read = () => Map(("active", State.ActiveScheme().ToString("D", System.Globalization.CultureInfo.InvariantCulture))),
            ExpectAfterApply = Map(("active", HighPerformance.ToString("D", System.Globalization.CultureInfo.InvariantCulture))),
            SkipIfBlockedBy = ["policy.power_scheme_missing"],
        };
    }

    // ------------------------------------------------------------------ time

    private static FixCase TimeResync(Lab lab)
    {
        // The clock-skew probe lives in VTD.Collectors (not referenced here): script it as "in sync" so verify reads 0.
        // w32tm /resync /force itself runs for real; it needs the Windows Time service running.
        lab.Probe.Set(GuardIds.ClockOk, _ => new SignalResult
        {
            Id = GuardIds.ClockOk,
            State = Tri.True,
            Evidence = [new Evidence { TextKey = "it.clock", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["skewSeconds"] = "0.4" } }],
        });
        var query = lab.Run("sc.exe", "query", "W32Time");
        if (!query.Contains("RUNNING", StringComparison.Ordinal))
        {
            lab.Defer("stop W32Time again", () => lab.Run("sc.exe", "stop", "W32Time"));
            lab.Run("sc.exe", "start", "W32Time");
            Thread.Sleep(3000);
        }

        return new FixCase { Target = "the system clock via w32tm (Windows Time running; skew probe scripted)" };
    }
}
