using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis.Signals;

public static partial class BuiltinSignals
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string CdromClassGuid = "{4d36e965-e325-11ce-bfc1-08002be10318}";

    /// <summary>Overlay process names that are never offered for closing (voice chat, driver services).</summary>
    private static readonly HashSet<string> OverlayKeep = new(StringComparer.OrdinalIgnoreCase) { "Discord.exe", "nvcontainer.exe", "AMDRSServ.exe" };

    /// <summary>System modules that crash dumps commonly name but that are not a third-party cause on their own.</summary>
    private static readonly HashSet<string> CommonModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT-Win64-Shipping.exe", "VALORANT.exe", "RiotClientServices.exe", "ntdll.dll", "KERNELBASE.dll", "kernel32.dll",
        "ucrtbase.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "d3d11.dll", "dxgi.dll", "d3d12.dll",
        "nvwgf2umx.dll", "nvwgf2um.dll", "nvldumdx.dll", "amdxx64.dll", "atidxx64.dll", "amdxc64.dll", "igd10iumd64.dll", "igdumdim64.dll",
        "unknown", "combase.dll", "user32.dll", "win32u.dll", "gdi32full.dll",
    };

    private static IEnumerable<SignalResult> PowerPlan(SignalContext c)
    {
        var p = c.Snapshot.Power;
        var mode = p.Mode.TryGetValue(out var m) ? m : (PowerMode?)null;
        yield return Sig.FromFact("power.saver_plan", p.ActiveScheme, s => Sig.Of("power.saver_plan", s.Kind == PowerSchemeKind.PowerSaver || mode == PowerMode.BestPowerEfficiency));
        yield return Sig.FromFact("power.on_battery", p.Battery, b => b.Present ? Sig.Of("power.on_battery", !b.OnAc) : Sig.NotApplicable("power.on_battery"));
        yield return Sig.FromFact("power.cpu_max_limited", p.ProcessorMaxPercentAc, v => Sig.Of("power.cpu_max_limited", v < 100, 1.0, [Sig.Ev("power.cpu_max_limited", ("percent", Sig.Num(v)))]));
    }

    private static IEnumerable<SignalResult> RamConfig(SignalContext c)
    {
        var mem = c.Snapshot.Hardware.Memory;
        yield return Sig.FromFact("hw.ram_low", mem, x => Sig.Of("hw.ram_low", x.TotalMb < 12_288, 1.0, [Sig.Ev("hw.ram_low", ("mb", Sig.Num(x.TotalMb)))]));
        yield return Sig.FromFact("hw.ram_single_module", mem, x => x.Modules.Count == 0 ? Sig.Unknown("hw.ram_single_module", UnknownReason.NotCollected) : Sig.Of("hw.ram_single_module", x.Modules.Count == 1));
        yield return Sig.FromFact("hw.ram_below_rated", mem, x =>
        {
            var known = x.Modules.Where(mm => mm.RatedMts is > 0 && mm.ConfiguredMts is > 0).ToList();
            return known.Count == 0 ? Sig.Unknown("hw.ram_below_rated", UnknownReason.NotCollected)
                : Sig.Of("hw.ram_below_rated", known.Any(mm => mm.ConfiguredMts < mm.RatedMts * 0.9));
        });
    }

    private static IEnumerable<SignalResult> StandbyMem(SignalContext c)
    {
        yield return Sig.FromFact("hw.standby_mem_high", c.Snapshot.Hardware.Memory, x => x.StandbyMb is null
            ? Sig.Unknown("hw.standby_mem_high", UnknownReason.NotCollected)
            : Sig.Of("hw.standby_mem_high", x.StandbyMb.Value >= x.TotalMb * 0.5 && (x.AvailableMb ?? 0) < x.TotalMb * 0.6, 1.0,
                [Sig.Ev("hw.standby_mem_high", ("standby", Sig.Num(x.StandbyMb.Value)), ("total", Sig.Num(x.TotalMb)))]));
    }

    private static IEnumerable<SignalResult> BiosAge(SignalContext c)
    {
        var board = c.Snapshot.Hardware.Board;
        var today = DateOnly.FromDateTime(AsOf(c).UtcDateTime);
        foreach (var (id, years) in new[] { ("hw.bios_old", 2), ("hw.bios_very_old", 4) })
        {
            yield return Sig.FromFact(id, board, b => b.BiosReleaseDate is null
                ? Sig.Unknown(id, UnknownReason.NotCollected)
                : Sig.Of(id, b.BiosReleaseDate.Value < today.AddYears(-years), 1.0,
                    [Sig.Ev(id, ("date", b.BiosReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("version", b.BiosVersion ?? "?"))]));
        }
    }

    private static IEnumerable<SignalResult> HwExtras(SignalContext c)
    {
        var hw = c.Snapshot.Hardware;
        var laptop = hw.Board.TryGetValue(out var b) && b.IsLaptop is { } l ? l : (bool?)null;
        if (laptop is null && c.Snapshot.Power.Battery.TryGetValue(out var bat))
        {
            laptop = bat.Present;
        }

        yield return laptop is null ? Sig.Unknown("hw.laptop", UnknownReason.NotCollected) : Sig.Of("hw.laptop", laptop.Value);
        yield return Sig.FromFact("hw.cpu_x3d", hw.Cpu, cpu => Sig.Of("hw.cpu_x3d", cpu.Vendor == CpuVendor.Amd && cpu.Model.Contains("X3D", StringComparison.OrdinalIgnoreCase)));
    }

    private static EntityRef GpuEntity(GpuInfo g) => new() { Kind = EntityKind.Device, Id = g.InstanceId, DisplayName = g.Name };

    private static IEnumerable<SignalResult> IgpuState(SignalContext c)
    {
        var gpus = c.Snapshot.Hardware.Gpus;
        if (!gpus.TryGetValue(out var list))
        {
            var r = Sig.ReasonOf(gpus);
            return [Sig.Unknown("gpu.basic_display", r), Sig.Unknown("gpu.igpu_disabled", r), Sig.Unknown("gpu.igpu_present", r)];
        }

        var basic = list.Where(g => g.IsBasicDisplayAdapter).Select(GpuEntity).ToList();
        if (c.Snapshot.Devices.TryGetValue(out var devices))
        {
            basic.AddRange(devices.Where(d => d.Present && string.Equals(d.ClassGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase) && d.ProblemCode == 28)
                .Select(d => new EntityRef { Kind = EntityKind.Device, Id = d.InstanceId, DisplayName = d.ClassName }));
        }

        basic = [.. basic.DistinctBy(e => e.Id).OrderBy(e => e.Id, StringComparer.Ordinal)];
        var igpus = list.Where(g => g.IsIntegrated).ToList();
        var disabled = igpus.Where(g => !g.Enabled).Select(GpuEntity).ToList();
        return
        [
            basic.Count > 0 ? Sig.True("gpu.basic_display", 1.0, null, basic) : Sig.False("gpu.basic_display"),
            igpus.Count == 0 ? Sig.NotApplicable("gpu.igpu_disabled") : disabled.Count > 0 ? Sig.True("gpu.igpu_disabled", 1.0, null, disabled) : Sig.False("gpu.igpu_disabled"),
            Sig.Of("gpu.igpu_present", igpus.Count > 0),
        ];
    }

    private static IEnumerable<SignalResult> GpuDriver(SignalContext c)
    {
        var gpus = c.Snapshot.Hardware.Gpus;
        if (!gpus.TryGetValue(out var list))
        {
            var r = Sig.ReasonOf(gpus);
            return new[] { "gpu.amd_dgpu", "gpu.nvidia_dgpu", "gpu.driver_old", "gpu.problem_code" }.Select(i => Sig.Unknown(i, r));
        }

        var discrete = list.Where(g => !g.IsIntegrated && !g.IsBasicDisplayAdapter).ToList();
        var dated = discrete.Where(g => g.DriverDate is not null).ToList();
        var cutoff = DateOnly.FromDateTime(AsOf(c).UtcDateTime).AddDays(-365);
        var problems = list.Where(g => g.ProblemCode is not 0 and not 22).Select(GpuEntity).ToList();
        return
        [
            Sig.Of("gpu.amd_dgpu", discrete.Any(g => g.Vendor == GpuVendor.Amd)),
            Sig.Of("gpu.nvidia_dgpu", discrete.Any(g => g.Vendor == GpuVendor.Nvidia)),
            discrete.Count == 0 ? Sig.NotApplicable("gpu.driver_old") : dated.Count == 0 ? Sig.Unknown("gpu.driver_old", UnknownReason.NotCollected)
                : Sig.Of("gpu.driver_old", dated.Any(g => g.DriverDate < cutoff), 1.0, [Sig.Ev("gpu.driver_old", ("date", dated[0].DriverDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))]),
            problems.Count > 0 ? Sig.True("gpu.problem_code", 1.0, null, problems) : Sig.False("gpu.problem_code"),
        ];
    }

    private static IEnumerable<SignalResult> ShaderCache(SignalContext c)
    {
        const string Id = "gpu.driver_recently_changed";
        var since = DateOnly.FromDateTime(AsOf(c).UtcDateTime).AddDays(-7);
        if (c.Snapshot.Apps.TryGetValue(out var apps))
        {
            var drivers = apps.Where(a => a.DisplayName.StartsWith("NVIDIA Graphics Driver", StringComparison.OrdinalIgnoreCase)
                                          || a.DisplayName.StartsWith("AMD Software", StringComparison.OrdinalIgnoreCase)
                                          || a.DisplayName.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase)).ToList();
            if (drivers.Any(d => d.InstallDate is not null))
            {
                yield return Sig.Of(Id, drivers.Any(d => d.InstallDate >= since));
                yield break;
            }
        }

        if (c.Snapshot.Hardware.Gpus.TryGetValue(out var gpus) && gpus.Any(g => !g.IsIntegrated && g.DriverDate is not null))
        {
            // The driver date is the vendor's build date, not the install date: only a very fresh date counts.
            yield return Sig.Of(Id, gpus.Any(g => !g.IsIntegrated && g.DriverDate >= since.AddDays(-7)), 0.6);
            yield break;
        }

        yield return Sig.Unknown(Id, UnknownReason.NotCollected);
    }

    private static IEnumerable<SignalResult> IntelDriver(SignalContext c)
    {
        yield return Sig.FromFact("gpu.intel_driver_old", c.Snapshot.Hardware.Gpus, list =>
        {
            var intel = list.Where(g => g.Vendor == GpuVendor.Intel && g.IsIntegrated).ToList();
            if (intel.Count == 0)
            {
                return Sig.NotApplicable("gpu.intel_driver_old");
            }

            var cutoff = DateOnly.FromDateTime(AsOf(c).UtcDateTime).AddYears(-2);
            return intel.All(g => g.DriverDate is null) ? Sig.Unknown("gpu.intel_driver_old", UnknownReason.NotCollected)
                : Sig.Of("gpu.intel_driver_old", intel.Any(g => g.DriverDate < cutoff), 1.0,
                    [Sig.Ev("gpu.intel_driver_old", ("date", intel[0].DriverDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"))]);
        });
    }

    private static IEnumerable<SignalResult> GpuPref(SignalContext c)
    {
        var s = c.Snapshot;
        if (!s.Hardware.Gpus.TryGetValue(out var gpus))
        {
            var r = Sig.ReasonOf(s.Hardware.Gpus);
            return [Sig.Unknown("gpu.hybrid", r), Sig.Unknown("gpu.pref_not_high", r), Sig.Unknown("gpu.monitor_on_igpu", r)];
        }

        var igpu = gpus.Where(g => g.IsIntegrated && g.Enabled).ToList();
        var dgpu = gpus.Where(g => !g.IsIntegrated && g.Enabled && !g.IsBasicDisplayAdapter).ToList();
        var hybrid = igpu.Count > 0 && dgpu.Count > 0;
        var results = new List<SignalResult> { Sig.Of("gpu.hybrid", hybrid) };
        if (!hybrid)
        {
            results.Add(Sig.NotApplicable("gpu.pref_not_high"));
        }
        else if (!s.Riot.Valorant.TryGetValue(out var val) || !val.Installed || val.Path is null)
        {
            results.Add(s.Riot.Valorant.IsKnown ? Sig.NotApplicable("gpu.pref_not_high") : Sig.Unknown("gpu.pref_not_high", Sig.ReasonOf(s.Riot.Valorant)));
        }
        else
        {
            var exe = ValorantExePath(val.Path);
            results.Add(Sig.FromFact("gpu.pref_not_high", s.Display.GpuPreferences, prefs =>
            {
                var high = prefs.Any(p => string.Equals(p.ImageName, "VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) && p.Preference == 2);
                return high ? Sig.False("gpu.pref_not_high") : Sig.True("gpu.pref_not_high", 1.0, null, [new EntityRef { Kind = EntityKind.File, Id = exe, DisplayName = "VALORANT-Win64-Shipping.exe" }]);
            }));
        }

        var laptop = s.Hardware.Board.TryGetValue(out var board) && board.IsLaptop == true;
        if (dgpu.Count == 0 || igpu.Count == 0 || laptop)
        {
            results.Add(Sig.NotApplicable("gpu.monitor_on_igpu"));
        }
        else
        {
            results.Add(Sig.FromFact("gpu.monitor_on_igpu", s.Display.Monitors, mons =>
            {
                var primary = mons.FirstOrDefault(m => m.Primary);
                if (primary?.GpuInstanceId is null)
                {
                    return Sig.Unknown("gpu.monitor_on_igpu", UnknownReason.NotCollected);
                }

                return Sig.Of("gpu.monitor_on_igpu", igpu.Any(g => string.Equals(g.InstanceId, primary.GpuInstanceId, StringComparison.OrdinalIgnoreCase)));
            }));
        }

        return results;
    }

    private static IEnumerable<SignalResult> DisplayMode(SignalContext c)
    {
        var mons = c.Snapshot.Display.Monitors;
        yield return Sig.FromFact("display.multi_monitor", mons, m => Sig.Of("display.multi_monitor", m.Count >= 2));
        yield return Sig.FromFact("display.refresh_60", mons, m => m.FirstOrDefault(x => x.Primary) is { } p
            ? Sig.Of("display.refresh_60", p.RefreshHz <= 60, 1.0, [Sig.Ev("display.refresh_60", ("hz", Sig.Num(p.RefreshHz)))])
            : Sig.Unknown("display.refresh_60", UnknownReason.InsufficientData));
        yield return Sig.FromFact("display.hdr_on", mons, m => m.FirstOrDefault(x => x.Primary) is { } p ? Sig.FromNullable("display.hdr_on", p.HdrEnabled) : Sig.Unknown("display.hdr_on", UnknownReason.InsufficientData));
    }

    private static IEnumerable<SignalResult> HagsWindowed(SignalContext c)
    {
        var d = c.Snapshot.Display;
        yield return Sig.FromFact("display.hags_on", d.HagsEnabled);
        yield return Sig.FromFact("display.windowed_opt_on", d.WindowedOptimizations);
        yield return Sig.FromFact("display.game_mode_off", d.GameModeEnabled, invert: true);
        yield return Sig.FromFact("display.game_dvr_on", d.GameDvrEnabled);
    }

    private static IEnumerable<SignalResult> BtHfp(SignalContext c)
    {
        var eps = c.Snapshot.Audio.Endpoints;
        yield return Sig.FromFact("audio.bt_handsfree_present", eps, list =>
        {
            var hfp = list.Where(e => e.IsBluetoothHandsFree && e.State != AudioEndpointState.NotPresent).ToList();
            var ents = hfp.Where(e => e.DeviceInstanceId is not null).Select(e => new EntityRef { Kind = EntityKind.Device, Id = e.DeviceInstanceId!, DisplayName = e.Name })
                .DistinctBy(e => e.Id).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            return hfp.Count == 0 ? Sig.False("audio.bt_handsfree_present") : Sig.True("audio.bt_handsfree_present", 1.0, [Sig.Ev("audio.bt_handsfree_present", ("count", Sig.Num(hfp.Count)))], ents);
        });
        yield return Sig.FromFact("audio.bt_handsfree_default", eps, list =>
        {
            // Null IsDefault* = the default endpoint could not be read: never read that as "not default".
            var hfp = list.Where(e => e.IsBluetoothHandsFree && e.State == AudioEndpointState.Active).ToList();
            var state = Tri.False;
            foreach (var e in hfp)
            {
                var t = Or(e.IsDefault.ToTri(), e.IsDefaultCommunications.ToTri());
                if (t == Tri.True)
                {
                    state = Tri.True;
                    break;
                }

                if (t == Tri.Unknown)
                {
                    state = Tri.Unknown;
                }
            }

            return state == Tri.Unknown ? Sig.Unknown("audio.bt_handsfree_default", UnknownReason.Failed) : Sig.Of("audio.bt_handsfree_default", state == Tri.True);
        });
    }

    private static Tri Or(Tri a, Tri b) => a == Tri.True || b == Tri.True ? Tri.True : a == Tri.False && b == Tri.False ? Tri.False : Tri.Unknown;

    private static IEnumerable<SignalResult> AudioDevices(SignalContext c)
    {
        var eps = c.Snapshot.Audio.Endpoints;
        yield return Sig.FromFactAbsence("audio.no_default_output", eps, list =>
        {
            var outputs = list.Where(e => e.Flow == AudioFlow.Render && e.State == AudioEndpointState.Active).ToList();
            if (outputs.Any(e => e.IsDefault == true))
            {
                return Sig.False("audio.no_default_output");
            }

            return outputs.Any(e => e.IsDefault is null) ? Sig.Unknown("audio.no_default_output", UnknownReason.Failed) : Sig.True("audio.no_default_output");
        });
        yield return Sig.FromFact("audio.many_outputs", eps, list =>
        {
            var n = list.Count(e => e.Flow == AudioFlow.Render && e.State == AudioEndpointState.Active);
            return Sig.Of("audio.many_outputs", n >= 3, 1.0, [Sig.Ev("audio.many_outputs", ("count", Sig.Num(n)))]);
        });
    }

    private static IEnumerable<SignalResult> NahimicApo(SignalContext c)
    {
        const string Id = "apps.nahimic_apo";
        var nahimic = c.Kb.FindConflict("nahimic");
        if (nahimic is null || !c.Snapshot.Audio.Endpoints.TryGetValue(out var eps))
        {
            yield return Sig.Unknown(Id, c.Snapshot.Audio.Endpoints.IsKnown ? UnknownReason.Unsupported : Sig.ReasonOf(c.Snapshot.Audio.Endpoints));
            yield break;
        }

        var apos = eps.Any(e => e.Flow == AudioFlow.Render && e.State == AudioEndpointState.Active && e.ApoClsids.Count > 0);
        if (!apos)
        {
            yield return Sig.False(Id);
            yield break;
        }

        var present = ConflictMatcher.Evaluate(nahimic, c.Snapshot, AsOf(c)).First(r => r.Id.EndsWith(".any", StringComparison.Ordinal));
        yield return present.State == Tri.True ? Sig.True(Id, 0.8) : present.State == Tri.False ? Sig.False(Id) : Sig.Unknown(Id, present.UnknownReason ?? UnknownReason.NotCollected);
    }

    private static IEnumerable<SignalResult> PnpErrors(SignalContext c)
    {
        yield return Sig.FromFact("dev.problem_devices", c.Snapshot.Devices, devices =>
        {
            var bad = devices.Where(d => d.Present && d.ProblemCode is not 0 and not 22).OrderBy(d => d.InstanceId, StringComparer.Ordinal).ToList();
            return bad.Count == 0 ? Sig.False("dev.problem_devices")
                : Sig.True("dev.problem_devices", Math.Min(1.0, bad.Count / 2.0), [Sig.Ev("dev.problem_devices", ("count", Sig.Num(bad.Count)))],
                    [.. bad.Select(d => new EntityRef { Kind = EntityKind.Device, Id = d.InstanceId, DisplayName = d.ClassName })]);
        });
    }

    private static IEnumerable<SignalResult> UsbDevices(SignalContext c)
    {
        yield return Sig.FromFact("dev.usb_optical_drive", c.Snapshot.Devices, devices =>
        {
            var hits = devices.Where(d => d.Present && (string.Equals(d.ClassGuid, CdromClassGuid, StringComparison.OrdinalIgnoreCase) || string.Equals(d.ClassName, "CDROM", StringComparison.OrdinalIgnoreCase))
                                          && string.Equals(d.Enumerator, "USBSTOR", StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Count == 0 ? Sig.False("dev.usb_optical_drive")
                : Sig.True("dev.usb_optical_drive", 1.0, null, [.. hits.Select(d => new EntityRef { Kind = EntityKind.Device, Id = d.InstanceId, DisplayName = d.ClassName })]);
        });
    }

    private static IEnumerable<SignalResult> DiskDirty(SignalContext c)
    {
        yield return Sig.FromFact("disk.dirty", c.Snapshot.Storage.Volumes, vols =>
        {
            if (vols.Any(v => v.Dirty == true))
            {
                return Sig.True("disk.dirty", 1.0, [Sig.Ev("disk.dirty", ("items", Sig.List(vols.Where(v => v.Dirty == true).Select(v => v.DriveLetter))))]);
            }

            return vols.Count > 0 && vols.All(v => v.Dirty == false) ? Sig.False("disk.dirty") : Sig.Unknown("disk.dirty", Limited(c) ? UnknownReason.NeedsAdmin : UnknownReason.NotCollected);
        });
    }

    private static IEnumerable<SignalResult> DiskType(SignalContext c)
    {
        var st = c.Snapshot.Storage;
        SignalResult systemHdd = Sig.FromFact("disk.system_hdd", st.Disks, disks =>
        {
            var sys = disks.FirstOrDefault(d => d.IsSystemDisk);
            return sys is null || sys.Media == DiskMediaType.Unknown ? Sig.Unknown("disk.system_hdd", UnknownReason.InsufficientData) : Sig.Of("disk.system_hdd", sys.Media == DiskMediaType.Hdd);
        });
        yield return systemHdd;
        if (!st.Disks.TryGetValue(out var all))
        {
            yield return Sig.Unknown("disk.riot_on_hdd", Sig.ReasonOf(st.Disks));
            yield break;
        }

        if (all.Count > 0 && all.All(d => d.Media is DiskMediaType.Ssd or DiskMediaType.Scm))
        {
            yield return Sig.False("disk.riot_on_hdd");
            yield break;
        }

        var onSystem = c.Snapshot.Riot.Valorant.TryGetValue(out var val) ? val.OnSystemDrive : null;
        yield return onSystem == true ? systemHdd with { Id = "disk.riot_on_hdd" } : Sig.Unknown("disk.riot_on_hdd", UnknownReason.InsufficientData);
    }

    private static IEnumerable<SignalResult> DiskFree(SignalContext c)
    {
        yield return Sig.FromFact("disk.free_low", c.Snapshot.Storage.Volumes, vols => vols.FirstOrDefault(v => v.IsSystem) is { } sys
            ? Sig.Of("disk.free_low", sys.FreeMb < 10_240, 1.0, [Sig.Ev("disk.free_low", ("free", Sig.Num(sys.FreeMb / 1024)), ("drive", sys.DriveLetter))])
            : Sig.Unknown("disk.free_low", UnknownReason.InsufficientData));
    }

    private static IEnumerable<SignalResult> Vmd(SignalContext c)
    {
        yield return Sig.FromFact("storage.intel_vmd", c.Snapshot.Storage.Controllers, ctrls =>
            Sig.Of("storage.intel_vmd", ctrls.Any(x => x.IsVmd || string.Equals(x.DriverFile, "iaStorVD.sys", StringComparison.OrdinalIgnoreCase))));
    }

    private static IEnumerable<SignalResult> AppCrashModule(SignalContext c)
    {
        string[] ids = ["events.riot_crash_recent", "events.riot_crash_hook_module", "events.riot_crash_unknown_module"];
        if (!c.Snapshot.Events.AppCrashes.TryGetValue(out var crashes))
        {
            var r = Sig.ReasonOf(c.Snapshot.Events.AppCrashes);
            return ids.Select(i => Sig.Unknown(i, r));
        }

        var recent = crashes.Where(x => x.Utc >= AsOf(c) - ConflictMatcher.CrashWindow).ToList();
        var known = KnownModules(c.Kb);

        // Modules of conflicts that a dedicated rule covers (Nahimic, Bonjour...) count there, not as generic hooks.
        var dedicated = c.Kb.Rules.Where(r => r.Kind == VTD.Contracts.Knowledge.RuleKind.Hypothesis)
            .SelectMany(r => r.Supersedes).Where(s => s.StartsWith("conflict-", StringComparison.Ordinal))
            .Select(s => c.Kb.FindConflict(s["conflict-".Length..])).Where(x => x is not null)
            .SelectMany(x => x!.Detect.ModuleNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hook = recent.Where(x => x.Module is not null && known.Contains(x.Module) && !dedicated.Contains(x.Module)).ToList();
        var unknown = recent.Where(x => x.Module is not null && !known.Contains(x.Module) && !CommonModules.Contains(x.Module)).ToList();
        return
        [
            Sig.Of(ids[0], recent.Count > 0, Math.Min(1.0, recent.Count / 3.0), [Sig.Ev(ids[0], ("count", Sig.Num(recent.Count)))]),
            hook.Count > 0 ? Sig.True(ids[1], Math.Min(1.0, hook.Count / 2.0), [Sig.Ev(ids[1], ("module", Sig.List(hook.Select(h => h.Module!))), ("count", Sig.Num(hook.Count)))]) : Sig.False(ids[1]),
            unknown.Count > 0 ? Sig.True(ids[2], Math.Min(1.0, unknown.Count / 2.0), [Sig.Ev(ids[2], ("module", Sig.List(unknown.Select(h => h.Module!))), ("count", Sig.Num(unknown.Count)))]) : Sig.False(ids[2]),
        ];
    }

    /// <summary>Faulting-module names any conflict entry knows.</summary>
    internal static HashSet<string> KnownModules(IKnowledgeBase kb)
        => kb.Conflicts.SelectMany(x => x.Detect.ModuleNames).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<SignalResult> HookProcs(SignalContext c)
    {
        const string Id = "proc.overlay_running";
        if (!c.Snapshot.Processes.TryGetValue(out var procs))
        {
            yield return Sig.Unknown(Id, Sig.ReasonOf(c.Snapshot.Processes));
            yield break;
        }

        var names = c.Kb.Conflicts.Where(x => x.Kind is ConflictKind.Overlay or ConflictKind.Monitoring)
            .SelectMany(x => x.Detect.ProcessNames).Where(n => !OverlayKeep.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = procs.Where(p => !p.IsProtectedRiotProcess && names.Contains(p.Name)).Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        yield return running.Count == 0 ? Sig.False(Id)
            : Sig.True(Id, Math.Min(1.0, 0.5 + (running.Count * 0.25)), [Sig.Ev(Id, ("items", Sig.List(running)))],
                [.. running.Select(ProcessEntity)]);
    }

    private static IEnumerable<SignalResult> ConflictAppsInstalled(SignalContext c)
    {
        yield return Aggregate(c, "apps.conflict_installed", x => x.Confidence == ConfidenceLevel.High && x.Kind is not ConflictKind.Driver, ".installed");
    }

    private static IEnumerable<SignalResult> OtherAnticheat(SignalContext c)
    {
        yield return Aggregate(c, "apps.other_anticheat", x => x.Kind == ConflictKind.Anticheat, ".installed", ".driver_loaded", ".service_present");
    }

    private static IEnumerable<SignalResult> OemTools(SignalContext c)
    {
        yield return Aggregate(c, "apps.oem_tools", x => x.Kind is ConflictKind.RgbVendor or ConflictKind.NetworkOptimizer, ".installed");
    }

    private static SignalResult Aggregate(SignalContext c, string id, Func<ConflictDef, bool> which, params string[] suffixes)
    {
        var parts = new List<SignalResult>();
        var names = new List<string>();
        foreach (var def in c.Kb.Conflicts.Where(which).OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var rs = ConflictMatcher.Evaluate(def, c.Snapshot, AsOf(c)).Where(r => suffixes.Any(s => r.Id.EndsWith(s, StringComparison.Ordinal))).ToList();
            parts.AddRange(rs);
            if (rs.Any(r => r.State == Tri.True))
            {
                names.Add(def.Name);
            }
        }

        var any = Sig.Any(id, parts);
        return any.State == Tri.True ? any with { Evidence = [Sig.Ev(id, ("items", Sig.List(names, 4)))] } : any;
    }

    /// <summary>
    /// Counts matching events: the built-in event digest first (by query id, or provider and event id), then the pack
    /// collector result. Unknown only when neither source is available.
    /// </summary>
    private static SignalResult EventSignal(SignalContext c, string id, string collectorId, string[] providers, int[] eventIds, int withinDays, int minCount, int saturateAt)
    {
        var since = AsOf(c).AddDays(-withinDays);
        int? count = null;
        if (c.Snapshot.Events.Hits.TryGetValue(out var hits))
        {
            count = hits.Where(h => h.LastUtc >= since
                                    && (string.Equals(h.QueryId, collectorId, StringComparison.Ordinal)
                                        || (providers.Contains(h.Provider, StringComparer.OrdinalIgnoreCase) && (eventIds.Length == 0 || eventIds.Contains(h.EventId)))))
                .Sum(h => h.Count);
        }

        var pack = c.Snapshot.Pack.FirstOrDefault(p => string.Equals(p.CollectorId, collectorId, StringComparison.Ordinal));
        if (pack is not null && pack.Data.TryGetValue(out var data) && (data.LastUtc is null || data.LastUtc >= since))
        {
            count = Math.Max(count ?? 0, data.Count);
        }
        else if (count is null && pack is not null && !pack.Data.IsKnown)
        {
            return Sig.Unknown(id, Sig.ReasonOf(pack.Data));
        }

        if (count is null)
        {
            return Sig.Unknown(id, Sig.ReasonOf(c.Snapshot.Events.Hits));
        }

        // Some event queries failed (a partial digest) and the pack collector did not give a complete count: too few
        // events is not known to be False.
        var complete = c.Snapshot.Events.Hits.IsComplete || (pack is not null && pack.Data.TryGetValue(out var pd) && !pd.Truncated);
        if (count < minCount && !complete)
        {
            return Sig.Unknown(id, UnknownReason.Incomplete);
        }

        return count >= minCount
            ? Sig.True(id, Math.Min(1.0, count.Value / (double)Math.Max(saturateAt, 1)), [Sig.Ev(id, ("count", Sig.Num(count.Value)), ("days", Sig.Num(withinDays)))])
            : Sig.False(id);
    }
}
