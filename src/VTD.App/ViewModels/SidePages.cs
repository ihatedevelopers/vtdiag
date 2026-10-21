using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using VTD.App.Localization;
using VTD.App.Mvvm;
using VTD.Contracts;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Reporting;

namespace VTD.App.ViewModels;

/// <summary>An undo-center row.</summary>
public sealed class AttemptRowViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    /// <param name="page">Page.</param>
    /// <param name="a">Attempt.</param>
    public AttemptRowViewModel(UndoCenterViewModel page, AttemptSummary a)
    {
        ArgumentNullException.ThrowIfNull(page);
        Attempt = a ?? throw new ArgumentNullException(nameof(a));
        var l = Loc.Instance;
        Title = page.S.Text.Get(a.TitleKey);
        Date = Words.Date(a.AppliedUtc);
        StatusText = Words.Attempt(a.Status);
        IsOneWay = a.Reversibility == Reversibility.OneWay;
        OneWayText = IsOneWay
            ? a.RestorePointUtc is { } rp ? l.Format("Undo.OneWayRestorePoint", ("date", Words.Date(rp))) : l.Get("Undo.OneWay")
            : null;
        BlockedText = !a.CanUndo && !IsOneWay && a.UndoBlockedReasonKey is not null ? page.S.Text.Get(a.UndoBlockedReasonKey) : null;
        UndoCommand = new AsyncCommand(() => page.UndoAsync(a.AttemptId, UndoMode.IfUnchanged), () => CanUndo);
        ForceCommand = new AsyncCommand(() => page.UndoAsync(a.AttemptId, UndoMode.Force), () => IsConflict);
    }

    /// <summary>Attempt.</summary>
    public AttemptSummary Attempt { get; }

    /// <summary>Title.</summary>
    public string Title { get; }

    /// <summary>When.</summary>
    public string Date { get; }

    /// <summary>Status.</summary>
    public string StatusText { get; }

    /// <summary>One-way (no fake undo button).</summary>
    public bool IsOneWay { get; }

    /// <summary>"Can't be undone with a click. Restore point from …".</summary>
    public string? OneWayText { get; }

    /// <summary>Why undo isn't possible.</summary>
    public string? BlockedText { get; }

    /// <summary>Undo is possible now.</summary>
    public bool CanUndo => Attempt.CanUndo && !IsOneWay && Attempt.Status is not (AttemptStatus.Undone or AttemptStatus.RolledBack or AttemptStatus.UndoConflict);

    /// <summary>Someone changed the setting again since our fix.</summary>
    public bool IsConflict => Attempt.Status == AttemptStatus.UndoConflict;

    /// <summary>Undo.</summary>
    public AsyncCommand UndoCommand { get; }

    /// <summary>Put it back anyway (after a conflict).</summary>
    public AsyncCommand ForceCommand { get; }
}

/// <summary>The undo center: every change VTDiag made, across sessions.</summary>
public sealed class UndoCenterViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public UndoCenterViewModel(ShellViewModel shell)
        : base(shell)
    {
        UndoAllCommand = new AsyncCommand(UndoAllAsync, () => Rows.Any(r => r.CanUndo));
    }

    /// <summary>Rows.</summary>
    public ObservableCollection<AttemptRowViewModel> Rows { get; } = [];

    /// <summary>Nothing changed yet.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Undo everything.</summary>
    public AsyncCommand UndoAllCommand { get; }

    /// <inheritdoc />
    public override async Task OnShownAsync() => await ReloadAsync().ConfigureAwait(true);

    /// <summary>Undoes one attempt.</summary>
    /// <param name="attemptId">Attempt.</param>
    /// <param name="mode">Mode.</param>
    public async Task UndoAsync(string attemptId, UndoMode mode)
    {
        await Shell.SendAsync(new UndoRequested { EventId = Shell.NewId(), Utc = Shell.Now, AttemptId = attemptId, Mode = mode }).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        _ = ReloadAsync();
    }

    private async Task UndoAllAsync()
    {
        await Shell.SendAsync(new UndoAllRequested { EventId = Shell.NewId(), Utc = Shell.Now }).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        Rows.Clear();
        if (Shell.Engine is not null)
        {
            foreach (var a in (await Shell.Engine.ListAttemptsAsync(CancellationToken.None).ConfigureAwait(true)).OrderByDescending(a => a.AppliedUtc))
            {
                Rows.Add(new AttemptRowViewModel(this, a));
            }
        }

        UndoAllCommand.Refresh();
        Raise(string.Empty);
    }
}

