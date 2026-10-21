"""Build research/conflicts.json (environment conflict registry).

Usage: python -I build_conflicts.py <threads.jsonl> <out conflicts.json>

Each entry's Reddit evidence is computed here from ALL threads (bot / copy-paste template
comments removed: any comment whose 80-char prefix repeats >= 15 times). `reddit_query` is the
regex used, so counts are reproducible. Thread text is treated as data only.

detect{} base fields: process_names, service_names, driver_names, uninstall_displayname_regex,
network_adapter_regex, registry_keys. Optional extension fields (only where needed):
module_names (DLLs seen as faulting/hooked modules), file_paths, windows_features,
audio_endpoint_regex. Identifiers are only those observed in thread data, Riot/vendor docs,
or well-established product facts; unknown -> left empty.
"""
import json, re, sys
from collections import Counter

SRC, OUT = sys.argv[1], sys.argv[2]

# ---------------------------------------------------------------- web sources
W = {
    "riot_oem": "https://support.riotgames.com/en-us/riot/performance/van-incompatible-oem-driver",
    "riot_incompat": "https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software",
    "riot_devrel": "https://www.riotgames.com/en/DevRel/vanguard",
    "riot_van138": "https://support.riotgames.com/en-us/riot/performance/error-van-138",
    "riot_van185": "https://support.riotgames.com/en-us/riot/performance/error-van-185",
    "spike_incompat": "https://www.thespike.gg/valorant/error-codes/fixing-the-valorant-incompatible-software-component-detected-error",
    "spike_iommu": "https://www.thespike.gg/valorant/error-codes/fixing-the-iommu-valorant-error-how-it-impacts-security-and-performance",
    "spike_van9100": "https://www.thespike.gg/valorant/valorant-van-9100-error-causes-and-solutions-of-the-error-code",
    "esports_incompat": "https://www.esports.net/wiki/guides/valorant-incompatible-software-error-fix/",
    "toms_ondemand": "https://www.tomshardware.com/video-games/pc-gaming/riot-vanguard-adds-an-on-demand-mode-that-stops-anti-cheat-loading-at-boot-on-secured-windows-11-pcs",
    "neowin_winring0": "https://www.neowin.net/news/windows-1110-is-flagging-winring0-on-your-pc-monitoring-fan-control-apps-heres-why/",
    "evga_winring0": "https://forums.evga.com/EVGA-Update-all-of-your-programs-because-they-have-vulnerability-FIXED-with-PX1-v-107-m3050791.aspx",
    "thegamer_vanguard": "https://www.thegamer.com/vanguard-anti-cheat-pc-problems-for-valorant-players/",
    "freefixer_se64a": "https://www.freefixer.com/library/file/se64a.sys-85801",
    "freefixer_mdns": "https://www.freefixer.com/library/file/mdnsNSP.dll-29474/",
    "keengamer_loading": "https://www.keengamer.com/articles/features/troubleshooting/7-ways-to-fix-the-valorant-stuck-on-loading-screen-bug/",
    "gridinsoft_nahimic": "https://blog.gridinsoft.com/nahimicsvc64-exe/",
    "wondershare_blackscreen": "https://repairit.wondershare.com/video-repair/fix-valorant-black-screen-at-launch.html",
    "winreport_van84": "https://windowsreport.com/valorant-error-code-84/",
    "elgato": "https://help.elgato.com/hc/en-us/articles/9482241809805-Issues-with-Valorant-and-Elgato-Capture-Devices",
    "nvidia_mpo": "https://nvidia.custhelp.com/app/answers/detail/a_id/5157",
}

def D(proc=(), svc=(), drv=(), uninst=None, adapter=None, reg=(), **extra):
    d = {"process_names": list(proc), "service_names": list(svc), "driver_names": list(drv),
         "uninstall_displayname_regex": uninst, "network_adapter_regex": adapter, "registry_keys": list(reg)}
    for k, v in extra.items():
        d[k] = list(v) if isinstance(v, (list, tuple)) else v
    return d

def F(action, reversible=True, risk="low"):
    return {"action": action, "reversible": reversible, "risk": risk}

E = []
def add(id, name, kind, q, detect, effects, fix, web=(), conf="medium", note=None):
    e = {"id": id, "name": name, "kind": kind, "detect": detect, "effects": effects, "fix": fix,
         "_q": q, "_web": [W[w] if w in W else w for w in web], "confidence": conf}
    if note:
        e["notes"] = note
    E.append(e)

RIOT_EXCL = r"Add C:\Riot Games and C:\Program Files\Riot Vanguard (vgc.exe, vgk.sys) plus RiotClientServices.exe / VALORANT-Win64-Shipping.exe as exclusions; if still failing, uninstall with the vendor's removal tool and reboot (Defender takes over)."

# ================================================================ ANTIVIRUS / SECURITY
add("av_avast", "Avast Antivirus / Avast One", "antivirus", r"\bavast\b",
    D(proc=["AvastSvc.exe", "AvastUI.exe"], uninst=r"^Avast (Free Antivirus|Premium Security|One|Antivirus)"),
    ["Quarantines/blocks Vanguard or Riot Client files", "VAN 185 / VAN 72 disconnects (reported)", "Launch failure after update"],
    F(RIOT_EXCL, True, "medium"), ["riot_incompat"], "medium")
add("av_norton", "Norton 360 / Norton Security", "antivirus", r"\bnorton\b",
    D(proc=["NortonSvc.exe", "NortonUI.exe", "NortonSecurity.exe"], uninst=r"^Norton (360|Security|AntiVirus|Internet Security)"),
    ["Smart Firewall blocks Riot Client / Vanguard traffic (VAN 185, VAL 5)", "Files quarantined"], F(RIOT_EXCL + " Use Norton Remove and Reinstall tool for full removal.", True, "medium"), [], "medium")
add("av_mcafee", "McAfee (Total Protection / LiveSafe, often OEM-preloaded)", "antivirus", r"\bmc ?afee\b",
    D(proc=["mfemms.exe"], uninst=r"^McAfee"),
    ["Firewall blocks Riot Client (VAL 57/59 reported)", "Preloaded trial expires but filter drivers stay"], F(RIOT_EXCL + " Remove fully with McAfee Consumer Product Removal (MCPR) tool.", True, "medium"), [], "medium")
add("av_kaspersky", "Kaspersky", "antivirus", r"\bkaspersky\b",
    D(proc=["avp.exe", "avpui.exe"], uninst=r"^Kaspersky"),
    ["Blocks Vanguard driver / network (VAN 185, VAN 72)"], F(RIOT_EXCL, True, "medium"), [], "low")
add("av_bitdefender", "Bitdefender", "antivirus", r"\bbit ?defender\b",
    D(proc=["bdagent.exe", "vsserv.exe", "bdservicehost.exe"], uninst=r"^Bitdefender"),
    ["Advanced Threat Defense quarantines Vanguard/Valorant", "VAN 185 / VAN 72"], F(RIOT_EXCL, True, "medium"), [], "low")
add("av_malwarebytes", "Malwarebytes", "antivirus", r"\bmalware ?bytes\b|\bmbam\b",
    D(proc=["MBAMService.exe", "Malwarebytes.exe"], svc=["MBAMService"], uninst=r"^Malwarebytes"),
    ["Exploit protection / ransomware module blocks Valorant (VAN 57 reported)"], F("Add Riot folders to Malwarebytes Allow List or disable real-time Exploit Protection for VALORANT.", True, "low"), [], "low")
add("av_trend_micro", "Trend Micro (Maximum Security / OEM)", "antivirus", r"trend ?micro",
    D(proc=["PccNTMon.exe", "coreServiceShell.exe"], uninst=r"^Trend Micro"),
    ["Blocks Vanguard/Riot files (VAL 5, VAN 102)"], F(RIOT_EXCL, True, "medium"), [], "low")
add("av_360", "360 Total Security", "antivirus", r"360 ?(?:total ?security|safe)|qihoo",
    D(proc=["QHActiveDefense.exe", "QHSafeTray.exe"], uninst=r"^360 Total Security"),
    ["Leftover network drivers break Riot Client (VAL 43)", "Game booster kills services"], F("Uninstall 360 Total Security completely and reboot; check leftover filter drivers.", True, "medium"), [], "low")
