using VTD.Analysis.Health;
using VTD.Analysis.Ranking;
using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;

namespace VTD.Analysis;

/// <summary>
/// The ranking engine (DESIGN.md section 2): independent multi-label posteriors from flat smoothed priors, weighted
/// three-valued evidence (Unknown contributes nothing; a gate fires only on a definite False), the error-code table
/// with per-cause strength (logged &gt; typed), route short-circuits, date-limited waves, failed fixes as negative
/// evidence, the fixed unknown-cause floor, at most three questions, honesty buckets and utility-ordered proposals.
/// Deterministic: the id and the time come from <see cref="AnalysisInput"/>, nothing reads the wall clock.
/// </summary>
public sealed class Analyzer : IAnalyzer
{
    private readonly IKnowledgeBase _kb;
    private readonly SignalEngine _engine;

    /// <summary>Creates an analyzer.</summary>
    /// <param name="kb">KB.</param>
    /// <param name="providers">Signal providers (default: <see cref="SignalEngine.DefaultProviders"/>).</param>
    public Analyzer(IKnowledgeBase kb, IReadOnlyList<ISignalProvider>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(kb);
        _kb = kb;
        _engine = new SignalEngine(providers ?? SignalEngine.DefaultProviders());
    }

    /// <summary>Computes all signals for an input (helpers' fact browser, tests).</summary>
    /// <param name="input">Input.</param>
    public IReadOnlyDictionary<string, SignalResult> EvaluateSignals(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _engine.Evaluate(new SignalContext
        {
            Snapshot = input.Snapshot,
            Monitor = input.Monitor,
            Intake = input.Intake,
            History = input.History,
            Kb = _kb,
            AsOfUtc = input.AsOfUtc,
        });
    }

    /// <inheritdoc />
    public AnalysisResult Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var signals = EvaluateSignals(input);
        var codes = CodeUses.Collect(input.Snapshot, input.Intake, _kb);
        var priors = new PriorModel(_kb, input.Intake);

