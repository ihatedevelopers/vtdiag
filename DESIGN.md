# VTDiag — final design decisions

Source of truth for building VTDiag. Decided 2026-10-21 after two independent designs (`research/design_A.md`, `research/design_B.md`) and an independent critique (`research/critique.md`).

**Rule of precedence:** this file > `research/critique.md` §2 table > `research/design_B.md` > `research/design_A.md`. Where a section here says "per B §x" or "per A §x", read that section for detail; this file overrides it wherever they differ.

## 1. Product

VTDiag is a free, open-source Windows 10/11 desktop app for VALORANT players. Built for r/ValorantTechSupport. It is not affiliated with Riot Games; the name contains no Riot trademark, and the app says so on the About screen and in every report.

The flow:
1. Full PC scan.
2. Health checklist and ranked suspects.
3. Fixes one at a time. Each fix is explained, then the player consents, the tool backs up, applies, verifies, and offers undo.
4. A clear reboot notice when a fix needs one, then resume after the reboot.
5. ~~Live in-match monitoring.~~ DROPPED from scope (owner decision, M2): no Monitor process, no PresentMon. `VTD.Monitor*` stay as empty projects.
6. A shareable report, and a one-file **debug bundle** (`.vtddebug`: redacted rolling logs, last scan, analysis, journal, session, environment) so a bug report explains both a VTDiag problem and the PC problem.

The UI is in English by default, with Turkish as a second language. All machine data and matching are culture-invariant.

**Honesty rule:** the report always says which bucket the player is in:
- Fixable here.
- Guided manual step (BIOS, Secure Boot).
- ISP or network outside this PC.
- Riot or account side.
- Likely hardware.
- Unknown.

We never claim to fix what a PC tool cannot.

## 2. Architecture (adopted)

The skeleton is B; the engine ideas come from A. The whole critique §2 table is adopted. Key points:

**Stack**
- C# on .NET 10 LTS with WPF, x64.
- No PowerShell at runtime, no packing or obfuscation, no kernel drivers of any kind.
- Banned-API analyzers enforce this, plus a CI scan of the binary's import table. The banned list is B §13.2: no OpenProcess/ReadProcessMemory/WriteProcessMemory/CreateRemoteThread/SetWindowsHookEx against any process, no `NtQuerySystemInformation` handle classes, and no culture-sensitive string ops.

**Processes**
- `VTD.App` is the WPF UI and always runs unelevated as the player.
- `VTD.Engine` is the only writer. It is a headless elevated process and refuses to run elevated unless its own image is under `%ProgramFiles%`.
- The two talk over a named pipe with StreamJsonRpc. The pipe DACL allows only the interactive user, Administrators and SYSTEM, with `PIPE_REJECT_REMOTE_CLIENTS` and `FIRST_PIPE_INSTANCE`.
- The engine checks the client's identity from the pipe itself (PID → token SID, image path, signer). It never trusts what the client says about itself.
- `VTD.Monitor` becomes its own low-priority process in M3.

**Install**
- Per-machine Inno Setup into Program Files. No portable elevation path.

**Scan data and rules**
- **Three-valued facts:** `Unknown ≠ False`. Evidence that rests on an Unknown fact contributes nothing, and the report says "could not check X". A gate fires only on a definite False.
- **Rules:** B's JSON weighted-signal rules plus C# signal providers. On top of that, A's **read-only pack-declared collectors**: event-log provider/ID query, uninstall-key regex, loaded-driver name, faulting-module match, registry value read, file existence. These let mods ship a new conflict wave as data, with no release. There is no expression DSL until v0.3+, and only if external authors appear.
- **Ranking:**
  - Hypotheses are independent multi-label posteriors (two real problems can co-exist).
  - Each category starts from a flat smoothed prior, plus a fixed **"unknown cause" floor**.
  - Error codes go through a table with per-cause strength. Typed codes weigh less than codes found in logs. Route codes (VAN 152 → appeal; console/VM/Mac → out of scope; account-side VAL 43/57/59 → "try another account") short-circuit.
  - At most 3 intake questions, asked only when the top two are within a fixed margin and a question separates them.
  - A failed fix counts as negative evidence.

**Knowledge base**
- `kb/` is data, versioned apart from the app and signed with Ed25519, with two embedded public keys and a monotonic revocation list.
- A pack that fails verification is never loaded; the app falls back to the bundled KB.
- `tools/kb_builder.py` (Python, so mods need no .NET) generates the drafts from `data/catalog.json`, `research/conflicts.json` and `research/error_codes.json`.

**Fixes**
- Fixes are JSON that only composes ~25 audited C# primitives (B §4.3). They contain no commands, paths or URLs as actions.
- Each primitive does backup → apply → verify → undo.
- Apply uses a write-ahead journal: jsonl, CRC, fsync before every change.
- A consent token binds (fixId, bindingHash, previewHash), so the engine refuses if the preview changed (TOCTOU).
- Network operations get a 60 s deadman auto-revert.
- One fix at a time, and one-way fixes need a second consent.
- A restore point is created and verified before the first medium- or high-risk fix.
- `ProtectedTargetPolicy` (B §13.1) can't be overridden by the KB or a plan.
- **Privilege-escalation hardening (critique §1.3):**
  - Elevated file operations are reparse-safe.
  - A user-hive `UninstallString` is never run as admin; uninstalls run unelevated in the player's context or only from HKLM with a verified publisher.
  - Quarantine goes to an ACL'd folder under `%ProgramData%`, never a delete.