add("av_other_suites", "Other 3rd-party AV suites (AVG, ESET, Webroot, Comodo, Avira, Sophos, G DATA, F-Secure, Panda, Quick Heal, K7, TotalAV)", "antivirus",
    r"avg (?:anti ?virus|free|internet security|ultimate|tuneup)|eset|nod32|webroot|comodo|avira|sophos|g ?data|f-?secure|panda (?:dome|antivirus|security)|quick ?heal|k7 (?:antivirus|security|total)|total ?av",
    D(proc=["AVGSvc.exe", "AVGUI.exe", "ekrn.exe", "egui.exe", "WRSA.exe", "cmdagent.exe"], svc=["ekrn"],
      uninst=r"^(AVG (AntiVirus|Internet Security|Ultimate)|ESET|Webroot|COMODO|Avira|Sophos|G DATA|F-Secure|Panda (Dome|Free Antivirus)|Quick Heal|K7 |TotalAV)"),
    ["Blocks Vanguard files / network (HIPS, firewall, auto-sandbox)"], F(RIOT_EXCL, True, "medium"), [], "low")
add("defender_firewall_block", "Windows Defender Firewall / 3rd-party firewall rules blocking Riot", "antivirus",
    r"\bfirewall\b", D(svc=["MpsSvc", "BFE"]),
    ["VAN 185, VAL 29, VAL 43, VAN 84 (Riot Client / vgc cannot reach servers)"],
    F("Ensure MpsSvc/BFE running; add allow rules for RiotClientServices.exe, VALORANT-Win64-Shipping.exe, vgc.exe; remove stale block rules.", True, "low"),
    ["riot_van185"], "medium")
add("defender_cfa", "Defender Controlled Folder Access", "antivirus", r"controlled folder access|ransomware protection",
    D(reg=[r"HKLM\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access\EnableControlledFolderAccess",
           r"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access\EnableControlledFolderAccess"]),
    ["Riot Client/VALORANT cannot write config or install/update files (stuck update, settings not saved)"],
    F("Add RiotClientServices.exe, VALORANT-Win64-Shipping.exe and the Riot installer to 'Allow an app through Controlled folder access' (keep CFA on).", True, "low"), [], "low")
add("defender_smart_app_control", "Windows 11 Smart App Control", "antivirus", r"smart app control",
    D(reg=[r"HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState"]),
    ["Blocks unsigned/low-reputation helper DLLs or installers (launch/installer failures)"],
    F("Report only; turning SAC off cannot be undone without resetting Windows, so ask the user explicitly.", False, "medium"), [], "low",
    "VerifiedAndReputablePolicyState: 0=off, 1=on, 2=evaluation.")
add("exploit_protection_override", "Exploit protection overrides (CFG/DEP/ASLR) on Riot executables or system-wide", "antivirus",
    r"exploit protection|control flow guard|data execution prevention",
    D(reg=[r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\vgc.exe\MitigationOptions",
           r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT-Win64-Shipping.exe\MitigationOptions",
           r"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\MitigationOptions"]),
    ["VAN 185 / VAN 79 / launch failures when CFG/ASLR forced off or on by tweaks (community-reported)"],
    F("Back up with Get-ProcessMitigation -RegistryConfigFilePath; remove per-program overrides for Riot exes and reset system defaults.", True, "low"), [], "low")
add("dcontrol_defender_disabled", "Defender Control (dControl) / Defender force-disabled by policy", "tweak_tool", r"dcontrol|defender control",
    D(proc=["dControl.exe"], reg=[r"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\DisableAntiSpyware"]),
    ["Community claims of Vanguard flags/bans (unverified)", "Breaks HVCI/Memory Integrity requirement"],
    F("Re-enable Defender via the tool / remove policy; leave AV on.", True, "low"), [], "low")
add("hvci_off_when_required", "Memory Integrity (HVCI) off or blocked by incompatible drivers when Vanguard requires it", "other",
    r"memory integrity|core isolation|\bhvci\b",
    D(reg=[r"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled",
           r"HKLM\SYSTEM\CurrentControlSet\Control\CI\Config\VulnerableDriverBlocklistEnable"]),
    ["VAN RESTRICTION (HVCI/IOMMU required)", "VAN 185/57 when HVCI toggled", "Memory Integrity toggle reverts after reboot / 'incompatible drivers' list"],
    F("List incompatible drivers (Windows Security > Core isolation), remove/update their owning software, then enable Memory Integrity.", True, "medium"),
    ["toms_ondemand", "spike_iommu"], "high")

# ================================================================ VPN / VIRTUAL NETWORK
add("vpn_generic", "Any VPN / tunnel adapter (TAP, Wintun, WireGuard, TUN)", "vpn", r"\bvpns?\b",
    D(adapter=r"TAP-Windows|TAP-Win32|Wintun|WireGuard|\bTUN\b|VPN"),
    ["VAN 185, VAL 43, VAL 62, VAN 84 (Riot: 'Using a proxy or VPN can result in various errors on Vanguard')", "High ping / wrong region"],
    F("Fully exit VPN client (not just disconnect); disable leftover virtual adapters; uninstall if unused.", True, "low"),
    ["riot_incompat", "winreport_van84"], "high")
add("vpn_nordvpn", "NordVPN (NordLynx)", "vpn", r"\bnord ?vpn\b|\bnordlynx\b",
    D(proc=["NordVPN.exe"], svc=["nordvpn-service"], uninst=r"^NordVPN", adapter=r"NordLynx|TAP-NordVPN"),
    ["VAN errors / disconnects while tunnel or adapter active"], F("Quit NordVPN fully, disable NordLynx adapter or uninstall.", True, "low"), ["riot_incompat"], "medium")
add("vpn_expressvpn", "ExpressVPN", "vpn", r"\bexpress ?vpn\b",
    D(proc=["ExpressVPN.exe"], svc=["ExpressVpnService"], uninst=r"^ExpressVPN", adapter=r"ExpressVPN"),
    ["VAL 59 / connection errors"], F("Quit ExpressVPN and its service while playing.", True, "low"), ["riot_incompat"], "low")
add("vpn_protonvpn", "Proton VPN", "vpn", r"\bproton ?vpn\b",
    D(proc=["ProtonVPN.exe"], uninst=r"^Proton ?VPN", adapter=r"ProtonVPN"),
    ["VAN 185 while connected"], F("Disconnect and quit Proton VPN; disable its adapter.", True, "low"), ["riot_incompat"], "low")
add("vpn_surfshark", "Surfshark", "vpn", r"\bsurfshark\b", D(proc=["Surfshark.exe"], uninst=r"^Surfshark", adapter=r"Surfshark"),
    ["Connection errors while active"], F("Quit Surfshark fully.", True, "low"), ["riot_incompat"], "low")
add("vpn_cloudflare_warp", "Cloudflare WARP (1.1.1.1 app)", "vpn", r"cloudflare warp|\bwarp\b (?:vpn|app|client)|1\.1\.1\.1 (?:app|vpn|warp)|cloudfare warp",
    D(proc=["Cloudflare WARP.exe", "warp-svc.exe"], svc=["CloudflareWARP"], uninst=r"^Cloudflare WARP", adapter=r"Cloudflare WARP"),
    ["Used as a ping/routing fix but can trigger VAN 185 / VAL 29 / VAL 62"], F("Disable WARP while playing; keep only if it measurably fixes routing.", True, "low"), ["riot_incompat"], "medium")
add("vpn_openvpn_tap", "OpenVPN / TAP-Windows adapter", "vpn", r"\bopenvpn\b|\btap[- ]?windows\b|\btap adapter\b",
    D(svc=["OpenVPNService"], drv=["tap0901.sys"], uninst=r"^(OpenVPN|TAP-Windows)", adapter=r"TAP-Windows Adapter|TAP-Win32|OpenVPN"),
    ["Leftover TAP adapter causes VAN 84 / VAN 185"], F("Disable or uninstall unused TAP adapters in Device Manager.", True, "low"), ["winreport_van84"], "medium")
add("vpn_wireguard", "WireGuard", "vpn", r"\bwireguard\b",
    D(proc=["wireguard.exe"], drv=["wireguard.sys"], uninst=r"^WireGuard", adapter=r"WireGuard"),
    ["Tunnel adapter routes Riot traffic (disconnects)"], F("Deactivate tunnels while playing.", True, "low"), [], "low")
add("vpn_radmin", "Radmin VPN (LAN emulation)", "vpn", r"\bradmin\b",
    D(svc=["RvControlSvc"], uninst=r"^Radmin VPN", adapter=r"Radmin VPN"),
    ["Virtual LAN adapter conflicts (VAN 293 / connection errors)"], F("Disable Radmin VPN adapter or uninstall when not needed.", True, "low"), ["winreport_van84"], "medium")
