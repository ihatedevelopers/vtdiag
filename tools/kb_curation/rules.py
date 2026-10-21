# Hand-curated hypothesis rules for the 90 catalog problems (data/catalog.json) plus route rules.
# kb_builder.py takes id, category, thread_count, thread_ids, codes and fix evidence from the catalog and everything
# below from here. Weights are log-odds (Unknown never counts; see DESIGN.md section 2):
#   ~3 = near-proof, 2 = strong, 1-1.5 = clear, 0.3-0.6 = weak hint, negative = argues against.
# veto="true"/"false" rules the cause out on that definite value. group = correlated evidence (largest |delta| counts).
# Strings: rule.<id>.title / .summary / rule.<id>.ev.<evId> ("why we think so") come from the English text here.

RULES = {}


def E(id, signal, t=None, f=None, veto=None, group=None, ask=None, why=None, only_if=()):
    return dict(id=id, signal=signal, t=t, f=f, veto=veto, group=group, ask=ask, why=why, only_if=list(only_if))


def R(id, title, summary, *, bucket="fixableHere", symptoms=(), requires=(), excludes=(), ev=(), fixes=(), guides=(),
      questions=(), wave=None, supersedes=(), related=(), kind="hypothesis", route=None, prior_override=None,
      override_reason=None):
    assert id not in RULES, id
    RULES[id] = dict(id=id, title=title, summary=summary, bucket=bucket, symptoms=list(symptoms), requires=list(requires),
                     excludes=list(excludes), ev=list(ev), fixes=list(fixes), guides=list(guides), questions=list(questions),
                     wave=wave, supersedes=list(supersedes), related=list(related), kind=kind, route=route,
                     prior_override=prior_override, override_reason=override_reason)


ONLY_VAL = E("only_valorant", "ask.only_valorant_affected", t=0.4, f=-0.6, ask="ask.only_valorant_affected")
AFTER_UPDATE = E("after_update", "ask.started_after_update", t=0.4, ask="ask.started_after_update")

# ============================================================== fps_performance
R("realtek-2-5gbe-nic-stutter",
  "Your Realtek wired network port is causing the stutters",
  "A Realtek 2.5GbE / GbE network port is switched on in your PC. Its driver is known to clash with Vanguard and cause tiny freezes every few seconds, even when you play on Wi-Fi. Turning off the port you don't use fixes it for most players.",
  symptoms=["stutter", "fps_low", "high_ping", "freeze_hard"],
  requires=["net.realtek25.present"],
  ev=[E("enabled", "net.realtek25.enabled", t=0.4, veto="false", group="nic"),
      E("unused", "net.realtek25.unused", t=1.6, group="nic", why="Your Realtek wired port is switched on, but you use another connection."),
      E("active", "net.realtek25.active", t=0.6, group="nic", why="Your Realtek port is your active connection."),
      E("resets", "events.realtek_nic_resets", t=2.0, why="Windows logged Realtek network driver errors recently."),
      E("vgm_crash", "events.vgm_crash_0xc0000409", t=0.8),
      E("multi_nic", "net.multi_nic_up", t=0.3, group="nic"),
      E("periodic", "ask.stutter_periodic", t=1.0, f=-0.8, ask="ask.stutter_periodic"),
      E("mon_periodic", "mon.frametime.periodic_spikes", t=1.5, f=-1.0),
      E("mon_nic", "mon.spikes_correlate.nic_event", t=2.5),
      ONLY_VAL],
  fixes=["fix.net.disable_unused_adapter", "fix.net.realtek_power_saving_off"],
  guides=["guide.net.local_connection"],
  questions=["ask.stutter_periodic", "ask.only_valorant_affected"],
  supersedes=["conflict-realtek_2_5gbe", "conflict-realtek_gbe_family"])

R("third-party-hook-overlay-conflict",
  "An overlay or recording program is hooking into VALORANT",
  "Programs that draw on top of the game (RivaTuner, Overwolf, trackers, clip recorders, some OEM tools) hook into VALORANT. After recent updates these hooks cause stutter, freezes and stuck loading screens. Closing them for a session usually tells you quickly.",
  symptoms=["stutter", "fps_low", "launch_black_screen", "crash_desktop"],
  ev=[E("overlay_running", "proc.overlay_running", t=1.2, f=-0.8, why="Overlay or recording programs are running right now."),
      E("hook_crash", "events.riot_crash_hook_module", t=2.5, why="VALORANT crashed inside a known add-on program's file."),
      E("rtss", "conflict.rtss.running", t=0.8, group="known_hook"),
      E("overwolf", "conflict.overwolf_trackers.running", t=0.6, group="known_hook"),
      E("premier", "conflict.premier_opinion.installed", t=1.5, group="known_hook2"),
      E("nielsen", "conflict.nielsen_meter.installed", t=1.2, group="known_hook2"),
      E("screenxpert", "conflict.asus_screenxpert.installed", t=1.0, group="known_hook2"),
      E("dvr", "display.game_dvr_on", t=0.3),
      AFTER_UPDATE],
  fixes=["fix.proc.close_overlays", "fix.display.game_dvr_off", "fix.svc.disable_conflict_service"],
  guides=["guide.apps.close_overlays", "guide.apps.uninstall_conflict"],
  questions=["ask.started_after_update"],
  wave={"from": "2026-10-05", "to": "2026-12-31", "boost": 0.6})

R("power-plan-throttling-low-fps",
  "Your PC is held back by power saving",
  "Windows or the laptop is saving power: a power-saver plan, running on battery, or a limit on processor speed. VALORANT then runs slower than your hardware can.",
  symptoms=["fps_low", "stutter"],
  ev=[E("saver", "power.saver_plan", t=1.8, f=-0.4, why="A power-saving plan is active."),
      E("battery", "power.on_battery", t=1.8, why="The laptop is running on battery."),
      E("cpu_max", "power.cpu_max_limited", t=2.0, why="The maximum processor speed is set below 100%."),
      E("laptop", "hw.laptop", t=0.4),
      E("ram_low", "hw.ram_low", t=0.5),
      E("single", "hw.ram_single_module", t=0.4),
      E("hdd", "disk.riot_on_hdd", t=0.6),
      E("mon_throttle", "mon.cpu.throttling", t=1.5)],
  fixes=["fix.power.high_performance", "fix.power.cpu_max_100"],
  guides=["guide.power.laptop_mode", "guide.hardware.thermal", "guide.performance.basics"])

R("shader-cache-compilation-stutter",
  "The game is rebuilding its shaders after a driver or game update",
  "After a new graphics driver, a new graphics card or a big patch, VALORANT has to rebuild its shader cache. The first matches stutter, then it gets better by itself.",
  bucket="guidedManual",
  symptoms=["stutter", "fps_low"],
  ev=[E("driver_new", "gpu.driver_recently_changed", t=1.5, why="Your graphics driver changed in the last few days."),
      E("first_matches", "ask.only_first_matches", t=1.8, f=-1.2, ask="ask.only_first_matches"),
      E("after_update", "ask.started_after_update", t=0.6, ask="ask.started_after_update"),
      E("overlay", "proc.overlay_running", t=0.2)],
  guides=["guide.shader_cache.warmup"],
  questions=["ask.only_first_matches"])

R("low-gpu-usage-after-ue5-patch",
  "VALORANT runs on the wrong graphics chip or is capped",
  "Your graphics card isn't working hard: VALORANT may run on the weaker built-in graphics, or a power plan or frame cap holds it back.",
  symptoms=["fps_low"],
  ev=[E("hybrid", "gpu.hybrid", t=0.6),
      E("pref", "gpu.pref_not_high", t=1.2, why="Windows isn't told to use your strong graphics card for VALORANT."),
      E("monitor_igpu", "gpu.monitor_on_igpu", t=1.5, why="Your monitor is plugged into the motherboard, not the graphics card."),
      E("saver", "power.saver_plan", t=0.6),
      E("battery", "power.on_battery", t=0.6),
      E("driver_old", "gpu.driver_old", t=0.5),
      E("mon_gpu", "mon.gpu.low_usage", t=1.5),
      AFTER_UPDATE],
  fixes=["fix.display.gpu_pref_high", "fix.power.high_performance"],
  guides=["guide.gpu.igpu", "guide.gpu.clean_driver"])

R("riot-client-not-elevated",
  "The Riot Client isn't running as administrator",
  "Many players fixed freezes, black screens and FPS drops after updates by running the Riot Client as administrator. It's quick to try and easy to undo.",
  symptoms=["stutter", "fps_low", "launch_black_screen", "wont_launch"],
  ev=[E("not_elevated", "riot.client_not_elevated", t=0.4, veto="false"),
      E("after_update", "ask.started_after_update", t=0.5, ask="ask.started_after_update")],
  fixes=["fix.riot.client_runasadmin"],
  guides=["guide.riot.client_settings"])

R("fps-decay-over-time",
  "FPS drops the longer you play",
  "FPS starts fine and sinks over time. Usual causes are heat (the PC slows itself down), overlays, the display mode, or a driver version.",
  bucket="guidedManual",
  symptoms=["fps_low", "stutter"],
  ev=[E("over_time", "ask.fps_drops_over_time", t=2.0, f=-1.5, ask="ask.fps_drops_over_time"),
      E("laptop", "hw.laptop", t=0.4),
      E("overlay", "proc.overlay_running", t=0.4),
      E("throttle", "mon.cpu.throttling", t=1.5),
      E("standby", "hw.standby_mem_high", t=0.4)],
  fixes=["fix.proc.close_overlays", "fix.display.disable_fso"],
  guides=["guide.hardware.thermal", "guide.gpu.clean_driver"],
  questions=["ask.fps_drops_over_time"])

