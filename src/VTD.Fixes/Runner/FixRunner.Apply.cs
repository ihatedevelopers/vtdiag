using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;
using VTD.Fixes.Ops;
using VTD.Journal;

namespace VTD.Fixes.Runner;

/// <content>Apply.</content>
public sealed partial class FixRunner
{
    /// <inheritdoc />
    public async Task<FixRunResult> ApplyAsync(ConsentToken consent, IProgress<FixProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            throw new ConsentRefusedException("fix.busy");
        }

        try
        {
            PreviewEntry entry;
            lock (_sync)
            {
                if (CheckConsentLocked(consent, out var found) is { } refused)
                {
                    throw new ConsentRefusedException(refused);
                }

                entry = found!;
                if (_deadmans.Count > 0)
                {
                    throw new ConsentRefusedException("fix.deadman_pending");
                }

                if (entry.Preview.RequiresSecondConsent && consent.SecondConsentNonce is null)
                {
                    entry.SecondNonce = _ids.NewId();
                    return new FixRunResult
                    {
                        AttemptId = string.Empty,
                        FixId = entry.Preview.FixId,
                        Status = AttemptStatus.AwaitingSecondConsent,
                        SecondConsentNonce = entry.SecondNonce,
                        MessageKey = "consent.one_way_confirm",
                    };
                }

                entry.Used = true; // single use from here on, whatever happens next
            }

            progress?.Report(new FixProgress { AttemptId = string.Empty, StepIndex = 0, StepCount = entry.Plan.Steps.Count, Phase = FixPhase.Policy });
            var fresh = await BuildAsync(entry.Plan.Fix, entry.Plan.Binding, entry.Preview.ProposedBy, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(fresh.Preview.PreviewHash, entry.Preview.PreviewHash, StringComparison.Ordinal))
            {
                throw new ConsentRefusedException("consent.preview_changed");
            }

            if (!fresh.Preview.CanApply)
            {
                throw new ConsentRefusedException("consent.blocked");
            }

            return await RunAttemptAsync(fresh.Plan, entry.Preview, consent, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FixRunResult> RunAttemptAsync(FixPlan plan, FixPreview preview, ConsentToken consent, IProgress<FixProgress>? progress, CancellationToken cancellationToken)
    {
        var sessionId = _session.SessionId;
        var attemptId = _ids.NewId();
        var fix = plan.Fix;
        var count = plan.Steps.Count;
        var stepResults = new List<StepResult>();

        // 1. Restore point (S12): before the first medium/high-risk or one-way fix of the session; no point, no change.
        DateTimeOffset? restorePointUtc = null;
        if (preview.RestorePointFirst)
        {
            progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = 0, StepCount = count, Phase = FixPhase.RestorePoint });
            RestorePointInfo info;
            try
            {
                info = await _restorePoints.EnsureAsync(sessionId, "restorepoint.before_fix", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                info = new RestorePointInfo { Status = RestorePointStatus.Failed, MessageKey = "restorepoint.failed" };
            }

            if (info.Status is not (RestorePointStatus.Created or RestorePointStatus.ContinuedWithout or RestorePointStatus.NotNeeded))
            {
                return new FixRunResult { AttemptId = attemptId, FixId = fix.Id, Status = AttemptStatus.Failed, MessageKey = info.MessageKey ?? "restorepoint." + info.Status.ToString() };
            }

            restorePointUtc = info.CreatedUtc;
            lock (_sync)
            {
                _restorePointSessions.Add(sessionId);
            }
        }

        // 2. The attempt-start record: what was consented, by whom, and how to undo it without the KB.
        var startArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JournalConventions.StartArgs.StepCount] = count.ToString(CultureInfo.InvariantCulture),
            [JournalConventions.StartArgs.FixRev] = fix.Rev.ToString(CultureInfo.InvariantCulture),
            [JournalConventions.StartArgs.TitleKey] = fix.TitleKey,
            [JournalConventions.StartArgs.Risk] = plan.Risk.ToString(),
            [JournalConventions.StartArgs.Reversibility] = plan.Reversibility.ToString(),
            [JournalConventions.StartArgs.Reboot] = plan.Reboot.ToString(),
            [JournalConventions.StartArgs.Scope] = fix.Scope.ToString(),
            [JournalConventions.StartArgs.Drift] = Captures.Bool(fix.Drift?.Watch == true),
        };
        if (fix.Drift?.ReasonKey is { } driftKey)
        {
            startArgs[JournalConventions.StartArgs.DriftReasonKey] = driftKey;
        }

