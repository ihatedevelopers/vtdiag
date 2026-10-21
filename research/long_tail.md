# Long tail: what the unsolved threads show that the solved set may miss

Data: 7,821 threads. 952 count as "solved" (solved flag or the Technical Solution flair), 6,869 as unsolved. Bot and copy-paste template comments are removed first. Counts are threads matched by regex, shown as **U** (unsolved) / **S** (solved). Most of these counts are rough, keyword-level signals.
Scripts: `research/scripts/long_tail.py` and the inline counts in this file. Snippets come from `research/scripts/snip.py`.

## Problem areas that are mostly unsolved (high U:S ratio)
- **VAN RESTRICTION: HVCI + IOMMU required** (old or unsupported hardware, BIOS without IOMMU/VT-d, "incompatible drivers" blocking Memory Integrity): **146 U / 24 S**. For many of these users the real answer is "this hardware can't comply", and the solved set rarely says so. The tool should tell them clearly instead of looping through fixes.
- **VAN 152 / HWID restriction**: **55 U / 3 S**. This needs an appeal, not a tech fix. Detect it and route the user to Riot support.
- **Riot Client won't open / login loop**: **38 U / 1 S**.
- **Account-specific failures** (VAL 43/57/59 on the main account while an alt account works on the same PC): **33 U / 2 S**. These are server or account side, and no PC scan will find them. Add a "test with another account" step.
- **Packet loss / jitter despite good ping**: **259 U / 45 S**. **ISP routing to a region** (Mumbai, Middle East, SEA): **270 U / 44 S**. Starlink, hotspot and CGNAT: **33 U / 10 S**. ISP or country blocking and DPI: **22 U / 8 S**.
- **Voice chat / mic not working** (beyond the Bluetooth Hands-Free case): **245 U / 40 S**, and 95 of them got no reply at all.
- **Whole-PC hard freeze in a match, no BSOD**: **60 U / 9 S**. **Kernel-Power 41 / random restarts**: **49 U / 9 S**. **The game freezes while audio keeps playing** (GPU hang): **18 U / 2 S**. **GPU TDR** (nvlddmkm/amdkmdag): **20 U / 4 S**.
- **Crash to desktop with no error / "Critical error"**: **52 U / 7 S**. **Stuck on the loading or splash screen with no code**: **100 U / 23 S**. The solved set covers the Bonjour, Nahimic and Oct-2026 hook-list causes, but the rest has no known cause.
- **Overheating / thermal throttling**: **240 U / 29 S**. **Failing hardware** (PSU, RAM errors, GPU artifacts): **96 U / 20 S**. **Intel 13th/14th gen instability**: **48 U / 12 S**. These are hardware problems, so the tool should run sensor and WHEA checks rather than apply software fixes.
- **Laptop iGPU/dGPU selection** (game on the wrong GPU, MUX/Optimus): **35 U / 4 S**. GPU below the minimum (Intel HD, D3D11 feature level): **23 U / 4 S**.
- **Input lag / floaty mouse / delayed shots**: **86 U / 18 S**. High polling rate (4k/8k): **85 U / 23 S**. **Peeker's advantage / desync**: **50 U / 9 S**.
- **Low-spec bottlenecks** (8 GB RAM / pagefile **73 U / 16 S**, install on an HDD **54 U / 11 S**).
- **Display**: stretched res / black bars **147 U / 29 S**. Refresh rate, HDR and color **135 U / 25 S**. Custom resolution, DSR and CRU **36 U / 6 S**. Multi-monitor **32 U / 9 S**.
- **Console (PS5/Xbox) and controller threads**: **99 U / 13 S**. These are out of scope, so detect and redirect them.
- **Mac, Linux, VM or cloud gaming** (unsupported): **43 U / 10 S**. **Windows on ARM**: 5 U / 3 S.
- **Both League and VALORANT broken** (Vanguard-wide problem): **24 U / 4 S**.
- **System clock / time sync**: **12 U / 2 S**. **TPM cleared / VAN 9090 after a BIOS update**: 4 U / 1 S.
- **Missing DLLs** (DSOUND.dll on Windows N, XInput, MSVCP): **5 U / 1 S**. Windows N / Media Feature Pack: 2 U / 0 S. Small numbers, but easy to detect and fix.

## Conflict-registry entries that skew unsolved (mentioned, rarely confirmed as the fix)
- ExitLag **57 U / 8 S**. Game accelerators **24 U / 4 S**. GearUP **10 U / 1 S**. People suggest these for routing, but they also show up as suspects.
- Clip recorders: Medal **55 U / 8 S**, OBS **30 U / 6 S**.
- Modified Windows (Atlas, Revi, Tiny11, LTSC): **46 U / 7 S**. Services disabled by debloaters: **57 U / 11 S**.
- AMD Adrenalin features or driver regressions: **130 U / 30 S**. HAGS **90 U / 21 S**. MPO **36 U / 9 S**.
- Sound Blaster PCIe cards killed by IOMMU/DMAr: **12 U / 1 S**. Armoury Crate **14 U / 2 S**. G HUB **14 U / 2 S**.

## Newer or one-off causes found in unsolved threads (often only 1–5 threads, so check the trend)
- Hooked DLLs named in Event Viewer after the Oct-2026 update: `n64hooks.dll` (Nielsen), `udp_connect_lsp64.dll` (GearUP), `GTIII-OSD64-VK.dll` (ASUS GPU Tweak OSD), `btmmhook.dll` (Bluetooth tray hook, vendor unverified), `PerfWndMonModule.dll` (Huawei PC Manager). Several users report none of the listed tools. The tool should read the VALORANT faulting module from the Application log and match it against `module_names`.
- Vanguard "incompatible component" popups with unidentified owners: `NPWEBP_64.sys`, `DirectX64.sys` (not a Microsoft file, possibly malware), `adlocknetdrv.sys`, `osfdisk.sys`, plus `e1g6032e.sys` and `PaeFireStudio*.sys` blocking HVCI.
- Pattern to watch: VAN errors only on certain days or only after a Riot patch. These are usually server-side waves (Nahimic in Sep–Oct 2026, ene.sys in Jan–Feb 2026, Bonjour in Oct–Nov 2025, the hook list in Oct 2026). Tag fixes with dates.
- Non-English threads (FR, PT, ES, TR, VI, ZH) get fewer replies, so add a localized UI or report.
- 2,180 threads got no reply at all (from `stats.json`). The tool's shareable report can fill that gap.
