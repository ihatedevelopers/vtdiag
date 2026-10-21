using VTD.Contracts;
using VTD.Contracts.Knowledge;

namespace VTD.Knowledge;

/// <summary>The parts of a KB, as read from a pack folder (or built in a test).</summary>
public sealed record KbContent
{
    /// <summary>Rules.</summary>
    public IReadOnlyList<RuleDef> Rules { get; init; } = [];

    /// <summary>Signals.</summary>
    public IReadOnlyList<SignalDef> Signals { get; init; } = [];

    /// <summary>Pack collectors.</summary>
    public IReadOnlyList<CollectorDef> Collectors { get; init; } = [];

    /// <summary>Conflicts.</summary>
    public IReadOnlyList<ConflictDef> Conflicts { get; init; } = [];

    /// <summary>Error codes.</summary>
    public IReadOnlyList<ErrorCodeDef> ErrorCodes { get; init; } = [];

    /// <summary>Fixes.</summary>
    public IReadOnlyList<FixDef> Fixes { get; init; } = [];

    /// <summary>Guides.</summary>
    public IReadOnlyList<GuideDef> Guides { get; init; } = [];

    /// <summary>Questions.</summary>
    public IReadOnlyList<QuestionDef> Questions { get; init; } = [];

    /// <summary>Symptoms.</summary>
    public IReadOnlyList<SymptomDef> Symptoms { get; init; } = [];

    /// <summary>Tables.</summary>
    public IReadOnlyList<TableDef> Tables { get; init; } = [];

    /// <summary>Trusted helper keys.</summary>
    public IReadOnlyList<TrustedHelperKey> TrustedHelpers { get; init; } = [];

    /// <summary>Strings by language.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Strings { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
}

/// <summary>An immutable, indexed <see cref="IKnowledgeBase"/>.</summary>
public sealed class KnowledgeBase : IKnowledgeBase
{
    private readonly Dictionary<string, RuleDef> _rules;
    private readonly Dictionary<string, FixDef> _fixes;
    private readonly Dictionary<string, GuideDef> _guides;
    private readonly Dictionary<string, QuestionDef> _questions;
    private readonly Dictionary<string, ConflictDef> _conflicts;
    private readonly Dictionary<string, (ErrorCodeDef Def, string Code)> _codes;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _strings;