        if (fix.Deadman is { } dmDef)
        {
            startArgs[JournalConventions.StartArgs.DeadmanSeconds] = DeadmanSeconds(dmDef).ToString(CultureInfo.InvariantCulture);
            startArgs[JournalConventions.StartArgs.DeadmanCheck] = dmDef.Check;
        }

        if (restorePointUtc is { } rp)
        {
            startArgs[JournalConventions.StartArgs.RestorePointUtc] = rp.ToString("O", CultureInfo.InvariantCulture);
        }

        foreach (var (k, v) in JournalConventions.EncodeBinding(plan.Binding))
        {
            startArgs[k] = v;
        }

        JournalRecord start;
        try
        {
            start = await _journal.AppendAsync(
                Rec(sessionId, attemptId, fix.Id, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Intent, startArgs) with
                {
                    ConsentTokenId = consent.TokenId,
                    PreviewHash = preview.PreviewHash,
                    ProposedBy = preview.ProposedBy,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (JournalWriteException)
        {
            return new FixRunResult { AttemptId = attemptId, FixId = fix.Id, Status = AttemptStatus.Failed, MessageKey = "journal.write_failed" };
        }

        var applied = new List<UndoUnit>();
        var reboot = plan.Reboot == RebootNeed.Required;

        // 3. Steps: capture → fsynced intent → apply → probe → journal. Cancellation is honoured between steps only.
        foreach (var step in plan.Steps)
        {
            var op = step.Op!;
            if (cancellationToken.IsCancellationRequested)
            {
                return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.Cancelled, "fix.cancelled", start.Seq).ConfigureAwait(false);
            }

            progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = step.Index, StepCount = count, Phase = FixPhase.Backup });
            StateCapture before;
            try
            {
                before = await op.CaptureAsync(step.OpArgs, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await TryAppend(Rec(sessionId, attemptId, fix.Id, step.Index, op.Op, JournalPhase.Failed, step.Args, error: "capture.failed")).ConfigureAwait(false);
                stepResults.Add(new StepResult { Index = step.Index, Op = op.Op, Status = StepStatus.Failed, ErrorCode = "capture.failed", MessageKey = "step.capture_failed" });
                if (step.Def.OnFail == StepFailure.Abort)
                {
                    return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.StepFailed, "fix.step_failed", start.Seq).ConfigureAwait(false);
                }

                continue;
            }

            if (!await TryAppend(Rec(sessionId, attemptId, fix.Id, step.Index, op.Op, JournalPhase.Intent, step.Args, before)).ConfigureAwait(false))
            {
                stepResults.Add(new StepResult { Index = step.Index, Op = op.Op, Status = StepStatus.Failed, ErrorCode = "journal.write_failed", MessageKey = "journal.write_failed" });
                return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.JournalFailed, "journal.write_failed", start.Seq).ConfigureAwait(false);
            }

            var intended = op.Intended(step.OpArgs, before);
            if (!op.AlwaysApply && op.Same(step.OpArgs, before, intended))
            {
                if (!await TryAppend(Rec(sessionId, attemptId, fix.Id, step.Index, op.Op, JournalPhase.Applied, step.Args, before, before, JournalConventions.StepSkipped)).ConfigureAwait(false))
                {
                    return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.JournalFailed, "journal.write_failed", start.Seq).ConfigureAwait(false);
                }