/// <summary>A snapshot section the player can leave out of an export.</summary>
/// <param name="id">Section id (snapshot JSON property name).</param>
/// <param name="text">Friendly name.</param>
public sealed class SectionViewModel(string id, string text) : ObservableObject
{
    private bool _included = true;

    /// <summary>Id.</summary>
    public string Id { get; } = id;

    /// <summary>Friendly name.</summary>
    public string Text { get; } = text;

    /// <summary>Included.</summary>
    public bool IsIncluded { get => _included; set => Set(ref _included, value); }
}

/// <summary>Report / share: Copy for Reddit, Copy for AI, save .html/.json/.vtdscan, with a redaction preview.</summary>
public sealed class ReportViewModel : PageViewModel
{
    private string _preview = string.Empty;
    private string? _status;
    private bool _statusIsProblem;
    private bool _includeServerIp;
    private IReadOnlyList<string> _blockedSections = [];

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public ReportViewModel(ShellViewModel shell)
        : base(shell)
    {
        Sections = [.. ShareExporter.ExcludableSections.Select(id => new SectionViewModel(id, Loc.Has("Section." + id) ? L.Get("Section." + id) : KbText.Humanize(id)))];
        CopyRedditCommand = new AsyncCommand(() => CopyAsync(ExportKind.ReportMarkdown, "Report.Copied.Reddit"), () => HasScan);
        CopyAiCommand = new AsyncCommand(() => CopyAsync(ExportKind.AiDigest, "Report.Copied.Ai"), () => HasScan);
        SaveHtmlCommand = new AsyncCommand(() => SaveAsync(ExportKind.ReportHtml, "Report.Filter.Html"), () => HasScan);
        SaveJsonCommand = new AsyncCommand(() => SaveAsync(ExportKind.ReportJson, "Report.Filter.Json"), () => HasScan);
        SaveBundleCommand = new AsyncCommand(() => SaveAsync(ExportKind.ScanBundle, "Report.Filter.Bundle"), () => HasScan);
        PreviewCommand = new AsyncCommand(LoadPreviewAsync, () => HasScan);
        LeaveOutCommand = new AsyncCommand(LeaveOutAsync, () => _blockedSections.Count > 0);
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Share;

    /// <summary>Opened from "Get help from a helper".</summary>
    public bool HelperFocus { get; init; }

    /// <summary>A scan exists.</summary>
    public bool HasScan => Shell.Engine is not null && Shell.Session?.SnapshotId is not null;

    /// <summary>Sections.</summary>
    public IReadOnlyList<SectionViewModel> Sections { get; }

    /// <summary>Keep the game server address (for an ISP ticket).</summary>
    public bool IncludeServerIp { get => _includeServerIp; set => Set(ref _includeServerIp, value); }

    /// <summary>The Reddit text as it will be shared.</summary>
    public string PreviewText { get => _preview; private set => Set(ref _preview, value); }

    /// <summary>Characters in the preview (Reddit comments allow 10,000).</summary>
    public string PreviewLength => L.Format("Report.Length", ("count", PreviewText.Length.ToString("N0", L.Culture)));

    /// <summary>"We hid or changed 12 things: your user name, …".</summary>
    public string RedactionSummary { get; private set; } = string.Empty;

    /// <summary>Last action result.</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The status is a problem (blocked/failed).</summary>
    public bool StatusIsProblem { get => _statusIsProblem; private set => Set(ref _statusIsProblem, value); }

    /// <summary>The sweep blocked an export.</summary>
    public bool IsBlocked => _blockedSections.Count > 0;

    /// <summary>Copy for Reddit.</summary>
    public AsyncCommand CopyRedditCommand { get; }

    /// <summary>Copy for AI.</summary>
    public AsyncCommand CopyAiCommand { get; }

    /// <summary>Save the report (.html).</summary>
    public AsyncCommand SaveHtmlCommand { get; }

    /// <summary>Save JSON.</summary>
    public AsyncCommand SaveJsonCommand { get; }

    /// <summary>Save a scan file for a helper (.vtdscan).</summary>
    public AsyncCommand SaveBundleCommand { get; }

    /// <summary>Update the preview.</summary>
    public AsyncCommand PreviewCommand { get; }

    /// <summary>Leave the blocked section out and try again.</summary>
    public AsyncCommand LeaveOutCommand { get; }

    /// <inheritdoc />
    public override async Task OnShownAsync()
    {
        if (HasScan)
        {
            await LoadPreviewAsync().ConfigureAwait(true);
        }

        Raise(string.Empty);
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (HasScan && PreviewText.Length > 0)
        {
            _ = LoadPreviewAsync();
        }

        base.Refresh();
    }

    private async Task<ExportResult?> ExportAsync(ExportKind kind)
    {
        var request = new ExportRequest
        {
            Kind = kind,
            Language = L.DataLanguage,
            ExcludedSections = [.. Sections.Where(s => !s.IsIncluded).Select(s => s.Id)],
            IncludeServerIp = IncludeServerIp,
        };
        var result = await Shell.Engine!.ExportAsync(request, CancellationToken.None).ConfigureAwait(true);
        switch (result.Status)
        {
            case ExportStatus.BlockedByRedaction:
                _blockedSections = [.. result.SweepHits.Select(h => h.Section).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.Ordinal)];
                var names = _blockedSections.Select(id => Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))?.Text ?? id);
                SetStatus(_blockedSections.Count > 0 ? L.Format("Report.Blocked", ("sections", string.Join(", ", names))) : L.Get("Report.BlockedNoSection"), problem: true);
                LeaveOutCommand.Refresh();
                Raise(nameof(IsBlocked));
                return null;
            case ExportStatus.Failed:
                SetStatus(L.Get("Report.Failed"), problem: true);
                return null;
            default:
                _blockedSections = [];
                LeaveOutCommand.Refresh();
                Raise(nameof(IsBlocked));
                UpdateSummary(result.Redaction);
                return result;
        }
    }

    private async Task LoadPreviewAsync()
    {
        var r = await ExportAsync(ExportKind.ReportMarkdown).ConfigureAwait(true);
        PreviewText = r?.TextContent ?? string.Empty;
        Raise(nameof(PreviewLength));
    }

    private async Task CopyAsync(ExportKind kind, string doneKey)
    {
        var r = await ExportAsync(kind).ConfigureAwait(true);
        if (r?.TextContent is { } text)
        {
            S.Io.SetClipboard(text);
            SetStatus(L.Get(doneKey), problem: false);
        }
    }

    private async Task SaveAsync(ExportKind kind, string filterKey)
    {
        var r = await ExportAsync(kind).ConfigureAwait(true);
        if (r is null)
        {
            return;
        }

        var bytes = r.Base64Content is not null ? Convert.FromBase64String(r.Base64Content) : Encoding.UTF8.GetBytes(r.TextContent ?? string.Empty);
        if (S.Io.Save(r.FileName ?? "VTDiag-Report", L.Get(filterKey), bytes))
        {
            SetStatus(L.Format("Report.Saved", ("name", r.FileName ?? string.Empty)), problem: false);
        }
    }

    private async Task LeaveOutAsync()
    {
        foreach (var s in Sections.Where(s => _blockedSections.Contains(s.Id, StringComparer.Ordinal)))
        {
            s.IsIncluded = false;
        }

        await LoadPreviewAsync().ConfigureAwait(true);
        if (!IsBlocked)
        {
            SetStatus(L.Get("Report.LeftOut"), problem: false);
        }
    }

    private void UpdateSummary(RedactionManifest? manifest)
    {
        if (manifest is null)
        {
            RedactionSummary = string.Empty;
        }
        else
        {
            var total = manifest.Entries.Sum(e => e.Count);
            var kinds = manifest.Entries.Select(e => RuleName(e.Rule)).Distinct(StringComparer.Ordinal).Take(5).ToList();
            RedactionSummary = total == 0 ? L.Get("Report.RedactedNone") : L.Format("Report.Redacted", ("count", total), ("what", string.Join(", ", kinds)));
        }

        Raise(nameof(RedactionSummary));
    }

    private static string RuleName(string rule)
    {
        var head = rule.Split('.')[0];
        return Loc.Has("Rule." + head) ? Loc.Instance.Get("Rule." + head) : Loc.Instance.Get("Rule.other");
    }

    private void SetStatus(string text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }
}

