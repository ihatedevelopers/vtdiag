using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;
using VTD.Fixes.Tests.Fakes;

namespace VTD.Fixes.Tests;

/// <summary>
/// ProtectedTargetPolicy deny matrix (S4/S5): every protected target is refused through the primitive itself, whatever
/// the KB or a plan asks, and the apply path re-checks the policy so a denied target is never touched.
/// </summary>
public sealed class PolicyTests
{
    private const string ValorantExe = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>(primitive, args, expected rule).</summary>
    public static TheoryData<string, string, string> Denied => new()
    {
        // services
        { OpIds.ServiceSetStartType, "name=vgk;startType=Disabled", "protected.service.vgk" },
        { OpIds.ServiceStop, "name=vgk", "protected.service.vgk" },
        { OpIds.ServiceSetStartType, "name=vgc;startType=Disabled", "protected.service.vgc" },
        { OpIds.ServiceSetStartType, "name=vgc;startType=Manual", "protected.service.vgc" },
        { OpIds.ServiceStop, "name=vgc", "protected.service.vgc" },
        { OpIds.ServiceSetStartType, "name=vgc;startType=Automatic", "protected.service.vgc_precheck" },
        { OpIds.ServiceSetStartType, "name=WinDefend;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=BFE;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=mpssvc;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=EventLog;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=Winmgmt;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=TBS;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=Dnscache;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=wuauserv;startType=Disabled", "protected.service.protected" },
        { OpIds.ServiceStop, "name=SecurityHealthService", "protected.service.protected" },
        { OpIds.ServiceSetStartType, "name=iaStorAVC;startType=Disabled", "protected.service.boot_start" },
        { OpIds.ServiceSetStartType, "name=WSearch;startType=Disabled", "protected.service.shared_host" },
        { OpIds.ServiceStart, "name=RemoteRegistry", "protected.service.remote_access" },

        // devices
        { OpIds.DeviceDisable, @"instanceId=PCI\VEN_144D&DEV_A808\4&1", "protected.device.class" },
        { OpIds.DeviceDisable, @"instanceId=SCSI\DISK&VEN_NVME\5&1", "protected.device.class" },
        { OpIds.DeviceDisable, @"instanceId=PCI\VEN_10DE&DEV_2684\4&2", "protected.device.class" },
        { OpIds.DeviceDisable, @"instanceId=PCI\VEN_8086&DEV_7A60\3&1", "protected.device.class" },
        { OpIds.DeviceDisable, @"instanceId=USB\VID_046D&PID_C539\6&1", "protected.device.class" },
        { OpIds.DeviceDisable, @"instanceId=ACPI\PNP0A08\0", "protected.device.class" },
        { OpIds.DeviceDisable, @"instanceId=USB\VID_8087&PID_0032\BT1", "protected.device.bluetooth_radio" },
        { OpIds.DeviceDisable, @"instanceId=PCI\VEN_10EC&DEV_8168\R3ECEB671D921", "protected.default_route_nic" },
        { OpIds.DeviceDisable, @"instanceId=USB\VID_1532&PID_0084\COMPOSITE", "protected.device.ancestor" },

        // adapters
        { OpIds.NetAdapterSetAdvancedProperty, @"instanceId=PCI\VEN_10EC&DEV_8125\01000000684CE00000;keyword=NetworkAddress;value=001122334455", "protected.netadapter.keyword" },
        { OpIds.NetAdapterSetAdvancedProperty, @"instanceId=PCI\VEN_10EC&DEV_8125\01000000684CE00000;keyword=VlanID;value=5", "protected.netadapter.keyword" },
        { OpIds.NetAdapterUnbindIpv6, @"instanceId=BTHHFENUM\BTHHFPAUDIO\8&HFP1", "protected.netadapter.not_network" },
        { OpIds.NetAdapterSetMetric, @"instanceId=SCSI\DISK&VEN_NVME\5&1;metric=5", "protected.netadapter.not_network" },

        // registry: security (S5), allowlist, data
        { OpIds.RegistrySetValue, @"path=HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System;name=EnableLUA;kind=DWord;data=0", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SOFTWARE\Policies\Microsoft\Windows Defender;name=DisableAntiSpyware;kind=DWord;data=1", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard;name=EnableVirtualizationBasedSecurity;kind=DWord;data=0", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity;name=Enabled;kind=DWord;data=0", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State;name=UEFISecureBootEnabled;kind=DWord;data=0", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile;name=EnableFirewall;kind=DWord;data=0", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Services\vgk;name=Start;kind=DWord;data=4", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run;name=x;kind=String;data=c", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKCU\Software\Microsoft\Windows\CurrentVersion\Run;name=x;kind=String;data=c", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT.exe;name=Debugger;kind=String;data=x", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\Lsa;name=RunAsPPL;kind=DWord;data=0", "protected.registry.security" },
        { OpIds.RegistrySetValue, @"path=HKLM\SOFTWARE\Contoso;name=x;kind=DWord;data=1", "protected.registry.not_allowlisted" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=TdrDelay;kind=DWord;data=10", "protected.registry.not_allowlisted" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\ControlSet001\Control\GraphicsDrivers;name=HwSchMode;kind=DWord;data=1", "protected.registry.not_allowlisted" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode;kind=DWord;data=0", "protected.registry.data" },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode;kind=String;data=1", "protected.registry.data" },
        { OpIds.RegistrySetValue, @"path=HKCU\Software\Microsoft\DirectX\UserGpuPreferences;name=notapath;kind=String;data=GpuPreference=2;", "protected.registry.not_allowlisted" },
        { OpIds.RegistryDeleteValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode", "protected.registry.delete_not_allowed" },

        // power
        { OpIds.PowerCfgSetValue, "scheme=active;subgroup=0012ee47-9041-4b5d-9b77-535fba8b1442;setting=6738e2c4-e8a5-4a42-b16a-e040e769756e;ac=0", "protected.power.setting" },
        { OpIds.PowerCfgSetValue, "scheme=active;subgroup=54533251-82be-4824-96c1-47b60b740d00;setting=bc5038f7-23e0-4960-96da-33abaf5935ec;ac=50", "protected.power.value" },
        { OpIds.PowerCfgSetValue, "scheme=11111111-2222-3333-4444-555555555555;subgroup=54533251-82be-4824-96c1-47b60b740d00;setting=bc5038f7-23e0-4960-96da-33abaf5935ec;ac=100", "protected.power.scheme_missing" },
        { OpIds.PowerCfgSetActiveScheme, "scheme=11111111-2222-3333-4444-555555555555", "protected.power.scheme" },
        { OpIds.PowerCfgSetActiveScheme, "scheme=e9a42b02-d5df-448d-aa00-03f14749eb61", "protected.power.scheme_missing" },

        // app compat, IFEO
        { OpIds.AppCompatSetLayers, @"exePath=C:\Windows\notepad.exe;hive=HKCU;layers=~ RUNASADMIN", "protected.appcompat.not_riot" },
        { OpIds.AppCompatSetLayers, @"exePath=C:\Users\p\evil\VALORANT.exe.lnk;hive=HKCU;layers=RUNASADMIN", "protected.appcompat.not_riot" },
        { OpIds.AppCompatSetLayers, "exePath=" + ValorantExe + ";hive=HKCU;layers=WIN7RTM", "protected.appcompat.token" },
        { OpIds.IfeoResetMitigations, "imageName=notepad.exe", "protected.ifeo.not_riot" },
        { OpIds.IfeoRemovePerfOptions, "imageName=lsass.exe", "protected.ifeo.not_riot" },

        // files
        { OpIds.FileQuarantine, @"path=C:\Windows\System32\drivers\vgk.sys", "protected.file.not_allowlisted" },
        { OpIds.FileQuarantine, @"path=C:\Windows\System32\392667600.dat", "protected.file.not_allowlisted" },
        { OpIds.FileQuarantine, @"path=C:\Windows\System32\drivers\ene.sys", "protected.file.not_allowlisted" },
        { OpIds.FileQuarantine, @"path=C:\Windows\System32\config\SAM", "protected.file.not_allowlisted" },
        { OpIds.FileQuarantine, @"path=C:\Users\p\AppData\Local\Riot Games\392667600.dat", "protected.file.not_allowlisted" },
        { OpIds.FileQuarantine, @"path=C:\Windows\..\Windows\392667600.dat", "protected.args" },
        { OpIds.FileQuarantine, @"path=\\?\C:\Windows\392667600.dat", "protected.args" },
        { OpIds.FileQuarantine, @"path=C:\Windows\392667600.dat:stream", "protected.args" },

        // firewall, Defender, BCD
        { OpIds.FirewallAddRule, @"name=VTDiag X;program=C:\Windows\notepad.exe;direction=In", "protected.firewall.program" },
        { OpIds.FirewallRemoveRule, "name=VTDiag Foreign", "protected.firewall.not_ours" },
        { OpIds.DefenderAddExclusion, @"path=C:\Games", "protected.defender.path" },
        { OpIds.DefenderAddExclusion, @"path=D:\Riot Vanguard", "protected.defender.path" },
        { OpIds.DefenderAddExclusion, @"path=C:\Riot Games\Missing", "protected.defender.path_missing" },
        { OpIds.BcdSetValue, "element=testsigning;value=on", "protected.bcd.direction" },
        { OpIds.BcdSetValue, "element=nointegritychecks;value=on", "protected.bcd.direction" },
        { OpIds.BcdSetValue, "element=nx;value=OptOut", "protected.bcd.direction" },
        { OpIds.BcdSetValue, "element=hypervisorlaunchtype;value=Off", "protected.bcd.direction" },

        // processes, startup, misc
        { OpIds.ProcessCloseGraceful, "imageName=VALORANT-Win64-Shipping.exe", "protected.process.riot" },
        { OpIds.ProcessCloseGraceful, "imageName=vgc.exe", "protected.process.riot" },
        { OpIds.ProcessCloseGraceful, "imageName=RiotClientServices.exe", "protected.process.riot" },
        { OpIds.ProcessCloseGraceful, "imageName=explorer.exe", "protected.process.system" },
        { OpIds.ProcessCloseGraceful, "imageName=lsass.exe", "protected.process.system" },
        { OpIds.ProcessCloseGraceful, "imageName=MsMpEng.exe", "protected.process.system" },
        { OpIds.ProcessCloseGraceful, "imageName=VTDiag.Engine.exe", "protected.process.system" },
        { OpIds.StartupDisable, "location=HkcuRunOnce;name=Discord", "protected.startup.runonce" },
        { OpIds.StartupDisable, "location=HklmRun;name=Riot Vanguard", "protected.startup.protected_entry" },
        { OpIds.StartupDisable, "location=HklmRun;name=SecurityHealth", "protected.startup.protected_entry" },
        { OpIds.StartupDisable, "location=StartupFolderUser;name=..", "protected.args" },
        { OpIds.SystemProtectionEnable, "drive=D:", "protected.systemprotection.drive" },
        { OpIds.OneWaySfcDism, string.Empty, "protected.op.not_shipped_m4" },

        // argument shapes
        { OpIds.DeviceDisable, @"instanceId=PCI\..\X", "protected.args" },
        { OpIds.ServiceStop, @"name=..\vgk", "protected.args" },
        { OpIds.ProcessCloseGraceful, @"imageName=C:\Windows\explorer.exe", "protected.args" },
        { OpIds.RegistrySetValue, @"path=HKEY_LOCAL_MACHINE\SOFTWARE\X;name=a;kind=DWord;data=1", "protected.args" },
        { OpIds.RegistrySetValue, @"path=HKLM\SOFTWARE\..\SAM;name=a;kind=DWord;data=1", "protected.args" },
        { OpIds.ServiceSetStartType, "name=NahimicService;startType=Boot", "protected.args" },
        { OpIds.ServiceSetStartType, "name=NahimicService;startType=Disabled;extra=1", "protected.args" },
    };

    [Theory]
    [MemberData(nameof(Denied))]
    public async Task Protected_target_is_denied_and_never_touched(string opId, string argText, string rule)
    {
        using var rig = Rig.Create();
        Arrange(rig);
        var op = rig.Registry.FindBase(opId)!;
        var args = PrimitiveTests.Args(argText);
        var decision = await op.CheckPolicyAsync(args, Ct);
        Assert.False(decision.Allowed, opId + " " + argText + " was allowed");
        Assert.Equal(rule, decision.RuleId);
        Assert.NotNull(decision.ReasonKey);

        // The apply path re-checks: even with a forged capture, nothing changes.
        var fingerprint = rig.System.Fingerprint();
        var forged = Ops.Captures.Of(opId, new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "true", ["present"] = "true", ["running"] = "true" });
        var result = await op.ApplyAsync(args, forged, Ct);
        Assert.False(result.Success);
        Assert.Equal(fingerprint, rig.System.Fingerprint());
    }

