# Tech Stack Research: VALORANT PC Diagnostic App (Windows 10/11)

Research date: 2026-10-21. Legend: **[V]** = checked against a primary or official source during this research. **[C]** = community or third-party source, or reports that disagree. **[T]** = unverified, must be tested on a real machine with Vanguard running before shipping.

## Hard constraint: coexisting with Vanguard

- The app ships **no kernel driver at all**, not even a "safe" signed one. Vanguard (vgk) blocks drivers it considers exploitable, and other anti-cheats have blocked even PawnIO's signer (see section 2.4). Not shipping a driver removes that whole risk.
- The app never opens a handle to `VALORANT-Win64-Shipping.exe` with VM_READ/WRITE, never injects, never hooks, and never draws an overlay. We get frame data only from **ETW** (an OS facility the game cannot see) and network data from OS tables and ETW.
- The app does not call Riot's local client API (lockfile/GLZ endpoints) by default. It is unofficial and a grey area under the ToS. If we ever add it, it is opt-in and read-only.

---

## 1. Language / UI

| Option | Deep Windows access | Single-file exe size | Startup | Contributors | Theming |
|---|---|---|---|---|---|
| **.NET 10 + WPF** | Best: System.Management (WMI), TraceEvent (ETW), Microsoft.Win32 registry, ServiceController, EventLog, P/Invoke SetupAPI/CfgMgr32, COM for firewall/Task Scheduler | ~60-70 MB self-contained (no trimming), roughly 30-45 MB with `EnableCompressionInSingleFile` [T: measure] | Good, about 0.5-1.5 s cold | Huge C# pool, mature tooling | Built-in Fluent theme (`ThemeMode`, .NET 9+; Microsoft says Fluent support is "still in progress" in .NET 10 [V]) or **WPF-UI** (lepoco, MIT) |
| WinUI 3 | Same .NET APIs | Unpackaged needs the Windows App SDK runtime bundled or installed; single-file is awkward [C] | OK | Smaller pool, more friction | Best native Fluent look |
| Avalonia 11 | Same .NET APIs (Windows-only parts by P/Invoke) | Plain `dotnet publish`, self-contained works; trim-friendly [C] | Good | Growing | Good (Fluent theme) |
| Tauri 2 (Rust + WebView2) | Windows-rs crate covers it, but WMI/ETW/SetupAPI glue is much more work | Tiny installer (~3 MB) [C] | Waits for WebView2 to start (~300 ms in one benchmark) [C] | Web UI is easy, the Rust system layer is hard | Excellent (CSS) |
| Electron | Needs a native helper or PowerShell calls | ~85 MB+ [C] | Slower, heavy RAM | Easy | Excellent |
| Python (PySide/Tk) | pywin32/wmi work | PyInstaller bundles are large and **frequently flagged by AV** | Slow | Easy | Weak or medium |

**Recommendation: C# on .NET 10 LTS + WPF (with the WPF-UI library or the built-in Fluent theme), MVVM (CommunityToolkit.Mvvm), and resx localization (en, tr).**
- .NET 8 and 9 both reach end of support on **2026-11-10**. .NET 10 is LTS until November 2028 [V]. Start on .NET 10.
- WPF has the shortest route to every API we need, and the most Stack Overflow answers for Windows admin tooling.
- Downside: **WPF cannot be trimmed or compiled Native AOT** (the SDK disables trimming for WPF) [V], so the exe stays around 30-70 MB. That is acceptable for this kind of tool. If size ever becomes critical, Avalonia is the fallback with the same C# core.
- Architecture: keep the engine (scan, rules, fixes, journal, monitoring) in a UI-free class library `Core` that has a CLI host. Then a UI swap is cheap, tests run headless, and the elevated worker can be separated later.
- Elevation: the manifest sets `requireAdministrator`. Most fixes need admin, and one UAC prompt at start is simpler than brokering.

## 2. Live monitoring without injection

