# Curation layer over research/conflicts.json (113 entries, imported mechanically by kb_builder.py).
# Defaults per kind come first; per-entry overrides follow. Effects/actions keep the research wording (English);
# Turkish comes from tools/kb_src/strings_tr.json.

KIND_CATEGORY = {
    "antivirus": "vanguard", "vpn": "network_ping", "rgb_vendor": "vanguard", "overlay": "fps_performance",
    "driver": "crash_freeze_bsod", "tweak_tool": "other", "virtualization": "vanguard", "anticheat": "vanguard",
    "audio": "audio_mic", "network_optimizer": "network_ping", "monitoring": "fps_performance", "other": "other",
}

KIND_GUIDES = {
    "antivirus": ["guide.av.exclusions"], "vpn": ["guide.vpn.remove"], "overlay": ["guide.apps.close_overlays"],
    "monitoring": ["guide.apps.close_overlays"], "anticheat": ["guide.vpn.remove"],
    "network_optimizer": ["guide.apps.uninstall_conflict"], "rgb_vendor": ["guide.apps.uninstall_conflict"],
    "audio": ["guide.apps.uninstall_conflict"], "other": ["guide.apps.uninstall_conflict"],
    "tweak_tool": ["guide.apps.uninstall_conflict"], "virtualization": ["guide.apps.uninstall_conflict"],
    "driver": ["guide.apps.uninstall_conflict"],
}

# Never automate anything against these (security software, required Windows/Riot services, things the player needs).
NO_AUTOFIX = {
    "av_avast", "av_norton", "av_mcafee", "av_kaspersky", "av_bitdefender", "av_malwarebytes", "av_trend_micro", "av_360",
    "av_other_suites", "defender_firewall_block", "defender_cfa", "defender_smart_app_control", "dcontrol_defender_disabled",
    "hvci_off_when_required", "required_services_disabled", "modified_windows", "hyperv_hypervisor", "wsl_vmp",
    "remote_desktop_tools", "sandbox_rollback", "storage_oem_drivers", "intel_vmd_rst", "tcp_tweakers", "bcd_tweaks",
    "windows_insider", "discord_overlay", "nvidia_overlay", "steam_overlay", "vpn_corporate", "obs_streamlabs",
    "virtual_audio_mics", "elgato_capture", "ac_easyanticheat", "ac_battleye",
}

# Generated-rule weights by signal suffix (low on purpose: these are long-shot hypotheses).
DEFAULT_WEIGHTS = {
    "installed": 0.8, "running": 0.7, "service_present": 0.3, "service_running": 0.5, "driver_loaded": 0.9,
    "adapter_present": 0.4, "adapter_enabled": 0.4, "crash_module": 3.5, "audio_endpoint": 0.5, "feature_enabled": 0.5,
    "registry_present": 0.5, "file_present": 0.8,
}

# Registry keys that are keys (enumerate values), not "key\\value".
REGISTRY_KEYS_NOT_VALUES = {"Interfaces", "Layers"}

# Registry collector value filters (FieldEquals on the item's "data").
REGISTRY_DATA_EQUALS = {
    "DisableAntiSpyware": "1", "EnableControlledFolderAccess": "1", "GameDVR_Enabled": "1", "AppCaptureEnabled": "1",
    "HwSchMode": "2", "Enabled": "0", "ProxyEnable": "1", "Start": "4",
}