    [Fact]
    public async Task Vgc_goes_Automatic_only_when_the_PreCheck_gate_is_definitely_true()
    {
        using var rig = Rig.Create();
        var op = rig.Registry.FindBase(OpIds.ServiceSetStartType)!;
        var args = PrimitiveTests.Args("name=vgc;startType=Automatic");
        foreach (var gate in new[] { Tri.Unknown, Tri.False, Tri.NotApplicable })
        {
            rig.Context.VgcAutomaticConfirmed = gate;
            Assert.False((await op.CheckPolicyAsync(args, Ct)).Allowed, gate.ToString());
        }

        rig.Context.VgcAutomaticConfirmed = Tri.True;
        Assert.True((await op.CheckPolicyAsync(args, Ct)).Allowed);
        Assert.False((await op.CheckPolicyAsync(PrimitiveTests.Args("name=vgc;startType=Disabled"), Ct)).Allowed);
    }

    [Fact]
    public async Task Stale_dat_is_quarantined_only_when_older_than_the_running_boot_and_Vanguard_is_not_running()
    {
        using var rig = Rig.Create();
        var op = rig.Registry.FindBase(OpIds.FileQuarantine)!;
        var args = PrimitiveTests.Args(@"path=%SystemRoot%\392667600.dat");
        var boot = rig.System.Boot.BootUtc!.Value;
        Assert.True((await op.CheckPolicyAsync(args, Ct)).Allowed, "stale (3 days before boot) and vgk stopped");

        // Rewritten this boot (Vanguard working, as on the dev PC): never touched.
        rig.System.Files.Add(@"C:\Windows\392667600.dat", [1, 2, 3, 4, 5], boot.AddMinutes(1));
        Assert.Equal("protected.file.vg_dat_not_stale", (await op.CheckPolicyAsync(args, Ct)).RuleId);

        // Written just before the boot (inside the margin): still treated as fresh.
        rig.System.Files.Add(@"C:\Windows\392667600.dat", [1, 2, 3, 4, 5], boot.AddSeconds(-30));
        Assert.Equal("protected.file.vg_dat_not_stale", (await op.CheckPolicyAsync(args, Ct)).RuleId);

        rig.System.Files.Add(@"C:\Windows\392667600.dat", [1, 2, 3, 4, 5], boot.AddDays(-2));
        rig.System.Boot.BootUtc = null;
        Assert.Equal("protected.file.vg_dat_staleness_unknown", (await op.CheckPolicyAsync(args, Ct)).RuleId);

        rig.System.Boot.BootUtc = boot;
        rig.System.Services.Mutate("vgk", s => s with { State = ServiceRun.Running });
        Assert.Equal("protected.file.vanguard_running", (await op.CheckPolicyAsync(args, Ct)).RuleId);
        Assert.Equal("protected.file.vanguard_running", (await op.CheckPolicyAsync(PrimitiveTests.Args(@"path=C:\Program Files\Riot Vanguard\vgkbootstatus.dat"), Ct)).RuleId);
    }

