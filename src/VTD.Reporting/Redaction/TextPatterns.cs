using System.Text.RegularExpressions;

namespace VTD.Reporting;

/// <summary>
/// The regular expressions behind <see cref="Redactor.RedactText"/> (scrub) and <see cref="Redactor.Sweep"/> (detect).
/// Every pattern is culture-invariant and has a match timeout. They are written to leave real diagnostic data alone:
/// product GUIDs in uninstall ids, driver file names, versions, ULIDs, SHA-256 hex and PnP ids never match.
/// </summary>
internal static partial class TextPatterns
{
    /// <summary>Placeholder written in place of a removed value.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>Placeholder for an identifier value (computer name, user name...).</summary>
    public const string IdPlaceholder = "[id]";

    /// <summary>Profile replacement.</summary>
    public const string UserProfile = "%USERPROFILE%";

    private const RegexOptions Ci = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const RegexOptions Cs = RegexOptions.CultureInvariant;
    private const int TimeoutMs = 2000;

    /// <summary>Profile folder names that do not identify anybody (kept as they are).</summary>
    public static IReadOnlySet<string> NeutralProfileNames { get; } = new HashSet<string>(
        ["Public", "Default", "Default User", "All Users", "defaultuser0"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Profile names that are already placeholders (local-level redaction wrote them).</summary>
    public static IReadOnlySet<string> PlaceholderProfileNames { get; } = new HashSet<string>(
        ["<user>", "%USERNAME%", IdPlaceholder, Redacted],
        StringComparer.OrdinalIgnoreCase);

    /// <summary><c>CommandLine=…</c> up to the end of the line (command lines carry Riot tokens).</summary>
    [GeneratedRegex(@"\b(CommandLine)(\s*[=:]\s*)[^\r\n]*", Ci, TimeoutMs)]
    public static partial Regex CommandLine();

    /// <summary><c>--remoting-auth-token=…</c>, <c>-subject …</c> and friends (flag form; value after <c>=</c> or a space).</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(-{1,2}(?:remoting-auth-token|subject|login-token|auth-token|access-token|riotclient-auth-token))(=|\s+)(""[^""]*""|[^\s""'`]+)", Ci, TimeoutMs)]
    public static partial Regex AuthFlag();

    /// <summary><c>access_token=…</c>, <c>token: …</c>, <c>sessionid=…</c> and friends (key form; the value up to a separator).</summary>
    [GeneratedRegex(@"\b(access_token|refresh_token|id_token|entitlements_token|auth_token|accesstoken|token|secret|client_secret|api_key|apikey|sessionid|session_id|session_token|sessiontoken|cookie)\b(\s*[""']?\s*[=:]\s*[""']?)(""[^""]*""|[^\s""'`&,;]+)", Ci, TimeoutMs)]
    public static partial Regex SecretKeyValue();

    /// <summary><c>password=…</c> (the value runs to the end of the line or a <c>;</c>/<c>&amp;</c>: passwords may contain spaces).</summary>
    [GeneratedRegex(@"\b(password|passwd|pwd)\b(\s*[""']?\s*[=:]\s*)([^\r\n;&]+)", Ci, TimeoutMs)]
    public static partial Regex PasswordValue();

    /// <summary><c>Bearer &lt;token&gt;</c>.</summary>
    [GeneratedRegex(@"\b(Bearer)(\s+)([A-Za-z0-9._~+/=-]{8,})", Ci, TimeoutMs)]
    public static partial Regex Bearer();

    /// <summary><c>Basic|Digest|Negotiate &lt;credentials&gt;</c> (at least 12 base64-ish characters with a digit or <c>=+/</c>, so
    /// "Microsoft Basic Render Driver" stays).</summary>
    [GeneratedRegex(@"\b(Basic|Digest|Negotiate)(\s+)((?=[A-Za-z0-9._~+/=-]*[0-9=+/])[A-Za-z0-9._~+/=-]{12,})", Ci, TimeoutMs)]
    public static partial Regex OtherAuthScheme();

    /// <summary>The Riot Client lockfile shape <c>name:pid:port:password:https</c>.</summary>
    [GeneratedRegex(@"(:\d{2,6}:\d{2,6}:)()([A-Za-z0-9_-]{6,})(?=:https?\b)", Ci, TimeoutMs)]
    public static partial Regex Lockfile();

    /// <summary>An id right next to its key: <c>puuid=&lt;guid&gt;</c>, <c>LoginId: …</c>, <c>UserName=…</c>, <c>MachineId=…</c>.</summary>
    [GeneratedRegex(@"\b(puuid|loginid|login_id|machineid|machine_id|username|user_name|userid|user_id|accountid|account_id|riotid|riot_id|summonerid|summoner_id|hwid|gamename|game_name|tagline|tag_line)\b(\s*[""']?\s*[=:]\s*[""']?)([^\s""'`&,;]+)", Ci, TimeoutMs)]
    public static partial Regex IdNextToKey();

    /// <summary>A network identifier next to its key in free text: <c>SSID=…</c>, <c>LocalIp: …</c>, <c>PeerMac=…</c>.</summary>
    [GeneratedRegex(@"\b(ssid|bssid|mac|macaddress|mac_address|physicaladdress|physical_address|peermac|peer_mac|ip|ipv4|ipv6|ipaddress|ip_address|localip|local_ip|remoteip|remote_ip|peerip|peer_ip|clientip|client_ip|serverip|server_ip|gateway)\b(\s*[""']?\s*[=:]\s*[""']?)([^\s""'`&,;]+)", Ci, TimeoutMs)]
    public static partial Regex NetworkKeyValue();

    /// <summary>A MAC address with <c>:</c> or <c>-</c> separators (consistent).</summary>
    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])[0-9A-Fa-f]{2}([:-])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])", Cs, TimeoutMs)]
    public static partial Regex MacAddress();

    /// <summary>A JWT (three base64url parts, the first starting with <c>eyJ</c>).</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", Cs, TimeoutMs)]
    public static partial Regex Jwt();

    /// <summary>An e-mail address.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", Cs, TimeoutMs)]
    public static partial Regex Email();

    /// <summary>A user or Azure AD SID (<c>S-1-5-21-…</c>, <c>S-1-12-1-…</c>). Well-known SIDs such as <c>S-1-5-18</c> do not match.</summary>
    [GeneratedRegex(@"\bS-1-(?:5-21|12-1)(?:-\d+){2,}\b", Ci, TimeoutMs)]
    public static partial Regex Sid();

    /// <summary>
    /// A profile folder: <c>C:\Users\&lt;name&gt;</c>, <c>c:/Users/&lt;name&gt;</c>, <c>\\?\C:\Users\…</c>,
    /// <c>\Device\HarddiskVolumeN\Users\…</c>, <c>%SystemDrive%\Users\…</c>, UNC <c>\\host\share\Users\…</c>, a bare
    /// <c>\Users\…</c> or <c>..\Users\…</c> (not after a word character, so registry paths such as
    /// <c>…\Account\Users\000001F4</c> stay), and <c>Documents and Settings</c>. The name ends at a character Windows
    /// does not allow in user names (<c>\ / " [ ] : ; | = , + * ? &lt; &gt;</c>) or a quote followed by a space.
    /// </summary>
    [GeneratedRegex(@"(?<root>[A-Za-z]:|\\Device\\HarddiskVolume\d+|%(?:SystemDrive|HOMEDRIVE)%|\\\\[^\\/\s""]+\\[^\\/\s""]+|\\\\[^\\/\s""]+|(?<![A-Za-z0-9_%$]))(?<sep>[\\/]+)(?:Users|Documents and Settings)[\\/]+(?<name><[A-Za-z]{1,16}>|(?:[^\\/\r\n""\[\]:;|=,+*?<>']|'(?![\s)]|$))+)", Ci, TimeoutMs)]
    public static partial Regex ProfilePath();

    /// <summary>The organization in a OneDrive for Business folder name (<c>OneDrive - Contoso</c>).</summary>
    [GeneratedRegex(@"\b(OneDrive - )([^\\/\r\n""]+)", Ci, TimeoutMs)]
    public static partial Regex OneDriveOrg();

    /// <summary>Windows' default random computer names (<c>DESKTOP-4F2K9QH</c>, <c>LAPTOP-…</c>, <c>WIN-…</c>).</summary>
    [GeneratedRegex(@"\b(?:DESKTOP|LAPTOP|WIN)-[A-Z0-9]{6,11}\b", Cs, TimeoutMs)]
    public static partial Regex DefaultComputerName();

    /// <summary>A 12-hex-digit run (MAC address, serial) inside a PnP id segment.</summary>
    [GeneratedRegex(@"[0-9A-Fa-f]{12}", Cs, TimeoutMs)]
    public static partial Regex LongHexRun();

    /// <summary>A PnP enumerator (<c>PCI</c>, <c>USB</c>, <c>SWD</c>...).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,32}$", Cs, TimeoutMs)]
    public static partial Regex SafeEnumerator();

    /// <summary>A PnP device-id segment (<c>VEN_10EC&amp;DEV_8125&amp;SUBSYS_…</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_&{}.#+-]{1,200}$", Cs, TimeoutMs)]
    public static partial Regex SafeDeviceSegment();

    /// <summary>A PnP-instance-id-looking string (<c>ENUM\something</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,32}\\[^\\]+", Cs, TimeoutMs)]
    public static partial Regex LooksLikeDeviceId();

    /// <summary>A power setting id (<c>&lt;guid&gt;/&lt;guid&gt;</c>).</summary>
    [GeneratedRegex(@"^[0-9a-fA-F-]{36}/[0-9a-fA-F-]{36}$", Cs, TimeoutMs)]
    public static partial Regex PowerSettingId();

    /// <summary>A Windows optional feature name.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$", Cs, TimeoutMs)]
    public static partial Regex FeatureName();
}
