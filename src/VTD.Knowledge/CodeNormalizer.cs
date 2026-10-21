using System.Text;
using System.Text.RegularExpressions;

namespace VTD.Knowledge;

/// <summary>
/// Normalizes typed or logged VAN/VAL codes to the KB's canonical form (culture-invariant):
/// <c>van9003</c>, <c>VAN-9003</c>, <c>VAN: 9003</c> → <c>VAN 9003</c>; <c>VAN: RESTRICTION: 1</c> → <c>VAN RESTRICTION 1</c>;
/// <c>VAN -81</c> keeps its sign (Riot's negative codes) while a hyphen glued to the prefix is a separator.
/// The same rules live in <c>tools/kb_builder.py</c> (<c>normalize_code</c>).
/// </summary>
public static partial class CodeNormalizer
{
    /// <summary>Normalizes code text.</summary>
    /// <param name="text">Raw text.</param>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim().ToUpperInvariant();
        s = Leading().Replace(s, string.Empty);
        s = s.Replace(':', ' ').Replace('–', '-').Replace('−', '-');
        s = Spaces().Replace(s, " ").Trim();
        s = GluedHyphen().Replace(s, "$1 ");      // VAN-9003 -> VAN 9003 (hyphen glued to the prefix is a separator)
        s = GluedDigits().Replace(s, "$1 $2");    // VAN9003 -> VAN 9003
        s = SpacedSign().Replace(s, "$1 -$2");    // VAN - 81 -> VAN -81
        return s;
    }

    /// <summary>Alternative spellings to try when the exact form is unknown (sign dropped or added, prefix missing).</summary>
    /// <param name="normalized">Normalized code.</param>
    public static IEnumerable<string> Variants(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var m = Numbered().Match(normalized);
        if (m.Success)
        {
            var prefix = m.Groups[1].Value;
            var number = m.Groups[3].Value;
            yield return m.Groups[2].Success ? $"{prefix} {number}" : $"{prefix} -{number}";
            yield break;
        }

        if (BareNumber().IsMatch(normalized))
        {
            yield return "VAN " + normalized;
            yield return "VAL " + normalized;
            if (normalized.StartsWith('-'))
            {
                yield return "VAN " + normalized[1..];
            }
        }
    }

    /// <summary>Finds code-looking tokens in free text (for example a pasted error message).</summary>
    /// <param name="text">Text.</param>
    public static IReadOnlyList<string> Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<string>();
        foreach (Match m in InText().Matches(text.ToUpperInvariant()))
        {
            var code = Normalize(m.Value);
            if (!found.Contains(code, StringComparer.Ordinal))
            {
                found.Add(code);
            }
        }

        return found;
    }

    /// <summary>Lower-case, dash-separated id form of a normalized code (<c>VAN -81</c> → <c>van-m81</c>), as kb_builder.py makes row ids.</summary>
    /// <param name="normalized">Normalized code.</param>
    public static string Slug(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized.ToLowerInvariant())
        {
            sb.Append(c switch { '-' => 'm', '_' => '-', ' ' => '-', _ => c });
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^(ERROR CODE|ERROR|CODE)\s*[:#]?\s*(?=VAN|VAL)", RegexOptions.CultureInvariant)]
    private static partial Regex Leading();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(VAN|VAL)-(?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex GluedHyphen();

    [GeneratedRegex(@"^(VAN|VAL)(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex GluedDigits();

    [GeneratedRegex(@"^(VAN|VAL) - (\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedSign();

    [GeneratedRegex(@"^(VAN|VAL) (-)?(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^-?\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"\b(VAN|VAL)\s*[:\-]?\s*(RESTRICTION\s*:?\s*\d*|STATUS_[A-Z_0-9]+|-?\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex InText();
}
