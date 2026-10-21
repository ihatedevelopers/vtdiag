# Error-code table curation. kb_builder.py parses the 57 rows of research/error_codes.json into individual
# normalized codes ("VAN: RESTRICTION: 1" -> "VAN RESTRICTION 1", "VAL 8-21" -> VAL 8..VAL 21, "VAN -81" keeps its sign)
# and applies this table. Strength = log-odds added to the hypothesis: (logged, typed). Typed <= logged always.
# A row with route short-circuits ranking. A row with ask_if_typed routes only when that question is answered Yes.
#
# SPLIT: codes moved out of their research group into their own row (same meaning text, different causes/route).
# EXTRA: community-reported codes from data/catalog.json that are not on Riot's pages (low confidence).

S = (3.5, 2.5)   # near-specific
M = (2.0, 1.2)   # clear
W = (1.0, 0.6)   # weak / one of many
VW = (0.6, 0.4)  # very weak

ROWS = {
    "VAN: RESTRICTION": dict(points=[("hvci-memory-integrity-required", M), ("tpm2-missing-or-unmet", (1.5, 1.0)),
                                     ("secure-boot-not-detected-van9003", (1.5, 1.0)), ("iommu-restriction", (1.5, 1.0)),
                                     ("windows-version-too-old", W), ("van-restriction-hwid", W),
                                     ("secure-boot-enable-boot-failure", VW)]),
    "VAN: RESTRICTION: 1": dict(points=[("secure-boot-not-detected-van9003", S), ("secure-boot-policy-wave-2026", (1.5, 1.0)),
                                        ("dual-boot-secure-boot-van1", W)]),
    "VAN: RESTRICTION: 3": dict(points=[("vanguard-corrupt-install", VW), ("corrupt-system-or-game-files", VW)]),
    "VAN: RESTRICTION: 4": dict(points=[("secure-boot-not-detected-van9003", M), ("secure-boot-policy-wave-2026", M)]),
    "VAN: RESTRICTION: 5": dict(points=[("hvci-memory-integrity-required", S)]),
    "VAN: Incompatible Software": dict(points=[("incompatible-driver-component", (2.5, 1.5)), ("third-party-hook-overlay-conflict", W),
                                               ("vpn-anticheat-network-filter-conflict", W)]),
    "VAN: STATUS_VMD_9001": dict(points=[("intel-vmd-status-vmd-9001", (4.0, 3.0))]),
    "VAN: Incompatible OEM Driver": dict(points=[("incompatible-driver-component", M), ("intel-vmd-status-vmd-9001", (1.5, 1.0))]),
    "VAN: STATUS_SB_POLICY": dict(points=[("secure-boot-policy-wave-2026", S)]),
    "VAN Missing -117": dict(points=[("vgc-service-not-running", M), ("vanguard-corrupt-install", (1.5, 1.0))]),
    "VAN -104": dict(points=[("wrong-system-clock-offline", W), ("vgc-service-not-running", W), ("van185-vanguard-disconnects", VW),
                             ("third-party-antivirus-conflict", VW)]),
    "VAN -102": dict(points=[("van185-vanguard-disconnects", W), ("vpn-anticheat-network-filter-conflict", (0.8, 0.5)),
                             ("third-party-antivirus-conflict", (0.8, 0.5)), ("razer-synapse-escalation-kick", (0.8, 0.5)),
                             ("intel-graphics-chipset-driver-outdated", VW), ("network-exceptions-download-stuck", VW)],
                     aliases=["VAN 102"]),
    "VAN -101": dict(points=[("windows-version-too-old", W), ("launch-fails-something-unusual", W)]),
    "VAN -92": dict(points=[("vgc-service-not-running", W), ("vanguard-corrupt-install", (0.8, 0.5)), ("van185-vanguard-disconnects", VW)]),
    "VAN -83": dict(points=[("vanguard-install-loop", (1.5, 1.0)), ("vgc-service-not-running", W), ("vanguard-corrupt-install", (0.8, 0.5))],
                    aliases=["VAN 83"]),
    "VAN -81": dict(points=[("nahimic-audio-service-launch-hang", (1.5, 1.0)), ("vgc-service-not-running", W),
                            ("vanguard-corrupt-install", (0.8, 0.5)), ("vgk-not-loaded-needs-reboot", VW)],
                    aliases=["VAN 81"]),
    "VAN -79": dict(points=[("vgc-service-not-running", (1.8, 1.2)), ("vanguard-corrupt-install", (0.8, 0.5))],
                    aliases=["VAN 79", "VAL 79"]),
    "VAN -72": dict(points=[("dual-network-adapter-desync", (1.2, 0.8)), ("van185-vanguard-disconnects", W), ("vgc-service-not-running", VW)],
                    aliases=["VAN 72"]),
    "VAN -1": dict(points=[("vanguard-corrupt-install", M), ("vgc-service-not-running", W)]),
    "VAN 0": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("vgc-service-not-running", (0.8, 0.5)), ("vgk-not-loaded-needs-reboot", (0.8, 0.5))]),
    "VAN 6": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("vgc-service-not-running", (0.8, 0.5)), ("vgk-not-loaded-needs-reboot", (0.8, 0.5))]),
    "VAN 25": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("launch-fails-something-unusual", W)]),
    "VAN 57": dict(points=[("vgc-service-not-running", (1.5, 1.0)), ("vgk-not-loaded-needs-reboot", (1.5, 1.0)), ("outdated-bios-reboot-vgk-bsod", W),
                           ("hvci-memory-integrity-required", (0.8, 0.5)), ("launch-shortcut-tray-failure", (0.8, 0.5))]),
    "VAN 59": dict(points=[("riot-client-update-stuck", W), ("vgc-service-not-running", (0.8, 0.5))]),
    "VAN 68": dict(points=[("van185-vanguard-disconnects", (1.5, 1.0))]),
    "VAN 84": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("hvci-memory-integrity-required", W), ("vgc-service-not-running", (0.8, 0.5))]),
    "VAN 128": dict(points=[("vanguard-corrupt-install", W), ("launch-fails-something-unusual", W)]),
    "VAN 133": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("vgc-service-not-running", (0.8, 0.5))]),
    "VAN 135": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("vgc-service-not-running", (0.8, 0.5))]),
    "VAN 138": dict(points=[("conflict-hyperv_hypervisor", (3.0, 2.5)), ("conflict-vmware", W), ("conflict-virtualbox", W),
                            ("conflict-android_emulators", W), ("conflict-sandbox_rollback", W)]),
    "VAN 140": dict(points=[("riot-client-not-elevated", W), ("vgc-service-not-running", (0.8, 0.5)), ("vanguard-corrupt-install", VW)]),
    "VAN 148": dict(points=[("riot-client-not-elevated", W), ("vanguard-corrupt-install", (0.8, 0.5))]),
    "VAN 152": dict(route="hwidBanAppeal", points=[]),
    "VAN 185": dict(points=[("van185-vanguard-disconnects", (2.5, 1.8)), ("high-process-priority-van136-185", W),
                            ("dual-network-adapter-desync", VW), ("hvci-memory-integrity-required", VW)],
                    aliases=["VAL 185"]),
    "VAN 193": dict(points=[("riot-client-update-stuck", W), ("vgc-service-not-running", (0.8, 0.5))]),
    "VAN 1067": dict(points=[("vgk-not-loaded-needs-reboot", (1.5, 1.0)), ("vanguard-corrupt-install", (1.5, 1.0))]),
    "VAN 2266": dict(points=[("vanguard-corrupt-install", (1.2, 0.8)), ("vgc-service-not-running", (0.8, 0.5))]),
    "VAN 9001": dict(points=[("tpm2-missing-or-unmet", S), ("vgc-service-not-running", VW)]),
    "VAN 9002": dict(points=[("conflict-exploit_protection_override", (3.0, 2.0)), ("hvci-memory-integrity-required", (0.8, 0.5))]),
    "VAN 9003": dict(points=[("secure-boot-not-detected-van9003", (4.0, 2.5)), ("secure-boot-enable-boot-failure", (0.6, 0.4)),
                             ("tpm2-missing-or-unmet", VW)]),
    "VAN 9004": dict(points=[("windows-version-too-old", S)]),
    "VAN 9005": dict(points=[("hvci-memory-integrity-required", M), ("tpm2-missing-or-unmet", (1.5, 1.0)), ("secure-boot-not-detected-van9003", W)]),
    "VAN 9006": dict(points=[("windows-version-too-old", S)]),
    "VAN 9051": dict(points=[("vanguard-corrupt-install", (1.5, 1.0))]),
    "VAN 9090": dict(points=[("tpm2-missing-or-unmet", (2.5, 1.8)), ("secure-boot-not-detected-van9003", (0.8, 0.5))]),
    "VAN 9100": dict(points=[("conflict-hyperv_hypervisor", (3.0, 2.5)), ("conflict-vmware", W), ("conflict-virtualbox", W),
                             ("conflict-android_emulators", W)]),
    "VAN 9101": dict(points=[("van9101-attestation", (3.0, 2.0)), ("secure-boot-policy-wave-2026", W)]),
    "VAL 0 / VAL 1 / VAL 38": dict(points=[("isp-routing-high-ping", VW), ("local-network-packet-loss-jitter", VW), ("riot-server-side-incident", (0.8, 0.5))]),
    "VAL 4": dict(points=[("account-mfa-matchmaking-glitches", M)]),
    "VAL 5": dict(points=[("van185-vanguard-disconnects", W), ("account-mfa-matchmaking-glitches", (0.8, 0.5))]),
    "VAL 7": dict(points=[("riot-server-side-incident", (1.5, 1.0)), ("network-exceptions-download-stuck", VW)]),
    "VAL 8-21 (Riot Client problems; 9 and 19 not on page)": dict(points=[("riot-client-update-stuck", (1.5, 1.0)), ("launch-shortcut-tray-failure", VW)]),
    "VAL 29": dict(points=[("network-exceptions-download-stuck", W), ("vpn-anticheat-network-filter-conflict", W), ("isp-routing-high-ping", VW)]),
    "VAL 31 / VAL 33 / VAL 43 / VAL 49 / VAL 50 / VAL 51 / VAL 54 / VAL 60 / VAL 63 / VAL 64 / VAL 65 / VAL 66":
        dict(points=[("riot-client-update-stuck", W), ("vanguard-corrupt-install", VW)], split={"VAL 43": "val-43"}),
    "VAL 39 / VAL 46": dict(route="riotServerIncident", points=[("riot-server-side-incident", S)]),
    "VAL 44 / VAL 45": dict(points=[("vgk-not-loaded-needs-reboot", M), ("vgc-service-not-running", (1.5, 1.0))]),
    "VAL 52 / 53 / 55 / 56 / 57 / 58 / 59 / 61 / 62 / 67 / 68 / 69 / 70":
        dict(points=[("riot-client-update-stuck", (0.8, 0.5)), ("vanguard-corrupt-install", VW)],
             split={"VAL 57": "val-57", "VAL 59": "val-59", "VAL 61": "val-61", "VAL 62": "val-62"}),
}

