using System.Globalization;
using System.Net;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Session;

/// <summary>Everything the session runtime drives. Optional members are absent in limited mode or not built yet.</summary>
public sealed record SessionServices
{
    /// <summary>KB pinned for the engine's life.</summary>
    public required IKnowledgeBase Kb { get; init; }

    /// <summary>Scanner (A1).</summary>
    public required IScanService Scanner { get; init; }

    /// <summary>Analyzer (A2).</summary>
    public required IAnalyzer Analyzer { get; init; }

    /// <summary>Fix runner (A3); null in limited mode.</summary>
    public IFixRunner? FixRunner { get; init; }

    /// <summary>Reboot coordinator; null in limited mode.</summary>
    public IRebootCoordinator? Reboot { get; init; }

    /// <summary>Crash recovery (A3); null in limited mode (no journal).</summary>
    public IRecovery? Recovery { get; init; }

    /// <summary>Monitor (A5, M3); null until it exists.</summary>
    public IMonitorService? Monitor { get; init; }

    /// <summary>Live probes for guide verification.</summary>
    public IFactProbe? Probe { get; init; }

    /// <summary>Raw snapshots and analyses by id.</summary>
    public IArtifactStore Artifacts { get; init; } = new MemoryArtifactStore();

    /// <summary>Engine clock.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>Ids.</summary>
    public required IIdGenerator Ids { get; init; }

    /// <summary>The interactive player (established by the engine, refined when the verified UI connects).</summary>
    public required Func<PlayerContext> Player { get; init; }

    /// <summary>Elevated or limited.</summary>
    public required PrivilegeMode Privilege { get; init; }
}

/// <summary>
/// The session runtime (A4): implements <see cref="ISessionEnvironment"/> over the A1/A2/A3/A5 services. Holds the
/// engine-memory records the consent and restart flows bind to (previews, outcome waiters, restart prompts, validated
/// plans), and turns every effect result or failure into an event.
/// </summary>
public sealed class SessionRuntime : ISessionEnvironment
{
    private readonly SessionServices _s;
    private readonly object _gate = new();
    private readonly HashSet<string> _validatedPlans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deadmanInFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITimer> _deadmanTimers = new(StringComparer.Ordinal);
    private readonly Dictionary<EventOrigin, string?> _labels = [];
    private CancellationTokenSource? _scanCts;
    private SessionController? _controller;

    /// <summary>Creates the runtime.</summary>
    /// <param name="services">Services.</param>
    /// <param name="hub">Notification hub.</param>
    /// <param name="ledger">Restart prompt ledger (shared with the reboot coordinator).</param>
    public SessionRuntime(SessionServices services, NotificationHub hub, RestartPromptLedger ledger)
    {
        _s = services ?? throw new ArgumentNullException(nameof(services));
        Hub = hub ?? throw new ArgumentNullException(nameof(hub));
        Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        Previews = new PreviewRegistry(services.Clock);
    }

    /// <summary>Services.</summary>
    public SessionServices Services => _s;

    /// <summary>Notifications.</summary>
    public NotificationHub Hub { get; }

    /// <summary>Restart prompts.</summary>
    public RestartPromptLedger Ledger { get; }

    /// <summary>Previews awaiting consent.</summary>
    public PreviewRegistry Previews { get; }

    /// <summary>Waiters for CLI/MCP proposals.</summary>
    public OutcomeWaiters Outcomes { get; } = new();

    /// <summary>The report of the recovery that ran at start.</summary>
    public RecoveryReport? LastRecovery { get; private set; }

    /// <summary>Drift found by the last rescan.</summary>
    public IReadOnlyList<DriftFinding> LastDrift { get; private set; } = [];

    /// <summary>Connects the controller (timers and service callbacks post events to it).</summary>
    /// <param name="controller">Controller.</param>
    public void Attach(SessionController controller) => _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    /// <summary>UI language (snapshot meta), from the verified UI's hello.</summary>
    public string Language { get; set; } = Languages.English;

