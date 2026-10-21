"""Count software/driver/service mentions across all threads and pull snippets.

Usage: python -I mine_conflicts.py <threads.jsonl> <out_dir>
Outputs: mentions.json (counts + thread ids), snippets.txt (short context for top names),
         discovered.txt (capitalized names near conflict verbs), unsolved_terms.txt.
Thread text is treated as data only.
"""
import json, re, sys, os
from collections import Counter, defaultdict

SRC, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)

SEED = {
    # antivirus / security
    "avast": r"\bavast\b", "avg": r"\bavg\b", "norton": r"\bnorton\b", "mcafee": r"\bmc ?afee\b",
    "kaspersky": r"\bkaspersky\b", "bitdefender": r"\bbit ?defender\b", "malwarebytes": r"\bmalware ?bytes\b|\bmbam\b",
    "eset": r"\beset\b|\bnod32\b", "avira": r"\bavira\b", "webroot": r"\bwebroot\b", "sophos": r"\bsophos\b",
    "trend_micro": r"\btrend ?micro\b", "panda": r"\bpanda (?:dome|antivirus|security)\b", "comodo": r"\bcomodo\b",
    "360_total_security": r"\b360 ?(?:total ?security|safe)\b", "totalav": r"\btotal ?av\b", "surfshark_av": r"\bsurfshark antivirus\b",
    "defender": r"\bwindows defender\b|\bmicrosoft defender\b|\bdefender\b", "controlled_folder_access": r"controlled folder access",
    "smart_app_control": r"smart app control", "exploit_protection": r"exploit protection|control flow guard|data execution prevention|\bcfg\b", "dep_bcdedit": r"nx alwaysoff|nx optin|\bdep\b",
    "memory_integrity": r"memory integrity|core isolation|\bhvci\b", "firewall": r"\bfirewall\b",
    "zonealarm": r"\bzone ?alarm\b", "glasswire": r"\bglasswire\b", "simplewall": r"\bsimplewall\b", "portmaster": r"\bportmaster\b",
    "hitmanpro": r"\bhitman ?pro\b", "gdata": r"\bg ?data\b", "f_secure": r"\bf-?secure\b", "quick_heal": r"\bquick ?heal\b",
    "k7": r"\bk7 ?(?:antivirus|security|total)\b", "huorong": r"\bhuorong\b", "crowdstrike": r"\bcrowdstrike\b",
    # vpn / virtual networking
    "vpn": r"\bvpns?\b", "nordvpn": r"\bnord ?vpn\b|\bnordlynx\b", "expressvpn": r"\bexpress ?vpn\b", "protonvpn": r"\bproton ?vpn\b",
    "surfshark": r"\bsurfshark\b", "cloudflare_warp": r"\bwarp\b|\b1\.1\.1\.1 app\b|cloudflare warp", "mullvad": r"\bmullvad\b",
    "pia": r"\bprivate internet access\b|\bpia vpn\b", "cyberghost": r"\bcyber ?ghost\b", "windscribe": r"\bwindscribe\b",
    "hotspot_shield": r"\bhotspot shield\b", "tunnelbear": r"\btunnel ?bear\b", "openvpn_tap": r"\bopenvpn\b|\btap[- ]?windows\b|\btap adapter\b",
    "wireguard": r"\bwireguard\b", "radmin": r"\bradmin\b", "hamachi": r"\bhamachi\b", "zerotier": r"\bzero ?tier\b",
    "tailscale": r"\btailscale\b", "urban_vpn": r"\burban ?vpn\b", "hola": r"\bhola vpn\b", "psiphon": r"\bpsiphon\b",
    "forticlient": r"\bforti ?client\b", "cisco_anyconnect": r"\banyconnect\b|cisco secure client", "globalprotect": r"\bglobal ?protect\b",
    "softether": r"\bsoftether\b", "proxifier": r"\bproxifier\b", "goodbyedpi": r"\bgoodbye ?dpi\b|\bzapret\b", "proxy": r"\bproxy\b",
    # anticheat
    "faceit": r"\bfaceit\b", "esea": r"\besea\b", "easy_anticheat": r"\beasy ?anti ?cheat\b|\beac\b", "battleye": r"\bbattle ?eye\b",
    "vac": r"\bvac\b", "ricochet": r"\bricochet\b", "nprotect": r"\bnprotect\b|gameguard", "xigncode": r"\bxigncode\b",
    "mrac": r"\bmrac\b", "acebase": r"\bace ?anti ?cheat\b|\bace\b anticheat|\bacebase\b", "vanguard_lol": r"league of legends",
    # overlays / capture
    "discord_overlay": r"discord overlay|\bdiscord\b", "steam_overlay": r"steam overlay", "geforce_experience": r"geforce experience|\bgfe\b|nvidia app|shadowplay|nvidia overlay|instant replay",
    "amd_adrenalin": r"adrenalin|radeon software|amd software", "xbox_game_bar": r"game ?bar|xbox app|game dvr", "obs": r"\bobs\b",
    "medal": r"\bmedal(?:\.tv)?\b", "outplayed": r"\boutplayed\b", "overwolf": r"\boverwolf\b", "blitz": r"\bblitz\b",
    "mobalytics": r"\bmobalytics\b", "porofessor": r"\bporofessor\b", "streamlabs": r"\bstreamlabs\b", "fraps": r"\bfraps\b",
    "bandicam": r"\bbandicam\b", "wallpaper_engine": r"wallpaper engine", "lively": r"lively wallpaper", "rainmeter": r"\brainmeter\b",
    "crosshair_x": r"crosshair ?x\b|crosshair overlay", "reshade": r"\breshade\b", "lossless_scaling": r"lossless scaling",
    "translucenttb": r"translucent ?tb", "startallback": r"start ?all ?back|startisback", "explorerpatcher": r"explorer ?patcher",
    "displayfusion": r"display ?fusion", "fancyzones_powertoys": r"power ?toys", "nvidia_broadcast": r"nvidia broadcast|rtx voice",
    "nvidia_control_panel": r"nvidia control panel",
    # monitoring / OC
    "msi_afterburner": r"after ?burner", "rtss": r"\brtss\b|riva ?tuner", "hwinfo": r"\bhwinfo\b", "hwmonitor": r"\bhw ?monitor\b",
    "cpu_z": r"\bcpu-?z\b", "aida64": r"\baida ?64\b", "openrgb": r"\bopen ?rgb\b", "fan_control": r"fan ?control\b", "speedfan": r"\bspeed ?fan\b",
    "libre_hw_monitor": r"libre ?hardware ?monitor|open ?hardware ?monitor", "throttlestop": r"throttle ?stop", "intel_xtu": r"\bxtu\b|extreme tuning utility",
    "ryzen_master": r"ryzen master", "evga_precision": r"precision x1?\b", "asus_gpu_tweak": r"gpu ?tweak", "zotac_firestorm": r"firestorm",
    "nzxt_cam": r"\bnzxt\b|\bcam software\b", "corsair_icue": r"\bicue\b|corsair", "armoury_crate": r"armou?ry ?crate|aura sync|\blightingservice\b|lighting service",
    "razer_synapse": r"\bsynapse\b|razer cortex|razer", "logitech_ghub": r"g ?hub\b|logitech gaming software|\blgs\b", "steelseries_gg": r"steelseries ?gg|steelseries engine|sonar",
    "msi_center": r"msi center|dragon center|mystic light|msi sdk", "gigabyte_rgb": r"rgb fusion|gigabyte control center|\bgcc\b|app center",
    "asrock_polychrome": r"polychrome", "signalrgb": r"signal ?rgb", "hyperx_ngenuity": r"ngenuity", "wooting": r"\bwootility\b",
    "glorious_core": r"glorious core", "roccat_swarm": r"roccat swarm", "alienware_command": r"alienware command center|\bawcc\b",
    "lenovo_vantage": r"lenovo vantage|legion zone", "omen_gaming_hub": r"omen gaming hub|omen hub", "acer_predatorsense": r"predator ?sense|nitrosense",
    "asus_armoury_laptop": r"\bg-?helper\b",
    # audio
    "nahimic": r"\bnahimic\b", "sonic_studio": r"sonic studio|sonic radar", "voicemeeter": r"voice ?meeter", "vb_cable": r"vb[- ]?cable|vb-audio",
    "dolby_atmos": r"dolby atmos|dolby access", "dts_sound": r"\bdts\b|sound unbound", "realtek_audio_console": r"realtek audio console|realtek hd audio manager",
    "equalizer_apo": r"equalizer ?apo|peace equalizer", "fxsound": r"\bfx ?sound\b", "boom3d": r"boom ?3d", "voicemod": r"\bvoicemod\b",
    "krisp": r"\bkrisp\b", "waves_maxx": r"maxxaudio|waves audio", "sonar": r"steelseries sonar",
    # network optimizers
    "exitlag": r"\bexit ?lag\b", "gearup": r"\bgear ?up\b", "noping": r"\bno ?ping\b", "wtfast": r"\bwtfast\b", "mudfish": r"\bmudfish\b",
    "haste": r"\bhaste\b", "lagofast": r"\blago ?fast\b", "killer_network": r"\bkiller\b|killer intelligence|killer control center",
    "smartbyte": r"\bsmart ?byte\b", "cfosspeed": r"\bcfosspeed\b", "netlimiter": r"\bnet ?limiter\b", "tcp_optimizer": r"tcp ?optimizer",
    "leatrix": r"\bleatrix\b", "asus_gamefirst": r"game ?first", "intel_killer_prioritization": r"killer prioritization",
    "outfox": r"\boutfox\b", "pingzapper": r"\bping ?zapper\b", "battleping": r"\bbattle ?ping\b",
    # virtualization
    "hyper_v": r"\bhyper-?v\b", "vmware": r"\bvmware\b", "virtualbox": r"\bvirtual ?box\b", "wsl": r"\bwsl2?\b",
    "windows_sandbox": r"windows sandbox", "bluestacks": r"\bblue ?stacks\b", "ldplayer": r"\bld ?player\b", "nox": r"\bnox ?player\b|\bnox\b",
    "memu": r"\bmemu\b", "gameloop": r"\bgameloop\b", "docker": r"\bdocker\b", "android_subsystem": r"subsystem for android|\bwsa\b",
    "virtual_machine_platform": r"virtual machine platform", "vbs": r"\bvbs\b|virtuali[sz]ation[- ]based security",
    "svm_vtx": r"\bsvm\b|vt-?x|\bvt-?d\b|virtuali[sz]ation", "parsec": r"\bparsec\b", "anydesk": r"\bany ?desk\b", "teamviewer": r"\bteam ?viewer\b",
    "rustdesk": r"\brust ?desk\b", "sunshine": r"\bsunshine\b|\bmoonlight\b", "steam_link": r"steam link|remote play",
    # cheats/macros/input
    "cheat_engine": r"cheat ?engine", "autohotkey": r"auto ?hot ?key|\bahk\b", "macro": r"\bmacros?\b", "x_mouse": r"x-?mouse button control",
    "rewasd": r"\brewasd\b", "ds4windows": r"\bds4 ?windows\b", "vigembus": r"\bvigem\b", "steam_input": r"steam input",
    "interception_driver": r"interception driver", "joytokey": r"joy ?to ?key", "kprocesshacker": r"process hacker|system informer|kprocesshacker",
    "wemod": r"\bwemod\b", "artmoney": r"\bartmoney\b", "ida_x64dbg": r"x64dbg|\bida pro\b", "kernel_debugging": r"\bbcdedit\b|debug mode|test ?signing|testsigning",
    "driver_signature": r"driver signature enforcement", "spoofer": r"\bspoof(?:er|ing)?\b", "dma": r"\bdma\b",
    # tweak / debloat
    "process_lasso": r"process ?lasso", "park_control": r"park ?control", "atlas_os": r"\batlas ?os\b|\batlasos\b", "revios": r"\brevi ?os\b",
    "ghost_spectre": r"ghost ?spectre", "tiny11": r"\btiny ?1[01]\b", "chris_titus": r"chris titus|\bwinutil\b|ctt", "debloat": r"\bdebloat(?:er|ed|ing)?\b",
    "o_and_o_shutup": r"shutup10|o&o", "win10privacy": r"privacy\.sexy|w10privacy", "ccleaner": r"\bccleaner\b", "iobit": r"\biobit\b|advanced systemcare|driver booster|smart defrag",
    "driver_easy": r"driver ?easy", "razer_cortex_boost": r"razer cortex", "wise_care": r"wise care|wise game booster", "game_booster": r"game booster",
    "timer_resolution": r"timer ?resolution|islc|intelligent standby list cleaner", "msi_mode_utility": r"msi mode|msi_util", "nvidia_profile_inspector": r"profile inspector",
    "nvcleanstall": r"nvcleanstall", "ddu": r"\bddu\b|display driver uninstaller", "regedit_tweaks": r"\bregistry tweaks?\b|reg tweaks?",
    "fps_boost_packs": r"fps boost(?:er)?|optimi[sz]ation pack|optimizer", "hone": r"\bhone(?:\.gg)?\b", "windows_update_blocker": r"windows update blocker|wub",
    "winaero": r"winaero", "stopupdates10": r"stopupdates", "bloatynosy": r"bloatynosy",
    # drivers / hw
    "vgk": r"\bvgk\b", "vgc": r"\bvgc\b", "winring0": r"winring0", "rtcore64": r"rtcore", "inpoutx64": r"inpout", "gdrv": r"\bgdrv\b",
    "asio": r"\basio\b", "cpuz_driver": r"cpuz1\d\d", "iqvw64e": r"iqvw64", "mhyprot": r"mhyprot", "faceit_driver": r"faceit\.sys",
    "intel_gpu_driver": r"intel (?:hd|uhd|iris|arc)|intel graphics", "nvidia_driver": r"nvidia driver|geforce driver|\b5[0-9]{2}\.\d{2}\b",
    "amd_chipset": r"chipset driver", "realtek_lan": r"realtek (?:pcie|gbe|ethernet|lan|2\.5g)|rtl81", "intel_lan": r"intel (?:i225|i226|ethernet)",
    "killer_wifi": r"killer (?:wi-?fi|ax|1[56]\d\d)", "mediatek_wifi": r"mediatek", "bluetooth": r"\bbluetooth\b",
    "tpm": r"\btpm\b|\bftpm\b|\bptt\b", "secure_boot": r"secure ?boot", "csm": r"\bcsm\b|legacy bios|\bmbr\b", "bios_update": r"bios update|update (?:the|my|your) bios",
    "intel_13_14_gen": r"1[34][0-9]{3}k|raptor lake|microcode|0x12[9b]", "xmp_expo": r"\bxmp\b|\bexpo\b|docp", "undervolt": r"undervolt|curve optimi[sz]er|\bpbo\b",
    "overclock": r"\boverclock", "fast_startup": r"fast startup", "windows_insider": r"insider (?:build|preview|program)|canary|dev channel",
    "windows_n": r"windows 1[01] n\b|media feature pack", "windows_ltsc": r"\bltsc\b", "windows_server": r"windows server",
    "cracked_windows": r"\bkms\b|activator|cracked windows|pirated windows", "faceit_ac": r"faceit anti",
    "riot_client_services": r"riot client services|riotclientservices", "vc_redist": r"visual c\+\+|vc_?redist|vcruntime", "directx": r"directx|d3d11|dxgi",
    "dotnet": r"\.net framework", "smb_lanman": r"lanmanworkstation|workstation service", "kerberos_dns": r"\bdns\b",
    "ipv6": r"\bipv6\b", "winsock": r"winsock|netsh", "ethernet_qos": r"\bqos\b", "hosts_file": r"hosts file", "isp": r"\bisp\b",
    "starlink": r"\bstarlink\b", "mobile_hotspot": r"hotspot|tethering", "powerline": r"powerline", "mesh": r"\bmesh\b",
    # discovered pass
    "hags": r"\bhags\b|hardware[- ]accelerated gpu scheduling", "mpo": r"\bmpo\b|multi-?plane overlay", "hpet": r"\bhpet\b|useplatformclock",
    "iommu": r"\biommu\b", "bonjour": r"\bbonjour\b", "asus_atk": r"\batk (?:package|0110)|\batkexcomsvc|asus atk", "intel_vmd_rst": r"\bvmd\b|rapid storage|\birst\b",
    "realtek_2_5g": r"realtek (?:gaming )?2\.5|2\.5 ?gbe|rtl8125", "realtek_gaming_lan": r"realtek gaming", "dragon_lan": r"dragon (?:gaming|lan)",
    "bt_handsfree": r"hands[- ]?free|handsfree telephony|hfp", "trident_z5": r"trident z5|g\.skill|gskill", "valorant_tracker": r"valorant tracker|tracker\.gg",
    "x3d_turbo": r"x3d turbo|turbo mode", "usb_selective_suspend": r"selective suspend", "amd_igpu": r"\bigpu\b|integrated graphics|radeon\(tm\) graphics|vega 8",
    "fso": r"fullscreen optimi[sz]ation|full screen optimi[sz]ation", "hw_accel_discord": r"hardware acceleration", "ecores": r"\be-?cores?\b",
    "smt": r"\bsmt\b|hyper-?threading", "exclusive_mode": r"exclusive mode", "game_mode": r"\bgame mode\b", "power_saving_nic": r"energy efficient ethernet|green ethernet|power saving mode",
    "windows_update": r"windows update", "kb_update": r"\bkb50\d{5}\b", "vgc_service_disabled": r"vgc (?:service )?(?:is )?(?:disabled|stopped|not running)",
    "msconfig_hide_ms": r"msconfig|clean boot", "riot_vanguard_tray": r"vanguard tray",
    # misc
    "onedrive": r"\bonedrive\b", "google_drive": r"google drive", "dropbox": r"\bdropbox\b", "riot_vanguard_folder_onedrive": r"documents folder.*onedrive",
    "epic_games": r"epic games launcher", "steam": r"\bsteam\b", "ea_app": r"\bea app\b|\borigin\b", "battlenet": r"battle\.net", "ubisoft": r"ubisoft connect|uplay",
    "genshin": r"genshin", "fortnite": r"fortnite", "apex": r"\bapex\b", "cs2": r"\bcs2\b|counter-?strike", "roblox": r"\broblox\b",
    "pubg": r"\bpubg\b", "rust_game": r"\brust\b", "warzone": r"warzone|call of duty", "bloodstrike": r"blood ?strike", "delta_force": r"delta force",
    "marvel_rivals": r"marvel rivals", "the_finals": r"the finals", "arc_raiders": r"arc raiders",
    "intel_driver_support": r"intel driver (?:&|and) support assistant|\bdsa\b", "armoury_crate_service": r"armou?ry ?crate service",
    "asus_ai_suite": r"ai suite", "asus_dipm": r"\bdip5\b", "msi_dragon": r"dragon center", "nvidia_gpu_scaling": r"gpu scaling",
    "hdr": r"\bhdr\b|auto hdr", "gsync": r"g-?sync|freesync", "multi_monitor": r"second monitor|dual monitor|multiple monitors",
    "duet_display": r"duet display|spacedesk", "displaylink": r"displaylink", "fsr_upscalers": r"\bnis\b|\brsr\b|radeon super resolution",
    "afmf": r"\bafmf\b|fluid motion", "anti_lag": r"anti-?lag", "reflex": r"\breflex\b",
    "language_ime": r"\bime\b|keyboard layout|input method", "region_locale": r"system locale|region settings|beta: use unicode utf-8",
    "time_sync": r"time sync|system time|date and time", "username_unicode": r"username (?:has|with|contains) (?:special|non-english)",
    "kaspersky_free": r"kaspersky free", "sandboxie": r"\bsandboxie\b", "deep_freeze": r"deep ?freeze", "shadow_defender": r"shadow defender",
    "faronics": r"faronics", "school_mdm": r"school (?:laptop|computer|pc)|work laptop|company laptop|managed by your organization|intune",
    "family_safety": r"family safety|parental control", "qustodio": r"\bqustodio\b", "net_nanny": r"net nanny",
    "nvidia_container": r"nvidia container", "amd_noise": r"amd noise suppression", "logitech_g_hub_lghub_agent": r"lghub",
    "msi_afterburner_rtss_driver": r"rtcore64", "elgato": r"\belgato\b|4k capture utility|stream deck", "wacom": r"\bwacom\b|opentabletdriver",
    "vjoy": r"\bvjoy\b", "tobii": r"\btobii\b", "trackir": r"track ?ir", "openrgb_driver": r"openrgb",
    "voice_changer": r"voice ?changer|clownfish", "nvidia_rtx_video": r"rtx video", "ultimate_performance": r"ultimate performance",
    "high_performance_power": r"power plan", "virtual_desktop": r"virtual desktop|\bvr\b|steamvr|oculus", "msi_kombustor": r"kombustor|furmark",
    "punkbuster": r"punkbuster", "faceit_client": r"faceit client", "gamersclub": r"gamers ?club", "5e_arena": r"\b5e(?:play| arena)\b",
    "perfect_world_arena": r"perfect world arena", "wegame": r"\bwegame\b", "ggpoker_anticheat": r"ggpoker",
}
CRX = {k: re.compile(v, re.I) for k, v in SEED.items()}

