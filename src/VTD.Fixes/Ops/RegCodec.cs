using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// Registry value encodings used in captures: an exact raw form (<c>&lt;type&gt;:&lt;hex&gt;</c>) for bit-identical undo, and the
/// readable forms the KB verifies against (kind names, DWORD/QWORD as unsigned decimal, strings as text, multi-strings
/// joined with <c>\n</c>, binary as lowercase hex).
/// </summary>
public static class RegCodec
{
    /// <summary>Kind names accepted by <c>registry.setValue</c>.</summary>
    public static IReadOnlyList<string> SettableKinds { get; } = ["String", "ExpandString", "DWord", "QWord", "MultiString"];

    /// <summary>Exact raw form.</summary>
    /// <param name="value">Value.</param>
    public static string Raw(RegValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Type.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(value.Data);
    }

    /// <summary>Parses the raw form; null when malformed.</summary>
    /// <param name="raw">Raw form.</param>
    public static RegValue? ParseRaw(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !int.TryParse(raw.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var type))
        {
            return null;
        }

        try
        {
            return new RegValue(type, Convert.FromHexString(raw.AsSpan(colon + 1)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Kind name of a type code.</summary>
    /// <param name="type">REG_* type.</param>
    public static string KindName(int type) => type switch
    {
        RegValue.None => "None",
        RegValue.String => "String",
        RegValue.ExpandString => "ExpandString",
        RegValue.Binary => "Binary",
        RegValue.DWord => "DWord",
        RegValue.MultiString => "MultiString",
        RegValue.QWord => "QWord",
        _ => "Type" + type.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Readable data.</summary>
    /// <param name="value">Value.</param>
    public static string Display(RegValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var d = value.Data;
        return value.Type switch
        {
            RegValue.DWord when d.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(d).ToString(CultureInfo.InvariantCulture),
            RegValue.QWord when d.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(d).ToString(CultureInfo.InvariantCulture),
            RegValue.String or RegValue.ExpandString => Utf16Text(d),
            RegValue.MultiString => string.Join('\n', Utf16Text(d, keepNulls: true).Split('\0', StringSplitOptions.RemoveEmptyEntries)),
            _ => Convert.ToHexStringLower(d),
        };
    }

    /// <summary>Encodes data of a settable kind; null when it does not parse.</summary>
    /// <param name="kind">Kind name.</param>
    /// <param name="data">Invariant data.</param>
    public static RegValue? Encode(string kind, string data)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(data);
        switch (kind)
        {
            case "DWord":
                if (!uint.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out var dw))
                {
                    return null;
                }

                var b4 = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(b4, dw);
                return new RegValue(RegValue.DWord, b4);
            case "QWord":
                if (!ulong.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out var qw))
                {
                    return null;
                }

                var b8 = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(b8, qw);
                return new RegValue(RegValue.QWord, b8);
            case "String":
                return data.Contains('\0', StringComparison.Ordinal) ? null : new RegValue(RegValue.String, Encoding.Unicode.GetBytes(data + "\0"));
            case "ExpandString":
                return data.Contains('\0', StringComparison.Ordinal) ? null : new RegValue(RegValue.ExpandString, Encoding.Unicode.GetBytes(data + "\0"));
            case "MultiString":
                if (data.Contains('\0', StringComparison.Ordinal))
                {
                    return null;
                }

                var parts = data.Length == 0 ? [] : data.Split('\n');
                if (parts.Any(p => p.Length == 0))
                {
                    return null;
                }

                return new RegValue(RegValue.MultiString, Encoding.Unicode.GetBytes(string.Concat(parts.Select(p => p + "\0")) + "\0"));
            default:
                return null;
        }
    }

    /// <summary>The three readable keys plus the raw key for a value (absent: <c>exists=false</c> and empty strings).</summary>
    /// <param name="value">Value or null.</param>
    /// <param name="map">Map to fill.</param>
    /// <param name="prefix">Key prefix.</param>
    public static void Fill(RegValue? value, IDictionary<string, string> map, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(map);
        map[prefix + "exists"] = Captures.Bool(value is not null);
        map[prefix + "kind"] = value is null ? string.Empty : KindName(value.Type);
        map[prefix + "data"] = value is null ? string.Empty : Display(value);
        map["_" + prefix + "raw"] = value is null ? string.Empty : Raw(value);
    }

    private static string Utf16Text(byte[] d, bool keepNulls = false)
    {
        var text = Encoding.Unicode.GetString(d, 0, d.Length - (d.Length % 2));
        if (keepNulls)
        {
            return text;
        }

        var nul = text.IndexOf('\0', StringComparison.Ordinal);
        return nul < 0 ? text : text[..nul];
    }
}