/// <summary>Settings: language (EN/TR, pseudo for testing), developer tools in demo mode.</summary>
public sealed class SettingsViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public SettingsViewModel(ShellViewModel shell)
        : base(shell)
    {
        SimulateAgentCommand = new AsyncCommand(SimulateAgentAsync, () => Shell.Engine is Services.DemoEngine && Shell.Session?.SnapshotId is not null);
        CreateBundleCommand = new AsyncCommand(CreateBundleAsync);
    }

    private bool _debugMode;
    private string? _bundleStatus;
    private bool _bundleProblem;

    /// <summary>Debug mode: the log keeps more detail while the player reproduces the problem.</summary>
    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (Set(ref _debugMode, value))
            {
                S.Log.DebugMode = value;
                _ = ApplyDebugAsync(value);
            }
        }
    }

    /// <summary>Result of the last "Create debug bundle".</summary>
    public string? BundleStatus { get => _bundleStatus; private set => Set(ref _bundleStatus, value); }

    /// <summary>The last bundle attempt failed or was blocked.</summary>
    public bool BundleProblem { get => _bundleProblem; private set => Set(ref _bundleProblem, value); }

    /// <summary>Create the .vtddebug file.</summary>
    public AsyncCommand CreateBundleCommand { get; }

    private async Task ApplyDebugAsync(bool on)
    {
        if (Shell.Engine is { } engine)
        {
            await engine.SetDebugModeAsync(on, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task CreateBundleAsync()
    {
        BundleProblem = true;
        try
        {
            if (Shell.Engine is null)
            {
                await Shell.ConnectAsync(PrivilegeMode.Limited).ConfigureAwait(true);
            }

            if (_debugMode)
            {
                await ApplyDebugAsync(true).ConfigureAwait(true);
            }

            var r = await Shell.Engine!.ExportAsync(new ExportRequest { Kind = ExportKind.DebugBundle, Language = L.DataLanguage }, CancellationToken.None).ConfigureAwait(true);
            if (r.Status == ExportStatus.BlockedByRedaction)
            {
                BundleStatus = L.Format("Debug.Blocked", ("sections", string.Join(", ", r.SweepHits.Select(h => h.Section ?? "?").Distinct(StringComparer.Ordinal))));
                return;
            }

            if (r.Status != ExportStatus.Ok || r.Base64Content is null)
            {
                BundleStatus = L.Get("Debug.Failed");
                return;
            }

            if (S.Io.Save(r.FileName ?? "VTDiag-debug.vtddebug", L.Get("Debug.Filter"), Convert.FromBase64String(r.Base64Content)))
            {
                BundleStatus = L.Format("Report.Saved", ("name", r.FileName ?? string.Empty));
                BundleProblem = false;
            }
            else
            {
                BundleStatus = null;
            }
        }
        catch (InvalidOperationException ex)
        {
            S.Log.Error("bundle.failed", ex);
            BundleStatus = L.Get("Debug.Failed");
        }
    }

    /// <summary>English selected.</summary>
    public bool IsEnglish
    {
        get => string.Equals(L.Language, Languages.English, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                L.Language = Languages.English;
            }
        }
    }

    /// <summary>Turkish selected.</summary>
    public bool IsTurkish
    {
        get => L.IsTurkish;
        set
        {
            if (value)
            {
                L.Language = Languages.Turkish;
            }
        }
    }

    /// <summary>Pseudo-localization selected.</summary>
    public bool IsPseudo
    {
        get => string.Equals(L.Language, Loc.Pseudo, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                L.Language = Loc.Pseudo;
            }
        }
    }

    /// <summary>Developer tools visible.</summary>
    public bool ShowDeveloper => S.DeveloperMode || Shell.IsDemo;

    /// <summary>Demo: an AI agent proposes a one-way fix (shows the badge and the second click).</summary>
    public AsyncCommand SimulateAgentCommand { get; }

    private async Task SimulateAgentAsync()
    {
        if (Shell.Engine is Services.DemoEngine demo)
        {
            await demo.SimulateAgentProposalAsync().ConfigureAwait(true);
        }
    }
}

