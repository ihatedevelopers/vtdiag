using VTD.Contracts;
using VTD.Contracts.Knowledge;

namespace VTD.App.Localization;

/// <summary>
/// KB text for the screen (rule titles, evidence, fix explanations, guide steps). The KB (A2) owns these strings; when a
/// key is missing in the KB for the current language the UI falls back to the KB's English, then to its own
/// <c>kb.&lt;key&gt;</c> resx entry, then to a humanized id, so a player never sees a raw dotted key.
/// </summary>
/// <param name="kb">KB.</param>
/// <param name="loc">UI strings.</param>
public sealed class KbText(IKnowledgeBase kb, Loc loc)
{
    /// <summary>KB.</summary>
    public IKnowledgeBase Kb { get; } = kb ?? throw new ArgumentNullException(nameof(kb));

    /// <summary>Text for a key, or empty for null.</summary>
    /// <param name="key">KB string key.</param>
    /// <param name="args">Placeholder values (invariant strings from the engine).</param>
    public string Get(string? key, IReadOnlyDictionary<string, string>? args = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        string text;
        var lang = loc.DataLanguage;
        if (Kb.HasText(key, lang))
        {
            text = Kb.Text(key, lang);
        }
        else if (Loc.Raw("kb." + key, lang) is { } own)
        {
            // Our translated fallback beats the KB's English when the KB lacks this language.
            text = own;
        }
        else if (Kb.HasText(key, Languages.English))
        {
            text = Kb.Text(key, Languages.English);
        }
        else if (Loc.Has("kb." + key))
        {
            text = loc.Get("kb." + key);
        }
        else
        {
            text = Humanize(key);
        }

        if (string.Equals(loc.Language, Loc.Pseudo, StringComparison.Ordinal) && !text.StartsWith('['))
        {
            text = Loc.Pseudolocalize(text);
        }

        return args is null || args.Count == 0 ? text : Loc.Fill(text, loc.Culture, [.. args.Select(a => (a.Key, (object?)a.Value))]);
    }

    /// <summary>True when either the KB or the UI fallback has real text for the key.</summary>
    /// <param name="key">Key.</param>
    public bool Has(string? key) => !string.IsNullOrEmpty(key) && (Kb.HasText(key, Languages.English) || Loc.Has("kb." + key));

    /// <summary>"rule.realtek25.title" → "Realtek25 title"; "fix.net.dns_flush" → "Dns flush".</summary>
    /// <param name="key">Key or id.</param>
    public static string Humanize(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length == 0 ? key : parts[^1];
        if (parts.Length >= 2 && last is "title" or "text" or "name" or "meaning")
        {
            last = parts[^2];
        }

        var words = last.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
