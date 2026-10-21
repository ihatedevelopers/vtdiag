using VTD.App.Mvvm;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;

namespace VTD.App.ViewModels;

/// <summary>One guide step.</summary>
public sealed class GuideStepViewModel : ObservableObject
{
    /// <summary>Creates it.</summary>
    /// <param name="index">Index.</param>
    /// <param name="text">Text.</param>
    /// <param name="actionText">Action button text.</param>
    /// <param name="isDone">Done.</param>
    /// <param name="done">Mark done.</param>
    /// <param name="action">Run the action.</param>
    public GuideStepViewModel(int index, string text, string? actionText, bool isDone, AsyncCommand done, AsyncCommand? action)
    {
        Index = index;
        Number = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Text = text;
        ActionText = actionText;
        IsDone = isDone;
        DoneCommand = done;
        ActionCommand = action;
    }

    /// <summary>Index.</summary>
    public int Index { get; }

    /// <summary>Step number.</summary>
    public string Number { get; }

    /// <summary>Text.</summary>
    public string Text { get; }

    /// <summary>Action button text ("Restart into BIOS").</summary>
    public string? ActionText { get; }

    /// <summary>Has an action.</summary>
    public bool HasAction => ActionText is not null && ActionCommand is not null;

    /// <summary>Done.</summary>
    public bool IsDone { get; }

    /// <summary>Mark done.</summary>
    public AsyncCommand DoneCommand { get; }

    /// <summary>Action.</summary>
    public AsyncCommand? ActionCommand { get; }
}

/// <summary>An alternative guide branch.</summary>
/// <param name="Id">Branch id.</param>
/// <param name="Text">Branch text.</param>
/// <param name="Pick">Switch to it.</param>
public sealed record GuideBranchViewModel(string Id, string Text, RelayCommand Pick);

