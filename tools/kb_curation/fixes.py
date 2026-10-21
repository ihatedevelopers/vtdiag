# Fixes: JSON that only composes audited primitives from kb/schema/ops.json.
# No commands, scripts, URLs or free paths. BCD / HVCI / Secure Boot / BIOS / uninstall are guides, never steps.
# Strings: fix.<area>.<name>.title|explain|undo|notice|side.N are generated from the English text here.
# success_match: regexes over the catalog fix texts of the hypotheses in applies_to; the matching report counts
# seed P(fix works | cause) as Beta(success + 1, trials - success + 1).

FIXES = []
OUTCOMES = {}  # key -> English text
DRIFTS = {}
GUARD_TEXTS = {}


def outcome(key, text):
    OUTCOMES[key] = text
    return key


def drift(key, text):
    DRIFTS[key] = text
    return key


def guard(probe, fail_key, text, must="true", severity="block", **args):
    GUARD_TEXTS[fail_key] = text
    return dict(probe=probe, args=args, must=must, severity=severity, failKey=fail_key)


def step(op, on_fail="abort", **args):
    return dict(op=op, args={k: str(v) for k, v in args.items()}, onFail=on_fail)


def verify(probe, expect, when="immediate", **args):
    return dict(probe=probe, args={k: str(v) for k, v in args.items()}, expect={k: str(v) for k, v in expect.items()}, when=when)


def fix(id, title, explain, undo, *, risk="low", rev="exact", admin=True, reboot="none", scope="persistent", minutes=0.5,
        applies=(), template=False, bind=None, guards=(), steps=(), verifies=(), deadman=False, notice=None,
        side_effects=(), outcome_kind="playtest", outcome_key=None, monitor_signals=(), drift_key=None,
        success_match=(), tested=("win11-25H2", "win10-22H2")):
    FIXES.append(dict(id=id, title=title, explain=explain, undo=undo, risk=risk, rev=rev, admin=admin, reboot=reboot,
                      scope=scope, minutes=minutes, applies=list(applies), template=template, bind=bind or {},
                      guards=list(guards), steps=list(steps), verifies=list(verifies), deadman=deadman, notice=notice,
                      side_effects=list(side_effects), outcome_kind=outcome_kind, outcome_key=outcome_key,
                      monitor_signals=list(monitor_signals), drift_key=drift_key, success_match=list(success_match),
                      tested=list(tested)))


O_STUTTER = outcome("outcome.stutter_gone", "Play one match. Did the stutter stop?")
O_LAUNCH = outcome("outcome.past_loading_screen", "Start VALORANT. Does it get past the loading screen now?")
O_CONNECT = outcome("outcome.connects_now", "Try again. Does it connect and stay connected now?")
O_VOICE = outcome("outcome.voice_ok", "Join a match with voice chat on. Does your headset sound normal now?")
O_FREEZE = outcome("outcome.no_freeze", "Play for a while. Did the freezes stop?")
O_FPS = outcome("outcome.fps_better", "Play one match. Is your FPS better now?")
O_ERROR = outcome("outcome.error_gone", "Start VALORANT. Is the error gone?")
O_MOUSE = outcome("outcome.mouse_ok", "Try aiming in the Range. Does your mouse feel normal now?")
O_PING = outcome("outcome.ping_ok", "Play one match. Is your ping steady now?")

D_ADAPTER = drift("drift.adapter_reenabled", "The network port we turned off is on again (Windows Update or a driver install can do this).")
D_SERVICE = drift("drift.oem_reinstalled", "The program we turned off came back (often after a Windows or manufacturer update).")
D_SETTING = drift("drift.setting_reverted", "A setting we changed went back to how it was (often after an update).")

G_NOT_DEFAULT = guard("adapter.not_default_route", "guard.adapter_is_active",
                      "This is the connection you are using right now, so we will not turn it off.", instanceId="$adapter.id")
G_OTHER_ONLINE = guard("connectivity.other_adapter_online", "guard.no_other_connection",
                       "No other internet connection is working, so turning this off could cut your internet.",
                       exceptInstanceId="$adapter.id")
G_INTERNET = guard("connectivity.internet", "guard.no_internet",
                   "Your PC has no internet right now. Fix the connection first.")