R("fps-lower-than-expected-caps",
  "FPS is lower than your PC should get",
  "Your FPS is lower than similar PCs get. Common causes: memory running slow or in single channel, frame caps in the game, or the power plan.",
  bucket="guidedManual",
  symptoms=["fps_low"],
  ev=[E("single", "hw.ram_single_module", t=0.8, why="Only one memory stick is installed (single channel)."),
      E("below_rated", "hw.ram_below_rated", t=0.6),
      E("ram_low", "hw.ram_low", t=0.6),
      E("saver", "power.saver_plan", t=0.6),
      E("cpu_max", "power.cpu_max_limited", t=0.8),
      E("hdd", "disk.riot_on_hdd", t=0.5)],
  fixes=["fix.power.high_performance", "fix.power.cpu_max_100"],
  guides=["guide.performance.basics"])

R("frametime-latency-spikes",
  "Frame-time or latency spikes from background apps or drivers",
  "You see short frame-time or latency spikes. Background apps like Discord, overlays, network jitter or a driver version are the usual suspects.",
  bucket="guidedManual",
  symptoms=["stutter", "fps_low"],
  ev=[E("discord", "conflict.discord_overlay.running", t=0.4),
      E("overlay", "proc.overlay_running", t=0.5),
      E("hags", "display.hags_on", t=0.3),
      E("jitter", "net.region_jitter_high", t=0.4),
      E("mon_periodic", "mon.frametime.periodic_spikes", t=0.5)],
  fixes=["fix.proc.close_overlays", "fix.display.hags_off"],
  guides=["guide.apps.close_overlays", "guide.gpu.clean_driver"])

R("intel-hybrid-cpu-ecore-stutter",
  "Your Intel CPU puts VALORANT on its slower cores",
  "Your Intel CPU has fast (P) and slow (E) cores. Sometimes Windows schedules VALORANT or Vanguard on the slow cores, which causes stutter.",
  bucket="guidedManual",
  symptoms=["stutter", "fps_low"],
  requires=["hw.cpu_hybrid"],
  ev=[E("hybrid", "hw.cpu_hybrid", t=0.8, veto="false"),
      E("perf", "sec.ifeo_perfoptions_riot", t=0.6),
      E("saver", "power.saver_plan", t=0.6),
      E("lasso", "conflict.process_lasso.running", t=0.5)],
  fixes=["fix.power.high_performance", "fix.sec.remove_riot_perfoptions"],
  guides=["guide.hybrid_cpu.priority"])

R("msi-x3d-game-mode-lobby-freeze",
  "MSI 'X3D Gaming Mode' makes the lobby freeze",
  "On MSI boards with an AMD X3D processor, the BIOS 'X3D Gaming Mode' option can freeze VALORANT in the lobby. Turning it off in the BIOS fixed it.",
  bucket="guidedManual",
  symptoms=["stutter", "freeze_hard", "fps_low"],
  requires=["hw.cpu_x3d"],
  ev=[E("x3d", "hw.cpu_x3d", t=1.0, veto="false"),
      E("msi", "conflict.msi_center.installed", t=0.6)],
  guides=["guide.msi_x3d.game_mode"])

R("patch-13-01-fps-drop",
  "FPS dropped after a VALORANT patch",
  "FPS fell right after a game patch. Repairing or reinstalling VALORANT and Vanguard and clearing the shader cache helped players; memory leaks are another cause.",
  bucket="guidedManual",
  symptoms=["fps_low"],
  ev=[E("after_update", "ask.started_after_update", t=0.8, f=-0.6, ask="ask.started_after_update"),
      E("standby", "hw.standby_mem_high", t=0.4),
      E("vgc_cpu", "vg.vgc_cpu_high", t=0.6)],
  guides=["guide.riot.client_repair", "guide.shader_cache.warmup"])

R("standby-memory-stutter",
  "Windows memory fills up and causes stutter",
  "A lot of memory sits in Windows' standby list. On PCs with little RAM this can cause stutter after long sessions.",
  bucket="guidedManual",
  symptoms=["stutter", "fps_low"],
  ev=[E("standby", "hw.standby_mem_high", t=1.2, why="A lot of your memory is in Windows' standby list."),
      E("ram_low", "hw.ram_low", t=0.8),
      E("over_time", "ask.fps_drops_over_time", t=0.5, ask="ask.fps_drops_over_time")],
  guides=["guide.standby_memory"])

R("windows-update-kb5101650-microstutter",
  "Windows update KB5101650 is causing micro-stutter",
  "Players reported micro-stutter after Windows update KB5101650. Uninstalling that update removed the stutter for them.",
  bucket="guidedManual",
  symptoms=["stutter"],
  requires=["os.hotfix_kb5101650"],
  ev=[E("kb", "os.hotfix_kb5101650", t=3.5, veto="false", why="Update KB5101650 is installed."),
      E("mpo", "display.mpo_enabled", t=0.2),
      AFTER_UPDATE],
  fixes=["fix.display.mpo_off"],
  guides=["guide.windows.update"])

R("riot-client-hardware-acceleration",
  "Riot Client hardware acceleration slows the game",
  "The Riot Client draws its window with your graphics card. On some PCs turning off its hardware acceleration helped FPS.",
  bucket="guidedManual",
  symptoms=["fps_low"],
  ev=[E("hybrid", "gpu.hybrid", t=0.3)],
  guides=["guide.riot.client_settings"])

# ============================================================== vanguard
R("van185-vanguard-disconnects",
  "Vanguard loses its connection mid-match (VAN 185 / VAL 5)",
  "Vanguard's connection to Riot drops during a match. Unstable network, Vanguard not starting properly, antivirus or firewall blocks, and VPNs are the usual causes. Fix the network side and Vanguard's start-up first.",
  symptoms=["disconnect", "error_code"],
  ev=[E("vgc_disabled", "vg.vgc_disabled", t=1.5, why="Vanguard's service is disabled."),
      E("vgc_auto", "vg.vgc_automatic_needed", t=1.2),
      E("av", "sec.third_party_av", t=0.6),
      E("fw_block", "net.firewall_blocks_riot", t=1.5, why="A firewall rule blocks a Riot program."),
      E("vpn", "net.vpn_adapter_up", t=0.6),
      E("wifi", "net.on_wifi", t=0.3),
      E("weak", "net.wifi_weak", t=0.6),
      E("loss", "net.gateway_loss", t=0.8),
      E("rloss", "net.region_loss", t=0.6),
      E("multi", "net.multi_nic_up", t=0.4),
      E("log_disc", "log.net_disconnects", t=0.8),
      E("perf", "sec.ifeo_perfoptions_riot", t=0.6),
      E("mon_loss", "mon.network.packet_loss", t=1.2)],
  fixes=["fix.vg.vgc_automatic", "fix.net.disable_second_adapter", "fix.net.dns_flush", "fix.display.disable_fso"],
  guides=["guide.net.local_connection", "guide.av.exclusions", "guide.network.firewall"],
  questions=["ask.vanguard_precheck_on", "ask.friends_same_server"])

R("vgc-service-not-running",
  "Vanguard's service isn't running",
  "The Vanguard service (vgc) isn't running or is set so it doesn't start. Riot's fix is to set it to start automatically - unless you use Vanguard Pre-Check, where 'manual and stopped' is normal.",
  symptoms=["error_code", "wont_launch", "launch_black_screen"],
  ev=[E("disabled", "vg.vgc_disabled", t=2.5, why="Vanguard's service is set to Disabled."),
      E("needed", "vg.vgc_automatic_needed", t=1.5, group="vgc_fail"),
      E("never", "vg.vgc_never_started", t=1.2, group="vgc_fail", why="Vanguard hasn't written a log recently, so it probably never started."),
      E("running", "vg.vgc_running", t=-1.2),
      E("precheck", "ask.vanguard_precheck_on", t=-1.0, ask="ask.vanguard_precheck_on"),
      E("insider", "os.insider", t=0.6),
      E("vg_log", "log.vanguard_init_failed", t=1.0)],
  fixes=["fix.vg.vgc_automatic"],
  guides=["guide.vanguard.precheck", "guide.vanguard.reinstall"],
  questions=["ask.vanguard_precheck_on"])

R("vgk-not-loaded-needs-reboot",
  "Vanguard's driver is waiting for a real restart",
  "Vanguard's driver (vgk) only loads when Windows really restarts. After installing or updating Vanguard, 'Shut down' with Fast Startup isn't enough.",
  symptoms=["error_code", "wont_launch"],
  ev=[E("not_loaded", "vg.vgk_not_loaded", t=2.5, why="Vanguard's driver is installed but not loaded."),
      E("pending", "os.pending_reboot", t=0.8),
      E("fast", "os.fast_startup_on", t=0.5),
      E("bootstatus", "vg.bootstatus_dat", t=0.8),
      E("vgk_running", "vg.vgk_running", t=-2.0)],
  fixes=["fix.vg.quarantine_bootstatus", "fix.os.fast_startup_off"],
  guides=["guide.vanguard.real_restart"])

