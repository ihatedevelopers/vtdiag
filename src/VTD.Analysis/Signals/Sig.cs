using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Analysis;

namespace VTD.Analysis.Signals;

/// <summary>Small constructors for <see cref="SignalResult"/> (three-valued: Unknown is never False).</summary>
internal static class Sig
{
    public static SignalResult True(string id, double strength = 1.0, IReadOnlyList<Evidence>? evidence = null, IReadOnlyList<EntityRef>? entities = null)
        => new() { Id = id, State = Tri.True, Strength = Math.Clamp(strength, 0.0, 1.0), Evidence = evidence ?? [], Entities = entities ?? [] };

    public static SignalResult False(string id, IReadOnlyList<Evidence>? evidence = null)
        => new() { Id = id, State = Tri.False, Evidence = evidence ?? [] };

    public static SignalResult Of(string id, bool value, double strength = 1.0, IReadOnlyList<Evidence>? evidence = null, IReadOnlyList<EntityRef>? entities = null)
        => value ? True(id, strength, evidence, entities) : False(id);

    public static SignalResult Unknown(string id, UnknownReason reason)
        => new() { Id = id, State = Tri.Unknown, UnknownReason = reason, Strength = 0 };

    public static SignalResult NotApplicable(string id) => new() { Id = id, State = Tri.NotApplicable, Strength = 0 };

    /// <summary>Signal from a boolean fact (Unknown and NotApplicable pass through).</summary>
    public static SignalResult FromFact(string id, Fact<bool> fact, bool invert = false)
    {
        var tri = fact.ToTri();
        if (invert)
        {
            tri = tri.Not();
        }

        return tri switch
        {
            Tri.True => True(id),
            Tri.False => False(id),
            Tri.NotApplicable => NotApplicable(id),
            _ => Unknown(id, ReasonOf(fact)),
        };
    }

    /// <summary>Signal from any fact through a projection.</summary>
    public static SignalResult FromFact<T>(string id, Fact<T> fact, Func<T, SignalResult> project)
    {
        if (fact.TryGetValue(out var value))
        {
            // A partial list (Fact.IsComplete false): what is in it is real, so True stays; a False ("not found",
            // "fewer than N") could flip with the missing entries, so it is Unknown(Incomplete), never False.
            // Signals whose True means ABSENCE must check IsComplete themselves.
            var result = project(value);
            return !fact.IsComplete && result.State == Tri.False ? Unknown(result.Id, UnknownReason.Incomplete) : result;
        }

        return fact.Status == FactStatus.NotApplicable ? NotApplicable(id) : Unknown(id, ReasonOf(fact));
    }

    /// <summary>
    /// Like <see cref="FromFact{T}"/> for a signal whose True means ABSENCE ("nothing like X is installed"): on a partial
    /// list (<see cref="Fact{T}.IsComplete"/> false) a True could be wrong, so it becomes Unknown(Incomplete); a False
    /// (X was found) stays.
    /// </summary>
    public static SignalResult FromFactAbsence<T>(string id, Fact<T> fact, Func<T, SignalResult> project)
    {
        if (fact.TryGetValue(out var value))
        {
            var result = project(value);
            return !fact.IsComplete && result.State == Tri.True ? Unknown(result.Id, UnknownReason.Incomplete) : result;
        }

        return fact.Status == FactStatus.NotApplicable ? NotApplicable(id) : Unknown(id, ReasonOf(fact));
    }

    /// <summary>Signal from a nullable boolean inside a list item (null means "not read").</summary>
    public static SignalResult FromNullable(string id, bool? value, UnknownReason reasonIfNull = UnknownReason.NotCollected)
        => value is null ? Unknown(id, reasonIfNull) : Of(id, value.Value);

    public static UnknownReason ReasonOf<T>(Fact<T> fact) => fact.Reason ?? UnknownReason.Failed;

    public static Evidence Ev(string signalId, params (string Key, string Value)[] args)
        => new()
        {
            TextKey = $"signal.{signalId}.ev",
            Args = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
            Source = EvidenceSource.Scan,
        };

    public static Evidence EvKey(string textKey, EvidenceSource source, params (string Key, string Value)[] args)
        => new() { TextKey = textKey, Args = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal), Source = source };

    public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    public static string List(IEnumerable<string> items, int max = 5)
    {
        var list = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", list.Take(max));
        return list.Count > max ? shown + string.Create(CultureInfo.InvariantCulture, $" (+{list.Count - max})") : shown;
    }

    /// <summary>The three-valued OR of several results: True if any True, False if all False, else Unknown.</summary>
    public static SignalResult Any(string id, IReadOnlyList<SignalResult> parts)
    {
        var trues = parts.Where(p => p.State == Tri.True).ToList();
        if (trues.Count > 0)
        {
            return True(id, trues.Max(p => p.Strength), [.. trues.SelectMany(p => p.Evidence)], [.. trues.SelectMany(p => p.Entities).DistinctBy(e => (e.Kind, e.Id))]);
        }

        if (parts.Count > 0 && parts.All(p => p.State is Tri.False or Tri.NotApplicable) && parts.Any(p => p.State == Tri.False))
        {
            return False(id);
        }

        if (parts.Count > 0 && parts.All(p => p.State == Tri.NotApplicable))
        {
            return NotApplicable(id);
        }

        var reason = parts.Where(p => p.State == Tri.Unknown).Select(p => p.UnknownReason ?? UnknownReason.NotCollected).DefaultIfEmpty(UnknownReason.NotCollected).Min();
        return Unknown(id, reason);
    }
}
