using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Analysis;

/// <summary>Where a piece of evidence came from.</summary>
public enum EvidenceSource
{
    /// <summary>The scan snapshot.</summary>
    Scan = 0,

    /// <summary>The live monitor.</summary>
    Monitor = 1,

    /// <summary>The player's answers.</summary>
    User = 2,

    /// <summary>A helper's plan.</summary>
    Helper = 3,

    /// <summary>Fix history on this PC.</summary>
    History = 4,

    /// <summary>Game or client logs.</summary>
    Log = 5,
}

/// <summary>A human-readable piece of evidence ("14 Realtek driver resets in 14 days").</summary>
[Sensitivity(Sensitivity.None)]
public sealed record Evidence
{
    /// <summary>String key.</summary>
    public required string TextKey { get; init; }

    /// <summary>Placeholder values (invariant strings).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Snapshot path the evidence rests on, for example <c>network.adapters[2].enabled</c>.</summary>
    public string? FactPath { get; init; }

    /// <summary>Source.</summary>
    public EvidenceSource Source { get; init; } = EvidenceSource.Scan;
}

/// <summary>
/// A named three-valued signal with evidence and the entities it points at (which bind fixes). Signals are computed by
/// C# providers from the snapshot, by the generic conflict provider, from pack collectors, from the monitor and from
/// answers. A signal that rests on an Unknown fact is Unknown, with <see cref="UnknownReason"/> set.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record SignalResult
{
    /// <summary>Signal id.</summary>
    public required string Id { get; init; }

    /// <summary>State.</summary>
    public required Tri State { get; init; }

    /// <summary>Strength 0..1 that scales the evidence weight (for example 14 NIC resets = 1.0).</summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>Why the signal is Unknown.</summary>
    public UnknownReason? UnknownReason { get; init; }

    /// <summary>Evidence.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    /// <summary>Entities that matched (for example the adapter whose InstanceId binds the disable fix).</summary>
    public IReadOnlyList<EntityRef> Entities { get; init; } = [];
}

/// <summary>What the player told us. Typed codes are kept raw here; normalization happens in Analysis.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record IntakeAnswers
{
    /// <summary>No answers.</summary>
    public static IntakeAnswers Empty { get; } = new();

    /// <summary>Chosen symptom ids.</summary>
    public IReadOnlyList<string> Symptoms { get; init; } = [];

    /// <summary>Codes the player typed (raw text, for example <c>van 9003</c>).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public IReadOnlyList<string> TypedCodes { get; init; } = [];

    /// <summary>"When?" chips: <c>in_menu</c>, <c>in_match</c>, <c>at_launch</c>, <c>always</c>.</summary>
    public IReadOnlyList<string> When { get; init; } = [];

    /// <summary>Question id to chosen option id.</summary>
    public IReadOnlyDictionary<string, string> Answers { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>Input of a signal provider. Not serialized.</summary>
public sealed record SignalContext
{
    /// <summary>Snapshot.</summary>
    public required ScanSnapshot Snapshot { get; init; }

    /// <summary>Monitor summary, when a match was watched.</summary>
    public MonitorSummary? Monitor { get; init; }

    /// <summary>Answers.</summary>
    public required IntakeAnswers Intake { get; init; }

    /// <summary>Fix history on this PC.</summary>
    public IReadOnlyList<FixAttempt> History { get; init; } = [];

    /// <summary>KB.</summary>
    public required IKnowledgeBase Kb { get; init; }

    /// <summary>Analysis time (waves and "last N days" use this, never the wall clock).</summary>
    public required DateTimeOffset AsOfUtc { get; init; }
}

/// <summary>Computes signals (A2). Must be total: never throws on data, returns Unknown instead.</summary>
public interface ISignalProvider
{
    /// <summary>Signal ids (or <c>prefix.*</c> patterns) this provider emits.</summary>
    IReadOnlyCollection<string> Provides { get; }

    /// <summary>Evaluates.</summary>
    /// <param name="context">Context.</param>
    IEnumerable<SignalResult> Evaluate(SignalContext context);
}
