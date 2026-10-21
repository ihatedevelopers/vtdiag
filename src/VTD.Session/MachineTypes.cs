using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;

namespace VTD.Session;

/// <summary>
/// Read-only facts the runtime looked up BEFORE the transition (a preview computed from fresh probes, the consent and
/// restart pre-checks against the engine's own records, the current boot, ...). The machine stays a pure function of
/// (state, event, context).
/// </summary>
public sealed record MachineContext
{
    /// <summary>Engine clock.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary><see cref="FixSelected"/>: the preview the engine computed; <see cref="FixApplyCompleted"/>: the preview of
    /// the attempt (for its binding hash and reversibility).</summary>
    public FixPreview? Preview { get; init; }

    /// <summary>The verified proposer (origin from the pipe; plan id only when it equals the imported plan).</summary>
    public Proposer? Proposer { get; init; }

    /// <summary><see cref="ConsentGiven"/>: why the engine's own pre-check refuses the token (null = acceptable).</summary>
    public string? ConsentRefusal { get; init; }

    /// <summary><see cref="RestartNowClicked"/>: why the restart-prompt ledger refuses the token (null = acceptable).</summary>
    public string? RestartRefusal { get; init; }

    /// <summary>Current boot (restart and resume decisions); null = could not be read.</summary>
    public BootIdentity? CurrentBoot { get; init; }

    /// <summary>A Windows Update reboot is pending right now.</summary>
    public bool WindowsUpdateRebootPending { get; init; }

    /// <summary>A new session id, used when a command starts over from a finished session.</summary>
    public string? NewSessionId { get; init; }

    /// <summary>KB version for a new session.</summary>
    public string? KbVersion { get; init; }

    /// <summary><see cref="PlanImported"/>: the engine validated this plan id (valid) through ImportPlanAsync.</summary>
    public bool PlanValidated { get; init; }

    /// <summary><see cref="GuideStarted"/>/<see cref="GuideStepCompleted"/>: the guide from the KB.</summary>
    public GuideDef? Guide { get; init; }

    /// <summary><see cref="UndoProposed"/>/<see cref="UndoRequested"/>: the attempt is known (this session or the undo center).</summary>
    public bool UndoTargetKnown { get; init; }

    /// <summary><see cref="UndoProposed"/>: fix id of the attempt.</summary>
    public string? UndoTargetFixId { get; init; }

    /// <summary><see cref="FixApplyCompleted"/>: hypotheses the fix was proposed for (negative evidence target).</summary>
    public IReadOnlyList<string> HypothesisIds { get; init; } = [];

    /// <summary><see cref="RecoveryCompleted"/>: the undo-center rows of THIS session, read after recovery (the journal is the
    /// source of truth). The machine adopts rows the state does not list (an attempt that finished while the engine went
    /// down) and takes each listed attempt's status from its row, so the session never loses or misreports a change.</summary>
    public IReadOnlyList<AttemptSummary> AttemptRows { get; init; } = [];

    /// <summary>A monitor implementation is available.</summary>
    public bool MonitorAvailable { get; init; }
}

/// <summary>Result of <see cref="SessionMachine.Next"/>.</summary>
public sealed record Transition
{
    private Transition(SessionState state, IReadOnlyList<SessionEffect> effects, bool changed, string? rejection)
    {
        State = state;
        Effects = effects;
        Changed = changed;
        Rejection = rejection;
    }

    /// <summary>The next state (revision and update time are stamped by the controller when it persists).</summary>
    public SessionState State { get; }

    /// <summary>Effects to run after the state is persisted, in order.</summary>
    public IReadOnlyList<SessionEffect> Effects { get; }

    /// <summary>The state changed and must be persisted.</summary>
    public bool Changed { get; }

    /// <summary>Machine refusal code; nothing changes and the caller gets an error.</summary>
    public string? Rejection { get; }

    /// <summary>A change.</summary>
    /// <param name="state">Next state.</param>
    /// <param name="effects">Effects.</param>
    public static Transition To(SessionState state, params SessionEffect[] effects) => new(state, effects, true, null);

    /// <summary>A change.</summary>
    /// <param name="state">Next state.</param>
    /// <param name="effects">Effects.</param>
    public static Transition To(SessionState state, IReadOnlyList<SessionEffect> effects) => new(state, effects, true, null);

    /// <summary>No state change, but effects (for example forwarding "mark bad").</summary>
    /// <param name="state">Unchanged state.</param>
    /// <param name="effects">Effects.</param>
    public static Transition Only(SessionState state, params SessionEffect[] effects) => new(state, effects, false, null);