WIN64 = "ShooterGame\\Binaries\\Win64\\VALORANT-Win64-Shipping.exe"

# ---------------------------------------------------------------- network
fix("fix.net.disable_unused_adapter",
    "Turn off the network port you're not using",
    "Your PC has a wired Realtek network port that is switched on but not used. Its driver is known to cause short freezes every few seconds in VALORANT. We'll switch it off; your current connection keeps working.",
    "Open VTDiag's undo list and press Undo. The port turns back on right away.",
    notice="You stay on your current connection. If you plug in a cable into that port later, undo this first.",
    applies=["realtek-2-5gbe-nic-stutter", "whole-pc-freeze-kernel-power-41", "usb-selective-suspend-freeze", "conflict-realtek_2_5gbe", "conflict-realtek_gbe_family"],
    bind={"adapter": ("adapter", "net.realtek25.unused", "one")},
    guards=[G_NOT_DEFAULT, G_OTHER_ONLINE],
    steps=[step("device.disable", instanceId="$adapter.id")],
    verifies=[verify("device.disable", {"enabled": "false"}, instanceId="$adapter.id")],
    deadman=True, minutes=0.2, outcome_kind="monitorCompare", outcome_key=O_STUTTER,
    monitor_signals=["mon.frametime.periodic_spikes"], drift_key=D_ADAPTER,
    success_match=[r"disable.*(realtek|unused|onboard lan|adapter)", r"realtek.*disable"])

fix("fix.net.disable_second_adapter",
    "Turn off the second network connection",
    "Two network connections are active at the same time (for example cable and Wi-Fi). Windows can switch between them in the middle of a match, which causes lag and disconnects. We'll switch off the one you're not using.",
    "Press Undo in VTDiag's undo list and the adapter turns back on.",
    notice="Your main connection keeps working.",
    applies=["dual-network-adapter-desync", "local-network-packet-loss-jitter", "lenovo-vantage-wifi-collapse", "van185-vanguard-disconnects"],
    bind={"adapter": ("adapter", "net.secondary_adapter_up", "one")},
    guards=[G_NOT_DEFAULT, G_OTHER_ONLINE],
    steps=[step("device.disable", instanceId="$adapter.id")],
    verifies=[verify("device.disable", {"enabled": "false"}, instanceId="$adapter.id")],
    deadman=True, minutes=0.2, outcome_key=O_CONNECT, drift_key=D_ADAPTER,
    success_match=[r"disable.*(wi-?fi|wireless|unused|weaker).*adapter", r"disable.*adapter"])

fix("fix.net.realtek_power_saving_off",
    "Turn off power saving on your network card",
    "Your network card has 'Energy Efficient Ethernet' or 'Green Ethernet' switched on. These power-saving features can make the connection pause for a split second. We'll switch them off.",
    "Press Undo and the old power-saving settings come back.",
    notice="Your connection drops for a few seconds while the card restarts.",
    applies=["realtek-2-5gbe-nic-stutter", "local-network-packet-loss-jitter", "conflict-realtek_2_5gbe", "conflict-realtek_gbe_family"],
    bind={"adapter": ("adapter", "net.nic_power_saving_on", "one")},
    guards=[G_INTERNET],
    steps=[step("netadapter.setAdvancedProperty", "continue", instanceId="$adapter.id", keyword="*EEE", value="0"),
           step("netadapter.setAdvancedProperty", "continue", instanceId="$adapter.id", keyword="EnableGreenEthernet", value="0"),
           step("netadapter.setAdvancedProperty", "continue", instanceId="$adapter.id", keyword="AdvancedEEE", value="0"),
           step("netadapter.setAdvancedProperty", "continue", instanceId="$adapter.id", keyword="GigaLite", value="0")],
    verifies=[verify("netadapter.setAdvancedProperty", {"value": "0"}, instanceId="$adapter.id", keyword="*EEE")],
    deadman=True, minutes=0.5, outcome_key=O_STUTTER, drift_key=D_SETTING,
    success_match=[r"(eee|green ethernet|energy|power.?saving)"])

