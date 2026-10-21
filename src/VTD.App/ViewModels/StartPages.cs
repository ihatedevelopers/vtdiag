using System.Collections.ObjectModel;
using VTD.App.Localization;
using VTD.App.Mvvm;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.App.ViewModels;

/// <summary>Screen 0: welcome, three promises, language switch.</summary>
public sealed class WelcomeViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public WelcomeViewModel(ShellViewModel shell)
        : base(shell)
    {
        StartCommand = new RelayCommand(() => Shell.ShowFlow(new IntakeViewModel(Shell)));
        PrivacyCommand = new RelayCommand(() => Shell.OpenSide(new AboutViewModel(Shell)));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Describe;

    /// <summary>Start.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Privacy details.</summary>
    public RelayCommand PrivacyCommand { get; }
}

/// <summary>A selectable chip.</summary>
/// <param name="id">Id.</param>
/// <param name="text">Text.</param>
public sealed class ChipViewModel(string id, string text) : ObservableObject
{
    private bool _selected;
    private string _text = text;

    /// <summary>Id.</summary>
    public string Id { get; } = id;

    /// <summary>Text.</summary>
    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>Selected.</summary>
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>An intake question with its options.</summary>
public sealed class IntakeQuestionViewModel : ObservableObject
{
    private string? _answer;

    /// <summary>Creates it.</summary>
    /// <param name="def">Question.</param>
    /// <param name="text">KB text.</param>
    public IntakeQuestionViewModel(QuestionDef def, KbText text)
    {
        Def = def ?? throw new ArgumentNullException(nameof(def));
        ArgumentNullException.ThrowIfNull(text);
        Text = text.Get(def.TextKey);
        var options = def.Options.Count > 0 ? def.Options : [new QuestionOption { Id = "yes", TextKey = "ask.yes", Sets = Tri.True }, new QuestionOption { Id = "no", TextKey = "ask.no", Sets = Tri.False }, new QuestionOption { Id = "unsure", TextKey = "ask.unsure", Sets = Tri.Unknown }];
        Options = [.. options.Select(o => new ChipViewModel(o.Id, text.Has(o.TextKey) ? text.Get(o.TextKey) : Loc.Instance.Get("Answer." + o.Id)))];
        PickCommand = new RelayCommand(p =>
        {
            Answer = p as string;
            foreach (var o in Options)
            {
                // Re-raise even when unchanged: a click toggles the chip's visual state on its own.
                o.IsSelected = !string.Equals(o.Id, Answer, StringComparison.Ordinal);
                o.IsSelected = string.Equals(o.Id, Answer, StringComparison.Ordinal);
            }
        });
    }

    /// <summary>Question.</summary>
    public QuestionDef Def { get; }

    /// <summary>Text.</summary>
    public string Text { get; }

    /// <summary>Options.</summary>
    public IReadOnlyList<ChipViewModel> Options { get; }

    /// <summary>Chosen option id.</summary>
    public string? Answer { get => _answer; private set => Set(ref _answer, value); }

