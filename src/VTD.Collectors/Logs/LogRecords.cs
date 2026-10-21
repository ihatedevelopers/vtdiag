using System.Globalization;
using System.Text.RegularExpressions;

namespace VTD.Collectors.Logs;

/// <summary>A structured record a safe extractor emits instead of a raw line: <c>patternId|utc|group|group...</c>.</summary>
/// <param name="PatternId">Allowlisted pattern id.</param>
/// <param name="Utc">Timestamp when the line had one.</param>
/// <param name="Groups">Whitelisted, scrubbed groups.</param>
public sealed record LogRecord(string PatternId, DateTimeOffset? Utc, IReadOnlyList<string> Groups)
{
    /// <summary>Serializes the record (the only form that leaves the file source).</summary>
    public string Encode()
        => string.Join('|', new[] { PatternId, Utc?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty }.Concat(Groups));

    /// <summary>Parses an encoded record.</summary>
    /// <param name="line">Encoded record.</param>
    public static LogRecord? Decode(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = line.Split('|');
        if (parts.Length < 2 || parts[0].Length == 0)
        {
            return null;
        }

        DateTimeOffset? utc = DateTimeOffset.TryParseExact(parts[1], "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        return new LogRecord(parts[0], utc, parts.Skip(2).ToList());
    }
}

/// <summary>Normalizes Riot error codes to the KB form (<c>VAN 9003</c>, <c>VAN -81</c>, <c>VAN: RESTRICTION: 1</c>, <c>VAL 43</c>).</summary>
public static partial class CodeNormalizer
{
    /// <summary>Finds codes in a (raw, not yet emitted) line. Only the normalized codes leave the extractor.</summary>
    /// <param name="line">Line.</param>
    public static IEnumerable<string> Find(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (Match m in Restriction().Matches(line))
        {
            yield return m.Groups[1].Success ? "VAN: RESTRICTION: " + m.Groups[1].Value : "VAN: RESTRICTION";
        }

        foreach (Match m in Van().Matches(line))
        {
            var digits = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n is >= -200 and <= 99999)
            {
                yield return "VAN " + n.ToString(CultureInfo.InvariantCulture);
            }
        }

        foreach (Match m in Val().Matches(line))
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 200)
            {
                yield return "VAL " + n.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    [GeneratedRegex(@"\bVAN:\s?RESTRICTION(?::\s?(\d))?", RegexOptions.CultureInvariant)]
    private static partial Regex Restriction();

    [GeneratedRegex(@"\bVAN(?:[\s_:]{1,2}(-?\d{1,5})|-(\d{1,5}))\b", RegexOptions.CultureInvariant)]
    private static partial Regex Van();

    [GeneratedRegex(@"\bVAL[\s_]{1,2}(\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Val();
}