add("vpn_hamachi", "LogMeIn Hamachi", "vpn", r"\bhamachi\b",
    D(proc=["hamachi-2.exe", "hamachi-2-ui.exe"], svc=["Hamachi2Svc"], uninst=r"Hamachi", adapter=r"Hamachi"),
    ["Virtual adapter takes route priority (disconnects)"], F("Stop Hamachi2Svc and disable its adapter.", True, "low"), ["winreport_van84"], "medium")
add("vpn_zerotier", "ZeroTier One", "vpn", r"\bzero ?tier\b",
    D(svc=["ZeroTierOneService"], uninst=r"^ZeroTier", adapter=r"ZeroTier Virtual Port"),
    ["Virtual adapter conflicts"], F("Stop ZeroTierOneService / disable adapter.", True, "low"), ["winreport_van84"], "medium")
add("vpn_tailscale", "Tailscale", "vpn", r"\btailscale\b",
    D(proc=["tailscaled.exe", "tailscale-ipn.exe"], svc=["Tailscale"], uninst=r"^Tailscale", adapter=r"Tailscale"),
    ["Exit-node / adapter routing conflicts"], F("Disconnect Tailscale or disable exit node.", True, "low"), [], "low")
add("vpn_mullvad_pia_windscribe", "Mullvad / Private Internet Access / Windscribe / CyberGhost / Hotspot Shield / free VPNs (Urban, Hola, Psiphon)", "vpn",
    r"\bmullvad\b|private internet access|\bpia vpn\b|\bwindscribe\b|cyber ?ghost|hotspot shield|urban ?vpn|hola vpn|\bpsiphon\b|tunnel ?bear",
    D(proc=["mullvad-daemon.exe", "Windscribe.exe", "psiphon3.exe"], svc=["PrivateInternetAccessService", "WindscribeService"],
      uninst=r"^(Mullvad VPN|Private Internet Access|Windscribe|CyberGhost|Hotspot Shield|Urban VPN|Hola|Psiphon|TunnelBear)"),
    ["VAN 185 / VAL errors while tunnel active", "Free VPN browser proxies leave system proxy set"], F("Quit client fully; uninstall if unused.", True, "low"), ["riot_incompat"], "low")
add("vpn_corporate", "Corporate VPN / endpoint agents (GlobalProtect, Cisco AnyConnect/Secure Client, FortiClient)", "vpn",
    r"global ?protect|anyconnect|cisco secure client|forti ?client",
    D(proc=["PanGPS.exe", "PanGPA.exe", "vpnagent.exe", "FortiClient.exe"], svc=["PanGPS", "vpnagent", "csc_vpnagent"],
      uninst=r"^(GlobalProtect|Cisco AnyConnect|Cisco Secure Client|FortiClient)", adapter=r"PANGP|AnyConnect|Fortinet"),
    ["VAN 289 / connection errors (network filter drivers)"], F("Disconnect and stop agent; work-managed PCs may not allow removal.", True, "low"), [], "medium")
add("dpi_bypass_windivert", "DPI-bypass / packet-diversion tools (GoodbyeDPI, zapret, WinDivert-based)", "vpn",
    r"goodbye ?dpi|\bzapret\b|windivert",
    D(proc=["goodbyedpi.exe", "winws.exe"], svc=["GoodbyeDPI", "WinDivert"], drv=["WinDivert64.sys", "WinDivert.sys"]),
    ["Packet-modifying kernel driver in the network path: possible Vanguard network errors / disconnects"],
    F("Stop/remove the GoodbyeDPI/zapret service and WinDivert driver service before playing.", True, "low"), [], "low",
    "No thread evidence in this dataset; included because the tools are common in TR/RU and load a kernel packet driver.")
add("system_proxy", "System / WinHTTP proxy set", "vpn", r"\bproxy\b|\bproxifier\b",
    D(proc=["Proxifier.exe"], reg=[r"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ProxyEnable",
                                   r"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ProxyServer"]),
    ["VAL 29 / Riot Client cannot connect (Riot lists proxy as cause)"], F("Back up and clear system proxy (and netsh winhttp reset proxy).", True, "low"), ["riot_incompat"], "medium")

# ================================================================ OTHER ANTI-CHEATS
add("ac_faceit", "FACEIT Anti-Cheat", "anticheat", r"\bfaceit\b",
    D(svc=["FACEITService"], drv=["FACEIT.sys"], uninst=r"^FACEIT"),
    ["VAL 29 after installing FACEIT", "Requires SVM/VBS settings that change Valorant performance", "Conflict with Vanguard (kernel AC)"],
    F("Uninstall FACEIT AC (or stop its service and reboot) when playing VALORANT.", True, "low"), [], "medium")
add("ac_esea", "ESEA client anti-cheat", "anticheat", r"\besea\b", D(svc=["ESEADriver2"], drv=["ESEADriver2.sys"], uninst=r"^ESEA"),
    ["Second kernel AC: launch errors"], F("Uninstall ESEA client when not used.", True, "low"), [], "low")
add("ac_battleye", "BattlEye", "anticheat", r"battle ?eye|bedaisy",
    D(svc=["BEService"], drv=["BEDaisy.sys"]), ["Kernel driver present alongside Vanguard (rare conflicts)"], F("Usually coexists; only stop BEService if a conflict is observed.", True, "low"), [], "low")
add("ac_easyanticheat", "Easy Anti-Cheat", "anticheat", r"easy ?anti ?cheat|\beac\b",
    D(svc=["EasyAntiCheat", "EasyAntiCheat_EOS"], drv=["EasyAntiCheat.sys", "EasyAntiCheat_EOS.sys"]),
    ["Usually coexists; shared stutter pattern with Realtek NIC issues"], F("No action unless running simultaneously.", True, "low"), [], "low")
add("ac_xigncode", "XIGNCODE3 (xhunter1.sys)", "anticheat", r"xhunter|xigncode",
    D(drv=["xhunter1.sys"]), ["Listed as incompatible driver blocking Memory Integrity (HVCI)"],
    F("Uninstall the game using XIGNCODE3 / delete leftover xhunter1 service, then enable HVCI.", True, "medium"), [], "medium")
add("ac_mhyprot", "miHoYo anti-cheat driver (Genshin/HSR/ZZZ)", "anticheat", r"mhyprot|genshin|honkai|zenless",
    D(drv=["mhyprot2.sys", "mhyprot3.sys"]), ["Known-vulnerable driver; can block HVCI / be flagged"], F("Close the miHoYo game fully; uninstall if HVCI cannot be enabled.", True, "low"), [], "low")

# ================================================================ OVERLAYS / HOOKS (Oct 2026 Riot incompatible list: RTSS, Stardock, Premier Opinion, ASUS ScreenXpert, GearUP Booster, UltraViewer)
add("rtss", "RivaTuner Statistics Server (RTSS)", "overlay", r"\brtss\b|riva ?tuner",
    D(proc=["RTSS.exe", "RTSSHooksLoader64.exe"], uninst=r"RivaTuner Statistics Server", module_names=["RTSSHooks64.dll"]),
    ["Oct 2026 update: stuck on loading screen, huge FPS drops/stutter (Riot dev incompatible list)", "Frame-limiter/OSD stops working in VALORANT"],
    F("Exit RTSS (and Afterburner OSD) before launching; or add VALORANT to RTSS exclusions (Application detection level: None). Uninstall if it persists.", True, "low"),
    ["thegamer_vanguard"], "high")
add("msi_afterburner", "MSI Afterburner (RTCore64.sys)", "monitoring", r"after ?burner",
    D(proc=["MSIAfterburner.exe"], drv=["RTCore64.sys"], uninst=r"^MSI Afterburner"),
    ["Bundles RTSS hooks (see rtss)", "RTCore64.sys flagged as incompatible software component", "Monitoring polling causes micro-stutter"],
    F("Close Afterburner + RTSS while playing; update to latest (old RTCore64 versions are vulnerable).", True, "low"),
    ["spike_incompat", "thegamer_vanguard"], "high")
add("stardock", "Stardock (Fences, Start11, WindowBlinds, Groupy, etc.)", "overlay", r"stardock|\bfences\b|start11|windowblinds",
    D(uninst=r"^(Stardock|Fences|Start11|WindowBlinds|Groupy|ObjectDock|DeskScapes|Multiplicity|CursorFX)"),
    ["Oct 2026: choppy game / loading-screen hang (Riot dev incompatible list)"], F("Exit or uninstall Stardock apps that hook explorer/DWM.", True, "low"), [], "high")