/// <summary>Screen 11: a guided manual step (BIOS, Secure Boot, uninstall): pre-flight, board-specific steps, restart into BIOS.</summary>
public sealed class GuideViewModel : PageViewModel
{
    private string? _branchId;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public GuideViewModel(ShellViewModel shell)
        : base(shell)
    {
        LaterCommand = new RelayCommand(() => Shell.OpenSide(new SuspectsViewModel(Shell)));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Title.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Branch explanation.</summary>
    public string? BranchText { get; private set; }

    /// <summary>Bucket sentence (a guide is a step only the player can do).</summary>
    public string BucketText { get; private set; } = string.Empty;

    /// <summary>"Before you start" warnings (BitLocker recovery key...).</summary>
    public IReadOnlyList<string> Preflight { get; private set; } = [];

    /// <summary>Has preflight lines.</summary>
    public bool HasPreflight => Preflight.Count > 0;

    /// <summary>Steps.</summary>
    public IReadOnlyList<GuideStepViewModel> Steps { get; private set; } = [];

    /// <summary>Other branches ("If that doesn't match your PC").</summary>
    public IReadOnlyList<GuideBranchViewModel> OtherBranches { get; private set; } = [];

    /// <summary>Has other branches.</summary>
    public bool HasOtherBranches => OtherBranches.Count > 0;

    /// <summary>Vendor line ("Your board: ASUS · key: Del").</summary>
    public string? VendorLine { get; private set; }

    /// <summary>Where the same steps live for a phone ("docs/guides/secure-boot.md").</summary>
    public string? DocsLine { get; private set; }

    /// <summary>I'll do it later (back to the results).</summary>
    public RelayCommand LaterCommand { get; }

    /// <inheritdoc />
    public override void Refresh()
    {
        var progress = Shell.Session?.Guide;
        var guide = progress is null ? null : S.Kb.FindGuide(progress.GuideId);
        if (guide is null || guide.Branches.Count == 0)
        {
            base.Refresh();
            return;
        }

        Title = S.Text.Get(guide.TitleKey);
        BucketText = Words.BucketExplain(guide.Bucket);
        _branchId ??= progress!.BranchId
            ?? Shell.Analysis?.Guides.FirstOrDefault(g => string.Equals(g.GuideId, guide.Id, StringComparison.Ordinal))?.BranchId
            ?? guide.Branches[0].Id;
        var branch = guide.Branches.FirstOrDefault(b => string.Equals(b.Id, _branchId, StringComparison.Ordinal)) ?? guide.Branches[0];
        BranchText = branch.TextKey is null ? null : S.Text.Get(branch.TextKey);
        Preflight = [.. guide.Preflight.Select(p => S.Text.Get(p.FailKey))];
        var vendorArgs = VendorArgs(branch);
        VendorLine = vendorArgs is null ? null : L.Format("Guide.Vendor", ("vendor", vendorArgs["vendor"]), ("key", vendorArgs["key"]));
        DocsLine = guide.DocsPath is null ? null : L.Format("Guide.Docs", ("path", guide.DocsPath));
        var doneUpTo = progress!.StepIndex;
        Steps = [.. branch.Steps.Select((st, i) =>
        {
            var index = i;
            var done = new AsyncCommand(() => Shell.SendAsync(new GuideStepCompleted { EventId = Shell.NewId(), Utc = Shell.Now, GuideId = guide.Id, StepIndex = index }), () => index >= doneUpTo);
            AsyncCommand? action = st.Action switch
            {
                GuideAction.RestartToFirmware => new AsyncCommand(() =>
                {
                    Shell.ShowFlow(new RestartViewModel(Shell, RestartKind.ToFirmware));
                    return Task.CompletedTask;
                }),
                GuideAction.CopyText when st.Target is not null => new AsyncCommand(() =>
                {
                    S.Io.SetClipboard(S.Text.Get(st.Target));
                    return Task.CompletedTask;
                }),
                GuideAction.RunCheck => new AsyncCommand(() => Shell.SendAsync(new RescanRequested { EventId = Shell.NewId(), Utc = Shell.Now })),
                _ => null,
            };
            var actionText = st.Action switch
            {
                GuideAction.RestartToFirmware => L.Get("Guide.RestartBios"),
                GuideAction.CopyText => L.Get("Guide.Copy"),
                GuideAction.RunCheck => L.Get("Guide.CheckNow"),
                _ => null,
            };
            return new GuideStepViewModel(i, S.Text.Get(st.TextKey, vendorArgs), actionText, i < doneUpTo, done, action);
        })];
        OtherBranches = [.. guide.Branches.Where(b => !ReferenceEquals(b, branch)).Select(b => new GuideBranchViewModel(
            b.Id,
            Localization.Loc.Has("Guide.Branch." + b.Id) ? L.Get("Guide.Branch." + b.Id) : b.TextKey is not null && S.Text.Has(b.TextKey) ? S.Text.Get(b.TextKey) : Localization.KbText.Humanize(b.Id),
            new RelayCommand(() =>
            {
                _branchId = b.Id;
                Refresh();
            })))];
        base.Refresh();
    }

    private Dictionary<string, string>? VendorArgs(GuideBranchDef branch)
    {
        var tableId = branch.Steps.Select(s => s.VendorTable).FirstOrDefault(t => t is not null);
        var table = tableId is null ? null : S.Kb.Tables.FirstOrDefault(t => string.Equals(t.Id, tableId, StringComparison.Ordinal));
        if (table is null)
        {
            return null;
        }

        string? vendor = null;
        if (Shell.Snapshot?.Hardware.Board.TryGetValue(out var board) == true)
        {
            vendor = (board.SystemManufacturer ?? board.BoardManufacturer)?.Trim().TrimEnd('.');
        }

        var keyColumn = table.KeyColumn ?? "vendor";
        var row = vendor is null ? null : table.Rows.FirstOrDefault(r => r.TryGetValue(keyColumn, out var v) && string.Equals(v, vendor, StringComparison.OrdinalIgnoreCase));
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vendor"] = vendor ?? L.Get("Guide.UnknownVendor"),
            ["key"] = row is not null && row.TryGetValue("key", out var key) ? key : L.Get("Guide.DefaultKey"),
            ["path"] = row is not null && row.TryGetValue("path", out var path) ? path : L.Get("Guide.DefaultPath"),
        };
        return args;
    }
}

