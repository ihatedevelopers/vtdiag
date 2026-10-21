using System.Text.Json.Serialization;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Scanning;

namespace VTD.Contracts.Sessions;

/// <summary>
/// Who caused an event. NEVER serialized and never taken from a client: the engine stamps it from the verified pipe
/// identity (PID → token SID, image path under %ProgramFiles%, signer) after deserialization.
/// </summary>
public enum EventOrigin
{
    /// <summary>Not stamped yet. The engine refuses an event it has not stamped.</summary>
    Unspecified = 0,

    /// <summary>The verified VTD.App client: the only origin whose consent, restart and undo commands are accepted.</summary>
    Ui = 1,

    /// <summary>The engine itself (effect results).</summary>
    Engine = 2,

    /// <summary>The resume task after logon.</summary>
    Resume = 3,

    /// <summary>The CLI (<c>VTDiag.Engine.exe --cli …</c>, a pure <see cref="Ipc.IEngineRpc"/> client).</summary>
    Cli = 4,

    /// <summary>The MCP host (M3), also a pure <see cref="Ipc.IEngineRpc"/> client.</summary>
    Mcp = 5,
}

/// <summary>
/// Every user action (<see cref="SessionCommand"/>) and every effect result (<see cref="EngineEvent"/>) is an event
/// (design_B.md section 6.1). The session machine is a pure function <c>(state, event, now) → (state', effects)</c> owned
/// by A4. JSON discriminator: <c>type</c>. Clients can only ever send <see cref="SessionCommand"/>s.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(IntakeSubmitted), "intakeSubmitted")]
[JsonDerivedType(typeof(ScanRequested), "scanRequested")]
[JsonDerivedType(typeof(RescanRequested), "rescanRequested")]
[JsonDerivedType(typeof(QuestionAnswered), "questionAnswered")]
[JsonDerivedType(typeof(FixSelected), "fixSelected")]
[JsonDerivedType(typeof(ConsentGiven), "consentGiven")]
[JsonDerivedType(typeof(FixSkipped), "fixSkipped")]
[JsonDerivedType(typeof(DeadmanAnswered), "deadmanAnswered")]
[JsonDerivedType(typeof(RestartNowClicked), "restartNowClicked")]
[JsonDerivedType(typeof(RestartLaterChosen), "restartLaterChosen")]
[JsonDerivedType(typeof(OutcomeReported), "outcomeReported")]
[JsonDerivedType(typeof(MonitorRequested), "monitorRequested")]
[JsonDerivedType(typeof(GuideStarted), "guideStarted")]
[JsonDerivedType(typeof(GuideStepCompleted), "guideStepCompleted")]
[JsonDerivedType(typeof(UndoProposed), "undoProposed")]
[JsonDerivedType(typeof(UndoRequested), "undoRequested")]
[JsonDerivedType(typeof(UndoAllRequested), "undoAllRequested")]
[JsonDerivedType(typeof(PlanImported), "planImported")]
[JsonDerivedType(typeof(AbandonRequested), "abandonRequested")]
[JsonDerivedType(typeof(ScanCancelRequested), "scanCancelRequested")]
[JsonDerivedType(typeof(MonitorMarkBadRequested), "monitorMarkBadRequested")]
[JsonDerivedType(typeof(ScanCompleted), "scanCompleted")]
[JsonDerivedType(typeof(ScanFailed), "scanFailed")]
[JsonDerivedType(typeof(AnalysisCompleted), "analysisCompleted")]
[JsonDerivedType(typeof(FixApplyCompleted), "fixApplyCompleted")]
[JsonDerivedType(typeof(ResumeStarted), "resumeStarted")]
[JsonDerivedType(typeof(PostRebootVerifyCompleted), "postRebootVerifyCompleted")]
[JsonDerivedType(typeof(MonitorCompleted), "monitorCompleted")]
[JsonDerivedType(typeof(GuideVerifyCompleted), "guideVerifyCompleted")]
[JsonDerivedType(typeof(UndoCompleted), "undoCompleted")]
[JsonDerivedType(typeof(UiConnected), "uiConnected")]
[JsonDerivedType(typeof(SessionExpired), "sessionExpired")]
[JsonDerivedType(typeof(RecoveryCompleted), "recoveryCompleted")]
[Sensitivity(Sensitivity.None)]
public abstract record SessionEvent
{
    /// <summary>ULID (idempotency key).</summary>
    public required string EventId { get; init; }

    /// <summary>When (client clock for commands; informational only).</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>Origin, stamped by the engine from the pipe identity. Not part of the JSON contract.</summary>
    [JsonIgnore]
    public EventOrigin Origin { get; init; }
}

