using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;

namespace VTD.Analysis.Ranking;

/// <summary>A scored hypothesis before it becomes a <see cref="RankedHypothesis"/>.</summary>
internal sealed class Scored
{
    public required RuleDef Rule { get; init; }

    public double PriorLogOdds { get; set; }

    public double EvidenceLogOdds { get; set; }

    public double CodeLogOdds { get; set; }

    public double WaveLogOdds { get; set; }

    public double FailedFixLogOdds { get; set; }

    public bool CapApplied { get; set; }

    public List<EvidenceContribution> Contributions { get; } = [];

    public List<EntityRef> Entities { get; } = [];

    public RuledOut? Out { get; set; }

    public double LogOdds => PriorLogOdds + EvidenceLogOdds + CodeLogOdds + WaveLogOdds + FailedFixLogOdds;

    public double Posterior => Logistic(LogOdds);

    public static double Logistic(double x) => 1.0 / (1.0 + Math.Exp(-x));

    public static double Logit(double p) => Math.Log(p / (1.0 - p));
}

/// <summary>Flat smoothed priors (critique row 5): a rule's share of its category times the chip relevance.</summary>
internal sealed class PriorModel
{
    private readonly Dictionary<ProblemCategory, (int Threads, int Rules)> _categories = [];
    private readonly HashSet<string> _chosen;
    private readonly HashSet<ProblemCategory> _chosenCategories;

    public PriorModel(IKnowledgeBase kb, IntakeAnswers intake)
    {
        foreach (var r in kb.Rules.Where(r => r.Kind == RuleKind.Hypothesis && r.Deprecated is null))
        {
            var (t, n) = _categories.GetValueOrDefault(r.Category);
            _categories[r.Category] = (t + Math.Max(0, r.Prior.Threads), n + 1);
        }

        _chosen = intake.Symptoms.ToHashSet(StringComparer.Ordinal);
        _chosenCategories = kb.Symptoms.Where(s => _chosen.Contains(s.Id)).SelectMany(s => s.Categories).ToHashSet();
    }

    public double Prior(RuleDef rule)
    {
        if (rule.Prior.Override is { } fixedPrior)
        {
            return Math.Clamp(fixedPrior, AnalysisTuning.MinPrior, AnalysisTuning.MaxPrior);
        }

        var (threads, rules) = _categories.GetValueOrDefault(rule.Category);
        double share;
        if (rule.Kind == RuleKind.Generated)
        {
            share = 1.0 / Math.Max(threads + rules, AnalysisTuning.GeneratedDenominatorFloor);
        }
        else
        {
            share = (Math.Max(0, rule.Prior.Threads) + 1.0) / (threads + rules + AnalysisTuning.CategorySmoothing);
        }

        return Math.Clamp(share * Relevance(rule), AnalysisTuning.MinPrior, AnalysisTuning.MaxPrior);
    }

    private double Relevance(RuleDef rule)
    {
        if (_chosen.Count == 0)
        {
            return AnalysisTuning.NoSymptoms;
        }

        if (rule.Symptoms.Any(_chosen.Contains))
        {
            return AnalysisTuning.SymptomMatch;
        }

        return _chosenCategories.Contains(rule.Category) ? AnalysisTuning.CategoryMatch : AnalysisTuning.NoMatch;
    }
}