    /// <summary>Remembers a client's self-declared label (unverified; shown as "Suggested by …").</summary>
    /// <param name="origin">Verified origin.</param>
    /// <param name="label">Label.</param>
    public void SetLabel(EventOrigin origin, string? label)
    {
        var clean = label is null ? null : new string([.. label.Where(c => !char.IsControl(c)).Take(64)]);
        lock (_gate)
        {
            _labels[origin] = string.IsNullOrWhiteSpace(clean) ? null : clean;
        }
    }

    /// <summary>The proposer a verified origin gets.</summary>
    /// <param name="origin">Origin.</param>
    public Proposer ProposerFor(EventOrigin origin)
    {
        lock (_gate)
        {
            return new Proposer { Origin = origin, Label = _labels.GetValueOrDefault(origin) };
        }
    }

    /// <summary>A plan the engine validated (valid) may be imported into the session.</summary>
    /// <param name="planId">Plan.</param>
    public void MarkPlanValidated(string planId)
    {
        lock (_gate)
        {
            _validatedPlans.Add(planId);
        }
    }

    /// <summary>Region ping targets from the KB table <c>ping_targets</c> (IP literals only, at most <see cref="ProbeTarget.MaxTargets"/>).</summary>
    /// <param name="kb">KB.</param>
    public static IReadOnlyList<ProbeTarget> ProbeTargetsOf(IKnowledgeBase kb)
    {
        ArgumentNullException.ThrowIfNull(kb);
        var table = kb.Tables.FirstOrDefault(t => string.Equals(t.Id, ProbeTarget.KbTableId, StringComparison.Ordinal));
        if (table is null)
        {
            return [];
        }

        return [.. table.Rows
            .Select(r => (Label: r.GetValueOrDefault("label"), Host: r.GetValueOrDefault("host")))
            .Where(r => !string.IsNullOrWhiteSpace(r.Label) && r.Host is not null && IPAddress.TryParse(r.Host, out _))
            .Take(ProbeTarget.MaxTargets)
            .Select(r => new ProbeTarget { Label = r.Label!, Host = r.Host! })];
    }

