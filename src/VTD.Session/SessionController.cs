using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;

namespace VTD.Session;

/// <summary>What the controller needs from the outside world: read-only lookups before a transition, and effect execution after it.</summary>
public interface ISessionEnvironment
{
    /// <summary>Looks up the read-only facts the machine needs for this event (no system writes).</summary>
    /// <param name="state">Current state.</param>
    /// <param name="sessionEvent">Event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<MachineContext> PrepareAsync(SessionState state, SessionEvent sessionEvent, CancellationToken cancellationToken);

    /// <summary>Runs one effect and returns the events it produced (already stamped <see cref="EventOrigin.Engine"/>).
    /// Failures are turned into result events by the environment; an exception means "nothing to report".</summary>
    /// <param name="state">State at the time the effect starts.</param>
    /// <param name="effect">Effect.</param>
    /// <param name="cancellationToken">Engine lifetime.</param>
    Task<IReadOnlyList<SessionEvent>> RunEffectAsync(SessionState state, SessionEffect effect, CancellationToken cancellationToken);
}

/// <summary>Thrown when the machine or the policy refuses an event; <see cref="Exception.Message"/> is the machine code.</summary>
public sealed class SessionRejectedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public SessionRejectedException()
        : base(SessionCodes.InvalidPhase)
    {
        Code = SessionCodes.InvalidPhase;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Machine code.</param>
    public SessionRejectedException(string code)
        : base(code)
    {
        Code = code;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Machine code.</param>
    /// <param name="innerException">Cause.</param>
    public SessionRejectedException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    /// <summary>Machine code.</summary>
    public string Code { get; }
}

/// <summary>Controller options.</summary>
public sealed record SessionControllerOptions
{
    /// <summary>Run long effects inline (deterministic tests) instead of in the background.</summary>
    public bool InlineEffects { get; init; }
}

/// <summary>
/// Owns the session (A4): checks the verified origin against <see cref="CommandPolicy"/>, computes the pure transition,
/// PERSISTS the new state (revision + 1) BEFORE running any effect, then runs the effects and feeds their results back
/// as events. Transitions are serialized; effects run outside the lock.
/// </summary>
public sealed class SessionController : ISessionController, IAsyncDisposable
{
    private readonly IStateStore<SessionState> _store;
    private readonly ISessionEnvironment _environment;
    private readonly TimeProvider _clock;
    private readonly SessionControllerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _background = [];
    private SessionState _current;

    /// <summary>Creates the controller around an already loaded (and persisted) state.</summary>
    /// <param name="initial">Current state.</param>
    /// <param name="store">State store (atomic replace + .bak).</param>
    /// <param name="environment">Lookups and effects.</param>
    /// <param name="clock">Engine clock.</param>
    /// <param name="options">Options.</param>
    public SessionController(SessionState initial, IStateStore<SessionState> store, ISessionEnvironment environment, TimeProvider clock, SessionControllerOptions? options = null)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new SessionControllerOptions();
    }

    /// <inheritdoc />
    public event EventHandler<SessionState>? Changed;

    /// <inheritdoc />
    public SessionState Current => Volatile.Read(ref _current);

    /// <summary>Unexpected failures of background effects (the effect produced no event), for logging.</summary>
    public event EventHandler<Exception>? EffectFaulted;

    /// <summary>
    /// Loads the state (primary, .bak, or rebuilt from the journal by the store) or creates a new session, and persists a
    /// new one before anything can act on it.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="createNew">Factory for a new session (revision 0).</param>
    public static (SessionState State, StateLoadResult<SessionState> Load) LoadOrCreate(IStateStore<SessionState> store, Func<SessionState> createNew)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(createNew);
        var load = store.Load();
        if (load.State is { } existing)
        {
            return (existing, load);
        }

        var fresh = createNew();
        fresh = fresh with { Revision = fresh.Revision + 1 };
        store.Save(fresh);
        return (fresh, load);
    }

    /// <inheritdoc />
    public async Task<SessionState> HandleAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        CheckOrigin(sessionEvent);
        IReadOnlyList<SessionEffect> effects;
        SessionState persisted;
        SessionState? changed = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _current;
            if (sessionEvent is SessionCommand && !SessionMachine.IsClosed(state.Phase) && _clock.GetUtcNow() > state.ExpiresUtc)
            {
                // The session expired: close it first (persisted), then refuse the command.
                var expired = SessionMachine.Next(state, new SessionExpired { EventId = sessionEvent.EventId, Utc = _clock.GetUtcNow(), Origin = EventOrigin.Engine }, new MachineContext { Now = _clock.GetUtcNow() });
                var expiryEffects = Persist(state, expired);
                if (!ReferenceEquals(_current, state))
                {
                    changed = _current;
                }

                RunAfterRejection(expiryEffects);
                throw new SessionRejectedException(SessionCodes.Expired);
            }

            var context = await _environment.PrepareAsync(state, sessionEvent, cancellationToken).ConfigureAwait(false);
            var transition = SessionMachine.Next(state, sessionEvent, context);
            if (transition.Rejection is { } code)
            {
                throw new SessionRejectedException(code);
            }

            effects = Persist(state, transition);
            persisted = _current;
            if (!ReferenceEquals(persisted, state))
            {
                changed = persisted;
            }
        }
        finally
        {
            _gate.Release();
            if (changed is not null)
            {
                Changed?.Invoke(this, changed);
            }
        }

        foreach (var effect in effects)
        {
            if (effect.LongRunning && !_options.InlineEffects)
            {
                Track(Task.Run(() => RunAsync(persisted, effect), CancellationToken.None));
            }
            else
            {
                await RunAsync(persisted, effect).ConfigureAwait(false);
            }
        }

        return _current;
    }

    /// <summary>Feeds an event from a timer or a service callback (deadman expiry, restore point) without awaiting it.</summary>
    /// <param name="sessionEvent">Event stamped <see cref="EventOrigin.Engine"/>.</param>
    public void Post(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        Track(Task.Run(() => HandleQuietlyAsync(sessionEvent), CancellationToken.None));
    }

    /// <summary>Waits until no background effect or posted event is running (tests, orderly shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_background)
            {
                _background.RemoveAll(t => t.IsCompleted);
                pending = [.. _background];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <summary>True while a background effect runs (the engine does not idle-exit then).</summary>
    public bool Busy
    {
        get
        {
            lock (_background)
            {
                return _background.Exists(t => !t.IsCompleted);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await WhenIdleAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SessionRejectedException)
        {
            // Shutting down.
        }

        _lifetime.Dispose();
        _gate.Dispose();
    }

    private static void CheckOrigin(SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case { Origin: EventOrigin.Unspecified }:
                throw new SessionRejectedException(SessionCodes.Unstamped);
            case SessionCommand command when !CommandPolicy.IsAllowed(command, command.Origin):
                throw new SessionRejectedException(SessionCodes.NotAllowed);
            case EngineEvent when sessionEvent.Origin is not (EventOrigin.Engine or EventOrigin.Resume):
                throw new SessionRejectedException(SessionCodes.NotAllowed);
            default:
                break;
        }
    }

    // Called under the gate. Persist BEFORE any effect runs; a store failure throws and no effect runs.
    private IReadOnlyList<SessionEffect> Persist(SessionState state, Transition transition)
    {
        if (!transition.Changed)
        {
            return transition.Effects;
        }

        var next = transition.State with { Revision = state.Revision + 1, UpdatedUtc = _clock.GetUtcNow() };
        _store.Save(next);
        Volatile.Write(ref _current, next);
        return transition.Effects;
    }

    private void RunAfterRejection(IReadOnlyList<SessionEffect> effects)
    {
        if (effects.Count == 0)
        {
            return;
        }

        var state = _current;
        Track(Task.Run(
            async () =>
            {
                foreach (var effect in effects)
                {
                    await RunAsync(state, effect).ConfigureAwait(false);
                }
            },
            CancellationToken.None));
    }

    private async Task RunAsync(SessionState state, SessionEffect effect)
    {
        IReadOnlyList<SessionEvent> events;
        try
        {
            events = await _environment.RunEffectAsync(state, effect, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            EffectFaulted?.Invoke(this, ex);
            if (_options.InlineEffects)
            {
                throw;
            }

            return;
        }

        foreach (var next in events)
        {
            await HandleQuietlyAsync(next).ConfigureAwait(false);
        }
    }

    private async Task HandleQuietlyAsync(SessionEvent sessionEvent)
    {
        try
        {
            await HandleAsync(sessionEvent, _lifetime.Token).ConfigureAwait(false);
        }
        catch (SessionRejectedException)
        {
            // An effect result that no longer applies (the player moved on): ignored by design.
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex) when (!_options.InlineEffects && ex is not OutOfMemoryException)
        {
            EffectFaulted?.Invoke(this, ex);
        }
    }

    private void Track(Task task)
    {
        lock (_background)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }
}
