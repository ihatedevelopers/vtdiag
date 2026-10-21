using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Journal;

namespace VTD.Fixes.Runner;

/// <content>
/// Crash recovery (design_B.md section 6.5), run by the engine at every start before any RPC is accepted:
/// <list type="number">
/// <item>Every step intent without an outcome is resolved by probing: state equals the captured before → not applied;
/// equals the intended → applied (rolled forward); anything else → in doubt (the player decides).</item>
/// <item>Every step undo without an outcome is resolved the same way.</item>
/// <item>Then per attempt: a deadman that was armed and never confirmed is UNDONE (a crash or reboot inside the countdown
/// never keeps a network-cutting change); an interrupted undo is finished; a partial multi-step apply is rolled BACK (all or
/// nothing); a complete apply whose verification never ran is rolled forward and reported for verification; a verification
/// failure whose rollback never ran is rolled back.</item>
/// </list>
/// Recovery is idempotent: every decision is journaled, so a second run finds nothing to do.
/// </content>
public sealed partial class FixRunner : IRecovery
{
    /// <inheritdoc />
    public async Task<RecoveryReport> RunAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<RecoveryItem>();

            // Phase 1: resolve in-flight step records by probing.
            foreach (var log in await LoadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var step in log.Steps)
                {
                    if (step.ApplyInFlight && step.Before is { } before)
                    {
                        items.Add(await ResolveInFlightApplyAsync(log, step, before).ConfigureAwait(false));
                    }
                    else if (step.UndoInFlight && step.Before is { } b)
                    {
                        await ResolveInFlightUndoAsync(log, step, b).ConfigureAwait(false);
                    }
                }
            }

            // Phase 2: attempt-level decisions on the updated journal.
            foreach (var log in await LoadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (log.Start is null)
                {
                    continue;
                }

                var (undoIntent, undoOutcome) = log.LastUndo;
                var verdict = log.LastMark(JournalPhase.Verified, JournalPhase.Failed);
                // A network-cutting change is kept only after a confirmed deadman: armed and never confirmed, or a crash
                // between the change and the arming, both end undone.
                var deadmanDeclared = log.StartArg(JournalConventions.StartArgs.DeadmanSeconds) is not null;
                if (log.Deadman == DeadmanStatus.Armed
                    || (deadmanDeclared && log.Deadman is null && UnitsOf(log).Count > 0 && !(undoIntent is not null && undoOutcome?.Phase == JournalPhase.Undone)))
                {
                    var (ok, steps, _) = await UndoUnitsAsync(log.SessionId, log.AttemptId, log.FixId, UnitsOf(log), UndoMode.Force, JournalConventions.Reasons.Deadman).ConfigureAwait(false);
                    await TryAppend(Rec(log.SessionId, log.AttemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.DeadmanReverted)).ConfigureAwait(false);
                    items.AddRange(ItemsOf(log, steps, ok));
                }
                else if (undoIntent is not null && undoOutcome is null)
                {
                    var (ok, steps, _) = await UndoUnitsAsync(log.SessionId, log.AttemptId, log.FixId, UnitsOf(log), UndoMode.IfUnchanged, undoIntent.ErrorCode ?? JournalConventions.Reasons.Recovery).ConfigureAwait(false);
                    items.AddRange(ItemsOf(log, steps, ok));
                }
                else if (undoIntent is null && verdict is null && !log.AllApplied)
                {
                    var stepCount = log.StepCount ?? int.MaxValue;
                    var done = log.Steps.Where(s => s.ApplyOutcome?.Phase is JournalPhase.Applied or JournalPhase.RecoveredApplied).ToList();
                    var troubled = log.Steps.Any(s => s.ApplyOutcome?.Phase is JournalPhase.Failed or JournalPhase.InDoubt);
                    if (!troubled && done.Count == stepCount)
                    {
                        var reboot = log.StartArg(JournalConventions.StartArgs.Reboot) ?? nameof(RebootNeed.None);
                        await TryAppend(Rec(log.SessionId, log.AttemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.RecoveredApplied,
                            new Dictionary<string, string>(StringComparer.Ordinal) { [JournalConventions.StartArgs.Reboot] = reboot })).ConfigureAwait(false);
                        items.Add(new RecoveryItem { AttemptId = log.AttemptId, FixId = log.FixId, StepIndex = JournalConventions.AttemptStep, Op = JournalConventions.AttemptOp, Resolution = RecoveryResolution.RolledForward });
                    }
                    else
                    {
                        // Partial: roll back what is definitely ours; a step in doubt stays for the player to decide.
                        var inDoubt = log.Steps.Any(s => s.ApplyOutcome?.Phase == JournalPhase.InDoubt && AttemptState.InEffect(s));
                        var units = UnitsOf(log).Where(u => log.Steps.First(s => s.Index == u.Index).ApplyOutcome?.Phase != JournalPhase.InDoubt).ToList();
                        if (units.Count > 0)
                        {
                            var (ok, steps, _) = await UndoUnitsAsync(log.SessionId, log.AttemptId, log.FixId, units, UndoMode.IfUnchanged, JournalConventions.Reasons.Recovery).ConfigureAwait(false);
                            items.AddRange(ItemsOf(log, steps, ok));
                        }
                        else if (!inDoubt)
                        {
                            await TryAppend(Rec(log.SessionId, log.AttemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Failed, error: JournalConventions.Reasons.Recovery)).ConfigureAwait(false);
                        }
                    }
                }
                else if (verdict?.Phase == JournalPhase.Failed && string.Equals(verdict.ErrorCode, JournalConventions.VerifyFailed, StringComparison.Ordinal) && undoIntent is null)
                {
                    var (ok, steps, _) = await UndoUnitsAsync(log.SessionId, log.AttemptId, log.FixId, UnitsOf(log), UndoMode.IfUnchanged, JournalConventions.Reasons.VerifyFailed).ConfigureAwait(false);
                    items.AddRange(ItemsOf(log, steps, ok));
                }
            }

            var needsAttention = items.Any(i => i.Resolution == RecoveryResolution.InDoubt);
            return new RecoveryReport
            {
                RanUtc = _clock.GetUtcNow(),
                Items = items,
                NeedsAttention = needsAttention,
                MessageKey = items.Count == 0 ? null : needsAttention ? "recovery.needs_attention" : "recovery.put_back",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecoveryItem> ResolveInFlightApplyAsync(AttemptLog log, StepLog step, StateCapture before)
    {
        var op = _registry.FindBase(step.Op);
        var args = new OpArgs { Values = step.Args };
        var current = op is null ? null : await TryProbe(op, args).ConfigureAwait(false);
        StateCapture? intended = null;
        try
        {
            intended = op?.Intended(args, before);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or FormatException)
        {
            intended = null;
        }

        JournalPhase phase;
        RecoveryResolution resolution;
        if (op is not null && current is not null && op.Same(args, current, before))
        {
            (phase, resolution) = (JournalPhase.RecoveredNotApplied, RecoveryResolution.NotApplied);
        }
        else if (op is not null && current is not null && intended is not null && op.Same(args, current, intended))
        {
            (phase, resolution) = (JournalPhase.RecoveredApplied, RecoveryResolution.RolledForward);
        }
        else
        {
            (phase, resolution) = (JournalPhase.InDoubt, RecoveryResolution.InDoubt);
        }

        await TryAppend(Rec(log.SessionId, log.AttemptId, log.FixId, step.Index, step.Op, phase, step.Args, before, current, "recovery")).ConfigureAwait(false);
        return new RecoveryItem { AttemptId = log.AttemptId, FixId = log.FixId, StepIndex = step.Index, Op = step.Op, Resolution = resolution, Before = before, Current = current, Intended = intended };
    }

    private async Task ResolveInFlightUndoAsync(AttemptLog log, StepLog step, StateCapture before)
    {
        var op = _registry.FindBase(step.Op);
        var args = new OpArgs { Values = step.Args };
        var current = op is null ? null : await TryProbe(op, args).ConfigureAwait(false);
        var phase = op is not null && current is not null && op.Same(args, current, before)
            ? JournalPhase.Undone
            : op is not null && current is not null && step.ExpectedAfter is { } expected && op.Same(args, current, expected)
                ? JournalPhase.UndoFailed // the undo never happened; the attempt-level pass retries it
                : JournalPhase.InDoubt;
        await TryAppend(Rec(log.SessionId, log.AttemptId, log.FixId, step.Index, step.Op, phase, step.Args, before, current, "recovery")).ConfigureAwait(false);
    }

    private static IEnumerable<RecoveryItem> ItemsOf(AttemptLog log, IReadOnlyList<StepResult> steps, bool ok)
        => steps.Select(s => new RecoveryItem
        {
            AttemptId = log.AttemptId,
            FixId = log.FixId,
            StepIndex = s.Index,
            Op = s.Op,
            Resolution = s.Status == StepStatus.Undone ? RecoveryResolution.RolledBack : RecoveryResolution.InDoubt,
            Before = log.Steps.FirstOrDefault(x => x.Index == s.Index)?.Before,
        }).DefaultIfEmpty(new RecoveryItem
        {
            AttemptId = log.AttemptId,
            FixId = log.FixId,
            StepIndex = JournalConventions.AttemptStep,
            Op = JournalConventions.AttemptOp,
            Resolution = ok ? RecoveryResolution.RolledBack : RecoveryResolution.InDoubt,
        });
}
