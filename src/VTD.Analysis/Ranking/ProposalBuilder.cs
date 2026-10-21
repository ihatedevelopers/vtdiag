using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis.Ranking;

/// <summary>
/// Fix proposals ordered by utility = Σ P(h) × P(fix works | h) / cost (design_B.md section 5.1), with
/// cost = 1 + 2·reboot required (1 if recommended) + 3·medium risk + 5·one-way + minutes / 5. Effective risk,
/// reversibility, reboot and admin come from the primitives and are never lower than declared. Each proposal binds
/// concrete entities from its source signal; a fix with nothing to bind is not proposed.
/// </summary>
internal static class ProposalBuilder
{
    public static IReadOnlyList<FixProposal> Build(IReadOnlyList<Scored> survivors, IReadOnlyDictionary<string, SignalResult> signals, AnalysisInput input, IKnowledgeBase kb)
    {
        var candidates = new Dictionary<string, List<Scored>>(StringComparer.Ordinal);
        foreach (var h in survivors.Where(h => h.Posterior >= RankingConstants.Possible))
        {
            var ids = h.Rule.Fixes.Concat(kb.Fixes.Where(f => f.AppliesTo.Contains(h.Rule.Id, StringComparer.Ordinal)).Select(f => f.Id)).Distinct(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!candidates.TryGetValue(id, out var list))
                {
                    candidates[id] = list = [];
                }

                list.Add(h);
            }
        }

        var proposals = new List<FixProposal>();
        foreach (var (fixId, allHyps) in candidates)
        {
            var fix = kb.FindFix(fixId);
            if (fix is null || fix.Deprecated is not null || fix.Steps.Count == 0)
            {
                continue;
            }

            var p = Effective(fix);
            if (p.Risk == RiskLevel.High)
            {
                continue; // high risk is guide-only; never trust a pack that says otherwise
            }

            // A one-way or medium-risk change needs real findings behind it, not just a prior that happens to be high.
            if ((p.Rev == Reversibility.OneWay || p.Risk >= RiskLevel.Medium) && !allHyps.Any(h => h.EvidenceLogOdds + h.CodeLogOdds >= 0.5))
            {
                continue;
            }

            foreach (var (binding, hyps) in Bindings(fix, allHyps, signals, kb))
            {
            var benefit = hyps.Sum(h => h.Posterior * HypothesisScorer.SuccessProbability(fix));
            var cost = 1.0 + (p.Reboot == RebootNeed.Required ? 2 : p.Reboot == RebootNeed.Recommended ? 1 : 0)
                       + (p.Risk == RiskLevel.Medium ? 3 : 0) + (p.Rev == Reversibility.OneWay ? 5 : 0) + ((fix.EstMinutes ?? 1.0) / 5.0);
            var (available, reason) = Availability(fix, p.Admin, binding, input, kb);
            proposals.Add(new FixProposal
            {
                FixId = fix.Id,
                HypothesisIds = [.. hyps.OrderByDescending(h => h.Posterior).Select(h => h.Rule.Id)],
                Binding = binding,
                Utility = Math.Round(benefit / cost, 4),
                ExpectedBenefit = Math.Round(benefit, 4),
                Cost = Math.Round(cost, 4),
                ReasonKey = ReasonKey(p),
                Risk = p.Risk,
                Reversibility = p.Rev,
                Reboot = p.Reboot,
                NeedsAdmin = p.Admin,
                Available = available,
                UnavailableReasonKey = reason,
            });
            }
        }