fix("fix.net.dns_flush",
    "Clear the DNS cache",
    "Windows keeps a list of internet addresses it looked up before. If an entry is old or wrong, Riot servers can't be reached. Clearing it is harmless; Windows rebuilds the list by itself.",
    "There is nothing to undo: Windows refills the list automatically.",
    rev="oneWay", minutes=0.1,
    applies=["voice-chat-network-dns-ipv6", "network-exceptions-download-stuck", "local-network-packet-loss-jitter", "van185-vanguard-disconnects"],
    steps=[step("dns.flush")],
    outcome_key=O_CONNECT,
    success_match=[r"flushdns|flush dns"])

fix("fix.net.unbind_ipv6_active",
    "Turn off IPv6 on your connection",
    "Some network cards (for example Killer) and some internet providers have trouble with IPv6, which can break voice chat or cause VAL 57 / 59 errors. We'll switch IPv6 off only on the connection you use. Normal internet keeps working over IPv4.",
    "Press Undo and IPv6 is switched back on.",
    notice="Your connection drops for a moment.",
    applies=["val57-59-ipv6-killer", "voice-chat-network-dns-ipv6"],
    bind={"adapter": ("adapter", "net.active_ipv6_bound", "one")},
    guards=[G_INTERNET],
    steps=[step("netadapter.unbindIpv6", instanceId="$adapter.id")],
    verifies=[verify("netadapter.unbindIpv6", {"ipv6Bound": "false"}, instanceId="$adapter.id")],
    deadman=True, minutes=0.3, outcome_key=O_CONNECT, drift_key=D_SETTING,
    success_match=[r"ipv6"])

fix("fix.net.winsock_reset",
    "Reset the Windows network settings (Winsock)",
    "Extra network filters from VPNs, boosters or old software can break how Windows talks to Riot. A Winsock reset puts the network settings back to factory state. This can't be undone with a click and needs a restart.",
    "This can't be undone with one click. If something stops working, reinstall the VPN or network tool that needed the old settings.",
    risk="medium", rev="oneWay", reboot="required", minutes=3,
    notice="You'll need to restart. VPN and network-booster apps may need to be reinstalled.",
    side_effects=["VPN or proxy programs may stop working until you reinstall them."],
    applies=["local-network-packet-loss-jitter", "network-exceptions-download-stuck", "mdns-bonjour-ni-val43-loading", "conflict-gearup_booster", "conflict-tcp_tweakers"],
    guards=[G_INTERNET],
    steps=[step("oneway.winsockReset")],
    deadman=True, outcome_key=O_CONNECT,
    success_match=[r"winsock"])

# ---------------------------------------------------------------- services, processes (templates + bound)
fix("fix.svc.disable_conflict_service",
    "Turn off the program that clashes with VALORANT",
    "A background program on your PC is known to clash with VALORANT or Vanguard. We'll stop it and keep it from starting with Windows. The program stays installed.",
    "Press Undo and the program starts with Windows again.",
    notice="The program's features (for example audio effects or RGB lighting) stop until you undo this.",
    template=True,
    applies=["nahimic-audio-service-launch-hang", "mdns-bonjour-ni-val43-loading", "razer-synapse-escalation-kick",
             "third-party-hook-overlay-conflict"],
    bind={"svc": ("service", "conflict.*.service_present", "all")},
    steps=[step("service.setStartType", name="$svc.id", startType="Disabled"),
           step("service.stop", "continue", name="$svc.id", timeoutSec="20")],
    verifies=[verify("service.setStartType", {"startType": "Disabled"}, name="$svc.id")],
    minutes=0.3, outcome_kind="relaunch", outcome_key=O_LAUNCH, drift_key=D_SERVICE,
    success_match=[r"(stop|disable).*(service|nahimic|bonjour|synapse|escalation)"])

fix("fix.proc.close_overlays",
    "Close overlay and recording programs for this session",
    "Overlay programs (like RivaTuner, Overwolf or clip recorders) hook into the game to draw on screen. After recent VALORANT updates, these hooks cause stutter, freezes and stuck loading. We'll close them for now; they'll start again next time you turn on your PC.",
    "Nothing to undo: open the programs again whenever you want.",
    rev="sessionOnly", admin=False, scope="session", minutes=0.2,
    applies=["third-party-hook-overlay-conflict", "shader-cache-compilation-stutter", "fps-decay-over-time", "frametime-latency-spikes", "igpu-disabled-or-driverless-freeze", "van185-vanguard-disconnects"],
    bind={"proc": ("process", "proc.overlay_running", "all")},
    steps=[step("process.closeGraceful", "continue", imageName="$proc.id")],
    verifies=[verify("process.closeGraceful", {"running": "false"}, imageName="$proc.id")],
    outcome_key=O_STUTTER,
    success_match=[r"close|exit|uninstall.*(rtss|rivatuner|overwolf|overlay|medal|afterburner)", r"(rtss|overwolf|overlay).*(close|off|disable)"])

