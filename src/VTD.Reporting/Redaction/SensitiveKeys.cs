using System.Net;
using VTD.Contracts;

namespace VTD.Reporting;

/// <summary>
/// Map keys that say what their value is (event-data fields, pack-collector items, adapter advanced properties):
/// <c>{"SSID": "…"}</c>, <c>{"Password": "…"}</c>, <c>{"NetworkAddress": "…"}</c>. The redactor treats such a value with
/// the matching <see cref="Sensitivity"/>, and the sweep flags a raw value under such a key. Keys are compared
/// case-insensitively with <c>_</c> and <c>-</c> ignored.
/// </summary>
internal static class SensitiveKeys
{
    private static readonly Dictionary<string, Sensitivity> Keys = Build();

    /// <summary>The sensitivity a value under <paramref name="key"/> has, or null for an ordinary key.</summary>
    /// <param name="key">Map key.</param>
    public static Sensitivity? Classify(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var normalized = new string([.. key.Where(c => c is not '_' and not '-' and not ' ').Select(char.ToLowerInvariant)]);
        return Keys.TryGetValue(normalized, out var s) ? s : null;
    }

    /// <summary>Sweep rule for a raw value under a sensitive key (null when the value is already safe).</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    public static string? SweepRule(string key, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || TextScrubber.IsPlaceholder(trimmed))
        {
            return null;
        }

        return Classify(key) switch
        {
            Sensitivity.Identifier => RedactionSweep.Rules.IdNextToKey,
            Sensitivity.Hashed => trimmed.StartsWith("h:", StringComparison.Ordinal) ? null : RedactionSweep.Rules.NetworkId,
            Sensitivity.MacAddress => trimmed.Count(Uri.IsHexDigit) == 12 && !trimmed.Contains("h:", StringComparison.Ordinal) ? RedactionSweep.Rules.Mac : null,
            Sensitivity.IpAddress => IPAddress.TryParse(trimmed.Split('%')[0], out _) ? RedactionSweep.Rules.NetworkId : null,
            _ => null,
        };
    }

    private static Dictionary<string, Sensitivity> Build()
    {
        var map = new Dictionary<string, Sensitivity>(StringComparer.Ordinal);
        foreach (var k in (string[])
                 [
                     "password", "passwd", "pwd", "puuid", "username", "loginid", "machineid", "accountid", "userid", "usersid", "sid",
                     "accesstoken", "refreshtoken", "idtoken", "authtoken", "entitlementstoken", "token", "secret", "clientsecret",
                     "apikey", "sessiontoken", "cookie", "riotid", "gamename", "tagline", "hwid", "summonerid", "computername",
                     "hostname", "machinename", "domainname", "commandline", "email", "mail",
                 ])
        {
            map[k] = Sensitivity.Identifier;
        }

        foreach (var k in (string[])["ssid", "wifiname", "profilename", "wlanprofile"])
        {
            map[k] = Sensitivity.Hashed;
        }

        foreach (var k in (string[])["mac", "macaddress", "physicaladdress", "peermac", "networkaddress", "bssid", "clientmac"])
        {
            map[k] = Sensitivity.MacAddress;
        }

        foreach (var k in (string[])["ip", "ipv4", "ipv6", "ipaddress", "localip", "remoteip", "peerip", "clientip", "serverip", "localaddress", "remoteaddress", "sourceaddress", "destaddress", "gateway", "dns", "dnsserver"])
        {
            map[k] = Sensitivity.IpAddress;
        }

        return map;
    }
}
