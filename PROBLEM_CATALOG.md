# Valorant Tech Support – Problem Catalog

Built from r/ValorantTechSupport (Oct 2025 – Oct 2026). Meant as the input for planning a Windows diagnostic tool. The machine-readable version is `data/catalog.json`: same problems, plus every merged symptom, root cause, fix and thread ID.

## Overview

- **7,821 posts** and **33,390 comments** in the archive. **2,180 posts (27.9%) never got a reply.** Flairs: Technical Support Request 6,439, Technical Discussion 822, Technical Solution 560.
- **952 solved or guide threads** went through extraction, which gave 216 raw problem records. After deduplication those became **90 canonical problems**, covering **732 unique threads**. The other threads produced no usable record.
- **Most frequent topics** across all posts (keyword hits; one post can count in several topics): install update 2,793, crash 1,606, network 1,516, gpu 1,484, vanguard 1,280, login account 1,200, fps drop 974, display 927, tpm secureboot 762, audio mic 662, input mouse 453.
- **Top error codes mentioned** across all posts: VAN 185 (122), VAN 9003 (71), VAN 79 (50), VAL 5 (45), VAN 84 (35), VAL 43 (34), VAN 102 (34), VAN 1 (21), VAN 9001 (19), VAN 72 (19), VAN 5 (18), VAN 152 (18). VAN 185 (mid-match Vanguard disconnect) is far ahead of everything else. The next group is VAN 9003 (Secure Boot), VAN 79 (vgc service), VAL 5 / VAN 102 and VAL 43 (stuck loading).
- **Monthly volume:** 217–828 posts per month. The peak is 2026-01. The first and last months are partial.
- **The big finding:** the most common *solved* problem by far is not a Vanguard error code. It is stutter caused by the Realtek 2.5GbE NIC. Second is overlay/hooking software. Both can be detected locally with high confidence.

## Top 25 problems

Auto-detectable: **Yes** means at least one high-confidence local check exists. **Partly** means only medium-confidence checks. **Weak** means the checks are only indicative. Best fix is the top-ranked fix: OP-confirmed or guide fixes come first, then by how many threads reported it.