/// <summary>Screen 12: completed (or rolled back / abandoned): summary, keep changes, report, undo.</summary>
public sealed class CompletedViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public CompletedViewModel(ShellViewModel shell)
        : base(shell)
    {
        ReportCommand = new RelayCommand(() => Shell.OpenSide(new ReportViewModel(Shell)));
        UndoCommand = new RelayCommand(() => Shell.OpenSide(new UndoCenterViewModel(Shell)));
        WatchCommand = new AsyncCommand(() => Shell.SendAsync(new MonitorRequested { EventId = Shell.NewId(), Utc = Shell.Now, Purpose = Contracts.Monitoring.MonitorPurpose.UserRequested }), () => Shell.Can("monitor"));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Share;

    /// <summary>Headline by phase.</summary>
    public string Headline => L.Get("Completed.Title." + (Shell.Session?.Phase ?? Phase.Completed));

    /// <summary>Sub line by phase.</summary>
    public string SubLine => L.Get("Completed.Sub." + (Shell.Session?.Phase ?? Phase.Completed));

    /// <summary>What was done.</summary>
    public IReadOnlyList<LineViewModel> Attempts { get; private set; } = [];

    /// <summary>Has attempts.</summary>
    public bool HasAttempts => Attempts.Count > 0;

    /// <summary>Save report.</summary>
    public RelayCommand ReportCommand { get; }

    /// <summary>Undo something.</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>Watch my next match.</summary>
    public AsyncCommand WatchCommand { get; }

    /// <inheritdoc />
    public override void Refresh()
    {
        Attempts = [.. (Shell.Session?.Attempts ?? []).Select(a => new LineViewModel(
            S.Text.Get(S.Kb.FindFix(a.FixId)?.TitleKey ?? a.FixId),
            a.Status is AttemptStatus.Undone or AttemptStatus.RolledBack ? HealthStatus.NotApplicable : a.Outcome == FixOutcome.Fixed ? HealthStatus.Ok : HealthStatus.Warn,
            Words.Attempt(a.Status) + L.Get("Common.ListSeparator") + Words.Outcome(a.Outcome)))];
        base.Refresh();
    }
}

/// <summary>NeedsAttention: expired, too many resume attempts or recovery in doubt: "Here's what we changed".</summary>
public sealed class AttentionViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public AttentionViewModel(ShellViewModel shell)
        : base(shell)
    {
        UndoAllCommand = new AsyncCommand(() => Shell.SendAsync(new UndoAllRequested { EventId = Shell.NewId(), Utc = Shell.Now }));
        KeepCommand = new AsyncCommand(() => Shell.SendAsync(new AbandonRequested { EventId = Shell.NewId(), Utc = Shell.Now }));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Fix;

    /// <summary>Why.</summary>
    public string Reason => Shell.Session?.NeedsAttentionReasonKey is { } k ? S.Text.Get(k) : L.Get("Attention.Generic");

    /// <summary>What we changed.</summary>
    public IReadOnlyList<LineViewModel> Changes => [.. (Shell.Session?.Attempts ?? []).Select(a => new LineViewModel(S.Text.Get(S.Kb.FindFix(a.FixId)?.TitleKey ?? a.FixId), HealthStatus.Warn, Words.Attempt(a.Status)))];

    /// <summary>Undo all.</summary>
    public AsyncCommand UndoAllCommand { get; }

    /// <summary>Keep.</summary>
    public AsyncCommand KeepCommand { get; }
}

/// <summary>An undo proposed by the CLI/MCP: only the player can confirm it.</summary>
public sealed class UndoConsentViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    /// <param name="request">Request.</param>
    public UndoConsentViewModel(ShellViewModel shell, ConsentRequestedNotification request)
        : base(shell)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        UndoCommand = new AsyncCommand(
            async () =>
            {
                if (await Shell.SendAsync(new UndoRequested { EventId = Shell.NewId(), Utc = Shell.Now, AttemptId = Request.AttemptId! }).ConfigureAwait(true))
                {
                    Shell.CloseSide();
                }
            },
            () => CanUndo && Request.AttemptId is not null);
        KeepCommand = new RelayCommand(Shell.CloseSide);
    }

    /// <summary>Request.</summary>
    public ConsentRequestedNotification Request { get; }

    /// <summary>The change can be undone with a click (one-way changes can't; no fake undo button).</summary>
    public bool CanUndo => Shell.Session?.Attempts.FirstOrDefault(a => string.Equals(a.AttemptId, Request.AttemptId, StringComparison.Ordinal)) is { } a
        && a.Reversibility != Reversibility.OneWay && a.Status is not (AttemptStatus.Undone or AttemptStatus.RolledBack);

    /// <summary>Who asked.</summary>
    public string ProposerText => Words.Proposer(Request.ProposedBy) ?? L.Get("Proposer.Cli");

    /// <summary>Fix title.</summary>
    public string FixTitle => S.Text.Get(S.Kb.FindFix(Request.FixId)?.TitleKey ?? Request.FixId);

    /// <summary>Undo it.</summary>
    public AsyncCommand UndoCommand { get; }

    /// <summary>Keep it.</summary>
    public RelayCommand KeepCommand { get; }
}