        return [.. proposals.OrderByDescending(p => p.Available).ThenByDescending(p => p.Utility).ThenBy(p => p.FixId, StringComparer.Ordinal)
            .ThenBy(p => p.Binding.ComputeHash(), StringComparer.Ordinal)];
    }

    /// <summary>
    /// Bindings for a fix. A binding from a conflict wildcard (<c>conflict.*.service_present</c>) yields ONE proposal per
    /// conflict, so one consent never covers two unrelated programs; each proposal keeps only the hypotheses about it.
    /// </summary>
    private static IEnumerable<(FixBinding Binding, List<Scored> Hyps)> Bindings(FixDef fix, List<Scored> hyps, IReadOnlyDictionary<string, SignalResult> signals, IKnowledgeBase kb)
    {
        var wildcard = fix.Bind.Values.Any(b => b.FromSignal is { } f && f.StartsWith("conflict.", StringComparison.Ordinal) && f.Contains('*', StringComparison.Ordinal));
        if (!wildcard)
        {
            if (Bind(fix, hyps, signals, kb, onlyConflict: null) is { } single)
            {
                yield return (single, hyps);
            }

            yield break;
        }

        var conflictIds = kb.Conflicts.Where(c => c.Fix.FixIds.Contains(fix.Id, StringComparer.Ordinal)).Select(c => c.Id).Order(StringComparer.Ordinal);
        foreach (var id in conflictIds)
        {
            var about = hyps.Where(h => ReferencedConflicts(h).Contains(id, StringComparer.Ordinal) || !ReferencedConflicts(h).Any()).ToList();
            if (about.Count == 0)
            {
                continue;
            }

            if (Bind(fix, about, signals, kb, onlyConflict: id) is { } binding)
            {
                yield return (binding, about);
            }
        }
    }

    public static (RiskLevel Risk, Reversibility Rev, RebootNeed Reboot, bool Admin) Effective(FixDef fix)
    {
        var risk = fix.Risk;
        var rev = fix.Reversibility;
        var reboot = fix.Reboot;
        var admin = fix.Admin;
        foreach (var st in fix.Steps)
        {
            if (OpCatalog.Find(st.Op) is not { } op)
            {
                continue;
            }

            risk = (RiskLevel)Math.Max((int)risk, (int)op.RiskFloor);
            rev = (Reversibility)Math.Max((int)rev, (int)op.Reversibility);
            reboot = (RebootNeed)Math.Max((int)reboot, (int)op.Reboot);
            admin |= op.RequiresAdmin;
        }

        return (risk, rev, reboot, admin);
    }

    private static string ReasonKey((RiskLevel Risk, Reversibility Rev, RebootNeed Reboot, bool Admin) p)
        => p.Rev == Reversibility.OneWay ? "proposal.reason.one_way"
            : p.Risk >= RiskLevel.Medium ? "proposal.reason.medium_risk"
            : p.Reboot == RebootNeed.Required ? "proposal.reason.needs_restart"
            : p.Rev == Reversibility.SessionOnly ? "proposal.reason.session_only"
            : "proposal.reason.quick_harmless_undoable";

    private static (bool, string?) Availability(FixDef fix, bool needsAdmin, FixBinding binding, AnalysisInput input, IKnowledgeBase kb)
    {
        var s = input.Snapshot;
        if (kb.Version.Source == KbSource.Dev)
        {
            return (false, "proposal.unavailable.dev_pack");
        }

        if (needsAdmin && s.Meta.Privilege == PrivilegeMode.Limited)
        {
            return (false, "proposal.unavailable.limited_mode");
        }

        if (input.SkippedFixIds.Contains(fix.Id, StringComparer.Ordinal))
        {
            return (false, "proposal.unavailable.skipped");
        }

        if (input.History.Any(a => string.Equals(a.FixId, fix.Id, StringComparison.Ordinal) && a.Outcome == FixOutcome.NotFixed))
        {
            return (false, "proposal.unavailable.failed_before");
        }

        foreach (var g in fix.Guards.Where(g => g.Severity == GuardSeverity.Block))
        {
            if (StaticGuardFails(g, binding, s))
            {
                return (false, g.FailKey);
            }
        }

        return (true, null);
    }

    /// <summary>Guards we can already see failing in the snapshot (the fix runner re-checks every guard live before apply).</summary>
    private static bool StaticGuardFails(GuardDef g, FixBinding binding, ScanSnapshot s)
    {
        if (!s.Network.Adapters.TryGetValue(out var adapters))
        {
            return false;
        }

        var bound = binding.Entities.Values.SelectMany(e => e).Where(e => e.Kind == EntityKind.Adapter).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return g.Probe switch
        {
            GuardIds.AdapterNotDefaultRoute => adapters.Any(a => bound.Contains(a.InstanceId) && a.IsDefaultRoute),
            GuardIds.OtherAdapterOnline => !adapters.Any(a => a.Physical && !bound.Contains(a.InstanceId) && a.Enabled && a.Status == AdapterOperStatus.Up && (a.HasInternet == true || a.IsDefaultRoute)),
            GuardIds.InternetReachable => s.Network.Connectivity.TryGetValue(out var ci) && !ci.Ipv4Internet,
            _ => false,
        };
    }

    private static FixBinding? Bind(FixDef fix, List<Scored> hyps, IReadOnlyDictionary<string, SignalResult> signals, IKnowledgeBase kb, string? onlyConflict)
    {
        var map = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal);
        foreach (var (name, b) in fix.Bind.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (b.FromSignal is null)
            {
                return null; // plan-only template binding
            }

            var entities = new List<EntityRef>();
            foreach (var sig in MatchingSignals(b.FromSignal, fix, hyps, signals, kb, onlyConflict))
            {
                entities.AddRange(sig.Entities.Where(e => e.Kind == b.Kind));
            }

            entities = [.. entities.DistinctBy(e => e.Id).OrderBy(e => e.Id, StringComparer.Ordinal)];
            if (entities.Count == 0)
            {
                return null;
            }

            map[name] = b.Pick == BindPick.One ? [entities[0]] : entities;
        }

        return map.Count == 0 ? FixBinding.Empty : new FixBinding { Entities = map };
    }

    private static IEnumerable<SignalResult> MatchingSignals(string pattern, FixDef fix, List<Scored> hyps, IReadOnlyDictionary<string, SignalResult> signals, IKnowledgeBase kb, string? onlyConflict)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return signals.TryGetValue(pattern, out var r) && r.State == Tri.True ? [r] : [];
        }

        var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", "([^.]+)", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
        var referenced = hyps.SelectMany(ReferencedConflicts).ToHashSet(StringComparer.Ordinal);
        var eligible = kb.Conflicts.Where(c => c.Fix.FixIds.Contains(fix.Id, StringComparer.Ordinal)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (referenced.Count > 0)
        {
            eligible.IntersectWith(referenced);
        }

        if (onlyConflict is not null)
        {
            eligible.IntersectWith([onlyConflict]);
        }

        return signals.Values.Where(sig => sig.State == Tri.True)
            .Select(sig => (sig, m: rx.Match(sig.Id)))
            .Where(x => x.m.Success && (!pattern.StartsWith("conflict.", StringComparison.Ordinal) || eligible.Contains(x.m.Groups[1].Value)))
            .OrderBy(x => x.sig.Id, StringComparer.Ordinal)
            .Select(x => x.sig);
    }

    /// <summary>Conflict ids a hypothesis talks about (its signals, or its own id for a generated rule).</summary>
    private static IEnumerable<string> ReferencedConflicts(Scored h)
    {
        if (h.Rule.Kind == RuleKind.Generated && h.Rule.Id.StartsWith("conflict-", StringComparison.Ordinal))
        {
            yield return h.Rule.Id["conflict-".Length..];
        }

        foreach (var s in h.Rule.Evidence.Select(e => e.Signal).Concat(h.Rule.Requires))
        {
            if (s.StartsWith("conflict.", StringComparison.Ordinal))
            {
                var parts = s.Split('.');
                if (parts.Length >= 3)
                {
                    yield return parts[1];
                }
            }
        }
    }
}