| # | Problem | Error codes | Threads | Best working fix (evidence, reports) | Auto-detect | Auto-fix |
|---|---|---|---|---|---|---|
| 1 | **Periodic micro-stutter / freezes every few seconds caused by Realtek 2.5GbE / PCIe GbE controller driver…** `realtek-2-5gbe-nic-stutter` | – | 76 | Device Manager > Network adapters > disable the Realtek PCIe 2.5GbE controller (or unused onboard LAN) when using… (*OP-confirmed*, 19) | Yes | Yes |
| 2 | **Stutter / FPS drops / freezes / loading hangs caused by overlay or hooking software (RTSS, Overwolf/trackers,…** `third-party-hook-overlay-conflict` | – | 49 | Close/uninstall RivaTuner Statistics Server (RTSS) before playing; MSI Afterburner can stay if all monitoring items are… (*OP-confirmed*, 9) | Yes | Partly |
| 3 | **Secure Boot cannot be enabled or PC won't boot after enabling it (Secure Boot Violation, 'Invalid signature',…** `secure-boot-enable-boot-failure` | VAN 9003, VAN RESTRICTION | 32 | Update the BIOS (install intermediate versions step by step if very old); user's 2017 BIOS had a TPM/Secure Boot bug… (*OP-confirmed*, 7) | Yes | No |
| 4 | **Packet loss / jitter / high or constant ping from Wi-Fi, router placement, stale TCP stack, bufferbloat, NIC…** `local-network-packet-loss-jitter` | – | 30 | Switch from Wi-Fi to Ethernet cable. (*OP-confirmed*, 5) | Yes | Partly |
| 5 | **Bluetooth headset muffled / call-quality audio or no voice when Valorant voice chat switches it to Hands-Free…** `bluetooth-headset-hfp-voice-chat` | – | 29 | Disable 'Handsfree Telephony' service on the headset: Settings > Bluetooth & devices > More devices and printer… (*OP-confirmed*, 7) | Yes | Yes |
| 6 | **Black screen / stuck loading bar (~10%) on launch caused by Nahimic (A-Volute) audio service injecting into…** `nahimic-audio-service-launch-hang` | VAN 81 | 29 | Stop and disable the 'Nahimic service' (services.msc -> Nahimic Service -> Startup type Disabled -> Stop; or Task… (*OP-confirmed*, 19) | Yes | Yes |
| 7 | **Vanguard requires Memory Integrity (HVCI): VAN RESTRICTION / VAN 9005 / VAN 57/84; HVCI won't turn on or stay…** `hvci-memory-integrity-required` | VAN RESTRICTION, VAN 185, VAN 57, VAN 84, VAN 184 | 27 | Windows Security > Device security > Core isolation > turn on Memory Integrity; reboot; Valorant 'repair' if it reports… (*OP-confirmed*, 8) | Yes | Partly |
| 8 | **Sudden high ping / packet loss / chat service unavailable from ISP routing or peering issues (regional,…** `isp-routing-high-ping` | – | 26 | Install and enable Cloudflare WARP (1.1.1.1 app, WARP mode) while playing; several OPs confirmed it restored normal… (*OP-confirmed*, 8) | Yes | No |
| 9 | **VAN 185 / VAN 5 / VAN 102 / VAL 5 mid-match disconnects and escalating suspensions (network instability,…** `van185-vanguard-disconnects` | VAN 185, VAL 5, ERROR 43, VAN 5, VAN 102 | 24 | Set vgc service to Automatic, disable fullscreen optimizations, allow Vanguard through antivirus (earlier fix used by… (*OP-confirmed*, 4) | Yes | Partly |
| 10 | **AMD Adrenalin driver regressions (26.5.x, 26.7.1, 26.8.1): freezes, black screens, GPU driver crash/disabled,…** `amd-adrenalin-driver-regression` | – | 21 | Roll back/DDU-clean-install an older AMD driver (26.1.1, 26.3.1 or 26.6.1 reported); one user confirmed fix for a… (*OP-confirmed*, 7) | Yes | Partly |
| 11 | **Low / dropping FPS or laptop freezes from power plan/power mode, battery mode, thermal or power-limit…** `power-plan-throttling-low-fps` | – | 21 | Issue resolved after Microsoft Store outage ended and Lenovo Vantage could be installed (laptop performance mode). (*OP-confirmed*, 1) | Yes | Partly |
| 12 | **Whole-PC freeze / reboot / Kernel-Power 41 / LiveKernelEvent 141 while playing (mostly new AMD builds, often…** `whole-pc-freeze-kernel-power-41` | – | 21 | Disable Realtek ethernet/unused adapter (fixed LiveKernelEvent 141) (*OP-confirmed*, 1) | Yes | Partly |
| 13 | **VAL 43 / stuck on loading screen caused by Apple Bonjour (mdnsNSP.dll) or National Instruments…** `mdns-bonjour-ni-val43-loading` | VAL 43 | 19 | Uninstall Bonjour (Apple) completely via Revo/Uninstalr, optionally run 'sfc /scannow' first, then uninstall Vanguard,… (*OP-confirmed*, 4) | Yes | Partly |
| 14 | **PC reboots instantly (no BSOD), whole-PC crash/freeze or vgk.sys BSOD on Valorant launch due to outdated…** `outdated-bios-reboot-vgk-bsod` | VAN 57 | 19 | Update motherboard BIOS to latest from manufacturer (flash drive/EZ Flash etc.), back up/disable BitLocker first;… (*OP-confirmed*, 13) | Yes | No |
| 15 | **VAN 9003 / VAN RESTRICTION 1 / VAN 9090: 'UEFI Secure Boot required' even though it looks enabled…** `secure-boot-not-detected-van9003` | VAN 9003, VAN 9090, VAN RESTRICTION 1, VAN 9001, VAN RESTRICTION | 19 | In BIOS Secure Boot > Key Management: 'Install default/factory keys' or 'Reset to factory keys', then verify msinfo32… (*OP-confirmed*, 2) | Yes | No |
| 16 | **Vanguard never installs / 'Preparing download' / Update-Play loop / 'Installing Vanguard' loop / vgk.sys or…** `vanguard-install-loop` | VAN 83 | 18 | Admin cmd: sc delete vgc ; sc delete vgk ; reboot ; delete C:\Program Files\Riot Vanguard ; reopen Riot Client and… (*OP-confirmed*, 3) | Yes | Yes |
| 17 | **Riot Client update/install fails or loops ('Something went wrong', endless 'Updating', fatal error after…** `riot-client-update-stuck` | VAL 43 | 17 | Riot client profile > Repair; or restart PC; or disable auto updates; or exit Vanguard in tray, reboot (*OP-confirmed*, 3) | Yes | Yes |
| 18 | **Shader compilation / shader-cache stutter after fresh install, driver or GPU change (UE5/DX11), and…** `shader-cache-compilation-stutter` | – | 16 | Use SCSKiller: Library tab -> add game by selecting VALORANT.exe (not Win64-Shipping), clear shader cache, then… (*OP-confirmed*, 1) | Partly | Partly |
| 19 | **Corrupted Vanguard (VAN -1 / VAN 1067 / vgk.sys BSOD / restart-required loop / crash at launch / 'Something…** `vanguard-corrupt-install` | VAN 1, VAN 1067, VAN -1, VAN 84 | 15 | Follow the Vanguard 1067 thread steps (OP of a VAL 43 thread said this fixed it; steps were linked, not quoted) (*OP-confirmed*, 1) | Yes | Partly |
| 20 | **Stretched / true-stretched resolution broken (patch 13.01), black bars / Fill not working, doubled saturation** `stretched-resolution-issues` | – | 14 | Create custom resolutions (e.g. 1568x1080, 1280x882) via CRU or GPU control panel; set Fill via game config then use… (*guide*, 2) | Partly | No |
| 21 | **Hard freeze / black screen needing power-button reset, or freeze/stutter, linked to USB selective suspend,…** `usb-selective-suspend-freeze` | – | 13 | Power Options > Advanced > USB settings > USB selective suspend = Disabled (or move keyboard/mouse to other USB ports) (*guide*, 2) | Yes | Yes |
| 22 | **Display misc: monitor disabled/not shown, wrong (4K) launch resolution from NVIDIA app optimization,…** `display-misc-blur-wrong-res` | – | 11 | Re-enable the monitor in Device Manager (*OP-confirmed*, 1) | Yes | Partly |
| 23 | **Low GPU usage / FPS drops after the UE5 patch on laptops and new PCs (wrong GPU, NVIDIA app frame cap/V-Sync,…** `low-gpu-usage-after-ue5-patch` | – | 11 | Update or clean install GPU driver (NVIDIA 581.80/581.94 beta or hotfix), DDU if Windows Update installed a driver (*OP-confirmed*, 3) | Yes | Partly |
| 24 | **No audio / broken voice chat / crackling from wrong per-app output, exclusive mode, ExitLag dual routing, USB…** `audio-device-config-issues` | – | 10 | Win+I > System > Sound > Advanced > App volume and device preferences: check output device used by Valorant. (*OP-confirmed*, 1) | Yes | Partly |
| 25 | **Freezes, stutter, FPS drops or black screen after updates fixed by running RiotClientServices.exe as…** `riot-client-not-elevated` | – | 10 | Right-click RiotClientServices.exe (Riot Client root folder, not VALORANT exe) -> Properties -> Compatibility -> Run as… (*OP-confirmed*, 4) | Yes | Yes |

## Problems by category

### FPS / stutter / performance (15 problems,213 threads)

- **Periodic micro-stutter / freezes every few seconds caused by Realtek 2.5GbE / PCIe GbE controller driver conflicting with Vanguard** (`realtek-2-5gbe-nic-stutter`, 76 threads)
  - Fix (OP-confirmed, 19): Device Manager > Network adapters > disable the Realtek PCIe 2.5GbE controller (or unused onboard LAN) when using Wi-Fi; multiple OPs confirmed immediate fix. If using Ethernet, disable the wireless adapter instead or…
  - Fix (OP-confirmed, 9): Device Manager > Network adapters > right-click 'Realtek Gaming 2.5GbE Family Controller' (or Realtek PCIe 2.5GbE) > Disable (not uninstall). Use Wi-Fi or another NIC meanwhile. Re-enable if it does not help.
  - Detect: realtek_nic (high); realtek_events (high); multi_nic (high); nic_power (medium); hags_windowed (low)
  - Auto-fix: **yes**, risk low. If the Realtek adapter is not the active connection: Disable-NetAdapter on it (reversible). If it is the active connection: offer to disable EEE/Green Ethernet/Interrupt Moderation and point to the…
- **Stutter / FPS drops / freezes / loading hangs caused by overlay or hooking software (RTSS, Overwolf/trackers, Discord/NVIDIA/Radeon overlay, Game Bar, Wallpaper Engine, Premier Opinion, ASUS ScreenXpert, TikTok Live Studio) - strongly after Oct 5 2026 patch** (`third-party-hook-overlay-conflict`, 49 threads)
  - Fix (OP-confirmed, 9): Close/uninstall RivaTuner Statistics Server (RTSS) before playing; MSI Afterburner can stay if all monitoring items are unchecked so RTSS does not autorestart, or add Riot client + VALORANT exes to RTSS exclusions
  - Fix (OP-confirmed, 6): Fully close Overwolf and Valorant Tracker (kill from Task Manager) before launching; or only turn off the Overwolf in-game overlay (tracker keeps working) - multiple users confirmed
  - Detect: hook_procs (high); app_crash_module (high); conflict_apps_installed (high)
  - Auto-fix: **partial**, risk low. List detected hooking processes/services and offer to close them for the session (Stop-Process) or disable the service (Set-Service -StartupType Disabled); uninstall stays manual
- **Low / dropping FPS or laptop freezes from power plan/power mode, battery mode, thermal or power-limit throttling, HDD install, low/single-channel RAM, or a BIOS change capping CPU at ~800 MHz** (`power-plan-throttling-low-fps`, 21 threads)
  - Fix (OP-confirmed, 1): Issue resolved after Microsoft Store outage ended and Lenovo Vantage could be installed (laptop performance mode).
  - Fix (OP-confirmed, 1): Debloat/optimize Windows 11 (one OP fixed 70-100 fps stutter)
  - Detect: power_plan (high); thermals (low); ram_config (high); disk_type (high); oem_tools (low)
  - Auto-fix: **partial**, risk low. Switch to High performance / Best performance power plan (powercfg /setactive), warn when on battery, report HDD/RAM/throttle findings; cooling and BIOS reverts are manual
- **Shader compilation / shader-cache stutter after fresh install, driver or GPU change (UE5/DX11), and AMD/NVIDIA driver features (Anti-Lag, AFMF, FSR, RTSS) interfering** (`shader-cache-compilation-stutter`, 16 threads)
  - Fix (OP-confirmed, 1): Use SCSKiller: Library tab -> add game by selecting VALORANT.exe (not Win64-Shipping), clear shader cache, then Compile; author reported smoother frames
  - Fix (OP-confirmed, 1): Nvidia Control Panel > Adjust image settings > Balanced/Best performance (OP: FPS improved on Ryzen 5600 + RTX 5060)
  - Detect: shader_cache (medium); gpu_driver (medium); hook_procs (medium); amd_settings (low); ram_config (low)
  - Auto-fix: **partial**, risk low. Explain warm-up (play Range/matches); optionally clear DirectX shader cache with consent; driver-setting changes are manual
- **Low GPU usage / FPS drops after the UE5 patch on laptops and new PCs (wrong GPU, NVIDIA app frame cap/V-Sync, power plan, driver)** (`low-gpu-usage-after-ue5-patch`, 11 threads)
  - Fix (OP-confirmed, 3): Update or clean install GPU driver (NVIDIA 581.80/581.94 beta or hotfix), DDU if Windows Update installed a driver
  - Fix (OP-confirmed, 1): Turn off V-Sync, in NVIDIA app set Valorant max frame rate, make sure it runs on the RTX GPU not iGPU; follow AMDHelp guide steps 9, 10, 11-NV
  - Detect: gpu_pref (high); power_plan (high); gpu_driver (medium); nv_3d_settings (low)
  - Auto-fix: **partial**, risk low. Set high-performance GPU preference and High performance power plan; driver update/DDU is guide-only
- **Freezes, stutter, FPS drops or black screen after updates fixed by running RiotClientServices.exe as administrator** (`riot-client-not-elevated`, 10 threads)
  - Fix (OP-confirmed, 4): Right-click RiotClientServices.exe (Riot Client root folder, not VALORANT exe) -> Properties -> Compatibility -> Run as administrator; fully close Riot Client incl. tray and relaunch
  - Fix (guide, 2): Run only the Riot Client elevated, not VALORANT-Win64-Shipping.exe (setting shipping exe to admin does not launch)
  - Detect: riot_elevation (high)
  - Auto-fix: **yes**, risk low. Add RUNASADMIN compatibility flag for RiotClientServices.exe (not for VALORANT-Win64-Shipping.exe) and restart the client
- **FPS decays over time or counter shows half of cap (windowed fullscreen, new NVIDIA driver, patch regression)** (`fps-decay-over-time`, 7 threads)
  - Fix (OP-confirmed, 1): Switch from Windowed Fullscreen to Fullscreen in video settings
  - Fix (OP-confirmed, 1): Roll back to previous NVIDIA Game Ready driver
  - Detect: gpu_driver (medium); hook_procs (medium); thermals (low)
  - Auto-fix: **partial**, risk low. Suggest Fullscreen mode; driver rollback guided
- **FPS lower than benchmarks: CPU-bound game, FPS caps (gameplay/menu/background), XMP/EXPO off, mismatched RAM** (`fps-lower-than-expected-caps`, 7 threads)
  - Fix (OP-confirmed, 2): Set menu FPS cap to 30 (author confirmed it removed lag; another user suggested it for frame drops)
  - Fix (suggested only, 5): Expect lower FPS in real matches; enable XMP/EXPO, High performance plan, Resizable BAR, lock FPS cap and Reflex
  - Detect: ram_config (high); power_plan (medium); valorant_config (low)
  - Auto-fix: **partial**, risk low. Report XMP/EXPO and RAM config; suggest menu FPS cap 30 and caps check; BIOS XMP is manual
- **Frame-time / latency spikes (Game Latency CPU, Render Latency GPU) from packet loss/jitter, NVIDIA driver, Discord** (`frametime-latency-spikes`, 4 threads)
  - Fix (guide, 2): Close Discord or set its priority to lowest in Task Manager (helped some).
  - Fix (OP-confirmed, 1): Fresh Windows install fixed it for OP.
  - Detect: ping_route (medium); gpu_driver (medium); hook_procs (medium)
  - Auto-fix: **partial**, risk low. Report Discord/overlays and network jitter; driver rollback guided
- **Stutter in gunfights on Intel hybrid CPUs (game on E-cores / P-cores saturated by background apps)** (`intel-hybrid-cpu-ecore-stutter`, 3 threads)
  - Fix (guide, 3): Task Manager > Details: set VALORANT-Win64-Shipping, VALORANT.exe, vgc.exe to High priority with affinity limited to P-cores; move other apps to E-cores; use ReservedCpuSets (valleyofdoom) to reserve P-cores then reboot
  - Detect: hybrid_cpu (high); priority (medium)
  - Auto-fix: **partial**, risk medium. Per-session affinity/priority for VALORANT-Win64-Shipping, VALORANT.exe, vgc.exe to P-cores (resets each launch)
- **FPS drop / lag after patch 13.01 (July 2026): corrupt install, Vanguard CPU, non-paged pool leak** (`patch-13-01-fps-drop`, 3 threads)
  - Fix (OP-confirmed, 2): Redownload/reinstall Valorant and Vanguard (Revo Uninstaller full wipe); OPs confirmed fps recovered
  - Fix (OP-confirmed, 1): Memory leak found with RAMMap (non-paged pool 90-95%) - clean Windows install fixed it
  - Detect: vgc_cpu (medium); standby_mem (medium); hook_procs (medium)
  - Auto-fix: **partial**, risk low. Suggest Repair/reinstall of Valorant+Vanguard; report non-paged pool size
- **Stutter from standby memory list not being released** (`standby-memory-stutter`, 2 threads)
  - Fix (guide, 1): Run ISLC (Wagnardsoft) as admin, 'Free memory is lower than' = half of RAM, 1000ms polling, start with Windows
  - Fix (suggested only, 1): Check Resource Monitor free vs standby memory
  - Detect: standby_mem (medium); ram_config (medium)
  - Auto-fix: **partial**, risk low. Report standby/free memory; ISLC suggestion is guide-only
- **Microstutter on alt-tab / Graphics Quality after Windows update KB5101650 (14 July 2026) or fresh Windows install** (`windows-update-kb5101650-microstutter`, 2 threads)
  - Fix (suggested only, 2): Uninstall Windows update KB5101650 (Settings > Windows Update > Update history > Uninstall updates); users report microstutter removed
  - Fix (suggested only, 1): Disable MPO via NVIDIA registry script (contested by others)
  - Detect: hotfix (high); realtek_events (medium); fso_mpo (medium)
  - Auto-fix: **partial**, risk medium. Detect KB5101650 and offer wusa /uninstall /kb:5101650 with consent
- **Valorant freezes in lobby with MSI BIOS X3D Gaming Mode enabled (disputed)** (`msi-x3d-game-mode-lobby-freeze`, 1 threads)
  - Fix (guide, 1): Disable X3D gaming mode in MSI BIOS
  - Detect: oem_tools (low); hybrid_cpu (low)
  - Auto-fix: **no**, risk high. Guide: disable X3D Gaming Mode in MSI BIOS
- **FPS stutter fixed by disabling Riot Client hardware acceleration** (`riot-client-hardware-acceleration`, 1 threads)
  - Fix (guide, 1): Turn off hardware acceleration in Riot Client
  - Detect: riot_folders (low)
  - Auto-fix: **partial**, risk low. Guide user to Riot Client settings to turn off hardware acceleration

### Crashes, freezes, BSOD (10 problems,105 threads)

- **Black screen / stuck loading bar (~10%) on launch caused by Nahimic (A-Volute) audio service injecting into the game** (`nahimic-audio-service-launch-hang`, 29 threads · codes: VAN 81)
  - Fix (OP-confirmed, 19): Stop and disable the 'Nahimic service' (services.msc -> Nahimic Service -> Startup type Disabled -> Stop; or Task Manager Services tab -> stop; or msconfig Services tab untick) then relaunch/restart PC
  - Fix (OP-confirmed, 6): Fully uninstall Nahimic and restart (needed in some cases where only stopping did not clear the 10% hang); Windows Update may reinstall it
  - Detect: nahimic (high); app_crash_module (high); vgc_service (medium)
  - Auto-fix: **yes**, risk low. Stop-Service + Set-Service -StartupType Disabled for the Nahimic service (reversible); warn that Windows Update/OEM tools may reinstall it
- **Whole-PC freeze / reboot / Kernel-Power 41 / LiveKernelEvent 141 while playing (mostly new AMD builds, often unresolved)** (`whole-pc-freeze-kernel-power-41`, 21 threads)
  - Fix (OP-confirmed, 1): Disable Realtek ethernet/unused adapter (fixed LiveKernelEvent 141)
  - Fix (OP-confirmed, 1): Whitelist Valorant, Riot Client, Vanguard in Windows Firewall/antivirus (Riot support)
  - Detect: kp41_lke (high); realtek_nic (medium); gpu_driver (medium); usb_ss (medium); igpu_state (medium); bios_age (medium); virtualization (low); hags_windowed (low)
  - Auto-fix: **partial**, risk medium. Collect evidence (event IDs, driver versions) and run the sub-checks; apply the safe items (USB selective suspend off, unused Realtek NIC off); driver rollback/BIOS update are guide-only
- **PC reboots instantly (no BSOD), whole-PC crash/freeze or vgk.sys BSOD on Valorant launch due to outdated motherboard BIOS** (`outdated-bios-reboot-vgk-bsod`, 19 threads · codes: VAN 57)
  - Fix (OP-confirmed, 13): Update motherboard BIOS to latest from manufacturer (flash drive/EZ Flash etc.), back up/disable BitLocker first; afterwards re-enable Secure Boot, TPM/fTPM, UEFI (not CSM) because BIOS update resets them
  - Fix (OP-confirmed, 1): After BIOS update also run sfc /scannow, DISM /Online /Cleanup-Image /RestoreHealth, DDU GPU driver reinstall, reinstall Vanguard
  - Detect: bios_age (high); kp41_lke (high); minidump (medium)
  - Auto-fix: **no**, risk high. Guide only: show BIOS version/date and board model, link vendor support page; remind to back up BitLocker key and re-enable Secure Boot/TPM afterwards
- **Hard freeze / black screen needing power-button reset, or freeze/stutter, linked to USB selective suspend, peripherals on chipset USB, MSI Afterburner or old Bluetooth (WIDCOMM) software** (`usb-selective-suspend-freeze`, 13 threads)
  - Fix (guide, 2): Power Options > Advanced > USB settings > USB selective suspend = Disabled (or move keyboard/mouse to other USB ports)
  - Fix (OP-confirmed, 1): Exit MSI Afterburner
  - Detect: usb_ss (high); hook_procs (medium); kp41_lke (medium); realtek_events (medium); usb_devices (low)
  - Auto-fix: **yes**, risk low. powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 (and DC) + powercfg /setactive SCHEME_CURRENT
- **PC crash/BSOD/restart (PAGE_FAULT_IN_NONPAGED_AREA, KMODE) or boot failure after Vanguard reboot: stale 392667600.dat, Fast Startup, corrupted Windows files** (`fast-startup-stale-dat-bsod`, 6 threads)
  - Fix (guide, 1): Delete C:\Windows\392667600.dat, disable Fast Startup (Power buttons > Turn on fast startup unchecked), restart, run Valorant as admin (Riot support approved)
  - Fix (suggested only, 1): Boot to recovery cmd and run chkdsk and sfc /scannow /offbootdir=X:\ /offwindir=D:\Windows to repair corrupted files (one user still stuck)
  - Detect: stale_dat (high); pending_reboot (high); minidump (medium); system_files (medium)
  - Auto-fix: **partial**, risk medium. Delete C:\Windows\392667600.dat and set HiberbootEnabled=0 (disable Fast Startup), then reboot; offline sfc/chkdsk is guide-only
- **System freeze/hang on AMD Ryzen desktops when iGPU is disabled in BIOS or present without a driver (unknown display device)** (`igpu-disabled-or-driverless-freeze`, 6 threads)
  - Fix (OP-confirmed, 1): Disable iGPU in BIOS (confirmed by OP, RX 7900XT + 7950X3D)
  - Fix (OP-confirmed, 1): Install the missing AMD iGPU/chipset drivers so no device shows a yellow warning in Device Manager (confirmed by OP and another user; works with iGPU on or off)
  - Detect: igpu_state (high); pnp_errors (high); hook_procs (medium); kp41_lke (medium); secure_boot (low)
  - Auto-fix: **partial**, risk medium. If iGPU shows as driverless/unknown: point to AMD chipset/iGPU driver package. Enabling iGPU in BIOS (Integrated Graphics = Auto) is guide-only
- **Game freezes or crashes on click/lobby (display mode, corrupted Windows user profile, Bluetooth hook btmmhook.dll)** (`freeze-on-click-lobby-misc`, 4 threads)
  - Fix (OP-confirmed, 1): Press F11 to set display mode to fullscreen/windowed.
  - Fix (OP-confirmed, 1): Log in with a different Windows user account.
  - Detect: app_crash_module (high); hook_procs (medium); igpu_state (low)
  - Auto-fix: **partial**, risk medium. Detect btmmhook.dll / old Bluetooth software and offer uninstall; profile change is manual
- **BSOD KERNEL_SECURITY_CHECK_FAILURE (0x139) / vgk.sys on Windows Insider 26H1 builds, or after Linux dual boot with Secure Boot off** (`insider-build-vgk-bsod`, 3 threads)
  - Fix (guide, 3): Boot recovery/safe mode and delete C:\Program Files\Riot Vanguard\vgk.sys (or whole folder / disable via Autoruns) instead of reinstalling Windows
  - Fix (OP-confirmed, 1): Enable Secure Boot + TPM 2.0 and reinstall Vanguard (OP fix for vgk.sys 0x7E after Linux dual boot)
  - Detect: win_build (high); minidump (high); secure_boot (medium)
  - Auto-fix: **no**, risk high. Guide: boot recovery/safe mode and remove C:\Program Files\Riot Vanguard\vgk.sys, move to stable Windows build
- **Valorant black screen / CreateProcess 2e4 / unspecified launch failures (no fix reported)** (`unexplained-launch-failure`, 3 threads)
  - Fix (suggested only, 1): No fix reported
  - Detect: app_crash_module (medium)
  - Auto-fix: **no**, risk low. Collect logs only
- **BSOD PAGE_FAULT_IN_NONPAGED_AREA with WdFilter.sys while playing** (`wdfilter-bsod`, 1 threads)
  - Fix (OP-confirmed, 1): Safe mode: uninstall Riot/Valorant/Vanguard, clean leftover drivers via PowerShell, reset Windows Security, reinstall
  - Detect: minidump (high)
  - Auto-fix: **no**, risk high. Guide: safe-mode uninstall of Riot/Vanguard, reset Windows Security, reinstall

### TPM / Secure Boot / HVCI / BIOS (9 problems,103 threads)

- **Secure Boot cannot be enabled or PC won't boot after enabling it (Secure Boot Violation, 'Invalid signature', boot loop to BIOS, keys 'External', outdated BIOS, corrupt EFI files, MBR disk)** (`secure-boot-enable-boot-failure`, 32 threads · codes: VAN 9003, VAN RESTRICTION)
  - Fix (OP-confirmed, 7): Update the BIOS (install intermediate versions step by step if very old); user's 2017 BIOS had a TPM/Secure Boot bug fixed in newer BIOS
  - Fix (OP-confirmed, 5): In BIOS disable Secure Boot, restore factory keys, answer No when prompted to exit, re-enable Secure Boot, F10 save (OP confirmed on Gigabyte B550 UD AC)
  - Detect: uefi_gpt (high); secure_boot (high); sb_keys (high); bios_age (high); tpm (medium)
  - Auto-fix: **no**, risk high. Guide only: pre-flight check before the user touches BIOS (GPT? UEFI? CA 2023 cert? BIOS age?), recommend mbr2gpt / BIOS update / factory keys; warn about BitLocker
- **Vanguard requires Memory Integrity (HVCI): VAN RESTRICTION / VAN 9005 / VAN 57/84; HVCI won't turn on or stay on, blocked by incompatible drivers or virtualization off; toggling causes boot issues** (`hvci-memory-integrity-required`, 27 threads · codes: VAN RESTRICTION, VAN 185, VAN 57, VAN 84, VAN 184, VAN 9005, VAN 9002)
  - Fix (OP-confirmed, 8): Windows Security > Device security > Core isolation > turn on Memory Integrity; reboot; Valorant 'repair' if it reports corrupted files
  - Fix (OP-confirmed, 2): Remove/update incompatible driver blocking Memory Integrity: boot safe mode and delete gdrv3.sys (Gigabyte) / update or reinstall Realtek audio driver, then enable Memory Integrity
  - Detect: hvci (high); virtualization (high); hvci_incompat_drivers (high); win_build (high); secure_boot (medium); tpm (medium)
  - Auto-fix: **partial**, risk high. Report HVCI/virtualization/incompatible drivers; enable Memory Integrity via HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity Enabled=1 only after the…
- **VAN 9003 / VAN RESTRICTION 1 / VAN 9090: 'UEFI Secure Boot required' even though it looks enabled (CSM/Legacy, keys missing, Setup mode, toggle not applied, Lenovo LOQ BIOS bug)** (`secure-boot-not-detected-van9003`, 19 threads · codes: VAN 9003, VAN 9090, VAN RESTRICTION 1, VAN 9001, VAN RESTRICTION)
  - Fix (OP-confirmed, 2): In BIOS Secure Boot > Key Management: 'Install default/factory keys' or 'Reset to factory keys', then verify msinfo32 Secure Boot State = On
  - Fix (OP-confirmed, 1): Old MSI B250M board: check disk is GPT (diskpart > list disk, asterisk in GPT column), UEFI only (not UEFI+Legacy), enable TPM/fTPM; Secure Boot keys/Fast Boot options were hidden under Advanced options - OP solved it…
  - Detect: secure_boot (high); uefi_gpt (high); sb_keys (high); tpm (high); bios_age (medium)
  - Auto-fix: **no**, risk high. Guide only: report exact state (Secure Boot off / Setup mode / CSM / MBR) and show the matching BIOS steps (disable CSM, Restore Factory Keys, re-toggle Secure Boot)
- **VAN 9001 / 9003 / Restriction: TPM 2.0 missing (old boards, TPM 1.2) or not enabled; enforced on Win10 only for flagged accounts** (`tpm2-missing-or-unmet`, 8 threads · codes: VAN 9001, VAN RESTRICTION, VAN 9003, VAN RESTRICTION 2)
  - Fix (guide, 1): On Windows 10 disable VBS (virtualization-based security) if it blocks; enable TPM/fTPM in BIOS
  - Fix (guide, 1): Add a TPM 2.0 module on motherboard header (e.g. JLPC1 on Huananzhi X99), enable Security Device Support, set CSM Disabled/UEFI, delete Secure Boot variables, enroll factory default keys, enable Secure Boot; verify…
  - Detect: tpm (high); secure_boot (high); win_build (medium); hvci (low)
  - Auto-fix: **no**, risk high. Guide: enable fTPM/PTT in BIOS or add a TPM 2.0 module; nothing to auto-fix
- **VAN: STATUS_SB_POLICY / VAN Restriction 1 - Secure Boot policy could not be verified (May-June 2026 wave, Riot-side / temporary HWID restriction)** (`secure-boot-policy-wave-2026`, 7 threads · codes: VAN STATUS_SB_POLICY, VAN RESTRICTION 1)
  - Fix (guide, 2): Contact Riot support to check for a ~1 month hardware/HWID restriction; waiting until the stated end date made the game work again (triggers cited: old BIOS, VMware/VPN/third-party software, outdated secure boot keys)
  - Fix (OP-confirmed, 1): Wait for Riot's small client patch (25.3 MB update on May 19 fixed it for one Surface Book 2 user) and re-launch; open a Riot support ticket naming the device model so they patch it
  - Detect: sb_keys (high); secure_boot (high); riot_status (low)
  - Auto-fix: **no**, risk low. Report Secure Boot cert status; advise waiting for Riot patch / support ticket
- **Stutter / device chime / freezes with TPM Event ID 14/17 (AMD fTPM, AM5 boards)** (`ftpm-event-14-17-stutter`, 4 threads)
  - Fix (guide, 1): Disable TPM 2.0 in BIOS on Windows 10 (Win10 does not require TPM unless account is flagged) - guide author reports stutter gone; many commenters warn Win10 is unsupported and other users still had stutter
  - Fix (suggested only, 3): Update BIOS/AGESA containing TPM firmware update (B550 + 5800X reported fixed); set BIOS TPM to Auto instead of fTPM; or install a discrete TPM module
  - Detect: tpm_events (high); tpm (medium); bios_age (medium); usb_ss (medium)
  - Auto-fix: **partial**, risk medium. Disable USB selective suspend (safe); BIOS/AGESA update and TPM changes are guide-only
- **VAN IOMMU restriction / 'IOMMU must be enabled': muted audio, BSOD loop, locked OEM BIOS (HP Victus)** (`iommu-restriction`, 3 threads · codes: VAN IOMMU)
  - Fix (OP-confirmed, 1): Upgrade to Windows 11 (fixed it), but reinstalling the WiFi card driver brought it back; run on Ethernet
  - Fix (OP-confirmed, 1): Install audio drivers from hardware vendor site, create a new sound output and set it as default (user unsure which step helped)
  - Detect: iommu_dma (high); audio_devices (low); win_build (medium)
  - Auto-fix: **no**, risk high. Guide: enable IOMMU/VT-d/AMD-Vi + SVM in BIOS, update audio/Wi-Fi drivers
- **VAN: STATUS_VMD_9001 - Intel VMD / RST controller conflict (Dell/Alienware)** (`intel-vmd-status-vmd-9001`, 2 threads · codes: VAN STATUS_VMD_9001)
  - Fix (OP-confirmed, 2): Disable VMD in BIOS safely: admin cmd 'bcdedit /set {current} safeboot minimal', reboot (boots Safe Mode), enter BIOS (F2/F12) and turn off VMD, then remove safeboot flag to return to normal boot; avoids BSOD (steps…
  - Fix (suggested only, 1): No fix; commenters concluded it is on Riot's side, OP filed support ticket
  - Detect: vmd (high)
  - Auto-fix: **no**, risk high. Guide: bcdedit /set {current} safeboot minimal, disable VMD in BIOS, boot, remove safeboot flag (risk: unbootable if done wrong)
- **VAN 9101 attestation failure (security violation #0000002) after Feb patch** (`van9101-attestation`, 1 threads · codes: VAN 9101)
  - Detect: win_build (medium); tpm (medium); secure_boot (medium)
  - Auto-fix: **no**, risk low. No fix found; collect build/TPM state for support

### Vanguard (VAN errors, service, conflicts) (15 problems,89 threads)

- **VAN 185 / VAN 5 / VAN 102 / VAL 5 mid-match disconnects and escalating suspensions (network instability, Wi-Fi, DNS/IPv6, firewall, AV, Overwolf, thermals)** (`van185-vanguard-disconnects`, 24 threads · codes: VAN 185, VAL 5, ERROR 43, VAN 5, VAN 102, VAL 102)
  - Fix (OP-confirmed, 4): Set vgc service to Automatic, disable fullscreen optimizations, allow Vanguard through antivirus (earlier fix used by OP of 1vuavg9)
  - Fix (OP-confirmed, 3): Full Windows reinstall/format (Win10), optionally old BIOS
  - Detect: vgc_service (high); firewall_rules (high); av_products (high); hook_procs (medium); wifi (medium); dns_ipv6 (medium); ping_route (medium); thermals (low); pnp_errors (low)
  - Auto-fix: **partial**, risk medium. Set vgc to Automatic, add firewall allow rules for Riot Client/VALORANT/vgc, flush DNS; DNS/IPv6/Teredo changes offered with consent; AV removal manual
- **Corrupted Vanguard (VAN -1 / VAN 1067 / vgk.sys BSOD / restart-required loop / crash at launch / 'Something unusual happened' after VAN 84) needing clean reinstall** (`vanguard-corrupt-install`, 15 threads · codes: VAN 1, VAN 1067, VAN -1, VAN 84)
  - Fix (OP-confirmed, 1): Follow the Vanguard 1067 thread steps (OP of a VAL 43 thread said this fixed it; steps were linked, not quoted)
  - Fix (OP-confirmed, 1): Fully power off/unplug PC, replug, Repair Files in Riot launcher until it repairs (repeated per account)
  - Detect: vgc_service (high); vgk_files (high); minidump (medium); av_products (medium); win_build (low)
  - Auto-fix: **partial**, risk medium. Clean Vanguard reinstall: sc delete vgc, sc delete vgk, reboot, remove C:\Program Files\Riot Vanguard, relaunch Riot Client to reinstall, reboot. Safe-mode registry deletion stays guide-only
- **VAN 289 / VAN 293 / VAL 29 / loading stuck at 20% from VPNs, virtual adapters, Docker/NetLimiter, other anti-cheats (FACEIT, GlobalProtect) or disabled Hyper-V platform features** (`vpn-anticheat-network-filter-conflict`, 10 threads · codes: VAN 289, VAN 293, VAL 29)
  - Fix (OP-confirmed, 1): Uninstall GlobalProtect and FACEIT anti-cheat (and other VPN/anticheat apps that can block Vanguard); OP reported solved after 2-3 games, durability unconfirmed
  - Fix (OP-confirmed, 1): Uninstall VPN software and Docker Desktop; the OP said deleting VPN fixed theirs
  - Detect: virtual_nic_vpn (high); other_anticheat (high); vgc_service (medium); virtualization (medium); av_products (medium)
  - Auto-fix: **partial**, risk medium. Disable detected virtual/VPN adapters (reversible); enable VirtualMachinePlatform/HypervisorPlatform (Enable-WindowsOptionalFeature) with consent; uninstalling VPN/FACEIT is manual
- **Vanguard (vgc.exe) high CPU causing FPS drops; Defender scanning Riot folders; VAN 136 on very old CPUs** (`vgc-high-cpu`, 7 threads · codes: VAN 136)
  - Fix (OP-confirmed, 2): Task Manager > Details > vgc.exe > Set affinity: uncheck CPU 0 and keep a single other core (repeat each launch or use Process Lasso)
  - Fix (OP-confirmed, 1): Clean fans (dust) on gaming laptop
  - Detect: vgc_cpu (high); av_products (medium); thermals (low)
  - Auto-fix: **partial**, risk medium. Add Defender exclusions for Riot Games / Riot Vanguard folders (Add-MpPreference -ExclusionPath) with consent; vgc affinity tweak is per-session
- **'VAN: Incompatible software component detected' / incompatible OEM driver: ene.sys (RGB), se64a.sys (monitor control), mtkwl6ex.sys (MediaTek Wi-Fi)** (`incompatible-driver-component`, 5 threads)
  - Fix (OP-confirmed, 3): Update/replace the RGB software (Razer Synapse 4, motherboard vendor tool) or uninstall it (e.g. Asus Aura).
  - Fix (OP-confirmed, 2): Rename C:\Windows\System32\drivers\ene.sys to ene.sys.old (or .bak); if locked, boot Safe Mode (Shift+Restart) and rename/delete, then restart. One user then got VAN 84 instead.
  - Detect: hvci_incompat_drivers (high); conflict_apps_installed (medium)
  - Auto-fix: **yes**, risk medium. Uninstall/update the owning RGB/monitor software; rename C:\Windows\System32\drivers\ene.sys to .old / sc delete se64a (safe mode if locked). MediaTek driver replacement is guided
- **VAL 79 / VAN 79 / VAN 9001 hang / recurrent VAL 185 or VAN 102: vgc service stopped or set to Manual (also Insider/Canary builds)** (`vgc-service-not-running`, 5 threads · codes: VAL 79, VAN 79, VAN 9001, VAL 185, VAN 102)
  - Fix (OP-confirmed, 2): services.msc -> vgc -> Startup type Automatic and Start the service, then launch VALORANT
  - Fix (OP-confirmed, 1): Roll back from Insider/Canary to stable Windows (OP's conclusion)
  - Detect: vgc_service (high); win_build (medium); tpm_events (medium)
  - Auto-fix: **yes**, risk low. sc config vgc start= auto ; net start vgc (or restart vgc + vgtray.exe without reboot)
- **VAN 57 / VAN 81 / VAN 1067: Vanguard driver (vgk) not loaded until a real reboot (after install or game update)** (`vgk-not-loaded-needs-reboot`, 5 threads · codes: VAN 57, VAN 81, VAN 1067)
  - Fix (OP-confirmed, 1): Restart PC after each game update (VAN 1067)
  - Fix (guide, 1): Close all Riot processes; ipconfig /flushdns, /release, /renew; delete C:\Windows\vgkbootstatus.dat; uninstall Vanguard and reboot; let Riot Client reinstall Vanguard then REBOOT before pressing Play (do not press Play…
  - Detect: pending_reboot (high); vgk_files (high); vgc_service (high)
  - Auto-fix: **yes**, risk low. Delete C:\Windows\vgkbootstatus.dat if present and perform a full restart (shutdown /r /t 0, not Fast Startup shutdown)
- **'Something unusual happened' / nothing happens on launch: registry tampering (IFEO DevOverrideEnable, cracked software), Vanguard restriction requirements** (`launch-fails-something-unusual`, 4 threads)
  - Fix (OP-confirmed, 1): Cracked Vegas editing software had modified a registry value; after fixing it Valorant launched.
  - Fix (OP-confirmed, 1): Satisfy Vanguard Restrictions article (BIOS/Windows items); OP changed several things and it worked.
  - Detect: ifeo (high); vgc_service (medium); hvci (medium); secure_boot (medium)
  - Auto-fix: **partial**, risk medium. Detect IFEO DevOverrideEnable / Debugger values and offer to reset (DevOverrideEnable=0) with registry backup
- **Third-party antivirus / security software (Trend Micro, Norton) blocking Vanguard (VAL 5, VAN 102, lag)** (`third-party-antivirus-conflict`, 4 threads · codes: VAL 5, VAN 102)
  - Fix (OP-confirmed, 1): Add exceptions in Trend Micro for ~6 files (3 Vanguard files, Riot Games folder, 2 Valorant files) - fixed after Riot staff log review.
  - Fix (OP-confirmed, 1): Uninstall third-party antivirus (e.g. Norton) leaving Windows Defender (with Realtek fix).
  - Detect: av_products (high); vgc_service (medium)
  - Auto-fix: **partial**, risk low. Detect AV product; add Defender exclusions; third-party AV exceptions/uninstall are guided
- **Hardware-level / account VAN RESTRICTION (incl. VAN 152) that cannot be removed (~30 days)** (`van-restriction-hwid`, 3 threads · codes: VAN RESTRICTION, VAN 152)
  - Fix (OP-confirmed, 1): OP says they fixed it themselves (method not described)
  - Fix (suggested only, 2): Wait until restriction expires; submit support tickets with proof (no confirmed removal)
  - Detect: secure_boot (medium); tpm (medium); hvci (medium); dualboot (low)
  - Auto-fix: **no**, risk low. Nothing to fix locally; show requirement status and point to Riot support
- **VAN 1 / UEFI Secure Boot verification failure with dual boot (Windows Boot Manager on another drive / GRUB first)** (`dual-boot-secure-boot-van1`, 2 threads · codes: VAN 1)
  - Fix (OP-confirmed, 1): In BIOS boot order put Windows Boot Manager before Linux/GRUB
  - Fix (guide, 1): Install the Windows boot loader onto the Windows drive: shrink ~300MB on the Windows disk, disconnect the Linux drive, boot Windows install media > Repair > cmd, diskpart (list disk, select disk, create EFI partition)…
  - Detect: dualboot (high); secure_boot (high)
  - Auto-fix: **no**, risk high. Guide: put Windows Boot Manager first or create an ESP on the Windows disk
- **VAN 185 / VAN 136 / Discord freezing after forcing VALORANT/Discord priority to High/Realtime** (`high-process-priority-van136-185`, 2 threads · codes: VAN 185, VAN 136, VAN 216, VAN -81)
  - Fix (OP-confirmed, 1): Set VALORANT-Win64-Shipping.exe (and Discord) priority to Normal/Above normal instead of High (Task Manager -> Details -> Set priority); OP confirmed Normal fixed everything
  - Fix (guide, 1): Set VALORANT and Discord priority back to Above normal (avoid High/Realtime)
  - Detect: priority (high)
  - Auto-fix: **yes**, risk low. Reset VALORANT and Discord priority to Normal/Above normal and remove IFEO PerfOptions CpuPriorityClass overrides
- **Missing Windows system environment variables break Vanguard log export ('Failed to create zip archive')** (`missing-environment-variables`, 1 threads)
  - Fix (OP-confirmed, 1): Restore missing system environment variables (System Properties > Environment Variables), reboot
  - Detect: env_vars (high)
  - Auto-fix: **partial**, risk medium. Detect missing TEMP/TMP/ComSpec/Path and show the defaults; writing machine env vars only with consent
- **VAN restriction / error 5 / 102 kick caused by Razer Synapse game escalation service shutting down VGC** (`razer-synapse-escalation-kick`, 1 threads · codes: VAN 102)
  - Fix (guide, 1): Disable Razer Synapse's game escalation service and reset Valorant profile cache (author; others without Synapse still affected)
  - Detect: other_anticheat (medium); vgc_service (medium)
  - Auto-fix: **partial**, risk low. Offer to disable the Razer Synapse game escalation service
- **Vanguard blocks chkdsk / file system repair at boot (dirty NTFS flag)** (`vanguard-blocks-chkdsk`, 1 threads)
  - Fix (guide, 1): Exit Vanguard from tray, or uninstall Vanguard and reboot so Windows can repair; run chkdsk C: /f /r manually then; Riot reinstalls Vanguard later. Alternatively boot Safe Mode. Run TRIM afterwards
  - Detect: disk_dirty (high)
  - Auto-fix: **partial**, risk medium. Detect dirty bit; guide: exit Vanguard from tray / uninstall, run chkdsk C: /f /r, reinstall

### Network / ping (6 problems,66 threads)

- **Packet loss / jitter / high or constant ping from Wi-Fi, router placement, stale TCP stack, bufferbloat, NIC buffers or corrupt install** (`local-network-packet-loss-jitter`, 30 threads)
  - Fix (OP-confirmed, 5): Switch from Wi-Fi to Ethernet cable.
  - Fix (OP-confirmed, 2): ipconfig /release /renew /flushdns, netsh winsock reset (temporary)
  - Detect: wifi (high); ping_route (high); dns_ipv6 (medium); tcp_stack (medium); nic_power (low)
  - Auto-fix: **partial**, risk medium. ipconfig /flushdns, /release, /renew and netsh winsock reset with consent; advise Ethernet/5 GHz; TCP congestion tweaks guided
- **Sudden high ping / packet loss / chat service unavailable from ISP routing or peering issues (regional, undersea cable) - WARP/VPN workaround** (`isp-routing-high-ping`, 26 threads)
  - Fix (OP-confirmed, 8): Install and enable Cloudflare WARP (1.1.1.1 app, WARP mode) while playing; several OPs confirmed it restored normal ping/removed packet loss. Described as temporary until ISP/Riot fixes routing; ping may rise a few ms…
  - Fix (OP-confirmed, 3): Wait: issue self-resolved after hours/days (Frankfurt, German ISP routing 18 Sept, reported to Riot support)
  - Detect: ping_route (high); virtual_nic_vpn (low); wifi (medium)
  - Auto-fix: **no**, risk low. Diagnose only: compare ping/traceroute with and without WARP, tell the user it's ISP-side and what to send to ISP/Riot
- **Download stuck / client errors (VAL 62, VAN 102/VAL 5) from ISP blocking, firewall blocks or remapper/overlay apps; region-unavailable** (`network-exceptions-download-stuck`, 5 threads · codes: VAL 62, VAN 102, VAL 5)
  - Fix (OP-confirmed, 2): Use a free VPN/Cloudflare WARP (WRAP VPN, Cloudflare) - fixed VAL 62 and stuck download
  - Fix (OP-confirmed, 1): Valorant unavailable in Iran: used VPN to log in
  - Detect: firewall_rules (high); ping_route (medium); dns_ipv6 (medium); conflict_apps_installed (medium)
  - Auto-fix: **partial**, risk medium. Add Windows Firewall allow rules for Riot Client, Vanguard and VALORANT; ipconfig /flushdns; Winsock/TCP reset with consent; VPN/WARP is user choice
- **Desync/teleporting, VAN 72/185 or ping spikes from two active adapters (Wi-Fi connecting alongside Ethernet, external antenna) or Vanguard/Riot Client efficiency mode** (`dual-network-adapter-desync`, 3 threads · codes: VAN 72, VAN 185)
  - Fix (OP-confirmed, 1): Disable the unused/weaker Wi-Fi adapter in Device Manager and fix adapter priority (OP confirmed)
  - Fix (guide, 1): Disable Wi-Fi adapter in Device Manager when playing on Ethernet, restart (no errors for a week)
  - Detect: multi_nic (high); wifi (medium)
  - Auto-fix: **yes**, risk low. Disable the unused adapter (Disable-NetAdapter) or set interface metrics, reversible
- **Wi-Fi throughput collapses while Valorant runs (Lenovo Vantage)** (`lenovo-vantage-wifi-collapse`, 1 threads)
  - Fix (OP-confirmed, 1): Install the newer Lenovo Vantage update; OP confirmed fix
  - Detect: oem_tools (medium)
  - Auto-fix: **no**, risk low. Flag Lenovo Vantage version; guide update
- **Packet loss / rubber-banding on EU servers that changed with NVIDIA driver version (unconfirmed)** (`nvidia-driver-packet-loss`, 1 threads)
  - Fix (OP-confirmed, 1): Roll back NVIDIA driver (OP: 595.79 stable; others cited 576.80) - OP later said problem returned and deleted post
  - Detect: gpu_driver (medium); ping_route (medium)
  - Auto-fix: **no**, risk medium. Report driver version; rollback is guide-only

### Install / update / corrupt files (9 problems,61 threads)

- **Vanguard never installs / 'Preparing download' / Update-Play loop / 'Installing Vanguard' loop / vgk.sys or vgc missing (VAN 83)** (`vanguard-install-loop`, 18 threads · codes: VAN 83)
  - Fix (OP-confirmed, 3): Admin cmd: sc delete vgc ; sc delete vgk ; reboot ; delete C:\Program Files\Riot Vanguard ; reopen Riot Client and reinstall Vanguard, reboot
  - Fix (OP-confirmed, 2): Reinstall the game / delete Valorant and Riot Games and reinstall (loop, Section suspension misfire)
  - Detect: vgk_files (high); vgc_service (high); riot_folders (medium); av_products (medium); win_build (medium); disk_dirty (low)
  - Auto-fix: **yes**, risk medium. Remove leftover services (sc delete vgc / vgk), reboot, delete C:\Program Files\Riot Vanguard, relaunch Riot Client; check Defender protection history for blocked Riot items
- **Riot Client update/install fails or loops ('Something went wrong', endless 'Updating', fatal error after update, version mismatch, outdated installer)** (`riot-client-update-stuck`, 17 threads · codes: VAL 43)
  - Fix (OP-confirmed, 3): Riot client profile > Repair; or restart PC; or disable auto updates; or exit Vanguard in tray, reboot
  - Fix (OP-confirmed, 2): Uninstall Bonjour app (conflicts with Vanguard) for VAL 43
  - Detect: riot_folders (high); mdns (medium); pending_reboot (medium)
  - Auto-fix: **yes**, risk low. Close Riot processes and delete %LOCALAPPDATA%\Riot Games\Riot Client (cache/login only), then Repair; full clean reinstall is guided
- **Crash on Graphics Quality, stuck/auto-firing or non-working inputs, scroll wheel not working after patch - corrupt %LOCALAPPDATA%\VALORANT\Saved config or agent control profiles** (`valorant-config-corrupt`, 9 threads)
  - Fix (OP-confirmed, 5): Close game and client; delete all files in %LOCALAPPDATA%\VALORANT\Saved\Config (delete again if they reappear); start custom game 5 min; Settings > Controls > Reset to game defaults (Riot support steps).
  - Fix (OP-confirmed, 1): Riot support clean reinstall: delete Riot Client folder in C:\Riot Games, delete %localappdata%\Riot Games, uninstall and reinstall (game was installed on a non-C drive); OP confirmed it worked
  - Detect: valorant_config (high); riot_folders (medium)
  - Auto-fix: **yes**, risk low. Back up then delete %LOCALAPPDATA%\VALORANT\Saved\Config (user loses local settings; crosshair codes should be saved first)
- **Valorant won't start from desktop shortcut / loading loops because Riot Client is hidden in tray, broken shortcut args, third-party firewall or Vanguard not initialized (VAN 57)** (`launch-shortcut-tray-failure`, 8 threads · codes: VAN 57)
  - Fix (OP-confirmed, 1): Launch via Riot Client from system tray; right-click tray Riot Client > Exit before uninstalling
  - Fix (OP-confirmed, 1): Reinstall Vanguard, repair, then set Memory Integrity on
  - Detect: riot_folders (high); vgc_service (medium); firewall_rules (medium)
  - Auto-fix: **partial**, risk low. Detect hidden RiotClientServices process and recreate shortcut to RiotClientServices.exe --launch-product=valorant --launch-patchline=live
- **Corrupted Windows system files causing post-update stutter, or corrupted game files crashing during matches** (`corrupt-system-or-game-files`, 3 threads)
  - Fix (guide, 1): Run sfc /scannow in admin Command Prompt and restart
  - Fix (suggested only, 1): No fix found in thread (reinstall, repair, Windows reinstall did not help)
  - Detect: system_files (high); disk_type (medium); disk_dirty (medium)
  - Auto-fix: **yes**, risk low. Run sfc /scannow (and DISM /Online /Cleanup-Image /RestoreHealth) with consent
- **'Application failed to start - side-by-side configuration is incorrect' (VC++ redistributables)** (`side-by-side-config-error`, 2 threads)
  - Fix (OP-confirmed, 1): OP said 'got it fixed' (fix not stated in thread)
  - Detect: runtimes (high)
  - Auto-fix: **partial**, risk low. Detect missing VC++ 2015-2022 x86/x64 and offer the Microsoft installer
- **XINPUT1_3.dll / DirectX runtime error (0xc000012f) on launch** (`xinput-directx-runtime-error`, 2 threads · codes: 0xc000012f)
  - Fix (OP-confirmed, 1): Delete the xinput file (xinput1_3.dll) from C drive (OP, fixed Valorant and Steam games; exact path not given)
  - Fix (guide, 1): Revo Uninstaller full removal then reinstall, TPM/Secure Boot on
  - Detect: runtimes (high); app_crash_module (medium)
  - Auto-fix: **partial**, risk medium. Detect xinput1_3.dll state; guide DirectX runtime repair/reinstall
- **Slow Valorant download (~1 MB/s) - Riot CDN side** (`slow-download-riot-cdn`, 1 threads)
  - Fix (OP-confirmed, 1): Wait, resolved by itself next morning (Riot-side); pause/resume and VPN suggested
  - Detect: riot_status (low); ping_route (low)
  - Auto-fix: **no**, risk low. Nothing local; advise waiting / pause-resume
- **Valorant says Windows 10 version is too old (Windows Update can't upgrade)** (`windows-version-too-old`, 1 threads)
  - Fix (OP-confirmed, 1): Download the Windows ISO from Microsoft site, mount it and run setup to upgrade; check winver afterwards
  - Detect: win_build (high)
  - Auto-fix: **partial**, risk medium. Detect build < 19045 and guide upgrade via Microsoft ISO / Update Assistant

### Audio / voice chat (4 problems,48 threads)

- **Bluetooth headset muffled / call-quality audio or no voice when Valorant voice chat switches it to Hands-Free (HFP) profile** (`bluetooth-headset-hfp-voice-chat`, 29 threads)
  - Fix (OP-confirmed, 7): Disable 'Handsfree Telephony' service on the headset: Settings > Bluetooth & devices > More devices and printer settings > right-click headset > Properties > Services tab > untick Handsfree Telephony > Apply;…
  - Fix (OP-confirmed, 5): Launch Valorant with party/voice chat on (quality will be bad, keep it). Windows Settings > Bluetooth & devices > Devices > More devices and printer settings > right-click headset > Properties > Services tab > uncheck…
  - Detect: bt_hfp (high); audio_devices (medium)
  - Auto-fix: **yes**, risk low. Disable BTAGService (Set-Service -StartupType Disabled + Stop) or guide disabling 'Handsfree Telephony' on the headset; user loses headset mic
- **No audio / broken voice chat / crackling from wrong per-app output, exclusive mode, ExitLag dual routing, USB power saving, 2026 audio driver, Sound Blaster, outdated BIOS** (`audio-device-config-issues`, 10 threads)
  - Fix (OP-confirmed, 1): Win+I > System > Sound > Advanced > App volume and device preferences: check output device used by Valorant.
  - Fix (OP-confirmed, 1): Turn off ExitLag 'dual routing' option (and possibly redirect login).
  - Detect: audio_devices (high); virtual_nic_vpn (low); bios_age (low)
  - Auto-fix: **partial**, risk low. Report device/driver/per-app output; exclusive-mode and device toggles are guided
- **In-game voice chat not working / robotic / muted for everyone (DNS, IPv6, Vivox routing, overlays) - WARP/DNS change workaround** (`voice-chat-network-dns-ipv6`, 8 threads)
  - Fix (OP-confirmed, 1): Change to public DNS (Google 8.8.8.8 or Cloudflare 1.1.1.1); one reported it lasted only ~3 days because ISP reset it
  - Fix (OP-confirmed, 1): Cloudflare WARP fixed voice chat (slightly higher ping)
  - Detect: dns_ipv6 (high); hook_procs (medium); oem_tools (low)
  - Auto-fix: **partial**, risk low. Offer public DNS (Set-DnsClientServerAddress) and IPv6 unbind, reversible
- **Discord screen-share: friends cannot hear Valorant voice chat** (`discord-stream-no-voice`, 1 threads)
  - Fix (suggested only, 1): Capture via OBS with a separate 'Game' output device added as audio source
  - Fix (suggested only, 1): Run Discord as admin (OP reported not working)
  - Detect: hook_procs (low)
  - Auto-fix: **no**, risk low. Guide: OBS separate output / Discord experimental capture setting

### GPU drivers (5 problems,40 threads)

- **AMD Adrenalin driver regressions (26.5.x, 26.7.1, 26.8.1): freezes, black screens, GPU driver crash/disabled, reboots; plus low FPS on high-end AMD builds** (`amd-adrenalin-driver-regression`, 21 threads)
  - Fix (OP-confirmed, 7): Roll back/DDU-clean-install an older AMD driver (26.1.1, 26.3.1 or 26.6.1 reported); one user confirmed fix for a freeze, another saw it still crash after downgrade
  - Fix (OP-confirmed, 2): Roll back NVIDIA driver (552.22) to fix huge latency counter / monitors turning off
  - Detect: gpu_driver (high); kp41_lke (high); pnp_errors (medium); fso_mpo (medium); igpu_state (medium); amd_settings (low); display_mode (low)
  - Auto-fix: **partial**, risk medium. Detect driver version against reported-bad list and recommend rollback (DDU clean install to 26.1.1/26.3.1/26.6.1); remove duplicate MPO DWORDs; DDU itself is guide-only
- **Graphics driver crash / black screen / very low FPS (3-5) from a faulty or corrupt GPU driver install (DDU clean install, older WHQL, page file)** (`gpu-driver-corrupt-needs-clean-install`, 7 threads)
  - Fix (OP-confirmed, 1): Page file: disable all, restart, set C: initial/max 16384 MB
  - Fix (guide, 1): Riot-support steps: download latest NVIDIA driver, run DDU (Display Driver Uninstaller) 'Clean and restart', install driver, restart (OP says it solved after months; another user says it did not work for them)
  - Detect: gpu_driver (high); kp41_lke (medium); win_build (medium); amd_settings (low)
  - Auto-fix: **partial**, risk medium. Detect driver version/date and TDR 4101 events; recommend DDU + clean install of a stable version (guide)
- **Freeze / stuck loading / crash after match start or in settings on Intel laptops due to outdated Intel iGPU/chipset drivers (VAN -102)** (`intel-graphics-chipset-driver-outdated`, 5 threads · codes: VAN 102)
  - Fix (OP-confirmed, 2): Update Intel Graphics driver (Intel Driver Assistant / newer September package)
  - Fix (OP-confirmed, 1): Update all Intel drivers from Intel Driver & Support site; OP confirmed fixed
  - Detect: intel_driver (high); gpu_driver (medium)
  - Auto-fix: **partial**, risk low. Flag old Intel driver dates and open Intel Driver & Support Assistant
- **Valorant runs on the integrated GPU / dedicated GPU not used, monitor on motherboard output, or freeze until iGPU adapter is re-enabled** (`wrong-gpu-or-display-output`, 4 threads)
  - Fix (OP-confirmed, 1): Install older NVIDIA driver and adjust NVIDIA app settings (1100 fps afterwards)
  - Fix (OP-confirmed, 1): Enable the 'AMD Radeon Graphics' (iGPU) display adapter in Device Manager
  - Detect: gpu_pref (high); igpu_state (high); display_mode (medium)
  - Auto-fix: **partial**, risk low. Set HKCU\Software\Microsoft\DirectX\UserGpuPreferences GpuPreference=2 for VALORANT; re-enable a disabled iGPU device (Enable-PnpDevice); cable move is manual
- **'A critical error has occurred and the process must be terminated' caused by NVIDIA Control Panel Vertical sync = Fast** (`nvidia-vsync-fast-critical-error`, 3 threads)
  - Fix (OP-confirmed, 1): Same fix confirmed by ~8 commenters (also resetting 3D settings)
  - Fix (guide, 1): NVIDIA Control Panel -> Manage 3D settings -> Vertical sync: change Fast to 'Use the 3D application setting', apply, relaunch
  - Detect: nv_3d_settings (medium); gpu_driver (medium)
  - Auto-fix: **partial**, risk low. Guide user to set Vertical sync to 'Use the 3D application setting' (no stable API to change it)

### Display / resolution (4 problems,35 threads)

- **Stretched / true-stretched resolution broken (patch 13.01), black bars / Fill not working, doubled saturation** (`stretched-resolution-issues`, 14 threads)
  - Fix (guide, 2): Create custom resolutions (e.g. 1568x1080, 1280x882) via CRU or GPU control panel; set Fill via game config then use Winexp/HRC (Hotkey Resolution Changer) to switch; repeated each game
  - Fix (OP-confirmed, 1): Toggle V-Sync on monitor OSD on/off
  - Detect: display_mode (medium)
  - Auto-fix: **no**, risk low. Guide only (CRU / GPU control panel); config-file tricks risk account suspension
- **Display misc: monitor disabled/not shown, wrong (4K) launch resolution from NVIDIA app optimization, pixelated UI, blurry skins/textures (AF, VRAM)** (`display-misc-blur-wrong-res`, 11 threads)
  - Fix (OP-confirmed, 1): Re-enable the monitor in Device Manager
  - Fix (OP-confirmed, 1): Fixed by selecting the correct resolution (user had wrong res)
  - Detect: display_mode (high); nv_3d_settings (low); ram_config (low)
  - Auto-fix: **partial**, risk low. Re-enable disabled monitor device (Enable-PnpDevice); other items guided
- **Freezes on alt-tab, crashes on agent select, fullscreen flicker or windowed lag linked to Fullscreen Optimizations / Multi-Plane Overlay / non-independent flip** (`fullscreen-optimizations-mpo`, 5 threads)
  - Fix (OP-confirmed, 1): Right-click VALORANT-Win64-Shipping.exe > Properties > Compatibility > Disable fullscreen optimizations; OP confirmed (another user it did not help)
  - Fix (OP-confirmed, 1): Disable MPO (ChrisTitusTool tweak) and tweak settings in AMD Adrenalin
  - Detect: fso_mpo (high); hook_procs (medium); display_mode (low)
  - Auto-fix: **yes**, risk medium. Set DISABLEDXMAXIMIZEDWINDOWEDMODE for VALORANT-Win64-Shipping.exe; optionally set OverlayTestMode=5 (MPO off) with backup of the key
- **Tearing / choppiness / blur with G-Sync/V-Sync, or FPS locked at refresh rate after driver update** (`vsync-gsync-tearing-fps-lock`, 5 threads)
  - Fix (OP-confirmed, 1): Turn off G-Sync in NVIDIA Control Panel.
  - Fix (OP-confirmed, 1): G-Sync + V-Sync ON in NVCP and cap FPS to just below monitor refresh (e.g. 171 for 180Hz).
  - Detect: nv_3d_settings (medium); display_mode (medium); gpu_driver (low)
  - Auto-fix: **partial**, risk low. Report refresh rate vs in-game cap; NVCP changes guided

### Login / account / client (7 problems,33 threads)

- **VAL 43 / stuck on loading screen caused by Apple Bonjour (mdnsNSP.dll) or National Instruments (nimdnsResponder.dll) mDNS DLL injected into the game** (`mdns-bonjour-ni-val43-loading`, 19 threads · codes: VAL 43)
  - Fix (OP-confirmed, 4): Uninstall Bonjour (Apple) completely via Revo/Uninstalr, optionally run 'sfc /scannow' first, then uninstall Vanguard, let Riot Client reinstall it and reboot
  - Fix (guide, 3): Event Viewer > Windows Logs > Application: find faulting module; uninstall National Instruments software (or disable its auto updater); uninstall Apple Bonjour (mdnsNSP.dll)
  - Detect: mdns (high); app_crash_module (high); firewall_rules (medium); av_products (low)
  - Auto-fix: **partial**, risk medium. Detect Bonjour/NI and the faulting module; open the uninstaller (Bonjour / National Instruments) with consent; then netsh winsock reset and reboot. DLL deletion via takeown/icacls is guide-only
- **Account / MFA prompts / MFA queue lock / matchmaking glitches (non-technical)** (`account-mfa-matchmaking-glitches`, 5 threads)
  - Fix (OP-confirmed, 1): Link the original Gmail in Linked Accounts (OP)
  - Fix (OP-confirmed, 1): Email Riot support to correct MFA email.
  - Auto-fix: **no**, risk low. Not detectable locally; point to Riot support / workaround
- **PlayStation (PS5/Portal) Valorant errors 51/53/54 / cannot connect** (`console-ps5-errors`, 3 threads)
  - Fix (OP-confirmed, 1): Uninstall and reinstall the game
  - Fix (OP-confirmed, 1): Have a friend log into Valorant with your PSN account from their console, then log back in at home.
  - Auto-fix: **no**, risk low. Not applicable to a Windows tool
- **VAL 57 / VAL 59 connection errors (IPv6 on adapter, Killer Intelligence Center)** (`val57-59-ipv6-killer`, 2 threads · codes: VAL 57, VAL 59)
  - Fix (guide, 1): After reinstalling Vanguard, uncheck Internet Protocol Version 6 (TCP/IPv6) in Ethernet adapter Properties.
  - Fix (suggested only, 1): Disable features in Killer Intelligence Center and update Intel drivers.
  - Detect: dns_ipv6 (medium); oem_tools (medium); vgc_service (low)
  - Auto-fix: **partial**, risk low. Offer to unbind IPv6 on the active adapter (Disable-NetAdapterBinding -ComponentID ms_tcpip6), reversible
- **VALORANT shows offline / greyed Play button because Windows date/time is wrong (SSL validation fails)** (`wrong-system-clock-offline`, 2 threads)
  - Fix (guide, 2): Correct Windows date/time and sync time (ReviOS install had wrong clock)
  - Detect: clock (high)
  - Auto-fix: **yes**, risk low. Start W32Time and w32tm /resync, enable automatic time
- **Black screen at match start / chat outage due to Riot server-side incident** (`riot-server-side-incident`, 1 threads)
  - Fix (suggested only, 1): Wait; issue disappeared after alert cleared
  - Detect: riot_status (medium)
  - Auto-fix: **no**, risk low. Check Riot status; wait
- **'VALORANT Failed To Launch - Something unusual happened' because Terms and Conditions popup not accepted** (`terms-not-accepted-failed-to-launch`, 1 threads)
  - Fix (OP-confirmed, 2): Log out and back into Riot Client to trigger the terms and conditions popup and accept
  - Auto-fix: **no**, risk low. Guide: log out/in to Riot Client and accept terms

### Mouse / input (3 problems,13 threads)

- **Mouse lag / stutter on fast movement or flicker from USB port (2.0 vs 3.0), polling rate, wireless dongle distance, driver** (`mouse-usb-port-polling-lag`, 7 threads)
  - Fix (OP-confirmed, 1): Move mouse to a USB 3.0 port.
  - Fix (OP-confirmed, 1): Switch to wireless mouse (OP)
  - Detect: usb_devices (medium); mouse_settings (medium); gpu_driver (low)
  - Auto-fix: **no**, risk low. Guide only (port, dongle, polling)
- **Mouse feels floaty / faster or inconsistent (Enhance pointer precision on, Win11 24H2 'Optimizations for windowed games', HAGS)** (`mouse-acceleration-floaty-aim`, 5 threads)
  - Fix (OP-confirmed, 1): Turn off Enhance pointer precision in Windows mouse settings (OP found it was on)
  - Fix (OP-confirmed, 1): Turn off Windows Graphics setting 'Optimizations for windowed games' (fixed mouse for OP)
  - Detect: mouse_settings (high); hags_windowed (high); win_build (medium)
  - Auto-fix: **yes**, risk low. Set HKCU\Control Panel\Mouse MouseSpeed=0, MouseThreshold1=0, MouseThreshold2=0 (EPP off); windowed-games optimization toggle with consent
- **Mouse clicks stop working after Alt-Tab (MB4 bound to Browser Back) and random crashes (RAM overclock instability)** (`mouse-clicks-stop-after-alt-tab`, 1 threads)
  - Fix (OP-confirmed, 1): Rebind MB4 in iCUE so it does not send Browser Back
  - Fix (OP-confirmed, 1): Keep DOCP enabled but manually adjust memory settings for stability (OP post truncated)
  - Detect: ram_config (medium); conflict_apps_installed (low)
  - Auto-fix: **no**, risk low. Guide: rebind MB4 in mouse software, tune memory settings

### Other (1 problem,3 threads)

- **Replay system issues (FPS cap 500)** (`replay-system-issues`, 3 threads)
  - Fix (OP-confirmed, 1): Cap FPS to 360
  - Fix (OP-confirmed, 1): Fixed on its own
  - Detect: valorant_config (low)
  - Auto-fix: **no**, risk low. Suggest FPS cap 360

### Hardware / other (2 problems,3 threads)

- **Black screen/freeze with volmgr 161; GPU fan stuck at 100% after first Vanguard install** (`volmgr-161-black-screen-fan`, 2 threads)
  - Fix (OP-confirmed, 1): Fan issue went away after 4 reboots
  - Fix (suggested only, 1): Check Event Viewer for volmgr 161
  - Detect: kp41_lke (low)
  - Auto-fix: **no**, risk low. Check System log volmgr event 161; reboot
- **PC restarts on Valorant launch caused by external USB CD/Blu-ray drive** (`usb-optical-drive-restart`, 1 threads)
  - Fix (guide, 1): Unplug external Blu-ray/CD drive
  - Detect: usb_devices (high)
  - Auto-fix: **no**, risk low. Detect USB optical drive and tell user to unplug

## Diagnostic checklist (what the tool should check)

Each check appears only once, even when several problems need it. Checks are ordered by how many unique solved threads they cover, counting the threads of every problem the check detects with high or medium confidence. Low-confidence links are listed but do not count toward coverage. If you build the top of this list first, it covers the largest share of real cases. Confidence is the best confidence the check reaches for any of its problems.

| # | Check | Threads covered | Best confidence | Detects (problem id: confidence) |
|---|---|---|---|---|
| 1 | **Hooking / overlay software running (RTSS, MSI Afterburner, Overwolf, trackers, Discord/NVIDIA/Radeon overlay, Xbox Game Bar, Wallpaper Engine, Medal, BTTray, TikTok LIVE Studio)** `hook_procs` | 136 | high | third-party-hook-overlay-conflict: H, van185-vanguard-disconnects: M, shader-cache-compilation-stutter: M, usb-selective-suspend-freeze: M, voice-chat-network-dns-ipv6: M, fps-decay-over-time: M, igpu-disabled-or-driverless-freeze: M, fullscreen-optimizations-mpo: M, frametime-latency-spikes: M, freeze-on-click-lobby-misc: M, patch-13-01-fps-drop: M, discord-stream-no-voice: L |
| 2 | **Vanguard services: vgc running and its start type; vgk registered** `vgc_service` | 123 | high | nahimic-audio-service-launch-hang: M, van185-vanguard-disconnects: H, vanguard-install-loop: H, vanguard-corrupt-install: H, vpn-anticheat-network-filter-conflict: M, launch-shortcut-tray-failure: M, vgc-service-not-running: H, vgk-not-loaded-needs-reboot: H, launch-fails-something-unusual: M, third-party-antivirus-conflict: M, val57-59-ipv6-killer: L, razer-synapse-escalation-kick: M |
| 3 | **'Faulting module name' in VALORANT / Riot crash events (mdnsNSP.dll, nimdnsResponder.dll, ProductInfo.dll, RTSSHooks64.dll, pmls64.dll, btmmhook.dll...)** `app_crash_module` | 106 | high | third-party-hook-overlay-conflict: H, nahimic-audio-service-launch-hang: H, mdns-bonjour-ni-val43-loading: H, freeze-on-click-lobby-misc: H, unexplained-launch-failure: M, xinput-directx-runtime-error: M |
| 4 | **Secure Boot actually active** `secure_boot` | 99 | high | secure-boot-enable-boot-failure: H, hvci-memory-integrity-required: M, secure-boot-not-detected-van9003: H, tpm2-missing-or-unmet: H, secure-boot-policy-wave-2026: H, igpu-disabled-or-driverless-freeze: L, launch-fails-something-unusual: M, insider-build-vgk-bsod: M, van-restriction-hwid: M, dual-boot-secure-boot-van1: H, van9101-attestation: M |
| 5 | **Realtek 2.5GbE / PCIe GbE adapter present and enabled; is the active connection Wi-Fi (i.e. the NIC is unused)** `realtek_nic` | 96 | high | realtek-2-5gbe-nic-stutter: H, whole-pc-freeze-kernel-power-41: M |
| 6 | **Motherboard BIOS version / release date is old** `bios_age` | 94 | high | secure-boot-enable-boot-failure: H, whole-pc-freeze-kernel-power-41: M, outdated-bios-reboot-vgk-bsod: H, secure-boot-not-detected-van9003: M, audio-device-config-issues: L, ftpm-event-14-17-stutter: M |
| 7 | **GPU driver version / date; known-bad versions reported (AMD 26.5.x, 26.7.1, 26.8.1 etc.) and leftover drivers from a previous GPU vendor** `gpu_driver` | 93 | high | amd-adrenalin-driver-regression: H, whole-pc-freeze-kernel-power-41: M, shader-cache-compilation-stutter: M, low-gpu-usage-after-ue5-patch: M, fps-decay-over-time: M, gpu-driver-corrupt-needs-clean-install: H, mouse-usb-port-polling-lag: L, intel-graphics-chipset-driver-outdated: M, vsync-gsync-tearing-fps-lock: L, frametime-latency-spikes: M, nvidia-vsync-fast-critical-error: M, nvidia-driver-packet-loss: M |
| 8 | **Realtek NIC driver errors / adapter reset events** `realtek_events` | 91 | high | realtek-2-5gbe-nic-stutter: H, usb-selective-suspend-freeze: M, windows-update-kb5101650-microstutter: M |
| 9 | **TPM 2.0 present and ready; fTPM vs dTPM** `tpm` | 89 | high | secure-boot-enable-boot-failure: M, hvci-memory-integrity-required: M, secure-boot-not-detected-van9003: H, tpm2-missing-or-unmet: H, ftpm-event-14-17-stutter: M, van-restriction-hwid: M, van9101-attestation: M |
| 10 | **Ping / jitter / packet loss and route to the Riot server (ISP routing)** `ping_route` | 88 | high | local-network-packet-loss-jitter: H, isp-routing-high-ping: H, van185-vanguard-disconnects: M, network-exceptions-download-stuck: M, frametime-latency-spikes: M, nvidia-driver-packet-loss: M, slow-download-riot-cdn: L |
| 11 | **Hard-freeze / sudden-shutdown traces: Kernel-Power 41, LiveKernelEvent 141/144/15e, Display 4101 (TDR)** `kp41_lke` | 85 | high | amd-adrenalin-driver-regression: H, whole-pc-freeze-kernel-power-41: H, outdated-bios-reboot-vgk-bsod: H, usb-selective-suspend-freeze: M, gpu-driver-corrupt-needs-clean-install: M, igpu-disabled-or-driverless-freeze: M, volmgr-161-black-screen-fan: L |
| 12 | **Connection is Wi-Fi; band (2.4/5 GHz), signal** `wifi` | 81 | high | local-network-packet-loss-jitter: H, isp-routing-high-ping: M, van185-vanguard-disconnects: M, dual-network-adapter-desync: M |
| 13 | **More than one physical network adapter up at once (Ethernet + Wi-Fi, two Wi-Fi) and their metric order** `multi_nic` | 79 | high | realtek-2-5gbe-nic-stutter: H, dual-network-adapter-desync: H |
| 14 | **Third-party antivirus (Trend Micro, Norton, Quick Heal, 360 Total Security) and Defender exclusions/blocks** `av_products` | 78 | high | van185-vanguard-disconnects: H, mdns-bonjour-ni-val43-loading: L, vanguard-install-loop: M, vanguard-corrupt-install: M, vpn-anticheat-network-filter-conflict: M, vgc-high-cpu: M, third-party-antivirus-conflict: H |
| 15 | **Windows version / build (Win10 below 22H2 19045 is too old; Insider/Canary channel)** `win_build` | 77 | high | hvci-memory-integrity-required: H, vanguard-install-loop: M, vanguard-corrupt-install: L, tpm2-missing-or-unmet: M, gpu-driver-corrupt-needs-clean-install: M, mouse-acceleration-floaty-aim: M, vgc-service-not-running: M, insider-build-vgk-bsod: H, iommu-restriction: M, van9101-attestation: M, windows-version-too-old: H |
| 16 | **NIC power saving / advanced properties (EEE, Green Ethernet, Interrupt Moderation, 'allow the computer to turn off this device')** `nic_power` | 76 | medium | realtek-2-5gbe-nic-stutter: M, local-network-packet-loss-jitter: L |
| 17 | **DNS servers, IPv6 binding, Teredo** `dns_ipv6` | 68 | high | local-network-packet-loss-jitter: M, van185-vanguard-disconnects: M, voice-chat-network-dns-ipv6: H, network-exceptions-download-stuck: M, val57-59-ipv6-killer: M |
| 18 | **Known conflicting apps installed (Premier Opinion / pmls64.dll, Huawei PC Manager, MoniVisor, ASUS ScreenXpert & other ASUS services, Razer Synapse, Logitech G HUB, reWASD/x360ce)** `conflict_apps_installed` | 59 | high | third-party-hook-overlay-conflict: H, incompatible-driver-component: M, network-exceptions-download-stuck: M, mouse-clicks-stop-after-alt-tab: L |
| 19 | **Secure Boot keys: Setup mode, 'Windows UEFI CA 2023' present in db** `sb_keys` | 57 | high | secure-boot-enable-boot-failure: H, secure-boot-not-detected-van9003: H, secure-boot-policy-wave-2026: H |
| 20 | **Firewall allow rules for Riot Client / VALORANT / vgc; firewall profiles** `firewall_rules` | 56 | high | van185-vanguard-disconnects: H, mdns-bonjour-ni-val43-loading: M, launch-shortcut-tray-failure: M, network-exceptions-download-stuck: H |
| 21 | **iGPU (AMD Radeon Graphics / Intel) present, disabled, or driverless 'unknown display device'** `igpu_state` | 52 | high | amd-adrenalin-driver-regression: M, whole-pc-freeze-kernel-power-41: M, igpu-disabled-or-driverless-freeze: H, freeze-on-click-lobby-misc: L, wrong-gpu-or-display-output: H |
| 22 | **Riot Client install / cache folders, install path, client logs, client stuck in tray** `riot_folders` | 52 | high | vanguard-install-loop: M, riot-client-update-stuck: H, valorant-config-corrupt: M, launch-shortcut-tray-failure: H, riot-client-hardware-acceleration: L |
| 23 | **Firmware in UEFI mode (not CSM/Legacy) and system disk is GPT** `uefi_gpt` | 50 | high | secure-boot-enable-boot-failure: H, secure-boot-not-detected-van9003: H |
| 24 | **BSOD dumps and bugcheck naming a driver such as vgk.sys / WdFilter.sys** `minidump` | 44 | high | outdated-bios-reboot-vgk-bsod: M, vanguard-corrupt-install: M, fast-startup-stale-dat-bsod: M, insider-build-vgk-bsod: H, wdfilter-bsod: H |
| 25 | **Audio devices: default output/input, per-app output, exclusive mode, Sound Blaster, audio driver version** `audio_devices` | 39 | high | bluetooth-headset-hfp-voice-chat: M, audio-device-config-issues: H, iommu-restriction: L |
| 26 | **Active power plan / power mode, on AC or battery, CPU reaching max clock (throttling, 800 MHz lock, 10 W power limit)** `power_plan` | 39 | high | power-plan-throttling-low-fps: H, low-gpu-usage-after-ue5-patch: H, fps-lower-than-expected-caps: M |
| 27 | **Vanguard file / registry integrity and boot-status files** `vgk_files` | 38 | high | vanguard-install-loop: H, vanguard-corrupt-install: H, vgk-not-loaded-needs-reboot: H |
| 28 | **USB selective suspend enabled; peripherals on chipset USB ports** `usb_ss` | 37 | high | whole-pc-freeze-kernel-power-41: M, usb-selective-suspend-freeze: H, ftpm-event-14-17-stutter: M |
| 29 | **CPU virtualization (SVM / VT-x) enabled, hypervisorlaunchtype, VirtualMachinePlatform / HypervisorPlatform features** `virtualization` | 37 | high | hvci-memory-integrity-required: H, whole-pc-freeze-kernel-power-41: L, vpn-anticheat-network-filter-conflict: M |
| 30 | **Bonjour / National Instruments mDNS component present** `mdns` | 36 | high | mdns-bonjour-ni-val43-loading: H, riot-client-update-stuck: M |
| 31 | **Monitor / resolution / refresh rate, disabled monitor, multi-monitor** `display_mode` | 34 | high | amd-adrenalin-driver-regression: L, stretched-resolution-issues: M, display-misc-blur-wrong-res: H, fullscreen-optimizations-mpo: L, vsync-gsync-tearing-fps-lock: M, wrong-gpu-or-display-output: M |
| 32 | **Memory Integrity (HVCI) / VBS state** `hvci` | 33 | high | hvci-memory-integrity-required: H, tpm2-missing-or-unmet: L, launch-fails-something-unusual: M, van-restriction-hwid: M |
| 33 | **Known incompatible drivers blocking HVCI / Vanguard (gdrv3.sys, xhunter1.sys, ene.sys, se64a.sys, mtkwl6ex.sys)** `hvci_incompat_drivers` | 32 | high | hvci-memory-integrity-required: H, incompatible-driver-component: H |
| 34 | **RAM amount, channel count, XMP/EXPO (ConfiguredClockSpeed vs Speed)** `ram_config` | 31 | high | power-plan-throttling-low-fps: H, shader-cache-compilation-stutter: L, display-misc-blur-wrong-res: L, fps-lower-than-expected-caps: H, standby-memory-stutter: M, mouse-clicks-stop-after-alt-tab: M |
| 35 | **TCP/Winsock stack: congestion provider, MTU, buffer settings** `tcp_stack` | 30 | medium | local-network-packet-loss-jitter: M |
| 36 | **Bluetooth headset falling back to Hands-Free (HFP) profile** `bt_hfp` | 29 | high | bluetooth-headset-hfp-voice-chat: H |
| 37 | **Nahimic / A-Volute audio service installed and running** `nahimic` | 29 | high | nahimic-audio-service-launch-hang: H |
| 38 | **Fullscreen optimizations and MPO (Multi-Plane Overlay) state** `fso_mpo` | 28 | high | amd-adrenalin-driver-regression: M, fullscreen-optimizations-mpo: H, windows-update-kb5101650-microstutter: M |
| 39 | **Pending reboot / real reboot done after Vanguard install / Fast Startup enabled** `pending_reboot` | 28 | high | riot-client-update-stuck: M, fast-startup-stale-dat-bsod: H, vgk-not-loaded-needs-reboot: H |
| 40 | **Devices with yellow warning / error / disabled state in Device Manager** `pnp_errors` | 27 | high | van185-vanguard-disconnects: L, amd-adrenalin-driver-regression: M, igpu-disabled-or-driverless-freeze: H |
| 41 | **Game installed on HDD / disk health** `disk_type` | 24 | high | power-plan-throttling-low-fps: H, corrupt-system-or-game-files: M |
| 42 | **Shader cache state / first launches after new driver or GPU** `shader_cache` | 16 | medium | shader-cache-compilation-stutter: M |
| 43 | **Which GPU VALORANT runs on / monitor plugged into motherboard output** `gpu_pref` | 15 | high | low-gpu-usage-after-ue5-patch: H, wrong-gpu-or-display-output: H |
| 44 | **Mouse acceleration (Enhance pointer precision) and polling/USB** `mouse_settings` | 12 | high | mouse-usb-port-polling-lag: M, mouse-acceleration-floaty-aim: H |
| 45 | **Other anti-cheats / escalation services (FACEIT, ESEA, XIGNCODE3, Razer Synapse game escalation)** `other_anticheat` | 11 | high | vpn-anticheat-network-filter-conflict: H, razer-synapse-escalation-kick: M |
| 46 | **RiotClientServices.exe running elevated / its compatibility flags** `riot_elevation` | 10 | high | riot-client-not-elevated: H |
| 47 | **vgc.exe CPU usage** `vgc_cpu` | 10 | high | vgc-high-cpu: H, patch-13-01-fps-drop: M |
| 48 | **VPN / virtual network adapters and network-filtering software (TAP, VMware, VirtualBox, GlobalProtect, NetLimiter, Docker Desktop, ExitLag)** `virtual_nic_vpn` | 10 | high | isp-routing-high-ping: L, audio-device-config-issues: L, vpn-anticheat-network-filter-conflict: H |
| 49 | **Windows system file integrity** `system_files` | 9 | high | fast-startup-stale-dat-bsod: M, corrupt-system-or-game-files: H |
| 50 | **TPM Event ID 14 / 17 in the System log (fTPM issues)** `tpm_events` | 9 | high | vgc-service-not-running: M, ftpm-event-14-17-stutter: H |
| 51 | **VALORANT user config folder (corrupt settings / keybinds)** `valorant_config` | 9 | high | valorant-config-corrupt: H, fps-lower-than-expected-caps: L, replay-system-issues: L |
| 52 | **NVIDIA 3D settings: Vertical sync = Fast, G-Sync, frame-cap overrides, NVIDIA app automatic optimization** `nv_3d_settings` | 8 | medium | display-misc-blur-wrong-res: L, low-gpu-usage-after-ue5-patch: L, vsync-gsync-tearing-fps-lock: M, nvidia-vsync-fast-critical-error: M |
| 53 | **Problematic USB devices (external CD/Blu-ray drive, wireless dongle, mouse port)** `usb_devices` | 8 | high | usb-selective-suspend-freeze: L, mouse-usb-port-polling-lag: M, usb-optical-drive-restart: H |
| 54 | **Stale C:\Windows\392667600.dat file** `stale_dat` | 6 | high | fast-startup-stale-dat-bsod: H |
| 55 | **HAGS, 'Optimizations for windowed games' and Game Mode state** `hags_windowed` | 5 | high | realtek-2-5gbe-nic-stutter: L, whole-pc-freeze-kernel-power-41: L, mouse-acceleration-floaty-aim: H |
| 56 | **Intel iGPU / chipset driver outdated** `intel_driver` | 5 | high | intel-graphics-chipset-driver-outdated: H |
| 57 | **VALORANT / Discord / vgc forced to High/Realtime priority; Process Lasso rules** `priority` | 5 | high | intel-hybrid-cpu-ecore-stutter: M, high-process-priority-van136-185: H |
| 58 | **Standby memory / non-paged pool growth** `standby_mem` | 5 | medium | patch-13-01-fps-drop: M, standby-memory-stutter: M |
| 59 | **NTFS dirty bit / disk health** `disk_dirty` | 4 | high | vanguard-install-loop: L, corrupt-system-or-game-files: M, vanguard-blocks-chkdsk: H |
| 60 | **Image File Execution Options entries (DevOverrideEnable, Debugger hooks)** `ifeo` | 4 | high | launch-fails-something-unusual: H |
| 61 | **VC++ 2015-2022 x86/x64 and DirectX (xinput1_3.dll) components; SideBySide errors** `runtimes` | 4 | high | side-by-side-config-error: H, xinput-directx-runtime-error: H |
| 62 | **Intel hybrid CPU (P/E cores) and background apps eating CPU** `hybrid_cpu` | 3 | high | intel-hybrid-cpu-ecore-stutter: H, msi-x3d-game-mode-lobby-freeze: L |
| 63 | **IOMMU / Kernel DMA Protection state** `iommu_dma` | 3 | high | iommu-restriction: H |
| 64 | **OEM / network tools (Lenovo Vantage, Killer Intelligence Center, ExitLag, MSI X3D mode)** `oem_tools` | 3 | medium | power-plan-throttling-low-fps: L, voice-chat-network-dns-ipv6: L, val57-59-ipv6-killer: M, lenovo-vantage-wifi-collapse: M, msi-x3d-game-mode-lobby-freeze: L |
| 65 | **System date/time correct** `clock` | 2 | high | wrong-system-clock-offline: H |
| 66 | **Dual boot / Windows Boot Manager on another disk / GRUB first in boot order** `dualboot` | 2 | high | van-restriction-hwid: L, dual-boot-secure-boot-van1: H |
| 67 | **Update reported as problematic installed (KB5101650)** `hotfix` | 2 | high | windows-update-kb5101650-microstutter: H |
| 68 | **Intel VMD / RST controller (mainly Dell/Alienware)** `vmd` | 2 | high | intel-vmd-status-vmd-9001: H |
| 69 | **Basic system environment variables missing** `env_vars` | 1 | high | missing-environment-variables: H |
| 70 | **Riot-side incident / maintenance** `riot_status` | 1 | medium | secure-boot-policy-wave-2026: L, riot-server-side-incident: M, slow-download-riot-cdn: L |
| 71 | **AMD Adrenalin features (Anti-Lag, AFMF, FSR, Radeon Boost/Chill, FRTC, Enhanced Sync, overlay, GPU Scaling)** `amd_settings` | 0 | low | amd-adrenalin-driver-regression: L, shader-cache-compilation-stutter: L, gpu-driver-corrupt-needs-clean-install: L |
| 72 | **CPU/GPU temperature and thermal throttling** `thermals` | 0 | low | van185-vanguard-disconnects: L, power-plan-throttling-low-fps: L, fps-decay-over-time: L, vgc-high-cpu: L |

### How to run each check (concrete Windows method)

1. **`hook_procs`**: Hooking / overlay software running (RTSS, MSI Afterburner, Overwolf, trackers, Discord/NVIDIA/Radeon overlay, Xbox Game Bar, Wallpaper Engine, Medal, BTTray, TikTok LIVE Studio)  
   How: `Get-Process | ? Name -match '^(RTSS|MSIAfterburner|Overwolf|OverwolfBrowser|wallpaper32|wallpaper64|BTTray|Medal|Discord|GameBar|RadeonSoftware|TikTok LIVE Studio)$' ; loaded modules: (Get-Process VALORANT-Win64-Shipping).Modules | ? ModuleName -match 'RTSSHooks|pmls64|btmmhook|mdnsNSP|nimdns|ProductInfo'`
2. **`vgc_service`**: Vanguard services: vgc running and its start type; vgk registered  
   How: `sc query vgc ; sc qc vgc (START_TYPE) ; sc query vgk ; sc qc vgk (SYSTEM_START) ; Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Service Control Manager';Id=7000,7023,7031,7034} | ? Message -match 'vgc|vgk'`
3. **`app_crash_module`**: 'Faulting module name' in VALORANT / Riot crash events (mdnsNSP.dll, nimdnsResponder.dll, ProductInfo.dll, RTSSHooks64.dll, pmls64.dll, btmmhook.dll...)  
   How: `Get-WinEvent -FilterHashtable @{LogName='Application';Id=1000} | ? Message -match 'VALORANT|Riot' -> parse 'Faulting module name' ; Reliability Monitor (perfmon /rel)`
4. **`secure_boot`**: Secure Boot actually active  
   How: `Confirm-SecureBootUEFI (admin) ; HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State UEFISecureBootEnabled ; msinfo32 'Secure Boot State'`
5. **`realtek_nic`**: Realtek 2.5GbE / PCIe GbE adapter present and enabled; is the active connection Wi-Fi (i.e. the NIC is unused)  
   How: `PowerShell: Get-NetAdapter | ? InterfaceDescription -match 'Realtek.*(2\.5GbE|GbE)' | select Name,Status,DriverVersion,DriverDate ; active route: Get-NetRoute 0.0.0.0/0 | Get-NetAdapter`
6. **`bios_age`**: Motherboard BIOS version / release date is old  
   How: `Get-CimInstance Win32_BIOS | select SMBIOSBIOSVersion,ReleaseDate ; Get-CimInstance Win32_BaseBoard | select Manufacturer,Product ; e.g. warn if ReleaseDate older than ~2023`
7. **`gpu_driver`**: GPU driver version / date; known-bad versions reported (AMD 26.5.x, 26.7.1, 26.8.1 etc.) and leftover drivers from a previous GPU vendor  
   How: `Get-CimInstance Win32_VideoController | select Name,DriverVersion,DriverDate ; Get-CimInstance Win32_PnPSignedDriver | ? DeviceClass -eq 'DISPLAY' ; pnputil /enum-drivers | findstr /i 'nvidia amd' (leftovers after GPU swap)`
8. **`realtek_events`**: Realtek NIC driver errors / adapter reset events  
   How: `Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='rt25cx21','rt640x64'} -MaxEvents 50 (Hardware IO error, reset, disconnect); NDIS reset warnings`
9. **`tpm`**: TPM 2.0 present and ready; fTPM vs dTPM  
   How: `Get-Tpm | select TpmPresent,TpmReady,ManufacturerIdTxt ; Get-CimInstance -Namespace root/cimv2/Security/MicrosoftTpm Win32_Tpm | select SpecVersion`
10. **`ping_route`**: Ping / jitter / packet loss and route to the Riot server (ISP routing)  
   How: `Test-Connection <Riot server IP> -Count 50 (loss, avg, deviation) ; tracert / pathping <IP> (unexpected country / hop) ; compare with WARP/VPN on vs off ; server info from Riot Client logs`
11. **`kp41_lke`**: Hard-freeze / sudden-shutdown traces: Kernel-Power 41, LiveKernelEvent 141/144/15e, Display 4101 (TDR)  
   How: `Get-WinEvent -FilterHashtable @{LogName='System';Id=41;ProviderName='Microsoft-Windows-Kernel-Power'} ; Get-WinEvent -FilterHashtable @{LogName='System';Id=4101} ; dir C:\Windows\LiveKernelReports ; Reliability Monitor`
12. **`wifi`**: Connection is Wi-Fi; band (2.4/5 GHz), signal  
   How: `netsh wlan show interfaces (Radio type, Channel, Signal) ; Get-NetAdapter | ? Status -eq Up | select Name,MediaType,PhysicalMediaType`
13. **`multi_nic`**: More than one physical network adapter up at once (Ethernet + Wi-Fi, two Wi-Fi) and their metric order  
   How: `Get-NetAdapter -Physical | ? Status -eq 'Up' ; Get-NetIPInterface -AddressFamily IPv4 | sort InterfaceMetric`
14. **`av_products`**: Third-party antivirus (Trend Micro, Norton, Quick Heal, 360 Total Security) and Defender exclusions/blocks  
   How: `Get-CimInstance -Namespace root/SecurityCenter2 AntiVirusProduct | select displayName,productState ; (Get-MpPreference).ExclusionPath ; Get-MpThreatDetection (Riot/Vanguard items blocked)`
15. **`win_build`**: Windows version / build (Win10 below 22H2 19045 is too old; Insider/Canary channel)  
   How: `Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | select ProductName,DisplayVersion,CurrentBuild,UBR ; Insider: HKLM\SOFTWARE\Microsoft\WindowsSelfHost\Applicability BranchName`
16. **`nic_power`**: NIC power saving / advanced properties (EEE, Green Ethernet, Interrupt Moderation, 'allow the computer to turn off this device')  
   How: `Get-NetAdapterAdvancedProperty -Name <nic> | ? DisplayName -match 'Energy|Green|Interrupt|Flow|Large Send|Buffers' ; Get-NetAdapterPowerManagement -Name <nic>`
17. **`dns_ipv6`**: DNS servers, IPv6 binding, Teredo  
   How: `Get-DnsClientServerAddress -AddressFamily IPv4 ; Get-NetAdapterBinding -ComponentID ms_tcpip6 ; netsh interface teredo show state ; Resolve-DnsName on Riot hosts (OS error 11001)`
18. **`conflict_apps_installed`**: Known conflicting apps installed (Premier Opinion / pmls64.dll, Huawei PC Manager, MoniVisor, ASUS ScreenXpert & other ASUS services, Razer Synapse, Logitech G HUB, reWASD/x360ce)  
   How: `Uninstall registry DisplayName regex ; Get-Service | ? DisplayName -match 'ScreenXpert|Asus|Razer' ; Test-Path 'C:\Program Files (x86)\Windows Assistant App\MV\unins000.exe'`
19. **`sb_keys`**: Secure Boot keys: Setup mode, 'Windows UEFI CA 2023' present in db  
   How: `(Get-SecureBootUEFI SetupMode).bytes (1 = setup mode, no keys) ; [Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI db).bytes) -match 'Windows UEFI CA 2023' ; Windows Security > Device security certificate status`
20. **`firewall_rules`**: Firewall allow rules for Riot Client / VALORANT / vgc; firewall profiles  
   How: `Get-NetFirewallApplicationFilter | ? Program -match 'Riot|VALORANT|vgc' | Get-NetFirewallRule | select DisplayName,Enabled,Action,Direction ; Get-NetFirewallProfile | select Name,Enabled`
21. **`igpu_state`**: iGPU (AMD Radeon Graphics / Intel) present, disabled, or driverless 'unknown display device'  
   How: `Get-CimInstance Win32_VideoController | select Name,Status,ConfigManagerErrorCode,DriverVersion ; Get-PnpDevice -Class Display | select FriendlyName,Status ; Win32_Processor.Name (Ryzen 7000/9000 have an iGPU)`
22. **`riot_folders`**: Riot Client install / cache folders, install path, client logs, client stuck in tray  
   How: `Test-Path 'C:\Riot Games\Riot Client', $env:LOCALAPPDATA\Riot Games\Riot Client, $env:ProgramData\Riot Games ; logs in $env:LOCALAPPDATA\Riot Games\Riot Client\Logs ; Get-Process RiotClientServices,RiotClientUx -EA 0`
23. **`uefi_gpt`**: Firmware in UEFI mode (not CSM/Legacy) and system disk is GPT  
   How: `$env:firmware_type or bcdedit /enum {current} (path \Windows\system32\winload.efi) ; (Get-Partition -DriveLetter C | Get-Disk).PartitionStyle ; ESP present: Get-Partition | ? GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'`
24. **`minidump`**: BSOD dumps and bugcheck naming a driver such as vgk.sys / WdFilter.sys  
   How: `dir C:\Windows\Minidump\*.dmp ; Get-WinEvent -FilterHashtable @{LogName='System';Id=1001;ProviderName='Microsoft-Windows-WER-SystemErrorReporting'} (bugcheck 0x139/0x7E/0x50/0xD1) ; module name from dump via WinDbg/WhoCrashed`
25. **`audio_devices`**: Audio devices: default output/input, per-app output, exclusive mode, Sound Blaster, audio driver version  
   How: `Get-PnpDevice -Class AudioEndpoint,MEDIA | select FriendlyName,Status ; Get-CimInstance Win32_PnPSignedDriver | ? DeviceClass -eq 'MEDIA' | select DeviceName,DriverVersion,DriverDate ; per-app output: HKCU\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore`
26. **`power_plan`**: Active power plan / power mode, on AC or battery, CPU reaching max clock (throttling, 800 MHz lock, 10 W power limit)  
   How: `powercfg /getactivescheme ; Get-CimInstance Win32_Battery (BatteryStatus 2 = AC) ; Get-CimInstance Win32_Processor | select CurrentClockSpeed,MaxClockSpeed ; Get-Counter '\Processor Information(_Total)\% Processor Performance' under load (<100 = throttling)`
27. **`vgk_files`**: Vanguard file / registry integrity and boot-status files  
   How: `Test-Path 'C:\Program Files\Riot Vanguard\vgk.sys','C:\Program Files\Riot Vanguard\vgc.exe' ; Test-Path HKLM:\SYSTEM\CurrentControlSet\Services\vgk and ...\vgc ; Test-Path C:\Windows\vgkbootstatus.dat ; Vanguard logs in C:\Program Files\Riot Vanguard\Logs`
28. **`usb_ss`**: USB selective suspend enabled; peripherals on chipset USB ports  
   How: `powercfg /query SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 (AC/DC index 1 = enabled) ; Get-PnpDevice -Class Mouse,Keyboard + Get-PnpDeviceProperty DEVPKEY_Device_Parent to find the root hub`
29. **`virtualization`**: CPU virtualization (SVM / VT-x) enabled, hypervisorlaunchtype, VirtualMachinePlatform / HypervisorPlatform features  
   How: `Get-CimInstance Win32_Processor | select VirtualizationFirmwareEnabled ; bcdedit /enum {current} | findstr hypervisorlaunchtype ; Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform,HypervisorPlatform`
30. **`mdns`**: Bonjour / National Instruments mDNS component present  
   How: `Get-Service 'Bonjour Service' -EA 0 ; Test-Path C:\Windows\System32\mdnsNSP.dll, C:\Windows\System32\nimdnsResponder.dll, C:\Windows\SysWOW64\nimdnsResponder.dll ; Uninstall DisplayName match 'Bonjour|National Instruments' ; netsh winsock show catalog | findstr /i mdns`
31. **`display_mode`**: Monitor / resolution / refresh rate, disabled monitor, multi-monitor  
   How: `Get-CimInstance Win32_VideoController | select CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate ; Get-PnpDevice -Class Monitor | select FriendlyName,Status ; Get-CimInstance -Namespace root/wmi WmiMonitorID (monitor count)`
32. **`hvci`**: Memory Integrity (HVCI) / VBS state  
   How: `Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard Win32_DeviceGuard | select SecurityServicesRunning,VirtualizationBasedSecurityStatus (2 in SecurityServicesRunning = HVCI running) ; HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity Enabled`
33. **`hvci_incompat_drivers`**: Known incompatible drivers blocking HVCI / Vanguard (gdrv3.sys, xhunter1.sys, ene.sys, se64a.sys, mtkwl6ex.sys)  
   How: `Test-Path C:\Windows\System32\drivers\{gdrv3,xhunter1,ene,se64a,mtkwl6ex}.sys ; sc query ene / sc query se64a ; pnputil /enum-drivers (Original Name) ; Windows Security > Core isolation incompatible driver list`
34. **`ram_config`**: RAM amount, channel count, XMP/EXPO (ConfiguredClockSpeed vs Speed)  
   How: `Get-CimInstance Win32_PhysicalMemory | select BankLabel,Capacity,Speed,ConfiguredClockSpeed ; warn on <=8 GB or a single stick ; ConfiguredClockSpeed below module rating = XMP/EXPO likely off`
35. **`tcp_stack`**: TCP/Winsock stack: congestion provider, MTU, buffer settings  
   How: `netsh int tcp show supplemental ; netsh interface ipv4 show subinterfaces (MTU) ; netsh winsock show catalog (LSPs)`
36. **`bt_hfp`**: Bluetooth headset falling back to Hands-Free (HFP) profile  
   How: `Get-PnpDevice -Class AudioEndpoint | ? FriendlyName -match 'Hands-Free|Headset' ; Get-Service BTAGService | select Status,StartType ; default recording/communication device is the BT headset (Sound control panel / MMDevices registry)`
37. **`nahimic`**: Nahimic / A-Volute audio service installed and running  
   How: `Get-Service *Nahimic* | select Name,Status,StartType ; Get-Process *nahimic* -EA 0 ; Win32_ComputerSystem.Manufacturer (Lenovo/MSI/ASUS/Alienware)`
38. **`fso_mpo`**: Fullscreen optimizations and MPO (Multi-Plane Overlay) state  
   How: `AppCompatFlags\Layers value for '...\VALORANT-Win64-Shipping.exe' containing DISABLEDXMAXIMIZEDWINDOWEDMODE ; HKLM\SOFTWARE\Microsoft\Windows\Dwm OverlayTestMode (5 = MPO off) ; duplicate/conflicting MPO DWORDs under HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`
39. **`pending_reboot`**: Pending reboot / real reboot done after Vanguard install / Fast Startup enabled  
   How: `Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending' ; HKLM\SYSTEM\CurrentControlSet\Control\Session Manager PendingFileRenameOperations ; (Get-CimInstance Win32_OperatingSystem).LastBootUpTime vs vgk.sys LastWriteTime ; HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power HiberbootEnabled (1 = Fast Startup)`
40. **`pnp_errors`**: Devices with yellow warning / error / disabled state in Device Manager  
   How: `Get-PnpDevice -PresentOnly | ? Status -ne 'OK' | select Class,FriendlyName,Status,Problem ; Get-CimInstance Win32_PnPEntity | ? ConfigManagerErrorCode -ne 0`
41. **`disk_type`**: Game installed on HDD / disk health  
   How: `Get-Partition -DriveLetter <game drive> | Get-Disk | Get-PhysicalDisk | select MediaType,HealthStatus ; Get-PhysicalDisk | Get-StorageReliabilityCounter ; System log providers disk, Ntfs`
42. **`shader_cache`**: Shader cache state / first launches after new driver or GPU  
   How: `$env:LOCALAPPDATA\NVIDIA\DXCache, $env:LOCALAPPDATA\AMD\DxCache folder size/date ; GPU driver install date (Win32_VideoController.DriverDate)`
43. **`gpu_pref`**: Which GPU VALORANT runs on / monitor plugged into motherboard output  
   How: `HKCU\Software\Microsoft\DirectX\UserGpuPreferences value for the VALORANT-Win64-Shipping.exe path (GpuPreference=2 = high performance) ; Win32_VideoController with non-null CurrentHorizontalResolution = adapter driving the monitor ; Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' filtered by pid`
44. **`mouse_settings`**: Mouse acceleration (Enhance pointer precision) and polling/USB  
   How: `HKCU\Control Panel\Mouse MouseSpeed (1 = EPP on), MouseThreshold1/2 ; Get-PnpDevice -Class Mouse,HIDClass`
45. **`other_anticheat`**: Other anti-cheats / escalation services (FACEIT, ESEA, XIGNCODE3, Razer Synapse game escalation)  
   How: `Uninstall DisplayName match 'FACEIT|ESEA' ; Get-Service | ? DisplayName -match 'FACEIT|ESEA|Razer' ; Test-Path C:\Windows\System32\drivers\xhunter1.sys`
46. **`riot_elevation`**: RiotClientServices.exe running elevated / its compatibility flags  
   How: `HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers value named '<path>\Riot Client\RiotClientServices.exe' containing RUNASADMIN / WIN8RTM ; elevation of the running process (token check / Process Explorer)`
47. **`vgc_cpu`**: vgc.exe CPU usage  
   How: `Get-Counter '\Process(vgc)\% Processor Time' -SampleInterval 2 -MaxSamples 10 ; Get-Process vgc | select CPU,ProcessorAffinity`
48. **`virtual_nic_vpn`**: VPN / virtual network adapters and network-filtering software (TAP, VMware, VirtualBox, GlobalProtect, NetLimiter, Docker Desktop, ExitLag)  
   How: `Get-NetAdapter -IncludeHidden | ? InterfaceDescription -match 'TAP|VMware|VirtualBox|WAN|Hyper-V|Wintun|WireGuard' ; Uninstall keys (HKLM/HKCU ...\CurrentVersion\Uninstall) DisplayName match 'GlobalProtect|NetLimiter|Docker Desktop|ExitLag|VPN' ; Get-Process ExitLag -EA 0`
49. **`system_files`**: Windows system file integrity  
   How: `sfc /verifyonly ; DISM /Online /Cleanup-Image /ScanHealth ; C:\Windows\Logs\CBS\CBS.log`
50. **`tpm_events`**: TPM Event ID 14 / 17 in the System log (fTPM issues)  
   How: `Get-WinEvent -FilterHashtable @{LogName='System';Id=14,17} | ? ProviderName -match 'TPM'`
51. **`valorant_config`**: VALORANT user config folder (corrupt settings / keybinds)  
   How: `Test-Path $env:LOCALAPPDATA\VALORANT\Saved\Config ; date/size of GameUserSettings.ini and input ini files`
52. **`nv_3d_settings`**: NVIDIA 3D settings: Vertical sync = Fast, G-Sync, frame-cap overrides, NVIDIA app automatic optimization  
   How: `Not reliably readable via API: export global/VALORANT profile with NVIDIA Profile Inspector, otherwise ask user for a screenshot of NVIDIA Control Panel > Manage 3D settings`
53. **`usb_devices`**: Problematic USB devices (external CD/Blu-ray drive, wireless dongle, mouse port)  
   How: `Get-PnpDevice -Class CDROM -PresentOnly ; Get-CimInstance Win32_DiskDrive | ? InterfaceType -eq 'USB' ; Get-PnpDevice -Class Mouse,HIDClass -PresentOnly`
54. **`stale_dat`**: Stale C:\Windows\392667600.dat file  
   How: `Test-Path C:\Windows\392667600.dat`
55. **`hags_windowed`**: HAGS, 'Optimizations for windowed games' and Game Mode state  
   How: `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers HwSchMode (2 = on) ; HKCU\Software\Microsoft\DirectX\UserGpuPreferences DirectXUserGlobalSettings (SwapEffectUpgradeEnable=) ; HKCU\Software\Microsoft\GameBar AutoGameModeEnabled`
56. **`intel_driver`**: Intel iGPU / chipset driver outdated  
   How: `Get-CimInstance Win32_VideoController | ? Name -match 'Intel' | select DriverVersion,DriverDate ; Get-CimInstance Win32_PnPSignedDriver | ? Manufacturer -match 'Intel' | select DeviceName,DriverVersion,DriverDate`
57. **`priority`**: VALORANT / Discord / vgc forced to High/Realtime priority; Process Lasso rules  
   How: `Get-Process VALORANT-Win64-Shipping,Discord,vgc -EA 0 | select Name,PriorityClass,ProcessorAffinity ; HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>\PerfOptions CpuPriorityClass ; Get-Process ProcessLasso -EA 0`
58. **`standby_mem`**: Standby memory / non-paged pool growth  
   How: `Get-Counter '\Memory\Available MBytes','\Memory\Standby Cache Normal Priority Bytes','\Memory\Pool Nonpaged Bytes' ; RAMMap`
59. **`disk_dirty`**: NTFS dirty bit / disk health  
   How: `fsutil dirty query C: ; Get-PhysicalDisk | select FriendlyName,HealthStatus ; System log providers Ntfs, disk, Chkdsk`
60. **`ifeo`**: Image File Execution Options entries (DevOverrideEnable, Debugger hooks)  
   How: `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options DevOverrideEnable ; Debugger value under subkeys for VALORANT/Riot executables`
61. **`runtimes`**: VC++ 2015-2022 x86/x64 and DirectX (xinput1_3.dll) components; SideBySide errors  
   How: `Uninstall DisplayName match 'Visual C\+\+ 2015-2022' (x86 and x64) ; Test-Path C:\Windows\System32\xinput1_3.dll, C:\Windows\SysWOW64\xinput1_3.dll ; Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='SideBySide'}`
62. **`hybrid_cpu`**: Intel hybrid CPU (P/E cores) and background apps eating CPU  
   How: `Get-CimInstance Win32_Processor | select Name,NumberOfCores,NumberOfLogicalProcessors (Intel 12th gen+ = hybrid) ; Get-Process | sort CPU -desc | select -First 10`
63. **`iommu_dma`**: IOMMU / Kernel DMA Protection state  
   How: `msinfo32 'Kernel DMA Protection' ; Win32_DeviceGuard AvailableSecurityProperties contains 3 (DMA protection) ; VT-d / AMD-Vi in BIOS`
64. **`oem_tools`**: OEM / network tools (Lenovo Vantage, Killer Intelligence Center, ExitLag, MSI X3D mode)  
   How: `Get-AppxPackage *Vantage* | select Version ; Get-Service Killer* -EA 0 ; Get-Process ExitLag -EA 0 ; Win32_BaseBoard (MSI) + Win32_Processor.Name (X3D)`
65. **`clock`**: System date/time correct  
   How: `w32tm /query /status ; w32tm /stripchart /computer:time.windows.com /samples:1 /dataonly (offset) ; Get-Service W32Time`
66. **`dualboot`**: Dual boot / Windows Boot Manager on another disk / GRUB first in boot order  
   How: `bcdedit /enum firmware ; disk number of the ESP vs disk of C: (Get-Partition) ; Secure Boot state`
67. **`hotfix`**: Update reported as problematic installed (KB5101650)  
   How: `Get-HotFix -Id KB5101650 -EA 0`
68. **`vmd`**: Intel VMD / RST controller (mainly Dell/Alienware)  
   How: `Get-PnpDevice | ? FriendlyName -match 'VMD|Rapid Storage' ; Get-CimInstance Win32_ComputerSystem | select Manufacturer`
69. **`env_vars`**: Basic system environment variables missing  
   How: `[Environment]::GetEnvironmentVariables('Machine') must contain TEMP, TMP, ComSpec, Path, windir ; HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`
70. **`riot_status`**: Riot-side incident / maintenance  
   How: `Riot service status page / client banner (no local detection)`
71. **`amd_settings`**: AMD Adrenalin features (Anti-Lag, AFMF, FSR, Radeon Boost/Chill, FRTC, Enhanced Sync, overlay, GPU Scaling)  
   How: `Weak programmatic access: UMD keys under HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\00xx, or screenshot from user ; RadeonSoftware.exe process check`
72. **`thermals`**: CPU/GPU temperature and thermal throttling  
   How: `No reliable built-in source: Get-CimInstance -Namespace root/wmi MSAcpi_ThermalZoneTemperature (missing on many PCs) ; in practice HWiNFO / LibreHardwareMonitor CSV log ; '% Processor Performance' counter`

Of the 72 checks, the top 10 together cover **529** of the 732 catalogued threads, and the top 20 cover **555**.

## Auto-fix candidates by risk

All of these need the user's consent before anything is applied. Low risk means the change is reversible and does not touch firmware or the boot path, so the tool can apply it after one confirmation. Medium risk means a registry, service or driver change that can have side effects: show a diff, make a backup or restore point first, and apply only on an explicit click. High risk means BIOS, Secure Boot, partitions, safe mode or a reformat. The tool should only explain these step by step and never perform them.

### Low risk (safe to auto-apply with consent)

| Problem | Threads | Auto-fix | Action | Admin | Reboot |
|---|---|---|---|---|---|
| `realtek-2-5gbe-nic-stutter` | 76 | yes | If the Realtek adapter is not the active connection: Disable-NetAdapter on it (reversible). If it is the active connection: offer to disable EEE/Green Ethernet/Interrupt Moderation and point to the vendor 'NDIS Not… | yes | no |
| `third-party-hook-overlay-conflict` | 49 | partial | List detected hooking processes/services and offer to close them for the session (Stop-Process) or disable the service (Set-Service -StartupType Disabled); uninstall stays manual | no | no |
| `bluetooth-headset-hfp-voice-chat` | 29 | yes | Disable BTAGService (Set-Service -StartupType Disabled + Stop) or guide disabling 'Handsfree Telephony' on the headset; user loses headset mic | yes | yes |
| `nahimic-audio-service-launch-hang` | 29 | yes | Stop-Service + Set-Service -StartupType Disabled for the Nahimic service (reversible); warn that Windows Update/OEM tools may reinstall it | yes | no |
| `power-plan-throttling-low-fps` | 21 | partial | Switch to High performance / Best performance power plan (powercfg /setactive), warn when on battery, report HDD/RAM/throttle findings; cooling and BIOS reverts are manual | no | no |
| `riot-client-update-stuck` | 17 | yes | Close Riot processes and delete %LOCALAPPDATA%\Riot Games\Riot Client (cache/login only), then Repair; full clean reinstall is guided | no | no |
| `shader-cache-compilation-stutter` | 16 | partial | Explain warm-up (play Range/matches); optionally clear DirectX shader cache with consent; driver-setting changes are manual | no | no |
| `usb-selective-suspend-freeze` | 13 | yes | powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 (and DC) + powercfg /setactive SCHEME_CURRENT | no | no |
| `display-misc-blur-wrong-res` | 11 | partial | Re-enable disabled monitor device (Enable-PnpDevice); other items guided | yes | no |
| `low-gpu-usage-after-ue5-patch` | 11 | partial | Set high-performance GPU preference and High performance power plan; driver update/DDU is guide-only | no | no |
| `audio-device-config-issues` | 10 | partial | Report device/driver/per-app output; exclusive-mode and device toggles are guided | no | no |
| `riot-client-not-elevated` | 10 | yes | Add RUNASADMIN compatibility flag for RiotClientServices.exe (not for VALORANT-Win64-Shipping.exe) and restart the client | yes | no |
| `valorant-config-corrupt` | 9 | yes | Back up then delete %LOCALAPPDATA%\VALORANT\Saved\Config (user loses local settings; crosshair codes should be saved first) | no | no |
| `launch-shortcut-tray-failure` | 8 | partial | Detect hidden RiotClientServices process and recreate shortcut to RiotClientServices.exe --launch-product=valorant --launch-patchline=live | no | no |
| `voice-chat-network-dns-ipv6` | 8 | partial | Offer public DNS (Set-DnsClientServerAddress) and IPv6 unbind, reversible | yes | no |
| `fps-decay-over-time` | 7 | partial | Suggest Fullscreen mode; driver rollback guided | no | no |
| `fps-lower-than-expected-caps` | 7 | partial | Report XMP/EXPO and RAM config; suggest menu FPS cap 30 and caps check; BIOS XMP is manual | no | no |
| `intel-graphics-chipset-driver-outdated` | 5 | partial | Flag old Intel driver dates and open Intel Driver & Support Assistant | yes | yes |
| `mouse-acceleration-floaty-aim` | 5 | yes | Set HKCU\Control Panel\Mouse MouseSpeed=0, MouseThreshold1=0, MouseThreshold2=0 (EPP off); windowed-games optimization toggle with consent | no | no |
| `vgc-service-not-running` | 5 | yes | sc config vgc start= auto ; net start vgc (or restart vgc + vgtray.exe without reboot) | yes | no |
| `vgk-not-loaded-needs-reboot` | 5 | yes | Delete C:\Windows\vgkbootstatus.dat if present and perform a full restart (shutdown /r /t 0, not Fast Startup shutdown) | yes | yes |
| `vsync-gsync-tearing-fps-lock` | 5 | partial | Report refresh rate vs in-game cap; NVCP changes guided | no | no |
| `frametime-latency-spikes` | 4 | partial | Report Discord/overlays and network jitter; driver rollback guided | no | no |
| `third-party-antivirus-conflict` | 4 | partial | Detect AV product; add Defender exclusions; third-party AV exceptions/uninstall are guided | yes | no |
| `wrong-gpu-or-display-output` | 4 | partial | Set HKCU\Software\Microsoft\DirectX\UserGpuPreferences GpuPreference=2 for VALORANT; re-enable a disabled iGPU device (Enable-PnpDevice); cable move is manual | no | no |
| `corrupt-system-or-game-files` | 3 | yes | Run sfc /scannow (and DISM /Online /Cleanup-Image /RestoreHealth) with consent | yes | yes |
| `dual-network-adapter-desync` | 3 | yes | Disable the unused adapter (Disable-NetAdapter) or set interface metrics, reversible | yes | no |
| `nvidia-vsync-fast-critical-error` | 3 | partial | Guide user to set Vertical sync to 'Use the 3D application setting' (no stable API to change it) | no | no |
| `patch-13-01-fps-drop` | 3 | partial | Suggest Repair/reinstall of Valorant+Vanguard; report non-paged pool size | yes | yes |
| `high-process-priority-van136-185` | 2 | yes | Reset VALORANT and Discord priority to Normal/Above normal and remove IFEO PerfOptions CpuPriorityClass overrides | yes | no |
| `side-by-side-config-error` | 2 | partial | Detect missing VC++ 2015-2022 x86/x64 and offer the Microsoft installer | yes | no |
| `standby-memory-stutter` | 2 | partial | Report standby/free memory; ISLC suggestion is guide-only | no | no |
| `val57-59-ipv6-killer` | 2 | partial | Offer to unbind IPv6 on the active adapter (Disable-NetAdapterBinding -ComponentID ms_tcpip6), reversible | yes | no |
| `wrong-system-clock-offline` | 2 | yes | Start W32Time and w32tm /resync, enable automatic time | yes | no |
| `razer-synapse-escalation-kick` | 1 | partial | Offer to disable the Razer Synapse game escalation service | yes | no |
| `riot-client-hardware-acceleration` | 1 | partial | Guide user to Riot Client settings to turn off hardware acceleration | no | no |

### Medium risk (apply with backup + explicit confirmation)

| Problem | Threads | Auto-fix | Action | Admin | Reboot |
|---|---|---|---|---|---|
| `local-network-packet-loss-jitter` | 30 | partial | ipconfig /flushdns, /release, /renew and netsh winsock reset with consent; advise Ethernet/5 GHz; TCP congestion tweaks guided | yes | yes |
| `van185-vanguard-disconnects` | 24 | partial | Set vgc to Automatic, add firewall allow rules for Riot Client/VALORANT/vgc, flush DNS; DNS/IPv6/Teredo changes offered with consent; AV removal manual | yes | no |
| `amd-adrenalin-driver-regression` | 21 | partial | Detect driver version against reported-bad list and recommend rollback (DDU clean install to 26.1.1/26.3.1/26.6.1); remove duplicate MPO DWORDs; DDU itself is guide-only | yes | yes |
| `whole-pc-freeze-kernel-power-41` | 21 | partial | Collect evidence (event IDs, driver versions) and run the sub-checks; apply the safe items (USB selective suspend off, unused Realtek NIC off); driver rollback/BIOS update are guide-only | yes | no |
| `mdns-bonjour-ni-val43-loading` | 19 | partial | Detect Bonjour/NI and the faulting module; open the uninstaller (Bonjour / National Instruments) with consent; then netsh winsock reset and reboot. DLL deletion via takeown/icacls is guide-only | yes | yes |
| `vanguard-install-loop` | 18 | yes | Remove leftover services (sc delete vgc / vgk), reboot, delete C:\Program Files\Riot Vanguard, relaunch Riot Client; check Defender protection history for blocked Riot items | yes | yes |
| `vanguard-corrupt-install` | 15 | partial | Clean Vanguard reinstall: sc delete vgc, sc delete vgk, reboot, remove C:\Program Files\Riot Vanguard, relaunch Riot Client to reinstall, reboot. Safe-mode registry deletion stays guide-only | yes | yes |
| `vpn-anticheat-network-filter-conflict` | 10 | partial | Disable detected virtual/VPN adapters (reversible); enable VirtualMachinePlatform/HypervisorPlatform (Enable-WindowsOptionalFeature) with consent; uninstalling VPN/FACEIT is manual | yes | yes |
| `gpu-driver-corrupt-needs-clean-install` | 7 | partial | Detect driver version/date and TDR 4101 events; recommend DDU + clean install of a stable version (guide) | yes | yes |
| `vgc-high-cpu` | 7 | partial | Add Defender exclusions for Riot Games / Riot Vanguard folders (Add-MpPreference -ExclusionPath) with consent; vgc affinity tweak is per-session | yes | no |
| `fast-startup-stale-dat-bsod` | 6 | partial | Delete C:\Windows\392667600.dat and set HiberbootEnabled=0 (disable Fast Startup), then reboot; offline sfc/chkdsk is guide-only | yes | yes |
| `igpu-disabled-or-driverless-freeze` | 6 | partial | If iGPU shows as driverless/unknown: point to AMD chipset/iGPU driver package. Enabling iGPU in BIOS (Integrated Graphics = Auto) is guide-only | no | yes |
| `fullscreen-optimizations-mpo` | 5 | yes | Set DISABLEDXMAXIMIZEDWINDOWEDMODE for VALORANT-Win64-Shipping.exe; optionally set OverlayTestMode=5 (MPO off) with backup of the key | yes | yes |
| `incompatible-driver-component` | 5 | yes | Uninstall/update the owning RGB/monitor software; rename C:\Windows\System32\drivers\ene.sys to .old / sc delete se64a (safe mode if locked). MediaTek driver replacement is guided | yes | yes |
| `network-exceptions-download-stuck` | 5 | partial | Add Windows Firewall allow rules for Riot Client, Vanguard and VALORANT; ipconfig /flushdns; Winsock/TCP reset with consent; VPN/WARP is user choice | yes | yes |
| `freeze-on-click-lobby-misc` | 4 | partial | Detect btmmhook.dll / old Bluetooth software and offer uninstall; profile change is manual | yes | no |
| `ftpm-event-14-17-stutter` | 4 | partial | Disable USB selective suspend (safe); BIOS/AGESA update and TPM changes are guide-only | no | no |
| `launch-fails-something-unusual` | 4 | partial | Detect IFEO DevOverrideEnable / Debugger values and offer to reset (DevOverrideEnable=0) with registry backup | yes | yes |
| `intel-hybrid-cpu-ecore-stutter` | 3 | partial | Per-session affinity/priority for VALORANT-Win64-Shipping, VALORANT.exe, vgc.exe to P-cores (resets each launch) | yes | no |
| `windows-update-kb5101650-microstutter` | 2 | partial | Detect KB5101650 and offer wusa /uninstall /kb:5101650 with consent | yes | yes |
| `xinput-directx-runtime-error` | 2 | partial | Detect xinput1_3.dll state; guide DirectX runtime repair/reinstall | yes | no |
| `missing-environment-variables` | 1 | partial | Detect missing TEMP/TMP/ComSpec/Path and show the defaults; writing machine env vars only with consent | yes | yes |
| `vanguard-blocks-chkdsk` | 1 | partial | Detect dirty bit; guide: exit Vanguard from tray / uninstall, run chkdsk C: /f /r, reinstall | yes | yes |
| `windows-version-too-old` | 1 | partial | Detect build < 19045 and guide upgrade via Microsoft ISO / Update Assistant | yes | yes |

### High risk (guide only)

| Problem | Threads | Auto-fix | Action | Admin | Reboot |
|---|---|---|---|---|---|
| `secure-boot-enable-boot-failure` | 32 | no | Guide only: pre-flight check before the user touches BIOS (GPT? UEFI? CA 2023 cert? BIOS age?), recommend mbr2gpt / BIOS update / factory keys; warn about BitLocker | yes | yes |
| `hvci-memory-integrity-required` | 27 | partial | Report HVCI/virtualization/incompatible drivers; enable Memory Integrity via HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity Enabled=1 only after the driver check passes; BIOS… | yes | yes |
| `outdated-bios-reboot-vgk-bsod` | 19 | no | Guide only: show BIOS version/date and board model, link vendor support page; remind to back up BitLocker key and re-enable Secure Boot/TPM afterwards | yes | yes |
| `secure-boot-not-detected-van9003` | 19 | no | Guide only: report exact state (Secure Boot off / Setup mode / CSM / MBR) and show the matching BIOS steps (disable CSM, Restore Factory Keys, re-toggle Secure Boot) | yes | yes |
| `tpm2-missing-or-unmet` | 8 | no | Guide: enable fTPM/PTT in BIOS or add a TPM 2.0 module; nothing to auto-fix | yes | yes |
| `insider-build-vgk-bsod` | 3 | no | Guide: boot recovery/safe mode and remove C:\Program Files\Riot Vanguard\vgk.sys, move to stable Windows build | yes | yes |
| `iommu-restriction` | 3 | no | Guide: enable IOMMU/VT-d/AMD-Vi + SVM in BIOS, update audio/Wi-Fi drivers | yes | yes |
| `dual-boot-secure-boot-van1` | 2 | no | Guide: put Windows Boot Manager first or create an ESP on the Windows disk | yes | yes |
| `intel-vmd-status-vmd-9001` | 2 | no | Guide: bcdedit /set {current} safeboot minimal, disable VMD in BIOS, boot, remove safeboot flag (risk: unbootable if done wrong) | yes | yes |
| `msi-x3d-game-mode-lobby-freeze` | 1 | no | Guide: disable X3D Gaming Mode in MSI BIOS | no | yes |
| `wdfilter-bsod` | 1 | no | Guide: safe-mode uninstall of Riot/Vanguard, reset Windows Security, reinstall | yes | yes |

Detection-only or not fixable locally (ISP routing, Riot-side incidents, account issues, console): `isp-routing-high-ping`, `stretched-resolution-issues`, `mouse-usb-port-polling-lag`, `secure-boot-policy-wave-2026`, `account-mfa-matchmaking-glitches`, `console-ps5-errors`, `replay-system-issues`, `unexplained-launch-failure`, `van-restriction-hwid`, `volmgr-161-black-screen-fan`, `discord-stream-no-voice`, `lenovo-vantage-wifi-collapse`, `mouse-clicks-stop-after-alt-tab`, `nvidia-driver-packet-loss`, `riot-server-side-incident`, `slow-download-riot-cdn`, `terms-not-accepted-failed-to-launch`, `usb-optical-drive-restart`, `van9101-attestation`.

## Known limitations

- **Coverage:** only 952 of 7,821 threads (12.2%) were analysed in depth, and 732 of them produced catalogued problems. Thread counts are therefore relative frequencies among *solved* threads. They are not absolute prevalence. Unsolved and unanswered threads (2,180 with zero replies) are not represented at all. Problems without a known fix (e.g. VAN 9101, STATUS_VMD_9001, some Kernel-Power 41 freezes) are probably under-counted.
- **The 'solved' label is a heuristic.** Threads were picked by flair, keywords or OP replies, not by an explicit 'solved' marker. Some 'op_confirmed' fixes are one person's report and were never followed up, and a few OPs later said the problem came back.
- **Truncated text:** long post bodies and comment chains were cut off during extraction. Some fixes are therefore incomplete (e.g. 'OP fixed it, method not stated', or linked guides whose content was not in the thread).
- **Source:** the archive comes from Arctic Shift (a Reddit archive), not the live API. Deleted or edited posts and late comments may be missing or out of date.
- **Merging is approximate:** chunks overlapped, and equivalent fixes were merged by similar wording plus a few action keys (e.g. 'disable Realtek adapter', 'Cloudflare WARP', 'update BIOS'). Fix counts are summed reports across merged records. A thread can be counted once per problem, and some threads belong to more than one problem.
- **Error-code counts in the overview come from all posts** (regex matches), while the per-problem codes come only from the solved-thread extraction. The two sets are not directly comparable.
- **Diagnostic methods are proposed, not tested.** The checks and Windows methods (cmdlets, registry paths, event IDs) were designed from the reported symptoms and signals, and none have been run on real machines yet. Some settings (NVIDIA/AMD control panel, BIOS options, temperatures) cannot be read reliably without third-party tools or a screenshot from the user.
- **Version-specific facts go stale:** the known-bad driver versions (AMD 26.5.x/26.7.1/26.8.1, NVIDIA 581.x/591.x), KB5101650, patch 13.01 and the Oct 5 2026 patch are snapshots. Keep them in an updatable rules file, not hard-coded.
