using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;

namespace VTD.Analysis.Signals;

/// <summary>A provider with a fixed list of signal ids (one per catalog check id, see <see cref="BuiltinSignals"/>).</summary>
/// <param name="checkId">Catalog check id (or <c>-</c> for extra signals).</param>
/// <param name="provides">Signal ids.</param>
/// <param name="evaluate">Evaluation (may throw: the engine turns a throw into Unknown(Failed)).</param>
public sealed class CheckSignalProvider(string checkId, IReadOnlyCollection<string> provides, Func<SignalContext, IEnumerable<SignalResult>> evaluate) : ISignalProvider
{
    /// <summary>Catalog check id.</summary>
    public string CheckId { get; } = checkId;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Provides { get; } = provides;

    /// <inheritdoc />
    public IEnumerable<SignalResult> Evaluate(SignalContext context) => evaluate(context);
}

/// <summary>
/// Runs every provider and merges the results. Total by construction: a provider that throws yields Unknown(Failed) for
/// all its ids; an id a provider forgot yields Unknown(NotCollected); declared KB signals no provider computed are
/// Unknown(NotCollected). Results never depend on provider order (duplicates: the first provider registered wins).
/// </summary>
public sealed class SignalEngine
{
    private readonly IReadOnlyList<ISignalProvider> _providers;

    /// <summary>Creates an engine.</summary>
    /// <param name="providers">Providers.</param>
    public SignalEngine(IReadOnlyList<ISignalProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    /// <summary>The default provider set: built-in checks, the generic conflict provider, pack collectors, answers, codes, monitor.</summary>
    public static IReadOnlyList<ISignalProvider> DefaultProviders()
        => [.. BuiltinSignals.All, new ConflictSignalProvider(), new CollectorSignalProvider(), new AskSignalProvider(), new CodeSignalProvider(), new MonitorSignalProvider()];

    /// <summary>Evaluates all signals.</summary>
    /// <param name="context">Context.</param>
    public IReadOnlyDictionary<string, SignalResult> Evaluate(SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var results = new Dictionary<string, SignalResult>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            List<SignalResult> produced;
            try
            {
                produced = [.. provider.Evaluate(context)];
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                foreach (var id in provider.Provides.Where(p => !p.EndsWith('*')))
                {
                    results.TryAdd(id, Sig.Unknown(id, UnknownReason.Failed));
                }

                continue;
            }

            foreach (var r in produced)
            {
                results.TryAdd(r.Id, r);
            }

            foreach (var id in provider.Provides.Where(p => !p.EndsWith('*')))
            {
                results.TryAdd(id, Sig.Unknown(id, UnknownReason.NotCollected));
            }
        }

        foreach (var def in context.Kb.Signals)
        {
            results.TryAdd(def.Id, Sig.Unknown(def.Id, def.Source == SignalSource.Monitor ? UnknownReason.InsufficientData : UnknownReason.NotCollected));
        }

        return results;
    }
}
