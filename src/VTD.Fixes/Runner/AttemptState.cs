using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Journal;

namespace VTD.Fixes.Runner;

/// <summary>Derives an attempt's status and undo-ability from its journal records (the journal is the source of truth).</summary>
public static class AttemptState
{
    /// <summary>The attempt's status.</summary>
    /// <param name="log">Attempt log.</param>
    public static AttemptStatus Status(AttemptLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Steps.Any(s => (s.ApplyOutcome?.Phase == JournalPhase.InDoubt || s.UndoOutcome?.Phase == JournalPhase.InDoubt) && InEffect(s)))
        {
            return AttemptStatus.InDoubt;
        }

        var (undoIntent, undoOutcome) = log.LastUndo;
        if (undoIntent is not null)
        {
            if (undoOutcome is null || undoOutcome.Phase == JournalPhase.UndoFailed)
            {
                return AttemptStatus.InDoubt;
            }

            if (undoOutcome.Phase == JournalPhase.UndoConflict)
            {
                return AttemptStatus.UndoConflict;
            }

            return undoIntent.ErrorCode switch
            {
                JournalConventions.Reasons.PlayerUndo => AttemptStatus.Undone,
                JournalConventions.Reasons.VerifyFailed => AttemptStatus.VerifyFailed,
                _ => AttemptStatus.RolledBack,
            };
        }

        var verdict = log.LastMark(JournalPhase.Verified, JournalPhase.Failed);
        if (verdict?.Phase == JournalPhase.Verified)
        {
            return AttemptStatus.Verified;
        }

        if (verdict?.Phase == JournalPhase.Failed)
        {
            return string.Equals(verdict.ErrorCode, JournalConventions.VerifyFailed, StringComparison.Ordinal) ? AttemptStatus.VerifyFailed : AttemptStatus.Failed;
        }

        if (log.AllApplied)
        {
            return RebootPending(log) ? AttemptStatus.AwaitingReboot : AttemptStatus.Applied;
        }

        return log.Start is null ? AttemptStatus.Failed : AttemptStatus.Applying;
    }

    /// <summary>True when the applied attempt waits for a restart (the <c>Applied(-1)</c> mark, or the start record, says so).</summary>
    /// <param name="log">Attempt log.</param>
    public static bool RebootPending(AttemptLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var mark = log.LastMark(JournalPhase.Applied, JournalPhase.RecoveredApplied);
        var word = mark is not null && mark.Args.TryGetValue(JournalConventions.StartArgs.Reboot, out var r) ? r : log.StartArg(JournalConventions.StartArgs.Reboot);
        return string.Equals(word, nameof(RebootNeed.Required), StringComparison.Ordinal);
    }

    /// <summary>The attempt's effective reversibility (from the start record).</summary>
    /// <param name="log">Attempt log.</param>
    public static Reversibility ReversibilityOf(AttemptLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return Enum.TryParse<Reversibility>(log.StartArg(JournalConventions.StartArgs.Reversibility), out var r) ? r : Reversibility.Exact;
    }

    /// <summary>True when the step changed something that is still in effect (applied, recovered as applied, in doubt,
    /// or failed after a partial change), and was not undone since.</summary>
    /// <param name="step">Step.</param>
    public static bool InEffect(StepLog step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Undone || step.Intent is null)
        {
            return false;
        }

        return step.ApplyOutcome switch
        {
            null => true, // in flight: may have happened
            { Phase: JournalPhase.Applied } => !step.Skipped,
            { Phase: JournalPhase.RecoveredApplied or JournalPhase.InDoubt } => true,
            { Phase: JournalPhase.Failed } f => f.After is not null && step.Before is not null && f.After.Hash != step.Before.Hash,
            _ => false,
        };
    }

    /// <summary>Whether the undo button makes sense, and why not.</summary>
    /// <param name="log">Attempt log.</param>
    public static (bool CanUndo, string? Reason) Undoable(AttemptLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var rev = ReversibilityOf(log);
        if (rev == Reversibility.OneWay)
        {
            return (false, "undo.one_way");
        }

        if (rev == Reversibility.SessionOnly)
        {
            return (false, "undo.session_only");
        }

        var status = Status(log);
        if (status is AttemptStatus.Undone or AttemptStatus.RolledBack or AttemptStatus.VerifyFailed or AttemptStatus.Failed)
        {
            return (false, "undo.nothing");
        }

        if (status == AttemptStatus.Applying)
        {
            return (false, "undo.in_progress");
        }

        return log.Steps.Any(InEffect) ? (true, null) : (false, "undo.nothing");
    }
}
