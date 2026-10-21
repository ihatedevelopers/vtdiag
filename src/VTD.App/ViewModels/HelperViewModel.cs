using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VTD.App.Mvvm;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Reporting;

namespace VTD.App.ViewModels;

/// <summary>A hypothesis with its math, for helpers.</summary>
/// <param name="Title">Title.</param>
/// <param name="Header">Posterior, band, bucket.</param>
/// <param name="Lines">Per-signal contributions.</param>
public sealed record HelperHypothesisViewModel(string Title, string Header, IReadOnlyList<string> Lines);

/// <summary>Something the helper can add to the plan.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Id">KB id.</param>
/// <param name="Title">Title.</param>
/// <param name="Detail">Risk, reversibility, binding.</param>
/// <param name="Bind">Share-level binding (fix steps).</param>
/// <param name="AddCommand">Adds it.</param>
public sealed record PlanCandidateViewModel(PlanStepKind Kind, string Id, string Title, string Detail, IReadOnlyDictionary<string, IReadOnlyList<string>> Bind, RelayCommand AddCommand);

/// <summary>A step in the plan being written.</summary>
public sealed class PlanDraftStepViewModel : ObservableObject
{
    private string _note = string.Empty;

    /// <summary>Creates it.</summary>
    /// <param name="candidate">Candidate.</param>
    /// <param name="minutes">Monitor minutes.</param>
    public PlanDraftStepViewModel(PlanCandidateViewModel candidate, int minutes)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Minutes = minutes;
    }

    /// <summary>Candidate.</summary>
    public PlanCandidateViewModel Candidate { get; }

    /// <summary>Monitor minutes.</summary>
    public int Minutes { get; }

    /// <summary>Title.</summary>
    public string Title => Candidate.Title;

    /// <summary>Note to the player (plain text, ≤ 500 chars).</summary>
    public string Note { get => _note; set => Set(ref _note, (value ?? string.Empty).Length > PlanLimits.MaxNoteLength ? value![..PlanLimits.MaxNoteLength] : value ?? string.Empty); }

    /// <summary>Command set by the page.</summary>
    public RelayCommand? UpCommand { get; set; }

    /// <summary>Command set by the page.</summary>
    public RelayCommand? DownCommand { get; set; }

    /// <summary>Command set by the page.</summary>
    public RelayCommand? RemoveCommand { get; set; }

    /// <summary>The plan step.</summary>
    public PlanStep ToStep()
    {
        var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        return Candidate.Kind switch
        {
            PlanStepKind.Fix => new PlanFixStep { FixId = Candidate.Id, Bind = Candidate.Bind, Note = note },
            PlanStepKind.Guide => new PlanGuideStep { GuideId = Candidate.Id, Note = note },
            PlanStepKind.Question => new PlanQuestionStep { QuestionId = Candidate.Id, Note = note },
            _ => new PlanMonitorStep { Minutes = Minutes, Note = note },
        };
    }
}

/// <summary>
/// Helper mode (design_B.md section 9): open a player's <c>.vtdscan</c> (always share-redacted), read the summary, the
/// ranking math and every fact, then write a <c>.vtdplan</c> from KB ids only, or "Copy for AI" so the helper's AI can
/// write one. Nothing on the helper's PC changes; no engine is needed.
/// </summary>
public sealed class HelperViewModel : PageViewModel
{
    private readonly Redactor _redactor = new(RandomNumberGenerator.GetBytes(32));
    private ScanBundle? _bundle;
    private string _search = string.Empty;
    private string _author = string.Empty;
    private string _message = string.Empty;
    private string? _status;
    private IReadOnlyList<string> _allFacts = [];

    /// <summary>Creates the page.</summary>
    /// <param name="shell">Shell.</param>
    public HelperViewModel(ShellViewModel shell)
        : base(shell)
    {
        OpenCommand = new AsyncCommand(OpenAsync);
        OpenSampleCommand = new AsyncCommand(OpenSampleAsync, () => Shell.IsDemo);
        SavePlanCommand = new RelayCommand(SavePlan, () => IsLoaded && Draft.Count > 0);
        CopyPlanCommand = new RelayCommand(CopyPlan, () => IsLoaded && Draft.Count > 0);
        CopyAiCommand = new RelayCommand(CopyAi, () => IsLoaded);
    }

    /// <summary>A scan is open.</summary>
    public bool IsLoaded => _bundle is not null;