    [Fact]
    public async Task Unverifiable_facts_deny()
    {
        using var rig = Rig.Create();
        var disable = rig.Registry.FindBase(OpIds.DeviceDisable)!;
        var secondNic = PrimitiveTests.Args(@"instanceId=PCI\VEN_10EC&DEV_8125\01000000684CE00000");
        Assert.True((await disable.CheckPolicyAsync(secondNic, Ct)).Allowed);

        rig.System.Network.RouteUnknown = true;
        Assert.Equal("protected.default_route_nic", (await disable.CheckPolicyAsync(secondNic, Ct)).RuleId);
        rig.System.Network.RouteUnknown = false;

        rig.System.Devices.FailEnumeration = true;
        Assert.Equal("protected.device.unverifiable", (await disable.CheckPolicyAsync(secondNic, Ct)).RuleId);

        rig.Context.Player = null;
        var reg = rig.Registry.FindBase(OpIds.RegistrySetValue)!;
        Assert.Equal("protected.player_unknown", (await reg.CheckPolicyAsync(PrimitiveTests.Args(@"path=HKCU\Control Panel\Mouse;name=MouseSpeed;kind=String;data=0"), Ct)).RuleId);
    }

    [Fact]
    public void Nothing_on_the_registry_allowlist_touches_a_security_area()
    {
        foreach (var e in RegistryAllowlist.Entries)
        {
            Assert.False(RegistryAllowlist.SecurityDenied(e.SubKey), e.SubKey);
        }

        foreach (var (element, value) in ProtectedTargetPolicy.SecureBcdValues)
        {
            Assert.True(ProtectedTargetPolicy.CheckBcd(element, value).Allowed);
        }
    }

