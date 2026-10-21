using System.Text;
using System.Text.RegularExpressions;

namespace VTD.Reporting;

/// <summary>
/// Identifier values (computer name, user name, interface GUIDs, profile-folder names, SSIDs, MAC and IP addresses...)
/// that must not appear anywhere in a share export. Matching is case-insensitive on whole tokens (not inside a longer
/// word), so a user called "Ali" does not break "Alienware"; the Turkish dotted and dotless i (I, ı, İ, i) all match
/// each other, so "IŞIK", "ışık" and "Işık" are the same name. Values shorter than 3 characters and generic account
/// names are ignored: they identify nobody and would wreck legitimate text.
/// </summary>
internal sealed class IdentifierSet
{
    private static readonly HashSet<string> Generic = new(
        [
            "user", "users", "admin", "administrator", "administrators", "owner", "gamer", "player", "pc", "home", "guest",
            "public", "default", "system", "local", "desktop", "laptop", "windows", "microsoft", "computer", "test", "dev",
            "root", "win", "notebook", "intel", "amd", "nvidia",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly Regex? _regex;

    private IdentifierSet(IReadOnlyList<string> values)
    {
        Values = values;
        if (values.Count > 0)
        {
            var alternation = string.Join('|', values.OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal).Select(Pattern));
            _regex = new Regex(
                "(?<![\\p{L}\\p{N}])(?:" + alternation + ")(?![\\p{L}\\p{N}])",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>No identifiers.</summary>
    public static IdentifierSet Empty { get; } = new([]);

    /// <summary>The usable values.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Creates a set from raw values (unusable values are skipped).</summary>
    /// <param name="values">Values.</param>
    public static IdentifierSet Create(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var usable = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var v = raw?.Trim();
            if (IsUsable(v))
            {
                usable.TryAdd(Fold(v!), v!);
            }
        }

        return usable.Count == 0 ? Empty : new IdentifierSet([.. usable.Values]);
    }

    /// <summary>Case- and Turkish-i-insensitive canonical form (for hashing a matched identifier).</summary>
    /// <param name="value">Value.</param>
    public static string Fold(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch is 'I' or 'ı' or 'İ' ? 'i' : char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>This set plus more values.</summary>
    /// <param name="more">More values.</param>
    public IdentifierSet With(IEnumerable<string?> more) => Create(Values.Concat(more));

    /// <summary>Replaces every identifier (default with <see cref="TextPatterns.IdPlaceholder"/>).</summary>
    /// <param name="text">Text.</param>
    /// <param name="sink">Rule sink.</param>
    /// <param name="replacement">Replacement for a matched identifier (receives the matched text).</param>
    public string Replace(string text, RuleSink? sink, Func<string, string>? replacement = null)
    {
        if (_regex is null || text.Length == 0)
        {
            return text;
        }

        var count = 0;
        var result = _regex.Replace(text, m =>
        {
            count++;
            return replacement?.Invoke(m.Value) ?? TextPatterns.IdPlaceholder;
        });
        if (count > 0)
        {
            sink?.Invoke(TextScrubber.Rules.Identifier, count);
        }

        return result;
    }

    private static string Pattern(string value)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var ch in value)
        {
            if (ch is 'i' or 'I' or 'ı' or 'İ')
            {
                sb.Append("[iIıİ]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }

        return sb.ToString();
    }

    private static bool IsUsable(string? v)
        => v is { Length: >= 3 and <= 256 }
           && !Generic.Contains(v)
           && !TextPatterns.PlaceholderProfileNames.Contains(v)
           && !v.StartsWith('%')
           && v.Any(char.IsLetterOrDigit);
}
