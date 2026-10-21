using System.Globalization;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;

namespace VTD.Contracts.Fixes;

/// <summary>Concrete entities a fix acts on, by binding name.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixBinding
{
    /// <summary>No entities.</summary>
    public static FixBinding Empty { get; } = new();

    /// <summary>Entities per binding name, in pick order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<EntityRef>> Entities { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<EntityRef>>.Empty;

    /// <summary>
    /// The binding hash every component uses (consent token, journal, session, plan validation):
    /// <see cref="CanonicalHash.Of"/> over <c>&lt;name&gt;[&lt;i&gt;].kind</c> = <see cref="EntityKind"/> name and
    /// <c>&lt;name&gt;[&lt;i&gt;].id</c> = <see cref="EntityRef.Id"/>, for every binding name and entity index.
    /// Display names are not part of the hash.
    /// </summary>
    public string ComputeHash()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, entities) in Entities)
        {
            for (var i = 0; i < entities.Count; i++)
            {
                map[string.Create(CultureInfo.InvariantCulture, $"{name}[{i}].kind")] = entities[i].Kind.ToString();
                map[string.Create(CultureInfo.InvariantCulture, $"{name}[{i}].id")] = entities[i].Id;
            }
        }

        return CanonicalHash.Of(map);
    }
}

/// <summary>Who proposed a fix or an undo. Shown on the consent dialog ("Suggested by …").</summary>
[Sensitivity(Sensitivity.None)]
public sealed record Proposer
{
    /// <summary>Verified origin (stamped by the engine).</summary>
    public required EventOrigin Origin { get; init; }

    /// <summary>Client-declared label, for example <c>Claude Code (MCP)</c> or <c>u/ModName</c>; plain text, unverified.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Label { get; init; }

    /// <summary>Helper plan the step came from.</summary>
    public string? PlanId { get; init; }
}

/// <summary>Result of the protected-target policy (S4/S5). Not overridable by the KB or a plan.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PolicyDecision
{
    /// <summary>Allowed.</summary>
    public required bool Allowed { get; init; }

    /// <summary>Policy rule that decided (for example <c>protected.service.vgc</c>, <c>protected.default_route_nic</c>).</summary>
    public string? RuleId { get; init; }

    /// <summary>Explanation key.</summary>
    public string? ReasonKey { get; init; }

    /// <summary>An allow decision.</summary>
    public static PolicyDecision Allow { get; } = new() { Allowed = true };
}

/// <summary>A guard evaluated with a fresh probe.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuardResult
{
    /// <summary>Probe id.</summary>
    public required string Probe { get; init; }

    /// <summary>Observed value.</summary>
    public required Tri Observed { get; init; }

    /// <summary>Required value.</summary>
    public required Tri Must { get; init; }

    /// <summary>Passed (Unknown never passes a Block guard).</summary>
    public required bool Passed { get; init; }

    /// <summary>Severity.</summary>
    public required GuardSeverity Severity { get; init; }

    /// <summary>Message key when failed.</summary>
    public required string FailKey { get; init; }
}

/// <summary>One step of a preview, in plain words, with the captured "before" and the intended "after".</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PreviewStep
{
    /// <summary>Index.</summary>
    public required int Index { get; init; }

    /// <summary>Primitive id.</summary>
    public required string Op { get; init; }

    /// <summary>Resolved arguments.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Plain-language summary key ("We'll turn off the wired network port you're not using").</summary>
    public required string SummaryKey { get; init; }

    /// <summary>Summary placeholder values.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> SummaryArgs { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Current state (probe).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Before { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Intended state.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Intended { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Reversibility of this step.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Policy decision for this step.</summary>
    public required PolicyDecision Policy { get; init; }
}