/// <summary>About: version, KB version, privacy promises, "not affiliated with Riot Games".</summary>
/// <param name="shell">Shell.</param>
public sealed class AboutViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    /// <summary>"Version 0.0.1 · knowledge base 2026.10.1".</summary>
    public string VersionLine => L.Format("About.Version", ("app", S.AppVersion), ("kb", S.Kb.Version.Version));
}

/// <summary>An imported plan the player is working through.</summary>
/// <param name="Plan">Plan (as parsed for display).</param>
/// <param name="Validation">Engine validation.</param>
/// <param name="HelperName">Name to show (verified, or as written).</param>
public sealed record PlanSession(FixPlan Plan, PlanValidation Validation, string HelperName);

/// <summary>One step of an imported plan.</summary>
/// <param name="Number">Number.</param>
/// <param name="Title">Title.</param>
/// <param name="Note">Helper note (plain text; links are marked, never opened).</param>
/// <param name="IsGreyed">Doesn't apply.</param>
/// <param name="Reason">Why greyed.</param>
/// <param name="StartCommand">Start this step.</param>
public sealed record PlanStepRowViewModel(string Number, string Title, string? Note, bool IsGreyed, string? Reason, AsyncCommand StartCommand)
{
    /// <summary>Has a note.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>Open a helper's plan (.vtdplan): trust badge, plain-text message, steps (greyed with a reason when they don't fit).</summary>
public sealed partial class PlanImportViewModel : PageViewModel
{
    private PlanSession? _session;
    private string? _status;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public PlanImportViewModel(ShellViewModel shell)
        : base(shell)
    {
        OpenCommand = new AsyncCommand(OpenAsync, () => CanImport);
    }

