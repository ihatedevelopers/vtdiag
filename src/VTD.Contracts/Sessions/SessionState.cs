using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Monitoring;

namespace VTD.Contracts.Sessions;

/// <summary>Session phase (design_B.md section 4.4 / 6.1).</summary>
public enum Phase
{
    /// <summary>Choosing symptoms and codes.</summary>
    Intake = 0,

    /// <summary>Scanning.</summary>
    Scanning = 1,

    /// <summary>Analysis ready: health checklist and suspects.</summary>
    Analyzed = 2,

    /// <summary>Showing the next fix.</summary>
    Proposing = 3,

    /// <summary>Consent screen for one fix.</summary>
    AwaitingConsent = 4,

    /// <summary>Applying.</summary>
    Applying = 5,

    /// <summary>Verifying (and deadman countdown).</summary>
    Verifying = 6,

    /// <summary>The player must click "Restart now" (or "later").</summary>
    AwaitingRestart = 7,

    /// <summary>Persisted right before the restart call; resume tasks registered.</summary>
    RestartRequested = 8,

    /// <summary>After logon: recovery and post-reboot verification.</summary>
    Resuming = 9,

    /// <summary>A manual/BIOS/ISP step is in progress (may include a firmware restart).</summary>
    GuidedStep = 10,

    /// <summary>"Did it help?"</summary>
    AwaitingOutcome = 11,

    /// <summary>Watching a match.</summary>
    Monitoring = 12,

    /// <summary>Done (changes kept; report).</summary>
    Completed = 13,

    /// <summary>Everything undone at the player's request.</summary>
    RolledBack = 14,

    /// <summary>Abandoned by the player.</summary>
    Abandoned = 15,

    /// <summary>Expired, too many resume attempts, or recovery in doubt: "Here's what we changed: [Undo all] [Keep]".</summary>
    NeedsAttention = 16,
}

/// <summary>Session invariants fixed by DESIGN.md (reboot-resume, consent).</summary>
public static class SessionLimits
{
    /// <summary>Resume attempts are capped (incremented before any other work).</summary>
    public const int MaxResumeAttempts = 3;

    /// <summary>A session expires 7 days after creation.</summary>
    public static TimeSpan Expiry { get; } = TimeSpan.FromDays(7);

