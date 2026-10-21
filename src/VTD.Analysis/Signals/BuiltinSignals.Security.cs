using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis.Signals;

public static partial class BuiltinSignals
{
    private static readonly string[] PriorityImages = ["VALORANT-Win64-Shipping.exe", "VALORANT.exe", "vgc.exe", "RiotClientServices.exe"];

    private static IEnumerable<SignalResult> SecureBootKeys(SignalContext c)
    {
        var sec = c.Snapshot.Security;
        yield return Sig.FromFact("sec.secureboot.setup_mode", sec.SecureBootSetupMode);
        yield return Sig.FromFact("sec.secureboot.db_missing_ca2023", sec.SecureBootDbHasCa2023, invert: true);
    }

    private static IEnumerable<SignalResult> UefiGpt(SignalContext c)
    {
        yield return Sig.FromFact("fw.uefi", c.Snapshot.Firmware.Type, t => Sig.Of("fw.uefi", t == FirmwareType.Uefi));
        yield return Sig.FromFact("disk.system_gpt", c.Snapshot.Storage.SystemDiskPartitionStyle, s => Sig.Of("disk.system_gpt", s == PartitionStyle.Gpt));
    }

    private static IEnumerable<SignalResult> Tpm(SignalContext c)
    {
        var tpm = c.Snapshot.Security.Tpm;
        if (!tpm.TryGetValue(out var t))
        {
            var r = Sig.ReasonOf(tpm);
            return new[] { "sec.tpm.present", "sec.tpm2", "sec.tpm.not_ready", "sec.tpm.firmware" }.Select(id => Sig.Unknown(id, r));
        }

        if (!t.Present)
        {
            return [Sig.False("sec.tpm.present"), Sig.False("sec.tpm2"), Sig.NotApplicable("sec.tpm.not_ready"), Sig.NotApplicable("sec.tpm.firmware")];
        }

        var spec = t.SpecVersion;
        var tpm2 = spec is null ? Sig.Unknown("sec.tpm2", UnknownReason.NotCollected)
            : Sig.Of("sec.tpm2", spec.TrimStart().StartsWith('2'), 1.0, [Sig.Ev("sec.tpm2", ("version", spec))]);
        var notReady = t.Ready is null ? Sig.Unknown("sec.tpm.not_ready", Limited(c) ? UnknownReason.NeedsAdmin : UnknownReason.NotCollected) : Sig.Of("sec.tpm.not_ready", !t.Ready.Value);
        var firmware = t.Kind == TpmKind.Unknown ? Sig.Unknown("sec.tpm.firmware", UnknownReason.NotCollected) : Sig.Of("sec.tpm.firmware", t.Kind == TpmKind.Firmware);
        return [Sig.True("sec.tpm.present"), tpm2, notReady, firmware];
    }

    private static IEnumerable<SignalResult> Hvci(SignalContext c)
    {
        var vbs = c.Snapshot.Security.Vbs;
        yield return Sig.FromFact("sec.hvci.running", vbs, v => Sig.Of("sec.hvci.running", v.HvciRunning));
        yield return Sig.FromFact("sec.hvci.configured_not_running", vbs, v => Sig.Of("sec.hvci.configured_not_running", v.HvciConfigured && !v.HvciRunning));
        yield return Sig.FromFact("sec.vbs.running", vbs, v => Sig.Of("sec.vbs.running", v.Status == VbsStatus.Running));
    }

    private static IEnumerable<SignalResult> HvciDrivers(SignalContext c)
    {
        yield return Sig.FromFact("sec.hvci.incompatible_drivers", c.Snapshot.Security.HvciIncompatibleDrivers, list =>
            list.Count == 0 ? Sig.False("sec.hvci.incompatible_drivers")
                : Sig.True("sec.hvci.incompatible_drivers", 1.0, [Sig.Ev("sec.hvci.incompatible_drivers", ("items", Sig.List(list)))],
                    [.. list.Order(StringComparer.OrdinalIgnoreCase).Select(d => new EntityRef { Kind = EntityKind.Driver, Id = d, DisplayName = d })]));
    }