R("vanguard-corrupt-install",
  "Vanguard's installation is damaged",
  "Vanguard's files or services are broken or incomplete. A clean reinstall of Vanguard (Riot's official steps) fixes this.",
  symptoms=["error_code", "wont_launch", "freeze_hard"],
  ev=[E("files", "vg.files_missing", t=2.0, why="Some of Vanguard's files are missing."),
      E("vgc_missing", "vg.vgc_missing", t=1.5, group="svc"),
      E("vgk_missing", "vg.vgk_missing", t=1.5, group="svc"),
      E("bsod_vgk", "events.bugcheck_vgk", t=1.2),
      E("crash_loop", "vg.vgc_crash_loop", t=1.2, why="Vanguard keeps restarting over and over."),
      E("league", "ask.league_also_broken", t=1.5, f=-0.4, ask="ask.league_also_broken"),
      E("vg_log", "log.vanguard_init_failed", t=0.8),
      E("stale", "vg.stale_dat", t=0.4)],
  fixes=["fix.vg.quarantine_bootstatus", "fix.vg.quarantine_stale_dat", "fix.vg.vgc_automatic"],
  guides=["guide.vanguard.reinstall"],
  questions=["ask.league_also_broken"])

R("vgc-high-cpu",
  "Vanguard uses a lot of CPU",
  "vgc.exe (Vanguard) uses a lot of CPU. Antivirus scanning Vanguard, heat on laptops, or a weak CPU are the usual causes.",
  bucket="guidedManual",
  symptoms=["fps_low", "stutter"],
  ev=[E("cpu", "vg.vgc_cpu_high", t=2.0),
      E("mon_cpu", "mon.vgc.cpu_high", t=2.0),
      E("av", "sec.third_party_av", t=0.4),
      E("laptop", "hw.laptop", t=0.3)],
  guides=["guide.av.exclusions", "guide.hardware.thermal"])

R("vpn-anticheat-network-filter-conflict",
  "A VPN, network filter or another anti-cheat blocks Vanguard",
  "VPNs, network filters (VPN drivers, Docker, VirtualBox adapters) and other anti-cheats like FACEIT can block Vanguard's connection or driver.",
  symptoms=["error_code", "disconnect", "wont_launch"],
  ev=[E("vpn_up", "net.vpn_adapter_up", t=1.5, why="A VPN connection is active."),
      E("virtual", "net.virtual_adapter_present", t=0.5),
      E("ac", "apps.other_anticheat", t=1.2, why="Another anti-cheat is installed."),
      E("faceit", "conflict.ac_faceit.driver_loaded", t=1.0, group="ac"),
      E("lsp", "net.lsp_third_party", t=0.8),
      E("proxy", "net.proxy_on", t=0.5)],
  guides=["guide.vpn.remove"])

R("incompatible-driver-component",
  "A driver from RGB or tool software is blocked by Vanguard",
  "Vanguard found a driver it doesn't trust, usually from RGB lighting, monitor or hardware tools (ene.sys, gdrv3.sys, se64a.sys...). Updating or uninstalling the program that installed it fixes this.",
  bucket="guidedManual",
  symptoms=["error_code", "wont_launch", "bios_requirement"],
  ev=[E("hvci_drv", "sec.hvci.incompatible_drivers", t=1.2, why="Windows lists drivers that are not compatible with Memory Integrity."),
      E("ene", "conflict.ene_rgb_driver.driver_loaded", t=2.0, group="drv"),
      E("se64a", "conflict.entech_se64a.driver_loaded", t=2.0, group="drv"),
      E("gdrv", "conflict.gigabyte_gcc_rgbfusion.driver_loaded", t=1.5, group="drv"),
      E("xhunter", "conflict.ac_xigncode.driver_loaded", t=1.5, group="drv"),
      E("misc", "conflict.misc_flagged_drivers.driver_loaded", t=1.5, group="drv"),
      E("winring0", "conflict.winring0_tools.driver_loaded", t=1.2, group="drv"),
      E("ci_block", "events.codeintegrity_blocked", t=1.0)],
  guides=["guide.apps.uninstall_conflict", "guide.hvci.enable"])

R("launch-fails-something-unusual",
  "Something unusual on this PC stops VALORANT from launching",
  "Unusual system changes stop the launch: debugger hooks or Exploit Protection overrides on Riot programs, test signing, or software that modified Windows.",
  symptoms=["wont_launch", "error_code"],
  ev=[E("debugger", "sec.ifeo_riot_debugger", t=2.5, why="A debugger hook is set on a Riot program."),
      E("exploit", "sec.exploit_override_riot", t=2.0, why="Exploit Protection is overridden for a Riot program."),
      E("testsign", "sec.bcd.testsigning", t=2.0, why="Windows test signing or debug mode is on."),
      E("modified", "os.modified_windows", t=0.8),
      E("hvci_off", "sec.hvci.running", f=0.3)],
  fixes=["fix.sec.reset_exploit_overrides"],
  guides=["guide.bcd.restore_security", "guide.vanguard.reinstall"])

R("third-party-antivirus-conflict",
  "Your antivirus blocks Riot or Vanguard files",
  "A third-party antivirus (Norton, Trend Micro, Avast, McAfee...) can quarantine or block Vanguard and the Riot Client. Adding exclusions, or uninstalling it so Windows Defender takes over, fixes this.",
  bucket="guidedManual",
  symptoms=["error_code", "disconnect", "wont_launch", "install_update"],
  requires=["sec.third_party_av"],
  ev=[E("av", "sec.third_party_av", t=1.8, veto="false", why="A third-party antivirus is active."),
      E("cfa", "sec.defender_cfa_on", t=0.8),
      E("files", "vg.files_missing", t=0.8)],
  guides=["guide.av.exclusions"])

R("launch-shortcut-tray-failure",
  "The Riot Client is stuck in the tray or the shortcut is broken",
  "The game doesn't open from the shortcut, or the Riot Client hides in the tray. Opening it from the tray, a fresh shortcut, or a Vanguard repair usually fixes it.",
  bucket="guidedManual",
  symptoms=["wont_launch"],
  ev=[E("client_running", "riot.game_running", t=-0.5),
      E("vgc_auto", "vg.vgc_automatic_needed", t=0.6),
      E("av", "sec.third_party_av", t=0.4)],
  guides=["guide.riot.client_settings", "guide.vanguard.reinstall"])

R("razer-synapse-escalation-kick",
  "Razer Synapse's game service clashes with Vanguard",
  "Razer Synapse's 'game escalation' service can get you kicked (VAN 102). Turning that service off helped.",
  symptoms=["disconnect", "error_code"],
  requires=["conflict.razer_synapse.installed"],
  ev=[E("installed", "conflict.razer_synapse.installed", t=0.8, veto="false"),
      E("service", "conflict.razer_synapse.service_present", t=0.8)],
  fixes=["fix.svc.disable_conflict_service"],
  guides=["guide.apps.uninstall_conflict"],
  supersedes=["conflict-razer_synapse"])

R("missing-environment-variables",
  "Basic Windows settings (environment variables) are missing",
  "Windows environment variables like TEMP or Path are missing or broken. Vanguard and the Riot Client need them.",
  bucket="guidedManual",
  symptoms=["wont_launch", "error_code", "install_update"],
  requires=["os.env_vars_broken"],
  ev=[E("env", "os.env_vars_broken", t=3.0, veto="false", why="Some Windows environment variables are missing or point nowhere.")],
  guides=["guide.env_vars.restore"])

R("van-restriction-hwid",
  "Riot restricted this PC or account (VAN RESTRICTION / HWID)",
  "Riot applies stricter checks or a time-limited restriction to this PC or account. There is nothing to repair on your PC; meet the listed requirements and contact Riot support.",
  bucket="riotOrAccount",
  symptoms=["error_code"],
  ev=[E("dualboot", "fw.dualboot", t=0.3)],
  guides=["guide.riot.support_ticket"])

R("dual-boot-secure-boot-van1",
  "Dual boot breaks Vanguard's Secure Boot check (VAN 1)",
  "Another operating system (often Linux with GRUB) starts first. Vanguard then can't confirm a clean Secure Boot. Putting Windows Boot Manager first fixes it.",
  bucket="guidedManual",
  symptoms=["error_code", "wont_launch"],
  requires=["fw.dualboot"],
  ev=[E("dualboot", "fw.dualboot", t=2.0, veto="false", why="Your boot menu has another operating system."),
      E("sb", "sec.secureboot.on", f=0.6)],
  guides=["guide.boot.dualboot"])

R("high-process-priority-van136-185",
  "VALORANT or Vanguard is forced to High priority (VAN 136)",
  "Forcing VALORANT, Discord or Vanguard to High or Realtime priority starves Vanguard and gets you kicked (VAN 136 / 185).",
  symptoms=["disconnect", "error_code"],
  ev=[E("perf", "sec.ifeo_perfoptions_riot", t=2.5, why="A forced CPU priority is set on a Riot program."),
      E("lasso", "conflict.process_lasso.installed", t=1.0)],
  fixes=["fix.sec.remove_riot_perfoptions"],
  guides=["guide.hybrid_cpu.priority"],
  supersedes=["conflict-process_lasso"])

