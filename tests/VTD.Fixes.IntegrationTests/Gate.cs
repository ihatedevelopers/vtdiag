using System.Security.Principal;
using VTD.Contracts;

namespace VTD.Fixes.IntegrationTests;

/// <summary>
/// The write gate. Tests that change real system state (services, devices, adapters, power, BCD, firewall, Defender,
/// HKLM, real Riot/Vanguard paths) run ONLY with <c>VTD_ALLOW_SYSTEM_WRITES=1</c> in an elevated process: a CI runner, a
/// VM or Windows Sandbox. They are skipped everywhere else, including the maintainer's own PC.
/// </summary>
public static class Gate
{
    /// <summary>The variable that opens the gate.</summary>
    public const string Variable = "VTD_ALLOW_SYSTEM_WRITES";

    /// <summary>True when system writes are allowed in this process.</summary>
    public static bool SystemWrites => string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    /// <summary>True when the test process is elevated.</summary>
    public static bool Elevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Skips unless writes are allowed and the process is elevated.</summary>
    public static void RequireSystemWrites()
    {
        if (!SystemWrites)
        {
            Assert.Skip(Variable + "=1 is not set: system writes run only on CI runners, VMs or Windows Sandbox.");
        }

        if (!Elevated)
        {
            Assert.Skip("System write tests need an elevated process.");
        }
    }

    /// <summary>Skips unless the named variable holds a value (a target chosen for this VM, for example a spare device).</summary>
    /// <param name="variable">Variable.</param>
    public static string RequireTarget(string variable)
    {
        RequireSystemWrites();
        return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : throw Skip(variable);
    }

    /// <summary>The interactive user of this test process as the "player".</summary>
    public static PlayerContext CurrentPlayer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new PlayerContext
        {
            Sid = identity.User!.Value,
            ProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            IsAdmin = Elevated,
        };
    }

    private static Exception Skip(string variable)
    {
        Assert.Skip(variable + " is not set for this machine.");
        return new InvalidOperationException();
    }
}