fix("fix.generic.close_process",
    "Close this program for this session",
    "This program is known to clash with VALORANT. We'll ask it to close (like clicking its X). It starts again next time you turn on your PC.",
    "Nothing to undo: open the program again whenever you want.",
    rev="sessionOnly", admin=False, scope="session", minutes=0.2, template=True,
    bind={"proc": ("process", "conflict.*.running", "all")},
    steps=[step("process.closeGraceful", "continue", imageName="$proc.id")],
    verifies=[verify("process.closeGraceful", {"running": "false"}, imageName="$proc.id")],
    outcome_key=O_LAUNCH)

fix("fix.generic.disable_device",
    "Turn off this device",
    "This device or its driver is known to clash with VALORANT. We'll switch it off in Device Manager. Nothing is uninstalled.",
    "Press Undo and the device turns back on.",
    template=True,
    bind={"dev": ("device", None, "one")},
    steps=[step("device.disable", instanceId="$dev.id")],
    verifies=[verify("device.disable", {"enabled": "false"}, instanceId="$dev.id")],
    outcome_key=O_LAUNCH, drift_key=D_ADAPTER)

fix("fix.generic.remove_compat_layer",
    "Remove old compatibility settings from a program",
    "A program has old Windows compatibility settings (like 'run in Windows 8 mode'). These can stop VALORANT or the Riot Client from starting. We'll remove the listed settings.",
    "Press Undo and the settings come back.",
    template=True,
    bind={"layer": ("compatLayer", "riot.compat_layers_present", "one")},
    steps=[step("appcompat.removeLayers", exePath="$layer.id", hive="HKCU", layers="$layer.name")],
    verifies=[verify("appcompat.removeLayers", {"exists": "false"}, exePath="$layer.id", hive="HKCU", layers="$layer.name")],
    outcome_key=O_LAUNCH)

fix("fix.generic.unbind_ipv6",
    "Turn off IPv6 on this adapter",
    "We'll switch IPv6 off on this one network adapter. Internet keeps working over IPv4.",
    "Press Undo and IPv6 is switched back on.",
    template=True,
    bind={"adapter": ("adapter", None, "one")},
    guards=[G_INTERNET],
    steps=[step("netadapter.unbindIpv6", instanceId="$adapter.id")],
    verifies=[verify("netadapter.unbindIpv6", {"ipv6Bound": "false"}, instanceId="$adapter.id")],
    deadman=True, outcome_key=O_CONNECT)

fix("fix.generic.disable_startup_hkcu",
    "Stop this program from starting with Windows",
    "This program starts every time you sign in and can clash with VALORANT. We'll switch off its auto-start; it stays installed and you can still open it yourself.",
    "Press Undo and it starts with Windows again.",
    admin=False, template=True,
    bind={"item": ("startupItem", None, "one")},
    steps=[step("startup.disable", location="HkcuRun", name="$item.name")],
    verifies=[verify("startup.disable", {"enabled": "false"}, location="HkcuRun", name="$item.name")],
    outcome_key=O_LAUNCH)

fix("fix.generic.disable_startup_hklm",
    "Stop this program from starting with Windows (all users)",
    "This program starts for every user when Windows starts and can clash with VALORANT. We'll switch off its auto-start; it stays installed.",
    "Press Undo and it starts with Windows again.",
    template=True,
    bind={"item": ("startupItem", None, "one")},
    steps=[step("startup.disable", location="HklmRun", name="$item.name")],
    verifies=[verify("startup.disable", {"enabled": "false"}, location="HklmRun", name="$item.name")],
    outcome_key=O_LAUNCH)

