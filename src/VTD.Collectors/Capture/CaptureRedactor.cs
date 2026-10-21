using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VTD.Contracts.Scanning;
using VTD.Platform.Capture;

namespace VTD.Collectors.Capture;

/// <summary>Who the capture must not identify.</summary>
public sealed record CaptureIdentity
{
    /// <summary>Player SID.</summary>
    public required string Sid { get; init; }

    /// <summary>Profile folder (for example <c>C:\Users\Name</c>).</summary>
    public required string ProfilePath { get; init; }

    /// <summary>Windows user name.</summary>
    public required string UserName { get; init; }

    /// <summary>Computer name.</summary>
    public required string MachineName { get; init; }

    /// <summary>Salt for the stable replacement hashes.</summary>
    public byte[] Salt { get; init; } = RandomNumberGenerator.GetBytes(16);
}

/// <summary>
/// Makes a raw capture safe to check in or share with a developer (local level, consistent across keys and payloads so
/// the replay still works): the player SID becomes a fake SID, the profile folder becomes <c>&lt;user&gt;</c>, the user and
/// computer names disappear, device-instance serial tails, MAC addresses, IP addresses, SSIDs and Riot account folder ids are
/// replaced with stable salted hashes, and registry values that carry licence or owner data are removed.
/// </summary>
public sealed partial class CaptureRedactor
{
    /// <summary>The SID that replaces the player's.</summary>
    public const string FakeSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";

    /// <summary>Registry value names never kept in a capture.</summary>
    public static IReadOnlyList<string> DroppedRegistryValues { get; } =
    [
        "DigitalProductId", "DigitalProductId4", "ProductId", "RegisteredOwner", "RegisteredOrganization", "BackupProductKeyDefault",
        "LastLoggedOnUser", "LastLoggedOnSAMUser", "LastLoggedOnDisplayName", "LastLoggedOnUserSID", "DefaultUserName", "DefaultDomainName",
        "NetworkAddress", "MachineGuid", "ComputerName", "Hostname", "NV Hostname", "Domain", "NV Domain", "Dhcpv6DUID", "DhcpIPAddress",
        "IPAddress", "DhcpServer", "DhcpDefaultGateway", "DhcpNameServer", "NameServer", "DefaultGateway", "DhcpSubnetMask", "SubnetMask",
        "OneDrive", "OneDriveConsumer", "OneDriveCommercial", "USERNAME", "USERDOMAIN", "LOGONSERVER", "COMPUTERNAME",
    ];

    /// <summary>Registry values whose data is a command line: only the executable is kept.</summary>
    public static IReadOnlyList<string> MaskedCommandValues { get; } = ["UninstallString", "QuietUninstallString", "ModifyPath", "Debugger", "ImagePath", "FailureCommand"];