/// <summary>
/// What the consent screen shows, computed by the engine from fresh probes. The UI mints a <see cref="ConsentToken"/>
/// that echoes <see cref="BindingHash"/>, <see cref="PreviewHash"/> and <see cref="ConsentNonce"/>; the engine
/// recomputes the preview at apply and refuses if anything changed (TOCTOU guard). Previews returned to CLI/MCP clients
/// carry no nonce and are Share-redacted.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixPreview
{
    /// <summary>ULID.</summary>
    public required string PreviewId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Fix revision.</summary>
    public int FixRev { get; init; } = 1;

    /// <summary>Binding.</summary>
    public required FixBinding Binding { get; init; }

    /// <summary><see cref="FixBinding.ComputeHash"/> of the binding.</summary>
    public required string BindingHash { get; init; }

    /// <summary>Canonical hash of everything shown (steps, args, before, intended, effective risk/reversibility/reboot).</summary>
    public required string PreviewHash { get; init; }

    /// <summary>Single-use nonce the consent token must echo. Only sent to the verified UI client; null for CLI/MCP.</summary>
    public string? ConsentNonce { get; init; }

    /// <summary>Created (engine clock).</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Consent must arrive before this, on the engine clock (at most <see cref="SessionLimits.TokenLifetime"/>).</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Who proposed it (the UI itself, the CLI, an MCP agent, or a helper plan).</summary>
    public required Proposer ProposedBy { get; init; }

    /// <summary>Title key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Explanation key.</summary>
    public required string ExplainKey { get; init; }

    /// <summary>Undo explanation key.</summary>
    public required string UndoExplainKey { get; init; }

    /// <summary>"What you'll notice" key.</summary>
    public string? NoticeKey { get; init; }

    /// <summary>Side effects.</summary>
    public IReadOnlyList<string> SideEffectKeys { get; init; } = [];

    /// <summary>Effective risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>Effective reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Reboot need.</summary>
    public required RebootNeed Reboot { get; init; }

    /// <summary>Scope.</summary>
    public required FixScope Scope { get; init; }

    /// <summary>Needs the elevated engine.</summary>
    public required bool NeedsAdmin { get; init; }

    /// <summary>One-way: a second, explicit "this cannot be undone" consent is required (a separate click, see
    /// <see cref="ConsentToken.SecondConsentNonce"/>).</summary>
    public required bool RequiresSecondConsent { get; init; }

    /// <summary>A restore point will be created and verified first (first medium/high-risk fix of the session).</summary>
    public required bool RestorePointFirst { get; init; }

    /// <summary>Deadman auto-revert, when the fix has one.</summary>
    public DeadmanDef? Deadman { get; init; }

    /// <summary>Steps.</summary>
    public required IReadOnlyList<PreviewStep> Steps { get; init; }

    /// <summary>Guards.</summary>
    public IReadOnlyList<GuardResult> Guards { get; init; } = [];

    /// <summary>Everything passes: the "Fix it" button is enabled.</summary>
    public required bool CanApply { get; init; }

    /// <summary>Why not.</summary>
    public IReadOnlyList<string> BlockReasonKeys { get; init; } = [];
}

/// <summary>
/// Minted by the UI on the "Fix it" click (one fix per consent, S1). The engine accepts it only in a
/// <see cref="ConsentGiven"/> from the verified UI client (<see cref="CommandPolicy"/>), only once (nonce), only before
/// the preview's engine-side expiry, and only if the preview it recomputes now has the same hashes. The CLI, the MCP
/// host and plans can never mint consent.
/// </summary>
/// <remarks>
/// One-way fixes take two clicks: the first token (no <see cref="SecondConsentNonce"/>) makes the runner answer
/// <see cref="AttemptStatus.AwaitingSecondConsent"/> with a fresh <see cref="FixRunResult.SecondConsentNonce"/>; the
/// "this cannot be undone" click mints a second token that echoes it. Residual risk: a same-user process could drive the
/// UI with UI Automation; A6 must make the consent buttons ignore synthesized input where Windows allows it.
/// </remarks>
[Sensitivity(Sensitivity.None)]
public sealed record ConsentToken
{
    /// <summary>ULID.</summary>
    public required string TokenId { get; init; }

