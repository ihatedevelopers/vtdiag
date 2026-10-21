using VTD.Contracts.Fixes;

namespace VTD.Contracts.Journaling;

/// <summary>Phase of a journal record (design_B.md section 4.3).</summary>
public enum JournalPhase
{
    /// <summary>About to change; carries the backup. Fsynced BEFORE the change.</summary>
    Intent = 0,

    /// <summary>Change applied; carries the probed after-state.</summary>
    Applied = 1,

    /// <summary>Verified.</summary>
    Verified = 2,

    /// <summary>Apply or verify failed.</summary>
    Failed = 3,

    /// <summary>About to undo.</summary>
    UndoIntent = 4,

    /// <summary>Undone.</summary>
    Undone = 5,

    /// <summary>Undo failed.</summary>
    UndoFailed = 6,

    /// <summary>Undo refused because the state changed since our apply.</summary>
    UndoConflict = 7,

    /// <summary>Recovery: the change never happened (crash before change).</summary>
    RecoveredNotApplied = 8,

    /// <summary>Recovery: the change is in place (crash after change); verify queued.</summary>
    RecoveredApplied = 9,

    /// <summary>Recovery: state matches neither before nor intended; the player decides.</summary>
    InDoubt = 10,

    /// <summary>A network-cutting change is applied and its deadman is counting down (carries
    /// <see cref="JournalRecord.DeadlineUtc"/>). Fsynced before the countdown starts.</summary>
    DeadmanArmed = 11,

    /// <summary>The player confirmed and the probe passed: the change is kept.</summary>
    DeadmanConfirmed = 12,

    /// <summary>The deadman reverted the change (player said undo, probe failed, or time ran out).</summary>
    DeadmanReverted = 13,
}

/// <summary>
/// One journal record. On disk: one JSON line with a CRC32 of the JSON body appended (format owned by A3), in
/// <c>%ProgramData%\VTDiag\journal\&lt;sessionId&gt;.jsonl</c>. The journal alone is enough to list and undo every change.
/// </summary>
public sealed record JournalRecord
{
    /// <summary>Monotonic sequence number, assigned by <see cref="IJournal.AppendAsync"/> (send 0).</summary>
    public long Seq { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>Session.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string SessionId { get; init; }

    /// <summary>Attempt.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string AttemptId { get; init; }

    /// <summary>Fix id.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string FixId { get; init; }

    /// <summary>Step index.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Primitive id.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string Op { get; init; }

    /// <summary>Phase.</summary>
    public required JournalPhase Phase { get; init; }

    /// <summary>Resolved arguments.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Captured before-state (on Intent).</summary>
    public StateCapture? Before { get; init; }

    /// <summary>Probed after-state (on Applied, Undone...).</summary>
    public StateCapture? After { get; init; }

    /// <summary>Boot id at the time of the record.</summary>
    public uint? BootId { get; init; }

    /// <summary>App version.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string AppVersion { get; init; }

    /// <summary>KB version.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string KbVersion { get; init; }

    /// <summary>Machine error code.</summary>
    [Sensitivity(Sensitivity.None)]
    public string? ErrorCode { get; init; }

    /// <summary>Deadman deadline (engine clock), on <see cref="JournalPhase.DeadmanArmed"/>.</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>Consent token that authorized the attempt (audit trail), on <see cref="JournalPhase.Intent"/>.</summary>
    [Sensitivity(Sensitivity.None)]
    public string? ConsentTokenId { get; init; }

    /// <summary>Preview hash the player consented to, on <see cref="JournalPhase.Intent"/>.</summary>
    [Sensitivity(Sensitivity.None)]
    public string? PreviewHash { get; init; }

    /// <summary>Who proposed the change (UI, CLI, MCP agent, helper plan), on <see cref="JournalPhase.Intent"/>.</summary>
    public Proposer? ProposedBy { get; init; }
}

/// <summary>Thrown when a journal record cannot be made durable (disk full, AV lock). The change is aborted BEFORE it starts.</summary>
public sealed class JournalWriteException : IOException
{
    /// <summary>Creates the exception.</summary>
    public JournalWriteException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    public JournalWriteException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public JournalWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The write-ahead journal (A3).</summary>
public interface IJournal
{
    /// <summary>Appends a record and makes it durable (fsync) before returning. Throws <see cref="JournalWriteException"/>.</summary>
    /// <param name="record">Record (Seq ignored).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The record with its assigned <see cref="JournalRecord.Seq"/>.</returns>
    ValueTask<JournalRecord> AppendAsync(JournalRecord record, CancellationToken cancellationToken);