    /// <summary>Environment variables kept in a capture (everything else in an Environment key is dropped; Path keeps only its length).</summary>
    public static IReadOnlyList<string> KeptEnvironmentValues { get; } = ["TEMP", "TMP", "ComSpec", "Path", "windir", "PATHEXT", "__COMPAT_LAYER", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"];

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly CaptureIdentity _id;
    private readonly string _userFolder;
    private readonly Dictionary<string, string> _ips = new(StringComparer.Ordinal);

    /// <summary>Creates a redactor.</summary>
    /// <param name="identity">Identity.</param>
    public CaptureRedactor(CaptureIdentity identity)
    {
        _id = identity ?? throw new ArgumentNullException(nameof(identity));
        _userFolder = Path.GetFileName(identity.ProfilePath.TrimEnd('\\'));
    }

    /// <summary>Redacts a whole capture.</summary>
    /// <param name="entries">Entries.</param>
    public IReadOnlyList<RawCaptureEntry> Redact(IEnumerable<RawCaptureEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Select(e => e with { Source = RedactSource(e.Source), Payload = RedactPayload(e.Source, e.Payload) })
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Redacts one free string (paths, ids, names).</summary>
    /// <param name="value">Value.</param>
    public string RedactString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = value.Replace(_id.Sid, FakeSid, StringComparison.OrdinalIgnoreCase);
        s = ProfileRoot().Replace(s, m => string.Equals(m.Groups[3].Value, "Public", StringComparison.OrdinalIgnoreCase) || string.Equals(m.Groups[3].Value, "Default", StringComparison.OrdinalIgnoreCase)
            ? m.Value
            : m.Groups[1].Value + "Users" + m.Groups[2].Value + "<user>");
        if (_userFolder.Length >= 3)
        {
            s = s.Replace(_userFolder, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        if (_id.UserName.Length >= 3)
        {
            s = s.Replace(_id.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        if (_id.MachineName.Length >= 3)
        {
            s = s.Replace(_id.MachineName, "VTD-PC", StringComparison.OrdinalIgnoreCase);
        }

        s = Email().Replace(s, "<email>");
        s = Mac().Replace(s, m => "02:00:00:" + Hash(m.Value, 3, ':'));
        s = DeviceTail().Replace(s, m => IsLocationTail(m.Groups[2].Value) || m.Groups[2].Value.Length < 8 ? m.Value : m.Groups[1].Value + "R" + Hash(m.Groups[2].Value, 6, null));
        s = AccountFolder().Replace(s, m => HashGuid(m.Groups[1].Value) + m.Groups[2].Value);
        return s;
    }

    private static bool IsCommandName(string name)
        => name.EndsWith("String", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Command", StringComparison.OrdinalIgnoreCase);

    private string RedactSource(string source)
    {
        var s = RedactString(source);
        if (s.StartsWith("probe:ping:", StringComparison.Ordinal))
        {
            var bar = s.IndexOf('|', StringComparison.Ordinal);
            if (bar > 11)
            {
                s = "probe:ping:" + Ip(s[11..bar]) + s[bar..];
            }
        }

        return s;
    }

    private string RedactPayload(string source, string payload)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return RedactString(payload);
        }

        if (root is null)
        {
            return payload;
        }

        if (source.StartsWith("registry:", StringComparison.Ordinal) && root["values"] is JsonArray values)
        {
            var runKey = RunKey().IsMatch(source);
            var envKey = source.EndsWith(@"\Environment", StringComparison.OrdinalIgnoreCase);
            var uninstallKey = source.Contains(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);
            foreach (var v in values.ToList())
            {
                var name = v?["name"]?.GetValue<string>();
                if (name is not null && (DroppedRegistryValues.Contains(name, StringComparer.OrdinalIgnoreCase) || (envKey && !KeptEnvironmentValues.Contains(name, StringComparer.OrdinalIgnoreCase))))
                {
                    values.Remove(v);
                }
                else if (envKey && v is JsonObject p && string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
                {
                    p["data"] = new string('.', p["data"]?.GetValue<string>().Length ?? 0);
                }
                else if (v is JsonObject o && name is not null && (MaskedCommandValues.Contains(name, StringComparer.OrdinalIgnoreCase) || runKey || (uninstallKey && IsCommandName(name))))
                {
                    // Command lines can carry arguments (tokens); keep only the executable, which is all collectors use.
                    var data = o["data"]?.GetValue<string>();
                    o["data"] = data is null ? null : Core.PathText.Executable(data) ?? "<cmd>";
                }
            }
        }

        if (string.Equals(source, "sys:network", StringComparison.Ordinal) && root["interfaces"] is JsonArray nics)
        {
            foreach (var nic in nics.OfType<JsonObject>())
            {
                foreach (var field in new[] { "ipv4", "dns", "gateways" })
                {
                    if (nic[field] is JsonArray arr)
                    {
                        var mapped = arr.Select(x => (JsonNode?)JsonValue.Create(Ip(x?.GetValue<string>() ?? string.Empty))).ToArray();
                        nic[field] = new JsonArray(mapped);
                    }
                }
            }
        }

        if (string.Equals(source, "sys:wlan", StringComparison.Ordinal) && root["interfaces"] is JsonArray wlans)
        {
            foreach (var w in wlans.OfType<JsonObject>().Where(w => w["ssid"] is not null))
            {
                w["ssid"] = "<ssid>";
            }
        }

        if (string.Equals(source, CaptureFormat.MetaSource, StringComparison.Ordinal))
        {
            root["playerSid"] = FakeSid;
        }

        Walk(root);
        return root.ToJsonString(RelaxedJson);
    }

    private void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        obj[key] = RedactString(s);
                    }
                    else if (child is not null)
                    {
                        Walk(child);
                    }
                }

                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        arr[i] = RedactString(s);
                    }
                    else if (arr[i] is { } child)
                    {
                        Walk(child);
                    }
                }

                break;
        }
    }

    private string Ip(string ip)
    {
        if (ip.Length == 0 || ip is "1.1.1.1" or "8.8.8.8" or "127.0.0.1" || ip.StartsWith("127.", StringComparison.Ordinal))
        {
            return ip;
        }

        if (!_ips.TryGetValue(ip, out var fake))
        {
            var n = _ips.Count + 1;
            fake = ip.Contains(':', StringComparison.Ordinal) ? "fd00::" + n.ToString("x", System.Globalization.CultureInfo.InvariantCulture) : "10.77.0." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ips[ip] = fake;
        }

        return fake;
    }

    private string Hash(string value, int bytes, char? separator)
    {
        var h = HMACSHA256.HashData(_id.Salt, Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        var hex = Convert.ToHexString(h, 0, bytes);
        return separator is null ? hex : string.Join(separator.Value, Enumerable.Range(0, bytes).Select(i => hex.Substring(i * 2, 2)));
    }

    private string HashGuid(string guid)
    {
        var h = Convert.ToHexStringLower(HMACSHA256.HashData(_id.Salt, Encoding.UTF8.GetBytes(guid.ToLowerInvariant())));
        return h[..8] + "-" + h[8..12] + "-" + h[12..16] + "-" + h[16..20] + "-" + h[20..32];
    }

    private static bool IsLocationTail(string tail) => LocationTail().IsMatch(tail) || tail.All(char.IsAsciiDigit);

    [GeneratedRegex(@"\\CurrentVersion\\(Run|RunOnce|Run32)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RunKey();

    [GeneratedRegex(@"([A-Za-z]:[\\/]+)Users([\\/]+)([^\\/""'<>|:*?\r\n]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProfileRoot();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();

    [GeneratedRegex(@"((?:USB|USBSTOR|BTHENUM|BTHLE|BTHLEDEVICE|BTHHFENUM|BTH|HID|SWD|SCSI|STORAGE|WPDBUSENUM|PCI|HDAUDIO|DISPLAY|USB4|TS_USB_HUB|BTHA2DP|INTELAUDIO|NVME)[\\#][^\\#\s""{}|,]+[\\#])([^\\#\s""{}|,]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DeviceTail();

    [GeneratedRegex(@"^[0-9A-Fa-f]{1,2}&[0-9A-Fa-f]{1,8}&[0-9A-Fa-f]{1,2}(&[0-9A-Fa-f]{1,8}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex LocationTail();

    [GeneratedRegex(@"\b([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(-[a-z]{2,8})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AccountFolder();
}
