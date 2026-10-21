using System.Text.RegularExpressions;

namespace VTD.Collectors.Logs;

/// <summary>
/// Parse-time redaction for game and client logs (DESIGN.md: "redaction is applied at parse time"). A line that carries
/// any secret marker is discarded BEFORE pattern matching; surviving captured groups are scrubbed of profile paths,
/// GUIDs (PUUIDs are GUID-shaped), IPs, e-mail addresses and long hex/base64 tokens. Raw lines never leave the
/// file source: only <c>patternId|utc|groups</c> records do.
/// </summary>
public static partial class LogSafety
{
    /// <summary>Case-insensitive markers that make a whole line unusable.</summary>
    internal static readonly string[] DenyMarkers =
    [
        "remoting-auth-token", "auth-token", "subject", "commandline", "machineid", "loginid", "username", "user_name", "puuid",
        "bearer", "access_token", "id_token", "refresh_token", "entitlement", "authorization", "cookie", "password", "passwd",
        "session_token", "sessiontoken", "summoner", "gamename", "game_name", "tagline", "tag_line", "accountid", "account_id",
        "playerid", "player_id", "email", "e-mail", "token=", "token\":", "ssid", "machine_id", "hwid", "-user=",
    ];

    /// <summary>True when the line must be dropped.</summary>
    /// <param name="line">Raw line.</param>
    public static bool IsSensitive(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (var marker in DenyMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return Jwt().IsMatch(line) || Email().IsMatch(line);
    }

    /// <summary>Scrubs a captured group (never a whole raw line).</summary>
    /// <param name="value">Value.</param>
    /// <param name="maxLength">Max length kept.</param>
    /// <param name="keepDottedNumbers">Keep a.b.c.d numbers (version strings such as <c>10.0.26200.9711</c>).</param>
    public static string Scrub(string value, int maxLength = 128, bool keepDottedNumbers = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = ProfilePath().Replace(value, "%USERPROFILE%");
        s = Guid().Replace(s, "<guid>");
        if (!keepDottedNumbers)
        {
            s = Ipv4().Replace(s, "<ip>");
        }

        s = Email().Replace(s, "<email>");
        s = LongToken().Replace(s, "<token>");
        s = s.Replace('|', '/').Replace('\t', ' ');
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?i)[a-z]:[\\/]+users[\\/]+[^\\/""'<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{32,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex LongToken();
}
