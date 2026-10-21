# Design B: VTDiag, a VALORANT diagnose-and-fix tool (architecture proposal)

Author: Architect B. Date: 2026-10-21. Status: proposal, competing with Design A. The final decisions go in `DESIGN.md`.
Inputs read as data: `PROBLEM_CATALOG.md`, `data/catalog.json` (90 problems, 72 distinct `check_id`s), `research/conflicts.json` (113 entries in 12 kinds), `research/error_codes.json` (57 codes, Vanguard requirements, log locations, tool_design_notes), `research/tech_stack.md`, `research/long_tail.md`.
Markers: **[T]** means this has to be tested on a real PC with Vanguard running before we rely on it. **[D]** means a deliberate change from `tech_stack.md`, with the reason given.

**Design emphasis:** the tool must never leave a PC worse off. That needs exact undo, a reboot-resume that cannot loop, state that survives crashes and power cuts, few antivirus false positives, wording a frustrated 15-year-old understands, guided manual steps for what software can't do, a live monitor that costs no FPS and never touches the game, and an assisted mode that replaces AnyDesk sessions.

---

## 0. Ten decisions in one page

1. **C# / .NET 10 LTS + WPF (WPF-UI, MVVM Toolkit).** Same stack as `tech_stack.md`. The engine is a UI-free library set, and one CLI host runs it for tests.
2. **[D] Three processes instead of one elevated exe.** `VTDiag.exe` (UI) runs as the normal user. `VTDiag.Engine.exe` is elevated and is the only process that writes system state, the journal and the session. `VTDiag.Monitor.exe` is elevated, runs at low priority with EcoQoS, and is a child of the engine. If the UI crashes or the GPU driver resets mid-fix, the fix still finishes. If the monitor crashes, nothing else is affected.
3. **[D] Installed per-machine into `C:\Program Files\VTDiag` (Inno Setup), not with a per-user Velopack install.** A highest-privilege resume task that points at an exe a user can overwrite is a textbook privilege-escalation hole. Updates come as a signed installer downloaded and run with consent, never during a match or an active session.
4. **Fixes are data built from compiled, audited primitives.** A KB fix is JSON that combines about 25 typed `IOperation` primitives (service start type, device disable, registry value, powercfg value, ...). Every primitive captures its own backup and implements Probe and Undo. The KB **cannot contain commands, scripts or paths outside allowlists**, and KB packages are Ed25519-signed.
5. **Write-ahead journal plus an atomic session file.** Each operation writes an `intent` record, fsynced, before anything changes. After a crash, any intent with no outcome becomes "in doubt", and the operation's `Probe` decides whether to roll forward or back. No state is ever lost silently.
6. **The resume can't loop by construction.** The engine **never reboots unless a person clicks the button**. Resume keeps an attempt counter (max 3, incremented before any work), an expiry (7 days), and a **boot ID check** that catches "Shut down + Fast Startup is not a restart". The resume task deletes itself only after the UI has connected and the post-reboot verify has been persisted.
7. **Explainable ranking.** C# "signal providers" (about 80, one per catalog `check_id`, plus ones generated from the 113 conflict entries) feed a log-odds scorer driven by JSON rules. An explicit "unknown cause" pseudo-hypothesis lets the tool say *"we're not sure"* instead of forcing a fix. Fixes are proposed in order of **P(cause) × P(fix works) / cost**, so quick, harmless wins come first.
8. **The monitor never touches the game.** It uses ETW only: PresentMon (MIT) as a child process for frame data, PDH counters, an event-log subscription, and Kernel-Network ETW to find the server endpoint. It never opens a handle to any Riot or Vanguard process, never calls `timeBeginPeriod`, and shows no overlay. Budget: **≤1% total CPU, ≤100 MB, 0 GPU**, enforced by self-measurement and graceful degradation.
9. **Assisted mode built on files, not remote control.** The player exports `.vtdscan` (redacted). A helper (or the helper's AI agent) opens it in Helper mode, unelevated and on any PC, and exports a `.vtdplan` that contains **only KB fix IDs plus entity bindings**. The player's app re-validates every step against a fresh scan and asks for consent per step, through the same screens as a normal fix.
10. **Ship read-only first.** M1 is scan, report and guides, with no system writes. That builds SmartScreen and SignPath reputation and gives a real snapshot corpus for tests before any fix code ships.

---

## 1. Tech stack

| Concern | Choice | Why / notes |
|---|---|---|
| Runtime | .NET 10 LTS, `net10.0-windows10.0.19041`, self-contained, x64 only (Vanguard doesn't run on ARM) | LTS until Nov 2028; .NET 8/9 reach end of support 2026-11-10 |
| UI | WPF + **WPF-UI** (MIT) Fluent controls, **CommunityToolkit.Mvvm** | Shortest route to Windows APIs; Avalonia stays the fallback because Core is UI-free |
| IPC | Named pipes + **StreamJsonRpc** (MIT), System.Text.Json source-generated | Typed contracts, progress notifications, no HTTP port to open (AV and firewall friendly) |
| WMI | `Microsoft.Management.Infrastructure` (CIM) with timeouts | `System.Management` hangs on broken WMI repositories. CIM supports operation timeouts |
| ETW | **Microsoft.Diagnostics.Tracing.TraceEvent** (MIT) for kernel network, process, DPC and event signals; **PresentMon console** (MIT) for frames | PresentMon behind the `IFrameSource` interface, so a TraceEvent-native replacement can come later |
| Win32 | Source-generated `[LibraryImport]` P/Invoke: CfgMgr32, SetupAPI, advapi32 (SCM, registry), PowrProf, iphlpapi, wlanapi, tbs.dll, kernel32 firmware variables, MMDevice COM, Task Scheduler COM, INetFwPolicy2 COM | **No PowerShell anywhere.** Spawning hidden PowerShell is the #1 AV heuristic trigger. The few Microsoft CLIs we must call (`pnputil`, `powercfg` for one-off cases) run through an allowlisted `ProcessRunner` with fixed argument templates and no shell |
| Logging | Serilog to a rolling file in `%ProgramData%\VTDiag\logs`, with a redaction enricher | Never log secrets or command lines |
| Tests | xUnit, FsCheck (property tests), Verify (snapshot/golden tests), Hyper-V VM for reboot chaos tests | §15 |
| Installer | **Inno Setup 6** (free) → `VTDiag-Setup-x.y.z.exe`; portable zip for Helper mode only | §16 |
| Static analysis | **Microsoft.CodeAnalysis.BannedApiAnalyzers** per project, warnings as errors | Makes the Vanguard and safety rules compile-time facts (§13.2) |
| i18n | resx (UI) + KB string tables (`kb/strings/en.json`, `tr.json`), ICU-style placeholders via `Jeffijoe.MessageFormat` (MIT) | §11 |

---

## 2. Process model

```
                ┌──────────────────────────── user session (medium integrity) ────────────────────────────┐
                │  VTDiag.exe (WPF UI, asInvoker)                                                            │
                │   - all screens, i18n, report viewer, Helper mode (never needs elevation)                  │
                │   - holds NO system-writing code (banned APIs); talks only to Engine over the pipe         │
                └───────────────┬──────────────────────────────────────────────────────────────────────────┘
                                │ ShellExecute "runas" (one UAC prompt per session) — or unelevated "limited mode"
                                ▼  named pipe \\.\pipe\VTDiag.<nonce>  (ACL: interactive user SID + Admins; FIRST_PIPE_INSTANCE;
                                │                                       UI verifies GetNamedPipeServerProcessId == child PID)
                ┌───────────────┴──────────────── high integrity ───────────────────────────────────────────┐
                │  VTDiag.Engine.exe (headless, asInvoker manifest, started elevated)                        │
                │   - Scan (collectors), Analysis, FixRunner, Journal, SessionController, RebootCoordinator │
                │   - single writer of %ProgramData%\VTDiag\{session,journal,backups,quarantine}           │
                │   - Global\VTDiag.Engine mutex; runs Recovery before accepting any RPC                    │
                │   - Job object (KILL_ON_JOB_CLOSE) ──► VTDiag.Monitor.exe (BelowNormal + EcoQoS)           │
                │                                        └─► PresentMon.exe (child, same job)               │
                └──────────────────────────────────────────────────────────────────────────────────────────┘
   Task Scheduler folder \VTDiag\  (exists ONLY while a reboot is pending)
     - "Resume-Engine"  At logon of <user>, RunLevel=Highest  → "C:\Program Files\VTDiag\VTDiag.Engine.exe" --resume <sessionId>
     - "Resume-UI"      At logon of <user>, RunLevel=Limited, 15 s delay → "C:\Program Files\VTDiag\VTDiag.exe" --resume <sessionId>
```

- **Why the split.** (a) The UI renders with WPF/D3D, and some fixes touch GPU or display state. A UI crash must never interrupt a half-applied operation. (b) One writer means no locking bugs. (c) The UI can't be talked into writing anything, because write APIs are banned in its project. (d) Helper mode runs only the UI, with no admin. (e) The UI stays unelevated, so drag-and-drop of `.vtdplan` files from Explorer works (UIPI blocks drag-drop into elevated windows).
- **Limited mode.** If the user declines UAC, the UI starts the engine **unelevated** with `--limited`. Many checks work without admin (registry `SecureBoot\State`, `tbs.dll` TPM info, Win32_DeviceGuard, Get-Disk equivalents, uninstall keys, adapters). Collectors that need admin report `SkippedNoAdmin`, the ranking treats those signals as Unknown, and no fixes are offered except "Restart with permission". A limited engine writes nothing to `%ProgramData%`. Its snapshot and report go to `%LocalAppData%\VTDiag` and there is no session or journal, because nothing can change.
- **Resume uses two tasks, not token tricks.** Starting an unelevated UI from an elevated process normally needs explorer-token duplication, which looks malicious to EDR. Two tasks under different principals (Highest for the engine, Limited for the UI) is plain Task Scheduler use. If both fail (debloaters often disable Task Scheduler), the fallback is an HKCU `RunOnce` entry that starts the **unelevated UI**, which then asks for UAC once.
- **Engine lifetime.** The engine lives while a UI is connected, or while the monitor runs (tray mode), or for up to 10 minutes during an unattended long operation (SFC/DISM). An idle engine with no UI exits. The engine never keeps itself running at startup.
- **Watchdog.** The UI sends a heartbeat every 2 s. If the engine disappears, the UI shows *"The checker stopped unexpectedly. Nothing is lost. [Restart checker]"*. The restarted engine runs Recovery (§6.5) before anything else.

---

## 3. Solution and folder layout

```
vtdiag/
├─ VTDiag.sln
├─ Directory.Build.props          # net10.0-windows, Nullable, TreatWarningsAsErrors, analyzers, InvariantGlobalization=false
├─ Directory.Packages.props       # central package versions
├─ banned/                        # per-layer BannedSymbols files (see §13.2)
│   ├─ Banned.Common.txt          # culture-sensitive ToUpper/ToLower/string.Compare, Thread.Sleep in UI, timeBeginPeriod, OpenProcess/ReadProcessMemory/WriteProcessMemory/CreateRemoteThread/SetWindowsHookEx
│   ├─ Banned.ReadOnly.txt        # + RegistryKey.SetValue/DeleteValue/CreateSubKey, File.Delete/Move/Write*, Process.Start, ServiceController.Stop
│   └─ Banned.Ui.txt              # + everything in ReadOnly + P/Invoke into advapi32/cfgmgr32
├─ src/
│  ├─ VTD.Contracts/              # DTOs, interfaces, IPC contracts, enums. No logic. (owner: Lead)
│  ├─ VTD.Platform/               # READ-side interop + helpers: Cim, Registry readers, CfgMgr enumerate, Tbs, Firmware vars, BootId, ProcessSnapshot (ToolHelp), Signer (WinVerifyTrust), ProcessRunner(allowlist) (owner: Agent 1)
│  ├─ VTD.Collectors/             # ICollector implementations → ScanSnapshot sections (owner: Agent 1)
│  │   ├─ Os/ Hardware/ Firmware/ Security/ Devices/ Software/ Network/ Events/ Riot/ Power/ Display/ Audio/ Input/
│  │   └─ ScanOrchestrator.cs
│  ├─ VTD.Knowledge/              # KB loader, JSON schema validation, signature verify, string tables (owner: Agent 2)
│  ├─ VTD.Analysis/               # ISignalProvider impls, ConflictSignalGenerator, RuleEvaluator, Ranker, QuestionPlanner (owner: Agent 2)
│  ├─ VTD.Fixes/                  # IOperation primitives + WRITE-side interop (Interop/), ProtectedTargetPolicy, Guards, FixRunner (owner: Agent 3)
│  ├─ VTD.Journal/                # JournalWriter (jsonl+crc+fsync), AtomicStateStore<T>, BackupStore, Quarantine, RecoveryPlanner (owner: Agent 3)
│  ├─ VTD.Session/                # SessionMachine (pure), SessionController, RebootCoordinator, ResumeTasks, RestorePointService (owner: Agent 4)
│  ├─ VTD.Engine/  (exe)          # host: RPC server, DI wiring, --resume/--limited/--cli verbs, Monitor supervisor (owner: Agent 4)
│  ├─ VTD.Monitor.Core/           # IFrameSource(PresentMon), PdhSampler, EventWatcher, NetEndpointTracker, Pinger, Detectors, SelfBudget (owner: Agent 5)
│  ├─ VTD.Monitor/ (exe)          # host: low-priority process, writes ticks to Engine over pipe (owner: Agent 5)
│  ├─ VTD.Reporting/              # Redactor, ReportBuilder (json/html/md), Bundle (.vtdscan/.vtdreport), PlanValidator(schema part) (owner: Agent 6)
│  └─ VTD.App/     (exe, WPF)     # Views, ViewModels, Resources/Strings.resx + Strings.tr.resx, Helper mode, tray (owner: Agent 6)
├─ kb/                            # data, versioned separately from the app (owner: Agent 2; content PRs from mods)
│  ├─ schema/ rule.schema.json fix.schema.json guide.schema.json conflict.schema.json plan.schema.json snapshot.schema.json report.schema.json
│  ├─ rules/<category>/<problem-id>.json        # 90 from catalog.json
│  ├─ conflicts/<kind>.json                     # 113 from conflicts.json
│  ├─ fixes/<area>/<fix-id>.json
│  ├─ guides/<guide-id>.json                    # BIOS/ISP/manual flows
│  ├─ codes/error_codes.json                    # VAN/VAL → hypotheses + official links
│  ├─ strings/en.json  strings/tr.json
│  └─ kb.manifest.json  (+ kb.manifest.sig)
├─ tools/
│  ├─ KbBuilder/                  # converts data/catalog.json + research/*.json into kb/ drafts; lints KB
│  ├─ FixtureGen/                 # synthesizes snapshot fixtures per problem (§15)
│  └─ KbSign/                     # Ed25519 signing (key in GitHub Actions secret)
├─ tests/
│  ├─ VTD.Analysis.Tests/  VTD.Session.Tests/  VTD.Journal.Tests/  VTD.Reporting.Tests/  VTD.Monitor.Tests/  VTD.Kb.Tests/
│  ├─ VTD.Fixes.IntegrationTests/        # admin; sandboxed targets; runs in CI windows runner + Windows Sandbox
│  ├─ VTD.Chaos/                         # Hyper-V VM orchestration scripts for reboot/power-cut tests (nightly, self-hosted)
│  └─ fixtures/ snapshots/*.json  presentmon/*.csv  events/*.xml  plans/*.vtdplan
├─ third_party/PresentMon/  PresentMon-x64.exe  LICENSE.txt  SHA256
├─ installer/ VTDiag.iss
├─ docs/                          # GitHub Pages: phone-friendly BIOS guides, code-signing policy, privacy policy, plan schema docs
└─ .github/workflows/ build.yml  release.yml  kb-release.yml  vt-scan.yml
```

Dependency direction (enforced by project references): `Contracts ← Platform ← Collectors`, `Contracts ← Knowledge ← Analysis`, `Contracts ← Journal ← Fixes`, `Contracts ← Session`, `Engine → all`, `Monitor → Monitor.Core → Platform`, `App → Contracts + Reporting (+ Knowledge for Helper mode)`. **Only `VTD.Fixes`, `VTD.Journal` and `VTD.Session` may reference write APIs.**

---

## 4. Data model

### 4.1 Scan snapshot

Typed C# records serialized with source-generated System.Text.Json. Each record carries a `Sensitivity` annotation that drives redaction (§12).

```csharp
namespace VTD.Contracts.Snapshot;

public sealed record ScanSnapshot(
    SnapshotMeta Meta,
    OsInfo Os, HardwareInfo Hardware, FirmwareInfo Firmware, SecurityState Security,
    IReadOnlyList<DeviceInfo> Devices, IReadOnlyList<LoadedDriver> Drivers,
    IReadOnlyList<ServiceInfo> Services, IReadOnlyList<ProcessInfo> Processes,
    IReadOnlyList<InstalledApp> Apps, IReadOnlyList<StartupItem> Startup,
    NetworkState Network, PowerState Power, DisplayState Display, AudioState Audio,
    InputState Input, RiotState Riot, EventDigest Events,
    IReadOnlyList<CollectorRun> Runs);                 // per-collector status/time/issues

public sealed record SnapshotMeta(
    Ulid SnapshotId, int SchemaVersion, string AppVersion, string KbVersion,
    DateTimeOffset CreatedUtc, bool Elevated, BootIdentity Boot,
    string OsUiCulture, string AppLanguage, RedactionState Redaction, string MachineKey /* salted hash, see §12 */);

public sealed record BootIdentity(uint BootId, DateTimeOffset LastBootUpTimeUtc);  // BootId: HKLM\...\Memory Management\PrefetchParameters\BootId [T]

public sealed record CollectorRun(string CollectorId, CollectorStatus Status, TimeSpan Elapsed, IReadOnlyList<string> IssueCodes);
public enum CollectorStatus { Ok, Partial, SkippedNoAdmin, TimedOut, Failed, NotApplicable }

// examples of section records
public sealed record NetAdapter(
    string InstanceId /* PnP id, stable */, string Description, string Alias, AdapterKind Kind /* Ethernet, WiFi, Virtual, Tunnel, Bluetooth */,
    bool Enabled, OperStatus Status, long LinkSpeedBps, bool IsDefaultRoute, int Ipv4Metric,
    string? DriverFile, string? DriverVersion, DateOnly? DriverDate, string? DriverProvider,
    IReadOnlyDictionary<string,string> AdvancedProps /* *EEE, *GreenEthernet, *InterruptModeration, AdvancedEEE ... raw keywords */,
    bool AllowPowerOff, [property: Sensitivity(Sens.Hashed)] string? Mac);

public sealed record ServiceInfo(string Name, string DisplayName, ServiceStartType StartType, bool DelayedAuto,
    ServiceState State, [property: Sensitivity(Sens.PathRedact)] string? ImagePath, SignerInfo? Signer);

public sealed record SecurityState(
    Tri SecureBootOn, Tri SecureBootSetupMode, Tri Db_WindowsUefiCa2023, FirmwareType FirmwareType /* Uefi|Bios */,
    PartitionStyle SystemDiskStyle, TpmInfo Tpm, VbsInfo Vbs /* HVCI configured/running, VBS status, required props */,
    Tri VirtualizationFirmwareEnabled, Tri KernelDmaProtection, BcdFlags Bcd /* testsigning, nointegritychecks, nx, hypervisorlaunchtype, safeboot */,
    BitLockerInfo SystemVolumeBitLocker, DefenderInfo Defender, IReadOnlyList<AvProduct> SecurityCenterAv,
    IReadOnlyList<ExploitOverride> ExploitOverrides /* IFEO MitigationOptions on Riot exes */, IReadOnlyList<IfeoEntry> Ifeo);

public sealed record RiotState(
    RiotInstall? Client, RiotInstall? Valorant, VanguardInfo Vanguard /* vgc/vgk config+state, files, versions, vgkbootstatus.dat, 392667600.dat */,
    IReadOnlyList<AppCompatLayer> CompatLayers /* RUNASADMIN, DISABLEDXMAXIMIZEDWINDOWEDMODE ... per exe */,
    GameConfigDigest? GameConfig /* window mode, fps caps from GameUserSettings.ini */, ShooterLogDigest? LastLog /* curated lines only */,
    bool GameRunning, bool ClientRunning);

public sealed record EventDigest(DateTimeOffset WindowStartUtc,
    IReadOnlyList<EventHit> Hits /* provider,id,count,first,last,sampleRedacted,params(faulting module etc.) */);
```

**Collector catalog** (each is an `ICollector` with a timeout; the slowest is ≤15 s; the whole scan targets 30-60 s):

| Collector | Main sources (all native, no PowerShell) | Feeds check_ids (examples) |
|---|---|---|
| Os | registry `CurrentVersion` (build, UBR, Insider ring), pending-reboot keys (CBS, WU, PendingFileRenameOperations), HiberbootEnabled, env vars, WUA COM history | win_build, pending_reboot, hotfix, env_vars, clock |
| Hardware | CPU (GetLogicalProcessorInformationEx → hybrid EfficiencyClass), Win32_PhysicalMemory (speed vs configured → XMP hint, channels), DXGI adapters, MSFT_PhysicalDisk (MediaType/BusType), Win32_BaseBoard/BIOS (release date), battery | hybrid_cpu, ram_config, disk_type, bios_age, igpu_state |
| Firmware/Security | `GetFirmwareType`, `GetFirmwareEnvironmentVariable` (SecureBoot, SetupMode, db in the image-security GUID; needs SE_SYSTEM_ENVIRONMENT, admin), `tbs.dll Tbsi_GetDeviceInfo` (TPM version, no admin), Win32_Tpm (admin), Win32_DeviceGuard, BCD via WMI `root\wmi BcdStore`, Win32_EncryptableVolume, MSFT_MpComputerStatus, SecurityCenter2, IFEO | secure_boot, uefi_gpt, sb_keys, tpm, hvci, virtualization, iommu_dma, ifeo, av_products |
| Devices/Drivers | CfgMgr32 devnode walk (problem codes, class, InstanceId), driver store info, `EnumDeviceDrivers` (loaded .sys names), CodeIntegrity Operational events | pnp_errors, realtek_nic, vmd, hvci_incompat_drivers, gpu_driver, intel_driver |
| Software | Uninstall keys (HKLM, WOW6432, HKCU), SCM enumeration, ToolHelp process snapshot (**names only for Riot/Vanguard processes, never a handle**), Run keys, Startup folder, optional features, `WSCEnumProtocols` (Winsock LSP catalog) | hook_procs, conflict_apps_installed, other_anticheat, oem_tools, mdns, nahimic, vgc_cpu (from the monitor) |
| Network | GetAdaptersAddresses, GetBestInterfaceEx, adapter Class-key advanced props, WLAN API (RSSI, band), WinHTTP/WinINet proxy, INetFwPolicy2 rules for Riot exes, NLM connectivity, IPv6/Teredo state, ICMP/TCP probes to region hosts (KB list, "indicative") | multi_nic, nic_power, wifi, virtual_nic_vpn, dns_ipv6, firewall_rules, ping_route, tcp_stack |
| Events | `EventLogReader` with XPath, last 14 days, capped at 200 hits per query: Kernel-Power 41, BugCheck 1001, Display 4101, WHEA 17/18/19/47, rt25cx21/rt640x64, SCM 7000-7034 (vgc/vgk), Kernel-Processor-Power 37, TPM 14/17, App 1000/1001 (faulting module for VALORANT/Riot/vgc), CodeIntegrity 3033/3063 (DLLs blocked from loading into VALORANT, a passive hook detector), `FSCTL_IS_VOLUME_DIRTY` | kp41_lke, realtek_events, app_crash_module, minidump, tpm_events, disk_dirty, thermals (throttle proxy) |
| Riot | `%ProgramData%\Riot Games\RiotClientInstalls.json`, product_settings yaml, Vanguard folder/files/versions, AppCompat layers, GameUserSettings.ini, ShooterGame.log digest (whitelisted line patterns, redacted at parse time). **Never Vanguard logs** (encrypted) beyond file dates | vgc_service, vgk_files, stale_dat, riot_elevation, riot_folders, valorant_config, display_mode |
| Power | PowerGetActiveScheme, PowerReadAC/DCValueIndex (USB selective suspend, processor min/max), GetSystemPowerStatus, effective overlay scheme | power_plan, usb_ss |
| Display | QueryDisplayConfig (mode, refresh, HDR), UserGpuPreferences, HwSchMode, OverlayTestMode, DirectXUserGlobalSettings, GameDVR keys, FSO layers | gpu_pref, hags_windowed, fso_mpo, display_mode, nv_3d_settings (later: NVAPI DRS read) |
| Audio/Input | MMDevice enumeration (default endpoints, Hands-Free AG endpoints, exclusive mode), HID/USB devnodes, controllers, mouse acceleration (SPI) | bt_hfp, audio_devices, usb_devices, mouse_settings |
| Optional online | Riot status JSON for the region [T], KB update check | riot_status |

Locale rule (from `tool_design_notes`): match **InstanceIds, numeric WMI values, provider GUIDs and event IDs**, never localized display names. The test machine is Turkish.

### 4.2 Signals, rules and hypotheses

Two layers keep the rules data-driven while the hard logic stays testable:

- **Signals (C#).** `ISignalProvider` classes compute named tri-state facts with evidence. There is one provider per catalog `check_id` (72), plus `mon.*` signals from the monitor, `ask.*` signals from user answers, and **generated `conflict.<id>.*` signals for all 113 conflict entries** (`installed`, `running`, `driver_loaded`, `adapter_present`, `crash_module`), which a single generic provider builds from each entry's `detect` block.
- **Rules (JSON).** Each hypothesis (catalog problem) is a list of weighted signal references. Mods can edit rules without knowing C#.

```csharp
namespace VTD.Contracts.Analysis;

public enum Tri { True, False, Unknown, NotApplicable }

public sealed record Evidence(string TextKey, IReadOnlyDictionary<string, string> Args, string? FactPath, EvidenceSource Source /* Scan|Monitor|User|Helper */);

public sealed record SignalResult(string Id, Tri State, double Strength /*0..1, e.g. 14 NIC resets → 1.0*/, IReadOnlyList<Evidence> Evidence,
                                  IReadOnlyList<EntityRef> Entities /* e.g. the adapter(s) that matched — used to bind fixes */);

public interface ISignalProvider
{
    IReadOnlyCollection<string> Provides { get; }
    IEnumerable<SignalResult> Evaluate(SignalContext ctx);   // ctx: snapshot, monitor summary?, intake answers, KB
}

public sealed record EntityRef(EntityKind Kind /* Adapter, Service, Process, Driver, App, File, RegistryValue, Device */, string Id, string DisplayName);
```

Rule schema (`kb/schema/rule.schema.json`, abridged):

```jsonc
{
  "id": "string (catalog id)", "category": "fps_performance|crash_freeze_bsod|...",
  "title": "strings key", "summary": "strings key",
  "symptoms": ["stutter", "fps_drop", "launch_black_screen", "loading_stuck", "error_code", "disconnect", "high_ping", "voice", "bsod_freeze", ...],
  "errorCodes": ["VAN 9003"],
  "prior": { "threads": 76 },                    // converted to a log prior per category at load time
  "evidence": [ { "signal": "id", "true": 1.5, "false": "veto" | -1.0, "unknown": 0 } ],
  "requires": ["signal ids that must be True (hard gate)"],
  "wave": { "from": "2026-09-01", "to": "2026-11-30", "boost": 0.7 },   // date-limited outbreaks (Nahimic, hook list)
  "fixes": ["fix ids, in KB-preferred order"], "guides": ["guide ids"],
  "outOfScope": false,                           // e.g. VAN 152 → "appeal, not a tech fix"
  "sources": { "evidence": "op_confirmed", "threadCount": 76 }
}
```

**Example 1: Realtek 2.5GbE stutter** (`kb/rules/fps_performance/realtek-2-5gbe-nic-stutter.json`)

```json
{
  "id": "realtek-2-5gbe-nic-stutter",
  "category": "fps_performance",
  "title": "rule.realtek25.title",
  "symptoms": ["stutter", "fps_drop", "freeze_short", "ping_spikes"],
  "errorCodes": [],
  "prior": { "threads": 76 },
  "requires": ["conflict.realtek_2_5gbe.adapter_present"],
  "evidence": [
    { "signal": "conflict.realtek_2_5gbe.adapter_enabled", "true": 1.0, "false": "veto" },
    { "signal": "net.realtek25.unused",                    "true": 1.0 },
    { "signal": "events.realtek_nic_resets",               "true": 2.0 },
    { "signal": "net.multi_nic_up",                        "true": 0.5 },
    { "signal": "hw.amd_am5_platform",                     "true": 0.4 },
    { "signal": "mon.frametime.periodic_spikes",           "true": 2.0, "false": -1.2 },
    { "signal": "mon.spikes_correlate.nic_event",          "true": 3.0 },
    { "signal": "ask.only_valorant_affected",              "true": 0.5 }
  ],
  "fixes": ["fix.net.disable_unused_adapter", "fix.net.realtek_power_saving_off"],
  "guides": ["guide.net.realtek_driver_update"],
  "sources": { "evidence": "op_confirmed", "threadCount": 76 }
}
```
`net.realtek25.unused` = adapter enabled AND not the default-route adapter AND another physical adapter is Up with internet (NLM). Its `Entities` carry the adapter InstanceId, which binds the fix.

**Example 2: Nahimic black screen**

```json
{
  "id": "nahimic-audio-service-launch-hang",
  "category": "crash_freeze_bsod",
  "title": "rule.nahimic.title",
  "symptoms": ["launch_black_screen", "loading_stuck"],
  "errorCodes": ["VAN 81"],
  "prior": { "threads": 29 },
  "requires": ["conflict.nahimic.installed"],
  "evidence": [
    { "signal": "conflict.nahimic.running",                "true": 1.5, "false": -0.5 },
    { "signal": "conflict.nahimic.crash_module",           "true": 3.0 },
    { "signal": "events.valorant_crash_0xc0000005_recent", "true": 0.8 },
    { "signal": "hw.oem_in_nahimic_vendors",               "true": 0.3 }
  ],
  "wave": { "from": "2026-09-01", "to": "2026-12-31", "boost": 0.7 },
  "fixes": ["fix.svc.disable_conflict_service"],
  "guides": ["guide.apps.uninstall_nahimic"],
  "sources": { "evidence": "op_confirmed", "threadCount": 29 }
}
```
`conflict.nahimic.crash_module` is True when an App 1000 event for `VALORANT-Win64-Shipping.exe` has `Faulting module` ∈ `conflicts[nahimic].detect.module_names` (`ProductInfo.dll`). The generic provider computes this for **every** conflict that has `module_names`.

**Example 3: VAN 9003 Secure Boot.** A rule plus a **decision-tree guide**, because nothing here is automatable:

```json
{
  "id": "secure-boot-not-detected-van9003",
  "category": "tpm_secureboot_bios",
  "title": "rule.van9003.title",
  "symptoms": ["error_code"],
  "errorCodes": ["VAN 9003", "VAN 9090", "VAN: RESTRICTION: 1", "VAN: RESTRICTION: 4"],
  "prior": { "threads": 19 },
  "evidence": [
    { "signal": "sec.secureboot.on",          "true": "veto", "false": 3.0 },
    { "signal": "fw.uefi",                    "false": 1.0 },
    { "signal": "disk.system_gpt",            "false": 1.0 },
    { "signal": "sec.secureboot.setup_mode",  "true": 1.5 },
    { "signal": "sec.db_missing_ca2023",      "true": 0.5 },
    { "signal": "hw.bios_older_than_3y",      "true": 0.4 }
  ],
  "fixes": [],
  "guides": ["guide.secureboot.enable"],
  "sources": { "evidence": "op_confirmed", "threadCount": 19 }
}
```
`guide.secureboot.enable` branches on live signals: **MBR disk** → `mbr2gpt /validate /allowFullOS`, run read-only by us, then a separate one-way consent for `/convert` with a BitLocker check. **CSM/Legacy** → the "CSM off" BIOS page. **Setup mode / keys missing** → "Restore factory keys". **SB on but VAN persists** → `secure-boot-policy-wave-2026` / Riot ticket. Each branch ends with a verify step: after "Restart into BIOS" and resume, re-read `UEFISecureBootEnabled` and show ✅ or "not yet, here's the next thing to try".

### 4.3 Fix schema

```jsonc
// kb/schema/fix.schema.json (abridged)
{
  "id": "fix.<area>.<name>",
  "title": "key", "explain": "key (what & why, ≤3 sentences)", "undoExplain": "key",
  "risk": "low|medium|high",                 // high ⇒ never automated; guide only (validator enforces)
  "reversibility": "exact|bestEffort|sessionOnly|oneWay",   // derived from ops, validator checks consistency
  "admin": true,
  "reboot": "none|recommended|required",     // required ⇒ verify phase runs after reboot
  "scope": "persistent|session",
  "bind": { "<name>": { "kind": "Adapter|Service|Process|...", "fromSignal": "net.realtek25.unused", "pick": "all|one" } },
  "guards": [                                 // re-evaluated with FRESH probes immediately before apply
    { "signal": "riot.game_running", "must": "False", "failKey": "guard.close_game" },
    { "check": "adapter.notDefaultRoute", "of": "$adapter" },
    { "check": "connectivity.otherAdapterOnline", "except": "$adapter" }
  ],
  "steps": [ { "op": "<primitive>", "args": { ... } } ],
  "verify": [ { "probe": "<primitive probe>", "args": { ... }, "expect": { ... } } ],
  "deadman": { "seconds": 60, "check": "connectivity.internet" },   // optional: auto-revert unless confirmed
  "outcome": { "kind": "playtest|relaunch|errorGone|monitorCompare", "askKey": "...", "monitorSignals": ["mon.frametime.periodic_spikes"] },
  "drift": { "watch": true, "reasonKey": "drift.windows_update_reenabled" },
  "minApp": "0.2.0", "tested": ["win10-22H2", "win11-24H2", "win11-25H2"]
}
```

**Primitive operations** (C#, in `VTD.Fixes`; each one has a JSON args schema):

```csharp
namespace VTD.Contracts.Fixes;

public enum Reversibility { Exact, BestEffort, SessionOnly, OneWay }

public interface IOperation
{
    string Op { get; }                                    // "service.setStartType"
    Reversibility Reversibility { get; }
    JsonSchema ArgsSchema { get; }
    ValueTask<PolicyDecision> CheckPolicyAsync(OpArgs args, CancellationToken ct);   // ProtectedTargetPolicy (§13)
    ValueTask<StateCapture>  CaptureAsync(OpArgs args, CancellationToken ct);        // backup: exact prior state
    ValueTask<OpResult>      ApplyAsync(OpArgs args, StateCapture before, CancellationToken ct);
    ValueTask<StateCapture>  ProbeAsync(OpArgs args, CancellationToken ct);          // read current state (verify, recovery, drift)
    ValueTask<OpResult>      UndoAsync(OpArgs args, StateCapture before, StateCapture? expectedAfter, UndoMode mode, CancellationToken ct);
}
public enum UndoMode { IfUnchanged /*default: refuse if someone else changed it since*/, Force }
public sealed record StateCapture(string Op, JsonObject Data, string Hash);
```

MVP primitive set (about 25): `service.setStartType`, `service.stop`, `service.start`, `device.disable` / `device.enable` (CM_Disable_DevNode with `CM_DISABLE_PERSIST`), `netadapter.setAdvancedProperty` (Class-key keyword + adapter restart), `netadapter.setMetric`, `registry.setValue` / `registry.deleteValue` (allowlisted roots only), `powercfg.setValue` (PowerWriteAC/DCValueIndex), `powercfg.setActiveScheme`, `appcompat.setLayers` / `appcompat.removeLayers`, `ifeo.resetMitigations`, `file.quarantine` (move to ACL'd quarantine; **never delete**), `firewall.addRule` / `firewall.removeGroup` (group "VTDiag"), `defender.addExclusion` (Riot paths only, off by default), `bcd.setValue` (allowlist: testsigning off, nointegritychecks off, nx OptIn, hypervisorlaunchtype auto), `time.resync`, `dns.flush` (one-way, harmless), `process.closeGraceful` (session), `restorePoint.create`, `oneway.winsockReset` / `oneway.ipReset` (one-way, require reboot), `oneway.sfcDism` (streamed, long).

**Example fix: disable an unused Realtek adapter**

```json
{
  "id": "fix.net.disable_unused_adapter",
  "title": "fix.disable_adapter.title",
  "explain": "fix.disable_adapter.explain",
  "risk": "low", "reversibility": "exact", "admin": true, "reboot": "none", "scope": "persistent",
  "bind": { "adapter": { "kind": "Adapter", "fromSignal": "net.realtek25.unused", "pick": "one" } },
  "guards": [
    { "signal": "riot.game_running", "must": "False", "failKey": "guard.close_game" },
    { "check": "adapter.notDefaultRoute", "of": "$adapter" },
    { "check": "connectivity.otherAdapterOnline", "except": "$adapter" }
  ],
  "steps": [ { "op": "device.disable", "args": { "instanceId": "$adapter.id" } } ],
  "verify": [ { "probe": "device.state", "args": { "instanceId": "$adapter.id" }, "expect": { "enabled": false } } ],
  "deadman": { "seconds": 60, "check": "connectivity.internet" },
  "outcome": { "kind": "monitorCompare", "askKey": "outcome.stutter_gone", "monitorSignals": ["mon.frametime.periodic_spikes"] },
  "drift": { "watch": true, "reasonKey": "drift.adapter_reenabled" }
}
```

**Example fix: disable a conflicting service (Nahimic)**

```json
{
  "id": "fix.svc.disable_conflict_service",
  "risk": "low", "reversibility": "exact", "admin": true, "reboot": "none", "scope": "persistent",
  "bind": { "svc": { "kind": "Service", "fromSignal": "conflict.*.service_present", "pick": "all" } },
  "guards": [ { "signal": "riot.game_running", "must": "False", "failKey": "guard.close_game" } ],
  "steps": [
    { "op": "service.setStartType", "args": { "name": "$svc.id", "startType": "Disabled" } },
    { "op": "service.stop",         "args": { "name": "$svc.id", "timeoutSec": 20 } }
  ],
  "verify": [ { "probe": "service.config", "args": { "name": "$svc.id" }, "expect": { "startType": "Disabled", "state": "Stopped" } } ],
  "outcome": { "kind": "relaunch", "askKey": "outcome.past_loading_screen" },
  "drift": { "watch": true, "reasonKey": "drift.oem_reinstalled" }
}
```
The policy layer rejects this fix for any service on the protected list (vgc, vgk, WinDefend, BFE, mpssvc, EventLog, Winmgmt, TBS, Dnscache, wuauserv...), whatever the KB or a plan says.

**Journal record** (one JSON line, CRC32 of the JSON body appended):

```json
{"seq":41,"utc":"2026-10-21T19:02:11.512Z","session":"01JB6...","attempt":"9f1c...","fix":"fix.svc.disable_conflict_service",
 "step":0,"op":"service.setStartType","phase":"intent","args":{"name":"NahimicService","startType":"Disabled"},
 "before":{"startType":"Automatic","delayed":false,"state":"Running","hash":"a1b2"},"bootId":8812,"app":"0.2.0","kb":"2026.10.3"}  |crc=5E1A9C07
```
Phases: `intent → applied → verified | failed`, and for undo `undo-intent → undone | undo-failed | undo-conflict`.

### 4.4 Session state

```csharp
namespace VTD.Contracts.Session;

public enum Phase
{
    Intake, Scanning, Analyzed,                       // read-only part
    Proposing, AwaitingConsent, Applying, Verifying,  // one fix at a time
    AwaitingRestart,                                  // user must click "Restart now" (or "later")
    RestartRequested,                                 // persisted right before ExitWindowsEx; task registered
    Resuming,                                         // post-logon: recovery + post-reboot verify
    GuidedStep,                                       // manual/BIOS/ISP step in progress (may include restart to firmware)
    AwaitingOutcome, Monitoring,                      // playtest / "did it help?"
    Completed, RolledBack, Abandoned, NeedsAttention  // terminal / attention
}

public sealed record SessionState(
    Ulid SessionId, int SchemaVersion, long Revision, Phase Phase, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc,
    IntakeAnswers Intake, Ulid? SnapshotId, Ulid? AnalysisId,
    IReadOnlyList<FixAttempt> Attempts, string? CurrentFixId, GuideProgress? Guide,
    RestartInfo? Restart, ResumeInfo Resume, bool RestorePointMade, string? ImportedPlanId);

public sealed record FixAttempt(Guid AttemptId, string FixId, string BindingHash, AttemptStatus Status /* Applied, Verified, VerifyFailed, Undone, ... */,
    Outcome UserOutcome /* Fixed, NotFixed, Unsure, Pending */, bool RebootPending, DateTimeOffset AppliedUtc);

public sealed record RestartInfo(RestartKind Kind /* Normal, ToFirmware */, string ReasonKey, BootIdentity BootAtRequest,
    DateTimeOffset RequestedUtc, IReadOnlyList<Guid> AttemptsAwaitingPostRebootVerify);

public sealed record ResumeInfo(int Attempts, int MaxAttempts /*3*/, bool TasksRegistered, BootIdentity? LastSeenBoot);
```

---

## 5. Ranking

**Score per hypothesis *h*** (log-odds, deterministic, explainable):

```
score(h) = ln prior(h | chosen symptom category)
         + Σ_signals w(h, s, state(s)) × strength(s)        // JSON weights; Unknown ⇒ 0
         + 4.0 · [user error code ∈ h.errorCodes]            // official mapping is strong evidence
         − 2.0 · [h.category ∉ categories of chosen symptoms] // soft, not a veto
         + wave boost (if today ∈ h.wave)
         − 2.5 · [a fix for h was verified-applied and outcome = NotFixed]
veto ⇒ h removed; requires-gate false ⇒ h removed
```

- **Priors** come from `thread_count` with add-one smoothing inside each category. That makes Realtek, which tops the solved set, a strong default for "stutter".
- **Weights.** The initial weights come from the catalog's confidence labels (high 2.0, medium 1.0, low 0.4) and are tuned on the golden fixture corpus (§15) to maximize top-3 recall. Weights live in the KB, so tuning ships as a KB update.
- **"Unknown cause" pseudo-hypothesis** with a fixed score (≈ ln 0.15). If it ranks first, or the top hypothesis's normalized probability is < 0.35, the UI says *"We couldn't pin this down"* and offers the escalation ladder (§5.2) instead of guessing.
- **Display.** Softmax over the surviving hypotheses plus Unknown. Players see **bands** ("Very likely / Possible / Long shot"), never fake-precise percentages. Helpers see the numbers and the per-signal breakdown.
- **Question planner.** When the top two are within 1.0 log-odds and an `ask.*` signal separates them ("Does this happen in other games?", "Does a second Riot account work on this PC?"; long_tail shows account-side failures need this), ask **one** question chosen by expected information gain, at most 3 questions per session.
- **Health baseline** runs regardless of the complaint, so the tool can confirm "everything is healthy": the Vanguard readiness checklist (TPM 2.0, Secure Boot, HVCI/VBS, IOMMU, NX/virtualization, Windows build ≥ 19045, no test-signing), pending reboot, disk free space, clock, driver problem codes, and required services. These show as a green/amber checklist even when nothing is wrong.

### 5.1 Fix proposal order

For each candidate fix *f* of each surviving hypothesis *h*:
`utility(f) = P(h) × P(f works | h) / cost(f)`, where `P(f works | h)` comes from the catalog's fix evidence and counts (op_confirmed 19 ⇒ high), and `cost = 1 + 2·[reboot] + 3·[risk=medium] + 5·[oneWay] + minutes/5`. High-risk fixes have no utility because they are guides. The UI shows the top-utility fix as "Let's try this first", with the reason: *"It's quick, harmless and undoable, so we'll try it before the bigger stuff."*

### 5.2 Escalation ladder: a path for every software problem

When no KB hypothesis fits, the ladder runs from least to most invasive, with each step consented:
1. **Faulting-module matcher.** An unknown DLL in App 1000 or CodeIntegrity 3033 events → look up the owning product from the file path, signer and uninstall entry → generic bound fix (`fix.generic.close_process`, `fix.svc.disable_conflict_service` with an entity bound) plus a flag in the report so the KB grows (long_tail's `n64hooks.dll`, `udp_connect_lsp64.dll` cases).
2. **Winsock LSP / network filter check** → guided removal of the owning app.
3. **Official Riot flows**: Riot Client Repair (guided), Vanguard clean reinstall (scripted with reboot-resume, one-way), RRT log gathering (guided; we never touch the RRT zip).
4. **Clean-boot bisection (M5).** Session-scoped disabling of non-Microsoft services and startup items in halves, one reboot plus playtest per round, about log2(N) rounds. Every round is user-initiated, so it can't loop. At the end, everything except the culprit is restored.
5. **One-way repairs**: network stack reset, SFC/DISM. Each is labeled "can't be undone", with its own consent.
6. **Helper mode** export and Riot-ticket package (redacted report plus what was already tried).

Out of scope, detected and redirected kindly: console threads, VM/ARM/Mac, VAN 152 / HWID restriction ("This needs Riot's appeal, there's nothing to fix on your PC"), Riot server incidents (status JSON), and "your hardware can't meet this requirement" (long_tail: 146 unsolved HVCI/IOMMU threads, so the tool says it clearly instead of looping through fixes).

---

## 6. Session state machine, reboot-resume and crash safety

### 6.1 Machine

```
Intake ─► Scanning ─► Analyzed ─► Proposing ─► AwaitingConsent ──(skip)──► Proposing (next)
                          ▲                        │consent(token)
                          │                        ▼
                          │                    Applying ──(guard/policy fail)──► Proposing (explain why)
                          │                        │ all steps applied
                          │                        ▼
                          │                    Verifying ──(verify fail)──► auto-Undo ─► Proposing
                          │                        │ ok
                          │          ┌─────────────┴──────────────┐
                          │   reboot=required                reboot=none
                          │          ▼                            ▼
                          │   AwaitingRestart ──(later)──► (task registered; resume at next logon)
                          │          │ user clicks Restart       │
                          │          ▼                            │
                          │   RestartRequested ─► [reboot] ─► Resuming ─► post-reboot Verify
                          │                                       │
                          └──── rescan/rerank ◄─ AwaitingOutcome ◄┘ ◄── Monitoring (optional playtest)
                                   NotFixed            │ Fixed
                                                       ▼
                                          Completed (keep or undo; report)
   Any phase ──(user: Stop & undo all)──► RolledBack        Any phase ──(expired / 3 resume attempts)──► NeedsAttention
```

The machine is a **pure function**, so it can be property-tested (§15):

```csharp
public static class SessionMachine
{
    public static Transition Next(SessionState s, SessionEvent e, Clock now);   // no I/O
}
public sealed record Transition(SessionState State, IReadOnlyList<Effect> Effects);  // Effects: RunScan, ApplyFix, RegisterResume, Restart, ...
```

`SessionController.HandleAsync(e)` computes the transition, **persists the new state (revision+1) before executing any effect**, then runs the effects, each of which emits events back. Effects are idempotent, keyed by `(sessionId, revision, effectIndex)`, so re-running an effect after a crash is safe.

### 6.2 Persistence layout (`%ProgramData%\VTDiag\`, ACL: SYSTEM and Administrators full; the interactive user gets read on `reports\` only)

```
session/current.json        # atomic: write current.json.tmp → FlushFileBuffers → File.Replace(tmp, current, current.bak)
session/current.bak
journal/<sessionId>.jsonl   # append-only, one fsync per record (FileOptions.WriteThrough)
backups/<attempt>/...       # large captures: reg export, bcd export, service qc dumps
quarantine/<attempt>/...    # moved files (never deleted), with original path + hash manifest
snapshots/<snapshotId>.json # raw (unredacted) snapshot, local only
reports/                    # redacted exports
logs/
```

- If `current.json` fails to parse or validate → use `current.bak` → if that also fails, **rebuild state from the journal** (the journal alone is enough to list every applied change and undo it).
- At least 200 MB of free disk space is required before any apply. If a journal write fails (disk full, AV lock), **the operation is aborted before it starts**.

### 6.3 Apply protocol, per operation

```
1. policy = op.CheckPolicyAsync(args)            → deny ⇒ stop, explain
2. guards = fresh probes                         → fail ⇒ stop, explain
3. before = op.CaptureAsync(args)                → failure ⇒ stop (no backup, no change)
4. journal.Append(intent{before})  + fsync
5. result = op.ApplyAsync(args, before)
6. journal.Append(applied{after=op.Probe})  + fsync
   (multi-step fix: on step k failure ⇒ undo steps k-1..0 in reverse, journal each)
7. verify probes ⇒ journal.Append(verified|failed)
8. deadman (if any): UI shows "Is your internet working? [Yes, keep] [No, undo]"; engine also probes NLM.
   No confirmation in N s, or the probe fails ⇒ automatic undo.
```

The `ConsentToken` minted by the UI on click contains `(fixId, bindingHash, previewHash, expiry 5 min)`. The engine refuses to apply if the preview it would show now differs (TOCTOU guard: the adapter changed, the service disappeared).

### 6.4 Reboot-resume: loop-proof by construction

**Invariant R1.** No code path calls `ExitWindowsEx` / `InitiateShutdown` except the handler for the UI's **"Restart now"** button, and that handler accepts only a click-minted `RestartToken`. On resume the engine has no way to restart the PC, so automatic reboot loops can't happen.

**Restart sequence** (user clicked "Restart now"):
1. Persist `Phase=RestartRequested`, `Restart.BootAtRequest = {BootId, LastBootUpTime}`.
2. Register both resume tasks with `EndBoundary = now + 7 days`. Read them back to confirm (`ITaskFolder.GetTask`). If registration fails, fall back to the HKCU RunOnce UI launcher. If that fails too, tell the user *"After restart, open VTDiag from the Start menu."*
3. Flush everything, then call `InitiateShutdownW(SHUTDOWN_RESTART [| SHUTDOWN_RESTART_BOOTOPTIONS for firmware])`. This is a real restart, not a hybrid shutdown. For "Restart into BIOS" use the equivalent of `shutdown /r /fw` [T: check on vendors without firmware boot support; fallback text: "press Del/F2 while it starts"].

**Resume** (`VTDiag.Engine.exe --resume <id>` from the Highest task):
```
acquire Global\VTDiag.Engine mutex (else exit: another engine is already handling it)
s = LoadState()                                   // .bak / journal fallback
if s.SessionId != arg or s.Phase ∉ {RestartRequested, AwaitingRestart, GuidedStep}:
     UnregisterTasks(); exit                      // stale task: clean up, do nothing
s.Resume.Attempts++ ; Persist(s)                  // BEFORE any other work
if s.Resume.Attempts > 3 or now > s.ExpiresUtc:
     UnregisterTasks(); s.Phase = NeedsAttention; Persist; wait for UI → "We couldn't continue automatically. Here's what we changed: [Undo all] [Keep]"
     exit
boot = ReadBootIdentity()
if boot == s.Restart.BootAtRequest:              // no real reboot happened (Fast Startup "shut down", or the user hasn't restarted)
     keep tasks; Phase stays; UI shows "Your PC didn't fully restart. Please use Restart (not Shut down)."
     (Attempts is NOT counted for this case: Attempts-- and persist, capped by ExpiresUtc)
else:
     RunRecovery()                                 // §6.5
     run post-reboot verify for AttemptsAwaitingPostRebootVerify
     Persist(Phase=AwaitingOutcome)
     wait (≤ 5 min) for the UI to connect (Resume-UI task, or user opens the app)
     on UI connect: UnregisterTasks(); Persist(Resume.TasksRegistered=false)
```
- If the engine crashes during resume, the next logon retries, still capped at 3. A guaranteed crash on startup can therefore cost at most three logons, then the tasks are gone and a manual launch lands on NeedsAttention.
- The UI on resume appears within about 5 s: *"Welcome back! We're checking whether the fix worked..."*. It shows a **[Stop and undo everything]** link on every resume screen.
- Uninstall (Inno `[UninstallRun]`) calls `VTDiag.Engine.exe --uninstall`. That deletes the `\VTDiag\` task folder and RunOnce entry, offers "Undo all changes made by VTDiag", and only then removes files. The journal and backups are kept in ProgramData unless the user ticks "remove everything".
- **Safe Mode is never used in MVP.** If a later version adds the DDU flow, it adds a "safeboot failsafe": a `*`-prefixed RunOnce whose only job is `bcdedit /deletevalue {current} safeboot`, written *before* safeboot is set, and verified on every boot.

### 6.5 Crash recovery (runs at every engine start, before any RPC)

```
for each journal record with phase ∈ {intent, undo-intent} and no terminal successor:
    cur = op.Probe(args)
    if cur ≈ before   → mark "not-applied" (crash before change)       → state consistent
    if cur ≈ intended → mark "applied (recovered)" and queue verify    → state consistent
    else              → mark "in-doubt" → NeedsAttention: show diff, offer [Restore previous] (Undo Force from `before`) or [Keep current]
```
Multi-step fixes with a partial application are rolled **back** by default ("all or nothing"). The user is told: *"Your PC turned off while we were changing something. We put it back the way it was."*

### 6.6 Undo center

- Reachable from the main menu, the Start-menu shortcut "VTDiag – Undo changes", and `VTDiag.Engine.exe --undo-all` (for helpers and support).
- Lists every attempt across sessions with status, date and reversibility. Undo runs the steps in reverse with `UndoMode.IfUnchanged`. If the current state differs from what we left (someone else changed it), the UI asks: *"This setting was changed again after our fix (probably by Windows Update). Put it back to how it was before VTDiag? [Yes] [Leave it]"*.
- One-way attempts show what was done and the restore-point date, with no fake undo button.
- **Drift watch.** On each rescan, re-probe fixes with `drift.watch`. *"Nahimic came back after a Windows update. Turn it off again?"*. This is common, since the catalog notes that Windows Update and OEM tools reinstall it.
- **Restore point.** One per session, before the first persistent system fix. Enable System Protection with consent if it's off, work around the 24 h throttle by setting the frequency value temporarily (itself journaled and restored), and confirm the point exists afterward. If it fails, show *"Windows wouldn't make a checkpoint. Our own undo still works."* and continue only on explicit OK.

---

## 7. User flow, screen by screen

Wording rules: about 6th-grade reading level. Say what happens next. Never blame the user. Explain jargon inline ("Secure Boot, a security switch inside your PC's BIOS"). No red walls of text. One primary button per screen. Every screen has a visible "What does this mean?" expander for the technical detail helpers want.

| # | Screen | Content | Primary / secondary |
|---|---|---|---|
| 0 | **Welcome** | "Let's find out why VALORANT isn't working right." 3 promises: *We only change things you approve · Everything can be undone (we'll warn you if not) · Safe with Vanguard: we never touch the game.* Language switch EN/TR (auto from Windows) | **Start** / Privacy details |
| 1 | **What's wrong?** | Big chips: Won't start / Error code / Stutter or low FPS / Lag or ping / Crash or freeze / Voice or sound / Just check my PC. Error-code box with autocomplete ("VAN 9003 – Secure Boot"). Optional "When?" chips (in menu, in match, at launch, always) | **Next** |
| 2 | **Permission** | "Windows will ask: *Do you want to allow this app to make changes?* Click **Yes**. We need it to read security settings and to fix things." | **Continue** / Scan without permission (limited) |
| 3 | **Scanning** (30-60 s) | Friendly area list with ticks (Security · Drivers · Apps · Network · Game files · Logs). Each area has a timeout; a slow area shows "Skipping, taking too long" and never hangs | Cancel |
| 4 | **Health check** | Green/amber checklist: Vanguard requirements, Windows, drivers, network, background apps. "Good news: 14 things look healthy." | **See what we found** |
| 5 | **Suspects** | Ranked cards: *Very likely: your unused wired network port (Realtek 2.5G) is causing the stutters.* "Why we think so" (3 bullets of evidence). Badges: ⏱ 10 s · ↩ Undoable · 🔁 No restart · 🟢 Low risk. Unknown-cause card when applicable | **Try the first fix** / Show all / Get help from a helper |
| 6 | **Fix detail & consent** | What we'll change (plain), how to undo, what you'll notice ("You'll stay on Wi-Fi, internet keeps working"). Restore-point note on first system fix | **Fix it** / Skip / Show me how to do it myself |
| 7 | **Applying** | Step list with live ticks. Close is disabled with the note *"Almost done, closing now could leave things half-finished."* (The engine continues even if the window is killed) | — |
| 7b | **Keep this change?** (deadman) | "Is your internet still working?" 60 s countdown, auto-undo | **Yes, keep** / Undo |
| 8a | **Done, now test** | "Play one Deathmatch or 5 minutes in the Range. We'll watch quietly in the background. It won't affect your FPS." | **Start test (monitor on)** / Test without monitor |
| 8b | **Restart needed** | "This change only works after a restart. Save your stuff first. We'll open again automatically after you sign in." Shows what happens after the restart | **Restart now** / Restart later (resume at next sign-in) |
| 9 | **Welcome back** (resume) | "We're checking the fix... ✅ Memory Integrity is ON now." | **Continue** / Stop and undo everything |
| 10 | **Did it help?** | Yes / No / Not sure. Monitor comparison when available: *"Stutters: 14/min before → 0/min after."* | Yes → Completed / No → next suspect |
| 11 | **Guided step** (BIOS, ISP, uninstall) | Pre-flight results (GPT ✅, BitLocker ON ⚠ → "First, save your recovery key", with steps and an aka.ms link), board-specific instructions (vendor detected: key to press, menu path), **QR code to the same guide on GitHub Pages** (the PC screen is in BIOS, so the phone shows the steps), print option. "When you're ready, we'll restart straight into BIOS and continue here afterwards." | **Restart into BIOS** / I'll do it later |
| 12 | **Completed** | Summary, "Keep changes" by default, report export, "Monitor my next matches" toggle | **Save report** / Undo something |
| — | **Tray** | Monitoring status, "Mark last match as bad", Open, Stop monitoring | — |
| — | **Undo center**, **Report**, **Settings**, **Helper mode** | §6.6, §10, §9 | — |

Microcopy examples (EN / TR):
- Bad: "Disabling device PCI\VEN_10EC&DEV_8125 via CM_Disable_DevNode." Good: "We'll turn off the wired network port you're not using. You're on Wi-Fi, so your internet stays on. One click turns it back on." / "Kullanmadığın kablolu ağ girişini kapatacağız. Wi-Fi'dasın, internetin kesilmez. Tek tıkla geri açabilirsin."
- One-way: "This one can't be undone with a click. It resets your network settings to Windows defaults. You'll need to restart." / "Bu adım tek tıkla geri alınamaz..."
- Out of scope: "This isn't something on your PC. Riot has limited your account or hardware for a while. Here's how to appeal."

---

## 8. Live monitor

### 8.1 Hard rules (Vanguard and FPS)

- **No handle of any kind** to `VALORANT-Win64-Shipping.exe`, `VALORANT.exe`, `vgc.exe`, `vgtray.exe` or `Riot*`. PIDs come from the ETW process provider or a ToolHelp snapshot (which needs no handle). The PresentMon filter is by process name.
- No injection, no hooks, no overlay windows, no `SetWindowsHookEx`, no foreground stealing, **no toasts or popups while the game runs** (toast after the game exits).
- **Never `timeBeginPeriod`** (it changes global timer resolution and the game's scheduling). Use waitable timers at default resolution.
- The monitor never runs together with a scan or a fix. The engine refuses fixes while monitoring, and while the game runs.
- The UI window closes to the tray during monitoring (no WPF rendering, so no GPU work).

### 8.2 Metrics and method

| Metric | Source | Rate | Notes |
|---|---|---|---|
| Frame times, present mode, tearing, dropped | PresentMon child (`--process_name VALORANT-Win64-Shipping.exe --output_stdout --no_console_stats --session_name VTDiag_PM --stop_existing_session --terminate_on_proc_exit`) | per frame, parsed with a Span-based CSV reader, aggregated per second | Raw frames are kept only in a 120 s ring buffer (for spike context), then dropped |
| CPU util and % of max frequency | PDH `\Processor Information(_Total)\% Processor Utility`, `% of Maximum Frequency` | 1 Hz | Throttle proxy, no temperature driver |
| GPU engine util, dedicated VRAM | PDH `GPU Engine(*engtype_3D)` (summed over the game's pid_ instances), `GPU Adapter Memory` | 1 Hz | Vendor-neutral |
| GPU temp, clocks, throttle reasons | NVML (`nvml.dll` from the driver) / ADLX, optional | 0.5 Hz | Off on the first degrade step |
| Server endpoint | TraceEvent `Microsoft-Windows-Kernel-Network` UDP send/recv, PID-filtered, **sampled for 10 s every 60 s** | burst | Finds the game server IP:port with no handle |
| RTT, jitter, loss | ICMP echo to the endpoint (fallback: last responding traceroute hop), 1/s, 32-byte payload | 1 Hz | Labelled "approximate" when ICMP is filtered |
| System events | `EventLogWatcher` subscriptions (push, no polling): rt25cx21/rt640x64 resets, Display 4101 (TDR), WHEA, Kernel-Power, Kernel-Processor-Power 37, App 1000 for Riot exes | event-driven | Correlation anchors |
| Background spikes | ETW Kernel-Process start/stop (names only) | event-driven | "Windows Update / AV scan started at 12:41:03" |
| Disk queue, available memory, standby list | PDH | 0.2 Hz | Low-RAM / HDD hypotheses |
| DPC/ISR latency | **Not during matches.** A separate 60 s "latency test" (TraceEvent kernel DPC/ISR on an own-named session) run in the menu or Range | on demand | M5 |

### 8.3 Overhead budget (enforced)

| Resource | Budget (monitor + PresentMon combined) | Enforcement |
|---|---|---|
| CPU | avg ≤ 1.0 % of total machine, p99 per-second ≤ 3 % (reference: 6C/12T) | `SelfBudget` reads `GetProcessTimes` for itself and the child every 5 s |
| Memory | ≤ 100 MB working set | `SetProcessInformation(ProcessMemoryPriority = low)`; ring buffers preallocated; `<ConcurrentGarbageCollection>false</…>`, workstation GC |
| GPU | 0 | no rendering |
| Disk | ≤ 64 KB/s, one batched write per 30 s | summaries only |
| Network | ≤ 1 ICMP/s | endpoint sampling 10 s/min |
| Scheduling | `BELOW_NORMAL_PRIORITY_CLASS` + EcoQoS (`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) | the OS prefers efficiency cores on hybrid CPUs, which keeps us off the P-cores the game needs |

**Degrade ladder** when the budget is exceeded for 30 s: (1) drop vendor GPU polling, (2) PDH to 0.5 Hz, (3) stop endpoint ETW sampling and ping, (4) frames-only mode, (5) stop entirely and note "monitor stopped to protect your FPS" in the summary.
**Acceptance test** [T]: in a Range benchmark with a fixed route, avg FPS and 1% lows with the monitor on differ from off by less than run-to-run noise (target < 1 % avg, < 2 % 1%-low), and no VAN error occurs over 3 × 30-min matches on Win10 22H2, Win11 24H2 and 25H2.

**ETW hygiene.** Use fixed session names (`VTDiag_PM`, `VTDiag_Net`). At start, stop leaked sessions with the same names (a crash leaves them running, and there are only 64 slots system-wide). The monitor and PresentMon run in the engine's job object, so they die with it.

### 8.4 Detectors, thresholds and feedback into hypotheses

| Signal emitted | Rule (per match segment ≥ 3 min) | Feeds hypotheses |
|---|---|---|
| `mon.frametime.spike` | frame time > max(2.5 × rolling 5 s median, median + 8 ms) | base for the others |
| `mon.frametime.periodic_spikes` | ≥ 6 spikes and the inter-spike interval autocorrelation peak sits at 2-15 s with ≥ 0.5 normalized strength | realtek-2-5gbe-nic-stutter, usb-selective-suspend-freeze, ftpm-event-14-17-stutter |
| `mon.spikes_correlate.nic_event` | ≥ 3 spikes within ±1 s of an rt25cx21/rt640x64 or NDIS reset event | realtek (+3.0), whole-pc-freeze-kernel-power-41 |
| `mon.spikes_correlate.process:<name>` | ≥ 3 spikes within 2 s after a process start of the same image | overlay/hook, cleaners, AV scans (conflict ids by image name) |
| `mon.stutter_rate` | spikes/min: < 1 good, 1-5 noticeable, > 5 bad | outcome comparison |
| `mon.cpu.throttle` | % of max freq < 70 for ≥ 30 s while CPU util > 50 % | power-plan-throttling-low-fps, thermals |
| `mon.gpu.underutilized` | GPU 3D util < 60 % AND fps < cap − 10 % AND (one core saturated or CPU util > 85 %) for ≥ 60 s | low-gpu-usage-after-ue5-patch, intel-hybrid-cpu-ecore-stutter, fps-lower-than-expected-caps |
| `mon.presentmode.composed` | > 80 % of frames `Composed: Flip` while the game is set to Fullscreen | fullscreen-optimizations-mpo, fps-decay-over-time |
| `mon.vram.saturated` | dedicated usage > 95 % of budget for ≥ 30 s | shader-cache, low-spec |
| `mon.ping.jitter_high` / `mon.ping.loss` | jitter (stddev RTT) > 10 ms, or loss > 1 % over 60 s | local-network-packet-loss-jitter; with a clean first-hop loss → isp-routing-high-ping |
| `mon.ping.local_vs_remote` | loss/jitter already at hop 1 (router) → local; only beyond the ISP hop → ISP | splits Wi-Fi/LAN from ISP |
| `mon.gpu.tdr` | Display 4101 during a match | gpu-driver-corrupt, amd-adrenalin-driver-regression |
| `mon.game_exit_crash` | game exit plus App 1000 within 10 s | app_crash_module path |

At game exit the monitor emits a `MonitorSummary` (per-minute series, spike list with context, detector outputs). Analysis re-ranks with the `mon.*` signals and the UI shows *"After watching your match: the stutters line up with your network card resetting. That makes the Realtek fix very likely."* For outcome checks, `monitorCompare` contrasts stutter rate, 1% lows and jitter before and after a fix. "Mark last match as bad" in the tray tags that segment for comparisons.

---

## 9. Assisted mode (replaces AnyDesk sessions)

```
Player                                   Helper (mod, or helper's AI agent)
------                                   ---------------------------------
Scan → "Get help from a helper"
  → redaction preview → save file
  VTDiag-<shortid>.vtdscan  ─────(Discord / Reddit DM / any)────►  VTDiag Helper mode (portable, no admin, nothing changes on helper PC)
                                                                     - full fact browser (search, raw values), ranking with per-signal math
                                                                     - KB fix catalog filtered by applicability to THIS snapshot
                                                                     - Plan builder: pick fixes/guides/questions, bind entities from snapshot,
                                                                       write notes (plain text), order steps
                                                                     - "Copy for AI" = markdown digest + plan.schema.json for an AI agent
                          ◄────────────  VTDiag-<shortid>.vtdplan  ──
Open plan → validation → step-by-step,
  same consent screens, badges "Suggested by <helper>"
→ results exported as .vtdscan (v2, with journal + outcomes) ───────►  helper reviews, sends next plan
```

**Formats**
- `.vtdscan`: a zip containing `manifest.json` (ids, versions, SHA-256 of each part), `snapshot.redacted.json`, `analysis.json`, `journal.summary.json`, `monitor/*.json`, `report.html`. It always holds the **redacted** snapshot, and the player saw the preview first.
- `.vtdplan`: JSON validated against `kb/schema/plan.schema.json`:

```json
{
  "schema": 1, "planId": "01JB7...", "forSnapshotId": "01JB6...", "kbMin": "2026.10.1",
  "createdUtc": "2026-10-21T20:10:00Z", "expiresUtc": "2026-10-28T20:10:00Z",
  "author": { "name": "u/ModName", "publicKey": "ed25519:...", "signature": "..." },
  "message": "Hey! Looks like your spare network port. Try step 1, then play a DM.",
  "steps": [
    { "kind": "fix",      "fixId": "fix.net.disable_unused_adapter", "bind": { "adapter": "PCI\\VEN_10EC&DEV_8125&..." }, "note": "You're on Wi-Fi so this is safe." },
    { "kind": "monitor",  "minutes": 10, "note": "Play one Deathmatch." },
    { "kind": "question", "askId": "ask.only_valorant_affected" },
    { "kind": "guide",    "guideId": "guide.net.realtek_driver_update" }
  ]
}
```

**Plan safety (the helper is an untrusted person, and so is any AI that wrote the plan):**
- Steps can only be KB fix, guide, question or monitor IDs. **No commands, paths, registry keys, URLs-as-actions or scripts.** Notes render as plain text; URLs are shown but marked "link from helper", never auto-opened.
- The KB version must be ≥ `kbMin`. The plan expires after 7 days. The plan is bound to `forSnapshotId`, and the app does a **fresh rescan**: every binding must resolve to a live entity with the same stable ID, and every guard and protected-target policy is re-checked. Steps that fail validation are shown greyed out with the reason.
- Maximum 10 steps. High-risk items are guides only. One-way fixes need their own extra confirmation. **Every step goes through normal per-step consent; there is no "apply all".**
- Trust badges: a plan signed by a key on the KB's `trusted_helpers.json` (subreddit mods) shows "Verified r/ValorantTechSupport helper". Unsigned plans show "From someone you know? Only continue if you trust them." Signing raises trust but never removes consent.
- An AI agent can write plans, because the schema is published in `docs/`, which mirrors the moderator's current workflow. It still can only choose KB IDs.

---

## 10. Report format

Three renderings from one `ReportModel` (schema `kb/schema/report.schema.json`, versioned):

1. **`VTDiag-Report-<yyyymmdd>-<shortid>.html`**: single file, inline CSS, **no JavaScript**, so it opens anywhere, can be printed, and email or Discord previews can't execute anything. Sections: summary verdict, health checklist, ranked suspects with evidence, actions taken (status, verify, undo state, reboot), monitor charts (inline SVG), system specs, collector coverage (what we couldn't read and why), redaction manifest, app/KB version, report ID plus SHA-256.
2. **Reddit/Discord markdown** (copy button), ≤ 9,000 characters so it fits a Reddit comment (10k limit): specs one-liner, Vanguard readiness line, top 3 suspects, what was tried and the outcome, monitor key numbers, and "Full report: attach the .html". This targets the 2,180 threads that never got a reply, because helpers can answer from the summary alone.
3. **`.vtdreport` JSON** (the machine part of `.vtdscan`) for helpers and tooling.

Language: the report UI language is the player's, but **"Helper language" defaults to English** for the attached machine section. Machine keys are always English, so a Turkish player's report is readable on the English subreddit.
Later (M5): an offline viewer on GitHub Pages that decodes a compressed report from the URL **fragment** (never sent to a server), for one-link sharing of the summary.

---

## 11. Internationalization

- **UI**: `Strings.resx` (en, neutral) + `Strings.tr.resx`, accessed through a generated strongly-typed `L` class, with runtime switching (`LocalizationManager` raises property changed, no restart).
- **KB text**: `kb/strings/en.json` and `tr.json`, flat keys (`rule.realtek25.title`), ICU MessageFormat placeholders and plurals (`{count, plural, one {# stutter} other {# stutters}}`). Translators edit one file per language. CI fails if any key is missing in `en`; a missing `tr` key falls back to `en` with a lint warning.
- **Turkish-specific traps.** All matching and normalization uses `StringComparison.OrdinalIgnoreCase` / `ToUpperInvariant`. Culture-sensitive overloads are **banned by analyzer**, because `"nahimic".ToUpper()` under tr-TR gives `"NAHİMİC"` and breaks matching. Number and date formatting follows the UI culture on screen and invariant culture in machine files.
- **OS-locale independence.** Collectors never parse localized text (no `systeminfo`, no `netsh` output scraping). Match on InstanceIds, numeric values, GUIDs and event IDs.
- **Layout.** Pseudo-localization build (`qps-ploc`, +40 % length) in CI screenshots, because Turkish strings run about 30 % longer. No text baked into images (BIOS diagrams are SVG with string slots).
- **Language detection.** First run picks tr if the Windows UI culture is tr-*, otherwise en. Switchable on every screen header.
- **Reviewer rule.** Turkish strings for fixes and warnings need a native-speaker review label on the PR. Risk wording must not soften in translation.

---

## 12. Privacy and redaction

Collected by default: technical state only. **Not collected:** browser data, documents, full process command lines (they hold Riot auth tokens), Vanguard log contents, public IP, Riot ID / PUUID, Wi-Fi passwords.

`IRedactor` runs on **every export** and **every log line**. Redaction is attribute-driven (§4.1 `Sensitivity`) and followed by a regex sweep:

| Item | Treatment |
|---|---|
| Windows username, profile paths | `C:\Users\<user>\…` → `%USERPROFILE%\…` |
| Computer name, domain | removed |
| MAC addresses | keep OUI (vendor, useful for NIC identification) + salted hash of the rest |
| Serial numbers (BIOS, disk, TPM EK, board) | dropped |
| Local IPv4/IPv6 | class kept (`192.168.x.x` → `private/24`) |
| Game server IP | kept as region/ASN only, e.g. "AWS Frankfurt" [T: offline ASN table], unless the user ticks "include for ISP ticket" |
| SSIDs | hashed |
| ShooterGame.log / crash XML | parsed through a **whitelist of line patterns**; `-remoting-auth-token`, `-subject`, MachineId, LoginId, UserName, CommandLine are stripped at parse time, so they never reach memory structures we export |
| Event messages | only provider, ID, timestamp and whitelisted params (faulting module, exception code, adapter name) |
| `MachineKey` | HMAC of stable hardware IDs with a per-install random salt that is never exported, so a helper can tell two reports came from the same PC without learning any serial |

- **Preview before export**: "Here is exactly what's in this file" with a searchable tree and highlighted redactions. The user can untick sections (for example the app list).
- **No telemetry** by default, no backend. Optional later: opt-in anonymous outcome stats (rule id, fix id, outcome) to tune weights, only if a free host is available, with the exact payload shown.
- The optional AI explain mode (local Ollama or Foundry Local, M5) sees the redacted model only and **can only reference KB IDs**.

---

## 13. Safety rules and failure modes

### 13.1 Rules (enforced in code, not just written down)

| # | Rule | Enforcement point |
|---|---|---|
| S1 | One fix per consent; no batch-apply, not even from plans | `ConsentToken` per attempt; FixRunner rejects a second concurrent attempt |
| S2 | No backup, no change | `CaptureAsync` + journal fsync before `ApplyAsync`; abort on failure |
| S3 | Guards re-checked with fresh probes at apply time | FixRunner; preview hash in the token |
| S4 | **Protected targets**: boot/system disk controller, display adapters, input HID, the default-route NIC, vgc/vgk (except the official Vanguard-reinstall fix), Defender/firewall/WU/BFE/EventLog/Winmgmt/TBS services, `System32` files except the allowlisted quarantine set (`ene.sys` via guide only, `392667600.dat`, `vgkbootstatus.dat`) | `ProtectedTargetPolicy`, unit-tested, not overridable by KB or plans |
| S5 | **Never lower security as a fix**: never turn off Secure Boot, TPM, HVCI, Defender, firewall or UAC. Defender exclusions only for Riot paths, off by default, with a warning | policy + KB lint (rejects ops that set these to "off") |
| S6 | No unattended reboot; the engine has no restart capability on resume | `RestartToken` minted only by a UI click; resume code path has no reference to the restart service |
| S7 | KB is data, signed, primitives-only, allowlisted args | Ed25519 check on load; bundled KB fallback; JSON schema; registry/BCD allowlists |
| S8 | Vanguard: no driver, no injection, no game-process handle, no overlay, no `timeBeginPeriod`, no game memory | banned-API analyzer (`OpenProcess`, `ReadProcessMemory`, `CreateRemoteThread`, `SetWindowsHookEx`, `NtQuerySystemInformation` handle classes...), `ProtectedProcessNames` check inside `Platform.ProcessSnapshot` |
| S9 | No system fixes while VALORANT, the Riot Client or a Windows Update install is running, or a Windows reboot is pending (offer "restart first") | standard guards added to every persistent fix |
| S10 | Long ops (SFC/DISM, Vanguard reinstall) only on AC power or > 40 % battery | guard |
| S11 | BitLocker on + any firmware/boot change → recovery-key step first | guide pre-flight |
| S12 | One-way actions are labelled, need a second consent, and come after a restore point | UI + FixRunner |
| S13 | Files are never deleted, only quarantined with a manifest | `file.quarantine` is the only file-removal primitive; `File.Delete` banned elsewhere |
| S14 | Honest verification: "applied" ≠ "fixed"; we report both | verify vs outcome phases |
| S15 | Network-cutting changes have a deadman auto-revert | `deadman` in the fix schema; validator requires it for network ops |

### 13.2 Banned-API enforcement

`banned/Banned.Common.txt` applies to every project. `Banned.ReadOnly.txt` covers Contracts, Platform, Collectors, Knowledge, Analysis, Monitor.Core and Reporting. `Banned.Ui.txt` covers App. CI also scans the published binaries' import tables and fails if `ReadProcessMemory`, `WriteProcessMemory`, `CreateRemoteThread` or `SetWindowsHookExW` appear, as a belt-and-braces check for Vanguard and AV heuristics.

### 13.3 Failure modes

| Failure | Detection | Response | What the user sees |
|---|---|---|---|
| Power cut / BSOD during apply | intent without outcome in the journal | Recovery: Probe → roll forward or back; partial multi-step → roll back | "Your PC turned off while we were changing something. We put it back." |
| Engine crash | UI heartbeat lost | Restart engine (UAC) → Recovery first | "The checker stopped. Nothing is lost." |
| UI crash or GPU reset mid-fix | engine sees the pipe drop | engine finishes the current op and verify, persists, waits | On reopen: the result screen as if nothing happened |
| `current.json` corrupt | parse or schema failure | `.bak` → journal rebuild | Nothing (or "Recovered your session") |
| Journal write fails (disk full, AV lock) | IOException | abort before change | "Not enough space / blocked. Nothing was changed." |
| User chose Shut down with Fast Startup | BootId unchanged | keep tasks, don't count the attempt | "Your PC didn't fully restart. Use Restart." |
| Task Scheduler disabled by a debloater | registration readback fails | HKCU RunOnce UI fallback; else manual-open hint | "After restarting, open VTDiag from Start." |
| Resume crashes repeatedly | Attempts > 3 | tasks deleted → NeedsAttention | "We couldn't continue automatically. Here's what changed: [Undo all] [Keep]" |
| Session forgotten for weeks | `ExpiresUtc` passed | close session; changes stay; undo center keeps them | "Your last session expired. Your changes are listed in Undo." |
| Disabling the NIC kills the internet | NLM probe / deadman | auto-undo within 60 s | "Internet dropped, so we turned it back on." |
| Windows Update / OEM tool reverts a fix | drift probe on rescan | offer to re-apply; count as a "recurring" note in the report | "Nahimic came back after an update." |
| Our registry change is blocked (Defender Tamper Protection, policy) | verify mismatch | auto-undo partial, report honestly | "Your security software blocked this change. Here's how to do it yourself." |
| AV quarantines VTDiag files | startup self-check of the file manifest | stop; guide to restore + report false positive (WDSI link) | "Your antivirus removed part of VTDiag by mistake. Here's how to fix it." |
| ETW session leaked by a crash | name exists at start | stop the stale session | Nothing |
| Monitor over budget | SelfBudget | degrade ladder → stop | "Monitor paused to protect your FPS." |
| PresentMon missing or blocked | spawn failure / no output in 20 s | monitor continues without frames; frame signals Unknown | "Frame data unavailable this time." |
| Broken WMI repository | CIM timeouts across several collectors | mark Unknown; `wmi_broken` signal → guided repair | "Some Windows info couldn't be read." |
| Localized / modified Windows (Atlas, Tiny11) | `modified_windows` conflict signal | warn: fixes may behave differently; extra caution | Banner "This Windows is modified..." |
| Restore point unavailable | CreateRestorePoint failure / missing point | proceed only on explicit OK; journal still works | "Windows wouldn't make a checkpoint. Our undo still works." |
| Plan references a missing entity | fresh-scan binding check | step greyed out with the reason | "This step doesn't match your PC anymore." |
| Two instances | mutex | second instance focuses the first | Nothing |
| App update while a session is active | updater checks the session | update deferred | "Update ready, we'll install it after you're done." |
| Clock badly wrong (breaks signature or expiry checks) | `clock` signal (offset > 5 min) | offer time resync fix first; tolerate skew in expiry checks | "Your PC clock is off by 3 hours. Let's fix that first." |

---

## 14. Antivirus false-positive strategy

- **No PowerShell, no `cmd /c`, no encoded commands, no script files written to disk.** The few external tools (`pnputil`, `mbr2gpt /validate`, `sfc`, `DISM`) are Microsoft-signed and run via `ProcessRunner` with an allowlist of `(exe path under System32, fixed argument template)`, visible windows-off, output captured.
- **No packing or obfuscation**, self-contained but not compressed until measured [T: compare VT detections for compressed vs uncompressed single-file]. Accurate VERSIONINFO (product, company "VTDiag project", description), an app manifest with a clear `description`.
- **Persistence only when needed.** Scheduled tasks exist only between "Restart now" and a successful resume, in a clearly named folder, with an explanatory task description. No Run keys except the documented fallback. No services.
- **No self-copying, no downloading executables** except the signed updater installer (with user consent, signature and hash verified).
- **Signed binaries**, including the separately signed PresentMon (checked [T]). Submit every release to **Microsoft WDSI** and scan with VirusTotal in CI (`vt-scan.yml`, free API quota); publish hashes and the VT link on the release page.
- **Read-only M1 first**: a tool that only reads builds reputation (SmartScreen per file hash and per publisher) before the write-capable releases exist.

---

## 15. Testing strategy

| Layer | What | How |
|---|---|---|
| Rules / ranking | every catalog problem has ≥ 2 fixtures (positive and near-miss); target: correct problem in **top-1 for ≥ 70 %, top-3 for ≥ 90 %** of positives; near-misses must not rank it first | `FixtureGen` synthesizes snapshots from `catalog.json` `diagnostic_checks` and `observed_signals`; real redacted snapshots (opt-in donations) added over time; Verify snapshot tests of the ranking output |
| Signals | each provider against fixture snapshots, including Unknown paths (no admin, collector failure) | xUnit, table-driven |
| Session machine | **property tests**: for random event sequences and a crash injected at every persist point, recovery always reaches a consistent state, never reboots without a `UserClickedRestart` event, resume attempts never exceed 3, and the task set is always empty in terminal phases | FsCheck on the pure `SessionMachine` + in-memory journal |
| Journal | torn writes (truncate at random byte), CRC mismatch, duplicate records, out-of-order seq | fuzzing tests |
| Primitives | apply → verify → undo round-trip **leaves bit-identical state** (capture hash before == after undo); undo `IfUnchanged` detects drift | `VTD.Fixes.IntegrationTests` on GitHub `windows-latest` (admin): sandboxed targets: HKCU `Software\VTDiagTest`, dummy services created by the test (`sc create`), a Microsoft KM-TEST loopback adapter [T: installable on runners], temporary power scheme duplicate |
| Policy | protected targets can't be touched via KB, plan or direct call | unit tests over the deny matrix |
| Reboot / crash chaos | full flows (HVCI enable guide, Vanguard reinstall, fast-startup off) with hard power-off at random points during apply and resume; Fast-Startup shutdown instead of restart; Task Scheduler disabled | **nightly Hyper-V VMs** (Win10 22H2, Win11 24H2/25H2, Turkish-locale image) via PowerShell Direct on a self-hosted box (free); checkpoint restore between runs |
| Monitor | detectors on recorded PresentMon CSVs and event traces (real stutter recordings from Realtek, overlay and Wi-Fi cases) | replay tests; overhead benchmark script (manual, per release) |
| Vanguard compatibility | 3 × 30-min matches + Range benchmark with the monitor on, per release, on 3 real PCs (AMD+NVIDIA, Intel+AMD, laptop hybrid) | manual checklist in the release template; release blocked without sign-off |
| Redaction | golden files containing every PII pattern; asserts nothing survives | Verify tests; also run over all fixtures |
| KB lint | schema validity, every signal and fix id resolves, en strings complete, risk/reversibility consistency, network ops have deadman, high-risk has no ops | `tools/KbBuilder lint` in CI; required check on `kb/` PRs |
| UI | ViewModel unit tests against fake engine RPC; pseudo-loc screenshots; accessibility (keyboard-only pass, Narrator names) | `VTD.App.Tests` + manual |
| Fault injection | `VTD_FAULT=<point>:<action>` env (debug builds only, compiled out of release) to kill or throw at named points | used by chaos and property tests |

---

## 16. Distribution, signing and updates

- **Build**: GitHub Actions (free for OSS). Reproducible `dotnet publish -r win-x64 --self-contained`, SBOM (CycloneDX), SHA-256 list.
- **Signing**: apply to **SignPath Foundation** (free OSS). It requires an existing release, CI builds and a published signing policy, so **M1 ships unsigned** (read-only tool, with hashes and VT links, plus a short "More info → Run anyway" note) and switches to signed as soon as SignPath approves. Azure Artifact Signing isn't available to individuals in Turkey; EV no longer bypasses SmartScreen.
- **Installer**: Inno Setup, per-machine to `Program Files` (admin-protected, which closes the resume-task privilege-escalation hole). Start-menu entries: VTDiag, VTDiag – Undo changes. Uninstaller runs `--uninstall` (§6.4). **[D]** Velopack is not used because it installs per-user.
- **Portable zip** for helpers: Helper mode only. The engine refuses to start elevated from a non-Program-Files path.
- **winget** manifest via `wingetcreate` in CI.
- **App updates**: on start, at most once a day, query the GitHub Releases API. If a newer version exists, show a banner. On consent, download the installer, verify its Authenticode signature and SHA-256 against the release manifest, then run it. **Never during monitoring, a game, or an active session.**
- **KB updates** (no app release needed, for waves like Nahimic or the hook list): `kb-<version>.zip` + `kb.manifest.sig` (Ed25519, public key compiled into the app) on a GitHub Release tag `kb-*`. Checked on scan when online; `minApp` respected; fall back to the bundled KB on any verification failure. KB changes can only add rules, fixes over existing primitives, strings, conflict entries and helper keys, never code.

---

## 17. MVP scope vs later

| Milestone | Contents | Writes to system? |
|---|---|---|
| **M0 Contracts (1 lead, ~3 days)** | `VTD.Contracts`, solution skeleton, Directory.Build.props + banned APIs, JSON schemas, fakes (`VTD.Testing.Fakes`), fixture format, CI skeleton | — |
| **M1 Read-only diagnose (v0.1)** | all collectors for the top-25 problems + Vanguard readiness; ~80 signals; KB with all 90 rules + 113 conflicts (auto-converted, top 25 hand-tuned); ranking + question planner; guides (Secure Boot, TPM, HVCI, BIOS update, ISP/WARP, Nahimic uninstall, overlay exclusions, RTSS); HTML/Markdown/JSON report; redaction; EN+TR; installer | **No** (the "Restart into BIOS" button is the only action) |
| **M2 Fixes (v0.2)** | Journal, recovery, undo center, restore point, ~25 primitives, ~22 fixes: disable unused adapter, Realtek power saving off, multi-NIC metric, conflict service disable, close overlay processes (session), USB selective suspend off, power plan, Fast Startup off + quarantine `392667600.dat`, `vgkbootstatus.dat` quarantine + real restart, vgc Automatic (guarded by the Pre-Check question), Riot Client RUNASADMIN layer, remove bad compat/FSO layers, Exploit-Protection overrides reset (VAN 9002), IFEO reset, test-signing / nointegritychecks off, time resync, firewall allow rules, DNS flush, Bluetooth Hands-Free disable, GPU preference high-performance, HAGS toggle, priority/IFEO PerfOptions reset; reboot-resume; deadman | Yes |
| **M3 Monitor (v0.3)** | PresentMon frames, PDH, event watcher, detectors, tray, outcome comparison, re-ranking | No |
| **M4 Assisted mode (v0.4)** | Helper mode, `.vtdscan`/`.vtdplan`, plan validator, helper key signing + trusted list, "Copy for AI" | Through the normal fix path |
| **M5 Later** | network endpoint ETW + ping in match, Vanguard clean reinstall flow (one-way, reboot-resume), SFC/DISM streamed, clean-boot bisection, 60 s DPC latency test, NVML/ADLX, NVIDIA DRS read, offline report viewer (URL fragment), optional local-LLM explanations, opt-in outcome stats; maybe DDU guided flow with safeboot failsafe |

Explicitly **never**: kernel drivers, CPU temperature via WinRing0/PawnIO, HWID changes or restriction bypass, BIOS flashing, automatic Secure Boot key changes, disabling security features, reading game memory, Riot local client API (unless later opt-in, read-only).

---

## 18. Work breakdown for parallel agents

The lead does **M0** first and freezes `VTD.Contracts` v1. After that, contract changes go through the lead in small PRs. Each agent owns disjoint folders and develops against `VTD.Testing.Fakes` and the shared fixtures, so nobody waits on anybody.

| Agent | Owns (exclusive write) | Implements | Consumes | Done when |
|---|---|---|---|---|
| **Lead** | `VTD.Contracts/`, `Directory.*.props`, `banned/`, `kb/schema/`, `VTD.Testing.Fakes/`, `.github/` | interfaces, DTOs, schemas, fakes, CI | — | all projects compile against fakes |
| **A1 Collectors** | `VTD.Platform/`, `VTD.Collectors/`, `tests/fixtures/snapshots/real/` | `ICollector` ×13, `IScanService`, read-side interop, `IFactProbe` for live guards | Contracts | a full scan in < 60 s on 3 PCs, every collector has a timeout plus a NoAdmin path, snapshot validates against the schema |
| **A2 Knowledge & Analysis** | `VTD.Knowledge/`, `VTD.Analysis/`, `kb/` (except schema), `tools/KbBuilder`, `tools/FixtureGen`, `tests/VTD.Analysis.Tests`, `tests/VTD.Kb.Tests` | KB loader + signature, ~80 signal providers + the generic conflict provider, RuleEvaluator, Ranker, QuestionPlanner, all rules/fixes/guides JSON + en/tr strings | Contracts, fixture snapshots | top-3 ≥ 90 % on fixtures, KB lint green |
| **A3 Fixes & Journal** | `VTD.Fixes/`, `VTD.Journal/`, `tests/VTD.Fixes.IntegrationTests`, `tests/VTD.Journal.Tests` | `IOperation` ×25 (+ write-side interop), ProtectedTargetPolicy, FixRunner, Journal, AtomicStateStore, Quarantine, RecoveryPlanner | Contracts, `IFactProbe` (fake until A1) | round-trip bit-identical undo for every primitive; recovery tests green |
| **A4 Session & Engine** | `VTD.Session/`, `VTD.Engine/`, `installer/`, `tests/VTD.Session.Tests`, `tests/VTD.Chaos/` | SessionMachine (pure), SessionController, RebootCoordinator (tasks, boot ID, restart), RestorePointService, RPC server, DI host, CLI verbs, Inno script | Contracts, fakes of A1/A2/A3/A5 | property tests green; VM chaos run passes |
| **A5 Monitor** | `VTD.Monitor.Core/`, `VTD.Monitor/`, `third_party/PresentMon/`, `tests/VTD.Monitor.Tests` | IFrameSource (PresentMon), PDH sampler, EventWatcher, Pinger, detectors, SelfBudget, MonitorSummary | Contracts | replay tests green; overhead benchmark within budget |
| **A6 UI & Reporting** | `VTD.App/`, `VTD.Reporting/`, `docs/` (guides pages), `tests/VTD.App.Tests`, `tests/VTD.Reporting.Tests` | all screens, tray, Helper mode + plan builder, i18n resx, Redactor, ReportBuilder (html/md/json), bundle formats | Contracts, fake `IEngineRpc` | full flow clickable on the fake engine in EN/TR; redaction golden tests green |

**Key interfaces (frozen in M0):**

```csharp
namespace VTD.Contracts;

// A1
public interface ICollector
{
    string Id { get; }
    CollectorNeeds Needs { get; }            // None | Admin | Network
    TimeSpan Timeout { get; }
    Task<CollectorOutput> CollectAsync(CollectContext ctx, CancellationToken ct);   // returns a partial section patch
}
public interface IScanService { Task<ScanSnapshot> ScanAsync(ScanOptions o, IProgress<ScanProgress> progress, CancellationToken ct); }
public interface IFactProbe { Task<SignalResult> ProbeAsync(string signalOrCheckId, IReadOnlyDictionary<string,string> args, CancellationToken ct); }

// A2
public interface IKnowledgeBase
{
    KbVersion Version { get; }
    IReadOnlyList<RuleDef> Rules { get; } IReadOnlyList<ConflictDef> Conflicts { get; }
    FixDef GetFix(string id); GuideDef GetGuide(string id); ErrorCodeDef? FindCode(string userText);
    string Text(string key, string lang, IReadOnlyDictionary<string, object>? args = null);
}
public interface IAnalyzer
{
    AnalysisResult Analyze(ScanSnapshot snapshot, IntakeAnswers intake, MonitorSummary? monitor, IReadOnlyList<FixAttempt> history);
}
public sealed record AnalysisResult(Ulid AnalysisId, IReadOnlyList<RankedHypothesis> Ranked, HealthChecklist Health,
                                    QuestionDef? NextQuestion, IReadOnlyList<FixProposal> Proposals, bool UnknownCauseLeading);

// A3
public interface IFixRunner
{
    Task<FixPreview>   PreviewAsync(string fixId, FixBinding binding, CancellationToken ct);
    Task<FixRunResult> ApplyAsync(ConsentToken consent, IProgress<FixProgress> progress, CancellationToken ct);
    Task<VerifyResult> VerifyAsync(Guid attemptId, CancellationToken ct);
    Task<UndoResult>   UndoAsync(Guid attemptId, UndoMode mode, CancellationToken ct);
    Task<IReadOnlyList<DriftFinding>> CheckDriftAsync(CancellationToken ct);
}
public interface IJournal
{
    ValueTask AppendAsync(JournalRecord record, CancellationToken ct);      // fsync before return
    IAsyncEnumerable<JournalRecord> ReadAsync(Ulid sessionId, CancellationToken ct);
}
public interface IRecovery { Task<RecoveryReport> RunAsync(CancellationToken ct); }
public interface IStateStore<T> where T : class { T? Load(); void Save(T state); }   // atomic replace + .bak

// A4
public interface ISessionController { SessionState Current { get; } Task<SessionState> HandleAsync(SessionEvent e, CancellationToken ct); }
public interface IRebootCoordinator
{
    BootIdentity CurrentBoot();
    Task<bool> RegisterResumeAsync(Ulid sessionId, DateTimeOffset expires, CancellationToken ct);  // returns readback success
    Task UnregisterResumeAsync(CancellationToken ct);
    Task RestartAsync(RestartToken token, RestartKind kind);   // only entry to InitiateShutdownW
}

// A5
public interface IMonitorService
{
    Task StartAsync(MonitorOptions options, CancellationToken ct);
    IAsyncEnumerable<MonitorTick> TicksAsync(CancellationToken ct);   // 1 Hz aggregates for UI/tray
    Task<MonitorSummary> StopAsync(CancellationToken ct);
}

// A6
public interface IRedactor { Redacted<T> Redact<T>(T model, RedactionPolicy policy); }
public interface IReportBuilder { ReportBundle Build(ReportModel model, ReportOptions options); }   // html + md + json
public interface IPlanValidator { PlanValidation Validate(FixPlan plan, ScanSnapshot freshSnapshot, IKnowledgeBase kb); }

// IPC (Engine ⇄ App), StreamJsonRpc
public interface IEngineRpc
{
    Task<EngineHello> HelloAsync(ClientHello hello);                   // version check, elevated?, recovery report
    Task<SessionState> GetSessionAsync();
    Task<SessionState> SendAsync(SessionEvent e);                      // all user actions are events
    Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding);
    Task<ExportResult> ExportAsync(ExportRequest request);             // report / vtdscan
    Task<PlanValidation> ImportPlanAsync(string planJson);
    event EventHandler<EngineNotification> Notification;               // progress, phase changes, monitor ticks, drift
}
```

**Integration order:** M0 → (A1 ∥ A2 ∥ A3 ∥ A5 ∥ A6 on fakes, A4 on fakes) → week-2 integration: A1+A2 (real scan → ranking on 3 PCs) → A3+A4 (apply/undo/resume in VM) → A6 on the real engine → A5 plugged into the session. File-level rule: **no two agents write the same folder**. Shared needs go to the lead as a Contracts PR (≤ 1 per day per agent, batched). Builds, installers and commits are sequential (the lead merges).

---

## 19. Open items to test before relying on them

1. The `BootId` registry value increments on every real boot and not on Fast-Startup resume; confirm against `LastBootUpTime` behaviour on Win10/11.
2. PresentMon + kernel-network ETW sampling over a 30-min ranked match: no VAN, within the overhead budget.
3. EcoQoS on the monitor actually keeps it off P-cores on Intel hybrid CPUs, and has no effect on AMD.
4. `shutdown /r /fw` equivalent works on the main OEMs (ASUS, MSI, Gigabyte, ASRock, Lenovo, HP, Dell, Acer).
5. A programmatic signal for Vanguard Pre-Check/On-Demand (until found: ask the user before setting vgc to Automatic).
6. The KM-TEST loopback adapter is installable on GitHub Windows runners for device-disable tests.
7. VirusTotal detection difference: compressed vs uncompressed single-file publish.
8. Whether PresentMon release binaries are Authenticode-signed by Intel; if not, sign our redistributed copy under the SignPath policy (MIT allows it).
9. Riot status JSON endpoint stability (otherwise drop it and rely on "Riot-side incident" questions).