R("vanguard-blocks-chkdsk",
  "Vanguard blocks Windows from repairing the disk",
  "Windows wants to check the disk at start-up, but Vanguard's driver loads first and blocks it. Exit or uninstall Vanguard, run the disk check, then let the Riot Client reinstall Vanguard.",
  bucket="guidedManual",
  symptoms=["freeze_hard", "wont_launch"],
  requires=["disk.dirty"],
  ev=[E("dirty", "disk.dirty", t=2.5, veto="false", why="Windows marked a disk as needing a check."),
      E("disk_err", "events.disk_errors", t=0.8)],
  guides=["guide.storage.chkdsk"])

# ============================================================== crash_freeze_bsod
R("nahimic-audio-service-launch-hang",
  "Nahimic audio software is blocking VALORANT at launch",
  "Nahimic (A-Volute) audio software, common on MSI, Lenovo, Dell and ASUS PCs, injects into VALORANT and makes it hang on a black screen or around 10% loading. Turning off the Nahimic service fixes this for most players.",
  symptoms=["launch_black_screen", "crash_desktop", "error_code"],
  requires=["conflict.nahimic.any"],
  ev=[E("installed", "conflict.nahimic.installed", t=0.6, group="present"),
      E("service", "conflict.nahimic.service_present", t=0.8, group="present"),
      E("running", "conflict.nahimic.running", t=1.2, f=-0.6, why="Nahimic is running right now."),
      E("crash_module", "conflict.nahimic.crash_module", t=3.0, why="VALORANT crashed inside Nahimic's file (ProductInfo.dll)."),
      E("apo", "apps.nahimic_apo", t=0.5),
      E("fatal", "log.fatal_error", t=0.3)],
  fixes=["fix.svc.disable_conflict_service"],
  guides=["guide.apps.uninstall_nahimic"],
  wave={"from": "2026-09-01", "to": "2026-12-31", "boost": 0.7},
  supersedes=["conflict-nahimic"])

R("whole-pc-freeze-kernel-power-41",
  "The whole PC freezes or restarts while playing",
  "The whole PC freezes or restarts (Kernel-Power 41). On new AMD builds this is often the Realtek network driver, USB power saving, a graphics driver or the BIOS. Hardware (power supply, heat, unstable memory) is possible too.",
  bucket="likelyHardware",
  symptoms=["freeze_hard", "crash_desktop"],
  ev=[E("kp41", "events.kernel_power_41", t=1.5, why="Windows logged sudden shutdowns (Kernel-Power 41)."),
      E("tdr", "events.display_tdr", t=0.8),
      E("whea", "events.whea_errors", t=1.2, why="Windows logged hardware errors (WHEA)."),
      E("realtek", "net.realtek25.unused", t=0.6),
      E("usb_ss", "power.usb_selective_suspend_on", t=0.3),
      E("bios_old", "hw.bios_old", t=0.4),
      E("whole", "ask.whole_pc_freezes", t=1.2, f=-1.5, ask="ask.whole_pc_freezes"),
      E("bsod", "events.bugcheck_recent", t=0.6)],
  fixes=["fix.net.disable_unused_adapter", "fix.power.usb_selective_suspend_off", "fix.display.hags_off"],
  guides=["guide.hardware.freeze_checks", "guide.bios.update", "guide.gpu.clean_driver"],
  questions=["ask.whole_pc_freezes"])

R("outdated-bios-reboot-vgk-bsod",
  "An old BIOS makes the PC reboot or blue-screen with Vanguard",
  "Your motherboard BIOS is old. Old BIOS versions have bugs with TPM, Secure Boot and memory that make the PC reboot instantly or blue-screen (vgk.sys) when Vanguard starts. Updating the BIOS fixed this for most players.",
  bucket="guidedManual",
  symptoms=["freeze_hard", "crash_desktop", "error_code"],
  ev=[E("old", "hw.bios_old", t=0.8, group="age", why="Your BIOS is more than two years old."),
      E("very_old", "hw.bios_very_old", t=1.4, group="age", why="Your BIOS is more than four years old."),
      E("vgk_bsod", "events.bugcheck_vgk", t=1.5, why="Recent blue screens point at Vanguard's driver (vgk.sys)."),
      E("kp41", "events.kernel_power_41", t=0.8),
      E("whole", "ask.whole_pc_freezes", t=0.4, ask="ask.whole_pc_freezes"),
      E("new_build", "ask.pc_built_recently", t=0.5, ask="ask.pc_built_recently")],
  guides=["guide.bios.update"])

R("usb-selective-suspend-freeze",
  "USB power saving freezes the PC",
  "Windows puts USB ports to sleep to save power. On some PCs this freezes the whole system or makes devices drop out, especially with VALORANT running. Switching USB selective suspend off fixes it.",
  symptoms=["freeze_hard", "stutter", "mouse_input"],
  ev=[E("usb_ss", "power.usb_selective_suspend_on", t=0.6, veto="false", why="USB selective suspend is on."),
      E("kp41", "events.kernel_power_41", t=0.6),
      E("whole", "ask.whole_pc_freezes", t=0.6, ask="ask.whole_pc_freezes"),
      E("fast", "os.fast_startup_on", t=0.2)],
  fixes=["fix.power.usb_selective_suspend_off", "fix.os.fast_startup_off"],
  guides=["guide.mouse.usb"])

R("fast-startup-stale-dat-bsod",
  "Fast Startup and an old Vanguard file cause blue screens",
  "Fast Startup means Windows never fully restarts, and an old Vanguard boot file (392667600.dat) stays behind. Together they cause blue screens or 'restart required' loops.",
  symptoms=["freeze_hard", "error_code", "wont_launch"],
  # The file is present on many PCs where Vanguard works fine (the dev PC: 256 MB, rewritten days before the scan), so
  # its presence only counts when the player reports a matching problem, and less than before.
  ev=[E("stale", "vg.stale_dat", t=1.5, why="A Vanguard boot file (392667600.dat) is on this PC.",
        only_if=["freeze_hard", "error_code", "wont_launch"]),
      E("fast", "os.fast_startup_on", t=0.8, why="Fast Startup is on."),
      E("bsod", "events.bugcheck_recent", t=1.0)],
  fixes=["fix.vg.quarantine_stale_dat", "fix.os.fast_startup_off"],
  guides=["guide.vanguard.real_restart"])

R("igpu-disabled-or-driverless-freeze",
  "The built-in graphics is off or has no driver",
  "The processor's built-in graphics is disabled or has no driver (shown as an unknown display device). On some PCs that causes freezes even when you play on the graphics card.",
  bucket="guidedManual",
  symptoms=["freeze_hard", "crash_desktop", "stutter"],
  ev=[E("basic", "gpu.basic_display", t=2.0, group="igpu", why="A graphics chip runs without its driver."),
      E("disabled", "gpu.igpu_disabled", t=1.2, group="igpu", why="The built-in graphics is disabled."),
      E("problem", "dev.problem_devices", t=0.5),
      E("overlay", "proc.overlay_running", t=0.2)],
  fixes=["fix.power.usb_selective_suspend_off"],
  guides=["guide.gpu.igpu"])

R("freeze-on-click-lobby-misc",
  "The game freezes when you click in the lobby",
  "VALORANT freezes on clicks in the lobby. Old Bluetooth tray software (btmmhook.dll), the display mode, or the built-in graphics were behind it for some players.",
  bucket="guidedManual",
  symptoms=["freeze_hard", "stutter"],
  ev=[E("hook_crash", "events.riot_crash_unknown_module", t=0.8),
      E("igpu", "gpu.igpu_disabled", t=0.4)],
  guides=["guide.apps.uninstall_conflict", "guide.gpu.igpu"])

R("insider-build-vgk-bsod",
  "A Windows Insider build makes Vanguard blue-screen",
  "You run a Windows Insider (preview) build. Vanguard often doesn't support new Insider builds yet and blue-screens (vgk.sys). Go back to a normal Windows release.",
  bucket="guidedManual",
  symptoms=["freeze_hard", "error_code", "wont_launch"],
  requires=["os.insider"],
  ev=[E("insider", "os.insider", t=2.5, veto="false", why="This PC runs a Windows Insider build."),
      E("vgk_bsod", "events.bugcheck_vgk", t=1.0)],
  guides=["guide.windows.update"],
  supersedes=["conflict-windows_insider"])

R("unexplained-launch-failure",
  "The game fails to launch without a clear reason",
  "VALORANT won't start and nothing on the PC explains it yet. Collect logs with Riot's repair tool and contact Riot support.",
  bucket="riotOrAccount",
  symptoms=["wont_launch", "crash_desktop"],
  ev=[E("crash", "crash.recent", t=0.4),
      E("unknown_mod", "events.riot_crash_unknown_module", t=0.6)],
  guides=["guide.riot.support_ticket"])

R("volmgr-161-black-screen-fan",
  "Black screen with loud fans and a crash-dump error (volmgr 161)",
  "The screen goes black and the fans spin up, and Windows logs volmgr event 161 (crash dump setup failed). Several restarts cleared it for players; if it keeps happening it points at hardware or drivers.",
  bucket="likelyHardware",
  symptoms=["freeze_hard"],
  requires=["events.volmgr_161"],
  ev=[E("volmgr", "events.volmgr_161", t=2.5, veto="false", why="Windows logged volmgr event 161."),
      E("kp41", "events.kernel_power_41", t=0.5)],
  guides=["guide.hardware.freeze_checks"])