**Reboot-resume (B §6.4 plus critique §3 hardening)**
- The engine has no restart path at all except a UI click.
- Boot-ID check, 3-attempt cap, 7-day expiry.
- Two tasks: an engine task at Highest and a UI task at Limited.
- Standard-user-with-separate-admin is detected and falls back to an in-context RunOnce plus UAC.
- The reboot prompt is gated on AC power.
- A pending Windows Update blocks applies; boot-ID tells "my reboot" apart from a Windows Update reboot.
- Fast Startup or hybrid shutdown is detected; disabling Fast Startup is itself a fix.
- Debloated Windows (Task Scheduler disabled) degrades to RunOnce, then to manual open.

**Signals both designs under-used (must be in M1)**
- ShooterGame.log safe-extractor patterns: LogInit hardware line, `LogPlatformInitializerV2 … Failed`, D3D device lost, LogNet.
- Riot Client logs.
- `Crashes\UECC-*` metadata.
- **vgc log file existence and timing** (metadata only; the content is encrypted). This tells Pre-Check apart from a broken Vanguard.
- An intake question: "is League also broken?"
- Clock skew is resynced or tolerated **before** signature and expiry checks.
- Redaction is applied at parse time. A redaction hit at export time blocks the export. Canary tests plant tokens in fixtures.

**Vanguard Pre-Check**
- vgc Manual + Stopped is normal. The tool never forces it to Automatic unless the player confirms Pre-Check is off and the vgc logs show a failure.

**Testing**
- Synthetic fixtures generated per problem.
- A's raw-capture fakes: `vtdiag scan --capture` records real probe outputs, which are replayed in tests. Real Turkish-locale captures from this dev PC are included.
- CI accuracy gate: top-3 hit rate on fixtures, and the rate of false "very likely" verdicts.

## 3. My additions beyond A, B and the critique

1. **AI agent bridge (closes the gap to the "AI over AnyDesk solves 90%" benchmark).**
   - The engine exposes the same operations through a CLI:
     - `vtdiag scan --json`
     - `vtdiag analyze --json`
     - `vtdiag fix preview <id> --bind ...`
     - `vtdiag fix apply` (consent is collected in the UI, never by the caller)
     - `vtdiag undo`
     - `vtdiag export`
   - In M3 the same surface is offered as a local **MCP server** (stdio). The moderator's existing AI agent (Antigravity, Claude Code, any MCP client) then investigates freely using VTDiag's safe, journaled, undoable primitives instead of raw PowerShell.
   - The agent can propose but can't self-consent. Every write still pops the VTDiag consent dialog for the human at the PC.
   - A generic **entity-bound fix template** set lets the agent or a helper plan act on software no rule names, still going through ProtectedTargetPolicy: close process, disable service, disable device, remove compat layer, unbind IPv6, disable startup entry.
   - `.vtdscan` / `.vtdplan` (B §9) is the offline version of the same loop, using the same `PlanValidator`.
2. **"Copy for AI / Copy for Reddit" buttons from M1.** A redacted digest plus `plan.schema.json`, so a helper's AI can answer from a scan even without remote access. This alone serves the 2,180 threads that never got a reply.
3. **Live monitoring is dropped** (owner decision). Instead: structured redacted JSON-lines logs (engine + UI via the engine, size-capped, redacted at write time), a Debug-mode toggle, and `VTDiag.Engine.exe --cli export --debug-bundle <file>` / Settings button producing the `.vtddebug` zip; any redaction-sweep hit blocks it and names the section. The App talks to the real engine over the hardened pipe by default (`--demo` selects the fixture engine); without UAC it falls back to the limited, read-only engine.

## 4. Milestones

| M | Name | Ships | Gate |
|---|---|---|---|
| M0 | Contracts | solution, props, banned analyzers, `VTD.Contracts`, `VTD.Testing.Fakes`, `kb/schema`, CI skeleton | everything compiles against fakes |
| M1 | Read-only doctor | collectors (top problems + full Vanguard readiness + logs), KB built from our data (90 problems, 113 conflicts, 57 codes), analysis/ranking, guides, report (HTML no-JS / MD ≤ 9k / JSON), EN+TR UI, CLI `scan/analyze/export --json` | real scan on this PC < 60 s with 0 crashes; fixture top-3 ≥ 90 %; redaction canaries pass |
| M2 | Fixes | journal, primitives, undo center, restore point, reboot-resume, drift watch, fix JSON for the auto-fixable set (critique §4 v0.2 list) | every primitive round-trips undo bit-identically; recovery and chaos tests pass in a VM / Windows Sandbox. Writes are never tested on the dev PC without explicit per-fix consent from the owner |
| M3 | Assist | ~~Monitor process~~ (dropped), `.vtdscan/.vtdplan`, MCP server, outcome cards | - |
| M4+ | Later | scripted Vanguard clean reinstall, clean-boot bisection, SFC/DISM, DPC test, NVML/ADLX, guided DDU, opt-in HWiNFO temps | — |

The release order is the critique's: ship M1 publicly first to build SmartScreen/AV reputation, then M2. Signing via SignPath Foundation (free for OSS), updates via Velopack or the installer. Publishing anything (GitHub repo, releases, Reddit post) is the owner's call.

## 5. Layout and work split

Folder tree, dependency direction, agent ownership and the key interfaces are per **B §3 and B §18**, with these changes:
- `VTD.Monitor*` is created in M0 as empty projects only.
- `tools/KbBuilder` is `tools/kb_builder.py`.
- `VTD.Cli` verbs live in `VTD.Engine` (`--cli`).
- An MCP host is added in M3 as `VTD.Mcp`.

Parallel agents work in **separate git worktrees on disjoint folders**. Only the lead edits `VTD.Contracts`; the lead merges, builds and commits. Contract changes are batched through the lead.
