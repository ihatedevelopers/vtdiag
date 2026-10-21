using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Snapshots;
using VTD.Knowledge;

namespace VTD.Analysis.Signals;

/// <summary>Collector-derived signals (<see cref="SignalSource.Collector"/>): a mod adds a collector and a signal as data, no recompile.</summary>
public sealed class CollectorSignalProvider : ISignalProvider
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Provides { get; } = ["collector:*"];

    /// <inheritdoc />
    public IEnumerable<SignalResult> Evaluate(SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var def in context.Kb.Signals.Where(s => s.FromCollector is not null))
        {
            yield return Evaluate(def, context.Snapshot, context.AsOfUtc);
        }
    }

    /// <summary>Derives one signal from its collector result (True ≥ minCount matches, False when the collector ran and fewer match).</summary>
    /// <param name="def">Signal declaration.</param>
    /// <param name="snapshot">Snapshot.</param>
    /// <param name="asOf">Analysis time.</param>
    public static SignalResult Evaluate(SignalDef def, ScanSnapshot snapshot, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(snapshot);
        var spec = def.FromCollector!;
        var result = snapshot.Pack.FirstOrDefault(p => string.Equals(p.CollectorId, spec.Collector, StringComparison.Ordinal));
        if (result is null)
        {
            return Sig.Unknown(def.Id, UnknownReason.NotCollected);
        }

        if (!result.Data.TryGetValue(out var data))
        {
            return result.Data.Status == FactStatus.NotApplicable ? Sig.NotApplicable(def.Id) : Sig.Unknown(def.Id, Sig.ReasonOf(result.Data));
        }

        var count = CountMatches(data, spec, asOf);
        var saturate = Math.Max(1, spec.SaturateAt ?? spec.MinCount);
        var evidence = new[] { Sig.EvKey("evidence.collector.count", EvidenceSource.Scan, ("count", Sig.Num(count)), ("collector", spec.Collector)) };
        return count >= spec.MinCount
            ? Sig.True(def.Id, Math.Min(1.0, count / (double)saturate), evidence, data.Entities)
            : Sig.False(def.Id);
    }

    private static int CountMatches(PackFactData data, CollectorSignalSpec spec, DateTimeOffset asOf)
    {
        var filtered = spec.WithinDays is not null || spec.FieldEquals.Count > 0;
        if (!filtered || data.Items.Count == 0)
        {
            if (!filtered)
            {
                return data.Count;
            }

            return spec.WithinDays is not null && spec.FieldEquals.Count == 0 && data.LastUtc is { } last && last >= asOf.AddDays(-spec.WithinDays.Value)
                ? data.Count
                : 0;
        }

        var since = spec.WithinDays is { } days ? asOf.AddDays(-days) : DateTimeOffset.MinValue;
        var n = 0;
        foreach (var item in data.Items)
        {
            if (spec.WithinDays is not null
                && (!item.TryGetValue("utc", out var utcText)
                    || !DateTimeOffset.TryParse(utcText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc) || utc < since))
            {
                continue; // a time window was asked for: an item without a readable time does not count
            }

            if (spec.FieldEquals.All(kv => item.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase)))
            {
                n++;
            }
        }

        // Items are capped at PackCollectorLimits.MaxItems; when the result was truncated and nothing filtered out, trust Count.
        return data.Truncated && n == data.Items.Count && spec.FieldEquals.Count == 0 ? Math.Max(n, data.Count) : n;
    }
}

/// <summary>Answers to intake questions become <c>ask.*</c> signals ("Not sure" stays Unknown).</summary>
public sealed class AskSignalProvider : ISignalProvider
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Provides { get; } = ["ask.*"];

    /// <inheritdoc />
    public IEnumerable<SignalResult> Evaluate(SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var q in context.Kb.Questions)
        {
            if (!context.Intake.Answers.TryGetValue(q.Id, out var optionId))
            {
                yield return Sig.Unknown(q.Id, UnknownReason.NotCollected);
                foreach (var o in Options(q))
                {
                    yield return Sig.Unknown($"{q.Id}.{o.Id}", UnknownReason.NotCollected);
                }

                continue;
            }

            var chosen = q.Options.FirstOrDefault(o => string.Equals(o.Id, optionId, StringComparison.Ordinal))
                         ?? DefaultYesNo(optionId);
            var evidence = new[] { Sig.EvKey(q.TextKey, EvidenceSource.User, ("answer", optionId)) };
            yield return chosen?.Sets switch
            {
                Tri.True => Sig.True(q.Id, 1.0, evidence),
                Tri.False => new SignalResult { Id = q.Id, State = Tri.False, Evidence = evidence },
                _ => Sig.Unknown(q.Id, UnknownReason.InsufficientData),
            };
            if (q.Kind == QuestionKind.SingleChoice)
            {
                foreach (var o in q.Options)
                {
                    yield return Sig.Of($"{q.Id}.{o.Id}", string.Equals(o.Id, optionId, StringComparison.Ordinal));
                }
            }
        }
    }

    private static IReadOnlyList<QuestionOption> Options(QuestionDef q) => q.Kind == QuestionKind.SingleChoice ? q.Options : [];

    /// <summary>"YesNo questions get yes/no/unsure by default when empty" (QuestionDef contract).</summary>
    private static QuestionOption? DefaultYesNo(string optionId) => optionId switch
    {
        "yes" => new QuestionOption { Id = "yes", TextKey = "answer.yes", Sets = Tri.True },
        "no" => new QuestionOption { Id = "no", TextKey = "answer.no", Sets = Tri.False },
        _ => null,
    };
}

