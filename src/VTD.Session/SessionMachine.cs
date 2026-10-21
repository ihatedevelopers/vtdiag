using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Journaling;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;

namespace VTD.Session;

/// <summary>
/// The session state machine (design_B.md section 6.1): a pure function <c>(state, event, context) → (state', effects)</c>
/// with no I/O, so it can be property-tested with a crash injected at every persist point.
/// </summary>
/// <remarks>
/// Invariants enforced here:
/// <list type="bullet">
/// <item>R1: the only <see cref="RestartEffect"/> comes from a <see cref="ResumeRegistered"/> carrying the token of a
/// <see cref="RestartNowClicked"/> that this machine accepted from <see cref="EventOrigin.Ui"/>, after
/// <see cref="RestartInfo.ShutdownInitiatedUtc"/> is set. No resume path can restart.</item>
/// <item>Resume work runs at most <see cref="SessionLimits.MaxResumeAttempts"/> times; the counter is incremented in the
/// same transition that starts the work (so it is persisted before any effect runs).</item>
/// <item>Entering a terminal phase (Completed, RolledBack, Abandoned, NeedsAttention) always clears
/// <see cref="ResumeInfo.Registered"/> and emits <see cref="UnregisterResumeEffect"/> while anything may still be
/// registered (<see cref="ResumeInfo.Mechanism"/> stays set until <see cref="ResumeUnregistered"/>, so a crash before the
/// unregister is retried on the next start).</item>
/// <item>Consent is checked BEFORE leaving AwaitingConsent; a refusal leaves the phase unchanged.</item>
/// </list>
/// The origin policy (<see cref="CommandPolicy"/>) is checked by the controller before the machine sees a command; the
/// machine re-checks the few UI-only decisions it depends on (defence in depth).
/// </remarks>
public static class SessionMachine
{
    /// <summary>Computes the next state and the effects to run after it is persisted.</summary>
    /// <param name="state">Current state.</param>
    /// <param name="sessionEvent">Event (origin already stamped).</param>
    /// <param name="context">Facts looked up by the runtime.</param>
    public static Transition Next(SessionState state, SessionEvent sessionEvent, MachineContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sessionEvent);
        ArgumentNullException.ThrowIfNull(context);
        return sessionEvent switch
        {
            SessionCommand command => OnCommand(state, command, context),
            EngineEvent engineEvent => OnEngineEvent(state, engineEvent, context),
            _ => Transition.Reject(state, SessionCodes.InvalidPhase),
        };
    }

    /// <summary>Completed, RolledBack, Abandoned.</summary>
    /// <param name="phase">Phase.</param>
    public static bool IsTerminal(Phase phase) => phase is Phase.Completed or Phase.RolledBack or Phase.Abandoned;

    /// <summary>Terminal or NeedsAttention: nothing may stay registered for resume.</summary>
    /// <param name="phase">Phase.</param>
    public static bool IsClosed(Phase phase) => IsTerminal(phase) || phase == Phase.NeedsAttention;

    /// <summary>Phases a resume task (or a manual start after a restart) continues from.</summary>
    /// <param name="state">State.</param>
    public static bool IsResumable(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            Phase.RestartRequested or Phase.AwaitingRestart or Phase.Resuming => true,
            Phase.GuidedStep => state.Guide?.AwaitingFirmwareRestart == true,
            _ => false,
        };
    }

    /// <summary>The phase a cancelled or failed scan returns to (computed from persisted state only).</summary>
    /// <param name="state">State.</param>
    public static Phase PhaseBeforeScan(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Guide is not null)
        {
            return Phase.GuidedStep;
        }

        if (state.CurrentAttemptId is { } current && state.Attempts.Any(a => Same(a.AttemptId, current) && a.Outcome == FixOutcome.Pending && a.Status is AttemptStatus.Verified or AttemptStatus.Applied))
        {
            return Phase.AwaitingOutcome;
        }

        return state.SnapshotId is not null && state.AnalysisId is not null ? Phase.Analyzed : Phase.Intake;
    }

    /// <summary>A new session (revision 0; the controller persists it as revision 1).</summary>
    /// <param name="sessionId">ULID.</param>
    /// <param name="now">Engine clock.</param>
    /// <param name="privilege">Privilege.</param>
    /// <param name="kbVersion">KB version pinned for the session.</param>
    /// <param name="playerSid">Player SID established by the engine.</param>
    public static SessionState NewSession(string sessionId, DateTimeOffset now, PrivilegeMode privilege, string kbVersion, string? playerSid) => new()
    {
        SessionId = sessionId,
        SchemaVersion = SessionState.CurrentSchemaVersion,
        Revision = 0,
        Phase = Phase.Intake,
        CreatedUtc = now,
        UpdatedUtc = now,
        ExpiresUtc = now + SessionLimits.Expiry,
        Privilege = privilege,
        KbVersion = kbVersion,
        Resume = new ResumeInfo(),
        PlayerSid = playerSid,
    };

    private static Transition OnCommand(SessionState s, SessionCommand c, MachineContext ctx)
    {
        if (s.Phase == Phase.NeedsAttention && c is not (UndoAllRequested or AbandonRequested or UndoRequested or UndoProposed or ScanCancelRequested or MonitorMarkBadRequested))
        {
            return Transition.Reject(s, SessionCodes.NeedsAttention);
        }

        return c switch
        {
            IntakeSubmitted e => OnIntake(s, e, ctx),
            ScanRequested e => OnScanRequested(s, e.Mode, checkDrift: false, ctx),
            RescanRequested => OnScanRequested(s, ScanMode.Full, checkDrift: true, ctx),
            ScanCancelRequested => s.Phase == Phase.Scanning
                ? Transition.To(s with { Phase = PhaseBeforeScan(s) }, new CancelScanEffect())
                : Transition.Ignore(s),
            QuestionAnswered e => OnQuestion(s, e),
            FixSelected e => OnFixSelected(s, e, ctx),
            ConsentGiven e => OnConsent(s, e, ctx),
            FixSkipped e => OnSkip(s, e),
            DeadmanAnswered e => s.Phase == Phase.Verifying && Same(e.AttemptId, s.CurrentAttemptId)
                ? Transition.Only(s, new ConfirmDeadmanEffect(e.AttemptId, e.Keep))
                : Transition.Reject(s, SessionCodes.DeadmanNotPending),
            RestartNowClicked e => OnRestartNow(s, e, ctx),
            RestartLaterChosen => OnRestartLater(s, ctx),
            OutcomeReported e => OnOutcome(s, e),
            MonitorRequested e => OnMonitor(s, e, ctx),
            MonitorMarkBadRequested => s.Phase == Phase.Monitoring ? Transition.Only(s, new MarkBadEffect()) : Transition.Ignore(s),
            GuideStarted e => OnGuideStarted(s, e, ctx),
            GuideStepCompleted e => OnGuideStep(s, e, ctx),
            UndoProposed e => OnUndoProposed(s, e, ctx),
            UndoRequested e => OnUndoRequested(s, e, ctx),
            UndoAllRequested => OnUndoAll(s),
            PlanImported e => OnPlanImported(s, e, ctx),
            AbandonRequested => OnAbandon(s),
            _ => Transition.Reject(s, SessionCodes.InvalidPhase),
        };
    }

    private static Transition OnEngineEvent(SessionState s, EngineEvent e, MachineContext ctx) => e switch
    {
        ScanCompleted x => s.Phase == Phase.Scanning
            ? Transition.To(s with { SnapshotId = x.SnapshotId }, new AnalyzeEffect(x.SnapshotId))
            : Transition.Ignore(s),
        ScanFailed x => s.Phase == Phase.Scanning
            ? Transition.To(s with { Phase = PhaseBeforeScan(s), LastErrorCode = x.ErrorCode })
            : Transition.Ignore(s),
        AnalysisCompleted x => Transition.To(s with
        {
            AnalysisId = x.AnalysisId,
            Phase = s.Phase == Phase.Scanning ? (s.Guide is not null ? Phase.GuidedStep : Phase.Analyzed) : s.Phase,
        }),
        FixApplyCompleted x => OnApplyCompleted(s, x.Result, ctx),
        DeadmanExpired x => s.Phase == Phase.Verifying && Same(x.AttemptId, s.CurrentAttemptId)
            ? Transition.Only(s, new ConfirmDeadmanEffect(x.AttemptId, Keep: false))
            : Transition.Ignore(s),
        ResumeStarted x => OnResumeStarted(s, x, ctx),
        RecoveryCompleted x => OnRecovery(s, x.Report, ctx),
        PostRebootVerifyCompleted x => OnPostRebootVerify(s, x, ctx),
        MonitorCompleted x => OnMonitorCompleted(s, x),
        GuideVerifyCompleted x => OnGuideVerified(s, x),
        UndoCompleted x => OnUndoCompleted(s, x.Result),
        UiConnected => OnUiConnected(s),
        SessionExpired => IsClosed(s.Phase) ? Transition.Ignore(s) : Close(s, Phase.NeedsAttention, SessionCodes.Expired),
        ResumeRegistered x => OnResumeRegistered(s, x, ctx),
        ResumeUnregistered => s.Resume.Mechanism == ResumeMechanism.None && !s.Resume.Registered
            ? Transition.Ignore(s)
            : Transition.To(s with { Resume = s.Resume with { Mechanism = ResumeMechanism.None, Registered = false } }),
        RestartFailed x => s.Phase == Phase.RestartRequested
            ? Transition.To(s with
            {
                Phase = s.Guide?.AwaitingFirmwareRestart == true ? Phase.GuidedStep : Phase.AwaitingRestart,
                Restart = s.Restart is null ? null : s.Restart with { ShutdownInitiatedUtc = null },
                LastErrorCode = x.ErrorCode,
            })
            : Transition.Ignore(s),
        RestorePointRecorded x => Transition.To(s with { RestorePoint = x.Info }),
        _ => Transition.Ignore(s),
    };

    // ---- commands -------------------------------------------------------------------------------------------------

    private static Transition OnIntake(SessionState s, IntakeSubmitted e, MachineContext ctx)
    {
        if (IsTerminal(s.Phase))
        {
            return Restarted(s, ctx) is { } fresh ? Transition.To(fresh with { Intake = e.Intake }) : Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        return s.Phase switch
        {
            Phase.Intake => Transition.To(s with { Intake = e.Intake }),
            Phase.Analyzed or Phase.Proposing when s.SnapshotId is { } snapshot => Transition.To(s with { Intake = e.Intake }, new AnalyzeEffect(snapshot)),
            _ => Transition.Reject(s, SessionCodes.InvalidPhase),
        };
    }

    private static Transition OnScanRequested(SessionState s, ScanMode mode, bool checkDrift, MachineContext ctx)
    {
        var start = s;
        if (IsTerminal(s.Phase))
        {
            start = Restarted(s, ctx) ?? s;
            if (ReferenceEquals(start, s))
            {
                return Transition.Reject(s, SessionCodes.InvalidPhase);
            }
        }
        else if (s.Phase is not (Phase.Intake or Phase.Analyzed or Phase.Proposing or Phase.AwaitingOutcome or Phase.GuidedStep))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        return Transition.To(start with { Phase = Phase.Scanning, LastErrorCode = null }, new StartScanEffect(mode, checkDrift));
    }

    private static Transition OnQuestion(SessionState s, QuestionAnswered e)
    {
        if (s.Phase is not (Phase.Analyzed or Phase.Proposing))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (s.QuestionsAsked >= RankingConstants.MaxQuestions)
        {
            return Transition.Reject(s, SessionCodes.QuestionLimit);
        }

        var answers = new Dictionary<string, string>(s.Intake.Answers, StringComparer.Ordinal) { [e.QuestionId] = e.OptionId };
        var next = s with { QuestionsAsked = s.QuestionsAsked + 1, Intake = s.Intake with { Answers = answers } };
        return s.SnapshotId is { } snapshot ? Transition.To(next, new AnalyzeEffect(snapshot)) : Transition.To(next);
    }

    private static Transition OnFixSelected(SessionState s, FixSelected e, MachineContext ctx)
    {
        if (s.Privilege != PrivilegeMode.Elevated)
        {
            return Transition.Reject(s, SessionCodes.LimitedMode);
        }

        if (s.Phase == Phase.AwaitingConsent && e.Origin != EventOrigin.Ui)
        {
            // Only the player may replace a proposal that is on screen: an agent swapping the preview under the dialog
            // would make the player consent to something else.
            return Transition.Reject(s, SessionCodes.ConsentPending);
        }

        if (s.Phase is not (Phase.Analyzed or Phase.Proposing or Phase.AwaitingOutcome or Phase.AwaitingConsent or Phase.GuidedStep))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (ctx.Preview is not { } preview || ctx.Proposer is not { } proposer || !Same(preview.FixId, e.FixId))
        {
            return Transition.Reject(s, SessionCodes.PreviewFailed);
        }

        var effects = new List<SessionEffect>();
        if (s.CurrentPreviewId is { } previous && !Same(previous, preview.PreviewId))
        {
            effects.Add(new ResolveOutcomeEffect(previous, null));
        }

        effects.Add(new NotifyConsentEffect(ConsentKind.Fix, preview.FixId, preview.PreviewId, null, proposer, null));
        return Transition.To(
            s with
            {
                Phase = Phase.AwaitingConsent,
                CurrentFixId = preview.FixId,
                CurrentPreviewId = preview.PreviewId,
                CurrentProposal = proposer,
                LastErrorCode = null,
            },
            effects);
    }

    private static Transition OnConsent(SessionState s, ConsentGiven e, MachineContext ctx)
    {
        if (e.Origin != EventOrigin.Ui)
        {
            return Transition.Reject(s, SessionCodes.NotAllowed);
        }

        if (s.Phase != Phase.AwaitingConsent)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (s.Privilege != PrivilegeMode.Elevated)
        {
            return Transition.Reject(s, SessionCodes.ConsentLimited);
        }

        if (!Same(e.Token.SessionId, s.SessionId))
        {
            return Transition.Reject(s, SessionCodes.ConsentWrongSession);
        }

        if (!Same(e.Token.PreviewId, s.CurrentPreviewId) || !Same(e.Token.FixId, s.CurrentFixId))
        {
            return Transition.Reject(s, SessionCodes.ConsentPreviewChanged);
        }

        if (ctx.ConsentRefusal is { } refusal)
        {
            return Transition.Reject(s, refusal);
        }

        return Transition.To(s with { Phase = Phase.Applying, CurrentAttemptId = null, LastErrorCode = null }, new ApplyFixEffect(e.Token));
    }

    private static Transition OnSkip(SessionState s, FixSkipped e)
    {
        if (s.Phase is not (Phase.AwaitingConsent or Phase.Analyzed or Phase.Proposing or Phase.AwaitingOutcome))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (s.Phase == Phase.AwaitingConsent && !Same(e.FixId, s.CurrentFixId))
        {
            return Transition.Reject(s, SessionCodes.ConsentPreviewChanged);
        }

        var effects = new List<SessionEffect>();
        if (s.CurrentPreviewId is { } previous)
        {
            effects.Add(new ResolveOutcomeEffect(previous, null));
        }

        var skipped = s.SkippedFixIds.Contains(e.FixId, StringComparer.Ordinal) ? s.SkippedFixIds : [.. s.SkippedFixIds, e.FixId];
        return Transition.To(s with { Phase = Phase.Proposing, SkippedFixIds = skipped, CurrentFixId = null, CurrentPreviewId = null, CurrentProposal = null }, effects);
    }

    private static Transition OnRestartNow(SessionState s, RestartNowClicked e, MachineContext ctx)
    {
        if (e.Origin != EventOrigin.Ui)
        {
            return Transition.Reject(s, SessionCodes.NotAllowed);
        }

        var allowed = s.Phase == Phase.AwaitingRestart || (s.Phase == Phase.GuidedStep && e.Token.Kind == RestartKind.ToFirmware);
        if (!allowed || s.Privilege != PrivilegeMode.Elevated)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (!Same(e.Token.SessionId, s.SessionId))
        {
            return Transition.Reject(s, SessionCodes.RestartTokenInvalid);
        }

        if (ctx.RestartRefusal is { } refusal)
        {
            return Transition.Reject(s, refusal);
        }

        if (ctx.CurrentBoot is not { } boot)
        {
            return Transition.Reject(s, SessionCodes.RestartBootUnknown);
        }

        var restart = new RestartInfo
        {
            Kind = e.Token.Kind,
            ReasonKey = s.Restart?.ReasonKey ?? (e.Token.Kind == RestartKind.ToFirmware ? SessionCodes.RestartReasonFirmware : SessionCodes.RestartReasonFix),
            BootAtRequest = boot,
            RequestedUtc = ctx.Now,
            WindowsUpdateRebootPendingAtRequest = ctx.WindowsUpdateRebootPending,
            AttemptsAwaitingPostRebootVerify = AwaitingReboot(s),
        };
        var guide = s.Guide is { } g && e.Token.Kind == RestartKind.ToFirmware ? g with { AwaitingFirmwareRestart = true } : s.Guide;
        return Transition.To(
            s with { Phase = Phase.RestartRequested, Restart = restart, Guide = guide, Resume = ResumeForNewRequest(s.Resume), LastErrorCode = null },
            new RegisterResumeEffect(s.SessionId, s.ExpiresUtc, e.Token));
    }

    private static Transition OnRestartLater(SessionState s, MachineContext ctx)
    {
        if (s.Phase != Phase.AwaitingRestart || s.Privilege != PrivilegeMode.Elevated)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (ctx.CurrentBoot is not { } boot)
        {
            return Transition.Reject(s, SessionCodes.RestartBootUnknown);
        }

        var restart = s.Restart ?? new RestartInfo
        {
            Kind = RestartKind.Normal,
            ReasonKey = SessionCodes.RestartReasonFix,
            BootAtRequest = boot,
            RequestedUtc = ctx.Now,
            WindowsUpdateRebootPendingAtRequest = ctx.WindowsUpdateRebootPending,
            AttemptsAwaitingPostRebootVerify = AwaitingReboot(s),
        };
        // "Later" on a request that already exists keeps its resume budget; only a new request starts a new one.
        var resume = s.Restart is null ? ResumeForNewRequest(s.Resume) : s.Resume with { Mechanism = s.Resume.Mechanism == ResumeMechanism.None ? ResumeMechanism.ScheduledTasks : s.Resume.Mechanism };
        return Transition.To(s with { Restart = restart with { ShutdownInitiatedUtc = null }, Resume = resume }, new RegisterResumeEffect(s.SessionId, s.ExpiresUtc, null));
    }

    private static Transition OnOutcome(SessionState s, OutcomeReported e)
    {
        if (s.Phase != Phase.AwaitingOutcome || !s.Attempts.Any(a => Same(a.AttemptId, e.AttemptId)) || e.Outcome == FixOutcome.Pending)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        var next = s with { Attempts = Update(s.Attempts, e.AttemptId, a => a with { Outcome = e.Outcome }), CurrentAttemptId = null };
        if (e.Outcome == FixOutcome.Fixed)
        {
            return Close(next, Phase.Completed, null);
        }

        // A failed fix is negative evidence: re-rank with the attempt history.
        return next.SnapshotId is { } snapshot
            ? Transition.To(next with { Phase = Phase.Proposing }, new AnalyzeEffect(snapshot))
            : Transition.To(next with { Phase = Phase.Proposing });
    }

    private static Transition OnMonitor(SessionState s, MonitorRequested e, MachineContext ctx)
    {
        if (s.Phase is not (Phase.Analyzed or Phase.Proposing or Phase.AwaitingOutcome))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (!ctx.MonitorAvailable)
        {
            return Transition.Reject(s, SessionCodes.MonitorUnavailable);
        }

        var baseline = s.Monitor?.SummaryId ?? s.Monitor?.BaselineSummaryId;
        var run = new MonitorRunInfo { Purpose = e.Purpose, StartedUtc = ctx.Now, BaselineSummaryId = baseline };
        return Transition.To(s with { Phase = Phase.Monitoring, Monitor = run }, new StartMonitorEffect(e.Purpose, e.Minutes));
    }

    private static Transition OnGuideStarted(SessionState s, GuideStarted e, MachineContext ctx)
    {
        if (s.Phase is not (Phase.Analyzed or Phase.Proposing or Phase.AwaitingOutcome or Phase.GuidedStep))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (ctx.Guide is not { } guide || !Same(guide.Id, e.GuideId))
        {
            return Transition.Reject(s, SessionCodes.UnknownGuide);
        }

        var branch = e.BranchId ?? guide.Branches.FirstOrDefault()?.Id;
        return Transition.To(s with
        {
            Phase = Phase.GuidedStep,
            Guide = new GuideProgress { GuideId = guide.Id, BranchId = branch, StepIndex = 0, StartedUtc = ctx.Now },
            LastErrorCode = null,
        });
    }

    private static Transition OnGuideStep(SessionState s, GuideStepCompleted e, MachineContext ctx)
    {
        if (s.Phase != Phase.GuidedStep || s.Guide is not { } progress || !Same(progress.GuideId, e.GuideId) || e.StepIndex < 0)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        var next = progress with { StepIndex = Math.Max(progress.StepIndex, e.StepIndex + 1) };
        var branch = ctx.Guide?.Branches.FirstOrDefault(b => Same(b.Id, progress.BranchId)) ?? ctx.Guide?.Branches.FirstOrDefault();
        var finished = branch is not null && next.StepIndex >= branch.Steps.Count && progress.StepIndex < branch.Steps.Count;
        return finished && branch!.Verify.Count > 0
            ? Transition.To(s with { Guide = next }, new VerifyGuideEffect(progress.GuideId, progress.BranchId))
            : Transition.To(s with { Guide = next });
    }

    private static Transition OnUndoProposed(SessionState s, UndoProposed e, MachineContext ctx)
    {
        if (IsBusy(s.Phase) || s.Phase == Phase.Verifying)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (!ctx.UndoTargetKnown || ctx.Proposer is not { } proposer)
        {
            return Transition.Reject(s, SessionCodes.UnknownAttempt);
        }

        var fixId = ctx.UndoTargetFixId ?? s.Attempts.FirstOrDefault(a => Same(a.AttemptId, e.AttemptId))?.FixId ?? string.Empty;
        var notify = new NotifyConsentEffect(ConsentKind.Undo, fixId, null, e.AttemptId, proposer, null);

        // While a fix is on the consent screen, its proposer stays on record; the undo request is a side dialog.
        return s.Phase == Phase.AwaitingConsent
            ? Transition.Only(s, notify)
            : Transition.To(s with { CurrentProposal = proposer }, notify);
    }

    private static Transition OnUndoRequested(SessionState s, UndoRequested e, MachineContext ctx)
    {
        if (e.Origin != EventOrigin.Ui)
        {
            return Transition.Reject(s, SessionCodes.NotAllowed);
        }

        if (IsBusy(s.Phase) || s.Phase == Phase.Verifying)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        if (!ctx.UndoTargetKnown)
        {
            return Transition.Reject(s, SessionCodes.UnknownAttempt);
        }

        var effect = new UndoEffect(e.AttemptId, e.Mode);
        return s.Phase != Phase.AwaitingConsent && s.CurrentProposal is not null
            ? Transition.To(s with { CurrentProposal = null }, effect)
            : Transition.Only(s, effect);
    }

    private static Transition OnUndoAll(SessionState s)
    {
        if (s.Phase is Phase.Applying or Phase.RestartRequested or Phase.Resuming || s.Phase == Phase.RolledBack)
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        var effects = new List<SessionEffect>();
        if (s.Phase == Phase.Scanning)
        {
            effects.Add(new CancelScanEffect());
        }

        if (s.CurrentPreviewId is { } preview)
        {
            effects.Add(new ResolveOutcomeEffect(preview, null));
        }

        effects.AddRange(UndoEffects(s));
        var closed = Close(s with { CurrentFixId = null, CurrentPreviewId = null, CurrentProposal = null }, Phase.RolledBack, null);
        return Transition.To(closed.State, [.. effects, .. closed.Effects]);
    }

    private static Transition OnPlanImported(SessionState s, PlanImported e, MachineContext ctx)
    {
        if (IsBusy(s.Phase) || IsClosed(s.Phase))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        return ctx.PlanValidated ? Transition.To(s with { ImportedPlanId = e.PlanId }) : Transition.Reject(s, SessionCodes.PlanNotValidated);
    }

    private static Transition OnAbandon(SessionState s)
    {
        if (s.Phase is Phase.Applying or Phase.RestartRequested or Phase.Resuming || IsTerminal(s.Phase))
        {
            return Transition.Reject(s, SessionCodes.InvalidPhase);
        }

        var effects = new List<SessionEffect>();
        if (s.Phase == Phase.Scanning)
        {
            effects.Add(new CancelScanEffect());
        }

        if (s.CurrentPreviewId is { } preview)
        {
            effects.Add(new ResolveOutcomeEffect(preview, null));
        }

        var closed = Close(s with { CurrentFixId = null, CurrentPreviewId = null, CurrentProposal = null }, Phase.Abandoned, null);
        return Transition.To(closed.State, [.. effects, .. closed.Effects]);
    }

    // ---- engine events --------------------------------------------------------------------------------------------

    private static Transition OnApplyCompleted(SessionState s, FixRunResult r, MachineContext ctx)
    {
        if (s.Phase == Phase.Verifying)
        {
            return OnDeadmanResult(s, r);
        }

        if (s.Phase != Phase.Applying)
        {
            // An undo center attempt or a late result after "undo all": only the attempt row changes.
            return s.Attempts.Any(a => Same(a.AttemptId, r.AttemptId))
                ? Transition.To(s with { Attempts = Update(s.Attempts, r.AttemptId, a => a with { Status = r.Status, RebootPending = r.RebootPending }) })
                : Transition.Ignore(s);
        }

        var previewId = s.CurrentPreviewId;
        if (r.Status == AttemptStatus.AwaitingSecondConsent)
        {
            var proposer = s.CurrentProposal ?? ctx.Preview?.ProposedBy ?? new Proposer { Origin = EventOrigin.Ui };
            return Transition.To(
                s with { Phase = Phase.AwaitingConsent },
                new NotifyConsentEffect(ConsentKind.SecondConsent, r.FixId, previewId, null, proposer, r.SecondConsentNonce));
        }

        if (r.Status == AttemptStatus.Failed && r.MessageKey is { } key && key.StartsWith("consent.", StringComparison.Ordinal))
        {
            // Refused at apply (raced with the pre-check): back to the decision, nothing was changed.
            return Transition.To(s with { Phase = Phase.AwaitingConsent, LastErrorCode = key });
        }

        var effects = new List<SessionEffect>();
        if (previewId is not null)
        {
            effects.Add(new ResolveOutcomeEffect(previewId, r));
        }

        var attempts = s.Attempts;
        if (!string.IsNullOrEmpty(r.AttemptId) && !attempts.Any(a => Same(a.AttemptId, r.AttemptId)))
        {
            attempts =
            [
                .. attempts,
                new FixAttempt
                {
                    AttemptId = r.AttemptId,
                    FixId = r.FixId,
                    FixRev = ctx.Preview?.FixRev ?? 1,
                    BindingHash = ctx.Preview?.BindingHash ?? string.Empty,
                    Status = r.Status,
                    HypothesisIds = ctx.HypothesisIds,
                    Reversibility = ctx.Preview?.Reversibility ?? Reversibility.Exact,
                    RebootPending = r.RebootPending,
                    AppliedUtc = r.Status == AttemptStatus.Failed ? null : ctx.Now,
                    VerifiedUtc = r.Status == AttemptStatus.Verified && r.Deadman is null ? ctx.Now : null,
                    PlanId = s.CurrentProposal?.PlanId,
                },
            ];
        }

        var cleared = s with { Attempts = attempts, CurrentPreviewId = null, CurrentProposal = null, CurrentFixId = null };
        switch (r.Status)
        {
            case AttemptStatus.InDoubt:
                var closed = Close(cleared with { CurrentAttemptId = r.AttemptId }, Phase.NeedsAttention, SessionCodes.FixInDoubt);
                return Transition.To(closed.State, [.. effects, .. closed.Effects]);
            case AttemptStatus.AwaitingReboot:
                return Transition.To(cleared with { Phase = Phase.AwaitingRestart, CurrentAttemptId = r.AttemptId, Restart = null }, effects);
            case AttemptStatus.Failed or AttemptStatus.VerifyFailed or AttemptStatus.RolledBack or AttemptStatus.Undone or AttemptStatus.UndoConflict:
                return Transition.To(cleared with { Phase = Phase.Proposing, CurrentAttemptId = null, LastErrorCode = r.MessageKey ?? r.Status.ToString() }, effects);
            default:
                if (r.Deadman is { Status: DeadmanStatus.Armed } armed)
                {
                    effects.Add(new WatchDeadmanEffect(r.AttemptId, armed.DeadlineUtc));
                    return Transition.To(cleared with { Phase = Phase.Verifying, CurrentAttemptId = r.AttemptId }, effects);
                }

                return Transition.To(cleared with { Phase = Phase.AwaitingOutcome, CurrentAttemptId = r.AttemptId }, effects);
        }
    }

    private static Transition OnDeadmanResult(SessionState s, FixRunResult r)
    {
        if (!Same(r.AttemptId, s.CurrentAttemptId))
        {
            return Transition.Ignore(s);
        }

        if (r.Status == AttemptStatus.InDoubt)
        {
            return Close(s with { Attempts = Update(s.Attempts, r.AttemptId, a => a with { Status = AttemptStatus.InDoubt }) }, Phase.NeedsAttention, SessionCodes.FixInDoubt);
        }

        if (r.Deadman?.Status == DeadmanStatus.Confirmed && r.Status is AttemptStatus.Verified or AttemptStatus.Applied)
        {
            return Transition.To(s with
            {
                Phase = Phase.AwaitingOutcome,
                Attempts = Update(s.Attempts, r.AttemptId, a => a with { Status = r.Status, VerifiedUtc = a.VerifiedUtc ?? s.UpdatedUtc }),
            });
        }

        if (r.Deadman?.Status == DeadmanStatus.Armed)
        {
            return Transition.Ignore(s);
        }

        var status = r.Status is AttemptStatus.Verified or AttemptStatus.Applied ? AttemptStatus.RolledBack : r.Status;
        return Transition.To(s with
        {
            Phase = Phase.Proposing,
            CurrentAttemptId = null,
            Attempts = Update(s.Attempts, r.AttemptId, a => a with { Status = status }),
            LastErrorCode = SessionCodes.DeadmanReverted,
        });
    }

    private static Transition OnResumeStarted(SessionState s, ResumeStarted e, MachineContext ctx)
    {
        if (!IsResumable(s))
        {
            // A stale task: clean it up, change nothing else.
            return s.Resume.Registered || s.Resume.Mechanism != ResumeMechanism.None
                ? Transition.To(s with { Resume = s.Resume with { Registered = false } }, new UnregisterResumeEffect())
                : Transition.Ignore(s);
        }

        if (ctx.Now > s.ExpiresUtc)
        {
            return Close(s, Phase.NeedsAttention, SessionCodes.Expired);
        }

        if (s.Resume.Attempts >= s.Resume.MaxAttempts || s.Resume.Attempts >= SessionLimits.MaxResumeAttempts)
        {
            return Close(s, Phase.NeedsAttention, SessionCodes.ResumeTooManyAttempts);
        }

        var real = s.Phase == Phase.Resuming || s.Restart is null || BootIdentities.IsRealRestart(s.Restart.BootAtRequest, e.Boot);
        if (!real)
        {
            // "Your PC didn't fully restart. Use Restart, not Shut down." Not counted; tasks stay; expiry still caps it.
            var phase = s.Phase == Phase.RestartRequested ? (s.Guide?.AwaitingFirmwareRestart == true ? Phase.GuidedStep : Phase.AwaitingRestart) : s.Phase;
            return Transition.To(s with
            {
                Phase = phase,
                Restart = s.Restart! with { ShutdownInitiatedUtc = null },
                Resume = s.Resume with { LastSeenBoot = e.Boot },
                LastErrorCode = SessionCodes.ResumeNotRealRestart,
            });
        }

        // Counted and persisted in this transition, before recovery or verification runs.
        return Transition.To(s with
        {
            Phase = Phase.Resuming,
            Resume = s.Resume with { Attempts = s.Resume.Attempts + 1, LastSeenBoot = e.Boot, LastAttemptUtc = ctx.Now },
            LastErrorCode = null,
        });
    }

    private static Transition OnRecovery(SessionState s, RecoveryReport report, MachineContext ctx)
    {
        // 1. The journal is the source of truth: take each listed attempt's status from its undo-center row, and adopt rows
        //    the state does not list (finished while the engine went down).
        var attempts = s.Attempts
            .Select(a => ctx.AttemptRows.FirstOrDefault(r => Same(r.AttemptId, a.AttemptId)) is { } row && row.Status != a.Status
                ? a with { Status = row.Status, RebootPending = row.Status == AttemptStatus.AwaitingReboot && a.RebootPending }
                : a)
            .ToList();
        foreach (var row in ctx.AttemptRows.Where(r => !attempts.Exists(a => Same(a.AttemptId, r.AttemptId))))
        {
            attempts.Add(new FixAttempt
            {
                AttemptId = row.AttemptId,
                FixId = row.FixId,
                BindingHash = ctx.Preview is { } p && Same(p.FixId, row.FixId) ? p.BindingHash : string.Empty,
                Status = row.Status,
                Outcome = row.Outcome,
                Reversibility = row.Reversibility,
                RebootPending = row.Status == AttemptStatus.AwaitingReboot,
                AppliedUtc = row.AppliedUtc,
                HypothesisIds = Same(row.FixId, s.CurrentFixId) ? ctx.HypothesisIds : [],
                PlanId = Same(row.FixId, s.CurrentFixId) ? s.CurrentProposal?.PlanId : null,
            });
        }

        // 2. Apply the recovery resolutions; an in-flight change rolled forward becomes an attempt.
        var mine = report.Items.Where(i => attempts.Exists(a => Same(a.AttemptId, i.AttemptId)) || (s.Phase == Phase.Applying && Same(i.FixId, s.CurrentFixId))).ToList();
        foreach (var item in mine.Where(i => !attempts.Exists(a => Same(a.AttemptId, i.AttemptId)) && i.Resolution is RecoveryResolution.RolledForward or RecoveryResolution.InDoubt))
        {
            attempts.Add(new FixAttempt
            {
                AttemptId = item.AttemptId,
                FixId = item.FixId,
                BindingHash = ctx.Preview?.BindingHash ?? string.Empty,
                Status = item.Resolution == RecoveryResolution.InDoubt ? AttemptStatus.InDoubt : AttemptStatus.Applied,
                Reversibility = ctx.Preview?.Reversibility ?? Reversibility.BestEffort,
                AppliedUtc = ctx.Now,
                HypothesisIds = ctx.HypothesisIds,
                PlanId = s.CurrentProposal?.PlanId,
            });
        }

        for (var i = 0; i < attempts.Count; i++)
        {
            var a = attempts[i];
            var item = mine.LastOrDefault(x => Same(x.AttemptId, a.AttemptId));
            if (item is null)
            {
                continue;
            }

            attempts[i] = a with
            {
                Status = item.Resolution switch
                {
                    RecoveryResolution.InDoubt => AttemptStatus.InDoubt,
                    RecoveryResolution.RolledBack or RecoveryResolution.NotApplied => a.Status is AttemptStatus.Undone ? a.Status : AttemptStatus.RolledBack,
                    _ => a.Status,
                },
                RebootPending = item.Resolution is RecoveryResolution.RolledBack or RecoveryResolution.NotApplied ? false : a.RebootPending,
            };
        }

        IReadOnlyList<FixAttempt> list = attempts;
        var next = attempts.Count == s.Attempts.Count && attempts.SequenceEqual(s.Attempts) ? s : s with { Attempts = list };
        if (list.Any(a => a.Status == AttemptStatus.InDoubt && !s.Attempts.Any(o => Same(o.AttemptId, a.AttemptId) && o.Status == AttemptStatus.InDoubt)))
        {
            return Close(next with { CurrentPreviewId = null, CurrentProposal = null, CurrentFixId = null }, Phase.NeedsAttention, SessionCodes.RecoveryInDoubt);
        }

        switch (s.Phase)
        {
            case Phase.Resuming:
                var verify = (s.Restart?.AttemptsAwaitingPostRebootVerify ?? [])
                    .Where(id => list.Any(a => Same(a.AttemptId, id) && a.Status is not (AttemptStatus.RolledBack or AttemptStatus.Undone)))
                    .ToList();
                return Transition.To(next, new PostRebootVerifyEffect(verify));
            case Phase.Applying:
                return RecoverApplying(s, next);
            case Phase.Verifying:
                var current = list.FirstOrDefault(a => Same(a.AttemptId, s.CurrentAttemptId));
                return current is null || current.Status is AttemptStatus.RolledBack or AttemptStatus.Undone or AttemptStatus.Failed or AttemptStatus.VerifyFailed
                    ? Transition.To(next with { Phase = Phase.Proposing, CurrentAttemptId = null, LastErrorCode = SessionCodes.DeadmanReverted })
                    : Transition.To(next with { Phase = Phase.AwaitingOutcome });
            case Phase.Scanning:
                return Transition.To(next with { Phase = PhaseBeforeScan(next), LastErrorCode = SessionCodes.ScanInterrupted });
            case Phase.AwaitingConsent:
                // Previews, nonces and second-consent nonces live in engine memory only: a restarted engine never honours
                // an old consent dialog. The player opens the fix again.
                return Transition.To(next with { Phase = Phase.Proposing, CurrentFixId = null, CurrentPreviewId = null, CurrentProposal = null });
            case Phase.RestartRequested:
                // Not a reboot (that path goes through ResumeStarted first): the restart never ran, or did not happen.
                return Transition.To(next with
                {
                    Phase = s.Guide?.AwaitingFirmwareRestart == true ? Phase.GuidedStep : Phase.AwaitingRestart,
                    Restart = s.Restart is null ? null : s.Restart with { ShutdownInitiatedUtc = null },
                    LastErrorCode = s.Restart?.ShutdownInitiatedUtc is null ? null : SessionCodes.RestartDidNotHappen,
                });
            case Phase.AwaitingRestart when !list.Any(a => a.Status == AttemptStatus.AwaitingReboot) && s.Guide is null:
                // The change the restart was for is gone (recovery rolled it back): nothing to restart for.
                var back = next with { Phase = Phase.Proposing, CurrentAttemptId = null, Restart = null };
                return back.Resume.Registered || back.Resume.Mechanism != ResumeMechanism.None
                    ? Transition.To(back with { Resume = back.Resume with { Registered = false } }, new UnregisterResumeEffect())
                    : Transition.To(back);
            case Phase.Monitoring:
                // The monitor process died with the engine; its partial data is not used.
                return Transition.To(next with { Phase = PhaseBeforeScan(next with { Guide = null }) });
            case Phase.RolledBack:
                // "Undo all" was persisted but may not have finished: finish it (undo is idempotent).
                var redo = UndoEffects(next);
                var cleanup = next.Resume.Mechanism != ResumeMechanism.None || next.Resume.Registered;
                return redo.Count == 0 && !cleanup
                    ? Changed(s, next)
                    : Transition.To(next with { Resume = next.Resume with { Registered = false } }, [.. redo, .. (cleanup ? new SessionEffect[] { new UnregisterResumeEffect() } : [])]);
            default:
                if (IsClosed(s.Phase) && (next.Resume.Mechanism != ResumeMechanism.None || next.Resume.Registered))
                {
                    return Transition.To(next with { Resume = next.Resume with { Registered = false } }, new UnregisterResumeEffect());
                }

                return Changed(s, next);
        }
    }

    private static Transition RecoverApplying(SessionState before, SessionState next)
    {
        // The attempt id is only known once the runner returned: the in-flight attempt is the newest one for the current
        // fix that the state did not list before recovery (adopted from the undo center or rolled forward).
        var attempt = next.Attempts.LastOrDefault(a => Same(a.FixId, before.CurrentFixId) && !before.Attempts.Any(o => Same(o.AttemptId, a.AttemptId)));
        var cleared = next with { CurrentPreviewId = null, CurrentProposal = null, CurrentFixId = null, LastErrorCode = SessionCodes.RecoveryInterrupted };
        return attempt?.Status switch
        {
            AttemptStatus.Verified or AttemptStatus.Applied => Transition.To(cleared with { Phase = Phase.AwaitingOutcome, CurrentAttemptId = attempt.AttemptId }),
            AttemptStatus.AwaitingReboot => Transition.To(cleared with { Phase = Phase.AwaitingRestart, CurrentAttemptId = attempt.AttemptId, Restart = null }),
            _ => Transition.To(cleared with { Phase = Phase.Proposing, CurrentAttemptId = null }),
        };
    }

    private static Transition OnPostRebootVerify(SessionState s, PostRebootVerifyCompleted e, MachineContext ctx)
    {
        if (s.Phase != Phase.Resuming)
        {
            return Transition.Ignore(s);
        }

        var attempts = s.Attempts;
        string? last = null;
        foreach (var r in e.Results)
        {
            last = r.AttemptId;
            attempts = Update(attempts, r.AttemptId, a => a with
            {
                Status = r.Status switch
                {
                    VerifyStatus.Passed => AttemptStatus.Verified,
                    VerifyStatus.Failed => AttemptStatus.VerifyFailed,
                    _ => AttemptStatus.Applied,
                },
                RebootPending = false,
                VerifiedUtc = r.Status == VerifyStatus.Passed ? ctx.Now : a.VerifiedUtc,
            });
        }

        var next = s with { Attempts = attempts, Restart = null };
        if (s.Guide is { AwaitingFirmwareRestart: true } guide)
        {
            return Transition.To(next with { Phase = Phase.GuidedStep, Guide = guide with { AwaitingFirmwareRestart = false } });
        }

        var current = last ?? s.CurrentAttemptId;
        var pending = current is not null && attempts.Any(a => Same(a.AttemptId, current) && a.Outcome == FixOutcome.Pending && a.Status is AttemptStatus.Verified or AttemptStatus.Applied);
        if (pending)
        {
            return Transition.To(next with { Phase = Phase.AwaitingOutcome, CurrentAttemptId = current });
        }

        return next.SnapshotId is { } snapshot
            ? Transition.To(next with { Phase = Phase.Proposing, CurrentAttemptId = null }, new AnalyzeEffect(snapshot))
            : Transition.To(next with { Phase = Phase.Proposing, CurrentAttemptId = null });
    }

    private static Transition OnMonitorCompleted(SessionState s, MonitorCompleted e)
    {
        if (s.Phase != Phase.Monitoring || s.Monitor is null)
        {
            return Transition.Ignore(s);
        }

        var next = s with { Monitor = s.Monitor with { SummaryId = e.SummaryId } };
        var phase = PhaseBeforeScan(next with { Guide = null });
        if (phase == Phase.AwaitingOutcome)
        {
            return Transition.To(next with { Phase = Phase.AwaitingOutcome });
        }

        return next.SnapshotId is { } snapshot
            ? Transition.To(next with { Phase = Phase.Analyzed }, new AnalyzeEffect(snapshot))
            : Transition.To(next with { Phase = Phase.Intake });
    }

    private static Transition OnGuideVerified(SessionState s, GuideVerifyCompleted e)
    {
        if (s.Phase != Phase.GuidedStep || s.Guide is null || !Same(s.Guide.GuideId, e.GuideId))
        {
            return Transition.Ignore(s);
        }

        return e.Result switch
        {
            Tri.True => Transition.To(s with { Phase = s.SnapshotId is null ? Phase.Intake : Phase.Analyzed, Guide = null, LastErrorCode = null }),
            Tri.False => Transition.To(s with { LastErrorCode = SessionCodes.GuideVerifyFailed }),
            _ => Transition.To(s with { LastErrorCode = SessionCodes.GuideVerifyUnknown }),
        };
    }

    private static Transition OnUndoCompleted(SessionState s, UndoResult r)
    {
        var known = s.Attempts.Any(a => Same(a.AttemptId, r.AttemptId));
        var attempts = !known ? s.Attempts : Update(s.Attempts, r.AttemptId, a => a with
        {
            Status = r.Status switch
            {
                UndoStatus.Undone => AttemptStatus.Undone,
                UndoStatus.NothingToUndo => a.Status is AttemptStatus.Verified or AttemptStatus.Applied or AttemptStatus.AwaitingReboot ? AttemptStatus.Undone : a.Status,
                UndoStatus.Conflict => AttemptStatus.UndoConflict,
                _ => a.Status,
            },
            RebootPending = r.Status is UndoStatus.Undone or UndoStatus.NothingToUndo ? false : a.RebootPending,
        });
        var next = s with
        {
            Attempts = attempts,
            CurrentProposal = s.Phase == Phase.AwaitingConsent ? s.CurrentProposal : null,
            LastErrorCode = r.Status is UndoStatus.Undone or UndoStatus.NothingToUndo ? s.LastErrorCode : r.MessageKey ?? "undo." + r.Status.ToString(),
        };
        if (s.Phase == Phase.RolledBack && r.Status is UndoStatus.Failed or UndoStatus.PartiallyUndone or UndoStatus.Conflict)
        {
            return Close(next, Phase.NeedsAttention, SessionCodes.UndoIncomplete);
        }

        // Undoing the change a restart was waiting for: there is nothing left to restart for.
        if (s.Phase == Phase.AwaitingRestart && !attempts.Any(a => a.Status == AttemptStatus.AwaitingReboot))
        {
            var back = next with { Phase = Phase.Proposing, CurrentAttemptId = null, Restart = null };
            return back.Resume.Registered || back.Resume.Mechanism != ResumeMechanism.None
                ? Transition.To(back with { Resume = back.Resume with { Registered = false } }, new UnregisterResumeEffect())
                : Transition.To(back);
        }

        return Changed(s, next);
    }

    private static Transition OnUiConnected(SessionState s)
    {
        var waiting = s.Phase is Phase.RestartRequested or Phase.AwaitingRestart || (s.Phase == Phase.GuidedStep && s.Guide?.AwaitingFirmwareRestart == true);
        if (waiting || (!s.Resume.Registered && s.Resume.Mechanism == ResumeMechanism.None))
        {
            return Transition.Ignore(s);
        }

        return Transition.To(s with { Resume = s.Resume with { Registered = false } }, new UnregisterResumeEffect());
    }

    private static Transition OnResumeRegistered(SessionState s, ResumeRegistered e, MachineContext ctx)
    {
        var mechanism = e.Registration.Mechanism;
        var registered = mechanism is ResumeMechanism.ScheduledTasks or ResumeMechanism.RunOnceUi;
        if (IsClosed(s.Phase))
        {
            // Closed while registering: remove whatever was just created.
            return Transition.To(s with { Resume = s.Resume with { Mechanism = mechanism, Registered = false } }, new UnregisterResumeEffect());
        }

        var next = s with { Resume = s.Resume with { Mechanism = mechanism, Registered = registered } };
        if (e.ThenRestart is { } token && s.Phase == Phase.RestartRequested && s.Restart is { ShutdownInitiatedUtc: null } restart && Same(token.SessionId, s.SessionId))
        {
            // Persist "right before the restart API is called", then call it.
            return Transition.To(next with { Restart = restart with { ShutdownInitiatedUtc = ctx.Now } }, new RestartEffect(token));
        }

        return Transition.To(next);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    // A new restart request (a human click) starts a new resume budget. Mechanism is set BEFORE registering, so a crash
    // between registering and persisting the result still leaves "something may be registered" on record and every
    // close or start-up removes it.
    private static ResumeInfo ResumeForNewRequest(ResumeInfo r)
        => r with { Attempts = 0, Mechanism = r.Mechanism == ResumeMechanism.None ? ResumeMechanism.ScheduledTasks : r.Mechanism };

    private static bool IsBusy(Phase phase) => phase is Phase.Applying or Phase.Scanning or Phase.RestartRequested or Phase.Resuming;

    private static Transition Close(SessionState s, Phase phase, string? reasonKey)
    {
        var mayBeRegistered = s.Resume.Registered || s.Resume.Mechanism != ResumeMechanism.None;
        var next = s with
        {
            Phase = phase,
            Resume = s.Resume with { Registered = false },
            NeedsAttentionReasonKey = phase == Phase.NeedsAttention ? reasonKey : s.NeedsAttentionReasonKey,
            Restart = IsTerminal(phase) ? null : s.Restart,
        };
        return mayBeRegistered ? Transition.To(next, new UnregisterResumeEffect()) : Transition.To(next);
    }

    private static Transition Changed(SessionState before, SessionState after) => after == before ? Transition.Ignore(before) : Transition.To(after);

    private static SessionState? Restarted(SessionState s, MachineContext ctx)
        => ctx.NewSessionId is { } id && !Same(id, s.SessionId)
            ? NewSession(id, ctx.Now, s.Privilege, ctx.KbVersion ?? s.KbVersion, s.PlayerSid) with { Revision = s.Revision }
            : null;

    private static List<SessionEffect> UndoEffects(SessionState s)
        => [.. s.Attempts
            .Where(a => a.Reversibility != Reversibility.OneWay && a.Status is AttemptStatus.Verified or AttemptStatus.Applied or AttemptStatus.AwaitingReboot or AttemptStatus.Applying)
            .Reverse()
            .Select(a => (SessionEffect)new UndoEffect(a.AttemptId, UndoMode.IfUnchanged))];

    private static IReadOnlyList<string> AwaitingReboot(SessionState s)
        => [.. s.Attempts.Where(a => a.RebootPending && a.Status == AttemptStatus.AwaitingReboot).Select(a => a.AttemptId)];

    private static IReadOnlyList<FixAttempt> Update(IReadOnlyList<FixAttempt> attempts, string attemptId, Func<FixAttempt, FixAttempt> change)
        => [.. attempts.Select(a => Same(a.AttemptId, attemptId) ? change(a) : a)];

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
}