# ---------------------------------------------------------------- audio
fix("fix.audio.bt_handsfree_off",
    "Stop your Bluetooth headset from switching to 'phone call' mode",
    "When VALORANT voice chat opens your Bluetooth headset's microphone, Windows switches the headset to Hands-Free mode, which sounds like an old phone call. We'll switch off Hands-Free mode, so game sound stays clear.",
    "Press Undo and Hands-Free mode comes back.",
    notice="Your headset's own microphone will not work over Bluetooth. Use a wired or USB microphone, or your laptop's microphone, for voice chat.",
    side_effects=["The Bluetooth headset microphone stops working until you undo this."],
    applies=["bluetooth-headset-hfp-voice-chat", "conflict-bluetooth_handsfree"],
    bind={"dev": ("device", "audio.bt_handsfree_present", "all")},
    steps=[step("device.disable", instanceId="$dev.id")],
    verifies=[verify("device.disable", {"enabled": "false"}, instanceId="$dev.id")],
    minutes=0.3, outcome_key=O_VOICE, drift_key=D_SETTING,
    success_match=[r"hands-?free|handsfree|btagservice|telephony"])

# ---------------------------------------------------------------- power
fix("fix.power.usb_selective_suspend_off",
    "Stop Windows from putting USB ports to sleep",
    "Windows can put USB ports to sleep to save power. On some PCs this makes the mouse, keyboard or headset drop out for a moment, or freezes the whole PC. We'll switch that off.",
    "Press Undo and the old power setting comes back.",
    notice="Laptops use slightly more battery.",
    applies=["usb-selective-suspend-freeze", "mouse-usb-port-polling-lag", "ftpm-event-14-17-stutter", "whole-pc-freeze-kernel-power-41", "igpu-disabled-or-driverless-freeze"],
    steps=[step("powercfg.setValue", scheme="active", subgroup="2a737441-1930-4402-8d77-b2bebba308a3",
                setting="48e6b7a6-50f5-4782-a5d4-53bb8f07e226", ac="0", dc="0")],
    verifies=[verify("powercfg.setValue", {"ac": "0"}, scheme="active", subgroup="2a737441-1930-4402-8d77-b2bebba308a3",
                     setting="48e6b7a6-50f5-4782-a5d4-53bb8f07e226")],
    minutes=0.2, outcome_key=O_FREEZE, drift_key=D_SETTING,
    success_match=[r"selective suspend"])

fix("fix.power.high_performance",
    "Switch to the High performance power plan",
    "Your PC uses a power plan that saves energy by slowing the processor down. We'll switch to the High performance plan so VALORANT gets full speed.",
    "Press Undo and your old power plan comes back.",
    notice="Your PC uses more power and laptops run warmer.",
    applies=["power-plan-throttling-low-fps", "low-gpu-usage-after-ue5-patch", "fps-lower-than-expected-caps", "fps-decay-over-time"],
    steps=[step("powercfg.setActiveScheme", scheme="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")],
    verifies=[verify("powercfg.setActiveScheme", {"activeScheme": "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"})],
    minutes=0.2, outcome_key=O_FPS, drift_key=D_SETTING,
    success_match=[r"high performance|best performance|power plan|power mode"])

fix("fix.power.cpu_max_100",
    "Let the processor run at full speed",
    "The 'maximum processor state' power setting is below 100%, so your processor never reaches full speed. We'll set it back to 100%.",
    "Press Undo and the old limit comes back.",
    applies=["power-plan-throttling-low-fps", "fps-lower-than-expected-caps"],
    steps=[step("powercfg.setValue", scheme="active", subgroup="54533251-82be-4824-96c1-47b60b740d00",
                setting="bc5038f7-23e0-4960-96da-33abaf5935ec", ac="100")],
    verifies=[verify("powercfg.setValue", {"ac": "100"}, scheme="active", subgroup="54533251-82be-4824-96c1-47b60b740d00",
                     setting="bc5038f7-23e0-4960-96da-33abaf5935ec")],
    minutes=0.2, outcome_key=O_FPS, drift_key=D_SETTING,
    success_match=[r"800 ?mhz|processor state|throttl"])

