using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;

namespace VTD.Analysis.Ranking;

/// <summary>Route verdicts (short-circuit): route rules whose required signals are all definitely True, and route codes.</summary>
internal static class RouteResolver
{
    private static readonly RouteKind[] Priority =
    [
        RouteKind.HwidBanAppeal, RouteKind.UnsupportedPlatform, RouteKind.HardwareCannotComply, RouteKind.RiotServerIncident,
        RouteKind.TryAnotherAccount, RouteKind.RiotSupportTicket,
    ];

    public static RouteVerdict? Resolve(IKnowledgeBase kb, IReadOnlyDictionary<string, SignalResult> signals, IReadOnlyList<CodeHit> codes)
    {
        var verdicts = new List<RouteVerdict>();
        foreach (var r in kb.Rules.Where(r => r.Kind == RuleKind.Route && r.Route is not null && r.Requires.Count > 0 && r.Deprecated is null))
        {
            if (r.Requires.All(s => HypothesisScorer.State(signals, s).State == Tri.True)
                && !r.Excludes.Any(s => HypothesisScorer.State(signals, s).State == Tri.True))
            {
                verdicts.Add(new RouteVerdict { Kind = r.Route!.Value, TriggeredBy = r.Id, TextKey = r.SummaryKey ?? r.TitleKey, Bucket = r.Bucket });
            }
        }

        foreach (var hit in codes.Where(h => h.Row?.Route is not null && !h.Stale))
        {
            var row = hit.Row!;
            if (row.AskIfTyped is { } q && HypothesisScorer.State(signals, q).State != Tri.True)
            {
                continue; // conditional route: only when the player confirmed it (for example "another account works")
            }

            verdicts.Add(new RouteVerdict
            {
                Kind = row.Route!.Value,
                TriggeredBy = row.Id,
                TextKey = $"route.{Camel(row.Route.Value)}.text",
                Urls = row.OfficialUrls,
                Bucket = HonestyBucket.RiotOrAccount,
            });
        }

        return verdicts.OrderBy(v => Array.IndexOf(Priority, v.Kind)).ThenBy(v => v.TriggeredBy, StringComparer.Ordinal).FirstOrDefault();
    }

    public static string Camel(RouteKind kind)
    {
        var s = kind.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}

/// <summary>
/// At most <see cref="RankingConstants.MaxQuestions"/> questions per session, only when the top two are within
/// <see cref="RankingConstants.QuestionMarginLogOdds"/> and an unanswered question separates them (design_B.md section 5).
/// A typed route code with <c>askIfTyped</c> asks its question first.
/// </summary>
internal static class QuestionPlanner
{
    public static QuestionPrompt? Next(IKnowledgeBase kb, IReadOnlyList<Scored> survivors, IReadOnlyDictionary<string, SignalResult> signals,
        IReadOnlyList<CodeHit> codes, AnalysisInput input)
    {
        if (input.QuestionsAsked >= RankingConstants.MaxQuestions)
        {
            return null;
        }

        var answered = input.Intake.Answers.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var hit in codes.Where(h => h.Row?.AskIfTyped is not null && h.Row.Route is not null && !h.Stale).OrderBy(h => h.Row!.Id, StringComparer.Ordinal))
        {
            var q = hit.Row!.AskIfTyped!;
            if (!answered.Contains(q) && kb.FindQuestion(q) is not null)
            {
                var causes = hit.Row.PointsTo.Select(p => p.Hypothesis).Take(2).ToList();
                return new QuestionPrompt { QuestionId = q, Separates = causes, Margin = 0 };
            }
        }

        var ranked = survivors.OrderByDescending(h => h.LogOdds).ThenBy(h => h.Rule.Id, StringComparer.Ordinal).Take(2).ToList();
        if (ranked.Count < 2)
        {
            return null;
        }

        var margin = ranked[0].LogOdds - ranked[1].LogOdds;
        if (margin > RankingConstants.QuestionMarginLogOdds)
        {
            return null;
        }

        var chosen = input.Intake.Symptoms.ToHashSet(StringComparer.Ordinal);
        QuestionPrompt? best = null;
        var bestSep = AnalysisTuning.MinQuestionSeparation;
        foreach (var q in kb.Questions.OrderBy(q => q.Id, StringComparer.Ordinal))
        {
            if (answered.Contains(q.Id) || HypothesisScorer.State(signals, q.Id).State is Tri.True or Tri.False)
            {
                continue;
            }

            if (q.Symptoms.Count > 0 && chosen.Count > 0 && !q.Symptoms.Any(chosen.Contains))
            {
                continue;
            }

            if (q.AskIf.Any(c => HypothesisScorer.State(signals, c.Signal).State != c.Is))
            {
                continue;
            }

            var sep = Math.Abs(Weight(ranked[0].Rule, q.Id, true) - Weight(ranked[1].Rule, q.Id, true))
                      + Math.Abs(Weight(ranked[0].Rule, q.Id, false) - Weight(ranked[1].Rule, q.Id, false));
            if (sep > bestSep)
            {
                bestSep = sep;
                best = new QuestionPrompt { QuestionId = q.Id, Separates = [ranked[0].Rule.Id, ranked[1].Rule.Id], Margin = Math.Round(margin, 4) };
            }
        }

        return best;
    }

    private static double Weight(RuleDef rule, string questionId, bool whenTrue)
        => rule.Evidence.Where(e => string.Equals(e.Signal, questionId, StringComparison.Ordinal) || e.Signal.StartsWith(questionId + ".", StringComparison.Ordinal))
            .Sum(e => (whenTrue ? e.WhenTrue : e.WhenFalse) ?? 0.0);
}