    /// <summary>A scan exists (plans are checked against this PC).</summary>
    public bool CanImport => Shell.Engine is not null && Shell.Session?.SnapshotId is not null;

    /// <summary>Loaded.</summary>
    public bool IsLoaded => _session is not null;

    /// <summary>Trust line.</summary>
    public string TrustText { get; private set; } = string.Empty;

    /// <summary>Trust.</summary>
    public PlanTrust Trust => _session?.Validation.Trust ?? PlanTrust.Unsigned;

    /// <summary>Helper's message (plain text).</summary>
    public string? Message { get; private set; }

    /// <summary>Plan-level issues.</summary>
    public IReadOnlyList<string> Issues { get; private set; } = [];

    /// <summary>Has issues.</summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>Steps.</summary>
    public IReadOnlyList<PlanStepRowViewModel> Steps { get; private set; } = [];

    /// <summary>Status line.</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Open a .vtdplan.</summary>
    public AsyncCommand OpenCommand { get; }

    /// <summary>Loads a plan text (also used by tests).</summary>
    /// <param name="text">File content.</param>
    public async Task LoadAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var plan = Parse(text);
        var validation = await Shell.Engine!.ImportPlanAsync(text, CancellationToken.None).ConfigureAwait(true);
        if (plan is null)
        {
            _session = null;
            Issues = [L.Get("PlanIssue.schema")];
            Status = L.Get("Plan.Unreadable");
            Raise(string.Empty);
            return;
        }

