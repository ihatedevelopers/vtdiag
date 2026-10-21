using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;

namespace VTD.Session;

/// <summary>
/// Something the session machine asks the runtime to do AFTER the new state is persisted (design_B.md section 6.1). Effects
/// are data: the pure <see cref="SessionMachine"/> returns them, the <see cref="SessionController"/> persists the state,
/// then runs them through <see cref="ISessionEnvironment.RunEffectAsync"/>; every result comes back as an event.
/// </summary>
public abstract record SessionEffect
{
    /// <summary>Runs in the background (scan, apply, monitor): the command that caused it returns before it finishes.</summary>
    public virtual bool LongRunning => false;
}

/// <summary>Run a scan, then <see cref="ScanCompleted"/> or <see cref="ScanFailed"/>.</summary>
/// <param name="Mode">Scan breadth.</param>
/// <param name="CheckDrift">Also re-probe drift-watched attempts (rescan).</param>
public sealed record StartScanEffect(ScanMode Mode, bool CheckDrift) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Cancel the running scan (its partial snapshot is discarded).</summary>
public sealed record CancelScanEffect : SessionEffect;

/// <summary>Analyze a snapshot, then <see cref="AnalysisCompleted"/>.</summary>
/// <param name="SnapshotId">Snapshot.</param>
public sealed record AnalyzeEffect(string SnapshotId) : SessionEffect;

/// <summary>Apply a consented fix through <see cref="IFixRunner.ApplyAsync"/>, then <see cref="FixApplyCompleted"/>.</summary>
/// <param name="Token">Token minted by the UI click (already pre-checked).</param>
public sealed record ApplyFixEffect(ConsentToken Token) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Answer the deadman (<see cref="IFixRunner.ConfirmDeadmanAsync"/>), then <see cref="FixApplyCompleted"/>.</summary>
/// <param name="AttemptId">Attempt.</param>
/// <param name="Keep">Keep (false = revert now).</param>
public sealed record ConfirmDeadmanEffect(string AttemptId, bool Keep) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Start the deadman countdown (ticks to the UI; <see cref="DeadmanExpired"/> at the deadline).</summary>
/// <param name="AttemptId">Attempt.</param>
/// <param name="DeadlineUtc">Deadline (engine clock).</param>
public sealed record WatchDeadmanEffect(string AttemptId, DateTimeOffset DeadlineUtc) : SessionEffect;

/// <summary>Undo one attempt, then <see cref="UndoCompleted"/>.</summary>
/// <param name="AttemptId">Attempt.</param>
/// <param name="Mode">Mode.</param>
public sealed record UndoEffect(string AttemptId, UndoMode Mode) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Register the resume (tasks, else RunOnce UI, else manual), then <see cref="ResumeRegistered"/>.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="ExpiresUtc">End boundary.</param>
/// <param name="ThenRestart">Set only on the "Restart now" path: the click-minted token the restart will use.</param>
public sealed record RegisterResumeEffect(string SessionId, DateTimeOffset ExpiresUtc, RestartToken? ThenRestart) : SessionEffect;

/// <summary>Remove the resume tasks and RunOnce entry, then <see cref="ResumeUnregistered"/>.</summary>
public sealed record UnregisterResumeEffect : SessionEffect;

/// <summary>
/// Restart the PC (<see cref="IRebootCoordinator.RestartAsync"/>). The machine emits it ONLY from a <see cref="ResumeRegistered"/>
/// that carries the token of a <see cref="RestartNowClicked"/> accepted from the verified UI, and only after
/// <see cref="RestartInfo.ShutdownInitiatedUtc"/> is persisted (invariant R1).
/// </summary>
/// <param name="Token">Click-minted token.</param>
public sealed record RestartEffect(RestartToken Token) : SessionEffect;

/// <summary>Verify attempts after a reboot (<see cref="IFixRunner.VerifyAsync"/>), then <see cref="PostRebootVerifyCompleted"/>.</summary>
/// <param name="AttemptIds">Attempts.</param>
public sealed record PostRebootVerifyEffect(IReadOnlyList<string> AttemptIds) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Watch a match, then <see cref="MonitorCompleted"/>.</summary>
/// <param name="Purpose">Purpose.</param>
/// <param name="Minutes">Stop after.</param>
public sealed record StartMonitorEffect(MonitorPurpose Purpose, int? Minutes) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Forward "mark last match as bad" to the monitor.</summary>
public sealed record MarkBadEffect : SessionEffect;

/// <summary>Verify a finished guide with fresh probes, then <see cref="GuideVerifyCompleted"/>.</summary>
/// <param name="GuideId">Guide.</param>
/// <param name="BranchId">Branch.</param>
public sealed record VerifyGuideEffect(string GuideId, string? BranchId) : SessionEffect
{
    /// <inheritdoc />
    public override bool LongRunning => true;
}

/// <summary>Tell the UI a decision only the player can make is waiting (<see cref="ConsentRequestedNotification"/>).</summary>
/// <param name="Kind">Kind.</param>
/// <param name="FixId">Fix.</param>
/// <param name="PreviewId">Preview (Fix, SecondConsent).</param>
/// <param name="AttemptId">Attempt (Undo).</param>
/// <param name="ProposedBy">Who asked.</param>
/// <param name="SecondConsentNonce">Second-click nonce (SecondConsent).</param>
public sealed record NotifyConsentEffect(ConsentKind Kind, string FixId, string? PreviewId, string? AttemptId, Proposer ProposedBy, string? SecondConsentNonce) : SessionEffect;

/// <summary>Complete <see cref="IEngineRpc.WaitForOutcomeAsync"/> waiters for a preview (null = declined, skipped or replaced).</summary>
/// <param name="PreviewId">Preview.</param>
/// <param name="Result">Result.</param>
public sealed record ResolveOutcomeEffect(string PreviewId, FixRunResult? Result) : SessionEffect;
