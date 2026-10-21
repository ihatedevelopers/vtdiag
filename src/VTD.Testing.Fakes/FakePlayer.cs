using VTD.Contracts;
using VTD.Contracts.Sessions;

namespace VTD.Testing.Fakes;

/// <summary>A fake interactive player and the fake restore-point service.</summary>
public static class FakePlayer
{
    /// <summary>A standard fake player (already redacted-looking profile path; no real user data).</summary>
    public static PlayerContext Default { get; } = new()
    {
        Sid = "S-1-5-21-1000000000-2000000000-3000000000-1001",
        ProfilePath = "C:\\Users\\<user>",
        LocalAppData = "C:\\Users\\<user>\\AppData\\Local",
        RoamingAppData = "C:\\Users\\<user>\\AppData\\Roaming",
        IsAdmin = true,
    };
}

/// <summary><see cref="IRestorePointService"/> that "creates" a restore point once per session, or reports a scripted status.</summary>
/// <param name="clock">Clock.</param>
public sealed class FakeRestorePointService(TimeProvider clock) : IRestorePointService
{
    private readonly Dictionary<string, RestorePointInfo> _bySession = new(StringComparer.Ordinal);

    /// <summary>When set, every call reports this status (for example SystemProtectionOff or Failed).</summary>
    public RestorePointStatus? ForcedStatus { get; set; }

    /// <summary>Calls so far.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<RestorePointInfo> EnsureAsync(string sessionId, string reasonKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (ForcedStatus is { } forced)
        {
            return Task.FromResult(new RestorePointInfo { Status = forced, MessageKey = "restorepoint." + forced.ToString() });
        }

        if (!_bySession.TryGetValue(sessionId, out var info))
        {
            info = new RestorePointInfo { Status = RestorePointStatus.Created, SequenceNumber = 100 + _bySession.Count, CreatedUtc = clock.GetUtcNow() };
            _bySession[sessionId] = info;
        }

        return Task.FromResult(info);
    }
}
