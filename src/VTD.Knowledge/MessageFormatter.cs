using System.Globalization;
using System.Text;

namespace VTD.Knowledge;

/// <summary>
/// The small ICU MessageFormat subset KB strings use: <c>{name}</c> and <c>{count, plural, one {# item} other {# items}}</c>
/// (<c>=0</c>/<c>=1</c> exact cases too; <c>#</c> is the number). Numbers and dates are formatted with the language's
/// culture for display only; unknown placeholders stay as written so a missing argument is visible, not silent.
/// </summary>
public static class MessageFormatter
{
    /// <summary>Formats a message.</summary>
    /// <param name="pattern">Message pattern.</param>
    /// <param name="language">Language (<c>en</c>, <c>tr</c>).</param>
    /// <param name="args">Arguments.</param>
    public static string Format(string pattern, string language, IReadOnlyDictionary<string, object>? args)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (args is null || args.Count == 0 || !pattern.Contains('{', StringComparison.Ordinal))
        {
            return pattern;
        }

        var culture = CultureFor(language);
        var sb = new StringBuilder(pattern.Length + 16);
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c != '{')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var end = MatchingBrace(pattern, i);
            if (end < 0)
            {
                sb.Append(pattern, i, pattern.Length - i);
                break;
            }

            var body = pattern.Substring(i + 1, end - i - 1);
            sb.Append(Placeholder(body, culture, args, pattern.AsSpan(i, end - i + 1)));
            i = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>Display culture of a KB language.</summary>
    /// <param name="language">Language.</param>
    public static CultureInfo CultureFor(string? language) => language switch
    {
        "tr" => CultureInfo.GetCultureInfo("tr-TR"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

    /// <summary>Placeholder names used in a pattern (for lint: English and Turkish must use the same ones).</summary>
    /// <param name="pattern">Pattern.</param>
    public static IReadOnlySet<string> PlaceholderNames(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var i = 0;
        while ((i = pattern.IndexOf('{', i)) >= 0)
        {
            var end = MatchingBrace(pattern, i);
            if (end < 0)
            {
                break;
            }

            var body = pattern.Substring(i + 1, end - i - 1);
            var comma = body.IndexOf(',', StringComparison.Ordinal);
            names.Add((comma < 0 ? body : body[..comma]).Trim());
            i = end + 1;
        }

        return names;
    }

    private static string Placeholder(string body, CultureInfo culture, IReadOnlyDictionary<string, object> args, ReadOnlySpan<char> original)
    {
        var comma = body.IndexOf(',', StringComparison.Ordinal);
        var name = (comma < 0 ? body : body[..comma]).Trim();
        if (!args.TryGetValue(name, out var value))
        {
            return original.ToString();
        }

        if (comma < 0)
        {
            return Display(value, culture);
        }

        var rest = body[(comma + 1)..].TrimStart();
        if (!rest.StartsWith("plural", StringComparison.Ordinal))
        {
            return Display(value, culture);
        }

        var cases = ParseCases(rest[(rest.IndexOf(',', StringComparison.Ordinal) + 1)..]);
        var number = ToDouble(value);
        var exact = string.Create(CultureInfo.InvariantCulture, $"={number}");
        var key = cases.ContainsKey(exact) ? exact : (Math.Abs(number - 1.0) < double.Epsilon ? "one" : "other");
        if (!cases.TryGetValue(key, out var text) && !cases.TryGetValue("other", out text))
        {
            return Display(value, culture);
        }

        return text.Replace("#", Display(value, culture), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseCases(string text)
    {
        var cases = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            if (open < 0)
            {
                break;
            }

            var close = MatchingBrace(text, open);
            if (close < 0)
            {
                break;
            }

            cases[text[i..open].Trim()] = text.Substring(open + 1, close - open - 1);
            i = close + 1;
        }

        return cases;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static double ToDouble(object value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => double.NaN,
    };

    private static string Display(object value, CultureInfo culture) => value switch
    {
        string s => s,
        DateOnly d => d.ToString("d", culture),
        DateTimeOffset t => t.ToString("g", culture),
        int or long or short => ((IFormattable)value).ToString("#,0", culture),
        double or float or decimal => ((IFormattable)value).ToString("#,0.##", culture),
        IFormattable f => f.ToString(null, culture),
        _ => Convert.ToString(value, culture) ?? string.Empty,
    };
}