    /// <summary>Creates a KB.</summary>
    /// <param name="version">Version.</param>
    /// <param name="content">Content.</param>
    public KnowledgeBase(KbVersion version, KbContent content)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(content);
        Version = version;
        Rules = content.Rules;
        Signals = content.Signals;
        Collectors = content.Collectors;
        Conflicts = content.Conflicts;
        ErrorCodes = content.ErrorCodes;
        Fixes = content.Fixes;
        Guides = content.Guides;
        Questions = content.Questions;
        Symptoms = content.Symptoms.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        Tables = content.Tables;
        TrustedHelpers = content.TrustedHelpers;
        _strings = content.Strings;
        Languages = [.. content.Strings.Keys.Order(StringComparer.Ordinal)];
        _rules = Index(content.Rules, r => r.Id);
        _fixes = Index(content.Fixes, f => f.Id);
        _guides = Index(content.Guides, g => g.Id);
        _questions = Index(content.Questions, q => q.Id);
        _conflicts = Index(content.Conflicts, c => c.Id);
        _codes = new Dictionary<string, (ErrorCodeDef, string)>(StringComparer.Ordinal);
        foreach (var def in content.ErrorCodes)
        {
            foreach (var code in def.Codes)
            {
                _codes.TryAdd(CodeNormalizer.Normalize(code), (def, code));
            }
        }
    }

    /// <summary>An empty KB (the last resort when even the bundled pack is broken).</summary>
    /// <param name="source">Source label.</param>
    public static KnowledgeBase Empty(KbSource source = KbSource.Bundled)
        => new(new KbVersion { Version = "0.0.0.0", Format = KbManifest.CurrentFormat, Source = source }, new KbContent());

    /// <inheritdoc />
    public KbVersion Version { get; }

    /// <inheritdoc />
    public IReadOnlyList<RuleDef> Rules { get; }

    /// <inheritdoc />
    public IReadOnlyList<SignalDef> Signals { get; }

    /// <inheritdoc />
    public IReadOnlyList<CollectorDef> Collectors { get; }

    /// <inheritdoc />
    public IReadOnlyList<ConflictDef> Conflicts { get; }

    /// <inheritdoc />
    public IReadOnlyList<ErrorCodeDef> ErrorCodes { get; }

    /// <inheritdoc />
    public IReadOnlyList<FixDef> Fixes { get; }

    /// <inheritdoc />
    public IReadOnlyList<GuideDef> Guides { get; }

    /// <inheritdoc />
    public IReadOnlyList<QuestionDef> Questions { get; }

    /// <inheritdoc />
    public IReadOnlyList<SymptomDef> Symptoms { get; }

    /// <inheritdoc />
    public IReadOnlyList<TableDef> Tables { get; }

    /// <inheritdoc />
    public IReadOnlyList<TrustedHelperKey> TrustedHelpers { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Languages { get; }

    /// <inheritdoc />
    public RuleDef? FindRule(string id) => id is not null && _rules.TryGetValue(id, out var r) ? r : null;

    /// <inheritdoc />
    public FixDef? FindFix(string id) => id is not null && _fixes.TryGetValue(id, out var f) ? f : null;

    /// <inheritdoc />
    public GuideDef? FindGuide(string id) => id is not null && _guides.TryGetValue(id, out var g) ? g : null;

    /// <inheritdoc />
    public QuestionDef? FindQuestion(string id) => id is not null && _questions.TryGetValue(id, out var q) ? q : null;

    /// <inheritdoc />
    public ConflictDef? FindConflict(string id) => id is not null && _conflicts.TryGetValue(id, out var c) ? c : null;

    /// <inheritdoc />
    public ErrorCodeMatch? FindCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = CodeNormalizer.Normalize(text);
        if (_codes.TryGetValue(normalized, out var hit))
        {
            return new ErrorCodeMatch { Def = hit.Def, Normalized = normalized, Exact = true };
        }

        foreach (var variant in CodeNormalizer.Variants(normalized))
        {
            if (_codes.TryGetValue(variant, out hit))
            {
                return new ErrorCodeMatch { Def = hit.Def, Normalized = variant, Exact = false };
            }
        }

        foreach (var extracted in CodeNormalizer.Extract(text))
        {
            if (!string.Equals(extracted, normalized, StringComparison.Ordinal) && _codes.TryGetValue(extracted, out hit))
            {
                return new ErrorCodeMatch { Def = hit.Def, Normalized = extracted, Exact = false };
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string Text(string key, string language, IReadOnlyDictionary<string, object>? args = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var lang = language ?? VTD.Contracts.Languages.English;
        string? pattern = null;
        if (_strings.TryGetValue(lang, out var table) && table.TryGetValue(key, out var text))
        {
            pattern = text;
        }
        else if (_strings.TryGetValue(VTD.Contracts.Languages.English, out var en) && en.TryGetValue(key, out var enText))
        {
            pattern = enText;
            lang = VTD.Contracts.Languages.English;
        }

        return pattern is null ? key : MessageFormatter.Format(pattern, lang, args);
    }

    /// <inheritdoc />
    public bool HasText(string key, string language)
        => key is not null && language is not null && _strings.TryGetValue(language, out var t) && t.ContainsKey(key);

    /// <summary>All keys of a language (lint, tests).</summary>
    /// <param name="language">Language.</param>
    public IReadOnlyCollection<string> Keys(string language)
        => _strings.TryGetValue(language, out var t) ? [.. t.Keys] : [];

    /// <summary>Raw pattern of a key in a language without fallback, or null.</summary>
    /// <param name="key">Key.</param>
    /// <param name="language">Language.</param>
    public string? RawText(string key, string language)
        => _strings.TryGetValue(language, out var t) && t.TryGetValue(key, out var v) ? v : null;

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> id)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            map.TryAdd(id(item), item);
        }

        return map;
    }
}