        var name = validation.Trust == PlanTrust.VerifiedHelper && validation.HelperName is not null ? validation.HelperName : Words.Clip(plan.Author.Name, 40);
        _session = new PlanSession(plan, validation, name);
        if (validation.Valid && validation.Trust != PlanTrust.BadSignature)
        {
            Shell.Plan = _session;
            await Shell.SendAsync(new PlanImported { EventId = Shell.NewId(), Utc = Shell.Now, PlanId = validation.PlanId }).ConfigureAwait(true);
        }

        Build();
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_session is not null)
        {
            Build();
        }

        base.Refresh();
    }

    /// <summary>Marks every URL in helper text as "link from helper" (shown, never opened).</summary>
    /// <param name="text">Text.</param>
    public static string MarkLinks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UrlPattern().Replace(text, m => m.Value + " " + Loc.Instance.Get("Plan.LinkFromHelper"));
    }

    private static FixPlan? Parse(string text)
    {
        try
        {
            if (text.Contains(SignedPlanEnvelope.CurrentFormat, StringComparison.Ordinal))
            {
                var envelope = VtdJson.DeserializeLenient<SignedPlanEnvelope>(text);
                return VtdJson.DeserializeLenient<FixPlan>(Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload)));
            }

            return VtdJson.DeserializeLenient<FixPlan>(text);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex UrlPattern();

    private async Task OpenAsync()
    {
        var file = S.Io.Open(L.Get("Plan.Filter"));
        if (file is null)
        {
            return;
        }

        await LoadAsync(Encoding.UTF8.GetString(file.Value.Content)).ConfigureAwait(true);
    }

    private void Build()
    {
        var (plan, v, name) = _session!;
        TrustText = v.Trust switch
        {
            PlanTrust.VerifiedHelper => L.Format("Plan.Trust.Verified", ("name", name)),
            PlanTrust.SignedUnknownKey => L.Format("Plan.Trust.SignedUnknown", ("name", name)),
            PlanTrust.BadSignature => L.Get("Plan.Trust.Bad"),
            _ => L.Format("Plan.Trust.Unsigned", ("name", name)),
        };
        Message = plan.Message is null ? null : MarkLinks(Words.Clip(plan.Message, PlanLimits.MaxMessageLength));
        Issues = [.. v.Issues.Select(i => Loc.Has("PlanIssue." + i.Code) ? L.Get("PlanIssue." + i.Code) : L.Get("PlanIssue.other"))];
        var usable = v.Valid && v.Trust != PlanTrust.BadSignature;
        Steps = [.. plan.Steps.Take(PlanLimits.MaxSteps).Select((step, i) =>
        {
            var sv = v.Steps.FirstOrDefault(x => x.Index == i);
            var greyed = !usable || sv is null || sv.Status == PlanStepStatus.Greyed;
            var reason = sv?.ReasonKey is { } rk ? (Loc.Has("PlanReason." + rk) ? L.Get("PlanReason." + rk) : S.Text.Get(rk)) : greyed && usable ? L.Get("PlanReason.generic") : null;
            return new PlanStepRowViewModel(
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                StepTitle(step),
                step.Note is null ? null : MarkLinks(Words.Clip(step.Note, PlanLimits.MaxNoteLength)),
                greyed,
                reason,
                new AsyncCommand(() => StartAsync(step, sv), () => !greyed));
        })];
        Status = usable ? L.Get("Plan.Ready") : L.Get("Plan.NotUsable");
        Raise(string.Empty);
    }

    private string StepTitle(PlanStep step)
    {
        string Unknown(string id) => L.Format("Plan.Step.Unknown", ("id", Words.Clip(id, 60)));
        return step switch
        {
            PlanFixStep f => S.Kb.FindFix(f.FixId) is { } fd ? S.Text.Get(fd.TitleKey) : Unknown(f.FixId),
            PlanGuideStep g => S.Kb.FindGuide(g.GuideId) is { } gd ? S.Text.Get(gd.TitleKey) : Unknown(g.GuideId),
            PlanQuestionStep q => S.Kb.FindQuestion(q.QuestionId) is { } qd ? S.Text.Get(qd.TextKey) : Unknown(q.QuestionId),
            PlanMonitorStep m => L.Format("Plan.Step.Monitor", ("minutes", m.Minutes)),
            _ => Unknown(step.GetType().Name),
        };
    }

    private async Task StartAsync(PlanStep step, PlanStepValidation? validation)
    {
        switch (step)
        {
            case PlanFixStep f:
                Shell.SelectedFixId = f.FixId;
                Shell.SelectedBinding = validation?.ResolvedBinding ?? FixBinding.Empty;
                Shell.PlanHelperName = _session?.HelperName;
                Shell.PlanHelperVerified = _session?.Validation.Trust == PlanTrust.VerifiedHelper;
                Shell.CloseSide();
                await Shell.SendAsync(new FixSelected { EventId = Shell.NewId(), Utc = Shell.Now, FixId = f.FixId, Binding = Shell.SelectedBinding, PlanId = _session?.Validation.PlanId }).ConfigureAwait(true);
                break;
            case PlanGuideStep g:
                Shell.CloseSide();
                await Shell.SendAsync(new GuideStarted { EventId = Shell.NewId(), Utc = Shell.Now, GuideId = g.GuideId, BranchId = g.BranchId }).ConfigureAwait(true);
                break;
            case PlanMonitorStep m:
                Shell.CloseSide();
                await Shell.SendAsync(new MonitorRequested { EventId = Shell.NewId(), Utc = Shell.Now, Minutes = m.Minutes }).ConfigureAwait(true);
                break;
            case PlanQuestionStep q when S.Kb.FindQuestion(q.QuestionId) is { } def:
                Shell.OpenSide(new PlanQuestionViewModel(Shell, def, this));
                break;
            default:
                break;
        }
    }
}

/// <summary>Answers a plan's question.</summary>
public sealed class PlanQuestionViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    /// <param name="def">Question.</param>
    /// <param name="back">Plan page to return to.</param>
    public PlanQuestionViewModel(ShellViewModel shell, Contracts.Knowledge.QuestionDef def, PlanImportViewModel back)
        : base(shell)
    {
        ArgumentNullException.ThrowIfNull(def);
        Question = new IntakeQuestionViewModel(def, S.Text);
        AnswerCommand = new AsyncCommand(async p =>
        {
            if (p is string option)
            {
                await Shell.SendAsync(new QuestionAnswered { EventId = Shell.NewId(), Utc = Shell.Now, QuestionId = def.Id, OptionId = option }).ConfigureAwait(true);
                Shell.OpenSide(back);
            }
        });
    }

    /// <summary>Question.</summary>
    public IntakeQuestionViewModel Question { get; }

    /// <summary>Answer (parameter: option id).</summary>
    public AsyncCommand AnswerCommand { get; }
}
