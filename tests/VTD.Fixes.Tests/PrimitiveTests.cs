using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Fixes.Ops;
using VTD.Fixes.Ports;
using VTD.Fixes.Tests.Fakes;
using VTD.Testing.Fakes;

namespace VTD.Fixes.Tests;

/// <summary>
/// The registry implements exactly <see cref="OpCatalog"/>, and every primitive round-trips: capture → apply → probe equals
/// the intended state → undo → probe hash equals the capture hash (bit-identical for Exact primitives).
/// </summary>
public sealed class PrimitiveTests
{
    private const string ValorantExe = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";
    private const string SecondNic = @"PCI\VEN_10EC&DEV_8125\01000000684CE00000";
    private const string DefaultNic = @"PCI\VEN_10EC&DEV_8168\R3ECEB671D921";
    private const string Hfp = @"BTHHFENUM\BTHHFPAUDIO\8&HFP1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Exact primitives with a setup and arguments.</summary>
    public static TheoryData<string, string> ExactCases => new()
    {
        { OpIds.ServiceSetStartType, "name=NahimicService;startType=Disabled" },
        { OpIds.ServiceSetStartType, "name=RtkAudioUniversalService;startType=Manual;delayed=false" },
        { OpIds.ServiceStop, "name=NahimicService;timeoutSec=20" },
        { OpIds.ServiceStart, "name=vgc;timeoutSec=30" },
        { OpIds.DeviceDisable, "instanceId=" + Hfp },
        { OpIds.DeviceEnable, "instanceId=" + SecondNic },
        { OpIds.NetAdapterSetAdvancedProperty, "instanceId=" + SecondNic + ";keyword=*EEE;value=0" },
        { OpIds.NetAdapterSetMetric, "instanceId=" + SecondNic + ";metric=automatic" },
        { OpIds.NetAdapterSetMetric, "instanceId=" + DefaultNic + ";metric=10" },
        { OpIds.NetAdapterUnbindIpv6, "instanceId=" + SecondNic },
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode;kind=DWord;data=1" },
        { OpIds.RegistrySetValue, @"path=HKCU\Software\Microsoft\DirectX\UserGpuPreferences;name=" + ValorantExe + ";kind=String;data=GpuPreference=2;" },
        { OpIds.RegistryDeleteValue, @"path=HKLM\SOFTWARE\Microsoft\Windows\Dwm;name=OverlayTestMode" },
        { OpIds.PowerCfgSetValue, "scheme=active;subgroup=54533251-82be-4824-96c1-47b60b740d00;setting=bc5038f7-23e0-4960-96da-33abaf5935ec;ac=100" },
        { OpIds.PowerCfgSetValue, "scheme=active;subgroup=2a737441-1930-4402-8d77-b2bebba308a3;setting=48e6b7a6-50f5-4782-a5d4-53bb8f07e226;ac=0;dc=0" },
        { OpIds.PowerCfgSetActiveScheme, "scheme=8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" },
        { OpIds.AppCompatSetLayers, "exePath=" + ValorantExe + ";hive=HKCU;layers=~ DISABLEDXMAXIMIZEDWINDOWEDMODE" },
        { OpIds.AppCompatRemoveLayers, "exePath=" + ValorantExe + ";hive=HKCU;layers=WIN7RTM" },
        { OpIds.IfeoResetMitigations, "imageName=VALORANT-Win64-Shipping.exe" },
        { OpIds.IfeoRemovePerfOptions, "imageName=VALORANT-Win64-Shipping.exe" },
        { OpIds.FileQuarantine, @"path=%SystemRoot%\392667600.dat" },
        { OpIds.FileQuarantine, @"path=C:\Program Files\Riot Vanguard\vgkbootstatus.dat" },
        { OpIds.FirewallAddRule, "name=VTDiag Valorant;program=" + ValorantExe + ";direction=In" },
        { OpIds.FirewallRemoveRule, "name=VTDiag Old" },
        { OpIds.DefenderAddExclusion, @"path=C:\Riot Games\VALORANT" },
        { OpIds.BcdSetValue, "element=testsigning;value=off" },
        { OpIds.StartupDisable, "location=HkcuRun;name=Discord" },
    };