### 2.1 Frametimes: PresentMon (Intel, **MIT license** [V])
- ETW-based. It consumes DXGI/D3D9/DxgKrnl/Win32k providers and never touches the process [V].
- Ship the **PresentMon console exe** (MIT allows redistribution) and run it as a child process. Alternatively, re-implement the consumer with TraceEvent later.
- Command line [V, README-ConsoleApplication]:
  `PresentMon.exe --process_name VALORANT-Win64-Shipping.exe --output_stdout --no_console_stats --session_name VTDiag --stop_existing_session --terminate_on_proc_exit`
  Parse CSV columns such as `MsBetweenPresents`, `MsBetweenDisplayChange`, `MsUntilDisplayed`, `PresentMode` (it reveals Independent Flip vs Composed, which is useful for MPO/fullscreen-optimization hypotheses), and `AllowsTearing`. `--track_pc_latency` exists but is beta.
- **Permissions:** you need admin or membership in the **Performance Log Users** group, or you get "failed to start trace session (access denied)" [V README]. Our app is already elevated. `--restart_as_admin` exists.
- Use a unique `--session_name` so the app does not collide with a user-run PresentMon, CapFrameX, or similar, and always stop the session on exit, including after a crash (`--terminate_existing_session` on the next start).
- Vanguard: ETW consumers are not drivers and never open the game process [T: confirm no VAN error after a 30-min match].

### 2.2 System counters (PDH, no admin needed for most)
- CPU: `\Processor Information(_Total)\% Processor Utility`, plus `% of Maximum Frequency`. A low value under load is a strong **throttling signal** without needing a temperature.
- GPU (vendor-neutral, WDDM 2.x): `\GPU Engine(*engtype_3D)\Utilization Percentage` (filter on pid_ for the game), `\GPU Adapter Memory(*)\Dedicated Usage`, `\GPU Process Memory(*)`.
- Memory, disk queue, and `\Network Interface(*)\Bytes Total/sec`.
- Use `System.Diagnostics.PerformanceCounter` or the PDH API directly (lower overhead). Sample at 1 Hz during matches.

### 2.3 GPU vendor metrics (user-mode APIs that ship with the driver, no extra driver)
- **NVIDIA NVML** (`nvml.dll`, installed with the GeForce driver): temperature, clocks, power, throttle reasons (`nvmlDeviceGetCurrentClocksThrottleReasons`), utilization. Load it dynamically. Do not redistribute the DLL.
- **AMD ADLX** (GPUOpen): `IADLXGPUMetrics` gives GPU temperature, hotspot, usage, fan, and VRAM temperature [V docs]. C# bindings are available. Check the license file in the SDK before vendoring headers or bindings [T].
- Intel Arc: IGCL (Intel Graphics Control Library) is optional and low priority.

