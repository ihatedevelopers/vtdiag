using System.Collections.ObjectModel;
using System.Windows.Threading;
using VTD.App.Localization;
using VTD.App.Mvvm;
using VTD.App.Services;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.App.ViewModels;

/// <summary>Where the player is in the flow (the left rail). A real sequence, so it is numbered on screen.</summary>
public enum FlowStep
{
    /// <summary>Not part of the flow (side pages).</summary>
    None = -1,

    /// <summary>Tell us what's wrong.</summary>
    Describe = 0,

    /// <summary>Scan.</summary>
    Scan = 1,

    /// <summary>Health + suspects.</summary>
    Results = 2,

    /// <summary>Fix (consent, apply, restart).</summary>
    Fix = 3,

    /// <summary>Test (monitor, did it help?).</summary>
    Test = 4,

    /// <summary>Done / share.</summary>
    Share = 5,
}

/// <summary>One rail entry.</summary>
public sealed class RailStepViewModel : ObservableObject
{
    private bool _current;
    private bool _done;

    /// <summary>Creates the entry.</summary>
    /// <param name="step">Step.</param>
    /// <param name="key">Label key.</param>
    public RailStepViewModel(FlowStep step, string key)
    {
        Step = step;
        Key = key;
        Number = ((int)step + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Loc.Instance.LanguageChanged += (_, _) => Raise(nameof(Label));
    }

    /// <summary>Label in the current language.</summary>
    public string Label => Loc.Instance.Get(Key);

    /// <summary>Last step (no route line below it).</summary>
    public bool IsLast => Step == FlowStep.Share;

    /// <summary>Step.</summary>
    public FlowStep Step { get; }

    /// <summary>Number shown in the marker.</summary>
    public string Number { get; }

    /// <summary>Label key.</summary>
    public string Key { get; }

    /// <summary>Current.</summary>
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }

    /// <summary>Done.</summary>
    public bool IsDone { get => _done; set => Set(ref _done, value); }
}