    [Fact]
    public void Registry_metadata_equals_OpCatalog()
    {
        using var rig = Rig.Create();
        Assert.Equal(OpCatalog.All.Select(o => o.Op).Order(StringComparer.Ordinal), rig.Registry.All.Select(o => o.Op).Order(StringComparer.Ordinal));
        foreach (var spec in OpCatalog.All)
        {
            var op = rig.Registry.FindBase(spec.Op)!;
            Assert.Equal(spec.Reversibility, op.Reversibility);
            Assert.Equal(spec.RiskFloor, op.RiskFloor);
            Assert.Equal(spec.RequiresAdmin, op.RequiresAdmin);
            Assert.Equal(spec.Reboot, op.Reboot);
            Assert.Equal(spec.AffectsNetwork, op.AffectsNetwork);
            Assert.Equal(spec.StateKeys, op.StateKeys);
            Assert.Equal(spec.Args.Count, op.Args.Count);
            for (var i = 0; i < spec.Args.Count; i++)
            {
                Assert.Equal(spec.Args[i].Name, op.Args[i].Name);
                Assert.Equal(spec.Args[i].Type, op.Args[i].Type);
                Assert.Equal(spec.Args[i].Required, op.Args[i].Required);
                Assert.Equal(spec.Args[i].Pattern, op.Args[i].Pattern);
                Assert.Equal(spec.Args[i].AllowedValues, op.Args[i].AllowedValues);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ExactCases))]
    public async Task Exact_primitive_round_trips_bit_identically(string opId, string argText)
    {
        using var rig = Rig.Create();
        Setup(rig);
        var op = rig.Registry.FindBase(opId)!;
        var args = Args(argText);
        Assert.True((await op.CheckPolicyAsync(args, Ct)).Allowed, opId + " policy: " + (await op.CheckPolicyAsync(args, Ct)).RuleId);
        var before = await op.CaptureAsync(args, Ct);
        Assert.Equal(op.StateKeys.Order(StringComparer.Ordinal), before.Data.Keys.Where(k => !k.StartsWith('_')).Order(StringComparer.Ordinal));
        var intended = op.Intended(args, before);
        Assert.False(op.Same(args, before, intended), opId + " test setup must need a change");
        var everything = rig.System.Fingerprint();

        var applied = await op.ApplyAsync(args, before, Ct);
        Assert.True(applied.Success, opId + " apply: " + applied.ErrorCode);
        var after = await op.ProbeAsync(args, Ct);
        Assert.True(op.Same(args, after, intended), opId + ": the probe after apply is not the intended state");
        Assert.NotEqual(everything, rig.System.Fingerprint());

        var again = await op.ApplyAsync(args, before, Ct);
        Assert.True(again.Success && again.Detail == "already", opId + " apply is not idempotent");

        var undone = await op.UndoAsync(args, before, after, UndoMode.IfUnchanged, Ct);
        Assert.True(undone.Success, opId + " undo: " + undone.ErrorCode);
        var restored = await op.ProbeAsync(args, Ct);
        Assert.Equal(before.Hash, restored.Hash);
        Assert.Equal(everything, rig.System.Fingerprint());

        var twice = await op.UndoAsync(args, before, after, UndoMode.IfUnchanged, Ct);
        Assert.True(twice.Success, opId + " undo is not idempotent");
    }

    [Theory]
    [MemberData(nameof(ExactCases))]
    public async Task Undo_refuses_when_someone_else_changed_the_target(string opId, string argText)
    {
        using var rig = Rig.Create();
        Setup(rig);
        var op = rig.Registry.FindBase(opId)!;
        var args = Args(argText);
        var before = await op.CaptureAsync(args, Ct);
        Assert.True((await op.ApplyAsync(args, before, Ct)).Success);
        var after = await op.ProbeAsync(args, Ct);

        // "Someone else": pretend the state we left was different, so the current state matches neither.
        var expectedOther = Captures.Of(op.Op, after.Data.ToDictionary(kv => kv.Key, kv => kv.Value + "-other", StringComparer.Ordinal));
        var refused = await op.UndoAsync(args, before, expectedOther, UndoMode.IfUnchanged, Ct);
        Assert.False(refused.Success);
        Assert.Equal("undo.conflict", refused.ErrorCode);
        Assert.True(op.Same(args, await op.ProbeAsync(args, Ct), after), "a refused undo changed something");

        var forced = await op.UndoAsync(args, before, expectedOther, UndoMode.Force, Ct);
        Assert.True(forced.Success, forced.ErrorCode);
        Assert.Equal(before.Hash, (await op.ProbeAsync(args, Ct)).Hash);
    }

