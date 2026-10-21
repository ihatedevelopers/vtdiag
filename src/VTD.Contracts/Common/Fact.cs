using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using VTD.Contracts.Serialization;

namespace VTD.Contracts;

/// <summary>
/// Three-valued truth used by signals, guards and conditions. <b>Unknown is never False</b> (DESIGN.md section 2):
/// evidence that rests on an Unknown value contributes nothing, and a gate fires only on a definite <see cref="False"/>.
/// </summary>
public enum Tri
{
    /// <summary>Could not be determined (not collected, needs admin, failed, timed out...). Never treat as False.</summary>
    Unknown = 0,

    /// <summary>Definitely false: it was checked and the condition does not hold.</summary>
    False = 1,

    /// <summary>Definitely true: it was checked and the condition holds.</summary>
    True = 2,

    /// <summary>The question does not apply to this PC (for example "is the Realtek adapter enabled" when there is none).
    /// Contributes nothing, like Unknown, but the report does not say "could not check".</summary>
    NotApplicable = 3,
}

/// <summary>Whether a <see cref="Fact{T}"/> carries a value.</summary>
public enum FactStatus
{
    /// <summary>The value was read. <see cref="Fact{T}.Value"/> is available.</summary>
    Known = 0,

    /// <summary>The value could not be read. <see cref="Fact{T}.Reason"/> says why. The report says "could not check X".</summary>
    Unknown = 1,

    /// <summary>The value does not exist on this PC by design (for example Secure Boot setup mode on legacy BIOS).</summary>
    NotApplicable = 2,
}

/// <summary>Why a fact or a signal is Unknown. Machine-readable; the UI maps each value to a sentence.</summary>
public enum UnknownReason
{
    /// <summary>The collector did not run (quick or targeted scan, or the area was skipped).</summary>
    NotCollected = 0,

    /// <summary>Needs an elevated engine; the scan ran in limited mode (UAC declined).</summary>
    NeedsAdmin = 1,

    /// <summary>Access was denied even though the engine was elevated (policy, ACL, Tamper Protection).</summary>
    AccessDenied = 2,

    /// <summary>The source did not answer within the collector timeout.</summary>
    TimedOut = 3,

    /// <summary>The API call failed (exception or error code; see <see cref="Fact{T}.Detail"/>).</summary>
    Failed = 4,

    /// <summary>This Windows build, firmware or hardware does not expose the value.</summary>
    Unsupported = 5,

    /// <summary>Too little data to decide (monitor: short match or too few frames).</summary>
    InsufficientData = 6,

    /// <summary>The data was present but could not be parsed.</summary>
    Unparseable = 7,

    /// <summary>The value was removed by redaction (only appears in exported, share-level snapshots).</summary>
    Redacted = 8,

    /// <summary>Only part of a list could be read (<see cref="Fact{T}.IsComplete"/> false), so "not in the list" cannot be
    /// decided. Signals use it when a partial list would otherwise give a definite False.</summary>
    Incomplete = 9,
}

/// <summary>
/// A three-valued fact with provenance. A fact is either Known (with a non-null value), Unknown (with a reason) or
/// NotApplicable. Reading <see cref="Value"/> on a fact that is not Known throws, so "Unknown means False" bugs fail
/// loudly instead of silently clearing a hypothesis.
/// </summary>
/// <remarks>
/// JSON shape (camelCase, culture-invariant):
/// <c>{"status":"known","value":...,"source":"..."}</c>,
/// <c>{"status":"known","value":[...],"complete":false,"detail":"...","source":"..."}</c> (a partial list, see
/// <see cref="IsComplete"/>),
/// <c>{"status":"unknown","reason":"needsAdmin","detail":"...","source":"..."}</c>,
/// <c>{"status":"notApplicable","detail":"..."}</c>.
/// Every <c>T</c> used in a <see cref="Fact{T}"/> must be registered in <see cref="VtdJsonContext"/>.
/// </remarks>
/// <typeparam name="T">The value type. Reference values are never null when Known.</typeparam>
[JsonConverter(typeof(FactJsonConverterFactory))]
public sealed class Fact<T> : IEquatable<Fact<T>>
{
    private readonly T? _value;

    private Fact(FactStatus status, T? value, UnknownReason? reason, string? detail, string? source, bool complete = true)
    {
        Status = status;
        _value = value;
        Reason = reason;
        Detail = detail;
        Source = source;
        IsComplete = complete;
    }

    /// <summary>Known, Unknown or NotApplicable.</summary>
    public FactStatus Status { get; }

    /// <summary>True when the fact carries a value.</summary>
    public bool IsKnown => Status == FactStatus.Known;

    /// <summary>The value. Throws <see cref="InvalidOperationException"/> unless <see cref="IsKnown"/>.</summary>
    public T Value => Status == FactStatus.Known
        ? _value!
        : throw new InvalidOperationException($"Fact is {Status} ({Reason?.ToString() ?? "no reason"}); Unknown is never False. Check IsKnown or use TryGetValue.");

    /// <summary>Why the fact is Unknown. Null when Known or NotApplicable.</summary>
    public UnknownReason? Reason { get; }

    /// <summary>Short, invariant, non-localized machine detail (for example <c>HRESULT 0x80041003</c>). Never PII.</summary>
    public string? Detail { get; }