add("premier_opinion", "Premier Opinion (market-research panel, pmls64.dll)", "other", r"premier ?opinion|premieronion|pmls64",
    D(uninst=r"Premier Opinion", module_names=["pmls64.dll"]),
    ["Oct 2026: stutter / FPS drops / loading hang; pmls64.dll as faulting module (Riot dev incompatible list)"], F("Uninstall Premier Opinion (often bundled adware).", True, "low"), [], "high")
add("nielsen_meter", "Nielsen digital meter (n64hooks.dll)", "other", r"n64hooks|\bnielsen\b",
    D(uninst=r"Nielsen", module_names=["n64hooks.dll"]),
    ["Injected DLL shown in Event Viewer after Oct 2026 update; stutter"], F("Uninstall Nielsen panel software.", True, "low"), [], "low")
add("asus_screenxpert", "ASUS ScreenXpert (AsusWallpaperSettingHook.dll)", "rgb_vendor", r"screen ?xpert|asuswallpapersettinghook",
    D(uninst=r"ScreenXpert", module_names=["AsusWallpaperSettingHook.dll"]),
    ["Oct 2026: stutter / loading-screen hang (Riot dev incompatible list)"], F("Uninstall ScreenXpert (ASUS dual-screen laptops) or its wallpaper component.", True, "low"), [], "high")
add("gearup_booster", "GearUP Booster", "network_optimizer", r"gear ?up(?: booster)?\b|gearupbooster",
    D(uninst=r"GearUP", module_names=["udp_connect_lsp64.dll"], file_paths=[r"C:\Windows\GearUPBooster"]),
    ["Oct 2026: injected LSP DLL; stutter / loading hang (Riot dev incompatible list)"], F("Uninstall GearUP Booster and verify C:\\Windows\\GearUPBooster LSP DLLs are gone.", True, "low"), [], "high")
add("ultraviewer", "UltraViewer (remote desktop)", "other", r"ultra ?viewer",
    D(uninst=r"UltraViewer"), ["Listed on Riot dev Oct 2026 incompatible software list"], F("Exit or uninstall UltraViewer.", True, "low"), [], "medium")
add("asus_gpu_tweak", "ASUS GPU Tweak III OSD", "monitoring", r"gpu ?tweak|gtiii-osd",
    D(uninst=r"GPU Tweak", module_names=["GTIII-OSD64-VK.dll"]), ["OSD hook DLL unloaded/crashing; stutter after Oct 2026 update"], F("Disable GPU Tweak OSD or exit GPU Tweak.", True, "low"), [], "medium")
add("overwolf_trackers", "Overwolf + Valorant Tracker / Outplayed / Blitz / Mobalytics", "overlay",
    r"\boverwolf\b|valorant tracker|tracker\.gg|\boutplayed\b|\bblitz\b|mobalytics",
    D(proc=["Overwolf.exe", "OverwolfBrowser.exe"], uninst=r"^(Overwolf|Blitz|Mobalytics|Outplayed)"),
    ["Memory leak / laggy menus after UI patches", "Stutter, VAN 152/185 reports", "Crash after game patch until app updates"],
    F("Close all Overwolf processes or uninstall tracker apps.", True, "low"), [], "high")
add("discord_overlay", "Discord in-game overlay / hardware acceleration", "overlay",
    r"discord(?:'s)? (?:in-?game )?overlay|discord hardware acceleration|overlays? (?:like|such as|from|in) discord",
    D(proc=["Discord.exe"]), ["FPS drops / stutter", "Voice/audio conflicts"], F("Disable Discord in-game overlay and hardware acceleration.", True, "low"), [], "medium")
add("nvidia_overlay", "NVIDIA App / GeForce Experience overlay (Instant Replay, filters)", "overlay",
    r"nvidia (?:app|overlay)|geforce experience|\bgfe\b|shadowplay|instant replay",
    D(proc=["NVIDIA Overlay.exe", "NVIDIA Share.exe", "nvcontainer.exe"], svc=["NvContainerLocalSystem"], uninst=r"^(NVIDIA App|NVIDIA GeForce Experience)"),
    ["FPS drops / stutter while recording", "Overlay shows N/A for VALORANT after Oct 2026 update"], F("Turn off in-game overlay / Instant Replay; keep driver.", True, "low"), [], "medium")
add("amd_adrenalin_features", "AMD Software: Adrenalin overlay & features (AFMF, Anti-Lag, RSR, Chill)", "overlay",
    r"adrenalin|radeon software|amd software|\bafmf\b|fluid motion|radeon super resolution",
    D(proc=["RadeonSoftware.exe", "AMDRSServ.exe"], uninst=r"^AMD Software"),
    ["Stutter / FPS regressions on certain driver versions", "Driver timeouts"], F("Disable overlay, AFMF, RSR for VALORANT profile; roll back to a known-good driver with DDU if needed.", True, "medium"), [], "medium")
add("xbox_game_bar_dvr", "Xbox Game Bar / Game DVR background recording", "overlay", r"game ?bar|game dvr|background recording",
    D(proc=["GameBar.exe", "GameBarFTServer.exe"], reg=[r"HKCU\System\GameConfigStore\GameDVR_Enabled", r"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled"]),
    ["Stutter / FPS drops from background capture"], F("Back up keys; disable background recording (GameDVR_Enabled=0, AppCaptureEnabled=0).", True, "low"), [], "medium")
add("medal_clipping", "Medal.tv / SteelSeries Moments / clip recorders", "overlay", r"\bmedal(?:\.tv)?\b|medalencoder|steelseries moments",
    D(proc=["Medal.exe", "MedalEncoder.exe"], uninst=r"^Medal"), ["FPS decay / stutter while recording"], F("Close clip recorder while testing.", True, "low"), [], "medium")
add("obs_streamlabs", "OBS Studio / Streamlabs (game capture)", "overlay", r"\bobs\b|streamlabs",
    D(proc=["obs64.exe", "Streamlabs OBS.exe"]), ["Game-capture hook / encoder load: FPS drops"], F("Use display capture or close while diagnosing.", True, "low"), [], "low")
add("steam_overlay", "Steam overlay", "overlay", r"steam overlay|gameoverlayui", D(proc=["GameOverlayUI.exe"]), ["Hook conflicts (rare)"], F("Close Steam while testing.", True, "low"), [], "low")
add("wallpaper_engine", "Wallpaper Engine / Lively Wallpaper", "overlay", r"wallpaper engine|lively wallpaper",
    D(proc=["wallpaper32.exe", "wallpaper64.exe", "Lively.exe"]), ["Stutter even when paused ('Unpause during Aero Peek')"], F("Quit Wallpaper Engine while playing or disable 'Unpause during Aero Peek'.", True, "low"), [], "medium")
add("omen_gaming_hub", "HP OMEN Gaming Hub (overlay / FPS boost)", "rgb_vendor", r"omen gaming hub|omen hub",
    D(uninst=r"OMEN Gaming Hub"), ["Crashes, VAN 185 with VALORANT in 'FPS boost'"], F("Disable OMEN overlay/boost for VALORANT or uninstall.", True, "low"), [], "low")
add("huawei_pc_manager", "Huawei PC Manager (PerfWndMonModule.dll)", "other", r"huawei pc manager|perfwndmon",
    D(uninst=r"PC Manager", module_names=["PerfWndMonModule.dll"]), ["Faulting module in VALORANT crashes"], F("Uninstall or disable performance monitor feature.", True, "low"), [], "low")
add("elgato_capture", "Elgato capture devices / DirectShow capture utilities", "other", r"\belgato\b|4k capture utility|game capture hd",
    D(uninst=r"^(Elgato|4K Capture Utility|Game Capture)"), ["Vanguard blocks DirectShow capture devices; capture card black/blocked"], F("Use Elgato's recommended workaround (capture on another PC / updated software).", True, "low"), ["elgato"], "low")

# ================================================================ RGB / VENDOR UTILITIES & LOW-LEVEL DRIVERS
add("ene_rgb_driver", "ENE RGB driver (ene.sys) from RGB/motherboard utilities", "rgb_vendor", r"ene\.sys",
    D(drv=["ene.sys"]),
    ["'Incompatible software component detected ... drivers\\ene.sys' and game will not start (wave Jan-Feb 2026)"],
    F("Update/uninstall owning RGB app (Aura/Armoury Crate, RGB Fusion, MSI Mystic Light, Synapse 3, SignalRGB), then remove leftover ene service; renaming the .sys is last resort.", True, "medium"),
    ["spike_incompat", "esports_incompat"], "high")