    [Fact]
    public async Task One_way_and_session_only_primitives_never_fake_an_undo()
    {
        using var rig = Rig.Create();
        foreach (var (opId, argText, tool) in new[] { (OpIds.DnsFlush, string.Empty, (SystemTool?)SystemTool.FlushDns), (OpIds.OneWayWinsockReset, string.Empty, SystemTool.WinsockReset), (OpIds.OneWayIpReset, string.Empty, SystemTool.IpReset) })
        {
            var op = rig.Registry.FindBase(opId)!;
            var args = Args(argText);
            var before = await op.CaptureAsync(args, Ct);
            var r = await op.ApplyAsync(args, before, Ct);
            Assert.True(r.Success, opId + ": " + r.ErrorCode);
            Assert.Contains(tool!.Value, rig.System.Tools.Runs);
            var u = await op.UndoAsync(args, before, r.After, UndoMode.Force, Ct);
            Assert.False(u.Success);
            Assert.Equal("undo.one_way", u.ErrorCode);
        }

        var close = rig.Registry.FindBase(OpIds.ProcessCloseGraceful)!;
        var cargs = Args("imageName=RTSS.exe");
        var cb = await close.CaptureAsync(cargs, Ct);
        Assert.Equal("true", cb.Get("running"));
        Assert.True((await close.ApplyAsync(cargs, cb, Ct)).Success);
        Assert.Equal("false", (await close.ProbeAsync(cargs, Ct)).Get("running"));
        var cu = await close.UndoAsync(cargs, cb, null, UndoMode.Force, Ct);
        Assert.False(cu.Success);
        Assert.Equal("undo.session_only", cu.ErrorCode);
    }

    [Fact]
    public async Task Close_never_kills_and_reports_a_process_that_stays()
    {
        using var rig = Rig.Create();
        var op = rig.Registry.FindBase(OpIds.ProcessCloseGraceful)!;
        var args = Args("imageName=Stubborn.exe");
        var before = await op.CaptureAsync(args, Ct);
        var task = op.ApplyAsync(args, before, Ct).AsTask();
        for (var i = 0; i < 60 && !task.IsCompleted; i++)
        {
            rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
            await Task.Delay(1, Ct);
        }

        var r = await task;
        Assert.False(r.Success);
        Assert.Equal("process.still_running", r.ErrorCode);
        Assert.Contains(rig.System.Processes.Snapshot(), p => p.ImageName == "Stubborn.exe");

        // A service process with no window in the console session is not touched.
        var svc = await op.ApplyAsync(Args("imageName=NahimicSvc64.exe"), await op.CaptureAsync(Args("imageName=NahimicSvc64.exe"), Ct), Ct);
        Assert.True(svc.Success && svc.Detail == "already", "a session-0 process must not be matched: " + svc.ErrorCode);
        Assert.DoesNotContain(4103, rig.System.Processes.Posted);
    }

    [Fact]
    public async Task Best_effort_primitives_undo_without_harm()
    {
        using var rig = Rig.Create();
        rig.Probe.Set(GuardIds.ClockOk, _ => new VTD.Contracts.Analysis.SignalResult
        {
            Id = GuardIds.ClockOk,
            State = Tri.False,
            Evidence = [new VTD.Contracts.Analysis.Evidence { TextKey = "x", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["skewSeconds"] = "3600.4" } }],
        });
        var time = rig.Registry.FindBase(OpIds.TimeResync)!;
        var none = Args(string.Empty);
        var before = await time.CaptureAsync(none, Ct);
        Assert.Equal("3600", before.Get("skewSeconds"));
        rig.System.Tools.OnRun = t => rig.Probe.Set(GuardIds.ClockOk, Tri.True);
        Assert.True((await time.ApplyAsync(none, before, Ct)).Success);
        Assert.Contains(SystemTool.TimeResync, rig.System.Tools.Runs);
        Assert.True((await time.UndoAsync(none, before, null, UndoMode.Force, Ct)).Success);