R("wdfilter-bsod",
  "Blue screens caused by Windows Defender's driver (WdFilter.sys)",
  "Blue screens name WdFilter.sys (Defender's filter driver), usually together with a broken Vanguard install. A safe-mode clean-up of Riot/Vanguard and a Windows Security reset fixed it.",
  bucket="guidedManual",
  symptoms=["freeze_hard"],
  requires=["events.bugcheck_wdfilter"],
  ev=[E("wdf", "events.bugcheck_wdfilter", t=3.0, veto="false", why="Recent blue screens name WdFilter.sys.")],
  guides=["guide.wdfilter.reset"])

# ============================================================== tpm_secureboot_bios
R("secure-boot-not-detected-van9003",
  "Secure Boot is off or not working (VAN 9003)",
  "Vanguard needs Secure Boot. On your PC it is off, or it can't work yet (legacy/CSM mode, MBR disk, or missing keys). It's switched on in the BIOS; the guide shows the exact steps for your PC.",
  bucket="guidedManual",
  symptoms=["error_code", "bios_requirement", "wont_launch"],
  ev=[E("sb_off", "sec.secureboot.on", f=3.0, veto="true", why="Secure Boot is off."),
      E("legacy", "fw.uefi", f=1.0, why="Your PC starts in legacy (CSM) mode, so Secure Boot can't turn on."),
      E("mbr", "disk.system_gpt", f=1.0, why="Your Windows disk uses MBR, which Secure Boot can't use."),
      E("setup_mode", "sec.secureboot.setup_mode", t=1.5, why="Secure Boot keys aren't installed (setup mode)."),
      E("ca2023", "sec.secureboot.db_missing_ca2023", t=0.4),
      E("bios_old", "hw.bios_old", t=0.3),
      E("broke_boot", "ask.secureboot_enable_broke_boot", t=-0.5)],
  guides=["guide.secureboot.enable"],
  related=["secure-boot-enable-boot-failure", "secure-boot-policy-wave-2026"])

R("secure-boot-enable-boot-failure",
  "Turning on Secure Boot stops the PC from starting",
  "Turning Secure Boot on made the PC fail to start (Secure Boot violation, invalid signature). Usually the disk is MBR, CSM is still on, the keys need resetting, or the BIOS is old.",
  bucket="guidedManual",
  symptoms=["bios_requirement", "error_code", "wont_launch"],
  ev=[E("broke_boot", "ask.secureboot_enable_broke_boot", t=3.0, f=-1.5, ask="ask.secureboot_enable_broke_boot"),
      E("mbr", "disk.system_gpt", f=1.2),
      E("legacy", "fw.uefi", f=1.0),
      E("bios_old", "hw.bios_old", t=0.6, group="age"),
      E("bios_very_old", "hw.bios_very_old", t=1.0, group="age"),
      E("setup", "sec.secureboot.setup_mode", t=0.6)],
  guides=["guide.secureboot.enable", "guide.bios.update"],
  questions=["ask.secureboot_enable_broke_boot"])

R("hvci-memory-integrity-required",
  "Vanguard needs Memory Integrity (Core isolation)",
  "Vanguard asks for Memory Integrity (HVCI) on your PC, and it is off or can't turn on. It usually needs CPU virtualization on in the BIOS and no incompatible drivers.",
  bucket="guidedManual",
  symptoms=["bios_requirement", "error_code", "wont_launch"],
  ev=[E("off", "sec.hvci.running", f=1.5, veto="true", why="Memory Integrity isn't running."),
      E("pending", "sec.hvci.configured_not_running", t=0.8),
      E("virt_off", "sec.virtualization.enabled", f=1.2, why="CPU virtualization is off in the BIOS."),
      E("drivers", "sec.hvci.incompatible_drivers", t=1.0, why="Some drivers block Memory Integrity."),
      E("hyp_off", "sec.bcd.hypervisor_off", t=0.8),
      E("old_build", "os.build_below_19045", t=0.6),
      E("exploit", "sec.exploit_override_riot", t=0.4)],
  fixes=["fix.sec.reset_exploit_overrides"],
  guides=["guide.hvci.enable", "guide.virtualization.enable"])

R("tpm2-missing-or-unmet",
  "TPM 2.0 is off or missing (VAN 9001)",
  "Vanguard needs TPM 2.0. On your PC it is off in the BIOS (fTPM / Intel PTT), missing, or only TPM 1.2. Most PCs from 2017 on have it built in; it's switched on in the BIOS.",
  bucket="guidedManual",
  symptoms=["bios_requirement", "error_code"],
  ev=[E("no_tpm2", "sec.tpm2", f=3.0, veto="true", why="TPM 2.0 isn't available to Windows."),
      E("not_ready", "sec.tpm.not_ready", t=1.2),
      E("tpm_events", "events.tpm_errors", t=0.5)],
  guides=["guide.tpm.enable"])

R("secure-boot-policy-wave-2026",
  "Secure Boot is on, but Vanguard rejects its policy (STATUS_SB_POLICY)",
  "Secure Boot is on, but Vanguard can't verify its keys or policy. This came in a wave with the 2026 Secure Boot certificate change; Windows/BIOS updates and Riot's own patches fixed it.",
  bucket="riotOrAccount",
  symptoms=["error_code", "bios_requirement"],
  requires=["sec.secureboot.on"],
  ev=[E("sb_on", "sec.secureboot.on", t=0.6, veto="false"),
      E("ca2023", "sec.secureboot.db_missing_ca2023", t=1.2, why="The 2023 Secure Boot certificate isn't installed yet."),
      E("bios_old", "hw.bios_old", t=0.4)],
  guides=["guide.secureboot.policy_wave", "guide.riot.support_ticket"],
  wave={"from": "2026-05-01", "to": "2026-12-31", "boost": 0.4})

R("ftpm-event-14-17-stutter",
  "Firmware TPM errors cause periodic stutter (TPM events 14/17)",
  "AMD firmware TPM (fTPM) bugs make the PC stutter for a moment, and Windows logs TPM events 14/17. A BIOS (AGESA) update fixes it.",
  bucket="guidedManual",
  symptoms=["stutter", "freeze_hard"],
  ev=[E("events", "events.tpm_errors", t=2.0, why="Windows logged TPM errors (event 14/17)."),
      E("ftpm", "sec.tpm.firmware", t=0.6),
      E("bios_old", "hw.bios_old", t=0.6),
      E("periodic", "ask.stutter_periodic", t=0.5, ask="ask.stutter_periodic")],
  fixes=["fix.power.usb_selective_suspend_off"],
  guides=["guide.bios.update"])

R("iommu-restriction",
  "Vanguard needs IOMMU / DMA protection (VAN RESTRICTION)",
  "Vanguard asks for IOMMU (AMD-Vi / Intel VT-d) and Kernel DMA Protection. It's switched on in the BIOS; some boards need a BIOS update first.",
  bucket="guidedManual",
  symptoms=["bios_requirement", "error_code"],
  ev=[E("dma_off", "sec.kernel_dma_protection", f=1.5, veto="true", why="Kernel DMA Protection is off."),
      E("no_iommu", "sec.iommu_available", f=1.0),
      E("virt_off", "sec.virtualization.enabled", f=0.6),
      E("bios_old", "hw.bios_old", t=0.4)],
  guides=["guide.iommu.enable", "guide.bios.update"])

R("intel-vmd-status-vmd-9001",
  "Intel VMD storage mode blocks Vanguard (STATUS_VMD_9001)",
  "Your PC (often Dell / Alienware) uses Intel VMD for the SSD. Vanguard can't talk to it. The right Intel RST driver or switching VMD off fixes it, but switching it off wrongly can stop Windows from booting.",
  bucket="guidedManual",
  symptoms=["error_code", "wont_launch"],
  requires=["storage.intel_vmd"],
  ev=[E("vmd", "storage.intel_vmd", t=2.0, veto="false", why="Your SSD runs through an Intel VMD controller.")],
  guides=["guide.storage.intel_vmd"],
  supersedes=["conflict-intel_vmd_rst"])

R("van9101-attestation",
  "Vanguard can't confirm the PC's security state (VAN 9101)",
  "Vanguard couldn't fully check this PC's security state. Check that TPM 2.0 and Secure Boot are on and Windows is up to date, then contact Riot support.",
  bucket="riotOrAccount",
  symptoms=["error_code"],
  ev=[E("tpm", "sec.tpm2", f=1.0),
      E("sb", "sec.secureboot.on", f=1.0)],
  guides=["guide.secureboot.policy_wave", "guide.riot.support_ticket"])