/// <summary>
/// A user action a client may send through <see cref="Ipc.IEngineRpc.SendAsync"/>. Which origins may send which command is
/// fixed by <see cref="CommandPolicy"/>; anything that writes, consents, restarts or undoes is UI-only, and the CLI/MCP
/// can only PROPOSE (<see cref="FixSelected"/>, <see cref="UndoProposed"/>), which pops the consent dialog in the UI.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(IntakeSubmitted), "intakeSubmitted")]
[JsonDerivedType(typeof(ScanRequested), "scanRequested")]
[JsonDerivedType(typeof(RescanRequested), "rescanRequested")]
[JsonDerivedType(typeof(QuestionAnswered), "questionAnswered")]
[JsonDerivedType(typeof(FixSelected), "fixSelected")]
[JsonDerivedType(typeof(ConsentGiven), "consentGiven")]
[JsonDerivedType(typeof(FixSkipped), "fixSkipped")]
[JsonDerivedType(typeof(DeadmanAnswered), "deadmanAnswered")]
[JsonDerivedType(typeof(RestartNowClicked), "restartNowClicked")]
[JsonDerivedType(typeof(RestartLaterChosen), "restartLaterChosen")]
[JsonDerivedType(typeof(OutcomeReported), "outcomeReported")]
[JsonDerivedType(typeof(MonitorRequested), "monitorRequested")]
[JsonDerivedType(typeof(GuideStarted), "guideStarted")]
[JsonDerivedType(typeof(GuideStepCompleted), "guideStepCompleted")]
[JsonDerivedType(typeof(UndoProposed), "undoProposed")]
[JsonDerivedType(typeof(UndoRequested), "undoRequested")]
[JsonDerivedType(typeof(UndoAllRequested), "undoAllRequested")]
[JsonDerivedType(typeof(PlanImported), "planImported")]
[JsonDerivedType(typeof(AbandonRequested), "abandonRequested")]
[JsonDerivedType(typeof(ScanCancelRequested), "scanCancelRequested")]
[JsonDerivedType(typeof(MonitorMarkBadRequested), "monitorMarkBadRequested")]
public abstract record SessionCommand : SessionEvent;

/// <summary>An effect result produced by the engine itself. Never accepted from a client.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ScanCompleted), "scanCompleted")]
[JsonDerivedType(typeof(ScanFailed), "scanFailed")]
[JsonDerivedType(typeof(AnalysisCompleted), "analysisCompleted")]
[JsonDerivedType(typeof(FixApplyCompleted), "fixApplyCompleted")]
[JsonDerivedType(typeof(ResumeStarted), "resumeStarted")]
[JsonDerivedType(typeof(PostRebootVerifyCompleted), "postRebootVerifyCompleted")]
[JsonDerivedType(typeof(MonitorCompleted), "monitorCompleted")]
[JsonDerivedType(typeof(GuideVerifyCompleted), "guideVerifyCompleted")]
[JsonDerivedType(typeof(UndoCompleted), "undoCompleted")]
[JsonDerivedType(typeof(UiConnected), "uiConnected")]
[JsonDerivedType(typeof(SessionExpired), "sessionExpired")]
[JsonDerivedType(typeof(RecoveryCompleted), "recoveryCompleted")]
public abstract record EngineEvent : SessionEvent;

/// <summary>
/// Which origins may send which command (DESIGN.md section 3.1: an agent can propose but can't self-consent). The engine
/// (A4) checks this before the session machine sees a command; the fake engine does too.
/// </summary>
public static class CommandPolicy
{
    private static readonly EventOrigin[] UiOnly = [EventOrigin.Ui];
    private static readonly EventOrigin[] AnyClient = [EventOrigin.Ui, EventOrigin.Cli, EventOrigin.Mcp];

    /// <summary>Origins allowed to send <paramref name="command"/>.</summary>
    /// <param name="command">Command.</param>
    public static IReadOnlyList<EventOrigin> AllowedOrigins(SessionCommand command) => command switch
    {
        // Writes, consent, restart, undo, keeping a network change, and the player's own verdicts: the human at the PC only.
        ConsentGiven or DeadmanAnswered or RestartNowClicked or RestartLaterChosen or OutcomeReported
            or UndoRequested or UndoAllRequested or AbandonRequested or GuideStepCompleted or MonitorMarkBadRequested => UiOnly,

        // Read-only actions and proposals: any verified client. A proposal ends in a consent dialog in the UI.
        IntakeSubmitted or ScanRequested or RescanRequested or QuestionAnswered or FixSelected or FixSkipped
            or MonitorRequested or GuideStarted or UndoProposed or PlanImported or ScanCancelRequested => AnyClient,

        _ => UiOnly,
    };

