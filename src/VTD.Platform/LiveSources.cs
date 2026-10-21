using System.Security.Principal;
using VTD.Contracts;
using VTD.Platform.Events;
using VTD.Platform.IO;
using VTD.Platform.Native;
using VTD.Platform.Net;
using VTD.Platform.Registry;
using VTD.Platform.Sources;
using VTD.Platform.Wmi;

namespace VTD.Platform;

/// <summary>Factory for the live (real PC) sources.</summary>
public static class LiveSources
{
    /// <summary>Creates the live sources.</summary>
    /// <param name="clock">Time source (used by the clock-skew probe).</param>
    public static SystemSources Create(TimeProvider clock) => new()
    {
        Registry = new RegistrySource(),
        Wmi = new WmiSource(),
        WmiMethods = new ReadOnlyWmiMethods(),
        Files = new FileSource(),
        Events = new EventLogSource(),
        System = new SystemInfoSource(),
        Probes = new NetworkProbeSource(clock),
    };
}

/// <summary>Builds <see cref="PlayerContext"/> values.</summary>
public static class PlayerContexts
{
    /// <summary>
    /// The account this process runs as. Only correct when the process runs as the interactive player (CLI, tests, the
    /// unelevated limited engine). The elevated engine must take the player from the pipe client's token (A4).
    /// </summary>
    public static PlayerContext FromCurrentProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current token has no user SID.");
        var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator)
                      || identity.Claims.Any(c => string.Equals(c.Value, "S-1-5-32-544", StringComparison.Ordinal));
        return new PlayerContext
        {
            Sid = sid,
            ProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            IsAdmin = isAdmin,
        };
    }
}