# ============================================================== install_update_corrupt
R("vanguard-install-loop",
  "Vanguard won't install or keeps reinstalling",
  "The Riot Client keeps saying 'Installing Vanguard' or loops between Update and Play. Leftover Vanguard services or files, a missing restart, or antivirus usually cause it. Riot's clean reinstall fixes it.",
  symptoms=["install_update", "wont_launch", "error_code"],
  ev=[E("files", "vg.files_missing", t=1.2),
      E("not_installed", "vg.installed", f=1.0),
      E("vgk_missing", "vg.vgk_missing", t=0.8),
      E("pending", "os.pending_reboot", t=0.6),
      E("av", "sec.third_party_av", t=0.6),
      E("bootstatus", "vg.bootstatus_dat", t=0.5),
      E("old_build", "os.build_below_19045", t=0.4),
      E("dirty", "disk.dirty", t=0.3)],
  fixes=["fix.vg.quarantine_bootstatus", "fix.os.fast_startup_off"],
  guides=["guide.vanguard.reinstall", "guide.av.exclusions"])

R("riot-client-update-stuck",
  "The Riot Client update is stuck or fails",
  "The Riot Client keeps updating, shows 'Something went wrong' or crashes after an update. A repair, clearing the client's cache folder, or a restart fixes this.",
  bucket="guidedManual",
  symptoms=["install_update", "login_issue", "wont_launch"],
  ev=[E("pending", "os.pending_reboot", t=0.6),
      E("non_ascii", "riot.path_non_ascii", t=1.0, why="The Riot install folder has special characters in its name."),
      E("mdns", "net.mdns_lsp", t=0.6),
      E("disk_low", "disk.free_low", t=1.0, why="Your Windows drive is almost full.")],
  guides=["guide.riot.client_repair", "guide.apps.uninstall_bonjour"])

R("valorant-config-corrupt",
  "VALORANT's settings files are damaged",
  "VALORANT's settings files are broken: keybinds or settings don't save, or odd bugs appear. Moving the Config folder away makes the game build fresh ones.",
  bucket="guidedManual",
  symptoms=["crash_desktop", "mouse_input", "display_issue"],
  ev=[E("corrupt", "riot.config_corrupt", t=3.0, why="VALORANT's settings file looks damaged.")],
  guides=["guide.valorant.config_reset"])

R("corrupt-system-or-game-files",
  "Windows or game files are damaged",
  "Some Windows or game files are damaged. Windows' own repair tools (DISM and SFC) and a game repair fix most cases.",
  bucket="guidedManual",
  symptoms=["crash_desktop", "wont_launch"],
  ev=[E("dirty", "disk.dirty", t=1.0),
      E("disk_err", "events.disk_errors", t=1.2, why="Windows logged disk errors."),
      E("hdd", "disk.system_hdd", t=0.3),
      E("sysfiles", "os.system_files_suspect", t=1.0)],
  guides=["guide.storage.chkdsk", "guide.riot.client_repair"])

R("side-by-side-config-error",
  "Microsoft Visual C++ runtimes are missing (side-by-side error)",
  "The game or client shows a 'side-by-side configuration' error because Microsoft Visual C++ runtime files are missing or broken. Installing the official Microsoft packages fixes it.",
  bucket="guidedManual",
  symptoms=["wont_launch", "crash_desktop"],
  ev=[E("vcredist", "apps.vcredist_missing", t=2.0, why="The Microsoft Visual C++ 2015-2022 runtime isn't installed.")],
  guides=["guide.runtimes.install"])

R("xinput-directx-runtime-error",
  "A DirectX file (xinput) is broken",
  "An error mentions xinput1_3.dll or 0xc000012f. The old DirectX runtime files are missing or damaged; Microsoft's DirectX End-User Runtime fixes it.",
  bucket="guidedManual",
  symptoms=["wont_launch", "crash_desktop"],
  ev=[E("vcredist", "apps.vcredist_missing", t=0.5),
      E("n", "os.windows_n", t=0.8)],
  guides=["guide.runtimes.install"])

R("windows-version-too-old",
  "Your Windows version is too old for Vanguard",
  "Vanguard needs at least Windows 10 22H2 (build 19045) or Windows 11. Updating Windows fixes it.",
  bucket="guidedManual",
  symptoms=["error_code", "install_update", "wont_launch"],
  ev=[E("unsupported", "os.build_supported", f=3.5, why="This Windows version is no longer supported by Vanguard."),
      E("old", "os.build_below_19045", t=2.0, veto="false", why="Your Windows 10 is older than 22H2 (build 19045).")],
  guides=["guide.windows.update"])

R("slow-download-riot-cdn",
  "The download is slow because of Riot's servers",
  "The game download crawls. Usually Riot's download servers or your route to them are slow; pausing and resuming, or waiting a few hours, helps.",
  bucket="riotOrAccount",
  symptoms=["install_update"],
  ev=[E("friends", "ask.friends_same_server", t=1.0, ask="ask.friends_same_server")],
  guides=["guide.riot.server_status", "guide.net.cloudflare_warp"])

# ============================================================== login_account_client
R("mdns-bonjour-ni-val43-loading",
  "Apple Bonjour or National Instruments software blocks the loading screen (VAL 43)",
  "Bonjour (installed with iTunes and other Apple or Adobe software) or National Instruments software adds a network component that makes VALORANT hang on the loading screen with VAL 43. Uninstalling it fixes this.",
  symptoms=["launch_black_screen", "error_code", "login_issue"],
  requires=["apps.mdns_component"],
  ev=[E("bonjour", "conflict.bonjour_mdns.installed", t=1.2, group="present", why="Apple Bonjour is installed."),
      E("ni", "conflict.ni_mdns.installed", t=1.2, group="present", why="National Instruments software is installed."),
      E("lsp", "net.mdns_lsp", t=1.0, why="Bonjour/NI added a network filter to Windows."),
      E("svc", "conflict.bonjour_mdns.running", t=0.5),
      E("crash_b", "conflict.bonjour_mdns.crash_module", t=2.5, group="crash"),
      E("crash_n", "conflict.ni_mdns.crash_module", t=2.5, group="crash")],
  fixes=["fix.svc.disable_conflict_service", "fix.net.winsock_reset"],
  guides=["guide.apps.uninstall_bonjour", "guide.vanguard.reinstall"],
  wave={"from": "2025-10-01", "to": "2025-11-30", "boost": 0.5},
  supersedes=["conflict-bonjour_mdns", "conflict-ni_mdns"])

R("account-mfa-matchmaking-glitches",
  "An account or login problem on Riot's side",
  "Login codes, linked accounts or matchmaking act strangely. These are account-side problems a PC tool can't fix; Riot support can.",
  bucket="riotOrAccount",
  symptoms=["login_issue"],
  ev=[E("other_acct", "ask.other_account_works", t=1.5, f=-1.0, ask="ask.other_account_works")],
  guides=["guide.account.other_account", "guide.riot.support_ticket"])

R("val57-59-ipv6-killer",
  "IPv6 on a Killer network card breaks login (VAL 57 / 59)",
  "Killer network cards and some ISPs have IPv6 problems that cause VAL 57 / 59. Turning IPv6 off on the connection fixed it for players.",
  symptoms=["error_code", "login_issue", "disconnect"],
  ev=[E("killer", "conflict.killer_suite.adapter_present", t=1.2, why="You have a Killer network card."),
      E("killer_sw", "conflict.killer_suite.installed", t=0.6),
      E("ipv6", "net.active_ipv6_bound", t=0.4)],
  fixes=["fix.net.unbind_ipv6_active"],
  guides=["guide.net.dns_ipv6"],
  supersedes=["conflict-killer_suite"])

R("wrong-system-clock-offline",
  "Your PC clock is wrong",
  "The PC's date or time is wrong. Secure connections then fail, so the Riot Client can't log in or Vanguard can't connect. Syncing the clock fixes it.",
  symptoms=["login_issue", "error_code", "disconnect"],
  requires=["os.clock_skewed"],
  ev=[E("skew", "os.clock_skewed", t=4.0, veto="false", why="Your PC clock is off by more than 5 minutes."),
      E("auto_off", "os.auto_time_off", t=0.6)],
  fixes=["fix.os.time_resync"],
  guides=["guide.clock.fix"])

R("terms-not-accepted-failed-to-launch",
  "New Riot terms haven't been accepted yet",
  "The game says it failed to launch because the new terms of service weren't accepted. Logging out and back into the Riot Client shows the terms again.",
  bucket="riotOrAccount",
  symptoms=["wont_launch", "login_issue"],
  guides=["guide.riot.client_settings"])

R("console-ps5-errors",
  "This is a console (PlayStation / Xbox) problem",
  "This happens on a console, which VTDiag can't check. Riot or the console maker's support can help.",
  bucket="riotOrAccount",
  symptoms=["error_code", "login_issue", "disconnect"],
  ev=[E("console", "ask.on_console", t=4.0, f=-3.0, ask="ask.on_console")],
  questions=["ask.on_console"])

R("riot-server-side-incident",
  "Riot's servers have a problem right now",
  "Many players have the same problem at the same time. That's a Riot server problem or maintenance; nothing on your PC needs fixing. Check Riot's status page and wait.",
  bucket="riotOrAccount",
  symptoms=["login_issue", "disconnect", "error_code", "high_ping"],
  ev=[E("friends", "ask.friends_same_server", t=3.0, f=-1.5, ask="ask.friends_same_server"),
      E("status", "riot.status_incident", t=3.0)],
  guides=["guide.riot.server_status"],
  questions=["ask.friends_same_server"])

