using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;

namespace VTD.Reporting;

/// <summary>A titled group of helper lines.</summary>
internal sealed record HelperGroup(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// The helper section: the ranking math (posteriors, log-odds, per-evidence deltas), proposals with their share-level
/// bindings, codes, ruled-out and unexplained findings. Rendered in <see cref="ReportOptions.HelperLanguage"/> (English
/// by default) so the subreddit can read a Turkish player's report; ids and signal names are machine English anyway.
/// </summary>
internal static class HelperView
{
    public static IReadOnlyList<HelperGroup> Build(ReportModel model, IKnowledgeBase kb, string helperLanguage)
    {
        var t = new ReportText(kb, helperLanguage);
        var analysis = model.Analysis;
        var groups = new List<HelperGroup>();
        if (analysis is null)
        {
            return groups;
        }

        var intro = new List<string>
        {
            t.Ui("helper.intro"),
            t.Ui(
                "helper.unknown_cause",
                ("p", Inv(analysis.UnknownCause.Probability)),
                ("floor", Inv(analysis.UnknownCause.Floor)),
                ("leading", analysis.UnknownCause.Leading ? t.Ui("helper.leading") : string.Empty)),
        };
        groups.Add(new HelperGroup(t.Ui("section.helper"), intro));

        foreach (var h in analysis.Hypotheses.OrderByDescending(h => h.Posterior).ThenBy(h => h.Id, StringComparer.Ordinal))
        {
            var lines = new List<string>
            {
                t.Ui("helper.hypothesis", ("id", h.Id), ("posterior", Inv(h.Posterior)), ("logodds", Inv(h.LogOdds)), ("prior", Inv(h.PriorLogOdds))),
            };
            if (h.CodeLogOdds != 0 || h.WaveLogOdds != 0 || h.FailedFixLogOdds != 0)
            {
                lines.Add(t.Ui("helper.extra", ("code", Inv(h.CodeLogOdds)), ("wave", Inv(h.WaveLogOdds)), ("failed", Inv(h.FailedFixLogOdds))));
            }

            foreach (var c in h.Contributions)
            {
                lines.Add(t.Ui(
                    "helper.contribution",
                    ("signal", c.SignalId),
                    ("state", ReportView.Camel(c.State.ToString())),
                    ("delta", Inv(c.DeltaLogOdds)),
                    ("extra", c.Counted ? string.Empty : t.Ui("helper.not_counted"))));
            }

            if (h.Entities.Count > 0)
            {
                lines.Add("entities: " + string.Join(", ", h.Entities.Select(e => ReportView.Camel(e.Kind.ToString()) + " " + e.Id)));
            }

            groups.Add(new HelperGroup(t.Kb(h.TitleKey, null, h.Id) + " [" + h.Id + "]", lines));
        }

        if (analysis.Proposals.Count > 0)
        {
            groups.Add(new HelperGroup(
                "fix proposals",
                [
                    .. analysis.Proposals.Select(p => t.Ui(
                        "helper.proposal",
                        ("fix", p.FixId + (p.Available ? string.Empty : " (unavailable: " + (p.UnavailableReasonKey ?? "?") + ")")),
                        ("utility", Inv(p.Utility)),
                        ("risk", ReportView.Camel(p.Risk.ToString())),
                        ("reversibility", ReportView.Camel(p.Reversibility.ToString())),
                        ("bind", BindText(p.Binding.Entities)))),
                ]));
        }

        if (analysis.Codes.Count > 0)
        {
            groups.Add(new HelperGroup(t.Ui("helper.codes"), [.. analysis.Codes.Select(c => c.Code + " (" + ReportView.Camel(c.Origin.ToString()) + (c.MatchedId is null ? ", unknown to KB" : " → " + c.MatchedId) + ")")]));
        }

        if (analysis.RuledOut.Count > 0)
        {
            groups.Add(new HelperGroup(t.Ui("helper.ruled_out"), [.. analysis.RuledOut.Select(r => r.HypothesisId + " · " + ReportView.Camel(r.Reason.ToString()) + (r.By is null ? string.Empty : " by " + r.By))]));
        }

        if (analysis.Anomalies.Count > 0)
        {
            groups.Add(new HelperGroup(t.Ui("helper.anomalies"), [.. analysis.Anomalies.Select(a => ReportView.Camel(a.Kind.ToString()) + ": " + a.Subject)]));
        }

        return groups;
    }

    public static string BindText(IReadOnlyDictionary<string, IReadOnlyList<EntityRef>> entities)
        => entities.Count == 0
            ? "-"
            : string.Join("; ", entities.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + string.Join(", ", e.Value.Select(v => v.Id))));

    public static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