        var sp = rig.Registry.FindBase(OpIds.SystemProtectionEnable)!;
        var drive = Args("drive=C:");
        var spBefore = await sp.CaptureAsync(drive, Ct);
        Assert.Equal("false", spBefore.Get("enabled"));
        Assert.True((await sp.ApplyAsync(drive, spBefore, Ct)).Success);
        Assert.True(rig.System.SystemProtection.IsEnabled("C:"));
        Assert.True((await sp.UndoAsync(drive, spBefore, null, UndoMode.Force, Ct)).Success);
        Assert.True(rig.System.SystemProtection.IsEnabled("C:"), "undo must never switch System Protection off (it deletes restore points)");
    }

    [Fact]
    public async Task Adapter_property_the_driver_does_not_declare_is_skipped_and_values_are_checked_against_the_driver()
    {
        using var rig = Rig.Create();
        var op = rig.Registry.FindBase(OpIds.NetAdapterSetAdvancedProperty)!;
        var args = Args("instanceId=" + SecondNic + ";keyword=GigaLite;value=0");
        var before = await op.CaptureAsync(args, Ct);
        Assert.Equal("false", before.Get("_declared"));
        Assert.True(op.Same(args, before, op.Intended(args, before)));

        var bad = Args("instanceId=" + SecondNic + ";keyword=*EEE;value=7");
        var b = await op.CaptureAsync(bad, Ct);
        var r = await op.ApplyAsync(bad, b, Ct);
        Assert.False(r.Success);
        Assert.Equal("netadapter.value_not_allowed", r.ErrorCode);
        Assert.Empty(rig.System.Devices.Restarts);
    }

    [Fact]
    public async Task Quarantine_moves_never_deletes_and_refuses_reparse_points_and_occupied_targets()
    {
        using var rig = Rig.Create();
        var op = rig.Registry.FindBase(OpIds.FileQuarantine)!;
        var args = Args(@"path=%SystemRoot%\392667600.dat");
        var before = await op.CaptureAsync(args, Ct);
        Assert.True((await op.ApplyAsync(args, before, Ct)).Success);
        var (from, to) = Assert.Single(rig.System.Files.Moves);
        Assert.Equal(@"C:\Windows\392667600.dat", from);
        Assert.StartsWith(@"C:\ProgramData\VTDiag\quarantine\", to, StringComparison.Ordinal);
        Assert.True(rig.System.Files.Probe(to).Exists);
        Assert.Contains(rig.System.Files.Manifests.Values, m => m["originalPath"] == from && m["sha256"] == before.Get("sha256"));

        // The original came back (Vanguard rewrote it): undo must not overwrite or delete it.
        rig.System.Files.Add(@"C:\Windows\392667600.dat", [7, 7, 7], rig.Clock.GetUtcNow());
        var after = Captures.Of(op.Op, ("exists", "false"), ("sha256", before.Get("sha256")), ("quarantinePath", to));
        var u = await op.UndoAsync(args, before, after, UndoMode.Force, Ct);
        Assert.False(u.Success);
        Assert.Equal("undo.target_exists", u.ErrorCode);
        Assert.True(rig.System.Files.Probe(to).Exists, "the quarantined copy must survive a refused undo");

        using var rig2 = Rig.Create();
        rig2.System.Files.ReparsePoints.Add(@"C:\Program Files\Riot Vanguard");
        var op2 = rig2.Registry.FindBase(OpIds.FileQuarantine)!;
        var a2 = Args(@"path=C:\Program Files\Riot Vanguard\vgkbootstatus.dat");
        rig2.System.Files.ReparsePoints.Remove(@"C:\Program Files\Riot Vanguard");
        var b2 = await op2.CaptureAsync(a2, Ct);
        rig2.System.Files.ReparsePoints.Add(@"C:\Program Files\Riot Vanguard");
        var r2 = await op2.ApplyAsync(a2, b2, Ct);
        Assert.False(r2.Success);
        Assert.Empty(rig2.System.Files.Moves);
    }

    [Fact]
    public async Task Probes_accept_the_partial_arguments_verify_uses()
    {
        using var rig = Rig.Create();
        var reg = rig.Registry.FindBase(OpIds.RegistrySetValue)!;
        var p = await reg.ProbeAsync(Args(@"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode"), Ct);
        Assert.Equal("2", p.Get("data"));
        var scheme = rig.Registry.FindBase(OpIds.PowerCfgSetActiveScheme)!;
        Assert.Equal(FakeSystem.Balanced.ToString("D"), (await scheme.ProbeAsync(Args(string.Empty), Ct)).Get("activeScheme"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await reg.CaptureAsync(Args(@"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode"), Ct));
    }

    /// <summary>Per-case setup on top of <see cref="FakeSystem.Typical"/>.</summary>
    /// <param name="rig">Rig.</param>
    internal static void Setup(Rig rig)
    {
        rig.System.Devices.Mutate(SecondNic, d => d with { Enabled = false, ProblemCode = 22 });
        rig.System.Registry.SetDword(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5);
        rig.System.Firewall.AddRule(new FirewallRuleInfo { Name = "VTDiag Old", Grouping = "VTDiag", Program = ValorantExe, Direction = "Out", Action = "Allow", Enabled = true });
        rig.System.Bcd.Set(BcdElement.TestSigning, "on");
        rig.System.Power.Values[(FakeSystem.Balanced, FakeSystem.UsbSubgroup, FakeSystem.UsbSuspend, false)] = 1;
    }

    /// <summary>Parses <c>k=v;k=v</c> (a value may contain <c>=</c>; <c>;</c> inside the GPU preference data is re-joined).</summary>
    /// <param name="text">Text.</param>
    internal static OpArgs Args(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastKey = null;
        foreach (var part in text.Split(';'))
        {
            if (part.Length == 0)
            {
                if (lastKey is not null && map[lastKey].StartsWith("GpuPreference=", StringComparison.Ordinal))
                {
                    map[lastKey] += ";";
                }

                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                map[lastKey!] += ";" + part;
                continue;
            }

            lastKey = part[..eq];
            map[lastKey] = part[(eq + 1)..];
        }

        return new OpArgs { Values = map };
    }
}