    /// <summary>Session.</summary>
    public required string SessionId { get; init; }

    /// <summary>Preview consented to.</summary>
    public required string PreviewId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Echo of <see cref="FixPreview.BindingHash"/>.</summary>
    public required string BindingHash { get; init; }

    /// <summary>Echo of <see cref="FixPreview.PreviewHash"/>.</summary>
    public required string PreviewHash { get; init; }

    /// <summary>Echo of <see cref="FixPreview.ConsentNonce"/>.</summary>
    public required string Nonce { get; init; }

    /// <summary>When the player clicked (client clock; audit only, never used for expiry).</summary>
    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>For one-way fixes: echo of <see cref="FixRunResult.SecondConsentNonce"/> from the second click.</summary>
    public string? SecondConsentNonce { get; init; }
}

/// <summary>Status of a fix attempt.</summary>
public enum AttemptStatus
{
    /// <summary>Steps are being applied.</summary>
    Applying = 0,

    /// <summary>All steps applied; verification pending.</summary>
    Applied = 1,

    /// <summary>Verified mechanically ("applied" is not "fixed": see the outcome).</summary>
    Verified = 2,

    /// <summary>Verification failed; auto-undo ran.</summary>
    VerifyFailed = 3,

    /// <summary>Takes effect after the reboot; post-reboot verification pending.</summary>
    AwaitingReboot = 4,

    /// <summary>Rolled back (step failure, deadman, crash recovery).</summary>
    RolledBack = 5,

    /// <summary>Undone by the player.</summary>
    Undone = 6,

    /// <summary>Undo refused: the state changed since our apply (needs the player's choice).</summary>
    UndoConflict = 7,

    /// <summary>Crash recovery could not tell; the player decides.</summary>
    InDoubt = 8,

    /// <summary>Failed before any change (policy, guard, backup, journal write).</summary>
    Failed = 9,

    /// <summary>One-way fix: nothing applied yet; waiting for the second "this cannot be undone" click.</summary>
    AwaitingSecondConsent = 10,
}

/// <summary>Status of one step.</summary>
public enum StepStatus
{
    /// <summary>Not run.</summary>
    NotRun = 0,

    /// <summary>Applied.</summary>
    Applied = 1,

    /// <summary>Failed.</summary>
    Failed = 2,

    /// <summary>Skipped (already in the intended state).</summary>
    Skipped = 3,

    /// <summary>Rolled back.</summary>
    RolledBack = 4,

    /// <summary>Denied by policy.</summary>
    PolicyDenied = 5,

    /// <summary>Undone.</summary>
    Undone = 6,
}

/// <summary>Result of one step.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record StepResult
{
    /// <summary>Index.</summary>
    public required int Index { get; init; }

    /// <summary>Primitive id.</summary>
    public required string Op { get; init; }

    /// <summary>Status.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Machine error code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Message key.</summary>
    public string? MessageKey { get; init; }
}

/// <summary>Deadman status.</summary>
public enum DeadmanStatus
{
    /// <summary>Counting down.</summary>
    Armed = 0,

    /// <summary>The player confirmed and the probe passed: change kept.</summary>
    Confirmed = 1,

    /// <summary>The player chose undo, the probe failed, or time ran out: reverted.</summary>
    Reverted = 2,
}

/// <summary>Deadman state of an attempt.</summary>
public sealed record DeadmanState
{
    /// <summary>Status.</summary>
    public required DeadmanStatus Status { get; init; }

    /// <summary>Auto-revert deadline.</summary>
    public required DateTimeOffset DeadlineUtc { get; init; }
}