add("asus_armoury_crate", "ASUS Armoury Crate / Aura / AI Suite (AsIO drivers, LightingService)", "rgb_vendor",
    r"armou?ry ?crate|aura sync|lighting ?service|ai suite|asio\d?\.sys",
    D(svc=["ArmouryCrateService", "LightingService"], drv=["AsIO.sys", "AsIO2.sys", "AsIO3.sys"], uninst=r"^(Armoury Crate|AURA|ASUS Aura|AI Suite)"),
    ["AsIO.sys flagged as incompatible", "RGB/fan service makes PC unstable in VALORANT", "VAN 9101 reports"],
    F("Close Armoury Crate/LightingService; use Armoury Crate Uninstall Tool for full removal (G-Helper as laptop alternative).", True, "medium"), ["spike_incompat"], "medium")
add("corsair_icue", "Corsair iCUE", "rgb_vendor", r"\bicue\b",
    D(proc=["iCUE.exe"], drv=["CorsairLLAccess64.sys"], uninst=r"^(Corsair iCUE|iCUE)"),
    ["Crashes on shutdown/restart after VALORANT", "Temperature-monitoring micro-stutter"], F("Close iCUE / disable its hardware monitoring; update to latest.", True, "low"), [], "medium")
add("razer_synapse", "Razer Synapse / Cortex", "rgb_vendor", r"razer synapse|\bsynapse [34]\b|razer cortex",
    D(svc=["Razer Synapse Service", "Razer Chroma SDK Service"], uninst=r"^Razer (Synapse|Cortex)"),
    ["VAN 102 kicks (service shutting vgc)", "Old Synapse 3 shipped ene.sys", "Mouse button-up not registered (reported)"],
    F("Update to Synapse 4 or uninstall Synapse 3; exit Cortex boosts.", True, "low"), [], "medium")
add("logitech_ghub", "Logitech G HUB", "rgb_vendor", r"\bg ?hub\b|lghub|logitech gaming software",
    D(proc=["lghub.exe", "lghub_agent.exe", "lghub_updater.exe"], svc=["LGHUBUpdaterService"], uninst=r"^(Logitech G HUB|Logitech Gaming Software)"),
    ["VAN 102 / VAL 5 kicks (reported)", "Macro/remap features"], F("Exit G HUB while testing; onboard memory profiles keep settings.", True, "low"), [], "low")
add("steelseries_gg", "SteelSeries GG / Engine / Sonar", "rgb_vendor", r"steelseries ?gg|steelseries engine|\bsonar\b",
    D(proc=["SteelSeriesGG.exe", "SteelSeriesEngine.exe"], uninst=r"^SteelSeries GG"),
    ["Mic sounds slowed/robotic in-game (Sonar)", "FPS decay with clip capture (Moments)"], F("Disable Sonar virtual devices / Moments; set real device as default.", True, "low"), ["neowin_winring0"], "low")
add("msi_center", "MSI Center / Dragon Center / Mystic Light (NTIOLib)", "rgb_vendor", r"msi center|dragon center|mystic light",
    D(drv=["NTIOLib_X64.sys"], uninst=r"^(MSI Center|Dragon Center|Mystic Light)"),
    ["Bundled ene.sys / NTIOLib low-level drivers flagged", "X3D Gaming Mode lobby freeze (MSI BIOS, disputed)"], F("Update or uninstall MSI Center components (Mystic Light).", True, "medium"), [], "low")
add("gigabyte_gcc_rgbfusion", "GIGABYTE Control Center / RGB Fusion / APP Center (gdrv)", "rgb_vendor",
    r"rgb fusion|gigabyte control center|\bgcc\b|app center|gdrv\d?\.sys",
    D(drv=["gdrv.sys", "gdrv3.sys"], uninst=r"^(GIGABYTE Control Center|RGB Fusion|APP Center)"),
    ["gdrv3.sys BSOD when enabling VBS/HVCI", "Stutter after GCC update"], F("Uninstall GCC/RGB Fusion; remove leftover gdrv service.", True, "medium"), [], "low")
add("nzxt_cam", "NZXT CAM", "rgb_vendor", r"nzxt cam|\bcam software\b", D(proc=["NZXT CAM.exe"], uninst=r"^NZXT CAM"),
    ["Monitoring overlay / polling stutter"], F("Exit CAM or disable its overlay.", True, "low"), [], "low")
add("winring0_tools", "WinRing0-based tools (Fan Control, LibreHardwareMonitor, OpenRGB, SignalRGB, old SteelSeries Engine/EVGA)", "monitoring",
    r"winring0|fan ?control\b|libre ?hardware ?monitor|open ?hardware ?monitor|open ?rgb|signal ?rgb|speed ?fan",
    D(proc=["FanControl.exe", "LibreHardwareMonitor.exe", "OpenRGB.exe", "SignalRgb.exe"], drv=["WinRing0x64.sys", "WinRing0.sys"]),
    ["Defender quarantines WinRing0 (HackTool/VulnerableDriver:WinNT/Winring0, 2025)", "Historically blocked by Vanguard/FACEIT", "Can block HVCI"],
    F("Update to PawnIO-based versions or uninstall; stop the WinRing0 driver service.", True, "medium"), ["neowin_winring0", "evga_winring0"], "medium")
add("inpoutx64", "InpOut32/64 low-level I/O driver", "driver", r"inpout",
    D(drv=["inpoutx64.sys"]), ["Flagged as incompatible software component"], F("Uninstall the tool that installed it (fan/RGB/hardware utilities).", True, "medium"), ["spike_incompat"], "medium")
add("lenovo_vantage", "Lenovo Vantage / Legion Zone (+ bundled Nahimic)", "rgb_vendor", r"lenovo vantage|legion zone",
    D(svc=["ImControllerService"], uninst=r"^(Lenovo Vantage|Legion Zone)"),
    ["Wi-Fi throughput collapse while game runs", "Pulls Nahimic back after removal"], F("Disable network boost/updates in Vantage; keep for BIOS/driver updates.", True, "low"), [], "low")

# ================================================================ AUDIO
add("nahimic", "Nahimic / A-Volute audio (MSI, Lenovo, Dell, ASUS Sonic Studio OEM)", "audio", r"\bnahimic\b|a-volute|productinfo\.dll",
    D(proc=["NahimicSvc64.exe", "NahimicSvc32.exe"], svc=["NahimicService"], uninst=r"Nahimic|A-Volute", module_names=["ProductInfo.dll"]),
    ["Sep-Oct 2026 wave: black screen / hang at launch, VALORANT stuck at tiny memory footprint", "VAN 81 reports", "Faulting module ProductInfo.dll 0xc0000005"],
    F("Stop + disable NahimicService first; if persists uninstall Nahimic (it may be reinstalled by OEM driver package/Windows Update).", True, "low"),
    ["gridinsoft_nahimic", "wondershare_blackscreen"], "high")
add("bluetooth_handsfree", "Bluetooth headset Hands-Free (HFP) profile", "audio", r"hands[- ]?free|handsfree telephony|\bhfp\b",
    D(audio_endpoint_regex=r"Hands-Free|Headset \(.*Hands-Free"),
    ["Voice chat switches BT headset to low-quality mono / no game audio after Nov 2025 patch"],
    F("Disable 'Handsfree Telephony' service on the headset (Devices and Printers > Properties > Services) or use a separate mic.", True, "low"), [], "high")
add("bonjour_mdns", "Apple Bonjour (mDNSResponder / mdnsNSP.dll)", "other", r"\bbonjour\b|mdnsnsp|mdnsresponder",
    D(proc=["mDNSResponder.exe"], svc=["Bonjour Service"], uninst=r"^Bonjour", module_names=["mdnsNSP.dll"]),
    ["Oct-Nov 2025: game stuck/not loading after patch, VAL 43; faulting module mdnsNSP.dll"],
    F("Uninstall Bonjour (iTunes/Adobe bundle); no reboot needed.", True, "low"), ["freefixer_mdns", "keengamer_loading"], "high")