/// <summary>
/// The app shell: owns the engine connection, mirrors the engine's <see cref="SessionState"/> and turns its phase into a
/// screen. The engine is the source of truth: every user action is a <see cref="SessionCommand"/>, and the screen follows
/// the phase the engine reports back. Side pages (undo center, report, settings, about, helper, plan import) sit on top
/// and return to the flow; a consent request always surfaces, whatever page is open.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private PageViewModel _page;
    private PageViewModel _flowPage;
    private string? _error;
    private bool _routeRequested;
    private bool _routing;
    private SessionState? _session;
    private string? _banner;

    /// <summary>Creates the shell.</summary>
    /// <param name="services">Services.</param>
    public ShellViewModel(AppServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;
        Rail =
        [
            new RailStepViewModel(FlowStep.Describe, "Rail.Describe"),
            new RailStepViewModel(FlowStep.Scan, "Rail.Scan"),
            new RailStepViewModel(FlowStep.Results, "Rail.Results"),
            new RailStepViewModel(FlowStep.Fix, "Rail.Fix"),
            new RailStepViewModel(FlowStep.Test, "Rail.Test"),
            new RailStepViewModel(FlowStep.Share, "Rail.Share"),
        ];
        _flowPage = _page = new WelcomeViewModel(this);
        UpdateRail();

        GoUndoCommand = new RelayCommand(() => OpenSide(new UndoCenterViewModel(this)));
        GoReportCommand = new RelayCommand(() => OpenSide(new ReportViewModel(this)));
        GoSettingsCommand = new RelayCommand(() => OpenSide(new SettingsViewModel(this)));
        GoAboutCommand = new RelayCommand(() => OpenSide(new AboutViewModel(this)));
        GoHelperCommand = new RelayCommand(() => OpenSide(new HelperViewModel(this)));
        GoPlanImportCommand = new RelayCommand(() => OpenSide(new PlanImportViewModel(this)));
        BackCommand = new RelayCommand(CloseSide, () => IsSidePage);
        SetLanguageCommand = new RelayCommand(p => Services.Loc.Language = p as string ?? Languages.English);
        DismissErrorCommand = new RelayCommand(() => Error = null);
        Services.Loc.LanguageChanged += (_, _) => _page.Refresh();
        AsyncCommand.Failed += (_, ex) => ShowError(ex);
    }

    /// <summary>Raised when the engine needs the player (a consent request): the window comes to the front.</summary>
    public event EventHandler? AttentionRequested;

    /// <summary>Services.</summary>
    public AppServices Services { get; }

    /// <summary>The engine, once connected.</summary>
    public IEngineRpc? Engine { get; private set; }

    /// <summary>The engine's hello.</summary>
    public EngineHello? Hello { get; private set; }

    /// <summary>Latest session.</summary>
    public SessionState? Session
    {
        get => _session;
        private set
        {
            if (value is not null && _session is not null && value.Revision < _session.Revision
                && string.Equals(value.SessionId, _session.SessionId, StringComparison.Ordinal))
            {
                return; // stale (notifications can arrive after the RPC reply)
            }

            Set(ref _session, value);
        }
    }

    /// <summary>Latest analysis.</summary>
    public AnalysisResult? Analysis { get; private set; }

    /// <summary>Latest snapshot (Local level).</summary>
    public ScanSnapshot? Snapshot { get; private set; }

    /// <summary>Current page.</summary>
    public PageViewModel Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(IsSidePage));
                BackCommand.Refresh();
                UpdateRail();
                _ = ShowSafelyAsync(value);
            }
        }
    }

    /// <summary>Monotonic time the latest deadman tick arrived (UI clock; the engine's clock may differ).</summary>
    public long DeadmanReceivedTimestamp { get; private set; }

    /// <summary>The helper plan the fix being opened came from is from a verified helper.</summary>
    public bool PlanHelperVerified { get; set; }

    /// <summary>A side page is open.</summary>
    public bool IsSidePage => !ReferenceEquals(_page, _flowPage);

    /// <summary>The flow rail.</summary>
    public ObservableCollection<RailStepViewModel> Rail { get; }

    /// <summary>Demo mode (fixture data; nothing on this PC is read or changed).</summary>
    public bool IsDemo => Services.Connector.IsDemo;

    /// <summary>An unpacked zip: Fix first copies VTDiag to Program Files (the elevated engine runs only from there).</summary>
    public bool NeedsInstallToFix => !IsDemo && Services.Installer is not null && !EngineLauncher.IsInstalled;

    /// <summary>Limited mode (no permission): some things couldn't be checked and no fixes run.</summary>
    public bool IsLimited => Hello?.Privilege == PrivilegeMode.Limited;

    /// <summary>A calm, plain error line.</summary>
    public string? Error { get => _error; set => Set(ref _error, value); }

    /// <summary>An informational banner (drift found, plan loaded...).</summary>
    public string? Banner { get => _banner; set => Set(ref _banner, value); }

    /// <summary>What the player picked on the intake screen.</summary>
    public IntakeAnswers Intake { get; set; } = IntakeAnswers.Empty;

    /// <summary>Binding the UI used for the fix it opened (the engine's preview is recomputed from it).</summary>
    public FixBinding? SelectedBinding { get; set; }

    /// <summary>Fix the UI opened.</summary>
    public string? SelectedFixId { get; set; }

    /// <summary>Helper name when the fix being opened came from an imported plan (badge "Suggested by …").</summary>
    public string? PlanHelperName { get; set; }

    /// <summary>The imported, validated plan the player is working through.</summary>
    public PlanSession? Plan { get; set; }

    /// <summary>Preview the consent screen shows (the token echoes its hashes and nonce).</summary>
    public FixPreview? CurrentPreview { get; set; }

    /// <summary>The latest consent request from the engine.</summary>
    public ConsentRequestedNotification? PendingConsent { get; private set; }

    /// <summary>Latest deadman tick.</summary>
    public DeadmanNotification? Deadman { get; private set; }

    /// <summary>The app came back after a restart: show "Welcome back" before "Did it help?".</summary>
    public bool JustResumed { get; set; }

    /// <summary>Monitor ticks of the current test.</summary>
    public List<MonitorTick> Ticks { get; } = [];

    /// <summary>Scan progress of the current scan.</summary>
    public List<ScanProgress> ScanProgress { get; } = [];

    /// <summary>Fix progress of the current apply.</summary>
    public List<FixProgress> FixProgress { get; } = [];

    /// <summary>Drift findings from the last rescan.</summary>
    public IReadOnlyList<DriftFinding> Drift { get; private set; } = [];

    /// <summary>The player chose "test without monitor" or a test finished: show "Did it help?".</summary>
    public bool OutcomeAsked { get; set; }

    /// <summary>Opens the undo center.</summary>
    public RelayCommand GoUndoCommand { get; }

    /// <summary>Opens the report page.</summary>
    public RelayCommand GoReportCommand { get; }

    /// <summary>Opens settings.</summary>
    public RelayCommand GoSettingsCommand { get; }

    /// <summary>Opens about.</summary>
    public RelayCommand GoAboutCommand { get; }

    /// <summary>Opens helper mode.</summary>
    public RelayCommand GoHelperCommand { get; }

    /// <summary>Opens a helper's plan.</summary>
    public RelayCommand GoPlanImportCommand { get; }

    /// <summary>Closes the side page.</summary>
    public RelayCommand BackCommand { get; }

    /// <summary>Switches the language (parameter <c>en</c>/<c>tr</c>).</summary>
    public RelayCommand SetLanguageCommand { get; }

    /// <summary>Hides the error line.</summary>
    public RelayCommand DismissErrorCommand { get; }

    /// <summary>The engine supports a capability (<c>fixes</c>, <c>monitor</c>, <c>plans</c>).</summary>
    /// <param name="capability">Capability.</param>
    public bool Can(string capability) => Hello?.Capabilities.Contains(capability, StringComparer.Ordinal) ?? false;

    /// <summary>A new ULID.</summary>
    public string NewId() => Services.Ids.NewId();

    /// <summary>Now (client clock; audit only).</summary>
    public DateTimeOffset Now => Services.Clock.GetUtcNow();

    /// <summary>Connects to the engine (once). Resumes a session the engine already has.</summary>
    /// <param name="requested">Elevated or limited.</param>
    public async Task ConnectAsync(PrivilegeMode requested)
    {
        if (Engine is not null)
        {
            return;
        }

        var engine = await Services.Connector.ConnectAsync(requested, CancellationToken.None).ConfigureAwait(true);
        engine.Notification += OnNotification;
        Engine = engine;
        Hello = await engine.HelloAsync(new ClientHello
        {
            ProtocolVersion = EngineProtocol.Version,
            AppVersion = Services.AppVersion,
            Language = Services.Loc.DataLanguage,
            Kind = ClientKind.Ui,
        }, CancellationToken.None).ConfigureAwait(true);
        Raise(nameof(IsLimited));
        Raise(nameof(Hello));
        if (Hello.Session is { } existing)
        {
            Session = existing;
            JustResumed = existing.Resume.Attempts > 0 && existing.Phase is Phase.Resuming or Phase.AwaitingOutcome;
        }
    }

    /// <summary>Sends a command; on refusal shows a plain error and returns false.</summary>
    /// <param name="command">Command.</param>
    public async Task<bool> SendAsync(SessionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Engine is null)
        {
            return false;
        }

        try
        {
            Error = null;
            var state = await Engine.SendAsync(command, CancellationToken.None).ConfigureAwait(true);
            Session = state;
            RequestRoute();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex);
            return false;
        }
    }

    /// <summary>Re-reads the session from the engine (after a refused command) and re-routes.</summary>
    public async Task ResyncAsync()
    {
        if (Engine is null)
        {
            return;
        }

        Session = await Engine.GetSessionAsync(CancellationToken.None).ConfigureAwait(true);
        RequestRoute();
    }

    /// <summary>Shows a calm error line for an exception (known engine codes get their own sentence).</summary>
    /// <param name="ex">Exception.</param>
    public void ShowError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Services.Log.Error("ui.error", ex);
        var key = "Error." + ex.Message;
        Error = Loc.Has(key) ? Services.Loc.Get(key) : Services.Loc.Get("Error.Generic");
    }

    /// <summary>Shows a flow page (start screens before the engine phase decides).</summary>
    /// <param name="page">Page.</param>
    public void ShowFlow(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var side = IsSidePage;
        _flowPage = page;
        if (!side)
        {
            Page = page;
        }
        else
        {
            UpdateRail();
        }
    }

    /// <summary>Opens a side page on top of the flow.</summary>
    /// <param name="page">Page.</param>
    public void OpenSide(PageViewModel page) => Page = page;

    /// <summary>Returns from a side page to the flow.</summary>
    public void CloseSide()
    {
        if (IsSidePage)
        {
            Page = _flowPage;
            RequestRoute();
        }
    }

    /// <summary>Asks for a re-route (coalesced; runs on the UI thread).</summary>
    public void RequestRoute()
    {
        if (_routeRequested)
        {
            return;
        }

        // Deferred: the engine often sends several notifications for one command (Applying → AwaitingConsent + the
        // second-consent request); routing once they have all arrived avoids flashing (and re-previewing) screens.
        _routeRequested = true;
        _dispatcher.BeginInvoke(
            () =>
            {
                if (!_routing)
                {
                    _ = RouteLoopAsync();
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>Completes when no route is pending (tests). Throws if routing never settles.</summary>
    public async Task IdleAsync()
    {
        for (var i = 0; _routing || _routeRequested; i++)
        {
            if (i > 1000)
            {
                throw new TimeoutException("Routing did not settle.");
            }

            await Task.Delay(5).ConfigureAwait(true);
        }

        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Fetches the latest analysis and snapshot when the session points at newer ones.</summary>
    public async Task RefreshDataAsync()
    {
        if (Engine is null || Session is null)
        {
            return;
        }

        if (Session.AnalysisId is not null && !string.Equals(Session.AnalysisId, Analysis?.AnalysisId, StringComparison.Ordinal))
        {
            Analysis = await Engine.GetAnalysisAsync(CancellationToken.None).ConfigureAwait(true);
            Raise(nameof(Analysis));
        }

        if (Session.SnapshotId is not null && !string.Equals(Session.SnapshotId, Snapshot?.Meta.SnapshotId, StringComparison.Ordinal))
        {
            Snapshot = await Engine.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
            Raise(nameof(Snapshot));
        }
    }

    private async Task ShowSafelyAsync(PageViewModel page)
    {
        try
        {
            await page.OnShownAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A page that fails to load shows a calm error instead of a blank screen or a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError(ex);
        }
    }

    private async Task RouteLoopAsync()
    {
        _routing = true;
        try
        {
            while (_routeRequested)
            {
                _routeRequested = false;
                await RouteAsync().ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Routing must never crash the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError(ex);
        }
        finally
        {
            _routing = false;
        }
    }

    private async Task RouteAsync()
    {
        var s = Session;
        if (s is null)
        {
            return;
        }

        await RefreshDataAsync().ConfigureAwait(true);
        var next = Desired(s);
        if (next is not null && !ReferenceEquals(next, _flowPage))
        {
            ShowFlow(next);
        }
        else
        {
            _flowPage.Refresh();
        }

        // A decision only the player can make is waiting (consent, "is your internet still working?"): always show it.
        // An agent's undo request (itself a side page) is answered first, then the flow shows.
        var decisionWaiting = (s.Phase == Phase.AwaitingConsent && PendingConsent is not null) || (s.Phase == Phase.Verifying && Deadman is not null);
        if (decisionWaiting && IsSidePage && _page is not UndoConsentViewModel)
        {
            CloseSide();
        }
    }

    private PageViewModel? Desired(SessionState s)
    {
        var current = _flowPage;
        return s.Phase switch
        {
            Phase.Intake => current is WelcomeViewModel or IntakeViewModel or PermissionViewModel ? current : new WelcomeViewModel(this),
            Phase.Scanning => current as ScanViewModel ?? new ScanViewModel(this),
            Phase.Analyzed => current is HealthViewModel or SuspectsViewModel ? current : new HealthViewModel(this),
            Phase.Proposing => current as SuspectsViewModel ?? new SuspectsViewModel(this),
            Phase.AwaitingConsent => current is FixDetailViewModel f && f.Matches(s, PendingConsent) ? current : new FixDetailViewModel(this),
            Phase.Applying => current as ApplyingViewModel ?? new ApplyingViewModel(this),
            Phase.Verifying => Deadman is not null && string.Equals(Deadman.AttemptId, s.CurrentAttemptId, StringComparison.Ordinal)
                ? current as DeadmanViewModel ?? new DeadmanViewModel(this)
                : current as ApplyingViewModel ?? new ApplyingViewModel(this),
            Phase.AwaitingRestart => current as RestartViewModel ?? new RestartViewModel(this, RestartKind.Normal),
            Phase.RestartRequested => current as RestartingViewModel ?? new RestartingViewModel(this),
            Phase.Resuming => current as ResumeViewModel ?? new ResumeViewModel(this),
            Phase.GuidedStep => current is GuideViewModel or RestartViewModel { IsFirmware: true } ? current : new GuideViewModel(this),
            Phase.AwaitingOutcome => JustResumed
                ? current as ResumeViewModel ?? new ResumeViewModel(this)
                : current as OutcomeViewModel ?? new OutcomeViewModel(this),
            Phase.Monitoring => current as MonitoringViewModel ?? new MonitoringViewModel(this),
            Phase.Completed or Phase.RolledBack or Phase.Abandoned => current as CompletedViewModel ?? new CompletedViewModel(this),
            Phase.NeedsAttention => current as AttentionViewModel ?? new AttentionViewModel(this),
            _ => null,
        };
    }

    private void OnNotification(object? sender, EngineNotification n)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnNotification(sender, n));
            return;
        }

        switch (n)
        {
            case SessionChangedNotification e:
                if (e.Session.Phase == Phase.Scanning && Session?.Phase != Phase.Scanning)
                {
                    ScanProgress.Clear();
                }

                if (e.Session.Phase == Phase.Applying && Session?.Phase != Phase.Applying)
                {
                    // A new attempt starts: nothing from the previous fix's test or deadman carries over.
                    FixProgress.Clear();
                    Ticks.Clear();
                    OutcomeAsked = false;
                    Deadman = null;
                }

                if (e.Session.Phase != Phase.AwaitingConsent)
                {
                    PendingConsent = null; // a consent request is only valid while the engine waits for it
                }

                Session = e.Session;
                RequestRoute();
                break;
            case ScanProgressNotification e:
                ScanProgress.Add(e.Progress);
                (_flowPage as ScanViewModel)?.Refresh();
                break;
            case FixProgressNotification e:
                FixProgress.Add(e.Progress);
                (_flowPage as ApplyingViewModel)?.Refresh();
                break;
            case MonitorTickNotification e:
                Ticks.Add(e.Tick);
                (_flowPage as MonitoringViewModel)?.Refresh();
                break;
            case DeadmanNotification e:
                Deadman = e;
                DeadmanReceivedTimestamp = Services.Clock.GetTimestamp();
                (_flowPage as DeadmanViewModel)?.Resync();
                RequestRoute();
                break;
            case DriftNotification e:
                Drift = e.Findings;
                Banner = e.Findings.Count == 0 ? null : Services.Loc.Format("Banner.Drift", ("count", e.Findings.Count));
                break;
            case ConsentRequestedNotification e:
                PendingConsent = e;
                if (e.Kind == ConsentKind.Undo)
                {
                    OpenSide(new UndoConsentViewModel(this, e));
                }
                else
                {
                    RequestRoute();
                }

                AttentionRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                break;
        }
    }

    private void UpdateRail()
    {
        var step = _flowPage.Step;
        foreach (var r in Rail)
        {
            r.IsCurrent = r.Step == step;
            r.IsDone = step != FlowStep.None && r.Step < step;
        }
    }
}