VERB = re.compile(r"(?:uninstall(?:ed|ing)?|disabl(?:e|ed|ing)|remov(?:e|ed|ing)|clos(?:e|ed|ing)|turn(?:ed|ing)? off|kill(?:ed|ing)?|delet(?:e|ed|ing)|conflict(?:s|ing)?(?: with)?|was causing|caus(?:es|ed|ing)|culprit|the problem was|the issue was|it was)\s+(?:the\s+|my\s+)?([A-Z][\w\.\-\+]*(?:\s+[A-Z][\w\.\-\+]*){0,3})")
VERB2 = re.compile(r"([A-Z][\w\.\-\+]*(?:\s+[A-Z][\w\.\-\+]*){0,3})\s+(?:was (?:the (?:issue|problem|culprit)|causing|blocking|interfering|conflicting)|is (?:causing|blocking|interfering|conflicting)|causes? (?:the|this|it)|conflicts? with)")
FIXWORDS = re.compile(r"\b(fixed|solved|worked|resolved|that did it|thank|works now|working now)\b", re.I)
STOP = set("I It The This That My Your You Valorant Vanguard Riot Windows Game Then And But So Also If When After Now Just Do Try Did Thanks Thank Please Edit Update Yes No Hi Hey Hello Okay Ok Still Even PC Settings Control Panel Task Manager Device Manager Start Restart BIOS TPM Secure Boot Discord".split())