add("ni_mdns", "National Instruments software (NI mDNS Responder, nimdnsResponder.dll)", "other", r"national instruments|nimdns",
    D(uninst=r"^NI |National Instruments", module_names=["nimdnsResponder.dll"]),
    ["Same as Bonjour: VAL 43 / not loading; faulting module nimdnsResponder.dll"], F("Uninstall NI mDNS Responder / unused NI packages.", True, "low"), ["keengamer_loading"], "high")
add("sound_blaster_iommu", "Creative Sound Blaster PCIe cards with IOMMU/DMA remapping", "audio", r"sound blaster|creative app",
    D(uninst=r"Sound Blaster|Creative App"), ["Sound card disabled/killed when VALORANT launches (IOMMU/DMAr required)"],
    F("Update Creative drivers; fall back to onboard/USB audio if card is not DMAr-compatible.", True, "low"), ["spike_iommu"], "medium")
add("presonus_firestudio", "PreSonus FireStudio audio drivers", "audio", r"paefirestudio|presonus",
    D(drv=["PaeFireStudio.sys", "PaeFireStudioMidi.sys"]), ["Listed as incompatible drivers preventing Memory Integrity"], F("Update/uninstall PreSonus Universal Control drivers.", True, "medium"), [], "low")
add("audio_enhancers", "Audio enhancement APOs (Dolby Atmos/Access, DTS Sound Unbound, Waves MaxxAudio, Sonic Studio, FxSound, Equalizer APO)", "audio",
    r"dolby (?:atmos|access)|\bdts\b|sound unbound|maxxaudio|waves audio|sonic studio|sonic radar|fx ?sound|equalizer ?apo",
    D(uninst=r"^(Dolby Access|Dolby Atmos|DTS Sound Unbound|DTS:X|Waves MaxxAudio|Sonic Studio|FxSound|Equalizer APO)"),
    ["Audio crackle / missing sounds / footsteps", "APO DLL loaded into audiodg"], F("Disable enhancements for the device; uninstall if issue persists.", True, "low"), [], "low")
add("virtual_audio_mics", "Virtual audio / voice processing (Voicemeeter, VB-CABLE, NVIDIA Broadcast, Krisp, Voicemod)", "audio",
    r"voice ?meeter|vb[- ]?cable|nvidia broadcast|rtx voice|\bkrisp\b|\bvoicemod\b|voice ?changer",
    D(proc=["voicemeeter.exe", "voicemeeterpro.exe", "voicemeeter8.exe", "NVIDIA Broadcast.exe", "krisp.exe"], uninst=r"^(VB:Meeter|Voicemeeter|VB-Audio|NVIDIA Broadcast|Krisp|Voicemod)"),
    ["Wrong default comms device, mic not heard", "Also used as workaround for the HFP voice-chat bug"], F("Check VALORANT voice input/output device mapping; set real devices as default.", True, "low"), [], "low")

# ================================================================ NETWORK OPTIMIZERS / NIC SUITES
add("exitlag", "ExitLag", "network_optimizer", r"\bexit ?lag\b", D(proc=["ExitLag.exe"], uninst=r"^ExitLag"),
    ["Mostly used to fix ISP routing; can mask/alter routes (VAL 62 reports)"], F("Disable while testing baseline ping; keep only if it fixes routing.", True, "low"), [], "medium")
add("game_accelerators", "Other game VPN accelerators (NoPing, WTFast, Mudfish, LagoFast, Haste, Outfox, PingZapper)", "network_optimizer",
    r"\bno ?ping\b|\bwtfast\b|\bmudfish\b|lago ?fast|\bhaste\b|\boutfox\b|ping ?zapper|battle ?ping",
    D(proc=["WTFast.exe"], uninst=r"^(NoPing|WTFast|Mudfish|LagoFast|Haste|Outfox|PingZapper)"),
    ["Tunnel/LSP routing: disconnects, VAL 62"], F("Exit accelerator while testing.", True, "low"), [], "low")
add("killer_suite", "Intel/Rivet Killer Intelligence/Control Center (prioritization)", "network_optimizer",
    r"killer (?:intelligence|control|performance|network|prioriti)|\bkiller\b (?:wi-?fi|ethernet|lan)",
    D(uninst=r"Killer (Intelligence Center|Control Center|Performance Driver Suite|Network Manager)", adapter=r"Killer"),
    ["VAL 57/59 launch errors, ping spikes"], F("Disable prioritization/bandwidth control or uninstall the suite (keep base driver).", True, "low"), [], "medium")
add("smartbyte_cfosspeed", "Traffic shapers (Dell SmartByte, cFosSpeed, ASUS GameFirst, NetLimiter)", "network_optimizer",
    r"smart ?byte|cfosspeed|game ?first|net ?limiter",
    D(svc=["nlsvc"], uninst=r"^(SmartByte|cFosSpeed|GameFirst|NetLimiter)"),
    ["VAN 293 / packet shaping interferes with Riot traffic"], F("Disable or uninstall the shaper.", True, "low"), [], "low")
add("tcp_tweakers", "TCP/registry network tweakers (TCP Optimizer, Leatrix Latency Fix)", "tweak_tool", r"tcp ?optimizer|\bleatrix\b",
    D(reg=[r"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"]), ["Bad TCP/Nagle/QoS settings, connection errors"],
    F("Restore defaults (TCP Optimizer 'Windows Default' or netsh int ip/winsock reset) after backup.", True, "medium"), [], "low")

# ================================================================ NIC / WI-FI / STORAGE DRIVERS
add("realtek_2_5gbe", "Realtek 2.5GbE / Gaming 2.5GbE Family Controller", "driver",
    r"realtek (?:gaming )?2\.5|2\.5 ?gbe|rtl8125|rt25cx21|realtek gaming",
    D(drv=["rt25cx21x64.sys"], adapter=r"Realtek.*2\.5GbE|Realtek Gaming 2\.5GbE"),
    ["Periodic micro-stutter/freezes every few seconds (vgc spikes) even when not in use", "Event log rt25cx21 resets"],
    F("If playing on Wi-Fi: disable the Realtek adapter. On Ethernet: install latest Realtek driver, disable EEE/Green Ethernet/power saving.", True, "low"), [], "high")
add("realtek_gbe_family", "Realtek PCIe GbE Family Controller", "driver",
    r"realtek (?:pcie|pci|gbe|ethernet|lan|family)|rtl81\d\d|rt640x64",
    D(drv=["rt640x64.sys"], adapter=r"Realtek PCIe GbE Family Controller|Realtek Gaming GbE"),
    ["Same micro-stutter pattern; whole-PC freezes with rt640x64 NDIS resets"], F("Same as realtek_2_5gbe.", True, "low"), [], "high")
add("mediatek_rz616", "MediaTek RZ616 / MT7922 Wi-Fi 6E (mtkwl6ex.sys)", "driver", r"mtkwl6ex|mykwl6ex|rz616|mt7922|mediatek",
    D(drv=["mtkwl6ex.sys"], adapter=r"RZ616|MT7922|MediaTek Wi-Fi 6E"),
    ["VAN: Incompatible OEM Driver (driver < 3.5)"], F("Update to driver 3.5+ via Windows Update optional updates / Microsoft Update Catalog.", True, "low"), ["riot_oem"], "high")
add("storage_oem_drivers", "Third-party storage drivers (Samsung secnvme, SK hynix/Solidigm solidnvm, ASMedia asstahci64)", "driver",
    r"secnvme|solidnvm|asstahci",
    D(drv=["secnvme.sys", "solidnvm.sys", "asstahci64.sys"]),
    ["VAN: Incompatible OEM Driver (not DMAr compatible)"], F("Switch device to Microsoft 'NVM Express Controller' / 'Standard SATA AHCI Controller' (Device Manager > Update driver > pick from list).", True, "medium"), ["riot_oem"], "high")
add("intel_vmd_rst", "Intel VMD / RST storage driver (iastorvd.sys)", "driver", r"\bvmd\b|rapid storage|\birst\b|iastorvd",
    D(drv=["iaStorVD.sys"]), ["VAN: STATUS_VMD_9001 / Incompatible OEM Driver"], F("Install Intel's updated VMD/RST driver for 12th-15th gen; restart.", True, "medium"), ["riot_oem"], "high")
add("intel_nal_diag", "Intel Network Adapter Diagnostic driver (iqvw64e.sys)", "driver", r"iqvw64",
    D(drv=["iqvw64e.sys"]), ["Incompatible software popup; known vulnerable driver"], F("Uninstall Intel PROSet/diagnostic tool that loads it.", True, "medium"), [], "medium")
