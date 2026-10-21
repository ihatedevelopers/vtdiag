using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VTD.Contracts;

namespace VTD.Reporting;

/// <summary>Share-level value transforms keyed by the install salt (design_B.md section 12).</summary>
internal sealed class ShareTransforms
{
    /// <summary>Rule ids.</summary>
    public static class Rules
    {
        public const string DeviceTail = "device.instance_tail";
        public const string DeviceSegment = "device.segment";
        public const string Hash = "hash.salted";
        public const string Mac = "net.mac";
        public const string IpClass = "net.ip_class";
        public const string ServerIp = "net.server_ip";
        public const string IdentifierDrop = "identifier.drop";
        public const string FactRedacted = "identifier.fact";
        public const string SectionExcluded = "section.excluded";
        public const string KeyCollision = "key.collision";
    }

    private readonly byte[] _salt;

    public ShareTransforms(byte[] salt) => _salt = salt;

    /// <summary><c>h:</c> + 16 hex characters of HMAC-SHA256(salt, value).</summary>
    /// <param name="value">Value.</param>
    public string Hash(string value) => "h:" + HexPrefix(value, 16);

    /// <summary>
    /// Device instance id: enumerator and device segment (VEN/DEV/SUBSYS/REV, VID/PID...) are kept; the instance tail
    /// becomes a salted hash of the whole id (upper-cased, because PnP ids are case-insensitive). A device segment that
    /// looks like it carries a MAC or serial (a 12-hex-digit run, odd characters) is hashed too. Ids without a tail
    /// (hardware ids) stay as they are.
    /// </summary>
    /// <param name="id">Instance id.</param>
    public string DeviceId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var parts = id.Split('\\');
        if (parts.Length == 0 || !TextPatterns.SafeEnumerator().IsMatch(parts[0]))
        {
            return Hash(id.ToUpperInvariant());
        }

        var sb = new StringBuilder(parts[0]);
        if (parts.Length >= 2)
        {
            var segment = parts[1];
            sb.Append('\\').Append(IsSafeSegment(segment) ? segment : Hash(segment.ToUpperInvariant()));
        }

        if (parts.Length >= 3)
        {
            sb.Append('\\').Append(Hash(id.ToUpperInvariant()));
        }

        return sb.ToString();
    }

    /// <summary>MAC address: OUI kept (vendor), rest replaced by a salted hash.</summary>
    /// <param name="mac">MAC in any common notation.</param>
    public string Mac(string mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        var hex = new string(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length != 12 || mac.Count(c => !Uri.IsHexDigit(c) && c is not ':' and not '-' and not '.') > 0)
        {
            return Hash(mac.ToUpperInvariant());
        }

        return string.Create(CultureInfo.InvariantCulture, $"{hex[..2]}:{hex[2..4]}:{hex[4..6]}:h:{HexPrefix(hex, 12)}");
    }

    /// <summary>IP address reduced to a class: <c>private/8</c>, <c>private/12</c>, <c>private/24</c>, <c>cgnat</c>,
    /// <c>loopback</c>, <c>link-local</c>, <c>private/ula</c>, <c>multicast</c>, <c>unspecified</c> or <c>public</c>.</summary>
    /// <param name="ip">Address text.</param>
    public static string IpClass(string ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var text = ip.Trim();
        var percent = text.IndexOf('%', StringComparison.Ordinal);
        if (percent > 0)
        {
            text = text[..percent];
        }

        if (!IPAddress.TryParse(text, out var address))
        {
            return "[ip]";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b switch
            {
                [0, 0, 0, 0] => "unspecified",
                [10, ..] => "private/8",
                [172, >= 16 and <= 31, ..] => "private/12",
                [192, 168, ..] => "private/24",
                [100, >= 64 and <= 127, ..] => "cgnat",
                [127, ..] => "loopback",
                [169, 254, ..] => "link-local",
                [>= 224 and <= 239, ..] => "multicast",
                _ => "public",
            };
        }

        if (IPAddress.IsLoopback(address))
        {
            return "loopback";
        }

        var v6 = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
        {
            return "unspecified";
        }

        if (address.IsIPv6LinkLocal)
        {
            return "link-local";
        }

        if (address.IsIPv6Multicast)
        {
            return "multicast";
        }

        return (v6[0] & 0xFE) == 0xFC ? "private/ula" : "public";
    }

    private static bool IsSafeSegment(string segment)
        => TextPatterns.SafeDeviceSegment().IsMatch(segment) && !TextPatterns.LongHexRun().IsMatch(segment);

    private string HexPrefix(string value, int length)
    {
        var mac = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes("vtd-share/1:" + value));
        return Convert.ToHexStringLower(mac)[..length];
    }

    /// <summary>Parses an <see cref="EntityKind"/> from its JSON (camelCase) name.</summary>
    /// <param name="text">JSON value.</param>
    /// <param name="kind">Kind.</param>
    public static bool TryParseKind(string? text, out EntityKind kind)
        => Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind) && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}
