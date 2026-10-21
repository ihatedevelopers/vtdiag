using VTD.Contracts;

namespace VTD.Session;

/// <summary>Boot comparisons for reboot-resume ("did MY restart happen, or was it Fast Startup / no restart at all?").</summary>
public static class BootIdentities
{
    /// <summary>
    /// Two boot-time readings of the same boot differ by rounding (about a second); a resume from hybrid shutdown keeps the
    /// original boot time, while a real restart (shutdown, firmware, boot) moves it by far more than this.
    /// </summary>
    public static TimeSpan BootTimeTolerance { get; } = TimeSpan.FromSeconds(10);

    /// <summary>True when both readings describe the same running kernel session.</summary>
    /// <param name="a">One reading.</param>
    /// <param name="b">Another reading.</param>
    public static bool SameBoot(BootIdentity a, BootIdentity b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.BootId == b.BootId || (a.LastBootUpTimeUtc - b.LastBootUpTimeUtc).Duration() <= BootTimeTolerance;
    }

    /// <summary>
    /// True when <paramref name="now"/> is a real restart after <paramref name="atRequest"/>: a different boot that is not
    /// a resume from hybrid shutdown (Fast Startup keeps the kernel session, so a change that needs a restart did not
    /// take effect).
    /// </summary>
    /// <param name="atRequest">Boot when the restart was requested.</param>
    /// <param name="now">Boot now.</param>
    public static bool IsRealRestart(BootIdentity atRequest, BootIdentity now)
    {
        ArgumentNullException.ThrowIfNull(atRequest);
        ArgumentNullException.ThrowIfNull(now);
        return !SameBoot(atRequest, now) && now.Kind != BootKind.FastStartupResume;
    }
}