    /// <summary>"Scan from 21 Oct 2026 · VTDiag 0.0.1 · KB 2026.10.1 · limited".</summary>
    public string BundleLine { get; private set; } = string.Empty;

    /// <summary>The Reddit-style summary of the scan.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Ranking with the math.</summary>
    public IReadOnlyList<HelperHypothesisViewModel> Hypotheses { get; private set; } = [];

    /// <summary>Unknown-cause line.</summary>
    public string UnknownLine { get; private set; } = string.Empty;

    /// <summary>Fact search.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? string.Empty))
            {
                Raise(nameof(Facts));
            }
        }
    }

    /// <summary>Facts (<c>path = value</c>), filtered.</summary>
    public IReadOnlyList<string> Facts => [.. _allFacts.Where(f => _search.Length == 0 || f.Contains(_search, StringComparison.OrdinalIgnoreCase)).Take(500)];

    /// <summary>What can go in the plan.</summary>
    public IReadOnlyList<PlanCandidateViewModel> Candidates { get; private set; } = [];

    /// <summary>The plan being written.</summary>
    public ObservableCollection<PlanDraftStepViewModel> Draft { get; } = [];

    /// <summary>"3 of 10 steps".</summary>
    public string DraftCount => L.Format("Helper.StepCount", ("count", Draft.Count), ("max", PlanLimits.MaxSteps));

    /// <summary>Helper name (plain text, unverified unless signed).</summary>
    public string Author { get => _author; set => Set(ref _author, value ?? string.Empty); }

    /// <summary>Message to the player (plain text).</summary>
    public string Message { get => _message; set => Set(ref _message, (value ?? string.Empty).Length > PlanLimits.MaxMessageLength ? value![..PlanLimits.MaxMessageLength] : value ?? string.Empty); }

    /// <summary>Status line.</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Open a .vtdscan.</summary>
    public AsyncCommand OpenCommand { get; }

    /// <summary>Demo: open a sample scan built from a fixture.</summary>
    public AsyncCommand OpenSampleCommand { get; }

    /// <summary>Save the plan (.vtdplan).</summary>
    public RelayCommand SavePlanCommand { get; }

    /// <summary>Copy the plan JSON.</summary>
    public RelayCommand CopyPlanCommand { get; }

    /// <summary>Copy for AI (digest + plan.schema.json).</summary>
    public RelayCommand CopyAiCommand { get; }

    /// <summary>Loads bundle bytes (also used by tests).</summary>
    /// <param name="bytes">.vtdscan bytes.</param>
    public void Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            _bundle = new ScanBundleCodec(_redactor).Read(stream);
            Draft.Clear();
            Status = null;
            Refresh();
        }
        catch (ScanBundleException ex)
        {
            Status = L.Format("Helper.BadBundle", ("code", ex.Code));
        }
        catch (InvalidDataException)
        {
            Status = L.Format("Helper.BadBundle", ("code", "format"));
        }
    }

    /// <summary>The plan as written.</summary>
    public FixPlan BuildPlan()
    {
        var b = _bundle ?? throw new InvalidOperationException("No scan open.");
        var now = S.Clock.GetUtcNow();
        return new FixPlan
        {
            Schema = PlanLimits.CurrentSchema,
            PlanId = Shell.NewId(),
            ForSnapshotId = b.Snapshot.Meta.SnapshotId,
            ForMachineKey = b.Snapshot.Meta.MachineKey,
            KbMin = b.Manifest.KbVersion,
            CreatedUtc = now,
            ExpiresUtc = now + PlanLimits.MaxLifetime,
            Author = new PlanAuthor { Name = string.IsNullOrWhiteSpace(Author) ? L.Get("Helper.AnonymousHelper") : Author.Trim() },
            Message = string.IsNullOrWhiteSpace(Message) ? null : Message.Trim(),
            Steps = [.. Draft.Select(d => d.ToStep())],
        };
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_bundle is not { } b)
        {
            base.Refresh();
            return;
        }

        var meta = b.Snapshot.Meta;
        BundleLine = L.Format(
            "Helper.BundleLine",
            ("date", Words.Date(meta.CreatedUtc)),
            ("app", b.Manifest.AppVersion),
            ("kb", b.Manifest.KbVersion),
            ("mode", L.Get("Helper.Mode." + meta.Privilege)));
        var model = Model(b);
        var built = new ReportBuilder(S.Kb, _redactor).Build(model, new ReportOptions { Formats = [ReportFormat.Markdown], Language = L.DataLanguage });
        Summary = built.Markdown ?? string.Empty;
        var a = b.Analysis;
        Hypotheses = a is null ? [] : [.. a.Hypotheses.Select(h => new HelperHypothesisViewModel(
            S.Text.Get(h.TitleKey) + "  (" + h.Id + ")",
            string.Create(CultureInfo.InvariantCulture, $"P = {h.Posterior:0.00} · {Words.Band(h.Band)} · {Words.Bucket(h.Bucket)} · prior {h.PriorLogOdds:0.00} → {h.LogOdds:0.00} (codes {h.CodeLogOdds:0.00}, wave {h.WaveLogOdds:0.00}, failed fixes {h.FailedFixLogOdds:0.00}{(h.CapApplied ? ", capped" : string.Empty)})"),
            [.. h.Contributions.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.SignalId} = {c.State} · Δ {c.DeltaLogOdds:+0.00;-0.00;0}{(c.Group is null ? string.Empty : " · group " + c.Group)}{(c.Counted ? string.Empty : " · not counted")}"))]))];
        UnknownLine = a is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"unknown cause {a.UnknownCause.Probability:0.00} (floor {a.UnknownCause.Floor:0.00}){(a.UnknownCause.Leading ? " · leading" : string.Empty)}");
        _allFacts = Flatten(JsonNode.Parse(VtdJson.Serialize(b.Snapshot)));
        Candidates = BuildCandidates(b);
        Raise(nameof(Facts));
        Raise(nameof(DraftCount));
        SavePlanCommand.Refresh();
        CopyPlanCommand.Refresh();
        CopyAiCommand.Refresh();
        base.Refresh();
    }

    private static ReportModel Model(ScanBundle b) => new()
    {
        SchemaVersion = ReportModel.CurrentSchemaVersion,
        Meta = new ReportMeta
        {
            ReportId = b.Manifest.BundleId,
            CreatedUtc = b.Manifest.CreatedUtc,
            AppVersion = b.Manifest.AppVersion,
            KbVersion = b.Manifest.KbVersion,
            Language = Languages.English,
            MachineKey = b.Snapshot.Meta.MachineKey,
        },
        Snapshot = b.Snapshot,
        Analysis = b.Analysis,
        Attempts = b.Journal,
        Monitor = b.Monitor.LastOrDefault(),
        Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = RedactionConstants.RulesVersion },
    };

    private static List<string> Flatten(JsonNode? node)
    {
        var lines = new List<string>();
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        Walk(v, path.Length == 0 ? k : path + "." + k);
                    }

                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        Walk(arr[i], string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]"));
                    }

                    break;
                case null:
                    lines.Add(path + " = null");
                    break;
                default:
                    lines.Add(path + " = " + n.ToJsonString());
                    break;
            }
        }

        Walk(node, string.Empty);
        return lines;
    }

    private List<PlanCandidateViewModel> BuildCandidates(ScanBundle b)
    {
        var list = new List<PlanCandidateViewModel>();
        var a = b.Analysis;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in a?.Proposals ?? [])
        {
            if (!seen.Add("fix:" + p.FixId))
            {
                continue;
            }

            var bind = p.Binding.Entities.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value.Select(e => e.Id)], StringComparer.Ordinal);
            list.Add(Candidate(PlanStepKind.Fix, p.FixId, S.Text.Get(S.Kb.FindFix(p.FixId)?.TitleKey ?? p.FixId), FixDetail(p.FixId, bind), bind));
        }

        var hypIds = a?.Hypotheses.Select(h => h.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var f in S.Kb.Fixes.Where(f => f.AppliesTo.Any(hypIds.Contains) && seen.Add("fix:" + f.Id)))
        {
            var bind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (name, def) in f.Bind)
            {
                var entities = a!.Hypotheses.Where(h => f.AppliesTo.Contains(h.Id, StringComparer.Ordinal)).SelectMany(h => h.Entities).Where(e => e.Kind == def.Kind).Select(e => e.Id).Distinct(StringComparer.Ordinal).ToList();
                if (entities.Count > 0)
                {
                    bind[name] = def.Pick == BindPick.One ? [entities[0]] : entities;
                }
            }

            list.Add(Candidate(PlanStepKind.Fix, f.Id, S.Text.Get(f.TitleKey), FixDetail(f.Id, bind), bind));
        }

        foreach (var g in (a?.Guides.Select(x => x.GuideId) ?? []).Concat(S.Kb.Guides.Where(g => g.AppliesTo.Any(hypIds.Contains)).Select(g => g.Id)))
        {
            if (seen.Add("guide:" + g) && S.Kb.FindGuide(g) is { } def)
            {
                list.Add(Candidate(PlanStepKind.Guide, g, S.Text.Get(def.TitleKey), L.Get("Helper.Kind.Guide"), Empty));
            }
        }

        foreach (var q in S.Kb.Questions)
        {
            list.Add(Candidate(PlanStepKind.Question, q.Id, S.Text.Get(q.TextKey), L.Get("Helper.Kind.Question"), Empty));
        }

        list.Add(Candidate(PlanStepKind.Monitor, "monitor", L.Format("Plan.Step.Monitor", ("minutes", 10)), L.Get("Helper.Kind.Monitor"), Empty));
        return list;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private string FixDetail(string fixId, IReadOnlyDictionary<string, IReadOnlyList<string>> bind)
    {
        var def = S.Kb.FindFix(fixId);
        var risk = def is null ? string.Empty : Words.Risk(def.Risk).Text + " · " + Words.Reversible(def.Reversibility).Text;
        var binding = bind.Count == 0 ? string.Empty : " · " + string.Join("; ", bind.Select(kv => kv.Key + " = " + string.Join(", ", kv.Value)));
        var high = def?.Risk == RiskLevel.High ? " · " + L.Get("Helper.HighRiskGuideOnly") : string.Empty;
        return fixId + " · " + risk + binding + high;
    }

    private PlanCandidateViewModel Candidate(PlanStepKind kind, string id, string title, string detail, IReadOnlyDictionary<string, IReadOnlyList<string>> bind)
    {
        PlanCandidateViewModel? c = null;
        c = new PlanCandidateViewModel(kind, id, title, detail, bind, new RelayCommand(() => Add(c!)));
        return c;
    }

    private void Add(PlanCandidateViewModel c)
    {
        if (Draft.Count >= PlanLimits.MaxSteps)
        {
            Status = L.Format("Helper.TooManySteps", ("max", PlanLimits.MaxSteps));
            return;
        }

        var step = new PlanDraftStepViewModel(c, 10);
        step.UpCommand = new RelayCommand(() => Move(step, -1));
        step.DownCommand = new RelayCommand(() => Move(step, 1));
        step.RemoveCommand = new RelayCommand(() =>
        {
            Draft.Remove(step);
            Changed();
        });
        Draft.Add(step);
        Changed();
    }

    private void Move(PlanDraftStepViewModel step, int delta)
    {
        var i = Draft.IndexOf(step);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Draft.Count)
        {
            return;
        }

        Draft.Move(i, j);
        Changed();
    }

    private void Changed()
    {
        Raise(nameof(DraftCount));
        SavePlanCommand.Refresh();
        CopyPlanCommand.Refresh();
    }

    private string PlanJson() => VtdJson.Serialize(BuildPlan(), indented: true);

    private void SavePlan()
    {
        var plan = BuildPlan();
        var name = "VTDiag-plan-" + plan.ForSnapshotId[^6..] + ".vtdplan";
        if (S.Io.Save(name, L.Get("Plan.Filter"), Encoding.UTF8.GetBytes(VtdJson.Serialize(plan, indented: true))))
        {
            Status = L.Format("Report.Saved", ("name", name));
        }
    }

    private void CopyPlan()
    {
        S.Io.SetClipboard(PlanJson());
        Status = L.Get("Helper.PlanCopied");
    }

    private void CopyAi()
    {
        var built = new ReportBuilder(S.Kb, _redactor).Build(Model(_bundle!), new ReportOptions { Formats = [ReportFormat.AiDigest] });
        if (built.Blocked || built.AiDigest is null)
        {
            Status = L.Get("Report.BlockedNoSection");
            return;
        }

        S.Io.SetClipboard(built.AiDigest);
        Status = L.Get("Report.Copied.Ai");
    }

    private async Task OpenAsync()
    {
        var file = S.Io.Open(L.Get("Report.Filter.Bundle"));
        if (file is not null)
        {
            Load(file.Value.Content);
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task OpenSampleAsync()
    {
        Load(await Services.DemoEngine.BuildSampleBundleAsync(Testing.Fakes.Fixtures.RealtekStutter).ConfigureAwait(true));
    }
}