### 2.4 CPU temperature
- **Do not use LibreHardwareMonitor/OpenHardwareMonitor with WinRing0.** WinRing0 is flagged by Defender as `VulnerableDriver:WinNT/Winring0` [V] and is on the driver blocklist. Vanguard also blocks such drivers and has been reported to break HWiNFO/HWMonitor/Afterburner readings [C].
- **PawnIO** (LHM's 2025-26 replacement) is still a kernel driver. One anti-cheat (FACEIT) blocked it because of its signer [C, PawnIO.Setup issue #1]. **Do not ship it.**
- **Safe approach:** (a) measure **throttling instead of temperature**: `% of Maximum Frequency` under load, and Event Log `Microsoft-Windows-Kernel-Processor-Power` event 37 ("speed limited by system firmware"); (b) ACPI `\Thermal Zone Information(*)\Temperature` counter or WMI `root\wmi:MSAcpi_ThermalZoneTemperature`. These are often inaccurate or static and must be labeled "approximate"; (c) **optional read-only import**: if the user already runs HWiNFO with shared memory enabled, read its shared-memory block (no driver from us; mention HWiNFO's free-version limits [T]). Otherwise leave CPU temperature out.

### 2.5 DPC/ISR latency (like LatencyMon)
- Use an ETW kernel trace with `EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT`. Microsoft's guidance says DPCs over ~100 µs and ISRs over ~25 µs are bad, and you need the QPC clock for resolution [V].
- In C#, use **Microsoft.Diagnostics.Tracing.TraceEvent** (MIT): `KernelTraceEventParser.Keywords.DeferedProcedureCalls | Interrupt` gives `PerfInfoDPC` and `PerfInfoISR` events with routine addresses. Map those addresses to drivers through the loaded-module list (`ImageLoad` rundown), which gives "nvlddmkm.sys max 850 µs". Use a **private or own-named kernel session** (Win8+ system-logger mode) rather than "NT Kernel Logger", so we don't fight other tools. Admin is required.
- Run it as a separate **60-second "latency test"** while idle or in the Range. Do not run it continuously during a match, because overhead and event volume are high.

### 2.6 Network: ping, jitter, loss
- Riot does not publish official per-region ping endpoints. Community lists of IPs are third-party and go stale, and ICMP is often filtered [C].
- **Recommended, passive and accurate:** while the game runs, find its live **UDP remote endpoint**. Use the ETW `Microsoft-Windows-Kernel-Network` UDP send/recv events filtered to the game PID (UDP tables don't contain the remote address). Then compute RTT, jitter, and loss to *that exact server* with a parallel low-rate ICMP probe (1-2/s), and fall back to traceroute hops when ICMP is blocked.
- **Pre-match:** probe a maintained, versioned JSON of region hosts (community-sourced, updatable without a release) with ICMP + TCP/443 connect timing, and label the results "indicative".
- Also read adapter stats (`Get-NetAdapterStatistics`: errors/discards), Wi-Fi signal (`netsh wlan show interfaces` / WLAN API), and bufferbloat (ping during a download test, optional).
- Tell the user about VALORANT's in-game network graphs (Settings → Video → Stats) for cross-checking.

## 3. Reboot-resume

**Recommendation: one-shot Task Scheduler task.** Trigger **At logon of the current user**, `RunLevel=Highest`, registered while the app is elevated (Task Scheduler COM API `ITaskService`, or `schtasks /create /sc onlogon /rl highest /ru <user> /it`).
- Elevated programs in **Run/RunOnce keys and the Startup folder are blocked at logon** (UAC cannot prompt there) [V, Microsoft UAC blog, still the behavior]. A highest-privilege scheduled task starts elevated **without a UAC prompt** because consent was given when it was created.
- A Windows service is overkill, needs SYSTEM, has no UI on the user desktop (session 0), and is a bigger AV/trust footprint. Avoid it.
- **Safe-mode exception (DDU flow only):** the scheduler does not run in Safe Mode. Use `HKLM\...\RunOnce` with a value name prefixed by `*` (forces it to run in Safe Mode) for the step that runs in Safe Mode.
- **State persistence:** `%ProgramData%\<App>\session.json` (ACL: Administrators + SYSTEM write) with `{sessionId, schemaVersion, step, pendingFixes[], completedFixes[], rebootReason, createdUtc, attempt}`. Write a temp file, flush, then `File.Replace` (atomic). On resume: validate, show "Resuming: step 3/5, verifying fix X".
- **Safe cleanup:** the task deletes itself on first successful run (`schtasks /delete` or `ITaskFolder.DeleteTask`). Add an **expiry** (task `EndBoundary` + a 3-attempt counter in session.json) so a stuck task never loops. Name it in a dedicated folder `\VTDiag\` for easy audit, and remove it on uninstall.

## 4. Safety

### 4.1 System Restore point
- Windows **skips** a new restore point if one was created in the last **24 h** (applies to `SystemRestore.CreateRestorePoint`, which is what `Checkpoint-Computer` wraps) [V]. Workaround: temporarily set `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency` (DWORD) = 0, create the point, then **restore the previous value or delete it** [V].
- Call the WMI class `root\default:SystemRestore.CreateRestorePoint` from C# directly. `Checkpoint-Computer` only exists in Windows PowerShell 5.1, not PS 7.
- System Protection is often **off by default** on Win11 OEM installs. Detect this, and offer `Enable-ComputerRestore -Drive C:\` (or WMI `Enable`) with consent. Always verify the point exists afterward (`SystemRestore` instances / `Get-ComputerRestorePoint`). If it fails, warn the user and rely on the journal.

### 4.2 Per-fix undo journal (append-only, write-ahead)
- File: `%ProgramData%\<App>\journal\<sessionId>.jsonl`, one entry per fix, **written and fsynced before** the change:
  `{fixId, kbRuleId, utc, kind, target, before:{...}, intended:{...}, after:{...}, verify:{ok,detail}, status: planned|applied|verified|undone|failed, rebootRequired}`
- Capture per kind:
  - Registry: export the exact values (type + data, or "absent"). For whole keys, use `reg export` to a .reg file under `backups\`.
  - Service: `StartType`, `DelayedAutoStart`, status, and the full `sc qc` output.
  - Device: InstanceId, ConfigFlags/enabled state, driver version.
  - BCD: `bcdedit /export backups\bcd-<ts>` (full store) **plus** the specific values.
  - Power: active scheme GUID (plus `powercfg /export` of the custom scheme).
  - Firewall/Defender: the list of rule names and exclusions we added.
  - Network: adapter DNS servers and `netsh int ip/winsock` show output.
- Undo = replay `before` in **reverse order**, then run `verify`. Fixes with no exact undo (winsock reset, SFC/DISM, driver clean install, Vanguard reinstall) are labeled **"one-way"** in the UI and need a separate, explicit consent.
- One fix at a time → verify → ask for the next. Never batch-apply silently.

## 5. Fix mechanisms (all native, no driver)

| Fix | Mechanism | Undo / notes |
|---|---|---|
| Disable/enable device | P/Invoke `CM_Disable_DevNode`/`CM_Enable_DevNode` (CfgMgr32) or SetupAPI `DIF_PROPERTYCHANGE`; CLI fallback `pnputil /disable-device "<InstanceId>"` (Win10 2004+) or `Disable-PnpDevice` | Enable the same InstanceId. Never touch the boot disk, display, or input devices without a big warning |
| Service start type | `ChangeServiceConfig` (P/Invoke) / `sc config X start= demand` | Restore the journaled type. Never touch vgc/vgk start types except in the official reinstall flow |
| Uninstall software | Read `QuietUninstallString` (fall back to `UninstallString`) from HKLM/HKLM WOW6432/HKCU Uninstall keys; MSI: `msiexec /x {GUID} /qn /norestart` | One-way; show the name and publisher and require consent |
| Power plan | `powercfg /getactivescheme`, `/setactive`, `/duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61` (Ultimate) | Restore the old GUID |
| MPO | `HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5` | **Reports disagree on whether it still works on 24H2/25H2** [C]. Measure the effect with PresentMon PresentMode before and after, and delete the value to undo |
| HAGS | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` 2=on, 1=off | Reboot required |
| Defender exclusion | `Add-MpPreference -ExclusionPath/-ExclusionProcess` (or the `MSFT_MpPreference` WMI) | `Remove-MpPreference`. Lowers security, so limit it to Riot install paths, default off, with a warning |
| Firewall | `INetFwPolicy2` COM or `New-NetFirewallRule -Group "VTDiag"` | Remove by group |
| DNS/winsock/TCP | `ipconfig /flushdns`; `netsh winsock reset`; `netsh int ip reset`; DNS via `Set-DnsClientServerAddress` | Winsock/IP reset is one-way and needs a reboot; journal the DNS settings |
| BCD | `bcdedit /set testsigning off`, `/set nx OptIn`, `/set hypervisorlaunchtype auto`; `/deletevalue {current} safeboot` | Full `bcdedit /export` first. Test-signing ON or DEP off blocks Vanguard. Recent Vanguard builds also require HVCI/VBS, TPM 2.0, and Secure Boot on Win11 (VAN 9003 etc.) [C], so the scan should *detect* these and *guide* the user to BIOS changes, never automate them |
| Vanguard clean reinstall | Official Riot steps: close Riot apps → `sc delete vgc` → `sc delete vgk` → **reboot** → delete `C:\Program Files\Riot Vanguard` → launch the Riot Client to reinstall [V, Riot support] | Fits the reboot-resume flow. One-way |
| Riot Client repair | Guide to the Riot Client/VALORANT "Repair" UI; optionally clear `%LocalAppData%\Riot Games\Riot Client\Data` cache after backup | Do not automate the client's internals |
| GPU clean driver install | DDU in Safe Mode: `bcdedit /set {current} safeboot minimal` + `*`-prefixed RunOnce → DDU `-silent -cleannvidia/-cleanamd -restart` → remove safeboot → install the driver (NVIDIA silent `setup.exe -s`) | **High risk.** DDU flags come from forum posts only and may be outdated [C]; redistribution terms are unclear → **do not bundle**, download from the official site with consent. Build a failsafe that always removes `safeboot`. Start with a "guided" mode, not full automation |
| System files | `DISM /Online /Cleanup-Image /RestoreHealth` then `sfc /scannow`, streamed progress | One-way, long running (10-30 min). Pair with Windows Update status via the WUA COM API |

## 6. Distribution

- **Code signing:**
  1. **SignPath Foundation (free for OSS) — primary choice.** It needs an OSI license with no proprietary components, an active project already released, an automated build (GitHub Actions), and a code-signing policy on the homepage. The publisher shown becomes "SignPath Foundation", and they can revoke the certificate if the rules are broken [V]. Microsoft itself lists it as the OSS option [V].
  2. **Azure Artifact Signing** (formerly Trusted Signing): **~$9.99/month**, 5,000 signatures. **Individuals: USA/Canada only** [V], so it is not available to an individual in Turkey (organizations: US/CA/EU/UK, etc.).
  3. OV certificate $150-300/yr with an HSM requirement. EV costs $400+ and **no longer bypasses SmartScreen (since 2024)** [V].
- **SmartScreen:** no signing option gives instant trust anymore. Reputation builds per publisher, and per file hash when unsigned [V]. Sign every release with the same identity, ship fewer and larger releases, and give users a short "More info → Run anyway" note for the first weeks.
- **AV false positives:** don't pack or obfuscate (no UPX/ConfuserEx). Prefer native APIs over spawning hidden PowerShell with encoded commands (a classic heuristic trigger). Keep the strings and manifest honest (description, company). Upload every release to **Microsoft WDSI** (microsoft.com/wdsi/filesubmission) and VirusTotal in CI, and publish the hashes.
- **Channels:** GitHub Releases (primary, SHA256 + SBOM) + **winget** via `wingetcreate`/`winget-releaser` in CI (MSI/EXE/MSIX are accepted; community review takes about 1-7 days) [C]. Microsoft Store MSIX gets free signing with no SmartScreen warning [V], but MSIX containerization conflicts with an elevated system tool, so it is low priority.
- **Auto-update:** **Velopack** (MIT, successor to Squirrel, delta packages, GitHub Releases source) [C]. Check its per-user install path against our `requireAdministrator` manifest [T]. Alternative: a simple "new version available → open the Releases page" check via the GitHub API. Never update silently during a match.

## 7. Optional AI assistant mode

- **Default: an offline rule engine.** It ranks KB hypotheses with weighted evidence and gives deterministic, explainable results. It is free and needs no network.
- **Option A, local model (free, private):** detect an existing **Ollama**, LM Studio, or **Microsoft Foundry Local** (free, OpenAI-compatible local REST API, CPU/GPU/NPU [C]; may still be in preview). Talk to `http://localhost` with the OpenAI-compatible schema. Don't bundle models (multi-GB, and big downloads raise ping). Use small models (3-8B) only to *explain* findings, not to choose fixes.
- **Option B, bring your own key:** OpenAI-compatible endpoint + key, stored with **DPAPI** (`ProtectedData`, CurrentUser) and never logged or written into reports.
- **Privacy:** before any cloud call, show the exact payload. Redact the username, computer name, MAC/serial numbers, local IPs, Riot ID, and file paths under the user's profile. Send summarized findings, not raw logs. Opt-in per session.
- **Safety:** the AI can **only suggest KB fix IDs**. Execution always goes through the same consent + journal path. It never runs free-form commands.

## Sources

- PresentMon README / console options / MIT license: https://github.com/GameTechDev/PresentMon , https://raw.githubusercontent.com/GameTechDev/PresentMon/main/README-ConsoleApplication.md , https://raw.githubusercontent.com/GameTechDev/PresentMon/main/LICENSE.txt
- .NET 8/9 end of support, .NET 10 LTS: https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/
- WPF trimming disabled: https://learn.microsoft.com/dotnet/core/deploying/trimming/incompatibilities ; WPF .NET 10 Fluent notes: https://learn.microsoft.com/dotnet/desktop/wpf/whats-new/net100
- Avalonia vs WinUI/WPF (vendor): https://avaloniaui.net/winui-compare , https://avaloniaui.net/wpf-compare
- Tauri vs Electron benchmarks: https://betterstack.com/community/guides/scaling-nodejs/tauri-vs-electron-vs-deno-vs-electrobun/ , https://www.levminer.com/blog/tauri-vs-electron
- WinRing0 Defender alert: https://support.microsoft.com/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0
- PawnIO signer blocked by FACEIT: https://github.com/namazso/PawnIO.Setup/issues/1 ; OpenRGB PawnIO MR: https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/2833
- Vanguard vs HWiNFO: https://www.hwinfo.com/forum/threads/riot-vanguard-vs-hwinfo.6374
- AMD ADLX GPU metrics: https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/performance-monitoring/iadlxgpumetrics/
- DPC/ISR via kernel ETW: https://learn.microsoft.com/windows-hardware/drivers/devtest/example-15--measuring-dpc-isr-time , https://learn.microsoft.com/windows/win32/etw/perfinfo , https://www.resplendence.com/latencymon_technical
- VALORANT ping approaches (community): https://www.esports.net/news/valorant/how-to-check-valorant-ping-without-logging-in/ , https://www.pong.com/latency/valorant
- Elevated startup blocked in logon path: https://learn.microsoft.com/archive/blogs/uac/elevations-are-now-blocked-in-the-users-logon-path ; https://www.winhelponline.com/blog/wp-json/wp/v2/posts/24055
- Restore point 24 h throttle + registry value: https://learn.microsoft.com/windows/win32/sr/createrestorepoint-systemrestore
- Riot Vanguard manual uninstall: https://support-valorant.riotgames.com/hc/articles/360044648213 , https://support.riotgames.com/en-us/riot/performance/uninstall-and-disable-vanguard
- Vanguard HVCI / VAN 9003 (community): https://dotesports.com/valorant/news/how-to-fix-valorant-hvci-enabled-van , https://windowsreport.com/van9003-valorant-windows-11/
- MPO OverlayTestMode on 24H2 (conflicting reports): https://www.sweclockers.com/forum/trad/1738707 , https://forums.guru3d.com/goto/post?id=6343762
- DDU command line (forum): https://www.wagnardsoft.com/forums/viewtopic.php?p=8253
- Code signing options, SmartScreen, Artifact Signing $9.99 + geography, EV change, SignPath: https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options
- SignPath Foundation terms: https://signpath.org/terms , https://about.signpath.io/product/open-source
- Artifact Signing quickstart (eligibility): https://learn.microsoft.com/azure/artifact-signing/quickstart
- winget submission (community docs): https://github.com/getopenscreen/openscreen/pull/285
- Velopack: https://docs.velopack.io/distributing/overview
- Foundry Local: https://learn.microsoft.com/windows/ai/windows-ai-comparison

## Open items to verify by test
1. PresentMon + TraceEvent kernel session during a full ranked match → no VAN error, under 2% CPU overhead.
2. Published WPF single-file size with and without compression on .NET 10.
3. Whether `OverlayTestMode=5` changes PresentMode on 25H2.
4. Whether Velopack's install location works with `requireAdministrator`.
5. ADLX and IGCL license terms for bundling C# bindings.
6. The current state of the DDU CLI flags (`-silent`, `-cleannvidia`, `-restart`).