    /// <summary>Provenance: <c>&lt;collectorId&gt;/&lt;method&gt;</c>, for example <c>security/registry:SecureBoot\State</c>.</summary>
    public string? Source { get; }

    /// <summary>
    /// False when a Known value (in practice a list) is only PART of the truth: some entries could not be read (access
    /// denied, needs admin, a per-source timeout) or the list hit a size cap. <see cref="Detail"/> says why. What IS in the
    /// list is real, so "X is present" stays evidence; "X is absent" from a partial list is Unknown, never False. Always
    /// true for Unknown and NotApplicable facts.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>Gets the value when Known.</summary>
    /// <param name="value">The value when this returns true.</param>
    /// <returns>True when the fact is Known.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return Status == FactStatus.Known;
    }

    /// <summary>Creates a Known fact.</summary>
    /// <param name="value">The value; must not be null.</param>
    /// <param name="source">Provenance.</param>
    public static Fact<T> Known(T value, string? source = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "A Known fact needs a value. Use Unknown or NotApplicable instead.");
        }

        return new Fact<T>(FactStatus.Known, value, null, null, source);
    }

    /// <summary>Creates a Known but partial fact (see <see cref="IsComplete"/>), for example a driver list read without
    /// admin rights or a process list that hit its cap.</summary>
    /// <param name="value">What could be read; must not be null.</param>
    /// <param name="detail">Short machine detail of what is missing (for example <c>3 keys access denied</c>), never PII.</param>
    /// <param name="source">Provenance.</param>
    public static Fact<T> Partial(T value, string? detail = null, string? source = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "A Known fact needs a value. Use Unknown or NotApplicable instead.");
        }

        return new Fact<T>(FactStatus.Known, value, null, detail, source, complete: false);
    }

    /// <summary>Creates an Unknown fact.</summary>
    /// <param name="reason">Why it is unknown.</param>
    /// <param name="detail">Short machine detail, never PII.</param>
    /// <param name="source">Provenance.</param>
    public static Fact<T> Unknown(UnknownReason reason, string? detail = null, string? source = null)
        => new(FactStatus.Unknown, default, reason, detail, source);

    /// <summary>Creates a NotApplicable fact.</summary>
    /// <param name="detail">Short machine detail.</param>
    /// <param name="source">Provenance.</param>
    public static Fact<T> NotApplicable(string? detail = null, string? source = null)
        => new(FactStatus.NotApplicable, default, null, detail, source);

    /// <summary>Projects a Known value; Unknown and NotApplicable pass through with their reason.</summary>
    /// <typeparam name="TOut">The projected type.</typeparam>
    /// <param name="map">Projection; must not return null.</param>
    public Fact<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Status switch
        {
            FactStatus.Known when !IsComplete => Fact<TOut>.Partial(map(_value!), Detail, Source),
            FactStatus.Known => Fact<TOut>.Known(map(_value!), Source),
            FactStatus.Unknown => Fact<TOut>.Unknown(Reason ?? UnknownReason.Failed, Detail, Source),
            _ => Fact<TOut>.NotApplicable(Detail, Source),
        };
    }

    /// <inheritdoc />
    public bool Equals(Fact<T>? other)
        => other is not null
           && Status == other.Status
           && Reason == other.Reason
           && IsComplete == other.IsComplete
           && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
           && string.Equals(Source, other.Source, StringComparison.Ordinal)
           && EqualityComparer<T?>.Default.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Fact<T> f && Equals(f);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Status, Reason, _value);

    /// <inheritdoc />
    public override string ToString() => Status switch
    {
        FactStatus.Known when !IsComplete => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Partial({_value})"),
        FactStatus.Known => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Known({_value})"),
        FactStatus.Unknown => $"Unknown({Reason})",
        _ => "NotApplicable",
    };
}

/// <summary>Non-generic helpers for <see cref="Fact{T}"/>.</summary>
public static class Fact
{
    /// <summary>Creates a Known fact with type inference.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">The value; must not be null.</param>
    /// <param name="source">Provenance.</param>
    public static Fact<T> Known<T>(T value, string? source = null) => Fact<T>.Known(value, source);

    /// <summary>Converts a boolean fact to <see cref="Tri"/>; Unknown stays Unknown, NotApplicable stays NotApplicable.</summary>
    /// <param name="fact">The fact.</param>
    public static Tri ToTri(this Fact<bool> fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return fact.Status switch
        {
            FactStatus.Known => fact.Value ? Tri.True : Tri.False,
            FactStatus.NotApplicable => Tri.NotApplicable,
            _ => Tri.Unknown,
        };
    }

    /// <summary>Converts a nullable boolean (used inside list items, where null means "not read") to <see cref="Tri"/>.</summary>
    /// <param name="value">The value.</param>
    public static Tri ToTri(this bool? value) => value switch
    {
        true => Tri.True,
        false => Tri.False,
        null => Tri.Unknown,
    };

    /// <summary>Kleene NOT. Unknown and NotApplicable are unchanged.</summary>
    /// <param name="value">The value.</param>
    public static Tri Not(this Tri value) => value switch
    {
        Tri.True => Tri.False,
        Tri.False => Tri.True,
        _ => value,
    };
}
