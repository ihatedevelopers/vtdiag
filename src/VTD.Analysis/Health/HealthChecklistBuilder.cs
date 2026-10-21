using System.Globalization;
using VTD.Analysis.Ranking;
using VTD.Contracts;
using VTD.Contracts.Analysis;

namespace VTD.Analysis.Health;

/// <summary>
/// The health baseline, computed regardless of the complaint (design_B.md section 5): Vanguard readiness, Windows,
/// drivers, network, background apps, storage, services. Unknown is shown as "could not check", never as OK.
/// </summary>
internal static class HealthChecklistBuilder
{
    private enum Bad
    {
        Fail,
        Warn,
    }

    public static HealthChecklist Build(IReadOnlyDictionary<string, SignalResult> s)
    {
        var items = new List<HealthItem>
        {
            Item("vanguard.tpm2", HealthGroup.VanguardReadiness, s, "sec.tpm2", okWhen: Tri.True, Bad.Fail, guide: "guide.tpm.enable"),
            Item("vanguard.secure_boot", HealthGroup.VanguardReadiness, s, "sec.secureboot.on", Tri.True, Bad.Fail, guide: "guide.secureboot.enable"),
            Item("vanguard.secure_boot_keys", HealthGroup.VanguardReadiness, s, "sec.secureboot.setup_mode", Tri.False, Bad.Fail, guide: "guide.secureboot.enable"),
            Both("vanguard.uefi_gpt", HealthGroup.VanguardReadiness, s, "fw.uefi", "disk.system_gpt", "guide.secureboot.enable"),
            Item("vanguard.hvci", HealthGroup.VanguardReadiness, s, "sec.hvci.running", Tri.True, Bad.Warn, guide: "guide.hvci.enable"),
            Item("vanguard.iommu", HealthGroup.VanguardReadiness, s, "sec.kernel_dma_protection", Tri.True, Bad.Warn, guide: "guide.iommu.enable"),
            Item("vanguard.virtualization", HealthGroup.VanguardReadiness, s, "sec.virtualization.enabled", Tri.True, Bad.Warn, guide: "guide.virtualization.enable"),
            WindowsBuild(s),
            Item("vanguard.test_signing_off", HealthGroup.VanguardReadiness, s, "sec.bcd.testsigning", Tri.False, Bad.Fail, guide: "guide.bcd.restore_security"),
            Item("vanguard.nx", HealthGroup.VanguardReadiness, s, "sec.bcd.nx_off", Tri.False, Bad.Fail, guide: "guide.bcd.restore_security"),
            Item("windows.pending_reboot", HealthGroup.Windows, s, "os.pending_reboot", Tri.False, Bad.Warn),
            Item("windows.clock", HealthGroup.Windows, s, "os.clock_skewed", Tri.False, Bad.Fail, fix: "fix.os.time_resync", guide: "guide.clock.fix"),
            Item("windows.modified_image", HealthGroup.Windows, s, "os.modified_windows", Tri.False, Bad.Warn),
            Item("windows.not_vm", HealthGroup.Windows, s, "os.vm", Tri.False, Bad.Fail),
            Item("drivers.problem_devices", HealthGroup.Drivers, s, "dev.problem_devices", Tri.False, Bad.Warn),
            Either("drivers.gpu", HealthGroup.Drivers, s, "gpu.problem_code", "gpu.basic_display", "guide.gpu.clean_driver"),
            Item("network.connectivity", HealthGroup.Network, s, "net.no_internet", Tri.False, Bad.Fail),
            Item("network.single_connection", HealthGroup.Network, s, "net.multi_nic_up", Tri.False, Bad.Warn, fix: "fix.net.disable_second_adapter"),
            Either("background.conflicts", HealthGroup.BackgroundApps, s, "proc.overlay_running", "apps.conflict_installed", "guide.apps.close_overlays", Bad.Warn),
            Item("storage.free_space", HealthGroup.Storage, s, "disk.free_low", Tri.False, Bad.Warn),
            Item("storage.disk_ok", HealthGroup.Storage, s, "disk.dirty", Tri.False, Bad.Warn, guide: "guide.storage.chkdsk"),
            Vanguard(s),
            Item("services.vgk_loaded", HealthGroup.Services, s, "vg.vgk_running", Tri.True, Bad.Warn, guide: "guide.vanguard.real_restart"),
            Item("services.required", HealthGroup.Services, s, "os.required_services_disabled", Tri.False, Bad.Fail, guide: "guide.vanguard.reinstall"),
        };
        return new HealthChecklist { Items = items };
    }

