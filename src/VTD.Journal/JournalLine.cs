using System.Globalization;
using System.Text;
using System.Text.Json;
using VTD.Contracts.Journaling;
using VTD.Contracts.Serialization;

namespace VTD.Journal;

/// <summary>
/// The on-disk journal line (format owned by A3): <c>&lt;compact JSON of the record&gt;|crc=&lt;8 uppercase hex&gt;\n</c>,
/// where the CRC-32 covers the UTF-8 bytes of the JSON body. The JSON never contains a raw newline (System.Text.Json
/// escapes control characters), so a line is one record.
/// </summary>
public static class JournalLine
{
    /// <summary>The separator between the JSON body and its checksum.</summary>
    public const string CrcMarker = "|crc=";

    /// <summary>Longest accepted line (a record carries a few small string maps; anything bigger is corrupt).</summary>
    public const int MaxLineBytes = 1 << 20;

    /// <summary>Encodes a record as one line including the trailing newline.</summary>
    /// <param name="record">Record.</param>
    public static byte[] Encode(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var json = VtdJson.Serialize(record);
        if (json.Contains('\n', StringComparison.Ordinal) || json.Contains('\r', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A journal record serialized to more than one line.");
        }

        var body = Encoding.UTF8.GetBytes(json);
        var crc = Crc32.Compute(body);
        var suffix = Encoding.ASCII.GetBytes(CrcMarker + crc.ToString("X8", CultureInfo.InvariantCulture) + "\n");
        var line = new byte[body.Length + suffix.Length];
        body.CopyTo(line, 0);
        suffix.CopyTo(line, body.Length);
        return line;
    }

    /// <summary>Decodes one line (without its newline). Returns null and an issue code when it is torn or corrupt.</summary>
    /// <param name="line">Line bytes without the trailing newline (a trailing CR is tolerated).</param>
    /// <param name="issue">Issue code when null is returned: <c>line.empty</c>, <c>line.no_crc</c>, <c>line.crc_mismatch</c>,
    /// <c>line.bad_json</c>, <c>line.too_long</c>.</param>
    public static JournalRecord? TryDecode(ReadOnlySpan<byte> line, out string? issue)
    {
        issue = null;
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            issue = "line.empty";
            return null;
        }

        if (line.Length > MaxLineBytes)
        {
            issue = "line.too_long";
            return null;
        }

        var marker = Encoding.ASCII.GetBytes(CrcMarker);
        var at = line.LastIndexOf(marker);
        if (at < 0 || line.Length - at - marker.Length != 8)
        {
            issue = "line.no_crc";
            return null;
        }

        var hex = Encoding.ASCII.GetString(line[(at + marker.Length)..]);
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var expected))
        {
            issue = "line.no_crc";
            return null;
        }

        var body = line[..at];
        if (Crc32.Compute(body) != expected)
        {
            issue = "line.crc_mismatch";
            return null;
        }

        try
        {
            return VtdJson.Deserialize<JournalRecord>(body);
        }
        catch (JsonException)
        {
            issue = "line.bad_json";
            return null;
        }
        catch (ArgumentException)
        {
            issue = "line.bad_json";
            return null;
        }
    }
}