    /// <summary>True when <paramref name="origin"/> may send <paramref name="command"/>.</summary>
    /// <param name="command">Command.</param>
    /// <param name="origin">Verified origin.</param>
    public static bool IsAllowed(SessionCommand command, EventOrigin origin)
        => origin != EventOrigin.Unspecified && AllowedOrigins(command).Contains(origin);
}

/// <summary>The player submitted symptoms, codes and "when?".</summary>
public sealed record IntakeSubmitted : SessionCommand
{
    /// <summary>Answers.</summary>
    public required IntakeAnswers Intake { get; init; }
}

/// <summary>Start a scan.</summary>
public sealed record ScanRequested : SessionCommand
{
    /// <summary>Mode.</summary>
    public ScanMode Mode { get; init; } = ScanMode.Full;
}

/// <summary>Cancel the running scan. The engine stops the collectors (each honours cancellation) and goes back to the phase
/// before the scan; a partial snapshot is discarded, never analysed. Ignored when no scan is running.</summary>
public sealed record ScanCancelRequested : SessionCommand;

/// <summary>The player marked the last match/segment as bad (tray, during monitoring): forwarded to
/// <see cref="Monitoring.IMonitorService.MarkBadAsync"/>. The player's own verdict, so UI-only. Ignored outside Monitoring.</summary>
public sealed record MonitorMarkBadRequested : SessionCommand;

/// <summary>Rescan and re-rank (also runs drift checks).</summary>
public sealed record RescanRequested : SessionCommand;

/// <summary>An answer to a question.</summary>
public sealed record QuestionAnswered : SessionCommand
{
    /// <summary>Question.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Option.</summary>
    public required string OptionId { get; init; }
}

/// <summary>
/// Open (UI) or propose (CLI/MCP) a fix: the engine computes a preview, moves to AwaitingConsent, stamps the proposer and
/// raises <see cref="Ipc.ConsentRequestedNotification"/> so the UI shows the consent dialog.
/// </summary>
public sealed record FixSelected : SessionCommand
{
    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Binding.</summary>
    public required FixBinding Binding { get; init; }

    /// <summary>The imported helper plan (<see cref="PlanImported.PlanId"/>) this step comes from, if any. The engine
    /// accepts it only when it equals the session's imported plan and stamps it into <see cref="Fixes.Proposer.PlanId"/>.</summary>
    [Sensitivity(Sensitivity.None)]
    public string? PlanId { get; init; }
}

/// <summary>The player clicked "Fix it" (or the second "this cannot be undone" button).</summary>
public sealed record ConsentGiven : SessionCommand
{
    /// <summary>Token minted by the click.</summary>
    public required ConsentToken Token { get; init; }
}

/// <summary>Skip a fix.</summary>
public sealed record FixSkipped : SessionCommand
{
    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }
}

/// <summary>The player answered the deadman ("Is your internet still working?").</summary>
public sealed record DeadmanAnswered : SessionCommand
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Keep the change.</summary>
    public required bool Keep { get; init; }
}

/// <summary>The player clicked "Restart now" (the ONLY path to a restart, invariant R1).</summary>
public sealed record RestartNowClicked : SessionCommand
{
    /// <summary>Token minted by the click.</summary>
    public required RestartToken Token { get; init; }
}

/// <summary>The player chose "Restart later" (resume at next sign-in).</summary>
public sealed record RestartLaterChosen : SessionCommand;

/// <summary>The player answered "Did it help?".</summary>
public sealed record OutcomeReported : SessionCommand
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Outcome.</summary>
    public required FixOutcome Outcome { get; init; }
}

/// <summary>Start watching a match.</summary>
public sealed record MonitorRequested : SessionCommand
{
    /// <summary>Purpose.</summary>
    public MonitorPurpose Purpose { get; init; } = MonitorPurpose.UserRequested;

    /// <summary>Stop after this many minutes.</summary>
    public int? Minutes { get; init; }
}

/// <summary>Start a guide.</summary>
public sealed record GuideStarted : SessionCommand
{
    /// <summary>Guide.</summary>
    public required string GuideId { get; init; }

    /// <summary>Branch.</summary>
    public string? BranchId { get; init; }
}

/// <summary>The player finished a guide step.</summary>
public sealed record GuideStepCompleted : SessionCommand
{
    /// <summary>Guide.</summary>
    public required string GuideId { get; init; }