/// <summary><c>code.&lt;row id&gt;</c>: True when a typed or logged code matched the row.</summary>
public sealed class CodeSignalProvider : ISignalProvider
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Provides { get; } = ["code.*"];

    /// <inheritdoc />
    public IEnumerable<SignalResult> Evaluate(SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var uses = CodeUses.Collect(context.Snapshot, context.Intake, context.Kb);
        var logsKnown = context.Snapshot.Riot.DetectedCodes.IsKnown;
        foreach (var row in context.Kb.ErrorCodes)
        {
            var id = "code." + row.Id;
            var hits = uses.Where(u => string.Equals(u.Use.MatchedId, row.Id, StringComparison.Ordinal)).ToList();
            if (hits.Count > 0)
            {
                yield return Sig.True(id, 1.0, [.. hits.Select(h => Sig.EvKey(h.Use.Origin == CodeOrigin.Logged ? "evidence.code.logged" : "evidence.code.typed",
                    h.Use.Origin == CodeOrigin.Logged ? EvidenceSource.Log : EvidenceSource.User, ("code", h.Use.Code)))]);
            }
            else
            {
                yield return logsKnown ? Sig.False(id) : Sig.Unknown(id, Sig.ReasonOf(context.Snapshot.Riot.DetectedCodes));
            }
        }
    }
}

/// <summary>Monitor detector outputs become <c>mon.*</c> signals for re-ranking.</summary>
public sealed class MonitorSignalProvider : ISignalProvider
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Provides { get; } = ["mon.*"];

    /// <inheritdoc />
    public IEnumerable<SignalResult> Evaluate(SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Monitor is null)
        {
            yield break;
        }

        foreach (var d in context.Monitor.Detectors.Where(d => d.Id.StartsWith("mon.", StringComparison.Ordinal)))
        {
            yield return context.Monitor.InsufficientData && d.State != Tri.True ? Sig.Unknown(d.Id, UnknownReason.InsufficientData) : d;
        }
    }
}

/// <summary>A typed or logged code with the table row it matched.</summary>
/// <param name="Use">The code use as reported.</param>
/// <param name="Row">Matched row, if any.</param>
/// <param name="Count">Occurrences (logged).</param>
/// <param name="Stale">A logged code last seen long before the scan: it weighs like a typed code and never routes.</param>
public sealed record CodeHit(CodeUse Use, ErrorCodeDef? Row, int Count, bool Stale = false);

/// <summary>Collects typed and logged codes and matches them against the code table.</summary>
public static class CodeUses
{
    /// <summary>A logged code older than this (before the scan) is stale: old logs must not decide today's verdict.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromDays(14);

    /// <summary>Collects codes (logged first; a code both typed and logged counts once, as logged unless the log is stale).</summary>
    /// <param name="snapshot">Snapshot.</param>
    /// <param name="intake">Answers.</param>
    /// <param name="kb">KB.</param>
    public static IReadOnlyList<CodeHit> Collect(ScanSnapshot snapshot, IntakeAnswers intake, IKnowledgeBase kb)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(kb);
        var hits = new List<CodeHit>();
        var since = snapshot.Meta.CreatedUtc - FreshWindow;
        if (snapshot.Riot.DetectedCodes.TryGetValue(out var detected))
        {
            foreach (var d in detected.OrderBy(d => d.Code, StringComparer.Ordinal))
            {
                var m = kb.FindCode(d.Code);
                var normalized = m?.Normalized ?? CodeNormalizer.Normalize(d.Code);
                var stale = d.LastUtc is { } last && last < since;
                if (hits.All(h => !string.Equals(h.Use.Code, normalized, StringComparison.Ordinal)))
                {
                    hits.Add(new CodeHit(new CodeUse { Code = normalized, Origin = CodeOrigin.Logged, MatchedId = m?.Def.Id }, m?.Def, Math.Max(1, d.Count), stale));
                }
            }
        }

        foreach (var raw in intake.TypedCodes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var m = kb.FindCode(raw);
            var normalized = m?.Normalized ?? CodeNormalizer.Normalize(raw);
            var existing = hits.FindIndex(h => string.Equals(h.Use.Code, normalized, StringComparison.Ordinal));
            if (existing < 0)
            {
                hits.Add(new CodeHit(new CodeUse { Code = normalized, Origin = CodeOrigin.Typed, MatchedId = m?.Def.Id }, m?.Def, 1));
            }
            else if (hits[existing].Stale)
            {
                hits[existing] = hits[existing] with { Stale = false, Use = hits[existing].Use with { Origin = CodeOrigin.Typed } }; // the player sees it again now
            }
        }

        return hits;
    }
}