# ---------------------------------------------------------------- Windows
fix("fix.os.fast_startup_off",
    "Turn off Fast Startup",
    "With Fast Startup on, 'Shut down' doesn't fully restart Windows, so Vanguard can't load fresh and old boot files stay around. We'll switch Fast Startup off so every shutdown is a clean start.",
    "Press Undo and Fast Startup is switched back on.",
    notice="Your PC may take a few seconds longer to start.",
    reboot="recommended",
    applies=["fast-startup-stale-dat-bsod", "vgk-not-loaded-needs-reboot", "usb-selective-suspend-freeze", "vanguard-install-loop"],
    steps=[step("registry.setValue", path="HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power",
                name="HiberbootEnabled", kind="DWord", data="0")],
    verifies=[verify("registry.setValue", {"data": "0"}, path="HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power",
                     name="HiberbootEnabled")],
    minutes=0.2, outcome_key=O_ERROR, drift_key=D_SETTING,
    success_match=[r"fast startup"])

fix("fix.os.time_resync",
    "Fix your PC clock",
    "Your PC clock is off. A wrong clock breaks secure connections, so the Riot Client can't log in or Vanguard can't connect. We'll sync the clock with Microsoft's time server.",
    "Undo doesn't set the clock back to the wrong time (that would break things again).",
    rev="bestEffort", minutes=0.2,
    applies=["wrong-system-clock-offline"],
    steps=[step("time.resync")],
    verifies=[verify("time.resync", {"skewSeconds": "0"})],
    outcome_key=O_CONNECT,
    success_match=[r"time|clock"])

# ---------------------------------------------------------------- Vanguard
fix("fix.vg.quarantine_stale_dat",
    "Move an old Vanguard boot file out of the way",
    "An old Vanguard boot file (392667600.dat) was left behind, which can cause blue screens or 'restart required' loops. We'll move it to VTDiag's safe quarantine folder (not delete it). Then you restart.",
    "Press Undo and the file is put back where it was.",
    reboot="required", minutes=1,
    notice="You'll need to restart your PC (a real restart, not Shut down).",
    applies=["fast-startup-stale-dat-bsod", "vanguard-corrupt-install"],
    bind={"file": ("file", "vg.stale_dat", "all")},
    steps=[step("file.quarantine", path="$file.id")],
    verifies=[verify("file.quarantine", {"exists": "false"}, path="$file.id")],
    outcome_key=O_ERROR,
    success_match=[r"392667600"])

fix("fix.vg.quarantine_bootstatus",
    "Reset Vanguard's boot status file",
    "Vanguard keeps a small file (vgkbootstatus.dat) that remembers whether it started correctly. If that file is stuck, Vanguard keeps asking for a restart. We'll move it to VTDiag's quarantine folder. Then you restart.",
    "Press Undo and the file is put back.",
    reboot="required", minutes=1,
    notice="You'll need to restart your PC (a real restart, not Shut down).",
    applies=["vgk-not-loaded-needs-reboot", "vanguard-install-loop", "vanguard-corrupt-install"],
    bind={"file": ("file", "vg.bootstatus_dat", "all")},
    steps=[step("file.quarantine", path="$file.id")],
    verifies=[verify("file.quarantine", {"exists": "false"}, path="$file.id")],
    outcome_key=O_ERROR,
    success_match=[r"vgkbootstatus"])

fix("fix.vg.vgc_automatic",
    "Make Vanguard start with Windows",
    "Vanguard's service (vgc) is set to start only when asked, and it isn't starting properly on your PC. Riot's own fix is to set it to start automatically. We only offer this because you told us Vanguard Pre-Check is off.",
    "Press Undo and the service goes back to its old start setting.",
    reboot="recommended", minutes=0.5,
    notice="Restart your PC afterwards so Vanguard starts fresh.",
    applies=["vgc-service-not-running", "van185-vanguard-disconnects", "vanguard-corrupt-install", "launch-shortcut-tray-failure"],
    bind={"svc": ("service", "vg.vgc_automatic_needed", "one")},
    steps=[step("service.setStartType", name="$svc.id", startType="Automatic"),
           step("service.start", "continue", name="$svc.id", timeoutSec="30")],
    verifies=[verify("service.setStartType", {"startType": "Automatic"}, name="$svc.id")],
    outcome_key=O_ERROR,
    success_match=[r"vgc.*automatic|automatic.*vgc"])