/// <summary>Scores one rule: gates, vetoes, weighted evidence with correlation groups and the cap, codes, waves, history.</summary>
internal static class HypothesisScorer
{
    public static Scored Score(RuleDef rule, double prior, IReadOnlyDictionary<string, SignalResult> signals, IReadOnlyList<CodeHit> codes,
        AnalysisInput input, IKnowledgeBase kb)
    {
        var s = new Scored { Rule = rule, PriorLogOdds = Scored.Logit(prior) };
        foreach (var req in rule.Requires)
        {
            if (State(signals, req).State == Tri.False)
            {
                s.Out = new RuledOut { HypothesisId = rule.Id, Reason = RuledOutReason.RequiresFalse, By = req };
                return s;
            }
        }

        foreach (var ex in rule.Excludes)
        {
            if (State(signals, ex).State == Tri.True)
            {
                s.Out = new RuledOut { HypothesisId = rule.Id, Reason = RuledOutReason.Excluded, By = ex };
                return s;
            }
        }

        var chosen = input.Intake.Symptoms.ToHashSet(StringComparer.Ordinal);
        var raw = new List<(EvidenceDef Def, SignalResult Signal, double Delta, bool Skipped)>();
        foreach (var e in rule.Evidence)
        {
            var sig = State(signals, e.Signal);
            if (e.Veto is { } veto && ((veto == VetoWhen.True && sig.State == Tri.True) || (veto == VetoWhen.False && sig.State == Tri.False)))
            {
                s.Out = new RuledOut { HypothesisId = rule.Id, Reason = RuledOutReason.Vetoed, By = e.Signal };
                return s;
            }

            var skipped = e.OnlyIfSymptoms.Count > 0 && !e.OnlyIfSymptoms.Any(chosen.Contains);
            var delta = skipped ? 0.0 : sig.State switch
            {
                Tri.True => (e.WhenTrue ?? 0.0) * Math.Clamp(sig.Strength, 0.0, 1.0),
                Tri.False => e.WhenFalse ?? 0.0,
                _ => 0.0,
            };
            raw.Add((e, sig, delta, skipped));
        }

        // Correlated groups: only the item with the largest |delta| counts.
        var winners = raw.Where(r => r.Def.Group is not null && r.Delta != 0)
            .GroupBy(r => r.Def.Group!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => Math.Abs(x.Delta)).ThenBy(x => x.Def.Id, StringComparer.Ordinal).First().Def.Id, StringComparer.Ordinal);
        double sum = 0, abs = 0;
        foreach (var (def, sig, delta, skipped) in raw)
        {
            var counted = !skipped && (def.Group is null || delta == 0 || string.Equals(winners.GetValueOrDefault(def.Group), def.Id, StringComparison.Ordinal));
            var applied = counted ? delta : 0.0;
            sum += applied;
            abs += Math.Abs(applied);
            s.Contributions.Add(new EvidenceContribution
            {
                EvidenceId = def.Id,
                SignalId = def.Signal,
                State = sig.State,
                Strength = sig.Strength,
                DeltaLogOdds = Math.Round(applied, 4),
                Group = def.Group,
                Counted = counted && !skipped,
                TextKey = def.TextKey,
                Evidence = sig.Evidence,
            });
            if (counted && applied > 0)
            {
                s.Entities.AddRange(sig.Entities);
            }
        }

        if (abs > RankingConstants.MaxEvidenceLogOdds)
        {
            sum *= RankingConstants.MaxEvidenceLogOdds / abs;
            s.CapApplied = true;
        }

        s.EvidenceLogOdds = sum;
        s.CodeLogOdds = CodeLogOdds(rule, codes);
        s.WaveLogOdds = rule.Wave is { } w && InWave(w, input.AsOfUtc) ? w.Boost : 0.0;
        s.FailedFixLogOdds = FailedFixes(rule, input.History, kb);
        var distinct = s.Entities.DistinctBy(e => (e.Kind, e.Id)).OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        s.Entities.Clear();
        s.Entities.AddRange(distinct);
        return s;
    }

    public static SignalResult State(IReadOnlyDictionary<string, SignalResult> signals, string id)
        => signals.TryGetValue(id, out var r) ? r : Sig.Unknown(id, UnknownReason.NotCollected);

    public static bool InWave(WaveDef wave, DateTimeOffset asOf)
    {
        var day = DateOnly.FromDateTime(asOf.UtcDateTime);
        return day >= wave.From && day <= wave.To;
    }

    private static double CodeLogOdds(RuleDef rule, IReadOnlyList<CodeHit> codes)
    {
        double total = 0;
        foreach (var hit in codes)
        {
            if (hit.Row is null)
            {
                continue;
            }

            foreach (var cause in hit.Row.PointsTo.Where(p => string.Equals(p.Hypothesis, rule.Id, StringComparison.Ordinal)))
            {
                total += hit.Use.Origin == CodeOrigin.Logged && !hit.Stale ? cause.Logged : Math.Min(cause.Typed, cause.Logged);
            }
        }

        return Math.Clamp(total, -AnalysisTuning.MaxCodeLogOdds, AnalysisTuning.MaxCodeLogOdds);
    }

    private static double FailedFixes(RuleDef rule, IReadOnlyList<VTD.Contracts.Sessions.FixAttempt> history, IKnowledgeBase kb)
    {
        double total = 0;
        foreach (var a in history.Where(a => a.Outcome == FixOutcome.NotFixed && a.Status is AttemptStatus.Verified or AttemptStatus.Applied))
        {
            var fix = kb.FindFix(a.FixId);
            var aimed = a.HypothesisIds.Count > 0 ? a.HypothesisIds.Contains(rule.Id, StringComparer.Ordinal) : fix?.AppliesTo.Contains(rule.Id, StringComparer.Ordinal) == true;
            if (aimed)
            {
                total -= AnalysisTuning.FailedFixPenalty * SuccessProbability(fix);
            }
        }

        return Math.Max(total, -2 * AnalysisTuning.FailedFixPenalty);
    }

    /// <summary>P(fix works | cause) from the fix's Beta seed (default 0.5 when unknown).</summary>
    public static double SuccessProbability(FixDef? fix)
        => fix?.SuccessPrior is { } sp && sp.Trials > 0 ? (sp.Success + 1.0) / (sp.Trials + 2.0) : 0.5;
}
