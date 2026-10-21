using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;

namespace VTD.Journal;

/// <summary>
/// How A3 lays fix attempts out in the journal (A4's session rebuilder and the undo center read the same records).
/// Step records carry the step index and primitive id. Attempt-level records use <see cref="AttemptStep"/> and
/// <see cref="AttemptOp"/>:
/// <list type="bullet">
/// <item><c>Intent(-1)</c>: the attempt starts (consent token id, preview hash, proposer; <see cref="StartArgs"/> describe the
/// fix and its binding). Written after the restore point and before the first change.</item>
/// <item><c>Applied(-1)</c>: every step ran (verification pending).</item>
/// <item><c>Verified(-1)</c> / <c>Failed(-1, verify.failed)</c>: verification outcome.</item>
/// <item><c>DeadmanArmed(-1)</c> (with <c>DeadlineUtc</c>) → <c>DeadmanConfirmed(-1)</c> | <c>DeadmanReverted(-1)</c>.</item>
/// <item><c>UndoIntent(-1, reason)</c> → step undo records → <c>Undone(-1)</c> | <c>UndoFailed(-1)</c> | <c>UndoConflict(-1)</c>.
/// The reason (<see cref="Reasons"/>) tells a player undo from a rollback.</item>
/// <item><c>RecoveredApplied(-1)</c>: recovery rolled a complete apply forward (verification queued).</item>
/// </list>
/// A step that was already in its intended state is journaled as <c>Intent</c> + <c>Applied</c> with
/// <see cref="StepSkipped"/>, so "every step has an outcome" holds for recovery.
/// </summary>
public static class JournalConventions
{
    /// <summary>Step index of attempt-level records.</summary>
    public const int AttemptStep = -1;

    /// <summary>Op of attempt-level records.</summary>
    public const string AttemptOp = "fix";

    /// <summary>Error code of an <c>Applied</c> record for a step that needed no change.</summary>
    public const string StepSkipped = "step.skipped";

    /// <summary>Error code of <c>Failed(-1)</c> when verification failed.</summary>
    public const string VerifyFailed = "verify.failed";

    /// <summary>Keys of the attempt-start record's <see cref="JournalRecord.Args"/>.</summary>
    public static class StartArgs
    {
        /// <summary>Number of resolved steps.</summary>
        public const string StepCount = "stepCount";

        /// <summary>Fix revision.</summary>
        public const string FixRev = "fixRev";

        /// <summary>Title key.</summary>
        public const string TitleKey = "titleKey";

        /// <summary>Effective risk.</summary>
        public const string Risk = "risk";

        /// <summary>Effective reversibility.</summary>
        public const string Reversibility = "reversibility";

        /// <summary>Effective reboot need.</summary>
        public const string Reboot = "reboot";

        /// <summary>Scope.</summary>
        public const string Scope = "scope";

        /// <summary>Deadman seconds (absent: no deadman).</summary>
        public const string DeadmanSeconds = "deadmanSeconds";

        /// <summary>Deadman probe id.</summary>
        public const string DeadmanCheck = "deadmanCheck";

        /// <summary><c>true</c> when the fix is drift-watched.</summary>
        public const string Drift = "drift";

        /// <summary>Drift reason key.</summary>
        public const string DriftReasonKey = "driftReasonKey";

        /// <summary>Restore point time (round-trip format), when one was made for the session.</summary>
        public const string RestorePointUtc = "restorePointUtc";

        /// <summary>Prefix of the binding entries: <c>bind.&lt;name&gt;[&lt;i&gt;].kind|id|name</c>.</summary>
        public const string BindingPrefix = "bind.";
    }

    /// <summary>Reasons on <c>UndoIntent(-1)</c>.</summary>
    public static class Reasons
    {
        /// <summary>The player chose undo.</summary>
        public const string PlayerUndo = "undo.player";

        /// <summary>A step failed with <c>onFail: abort</c>.</summary>
        public const string StepFailed = "rollback.step_failed";

        /// <summary>Verification failed.</summary>
        public const string VerifyFailed = "rollback.verify_failed";

        /// <summary>The deadman reverted the change.</summary>
        public const string Deadman = "rollback.deadman";

        /// <summary>Crash recovery rolled a partial attempt back.</summary>
        public const string Recovery = "rollback.recovery";

        /// <summary>The apply was cancelled between steps.</summary>
        public const string Cancelled = "rollback.cancelled";

        /// <summary>A journal write failed mid-attempt.</summary>
        public const string JournalFailed = "rollback.journal_failed";
    }

    /// <summary>Encodes a binding into attempt-start args.</summary>
    /// <param name="binding">Binding.</param>
    public static IEnumerable<KeyValuePair<string, string>> EncodeBinding(FixBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        foreach (var (name, entities) in binding.Entities)
        {
            for (var i = 0; i < entities.Count; i++)
            {
                var prefix = string.Create(CultureInfo.InvariantCulture, $"{StartArgs.BindingPrefix}{name}[{i}].");
                yield return new(prefix + "kind", entities[i].Kind.ToString());
                yield return new(prefix + "id", entities[i].Id);
                if (entities[i].DisplayName is { } display)
                {
                    yield return new(prefix + "name", display);
                }
            }
        }
    }