                stepResults.Add(new StepResult { Index = step.Index, Op = op.Op, Status = StepStatus.Skipped });
                continue;
            }

            progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = step.Index, StepCount = count, Phase = FixPhase.Apply });
            var result = await op.ApplyAsync(step.OpArgs, before, CancellationToken.None).ConfigureAwait(false);
            var after = result.After ?? await TryProbe(op, step.OpArgs).ConfigureAwait(false);
            var unit = new UndoUnit(step.Index, op.Op, step.Args, before, after);
            if (result.Success)
            {
                applied.Add(unit);
                reboot |= result.RebootRequired;
                stepResults.Add(new StepResult { Index = step.Index, Op = op.Op, Status = StepStatus.Applied });
                if (!await TryAppend(Rec(sessionId, attemptId, fix.Id, step.Index, op.Op, JournalPhase.Applied, step.Args, before, after)).ConfigureAwait(false))
                {
                    return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.JournalFailed, "journal.write_failed", start.Seq).ConfigureAwait(false);
                }

                continue;
            }

            await TryAppend(Rec(sessionId, attemptId, fix.Id, step.Index, op.Op, JournalPhase.Failed, step.Args, before, after, result.ErrorCode)).ConfigureAwait(false);
            var status = result.ErrorCode == "policy.denied" ? StepStatus.PolicyDenied : StepStatus.Failed;
            stepResults.Add(new StepResult { Index = step.Index, Op = op.Op, Status = status, ErrorCode = result.ErrorCode, MessageKey = "step.failed" });
            if (after is null || !op.Same(step.OpArgs, after, before))
            {
                applied.Add(unit); // partial change (or unknown): undo it too
            }

            if (step.Def.OnFail == StepFailure.Abort)
            {
                return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.StepFailed, "fix.step_failed", start.Seq).ConfigureAwait(false);
            }
        }

        if (!await TryAppend(Rec(sessionId, attemptId, fix.Id, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Applied,
                new Dictionary<string, string>(StringComparer.Ordinal) { [JournalConventions.StartArgs.Reboot] = reboot ? nameof(RebootNeed.Required) : plan.Reboot.ToString() })).ConfigureAwait(false))
        {
            return await AbortAsync(sessionId, attemptId, fix.Id, applied, stepResults, JournalConventions.Reasons.JournalFailed, "journal.write_failed", start.Seq).ConfigureAwait(false);
        }

        // 4. Verify ("applied" is not "fixed": the outcome comes later).
        progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = Math.Max(0, count - 1), StepCount = count, Phase = FixPhase.Verify });
        var verify = await RunVerifyAsync(attemptId, plan, postReboot: false).ConfigureAwait(false);
        if (verify.Status == VerifyStatus.Failed)
        {
            await TryAppend(Rec(sessionId, attemptId, fix.Id, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Failed, error: JournalConventions.VerifyFailed)).ConfigureAwait(false);
            var (ok, undoSteps) = await RollbackAsync(sessionId, attemptId, fix.Id, applied, JournalConventions.Reasons.VerifyFailed).ConfigureAwait(false);
            return new FixRunResult
            {
                AttemptId = attemptId,
                FixId = fix.Id,
                Status = ok ? AttemptStatus.VerifyFailed : AttemptStatus.InDoubt,
                Steps = Merge(stepResults, undoSteps),
                Verify = verify,
                MessageKey = ok ? "fix.verify_failed_undone" : "fix.rollback_failed",
                FirstJournalSeq = start.Seq,
                LastJournalSeq = await LastSeqAsync(attemptId).ConfigureAwait(false),
            };
        }

        var deferred = reboot || plan.Verify.Any(v => v.Def.When == VerifyTiming.PostReboot);
        AttemptStatus final;
        if (deferred)
        {
            final = AttemptStatus.AwaitingReboot;
            if (verify.Status == VerifyStatus.Passed)
            {
                verify = verify with { Status = VerifyStatus.DeferredUntilReboot };
            }
        }
        else if (verify.Status == VerifyStatus.Passed)
        {
            final = AttemptStatus.Verified;
            await TryAppend(Rec(sessionId, attemptId, fix.Id, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Verified)).ConfigureAwait(false);
        }
        else
        {
            final = AttemptStatus.Applied; // inconclusive: kept, the player can undo
        }

        // 5. Deadman (S15): journaled and fsynced BEFORE the countdown starts.
        DeadmanState? deadman = null;
        if (fix.Deadman is { } dm && applied.Count > 0)
        {
            var deadline = _clock.GetUtcNow().AddSeconds(DeadmanSeconds(dm));
            if (!await TryAppend(Rec(sessionId, attemptId, fix.Id, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.DeadmanArmed) with { DeadlineUtc = deadline }).ConfigureAwait(false))
            {
                var (ok, undoSteps) = await RollbackAsync(sessionId, attemptId, fix.Id, applied, JournalConventions.Reasons.JournalFailed).ConfigureAwait(false);
                return new FixRunResult { AttemptId = attemptId, FixId = fix.Id, Status = ok ? AttemptStatus.RolledBack : AttemptStatus.InDoubt, Steps = Merge(stepResults, undoSteps), MessageKey = "deadman.journal_failed", FirstJournalSeq = start.Seq };
            }

            Arm(attemptId, deadline, dm.Check);
            deadman = new DeadmanState { Status = DeadmanStatus.Armed, DeadlineUtc = deadline };
            progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = Math.Max(0, count - 1), StepCount = count, Phase = FixPhase.Deadman });
        }

        progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = Math.Max(0, count - 1), StepCount = count, Phase = FixPhase.Done });
        return new FixRunResult
        {
            AttemptId = attemptId,
            FixId = fix.Id,
            Status = final,
            Steps = stepResults,
            Verify = verify,
            RebootPending = reboot,
            Deadman = deadman,
            MessageKey = final switch
            {
                AttemptStatus.AwaitingReboot => "fix.restart_needed",
                AttemptStatus.Verified => "fix.applied_verified",
                _ => "fix.applied_unverified",
            },
            FirstJournalSeq = start.Seq,
            LastJournalSeq = await LastSeqAsync(attemptId).ConfigureAwait(false),
        };
    }

    private async Task<FixRunResult> AbortAsync(string sessionId, string attemptId, string fixId, List<UndoUnit> applied, List<StepResult> steps, string reason, string messageKey, long firstSeq)
    {
        if (applied.Count == 0)
        {
            await TryAppend(Rec(sessionId, attemptId, fixId, JournalConventions.AttemptStep, JournalConventions.AttemptOp, JournalPhase.Failed, error: reason)).ConfigureAwait(false);
            return new FixRunResult { AttemptId = attemptId, FixId = fixId, Status = AttemptStatus.Failed, Steps = steps, MessageKey = messageKey, FirstJournalSeq = firstSeq };
        }

        var (ok, undoSteps) = await RollbackAsync(sessionId, attemptId, fixId, applied, reason).ConfigureAwait(false);
        return new FixRunResult
        {
            AttemptId = attemptId,
            FixId = fixId,
            Status = ok ? AttemptStatus.RolledBack : AttemptStatus.InDoubt,
            Steps = Merge(steps, undoSteps),
            MessageKey = ok ? messageKey : "fix.rollback_failed",
            FirstJournalSeq = firstSeq,
        };
    }

    private static List<StepResult> Merge(List<StepResult> applied, IReadOnlyList<StepResult> undone)
    {
        var merged = applied.Select(s => undone.FirstOrDefault(u => u.Index == s.Index && u.Status == StepStatus.Undone) is { } u ? s with { Status = StepStatus.RolledBack } : s).ToList();
        merged.AddRange(undone.Where(u => u.Status != StepStatus.Undone && !merged.Any(m => m.Index == u.Index && m.Status == StepStatus.Failed)));
        return merged;
    }

    private async Task<long?> LastSeqAsync(string attemptId)
    {
        long? last = null;
        await foreach (var r in _journal.ReadAsync(_session.SessionId, CancellationToken.None).ConfigureAwait(false))
        {
            if (string.Equals(r.AttemptId, attemptId, StringComparison.Ordinal))
            {
                last = r.Seq;
            }
        }

        return last;
    }

    private static async Task<StateCapture?> TryProbe(OperationBase op, OpArgs args)
    {
        try
        {
            return await op.ProbeAsync(args, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>One change to undo: the step, its arguments, the captured before-state and the state we left.</summary>
    private sealed record UndoUnit(int Index, string Op, IReadOnlyDictionary<string, string> Args, StateCapture Before, StateCapture? ExpectedAfter)
    {
        public OpArgs OpArgs => new() { Values = Args };
    }
}