    /// <summary>Runs crash recovery (before any RPC) and returns the event for the machine.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<RecoveryCompleted> RecoverAsync(CancellationToken cancellationToken)
    {
        var report = _s.Recovery is null
            ? new RecoveryReport { RanUtc = _s.Clock.GetUtcNow() }
            : await _s.Recovery.RunAsync(cancellationToken).ConfigureAwait(false);
        LastRecovery = report;
        return Stamp(new RecoveryCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), Report = report });
    }

    /// <summary>Builds the analysis input for a snapshot from the session (also used by plan validation).</summary>
    /// <param name="state">State.</param>
    /// <param name="snapshot">Snapshot.</param>
    public AnalysisInput AnalysisInputFor(SessionState state, ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AnalysisInput
        {
            AnalysisId = _s.Ids.NewId(),
            Snapshot = snapshot,
            Intake = state.Intake,
            Monitor = _s.Artifacts.Summary(state.Monitor?.SummaryId),
            History = state.Attempts,
            AsOfUtc = _s.Clock.GetUtcNow(),
            QuestionsAsked = state.QuestionsAsked,
            SkippedFixIds = state.SkippedFixIds,
        };
    }

    /// <summary>The scan request the engine sends for this session.</summary>
    /// <param name="mode">Mode.</param>
    /// <param name="language">UI language for the snapshot meta.</param>
    public ScanRequest ScanRequestFor(ScanMode mode, string language = Languages.English) => new()
    {
        Mode = mode,
        KbVersion = _s.Kb.Version.Version,
        PackCollectors = _s.Kb.Collectors,
        ProbeTargets = ProbeTargetsOf(_s.Kb),
        Player = _s.Player(),
        AppLanguage = language,
    };

    /// <inheritdoc />
    public async Task<MachineContext> PrepareAsync(SessionState state, SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var ctx = new MachineContext { Now = _s.Clock.GetUtcNow(), MonitorAvailable = _s.Monitor is not null, KbVersion = _s.Kb.Version.Version };
        switch (sessionEvent)
        {
            case IntakeSubmitted or ScanRequested or RescanRequested when SessionMachine.IsTerminal(state.Phase):
                return ctx with { NewSessionId = _s.Ids.NewId() };
            case FixSelected f:
                return ctx with { Preview = await PreviewForProposalAsync(state, f, cancellationToken).ConfigureAwait(false), Proposer = ProposerOf(state, f) };
            case ConsentGiven c:
                return ctx with { ConsentRefusal = Previews.Check(c.Token) };
            case RestartNowClicked r:
                return ctx with
                {
                    RestartRefusal = Ledger.Check(r.Token),
                    CurrentBoot = CurrentBoot(),
                    WindowsUpdateRebootPending = Ledger.ReadinessOf(r.Token)?.WindowsUpdateRebootPending == Tri.True,
                };
            case RestartLaterChosen:
                return ctx with { CurrentBoot = CurrentBoot() };
            case PlanImported p:
                lock (_gate)
                {
                    return ctx with { PlanValidated = _validatedPlans.Contains(p.PlanId) };
                }

            case GuideStarted g:
                return ctx with { Guide = _s.Kb.FindGuide(g.GuideId) };
            case GuideStepCompleted g:
                return ctx with { Guide = _s.Kb.FindGuide(g.GuideId) };
            case UndoProposed u:
                var (knownP, fixP) = await FindAttemptAsync(state, u.AttemptId, cancellationToken).ConfigureAwait(false);
                return ctx with { UndoTargetKnown = knownP, UndoTargetFixId = fixP, Proposer = ProposerFor(u.Origin) };
            case UndoRequested u:
                var (knownR, fixR) = await FindAttemptAsync(state, u.AttemptId, cancellationToken).ConfigureAwait(false);
                return ctx with { UndoTargetKnown = knownR, UndoTargetFixId = fixR };
            case FixApplyCompleted or RecoveryCompleted:
                var preview = Previews.Get(state.CurrentPreviewId);
                var hypotheses = _s.Artifacts.Analysis(state.AnalysisId)?.Proposals
                    .FirstOrDefault(p => string.Equals(p.FixId, state.CurrentFixId ?? preview?.FixId, StringComparison.Ordinal))?.HypothesisIds ?? [];
                ctx = ctx with { Preview = preview, HypothesisIds = hypotheses };
                if (sessionEvent is RecoveryCompleted && _s.FixRunner is not null)
                {
                    var rows = await _s.FixRunner.ListAttemptsAsync(cancellationToken).ConfigureAwait(false);
                    ctx = ctx with { AttemptRows = [.. rows.Where(r => string.Equals(r.SessionId, state.SessionId, StringComparison.Ordinal))] };
                }

                return ctx;
            default:
                return ctx;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEvent>> RunEffectAsync(SessionState state, SessionEffect effect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(effect);
        switch (effect)
        {
            case StartScanEffect e:
                return await ScanAsync(state, e, cancellationToken).ConfigureAwait(false);
            case CancelScanEffect:
                CancellationTokenSource? running;
                lock (_gate)
                {
                    running = _scanCts;
                }

                if (running is not null)
                {
                    await running.CancelAsync().ConfigureAwait(false);
                }

                return [];
            case AnalyzeEffect e:
                return Analyze(state, e.SnapshotId);
            case ApplyFixEffect e:
                return [await ApplyAsync(e.Token, cancellationToken).ConfigureAwait(false)];
            case ConfirmDeadmanEffect e:
                return await ConfirmDeadmanAsync(e, cancellationToken).ConfigureAwait(false);
            case WatchDeadmanEffect e:
                WatchDeadman(e);
                return [];
            case UndoEffect e:
                return [await UndoAsync(e, cancellationToken).ConfigureAwait(false)];
            case RegisterResumeEffect e:
                return [await RegisterAsync(e, cancellationToken).ConfigureAwait(false)];
            case UnregisterResumeEffect:
                if (_s.Reboot is null)
                {
                    return [Stamp(new ResumeUnregistered { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow() })];
                }

                await _s.Reboot.UnregisterResumeAsync(cancellationToken).ConfigureAwait(false);
                return [Stamp(new ResumeUnregistered { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow() })];
            case RestartEffect e:
                return await RestartAsync(e, cancellationToken).ConfigureAwait(false);
            case PostRebootVerifyEffect e:
                return [await PostRebootVerifyAsync(e, cancellationToken).ConfigureAwait(false)];
            case StartMonitorEffect e:
                return await MonitorAsync(state, e, cancellationToken).ConfigureAwait(false);
            case MarkBadEffect:
                if (_s.Monitor is not null)
                {
                    await _s.Monitor.MarkBadAsync(cancellationToken).ConfigureAwait(false);
                }

                return [];
            case VerifyGuideEffect e:
                return [await VerifyGuideAsync(e, cancellationToken).ConfigureAwait(false)];
            case NotifyConsentEffect e:
                Hub.Raise((seq, utc) => new ConsentRequestedNotification
                {
                    Seq = seq,
                    Utc = utc,
                    Kind = e.Kind,
                    PreviewId = e.PreviewId,
                    AttemptId = e.AttemptId,
                    FixId = e.FixId,
                    ProposedBy = e.ProposedBy,
                    SecondConsentNonce = e.SecondConsentNonce,
                });
                return [];
            case ResolveOutcomeEffect e:
                Outcomes.Resolve(e.PreviewId, e.Result);
                return [];
            default:
                return [];
        }
    }

    /// <summary>
    /// The restart screen's prompt: readiness gates and a single-use nonce on the engine clock (<see cref="IEngineRpc.PrepareRestartAsync"/>).
    /// The button is enabled only while the session waits for a restart, on AC power (or no battery), not while Windows
    /// Update installs, and (for "Restart into BIOS") when the firmware supports it.
    /// </summary>
    /// <param name="state">Current state.</param>
    /// <param name="kind">Kind.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<RestartPrompt> PrepareRestartAsync(SessionState state, RestartKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var readiness = _s.Reboot is null
            ? new RebootReadiness
            {
                OnAcPower = Tri.Unknown,
                WindowsUpdateRebootPending = Tri.Unknown,
                WindowsUpdateInstalling = Tri.Unknown,
                FastStartupEnabled = Tri.Unknown,
                TaskSchedulerAvailable = Tri.Unknown,
                InteractiveUserIsAdmin = Tri.Unknown,
                FirmwareRestartSupported = Tri.Unknown,
                PlannedMechanism = ResumeMechanism.Manual,
            }
            : await _s.Reboot.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
        var blocks = new List<string>();
        if (_s.Privilege != PrivilegeMode.Elevated || _s.Reboot is null)
        {
            blocks.Add("restart.limited_mode");
        }

        if (!(state.Phase == Phase.AwaitingRestart || (state.Phase == Phase.GuidedStep && kind == RestartKind.ToFirmware)))
        {
            blocks.Add("restart.not_waiting");
        }

        if (readiness.OnAcPower == Tri.False)
        {
            blocks.Add("restart.on_battery");
        }

        if (readiness.WindowsUpdateInstalling == Tri.True)
        {
            blocks.Add("restart.wu_installing");
        }

        if (kind == RestartKind.ToFirmware && readiness.FirmwareRestartSupported == Tri.False)
        {
            blocks.Add("restart.firmware_unsupported");
        }

        var now = _s.Clock.GetUtcNow();
        var prompt = new RestartPrompt
        {
            Nonce = _s.Ids.NewId(),
            Kind = kind,
            Readiness = readiness,
            Allowed = blocks.Count == 0,
            BlockReasonKeys = blocks,
            ExpiresUtc = now + SessionLimits.TokenLifetime,
        };
        Ledger.Issue(state.SessionId, prompt);
        return prompt;
    }

    /// <summary>The current boot, or null when it cannot be read (or there is no coordinator).</summary>
    public BootIdentity? CurrentBoot()
        => _s.Reboot is not null && _s.Reboot.CurrentBoot().TryGetValue(out var boot) ? boot : null;

    private T Stamp<T>(T e)
        where T : EngineEvent => e with { Origin = EventOrigin.Engine };

    private Proposer ProposerOf(SessionState state, FixSelected f)
        => ProposerFor(f.Origin) with
        {
            PlanId = f.PlanId is not null && string.Equals(f.PlanId, state.ImportedPlanId, StringComparison.Ordinal) ? f.PlanId : null,
        };

    private async Task<FixPreview?> PreviewForProposalAsync(SessionState state, FixSelected f, CancellationToken ct)
    {
        if (_s.FixRunner is null || state.Privilege != PrivilegeMode.Elevated
            || state.Phase is not (Phase.Analyzed or Phase.Proposing or Phase.AwaitingOutcome or Phase.AwaitingConsent or Phase.GuidedStep))
        {
            return null;
        }

        try
        {
            var preview = await _s.FixRunner.PreviewAsync(f.FixId, f.Binding, ProposerOf(state, f), ct).ConfigureAwait(false);
            Previews.Add(preview);
            return preview;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<(bool Known, string? FixId)> FindAttemptAsync(SessionState state, string attemptId, CancellationToken ct)
    {
        if (state.Attempts.FirstOrDefault(a => string.Equals(a.AttemptId, attemptId, StringComparison.Ordinal)) is { } mine)
        {
            return (true, mine.FixId);
        }

        if (_s.FixRunner is null)
        {
            return (false, null);
        }

        var all = await _s.FixRunner.ListAttemptsAsync(ct).ConfigureAwait(false);
        var row = all.FirstOrDefault(a => string.Equals(a.AttemptId, attemptId, StringComparison.Ordinal));
        return (row is not null, row?.FixId);
    }

    private async Task<IReadOnlyList<SessionEvent>> ScanAsync(SessionState state, StartScanEffect e, CancellationToken lifetime)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        lock (_gate)
        {
            _scanCts = cts;
        }

        try
        {
            var progress = new Relay<ScanProgress>(p => Hub.Raise((seq, utc) => new ScanProgressNotification { Seq = seq, Utc = utc, Progress = p }));
            ScanResult result;
            try
            {
                result = await _s.Scanner.ScanAsync(ScanRequestFor(e.Mode, Language), progress, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return []; // cancelled: the partial snapshot is discarded, the phase already moved back
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return [Stamp(new ScanFailed { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), ErrorCode = "scan.failed" })];
            }

            if (cts.IsCancellationRequested)
            {
                return [];
            }

            _s.Artifacts.Put(result.Snapshot);
            if (e.CheckDrift && _s.FixRunner is not null)
            {
                try
                {
                    var drift = await _s.FixRunner.CheckDriftAsync(cts.Token).ConfigureAwait(false);
                    LastDrift = drift;
                    Hub.Raise((seq, utc) => new DriftNotification { Seq = seq, Utc = utc, Findings = drift });
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    LastDrift = [];
                }
            }

            return [Stamp(new ScanCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), SnapshotId = result.Snapshot.Meta.SnapshotId })];
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_scanCts, cts))
                {
                    _scanCts = null;
                }
            }
        }
    }

    private IReadOnlyList<SessionEvent> Analyze(SessionState state, string snapshotId)
    {
        if (_s.Artifacts.Snapshot(snapshotId) is not { } snapshot)
        {
            return [];
        }

        var analysis = _s.Analyzer.Analyze(AnalysisInputFor(state, snapshot));
        _s.Artifacts.Put(analysis);
        return [Stamp(new AnalysisCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), AnalysisId = analysis.AnalysisId })];
    }

    private async Task<SessionEvent> ApplyAsync(ConsentToken token, CancellationToken ct)
    {
        FixRunResult result;
        if (_s.FixRunner is null)
        {
            result = Failed(token.FixId, "fixes.unavailable", AttemptStatus.Failed);
        }
        else
        {
            var progress = new Relay<FixProgress>(p => Hub.Raise((seq, utc) => new FixProgressNotification { Seq = seq, Utc = utc, Progress = p }));
            try
            {
                result = await _s.FixRunner.ApplyAsync(token, progress, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("consent.", StringComparison.Ordinal))
            {
                result = Failed(token.FixId, ex.Message, AttemptStatus.Failed);
            }
            catch (JournalWriteException)
            {
                result = Failed(token.FixId, "journal.write_failed", AttemptStatus.Failed); // aborted before any change
            }
            catch (InvalidOperationException ex)
            {
                result = Failed(token.FixId, ex.Message.StartsWith("fix.", StringComparison.Ordinal) ? ex.Message : "fix.refused", AttemptStatus.Failed);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                // Unknown whether something changed: the journal decides at the next start; the player decides now.
                result = Failed(token.FixId, "fix.apply_error", AttemptStatus.InDoubt);
            }
        }

        if (result.Status == AttemptStatus.AwaitingSecondConsent)
        {
            Previews.SetSecondNonce(token.PreviewId, result.SecondConsentNonce);
        }
        else if (!(result.Status == AttemptStatus.Failed && result.MessageKey?.StartsWith("consent.", StringComparison.Ordinal) == true))
        {
            Previews.MarkUsed(token.PreviewId);
        }

        return Stamp(new FixApplyCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), Result = result });
    }

    private async Task<IReadOnlyList<SessionEvent>> ConfirmDeadmanAsync(ConfirmDeadmanEffect e, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_deadmanInFlight.Add(e.AttemptId))
            {
                return []; // the answer and the deadline raced: the first one wins
            }
        }

        try
        {
            StopDeadman(e.AttemptId);
            if (_s.FixRunner is null)
            {
                return [];
            }

            var result = await _s.FixRunner.ConfirmDeadmanAsync(e.AttemptId, e.Keep, ct).ConfigureAwait(false);
            return [Stamp(new FixApplyCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), Result = result })];
        }
        finally
        {
            lock (_gate)
            {
                _deadmanInFlight.Remove(e.AttemptId);
            }
        }
    }

    private void WatchDeadman(WatchDeadmanEffect e)
    {
        StopDeadman(e.AttemptId);
        void Tick()
        {
            var left = (int)Math.Ceiling((e.DeadlineUtc - _s.Clock.GetUtcNow()).TotalSeconds);
            var current = _controller?.Current;
            if (current is not null && (current.Phase != Phase.Verifying || !string.Equals(current.CurrentAttemptId, e.AttemptId, StringComparison.Ordinal)))
            {
                StopDeadman(e.AttemptId);
                return;
            }

            Hub.Raise((seq, utc) => new DeadmanNotification { Seq = seq, Utc = utc, AttemptId = e.AttemptId, SecondsLeft = Math.Max(0, left) });
            if (left <= 0)
            {
                StopDeadman(e.AttemptId);
                _controller?.Post(Stamp(new DeadmanExpired { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), AttemptId = e.AttemptId }));
            }
        }

        var timer = _s.Clock.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            _deadmanTimers[e.AttemptId] = timer;
        }

        Hub.Raise((seq, utc) => new DeadmanNotification { Seq = seq, Utc = utc, AttemptId = e.AttemptId, SecondsLeft = Math.Max(0, (int)Math.Ceiling((e.DeadlineUtc - utc).TotalSeconds)) });
    }

    private void StopDeadman(string attemptId)
    {
        ITimer? timer;
        lock (_gate)
        {
            _deadmanTimers.Remove(attemptId, out timer);
        }

        timer?.Dispose();
    }

    private async Task<SessionEvent> UndoAsync(UndoEffect e, CancellationToken ct)
    {
        UndoResult result;
        if (_s.FixRunner is null)
        {
            result = new UndoResult { AttemptId = e.AttemptId, Status = UndoStatus.Failed, MessageKey = "fixes.unavailable" };
        }
        else
        {
            try
            {
                result = await _s.FixRunner.UndoAsync(e.AttemptId, e.Mode, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                result = new UndoResult { AttemptId = e.AttemptId, Status = UndoStatus.NothingToUndo };
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                result = new UndoResult { AttemptId = e.AttemptId, Status = UndoStatus.Failed, MessageKey = "undo.error" };
            }
        }

        return Stamp(new UndoCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), Result = result });
    }

    private async Task<SessionEvent> RegisterAsync(RegisterResumeEffect e, CancellationToken ct)
    {
        if (e.ThenRestart is { } token && !Ledger.Arm(token))
        {
            // The nonce was spent between the pre-check and now: register for "later", never restart.
            e = e with { ThenRestart = null };
        }

        ResumeRegistration registration;
        if (_s.Reboot is null)
        {
            registration = new ResumeRegistration { Mechanism = ResumeMechanism.Manual, Verified = false, FallbackMessageKey = "resume.manual" };
        }
        else
        {
            try
            {
                registration = await _s.Reboot.RegisterResumeAsync(e.SessionId, e.ExpiresUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                registration = new ResumeRegistration { Mechanism = ResumeMechanism.Manual, Verified = false, FallbackMessageKey = "resume.manual" };
            }
        }

        return Stamp(new ResumeRegistered { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), Registration = registration, ThenRestart = e.ThenRestart });
    }

    private async Task<IReadOnlyList<SessionEvent>> RestartAsync(RestartEffect e, CancellationToken ct)
    {
        if (_s.Reboot is null)
        {
            return [Stamp(new RestartFailed { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), ErrorCode = "restart.unavailable" })];
        }

        try
        {
            await _s.Reboot.RestartAsync(e.Token, ct).ConfigureAwait(false);
            return [];
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return [Stamp(new RestartFailed { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), ErrorCode = ex.Message.StartsWith("restart.", StringComparison.Ordinal) ? ex.Message : "restart.failed" })];
        }
    }

    private async Task<SessionEvent> PostRebootVerifyAsync(PostRebootVerifyEffect e, CancellationToken ct)
    {
        var results = new List<VerifyResult>();
        foreach (var id in e.AttemptIds)
        {
            VerifyResult r;
            try
            {
                r = _s.FixRunner is null
                    ? new VerifyResult { AttemptId = id, Status = VerifyStatus.Inconclusive, VerifiedUtc = _s.Clock.GetUtcNow() }
                    : await _s.FixRunner.VerifyAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                r = new VerifyResult { AttemptId = id, Status = VerifyStatus.Inconclusive, VerifiedUtc = _s.Clock.GetUtcNow() };
            }

            results.Add(r);
        }

        return Stamp(new PostRebootVerifyCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), Results = results });
    }

    private async Task<IReadOnlyList<SessionEvent>> MonitorAsync(SessionState state, StartMonitorEffect e, CancellationToken ct)
    {
        if (_s.Monitor is not { } monitor)
        {
            return [];
        }

        await monitor.StartAsync(new MonitorOptions { SessionId = state.SessionId, Purpose = e.Purpose, MaxMinutes = Math.Clamp(e.Minutes ?? 60, 1, 180) }, ct).ConfigureAwait(false);
        try
        {
            await foreach (var tick in monitor.TicksAsync(ct).ConfigureAwait(false))
            {
                Hub.Raise((seq, utc) => new MonitorTickNotification { Seq = seq, Utc = utc, Tick = tick });
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled watch must not leave the monitor process running.
            await monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var summary = await monitor.StopAsync(ct).ConfigureAwait(false);
        _s.Artifacts.Put(summary);
        return [Stamp(new MonitorCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), SummaryId = summary.SummaryId })];
    }

    private async Task<SessionEvent> VerifyGuideAsync(VerifyGuideEffect e, CancellationToken ct)
    {
        var guide = _s.Kb.FindGuide(e.GuideId);
        var branch = guide?.Branches.FirstOrDefault(b => string.Equals(b.Id, e.BranchId, StringComparison.Ordinal)) ?? guide?.Branches.FirstOrDefault();
        var result = Tri.Unknown;
        if (branch is not null && branch.Verify.Count > 0 && _s.Probe is { } probe)
        {
            result = Tri.True;
            foreach (var condition in branch.Verify)
            {
                var observed = probe.SupportedIds.Contains(condition.Signal)
                    ? (await probe.ProbeAsync(condition.Signal, new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false)).State
                    : Tri.Unknown;
                if (observed == Tri.Unknown)
                {
                    result = Tri.Unknown;
                }
                else if (observed != condition.Is)
                {
                    result = Tri.False;
                    break;
                }
            }
        }

        return Stamp(new GuideVerifyCompleted { EventId = _s.Ids.NewId(), Utc = _s.Clock.GetUtcNow(), GuideId = e.GuideId, Result = result });
    }

    private static FixRunResult Failed(string fixId, string code, AttemptStatus status)
        => new() { AttemptId = string.Empty, FixId = fixId, Status = status, MessageKey = code };

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>For logs: an invariant one-line description of a state.</summary>
    /// <param name="state">State.</param>
    public static string Describe(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return string.Create(CultureInfo.InvariantCulture, $"{state.SessionId} r{state.Revision} {state.Phase} attempts={state.Attempts.Count} resume={state.Resume.Attempts}/{state.Resume.Mechanism}/{state.Resume.Registered}");
    }
}