/// <summary>Outcome of applying a fix.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixRunResult
{
    /// <summary>ULID of the attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Status.</summary>
    public required AttemptStatus Status { get; init; }

    /// <summary>Per-step results.</summary>
    public IReadOnlyList<StepResult> Steps { get; init; } = [];

    /// <summary>Immediate verification.</summary>
    public VerifyResult? Verify { get; init; }

    /// <summary>A reboot is needed for the change to take effect.</summary>
    public bool RebootPending { get; init; }

    /// <summary>Deadman, when armed.</summary>
    public DeadmanState? Deadman { get; init; }

    /// <summary>Set with <see cref="AttemptStatus.AwaitingSecondConsent"/>: the nonce the second click must echo.</summary>
    public string? SecondConsentNonce { get; init; }

    /// <summary>Message key for the result screen.</summary>
    public string? MessageKey { get; init; }

    /// <summary>First journal sequence number of the attempt.</summary>
    public long? FirstJournalSeq { get; init; }

    /// <summary>Last journal sequence number of the attempt.</summary>
    public long? LastJournalSeq { get; init; }
}

/// <summary>Verification status.</summary>
public enum VerifyStatus
{
    /// <summary>The change landed.</summary>
    Passed = 0,

    /// <summary>It did not (for example blocked by Tamper Protection): auto-undo partial, report honestly.</summary>
    Failed = 1,

    /// <summary>Runs after the reboot.</summary>
    DeferredUntilReboot = 2,

    /// <summary>A probe was Unknown.</summary>
    Inconclusive = 3,
}

/// <summary>One verification check.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record VerifyCheck
{
    /// <summary>Probe.</summary>
    public required string Probe { get; init; }

    /// <summary>Expected values.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Expected { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Actual values.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Actual { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Passed (Unknown when the probe failed).</summary>
    public required Tri Passed { get; init; }
}

/// <summary>Verification result.</summary>
public sealed record VerifyResult
{
    /// <summary>Attempt.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string AttemptId { get; init; }

    /// <summary>Status.</summary>
    public required VerifyStatus Status { get; init; }

    /// <summary>Checks.</summary>
    public IReadOnlyList<VerifyCheck> Checks { get; init; } = [];

    /// <summary>When.</summary>
    public required DateTimeOffset VerifiedUtc { get; init; }
}

/// <summary>Undo status.</summary>
public enum UndoStatus
{
    /// <summary>Undone; state equals the captured "before" (bit-identical for exact primitives).</summary>
    Undone = 0,

    /// <summary>Some steps undone.</summary>
    PartiallyUndone = 1,

    /// <summary>Refused: changed since our apply ("probably by Windows Update"); ask before forcing.</summary>
    Conflict = 2,

    /// <summary>One-way: nothing to click; show what was done and the restore point date.</summary>
    NotUndoable = 3,

    /// <summary>Failed.</summary>
    Failed = 4,

    /// <summary>Nothing to undo.</summary>
    NothingToUndo = 5,
}

/// <summary>Undo result.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record UndoResult
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Status.</summary>
    public required UndoStatus Status { get; init; }

    /// <summary>Steps (reverse order).</summary>
    public IReadOnlyList<StepResult> Steps { get; init; } = [];

    /// <summary>Conflicts found.</summary>
    public IReadOnlyList<DriftFinding> Conflicts { get; init; } = [];

    /// <summary>Message key.</summary>
    public string? MessageKey { get; init; }
}

/// <summary>A change VTDiag made that is no longer in place (drift watch), or an undo conflict.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DriftFinding
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Step.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Primitive.</summary>
    public required string Op { get; init; }

    /// <summary>Expected state.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Expected { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Actual state.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Actual { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Message key.</summary>
    public string? ReasonKey { get; init; }

    /// <summary>Detected.</summary>
    public required DateTimeOffset DetectedUtc { get; init; }
}

/// <summary>Phase of a running fix.</summary>
public enum FixPhase
{
    /// <summary>Policy check.</summary>
    Policy = 0,

    /// <summary>Fresh guards.</summary>
    Guards = 1,

    /// <summary>Restore point (the runner calls A4's <see cref="IRestorePointService"/>).</summary>
    RestorePoint = 2,

    /// <summary>Backup capture + journal intent.</summary>
    Backup = 3,