def tl(t):
    return "\n".join([t.get("title") or "", t.get("body") or ""] + [c.get("body") or "" for c in t.get("comments") or []])

threads = [json.loads(l) for l in open(SRC, encoding="utf-8")]
# Drop bot / copy-paste template comments: any comment whose 80-char prefix repeats >= 15 times.
_pref = Counter((c.get("body") or "")[:80] for t in threads for c in t.get("comments") or [])
TEMPLATES = {k for k, v in _pref.items() if v >= 15 and len(k) > 20}
for t in threads:
    t["comments"] = [c for c in t.get("comments") or [] if (c.get("body") or "")[:80] not in TEMPLATES]

def is_solved(t):
    return bool(t.get("solved")) or t.get("flair") == "Technical Solution"

cnt = Counter(); cnt_solved = Counter(); cnt_unsolved = Counter(); cnt_fix = Counter()
ids = defaultdict(list); snips = defaultdict(list); codes_by = defaultdict(Counter)
disc = Counter(); disc_ids = defaultdict(list)
for t in threads:
    txt = tl(t); s = is_solved(t)
    for k, rx in CRX.items():
        m = rx.search(txt)
        if not m:
            continue
        cnt[k] += 1
        (cnt_solved if s else cnt_unsolved)[k] += 1
        if len(ids[k]) < 400:
            ids[k].append(t["id"])
        for c in t.get("codes") or []:
            codes_by[k][c] += 1
        # fix signal: an OP comment/body with fix words, and the name appears in a comment near a removal verb
        lw = txt.lower()
        i = m.start()
        win = txt[max(0, i - 160): i + 200].replace("\n", " ")
        if re.search(r"uninstall|disabl|remov|turn(?:ed)? off|close|was causing|culprit|conflict", win, re.I):
            if s or any(c.get("op") and FIXWORDS.search(c.get("body") or "") for c in t.get("comments") or []):
                cnt_fix[k] += 1
                if len(snips[k]) < 6:
                    snips[k].append(f"[{t['id']}{' S' if s else ''}] {win}")
    for rx in (VERB, VERB2):
        for m in rx.finditer(txt):
            name = m.group(1).strip(" .,")
            first = name.split()[0]
            if first in STOP or len(name) < 3:
                continue
            disc[name] += 1
            if len(disc_ids[name]) < 5:
                disc_ids[name].append(t["id"])

res = {k: {"threads": cnt[k], "solved": cnt_solved[k], "unsolved": cnt_unsolved[k], "fix_context": cnt_fix[k],
           "top_codes": codes_by[k].most_common(5), "ids": ids[k][:12]} for k in cnt}
json.dump(dict(sorted(res.items(), key=lambda x: -x[1]["threads"])), open(os.path.join(OUT, "mentions.json"), "w", encoding="utf-8"), indent=1)
with open(os.path.join(OUT, "snippets.txt"), "w", encoding="utf-8") as f:
    for k, _ in sorted(cnt_fix.items(), key=lambda x: -x[1]):
        f.write(f"\n=== {k} threads={cnt[k]} fixctx={cnt_fix[k]} codes={codes_by[k].most_common(4)}\n")
        for s_ in snips[k]:
            f.write("  " + s_[:340] + "\n")
with open(os.path.join(OUT, "discovered.txt"), "w", encoding="utf-8") as f:
    for n, c in disc.most_common(600):
        if c >= 3:
            f.write(f"{c}\t{n}\t{disc_ids[n]}\n")
print("threads", len(threads), "solved", sum(is_solved(t) for t in threads))
