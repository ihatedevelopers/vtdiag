using System.Globalization;
using VTD.App.Mvvm;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Sessions;

namespace VTD.App.ViewModels;

/// <summary>A health checklist group.</summary>
/// <param name="Name">Group name.</param>
/// <param name="Items">Items.</param>
public sealed record HealthGroupViewModel(string Name, IReadOnlyList<LineViewModel> Items);

/// <summary>Screen 4: the health baseline. Unknown items say "couldn't check" and why; they are never shown as OK.</summary>
public sealed class HealthViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public HealthViewModel(ShellViewModel shell)
        : base(shell)
    {
        NextCommand = new RelayCommand(() => Shell.ShowFlow(new SuspectsViewModel(Shell)));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Results;

    /// <summary>Groups.</summary>
    public IReadOnlyList<HealthGroupViewModel> Groups { get; private set; } = [];

    /// <summary>"Good news: 14 things look healthy."</summary>
    public string Headline { get; private set; } = string.Empty;

    /// <summary>"3 things need a look · 2 we couldn't check".</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Limited-mode note.</summary>
    public bool ShowLimitedNote => Shell.IsLimited;

    /// <summary>See what we found.</summary>
    public RelayCommand NextCommand { get; }

    /// <inheritdoc />
    public override void Refresh()
    {
        var items = Shell.Analysis?.Health.Items ?? [];
        var ok = items.Count(i => i.Status == HealthStatus.Ok);
        var attention = items.Count(i => i.Status is HealthStatus.Warn or HealthStatus.Fail);
        var unknown = items.Count(i => i.Status == HealthStatus.Unknown);
        Headline = ok > 0 ? L.Format("Health.Headline", ("count", ok)) : L.Get("Health.HeadlineNone");
        var parts = new List<string>();
        if (attention > 0)
        {
            parts.Add(L.Format("Health.NeedLook", ("count", attention)));
        }

        if (unknown > 0)
        {
            parts.Add(L.Format("Health.CouldNotCheck", ("count", unknown)));
        }

        Summary = string.Join(L.Get("Common.ListSeparator"), parts);
        Groups = [.. items
            .Where(i => i.Status != HealthStatus.NotApplicable)
            .GroupBy(i => i.Group)
            .OrderBy(g => g.Key)
            .Select(g => new HealthGroupViewModel(L.Get("HealthGroup." + g.Key), [.. g
                .OrderBy(i => i.Status switch { HealthStatus.Fail => 0, HealthStatus.Warn => 1, HealthStatus.Unknown => 2, _ => 3 })
                .Select(i => new LineViewModel(S.Text.Get(i.TitleKey, i.Args), i.Status, Detail(i)))]))];
        base.Refresh();
    }

    private string? Detail(HealthItem item) => item.Status switch
    {
        HealthStatus.Unknown => L.Format("Health.UnknownDetail", ("reason", Words.Unknown(item.UnknownReason))),
        HealthStatus.Ok => null,
        _ => item.DetailKey is null ? Words.Health(item.Status) : S.Text.Get(item.DetailKey, item.Args),
    };
}

