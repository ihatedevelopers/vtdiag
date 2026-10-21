using VTD.Contracts.Journaling;
using VTD.Testing.Fakes;

namespace VTD.Fixes.Tests.Fakes;

/// <summary>The engine "dies" at a chosen journal write (the record is not stored and nothing after it runs).</summary>
public sealed class SimulatedCrashException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SimulatedCrashException()
        : base("simulated crash")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Inner.</param>
    public SimulatedCrashException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// An <see cref="IJournal"/> over <see cref="InMemoryJournal"/> that can fail (disk full) or crash at the n-th append,
/// and calls an observer before each record is stored (to check write-ahead ordering against the system state).
/// </summary>
public sealed class ScriptedJournal : IJournal
{
    private int _appends;

    /// <summary>The store.</summary>
    public InMemoryJournal Inner { get; } = new();

    /// <summary>1-based append number that throws <see cref="JournalWriteException"/> (not stored).</summary>
    public int? FailAt { get; set; }

    /// <summary>1-based append number that throws <see cref="SimulatedCrashException"/> (not stored).</summary>
    public int? CrashAt { get; set; }

    /// <summary>Fail every append from <see cref="FailAt"/> on.</summary>
    public bool FailFromThereOn { get; set; }

    /// <summary>Called with each record before it is stored.</summary>
    public Action<JournalRecord>? BeforeAppend { get; set; }

    /// <summary>Appends seen (stored or not).</summary>
    public int Appends => _appends;

    /// <inheritdoc />
    public ValueTask<JournalRecord> AppendAsync(JournalRecord record, CancellationToken cancellationToken)
    {
        var n = ++_appends;
        if (CrashAt == n)
        {
            throw new SimulatedCrashException();
        }

        if (FailAt is { } f && (n == f || (FailFromThereOn && n > f)))
        {
            throw new JournalWriteException("disk full");
        }

        BeforeAppend?.Invoke(record);
        return Inner.AppendAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<JournalRecord> ReadAsync(string sessionId, CancellationToken cancellationToken) => Inner.ReadAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<JournalRecord> ReadAllAsync(CancellationToken cancellationToken) => Inner.ReadAllAsync(cancellationToken);

    /// <summary>Stops failing and crashing (the engine restarted).</summary>
    public void Heal()
    {
        FailAt = null;
        CrashAt = null;
        BeforeAppend = null;
    }

    /// <summary>Records as a list.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<List<JournalRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<JournalRecord>();
        await foreach (var r in Inner.ReadAllAsync(cancellationToken))
        {
            list.Add(r);
        }

        return list;
    }
}