    /// <summary>A consent or restart token is valid for at most 5 minutes.</summary>
    public static TimeSpan TokenLifetime { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Default deadman countdown.</summary>
    public static TimeSpan DeadmanDefault { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum free space on the system drive before any apply (MB).</summary>
    public const int MinFreeDiskMb = 200;
}

/// <summary>
/// The persisted session (<c>%ProgramData%\VTDiag\session\current.json</c>, atomic replace + <c>.bak</c>, rebuildable
/// from the journal). The controller persists the new state (revision + 1) BEFORE executing any effect.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record SessionState
{
    /// <summary>Current schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>ULID.</summary>
    public required string SessionId { get; init; }

    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Revision, +1 on every persisted transition.</summary>
    public required long Revision { get; init; }

    /// <summary>Phase.</summary>
    public required Phase Phase { get; init; }

    /// <summary>Created.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Last persisted.</summary>
    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Expires (created + 7 days).</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Elevated or limited (limited sessions are never persisted to %ProgramData%).</summary>
    public required PrivilegeMode Privilege { get; init; }

    /// <summary>KB version pinned for the whole session.</summary>
    public required string KbVersion { get; init; }

    /// <summary>Answers.</summary>
    public IntakeAnswers Intake { get; init; } = IntakeAnswers.Empty;

    /// <summary>Latest snapshot.</summary>
    public string? SnapshotId { get; init; }

    /// <summary>Latest analysis.</summary>
    public string? AnalysisId { get; init; }

    /// <summary>Questions asked (at most 3).</summary>
    public int QuestionsAsked { get; init; }

    /// <summary>Fix attempts in this session.</summary>
    public IReadOnlyList<FixAttempt> Attempts { get; init; } = [];

    /// <summary>Fix being proposed or consented.</summary>
    public string? CurrentFixId { get; init; }

    /// <summary>Preview awaiting consent.</summary>
    public string? CurrentPreviewId { get; init; }

    /// <summary>Attempt being applied or verified.</summary>
    public string? CurrentAttemptId { get; init; }

    /// <summary>Who proposed the fix or undo now awaiting consent (UI, CLI, MCP agent, helper plan).</summary>
    public Proposer? CurrentProposal { get; init; }

    /// <summary>Fixes the player skipped.</summary>
    public IReadOnlyList<string> SkippedFixIds { get; init; } = [];

    /// <summary>Guide in progress.</summary>
    public GuideProgress? Guide { get; init; }

    /// <summary>Restart in progress.</summary>
    public RestartInfo? Restart { get; init; }

    /// <summary>Resume bookkeeping.</summary>
    public required ResumeInfo Resume { get; init; }

    /// <summary>Restore point of this session.</summary>
    public RestorePointInfo? RestorePoint { get; init; }

    /// <summary>Monitoring run.</summary>
    public MonitorRunInfo? Monitor { get; init; }

    /// <summary>Imported helper plan.</summary>
    public string? ImportedPlanId { get; init; }

    /// <summary>SID of the interactive player as established by the engine from the pipe (never a client claim).</summary>
    [Sensitivity(Sensitivity.Identifier)]
    public string? PlayerSid { get; init; }

    /// <summary>Why NeedsAttention.</summary>
    public string? NeedsAttentionReasonKey { get; init; }

    /// <summary>Last machine error code.</summary>
    public string? LastErrorCode { get; init; }
}

/// <summary>One fix attempt in a session.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixAttempt
{
    /// <summary>ULID.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Fix revision.</summary>
    public int FixRev { get; init; } = 1;

    /// <summary>Binding hash.</summary>
    public required string BindingHash { get; init; }

    /// <summary>Status.</summary>
    public required AttemptStatus Status { get; init; }

    /// <summary>Outcome.</summary>
    public FixOutcome Outcome { get; init; } = FixOutcome.Pending;

    /// <summary>Hypotheses it was applied for (negative evidence target when NotFixed).</summary>
    public IReadOnlyList<string> HypothesisIds { get; init; } = [];

    /// <summary>Reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Reboot pending.</summary>
    public bool RebootPending { get; init; }

    /// <summary>Applied.</summary>
    public DateTimeOffset? AppliedUtc { get; init; }

    /// <summary>Verified.</summary>
    public DateTimeOffset? VerifiedUtc { get; init; }

    /// <summary>Plan that suggested it.</summary>
    public string? PlanId { get; init; }
}

/// <summary>Restart kind.</summary>
public enum RestartKind
{
    /// <summary>A real restart (never hybrid shutdown).</summary>
    Normal = 0,

    /// <summary>Restart into firmware settings ("Restart into BIOS").</summary>
    ToFirmware = 1,
}

/// <summary>How the app comes back after a restart.</summary>
public enum ResumeMechanism
{
    /// <summary>Nothing registered.</summary>
    None = 0,

    /// <summary>Two tasks: engine at Highest, UI at Limited (15 s delay), both with a 7-day end boundary.</summary>
    ScheduledTasks = 1,

    /// <summary>HKCU RunOnce that starts the unelevated UI, which asks for UAC once (Task Scheduler disabled, or
    /// standard user with a separate admin account).</summary>
    RunOnceUi = 2,

    /// <summary>"After restart, open VTDiag from the Start menu."</summary>
    Manual = 3,
}

/// <summary>Restart bookkeeping.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RestartInfo
{
    /// <summary>Kind.</summary>
    public required RestartKind Kind { get; init; }

    /// <summary>Why ("This change only works after a restart").</summary>
    public required string ReasonKey { get; init; }

    /// <summary>Boot at the time of the request; resume compares it to tell a real reboot from Fast Startup.</summary>
    public required BootIdentity BootAtRequest { get; init; }

    /// <summary>Requested.</summary>
    public required DateTimeOffset RequestedUtc { get; init; }

    /// <summary>Persisted right before the restart API was called (null = the player chose "later" or it never ran).</summary>
    public DateTimeOffset? ShutdownInitiatedUtc { get; init; }

    /// <summary>A Windows Update reboot was already pending (distinguishes "my reboot" from "WU's reboot").</summary>
    public bool WindowsUpdateRebootPendingAtRequest { get; init; }

    /// <summary>Attempts verified after the reboot.</summary>
    public IReadOnlyList<string> AttemptsAwaitingPostRebootVerify { get; init; } = [];
}

/// <summary>Resume bookkeeping.</summary>
public sealed record ResumeInfo
{
    /// <summary>Resume attempts so far (incremented and persisted before any other work).</summary>
    public int Attempts { get; init; }

    /// <summary>Cap (<see cref="SessionLimits.MaxResumeAttempts"/>).</summary>
    public int MaxAttempts { get; init; } = SessionLimits.MaxResumeAttempts;

    /// <summary>Registered mechanism.</summary>
    public ResumeMechanism Mechanism { get; init; } = ResumeMechanism.None;

    /// <summary>Tasks or RunOnce are registered (must be false in every terminal phase).</summary>
    public bool Registered { get; init; }

    /// <summary>Last boot seen by a resume.</summary>
    public BootIdentity? LastSeenBoot { get; init; }

    /// <summary>Last resume attempt.</summary>
    public DateTimeOffset? LastAttemptUtc { get; init; }
}

/// <summary>Guide progress.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuideProgress
{
    /// <summary>Guide id.</summary>
    public required string GuideId { get; init; }

    /// <summary>Branch.</summary>
    public string? BranchId { get; init; }

    /// <summary>Step index.</summary>
    public int StepIndex { get; init; }

    /// <summary>Waiting for a restart into firmware and back.</summary>
    public bool AwaitingFirmwareRestart { get; init; }

    /// <summary>Started.</summary>
    public required DateTimeOffset StartedUtc { get; init; }
}

/// <summary>Restore point status.</summary>
public enum RestorePointStatus
{
    /// <summary>Not needed yet (no medium/high-risk fix so far).</summary>
    NotNeeded = 0,

    /// <summary>Created and verified to exist.</summary>
    Created = 1,

    /// <summary>Windows would not make one.</summary>
    Failed = 2,

    /// <summary>The player chose to continue without one ("Our own undo still works.").</summary>
    ContinuedWithout = 3,

    /// <summary>System Protection is off (enabling it is a consented step).</summary>
    SystemProtectionOff = 4,
}

/// <summary>Restore point info.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RestorePointInfo
{
    /// <summary>Status.</summary>
    public required RestorePointStatus Status { get; init; }

    /// <summary>Sequence number.</summary>
    public long? SequenceNumber { get; init; }

    /// <summary>Created.</summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>Message key.</summary>
    public string? MessageKey { get; init; }
}

/// <summary>Monitoring run info.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MonitorRunInfo
{
    /// <summary>Purpose.</summary>
    public required MonitorPurpose Purpose { get; init; }

    /// <summary>Started.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Summary id once stopped.</summary>
    public string? SummaryId { get; init; }

    /// <summary>Baseline summary id (first watched match), for before/after comparison.</summary>
    public string? BaselineSummaryId { get; init; }
}