    /// <summary>Apply.</summary>
    Apply = 4,

    /// <summary>Verify.</summary>
    Verify = 5,

    /// <summary>Deadman countdown.</summary>
    Deadman = 6,

    /// <summary>Undo.</summary>
    Undo = 7,

    /// <summary>Done.</summary>
    Done = 8,
}

/// <summary>Progress of a running fix.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixProgress
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Step index.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Step count.</summary>
    public required int StepCount { get; init; }

    /// <summary>Phase.</summary>
    public required FixPhase Phase { get; init; }

    /// <summary>Message key.</summary>
    public string? MessageKey { get; init; }
}

/// <summary>"Did it help?"</summary>
public enum FixOutcome
{
    /// <summary>Not asked yet.</summary>
    Pending = 0,

    /// <summary>Fixed.</summary>
    Fixed = 1,

    /// <summary>Not fixed (negative evidence for its hypotheses).</summary>
    NotFixed = 2,

    /// <summary>Not sure.</summary>
    Unsure = 3,
}

/// <summary>One row of the undo center (across sessions; also the <c>.vtdscan</c> journal summary).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record AttemptSummary
{
    /// <summary>Attempt.</summary>
    public required string AttemptId { get; init; }

    /// <summary>Session.</summary>
    public required string SessionId { get; init; }

    /// <summary>Fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Title key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Status.</summary>
    public required AttemptStatus Status { get; init; }

    /// <summary>Outcome.</summary>
    public required FixOutcome Outcome { get; init; }

    /// <summary>Reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Applied.</summary>
    public required DateTimeOffset AppliedUtc { get; init; }

    /// <summary>An undo button makes sense now.</summary>
    public required bool CanUndo { get; init; }

    /// <summary>Why not.</summary>
    public string? UndoBlockedReasonKey { get; init; }

    /// <summary>Restore point made before it, if any.</summary>
    public DateTimeOffset? RestorePointUtc { get; init; }
}

/// <summary>
/// Runs fixes (A3): preview, apply with a write-ahead journal (intent fsynced before every change), verify, deadman,
/// undo, drift. One fix at a time; a second concurrent attempt is rejected.
/// </summary>
public interface IFixRunner
{
    /// <summary>Builds a preview from fresh probes.</summary>
    /// <param name="fixId">Fix id.</param>
    /// <param name="binding">Binding.</param>
    /// <param name="proposedBy">Who proposed it (stamped by the engine from the pipe identity).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FixPreview> PreviewAsync(string fixId, FixBinding binding, Proposer proposedBy, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a consented preview. For a one-way fix, a token without <see cref="ConsentToken.SecondConsentNonce"/> applies
    /// nothing and returns <see cref="AttemptStatus.AwaitingSecondConsent"/> with the nonce for the second click.
    /// </summary>
    /// <param name="consent">Token minted by the UI click.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation (honoured only between steps; a started step completes).</param>
    Task<FixRunResult> ApplyAsync(ConsentToken consent, IProgress<FixProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Runs verification (immediate, or post-reboot on resume).</summary>
    /// <param name="attemptId">Attempt.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<VerifyResult> VerifyAsync(string attemptId, CancellationToken cancellationToken);

    /// <summary>Answers the deadman ("Is your internet still working?").</summary>
    /// <param name="attemptId">Attempt.</param>
    /// <param name="keep">True = keep, false = undo now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FixRunResult> ConfirmDeadmanAsync(string attemptId, bool keep, CancellationToken cancellationToken);

    /// <summary>Undoes an attempt in reverse step order.</summary>
    /// <param name="attemptId">Attempt.</param>
    /// <param name="mode">IfUnchanged (default) or Force.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<UndoResult> UndoAsync(string attemptId, UndoMode mode, CancellationToken cancellationToken);

    /// <summary>Re-probes drift-watched attempts.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<DriftFinding>> CheckDriftAsync(CancellationToken cancellationToken);

    /// <summary>Lists attempts for the undo center.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken);
}
