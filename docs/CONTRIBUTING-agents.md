# VTDiag: working rules for the parallel agents (M1+)

M0 is done: the solution, build rules, banned-API layers, `VTD.Contracts`, `VTD.Testing.Fakes`, `kb/schema`, three
fixture PCs and CI. Six agents now build on it **in parallel, in separate git worktrees, on disjoint folders**.
`DESIGN.md` is the source of truth; this file tells you where you may write and what "done" means.

## 0. The one rule

**Never edit `src/VTD.Contracts/`.** If you need a contract change (a field, a type, an interface member, an enum
value, a constant), keep working against what exists and put a **"Contract change requests"** section in your final
report: what, why, the exact C# you propose, and who else it affects. The lead batches these (at most one Contracts
PR per agent per day) and merges.

The same applies to everything the lead owns:

| Lead-owned (read-only for agents) | What it is |
|---|---|
| `src/VTD.Contracts/` | DTOs, records, enums, interfaces, JSON contract (`VtdJson`, `VtdJsonContext`), `OpCatalog`, `CommandPolicy`, `GuardIds`, `RankingConstants` |
| `src/VTD.Testing.Fakes/` | Fakes and the fixture builder (ask for a new fake or fixture variant in your report) |
| `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `.editorconfig` | Build, layers, package versions, analyzer severities |
| `banned/` | Banned-symbol files and the two allowlists |
| `kb/schema/` | Generated JSON Schemas and `ops.json` (never hand-edit) |
| `tests/VTD.Contracts.Tests/`, `tests/VTD.Policy.Tests/`, `tests/fixtures/snapshots/*.json` | Contract tests, repository policy, the three hand-made fixtures |
| `tests/VTD.Integration.Tests/` | End-to-end chain: snapshot → bundled KB → analyzer → share export → sweep, on the real dev-PC snapshot and all 55 fixtures |
| `VTDiag.sln`, `.github/` | Solution and CI |

Do not commit. The lead merges your worktree, builds, and commits. Do not add projects or packages: ask (a new
project needs a solution entry and a layer decision; a new package is an AV-reputation and licence decision).

## 1. Who owns what

| Agent | Owns (exclusive write) | Implements (from `VTD.Contracts`) | Develop against (fakes) | Done when |
|---|---|---|---|---|
| **A1 Collectors** | `src/VTD.Platform/`, `src/VTD.Collectors/`, `tests/VTD.Collectors.Tests/`, `tests/fixtures/captures/`, `tests/fixtures/snapshots/real/` | `ICollector` (os, hardware, firmware, security, storage, devices, software, network, power, display, audio, input, riot, events, pack), `IScanService` (orchestrator), `IFactProbe` (every `GuardIds` id incl. `os.clock_ok`), the pack-collector executor (six kinds, `PackCollectorLimits`), log safe-extractors (ShooterGame.log, Riot Client logs, `Crashes\UECC-*`, vgc log metadata only), raw capture + replay | `FixtureSnapshots` (target shapes), `FakeClock`, `SequentialIdGenerator`, `FakePlayer`, `CanaryTokens` | Full scan on the dev PC < 60 s with 0 crashes; every collector has a timeout and a limited-mode path (admin-only facts are `Unknown(NeedsAdmin)`, never defaults); the snapshot validates against `kb/schema/snapshot.schema.json`; per-user data comes from `ScanRequest.Player`, never the engine's own profile/HKCU; matching uses InstanceIds, numeric values, GUIDs and event ids, never localized names; Turkish-locale capture replay tests; process list without a single handle |
| **A2 Knowledge & Analysis** | `src/VTD.Knowledge/`, `src/VTD.Analysis/`, `kb/` except `kb/schema/`, `tools/kb_builder.py` (and any `tools/` FixtureGen), `tests/fixtures/snapshots/generated/`, `tests/VTD.Analysis.Tests/`, `tests/VTD.Kb.Tests/` | `IKnowledgeBaseLoader` + `IKnowledgeBase` (Ed25519, two embedded keys, monotonic revocation serial, bundled fallback, lenient read after schema validation, clock-corrected `NowUtc`), `ISignalProvider`s (one per catalog `check_id`, the generic conflict provider for all 113 entries, collector-derived, `ask.*`, codes), `IAnalyzer`; the KB content: 90 rules, 113 conflicts, 57 codes, guides, questions (incl. `ask.league_also_broken`), symptoms, en/tr strings | `Fixtures` + `Fixtures.ExpectedTopHypothesis`, `FakeKnowledgeBase.Sample()` (format examples), `CannedAnalyses` (target outputs), `kb/schema/ops.json` (primitive ids, args, state keys) | KB lint green (schema-valid, every signal/fix/guide/question/code reference resolves, every `op`/arg/verify key exists in `ops.json`, deadman on network-cutting fixes, high-risk fixes have no steps, `en` complete); fixture top-3 ≥ 90 % and a "false very likely" gate; analysis deterministic (id and time come from `AnalysisInput`) |
| **A3 Fixes & Journal** | `src/VTD.Fixes/`, `src/VTD.Journal/`, `tests/VTD.Fixes.Tests/`, `tests/VTD.Fixes.IntegrationTests/`, `tests/VTD.Journal.Tests/` | `IOperation` for **exactly** the `OpCatalog` entries + `IOperationRegistry` (a test asserts registry metadata == `OpCatalog`), ProtectedTargetPolicy, `IFixRunner`, `IJournal`, `IStateStore<T>` (atomic replace + `.bak` + `IStateRebuilder<T>`), `IRecovery`, quarantine | `FakeFactProbe.ReadyToFix()`, `FakeKnowledgeBase`, `InMemoryJournal` (reference semantics), `FakeRestorePointService`, `FakeClock`, `FakeFixRunner` (reference consent behaviour) | Every primitive round-trips undo bit-identically (capture hash before == probe hash after undo) on sandboxed targets; torn-write/CRC/duplicate/out-of-order journal fuzz tests; recovery tests incl. "deadman armed, never confirmed ⇒ undone"; policy deny-matrix tests. **Writes are only tested in CI runners, a VM or Windows Sandbox, never on the dev PC** |
| **A4 Session & Engine** | `src/VTD.Session/`, `src/VTD.Engine/`, `installer/`, `tests/VTD.Session.Tests/`, `tests/VTD.Engine.Tests/`, `tests/VTD.Chaos/` | `SessionMachine` (pure) + `ISessionController`, `IRebootCoordinator`, `IRestorePointService`, `IStateRebuilder<SessionState>`, the `IEngineRpc` server, CLI verbs, DI host, Inno script | `FakeScanService`, `FakeAnalyzer`, `FakeKnowledgeBase`, `FakeFixRunner`, `InMemoryJournal`/`InMemoryStateStore`, `FakeRecovery`, `FakeMonitorService`, `FakeClock`, `FakePlayer` | Property tests over random event sequences with a crash injected at every persist point: never restarts without a UI `RestartNowClicked`, resume attempts ≤ 3, no tasks/RunOnce in terminal phases, recovery always consistent; `CommandPolicy` enforced with `Origin` stamped from the pipe; the RPC contract test passes against the real server; a VM chaos run passes |
| **A5 Monitor** (M3; empty until then) | `src/VTD.Monitor.Core/`, `src/VTD.Monitor/`, `third_party/PresentMon/`, `tests/VTD.Monitor.Tests/`, `tests/fixtures/presentmon/` | `IMonitorService` (served over a pipe by `VTDiag.Monitor.exe`), frame source (PresentMon child), PDH, event watchers, endpoint + ping, detectors (`MonitorSummary.Detectors` = `mon.*` signals), SelfBudget + degrade ladder | `FakeMonitorService` (output shape), `FakeClock` | Replay tests on recorded PresentMon CSVs and event traces; ≤ 2 % CPU target with a hard auto-stop; no handle of any kind to Riot/Vanguard processes; never `timeBeginPeriod`; the moderator's 3 × 30-min in-match acceptance test |
| **A6 UI & Reporting** | `src/VTD.App/`, `src/VTD.Reporting/`, `docs/` (guide pages, privacy, plan docs; not this file), `tests/VTD.App.Tests/`, `tests/VTD.Reporting.Tests/` | All screens (EN/TR resx), tray, Helper mode + plan builder, `IRedactor` (incl. `ShareEntityId`), `IReportBuilder` (HTML no-JS, Markdown ≤ 9 000 chars, JSON, "Copy for AI" digest + `plan.schema.json`), `IScanBundleCodec`, `IPlanValidator` (plain plan or `SignedPlanEnvelope`), the consent dialog for `ConsentRequestedNotification` (proposer badge, second click for one-way) | `FakeEngineRpc` (set `ClientOrigin` to Cli/Mcp to act as an agent), `FakeKnowledgeBase`, `CannedAnalyses`, `Fixtures`, `CanaryTokens` | The full flow is clickable on `FakeEngineRpc` in EN and TR (pseudo-loc +40 % length); redaction golden + canary tests green (no canary survives a share export; any sweep hit blocks the export and names the section to leave out); reports open offline with no JavaScript |

Pre-approved audited exceptions (create the file at exactly this path; nothing else may suppress RS0030):
`src/VTD.Platform/Processes/ProcessRunner.cs`, `src/VTD.Platform/IO/SharedReadFile.cs`,
`src/VTD.Platform/Wmi/ReadOnlyWmiMethods.cs` (A1); `src/VTD.Monitor.Core/Frames/PresentMonProcess.cs` (A5);
`src/VTD.App/Services/EngineLauncher.cs`, `src/VTD.App/Services/LinkOpener.cs`,
`src/VTD.App/Services/PipeServerVerifier.cs` (A6); `src/VTD.Engine/Ipc/PipeClientVerifier.cs` (A4, see
`banned/import-allowlist.txt`).

## 2. Rules every agent follows

**Layers (enforced by the compiler).** `Directory.Build.targets` maps each project to a layer:
Write = Fixes, Journal, Session, Engine; Ui = App; Test = everything under `tests/`; ReadOnly = everything else.
ReadOnly code cannot write the registry, files, services, event logs or WMI, cannot start or kill processes, and cannot
open files with a `FileMode`. Every project, tests included, is banned from culture-sensitive string APIs, from
`DateTime.Now/UtcNow` (inject `TimeProvider`), from enumerating processes (`Process.GetProcesses*`; use the Platform
ToolHelp snapshot) and from process handles/modules. `tests/VTD.Policy.Tests` additionally fails the build if any
VTDiag binary imports `OpenProcess`, `ReadProcessMemory`, `WriteProcessMemory`, `CreateRemoteThread`,
`SetWindowsHookEx`, `NtQuerySystemInformation`, `timeBeginPeriod` and friends (outside the import allowlist), if a
project file opts out of analyzers, or if any folder adds its own `.editorconfig` / `Directory.Build.*`.

**Three-valued facts.** `Unknown ≠ False`. Read a `Fact<T>` with `TryGetValue` or `ToTri()`; `.Value` throws on
Unknown on purpose. Evidence over an Unknown fact contributes nothing, a gate fires only on a definite False, and the
report says "could not check X". Inside list items a null nullable means "not read".

**JSON.** Always `VtdJson` (`Options` strict for IPC, state, journal, fixtures; `LenientOptions` only for data a newer
producer may have written: downloaded KB packs, `.vtdplan`, `.vtdscan`, after checking the format major and
validating against `kb/schema`). camelCase, string enums, no NaN/Infinity (use null), no comments. Every `T` inside a
`Fact<T>` must be in `VtdJsonContext` (the lead adds it). `SessionEvent.Origin` is never serialized.

**Culture.** Machine data is invariant. Use `StringComparison.Ordinal(IgnoreCase)`, `ToUpperInvariant`,
`CultureInfo.InvariantCulture` for parse/format, and `string.Create(CultureInfo.InvariantCulture, $"...")` for
interpolated numbers in machine text (`$"{double}"` uses the current culture and the analyzers cannot see it). Display
formatting uses the UI culture. Never match on localized display names (Turkish Windows says "Dengeli", not "Balanced").

**Time and ids.** Inject `TimeProvider` (tests: `FakeClock`) and `IIdGenerator` (tests: `SequentialIdGenerator`). All
ids are ULIDs. All "last N days" windows are relative to the scan/analysis time, not the wall clock.

**Safety (cannot be overridden by the KB or a plan).** Consent comes only from a UI click (`ConsentGiven` is UI-only in
`CommandPolicy`); the CLI and MCP can only propose (`FixSelected`, `UndoProposed`) and wait
(`WaitForOutcomeAsync`). One fix at a time; one-way fixes need a second click; network-cutting changes have a journaled
60 s deadman; a restore point precedes the first medium/high-risk fix; files are quarantined, never deleted; security is
never lowered; no restart path except the UI's "Restart now" token; nothing ever opens a handle to a Riot/Vanguard
process.

**Privacy.** Secrets are never collected (command lines, Riot tokens, Vanguard log contents, Wi-Fi keys). Redact at
parse time. Every string in an exported type carries `[Sensitivity]` (the redactor reads the property attribute, then
the declaring type's with `inherit: true`); a contract test enforces this, so ask the lead when you need a new field.
Plant `CanaryTokens` in your fixtures' free-text fields.

**Fixtures and schemas.** `tests/fixtures/snapshots/*.json` are generated from `FixtureSnapshots` and
`kb/schema/*.json` from the contracts (`VTD_UPDATE_FIXTURES=1` / `VTD_UPDATE_SCHEMAS=1` when running
`tests/VTD.Contracts.Tests`, lead only). Your own fixtures live in your folders (`real/`, `generated/`, `captures/`).

## 3. After the M1 integration (read before starting M2)

M0 + A1 + A2 + A6 are merged and integrated. What changed in the contracts, and what A3 (Fixes & Journal) and A4
(Session & Engine) must honour:

**JSON.** `VtdJson.Options` now builds metadata by reflection, gated by `VtdJsonContext` (an unregistered type still
throws). Absent init-only members keep their C# initializer defaults (`FixDef.Admin` true, `GuideDef.Risk` High, lists
`[]`, `Rev` 1) in strict, lenient, indented and RPC options. Never use `VtdJsonContext.Default` or its `Options`
directly: that path still drops those defaults. `KbJson` is gone; read KB files, plans, bundles, state and journal
through `VtdJson`.

**Partial lists.** `Fact<T>.Partial(value, detail, source)` (JSON `"complete": false`) is a Known value that is only part
of the truth (needs admin, a hive that could not be read, a cap). What is in it is real; "not in it" is Unknown.
Collectors use `RunBuilder.Partial`; `Sig.FromFact` turns a False from a partial fact into `Unknown(Incomplete)`; a
signal whose True means ABSENCE ("no VC++ runtime", "no default output") uses `Sig.FromFactAbsence`. A1 marks apps
(player hive unreadable), startup (hive or `System32\Tasks` unreadable: always partial unelevated), events (a failed
query; a capped query is not partial, it already returned more than any threshold), the Winsock catalogs and the
drivers passthrough. A3: a guard or verify that reads a list must do the same.

**Engine (A4).**
- `IEngineRpc.GetPreviewAsync(previewId)` returns the preview computed for `FixSelected` (the exact one the consent token
  binds to): the UI gets `ConsentNonce`, CLI/MCP get it null; null once used or expired. The App consent screen now
  shows this preview (it only recomputes as a fallback).
- Check the consent token BEFORE leaving AwaitingConsent; a refusal throws and leaves the session in AwaitingConsent
  (reference: `FakeFixRunner.CheckConsent`, `FakeEngineRpc.ApplyAsync`).
- `GetMonitorSummaryAsync(summaryId)` (share-redacted for CLI/MCP); the Outcome screen shows it.
- `ScanCancelRequested` (any client): stop the collectors, discard the partial snapshot, go back to the phase before the
  scan; ignore it when no scan runs. `MonitorMarkBadRequested` (UI only, the tray): forward to
  `IMonitorService.MarkBadAsync`; ignore outside Monitoring.
- `FixSelected.PlanId`: stamp it into `Proposer.PlanId` only when it equals `SessionState.ImportedPlanId`.
- `ScanRequest.ProbeTargets`: fill from the KB table `ping_targets` (columns `label`, `host`) when it exists (it does
  not yet; do not invent Riot IPs). IP literals only, at most `ProbeTarget.MaxTargets`; A1 skips anything else.
- Persist `KbLoadResult.RevocationSerial`, `RevokedKeyIds` and `RevokedVersions` and pass them back as
  `KbLoadOptions.MinRevocationSerial`, `KnownRevokedKeyIds`, `KnownRevokedVersions`: revocation only grows, even if a
  list file is deleted.
- `IPlanValidator.Validate(..., freshAnalysis)` takes the analysis of the fresh scan; pass it (it restricts what a
  non-template fix may bind). Plans carry `FixPlan.ForMachineKey` (mismatch = invalid).

**Fixes (A3).**
- `392667600.dat` lives in `%WINDIR%` (`RiotFileInfo.Location` `windows`), not System32; `OpCatalog` policy notes say
  so. On the dev PC (Vanguard working) it is 256 MB and recently rewritten, so it is NOT necessarily stale: the KB only
  counts it with matching symptoms. Quarantine it only through the KB fix and the protected-target policy. **Open
  before M2 ships `fix.vg.quarantine_stale_dat`:** it has no staleness guard (A2 + A3: add one, for example "Vanguard
  failed to start since the last boot" from the vgc log metadata, or a file time well before the current boot).
- The sample KB has a restart-requiring fix, `fix.display.hags_off` (registry.setValue, `Reboot = Required`): use it
  for AwaitingRestart / resume tests.
- `EntityRef.DisplayName` is `FreeText` now (it may carry a user or device name): never log it unredacted.

**Reports.** `CouldNotCheck.TextKey` is the title of WHAT was not checked; the reason comes from `Reason`. Never render
"`<id>: value`" or "`<id> = value`" with KB ids in share text: ids ending in a key word (`ipv6`, `hwid`, `username`)
trip the sweep and block the export (the helper section now uses `·` and `→`).

**Regeneration (lead).** `VTD_UPDATE_SCHEMAS=1` / `VTD_UPDATE_FIXTURES=1` (Contracts.Tests), `VTD_UPDATE_GENERATED=1`
(Analysis.Tests), `VTD_UPDATE_GOLDEN=1` (Reporting.Tests), `VTD_UPDATE_CAPTURE=1` alone (Collectors.Tests) re-derives
`tests/fixtures/snapshots/real/dev-pc.json` from the checked-in capture without scanning; with `VTD_LIVE_SCAN=1` too it
re-captures this PC (read-only, unelevated). `python -I tools/kb_builder.py build` after any `tools/kb_curation` or
`tools/kb_src` change.

## 4. Build, test, report

```
dotnet build VTDiag.sln -c Debug     # must stay at 0 warnings, 0 errors (warnings are errors)
dotnet test VTDiag.sln -c Debug      # contracts + policy + every agent project
```

Replace the `AssemblyMarker.cs` / `PlaceholderTests.cs` placeholder in your projects when your first real type and
test land. Finish every task with a short report: what you built (paths), what you verified and how (test names,
measurements), deviations from `DESIGN.md`, open questions, and **Contract change requests** (or "none").