# ============================================================== network_ping
R("local-network-packet-loss-jitter",
  "Your home network loses packets (Wi-Fi, router or cable)",
  "Packets get lost or delayed between your PC and your router: weak or 2.4 GHz Wi-Fi, a busy network, an old cable or router. A cable or a better Wi-Fi spot fixes most of it.",
  bucket="externalNetwork",
  symptoms=["high_ping", "disconnect", "stutter", "voice_chat"],
  ev=[E("gw_loss", "net.gateway_loss", t=2.0, why="Packets get lost between your PC and your router."),
      E("wifi", "net.on_wifi", t=0.4, f=-0.4),
      E("weak", "net.wifi_weak", t=1.2, why="Your Wi-Fi signal is weak."),
      E("band", "net.wifi_2_4ghz", t=0.6),
      E("multi", "net.multi_nic_up", t=0.4),
      E("nic_ps", "net.nic_power_saving_on", t=0.3),
      E("mon_loss", "mon.network.packet_loss", t=1.2),
      E("ask_wifi", "ask.on_wifi_or_cable", t=0.5, ask="ask.on_wifi_or_cable")],
  fixes=["fix.net.disable_second_adapter", "fix.net.realtek_power_saving_off", "fix.net.dns_flush", "fix.net.winsock_reset"],
  guides=["guide.net.local_connection"])

R("isp-routing-high-ping",
  "Your internet provider's route to Riot is bad",
  "Your home network looks fine, but ping to Riot's servers is high or unstable. That's your internet provider's routing; a free tool like Cloudflare WARP often finds a better route, and your ISP can fix it for good.",
  bucket="externalNetwork",
  symptoms=["high_ping", "disconnect", "voice_chat"],
  ev=[E("high", "net.region_ping_high", t=1.5, why="Ping to the nearest Riot region is high."),
      E("rloss", "net.region_loss", t=1.2, why="Packets get lost on the way to Riot, not inside your home."),
      E("jitter", "net.region_jitter_high", t=0.8),
      E("gw_bad", "net.gateway_loss", t=-1.0),
      E("friends", "ask.friends_same_server", t=0.6, ask="ask.friends_same_server"),
      E("mon_ping", "mon.network.ping_spikes", t=0.8)],
  guides=["guide.net.cloudflare_warp"])

R("dual-network-adapter-desync",
  "Two network connections at once cause desync",
  "Cable and Wi-Fi (or two adapters) are connected at the same time. Windows may switch between them mid-match, which causes desync and VAN 72 / 185 errors.",
  symptoms=["disconnect", "high_ping", "error_code"],
  ev=[E("multi", "net.multi_nic_up", t=1.8, veto="false", why="Two network connections are active at the same time."),
      E("second", "net.secondary_adapter_up", t=0.6)],
  fixes=["fix.net.disable_second_adapter"],
  guides=["guide.net.local_connection"])

R("network-exceptions-download-stuck",
  "A firewall, VPN or network block stops the download or login (VAL 62)",
  "Riot's servers can't be reached properly: a firewall rule, an antivirus web filter, a country or ISP block, or old network settings. Allow Riot through the firewall or try a free VPN like WARP.",
  bucket="externalNetwork",
  symptoms=["install_update", "login_issue", "error_code"],
  ev=[E("fw", "net.firewall_blocks_riot", t=1.5),
      E("proxy", "net.proxy_on", t=0.8),
      E("lsp", "net.lsp_third_party", t=0.6),
      E("no_inet", "net.no_internet", t=1.0)],
  fixes=["fix.net.dns_flush", "fix.net.winsock_reset"],
  guides=["guide.network.firewall", "guide.net.cloudflare_warp"])

R("lenovo-vantage-wifi-collapse",
  "Lenovo Vantage breaks the Wi-Fi connection",
  "A Lenovo Vantage version made Wi-Fi collapse during games. Updating Lenovo Vantage fixed it.",
  bucket="guidedManual",
  symptoms=["disconnect", "high_ping"],
  requires=["conflict.lenovo_vantage.any"],
  ev=[E("vantage", "conflict.lenovo_vantage.installed", t=1.2, veto="false"),
      E("wifi", "net.on_wifi", t=0.6)],
  guides=["guide.power.laptop_mode"],
  supersedes=["conflict-lenovo_vantage"])

R("nvidia-driver-packet-loss",
  "An NVIDIA driver version causes packet loss",
  "One NVIDIA driver version was reported to cause packet loss. Rolling back to an older driver helped one player; it's a rare cause.",
  bucket="guidedManual",
  symptoms=["high_ping", "disconnect"],
  ev=[E("nv", "gpu.nvidia_dgpu", t=0.3, veto="false"),
      E("new", "gpu.driver_recently_changed", t=1.0)],
  guides=["guide.gpu.clean_driver"])

# ============================================================== gpu_driver
R("amd-adrenalin-driver-regression",
  "Your AMD graphics driver version is causing freezes or crashes",
  "Some AMD Adrenalin driver versions cause freezes, black screens and driver crashes in VALORANT. Rolling back to a stable version (clean install) fixes it; turning off extra Adrenalin features can help too.",
  bucket="guidedManual",
  symptoms=["freeze_hard", "crash_desktop", "stutter", "display_issue"],
  requires=["gpu.amd_dgpu"],
  ev=[E("amd", "gpu.amd_dgpu", t=0.6, veto="false"),
      E("tdr", "events.display_tdr", t=1.5, why="Windows logged graphics driver resets."),
      E("d3d", "log.d3d_device_lost", t=1.2, why="VALORANT's log shows the graphics driver crashed."),
      E("gpu_crash", "crash.gpu", t=1.0),
      E("new", "gpu.driver_recently_changed", t=0.8),
      E("features", "conflict.amd_adrenalin_features.running", t=0.3),
      E("mpo", "display.mpo_enabled", t=0.2),
      AFTER_UPDATE],
  fixes=["fix.display.mpo_off", "fix.display.hags_off"],
  guides=["guide.gpu.clean_driver"],
  supersedes=["conflict-amd_adrenalin_features"])

R("gpu-driver-corrupt-needs-clean-install",
  "Your graphics driver is broken and needs a clean install",
  "The graphics driver crashes or resets (TDR). A clean reinstall of a stable driver from NVIDIA, AMD or Intel fixes it.",
  bucket="guidedManual",
  symptoms=["crash_desktop", "freeze_hard", "display_issue"],
  ev=[E("tdr", "events.display_tdr", t=1.5, why="Windows logged graphics driver resets."),
      E("d3d", "log.d3d_device_lost", t=1.5, why="VALORANT's log shows the graphics driver crashed."),
      E("gpu_crash", "crash.gpu", t=1.0),
      E("problem", "gpu.problem_code", t=1.5, why="Device Manager shows an error on your graphics card."),
      E("basic", "gpu.basic_display", t=1.2),
      E("old", "gpu.driver_old", t=0.4)],
  guides=["guide.gpu.clean_driver"])

R("intel-graphics-chipset-driver-outdated",
  "Intel graphics or chipset drivers are outdated",
  "Old Intel graphics or chipset drivers cause crashes and VAN 102 kicks on Intel laptops. Intel's free Driver & Support Assistant updates them.",
  bucket="guidedManual",
  symptoms=["crash_desktop", "error_code", "fps_low"],
  requires=["gpu.intel_driver_old"],
  ev=[E("old", "gpu.intel_driver_old", t=1.5, veto="false", why="Your Intel graphics driver is more than two years old.")],
  guides=["guide.gpu.igpu"])

R("wrong-gpu-or-display-output",
  "VALORANT uses the wrong graphics chip or monitor port",
  "Your monitor is plugged into the motherboard, or Windows runs VALORANT on the built-in graphics. Plug into the graphics card and set VALORANT to the high-performance GPU.",
  symptoms=["fps_low", "display_issue"],
  ev=[E("monitor", "gpu.monitor_on_igpu", t=2.5, why="Your monitor is plugged into the motherboard, not the graphics card."),
      E("pref", "gpu.pref_not_high", t=1.0),
      E("igpu_off", "gpu.igpu_disabled", t=0.4)],
  fixes=["fix.display.gpu_pref_high"],
  guides=["guide.gpu.igpu"])

R("nvidia-vsync-fast-critical-error",
  "NVIDIA 'Fast' V-Sync crashes VALORANT",
  "NVIDIA Control Panel's Vertical sync set to 'Fast' makes VALORANT show a critical error. Setting it to 'Use the 3D application setting' fixes it.",
  bucket="guidedManual",
  symptoms=["crash_desktop", "wont_launch"],
  requires=["gpu.nvidia_dgpu"],
  ev=[E("nv", "gpu.nvidia_dgpu", t=0.5, veto="false"),
      E("fatal", "log.fatal_error", t=0.8),
      E("profile", "gpu.nv_profile_overrides", t=2.0)],
  guides=["guide.display.settings"])

