using VTD.Contracts;
using VTD.Contracts.Ipc;

namespace VTD.App.Services;

/// <summary>
/// The UI's log. The UI layer cannot write files, so entries are handed to the engine (<see cref="IEngineRpc.SubmitClientLogAsync"/>),
/// which redacts them at write time and rolls them into the same size-capped JSON-lines log as its own. Until the engine is
/// connected, entries wait in a small buffer (the start-up events of a failed launch are exactly the ones worth keeping:
/// they are sent as soon as any engine connects). Never throws, never blocks the UI.
/// </summary>
public sealed class AppLog
{
    private const int MaxPending = 200;

    private readonly object _gate = new();
    private readonly List<ClientLogEntry> _pending = [];
    private readonly TimeProvider _clock;
    private IEngineRpc? _engine;
    private volatile bool _debug;

    /// <summary>Creates the log.</summary>
    /// <param name="clock">Clock.</param>
    public AppLog(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>A log that keeps nothing (tests).</summary>
    public static AppLog Null { get; } = new(TimeProvider.System) { Enabled = false };

    /// <summary>False discards everything.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Debug mode: Debug-level entries are kept (and the engine raises its own verbosity too).</summary>
    public bool DebugMode
    {
        get => _debug;
        set => _debug = value;
    }

    /// <summary>Writes an entry.</summary>
    /// <param name="level">Level.</param>
    /// <param name="evt">Event name (<c>letters, digits, '.', '_', '-'</c>).</param>
    /// <param name="message">Message.</param>
    /// <param name="exception">Exception (type, message and stack trace go to the engine, which scrubs them).</param>
    public void Write(ClientLogLevel level, string evt, string? message = null, Exception? exception = null)
    {
        if (!Enabled || (level == ClientLogLevel.Debug && !_debug))
        {
            return;
        }

        var entry = new ClientLogEntry
        {
            Utc = _clock.GetUtcNow(),
            Level = level,
            Event = evt,
            Message = message ?? exception?.GetType().Name,
            Detail = exception?.ToString(),
        };
        IEngineRpc? engine;
        lock (_gate)
        {
            engine = _engine;
            if (engine is null)
            {
                if (_pending.Count >= MaxPending)
                {
                    _pending.RemoveAt(0);
                }

                _pending.Add(entry);
                return;
            }
        }

        _ = SendAsync(engine, [entry]);
    }

    /// <summary>Info.</summary>
    /// <param name="evt">Event.</param>
    /// <param name="message">Message.</param>
    public void Info(string evt, string? message = null) => Write(ClientLogLevel.Info, evt, message);

    /// <summary>Debug (kept only in debug mode).</summary>
    /// <param name="evt">Event.</param>
    /// <param name="message">Message.</param>
    public void Debug(string evt, string? message = null) => Write(ClientLogLevel.Debug, evt, message);

    /// <summary>Warning.</summary>
    /// <param name="evt">Event.</param>
    /// <param name="message">Message.</param>
    public void Warn(string evt, string? message = null) => Write(ClientLogLevel.Warning, evt, message);

    /// <summary>Error with an exception.</summary>
    /// <param name="evt">Event.</param>
    /// <param name="exception">Exception.</param>
    public void Error(string evt, Exception exception) => Write(ClientLogLevel.Error, evt, null, exception);

    /// <summary>Connects the log to an engine and sends what waited.</summary>
    /// <param name="engine">Engine.</param>
    public async Task AttachAsync(IEngineRpc engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        List<ClientLogEntry> waiting;
        lock (_gate)
        {
            _engine = engine;
            waiting = [.. _pending];
            _pending.Clear();
        }

        if (waiting.Count > 0)
        {
            await SendAsync(engine, waiting).ConfigureAwait(true);
        }
    }

    /// <summary>The entries still waiting for an engine (tests).</summary>
    public IReadOnlyList<ClientLogEntry> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
            }
        }
    }

    private static async Task SendAsync(IEngineRpc engine, IReadOnlyList<ClientLogEntry> entries)
    {
        try
        {
            await engine.SubmitClientLogAsync(entries, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Logging is best effort: a dead pipe must not become a second error.
        }
    }
}