    /// <summary>Step.</summary>
    public required int StepIndex { get; init; }
}

/// <summary>Propose an undo (CLI/MCP <c>vtdiag undo</c>): the UI shows a confirmation and sends <see cref="UndoRequested"/>.</summary>
public sealed record UndoProposed : SessionCommand
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }
}

/// <summary>Undo one attempt (UI only).</summary>
public sealed record UndoRequested : SessionCommand
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Mode (Force only after the player confirmed a conflict).</summary>
    public UndoMode Mode { get; init; } = UndoMode.IfUnchanged;
}

/// <summary>"Stop and undo everything" (UI only).</summary>
public sealed record UndoAllRequested : SessionCommand;

/// <summary>A helper plan was validated and accepted for step-by-step use (each step still needs consent).</summary>
public sealed record PlanImported : SessionCommand
{
    /// <summary>Plan.</summary>
    public required string PlanId { get; init; }
}

/// <summary>The player abandons the session (changes stay; the undo center keeps them).</summary>
public sealed record AbandonRequested : SessionCommand;

/// <summary>Scan finished.</summary>
public sealed record ScanCompleted : EngineEvent
{
    /// <summary>Snapshot.</summary>
    public required string SnapshotId { get; init; }
}

/// <summary>Scan failed.</summary>
public sealed record ScanFailed : EngineEvent
{
    /// <summary>Error code.</summary>
    public required string ErrorCode { get; init; }
}

/// <summary>Analysis finished.</summary>
public sealed record AnalysisCompleted : EngineEvent
{
    /// <summary>Analysis.</summary>
    public required string AnalysisId { get; init; }
}

/// <summary>Apply finished.</summary>
public sealed record FixApplyCompleted : EngineEvent
{
    /// <summary>Result.</summary>
    public required FixRunResult Result { get; init; }
}

/// <summary>The resume task started the engine after logon.</summary>
public sealed record ResumeStarted : EngineEvent
{
    /// <summary>Current boot.</summary>
    public required BootIdentity Boot { get; init; }
}

/// <summary>Post-reboot verification done.</summary>
public sealed record PostRebootVerifyCompleted : EngineEvent
{
    /// <summary>Results.</summary>
    public IReadOnlyList<VerifyResult> Results { get; init; } = [];
}

/// <summary>Monitoring stopped and summarized.</summary>
public sealed record MonitorCompleted : EngineEvent
{
    /// <summary>Summary.</summary>
    public required string SummaryId { get; init; }
}

/// <summary>Guide verification (for example Secure Boot now on after BIOS), from a fresh probe.</summary>
public sealed record GuideVerifyCompleted : EngineEvent
{
    /// <summary>Guide.</summary>
    public required string GuideId { get; init; }

    /// <summary>Result.</summary>
    public required Tri Result { get; init; }
}

/// <summary>Undo finished.</summary>
public sealed record UndoCompleted : EngineEvent
{
    /// <summary>Result.</summary>
    public required UndoResult Result { get; init; }
}

/// <summary>The verified UI connected (resume unregisters tasks after this).</summary>
public sealed record UiConnected : EngineEvent;

/// <summary>The session expired.</summary>
public sealed record SessionExpired : EngineEvent;

/// <summary>Crash recovery finished.</summary>
public sealed record RecoveryCompleted : EngineEvent
{
    /// <summary>Report.</summary>
    public required RecoveryReport Report { get; init; }
}

/// <summary>
/// Minted by the UI on the "Restart now" / "Restart into BIOS" click; echoes the engine's single-use
/// <see cref="Ipc.RestartPrompt.Nonce"/>. The engine checks expiry against its own prompt record, never against client
/// time. The engine has no other restart path.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record RestartToken
{
    /// <summary>ULID.</summary>
    public required string TokenId { get; init; }

    /// <summary>Session.</summary>
    public required string SessionId { get; init; }

    /// <summary>Kind.</summary>
    public required RestartKind Kind { get; init; }

    /// <summary>Echo of <see cref="Ipc.RestartPrompt.Nonce"/>.</summary>
    public required string Nonce { get; init; }

    /// <summary>When the player clicked (client clock; audit only).</summary>
    public required DateTimeOffset IssuedUtc { get; init; }
}

/// <summary>Owns the session (A4): checks <see cref="CommandPolicy"/>, computes the pure transition, persists, then runs effects.</summary>
public interface ISessionController
{
    /// <summary>Current state.</summary>
    SessionState Current { get; }