# ============================================================== audio_mic
R("bluetooth-headset-hfp-voice-chat",
  "Your Bluetooth headset switches to 'phone call' mode in voice chat",
  "When voice chat opens your Bluetooth headset's microphone, Windows switches it to Hands-Free mode. Game sound then turns muffled and mono. Turning off Hands-Free mode keeps sound clear (but the headset mic stops working over Bluetooth).",
  symptoms=["voice_chat", "no_audio"],
  requires=["audio.bt_handsfree_present"],
  ev=[E("hfp", "audio.bt_handsfree_present", t=1.5, why="Your Bluetooth headset has a Hands-Free (phone call) mode."),
      E("default", "audio.bt_handsfree_default", t=1.0, why="Windows uses the Hands-Free mode as a default device."),
      E("uses", "ask.uses_bluetooth_headset", t=1.0, f=-2.5, ask="ask.uses_bluetooth_headset")],
  fixes=["fix.audio.bt_handsfree_off"],
  guides=["guide.audio.bt_handsfree"],
  questions=["ask.uses_bluetooth_headset"],
  supersedes=["conflict-bluetooth_handsfree"])

R("audio-device-config-issues",
  "Sound goes to the wrong device or is set up oddly",
  "No sound, crackling or broken voice chat because of the output device, per-app sound settings, exclusive mode, virtual audio programs or an audio driver.",
  bucket="guidedManual",
  symptoms=["no_audio", "voice_chat"],
  ev=[E("no_default", "audio.no_default_output", t=2.0, why="Windows has no default sound output."),
      E("many", "audio.many_outputs", t=0.6),
      E("virtual", "conflict.virtual_audio_mics.installed", t=0.6),
      E("exitlag", "conflict.exitlag.running", t=0.4),
      E("enhancers", "conflict.audio_enhancers.installed", t=0.3)],
  guides=["guide.audio.output_device"])

R("voice-chat-network-dns-ipv6",
  "Voice chat fails because of DNS or IPv6",
  "Voice chat doesn't connect or keeps dropping. DNS or IPv6 problems on your connection are a common cause; public DNS, IPv6 off or WARP fixed it for players.",
  symptoms=["voice_chat"],
  ev=[E("ipv6", "net.active_ipv6_bound", t=0.4),
      E("teredo", "net.teredo_on", t=0.4),
      E("ipv6_off", "net.ipv6_disabled", t=0.6),
      E("cant_hear", "ask.others_cant_hear_me", t=0.3, ask="ask.others_cant_hear_me"),
      E("bt", "ask.uses_bluetooth_headset", t=-0.8, ask="ask.uses_bluetooth_headset")],
  fixes=["fix.net.dns_flush", "fix.net.unbind_ipv6_active"],
  guides=["guide.net.dns_ipv6", "guide.net.cloudflare_warp"])

R("discord-stream-no-voice",
  "Discord stream doesn't carry VALORANT's sound",
  "When you stream VALORANT on Discord, viewers can't hear the game. It's a Discord capture setting, not your PC.",
  bucket="guidedManual",
  symptoms=["voice_chat", "no_audio"],
  ev=[E("discord", "conflict.discord_overlay.running", t=0.8)],
  guides=["guide.discord.stream_audio"])

# ============================================================== display_resolution
R("stretched-resolution-issues",
  "Stretched resolution doesn't work",
  "Stretched or 'true stretched' resolution shows black bars or doesn't stick. It's set up in your graphics card's control panel; editing game files is risky.",
  bucket="guidedManual",
  symptoms=["display_issue"],
  ev=[E("multi", "display.multi_monitor", t=0.3)],
  guides=["guide.display.stretched_res"])

R("display-misc-blur-wrong-res",
  "Wrong resolution, blur or a missing monitor",
  "The game starts at the wrong resolution, looks blurry, or a monitor is missing. The NVIDIA app's automatic optimization, a disabled monitor or the in-game display mode are typical causes.",
  bucket="guidedManual",
  symptoms=["display_issue"],
  ev=[E("multi", "display.multi_monitor", t=0.5),
      E("problem", "dev.problem_devices", t=0.4),
      E("hdr", "display.hdr_on", t=0.3)],
  guides=["guide.display.settings"])

R("fullscreen-optimizations-mpo",
  "Fullscreen optimizations or MPO cause stutter and alt-tab freezes",
  "Windows fullscreen optimizations and Multi-Plane Overlay (MPO) change how the game is shown. With some drivers that causes stutter, flicker or freezes when you alt-tab. Turning them off helped players.",
  symptoms=["stutter", "display_issue", "freeze_hard"],
  ev=[E("mpo", "display.mpo_enabled", t=0.4),
      E("fso_off", "riot.fso_disabled", t=-1.0),
      E("multi", "display.multi_monitor", t=0.4),
      E("amd", "gpu.amd_dgpu", t=0.2)],
  fixes=["fix.display.disable_fso", "fix.display.mpo_off"],
  guides=["guide.display.settings"],
  supersedes=["conflict-mpo", "conflict-fso_compat_flags"])

R("vsync-gsync-tearing-fps-lock",
  "V-Sync / G-Sync settings cause tearing or an FPS lock",
  "FPS is locked to your refresh rate, or the picture tears. V-Sync, G-Sync and frame caps in the driver or game are set against each other.",
  bucket="guidedManual",
  symptoms=["display_issue", "fps_low"],
  ev=[E("refresh60", "display.refresh_60", t=0.8),
      E("nv", "gpu.nvidia_dgpu", t=0.2),
      E("profile", "gpu.nv_profile_overrides", t=1.0)],
  guides=["guide.display.settings"])

R("replay-system-issues",
  "The replay system lags or breaks",
  "The replay system stutters or fails. Capping FPS (for example to 360) fixed it for a player; otherwise wait for a Riot fix.",
  bucket="guidedManual",
  symptoms=["display_issue", "fps_low"],
  guides=["guide.display.settings"])

# ============================================================== input_mouse
R("mouse-usb-port-polling-lag",
  "The mouse lags because of its USB port or polling rate",
  "The mouse feels laggy or jumpy because of a front-panel/hub USB port, a far-away wireless dongle, a very high polling rate, or USB power saving.",
  bucket="guidedManual",
  symptoms=["mouse_input"],
  ev=[E("usb_ss", "power.usb_selective_suspend_on", t=0.5),
      E("accel", "input.mouse_accel_on", t=0.3)],
  fixes=["fix.power.usb_selective_suspend_off"],
  guides=["guide.mouse.usb"])

R("mouse-acceleration-floaty-aim",
  "Mouse acceleration makes your aim feel floaty",
  "'Enhance pointer precision' (mouse acceleration) is on, so the cursor moves further when you move fast. Aim feels floaty and inconsistent.",
  symptoms=["mouse_input"],
  ev=[E("accel", "input.mouse_accel_on", t=2.5, f=-1.0, why="'Enhance pointer precision' is on."),
      E("windowed", "display.windowed_opt_on", t=0.4),
      E("hags", "display.hags_on", t=0.2)],
  fixes=["fix.input.mouse_accel_off", "fix.display.hags_off"],
  guides=["guide.mouse.usb"])

R("mouse-clicks-stop-after-alt-tab",
  "Mouse clicks stop working after alt-tab",
  "Mouse buttons stop working after alt-tab. A side button mapped to 'Browser Back' in the mouse software, or unstable memory settings, were behind it.",
  bucket="guidedManual",
  symptoms=["mouse_input"],
  ev=[E("icue", "conflict.corsair_icue.installed", t=0.6),
      E("ghub", "conflict.logitech_ghub.installed", t=0.4),
      E("synapse", "conflict.razer_synapse.installed", t=0.4)],
  guides=["guide.mouse.usb"])

# ============================================================== hardware_other, other
R("usb-optical-drive-restart",
  "A USB CD/DVD drive makes the PC restart",
  "An external CD, DVD or Blu-ray drive on USB made the PC restart when VALORANT started. Unplugging it fixed it.",
  bucket="likelyHardware",
  symptoms=["freeze_hard"],
  requires=["dev.usb_optical_drive"],
  ev=[E("optical", "dev.usb_optical_drive", t=2.5, veto="false", why="A USB CD/DVD drive is connected.")],
  guides=["guide.hardware.freeze_checks"])

# ============================================================== route rules (short-circuit, no fixes)
# A route rule fires when every "requires" signal is definitely True (Unknown never fires a route).
R("route-virtual-machine",
  "VALORANT can't run in a virtual machine",
  "This Windows runs inside a virtual machine. Vanguard doesn't allow that, and no setting changes it. Play on a normal Windows PC.",
  kind="route", route="unsupportedPlatform", bucket="riotOrAccount",
  requires=["os.vm"])
R("route-arm-cpu",
  "VALORANT doesn't support ARM processors",
  "This PC has an ARM processor (for example Snapdragon). Vanguard only runs on x64 Intel and AMD processors.",
  kind="route", route="unsupportedPlatform", bucket="riotOrAccount",
  requires=["hw.cpu_arm"])
R("route-console",
  "This is a console problem",
  "You play on a console. VTDiag checks Windows PCs only; Riot or the console maker's support can help.",
  kind="route", route="unsupportedPlatform", bucket="riotOrAccount",
  requires=["ask.on_console"])
R("route-account-side",
  "This looks like an account problem, not your PC",
  "Another Riot account works on this PC, so your PC is fine. The problem is on your account's side; contact Riot support.",
  kind="route", route="tryAnotherAccount", bucket="riotOrAccount",
  requires=["ask.other_account_works"])
R("route-riot-incident",
  "Riot's servers have a problem right now",
  "Riot reports a server problem or maintenance. There's nothing to fix on your PC; wait until Riot's status page is clear.",
  kind="route", route="riotServerIncident", bucket="riotOrAccount",
  requires=["riot.status_incident"])
