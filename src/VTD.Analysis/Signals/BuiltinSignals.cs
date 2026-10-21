using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis.Signals;

/// <summary>
/// Built-in signal providers: one <see cref="CheckSignalProvider"/> per catalog <c>check_id</c> (data/catalog.json), plus a
/// few extra providers (check id <c>-</c>). The ids and their meaning are declared in <c>tools/kb_curation/signals.py</c>
/// (KB <c>signals/builtin.json</c>); a test keeps both sides equal. Every provider reads only the snapshot (and answers
/// where noted), treats an Unknown fact as Unknown, and never matches on localized names.
/// </summary>
public static partial class BuiltinSignals
{
    /// <summary>All built-in providers.</summary>
    public static IReadOnlyList<CheckSignalProvider> All { get; } = Build();

    private static List<CheckSignalProvider> Build()
    {
        var list = new List<CheckSignalProvider>();
        void Add(string check, string[] ids, Func<SignalContext, IEnumerable<SignalResult>> f) => list.Add(new CheckSignalProvider(check, ids, f));

        // network
        Add("realtek_nic", ["net.realtek25.present", "net.realtek25.enabled", "net.realtek25.unused", "net.realtek25.active"], RealtekNic);
        Add("realtek_events", ["events.realtek_nic_resets", "events.vgm_crash_0xc0000409"], RealtekEvents);
        Add("multi_nic", ["net.multi_nic_up", "net.secondary_adapter_up"], MultiNic);
        Add("nic_power", ["net.nic_power_saving_on"], NicPower);
        Add("wifi", ["net.on_wifi", "net.wifi_weak", "net.wifi_2_4ghz"], Wifi);
        Add("ping_route", ["net.gateway_loss", "net.region_ping_high", "net.region_loss", "net.region_jitter_high", "net.no_internet"], PingRoute);
        Add("dns_ipv6", ["net.active_adapter", "net.active_ipv6_bound", "net.ipv6_disabled", "net.teredo_on"], DnsIpv6);
        Add("tcp_stack", ["net.lsp_third_party"], TcpStack);
        Add("mdns", ["net.mdns_lsp", "apps.mdns_component"], Mdns);
        Add("virtual_nic_vpn", ["net.vpn_adapter_up", "net.virtual_adapter_present", "net.proxy_on"], VirtualNic);
        Add("firewall_rules", ["net.firewall_blocks_riot", "net.firewall_off"], Firewall);

        // Vanguard readiness and security
        Add("secure_boot", ["sec.secureboot.on"], c => [Sig.FromFact("sec.secureboot.on", c.Snapshot.Security.SecureBootEnabled)]);
        Add("sb_keys", ["sec.secureboot.setup_mode", "sec.secureboot.db_missing_ca2023"], SecureBootKeys);
        Add("uefi_gpt", ["fw.uefi", "disk.system_gpt"], UefiGpt);
        Add("tpm", ["sec.tpm.present", "sec.tpm2", "sec.tpm.not_ready", "sec.tpm.firmware"], Tpm);
        Add("tpm_events", ["events.tpm_errors"], c => [EventSignal(c, "events.tpm_errors", "tpm_errors", ["TPM", "Microsoft-Windows-TPM-WMI"], [14, 17], 30, 1, 5)]);
        Add("hvci", ["sec.hvci.running", "sec.hvci.configured_not_running", "sec.vbs.running"], Hvci);
        Add("hvci_incompat_drivers", ["sec.hvci.incompatible_drivers"], HvciDrivers);
        Add("iommu_dma", ["sec.kernel_dma_protection", "sec.iommu_available"], Iommu);
        Add("virtualization", ["sec.virtualization.enabled", "sec.hypervisor_present", "sec.bcd.hypervisor_off"], Virtualization);
        Add("dualboot", ["fw.dualboot"], c => [Sig.FromFact("fw.dualboot", c.Snapshot.Firmware.OtherOsBootEntries, n => Sig.Of("fw.dualboot", n > 0))]);
        Add("ifeo", ["sec.bcd.testsigning", "sec.bcd.nx_off", "sec.ifeo_riot_debugger", "sec.exploit_override_riot"], Ifeo);
        Add("priority", ["sec.ifeo_perfoptions_riot"], Priority);
        Add("av_products", ["sec.third_party_av", "sec.defender_cfa_on", "sec.defender_disabled_policy", "sec.smart_app_control_on"], AvProducts);
        Add("-", ["sec.bitlocker_on"], c => [Sig.FromFact("sec.bitlocker_on", c.Snapshot.Security.SystemVolumeBitLocker, b => Sig.Of("sec.bitlocker_on", b is BitLockerStatus.On or BitLockerStatus.InProgress))]);

        // Windows
        Add("win_build", ["os.build_supported", "os.build_below_19045", "os.win10", "os.insider"], WinBuild);
        Add("runtimes", ["os.windows_n", "apps.vcredist_missing"], Runtimes);
        Add("pending_reboot", ["os.pending_reboot", "os.wu_installing", "os.fast_startup_on"], PendingReboot);
        Add("clock", ["os.clock_skewed", "os.auto_time_off"], Clock);
        Add("env_vars", ["os.env_vars_broken"], EnvVars);
        Add("hotfix", ["os.hotfix_kb5101650"], Hotfix);
        Add("-", ["os.modified_windows", "os.required_services_disabled", "os.vm", "hw.cpu_arm"], OsExtras);

        // Vanguard
        Add("vgc_service", ["vg.installed", "vg.vgc_missing", "vg.vgk_missing", "vg.vgc_disabled", "vg.vgc_running", "vg.vgk_running", "vg.vgc_logs_recent", "vg.vgc_crash_loop", "vg.vgc_never_started", "vg.vgc_automatic_needed"], VgcService);
        Add("vgk_files", ["vg.vgk_not_loaded", "vg.files_missing", "vg.bootstatus_dat"], VgkFiles);
        Add("stale_dat", ["vg.stale_dat"], StaleDat);
        Add("vgc_cpu", ["vg.vgc_cpu_high"], c => [Mirror(c, "vg.vgc_cpu_high", "mon.vgc.cpu_high")]);
        Add("minidump", ["events.bugcheck_recent", "events.bugcheck_vgk", "events.bugcheck_wdfilter"], Minidump);

        // Riot
        Add("riot_folders", ["riot.client_installed", "riot.valorant_installed", "riot.path_non_ascii", "riot.valorant_exe"], RiotFolders);
        Add("riot_elevation", ["riot.client_not_elevated"], RiotElevation);
        Add("fso_mpo", ["riot.compat_layers_present", "riot.fso_disabled", "display.mpo_enabled"], FsoMpo);
        Add("valorant_config", ["riot.config_corrupt"], c => [Sig.FromFact("riot.config_corrupt", c.Snapshot.Riot.GameConfig, g => Sig.Of("riot.config_corrupt", g.CorruptMarkers))]);
        Add("riot_status", ["riot.status_incident"], _ => [Sig.Unknown("riot.status_incident", UnknownReason.NotCollected)]);
        Add("-", ["riot.game_running", "log.platform_init_failed", "log.vanguard_init_failed", "log.d3d_device_lost", "log.fatal_error", "log.net_disconnects", "crash.recent", "crash.gpu"], RiotLogs);

        // power and hardware
        Add("power_plan", ["power.saver_plan", "power.on_battery", "power.cpu_max_limited"], PowerPlan);
        Add("usb_ss", ["power.usb_selective_suspend_on"], c => [Sig.FromFact("power.usb_selective_suspend_on", c.Snapshot.Power.UsbSelectiveSuspendAc)]);
        Add("ram_config", ["hw.ram_low", "hw.ram_single_module", "hw.ram_below_rated"], RamConfig);
        Add("standby_mem", ["hw.standby_mem_high"], StandbyMem);
        Add("hybrid_cpu", ["hw.cpu_hybrid"], c => [Sig.FromFact("hw.cpu_hybrid", c.Snapshot.Hardware.Cpu, cpu => Sig.Of("hw.cpu_hybrid", cpu.Hybrid))]);
        Add("bios_age", ["hw.bios_old", "hw.bios_very_old"], BiosAge);
        Add("thermals", ["hw.thermal_throttle"], c => [Mirror(c, "hw.thermal_throttle", "mon.cpu.throttling")]);
        Add("-", ["hw.laptop", "hw.cpu_x3d"], HwExtras);

        // GPU and display
        Add("igpu_state", ["gpu.basic_display", "gpu.igpu_disabled", "gpu.igpu_present"], IgpuState);
        Add("gpu_driver", ["gpu.amd_dgpu", "gpu.nvidia_dgpu", "gpu.driver_old", "gpu.problem_code"], GpuDriver);
        Add("shader_cache", ["gpu.driver_recently_changed"], ShaderCache);
        Add("intel_driver", ["gpu.intel_driver_old"], IntelDriver);
        Add("gpu_pref", ["gpu.hybrid", "gpu.pref_not_high", "gpu.monitor_on_igpu"], GpuPref);
        Add("nv_3d_settings", ["gpu.nv_profile_overrides"], _ => [Sig.Unknown("gpu.nv_profile_overrides", UnknownReason.Unsupported)]);
        Add("amd_settings", ["gpu.amd_features_on"], _ => [Sig.Unknown("gpu.amd_features_on", UnknownReason.Unsupported)]);
        Add("display_mode", ["display.multi_monitor", "display.refresh_60", "display.hdr_on"], DisplayMode);
        Add("hags_windowed", ["display.hags_on", "display.windowed_opt_on", "display.game_mode_off", "display.game_dvr_on"], HagsWindowed);

        // audio, input, devices, storage
        Add("bt_hfp", ["audio.bt_handsfree_present", "audio.bt_handsfree_default"], BtHfp);
        Add("audio_devices", ["audio.no_default_output", "audio.many_outputs"], AudioDevices);
        Add("nahimic", ["apps.nahimic_apo"], NahimicApo);
        Add("mouse_settings", ["input.mouse_accel_on"], c => [Sig.FromFact("input.mouse_accel_on", c.Snapshot.Input.Mouse, m => Sig.Of("input.mouse_accel_on", m.EnhancePointerPrecision))]);
        Add("pnp_errors", ["dev.problem_devices"], PnpErrors);
        Add("usb_devices", ["dev.usb_optical_drive"], UsbDevices);
        Add("disk_dirty", ["disk.dirty"], DiskDirty);
        Add("disk_type", ["disk.system_hdd", "disk.riot_on_hdd"], DiskType);
        Add("-", ["disk.free_low"], DiskFree);
        Add("vmd", ["storage.intel_vmd"], Vmd);

        // events and crashes
        Add("kp41_lke", ["events.kernel_power_41", "events.display_tdr", "events.whea_errors"], c =>
        [
            EventSignal(c, "events.kernel_power_41", "kernel_power_41", ["Microsoft-Windows-Kernel-Power"], [41], 30, 1, 3),
            EventSignal(c, "events.display_tdr", "display_tdr", ["Display"], [4101], 30, 1, 3),
            EventSignal(c, "events.whea_errors", "whea_errors", ["Microsoft-Windows-WHEA-Logger"], [1, 17, 18, 19, 47], 30, 1, 5),
        ]);
        Add("-", ["events.volmgr_161"], c => [EventSignal(c, "events.volmgr_161", "volmgr_161", ["volmgr"], [161], 30, 1, 2)]);
        Add("system_files", ["events.disk_errors", "os.system_files_suspect"], c =>
        [
            EventSignal(c, "events.disk_errors", "disk_errors", ["disk", "Ntfs", "Microsoft-Windows-Ntfs"], [7, 55, 153], 30, 1, 5),
            Sig.Unknown("os.system_files_suspect", UnknownReason.Unsupported),
        ]);
        Add("app_crash_module", ["events.riot_crash_recent", "events.riot_crash_hook_module", "events.riot_crash_unknown_module"], AppCrashModule);

        // software aggregates
        Add("hook_procs", ["proc.overlay_running"], HookProcs);
        Add("conflict_apps_installed", ["apps.conflict_installed"], ConflictAppsInstalled);
        Add("other_anticheat", ["apps.other_anticheat"], OtherAnticheat);
        Add("oem_tools", ["apps.oem_tools"], OemTools);
        return list;
    }

    private static SignalResult Mirror(SignalContext c, string id, string monitorId)
    {
        var d = c.Monitor?.Detectors.FirstOrDefault(x => string.Equals(x.Id, monitorId, StringComparison.Ordinal));
        return d is null ? Sig.Unknown(id, UnknownReason.NotCollected) : d with { Id = id };
    }

    private static DateTimeOffset AsOf(SignalContext c) => c.AsOfUtc;

    private static bool Limited(SignalContext c) => c.Snapshot.Meta.Privilege == PrivilegeMode.Limited;
}
