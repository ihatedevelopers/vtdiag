using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using VTD.Contracts;

namespace VTD.Engine.Platform;

/// <summary>
/// The interactive player of the engine's own logon session, established by the engine (never a client claim). The
/// elevated engine may run as a different account (standard user + separate admin), so the player is the user of the
/// Windows session the engine runs in, and every per-user path comes from that user's profile, never the engine's.
/// </summary>
public static class InteractiveUser
{
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private static readonly IntPtr CurrentServer = IntPtr.Zero;
    private const int CurrentSession = -1;

    /// <summary>SID of the user signed in to the engine's Windows session, or null.</summary>
    public static string? SessionUserSid()
    {
        var user = Query(WtsUserName);
        var domain = Query(WtsDomainName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        try
        {
            var account = new NTAccount(string.IsNullOrEmpty(domain) ? user : domain + "\\" + user);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>SID of the account this process runs as.</summary>
    public static string ProcessUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The process token has no user SID.");
    }

    /// <summary>The player's context from their SID: profile from <c>ProfileList</c>, AppData folders from their hive.</summary>
    /// <param name="sid">Player SID.</param>
    /// <param name="isAdmin">The player is an administrator.</param>
    public static PlayerContext ForSid(string sid, bool isAdmin)
    {
        ArgumentNullException.ThrowIfNull(sid);
        string? profile = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
        {
            if (key?.GetValue("ProfileImagePath", null, RegistryValueOptions.None) is string p)
            {
                profile = p;
            }
        }

        profile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string Folder(string valueName, string fallback)
        {
            using var shell = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (shell?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw && raw.Length > 0)
            {
                return raw.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
            }

            return Path.Combine(profile, fallback);
        }

        return new PlayerContext
        {
            Sid = sid,
            ProfilePath = profile,
            LocalAppData = Folder("Local AppData", @"AppData\Local"),
            RoamingAppData = Folder("AppData", @"AppData\Roaming"),
            IsAdmin = isAdmin,
        };
    }

    /// <summary>The user name part of a profile path (to scrub it from share exports).</summary>
    /// <param name="player">Player.</param>
    public static string? UserNameOf(PlayerContext player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var name = Path.GetFileName(player.ProfilePath.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? Query(int infoClass)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