    /// <summary>Picks an option (parameter: option id).</summary>
    public RelayCommand PickCommand { get; }
}

/// <summary>Screen 1: what's wrong? Symptom chips, error code with suggestions, "when?" chips.</summary>
public sealed class IntakeViewModel : PageViewModel
{
    private static readonly string[] DefaultSymptoms = ["wont_start", "error_code", "stutter", "lag_ping", "crash_freeze", "voice_sound", "just_check"];
    private static readonly string[] WhenIds = ["at_launch", "in_menu", "in_match", "always"];
    private string _code = string.Empty;

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public IntakeViewModel(ShellViewModel shell)
        : base(shell)
    {
        var kbSymptoms = S.Kb.Symptoms.OrderBy(s => s.Order).ToList();
        var ids = kbSymptoms.Select(s => s.Id).ToList();
        if (ids.Count < 6)
        {
            ids.AddRange(DefaultSymptoms.Where(d => !ids.Contains(d, StringComparer.Ordinal)));
        }

        Symptoms = [.. ids.Select(id => new ChipViewModel(id, SymptomText(id)))];
        foreach (var chip in Symptoms)
        {
            chip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChipViewModel.IsSelected))
                {
                    Raise(nameof(CanContinue));
                    NextCommand?.Refresh();
                }
            };
        }

        When = [.. WhenIds.Select(id => new ChipViewModel(id, L.Get("When." + id)))];
        Questions = [.. S.Kb.Questions.Where(q => q.Intake).Take(RankingConstants.MaxQuestions).Select(q => new IntakeQuestionViewModel(q, S.Text))];
        ToggleCommand = new RelayCommand(p =>
        {
            if (p is ChipViewModel chip)
            {
                chip.IsSelected = !chip.IsSelected;
                Raise(nameof(ShowCodeBox));
                Raise(nameof(CanContinue));
                NextCommand!.Refresh();
            }
        });
        PickSuggestionCommand = new RelayCommand(p => Code = p as string ?? Code);
        NextCommand = new RelayCommand(Next, () => CanContinue);
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Describe;

    /// <summary>Symptom chips.</summary>
    public IReadOnlyList<ChipViewModel> Symptoms { get; }

    /// <summary>"When?" chips.</summary>
    public IReadOnlyList<ChipViewModel> When { get; }

    /// <summary>Intake questions ("is League also broken?").</summary>
    public IReadOnlyList<IntakeQuestionViewModel> Questions { get; }

    /// <summary>Has intake questions.</summary>
    public bool HasQuestions => Questions.Count > 0;

    /// <summary>Show the error-code box (the error-code chip is on, or a code was typed).</summary>
    public bool ShowCodeBox => Symptoms.Any(s => s.IsSelected && string.Equals(s.Id, "error_code", StringComparison.Ordinal)) || Code.Length > 0;

    /// <summary>Typed code.</summary>
    public string Code
    {
        get => _code;
        set
        {
            if (Set(ref _code, value ?? string.Empty))
            {
                UpdateSuggestions();
                Raise(nameof(CodeHint));
                Raise(nameof(CanContinue));
                NextCommand.Refresh();
            }
        }
    }

    /// <summary>Autocomplete suggestions ("VAN 9003 – Secure Boot is off").</summary>
    public ObservableCollection<ChipViewModel> Suggestions { get; } = [];

    /// <summary>What we know about the typed code.</summary>
    public string? CodeHint
    {
        get
        {
            if (Code.Trim().Length < 3)
            {
                return null;
            }

            var match = S.Kb.FindCode(Code);
            if (match is null && Suggestions.Count > 0)
            {
                return null; // still typing: the suggestions say more than "unknown" would
            }

            return match is null ? L.Get("Intake.CodeUnknown") : L.Format("Intake.CodeKnown", ("code", match.Normalized), ("meaning", S.Text.Get(match.Def.MeaningKey)));
        }
    }

    /// <summary>At least one chip or a code.</summary>
    public bool CanContinue => Symptoms.Any(s => s.IsSelected) || Code.Trim().Length > 0;

    /// <summary>Toggles a chip (parameter: the chip).</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>Takes a suggestion (parameter: code).</summary>
    public RelayCommand PickSuggestionCommand { get; }

    /// <summary>Next.</summary>
    public RelayCommand NextCommand { get; }

    /// <inheritdoc />
    public override void Refresh()
    {
        foreach (var chip in Symptoms)
        {
            chip.Text = SymptomText(chip.Id);
        }

        foreach (var chip in When)
        {
            chip.Text = L.Get("When." + chip.Id);
        }

        UpdateSuggestions();
        base.Refresh();
    }

    private string SymptomText(string id)
    {
        var def = S.Kb.Symptoms.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return def is not null && S.Text.Has(def.TitleKey) ? S.Text.Get(def.TitleKey) : L.Get("Symptom." + id);
    }

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        var typed = Code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (typed.Length < 2)
        {
            return;
        }

        foreach (var def in S.Kb.ErrorCodes)
        {
            foreach (var code in def.Codes)
            {
                if (code.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(code, Code.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Suggestions.Add(new ChipViewModel(code, code + " · " + S.Text.Get(def.MeaningKey)));
                }
            }
        }

        while (Suggestions.Count > 5)
        {
            Suggestions.RemoveAt(Suggestions.Count - 1);
        }
    }

    private void Next()
    {
        var codes = Code.Trim().Length > 0 ? new[] { Code.Trim() } : [];
        Shell.Intake = new IntakeAnswers
        {
            Symptoms = [.. Symptoms.Where(s => s.IsSelected).Select(s => s.Id)],
            TypedCodes = codes,
            When = [.. When.Where(w => w.IsSelected).Select(w => w.Id)],
            Answers = Questions.Where(q => q.Answer is not null).ToDictionary(q => q.Def.Id, q => q.Answer!, StringComparer.Ordinal),
        };
        var permission = new PermissionViewModel(Shell);
        if (!Shell.IsDemo && !Services.EngineLauncher.IsInstalled)
        {
            // Portable copy: the elevated engine can't run here, so skip the UAC explainer and scan read-only.
            permission.LimitedCommand.Execute(null);
            return;
        }

        Shell.ShowFlow(permission);
    }
}