    [Fact]
    public void The_protected_lists_cover_the_design()
    {
        foreach (var s in new[] { "vgc", "vgk", "WinDefend", "BFE", "mpssvc", "EventLog", "Winmgmt", "TBS", "Dnscache", "wuauserv", "Schedule" })
        {
            Assert.Contains(s, ProtectedTargetPolicy.ProtectedServices, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var p in RiotProcessNames.All)
        {
            Assert.False(ProtectedTargetPolicy.CheckProcessClose(p).Allowed, p);
            Assert.False(ProtectedTargetPolicy.CheckProcessClose(p.ToUpperInvariant()).Allowed, p);
        }
    }

    private static void Arrange(Rig rig)
    {
        // A composite USB device (non-protected class) that hosts a keyboard: protected as an ancestor.
        rig.System.Devices.Add(@"USB\VID_1532&PID_0084\COMPOSITE", "{88bae032-5a81-49f0-bc3d-a4ff138216d6}", @"PCI\VEN_8086&DEV_7A60\3&1");
        rig.System.Devices.Add(@"HID\VID_1532&PID_0084&MI_01\KBD", "{4d36e96b-e325-11ce-bfc1-08002be10318}", @"USB\VID_1532&PID_0084\COMPOSITE");
        rig.System.Firewall.AddRule(new FirewallRuleInfo { Name = "VTDiag Foreign", Grouping = "Contoso", Direction = "In", Action = "Allow", Enabled = true });
        rig.System.Registry.SetString(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe");
        rig.System.Files.AddDirectory(@"C:\Riot Games\VALORANT");
    }
}
