using VTD.Analysis.Ranking;
using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis;

/// <summary>"We could not check X" entries and unexplained findings ("help the KB grow").</summary>
internal static class Findings
{
    public static IReadOnlyList<CouldNotCheck> CouldNotCheck(IReadOnlyList<Scored> survivors, IReadOnlyDictionary<string, SignalResult> signals, IKnowledgeBase kb)
    {
        var defs = kb.Signals.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var relevant = survivors.OrderByDescending(s => s.Posterior).Select((s, i) => (s, i))
            .Where(x => x.i < 10 || x.s.Posterior >= AnalysisTuning.CouldNotCheckMinPosterior).Select(x => x.s).ToList();
        var map = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var h in relevant)
        {
            var mattering = h.Rule.Requires
                .Concat(h.Rule.Evidence.Where(e => e.Veto is not null || Math.Max(Math.Abs(e.WhenTrue ?? 0), Math.Abs(e.WhenFalse ?? 0)) >= AnalysisTuning.CouldNotCheckMinWeight).Select(e => e.Signal));
            foreach (var id in mattering)
            {
                var r = HypothesisScorer.State(signals, id);
                if (r.State != Tri.Unknown || !defs.TryGetValue(id, out var def) || def.Source is SignalSource.Ask or SignalSource.Monitor)
                {
                    continue;
                }

                if (r.UnknownReason == UnknownReason.Unsupported)
                {
                    continue; // not readable on any PC yet: listed once in the docs, not per report
                }

                if (!map.TryGetValue(id, out var set))
                {
                    map[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(h.Rule.Id);
            }
        }

        return [.. map.Select(kv => new CouldNotCheck
        {
            SignalId = kv.Key,
            Reason = HypothesisScorer.State(signals, kv.Key).UnknownReason ?? UnknownReason.NotCollected,
            TextKey = defs[kv.Key].TitleKey,
            AffectsHypotheses = [.. kv.Value],
        })];
    }

    public static IReadOnlyList<AnomalyFinding> Anomalies(ScanSnapshot s, IKnowledgeBase kb, DateTimeOffset asOf)
    {
        var list = new List<AnomalyFinding>();
        var knownModules = BuiltinSignals.KnownModules(kb);
        if (s.Events.AppCrashes.TryGetValue(out var crashes))
        {
            foreach (var g in crashes.Where(c => c.Module is not null && c.Utc >= asOf - ConflictMatcher.CrashWindow && !knownModules.Contains(c.Module) && !IsCommonModule(c.Module))
                         .GroupBy(c => c.Module!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new AnomalyFinding
                {
                    Kind = AnomalyKind.UnknownFaultingModule,
                    Subject = g.Key,
                    Evidence = [Sig.EvKey("anomaly.unknownFaultingModule", EvidenceSource.Log, ("subject", g.Key), ("count", Sig.Num(g.Count())))],
                });
            }
        }

        var knownDrivers = kb.Conflicts.SelectMany(c => c.Detect.DriverNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (s.Security.HvciIncompatibleDrivers.TryGetValue(out var blocked))
        {
            foreach (var d in blocked.Where(d => !knownDrivers.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new AnomalyFinding { Kind = AnomalyKind.BlockedDriver, Subject = d, Evidence = [Sig.EvKey("anomaly.blockedDriver", EvidenceSource.Scan, ("subject", d))] });
            }
        }

        if (s.Network.WinsockProviders.TryGetValue(out var lsps))
        {
            foreach (var l in lsps.Where(l => !l.Contains("mdns", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new AnomalyFinding { Kind = AnomalyKind.NonMicrosoftLsp, Subject = l, Evidence = [Sig.EvKey("anomaly.nonMicrosoftLsp", EvidenceSource.Scan, ("subject", l))] });
            }
        }

        var scm = s.Pack.FirstOrDefault(p => string.Equals(p.CollectorId, "scm_crash_loop", StringComparison.Ordinal));
        if (scm is not null && scm.Data.TryGetValue(out var scmData))
        {
            foreach (var g in scmData.Items.Where(i => i.TryGetValue("param1", out _)).GroupBy(i => i["param1"], StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 3).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new AnomalyFinding { Kind = AnomalyKind.CrashLoopingService, Subject = g.Key, Evidence = [Sig.EvKey("anomaly.crashLoopingService", EvidenceSource.Scan, ("subject", g.Key), ("count", Sig.Num(g.Count())))] });
            }
        }

        if (s.Devices.TryGetValue(out var devices))
        {
            foreach (var d in devices.Where(d => d.Present && d.ProblemCode is not 0 and not 22).OrderBy(d => d.InstanceId, StringComparer.Ordinal))
            {
                var subject = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{d.ClassName ?? "Device"} (code {d.ProblemCode})");
                list.Add(new AnomalyFinding { Kind = AnomalyKind.DeviceProblemCode, Subject = subject, Evidence = [Sig.EvKey("anomaly.deviceProblemCode", EvidenceSource.Scan, ("subject", subject))] });
            }
        }

        if (s.Drivers.TryGetValue(out var drivers))
        {
            foreach (var d in drivers.Where(d => d.Signer?.Status is SignatureStatus.Unsigned or SignatureStatus.Invalid).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new AnomalyFinding { Kind = AnomalyKind.UnsignedDriver, Subject = d.Name, Evidence = [Sig.EvKey("anomaly.unsignedDriver", EvidenceSource.Scan, ("subject", d.Name))] });
            }
        }

        return list;
    }

    private static bool IsCommonModule(string module)
        => module.Equals("VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) || module.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)
           || module.Equals("KERNELBASE.dll", StringComparison.OrdinalIgnoreCase) || module.Equals("ucrtbase.dll", StringComparison.OrdinalIgnoreCase)
           || module.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase) || module.Equals("unknown", StringComparison.OrdinalIgnoreCase)
           || module.StartsWith("nvwgf2um", StringComparison.OrdinalIgnoreCase) || module.StartsWith("amdxx", StringComparison.OrdinalIgnoreCase)
           || module.StartsWith("atidxx", StringComparison.OrdinalIgnoreCase) || module.StartsWith("igd", StringComparison.OrdinalIgnoreCase)
           || module.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) || module.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase);
}