    private static HealthItem Item(string id, HealthGroup group, IReadOnlyDictionary<string, SignalResult> s, string signal, Tri okWhen, Bad bad, string? fix = null, string? guide = null)
    {
        var r = HypothesisScorer.State(s, signal);
        var status = r.State switch
        {
            Tri.Unknown => HealthStatus.Unknown,
            Tri.NotApplicable => HealthStatus.NotApplicable,
            _ when r.State == okWhen => HealthStatus.Ok,
            _ => bad == Bad.Fail ? HealthStatus.Fail : HealthStatus.Warn,
        };
        return Make(id, group, status, signal, r, status is HealthStatus.Fail or HealthStatus.Warn ? fix : null, status is HealthStatus.Fail or HealthStatus.Warn ? guide : null);
    }

    private static HealthItem Both(string id, HealthGroup group, IReadOnlyDictionary<string, SignalResult> s, string a, string b, string guide)
    {
        var ra = HypothesisScorer.State(s, a);
        var rb = HypothesisScorer.State(s, b);
        var status = ra.State == Tri.False || rb.State == Tri.False ? HealthStatus.Fail
            : ra.State == Tri.True && rb.State == Tri.True ? HealthStatus.Ok
            : HealthStatus.Unknown;
        var basis = ra.State == Tri.Unknown ? ra : rb;
        return Make(id, group, status, ra.State == Tri.False ? a : b, basis, null, status == HealthStatus.Fail ? guide : null);
    }

    private static HealthItem Either(string id, HealthGroup group, IReadOnlyDictionary<string, SignalResult> s, string a, string b, string guide, Bad bad = Bad.Fail)
    {
        var ra = HypothesisScorer.State(s, a);
        var rb = HypothesisScorer.State(s, b);
        var status = ra.State == Tri.True || rb.State == Tri.True ? (bad == Bad.Fail ? HealthStatus.Fail : HealthStatus.Warn)
            : ra.State is Tri.False or Tri.NotApplicable && rb.State is Tri.False or Tri.NotApplicable ? HealthStatus.Ok
            : HealthStatus.Unknown;
        var basis = ra.State == Tri.True || ra.State == Tri.Unknown ? ra : rb;
        return Make(id, group, status, basis.Id, basis, null, status is HealthStatus.Fail or HealthStatus.Warn ? guide : null);
    }

    private static HealthItem WindowsBuild(IReadOnlyDictionary<string, SignalResult> s)
    {
        var supported = HypothesisScorer.State(s, "os.build_supported");
        var old = HypothesisScorer.State(s, "os.build_below_19045");
        var status = supported.State switch
        {
            Tri.False => HealthStatus.Fail,
            Tri.True => old.State == Tri.True ? HealthStatus.Warn : HealthStatus.Ok,
            _ => HealthStatus.Unknown,
        };
        return Make("vanguard.windows_build", HealthGroup.VanguardReadiness, status, "os.build_supported", supported, null,
            status is HealthStatus.Fail or HealthStatus.Warn ? "guide.windows.update" : null);
    }

    private static HealthItem Vanguard(IReadOnlyDictionary<string, SignalResult> s)
    {
        var installed = HypothesisScorer.State(s, "vg.installed");
        var disabled = HypothesisScorer.State(s, "vg.vgc_disabled");
        var status = installed.State switch
        {
            Tri.False => HealthStatus.Fail,
            Tri.True when disabled.State == Tri.True => HealthStatus.Fail,
            Tri.True when disabled.State is Tri.False or Tri.NotApplicable => HealthStatus.Ok,
            _ => HealthStatus.Unknown,
        };
        return Make("services.vanguard", HealthGroup.Services, status, "vg.installed", installed.State == Tri.Unknown ? installed : disabled, null,
            status == HealthStatus.Fail ? "guide.vanguard.reinstall" : null);
    }

    private static HealthItem Make(string id, HealthGroup group, HealthStatus status, string signal, SignalResult basis, string? fix, string? guide)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        UnknownReason? reason = null;
        if (status == HealthStatus.Unknown)
        {
            reason = basis.UnknownReason ?? UnknownReason.NotCollected;
            args["reason"] = "reason." + char.ToLowerInvariant(reason.Value.ToString()[0]) + reason.Value.ToString()[1..];
        }

        var count = basis.Evidence.SelectMany(e => e.Args).FirstOrDefault(a => string.Equals(a.Key, "count", StringComparison.Ordinal)).Value;
        if (count is not null && status is HealthStatus.Warn or HealthStatus.Fail)
        {
            args["count"] = count;
        }

        return new HealthItem
        {
            Id = id,
            Group = group,
            Status = status,
            TitleKey = $"health.{id}.title",
            DetailKey = "health.detail." + status.ToString().ToLowerInvariant(),
            Args = args,
            UnknownReason = reason,
            SignalId = signal,
            FixId = fix,
            GuideId = guide,
        };
    }
}