add("entech_se64a", "EnTech softEngine driver (se64a.sys; monitor-control tools e.g. Dell Monitor Asset Manager / softOSD)", "driver", r"se64a",
    D(drv=["se64a.sys"]), ["VAN: Incompatible software, game won't launch (Jan 2026; also League)"], F("Uninstall the monitor-control utility that installed it.", True, "medium"), ["freefixer_se64a"], "medium")
add("misc_flagged_drivers", "Other drivers seen in Vanguard 'incompatible' popups (EPMVolFlt.sys, osfdisk.sys, adlocknetdrv.sys, NPWEBP_64.sys, DirectX64.sys, e1g6032e.sys)", "driver",
    r"epmvolflt|osfdisk|adlocknetdrv|npwebp|directx64\.sys|e1g6032e",
    D(drv=["EPMVolFlt.sys", "osfdisk.sys", "adlocknetdrv.sys", "NPWEBP_64.sys", "DirectX64.sys", "e1g6032e.sys"]),
    ["'Incompatible software component detected' with path drivers\\<name>.sys", "Blocks Memory Integrity"],
    F("Find owning software via driver service ImagePath/Publisher, uninstall it; DirectX64.sys is not a Microsoft file: scan for malware.", True, "medium"),
    ["spike_incompat", "riot_incompat"], "medium",
    "Owners: EPMVolFlt=old disk tool (per thespike), osfdisk=PassMark OSFMount, adlocknetdrv=AdLock ad blocker, e1g6032e=Intel 82574 legacy NIC; NPWEBP_64 / DirectX64 owners unverified.")

# ================================================================ VIRTUALIZATION / REMOTE
add("hyperv_hypervisor", "Hyper-V / Windows Hypervisor Platform", "virtualization", r"\bhyper-?v\b|hypervisor platform|hypervisorlaunchtype",
    D(svc=["vmms", "vmcompute"], windows_features=["Microsoft-Hyper-V-All", "HypervisorPlatform"]),
    ["VAN 9100/138 'virtualized environment' when VALORANT itself runs in a VM", "Turning hypervisor off breaks VBS/HVCI (VAN RESTRICTION)"],
    F("Only remove Hyper-V role if not needed; keep hypervisorlaunchtype Auto when HVCI/VBS is required.", True, "medium"), ["riot_van138", "spike_van9100"], "medium")
add("vmware", "VMware Workstation / Player", "virtualization", r"\bvmware\b",
    D(svc=["VMAuthdService", "VMnetDHCP", "VMUSBArbService"], drv=["vmx86.sys"], uninst=r"^VMware (Workstation|Player)", adapter=r"VMware Virtual Ethernet Adapter"),
    ["Virtual adapters / hypervisor drivers conflict; VM detection"], F("Disable VMnet adapters; stop VMware services while playing.", True, "low"), ["riot_van138"], "medium")
add("virtualbox", "Oracle VirtualBox", "virtualization", r"virtual ?box",
    D(svc=["VBoxSDS"], drv=["VBoxSup.sys"], uninst=r"^Oracle VM VirtualBox|^Oracle VirtualBox", adapter=r"VirtualBox Host-Only"),
    ["Host-only adapter / VBoxSup driver conflicts"], F("Disable host-only adapter; uninstall if unused.", True, "low"), ["riot_van138"], "low")
add("wsl_vmp", "WSL2 / Virtual Machine Platform / Windows Sandbox", "virtualization", r"\bwsl2?\b|virtual machine platform|windows sandbox|subsystem for android",
    D(svc=["WSLService", "LxssManager"], windows_features=["VirtualMachinePlatform", "Microsoft-Windows-Subsystem-Linux", "Containers-DisposableClientVM"], adapter=r"vEthernet \(WSL"),
    ["Virtual switch adapters; claimed VAN 1067/9100 links"], F("Report only; disable feature only if user doesn't use it.", True, "medium"), ["spike_van9100"], "low")
add("docker_desktop", "Docker Desktop", "virtualization", r"\bdocker\b",
    D(proc=["Docker Desktop.exe"], svc=["com.docker.service"], uninst=r"^Docker Desktop"), ["VAN 293 / network filter conflicts (reported)"], F("Quit Docker Desktop while playing.", True, "low"), [], "low")
add("android_emulators", "Android emulators (BlueStacks, LDPlayer, Nox, MEmu, MuMu, GameLoop)", "virtualization",
    r"blue ?stacks|ld ?player|\bnox ?player\b|\bmemu\b|\bmumu\b|gameloop",
    D(proc=["HD-Player.exe", "dnplayer.exe", "Nox.exe", "MEmu.exe"], uninst=r"^(BlueStacks|LDPlayer|Nox|MEmu|MuMu|GameLoop)"),
    ["Own hypervisor drivers / require VBS off: conflicts with HVCI requirement"], F("Close emulator; uninstall if HVCI must be on.", True, "low"), [], "low")
add("remote_desktop_tools", "Remote desktop tools (AnyDesk, TeamViewer, Parsec, Sunshine)", "other", r"any ?desk|team ?viewer|\bparsec\b|\bsunshine\b",
    D(proc=["AnyDesk.exe", "TeamViewer.exe", "parsecd.exe"], svc=["AnyDesk", "TeamViewer", "Parsec"], uninst=r"^(AnyDesk|TeamViewer|Parsec|Sunshine)"),
    ["Virtual display/input drivers; remote-control tools flagged (UltraViewer on Riot list)"], F("Exit remote tools while playing.", True, "low"), [], "low")
add("sandbox_rollback", "Sandbox / reboot-restore tools (Sandboxie, Deep Freeze, Shadow Defender)", "virtualization", r"sandboxie|deep ?freeze|shadow defender|faronics",
    D(svc=["SbieSvc"], drv=["SbieDrv.sys"], uninst=r"^(Sandboxie|Deep Freeze|Shadow Defender)"), ["Vanguard install/update lost at reboot; driver filter conflicts"], F("Run VALORANT outside sandbox; thaw system before Vanguard updates.", True, "low"), [], "low")

# ================================================================ CHEAT / MACRO / INPUT
add("cheat_engine", "Cheat Engine (DBK kernel driver)", "other", r"cheat ?engine|dbk64",
    D(proc=["cheatengine-x86_64.exe", "cheatengine-x86_64-SSE4-AVX2.exe"], drv=["dbk64.sys"], uninst=r"^Cheat Engine"),
    ["VAN 79 after installing; IOMMU restriction; ban risk"], F("Uninstall Cheat Engine and reboot (do not run with Vanguard).", True, "low"), [], "medium")
add("macro_remap_tools", "Macro / remap / virtual controller tools (AutoHotkey, reWASD, DS4Windows/ViGEm, x360ce, Xpadder, JoyToKey, X-Mouse)", "other",
    r"auto ?hot ?key|\bahk\b|\brewasd\b|ds4 ?windows|\bvigem|x360ce|xpadder|joy ?to ?key|x-?mouse button control",
    D(proc=["AutoHotkey.exe", "AutoHotkey64.exe", "AutoHotkeyU64.exe", "reWASD.exe", "DS4Windows.exe", "x360ce.exe", "Xpadder.exe", "JoyToKey.exe", "XMouseButtonControl.exe"],
      svc=["ViGEmBus"], drv=["ViGEmBus.sys"], uninst=r"^(AutoHotkey|reWASD|DS4Windows|ViGEm Bus Driver|X-Mouse Button Control)"),
    ["VAN 102 / VAL 5 kicks (reported)", "Ban risk for triggerbot-style scripts"], F("Exit macro/remap tools before launching.", True, "low"), [], "low")

# ================================================================ TWEAK / DEBLOAT
add("process_lasso", "Process Lasso / ParkControl (priority & affinity rules)", "tweak_tool", r"process ?lasso|park ?control|bitsum",
    D(proc=["ProcessLasso.exe", "ProcessGovernor.exe"], svc=["ProcessGovernor"], uninst=r"^(Process Lasso|ParkControl)"),
    ["High/Realtime priority or affinity on VALORANT/vgc: VAN 136/185, stutter"], F("Remove rules for VALORANT/vgc.exe; reset priority to Normal.", True, "low"), [], "medium")