    /// <summary>Nothing to do (an event that does not apply in this phase, ignored by design).</summary>
    /// <param name="state">Unchanged state.</param>
    public static Transition Ignore(SessionState state) => new(state, [], false, null);

    /// <summary>A refusal.</summary>
    /// <param name="state">Unchanged state.</param>
    /// <param name="code">Code.</param>
    public static Transition Reject(SessionState state, string code) => new(state, [], false, code);
}

/// <summary>Machine codes (refusals, reasons, errors) the session layer produces. The UI maps them to text.</summary>
public static class SessionCodes
{
    /// <summary>The command is not valid in the current phase.</summary>
    public const string InvalidPhase = "session.invalid_phase";

    /// <summary>The verified origin may not send this command (<see cref="CommandPolicy"/>).</summary>
    public const string NotAllowed = "command.not_allowed";

    /// <summary>The event was not stamped with a verified origin.</summary>
    public const string Unstamped = "command.unstamped";

    /// <summary>The session expired.</summary>
    public const string Expired = "session.expired";

    /// <summary>The session needs the player's decision first.</summary>
    public const string NeedsAttention = "session.needs_attention";

    /// <summary>Fixes need the elevated engine.</summary>
    public const string LimitedMode = "fixes.limited_mode";

    /// <summary>The preview could not be computed.</summary>
    public const string PreviewFailed = "fix.preview_failed";

    /// <summary>Another proposal is waiting for the player's consent.</summary>
    public const string ConsentPending = "consent.pending";

    /// <summary>The token is for another session.</summary>
    public const string ConsentWrongSession = "consent.wrong_session";

    /// <summary>The token is not for the preview awaiting consent.</summary>
    public const string ConsentPreviewChanged = "consent.preview_changed";

    /// <summary>Consent in limited mode.</summary>
    public const string ConsentLimited = "consent.limited_mode";

    /// <summary>The restart token is for another session or kind.</summary>
    public const string RestartTokenInvalid = "restart.token_invalid";

    /// <summary>The current boot could not be read, so a restart could not be told apart from Fast Startup.</summary>
    public const string RestartBootUnknown = "restart.boot_unknown";

    /// <summary>No deadman is waiting for this attempt.</summary>
    public const string DeadmanNotPending = "deadman.not_pending";

    /// <summary>The deadman reverted the change.</summary>
    public const string DeadmanReverted = "deadman.reverted";

    /// <summary>At most three questions per session.</summary>
    public const string QuestionLimit = "question.limit";

    /// <summary>Unknown attempt.</summary>
    public const string UnknownAttempt = "undo.unknown_attempt";

    /// <summary>Undo did not complete (RolledBack ⇒ NeedsAttention).</summary>
    public const string UndoIncomplete = "undo.incomplete";

    /// <summary>The guide is not in the KB.</summary>
    public const string UnknownGuide = "guide.unknown";

    /// <summary>The guide's check did not pass.</summary>
    public const string GuideVerifyFailed = "guide.verify_failed";

    /// <summary>The guide's check could not be evaluated.</summary>
    public const string GuideVerifyUnknown = "guide.verify_unknown";

    /// <summary>The plan was not validated by this engine.</summary>
    public const string PlanNotValidated = "plan.not_validated";

    /// <summary>The monitor is not available in this build.</summary>
    public const string MonitorUnavailable = "monitor.unavailable";

    /// <summary>Too many resume attempts.</summary>
    public const string ResumeTooManyAttempts = "resume.too_many_attempts";

    /// <summary>The PC did not really restart (Fast Startup "Shut down", or no restart yet).</summary>
    public const string ResumeNotRealRestart = "resume.not_real_restart";

    /// <summary>The restart was requested but did not happen.</summary>
    public const string RestartDidNotHappen = "restart.did_not_happen";

    /// <summary>The engine stopped during a change; recovery resolved it.</summary>
    public const string RecoveryInterrupted = "recovery.interrupted";

    /// <summary>Recovery could not tell whether a change is in place.</summary>
    public const string RecoveryInDoubt = "recovery.in_doubt";

    /// <summary>A fix attempt ended in doubt.</summary>
    public const string FixInDoubt = "fix.in_doubt";

    /// <summary>Default restart reason.</summary>
    public const string RestartReasonFix = "restart.fix_needs_restart";

    /// <summary>Restart into firmware for a guide.</summary>
    public const string RestartReasonFirmware = "restart.firmware";

    /// <summary>The engine stopped during a scan.</summary>
    public const string ScanInterrupted = "scan.interrupted";
}
