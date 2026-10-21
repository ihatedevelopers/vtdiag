using System.Text;
using System.Text.Json;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;

namespace VTD.Knowledge.Packs;

/// <summary>How strictly a pack is read.</summary>
public enum KbReadMode
{
    /// <summary>Strict: unknown properties are errors (bundled and dev packs: same producer as the app).</summary>
    Strict = 0,

    /// <summary>Lenient: after schema validation, unknown properties from a newer producer are skipped (downloaded packs).</summary>
    Lenient = 1,
}

/// <summary>Reads the data files of a KB folder into <see cref="KbContent"/>. Never throws on data: problems become issues.</summary>
public static class KbPackReader
{
    /// <summary>The schema that validates a pack-relative path, or null when the path is not a KB data file.</summary>
    /// <param name="path">Path relative to the pack, forward slashes.</param>
    public static string? SchemaFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var top = path.Split('/')[0];
        return top switch
        {
            "rules" => "rule",
            "signals" => "signal",
            "collectors" => "collector",
            "conflicts" => "conflict",
            "codes" => "code",
            "fixes" => "fix",
            "guides" => "guide",
            "questions" => "question",
            "symptoms.json" or "symptoms" => "symptom",
            "tables" => "table",
            "strings" => "strings",
            "trusted_helpers.json" => "trusted-helpers",
            _ => null,
        };
    }

    /// <summary>Reads a set of files (already hash-checked) from a pack folder.</summary>
    /// <param name="files">Pack-relative path to file bytes.</param>
    /// <param name="mode">Read mode.</param>
    /// <param name="schemas">Schema name to validator; when null, schema validation is skipped (typed parsing still applies).</param>
    /// <param name="issues">Issues sink.</param>
    public static KbContent Read(IReadOnlyDictionary<string, byte[]> files, KbReadMode mode, IReadOnlyDictionary<string, MiniJsonSchema>? schemas, List<KbIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(issues);
        var rules = new List<RuleDef>();
        var signals = new List<SignalDef>();
        var collectors = new List<CollectorDef>();
        var conflicts = new List<ConflictDef>();
        var codes = new List<ErrorCodeDef>();
        var fixes = new List<FixDef>();
        var guides = new List<GuideDef>();
        var questions = new List<QuestionDef>();
        var symptoms = new List<SymptomDef>();
        var tables = new List<TableDef>();
        var helpers = new List<TrustedHelperKey>();
        var strings = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var schemaName = SchemaFor(path);
            if (schemaName is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (schemas is not null && schemas.TryGetValue(schemaName, out var schema))
                {
                    var errors = schema.Validate(doc.RootElement, allowAdditionalProperties: mode == KbReadMode.Lenient);
                    foreach (var e in errors)
                    {
                        issues.Add(new KbIssue { Code = "schema", Severity = KbIssueSeverity.Error, Path = path, Message = e });
                    }

                    if (errors.Count > 0)
                    {
                        continue;
                    }
                }

                var isArray = doc.RootElement.ValueKind == JsonValueKind.Array;
                var json = Encoding.UTF8.GetString(bytes);
                switch (schemaName)
                {
                    case "rule": AddAll(rules, json, isArray, mode); break;
                    case "signal": AddAll(signals, json, isArray, mode); break;
                    case "collector": AddAll(collectors, json, isArray, mode); break;
                    case "conflict": AddAll(conflicts, json, isArray, mode); break;
                    case "code": AddAll(codes, json, isArray, mode); break;
                    case "fix": AddAll(fixes, json, isArray, mode); break;
                    case "guide": AddAll(guides, json, isArray, mode); break;
                    case "question": AddAll(questions, json, isArray, mode); break;
                    case "symptom": AddAll(symptoms, json, isArray, mode); break;
                    case "table": AddAll(tables, json, isArray, mode); break;
                    case "trusted-helpers": helpers.AddRange(Parse<IReadOnlyList<TrustedHelperKey>>(json, mode)); break;
                    case "strings":
                        var lang = Path.GetFileNameWithoutExtension(path);
                        strings[lang] = Parse<IReadOnlyDictionary<string, string>>(json, mode);
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                issues.Add(new KbIssue { Code = "parse", Severity = KbIssueSeverity.Error, Path = path, Message = e.Message });
            }
        }

        Duplicates(rules.Select(r => r.Id), "rule", issues);
        Duplicates(signals.Select(s => s.Id), "signal", issues);
        Duplicates(collectors.Select(c => c.Id), "collector", issues);
        Duplicates(conflicts.Select(c => c.Id), "conflict", issues);
        Duplicates(codes.Select(c => c.Id), "code", issues);
        Duplicates(fixes.Select(f => f.Id), "fix", issues);
        Duplicates(guides.Select(g => g.Id), "guide", issues);
        Duplicates(questions.Select(q => q.Id), "question", issues);
        Duplicates(symptoms.Select(s => s.Id), "symptom", issues);
        Duplicates(tables.Select(t => t.Id), "table", issues);

        return new KbContent
        {
            Rules = rules, Signals = signals, Collectors = collectors, Conflicts = conflicts, ErrorCodes = codes, Fixes = fixes,
            Guides = guides, Questions = questions, Symptoms = symptoms, Tables = tables, TrustedHelpers = helpers, Strings = strings,
        };
    }

    private static void AddAll<T>(List<T> target, string json, bool isArray, KbReadMode mode)
    {
        if (isArray)
        {
            target.AddRange(Parse<IReadOnlyList<T>>(json, mode));
        }
        else
        {
            target.Add(Parse<T>(json, mode));
        }
    }

    private static T Parse<T>(string json, KbReadMode mode)
        => mode == KbReadMode.Lenient ? VtdJson.DeserializeLenient<T>(json) : VtdJson.Deserialize<T>(json);

    private static void Duplicates(IEnumerable<string> ids, string what, List<KbIssue> issues)
    {
        foreach (var dup in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal))
        {
            issues.Add(new KbIssue { Code = "duplicate." + what, Severity = KbIssueSeverity.Error, Message = $"{what} id '{dup}' is defined more than once." });
        }
    }
}
