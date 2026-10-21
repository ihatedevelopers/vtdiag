using System.Globalization;
using System.Windows.Threading;
using VTD.App.Mvvm;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Sessions;

namespace VTD.App.ViewModels;

/// <summary>
/// Screen 6: fix detail and consent. Shows what changes (plain words), what you'll notice, how to undo, the badges, who
/// suggested it (the player, a command-line tool, an AI agent or a helper plan) and, for one-way fixes, a second,
/// separate "this can't be undone" confirmation. The consent token echoes the preview's hashes and single-use nonce.
/// </summary>
public sealed class FixDetailViewModel : PageViewModel
{
    private FixPreview? _preview;
    private bool _understood;
    private string? _fixId;
    private bool _bindingUnknown;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public FixDetailViewModel(ShellViewModel shell)
        : base(shell)
    {
        ConsentCommand = new AsyncCommand(ConsentAsync, () => CanConsent);
        SecondConsentCommand = new AsyncCommand(SecondConsentAsync, () => IsSecondConsent && Understood);
        SkipCommand = new AsyncCommand(SkipAsync, () => _fixId is not null);
        DiyCommand = new AsyncCommand(DiyAsync, () => DiyGuideId is not null);
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>The preview shown.</summary>
    public FixPreview? Preview => _preview;

    /// <summary>Loaded.</summary>
    public bool IsLoaded => _preview is not null;

    /// <summary>Title.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>What we'll change.</summary>
    public string Explain { get; private set; } = string.Empty;

    /// <summary>What you'll notice.</summary>
    public string? Notice { get; private set; }

    /// <summary>How to undo.</summary>
    public string UndoText { get; private set; } = string.Empty;

    /// <summary>Side effects.</summary>
    public IReadOnlyList<string> SideEffects { get; private set; } = [];

    /// <summary>Notes: restore point first, deadman, reboot.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>Badges.</summary>
    public IReadOnlyList<BadgeViewModel> Badges { get; private set; } = [];

    /// <summary>Plain step list.</summary>
    public IReadOnlyList<string> Steps { get; private set; } = [];

    /// <summary>Technical detail for helpers (ops, args, before/after, guards).</summary>
    public IReadOnlyList<string> Technical { get; private set; } = [];

    /// <summary>Why "Fix it" is disabled.</summary>
    public IReadOnlyList<string> BlockReasons { get; private set; } = [];

    /// <summary>Has block reasons.</summary>
    public bool IsBlocked => BlockReasons.Count > 0;

    /// <summary>"Suggested by an AI agent (calls itself …)". Null for the player's own pick.</summary>
    public string? ProposerText { get; private set; }

    /// <summary>Proposed by someone other than the player.</summary>
    public bool HasProposer => ProposerText is not null;

    /// <summary>Proposed by an AI agent or a command-line tool (extra caution line).</summary>
    public bool IsAgentProposal { get; private set; }

    /// <summary>Second, separate confirmation for a one-way fix.</summary>
    public bool IsSecondConsent { get; private set; }

    /// <summary>The player ticked "I understand this can't be undone with a click".</summary>
    public bool Understood
    {
        get => _understood;
        set
        {
            if (Set(ref _understood, value))
            {
                SecondConsentCommand.Refresh();
            }
        }
    }

    /// <summary>"Fix it" enabled.</summary>
    public bool CanConsent => _preview is not null && _preview.CanApply && !IsSecondConsent && !Shell.IsLimited && _preview.ConsentNonce is not null && !_bindingUnknown;

    /// <summary>"It changes: Realtek Gaming 2.5GbE Family Controller".</summary>
    public string? Targets { get; private set; }

    /// <summary>Guide for "Show me how to do it myself".</summary>
    public string? DiyGuideId { get; private set; }

    /// <summary>Fix it.</summary>
    public AsyncCommand ConsentCommand { get; }

    /// <summary>Yes, do it anyway (one-way second click).</summary>
    public AsyncCommand SecondConsentCommand { get; }

    /// <summary>Skip / don't do this.</summary>
    public AsyncCommand SkipCommand { get; }

    /// <summary>Show me how to do it myself.</summary>
    public AsyncCommand DiyCommand { get; }

    /// <summary>True when this page already shows what the engine is waiting for.</summary>
    /// <param name="s">Session.</param>
    /// <param name="pending">Pending consent request.</param>
    public bool Matches(SessionState s, ConsentRequestedNotification? pending)
    {
        ArgumentNullException.ThrowIfNull(s);
        var fix = s.CurrentFixId ?? pending?.FixId;
        return _fixId is null || string.Equals(fix, _fixId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override async Task OnShownAsync()
    {
        await LoadAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        var pending = Shell.PendingConsent;
        var second = pending is { Kind: ConsentKind.SecondConsent } && string.Equals(pending.FixId, _fixId, StringComparison.Ordinal)
            && (pending.PreviewId is null || string.Equals(pending.PreviewId, _preview?.PreviewId, StringComparison.Ordinal));
        if (second != IsSecondConsent)
        {
            IsSecondConsent = second;
            Understood = false;
        }

        if (_preview is not null)
        {
            Build(_preview);
        }

        ConsentCommand.Refresh();
        SecondConsentCommand.Refresh();
        base.Refresh();
    }

    private async Task LoadAsync()
    {
        var s = Shell.Session;
        var engine = Shell.Engine;
        if (s is null || engine is null)
        {
            return;
        }

        _fixId = s.CurrentFixId ?? Shell.PendingConsent?.FixId;
        if (_fixId is null)
        {
            return;
        }

        var proposer = s.CurrentProposal ?? Shell.PendingConsent?.ProposedBy ?? new Proposer { Origin = EventOrigin.Ui };
        var external = proposer.Origin is not EventOrigin.Ui;
        if (Shell.CurrentPreview is { } kept && string.Equals(kept.FixId, _fixId, StringComparison.Ordinal)
            && Shell.PendingConsent is { Kind: ConsentKind.SecondConsent } pending
            && string.Equals(pending.PreviewId, kept.PreviewId, StringComparison.Ordinal))
        {
            _preview = kept; // the second click must echo the SAME preview
        }
        else if ((s.CurrentPreviewId ?? Shell.PendingConsent?.PreviewId) is { } previewId
                 && await engine.GetPreviewAsync(previewId, CancellationToken.None).ConfigureAwait(true) is { } enginePreview
                 && string.Equals(enginePreview.FixId, _fixId, StringComparison.Ordinal))
        {
            // The exact preview the engine computed for FixSelected (also an agent's proposal, with its binding): the
            // consent token binds to what the player sees here.
            _bindingUnknown = false;
            _preview = enginePreview;
            Shell.CurrentPreview = _preview;
        }
        else
        {
            // Fallback when the engine has no preview for this fix (expired or never computed): recompute from the binding
            // the UI knows; the engine checks the token against its own preview at apply (TOCTOU). For a proposal from
            // outside (CLI/MCP) the UI doesn't know the proposed binding: it never guesses one for a fix that targets
            // something specific (see _bindingUnknown).
            FixBinding binding;
            _bindingUnknown = false;
            if (!external && string.Equals(Shell.SelectedFixId, _fixId, StringComparison.Ordinal) && Shell.SelectedBinding is not null)
            {
                binding = Shell.SelectedBinding;
            }
            else
            {
                binding = FixBinding.Empty;
                _bindingUnknown = external && S.Kb.FindFix(_fixId)?.Bind.Count > 0;
                if (!external)
                {
                    binding = Shell.Analysis?.Proposals.FirstOrDefault(p => string.Equals(p.FixId, _fixId, StringComparison.Ordinal))?.Binding ?? FixBinding.Empty;
                }
            }

            _preview = await engine.PreviewFixAsync(_fixId, binding, CancellationToken.None).ConfigureAwait(true);
            Shell.CurrentPreview = _preview;
        }

        var fromPlan = !external && string.Equals(Shell.SelectedFixId, _fixId, StringComparison.Ordinal) ? Shell.PlanHelperName : null;
        ProposerText = Words.Proposer(proposer, fromPlan, Shell.PlanHelperVerified);
        IsAgentProposal = proposer.Origin is EventOrigin.Mcp or EventOrigin.Cli;
        DiyGuideId = Shell.Analysis?.Hypotheses
            .Where(h => h.FixIds.Contains(_fixId, StringComparer.Ordinal))
            .SelectMany(h => h.GuideIds)
            .FirstOrDefault();
        Refresh();
        SkipCommand.Refresh();
        DiyCommand.Refresh();
    }

    private void Build(FixPreview p)
    {
        Title = S.Text.Get(p.TitleKey);
        Explain = S.Text.Get(p.ExplainKey);
        Notice = p.NoticeKey is null ? null : S.Text.Get(p.NoticeKey);
        UndoText = p.Reversibility == Reversibility.OneWay ? L.Get("Fix.OneWayUndo") : S.Text.Get(p.UndoExplainKey);
        SideEffects = [.. p.SideEffectKeys.Select(k => S.Text.Get(k))];
        Badges = [Words.Reversible(p.Reversibility), Words.Reboot(p.Reboot), Words.Risk(p.Risk)];
        var notes = new List<string>();
        if (p.RestorePointFirst)
        {
            notes.Add(L.Get("Fix.RestorePointFirst"));
        }

        if (p.Deadman is { } d)
        {
            notes.Add(L.Format("Fix.Deadman", ("seconds", d.Seconds)));
        }

        if (p.Reboot == RebootNeed.Required)
        {
            notes.Add(L.Get("Fix.NeedsRestart"));
        }

        if (p.RequiresSecondConsent)
        {
            notes.Add(L.Get("Fix.OneWayNote"));
        }

        Notes = notes;
        var names = p.Binding.Entities.Values.SelectMany(v => v).Select(e => e.DisplayName ?? e.Id).ToList();
        Steps = [.. p.Steps.Select(st => S.Text.Has(st.SummaryKey)
            ? S.Text.Get(st.SummaryKey, st.SummaryArgs)
            : L.Format("Fix.StepGeneric", ("op", L.Get("Op." + st.Op.Split('.')[0])), ("what", names.Count > 0 ? string.Join(", ", names) : L.Get("Fix.ThisPc"))))];
        Technical = [.. p.Steps.Select(st => string.Create(CultureInfo.InvariantCulture, $"{st.Index + 1}. {st.Op}({string.Join(", ", st.Args.Select(a => a.Key + "=" + a.Value))}) · {st.Reversibility}"))
            .Concat(p.Guards.Select(g => string.Create(CultureInfo.InvariantCulture, $"guard {g.Probe}: {g.Observed} (needs {g.Must}) {(g.Passed ? "ok" : "FAILED")}")))
            .Append(string.Create(CultureInfo.InvariantCulture, $"fix {p.FixId} rev {p.FixRev} · preview {p.PreviewId} · binding {p.BindingHash[..12]}"))];
        var blocks = p.BlockReasonKeys.Select(k => S.Text.Get(k)).ToList();
        if (Shell.IsLimited)
        {
            blocks.Insert(0, L.Get("Fix.LimitedMode"));
        }

        if (p.ConsentNonce is null)
        {
            blocks.Add(L.Get("Fix.NoNonce"));
        }

        if (_bindingUnknown)
        {
            blocks.Add(L.Get("Fix.ExternalBindingUnknown"));
        }

        BlockReasons = blocks;
        Targets = names.Count == 0 ? null : L.Format("Fix.Targets", ("what", string.Join(", ", names.Select(n => Words.Clip(n, 80)))));
    }

    private ConsentToken Token(string? secondNonce) => new()
    {
        TokenId = Shell.NewId(),
        SessionId = Shell.Session!.SessionId,
        PreviewId = _preview!.PreviewId,
        FixId = _preview.FixId,
        BindingHash = _preview.BindingHash,
        PreviewHash = _preview.PreviewHash,
        Nonce = _preview.ConsentNonce!,
        IssuedUtc = Shell.Now,
        SecondConsentNonce = secondNonce,
    };

    private async Task ConsentAsync()
    {
        if (!await Shell.SendAsync(new ConsentGiven { EventId = Shell.NewId(), Utc = Shell.Now, Token = Token(null) }).ConfigureAwait(true))
        {
            await ReloadAfterRefusalAsync().ConfigureAwait(true);
        }
    }

    private async Task SecondConsentAsync()
    {
        var nonce = Shell.PendingConsent?.SecondConsentNonce;
        if (nonce is not null && !await Shell.SendAsync(new ConsentGiven { EventId = Shell.NewId(), Utc = Shell.Now, Token = Token(nonce) }).ConfigureAwait(true))
        {
            await ReloadAfterRefusalAsync().ConfigureAwait(true);
        }
    }

    private async Task ReloadAfterRefusalAsync()
    {
        if (Shell.Engine is null)
        {
            return;
        }

        // The engine may have moved on (or the token was stale): take its state, and show a fresh preview to click.
        await Shell.ResyncAsync().ConfigureAwait(true);
        if (Shell.Session?.Phase == Phase.AwaitingConsent)
        {
            Shell.CurrentPreview = null;
            IsSecondConsent = false;
            Understood = false;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private Task SkipAsync() => Shell.SendAsync(new FixSkipped { EventId = Shell.NewId(), Utc = Shell.Now, FixId = _fixId! });

    private Task DiyAsync() => Shell.SendAsync(new GuideStarted { EventId = Shell.NewId(), Utc = Shell.Now, GuideId = DiyGuideId! });
}

/// <summary>Screen 7: applying, with live step ticks. Closing is blocked (the engine continues anyway).</summary>
/// <param name="shell">Shell.</param>
public sealed class ApplyingViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Title of the fix.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Steps with state.</summary>
    public IReadOnlyList<LineViewModel> Steps { get; private set; } = [];

    /// <inheritdoc />
    public override void Refresh()
    {
        var p = Shell.CurrentPreview;
        Title = p is null ? string.Empty : S.Text.Get(p.TitleKey);
        var progress = Shell.FixProgress;
        var current = progress.Count == 0 ? -1 : progress[^1].StepIndex;
        var done = progress.Count > 0 && progress[^1].Phase is FixPhase.Verify or FixPhase.Done or FixPhase.Deadman;
        var steps = new List<LineViewModel>
        {
            new(L.Get("Applying.Backup"), progress.Count > 0 ? HealthStatus.Ok : HealthStatus.Warn),
        };
        if (p is not null)
        {
            steps.AddRange(p.Steps.Select(st => new LineViewModel(
                S.Text.Has(st.SummaryKey) ? S.Text.Get(st.SummaryKey, st.SummaryArgs) : L.Format("Applying.Step", ("n", st.Index + 1)),
                done || st.Index < current ? HealthStatus.Ok : st.Index == current ? HealthStatus.Warn : HealthStatus.Unknown)));
        }

        steps.Add(new LineViewModel(L.Get("Applying.Verify"), done ? HealthStatus.Warn : HealthStatus.Unknown));
        Steps = steps;
        base.Refresh();
    }
}

/// <summary>
/// Screen 7b: "Is your internet still working?" The engine owns the countdown and the auto-undo; the UI shows the time
/// left from the engine's latest tick (measured on the UI's own monotonic clock, since the two clocks may differ) and
/// keeps counting even while another page is open.
/// </summary>
public sealed class DeadmanViewModel : PageViewModel
{
    private readonly DispatcherTimer _timer;
    private int _secondsLeft;
    private bool _expiredHandled;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public DeadmanViewModel(ShellViewModel shell)
        : base(shell)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        KeepCommand = new AsyncCommand(() => AnswerAsync(true), () => _secondsLeft > 0);
        UndoCommand = new AsyncCommand(() => AnswerAsync(false), () => _secondsLeft > 0);
        Resync();
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Seconds left.</summary>
    public int SecondsLeft => _secondsLeft;

    /// <summary>Length of the countdown (from the preview's deadman, else the first tick).</summary>
    public int TotalSeconds { get; private set; } = 60;

    /// <summary>0..1 of the countdown left.</summary>
    public double Fraction => TotalSeconds <= 0 ? 0 : Math.Clamp((double)_secondsLeft / TotalSeconds, 0, 1);

    /// <summary>"Undoing automatically in 42 s".</summary>
    public string CountdownText => _secondsLeft > 0 ? L.Format("Deadman.Countdown", ("seconds", _secondsLeft)) : L.Get("Deadman.TimeUp");

    /// <summary>Yes, keep it.</summary>
    public AsyncCommand KeepCommand { get; }

    /// <summary>Undo.</summary>
    public AsyncCommand UndoCommand { get; }

    /// <summary>Recomputes the time left from the engine's latest deadman tick.</summary>
    public void Resync()
    {
        var tick = Shell.Deadman;
        if (tick is null)
        {
            _secondsLeft = 0;
            return;
        }

        TotalSeconds = Shell.CurrentPreview?.Deadman?.Seconds ?? Math.Max(TotalSeconds, tick.SecondsLeft);
        var elapsed = S.Clock.GetElapsedTime(Shell.DeadmanReceivedTimestamp);
        _secondsLeft = Math.Max(0, tick.SecondsLeft - (int)elapsed.TotalSeconds);
        Refresh();
        KeepCommand.Refresh();
        UndoCommand.Refresh();
    }

    /// <inheritdoc />
    public override Task OnShownAsync()
    {
        _timer.Start();
        return base.OnShownAsync();
    }

    private async Task TickAsync()
    {
        if (Shell.Session?.Phase != Phase.Verifying)
        {
            _timer.Stop();
            return;
        }

        Resync();
        if (_secondsLeft == 0 && !_expiredHandled)
        {
            _expiredHandled = true;
            _timer.Stop();

            // The real engine reverts by itself and reports the new phase; the demo engine is told to do the same.
            if (Shell.Engine is Services.DemoEngine demo && Shell.Session?.CurrentAttemptId is { } id)
            {
                await demo.SimulateDeadmanExpiredAsync(id).ConfigureAwait(true);
            }

            await Shell.ResyncAsync().ConfigureAwait(true);
        }
    }

    private async Task AnswerAsync(bool keep)
    {
        var id = Shell.Session?.CurrentAttemptId ?? Shell.Deadman?.AttemptId;
        if (id is null)
        {
            return;
        }

        _timer.Stop();
        await Shell.SendAsync(new DeadmanAnswered { EventId = Shell.NewId(), Utc = Shell.Now, AttemptId = id, Keep = keep }).ConfigureAwait(true);
    }
}

/// <summary>Screen 8b: restart needed (also "Restart into BIOS" from a guide). The only restart path is this click.</summary>
public sealed class RestartViewModel : PageViewModel
{
    private RestartPrompt? _prompt;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    /// <param name="kind">Normal or into firmware.</param>
    public RestartViewModel(ShellViewModel shell, RestartKind kind)
        : base(shell)
    {
        Kind = kind;
        RestartNowCommand = new AsyncCommand(RestartNowAsync, () => _prompt?.Allowed == true);
        LaterCommand = new AsyncCommand(LaterAsync, () => !ChoseLater);
        BackCommand = new RelayCommand(() => Shell.RequestRoute());
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Kind.</summary>
    public RestartKind Kind { get; }

    /// <summary>Into BIOS.</summary>
    public bool IsFirmware => Kind == RestartKind.ToFirmware;

    /// <summary>Warnings ("Use Restart, not Shut down", battery...).</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Why the restart button is disabled.</summary>
    public IReadOnlyList<string> BlockReasons { get; private set; } = [];

    /// <summary>"We'll open again automatically after you sign in." (depends on the resume mechanism).</summary>
    public string ResumeText { get; private set; } = string.Empty;

    /// <summary>The player chose "later".</summary>
    public bool ChoseLater => Shell.Session?.Phase == Phase.AwaitingRestart && Shell.Session.Resume.Registered && !IsFirmware;

    /// <summary>Restart now.</summary>
    public AsyncCommand RestartNowCommand { get; }

    /// <summary>Restart later.</summary>
    public AsyncCommand LaterCommand { get; }

    /// <summary>Back (firmware restart from a guide).</summary>
    public RelayCommand BackCommand { get; }

    /// <inheritdoc />
    public override async Task OnShownAsync()
    {
        if (Shell.Engine is not null)
        {
            _prompt = await Shell.Engine.PrepareRestartAsync(Kind, CancellationToken.None).ConfigureAwait(true);
        }

        Refresh();
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_prompt is { } p)
        {
            var r = p.Readiness;
            var warnings = p.Readiness.WarningKeys.Select(k => S.Text.Get(k)).ToList();
            if (r.OnAcPower == Tri.False)
            {
                warnings.Add(L.Get("Restart.Battery"));
            }

            Warnings = warnings;
            BlockReasons = [.. p.BlockReasonKeys.Select(k => S.Text.Get(k))];
            ResumeText = L.Get("Restart.Resume." + r.PlannedMechanism);
        }

        RestartNowCommand.Refresh();
        LaterCommand.Refresh();
        base.Refresh();
    }

    private async Task RestartNowAsync()
    {
        if (_prompt is null || Shell.Session is null)
        {
            return;
        }

        var token = new RestartToken { TokenId = Shell.NewId(), SessionId = Shell.Session.SessionId, Kind = Kind, Nonce = _prompt.Nonce, IssuedUtc = Shell.Now };
        await Shell.SendAsync(new RestartNowClicked { EventId = Shell.NewId(), Utc = Shell.Now, Token = token }).ConfigureAwait(true);
    }

    private async Task LaterAsync()
    {
        if (IsFirmware)
        {
            Shell.RequestRoute();
            Shell.ShowFlow(new GuideViewModel(Shell));
            return;
        }

        await Shell.SendAsync(new RestartLaterChosen { EventId = Shell.NewId(), Utc = Shell.Now }).ConfigureAwait(true);
        Refresh();
    }
}

/// <summary>Shown right before Windows restarts.</summary>
public sealed class RestartingViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public RestartingViewModel(ShellViewModel shell)
        : base(shell)
    {
        SimulateResumeCommand = new RelayCommand(
            () =>
            {
                if (Shell.Engine is Services.DemoEngine demo)
                {
                    Shell.JustResumed = true;
                    demo.SimulateResume();
                }
            },
            () => Shell.Engine is Services.DemoEngine);
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Into BIOS.</summary>
    public bool IsFirmware => Shell.Session?.Restart?.Kind == RestartKind.ToFirmware;

    /// <summary>Demo only: the PC comes back.</summary>
    public RelayCommand SimulateResumeCommand { get; }
}

/// <summary>Screen 9: welcome back after the restart, with the post-reboot verification.</summary>
public sealed class ResumeViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public ResumeViewModel(ShellViewModel shell)
        : base(shell)
    {
        ContinueCommand = new RelayCommand(() =>
        {
            Shell.JustResumed = false;
            Shell.RequestRoute();
        });
        UndoAllCommand = new AsyncCommand(() => Shell.SendAsync(new UndoAllRequested { EventId = Shell.NewId(), Utc = Shell.Now }));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Verification lines.</summary>
    public IReadOnlyList<LineViewModel> Checks { get; private set; } = [];

    /// <summary>Still checking.</summary>
    public bool IsChecking => Shell.Session?.Phase == Phase.Resuming;

    /// <summary>Continue.</summary>
    public RelayCommand ContinueCommand { get; }

    /// <summary>Stop and undo everything.</summary>
    public AsyncCommand UndoAllCommand { get; }

    /// <inheritdoc />
    public override void Refresh()
    {
        var attempts = Shell.Session?.Attempts ?? [];
        Checks = [.. attempts.Select(a =>
        {
            var title = S.Text.Get(S.Kb.FindFix(a.FixId)?.TitleKey ?? a.FixId);
            return a.Status switch
            {
                AttemptStatus.Verified => new LineViewModel(L.Format("Resume.InPlace", ("fix", title)), HealthStatus.Ok),
                AttemptStatus.AwaitingReboot => new LineViewModel(L.Format("Resume.Checking", ("fix", title)), HealthStatus.Warn),
                AttemptStatus.VerifyFailed or AttemptStatus.RolledBack => new LineViewModel(L.Format("Resume.NotInPlace", ("fix", title)), HealthStatus.Fail, L.Get("Resume.NotInPlaceDetail")),
                _ => new LineViewModel(title, HealthStatus.Unknown, Words.Attempt(a.Status)),
            };
        })];
        base.Refresh();
    }
}

/// <summary>Screens 8a + 10: "now test it" (monitor on/off), then "Did it help?".</summary>
public sealed class OutcomeViewModel : PageViewModel
{
    private string? _triedSummaryId;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public OutcomeViewModel(ShellViewModel shell)
        : base(shell)
    {
        StartTestCommand = new AsyncCommand(StartTestAsync, () => CanMonitor);
        SkipTestCommand = new RelayCommand(() =>
        {
            Shell.OutcomeAsked = true;
            Refresh();
        });
        AnswerCommand = new AsyncCommand(p => AnswerAsync(p as string));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Test;

    /// <summary>Show the "Did it help?" question.</summary>
    public bool AskOutcome => Shell.OutcomeAsked || !CanMonitor || Shell.Ticks.Count > 0;

    /// <summary>The monitor is available.</summary>
    public bool CanMonitor => Shell.Can("monitor");

    /// <summary>Fix title.</summary>
    public string FixTitle { get; private set; } = string.Empty;

    /// <summary>What the test saw (from the monitor ticks).</summary>
    public IReadOnlyList<string> TestLines { get; private set; } = [];

    /// <summary>Has test lines.</summary>
    public bool HasTestLines => TestLines.Count > 0;

    /// <summary>The engine's summary of the monitor run (<see cref="Contracts.Sessions.MonitorRunInfo.SummaryId"/>), once loaded.</summary>
    public Contracts.Monitoring.MonitorSummary? Summary { get; private set; }

    /// <summary>Start test (monitor on).</summary>
    public AsyncCommand StartTestCommand { get; }

    /// <summary>Test without monitor.</summary>
    public RelayCommand SkipTestCommand { get; }

    /// <summary>Answer (parameter: fixed / notfixed / unsure).</summary>
    public AsyncCommand AnswerCommand { get; }

    /// <inheritdoc />
    public override void Refresh()
    {
        var attempt = CurrentAttempt();
        FixTitle = attempt is null ? string.Empty : S.Text.Get(S.Kb.FindFix(attempt.FixId)?.TitleKey ?? attempt.FixId);
        var ticks = Shell.Ticks.Where(t => t.GameRunning).ToList();
        if (ticks.Count > 0)
        {
            var c = L.Culture;
            var spikes = ticks.Sum(t => t.Spikes);
            TestLines =
            [
                L.Format("Outcome.Fps", ("fps", ticks.Where(t => t.Fps is not null).Select(t => t.Fps!.Value).DefaultIfEmpty(0).Average().ToString("0", c))),
                L.Format("Outcome.Spikes", ("count", spikes), ("seconds", ticks.Count)),
                L.Format("Outcome.Ping", ("ms", ticks.Where(t => t.RttMs is not null).Select(t => t.RttMs!.Value).DefaultIfEmpty(0).Average().ToString("0", c))),
                L.Format("Outcome.SelfCost", ("pct", ticks.Select(t => t.SelfCpuPct).Average().ToString("0.0", c))),
            ];
        }
        else
        {
            TestLines = [];
        }

        var summaryId = Shell.Session?.Monitor?.SummaryId;
        if (Summary is not null && !string.Equals(Summary.SummaryId, summaryId, StringComparison.Ordinal))
        {
            Summary = null; // a new test run
        }

        if (Summary is { } sum)
        {
            TestLines = [.. TestLines, sum.InsufficientData
                ? L.Get("Outcome.Summary.Short")
                : L.Format("Outcome.Summary", ("spikes", sum.Spikes.Count), ("marked", sum.MarkedBad.Count))];
        }
        else if (summaryId is not null && Shell.Engine is { } engine && !string.Equals(_triedSummaryId, summaryId, StringComparison.Ordinal))
        {
            _ = LoadSummaryAsync(engine, summaryId);
        }

        StartTestCommand.Refresh();
        base.Refresh();
    }

    private async Task LoadSummaryAsync(Contracts.Ipc.IEngineRpc engine, string summaryId)
    {
        _triedSummaryId = summaryId; // one request per summary id; a null answer is not retried in a loop
        try
        {
            Summary = await engine.GetMonitorSummaryAsync(summaryId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or OperationCanceledException or TimeoutException)
        {
            Summary = null; // the summary is extra detail; the outcome question works without it
        }

        if (Summary is not null)
        {
            Refresh();
        }
    }

    private FixAttempt? CurrentAttempt()
    {
        var s = Shell.Session;
        return s?.Attempts.FirstOrDefault(a => string.Equals(a.AttemptId, s.CurrentAttemptId, StringComparison.Ordinal)) ?? s?.Attempts.LastOrDefault();
    }

    private async Task StartTestAsync()
    {
        Shell.Ticks.Clear();
        Shell.OutcomeAsked = true;
        await Shell.SendAsync(new MonitorRequested { EventId = Shell.NewId(), Utc = Shell.Now, Purpose = Contracts.Monitoring.MonitorPurpose.OutcomeCheck, Minutes = 10 }).ConfigureAwait(true);
    }

    private async Task AnswerAsync(string? answer)
    {
        var attempt = CurrentAttempt();
        var outcome = answer switch
        {
            "fixed" => FixOutcome.Fixed,
            "notfixed" => FixOutcome.NotFixed,
            _ => FixOutcome.Unsure,
        };
        if (attempt is null)
        {
            return;
        }

        await Shell.SendAsync(new OutcomeReported { EventId = Shell.NewId(), Utc = Shell.Now, AttemptId = attempt.AttemptId, Outcome = outcome }).ConfigureAwait(true);
    }
}

/// <summary>Watching a match: live numbers (the window goes to the tray in the real app; no rendering in-game).</summary>
/// <param name="shell">Shell.</param>
public sealed class MonitoringViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Test;

    /// <summary>Latest numbers.</summary>
    public IReadOnlyList<LineViewModel> Live { get; private set; } = [];

    /// <inheritdoc />
    public override void Refresh()
    {
        var t = Shell.Ticks.LastOrDefault();
        var c = L.Culture;
        // These are measurements, not verdicts: a neutral mark, "?" when a source has no data, a warning for stutters.
        static HealthStatus Info(double? v) => v is null ? HealthStatus.Unknown : HealthStatus.NotApplicable;
        var spikes = Shell.Ticks.Sum(x => x.Spikes);
        Live = t is null
            ? [new LineViewModel(L.Get("Monitor.Waiting"), HealthStatus.Unknown)]
            :
            [
                new LineViewModel(L.Format("Monitor.Fps", ("fps", t.Fps?.ToString("0", c) ?? L.Get("Monitor.NoData"))), Info(t.Fps)),
                new LineViewModel(L.Format("Monitor.Spikes", ("count", spikes)), spikes > 0 ? HealthStatus.Warn : HealthStatus.NotApplicable),
                new LineViewModel(L.Format("Monitor.Ping", ("ms", t.RttMs?.ToString("0", c) ?? L.Get("Monitor.NoData"))), Info(t.RttMs)),
                new LineViewModel(L.Format("Monitor.Self", ("pct", t.SelfCpuPct.ToString("0.0", c))), HealthStatus.NotApplicable),
            ];
        base.Refresh();
    }
}