    private static IEnumerable<SignalResult> Iommu(SignalContext c)
    {
        var sec = c.Snapshot.Security;
        yield return Sig.FromFact("sec.kernel_dma_protection", sec.KernelDmaProtection);
        yield return Sig.FromFact("sec.iommu_available", sec.Vbs, v => v.AvailableSecurityProperties.Count == 0
            ? Sig.Unknown("sec.iommu_available", UnknownReason.InsufficientData)
            : Sig.Of("sec.iommu_available", v.AvailableSecurityProperties.Contains(3)));
    }

    private static IEnumerable<SignalResult> Virtualization(SignalContext c)
    {
        var sec = c.Snapshot.Security;
        var enabled = Sig.FromFact("sec.virtualization.enabled", sec.VirtualizationFirmwareEnabled);
        if (enabled.State == Tri.Unknown && sec.HypervisorPresent.TryGetValue(out var hyp) && hyp)
        {
            enabled = Sig.True("sec.virtualization.enabled");
        }

        yield return enabled;
        yield return Sig.FromFact("sec.hypervisor_present", sec.HypervisorPresent);
        yield return Sig.FromFact("sec.bcd.hypervisor_off", sec.Bcd, b => b.HypervisorLaunchType is null
            ? Sig.Unknown("sec.bcd.hypervisor_off", UnknownReason.NotCollected)
            : Sig.Of("sec.bcd.hypervisor_off", string.Equals(b.HypervisorLaunchType, "Off", StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<SignalResult> Ifeo(SignalContext c)
    {
        var sec = c.Snapshot.Security;
        yield return Sig.FromFact("sec.bcd.testsigning", sec.Bcd, b =>
        {
            if (b.TestSigning == true || b.NoIntegrityChecks == true || b.Debug == true)
            {
                var on = new List<string>();
                if (b.TestSigning == true)
                {
                    on.Add("testsigning");
                }

                if (b.NoIntegrityChecks == true)
                {
                    on.Add("nointegritychecks");
                }

                if (b.Debug == true)
                {
                    on.Add("debug");
                }

                return Sig.True("sec.bcd.testsigning", 1.0, [Sig.Ev("sec.bcd.testsigning", ("items", Sig.List(on)))]);
            }

            return b.TestSigning is null && b.NoIntegrityChecks is null ? Sig.Unknown("sec.bcd.testsigning", UnknownReason.NotCollected) : Sig.False("sec.bcd.testsigning");
        });
        yield return Sig.FromFact("sec.bcd.nx_off", sec.Bcd, b => b.Nx is null ? Sig.Unknown("sec.bcd.nx_off", UnknownReason.NotCollected)
            : Sig.Of("sec.bcd.nx_off", string.Equals(b.Nx, "AlwaysOff", StringComparison.OrdinalIgnoreCase)));
        yield return Sig.FromFact("sec.ifeo_riot_debugger", sec.Ifeo, entries =>
        {
            var hits = entries.Where(e => (RiotProcessNames.IsProtected(e.ImageName) || PriorityImages.Contains(e.ImageName, StringComparer.OrdinalIgnoreCase)) && e.HasDebugger)
                .Select(e => e.ImageName).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return hits.Count == 0 ? Sig.False("sec.ifeo_riot_debugger")
                : Sig.True("sec.ifeo_riot_debugger", 1.0, [Sig.Ev("sec.ifeo_riot_debugger", ("items", Sig.List(hits)))], [.. hits.Select(ProcessEntity)]);
        });
        yield return Sig.FromFact("sec.exploit_override_riot", sec.ExploitOverrides, all =>
        {
            var list = all.Where(x => RiotProcessNames.IsProtected(x.ImageName) || PriorityImages.Contains(x.ImageName, StringComparer.OrdinalIgnoreCase)
                                      || string.Equals(x.ImageName, "vgk.sys", StringComparison.OrdinalIgnoreCase)).ToList();
            return list.Count == 0 ? Sig.False("sec.exploit_override_riot")
                : Sig.True("sec.exploit_override_riot", 1.0, [Sig.Ev("sec.exploit_override_riot", ("items", Sig.List(list.Select(x => x.ImageName))))],
                    [.. list.Select(x => x.ImageName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(ProcessEntity)]);
        });
    }

    private static IEnumerable<SignalResult> Priority(SignalContext c)
    {
        yield return Sig.FromFact("sec.ifeo_perfoptions_riot", c.Snapshot.Security.Ifeo, entries =>
        {
            // PerfOptions CpuPriorityClass: 3 = High, 4 = Realtime. Normal/Above normal are fine.
            var hits = entries.Where(e => (RiotProcessNames.IsProtected(e.ImageName) || PriorityImages.Contains(e.ImageName, StringComparer.OrdinalIgnoreCase))
                                          && e.CpuPriorityClass is 3 or 4)
                .Select(e => e.ImageName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return hits.Count == 0 ? Sig.False("sec.ifeo_perfoptions_riot")
                : Sig.True("sec.ifeo_perfoptions_riot", 1.0, [Sig.Ev("sec.ifeo_perfoptions_riot", ("items", Sig.List(hits)))], [.. hits.Select(ProcessEntity)]);
        });
    }

    private static IEnumerable<SignalResult> AvProducts(SignalContext c)
    {
        var sec = c.Snapshot.Security;
        yield return Sig.FromFact("sec.third_party_av", sec.AntivirusProducts, avs =>
        {
            var third = avs.Where(a => !a.IsDefender && a.Enabled != false).Select(a => a.Name).ToList();
            return third.Count == 0 ? Sig.False("sec.third_party_av") : Sig.True("sec.third_party_av", 1.0, [Sig.Ev("sec.third_party_av", ("items", Sig.List(third)))]);
        });
        yield return Sig.FromFact("sec.defender_cfa_on", sec.Defender, d => Sig.FromNullable("sec.defender_cfa_on", d.ControlledFolderAccess));
        yield return Sig.FromFact("sec.defender_disabled_policy", sec.Defender, d => Sig.FromNullable("sec.defender_disabled_policy", d.DisabledByPolicy));
        yield return Sig.FromFact("sec.smart_app_control_on", sec.Defender, d => d.SmartAppControl is null
            ? Sig.Unknown("sec.smart_app_control_on", UnknownReason.NotCollected)
            : Sig.Of("sec.smart_app_control_on", string.Equals(d.SmartAppControl, "on", StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<SignalResult> WinBuild(SignalContext c)
    {
        var os = c.Snapshot.Os;
        yield return Sig.FromFact("os.build_supported", os.Build, b => Sig.Of("os.build_supported", b >= 19041, 1.0, [Sig.Ev("os.build_supported", ("build", Sig.Num(b)))]));
        yield return Sig.FromFact("os.build_below_19045", os.Build, b => Sig.Of("os.build_below_19045", b < 19045, 1.0, [Sig.Ev("os.build_supported", ("build", Sig.Num(b)))]));
        yield return Sig.FromFact("os.win10", os.Build, b => Sig.Of("os.win10", b < 22000));
        yield return os.InsiderBranch.Status switch
        {
            FactStatus.NotApplicable => Sig.False("os.insider"),
            FactStatus.Known => Sig.Of("os.insider", !string.IsNullOrWhiteSpace(os.InsiderBranch.Value), 1.0, [Sig.Ev("os.insider", ("branch", os.InsiderBranch.Value))]),
            _ => Sig.Unknown("os.insider", Sig.ReasonOf(os.InsiderBranch)),
        };
    }

    private static IEnumerable<SignalResult> Runtimes(SignalContext c)
    {
        yield return Sig.FromFact("os.windows_n", c.Snapshot.Os.EditionId, e => Sig.Of("os.windows_n", e.EndsWith('N') && !e.EndsWith("EvaluationN", StringComparison.Ordinal)));
        yield return Sig.FromFactAbsence("apps.vcredist_missing", c.Snapshot.Apps, apps =>
        {
            var has = apps.Any(a => a.DisplayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
                                    && a.DisplayName.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
                                    && (a.DisplayName.Contains("2015", StringComparison.Ordinal) || a.DisplayName.Contains("2017", StringComparison.Ordinal)
                                        || a.DisplayName.Contains("2019", StringComparison.Ordinal) || a.DisplayName.Contains("2022", StringComparison.Ordinal)
                                        || a.DisplayName.Contains("v14", StringComparison.OrdinalIgnoreCase)));
            return Sig.Of("apps.vcredist_missing", !has);
        });
    }

    private static IEnumerable<SignalResult> PendingReboot(SignalContext c)
    {
        var os = c.Snapshot.Os;
        yield return Sig.FromFact("os.pending_reboot", os.PendingReboot, p =>
        {
            var why = new List<string>();
            if (p.ComponentServicing)
            {
                why.Add("CBS");
            }

            if (p.WindowsUpdate)
            {
                why.Add("WindowsUpdate");
            }

            if (p.PendingFileRename)
            {
                why.Add("PendingFileRename");
            }

            return why.Count > 0 ? Sig.True("os.pending_reboot", 1.0, [Sig.Ev("os.pending_reboot", ("items", Sig.List(why)))]) : Sig.False("os.pending_reboot");
        });
        yield return Sig.FromFact("os.wu_installing", os.WindowsUpdateInstalling);
        var fast = Sig.FromFact("os.fast_startup_on", os.FastStartupEnabled);
        if (fast.State == Tri.True && os.HibernationEnabled.TryGetValue(out var hib) && !hib)
        {
            fast = Sig.False("os.fast_startup_on");
        }

        yield return fast;
    }

    private static IEnumerable<SignalResult> Clock(SignalContext c)
    {
        var clock = c.Snapshot.Os.Clock;
        yield return Sig.FromFact("os.clock_skewed", clock, k => k.SkewSeconds is null
            ? Sig.Unknown("os.clock_skewed", UnknownReason.InsufficientData)
            : Sig.Of("os.clock_skewed", Math.Abs(k.SkewSeconds.Value) > 300, 1.0, [Sig.Ev("os.clock_skewed", ("seconds", Sig.Num(k.SkewSeconds.Value)))]));
        yield return Sig.FromFact("os.auto_time_off", clock, k => k.AutoTimeSync is null ? Sig.Unknown("os.auto_time_off", UnknownReason.NotCollected) : Sig.Of("os.auto_time_off", !k.AutoTimeSync.Value));
    }

    private static IEnumerable<SignalResult> EnvVars(SignalContext c)
    {
        yield return Sig.FromFact("os.env_vars_broken", c.Snapshot.Os.EnvironmentIssues, list =>
            list.Count == 0 ? Sig.False("os.env_vars_broken") : Sig.True("os.env_vars_broken", 1.0, [Sig.Ev("os.env_vars_broken", ("items", Sig.List(list.Select(v => v.Name))))]));
    }

    private static IEnumerable<SignalResult> Hotfix(SignalContext c)
    {
        yield return Sig.FromFact("os.hotfix_kb5101650", c.Snapshot.Os.Hotfixes, list => Sig.Of("os.hotfix_kb5101650", list.Any(h => string.Equals(h.Id, "KB5101650", StringComparison.OrdinalIgnoreCase))));
    }

    private static IEnumerable<SignalResult> OsExtras(SignalContext c)
    {
        var s = c.Snapshot;
        yield return Sig.FromFact("os.modified_windows", s.Os.ModifiedWindowsMarkers, m =>
            m.Count == 0 ? Sig.False("os.modified_windows") : Sig.True("os.modified_windows", 1.0, [Sig.Ev("os.modified_windows", ("items", Sig.List(m)))]));
        yield return Sig.FromFact("os.required_services_disabled", s.Services, svcs =>
        {
            string[] required = ["vgc", "vgk", "TBS", "Winmgmt", "BFE", "Dnscache", "EventLog", "mpssvc"];
            var off = svcs.Where(x => required.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && x.StartType == ServiceStartType.Disabled)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return off.Count == 0 ? Sig.False("os.required_services_disabled")
                : Sig.True("os.required_services_disabled", 1.0, [Sig.Ev("os.required_services_disabled", ("items", Sig.List(off.Select(x => x.Name))))],
                    [.. off.Select(x => new EntityRef { Kind = EntityKind.Service, Id = x.Name, DisplayName = x.DisplayName ?? x.Name })]);
        });
        yield return Sig.FromFact("os.vm", s.Os.IsVirtualMachine);
        yield return Sig.FromFact("hw.cpu_arm", s.Hardware.Cpu, cpu => Sig.Of("hw.cpu_arm", cpu.Vendor == CpuVendor.Arm || string.Equals(cpu.Architecture, "arm64", StringComparison.OrdinalIgnoreCase)));
    }

    private static EntityRef ProcessEntity(string image) => new() { Kind = EntityKind.Process, Id = image, DisplayName = image };
}
