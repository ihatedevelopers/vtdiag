# Design A: VALORANT diagnostic & fix tool ("VTDiag")

Architect A. Emphasis: **diagnostic accuracy** and a **knowledge base (KB) that mods and the community can extend without recompiling**.
Inputs read as data: `PROBLEM_CATALOG.md`, `data/catalog.json` (90 problems, 732 threads), `research/conflicts.json` (113 software signatures), `research/error_codes.json` (57 codes, Vanguard requirements, log locations, tool_design_notes), `research/tech_stack.md`, `research/long_tail.md`, `CLAUDE.md`.
Markers: **[T]** = has to be checked on a real machine with Vanguard running before shipping.

---

## 0. Decisions at a glance

| Area | Decision |
|---|---|
| Stack | C# / .NET 10 LTS, WPF + WPF-UI (Fluent), CommunityToolkit.Mvvm, System.Text.Json (source-gen), TraceEvent (ETW), bundled PresentMon console exe (MIT), YamlDotNet for KB authoring. No kernel driver. |
| Process model | **One signed binary, two modes.** The UI runs **unelevated** as the player. The **elevated helper** (`VTDiag.exe --helper`, started with UAC) owns probes that need admin, every fix, the journal and the live monitor. They talk over an ACL'd named pipe (JSON-RPC). The resume path is a one-shot **Task Scheduler task at logon, RunLevel=Highest** for the helper, plus HKCU RunOnce for the UI, so there is no UAC prompt after the reboot. |
| Core idea for accuracy | Four layers: **Facts → Signals → Hypotheses → Fixes**. Probes (C#) produce typed, **three-valued** facts (true, false or unknown; unknown is never treated as false). Everything above the facts is **data** in signed KB packs. |
| KB format | YAML packs (rules, fixes, software signatures, codes, questions, monitor signals, tables, i18n). CI compiles and signs them into a JSON bundle. The rule DSL **VEX** is a tiny, total, side-effect-free expression language that is type-checked against the fact catalog when a pack loads. |
| Extensibility without recompiling | Packs may declare extra **read-only collectors** (registry, event log, WMI class, file version, service, driver). Fixes can only be built from a **whitelisted set of C# action primitives**, each with backup, apply, verify and undo. Safety floors and forbidden targets are enforced in C#, so a pack can never lower them. |
| Ranking | Independent Bayesian log-odds per hypothesis. Priors come from thread counts, conditioned on the complaint, corrected for solved-thread bias, with an explicit "unknown cause" share. Evidence uses likelihood ratios with **correlation groups**, hard gates, user error codes and questionnaire answers. A failed fix counts as **negative evidence**. Calibration (temperature) is fitted in CI on real fixtures. Fixes are ordered by **expected benefit / cost**, which is a separate ranking from the diagnosis. |
| Live monitor | Runs in the helper. PresentMon for frametimes and PresentMode, PDH counters, event-log watchers, ETW Kernel-Network to find the game's UDP endpoint, plus a low-rate ICMP probe. **Stutter periodicity** and **time-correlation** with NIC resets, DPC, throttling and USB events become `mon.*` facts that feed straight back into ranking. It never opens a handle to the game and never shows UI while the game is in the foreground. |
| Feedback loop | No telemetry. The tool writes a redacted, human-readable **outcome card** that the player chooses to paste into their Reddit thread or a GitHub issue. Mods harvest the cards with the existing Arctic Shift pipeline (`collect.py`), and `kbtool` turns them into success rates and likelihood ratios through a reviewed PR. Real scans that a mod labels become **regression fixtures**, and CI fails a pack change that lowers accuracy. |
| Report | One snapshot JSON (redacted, **replayable** with newer packs), a self-contained HTML page, and Reddit Markdown (under 10k characters). |
| Distribution | GitHub Releases plus winget. SignPath Foundation (free for OSS) signs the binary. Inno Setup installs to Program Files. A portable zip copies the helper to a protected folder on first elevation. KB packs update separately and are **signed with ECDSA P-256** (built into .NET). |

---

## 1. Principles that drive accuracy

1. **Unknown is not false.** A probe that fails, is denied or isn't supported yields `unknown`. Evidence over an unknown fact changes nothing, and the report shows "could not check X". Most wrong diagnoses in remote-fix tools come from treating a failed check as "not present".
2. **Locale-independent facts.** The test machine runs Windows in Turkish (`tool_design_notes`). Probes use APIs, InstanceIds, numeric WMI values and event **EventData fields**. They never parse localized `netsh`, `systeminfo` or rendered event message text.
3. **Explainable by construction.** Every posterior carries the evidence items that moved it and the gates that ruled hypotheses out. The UI and the report show "why", and mods can argue with the rule rather than with a black box.
4. **The snapshot is data and can be replayed.** A scan is a pure JSON snapshot. Diagnosis is a pure function `Rank(snapshot, kb) → result`, so old reports can be re-diagnosed with new packs and every real case becomes a test.
5. **"Not a PC problem" and "can't comply" are first-class verdicts.** Examples: VAN 152 HWID ban → appeal. Account-specific VAL 43/57/59 → test with an alt account. HVCI/IOMMU unsupported on old hardware → say so plainly (long_tail: 146 unsolved vs 24 solved). Server waves → status page.
6. **The long tail has to be visible.** Generic **anomaly detectors** flag oddities that no rule explains (an unknown faulting module, a non-Microsoft driver blocked by CodeIntegrity, a service crashing in a loop). The report surfaces them so mods can write new rules.

---

## 2. Tech stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10 LTS (support until Nov 2028) | .NET 8 and 9 reach end of support on 2026-11-10 (tech_stack.md). |
| UI | WPF + WPF-UI (MIT), MVVM via CommunityToolkit.Mvvm | Shortest route to the Windows APIs, and the largest pool of C# contributors. Not trimmable (accepted). |
| System access | `Microsoft.Win32.Registry`, `System.Management` (WMI/CIM), `System.Diagnostics.Eventing.Reader`, `ServiceController` + P/Invoke `ChangeServiceConfig`, CfgMgr32/SetupAPI P/Invoke, NetAdapter CIM classes (`root/StandardCimv2:MSFT_NetAdapter*`), Task Scheduler COM, `INetFwPolicy2`, WinVerifyTrust | Native APIs instead of hidden PowerShell, which keeps AV heuristics quiet and avoids locale issues. |
| ETW | Microsoft.Diagnostics.Tracing.TraceEvent (MIT) | Kernel-Network UDP, DPC/ISR test, process start events. |
| Frametimes | PresentMon console (MIT), run as a child process | ETW only, no process access. |
| KB | YamlDotNet (authoring and dev packs), JSON bundle at runtime, JsonSchema.Net for validation | Humans write YAML and the app loads validated JSON. |
| Regex in rules | `RegexOptions.NonBacktracking` + timeout | Protects against ReDoS from community rules. |
| Signing (packs) | ECDSA P-256 via `System.Security.Cryptography` | No extra dependency. |
| Installer | Inno Setup (free) + portable zip | Program Files location (see §17). |
| Tests | xUnit, Verify (snapshot tests), FsCheck (properties for the DSL and redaction) | |
| CLI | `vtd` (console app sharing the Core libraries) | Mods and CI: lint, test, replay, priors, harvest. |

Rejected: Tauri/Electron (the system layer would be a second language), Python (AV flags, slow), a Windows service (session 0, larger trust footprint), and any kernel driver (Vanguard).

---

## 3. Process model

```
                  ┌──────────────────────────── user session (player, unelevated) ───────────────────────────┐
                  │  VTDiag.exe (UI mode, asInvoker)                                                          │
                  │   - WPF shell, questionnaire, ranking view, consent dialogs, report export, browser links │
                  │   - Limited mode: runs non-admin probes itself if UAC is declined                         │
                  │   - KB loader + Engine (pure, in-proc)                                                    │
                  └───────────────▲──────────────────────────────────────────────────────────────────────────┘
                                  │ named pipe \\.\pipe\vtdiag-<sessionId>
                                  │ ACL: player SID + Administrators; per-session 256-bit token; JSON-RPC 2.0
                  ┌───────────────┴──────────────── elevated (same or other admin user) ─────────────────────┐
                  │  VTDiag.exe --helper  (always run from %ProgramFiles%\VTDiag\<ver>\)                       │
                  │   - Probe runner (admin probes, HKU\<playerSID>, player profile paths)                     │
                  │   - Action executor (only place that changes the system) + write-ahead journal            │
                  │   - Session store (%ProgramData%\VTDiag, ACL Admins+SYSTEM write)                          │
                  │   - Live monitor (PresentMon child, PDH, ETW, event watchers)                              │
                  │   - Resume scheduler (Task Scheduler COM)                                                  │
                  └──────────────────────────────────────────────────────────────────────────────────────────┘
   After a reboot:   \VTDiag\Resume-<sid8>  (logon trigger, player SID, Highest, EndBoundary +3 d) → helper --resume <id>
                     HKCU\...\RunOnce\VTDiag → VTDiag.exe --resume <id>  (UI; connects to the waiting helper)
```

Why split it instead of running the whole app elevated (the tech_stack.md default):
- **Correct user context.** If a standard user types an admin password at the UAC prompt, a fully elevated app runs as the *admin* user. HKCU, `%LOCALAPPDATA%\VALORANT\Saved\Logs` and the per-user GPU preferences would then belong to the wrong user, and the diagnosis would be wrong. The helper always takes the player's SID from the UI and reads `HKU\<SID>` and the profile path from `ProfileList`.
- Links, file dialogs and drag-and-drop work normally (an elevated browser is bad practice).
- **Limited mode** stays useful when UAC is declined. Many checks work without admin (`tool_design_notes`).
- The helper is the **single executor**, so there is one journal, one place to audit and one crash-recovery path.

Dev and test builds use `InProcTransport` (the same interfaces, no pipe), so engine and UI work never needs elevation.

Helper lifetime: it exits when the UI disconnects for more than 60 s, unless the monitor is armed for a match or a resume is pending. It also exits when the session ends. On start it runs PresentMon with `--terminate_existing_session --session_name VTDiag_<pid>`.

Pipe security: the pipe is created by the helper with a DACL for the player SID only (plus SYSTEM and Administrators). The client must present the token that the helper wrote to `%ProgramData%\VTDiag\sessions\<id>\token` (ACL: player SID read). The helper also checks the client's image path with `GetNamedPipeClientProcessId` → `QueryFullProcessImageName` and confirms its Authenticode signer matches its own.

---

## 4. Solution and folder layout

```
VTDiag.sln
├─ src/
│  ├─ VTDiag.Contracts/          # DTOs + interfaces only. No dependencies. Owned by the lead; frozen at milestone 0
│  │   Facts/ (FactValue, FactPath, FactCatalog attributes)  Snapshot/  Kb/ (model records)  Actions/  Session/  Ipc/  Monitor/
│  ├─ VTDiag.Platform/           # ISystem facade + Windows implementations (the only P/Invoke code)
│  │   Registry/ Wmi/ Services/ Pnp/ NetAdapters/ EventLogs/ Power/ Bcd/ Tasks/ Firewall/ Defender/ Files/ Processes/ Trust/
│  ├─ VTDiag.Platform.Fakes/     # In-memory ISystem built from a "raw capture" JSON (for tests and replay)
│  ├─ VTDiag.Probes/             # Built-in probes + generic collectors (registry/eventlog/wmi/file/service/driver)
│  ├─ VTDiag.Kb/                 # Pack loader, JSON-Schema validation, signature check, VEX parser/type-checker/evaluator
│  ├─ VTDiag.Engine/             # Signal evaluation, scoring, calibration, question planner, fix planner, anomaly detectors
│  ├─ VTDiag.Actions/            # Fix primitives (backup/apply/verify/undo), SafetyPolicy, Journal, restore point
│  ├─ VTDiag.Session/            # State machine, SessionStore, ResumeScheduler, crash recovery
│  ├─ VTDiag.Monitor/            # PresentMon runner, PDH sampler, ETW network, event watchers, signal derivation
│  ├─ VTDiag.Privacy/            # Redactor, sensitive-field tagging, safe log extractors (ShooterGame.log, crash XML)
│  ├─ VTDiag.Report/             # JSON/HTML/Markdown renderers, outcome card codec
│  ├─ VTDiag.Ipc/                # Named-pipe JSON-RPC transport, InProcTransport
│  ├─ VTDiag.HelperHost/         # Helper composition root (library)
│  ├─ VTDiag.App/                # WPF exe (entry point: UI mode or --helper → HelperHost)
│  └─ VTDiag.Cli/                # `vtd` console: scan, replay, explain, kb lint|test|build|sign|priors|harvest, fixture add
├─ kb/
│  ├─ packs/core/
│  │   pack.yaml  facts.ext.yaml  defs.yaml
│  │   hypotheses/<category>/<id>.yaml   checks/*.yaml   routes/*.yaml
│  │   fixes/<id>.yaml   software/<kind>/<id>.yaml   codes/van.yaml codes/val.yaml
│  │   questions.yaml   monitor/*.yaml   tables/*.yaml   i18n/en.yaml i18n/tr.yaml
│  ├─ packs/community/          # same shape; reviewed by mods, signed by CI
│  ├─ fixtures/real/<case-id>/  snapshot.json  label.yaml        # from submitted reports
│  ├─ fixtures/synthetic/<id>/  snapshot.json  label.yaml        # generated from catalog signals
│  ├─ stats/priors.json  stats/outcomes.json  stats/calibration.json   # generated, reviewed in PRs
│  └─ schema/  pack.schema.json hypothesis.schema.json fix.schema.json ... facts.catalog.json (generated from C#)
├─ tools/import/                # one-time: catalog.json / conflicts.json / error_codes.json → YAML (Python, reuses the repo's scripts)
├─ third_party/presentmon/      # pinned exe + LICENSE + SHA256
└─ tests/  <Project>.Tests/ ...  tests/Accuracy.Tests/ (runs every fixture against the KB)
```

Dependency direction: `Contracts ← Platform ← Probes`, `Contracts ← Kb ← Engine`, `Contracts ← Actions ← Session`, `Contracts ← Monitor`, `Contracts ← Privacy ← Report`. `HelperHost`, `App` and `Cli` are the only composition roots. Engine and Kb are **pure** (no `ISystem`), which is what makes replay and fixtures possible.

---

## 5. Fact model and scan snapshot

### 5.1 Facts
A fact is addressed by a dotted **FactPath** (`fw.secure_boot.enabled`, `net.adapters[*].desc`). Each built-in probe declares its paths and types with attributes. `vtd kb facts` turns them into `kb/schema/facts.catalog.json`, which packs are type-checked against.

```csharp
public enum Tri : byte { Unknown = 0, False = 1, True = 2 }

public abstract record FactValue {
    public sealed record Unknown(UnknownReason Reason, string? Detail = null) : FactValue;  // NotCollected|Denied|Failed|Unsupported|Timeout
    public sealed record Bool(bool V) : FactValue;
    public sealed record Num(double V) : FactValue;
    public sealed record Str(string V) : FactValue;
    public sealed record Ver(Version V) : FactValue;
    public sealed record Time(DateTimeOffset V) : FactValue;
    public sealed record List(IReadOnlyList<FactValue> Items) : FactValue;
    public sealed record Obj(IReadOnlyDictionary<string, FactValue> Fields) : FactValue;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class FactAttribute(string path, FactType type) : Attribute {
    public string Path { get; } = path; public FactType Type { get; } = type;
    public Sensitivity Sensitivity { get; init; } = Sensitivity.None;   // None|Identifier|Path|Network|Secret
    public bool RequiresAdmin { get; init; }
}
```

**Sensitivity is part of the schema.** The redactor (§14) works from these tags, so a new probe cannot leak a field by accident: an untagged string field fails the CI lint unless it is marked `Sensitivity.None` explicitly.

### 5.2 Fact namespaces (v1)

| Namespace | Examples | Source |
|---|---|---|
| `os` | `build`, `ubr`, `edition`, `is_n`, `insider_channel`, `install_date`, `modified_windows` (Atlas/Revi/Tiny11 markers), `pending_reboot`, `fast_startup`, `last_boot.type` (cold/hybrid, Kernel-Boot 27) | registry, WMI |
| `hw` | `cpu.vendor/model/family`, `cpu.hybrid`, `ram.total_gb/channels/configured_mts/rated_mts`, `gpus[]` {vendor, pci_id, driver.version/date, is_igpu, enabled, status}, `board.vendor/product`, `is_laptop`, `on_battery` | WMI, SetupAPI |
| `fw` | `mode` (uefi/legacy), `secure_boot.enabled`, `secure_boot.setup_mode`, `secure_boot.db_ca2023`, `bios.version/release_date`, `virtualization.enabled`, `iommu.kernel_dma_protection` | registry, `Get-SecureBootUEFI`-equivalent API (`GetFirmwareEnvironmentVariableEx`), WMI |
| `tpm` | `present`, `spec_version`, `ready`, `kind` (ftpm/dtpm), `instance_id` | SetupAPI (`ACPI\MSFT0101`), Win32_Tpm (admin) |
| `sec` | `hvci.running`, `vbs.status`, `hvci.incompatible_drivers[]`, `testsigning`, `nointegritychecks`, `nx`, `bitlocker.os_protected`, `defender.realtime`, `cfa.enabled`, `smart_app_control`, `exploit_protection.overrides[]` | Win32_DeviceGuard, BCD API, CI logs |
| `disk` | `system.partition_style`, `system.media` (ssd/hdd), `game_drive.same_as_windows`, `free_gb` | Storage WMI |
| `net` | `adapters[]` {id, desc, kind, status, enabled, is_default_route, metric, driver{file,version,date}, adv{eee,green,irq_mod,...}, pm_allow_off, wifi{band, rssi}}, `dns[]`, `ipv6_bound`, `proxy`, `lsp_providers[]`, `wfp_callout_vendors[]` | MSFT_NetAdapter*, IP Helper, WLAN API |
| `svc` | `svc["<name>"]` {exists, state, start_type, delayed, image_path, signer} | SCM |
| `drv` | `loaded[]` {name, path, signer, version}, `blocked[]` (CodeIntegrity 3076/3077 EventData) | EnumDeviceDrivers + WinVerifyTrust, CI log |
| `proc` | `running[]` {name, pid, signer, company} (snapshot only; **no handles opened to Riot/Vanguard processes**) | NtQuerySystemInformation |
| `apps` | `installed[]` {display_name, publisher, version, install_date} | Uninstall keys (HKLM, WOW6432, HKU\<sid>) |
| `sw` | `sw["nahimic"]` {installed, running, components[]} produced from software signatures (§6.6) | derived |
| `vg` | `vgc` {exists,state,start_type}, `vgk` {exists,start}, `precheck_suspected`, `install_path_ok`, `bootstatus_present` | SCM, files |
| `riot` | `client.installed`, `client.running_elevated`, `game.install_drive`, `game.version` (from the log header), `config_corrupt_markers` | files, `ShooterGame.log` safe extractor |
| `ev` | `ev["<queryId>"]` {count, first_utc, last_utc, items[] of projected EventData fields} | pack-declared queries (§6.5) |
| `crash` | `app[]` {exe, module, exception, utc} (Application 1000/1001), `bugchecks[]` {code, driver, utc} (minidump header + WER), `tdr[]` (Display 4101, LiveKernelEvent 141/117) | event log, minidump header only |
| `perf` | `power.scheme_guid`, `power.usb_ss_ac/dc`, `hags`, `mpo_disabled`, `game_mode`, `gpu_pref["<exe>"]`, `fso_flags["<exe>"]` | registry, powrprof |
| `audio` | `endpoints[]` {name_hash, form_factor, bt_hfp, default_role}, `enhancers[]` | MMDevice API |
| `user` | `complaints[]`, `error_codes[]`, `answers["<qid>"]`, `alt_account_works`, `when_started` | questionnaire (§8.4) |
| `mon` | `stutter.rate_per_min`, `stutter.periodic`, `stutter.period_s`, `corr["<sourceId>"]`, `net.rtt_p50/jitter/loss_pct`, `present_mode_mix`, `cpu.max_freq_pct_p5`, … | live monitor (§10) |
| `hist` | `fixes_tried[]` {fixId, outcome, utc} on this PC | local history |

### 5.3 Snapshot schema (`vtd.snapshot/1`)

```jsonc
{
  "schema": "vtd.snapshot/1",
  "id": "snp_01JB7Q…",                    // ULID
  "createdUtc": "2026-10-21T18:02:11Z",
  "app": { "version": "0.4.0", "build": "a1b2c3d" },
  "kb":  [{ "pack": "core", "version": "2026.10.21.2", "sha256": "…" }],
  "context": { "elevated": true, "limitedMode": false, "uiLanguage": "tr-TR", "osLanguage": "tr-TR" },
  "probes": [
    { "id": "probe.firmware", "version": 3, "status": "ok", "ms": 41 },
    { "id": "probe.tpm.wmi", "version": 1, "status": "denied", "detail": "not elevated" }
  ],
  "facts": {                               // tree; leaves are typed FactValue in JSON form
    "fw": { "mode": "uefi", "secure_boot": { "enabled": false, "setup_mode": false, "db_ca2023": true } },
    "disk": { "system": { "partition_style": "GPT", "media": "ssd" } },
    "net": { "adapters": [
      { "id": "PCI\\VEN_10EC&DEV_8125&…", "desc": "Realtek Gaming 2.5GbE Family Controller", "kind": "ethernet",
        "status": "disconnected", "enabled": true, "is_default_route": false,
        "driver": { "file": "rt25cx21x64.sys", "version": "10.68.815.2023", "date": "2023-08-15" } },
      { "id": "PCI\\VEN_8086&DEV_2725&…", "desc": "Intel(R) Wi-Fi 6E AX210 160MHz", "kind": "wifi",
        "status": "up", "enabled": true, "is_default_route": true } ] },
    "tpm": { "spec_version": { "$unknown": "Denied" } }      // explicit unknown, never omitted when a probe ran
  },
  "redaction": { "level": "share", "rulesVersion": 2, "applied": ["user.name", "host.name", "path.profile", "net.mac", "net.ip"] },
  "rawCapture": null                       // only in fixture mode: the raw API results needed by Platform.Fakes
}
```

Facts in the snapshot are already redacted at the "local" level. Secrets are never captured at all (§14). Snapshots are what fixtures, reports and replay all use.

---

## 6. Knowledge base

### 6.1 Pack structure, versioning and trust

```yaml
# kb/packs/core/pack.yaml
id: core
version: 2026.10.21.2            # CalVer: date + build number; ordered
format: 1                        # KB file format major (loader refuses higher)
factCatalog: ">=1.3 <2"          # fact schema range this pack type-checks against
minApp: 0.4.0
depends: []                      # community packs: depends: [core@>=2026.10]
channel: stable                  # stable | beta | local
maintainers: [u/<mod1>, u/<mod2>]
```

- **Trust levels.** `core` and `community` are signed by CI after a reviewed merge, using a key in a protected GitHub environment that needs mod approval. `local` packs (a mod's dev folder, loaded as YAML) are unsigned, show a red "dev pack" banner, and **may define facts, signals, hypotheses and questions, but no fixes**. Only signed packs can propose actions.
- **Stable IDs.** Hypothesis, fix, evidence and question IDs are never reused. Deleting one means `deprecated: {since, replacedBy}`. Every object has a `rev` integer, and outcome cards record `id@rev` so the statistics stay attributable.
- **Pinning per session.** A session records the exact pack versions it started with. A KB update that arrives mid-session, or across a reboot, is only applied to *new* sessions, so a resumed session keeps the same rules.
- **Updating.** The app fetches `kb-index.json` (signed) from GitHub Releases at most once per launch, never while the game runs. The index lists `{pack, version, sha256, url, minApp, revoked[]}`. A bad pack is rolled back by adding it to `revoked` with an older version as `latest`. A pack that fails validation is skipped as a whole (no partial load), and the bundled pack in the binary is the fallback.
- **Time-limited knowledge.** Any object can have `valid: {from, until}` and `wave: {from, to, prior_factor}`, for example the Nahimic Sep–Oct 2026 wave, the Bonjour Oct–Nov 2025 wave, or known-bad driver versions. Expired waves stop boosting priors but stay available as normal hypotheses.

### 6.2 VEX: the rule expression language

Rules need a language that is readable by mods, **cannot do harm** and **always terminates**. VEX is a small expression language (about 1,500 lines of C# including the type checker) with no loops, user functions, recursion or I/O.

```
expr    := or
or      := and ('||' and)*
and     := unary ('&&' unary)*
unary   := '!' unary | cmp
cmp     := add (( '==' | '!=' | '<' | '<=' | '>' | '>=' | '~' | 'in' ) add)?
add     := mul (('+'|'-') mul)*
mul     := atom (('*'|'/') atom)*
atom    := literal | path | call | quant | '(' expr ')'
quant   := ('any'|'all'|'count'|'first'|'where'|'max'|'min'|'avg') '(' expr 'as' IDENT ':' expr ')'
path    := IDENT ('.' IDENT | '[' STRING ']')*
literal := NUMBER | STRING | true | false | null | /regex/flags | 14d | 30s | v"10.0.26100" | 2026-09-01
call    := whitelisted function: known(p) exists(p) len(x) lower(x) ver(x) days_since(t) now()
           code(c) sw(id) table(name) in_wave(id) baseline(path) def(name, args…)
```

Semantics:
- **Kleene three-valued logic.** A missing path or an `$unknown` leaf evaluates to `Unknown`. `Unknown && false = false`, `Unknown || true = true`, and anything else involving Unknown stays Unknown. `any(...)` over an Unknown list is Unknown, and over an empty list it is `false`. `known(p)` tests whether a fact was collected.
- `~` matches a regex (.NET NonBacktracking, 50 ms timeout). Regexes are compiled once when the pack loads.
- Version and date literals compare naturally. `days_since(t)` uses the snapshot time, not the wall clock, so replay is deterministic.
- `code("VAN 9003")` is true when that code is in `user.error_codes` or was found in the logs. Codes are normalized (`van9003`, `VAN-9003`, `VAN: 9003` → `VAN 9003`; `VAN: RESTRICTION: 1`).
- **Type-checked when a pack loads**, against `facts.catalog.json` plus the pack's own collectors. A typo such as `net.adaptors` fails CI with file:line instead of silently evaluating to Unknown.
- `defs.yaml` holds named, parametric, non-recursive macros (cycles are rejected at load time):

```yaml
# kb/packs/core/defs.yaml
realtek_nic(a): a.desc ~ /realtek.*(2\.5\s?gbe|gaming 2\.5|pcie gbe|10gbe|5gbe)/i || a.driver.file ~ /^(rt25cx21|rtcx21|rt640x)/i
active_conn(a): a.status == "up" && a.is_default_route
vg_precheck(): vg.precheck_suspected == true
```

The C# surface for the DSL:

```csharp
public interface IVexCompiler {
    VexProgram Compile(string source, VexScope scope, out IReadOnlyList<VexDiagnostic> diagnostics); // scope = fact catalog + defs + binds
}
public sealed class VexProgram {
    public VexType ResultType { get; }
    public VexValue Evaluate(IFactView facts, VexBindings binds);   // pure; never throws on data, returns Unknown instead
    public IReadOnlySet<string> ReferencedPaths { get; }             // used for targeted re-probing and explanations
}
```

### 6.3 Hypothesis schema

```yaml
id: <stable-kebab-id>
rev: <int>
kind: hypothesis | check | route | anomaly   # check = health board item, route = "not fixable here"
title: t:<i18n-key>
summary: t:<i18n-key>
category: fps_performance | crash_freeze_bsod | tpm_secureboot_bios | vanguard | network_ping | ...
complaints: [<complaint ids from questions.yaml>]      # where the prior applies
prior:
  threads: <int>              # solved threads (catalog.json thread_count); priors.json is generated from it
  override: <0..1>?           # rare; must carry a reason
wave: { from: <date>, to: <date>, factor: <num> }?
requires: <vex bool>?         # hard gate: False → posterior 0 and listed under "ruled out because …"; Unknown → no gate
excludes: [<vex bool>]?       # any True → ruled out (e.g. "Pre-Check mode explains vgc Manual")
bind: { <name>: <vex expr> }? # values reused by evidence text and fix parameters
evidence:
  - id: <stable id>
    when: <vex bool>
    for: decisive | strong | moderate | weak | hint | <LR number>     # LR+ applied when True
    against: decisive | strong | moderate | weak | hint | <number>?   # LR- (as 1/x) applied when False
    group: <name>?            # correlated evidence: only the strongest item in a group counts (§8.2)
    only_if_complaint: [..]?  # conditional evidence
    ask: <question id>?       # if `when` is Unknown, the planner may ask this question
    text: t:<key>             # shown in "why", supports {bind.field} placeholders
fixes: [<fix id>]             # candidates; each fix has its own `when`
verdict: t:<key>?             # for route/can't-comply kinds
related: [<hypothesis id>]
sources: { catalog: <id>, threads: [<reddit ids>], urls: [..], confidence: high|medium|low }
```

Strength scale (core table, learnable): `decisive=50, strong=10, moderate=4, weak=2, hint=1.3`. Community authors pick words, CI turns them into numbers, and `stats/outcomes.json` can replace the number for a specific `hypothesis#evidence` once there is enough data (§11).

### 6.4 The three worked examples

#### (a) Realtek 2.5GbE stutter (catalog: 76 threads, the #1 solved problem)

```yaml
# kb/packs/core/hypotheses/fps_performance/realtek-2-5gbe-nic-stutter.yaml
id: realtek-2-5gbe-nic-stutter
rev: 4
kind: hypothesis
title: t:realtek.title            # "Realtek 2.5GbE network driver causing periodic stutter"
category: fps_performance
complaints: [stutter, short_freeze, ping_spikes, audio_cuts]
prior: { threads: 76 }
requires: "any(net.adapters as a : realtek_nic(a))"          # no Realtek NIC at all → ruled out
bind:
  nic:   "first(net.adapters as a : realtek_nic(a) && a.enabled)"
  other: "first(net.adapters as a : !realtek_nic(a) && active_conn(a))"
evidence:
  - id: nic_enabled
    when: "nic != null"
    against: strong                      # a Realtek NIC that exists but is disabled makes this unlikely
    group: nic_state
    text: t:realtek.ev.enabled           # "{nic.desc} is enabled"
  - id: nic_enabled_unused
    when: "nic != null && !nic.is_default_route && other != null"
    for: strong
    group: nic_state
    text: t:realtek.ev.unused            # "{nic.desc} is enabled but you are online through {other.desc}"
  - id: nic_resets
    when: "ev['realtek_nic_errors'].count >= 3"
    for: strong
    group: nic_events
    text: t:realtek.ev.resets            # "{ev.realtek_nic_errors.count} Realtek driver errors/resets in 14 days"
  - id: vgm_crash
    when: "any(crash.app as c : c.exe ~ /^vgm\.exe$/i && c.exception == '0xc0000409')"
    for: moderate
    group: nic_events
  - id: old_driver
    when: "nic.driver.date < 2024-01-01"
    for: weak
  - id: amd_new_platform
    when: "hw.cpu.vendor == 'amd' && hw.board.product ~ /(b650|b850|x670|x870|a620)/i"
    for: hint
  - id: periodic_stutter_user
    when: "user.answers['stutter_pattern'] == 'periodic'"
    for: moderate
    group: stutter_shape
    ask: stutter_pattern
  - id: periodic_stutter_measured           # live monitor evidence (§10)
    when: "mon.stutter.periodic && mon.stutter.period_s >= 1.5 && mon.stutter.period_s <= 20"
    for: strong
    group: stutter_shape
  - id: stutter_aligned_with_nic_events
    when: "mon.corr['realtek_nic_errors'].hit_ratio >= 0.5"
    for: decisive
  - id: only_valorant
    when: "user.answers['other_games_ok'] == true"
    for: weak
fixes:
  - fix.realtek.disable-unused-nic        # chosen when nic is not the active connection
  - fix.realtek.power-saving-off          # chosen when nic is the active connection
  - fix.realtek.driver-update-guide
related: [dual-network-adapter-desync, usb-selective-suspend-freeze, third-party-hook-overlay-conflict]
sources: { catalog: realtek-2-5gbe-nic-stutter, threads: [1orgew9, 1osbhay, 1ox2x2m, 1pb93al, 1pbqpqu], confidence: high }
```

```yaml
# kb/packs/core/facts.ext.yaml  (pack-declared collector, no recompile)
collectors:
  - id: realtek_nic_errors
    type: eventlog
    log: System
    providers: [rt25cx21, rtcx21, rt640x64, rt68cx21]
    levels: [critical, error, warning]
    since: 14d
    max: 500
    project: [utc, provider, event_id]     # EventData only; no rendered (localized) message text is stored
```

#### (b) Nahimic black screen / launch hang (29 threads, VAN 81, Sep–Oct 2026 wave)

```yaml
id: nahimic-audio-service-launch-hang
rev: 3
kind: hypothesis
title: t:nahimic.title            # "Nahimic (A-Volute) audio service hooking VALORANT at launch"
category: crash_freeze_bsod
complaints: [black_screen_launch, stuck_loading, crash_on_launch]
prior: { threads: 29 }
wave: { from: 2026-09-01, to: 2026-11-30, factor: 3.0 }
requires: "sw('nahimic').installed"
evidence:
  - id: svc_running
    when: "svc['NahimicService'].state == 'running'"
    for: moderate
    against: moderate                     # installed but stopped/disabled → less likely
    group: presence
  - id: faulting_module
    when: "any(crash.app as c : c.exe ~ /^VALORANT-Win64-Shipping\.exe$/i && c.module ~ /^ProductInfo\.dll/i && days_since(c.utc) <= 30)"
    for: decisive
    text: t:nahimic.ev.module             # "VALORANT crashed in ProductInfo.dll (Nahimic) on {c.utc}"
  - id: van81
    when: "code('VAN 81') || code('VAN -81')"
    for: weak
  - id: oem_laptop
    when: "hw.board.vendor ~ /(lenovo|msi|micro-star|asus|dell|alienware)/i"
    for: hint
  - id: tiny_footprint
    when: "user.answers['launch_symptom'] == 'black_screen_tiny_memory'"
    for: moderate
    ask: launch_symptom
fixes: [fix.nahimic.disable-service, fix.nahimic.uninstall]
related: [mdns-bonjour-ni-val43-loading, third-party-hook-overlay-conflict]
sources: { catalog: nahimic-audio-service-launch-hang, threads: [1wciq6n, 1wdcqnv, 1wdq18p, 1wdqy1s, 1wefn02], confidence: high }
```

#### (c) VAN 9003: Secure Boot not enabled (19 + 32 related threads; 71 mentions overall)

One hypothesis, plus **variants** that pick the right guide. The error code is decisive about *what* is wrong, so the work goes into finding *why*.

```yaml
id: secure-boot-not-detected-van9003
rev: 5
kind: hypothesis
title: t:sb9003.title
category: tpm_secureboot_bios
complaints: [cant_launch, van_error]
prior: { threads: 19 }
evidence:
  - id: code
    when: "code('VAN 9003') || code('VAN: RESTRICTION: 1') || code('VAN: RESTRICTION: 4')"
    for: decisive
    group: code
  - id: sb_off
    when: "fw.secure_boot.enabled == false"
    for: strong
    against: strong                       # SB verified ON → probably a different cause (see secure-boot-policy-wave-2026, bios-update)
  - id: win11
    when: "os.build >= 22000"
    for: weak                             # base requirement on Win11
variants:                                 # first matching variant chooses fixes and explanation; all are shown when Unknown
  - id: legacy_mbr
    when: "fw.mode == 'legacy' && disk.system.partition_style == 'MBR'"
    text: t:sb9003.v.legacy_mbr           # needs MBR→GPT (one-way) then UEFI + Secure Boot
    fixes: [fix.guide.mbr2gpt, fix.guide.bios-uefi-secureboot]
  - id: legacy_gpt
    when: "fw.mode == 'legacy' && disk.system.partition_style == 'GPT'"
    fixes: [fix.guide.bios-disable-csm-enable-sb]
  - id: uefi_setup_mode
    when: "fw.mode == 'uefi' && fw.secure_boot.setup_mode == true"
    text: t:sb9003.v.setup_mode           # "no keys installed: Restore Factory Keys"
    fixes: [fix.guide.bios-restore-factory-keys]
  - id: uefi_sb_off
    when: "fw.mode == 'uefi' && fw.secure_boot.enabled == false"
    fixes: [fix.guide.bios-enable-sb]
  - id: sb_on_still_failing
    when: "fw.secure_boot.enabled == true"
    text: t:sb9003.v.on_but_failing       # route to bios-update / ca2023 / wave hypotheses
    fixes: [fix.guide.bios-update, fix.vanguard.repair-guide]
related: [secure-boot-enable-boot-failure, secure-boot-policy-wave-2026, tpm2-missing-or-unmet, dual-boot-secure-boot-van1]
sources: { catalog: secure-boot-not-detected-van9003, urls: ["https://support.riotgames.com/riot/client/error-van-9003"], confidence: high }
```

The guide fix (`fix.guide.bios-enable-sb`, §7.3) carries a **BitLocker preflight**, a vendor-specific step table, an optional "Restart into BIOS" primitive (`shutdown /r /fw`) with resume armed, and a post-reboot verify of `fw.secure_boot.enabled == true`.

### 6.5 Pack-declared collectors (read-only, no recompile)

| type | parameters | limits |
|---|---|---|
| `registry` | hive (HKLM/HKU-player), key, values[], recurse depth ≤ 2 | read-only. Denylist: SAM, SECURITY, credential and token locations, Riot/Vanguard private keys |
| `eventlog` | log, providers[], ids[], levels[], since ≤ 30 d, max ≤ 1000, project[] (EventData field names) | no rendered messages; fields pass through the redactor |
| `wmi` | namespace + class from an **allowlist**, properties[] | no methods, no WQL text from packs (the C# side builds the query) |
| `file` | path template (`%ProgramFiles%`, `%PlayerLocalAppData%`), what: exists/version/signer/size/mtime | never reads content (except built-in safe extractors) |
| `service`, `driver`, `pnp` | name or regex | metadata only |
| `log_extract` | one of the **built-in** safe extractors + pattern ids from an allowlisted pattern table | line content is never exported, only counts and captured whitelisted groups |

Anything else (a new API) needs a C# probe and a fact catalog minor bump. That's the deliberate line between data and code.

### 6.6 Software signatures (conflicts.json → `software/*.yaml`)

The 113 conflict entries are imported mechanically. Each has a `detect` block (process_names, service_names, driver_names, module_names, uninstall regex, network_adapter regex, audio_endpoint regex, windows_features, registry_keys, file_paths) and is materialized as `sw["<id>"] = {installed, running, components[]}`.
Each entry **also generates a low-prior generic hypothesis** `conflict:<id>`, whose evidence is the detected components plus the entry's `effects` mapped to complaints, and whose fix is taken from `fix.action` (as a guide, or as primitives once a maintainer turns it into steps). When a full hypothesis exists (as for Nahimic), it suppresses the generic one through `supersedes: [conflict:nahimic]`. This is how environment-specific causes (VPN, AV, other anti-cheats, overlays, vendor utilities) get covered from day one.

### 6.7 Codes, questions, tables

```yaml
# codes/van.yaml (from error_codes.json)
- code: "VAN 9003"
  meaning: t:code.van9003.meaning
  category: vanguard_requirement
  points_to: { secure-boot-not-detected-van9003: decisive, secure-boot-policy-wave-2026: moderate }
  official_url: https://support.riotgames.com/riot/client/error-van-9003
- code: "VAN 152"
  route: route.hwid-ban              # never offer fixes (tool_design_notes)
```

```yaml
# tables/gpu_known_bad.yaml — version-specific facts live here, not in code (PROBLEM_CATALOG "Known limitations")
amd_adrenalin_bad: { versions: ["26.5.*", "26.7.1", "26.8.1"], valid: { until: 2027-03-01 } }
```

Questions are described in §8.4.

---

## 7. Fixes

### 7.1 Fix schema

```yaml
id: <fix id>
rev: <int>
title: t:<key>
applies_to: [<hypothesis ids>]
when: <vex bool>                        # applicability (uses binds of the hypothesis)
kind: action | guide | route
risk: low | medium | high               # declared; effective risk = max(declared, max(primitive.RiskFloor))
admin: true|false                       # computed too; declared value is checked against primitives
reboot: none | recommended | required   # computed max over steps; may be raised, never lowered
reversibility: exact | best_effort | one_way   # computed min over steps; one_way needs a second explicit consent
est_minutes: <num>
preflight: [ { check: <vex bool>, block|warn: t:<key> } ]   # e.g. on battery, BitLocker without recovery key, game running
steps:
  - do: <primitive id>
    with: { <param>: <literal | "${bind.path}"> }
    on_fail: abort | continue
verify:
  immediate: <vex bool>?                # evaluated on a targeted re-probe of the referenced paths
  post_reboot: <vex bool>?
  outcome:                              # "did the problem go away?"
    monitor: <vex bool over mon.* and baseline()>?
    ask: <question id>?
success_prior: { success: <n>, trials: <n> }  # seeded from catalog fix counts; replaced by stats/outcomes.json
side_effects: [t:<key>]                 # e.g. "headset mic will stop working in Hands-Free mode"
recheck: { after: windows_update | driver_update | days:<n> }?   # Nahimic/Realtek can come back
```

### 7.2 Action primitives (C#, whitelisted)

```csharp
public interface IFixPrimitive {
    string Id { get; }                               // "netadapter.disable"
    RiskLevel RiskFloor { get; }
    bool RequiresAdmin { get; }
    Reversibility Reversibility { get; }             // Exact | BestEffort | OneWay
    RebootNeed Reboot { get; }
    ParamSchema Params { get; }

    ValueTask<PrimitivePlan> PlanAsync(ParamBag p, ISystem sys, SafetyPolicy policy, CancellationToken ct);  // resolves targets, refuses forbidden ones, builds a human diff
    ValueTask<BackupRecord>  BackupAsync(PrimitivePlan plan, ISystem sys, CancellationToken ct);              // captures "before" (journaled before Apply)
    ValueTask<ApplyResult>   ApplyAsync(PrimitivePlan plan, ISystem sys, CancellationToken ct);               // idempotent
    ValueTask<VerifyResult>  VerifyAsync(PrimitivePlan plan, ISystem sys, CancellationToken ct);              // mechanical check: did the change land?
    ValueTask<UndoResult>    UndoAsync(BackupRecord backup, ISystem sys, CancellationToken ct);               // restores "before"; idempotent
    ValueTask<ObservedState> ObserveAsync(PrimitivePlan plan, BackupRecord? backup, ISystem sys, CancellationToken ct); // crash recovery: AtBefore | AtIntended | Elsewhere
}
```

MVP primitives: `service.set_start`, `service.stop`, `service.start`, `netadapter.disable`, `netadapter.enable`, `netadapter.set_advanced`, `pnp.disable`, `pnp.enable`, `registry.set`, `registry.delete_value` (key-prefix allowlist per primitive instance), `powercfg.set_active`, `powercfg.set_value`, `process.close_graceful` (third-party only; Riot processes only through `riot.close_client`), `riot.close_client` (WM_CLOSE / tray exit, never kill), `folder.quarantine` (move to `%ProgramData%\VTDiag\quarantine\<session>`; never delete), `appcompat.set_layer`, `firewall.add_rule` (group `VTDiag`), `defender.add_exclusion` (Riot paths only, default off), `time.resync`, `bcd.set` (allowlisted: testsigning off, nointegritychecks off, nx OptIn, hypervisorlaunchtype; full `bcdedit /export` backup), `restore_point.create`, `reboot.normal`, `reboot.to_firmware`, `uninstall.run` (one-way), `open.settings` (ms-settings: URIs), `guide` (manual steps, see below).
Later: `dism_sfc` (one-way, long), `vanguard.reinstall` (official multi-step flow with reboot), `exploit_protection.reset`, `ddu.guided`.

### 7.3 Example fixes

```yaml
id: fix.realtek.disable-unused-nic
rev: 3
title: t:fix.realtek_disable.title           # "Disable the unused Realtek network adapter"
applies_to: [realtek-2-5gbe-nic-stutter]
when: "nic != null && !nic.is_default_route && other != null"
kind: action
risk: low
reboot: none
est_minutes: 1
preflight:
  - check: "other.kind == 'wifi' || other.kind == 'ethernet'"
    block: t:fix.realtek_disable.no_other_conn      # never cut the only connection
steps:
  - do: netadapter.disable
    with: { instance_id: "${nic.id}" }
verify:
  immediate: "first(net.adapters as a : a.id == nic.id).enabled == false && any(net.adapters as a : active_conn(a))"
  outcome:
    monitor: "!mon.stutter.periodic && mon.stutter.rate_per_min <= 0.3 * baseline('mon.stutter.rate_per_min')"
    ask: q.outcome.stutter
success_prior: { success: 33, trials: 41 }   # 41 op_confirmed reports across merged records, discounted 20%
side_effects: [t:fix.realtek_disable.side]   # "re-enable it if you switch to a cable later"
recheck: { after: windows_update }
```

```yaml
id: fix.nahimic.disable-service
rev: 2
applies_to: [nahimic-audio-service-launch-hang, conflict:nahimic]
when: "svc['NahimicService'].exists"
kind: action
risk: low
reboot: none
steps:
  - do: service.stop
    with: { name: NahimicService }
    on_fail: continue
  - do: service.set_start
    with: { name: NahimicService, start: disabled }
verify:
  immediate: "svc['NahimicService'].start_type == 'disabled' && svc['NahimicService'].state != 'running'"
  outcome: { ask: q.outcome.launch_ok }
success_prior: { success: 15, trials: 19 }
recheck: { after: driver_update }            # OEM packages reinstall it
```

```yaml
id: fix.guide.bios-enable-sb
rev: 2
applies_to: [secure-boot-not-detected-van9003]
kind: guide
risk: high                                   # firmware; guide only (tool_design_notes)
reboot: required
preflight:
  - check: "sec.bitlocker.os_protected != true"
    warn: t:guide.sb.bitlocker               # "Write down your BitLocker recovery key first (aka.ms/myrecoverykey)"
  - check: "fw.mode == 'uefi'"
    block: t:guide.sb.need_uefi
steps:
  - do: guide
    with:
      steps_key: guide.sb.enable
      vendor_table: bios_sb_paths            # tables/bios_sb_paths.yaml keyed by fw.board.vendor (ASUS/MSI/Gigabyte/ASRock/Lenovo/HP/Dell)
  - do: reboot.to_firmware                   # optional button "Restart into BIOS now"; arms resume
    optional: true
verify:
  post_reboot: "fw.secure_boot.enabled == true && fw.secure_boot.setup_mode == false"
  outcome: { ask: q.outcome.launch_ok }
```

---

## 8. Scoring and ranking

### 8.1 Priors from thread counts (generated by `vtd kb priors`, stored in `stats/priors.json`)

For complaint `c`, `H_c` is the set of hypotheses that list `c`. Thread counts come from the 952 solved threads, which **over-represent easy fixes**: Realtek is easy to solve, so it is over-represented, while HVCI-can't-comply has 146 unsolved vs 24 solved. Correction:

```
area(h)          = the long_tail area keyword group of h (or its category)
bias(area)       = (U_area + S_area) / S_area            # from long_tail.md / long_tail.py counts; capped at 8
w_h              = (n_h + α) · 1                         # α = 0.5 (Dirichlet smoothing)
unknown mass     = Σ_area∈c  (U_area + S_area) − Σ_h∈H_c n_h · bias(area(h))   # share no catalogued cause explains
prior_h | c      = clip( w_h / (Σ_h' w_h' + w_unknown(c)) , 0.002, 0.6 )
```

With several complaints selected, take `max` over them, because a hypothesis only has to explain one. Wave boost: `prior_h *= wave.factor` while `now ∈ [from, to]`, then re-clip. Hypotheses with no threads (imported conflicts, new rules) get `α` only. The **explicit unknown share** (`unknown:<complaint>`) is shown in the UI as "No known cause matched strongly". It is what stops the tool from confidently naming the least-bad candidate.

Example: complaint `stutter`. Among the catalog's fps_performance + short-freeze problems (about 213 + part of 105 threads), Realtek's raw share is about 0.30. After the unknown share for stutter is added, its prior is about **0.18** before any evidence, and it climbs to about 0.9 once `nic_enabled_unused` (×10) and `nic_resets` (×10) fire.

### 8.2 Evidence combination

```
logit(p_h) = logit(prior_h) / T                                 # T = calibration temperature (§8.5)
           + Σ_groups  G(group)
           + Σ_ungrouped  L(e)
           + Σ_failed_fixes  log(1 − s_{f,h})                   # negative evidence from failed fixes (§8.3)

L(e) = ln(LR+)  if when(e) = True
     = −ln(LR−) if when(e) = False and `against` is set
     = 0        if Unknown (or False without `against`)

G(group) = the L(e) with the largest |L(e)| in the group      # correlated evidence is not double-counted
gates:   requires = False → p_h = 0 (shown as ruled out, with the reason)
         any(excludes) = True → p_h = 0
cap:     Σ|L| ≤ ln(10^4) per hypothesis                        # no runaway certainty from many weak items
```

Hypotheses are **multi-label**, with independent binary posteriors, because real PCs often have two problems (for example Realtek plus an overlay). The UI groups the result into buckets: **Very likely ≥ 0.8**, **Likely ≥ 0.5**, **Possible ≥ 0.2**, with the top-3 evidence lines under each.

The health board (`kind: check`) is computed alongside. Its items are `ok | warn | fail | unknown` and include the Vanguard requirement set, pending reboot, disk space, Windows build, a modified Windows image, and so on. A failed check is also a fact (`check["<id>"].status`) that hypotheses can use as evidence.

### 8.3 Choosing the next fix (separate from diagnosis)

```
s_{f,h}  = Beta posterior mean of fix f's success given h  (success_prior + harvested outcomes)
EB(f)    = Σ_h p_h · s_{f,h}                                   # expected chance the fix solves "the" problem
cost(f)  = 1 + 1.5·risk + 1.0·[reboot required] + 0.1·est_minutes + 2.0·[one_way] + 0.5·[admin and limited mode]
score(f) = EB(f) / cost(f),  only for fixes whose `when` and preflight pass
```

The tool proposes **one** fix at a time: the highest score, with the runner-up shown as "or". After a fix is verified mechanically (`immediate`), the **outcome** is measured (monitor in the next match, or a question).
- **It worked**: the session marks h as resolved and asks whether anything else remains.
- **It didn't work**: add `log(1 − s_{f,h})` to every h the fix applies to. A strong fix failing therefore lowers its hypothesis a lot, and a weak one lowers it only a little. Then re-rank. This is the behaviour of a good remote helper ("not Realtek after all, so next…").
- Fixes already tried on this PC (`hist.fixes_tried`) are not proposed again unless the user asks.

### 8.4 Questionnaire and error-code input

`questions.yaml` holds the questions and the complaint picker. Answers are written to `user.*` facts.

```yaml
- id: complaint
  type: multi
  options: [stutter, low_fps, short_freeze, pc_freeze, crash_on_launch, black_screen_launch, stuck_loading, van_error,
            disconnects, ping_spikes, packet_loss, voice_chat, audio_cuts, input_lag, display, cant_install, other]
- id: error_code
  type: code_picker            # searchable list from codes/*.yaml + free text, normalized; multiple allowed
- id: stutter_pattern
  ask_if: "'stutter' in user.complaints || 'short_freeze' in user.complaints"
  options: { periodic: "Every few seconds, like clockwork", random: "Randomly", fights: "Only in fights / with abilities", over_time: "Gets worse the longer I play" }
- id: other_games_ok
  type: bool
- id: alt_account
  ask_if: "code('VAL 43') || code('VAL 57') || code('VAL 59')"
  text: t:q.alt_account        # account-specific failures (long_tail: 33 U / 2 S)
```

**Adaptive planner.** The complaint and error-code questions are always asked first, and **during** the scan (a scan takes 20–60 s). After that, while the scan or monitor runs, the planner picks at most 5 more questions by **expected information gain** over the top-8 hypotheses. For each candidate question q, it simulates each answer using the answer distribution implied by the current posteriors and the evidence LRs that reference `user.answers[q]`, and picks the one that most reduces the binary entropy summed over the top hypotheses. Only questions referenced by some live hypothesis's `ask:` are eligible, so mods add questions in data.

Error codes from the user carry `decisive` or `strong` LRs through `codes/*.yaml` → `points_to`. Codes found in local logs (Riot Client logs, ShooterGame.log patterns) are a separate source in `user.error_codes_detected`, with the same LR. A **route** code (VAN 152) short-circuits ranking to the route verdict.

### 8.5 Calibration and the accuracy gate (CI)

- `tests/Accuracy.Tests` runs the engine over every fixture in `kb/fixtures/real` and `synthetic`. Metrics: **top-1, top-3 hit rate** (label ∈ top-k), **Brier score**, **ECE** (expected calibration error, 10 bins), the **false "Very likely" rate** (posterior ≥ 0.8 but wrong), and route correctness.
- `vtd kb calibrate` fits one temperature `T` per pack (1-D, minimizes log loss on real fixtures; it falls back to 1.0 with fewer than 30 real fixtures) and writes `stats/calibration.json`.
- **Gate.** A pack PR fails if real-fixture top-3 drops by more than 1 percentage point, if the false "Very likely" rate rises at all, or if any fixture tagged `golden` changes its top-1. The CI comment prints a per-fixture diff (what moved and which evidence did it), so reviewers can see the effect of a rule change.

### 8.6 Anomaly detectors (long-tail discovery)

Built-in, data-parameterized `kind: anomaly` rules:
- A faulting module in a VALORANT/Riot/Vanguard crash that is not in any known `module_names`.
- A CodeIntegrity-blocked driver (3076/3077) or an HVCI-incompatible driver that is not in a software signature.
- A non-Microsoft LSP or WFP callout.
- A service that is crash-looping (SCM 7031/7034 more than 3 times in 7 days).
- A PnP device with a problem code.
- An unsigned kernel driver loaded.

They do **not** raise any hypothesis. They appear in the report as "Unexplained findings (help the KB grow)", which feeds the fixture and rule pipeline.

---

## 9. Session state machine, reboot-resume and crash safety

### 9.1 States

```
 New ─► Consent ─► Scanning ═╦═► Questionnaire (concurrent) ─► Ranked ◄──────────────────────────────┐
                             ╚═► (Limited mode if UAC declined)    │                                  │
                                                                   ▼                                  │
                                                            FixProposed ──decline──► (next candidate) ┤
                                                                   │accept (+2nd consent if one_way)  │
                                                                   ▼                                  │
                         ┌──────────────────────── Applying ─────────────────────────┐                │
                         │ Planned → BackedUp → Applied → Verifying ─ok─► Verified    │                │
                         │                │           └──fail──► RollingBack → RolledBack ───────────┤
                         └────────────────┼────────────────────────────────────────────┘              │
                                          ▼ (reboot required)                                         │
                                    AwaitingReboot ─(user reboots; Restart, not Shut down)─► Resuming │
                                          │ user postpones → Paused (resume armed, expires 3 d)  │     │
                                          ▼                                                      ▼     │
                                    PostRebootVerify ─────────────────────────────────────► OutcomeCheck
                                                                                (monitor next match or ask)
                                                                     worked ─► Resolved ─► Report/Done
                                                                     not worked ─► negative evidence ─┘
 Any state ─(crash / power loss)─► Recovering (on next start) ─► the state implied by the journal
```

### 9.2 Persistence

- `%ProgramData%\VTDiag\sessions\<id>\session.json`: `{schema, id, playerSid, createdUtc, state, packPins[], snapshotRef, posteriors, fixQueue[], currentFix{fixId, stepIndex}, bootId, rebootReason, resumeAttempts, expiresUtc}`. It is written to a temp file, flushed, then `File.Replace`d (atomic). ACL: Administrators and SYSTEM can write, the player SID can read.
- `journal.jsonl` is **write-ahead** and append-only. Every primitive step writes `intent` (with the backup) and calls `FlushFileBuffers` **before** `Apply`, then writes `result`. Entry: `{seq, utc, fixId@rev, step, primitive, params, before, intended, status: intent|applied|verified|failed|undone, rebootRequired}`. Large backups (`reg export`, `bcdedit /export`, `powercfg /export`) are stored under `backups\`, referenced by SHA256.
- **Restore point** before the first medium or high-risk *action* of a session. It works around the 24 h throttle with `SystemRestorePointCreationFrequency=0` and then restores the previous value. If System Protection is off, the tool offers to enable it (with consent). It verifies the point exists and otherwise warns and relies on the journal.

### 9.3 Reboot and resume

1. When a fix needs a reboot, `IResumeScheduler.ArmAsync(sessionId)` registers `\VTDiag\Resume-<id8>`: logon trigger for the player SID, `RunLevel=Highest`, action `"%ProgramFiles%\VTDiag\<ver>\VTDiag.exe" --helper --resume <id>`, `EndBoundary = now + 3 d`, "do not start a new instance", and an execution limit of 30 min. It also writes `HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce\VTDiag = "<path>" --resume <id>` through HKU\<sid>.
2. `bootId` (from `Win32_OperatingSystem.LastBootUpTime` plus the Kernel-Boot event 27 boot type) is stored. The UI says: **"Restart (not Shut down). Fast Startup keeps the old kernel."**
3. After logon, the helper starts elevated with no UAC prompt. It checks that `bootId` changed and that this was a real restart (cold boot type). If not, it goes back to `AwaitingReboot` and explains why. It runs `post_reboot` verify through a targeted re-probe of the `ReferencedPaths`, then waits up to 10 min for the UI (from RunOnce) to connect. If the UI never connects, it shows a toast "Click to continue your VALORANT diagnosis" and exits with the state saved.
4. **Disarm**: the task deletes itself on the first successful resume. `resumeAttempts` above 3, or an expired session, also disarms. Uninstall removes the `\VTDiag\` task folder. Every launch runs a "stray task sweep" that deletes tasks with no matching live session.
5. Safe-mode flows (DDU) are not in the MVP. Later they will use a `*`-prefixed HKLM RunOnce and a **failsafe** that always removes `safeboot`.

### 9.4 Crash recovery algorithm

```
on helper start:
  for each session in state ∉ {Done, Expired}:
    last = journal.LastEntry()
    if last.status == intent:                     # crashed between intent and result
        obs = primitive.ObserveAsync(plan, backup)
        AtBefore   → mark "not applied", state = FixProposed
        AtIntended → mark "applied (unverified)", state = Verifying
        Elsewhere  → state = NeedsUser: show before/now/intended, offer Undo-to-before
    if state == Applying with a multi-step fix and some steps applied:
        offer: "continue remaining steps" or "undo applied steps (reverse order)"
```

Every primitive is **idempotent** and `Undo` replays in reverse order. Tests kill the process at every step boundary (§16).

```csharp
public interface ISessionStore {
    ValueTask<Session> CreateAsync(PlayerIdentity player, IReadOnlyList<PackPin> pins, CancellationToken ct);
    ValueTask SaveAsync(Session s, CancellationToken ct);                 // atomic replace
    ValueTask<IReadOnlyList<Session>> ListOpenAsync(CancellationToken ct);
}
public interface IJournal {
    ValueTask<long> WriteIntentAsync(JournalIntent e, CancellationToken ct);   // durable before return
    ValueTask WriteResultAsync(long seq, JournalResult r, CancellationToken ct);
    IAsyncEnumerable<JournalEntry> ReadAsync(SessionId id, CancellationToken ct);
}
public interface IResumeScheduler {
    ValueTask ArmAsync(SessionId id, PlayerIdentity player, TimeSpan expiry, CancellationToken ct);
    ValueTask DisarmAsync(SessionId id, CancellationToken ct);
    ValueTask SweepAsync(IReadOnlySet<SessionId> live, CancellationToken ct);
}
```

---

## 10. Live monitor

### 10.1 Rules
- **Never touch the game.** No `OpenProcess` on Riot or Vanguard processes, no module enumeration, no overlay, no input hooks, no screen capture. Only ETW, PDH, event logs and OS tables.
- **No UI while the game is in the foreground.** It runs from the tray only, and findings are queued until the match ends (or the game loses focus for more than 30 s). It never steals focus.
- Overhead budget under 2 % CPU (1 Hz PDH, streaming CSV parse). This is checked by a self-measurement fact `mon.self_cpu_pct` and reported.
- DPC/ISR latency is a **separate 60 s test** (idle or in the Range), never run during a match.
- The monitor is armed by the user ("watch my next match") or by a fix whose outcome check needs it. It stops automatically when the game exits (PresentMon `--terminate_on_proc_exit`).

### 10.2 Sources and metrics

| Source | Metrics | Notes |
|---|---|---|
| PresentMon (`--process_name VALORANT-Win64-Shipping.exe --output_stdout --no_console_stats --session_name VTDiag_<pid>`) | frame ms, display-change ms, PresentMode mix (Independent Flip vs Composed), AllowsTearing | stutter events and periodicity |
| PDH | `% Processor Utility`, **`% of Maximum Frequency`** (throttle proxy), GPU Engine 3D utilization (game pid), GPU dedicated memory, available MB, disk queue, NIC bytes/s | 1 Hz |
| Event watchers (`EventLogWatcher`) | pack-declared queries live: Realtek NIC errors, WHEA-Logger, Display 4101, Kernel-Processor-Power 37, Kernel-PnP device arrival/removal (USB disconnects), audio endpoint changes, SCM vgc events | timestamps |
| ETW Microsoft-Windows-Kernel-Network | the game pid's UDP remote endpoint (game server) | [T] confirm no VAN error after a 30 min match |
| ICMP probe (1–2/s) to that endpoint, falling back to the first public hop | RTT p50/p95, jitter (RFC 3550), loss % | labelled "indicative" when ICMP is filtered |
| WLAN API | RSSI, band, link-speed changes | |
| ETW Kernel-Process (process start) | hook or overlay tools starting during the match | names only |
| Optional NVML / ADLX (user-mode, from the installed driver) | GPU temperature, clocks, throttle reasons | not bundled |

### 10.3 Derived signals (data: `kb/packs/core/monitor/*.yaml`)

```yaml
- id: mon.stutter
  from: frames
  stutter_event: "frame_ms > max(2.5 * rolling_median(frame_ms, 120), rolling_median(frame_ms, 120) + 12)"
  outputs:
    rate_per_min: rate(events)
    periodic: periodicity(events, min_s: 1.5, max_s: 20, min_events: 8).score >= 0.6   # autocorrelation of inter-event intervals
    period_s: periodicity(events).period
- id: mon.corr
  for_each_source: [realtek_nic_errors, usb_pnp_changes, whea, cpu_freq_dip, ping_spike, dpc_spike, hook_proc_start]
  outputs:
    hit_ratio: cooccur(mon.stutter.events, source.events, window_ms: 1000).ratio          # share of stutters with a source event within ±1 s
    lift: cooccur(...).lift                                                                # compared with a randomly shifted baseline
- id: mon.net
  outputs: { rtt_p50: p50(rtt), jitter_ms: rfc3550(rtt), loss_pct: loss(), spikes_per_min: rate(rtt > p50 + 40) }
- id: mon.throttle
  outputs: { cpu_max_freq_pct_p5: p5(cpu_max_freq_pct, when: gpu3d > 50) }
- id: mon.present
  outputs: { composed_share: share(present_mode == 'Composed*') }
```

The analysis functions (`periodicity`, `cooccur`, `rate`, `p50`, `rfc3550`, …) are a fixed C# library. Their thresholds and wiring are data.

### 10.4 Feedback into hypotheses
- At match end (and every 60 s for a "live" view), the monitor writes `mon.*` facts into a **new snapshot revision** (`snapshotRef` gets `rev+1`, and the old revision is kept for the before/after comparison). The engine re-ranks, and the UI shows the movement: "Live evidence: stutter every 4.1 s, 78 % aligned with Realtek driver resets → Realtek 41 % → 96 %."
- `baseline('<path>')` refers to the same metric from the session's **first monitored match**. Fix `outcome.monitor` expressions compare against it, so "it worked" means measured improvement and not just "the registry value changed".
- With too little data (a match under 5 min, or fewer than 2,000 frames), the facts are `Unknown(InsufficientData)` and do not act as evidence.

---

## 11. Telemetry-free feedback loop

1. **Outcome card.** When a fix outcome is known, the tool shows a short card the player can copy. Nothing is ever sent automatically.

```
VTD1 outcome · core@2026.10.21.2 · realtek-2-5gbe-nic-stutter@4 p=0.93
fix=fix.realtek.disable-unused-nic@3 result=worked verified=monitor
ev=nic_enabled_unused,nic_resets,periodic_stutter_measured · os=26200 cpu=amd gpu=amd · n=K7Q2
```
   It contains no identifiers. `n` is a random nonce per card, used only for de-duplication. It is offered as (a) a text block to paste into the player's own Reddit thread, which is also the natural "Solved" comment and helps the 2,180 threads that never got a reply, and (b) "Open a GitHub issue": a prefilled URL whose body contains only the card. The user clicks it themselves.
2. **Harvest.** `vtd kb harvest --from data/comments.jsonl` (the repo's existing Arctic Shift pipeline `collect.py`/`prep.py`) and the GitHub issues API parse the `VTD1` cards. It de-duplicates by nonce, drops cards for unknown or revoked revs, and updates `stats/outcomes.json` with per `fix@rev × hypothesis@rev` Beta counts and per `hypothesis#evidence` co-occurrence counts.
3. **Learning, damped and reviewed.**
   - Success rate: `Beta(success_prior + harvested)`.
   - Evidence LR refit: once an evidence item has at least 30 labelled outcomes, `LR+ ≈ P(e | h confirmed) / P(e | h refuted or other h confirmed)` with add-1 smoothing, shrunk toward the authored strength (`λ = n/(n+50)`).
   - Each card has weight ≤ 1. Spikes (more than 20 cards per day for one rev) are flagged for a mod.
   - `harvest` opens a PR that a mod must approve, and the accuracy gate (§8.5) runs on it.
4. **Fixtures from real scans.** In the report screen, "Help improve the tool" saves `snapshot.share.json` (redacted at the share level, preview shown) for the player to attach to a GitHub issue or send to a mod. A mod runs `vtd fixture add <file> --label realtek-2-5gbe-nic-stutter --fix fix.realtek.disable-unused-nic --source reddit:1xyz` → `kb/fixtures/real/<case>/`. Optionally `--minimize` removes facts that no rule references, using `ReferencedPaths`. Mods can also run `vtd replay <report.json>` on any submitted report to re-diagnose it with the newest packs. That's how a mod checks a new rule against old unsolved cases.
5. **Local memory.** `%LocalAppData%\VTDiag\history.jsonl` stores the fixes tried on this PC (`hist.*` facts). It never leaves the PC.

---

## 12. Report

| Artifact | Contents | Audience |
|---|---|---|
| `report.json` (`vtd.report/1`) | `{meta, packs[], snapshot (share-redacted), health[], hypotheses[{id@rev, p, bucket, evidence[{id, text, dLogit}], ruledOut[{id, reason}]}], fixes[{id@rev, status, before, after, verify, outcome}], monitor{summary, series (downsampled to ≤ 600 points)}, anomalies[], outcomeCard}` | mods, `vtd replay`, fixtures |
| `report.html` | self-contained (inline CSS/SVG charts, no external requests), sections: Summary → Health board → Likely causes with "why" → What we changed (before/after, how to undo) → Match monitor charts → Unexplained findings → Technical appendix (facts table) | player, helpers |
| Reddit Markdown | ≤ 9,500 chars: one-line summary, the top 3 hypotheses with posteriors and the main evidence, health failures, fixes tried and their outcomes, the outcome card, and "full report attached" | r/ValorantTechSupport posts |

- The report language is chosen separately from the UI language, and **defaults to English** so the subreddit can read it while the UI is in Turkish.
- Every report shows: app version, pack versions, redaction level, and the **SHA256** of the JSON (so a mod can tell it hasn't been edited).
- Before export, a **preview screen** shows exactly which fields are included (privacy).

---

## 13. Internationalization

- **UI strings**: `.resx` (en, tr) in `VTDiag.App`, using `IStringLocalizer`. Languages switch at runtime.
- **KB strings**: every user-visible KB text is a key `t:<key>`, defined in `i18n/en.yaml` and `i18n/tr.yaml` of the same pack. Placeholders `{bind.field}` use **ICU MessageFormat-lite** (plural/select) for counts such as "3 resets". English is the fallback per key. CI fails on a missing `en` key and warns on a missing `tr` key, and the release notes list the tr coverage %.
- Community translators edit only the `i18n/*.yaml` files, which are reviewed like any pack change. New languages (FR, PT, ES, VI, ZH from long_tail.md) are added per pack without an app release.
- **Facts are never localized**, and probes do not depend on locale (§1.2). Dates in reports use ISO 8601 and numbers are formatted with the report culture.
- Guide steps that name Windows UI labels show both the English label and the localized one where known (`tr`), because BIOS menus are usually English.

---

## 14. Privacy

| Data | Handling |
|---|---|
| `ShooterGame.log`, crash XML | Read only by **built-in safe extractors**, line by line, matched against an allowlisted pattern table (LogInit OS/CPU/GPU line, D3D adapter list, `LogPlatformInitializerV2 … Failed`, `D3D device being lost`, LogNet disconnect counts). **Any line containing `-remoting-auth-token`, `-subject`, `CommandLine`, `MachineId`, `LoginId`, `UserName`, `puuid`, `Bearer`, `access_token`, or a JWT-shaped string is discarded before matching.** Raw lines are never kept in memory past the loop and never written. |
| Vanguard logs (`C:\Program Files\Riot Vanguard\Logs`) | Never opened (encrypted, `tool_design_notes`). Only their existence and mtime are recorded. |
| Riot local client API / lockfile | Not used. |
| Username, computer name | Replaced with `<user>` / `<pc>` at collection. Profile paths become `%USERPROFILE%\…`. |
| MAC, serials (BIOS, disk, TPM EK), Wi-Fi SSID/BSSID, public IP | Dropped. MAC → OUI vendor only. |
| Local IPs | Reduced to a class (`private-192.168/16`, `cgnat-100.64/10`) because CGNAT/Starlink is diagnostic. |
| Game server IP (monitor) | Kept as region/ASN only in the share report. The full IP stays local. |
| Installed apps / processes | Kept (they're diagnostic). Publisher and name only. Anything with a user path is path-redacted. |
| Audio device names | Hashed, plus form factor and the BT-HFP flag. |

- **Two redaction levels.** `local` is for the on-disk session (no secrets, identifiers kept only where needed for fixes, such as the InstanceId). `share` is for anything exported (all of the above, plus InstanceIds trimmed to VEN/DEV).
- Redaction is driven by the fact schema's `Sensitivity` tags plus a final **regex sweep** over the serialized text (emails, JWT, GUID-like tokens next to "token", Windows SIDs, `C:\Users\<name>`). A sweep hit in a share export **blocks the export** and logs the field path, so the gap gets fixed in the schema instead of being silently patched.
- **Canary tests**: fixtures with planted tokens and usernames in every free-text field. The export must contain none of them (§16).
- Optional AI explanation (later) only ever receives the share-level JSON, shown first, and is opt-in per session.

---

## 15. Safety rules (enforced in C# by `SafetyPolicy`, not by packs)

1. **Vanguard coexistence**: no kernel driver. No injection, hooking or overlay. No `OpenProcess` on `VALORANT-Win64-Shipping.exe`, `RiotClient*`, `vgc.exe`, `vgtray.exe`, or `vgm.exe`. No ETW providers that read game memory. Riot processes are only ever closed gracefully, with consent.
2. **vgc/vgk start types** are never changed, except by the official Vanguard reinstall flow and the official "vgc Automatic" step, and **not when Pre-Check / On-Demand mode is suspected** (`tool_design_notes`).
3. **Guide-only, never automated**: BIOS settings, Secure Boot keys, MBR2GPT, BIOS flashing, storage-controller driver swaps, enabling HVCI while incompatible drivers are present, DDU in the MVP.
4. **Forbidden targets**: the boot/system disk controller, the active display adapter, the only active network connection, input devices (except with an explicit warning and a 15 s auto-revert countdown), Windows Defender core services, Windows Update services, BitLocker, and the SAM/SECURITY hives.
5. **Consent model**: explain → show the diff (before/after) → consent → back up → apply → verify. One fix at a time, never batched. A one-way change needs a second consent with the words "cannot be undone". A medium or high-risk action needs a restore point attempt first.
6. **Preflight**: never apply while the game is running (except monitor-only actions), on battery below 20 %, while a Windows Update install is pending, or while another VTDiag session is in `Applying`.
7. **Lowering security** (Defender exclusion, disabling CFA/Smart App Control) is off by default, limited to Riot paths, shown with a warning, and added to the report's "security changes" list.
8. **Packs can't escalate**: the effective risk, admin, reboot and reversibility of a fix are computed from its primitives. A fix declared lower than that is rejected at load. Registry primitives only write under per-primitive key allowlists in C#.
9. **No downloads without consent**: show the file name, source, size and signature, and verify the signature before running anything.
10. **HWID/ban codes** (VAN 152, VAL 61): no fixes, no HWID tampering advice. Show Riot's explanation and the appeal link only.

---

## 16. Testing strategy

| Layer | How |
|---|---|
| **Platform fakes** | `VTDiag.Platform.Fakes` implements every `ISystem` sub-interface from a **raw capture** JSON (registry subtree, WMI result sets, SCM state, event records with EventData, adapter list, firmware vars). `vtd scan --capture` on a real PC records the raw capture (redacted), so probes are tested against real-world shapes, including Turkish-locale machines. |
| Probes | For each probe: capture in → expected facts out (Verify snapshot tests). Denied/failed paths must yield `Unknown(reason)`, never defaults. |
| VEX | Parser golden tests. Type-checker diagnostics. FsCheck properties: evaluation is total (never throws), Kleene laws hold, regexes time out safely, and `ReferencedPaths` is complete. |
| KB lint (`vtd kb lint`) | Schema, unique IDs, i18n keys, unreachable fixes, every evidence item references a known path, every hypothesis has at least one fixture or is marked `untested`, and no fix lowers risk. |
| **Accuracy** | `tests/Accuracy.Tests` over `kb/fixtures/real` and `synthetic`, with the metrics and gates from §8.5. The synthetic set is generated from the catalog's `observed_signals`/`diagnostic_checks` for all 90 problems (one positive and one "look-alike" negative each), so a rule that fires on everything is caught. |
| Primitives | Against fakes: `Plan → Backup → Apply → Verify → Undo` must restore the fake system **byte-equal** to the before state. Idempotency (apply twice, undo twice). SafetyPolicy refusals (forbidden targets). |
| Journal / crash | A deterministic fault injector kills at every journal boundary of every MVP fix. Recovery must reach a consistent state (AtBefore or AtIntended or NeedsUser, never silent drift). |
| Session/resume | State machine model tests. Resume with a fake clock and a fake boot ID (no reboot, a hybrid boot, an expired session, 4 attempts). |
| Monitor | Recorded PresentMon CSVs and event streams (real matches and synthetic periodic stutters) → expected `mon.*` facts. Overhead benchmark. |
| Privacy | Canary fixtures (§14), and a fuzz test that injects secrets into every string fact. The share export must stay clean. |
| Manual / real hardware | A release checklist on at least 3 real PCs with Vanguard (AMD+Realtek, an Intel laptop with Nahimic, Win10 22H2): a 30-min ranked match with the monitor on and no VAN error, then reboot-resume, undo of every MVP fix, and a Turkish-UI run. VMs cannot run Vanguard, so this can't be automated. |

---

## 17. Distribution, signing, updates

- **Build**: GitHub Actions on `windows-latest`. Reproducible publish (`-p:Deterministic=true`), `win-x64` self-contained, **not** single-file-compressed (it reduces AV heuristics; [T] measure the size). One folder: `VTDiag.exe` (UI + helper modes), `presentmon\PresentMon.exe`, `kb\core.bundle.json`. Plus the `vtd` CLI as a separate artifact.
- **Code signing**: **SignPath Foundation** (free for OSI-licensed projects built in CI). The policy page goes on the homepage. Signed artifacts: exe, installer and CLI. No packing or obfuscation. Each release is submitted to Microsoft WDSI and VirusTotal from CI, with hashes published.
- **Installer**: Inno Setup → `%ProgramFiles%\VTDiag\<ver>\`. Start menu entry. The uninstaller removes `\VTDiag\` scheduled tasks, RunOnce values and `%ProgramData%\VTDiag` (it asks first if there are un-undone fixes, and offers "undo all").
- **Portable zip**: on its first elevation, the helper copies itself (after verifying its own Authenticode signature) to `%ProgramFiles%\VTDiag\<ver>\` and only ever runs elevated from there. **Never schedule an elevated task pointing at a user-writable path** (that would be a local privilege escalation).
- **Channels**: GitHub Releases (primary, SHA256 + SBOM) and winget (`winget-releaser` in CI).
- **App update**: at launch, never during a match, check the GitHub Releases API. With consent: download → verify Authenticode publisher + SHA256 from a signed `release-manifest.json` → run the installer silently (`/VERYSILENT`) → restart the UI. Open sessions keep their pack pins. No silent updates.
- **KB update**: a separate channel (§6.1), small and frequent, with no app release. That's how mods ship "Nahimic is back in a new version" in an hour.
- License: MIT (app) and CC BY-SA 4.0 (KB content, so improvements stay open). PresentMon MIT notice bundled.

---

## 18. MVP scope vs later

**MVP (v0.x, "the 80 % remote-session replacement")**
- The full scan with probes for the top ~40 checks of PROBLEM_CATALOG's diagnostic checklist (these cover most solved threads), plus the Vanguard requirements health board.
- KB: hand-tuned hypotheses for the **top 25 problems** + all 113 software signatures (auto-generated conflict hypotheses) + all 57 codes + route verdicts (HWID ban, account-specific, can't comply, console/Mac, server incident).
- The questionnaire with the adaptive planner, and error-code input.
- The engine with priors, groups, gates, negative evidence, calibration and explanations.
- About 20 primitives (§7.2 MVP list), the journal, restore points, undo and crash recovery.
- Reboot-resume through the task + RunOnce, with real-restart detection.
- The live monitor: PresentMon stutter/periodicity, PDH, event correlation, game-server RTT/jitter/loss, PresentMode mix.
- Reports (JSON, HTML, Markdown) and outcome cards. en + tr.
- Signed KB updates, SignPath signing, installer + portable, winget.
- `vtd`: lint, test, build, sign, priors, calibrate, replay, fixture add, harvest.

**Later**
- The 60 s DPC/ISR latency test with driver attribution.
- NVML/ADLX sensors. An optional HWiNFO shared-memory import.
- Vanguard clean reinstall flow (multi-reboot). Guided DDU with a safe-mode failsafe. DISM/SFC.
- Bufferbloat test. Region probe list.
- A mod web viewer for `report.json` (static page, client-side replay via WASM build of Engine+Kb).
- Optional local LLM explanations (Ollama/Foundry Local), limited to explaining and suggesting existing fix IDs.
- More languages. Community pack marketplace UI.
- Automated harvest cron (scheduled GitHub Action over new Arctic Shift data).
- Opt-in ETW image-load observation for injected-module detection, only after [T] confirms Vanguard tolerates it.

---

## 19. Work breakdown for parallel agents

**Milestone 0 (lead, sequential, about 1 day):** freeze `VTDiag.Contracts`: the fact model, the snapshot DTOs, the KB model records, IPC messages and every interface below. Also the JSON schemas in `kb/schema/` and fact catalog v1 (paths and types only). After that, each agent owns **disjoint folders** and codes only against Contracts and fakes.

| Agent | Owns (writes only here) | Builds | Consumes | Done when |
|---|---|---|---|---|
| **1 Platform** | `src/VTDiag.Platform/`, `src/VTDiag.Platform.Fakes/`, `tests/Platform*` | `ISystem` + Windows implementations, raw-capture recorder, fakes | Contracts | Every sub-interface has a real and a fake implementation; capture → fake round-trip test |
| **2 Probes** | `src/VTDiag.Probes/`, `tests/Probes*` | ~40 built-in probes, generic collectors, safe log extractors (with the Privacy interfaces from Contracts), fact catalog attributes | `ISystem` (fakes for tests), `IRedactor` interface | Probe snapshot tests pass on 3 recorded captures (including tr-TR) |
| **3 KB + Engine** | `src/VTDiag.Kb/`, `src/VTDiag.Engine/`, `src/VTDiag.Cli/`, `tests/Kb*`, `tests/Engine*`, `tests/Accuracy.Tests/` | VEX compiler, pack loader + signature check, scorer, planner, fix planner, calibration, anomalies, `vtd` commands | Contracts (pure; no ISystem) | Lint/test/replay work; accuracy harness runs on synthetic fixtures |
| **4 Actions + Session + Helper** | `src/VTDiag.Actions/`, `src/VTDiag.Session/`, `src/VTDiag.Ipc/`, `src/VTDiag.HelperHost/`, `tests/Actions*`, `tests/Session*` | Primitives, SafetyPolicy, journal, restore point, state machine, resume scheduler, pipe transport, helper composition | `ISystem`, `IEngine` interface | Primitive round-trip and fault-injection suites green; resume tests green |
| **5 Monitor** | `src/VTDiag.Monitor/`, `third_party/presentmon/`, `tests/Monitor*` | PresentMon runner, PDH, ETW network, event watchers, signal library, `mon.*` fact emitter | Contracts, `ISystem.EventLogs` | Recorded CSV/event streams → expected facts; overhead under 2 % |
| **6 UI + Report + Privacy** | `src/VTDiag.App/`, `src/VTDiag.Report/`, `src/VTDiag.Privacy/`, `tests/Report*`, `tests/Privacy*` | WPF screens (scan, questions, ranking, fix consent, monitor tray, report), resx en/tr, redactor, renderers, outcome card codec | `IHelperClient` (InProc fake), `IEngine` | End-to-end on InProc fakes; canary privacy tests green |
| **7 KB content** (can start on day 1, in parallel) | `kb/packs/**`, `kb/fixtures/synthetic/**`, `tools/import/**` | Import scripts (catalog/conflicts/codes → YAML), top-25 hypotheses, MVP fixes, questions, i18n en/tr, synthetic fixtures | `kb/schema/*`, fact catalog v1 | `vtd kb lint` clean, synthetic top-3 ≥ 90 % |

Order of integration: 1 → 2 (real probes) and 3 → 7 (accuracy) run in parallel. 4 needs 1. 6 needs 3+4 interfaces only. Renders, the installer, signing and commits are done sequentially by the lead.

Key interfaces (in Contracts):

```csharp
// ---- Platform (agent 1)
public interface ISystem {
    IRegistry Registry { get; } IWmi Wmi { get; } IServices Services { get; } IPnp Pnp { get; }
    INetAdapters Net { get; } IEventLogs EventLogs { get; } IPower Power { get; } IFirmware Firmware { get; }
    IBcd Bcd { get; } IFiles Files { get; } IProcesses Processes { get; } ITaskScheduler Tasks { get; }
    IFirewall Firewall { get; } ITrust Trust { get; } IClock Clock { get; } PlayerIdentity Player { get; } bool Elevated { get; }
}
public interface IRegistry {
    RegValue? Read(RegHive hive, string key, string value);                 // hive PlayerUser maps to HKU\<sid>
    IReadOnlyList<string> SubKeys(RegHive hive, string key);
    void Write(RegHive hive, string key, string value, RegValue data);       // only Actions may call (analyzer rule)
    void DeleteValue(RegHive hive, string key, string value);
}
public interface IEventLogs {
    IReadOnlyList<EventRecordData> Query(EventQuerySpec spec, CancellationToken ct);   // EventData fields only
    IDisposable Watch(EventQuerySpec spec, Action<EventRecordData> onEvent);
}

// ---- Probes (agent 2)
public interface IProbe {
    string Id { get; } int Version { get; } bool RequiresAdmin { get; } TimeSpan Timeout { get; }
    IReadOnlyList<string> Produces { get; }                                  // fact paths (validated against catalog)
    ValueTask<ProbeResult> RunAsync(ProbeContext ctx, CancellationToken ct); // ProbeResult = facts + status
}
public interface IProbeRunner {
    ValueTask<Snapshot> ScanAsync(ScanRequest req, IProgress<ScanProgress> progress, CancellationToken ct);
    ValueTask<Snapshot> ReprobeAsync(Snapshot prev, IReadOnlySet<string> paths, CancellationToken ct);  // targeted verify
}

// ---- KB + Engine (agent 3)
public interface IKbLoader { KbLoadResult Load(IEnumerable<PackSource> sources, KbLoadOptions opts); }   // validates, verifies signatures, compiles VEX
public interface IEngine {
    RankResult Rank(Snapshot s, KnowledgeBase kb, SessionEvidence ev);       // pure
    Question? NextQuestion(RankResult r, KnowledgeBase kb, int asked);
    IReadOnlyList<FixProposal> ProposeFixes(RankResult r, Snapshot s, KnowledgeBase kb, FixHistory h);
    VexValue Evaluate(string compiledExprId, Snapshot s, VexBindings b);     // verify expressions
}
public sealed record SessionEvidence(IReadOnlyList<FixOutcome> FailedFixes, IReadOnlyDictionary<string, FactValue> Answers);

// ---- Actions + Session (agent 4)
public interface IFixExecutor {
    ValueTask<FixPlan> PlanAsync(FixProposal p, Snapshot s, CancellationToken ct);
    ValueTask<FixRunResult> ApplyAsync(FixPlan plan, ConsentToken consent, IProgress<FixStep> progress, CancellationToken ct);
    ValueTask<FixRunResult> UndoAsync(SessionId session, string fixId, CancellationToken ct);
}
public interface ISessionController {
    IObservable<SessionState> States { get; }
    ValueTask StartAsync(StartOptions o, CancellationToken ct);
    ValueTask SubmitAnswerAsync(string questionId, FactValue answer, CancellationToken ct);
    ValueTask AcceptFixAsync(string fixId, ConsentToken consent, CancellationToken ct);
    ValueTask ReportOutcomeAsync(string fixId, OutcomeKind outcome, CancellationToken ct);
    ValueTask ResumeAsync(SessionId id, CancellationToken ct);
}

// ---- Monitor (agent 5)
public interface ILiveMonitor {
    ValueTask ArmAsync(MonitorSpec spec, CancellationToken ct);              // spec from kb monitor/*.yaml
    IObservable<MonitorTick> Ticks { get; }                                  // 1 Hz summaries for the tray
    ValueTask<MonitorFacts> CollectAsync(CancellationToken ct);              // mon.* facts at match end
}

// ---- Privacy + Report (agent 6)
public interface IRedactor { Snapshot Redact(Snapshot s, RedactionLevel level, out RedactionLog log); }
public interface IReportRenderer { string Format { get; } ValueTask RenderAsync(ReportModel m, Stream output, CultureInfo culture, CancellationToken ct); }
public interface IOutcomeCardCodec { string Encode(OutcomeCard c); bool TryDecode(string text, out OutcomeCard c); }

// ---- IPC (agent 4 implements, agent 6 consumes)
public interface IHelperClient : ISessionController { ValueTask<HelperInfo> ConnectAsync(PipeEndpoint ep, string token, CancellationToken ct); }
```

A Roslyn analyzer in Contracts (`VTD001`) forbids calling `ISystem` write methods outside `VTDiag.Actions`. That's a cheap, compile-time guarantee that probes stay read-only.

---

## 20. Risks and open questions

| Risk | Mitigation |
|---|---|
| ETW Kernel-Network / PresentMon during a match triggers Vanguard | [T] 30-min ranked test before release. Monitor sources can be switched off one by one. Network falls back to ICMP to the region list. |
| Priors from Reddit are skewed (solved-thread and self-reporting bias) | Bias correction with U/S, an explicit unknown share, calibration on real fixtures, and outcome harvest over time. |
| Too few real fixtures at launch | Synthetic fixtures for all 90 problems. Mods' current AnyDesk sessions become the first 50–100 real fixtures (`vtd scan --capture` run by the mod during a remote session). |
| Community rule quality | Schema + lint + accuracy gate + mod-approved signing. Local packs can't fix. Strength words instead of raw numbers. |
| Pack-signing key compromise | The key lives in a protected GitHub environment with required reviewers. The app embeds 2 public keys (current + next) for rotation. `revoked[]` in the index. |
| Pre-Check (On-Demand) detection is still an open question (error_codes.json) | `vg.precheck_suspected` is a heuristic fact (Win11 25H2 + all requirements met + vgc Manual/stopped + vgk Start=1). The vgc-Automatic fix is excluded when it is True or Unknown, and the user is asked instead. |
| Fast Startup "reboots" that aren't | Boot-type detection and the "Restart, not Shut down" UI. Verification refuses to run on a hybrid boot. |
| Event provider names / IDs for Realtek resets differ across driver versions | Collector providers are data (fix with a pack update). The anomaly detector catches new NIC providers. |