add("required_services_disabled", "Debloat scripts / tweakers that disable services Vanguard & Riot need (WinUtil, Chris Titus, O&O ShutUp10, privacy.sexy, batch 'optimizers')", "tweak_tool",
    r"\bdebloat|chris titus|\bwinutil\b|shutup10|o&o|privacy\.sexy|fps boost(?:er)?|optimi[sz]ation pack|services? (?:disabled|turned off)",
    D(svc=["vgc", "vgk", "TBS", "Winmgmt", "BFE", "Dnscache", "EventLog"],
      reg=[r"HKLM\SYSTEM\CurrentControlSet\Services\vgc\Start", r"HKLM\SYSTEM\CurrentControlSet\Services\vgk\Start", r"HKLM\SYSTEM\CurrentControlSet\Services\TBS\Start"]),
    ["vgc not running: VAL 79 / VAN 79 / VAL 185", "TPM Base Services off: VAN 9001/9003", "Firewall/BFE off: network errors"],
    F("Back up service config; restore default start types for listed services (vgc per Riot reinstall).", True, "medium"), [], "high")
add("modified_windows", "Modified / debloated Windows builds (AtlasOS, ReviOS, Tiny11, Ghost Spectre) and LTSC", "tweak_tool",
    r"atlas ?os|revi ?os|tiny ?1[01]|ghost ?spectre|\bltsc\b",
    D(reg=[r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\EditionID"], file_paths=[r"C:\Windows\AtlasModules"]),
    ["Mitigations/VBS/Defender stripped: HVCI/TPM requirements fail, missing components (VAN errors, crashes)"],
    F("Report; revert AtlasOS toggles or reinstall stock Windows (destructive, user decision).", False, "high"), [], "medium",
    "EditionID EnterpriseS / IoTEnterpriseS = LTSC.")
add("cleaners_boosters", "Cleaners / game boosters / driver updaters (CCleaner, IObit, Driver Booster, Driver Easy, Wise, Razer Cortex)", "tweak_tool",
    r"\bccleaner\b|\biobit\b|advanced systemcare|driver booster|driver ?easy|wise (?:care|game booster)|game booster|razer cortex",
    D(uninst=r"^(CCleaner|Advanced SystemCare|Driver Booster|Driver Easy|Wise Care|Wise Game Booster|Razer Cortex|IObit)"),
    ["Kill services in 'game mode', install wrong/old drivers, wipe Riot caches"], F("Disable boost/auto-driver features; uninstall.", True, "low"), [], "low")
add("timer_tools", "Timer resolution / standby-list tools (ISLC, TimerResolution)", "tweak_tool", r"timer ?resolution|\bislc\b|standby list cleaner",
    D(proc=["Intelligent standby list cleaner ISLC.exe", "TimerResolution.exe"]), ["Stutter/frametime variance (mixed reports)"], F("Close tool while testing baseline.", True, "low"), [], "low")
add("bcd_tweaks", "BCD tweaks: test signing / debug / hypervisor off / platform clock", "tweak_tool",
    r"bcdedit|test ?signing|testsigning|nointegritychecks|useplatformclock|useplatformtick|disabledynamictick|hypervisorlaunchtype",
    D(), ["Test-signing/debug: Vanguard refuses to run", "hypervisorlaunchtype off: VBS/HVCI unavailable (VAN RESTRICTION)", "HPET/tick tweaks: stutter"],
    F("Back up 'bcdedit /enum'; restore defaults (testsigning off, debug off, hypervisorlaunchtype auto, delete useplatformclock).", True, "medium"), [], "medium",
    "Detected via 'bcdedit /enum {current}', not registry.")
add("hags", "Hardware-accelerated GPU scheduling (HAGS)", "other", r"\bhags\b|hardware[- ]accelerated gpu scheduling",
    D(reg=[r"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode"]), ["Stutter / frame pacing (esp. AMD)"], F("Toggle HAGS (HwSchMode 2=on, 1=off), reboot, compare.", True, "low"), [], "medium")
add("mpo", "Multi-Plane Overlay (MPO)", "other", r"\bmpo\b|multi-?plane overlay",
    D(reg=[r"HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode"]), ["Flicker, stutter after alt-tab, frame drops when enemies appear"], F("Set OverlayTestMode=5 (NVIDIA's MPO disable), reboot; delete value to undo.", True, "low"), ["nvidia_mpo"], "medium")
add("fso_compat_flags", "Fullscreen optimizations / compatibility flags on VALORANT exe", "other",
    r"full ?screen optimi[sz]ation|compatibility mode",
    D(reg=[r"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
           r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"]),
    ["Stutter / input lag; compat-mode flags break launch"], F("Back up and remove stale compat flags for VALORANT/Riot exes; test FSO on/off.", True, "low"), [], "medium")
add("usb_selective_suspend", "USB selective suspend / device power saving", "other", r"selective suspend|usb power (?:saving|management)",
    D(), ["Freezes, mouse/headset disconnects mid-game"], F("Disable USB selective suspend in active power plan (powercfg).", True, "low"), [], "medium",
    "Detected via powercfg /q, not registry.")
add("ipv6_binding", "IPv6 binding / Teredo on active adapter", "other", r"\bipv6\b|teredo",
    D(reg=[r"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\DisabledComponents"]),
    ["VAN 185 / VAN 84 / VAL 57 (community fix: prefer IPv4)"], F("Unbind IPv6 on active adapter (reversible) rather than DisabledComponents=0xFF.", True, "low"), [], "medium")
add("windows_insider", "Windows Insider (Canary/Dev/Beta) builds", "other", r"insider (?:build|preview|program)|\bcanary\b|dev channel",
    D(reg=[r"HKLM\SOFTWARE\Microsoft\WindowsSelfHost\Applicability\BranchName"]), ["vgk.sys BSOD / Vanguard incompatibility on new builds"], F("Report; leave Insider and roll back to a release build (user decision).", False, "high"), [], "medium")


# ---------------------------------------------------------------- reddit evidence
threads = [json.loads(l) for l in open(SRC, encoding="utf-8")]
pref = Counter((c.get("body") or "")[:80] for t in threads for c in t.get("comments") or [])
tmpl = {k for k, v in pref.items() if v >= 15 and len(k) > 20}
FIXW = re.compile(r"uninstall|disabl|remov|turn(?:ed)? off|clos(?:e|ed|ing)|exit|stop|was causing|culprit|conflict|fixed|solved|worked", re.I)
docs = []
for t in threads:
    parts = [t.get("title") or "", t.get("body") or ""] + [c.get("body") or "" for c in t.get("comments") or [] if (c.get("body") or "")[:80] not in tmpl]
    s = bool(t.get("solved")) or t.get("flair") == "Technical Solution"
    docs.append((t["id"], s, " ".join(parts).replace("\n", " ")))

OBSERVED_DRV = {"misc_flagged_drivers", "presonus_firestudio", "entech_se64a", "ac_xigncode", "ene_rgb_driver", "mediatek_rz616", "gigabyte_gcc_rgbfusion"}
out = []
for e in E:
    rx = re.compile(e["_q"], re.I)
    hits = []
    for tid, s, txt in docs:
        m = rx.search(txt)
        if m:
            win = txt[max(0, m.start() - 200): m.end() + 200]
            hits.append((2 * s + (1 if FIXW.search(win) else 0), tid))
    hits.sort(key=lambda x: -x[0])
    n = len(hits)
    e2 = {k: v for k, v in e.items() if not k.startswith("_")}
    if e2["detect"].get("module_names") or e["id"] in OBSERVED_DRV:
        e2.setdefault("notes", "")
        e2["notes"] = (e2["notes"] + " " if e2["notes"] else "") + "DLL/driver names are as observed in r/ValorantTechSupport event-log / Vanguard popup quotes."
    e2["evidence"] = {"reddit_mentions": n, "reddit_solved_mentions": sum(1 for sc, _ in hits if sc >= 2),
                      "example_thread_ids": [tid for _, tid in hits[:5]], "web_sources": e["_web"], "reddit_query": e["_q"]}
    out.append(e2)

ids = [e["id"] for e in out]
assert len(ids) == len(set(ids)), "duplicate ids"
KINDS = {"antivirus", "vpn", "anticheat", "overlay", "rgb_vendor", "audio", "network_optimizer", "virtualization", "monitoring", "tweak_tool", "driver", "other"}
assert all(e["kind"] in KINDS for e in out)
json.dump(out, open(OUT, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print(len(out), "entries")
for e in sorted(out, key=lambda x: -x["evidence"]["reddit_mentions"])[:25]:
    print(e["evidence"]["reddit_mentions"], e["evidence"]["reddit_solved_mentions"], e["id"], e["confidence"])
print("zero:", [e["id"] for e in out if e["evidence"]["reddit_mentions"] == 0])