    /// <summary>Decodes the binding from attempt-start args.</summary>
    /// <param name="args">Args of the attempt-start record.</param>
    public static FixBinding DecodeBinding(IReadOnlyDictionary<string, string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parts = new Dictionary<string, SortedDictionary<int, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            var m = BindingKey().Match(key);
            if (!m.Success)
            {
                continue;
            }

            var name = m.Groups["n"].Value;
            var index = int.Parse(m.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (!parts.TryGetValue(name, out var byIndex))
            {
                parts[name] = byIndex = [];
            }

            if (!byIndex.TryGetValue(index, out var fields))
            {
                byIndex[index] = fields = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            fields[m.Groups["f"].Value] = value;
        }

        var entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal);
        foreach (var (name, byIndex) in parts)
        {
            var list = new List<EntityRef>();
            foreach (var fields in byIndex.Values)
            {
                if (fields.TryGetValue("kind", out var kind) && Enum.TryParse<EntityKind>(kind, ignoreCase: false, out var k) && fields.TryGetValue("id", out var id))
                {
                    list.Add(new EntityRef { Kind = k, Id = id, DisplayName = fields.GetValueOrDefault("name") });
                }
            }

            entities[name] = list;
        }

        return new FixBinding { Entities = entities };
    }

    private static Regex BindingKey() => BindingKeyRegex;