        var scored = _kb.Rules
            .Where(r => r.Kind is RuleKind.Hypothesis or RuleKind.Generated && r.Deprecated is null)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => HypothesisScorer.Score(r, priors.Prior(r), signals, codes, input, _kb))
            .ToList();

        // A more specific rule supersedes the generated one when it survives.
        var alive = scored.Where(s => s.Out is null).ToDictionary(s => s.Rule.Id, StringComparer.Ordinal);
        foreach (var s in scored.Where(s => s.Out is null).ToList())
        {
            foreach (var sup in s.Rule.Supersedes)
            {
                if (alive.TryGetValue(sup, out var victim) && victim.Out is null)
                {
                    victim.Out = new RuledOut { HypothesisId = sup, Reason = RuledOutReason.Superseded, By = s.Rule.Id };
                }
            }
        }

        var survivors = scored.Where(s => s.Out is null).OrderByDescending(s => s.Posterior).ThenBy(s => s.Rule.Id, StringComparer.Ordinal).ToList();
        var route = RouteResolver.Resolve(_kb, signals, codes);
        var proposals = route is null ? ProposalBuilder.Build(survivors, signals, input, _kb) : [];
        var fixable = proposals.Where(p => p.Available).SelectMany(p => p.HypothesisIds).ToHashSet(StringComparer.Ordinal);

        var ranked = survivors
            .Where((s, i) => i == 0 || s.Posterior >= AnalysisTuning.ListMinPosterior)
            .Take(AnalysisTuning.MaxListed)
            .Select(s => Rank(s, fixable))
            .ToList();

        var unknownP = RankingConstants.UnknownCauseProbability(survivors.Select(s => s.Posterior));
        var top = survivors.Count > 0 ? survivors[0].Posterior : 0.0;
        var unknown = new UnknownCause
        {
            Floor = RankingConstants.UnknownCauseFloor,
            Probability = Math.Round(unknownP, 4),
            Leading = unknownP >= top || top < RankingConstants.PinDownThreshold,
        };

        var primary = route?.Bucket ?? (unknown.Leading || ranked.Count == 0 ? HonestyBucket.Unknown : ranked[0].Bucket);
        var buckets = ranked.Where(h => h.Posterior >= RankingConstants.Likely)
            .GroupBy(h => h.Bucket)
            .Select(g => new BucketSummary { Bucket = g.Key, HypothesisIds = [.. g.Select(h => h.Id)], MaxPosterior = g.Max(h => h.Posterior) })
            .OrderByDescending(b => b.MaxPosterior).ThenBy(b => b.Bucket)
            .ToList();

        return new AnalysisResult
        {
            AnalysisId = input.AnalysisId,
            SnapshotId = input.Snapshot.Meta.SnapshotId,
            KbVersion = _kb.Version.Version,
            CreatedUtc = input.AsOfUtc,
            Hypotheses = ranked,
            UnknownCause = unknown,
            Route = route,
            PrimaryBucket = primary,
            Buckets = buckets,
            Health = HealthChecklistBuilder.Build(signals),
            NextQuestion = route is null ? QuestionPlanner.Next(_kb, survivors, signals, codes, input) : null,
            QuestionsAsked = input.QuestionsAsked,
            Proposals = proposals,
            Guides = Guides(survivors, route, signals),
            RuledOut = [.. scored.Where(s => s.Out is not null && (s.Rule.Kind == RuleKind.Hypothesis || s.Out!.Reason == RuledOutReason.Superseded))
                .Select(s => s.Out!).OrderBy(r => r.HypothesisId, StringComparer.Ordinal)],
            CouldNotCheck = Findings.CouldNotCheck(survivors, signals, _kb),
            Anomalies = Findings.Anomalies(input.Snapshot, _kb, input.AsOfUtc),
            Codes = [.. codes.Select(c => c.Use)],
        };
    }

    private RankedHypothesis Rank(Scored s, HashSet<string> fixable)
    {
        var bucket = s.Rule.Bucket;
        var fixIds = s.Rule.Fixes.Where(f => _kb.FindFix(f) is not null).ToList();
        var guideIds = s.Rule.Guides.Concat(_kb.Guides.Where(g => g.AppliesTo.Contains(s.Rule.Id, StringComparer.Ordinal)).Select(g => g.Id))
            .Distinct(StringComparer.Ordinal).Where(g => _kb.FindGuide(g) is not null).ToList();
        if (bucket == HonestyBucket.FixableHere && !fixable.Contains(s.Rule.Id))
        {
            bucket = guideIds.Count > 0 ? HonestyBucket.GuidedManual : HonestyBucket.Unknown;
        }

        return new RankedHypothesis
        {
            Id = s.Rule.Id,
            Rev = s.Rule.Rev,
            Category = s.Rule.Category,
            TitleKey = s.Rule.TitleKey,
            Posterior = Math.Round(s.Posterior, 4),
            LogOdds = Math.Round(s.LogOdds, 4),
            PriorLogOdds = Math.Round(s.PriorLogOdds, 4),
            Band = RankingConstants.BandOf(s.Posterior),
            Bucket = bucket,
            Contributions = s.Contributions,
            CodeLogOdds = Math.Round(s.CodeLogOdds, 4),
            WaveLogOdds = Math.Round(s.WaveLogOdds, 4),
            FailedFixLogOdds = Math.Round(s.FailedFixLogOdds, 4),
            CapApplied = s.CapApplied,
            Entities = s.Entities,
            FixIds = fixIds,
            GuideIds = guideIds,
        };
    }

    private IReadOnlyList<GuideProposal> Guides(IReadOnlyList<Scored> survivors, RouteVerdict? route, IReadOnlyDictionary<string, SignalResult> signals)
    {
        var byGuide = new Dictionary<string, List<Scored>>(StringComparer.Ordinal);
        IEnumerable<Scored> relevant = route is null ? survivors.Where(s => s.Posterior >= RankingConstants.Possible) : [];
        foreach (var h in relevant)
        {
            var ids = h.Rule.Guides.Concat(_kb.Guides.Where(g => g.AppliesTo.Contains(h.Rule.Id, StringComparer.Ordinal)).Select(g => g.Id)).Distinct(StringComparer.Ordinal);
            foreach (var id in ids.Where(i => _kb.FindGuide(i) is not null))
            {
                if (!byGuide.TryGetValue(id, out var list))
                {
                    byGuide[id] = list = [];
                }

                list.Add(h);
            }
        }

        if (route is not null && _kb.FindRule(route.TriggeredBy) is { } routeRule)
        {
            foreach (var g in routeRule.Guides.Where(g => _kb.FindGuide(g) is not null))
            {
                byGuide.TryAdd(g, []);
            }
        }

        return [.. byGuide
            .OrderByDescending(kv => kv.Value.Count == 0 ? 1.0 : kv.Value.Max(h => h.Posterior)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new GuideProposal
            {
                GuideId = kv.Key,
                HypothesisIds = [.. kv.Value.Select(h => h.Rule.Id)],
                BranchId = Branch(_kb.FindGuide(kv.Key)!, signals),
            })];
    }

    /// <summary>The first branch whose conditions all hold definitely (conditional branches first, then the default).</summary>
    private static string? Branch(GuideDef guide, IReadOnlyDictionary<string, SignalResult> signals)
    {
        var conditional = guide.Branches.FirstOrDefault(b => b.When.Count > 0 && b.When.All(c => HypothesisScorer.State(signals, c.Signal).State == c.Is));
        return conditional?.Id ?? guide.Branches.FirstOrDefault(b => b.When.Count == 0)?.Id;
    }
}
