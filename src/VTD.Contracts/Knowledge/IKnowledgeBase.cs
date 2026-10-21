namespace VTD.Contracts.Knowledge;

/// <summary>
/// A loaded, verified knowledge base (A2). Immutable for the life of a session: a KB update that arrives mid-session or
/// across a reboot applies only to new sessions (pinned by <see cref="Version"/>).
/// </summary>
public interface IKnowledgeBase
{
    /// <summary>Version and source.</summary>
    KbVersion Version { get; }

    /// <summary>Hypothesis and route rules.</summary>
    IReadOnlyList<RuleDef> Rules { get; }

    /// <summary>Declared signals.</summary>
    IReadOnlyList<SignalDef> Signals { get; }

    /// <summary>Pack-declared read-only collectors (passed to the scan in <see cref="Scanning.ScanRequest.PackCollectors"/>).</summary>
    IReadOnlyList<CollectorDef> Collectors { get; }

    /// <summary>Conflicting software entries.</summary>
    IReadOnlyList<ConflictDef> Conflicts { get; }

    /// <summary>Error-code table.</summary>
    IReadOnlyList<ErrorCodeDef> ErrorCodes { get; }

    /// <summary>Fixes.</summary>
    IReadOnlyList<FixDef> Fixes { get; }

    /// <summary>Guides.</summary>
    IReadOnlyList<GuideDef> Guides { get; }

    /// <summary>Questions.</summary>
    IReadOnlyList<QuestionDef> Questions { get; }

    /// <summary>Symptom chips.</summary>
    IReadOnlyList<SymptomDef> Symptoms { get; }

    /// <summary>Lookup tables.</summary>
    IReadOnlyList<TableDef> Tables { get; }

    /// <summary>Keys trusted to sign helper plans.</summary>
    IReadOnlyList<TrustedHelperKey> TrustedHelpers { get; }

    /// <summary>Languages with a string table.</summary>
    IReadOnlyList<string> Languages { get; }

    /// <summary>Finds a rule.</summary>
    /// <param name="id">Rule id.</param>
    RuleDef? FindRule(string id);

    /// <summary>Finds a fix.</summary>
    /// <param name="id">Fix id.</param>
    FixDef? FindFix(string id);

    /// <summary>Finds a guide.</summary>
    /// <param name="id">Guide id.</param>
    GuideDef? FindGuide(string id);

    /// <summary>Finds a question.</summary>
    /// <param name="id">Question id.</param>
    QuestionDef? FindQuestion(string id);

    /// <summary>Finds a conflict entry.</summary>
    /// <param name="id">Conflict id.</param>
    ConflictDef? FindConflict(string id);

    /// <summary>Normalizes and looks up a typed or logged code (<c>van9003</c>, <c>VAN-9003</c>, <c>VAN: 9003</c> → <c>VAN 9003</c>).</summary>
    /// <param name="text">Raw code text.</param>
    ErrorCodeMatch? FindCode(string text);

    /// <summary>
    /// Renders a string key in a language with ICU-style placeholders; falls back to English, then to the key itself.
    /// Numbers and dates are formatted with the language's culture for display only.
    /// </summary>
    /// <param name="key">String key.</param>
    /// <param name="language"><c>en</c> or <c>tr</c>.</param>
    /// <param name="args">Placeholder values.</param>
    string Text(string key, string language, IReadOnlyDictionary<string, object>? args = null);

    /// <summary>True when the key exists in the language (no fallback).</summary>
    /// <param name="key">String key.</param>
    /// <param name="language">Language.</param>
    bool HasText(string key, string language);
}