# ---------------------------------------------------------------- Riot programs
fix("fix.riot.client_runasadmin",
    "Always run the Riot Client as administrator",
    "Many players fixed freezes and black screens after updates by running the Riot Client (RiotClientServices.exe) as administrator. We'll set that option on the Riot Client only, not on the game.",
    "Press Undo and the option is removed.",
    notice="Windows will ask for permission (UAC) each time the Riot Client starts.",
    applies=["riot-client-not-elevated"],
    bind={"exe": ("file", "riot.client_not_elevated", "one")},
    steps=[step("appcompat.setLayers", exePath="$exe.id", hive="HKCU", layers="~ RUNASADMIN")],
    verifies=[verify("appcompat.setLayers", {"exists": "true"}, exePath="$exe.id", hive="HKCU", layers="~ RUNASADMIN")],
    minutes=0.3, outcome_kind="relaunch", outcome_key=O_LAUNCH,
    success_match=[r"(run|riot ?client).*admin"])

fix("fix.display.disable_fso",
    "Turn off fullscreen optimizations for VALORANT",
    "Windows 'fullscreen optimizations' change how the game is shown on screen. For some players this causes stutter or alt-tab freezes. We'll switch them off for VALORANT only.",
    "Press Undo and the setting is removed.",
    applies=["fullscreen-optimizations-mpo", "van185-vanguard-disconnects", "fps-decay-over-time"],
    bind={"exe": ("file", "riot.valorant_exe", "one")},
    steps=[step("appcompat.setLayers", exePath="$exe.id", hive="HKCU", layers="~ DISABLEDXMAXIMIZEDWINDOWEDMODE")],
    verifies=[verify("appcompat.setLayers", {"exists": "true"}, exePath="$exe.id", hive="HKCU", layers="~ DISABLEDXMAXIMIZEDWINDOWEDMODE")],
    minutes=0.2, outcome_key=O_STUTTER,
    success_match=[r"fullscreen optimi"])

fix("fix.display.gpu_pref_high",
    "Run VALORANT on your graphics card",
    "Your PC has two graphics chips, and Windows may run VALORANT on the weaker built-in one. We'll tell Windows to always use the strong graphics card for VALORANT.",
    "Press Undo and Windows chooses by itself again.",
    applies=["low-gpu-usage-after-ue5-patch", "wrong-gpu-or-display-output"],
    bind={"exe": ("file", "gpu.pref_not_high", "one")},
    steps=[step("registry.setValue", path="HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences", name="$exe.id",
                kind="String", data="GpuPreference=2;")],
    verifies=[verify("registry.setValue", {"data": "GpuPreference=2;"}, path="HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences", name="$exe.id")],
    minutes=0.2, outcome_key=O_FPS,
    success_match=[r"high.performance gpu|preferred gpu|rtx gpu not igpu|gpu preference"])

fix("fix.display.hags_off",
    "Turn off hardware-accelerated GPU scheduling",
    "Hardware-accelerated GPU scheduling (HAGS) is a Windows graphics option. With some drivers it causes stutter, input delay or freezes in VALORANT. We'll switch it off. It takes effect after a restart.",
    "Press Undo and restart to switch it back on.",
    reboot="required", minutes=0.5,
    applies=["mouse-acceleration-floaty-aim", "frametime-latency-spikes", "amd-adrenalin-driver-regression", "whole-pc-freeze-kernel-power-41", "conflict-hags"],
    steps=[step("registry.setValue", path="HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", name="HwSchMode", kind="DWord", data="1")],
    verifies=[verify("registry.setValue", {"data": "1"}, path="HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", name="HwSchMode")],
    outcome_key=O_STUTTER, drift_key=D_SETTING,
    success_match=[r"hags|hardware.accelerated gpu"])

fix("fix.display.mpo_off",
    "Turn off Multi-Plane Overlay (MPO)",
    "Multi-Plane Overlay is a Windows display feature that some graphics drivers handle badly, causing flicker, alt-tab freezes or micro-stutter. We'll switch it off with the same setting NVIDIA recommends.",
    "Press Undo and restart to switch it back on.",
    reboot="recommended", minutes=0.3,
    applies=["fullscreen-optimizations-mpo", "windows-update-kb5101650-microstutter", "amd-adrenalin-driver-regression", "conflict-mpo"],
    steps=[step("registry.setValue", path="HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm", name="OverlayTestMode", kind="DWord", data="5")],
    verifies=[verify("registry.setValue", {"data": "5"}, path="HKLM\\SOFTWARE\\Microsoft\\Windows\\Dwm", name="OverlayTestMode")],
    outcome_key=O_STUTTER, drift_key=D_SETTING,
    success_match=[r"\bmpo\b|multi.plane"])