/// <summary>Screen 2: explain the UAC prompt; "scan without permission" runs limited mode.</summary>
public sealed class PermissionViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public PermissionViewModel(ShellViewModel shell)
        : base(shell)
    {
        ContinueCommand = new AsyncCommand(() => StartAsync(PrivilegeMode.Elevated));
        LimitedCommand = new AsyncCommand(() => StartAsync(PrivilegeMode.Limited));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Describe;

    /// <summary>Continue (the engine asks for permission).</summary>
    public AsyncCommand ContinueCommand { get; }

    /// <summary>Scan without permission (limited).</summary>
    public AsyncCommand LimitedCommand { get; }

    private async Task StartAsync(PrivilegeMode mode)
    {
        await Shell.ConnectAsync(mode).ConfigureAwait(true);
        if (await Shell.SendAsync(new IntakeSubmitted { EventId = Shell.NewId(), Utc = Shell.Now, Intake = Shell.Intake }).ConfigureAwait(true))
        {
            await Shell.SendAsync(new ScanRequested { EventId = Shell.NewId(), Utc = Shell.Now }).ConfigureAwait(true);
        }
    }
}

/// <summary>One friendly scan area with its state.</summary>
/// <param name="Area">Area.</param>
/// <param name="Text">Area name.</param>
/// <param name="Status">Ok = done, Warn = running, Unknown = waiting, Fail = skipped.</param>
/// <param name="Detail">"Skipping, taking too long" and similar.</param>
public sealed record ScanAreaViewModel(ScanArea Area, string Text, HealthStatus Status, string? Detail)
{
    /// <summary>Being read right now.</summary>
    public bool IsRunning => Status == HealthStatus.Warn;

    /// <summary>Not started yet.</summary>
    public bool IsWaiting => Status == HealthStatus.Unknown;

    /// <summary>Finished (ok or skipped).</summary>
    public bool IsFinished => !IsRunning && !IsWaiting;

    /// <summary>Has detail.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>Screen 3: scanning, area by area; a slow area says it was skipped and never hangs.</summary>
/// <param name="shell">Shell.</param>
public sealed class ScanViewModel : PageViewModel
{
    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public ScanViewModel(ShellViewModel shell)
        : base(shell)
    {
        CancelCommand = new AsyncCommand(() => Shell.SendAsync(new ScanCancelRequested { EventId = Shell.NewId(), Utc = Shell.Now }));
    }

    /// <inheritdoc />
    public override FlowStep Step => FlowStep.Scan;

    /// <summary>Stops the running scan (the engine discards the partial snapshot and goes back).</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>Areas.</summary>
    public IReadOnlyList<ScanAreaViewModel> Areas { get; private set; } = [];

    /// <summary>0..100.</summary>
    public double Percent { get; private set; }

    /// <summary>"7 of 15 checks done".</summary>
    public string ProgressText { get; private set; } = string.Empty;

    /// <inheritdoc />
    public override void Refresh()
    {
        var progress = Shell.ScanProgress;
        var last = progress.Count == 0 ? null : progress[^1];
        Percent = last is null || last.Total == 0 ? 2 : 100.0 * last.Completed / last.Total;
        ProgressText = last is null ? L.Get("Scan.Starting") : L.Format("Scan.Progress", ("done", last.Completed), ("total", last.Total));
        var currentArea = last?.Area;
        Areas = [.. Enum.GetValues<ScanArea>().Select(area =>
        {
            var runs = progress.Where(p => p.Area == area).ToList();
            var text = L.Get("Area." + area);
            if (runs.Count == 0)
            {
                return new ScanAreaViewModel(area, text, area == currentArea ? HealthStatus.Warn : HealthStatus.Unknown, null);
            }

            var statuses = runs.Select(r => r.Status).ToList();
            string? detail = null;
            var status = HealthStatus.Ok;
            if (statuses.Contains(CollectorStatus.TimedOut))
            {
                detail = L.Get("Scan.Skipped.Slow");
                status = HealthStatus.Fail;
            }
            else if (statuses.Contains(CollectorStatus.SkippedNoAdmin))
            {
                detail = L.Get("Scan.Skipped.NoAdmin");
                status = HealthStatus.Fail;
            }
            else if (statuses.Contains(CollectorStatus.Failed))
            {
                detail = L.Get("Scan.Skipped.Failed");
                status = HealthStatus.Fail;
            }
            else if (statuses.Contains(CollectorStatus.Partial))
            {
                detail = L.Get("Scan.Partial");
            }

            return new ScanAreaViewModel(area, text, status, detail);
        })];
        base.Refresh();
    }
}