    /// <summary>Reads one session's records in order; torn or CRC-failing tail records are skipped and reported by recovery.</summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    IAsyncEnumerable<JournalRecord> ReadAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Reads all sessions (undo center).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    IAsyncEnumerable<JournalRecord> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>Where loaded state came from.</summary>
public enum StateLoadSource
{
    /// <summary>Nothing stored yet.</summary>
    Empty = 0,

    /// <summary><c>current.json</c>.</summary>
    Primary = 1,

    /// <summary><c>current.bak</c> (primary failed to parse or validate).</summary>
    Backup = 2,

    /// <summary>Rebuilt from the journal (both files failed).</summary>
    RebuiltFromJournal = 3,
}

/// <summary>Result of loading state.</summary>
/// <typeparam name="T">State type.</typeparam>
public sealed record StateLoadResult<T>
    where T : class
{
    /// <summary>State, null when <see cref="StateLoadSource.Empty"/>.</summary>
    public T? State { get; init; }

    /// <summary>Source.</summary>
    public required StateLoadSource Source { get; init; }

    /// <summary>Issues (machine codes).</summary>
    [Sensitivity(Sensitivity.None)]
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>
/// Atomic state store: write <c>.tmp</c> → FlushFileBuffers → File.Replace(tmp, current, current.bak) (A3). When both
/// files are unreadable it rebuilds through an injected <see cref="Sessions.IStateRebuilder{T}"/> (A4 owns the one for
/// the session).
/// </summary>
/// <typeparam name="T">State type.</typeparam>
public interface IStateStore<T>
    where T : class
{
    /// <summary>Loads with the <c>.bak</c> / journal fallback chain.</summary>
    StateLoadResult<T> Load();

    /// <summary>Saves atomically and durably.</summary>
    /// <param name="state">State.</param>
    void Save(T state);
}

/// <summary>How recovery resolved an in-flight operation.</summary>
public enum RecoveryResolution
{
    /// <summary>Crash before the change: nothing to do.</summary>
    NotApplied = 0,

    /// <summary>Crash after the change: kept, verification queued.</summary>
    RolledForward = 1,

    /// <summary>Partial multi-step fix: rolled back (all or nothing).</summary>
    RolledBack = 2,

    /// <summary>Neither before nor intended: NeedsAttention, the player chooses.</summary>
    InDoubt = 3,
}

/// <summary>One recovered operation.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RecoveryItem
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Step.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Primitive.</summary>
    public required string Op { get; init; }

    /// <summary>Resolution.</summary>
    public required RecoveryResolution Resolution { get; init; }

    /// <summary>Before.</summary>
    public StateCapture? Before { get; init; }

    /// <summary>Current.</summary>
    public StateCapture? Current { get; init; }

    /// <summary>Intended.</summary>
    public StateCapture? Intended { get; init; }
}

/// <summary>Recovery report (shown as "Your PC turned off while we were changing something. We put it back.").</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RecoveryReport
{
    /// <summary>When recovery ran.</summary>
    public required DateTimeOffset RanUtc { get; init; }

    /// <summary>Items.</summary>
    public IReadOnlyList<RecoveryItem> Items { get; init; } = [];

    /// <summary>The session file was rebuilt (from .bak or the journal).</summary>
    public bool SessionRebuilt { get; init; }

    /// <summary>Something needs the player's decision.</summary>
    public bool NeedsAttention { get; init; }

    /// <summary>Message key.</summary>
    public string? MessageKey { get; init; }
}

/// <summary>
/// Runs at every engine start, before any RPC is accepted (A3). Rules: an Intent/UndoIntent with no terminal successor is
/// resolved by probing (not applied / rolled forward / in doubt; partial multi-step fixes roll back), and any
/// <see cref="JournalPhase.DeadmanArmed"/> without a <see cref="JournalPhase.DeadmanConfirmed"/> is UNDONE (a crash or
/// reboot inside the countdown must never keep a network-cutting change).
/// </summary>
public interface IRecovery
{
    /// <summary>Recovers.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<RecoveryReport> RunAsync(CancellationToken cancellationToken);
}
