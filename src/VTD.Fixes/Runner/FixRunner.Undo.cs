using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Fixes.Ops;
using VTD.Journal;

namespace VTD.Fixes.Runner;

/// <content>Verify, deadman, undo, drift and the undo center list.</content>
public sealed partial class FixRunner
{
    /// <inheritdoc />
    public async Task<VerifyResult> VerifyAsync(string attemptId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var log = await LoadAttemptAsync(attemptId, cancellationToken).ConfigureAwait(false);
            var fix = _kb.FindFix(log.FixId) ?? throw new KeyNotFoundException("The fix of this attempt is not in the current KB.");
            var plan = FixPlanner.Plan(fix, log.Binding, _registry);
            var verify = await RunVerifyAsync(attemptId, plan, postReboot: true).ConfigureAwait(false);
            if (verify.Status == VerifyStatus.Passed)
            {
                await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Verified)).ConfigureAwait(false);
            }
            else if (verify.Status == VerifyStatus.Failed)
            {
                await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Failed, error: JournalConventions.VerifyFailed)).ConfigureAwait(false);
                await RollbackAsync(log.SessionId, attemptId, log.FixId, UnitsOf(log), JournalConventions.Reasons.VerifyFailed).ConfigureAwait(false);
            }

            return verify;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<FixRunResult> ConfirmDeadmanAsync(string attemptId, bool keep, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveDeadmanAsync(attemptId, keep, expiredByTimer: false).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UndoResult> UndoAsync(string attemptId, UndoMode mode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var log = await LoadAttemptAsync(attemptId, cancellationToken).ConfigureAwait(false);
            var rev = AttemptState.ReversibilityOf(log);
            if (rev == Reversibility.OneWay)
            {
                return new UndoResult { AttemptId = attemptId, Status = UndoStatus.NotUndoable, MessageKey = "undo.one_way" };
            }

            if (rev == Reversibility.SessionOnly)
            {
                return new UndoResult { AttemptId = attemptId, Status = UndoStatus.NotUndoable, MessageKey = "undo.session_only" };
            }

            var units = UnitsOf(log);
            if (units.Count == 0)
            {
                return new UndoResult { AttemptId = attemptId, Status = UndoStatus.NothingToUndo, MessageKey = "undo.nothing" };
            }

            var armed = Disarm(attemptId);

            // All or nothing: when anything changed since our apply, touch nothing and ask the player first.
            if (mode == UndoMode.IfUnchanged)
            {
                var conflicts = new List<DriftFinding>();
                foreach (var u in units)
                {
                    var op = _registry.FindBase(u.Op);
                    var current = op is null ? null : await TryProbe(op, u.OpArgs).ConfigureAwait(false);
                    if (op is null || current is null)
                    {
                        continue;
                    }

                    if (!op.Same(u.OpArgs, current, u.Before) && u.ExpectedAfter is not null && !op.Same(u.OpArgs, current, u.ExpectedAfter))
                    {
                        conflicts.Add(Finding(attemptId, log.FixId, u.Index, u.Op, u.ExpectedAfter, current, "undo.changed_since"));
                    }
                }

                if (conflicts.Count > 0)
                {
                    await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.UndoIntent, error: JournalConventions.Reasons.PlayerUndo)).ConfigureAwait(false);
                    await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.UndoConflict)).ConfigureAwait(false);
                    if (armed is not null)
                    {
                        Arm(attemptId, armed.Deadline, armed.Check);
                    }

                    return new UndoResult { AttemptId = attemptId, Status = UndoStatus.Conflict, Conflicts = conflicts, MessageKey = "undo.conflict" };
                }
            }

            var (ok, steps, anyUndone) = await UndoUnitsAsync(log.SessionId, attemptId, log.FixId, units, mode, JournalConventions.Reasons.PlayerUndo).ConfigureAwait(false);
            if (armed is not null || log.Deadman == DeadmanStatus.Armed)
            {
                await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.DeadmanReverted)).ConfigureAwait(false);
            }

            return new UndoResult
            {
                AttemptId = attemptId,
                Status = ok ? UndoStatus.Undone : anyUndone ? UndoStatus.PartiallyUndone : UndoStatus.Failed,
                Steps = steps,
                MessageKey = ok ? "undo.done" : "undo.failed",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriftFinding>> CheckDriftAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var findings = new List<DriftFinding>();
            foreach (var log in await LoadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = AttemptState.Status(log);
                if (log.StartArg(JournalConventions.StartArgs.Drift) != "true"
                    || status is not (AttemptStatus.Verified or AttemptStatus.Applied or AttemptStatus.AwaitingReboot)
                    || log.Deadman == DeadmanStatus.Armed)
                {
                    continue;
                }

                var reason = log.StartArg(JournalConventions.StartArgs.DriftReasonKey) ?? "drift.setting_reverted";
                foreach (var step in log.Steps.Where(AttemptState.InEffect))
                {
                    if (step.ExpectedAfter is not { } expected || _registry.FindBase(step.Op) is not { } op)
                    {
                        continue;
                    }

                    var args = new OpArgs { Values = step.Args };
                    var current = await TryProbe(op, args).ConfigureAwait(false);
                    if (current is not null && !op.Same(args, current, expected))
                    {
                        findings.Add(Finding(log.AttemptId, log.FixId, step.Index, step.Op, expected, current, reason));
                    }
                }
            }

            return findings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken)
    {
        var logs = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<AttemptSummary>();
        foreach (var log in logs)
        {
            var (canUndo, why) = AttemptState.Undoable(log);
            DateTimeOffset? rp = log.StartArg(JournalConventions.StartArgs.RestorePointUtc) is { } s
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
            list.Add(new AttemptSummary
            {
                AttemptId = log.AttemptId,
                SessionId = log.SessionId,
                FixId = log.FixId,
                TitleKey = log.StartArg(JournalConventions.StartArgs.TitleKey) ?? _kb.FindFix(log.FixId)?.TitleKey ?? log.FixId,
                Status = AttemptState.Status(log),
                Outcome = FixOutcome.Pending,
                Reversibility = AttemptState.ReversibilityOf(log),
                AppliedUtc = log.StartedUtc,
                CanUndo = canUndo,
                UndoBlockedReasonKey = why,
                RestorePointUtc = rp,
            });
        }

        return [.. list.OrderBy(a => a.AppliedUtc)];
    }

    // ------------------------------------------------------------------ internals

    private async Task<VerifyResult> RunVerifyAsync(string attemptId, FixPlan plan, bool postReboot)
    {
        var checks = new List<VerifyCheck>();
        foreach (var v in plan.Verify)
        {
            if (!postReboot && v.Def.When == VerifyTiming.PostReboot)
            {
                continue;
            }

            if (v.Op is { } op)
            {
                var state = await TryProbe(op, new OpArgs { Values = v.Args }).ConfigureAwait(false);
                if (state is null)
                {
                    checks.Add(new VerifyCheck { Probe = v.Def.Probe, Expected = v.Def.Expect, Passed = Tri.Unknown });
                    continue;
                }

                var actual = v.Def.Expect.Keys.ToDictionary(k => k, state.Get, StringComparer.Ordinal);
                var passed = v.Def.Expect.All(kv => string.Equals(state.Get(kv.Key), kv.Value, StringComparison.Ordinal));
                checks.Add(new VerifyCheck { Probe = v.Def.Probe, Expected = v.Def.Expect, Actual = actual, Passed = passed ? Tri.True : Tri.False });
            }
            else
            {
                var observed = await ProbeTri(v.Def.Probe, v.Args, CancellationToken.None).ConfigureAwait(false);
                var want = v.Def.Expect.TryGetValue("state", out var w) ? w : "true";
                var passed = observed switch
                {
                    Tri.Unknown => Tri.Unknown,
                    _ => string.Equals(observed.ToString(), want, StringComparison.OrdinalIgnoreCase) ? Tri.True : Tri.False,
                };
                checks.Add(new VerifyCheck
                {
                    Probe = v.Def.Probe,
                    Expected = v.Def.Expect,
                    Actual = new Dictionary<string, string>(StringComparer.Ordinal) { ["state"] = observed.ToString().ToLowerInvariant() },
                    Passed = passed,
                });
            }
        }

        var status = checks.Any(c => c.Passed == Tri.False) ? VerifyStatus.Failed
            : checks.Any(c => c.Passed != Tri.True) ? VerifyStatus.Inconclusive
            : VerifyStatus.Passed;
        return new VerifyResult { AttemptId = attemptId, Status = status, Checks = checks, VerifiedUtc = _clock.GetUtcNow() };
    }

    private async Task<FixRunResult> ResolveDeadmanAsync(string attemptId, bool keep, bool expiredByTimer)
    {
        var log = await LoadAttemptAsync(attemptId, CancellationToken.None).ConfigureAwait(false);
        var armedEntry = Disarm(attemptId);
        var deadline = armedEntry?.Deadline ?? log.DeadmanDeadline ?? _clock.GetUtcNow();
        if (log.Deadman != DeadmanStatus.Armed)
        {
            return ResultOf(log, null);
        }

        var expired = _clock.GetUtcNow() >= deadline;
        if (keep && !expired)
        {
            var check = armedEntry?.Check ?? log.StartArg(JournalConventions.StartArgs.DeadmanCheck) ?? GuardIds.InternetReachable;
            var observed = await ProbeTri(check, NoArgs, CancellationToken.None).ConfigureAwait(false);
            if (observed == Tri.True)
            {
                if (await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.DeadmanConfirmed)).ConfigureAwait(false))
                {
                    return ResultOf(log, new DeadmanState { Status = DeadmanStatus.Confirmed, DeadlineUtc = deadline }) with { MessageKey = "deadman.kept" };
                }
            }
        }

        var message = expiredByTimer || expired ? "deadman.expired" : keep ? "deadman.probe_failed" : "deadman.player_undo";
        var (ok, steps, _) = await UndoUnitsAsync(log.SessionId, attemptId, log.FixId, UnitsOf(log), UndoMode.IfUnchanged, JournalConventions.Reasons.Deadman).ConfigureAwait(false);
        await TryAppend(Rec(log.SessionId, attemptId, log.FixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.DeadmanReverted)).ConfigureAwait(false);
        return new FixRunResult
        {
            AttemptId = attemptId,
            FixId = log.FixId,
            Status = ok ? AttemptStatus.RolledBack : AttemptStatus.InDoubt,
            Steps = steps,
            Deadman = new DeadmanState { Status = DeadmanStatus.Reverted, DeadlineUtc = deadline },
            MessageKey = ok ? message : "fix.rollback_failed",
        };
    }

    private FixRunResult ResultOf(AttemptLog log, DeadmanState? deadman) => new()
    {
        AttemptId = log.AttemptId,
        FixId = log.FixId,
        Status = AttemptState.Status(log),
        Steps = [.. log.Steps.Select(s => new StepResult
        {
            Index = s.Index,
            Op = s.Op,
            Status = s.Undone ? StepStatus.Undone : s.Skipped ? StepStatus.Skipped : s.ApplyOutcome?.Phase == JournalPhase.Applied ? StepStatus.Applied : s.ApplyOutcome?.Phase == JournalPhase.Failed ? StepStatus.Failed : StepStatus.NotRun,
            ErrorCode = s.ApplyOutcome?.ErrorCode,
        })],
        RebootPending = AttemptState.RebootPending(log),
        Deadman = deadman ?? (log.Deadman is { } st && log.DeadmanDeadline is { } d ? new DeadmanState { Status = st, DeadlineUtc = d } : null),
        FirstJournalSeq = log.Records.Count > 0 ? log.Records[0].Seq : null,
        LastJournalSeq = log.Records.Count > 0 ? log.Records[^1].Seq : null,
    };

    private void Arm(string attemptId, DateTimeOffset deadline, string check)
    {
        lock (_sync)
        {
            if (_deadmans.Remove(attemptId, out var old))
            {
                old.Timer.Dispose();
            }

            var due = deadline - _clock.GetUtcNow();
            if (due < TimeSpan.Zero)
            {
                due = TimeSpan.Zero;
            }

            var timer = _clock.CreateTimer(OnDeadmanTimer, attemptId, due, Timeout.InfiniteTimeSpan);
            _deadmans[attemptId] = new DeadmanEntry(deadline, check, timer);
        }
    }

    private DeadmanEntry? Disarm(string attemptId)
    {
        lock (_sync)
        {
            if (_deadmans.Remove(attemptId, out var entry))
            {
                entry.Timer.Dispose();
                return entry;
            }

            return null;
        }
    }

    private void OnDeadmanTimer(object? state)
    {
        if (state is not string attemptId)
        {
            return;
        }

        var task = Task.Run(async () =>
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                bool stillArmed;
                lock (_sync)
                {
                    stillArmed = _deadmans.ContainsKey(attemptId);
                }

                if (stillArmed)
                {
                    await ResolveDeadmanAsync(attemptId, keep: false, expiredByTimer: true).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        });
        lock (_sync)
        {
            _background.Add(task);
        }
    }

    /// <summary>Changes of an attempt that are still in effect, in apply order.</summary>
    private static List<UndoUnit> UnitsOf(AttemptLog log)
        => [.. log.Steps.Where(AttemptState.InEffect).Where(s => s.Before is not null)
            .Select(s => new UndoUnit(s.Index, s.Op, s.Args, s.Before!, s.ApplyOutcome?.After))];

    private async Task<(bool Ok, IReadOnlyList<StepResult> Steps)> RollbackAsync(string sessionId, string attemptId, string fixId, List<UndoUnit> applied, string reason)
    {
        var (ok, steps, _) = await UndoUnitsAsync(sessionId, attemptId, fixId, applied, UndoMode.IfUnchanged, reason).ConfigureAwait(false);
        return (ok, steps);
    }

    /// <summary>Undoes changes in reverse order, then retries the ones that failed once (a later step's undo can unblock an
    /// earlier one, for example "start service" needs its start type back first). Journals every step.</summary>
    private async Task<(bool Ok, IReadOnlyList<StepResult> Steps, bool AnyUndone)> UndoUnitsAsync(string sessionId, string attemptId, string fixId, IReadOnlyList<UndoUnit> units, UndoMode mode, string reason)
    {
        await TryAppend(Rec(sessionId, attemptId, fixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.UndoIntent, error: reason)).ConfigureAwait(false);
        var results = new Dictionary<int, StepResult>();
        var failed = new List<UndoUnit>();
        foreach (var u in units.OrderByDescending(u => u.Index))
        {
            var r = await UndoOneAsync(sessionId, attemptId, fixId, u, mode).ConfigureAwait(false);
            results[u.Index] = r;
            if (r.Status != StepStatus.Undone)
            {
                failed.Add(u);
            }
        }

        foreach (var u in failed.ToList())
        {
            if (results[u.Index].ErrorCode == "undo.conflict")
            {
                continue;
            }

            var r = await UndoOneAsync(sessionId, attemptId, fixId, u, mode).ConfigureAwait(false);
            results[u.Index] = r;
            if (r.Status == StepStatus.Undone)
            {
                failed.Remove(u);
            }
        }

        var ok = failed.Count == 0;
        await TryAppend(Rec(sessionId, attemptId, fixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp,
            ok ? JournalPhase.Undone : failed.All(f => results[f.Index].ErrorCode == "undo.conflict") ? JournalPhase.UndoConflict : JournalPhase.UndoFailed)).ConfigureAwait(false);
        return (ok, [.. results.Values.OrderByDescending(r => r.Index)], results.Values.Any(r => r.Status == StepStatus.Undone));
    }

    private async Task<StepResult> UndoOneAsync(string sessionId, string attemptId, string fixId, UndoUnit u, UndoMode mode)
    {
        var op = _registry.FindBase(u.Op);
        if (op is null)
        {
            await TryAppend(Rec(sessionId, attemptId, fixId, u.Index, u.Op, JournalPhase.UndoFailed, u.Args, u.Before, null, "op.unknown")).ConfigureAwait(false);
            return new StepResult { Index = u.Index, Op = u.Op, Status = StepStatus.Failed, ErrorCode = "op.unknown" };
        }

        await TryAppend(Rec(sessionId, attemptId, fixId, u.Index, u.Op, JournalPhase.UndoIntent, u.Args, u.Before, u.ExpectedAfter)).ConfigureAwait(false);
        var result = await op.UndoAsync(u.OpArgs, u.Before, u.ExpectedAfter, mode, CancellationToken.None).ConfigureAwait(false);
        if (result.Success)
        {
            await TryAppend(Rec(sessionId, attemptId, fixId, u.Index, u.Op, JournalPhase.Undone, u.Args, u.Before, result.After)).ConfigureAwait(false);
            return new StepResult { Index = u.Index, Op = u.Op, Status = StepStatus.Undone };
        }

        var phase = result.ErrorCode == "undo.conflict" ? JournalPhase.UndoConflict : JournalPhase.UndoFailed;
        await TryAppend(Rec(sessionId, attemptId, fixId, u.Index, u.Op, phase, u.Args, u.Before, result.After, result.ErrorCode)).ConfigureAwait(false);
        return new StepResult { Index = u.Index, Op = u.Op, Status = StepStatus.Failed, ErrorCode = result.ErrorCode, MessageKey = "undo.step_failed" };
    }

    private DriftFinding Finding(string attemptId, string fixId, int index, string op, StateCapture expected, StateCapture actual, string reason) => new()
    {
        AttemptId = attemptId,
        FixId = fixId,
        StepIndex = index,
        Op = op,
        Expected = Public(expected),
        Actual = Public(actual),
        ReasonKey = reason,
        DetectedUtc = _clock.GetUtcNow(),
    };
}
