using System.Globalization;
using System.Resources;
using VTD.Contracts;
using VTD.Contracts.Knowledge;

namespace VTD.Reporting;

/// <summary>
/// Display text for one report language. Report chrome (headings, bucket sentences, bands, status words, unknown
/// reasons) comes from <c>Resources/ReportStrings(.tr).resx</c>; KB keys go through <see cref="IKnowledgeBase.Text"/>
/// first, then the report's own fallback table, then a humanized key, so a player never sees a raw dotted key.
/// Numbers and dates use the language's culture (display only).
/// </summary>
internal sealed class ReportText
{
    private static readonly ResourceManager Resources = new("VTD.Reporting.Resources.ReportStrings", typeof(ReportText).Assembly);
    private static readonly HashSet<string> GenericSegments = new(
        ["title", "text", "summary", "ev", "explain", "meaning", "detail", "reason", "desc", "name", "label"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IKnowledgeBase _kb;

    public ReportText(IKnowledgeBase kb, string language)
    {
        _kb = kb;
        Language = string.Equals(language, Languages.Turkish, StringComparison.OrdinalIgnoreCase) ? Languages.Turkish : Languages.English;
        Culture = CultureInfo.GetCultureInfo(Language == Languages.Turkish ? "tr-TR" : "en-US");
    }

    /// <summary><c>en</c> or <c>tr</c>.</summary>
    public string Language { get; }

    /// <summary>Display culture.</summary>
    public CultureInfo Culture { get; }

    /// <summary>A report string with <c>{name}</c> placeholders.</summary>
    /// <param name="key">Resource key.</param>
    /// <param name="args">Placeholder values (numbers are formatted with <see cref="Culture"/>).</param>
    public string Ui(string key, params (string Name, object? Value)[] args)
    {
        var text = Resources.GetString(key, Culture) ?? Humanize(key);
        return Fill(text, args);
    }

    /// <summary>True when the report has its own string for <paramref name="key"/>.</summary>
    /// <param name="key">Key.</param>
    public bool HasUi(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) is not null;

    /// <summary>KB text: the KB in this language, the KB in English, the report's fallback table, then a humanized key.</summary>
    /// <param name="key">KB string key.</param>
    /// <param name="args">Placeholder values.</param>
    /// <param name="fallbackId">Id to humanize when nothing else has text (for example a hypothesis id).</param>
    public string Kb(string? key, IReadOnlyDictionary<string, string>? args = null, string? fallbackId = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Humanize(fallbackId ?? "?");
        }

        IReadOnlyDictionary<string, object>? objectArgs = args?.ToDictionary(a => a.Key, a => (object)a.Value, StringComparer.Ordinal);
        if (_kb.HasText(key, Language))
        {
            return _kb.Text(key, Language, objectArgs);
        }

        if (_kb.HasText(key, Languages.English))
        {
            return _kb.Text(key, Languages.English, objectArgs);
        }

        if (HasUi(key))
        {
            return Fill(Resources.GetString(key, Culture)!, args?.Select(a => (a.Key, (object?)a.Value)).ToArray() ?? []);
        }

        return Humanize(fallbackId ?? key);
    }

    /// <summary>A number for display.</summary>
    /// <param name="value">Value.</param>
    /// <param name="format">Format.</param>
    public string Number(double value, string format = "0.#") => value.ToString(format, Culture);

    /// <summary>A date for display.</summary>
    /// <param name="value">Value.</param>
    public string Date(DateTimeOffset value) => value.UtcDateTime.ToString("d MMM yyyy HH:mm 'UTC'", Culture);

    /// <summary>A date for display.</summary>
    /// <param name="value">Value.</param>
    public string Date(DateOnly value) => value.ToString("d MMM yyyy", Culture);

    /// <summary>Turns a dotted or dashed key into words: <c>health.secure_boot_keys</c> → "Secure boot keys".</summary>
    /// <param name="key">Key or id.</param>
    public static string Humanize(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var pick = segments.LastOrDefault(s => !GenericSegments.Contains(s)) ?? segments.LastOrDefault() ?? key;
        var words = pick.Replace('_', ' ').Replace('-', ' ').Trim();
        if (words.Length == 0)
        {
            return key;
        }

        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>Turns a dotted id into words using its last two segments: <c>net.realtek25.unused</c> → "Realtek25 unused".</summary>
    /// <param name="id">Signal or other dotted id.</param>
    public static string HumanizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries).Where(s => !GenericSegments.Contains(s)).ToList();
        var pick = segments.Count >= 2 ? segments[^2] + " " + segments[^1] : segments.LastOrDefault() ?? id;
        return Humanize(pick);
    }

    private string Fill(string text, (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            var formatted = value switch
            {
                null => string.Empty,
                double d => Number(d),
                float f => Number(f),
                int i => i.ToString(Culture),
                long l => l.ToString(Culture),
                IFormattable formattable => formattable.ToString(null, Culture),
                _ => value.ToString() ?? string.Empty,
            };
            text = text.Replace("{" + name + "}", formatted, StringComparison.Ordinal);
        }

        return text;
    }
}