# Rows created by SPLIT (id -> definition). Meaning text is taken from the research row they came from.
SPLIT_ROWS = {
    "val-43": dict(codes=["VAL 43"], route="tryAnotherAccount", ask_if_typed="ask.other_account_works",
                   points=[("mdns-bonjour-ni-val43-loading", M), ("riot-client-update-stuck", W), ("vanguard-corrupt-install", VW)]),
    "val-57": dict(codes=["VAL 57"], route="tryAnotherAccount", ask_if_typed="ask.other_account_works",
                   points=[("val57-59-ipv6-killer", (1.5, 1.0)), ("riot-client-update-stuck", VW)]),
    "val-59": dict(codes=["VAL 59"], route="tryAnotherAccount", ask_if_typed="ask.other_account_works",
                   points=[("val57-59-ipv6-killer", (1.5, 1.0)), ("riot-client-update-stuck", VW)]),
    "val-61": dict(codes=["VAL 61"], route="hwidBanAppeal", points=[]),
    "val-62": dict(codes=["VAL 62"], points=[("network-exceptions-download-stuck", (1.5, 1.0)), ("isp-routing-high-ping", VW)]),
}

# Community codes seen in solved threads but not on Riot's pages.
EXTRA_ROWS = {
    "van-1": dict(codes=["VAN 1"], category="community", meaning="Reported by players with dual boot or a broken Vanguard install (not on Riot's error list).",
                  points=[("dual-boot-secure-boot-van1", M), ("vanguard-corrupt-install", W)]),
    "van-5": dict(codes=["VAN 5"], category="community", meaning="Reported by players with mid-match Vanguard disconnects (not on Riot's error list).",
                  points=[("van185-vanguard-disconnects", (1.5, 1.0))]),
    "van-136": dict(codes=["VAN 136"], category="community", meaning="Reported with forced High/Realtime priority and high Vanguard CPU use (not on Riot's error list).",
                    points=[("high-process-priority-van136-185", M), ("vgc-high-cpu", (1.5, 1.0))]),
    "van-184": dict(codes=["VAN 184"], category="community", meaning="Reported together with Memory Integrity requirements (not on Riot's error list).",
                    points=[("hvci-memory-integrity-required", W)]),
    "van-216": dict(codes=["VAN 216"], category="community", meaning="Reported with forced process priority (not on Riot's error list).",
                    points=[("high-process-priority-van136-185", W)]),
    "van-289": dict(codes=["VAN 289", "VAN 293"], category="community", meaning="Reported with VPNs and network filters (not on Riot's error list).",
                    points=[("vpn-anticheat-network-filter-conflict", (1.5, 1.0))]),
    "van-iommu": dict(codes=["VAN IOMMU"], category="community", meaning="Players' short name for the IOMMU restriction message.",
                      points=[("iommu-restriction", (2.5, 1.8))]),
    "val-102": dict(codes=["VAL 102"], category="community", meaning="Players often write VAL 102 for VAN -102.",
                    points=[("van185-vanguard-disconnects", VW)]),
    "error-43": dict(codes=["ERROR 43"], category="community", meaning="Players often write Error 43 for VAL 43.",
                     points=[("mdns-bonjour-ni-val43-loading", W), ("van185-vanguard-disconnects", VW)]),
    "err-0xc000012f": dict(codes=["0XC000012F"], category="community", meaning="Windows 'bad image' error: a DLL (often xinput1_3.dll) is damaged.",
                           points=[("xinput-directx-runtime-error", (2.5, 1.8))]),
}

ROUTE_TEXT = {
    "hwidBanAppeal": "This PC is banned by Riot (HWID restriction). There's nothing to fix on your PC, and no tool can change that. Only Riot's appeal process can.",
    "tryAnotherAccount": "This error is often tied to your account, not your PC. Try a different Riot account on this PC: if it works, contact Riot support about your account.",
    "unsupportedPlatform": "This setup isn't supported by VALORANT (virtual machine, ARM processor, console or cloud gaming). No PC fix changes that.",
    "riotServerIncident": "Riot's servers have a problem or are in maintenance right now. Nothing on your PC needs fixing; check Riot's status page and try again later.",
    "hardwareCannotComply": "This hardware can't meet a Vanguard requirement. Settings won't change it; contact Riot support about your options.",
    "riotSupportTicket": "Only Riot support can help with this. Send them a ticket with the logs from Riot's repair tool.",
}