/// <summary>A ranked suspect card.</summary>
public sealed class SuspectViewModel : ObservableObject
{
    /// <summary>Creates the card.</summary>
    /// <param name="page">Page.</param>
    /// <param name="h">Hypothesis.</param>
    /// <param name="proposal">Best fix proposal for it.</param>
    /// <param name="guide">Guide for it.</param>
    public SuspectViewModel(SuspectsViewModel page, RankedHypothesis h, FixProposal? proposal, GuideProposal? guide)
    {
        ArgumentNullException.ThrowIfNull(page);
        Hypothesis = h ?? throw new ArgumentNullException(nameof(h));
        Proposal = proposal;
        Guide = guide;
        var s = page.S;
        var l = Localization.Loc.Instance;
        Title = s.Text.Get(h.TitleKey);
        BandText = Words.Band(h.Band);
        BucketText = Words.Bucket(h.Bucket);
        Why = [.. h.Contributions
            .Where(c => c.Counted && c.DeltaLogOdds > 0)
            .OrderByDescending(c => c.DeltaLogOdds)
            .Select(c => WhyText(page, c))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .Take(3)];
        Math = [.. h.Contributions.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.SignalId} = {c.State}  Δ {c.DeltaLogOdds:+0.00;-0.00;0}{(c.Counted ? string.Empty : "  (not counted)")}"))];
        MathHeader = string.Create(CultureInfo.InvariantCulture, $"posterior {h.Posterior:0.00} · prior log-odds {h.PriorLogOdds:0.00} · log-odds {h.LogOdds:0.00}");
        if (proposal is not null)
        {
            FixTitle = s.Text.Get(s.Kb.FindFix(proposal.FixId)?.TitleKey ?? proposal.FixId);
            ManualSteps = [.. (s.Kb.FindFix(proposal.FixId)?.ManualStepKeys ?? []).Select((k, i) => string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {s.Text.Get(k)}"))];
            Badges = [Words.Time(s.Kb.FindFix(proposal.FixId)?.EstMinutes ?? 0.2), Words.Reversible(proposal.Reversibility), Words.Reboot(proposal.Reboot), Words.Risk(proposal.Risk)];
            if (page.Shell.IsLimited && proposal.NeedsAdmin)
            {
                UnavailableText = l.Get("Suspects.NeedsPermission");
            }
            else if (!proposal.Available)
            {
                UnavailableText = proposal.UnavailableReasonKey is null ? l.Get("Suspects.NotAvailable") : s.Text.Get(proposal.UnavailableReasonKey);
            }
        }

        if (guide is not null)
        {
            GuideTitle = s.Text.Get(s.Kb.FindGuide(guide.GuideId)?.TitleKey ?? guide.GuideId);
        }

        TryFixCommand = new AsyncCommand(() => page.OpenFixAsync(proposal!), () => CanTryFix);
        ShowGuideCommand = new AsyncCommand(() => page.StartGuideAsync(guide!), () => guide is not null);
    }

    /// <summary>Hypothesis.</summary>
    public RankedHypothesis Hypothesis { get; }

    /// <summary>Proposal.</summary>
    public FixProposal? Proposal { get; }

    /// <summary>Guide.</summary>
    public GuideProposal? Guide { get; }

    /// <summary>Title ("Your unused wired network port is causing the stutters").</summary>
    public string Title { get; }

    /// <summary>"Very likely".</summary>
    public string BandText { get; }

    /// <summary>Band.</summary>
    public ConfidenceBand Band => Hypothesis.Band;

    /// <summary>"Fixable here".</summary>
    public string BucketText { get; }

    /// <summary>Bucket.</summary>
    public HonestyBucket Bucket => Hypothesis.Bucket;

    /// <summary>Up to three "why we think so" lines.</summary>
    public IReadOnlyList<string> Why { get; }

    /// <summary>Has why lines.</summary>
    public bool HasWhy => Why.Count > 0;

    /// <summary>Helper detail: the per-signal math.</summary>
    public IReadOnlyList<string> Math { get; }

    /// <summary>Helper detail header.</summary>
    public string MathHeader { get; }

    /// <summary>Fix title.</summary>
    public string? FixTitle { get; }

    /// <summary>Guide title.</summary>
    public string? GuideTitle { get; }

    /// <summary>Badges (time, undoable, restart, risk).</summary>
    public IReadOnlyList<BadgeViewModel> Badges { get; } = [];

    /// <summary>Why the fix can't run now.</summary>
    public string? UnavailableText { get; }

    /// <summary>Has a runnable fix.</summary>
    public bool CanTryFix => Proposal is not null && UnavailableText is null;

    /// <summary>The fix done by hand, numbered.</summary>
    public IReadOnlyList<string> ManualSteps { get; } = [];

    /// <summary>Has manual steps.</summary>
    public bool HasManualSteps => ManualSteps.Count > 0;

    /// <summary>Has a fix (runnable or not).</summary>
    public bool HasFix => Proposal is not null;

    /// <summary>Has a guide.</summary>
    public bool HasGuide => Guide is not null;

    /// <summary>Open the fix.</summary>
    public AsyncCommand TryFixCommand { get; }

    /// <summary>Open the guide.</summary>
    public AsyncCommand ShowGuideCommand { get; }

    private static string? WhyText(SuspectsViewModel page, EvidenceContribution c)
    {
        var s = page.S;
        var args = c.Evidence.FirstOrDefault()?.Args;
        if (c.TextKey is not null && s.Text.Has(c.TextKey))
        {
            return s.Text.Get(c.TextKey, args);
        }

        var ev = c.Evidence.FirstOrDefault(e => s.Text.Has(e.TextKey));
        if (ev is not null)
        {
            return s.Text.Get(ev.TextKey, ev.Args);
        }

        var signal = s.Kb.Signals.FirstOrDefault(x => string.Equals(x.Id, c.SignalId, StringComparison.Ordinal));
        return signal?.TitleKey is not null && s.Text.Has(signal.TitleKey) ? s.Text.Get(signal.TitleKey) : null;
    }
}

/// <summary>Screen 5: ranked suspects with honesty buckets, a route verdict, the unknown-cause card and the next question.</summary>
public sealed class SuspectsViewModel : PageViewModel
{
    private bool _showAll;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public SuspectsViewModel(ShellViewModel shell)
        : base(shell)
    {
        ToggleAllCommand = new RelayCommand(() =>
        {
            _showAll = !_showAll;
            Refresh();
        });
        HelpCommand = new RelayCommand(() => Shell.OpenSide(new ReportViewModel(Shell) { HelperFocus = true }));
        OpenLinkCommand = new RelayCommand(p => S.Io.OpenLink(p as string ?? string.Empty));
        AnswerCommand = new AsyncCommand(p => AnswerAsync(p as string));
        PrimaryCommand = new AsyncCommand(PrimaryAsync);
        BackToHealthCommand = new RelayCommand(() => Shell.ShowFlow(new HealthViewModel(Shell)));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Results;

    /// <summary>Suspects shown.</summary>
    public IReadOnlyList<SuspectViewModel> Suspects { get; private set; } = [];

    /// <summary>Long shots hidden behind "Show all".</summary>
    public int HiddenCount { get; private set; }

    /// <summary>Show the "show all" toggle.</summary>
    public bool CanToggleAll => HiddenCount > 0 || _showAll;

    /// <summary>"Show all (2 more)" / "Show fewer".</summary>
    public string ToggleAllText => _showAll ? L.Get("Suspects.ShowFewer") : L.Format("Suspects.ShowAll", ("count", HiddenCount));

    /// <summary>The bucket the results lead with, as a sentence.</summary>
    public string BucketLine { get; private set; } = string.Empty;

    /// <summary>Primary bucket.</summary>
    public HonestyBucket PrimaryBucket => Shell.Analysis?.PrimaryBucket ?? HonestyBucket.Unknown;

    /// <summary>Route verdict text (VAN 152 → appeal ...).</summary>
    public string? RouteText { get; private set; }

    /// <summary>Official links for the route.</summary>
    public IReadOnlyList<string> RouteLinks { get; private set; } = [];

    /// <summary>Has a route verdict.</summary>
    public bool HasRoute => RouteText is not null;

    /// <summary>"We couldn't pin this down".</summary>
    public bool UnknownLeading => Shell.Analysis?.UnknownCause.Leading ?? false;

    /// <summary>The next question.</summary>
    public string? QuestionText { get; private set; }

    /// <summary>Its options.</summary>
    public IReadOnlyList<ChipViewModel> QuestionOptions { get; private set; } = [];

    /// <summary>Has a question.</summary>
    public bool HasQuestion => QuestionText is not null;

    /// <summary>"We couldn't check: Secure Boot keys (needs permission)".</summary>
    public IReadOnlyList<string> CouldNotCheck { get; private set; } = [];

    /// <summary>Has could-not-check lines.</summary>
    public bool HasCouldNotCheck => CouldNotCheck.Count > 0;

    /// <summary>Primary button text.</summary>
    public string PrimaryText { get; private set; } = string.Empty;

    /// <summary>Primary button enabled.</summary>
    public bool HasPrimary { get; private set; }

    /// <summary>Shows/hides long shots.</summary>
    public RelayCommand ToggleAllCommand { get; }

    /// <summary>Get help from a helper.</summary>
    public RelayCommand HelpCommand { get; }

    /// <summary>Opens an official link (parameter: url).</summary>
    public RelayCommand OpenLinkCommand { get; }

    /// <summary>Answers the question (parameter: option id).</summary>
    public AsyncCommand AnswerCommand { get; }

    /// <summary>The one primary action.</summary>
    public AsyncCommand PrimaryCommand { get; }

    /// <summary>Back to the health checklist.</summary>
    public RelayCommand BackToHealthCommand { get; }

    /// <summary>Opens a fix (FixSelected → AwaitingConsent → consent screen).</summary>
    /// <param name="proposal">Proposal.</param>
    public async Task OpenFixAsync(FixProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Shell.SelectedFixId = proposal.FixId;
        Shell.SelectedBinding = proposal.Binding;
        Shell.PlanHelperName = null;
        Shell.PlanHelperVerified = false;
        await Shell.SendAsync(new FixSelected { EventId = Shell.NewId(), Utc = Shell.Now, FixId = proposal.FixId, Binding = proposal.Binding }).ConfigureAwait(true);
    }

    /// <summary>Starts a guide.</summary>
    /// <param name="guide">Guide.</param>
    public Task StartGuideAsync(GuideProposal guide)
    {
        ArgumentNullException.ThrowIfNull(guide);
        return Shell.SendAsync(new GuideStarted { EventId = Shell.NewId(), Utc = Shell.Now, GuideId = guide.GuideId, BranchId = guide.BranchId });
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        var a = Shell.Analysis;
        if (a is null)
        {
            base.Refresh();
            return;
        }

        var skipped = Shell.Session?.SkippedFixIds ?? [];
        var all = a.Hypotheses.Select(h => new SuspectViewModel(
            this,
            h,
            a.Proposals.Where(p => p.HypothesisIds.Contains(h.Id, StringComparer.Ordinal) && !skipped.Contains(p.FixId, StringComparer.Ordinal)).OrderByDescending(p => p.Utility).FirstOrDefault(),
            a.Guides.FirstOrDefault(g => g.HypothesisIds.Contains(h.Id, StringComparer.Ordinal)))).ToList();
        var visible = _showAll ? all : all.Where(x => x.Band >= ConfidenceBand.Possible).ToList();
        if (visible.Count == 0 && all.Count > 0 && !UnknownLeading)
        {
            visible = all.Take(1).ToList();
        }

        Suspects = visible;
        HiddenCount = all.Count - visible.Count;
        BucketLine = Words.BucketExplain(a.PrimaryBucket);
        RouteText = a.Route is null ? null : S.Text.Get(a.Route.TextKey);
        RouteLinks = a.Route?.Urls.Where(Services.LinkOpener.IsAllowed).ToList() ?? [];
        CouldNotCheck = [.. a.CouldNotCheck.Select(c => L.Format("Suspects.CouldNotCheckLine", ("what", SignalName(c.SignalId)), ("reason", Words.Unknown(c.Reason))))];
        if (a.NextQuestion is { } q && S.Kb.FindQuestion(q.QuestionId) is { } def && (Shell.Session?.QuestionsAsked ?? 0) < RankingConstants.MaxQuestions)
        {
            var vm = new IntakeQuestionViewModel(def, S.Text);
            QuestionText = vm.Text;
            QuestionOptions = vm.Options;
        }
        else
        {
            QuestionText = null;
            QuestionOptions = [];
        }

        var top = Suspects.FirstOrDefault();
        if (a.Route is not null)
        {
            HasPrimary = false;
            PrimaryText = string.Empty;
        }
        else if (UnknownLeading || top is null || top.Band == ConfidenceBand.LongShot)
        {
            // Nothing convincing: the honest next step is help, not a fix for a long shot.
            HasPrimary = true;
            PrimaryText = L.Get("Suspects.GetHelp");
        }
        else if (top.CanTryFix)
        {
            HasPrimary = true;
            PrimaryText = L.Get("Suspects.TryFirstFix");
        }
        else if (top?.HasGuide == true)
        {
            HasPrimary = true;
            PrimaryText = L.Get("Suspects.ShowSteps");
        }
        else
        {
            HasPrimary = true;
            PrimaryText = L.Get("Suspects.GetHelp");
        }

        base.Refresh();
    }

    private string SignalName(string signalId)
    {
        var signal = S.Kb.Signals.FirstOrDefault(x => string.Equals(x.Id, signalId, StringComparison.Ordinal));
        if (signal?.TitleKey is not null && S.Text.Has(signal.TitleKey))
        {
            return S.Text.Get(signal.TitleKey);
        }

        return Localization.Loc.Has("kb.signal." + signalId) ? L.Get("kb.signal." + signalId) : Localization.KbText.Humanize(signalId);
    }

    private async Task PrimaryAsync()
    {
        var top = Suspects.FirstOrDefault();
        if (UnknownLeading || top is null || top.Band == ConfidenceBand.LongShot)
        {
            HelpCommand.Execute(null);
        }
        else if (top.CanTryFix)
        {
            await OpenFixAsync(top.Proposal!).ConfigureAwait(true);
        }
        else if (top?.HasGuide == true)
        {
            await StartGuideAsync(top.Guide!).ConfigureAwait(true);
        }
        else
        {
            HelpCommand.Execute(null);
        }
    }

    private async Task AnswerAsync(string? optionId)
    {
        var q = Shell.Analysis?.NextQuestion;
        if (q is null || optionId is null)
        {
            return;
        }

        await Shell.SendAsync(new QuestionAnswered { EventId = Shell.NewId(), Utc = Shell.Now, QuestionId = q.QuestionId, OptionId = optionId }).ConfigureAwait(true);
    }
}
