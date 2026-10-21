using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Testing.Fakes;

/// <summary>
/// An in-process <see cref="IEngineRpc"/> for A6 (UI) and the CLI/MCP shells: a simplified session flow driven by the
/// other fakes, enough to click through every screen in EN/TR (Intake → Scanning → Analyzed → consent → Applying →
/// deadman/restart → outcome → Completed, plus guides, monitor, undo, plans, export). It enforces the same
/// <see cref="CommandPolicy"/> as the real engine (set <see cref="ClientOrigin"/> to Cli/Mcp to act as an agent), but it
/// is NOT the real session machine (A4 owns that): transitions are simple and synchronous, notifications inline.
/// </summary>
public sealed class FakeEngineRpc : IEngineRpc
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FixRunResult> _outcomes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MonitorSummary> _summaries = new(StringComparer.Ordinal);
    private SessionState _session;
    private AnalysisResult? _analysis;
    private ScanSnapshot? _snapshot;
    private FixPreview? _preview;
    private long _seq;
    private string? _restartNonce;
    private string? _clientLabel;
    private Phase _phaseBeforeScan = Phase.Intake;

    /// <summary>Creates the fake engine.</summary>
    /// <param name="fixtureName">Fixture the fake scan returns (default Realtek).</param>
    /// <param name="privilege">Elevated or limited.</param>
    /// <param name="clock">Clock (default a new <see cref="FakeClock"/>).</param>
    public FakeEngineRpc(string fixtureName = Fixtures.RealtekStutter, PrivilegeMode privilege = PrivilegeMode.Elevated, FakeClock? clock = null)
    {
        Clock = clock ?? new FakeClock();
        Ids = new SequentialIdGenerator(Clock);
        Kb = FakeKnowledgeBase.Sample();
        Scan = FakeScanService.FromFixture(fixtureName);
        Analyzer = new FakeAnalyzer();
        FixRunner = new FakeFixRunner(Kb, Clock, Ids);
        Monitor = new FakeMonitorService(Clock, Ids);
        Recovery = new FakeRecovery(Clock);
        RestorePoints = new FakeRestorePointService(Clock);
        Privilege = privilege;
        _session = NewSession();
        FixRunner.SessionId = _session.SessionId;
    }

    /// <inheritdoc />
    public event EventHandler<EngineNotification>? Notification;

    /// <summary>Clock.</summary>
    public FakeClock Clock { get; }

    /// <summary>Ids.</summary>
    public SequentialIdGenerator Ids { get; }

    /// <summary>KB.</summary>
    public FakeKnowledgeBase Kb { get; }

    /// <summary>Scan service.</summary>
    public FakeScanService Scan { get; }

    /// <summary>Analyzer.</summary>
    public FakeAnalyzer Analyzer { get; }

    /// <summary>Fix runner.</summary>
    public FakeFixRunner FixRunner { get; }

    /// <summary>Monitor.</summary>
    public FakeMonitorService Monitor { get; }

    /// <summary>Recovery.</summary>
    public FakeRecovery Recovery { get; }

    /// <summary>Restore points.</summary>
    public FakeRestorePointService RestorePoints { get; }

    /// <summary>Privilege the fake engine reports.</summary>
    public PrivilegeMode Privilege { get; }

    /// <summary>The origin the fake "verifies" for its caller (the real engine derives it from the pipe). Default UI.</summary>
    public EventOrigin ClientOrigin { get; set; } = EventOrigin.Ui;

    /// <summary>Every command received, stamped with its origin, in order.</summary>
    public List<SessionCommand> Received { get; } = [];

    /// <inheritdoc />
    public async Task<EngineHello> HelloAsync(ClientHello hello, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.ProtocolVersion != EngineProtocol.Version)
        {
            throw new InvalidOperationException($"Protocol mismatch: engine {EngineProtocol.Version}, client {hello.ProtocolVersion}.");
        }

        _clientLabel = hello.Label;
        var recovery = await Recovery.RunAsync(cancellationToken).ConfigureAwait(false);
        return new EngineHello
        {
            ProtocolVersion = EngineProtocol.Version,
            EngineVersion = FixtureSnapshots.AppVersion,
            KbVersion = Kb.Version.Version,
            Privilege = Privilege,
            EngineProcessId = 4242,
            ConsentAllowed = ClientOrigin == EventOrigin.Ui,
            Recovery = recovery,
            Session = _session,
            Capabilities = Privilege == PrivilegeMode.Elevated ? ["fixes", "monitor", "plans"] : ["plans"],
        };
    }

    /// <inheritdoc />
    public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<SessionState> GetSessionAsync(CancellationToken cancellationToken) => Task.FromResult(_session);

    /// <inheritdoc />
    public Task<ScanSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

    /// <inheritdoc />
    public Task<AnalysisResult?> GetAnalysisAsync(CancellationToken cancellationToken) => Task.FromResult(_analysis);

    /// <inheritdoc />
    public Task<FixPreview?> GetPreviewAsync(string previewId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        var preview = FixRunner.FindPreview(previewId);
        return Task.FromResult(preview is null || ClientOrigin == EventOrigin.Ui ? preview : preview with { ConsentNonce = null });
    }

    /// <inheritdoc />
    public Task<MonitorSummary?> GetMonitorSummaryAsync(string summaryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summaryId);
        lock (_gate)
        {
            return Task.FromResult(_summaries.TryGetValue(summaryId, out var s) ? s : null);
        }
    }

    /// <inheritdoc />
    public async Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding, CancellationToken cancellationToken)
    {
        var preview = await FixRunner.PreviewAsync(fixId, binding, CurrentProposer(), cancellationToken).ConfigureAwait(false);
        return ClientOrigin == EventOrigin.Ui ? preview : preview with { ConsentNonce = null };
    }

    /// <inheritdoc />
    public Task<FixRunResult?> WaitForOutcomeAsync(string previewId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (timeoutSeconds <= 0 || timeoutSeconds > EngineProtocol.MaxWaitSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        lock (_gate)
        {
            return Task.FromResult(_outcomes.TryGetValue(previewId, out var r) ? r : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken)
        => FixRunner.ListAttemptsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<RestartPrompt> PrepareRestartAsync(RestartKind kind, CancellationToken cancellationToken)
    {
        _restartNonce = Ids.NewId();
        return Task.FromResult(new RestartPrompt
        {
            Nonce = _restartNonce,
            Kind = kind,
            Readiness = new RebootReadiness
            {
                OnAcPower = Tri.True,
                WindowsUpdateRebootPending = Tri.False,
                WindowsUpdateInstalling = Tri.False,
                FastStartupEnabled = Tri.True,
                TaskSchedulerAvailable = Tri.True,
                InteractiveUserIsAdmin = Tri.True,
                FirmwareRestartSupported = Tri.True,
                PlannedMechanism = ResumeMechanism.ScheduledTasks,
                WarningKeys = ["restart.use_restart_not_shutdown"],
            },
            Allowed = true,
            ExpiresUtc = Clock.GetUtcNow() + SessionLimits.TokenLifetime,
        });
    }

    /// <inheritdoc />
    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_snapshot is null)
        {
            return Task.FromResult(new ExportResult { Status = ExportStatus.Failed });
        }

        var top = _analysis?.Hypotheses.FirstOrDefault();
        var text = request.Kind switch
        {
            ExportKind.ReportMarkdown or ExportKind.AiDigest =>
                $"**VTDiag report** (fake) · KB {Kb.Version.Version}\n\nTop suspect: {(top is null ? "unknown" : Kb.Text(top.TitleKey, request.Language))}\n\n_{Kb.Text("report.disclaimer.not_affiliated", request.Language)}_",
            ExportKind.ReportJson => VtdJson.Serialize(_analysis),
            _ => "<!doctype html><html><body><p>VTDiag fake report</p></body></html>",
        };
        var bundle = request.Kind is ExportKind.ScanBundle or ExportKind.DebugBundle;
        return Task.FromResult(new ExportResult
        {
            Status = ExportStatus.Ok,
            FileName = request.Kind == ExportKind.DebugBundle ? "VTDiag-fake.vtddebug" : bundle ? "VTDiag-fake.vtdscan" : "VTDiag-Report-fake.txt",
            TextContent = bundle ? null : text,
            Base64Content = bundle ? Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) : null,
            Sha256 = CanonicalHash.OfText(text),
            Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = RedactionConstants.RulesVersion, ExcludedSections = request.ExcludedSections },
        });
    }

    /// <summary>Log entries the UI submitted (tests read them).</summary>
    public List<ClientLogEntry> ClientLog { get; } = [];

    /// <summary>Debug mode as last set.</summary>
    public bool DebugMode { get; private set; }

    /// <inheritdoc />
    public Task SubmitClientLogAsync(IReadOnlyList<ClientLogEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ClientLog.AddRange(entries);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> SetDebugModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        DebugMode = enabled;
        return Task.FromResult(enabled);
    }

    /// <inheritdoc />
    public Task<PlanValidation> ImportPlanAsync(string planText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planText);
        FixPlan plan;
        var trust = PlanTrust.Unsigned;
        try
        {
            if (planText.Contains(SignedPlanEnvelope.CurrentFormat, StringComparison.Ordinal))
            {
                var envelope = VtdJson.DeserializeLenient<SignedPlanEnvelope>(planText);
                plan = VtdJson.DeserializeLenient<FixPlan>(Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload)));
                trust = Kb.TrustedHelpers.Any(k => string.Equals(k.PublicKey, envelope.PublicKey, StringComparison.Ordinal)) ? PlanTrust.VerifiedHelper : PlanTrust.SignedUnknownKey;
            }
            else
            {
                plan = VtdJson.DeserializeLenient<FixPlan>(planText);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return Task.FromResult(new PlanValidation { PlanId = "invalid", Valid = false, Trust = PlanTrust.Unsigned, Issues = [new PlanIssue { Code = "schema" }] });
        }

        var issues = new List<PlanIssue>();
        if (plan.Steps.Count > PlanLimits.MaxSteps)
        {
            issues.Add(new PlanIssue { Code = "too_many_steps", Path = "/steps" });
        }

        if (plan.ExpiresUtc < Clock.GetUtcNow() || plan.ExpiresUtc - plan.CreatedUtc > PlanLimits.MaxLifetime)
        {
            issues.Add(new PlanIssue { Code = "expired", Path = "/expiresUtc" });
        }

        var steps = plan.Steps.Select((s, i) => s switch
        {
            PlanFixStep f => new PlanStepValidation { Index = i, Kind = PlanStepKind.Fix, Status = Kb.FindFix(f.FixId) is null ? PlanStepStatus.Greyed : PlanStepStatus.Ok, ReasonKey = Kb.FindFix(f.FixId) is null ? "plan.step.unknown_fix" : null },
            PlanGuideStep g => new PlanStepValidation { Index = i, Kind = PlanStepKind.Guide, Status = Kb.FindGuide(g.GuideId) is null ? PlanStepStatus.Greyed : PlanStepStatus.Ok },
            PlanQuestionStep q => new PlanStepValidation { Index = i, Kind = PlanStepKind.Question, Status = Kb.FindQuestion(q.QuestionId) is null ? PlanStepStatus.Greyed : PlanStepStatus.Ok },
            _ => new PlanStepValidation { Index = i, Kind = PlanStepKind.Monitor, Status = PlanStepStatus.Ok },
        }).ToList();

        return Task.FromResult(new PlanValidation { PlanId = plan.PlanId, Valid = issues.Count == 0, Trust = trust, Steps = steps, Issues = issues });
    }

    /// <inheritdoc />
    public async Task<SessionState> SendAsync(SessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var stamped = command with { Origin = ClientOrigin };
        if (!CommandPolicy.IsAllowed(stamped, ClientOrigin))
        {
            throw new InvalidOperationException("command.not_allowed");
        }

        Received.Add(stamped);
        switch (stamped)
        {
            case IntakeSubmitted e:
                Update(s => s with { Intake = e.Intake });
                break;
            case ScanRequested or RescanRequested:
                await RunScanAsync(cancellationToken).ConfigureAwait(false);
                break;
            case ScanCancelRequested:
                // The fake scan is synchronous, so there is never a scan to cancel by the time this arrives; when the
                // session still says Scanning (a test set it), go back to where the player was.
                if (_session.Phase == Phase.Scanning)
                {
                    Update(s => s with { Phase = _phaseBeforeScan });
                }

                break;
            case MonitorMarkBadRequested:
                if (_session.Phase == Phase.Monitoring)
                {
                    await Monitor.MarkBadAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            case QuestionAnswered e:
                Update(s => s with
                {
                    QuestionsAsked = s.QuestionsAsked + 1,
                    Intake = s.Intake with { Answers = new Dictionary<string, string>(s.Intake.Answers, StringComparer.Ordinal) { [e.QuestionId] = e.OptionId } },
                });
                Reanalyze();
                break;
            case FixSelected e:
                var proposer = CurrentProposer() with
                {
                    PlanId = e.PlanId is not null && string.Equals(e.PlanId, _session.ImportedPlanId, StringComparison.Ordinal) ? e.PlanId : null,
                };
                _preview = await FixRunner.PreviewAsync(e.FixId, e.Binding, proposer, cancellationToken).ConfigureAwait(false);
                Update(s => s with { Phase = Phase.AwaitingConsent, CurrentFixId = e.FixId, CurrentPreviewId = _preview.PreviewId, CurrentProposal = proposer });
                Raise(new ConsentRequestedNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Kind = ConsentKind.Fix, PreviewId = _preview.PreviewId, FixId = e.FixId, ProposedBy = proposer });
                break;
            case FixSkipped e:
                Update(s => s with { Phase = Phase.Proposing, SkippedFixIds = [.. s.SkippedFixIds, e.FixId], CurrentFixId = null, CurrentPreviewId = null, CurrentProposal = null });
                break;
            case ConsentGiven e:
                await ApplyAsync(e.Token, cancellationToken).ConfigureAwait(false);
                break;
            case DeadmanAnswered e:
                var dm = await FixRunner.ConfirmDeadmanAsync(e.AttemptId, e.Keep, cancellationToken).ConfigureAwait(false);
                Update(s => s with { Phase = e.Keep ? Phase.AwaitingOutcome : Phase.Proposing, Attempts = Replace(s.Attempts, dm.AttemptId, a => a with { Status = dm.Status }) });
                break;
            case RestartNowClicked e:
                if (_restartNonce is null || !string.Equals(e.Token.Nonce, _restartNonce, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("restart.token_invalid");
                }

                _restartNonce = null;
                Update(s => s with
                {
                    Phase = Phase.RestartRequested,
                    Restart = new RestartInfo { Kind = e.Token.Kind, ReasonKey = "restart.fix_needs_restart", BootAtRequest = new BootIdentity { BootId = 412, LastBootUpTimeUtc = Clock.GetUtcNow().AddHours(-3), Kind = BootKind.Cold }, RequestedUtc = Clock.GetUtcNow(), ShutdownInitiatedUtc = Clock.GetUtcNow() },
                    Resume = s.Resume with { Mechanism = ResumeMechanism.ScheduledTasks, Registered = true },
                });
                break;
            case RestartLaterChosen:
                Update(s => s with { Phase = Phase.AwaitingRestart, Resume = s.Resume with { Mechanism = ResumeMechanism.ScheduledTasks, Registered = true } });
                break;
            case OutcomeReported e:
                Update(s => s with
                {
                    Phase = e.Outcome == FixOutcome.Fixed ? Phase.Completed : Phase.Proposing,
                    Attempts = Replace(s.Attempts, e.AttemptId, a => a with { Outcome = e.Outcome }),
                });
                break;
            case MonitorRequested e:
                await Monitor.StartAsync(new MonitorOptions { SessionId = _session.SessionId, Purpose = e.Purpose }, cancellationToken).ConfigureAwait(false);
                Update(s => s with { Phase = Phase.Monitoring, Monitor = new MonitorRunInfo { Purpose = e.Purpose, StartedUtc = Clock.GetUtcNow() } });
                await foreach (var tick in Monitor.TicksAsync(cancellationToken).ConfigureAwait(false))
                {
                    Raise(new MonitorTickNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Tick = tick });
                }

                var summary = await Monitor.StopAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _summaries[summary.SummaryId] = summary;
                }

                Update(s => s with { Phase = Phase.AwaitingOutcome, Monitor = s.Monitor! with { SummaryId = summary.SummaryId } });
                break;
            case GuideStarted e:
                Update(s => s with { Phase = Phase.GuidedStep, Guide = new GuideProgress { GuideId = e.GuideId, BranchId = e.BranchId, StartedUtc = Clock.GetUtcNow() } });
                break;
            case GuideStepCompleted e:
                Update(s => s with { Guide = s.Guide is null ? null : s.Guide with { StepIndex = e.StepIndex + 1 } });
                break;
            case UndoProposed e:
                var attempt = _session.Attempts.FirstOrDefault(a => string.Equals(a.AttemptId, e.AttemptId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("undo.unknown_attempt");
                Update(s => s with { CurrentProposal = CurrentProposer() });
                Raise(new ConsentRequestedNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Kind = ConsentKind.Undo, AttemptId = e.AttemptId, FixId = attempt.FixId, ProposedBy = CurrentProposer() });
                break;
            case UndoRequested e:
                var undo = await FixRunner.UndoAsync(e.AttemptId, e.Mode, cancellationToken).ConfigureAwait(false);
                Update(s => s with { CurrentProposal = null, Attempts = Replace(s.Attempts, e.AttemptId, a => a with { Status = undo.Status == UndoStatus.Undone ? AttemptStatus.Undone : a.Status }) });
                break;
            case UndoAllRequested:
                foreach (var a in _session.Attempts.Where(a => a.Status is AttemptStatus.Verified or AttemptStatus.Applied or AttemptStatus.AwaitingReboot))
                {
                    await FixRunner.UndoAsync(a.AttemptId, UndoMode.IfUnchanged, cancellationToken).ConfigureAwait(false);
                }

                Update(s => s with { Phase = Phase.RolledBack, Attempts = s.Attempts.Select(x => x with { Status = AttemptStatus.Undone }).ToList() });
                break;
            case PlanImported e:
                Update(s => s with { ImportedPlanId = e.PlanId });
                break;
            case AbandonRequested:
                Update(s => s with { Phase = Phase.Abandoned });
                break;
            default:
                break;
        }

        return _session;
    }

    /// <summary>Simulates the PC coming back after "Restart now": resume, post-reboot verify, then AwaitingOutcome.</summary>
    public SessionState SimulateResumeAfterReboot()
    {
        Update(s => s with
        {
            Phase = Phase.AwaitingOutcome,
            Restart = null,
            Resume = s.Resume with { Attempts = s.Resume.Attempts + 1, Registered = false, LastAttemptUtc = Clock.GetUtcNow() },
            Attempts = s.Attempts.Select(a => a.Status == AttemptStatus.AwaitingReboot ? a with { Status = AttemptStatus.Verified, RebootPending = false, VerifiedUtc = Clock.GetUtcNow() } : a).ToList(),
        });
        return _session;
    }

    private Proposer CurrentProposer() => new() { Origin = ClientOrigin, Label = _clientLabel };

    private async Task RunScanAsync(CancellationToken cancellationToken)
    {
        _phaseBeforeScan = _session.Phase;
        Update(s => s with { Phase = Phase.Scanning });
        var progress = new SyncProgress<ScanProgress>(p => Raise(new ScanProgressNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Progress = p }));
        var result = await Scan.ScanAsync(new ScanRequest { KbVersion = Kb.Version.Version, PackCollectors = Kb.Collectors, Player = FakePlayer.Default }, progress, cancellationToken).ConfigureAwait(false);
        _snapshot = result.Snapshot;
        Update(s => s with { SnapshotId = _snapshot.Meta.SnapshotId });
        Reanalyze();
    }

    private void Reanalyze()
    {
        if (_snapshot is null)
        {
            return;
        }

        _analysis = Analyzer.Analyze(new AnalysisInput
        {
            AnalysisId = Ids.NewId(),
            Snapshot = _snapshot,
            Intake = _session.Intake,
            AsOfUtc = Clock.GetUtcNow(),
            QuestionsAsked = _session.QuestionsAsked,
            SkippedFixIds = _session.SkippedFixIds,
        });
        Update(s => s with { Phase = Phase.Analyzed, AnalysisId = _analysis.AnalysisId });
    }

    private async Task ApplyAsync(ConsentToken token, CancellationToken cancellationToken)
    {
        if (Privilege == PrivilegeMode.Limited)
        {
            throw new InvalidOperationException("consent.limited_mode");
        }

        // Check the token BEFORE leaving AwaitingConsent: a refused consent (stale preview, replay, expiry) must leave the
        // session where the player can still act on it, never stuck in Applying.
        if (FixRunner.CheckConsent(token) is { } refused)
        {
            throw new InvalidOperationException(refused);
        }

        Update(s => s with { Phase = Phase.Applying });
        var progress = new SyncProgress<FixProgress>(p => Raise(new FixProgressNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Progress = p }));
        FixRunResult result;
        try
        {
            result = await FixRunner.ApplyAsync(token, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("consent.", StringComparison.Ordinal))
        {
            // Refused at apply (raced with the pre-check): back to the decision. Other errors (another fix running) are
            // not this fix's consent and leave the phase alone.
            Update(s => s with { Phase = Phase.AwaitingConsent });
            throw;
        }
        if (result.Status == AttemptStatus.AwaitingSecondConsent)
        {
            Update(s => s with { Phase = Phase.AwaitingConsent });
            Raise(new ConsentRequestedNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Kind = ConsentKind.SecondConsent, PreviewId = token.PreviewId, FixId = token.FixId, ProposedBy = _session.CurrentProposal ?? CurrentProposer(), SecondConsentNonce = result.SecondConsentNonce });
            return;
        }

        lock (_gate)
        {
            _outcomes[token.PreviewId] = result;
        }

        var attempt = new FixAttempt
        {
            AttemptId = result.AttemptId,
            FixId = result.FixId,
            BindingHash = token.BindingHash,
            Status = result.Status,
            Reversibility = _preview?.Reversibility ?? Reversibility.Exact,
            RebootPending = result.RebootPending,
            AppliedUtc = Clock.GetUtcNow(),
            HypothesisIds = _analysis?.Proposals.FirstOrDefault(p => string.Equals(p.FixId, result.FixId, StringComparison.Ordinal))?.HypothesisIds ?? [],
        };
        var next = result.Status switch
        {
            AttemptStatus.AwaitingReboot => Phase.AwaitingRestart,
            AttemptStatus.VerifyFailed => Phase.Proposing,
            _ when result.Deadman is not null => Phase.Verifying,
            _ => Phase.AwaitingOutcome,
        };
        Update(s => s with { Phase = next, Attempts = [.. s.Attempts, attempt], CurrentAttemptId = result.AttemptId, CurrentPreviewId = null, CurrentProposal = null });
        if (result.Deadman is { } d)
        {
            Raise(new DeadmanNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), AttemptId = result.AttemptId, SecondsLeft = (int)(d.DeadlineUtc - Clock.GetUtcNow()).TotalSeconds });
        }
    }

    private SessionState NewSession()
    {
        var now = Clock.GetUtcNow();
        return new SessionState
        {
            SessionId = Ids.NewId(),
            SchemaVersion = SessionState.CurrentSchemaVersion,
            Revision = 1,
            Phase = Phase.Intake,
            CreatedUtc = now,
            UpdatedUtc = now,
            ExpiresUtc = now + SessionLimits.Expiry,
            Privilege = Privilege,
            KbVersion = Kb.Version.Version,
            Resume = new ResumeInfo(),
        };
    }

    private void Update(Func<SessionState, SessionState> change)
    {
        SessionState next;
        lock (_gate)
        {
            next = change(_session) with { Revision = _session.Revision + 1, UpdatedUtc = Clock.GetUtcNow() };
            _session = next;
        }

        Raise(new SessionChangedNotification { Seq = NextSeq(), Utc = Clock.GetUtcNow(), Session = next });
    }

    private static IReadOnlyList<FixAttempt> Replace(IReadOnlyList<FixAttempt> attempts, string attemptId, Func<FixAttempt, FixAttempt> change)
        => attempts.Select(a => string.Equals(a.AttemptId, attemptId, StringComparison.Ordinal) ? change(a) : a).ToList();

    private long NextSeq() => Interlocked.Increment(ref _seq);

    private void Raise(EngineNotification notification) => Notification?.Invoke(this, notification);

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
