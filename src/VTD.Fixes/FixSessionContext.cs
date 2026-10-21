using VTD.Contracts;

namespace VTD.Fixes;

/// <summary>
/// What the primitives, the policy and the runner need to know about the current session. A4's engine supplies it from
/// the session state (never from a client claim): the player comes from the pipe client's token, the Vanguard gate from
/// the player's Pre-Check answer AND the analysis of the vgc log metadata.
/// </summary>
public interface IFixSessionContext
{
    /// <summary>Current session id (journal file name).</summary>
    string SessionId { get; }

    /// <summary>The interactive player; null when unknown (every HKCU / per-user primitive then refuses).</summary>
    PlayerContext? Player { get; }

    /// <summary>The engine runs elevated (admin-only fixes are blocked otherwise).</summary>
    bool IsElevated { get; }

    /// <summary>
    /// The Pre-Check gate for <c>vgc → Automatic</c> (DESIGN.md "Vanguard Pre-Check"): True only when the player confirmed
    /// Pre-Check is OFF and the vgc log metadata shows a failure (the analysis signal <c>vg.vgc_automatic_needed</c> is
    /// True for this session). Anything else, Unknown included, keeps vgc untouched.
    /// </summary>
    Tri VgcAutomaticConfirmed { get; }

    /// <summary>Current boot id (journal audit), when known.</summary>
    uint? BootId { get; }

    /// <summary>App version written into journal records.</summary>
    string AppVersion { get; }
}

/// <summary>A settable <see cref="IFixSessionContext"/> (the engine updates it as the session moves; tests script it).</summary>
public sealed class FixSessionContext : IFixSessionContext
{
    private volatile string _sessionId;
    private volatile PlayerContext? _player;
    private volatile bool _elevated;
    private int _vgcGate;
    private long _bootId = -1;

    /// <summary>Creates the context.</summary>
    /// <param name="sessionId">Session id.</param>
    /// <param name="appVersion">App version.</param>
    public FixSessionContext(string sessionId, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        _sessionId = sessionId;
        AppVersion = appVersion;
    }

    /// <inheritdoc />
    public string SessionId
    {
        get => _sessionId;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _sessionId = value;
        }
    }

    /// <inheritdoc />
    public PlayerContext? Player
    {
        get => _player;
        set => _player = value;
    }

    /// <inheritdoc />
    public bool IsElevated
    {
        get => _elevated;
        set => _elevated = value;
    }

    /// <inheritdoc />
    public Tri VgcAutomaticConfirmed
    {
        get => (Tri)Volatile.Read(ref _vgcGate);
        set => Volatile.Write(ref _vgcGate, (int)value);
    }

    /// <inheritdoc />
    public uint? BootId
    {
        get
        {
            var v = Interlocked.Read(ref _bootId);
            return v < 0 ? null : (uint)v;
        }

        set => Interlocked.Exchange(ref _bootId, value is null ? -1 : value.Value);
    }

    /// <inheritdoc />
    public string AppVersion { get; }
}