fix("fix.display.game_dvr_off",
    "Turn off Xbox Game Bar background recording",
    "Game Bar can record your gameplay in the background all the time. That recording costs FPS and can cause stutter. We'll switch off background recording (Game Bar itself stays).",
    "Press Undo and background recording comes back.",
    admin=False, minutes=0.2,
    applies=["third-party-hook-overlay-conflict", "conflict-xbox_game_bar_dvr"],
    steps=[step("registry.setValue", path="HKCU\\System\\GameConfigStore", name="GameDVR_Enabled", kind="DWord", data="0"),
           step("registry.setValue", path="HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR", name="AppCaptureEnabled", kind="DWord", data="0")],
    verifies=[verify("registry.setValue", {"data": "0"}, path="HKCU\\System\\GameConfigStore", name="GameDVR_Enabled")],
    outcome_key=O_FPS, drift_key=D_SETTING,
    success_match=[r"game ?bar|game ?dvr|background recording"])

fix("fix.input.mouse_accel_off",
    "Turn off mouse acceleration",
    "'Enhance pointer precision' makes the cursor move further when you move the mouse fast. In a shooter that makes aim feel floaty and inconsistent. We'll switch it off.",
    "Press Undo and the old mouse setting comes back.",
    notice="Sign out and back in (or restart) for the change to apply everywhere.",
    admin=False, reboot="recommended", minutes=0.2,
    applies=["mouse-acceleration-floaty-aim", "mouse-usb-port-polling-lag"],
    steps=[step("registry.setValue", path="HKCU\\Control Panel\\Mouse", name="MouseSpeed", kind="String", data="0"),
           step("registry.setValue", path="HKCU\\Control Panel\\Mouse", name="MouseThreshold1", kind="String", data="0"),
           step("registry.setValue", path="HKCU\\Control Panel\\Mouse", name="MouseThreshold2", kind="String", data="0")],
    verifies=[verify("registry.setValue", {"data": "0"}, path="HKCU\\Control Panel\\Mouse", name="MouseSpeed")],
    outcome_key=O_MOUSE,
    success_match=[r"enhance pointer precision|acceleration"])

# ---------------------------------------------------------------- security settings that block Vanguard
fix("fix.sec.reset_exploit_overrides",
    "Remove Exploit Protection overrides from Riot programs",
    "Someone (or a tweak tool) turned off Windows Exploit Protection settings for Riot or Vanguard programs. Vanguard needs those protections and shows VAN 9002 without them. We'll remove the overrides so Windows defaults apply again.",
    "Press Undo and the old overrides come back.",
    minutes=0.3,
    applies=["conflict-exploit_protection_override", "launch-fails-something-unusual", "hvci-memory-integrity-required"],
    bind={"img": ("process", "sec.exploit_override_riot", "all")},
    steps=[step("ifeo.resetMitigations", imageName="$img.id")],
    verifies=[verify("ifeo.resetMitigations", {"mitigationOptions": ""}, imageName="$img.id")],
    outcome_key=O_ERROR,
    success_match=[r"exploit protection|mitigation"])

fix("fix.sec.remove_riot_perfoptions",
    "Remove forced CPU priority from Riot programs",
    "VALORANT or Vanguard is forced to run at 'High' or 'Realtime' priority. That starves Vanguard and Windows and can cause VAN 136 / VAN 185 kicks. We'll remove the forced priority so Windows manages it normally.",
    "Press Undo and the forced priority comes back.",
    minutes=0.3,
    applies=["high-process-priority-van136-185", "intel-hybrid-cpu-ecore-stutter", "conflict-process_lasso"],
    bind={"img": ("process", "sec.ifeo_perfoptions_riot", "all")},
    steps=[step("ifeo.removePerfOptions", imageName="$img.id")],
    verifies=[verify("ifeo.removePerfOptions", {"cpuPriorityClass": ""}, imageName="$img.id")],
    outcome_key=O_CONNECT,
    success_match=[r"priority"])
