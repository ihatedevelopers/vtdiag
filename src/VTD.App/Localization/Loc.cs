using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Text;
using VTD.Contracts;

namespace VTD.App.Localization;

/// <summary>
/// UI strings (design_B.md section 11): <c>Strings.resx</c> (English, neutral) + <c>Strings.tr.resx</c>, switched at
/// runtime without a restart. XAML binds through <see cref="T"/> (<c>{l:T Welcome.Title}</c>), which binds to this
/// indexer; changing <see cref="Language"/> raises <c>Item[]</c> so every bound string updates. The pseudo language
/// <see cref="Pseudo"/> renders English with accents and +40 % length to catch clipped layouts (Turkish runs ~30 % longer).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Pseudo-localization language id (qps-ploc).</summary>
    public const string Pseudo = "qps";

    private static readonly ResourceManager Resources = new("VTD.App.Localization.Strings", typeof(Loc).Assembly);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private string _language = Languages.English;

    /// <summary>The app-wide instance.</summary>
    public static Loc Instance { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Current language: <c>en</c>, <c>tr</c> or <see cref="Pseudo"/>.</summary>
    public string Language
    {
        get => _language;
        set
        {
            var next = value is Languages.Turkish or Pseudo ? value : Languages.English;
            if (string.Equals(next, _language, StringComparison.Ordinal))
            {
                return;
            }

            _language = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTurkish)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True for Turkish.</summary>
    public bool IsTurkish => string.Equals(_language, Languages.Turkish, StringComparison.Ordinal);

    /// <summary>The language for engine/KB/report calls (pseudo maps to English).</summary>
    public string DataLanguage => IsTurkish ? Languages.Turkish : Languages.English;

    /// <summary>Display culture (numbers, dates on screen). Machine data never uses it.</summary>
    public CultureInfo Culture => IsTurkish ? CultureInfo.GetCultureInfo("tr-TR") : CultureInfo.GetCultureInfo("en-US");

    /// <summary>Keys that were asked for but are not in the neutral resx (tests assert this stays empty).</summary>
    public IReadOnlyCollection<string> MissingKeys
    {
        get
        {
            lock (_missing)
            {
                return [.. _missing];
            }
        }
    }

    /// <summary>A string by key.</summary>
    /// <param name="key">Key.</param>
    public string this[string key] => Get(key);

    /// <summary>True when the neutral resx has the key.</summary>
    /// <param name="key">Key.</param>
    public static bool Has(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) is not null;

    /// <summary>The raw resx value for a language (no fallback); null when missing. Tests use this.</summary>
    /// <param name="key">Key.</param>
    /// <param name="language">Language.</param>
    public static string? Raw(string key, string language)
    {
        var culture = string.Equals(language, Languages.Turkish, StringComparison.Ordinal) ? CultureInfo.GetCultureInfo("tr") : CultureInfo.InvariantCulture;
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set?.GetString(key);
    }

    /// <summary>All keys of the neutral resx.</summary>
    public static IReadOnlyList<string> AllKeys()
    {
        var set = Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        var keys = new List<string>();
        if (set is not null)
        {
            foreach (System.Collections.DictionaryEntry entry in set)
            {
                keys.Add((string)entry.Key);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>A string by key, in the current language (Turkish falls back to English; missing keys show the key).</summary>
    /// <param name="key">Key.</param>
    public string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var english = Resources.GetString(key, CultureInfo.InvariantCulture);
        if (english is null)
        {
            lock (_missing)
            {
                _missing.Add(key);
            }

            return key;
        }

        return _language switch
        {
            Languages.Turkish => Resources.GetString(key, CultureInfo.GetCultureInfo("tr")) ?? english,
            Pseudo => Pseudolocalize(english),
            _ => english,
        };
    }

    /// <summary>A string with <c>{name}</c> placeholders filled (values formatted with the display culture).</summary>
    /// <param name="key">Key.</param>
    /// <param name="args">Name/value pairs.</param>
    public string Format(string key, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Fill(Get(key), Culture, args);
    }

    /// <summary>Fills <c>{name}</c> placeholders.</summary>
    /// <param name="text">Text.</param>
    /// <param name="culture">Culture for numbers.</param>
    /// <param name="args">Name/value pairs.</param>
    public static string Fill(string text, CultureInfo culture, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(args);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", Convert.ToString(value, culture) ?? string.Empty, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Pseudo-localizes English: accented letters, +40 % length, brackets to spot truncation. Placeholders survive.</summary>
    /// <param name="text">English text.</param>
    public static string Pseudolocalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length * 2);
        sb.Append('[');
        var inPlaceholder = false;
        foreach (var c in text)
        {
            if (c == '{')
            {
                inPlaceholder = true;
            }
            else if (c == '}')
            {
                inPlaceholder = false;
            }

            sb.Append(inPlaceholder ? c : Accent(c));
        }

        var pad = (int)Math.Ceiling(text.Length * 0.4);
        sb.Append(' ');
        for (var i = 0; i < pad; i++)
        {
            sb.Append(i % 6 == 5 ? ' ' : '~');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static char Accent(char c) => c switch
    {
        'a' => 'á', 'e' => 'é', 'i' => 'ï', 'o' => 'ö', 'u' => 'ü', 'c' => 'ç', 'n' => 'ñ', 's' => 'š', 'y' => 'ÿ', 'z' => 'ž',
        'A' => 'Å', 'E' => 'É', 'I' => 'İ', 'O' => 'Ö', 'U' => 'Ü', 'C' => 'Ç', 'N' => 'Ñ', 'S' => 'Š', 'Y' => 'Ý', 'Z' => 'Ž',
        _ => c,
    };
}