    /// <summary>Raised after every persisted transition.</summary>
    event EventHandler<SessionState>? Changed;

    /// <summary>Handles one event whose <see cref="SessionEvent.Origin"/> the engine has stamped.</summary>
    /// <param name="sessionEvent">Event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SessionState> HandleAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}

/// <summary>Conditions that gate the reboot prompt and select the resume mechanism.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RebootReadiness
{
    /// <summary>On AC power (the reboot prompt is gated on AC).</summary>
    public required Tri OnAcPower { get; init; }

    /// <summary>Battery charge.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>A Windows Update reboot is pending.</summary>
    public required Tri WindowsUpdateRebootPending { get; init; }

    /// <summary>A Windows Update install is running (blocks applies).</summary>
    public required Tri WindowsUpdateInstalling { get; init; }

    /// <summary>Fast Startup on ("Use Restart, not Shut down").</summary>
    public required Tri FastStartupEnabled { get; init; }

    /// <summary>Task Scheduler usable.</summary>
    public required Tri TaskSchedulerAvailable { get; init; }

    /// <summary>The interactive player is an administrator (false = standard user with a separate admin).</summary>
    public required Tri InteractiveUserIsAdmin { get; init; }

    /// <summary>Restart into firmware supported.</summary>
    public required Tri FirmwareRestartSupported { get; init; }

    /// <summary>Mechanism that would be used.</summary>
    public required ResumeMechanism PlannedMechanism { get; init; }

    /// <summary>Warnings for the restart screen.</summary>
    public IReadOnlyList<string> WarningKeys { get; init; } = [];
}

/// <summary>Result of registering the resume.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ResumeRegistration
{
    /// <summary>Mechanism actually registered.</summary>
    public required ResumeMechanism Mechanism { get; init; }

    /// <summary>Read back and confirmed (task found / RunOnce value present, action path under %ProgramFiles%).</summary>
    public required bool Verified { get; init; }

    /// <summary>Message when degraded ("After restart, open VTDiag from the Start menu").</summary>
    public string? FallbackMessageKey { get; init; }
}

/// <summary>
/// Reboot coordination (A4). Invariant R1: <see cref="RestartAsync"/> is the only entry to InitiateShutdownW and accepts
/// only a click-minted <see cref="RestartToken"/>; the resume code path has no reference to it.
/// </summary>
public interface IRebootCoordinator
{
    /// <summary>Reads the current boot identity.</summary>
    Fact<BootIdentity> CurrentBoot();

    /// <summary>Checks AC power, pending WU, Fast Startup, Task Scheduler, standard-user case, firmware restart support.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<RebootReadiness> CheckReadinessAsync(CancellationToken cancellationToken);

    /// <summary>Registers resume (tasks, else RunOnce UI, else manual) with an end boundary and reads it back.</summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="expiresUtc">End boundary.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ResumeRegistration> RegisterResumeAsync(string sessionId, DateTimeOffset expiresUtc, CancellationToken cancellationToken);

    /// <summary>Removes the task folder and RunOnce entry.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task UnregisterResumeAsync(CancellationToken cancellationToken);

    /// <summary>Restarts (real restart, or into firmware). Only with a valid, unexpired, single-use click token.</summary>
    /// <param name="token">Token.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RestartAsync(RestartToken token, CancellationToken cancellationToken);
}

/// <summary>
/// Restore points (A4 is the single owner; A3's FixRunner calls it before the first medium/high-risk fix of a session).
/// It only creates and verifies; enabling System Protection when it is off is a normal consented fix
/// (<c>systemProtection.enable</c>), proposed when <see cref="RestorePointStatus.SystemProtectionOff"/> comes back.
/// </summary>
public interface IRestorePointService
{
    /// <summary>Ensures a restore point exists for the session (creates and verifies it, or reports why not).</summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="reasonKey">Why.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<RestorePointInfo> EnsureAsync(string sessionId, string reasonKey, CancellationToken cancellationToken);
}

/// <summary>
/// Rebuilds a state object from the journal when both <c>current.json</c> and <c>current.bak</c> are unreadable
/// (A4 implements it for <see cref="SessionState"/>; A3's <see cref="IStateStore{T}"/> calls it).
/// </summary>
/// <typeparam name="T">State type.</typeparam>
public interface IStateRebuilder<T>
    where T : class
{
    /// <summary>Rebuilds the state, or returns null when the journal holds nothing for it.</summary>
    /// <param name="records">Journal records in order.</param>
    T? Rebuild(IReadOnlyList<JournalRecord> records);
}