OVERRIDES = {
    # ---- settings-type entries: their fingerprints exist on every PC, so evidence comes from built-in signals
    "required_services_disabled": dict(category="vanguard", symptoms=["error_code", "wont_launch"],
                                       evidence=[("os.required_services_disabled", 2.0), ("vg.vgc_disabled", 1.5)],
                                       guides=["guide.vanguard.reinstall"]),
    "defender_firewall_block": dict(category="vanguard", symptoms=["disconnect", "error_code"],
                                    evidence=[("net.firewall_blocks_riot", 2.0)], guides=["guide.network.firewall"]),
    "defender_cfa": dict(evidence=[("sec.defender_cfa_on", 1.5)], guides=["guide.av.exclusions"]),
    "defender_smart_app_control": dict(evidence=[("sec.smart_app_control_on", 1.0)], guides=["guide.av.exclusions"]),
    "exploit_protection_override": dict(category="vanguard", symptoms=["error_code", "wont_launch"],
                                        evidence=[("sec.exploit_override_riot", 2.5)], fixes=["fix.sec.reset_exploit_overrides"],
                                        guides=[]),
    "dcontrol_defender_disabled": dict(evidence=[("sec.defender_disabled_policy", 1.0), ("conflict.dcontrol_defender_disabled.running", 0.5)],
                                       guides=["guide.av.exclusions"]),
    "hvci_off_when_required": dict(category="tpm_secureboot_bios", evidence=[("sec.hvci.running", -0.8)], guides=["guide.hvci.enable"]),
    "modified_windows": dict(evidence=[("os.modified_windows", 1.5), ("conflict.modified_windows.file_present", 0.8)], guides=[]),
    "windows_insider": dict(category="crash_freeze_bsod", evidence=[("os.insider", 1.5)], guides=["guide.windows.update"]),
    "hags": dict(category="fps_performance", symptoms=["stutter", "mouse_input"], evidence=[("display.hags_on", 0.5)],
                 fixes=["fix.display.hags_off"], guides=[]),
    "mpo": dict(category="display_resolution", symptoms=["stutter", "display_issue"], evidence=[("display.mpo_enabled", 0.3)],
                fixes=["fix.display.mpo_off"], guides=["guide.display.settings"]),
    "fso_compat_flags": dict(category="display_resolution", symptoms=["stutter", "display_issue", "wont_launch"],
                             evidence=[("riot.compat_layers_present", 0.8)], fixes=["fix.generic.remove_compat_layer"],
                             guides=["guide.display.settings"]),
    "usb_selective_suspend": dict(category="crash_freeze_bsod", symptoms=["freeze_hard", "mouse_input"],
                                  evidence=[("power.usb_selective_suspend_on", 0.6)], fixes=["fix.power.usb_selective_suspend_off"],
                                  guides=["guide.mouse.usb"]),
    "ipv6_binding": dict(category="network_ping", symptoms=["voice_chat", "login_issue"],
                         evidence=[("net.ipv6_disabled", 0.8)], fixes=[], guides=["guide.net.dns_ipv6"]),
    "xbox_game_bar_dvr": dict(category="fps_performance", symptoms=["stutter", "fps_low"],
                              evidence=[("display.game_dvr_on", 0.8), ("conflict.xbox_game_bar_dvr.running", 0.3)],
                              fixes=["fix.display.game_dvr_off"], guides=["guide.apps.close_overlays"]),
    "system_proxy": dict(evidence=[("net.proxy_on", 1.0), ("conflict.system_proxy.running", 0.6)], guides=["guide.vpn.remove"]),
    "tcp_tweakers": dict(evidence=[("net.lsp_third_party", 0.3)], fixes=["fix.net.winsock_reset"], guides=[]),
    "bcd_tweaks": dict(category="vanguard", symptoms=["error_code", "wont_launch"],
                       evidence=[("sec.bcd.testsigning", 1.5), ("sec.bcd.hypervisor_off", 0.8), ("sec.bcd.nx_off", 1.0)],
                       guides=["guide.bcd.restore_security"]),
    "vpn_generic": dict(category="vanguard", symptoms=["disconnect", "error_code", "high_ping"],
                        evidence=[("net.vpn_adapter_up", 1.0), ("conflict.vpn_generic.adapter_enabled", 0.5)]),
    # ---- hardware / drivers
    "realtek_2_5gbe": dict(category="fps_performance", symptoms=["stutter"], fixes=["fix.net.disable_unused_adapter", "fix.net.realtek_power_saving_off"],
                           guides=["guide.net.local_connection"]),
    "realtek_gbe_family": dict(category="fps_performance", symptoms=["stutter"], fixes=["fix.net.disable_unused_adapter", "fix.net.realtek_power_saving_off"],
                               guides=["guide.net.local_connection"]),
    "mediatek_rz616": dict(category="vanguard", symptoms=["error_code", "bios_requirement"], guides=["guide.hvci.enable"]),
    "ene_rgb_driver": dict(category="vanguard", symptoms=["error_code", "bios_requirement"], guides=["guide.apps.uninstall_conflict", "guide.hvci.enable"]),
    "storage_oem_drivers": dict(category="vanguard", symptoms=["error_code"], guides=["guide.riot.support_ticket"]),
    "intel_vmd_rst": dict(category="tpm_secureboot_bios", symptoms=["error_code"], guides=["guide.storage.intel_vmd"]),
    "intel_nal_diag": dict(category="vanguard", symptoms=["error_code"]),
    "entech_se64a": dict(category="vanguard", symptoms=["error_code", "bios_requirement"]),
    "misc_flagged_drivers": dict(category="vanguard", symptoms=["error_code", "bios_requirement"]),
    "presonus_firestudio": dict(category="tpm_secureboot_bios", symptoms=["bios_requirement"], guides=["guide.hvci.enable"]),
    "sound_blaster_iommu": dict(category="audio_mic", symptoms=["no_audio", "bios_requirement"], guides=["guide.audio.output_device"]),
    "inpoutx64": dict(category="vanguard", symptoms=["error_code"]),
    "winring0_tools": dict(category="vanguard", symptoms=["error_code", "bios_requirement"]),
    # ---- audio
    "nahimic": dict(category="crash_freeze_bsod", symptoms=["launch_black_screen"], fixes=["fix.svc.disable_conflict_service"],
                    guides=["guide.apps.uninstall_nahimic"], wave={"from": "2026-09-01", "to": "2026-12-31", "boost": 0.7}),
    "bluetooth_handsfree": dict(category="audio_mic", symptoms=["voice_chat", "no_audio"], fixes=["fix.audio.bt_handsfree_off"],
                                guides=["guide.audio.bt_handsfree"], evidence=[("audio.bt_handsfree_present", 0.8)]),
    "audio_enhancers": dict(symptoms=["no_audio", "voice_chat"], guides=["guide.audio.output_device"]),
    "virtual_audio_mics": dict(symptoms=["no_audio", "voice_chat"], guides=["guide.audio.output_device"]),
    # ---- mDNS
    "bonjour_mdns": dict(category="login_account_client", symptoms=["launch_black_screen", "error_code"],
                         fixes=["fix.svc.disable_conflict_service"], guides=["guide.apps.uninstall_bonjour"],
                         wave={"from": "2025-10-01", "to": "2025-11-30", "boost": 0.5}),
    "ni_mdns": dict(category="login_account_client", symptoms=["launch_black_screen", "error_code"], guides=["guide.apps.uninstall_bonjour"]),
    # ---- hooks named after the Oct-2026 update
    "premier_opinion": dict(category="fps_performance", symptoms=["stutter", "launch_black_screen", "crash_desktop"],
                            wave={"from": "2026-10-05", "to": "2026-12-31", "boost": 0.6}),
    "nielsen_meter": dict(category="fps_performance", symptoms=["stutter", "launch_black_screen", "crash_desktop"],
                          wave={"from": "2026-10-05", "to": "2026-12-31", "boost": 0.6}),
    "asus_screenxpert": dict(category="fps_performance", symptoms=["stutter", "launch_black_screen", "crash_desktop"],
                             wave={"from": "2026-10-05", "to": "2026-12-31", "boost": 0.6}),
    "gearup_booster": dict(category="network_ping", symptoms=["launch_black_screen", "crash_desktop", "high_ping"],
                           fixes=["fix.net.winsock_reset"], wave={"from": "2026-10-05", "to": "2026-12-31", "boost": 0.6}),
    "huawei_pc_manager": dict(category="fps_performance", symptoms=["stutter", "crash_desktop"]),
    "asus_gpu_tweak": dict(category="fps_performance", symptoms=["stutter", "crash_desktop"]),
    "ene_rgb_driver_wave": None,  # placeholder: ene.sys wave Jan-Feb 2026 handled via ene_rgb_driver below
    # ---- virtualization (VAN 138 on bare metal)
    "hyperv_hypervisor": dict(symptoms=["error_code"]),
    "vmware": dict(symptoms=["error_code"]),
    "virtualbox": dict(symptoms=["error_code"]),
    "android_emulators": dict(symptoms=["error_code"]),
    "process_lasso": dict(category="vanguard", symptoms=["disconnect", "stutter"], fixes=["fix.sec.remove_riot_perfoptions", "fix.generic.close_process"],
                          guides=["guide.hybrid_cpu.priority"]),
    "razer_synapse": dict(category="vanguard", symptoms=["disconnect", "error_code"]),
    "lenovo_vantage": dict(category="network_ping", symptoms=["disconnect", "high_ping"], guides=["guide.power.laptop_mode"]),
    "killer_suite": dict(category="network_ping", symptoms=["login_issue", "voice_chat"], guides=["guide.net.dns_ipv6"]),
    "amd_adrenalin_features": dict(category="gpu_driver", symptoms=["stutter", "freeze_hard", "display_issue"], guides=["guide.gpu.clean_driver"]),
    "cheat_engine": dict(category="vanguard", symptoms=["error_code", "wont_launch"]),
}
OVERRIDES.pop("ene_rgb_driver_wave")
OVERRIDES["ene_rgb_driver"]["wave"] = {"from": "2026-01-01", "to": "2026-02-28", "boost": 0.5}