    private static readonly Regex BindingKeyRegex = new(@"^bind\.(?<n>[^\[\]]+)\[(?<i>[0-9]{1,4})\]\.(?<f>kind|id|name)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>The journal records of one step of one attempt.</summary>
public sealed class StepLog
{
    internal StepLog(int index, string op)
    {
        Index = index;
        Op = op;
    }

    /// <summary>Step index.</summary>
    public int Index { get; }

    /// <summary>Primitive id.</summary>
    public string Op { get; }

    /// <summary>Resolved arguments (from the intent).</summary>
    public IReadOnlyDictionary<string, string> Args { get; private set; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>The intent record (null when the step only has a failure record, for example a failed capture).</summary>
    public JournalRecord? Intent { get; private set; }

    /// <summary>The captured before-state.</summary>
    public StateCapture? Before => Intent?.Before;

    /// <summary>The outcome of the apply (Applied, Failed, RecoveredNotApplied, RecoveredApplied, InDoubt), or null while in flight.</summary>
    public JournalRecord? ApplyOutcome { get; private set; }

    /// <summary>The latest undo intent.</summary>
    public JournalRecord? UndoIntent { get; private set; }

    /// <summary>The outcome of the latest undo (Undone, UndoFailed, UndoConflict, InDoubt), or null while in flight.</summary>
    public JournalRecord? UndoOutcome { get; private set; }

    /// <summary>An intent with no apply outcome (crash during the change).</summary>
    public bool ApplyInFlight => Intent is not null && ApplyOutcome is null;

    /// <summary>An undo intent with no outcome (crash during the undo).</summary>
    public bool UndoInFlight => UndoIntent is not null && UndoOutcome is null;

    /// <summary>The step was journaled as already in its intended state.</summary>
    public bool Skipped => ApplyOutcome is { Phase: JournalPhase.Applied } a && string.Equals(a.ErrorCode, JournalConventions.StepSkipped, StringComparison.Ordinal);

    /// <summary>The latest undo succeeded.</summary>
    public bool Undone => UndoOutcome?.Phase == JournalPhase.Undone;

    /// <summary>The state VTDiag left behind (the probe after apply), when known.</summary>
    public StateCapture? ExpectedAfter => ApplyOutcome?.After;

    internal void Add(JournalRecord r)
    {
        switch (r.Phase)
        {
            case JournalPhase.Intent:
                Intent = r;
                Args = r.Args;
                ApplyOutcome = null;
                break;
            case JournalPhase.Applied or JournalPhase.Failed or JournalPhase.RecoveredNotApplied or JournalPhase.RecoveredApplied:
                ApplyOutcome = r;
                if (Intent is null)
                {
                    Args = r.Args;
                }

                break;
            case JournalPhase.InDoubt:
                if (UndoIntent is not null && UndoOutcome is null)
                {
                    UndoOutcome = r;
                }
                else
                {
                    ApplyOutcome = r;
                }

                break;
            case JournalPhase.UndoIntent:
                UndoIntent = r;
                UndoOutcome = null;
                break;
            case JournalPhase.Undone or JournalPhase.UndoFailed or JournalPhase.UndoConflict:
                UndoOutcome = r;
                break;
            default:
                break;
        }
    }
}

/// <summary>All journal records of one fix attempt, folded.</summary>
public sealed class AttemptLog
{
    private readonly List<JournalRecord> _records = [];
    private readonly List<JournalRecord> _marks = [];
    private readonly SortedDictionary<int, StepLog> _steps = [];

    internal AttemptLog(JournalRecord first)
    {
        AttemptId = first.AttemptId;
        SessionId = first.SessionId;
        FixId = first.FixId;
    }

    /// <summary>Attempt.</summary>
    public string AttemptId { get; }

    /// <summary>Session.</summary>
    public string SessionId { get; }

    /// <summary>Fix.</summary>
    public string FixId { get; }

    /// <summary>The attempt-start record.</summary>
    public JournalRecord? Start { get; private set; }

    /// <summary>Every record, in sequence order.</summary>
    public IReadOnlyList<JournalRecord> Records => _records;

    /// <summary>Attempt-level records other than the start, in order.</summary>
    public IReadOnlyList<JournalRecord> Marks => _marks;

    /// <summary>Steps by index.</summary>
    public IReadOnlyList<StepLog> Steps => [.. _steps.Values];

    /// <summary>When the attempt started.</summary>
    public DateTimeOffset StartedUtc => Start?.Utc ?? _records[0].Utc;

    /// <summary>Declared step count, when the start record is there.</summary>
    public int? StepCount => Start is not null && Start.Args.TryGetValue(JournalConventions.StartArgs.StepCount, out var s)
        && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>An attempt-start arg.</summary>
    /// <param name="key">Key from <see cref="JournalConventions.StartArgs"/>.</param>
    public string? StartArg(string key) => Start is not null && Start.Args.TryGetValue(key, out var v) ? v : null;

    /// <summary>The binding recorded at start.</summary>
    public FixBinding Binding => Start is null ? FixBinding.Empty : JournalConventions.DecodeBinding(Start.Args);

    /// <summary>The last attempt-level record with one of <paramref name="phases"/>.</summary>
    /// <param name="phases">Phases.</param>
    public JournalRecord? LastMark(params JournalPhase[] phases) => _marks.LastOrDefault(m => phases.Contains(m.Phase));

    /// <summary>Every step ran (an <c>Applied(-1)</c> or a roll-forward mark exists).</summary>
    public bool AllApplied => _marks.Any(m => m.Phase is JournalPhase.Applied or JournalPhase.RecoveredApplied);

    /// <summary>Deadman status from the marks (null: never armed).</summary>
    public DeadmanStatus? Deadman => LastMark(JournalPhase.DeadmanArmed, JournalPhase.DeadmanConfirmed, JournalPhase.DeadmanReverted)?.Phase switch
    {
        JournalPhase.DeadmanArmed => DeadmanStatus.Armed,
        JournalPhase.DeadmanConfirmed => DeadmanStatus.Confirmed,
        JournalPhase.DeadmanReverted => DeadmanStatus.Reverted,
        _ => null,
    };

    /// <summary>Deadline of the latest arming.</summary>
    public DateTimeOffset? DeadmanDeadline => LastMark(JournalPhase.DeadmanArmed)?.DeadlineUtc;

    /// <summary>The latest attempt-level undo intent and its outcome (Undone, UndoFailed, UndoConflict), if any.</summary>
    public (JournalRecord? Intent, JournalRecord? Outcome) LastUndo
    {
        get
        {
            JournalRecord? intent = null;
            JournalRecord? outcome = null;
            foreach (var m in _marks)
            {
                if (m.Phase == JournalPhase.UndoIntent)
                {
                    intent = m;
                    outcome = null;
                }
                else if (intent is not null && m.Phase is JournalPhase.Undone or JournalPhase.UndoFailed or JournalPhase.UndoConflict)
                {
                    outcome = m;
                }
            }

            return (intent, outcome);
        }
    }

    internal void Add(JournalRecord r)
    {
        _records.Add(r);
        if (r.StepIndex == JournalConventions.AttemptStep)
        {
            if (r.Phase == JournalPhase.Intent && Start is null)
            {
                Start = r;
            }
            else
            {
                _marks.Add(r);
            }

            return;
        }

        if (!_steps.TryGetValue(r.StepIndex, out var step))
        {
            _steps[r.StepIndex] = step = new StepLog(r.StepIndex, r.Op);
        }

        step.Add(r);
    }
}

/// <summary>Folds journal records into attempts.</summary>
public static class AttemptHistory
{
    /// <summary>Groups records (any order) into attempts, each in sequence order; attempts ordered by first record.</summary>
    /// <param name="records">Records.</param>
    public static IReadOnlyList<AttemptLog> Build(IEnumerable<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var byAttempt = new Dictionary<string, AttemptLog>(StringComparer.Ordinal);
        var order = new List<AttemptLog>();
        foreach (var r in records.OrderBy(r => r.Seq))
        {
            if (string.IsNullOrEmpty(r.AttemptId))
            {
                continue;
            }

            if (!byAttempt.TryGetValue(r.AttemptId, out var log))
            {
                byAttempt[r.AttemptId] = log = new AttemptLog(r);
                order.Add(log);
            }

            log.Add(r);
        }

        return order;
    }
}
