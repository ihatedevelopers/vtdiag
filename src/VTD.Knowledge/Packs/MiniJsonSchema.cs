using System.Globalization;
using System.Text.Json;

namespace VTD.Knowledge.Packs;

/// <summary>
/// A small JSON Schema (2020-12) validator for the subset that <c>kb/schema/*.schema.json</c> uses: <c>type</c>,
/// <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>items</c>, <c>enum</c>, <c>const</c>,
/// <c>anyOf</c>/<c>oneOf</c>/<c>allOf</c> and local <c>$ref</c>. <c>format</c> and annotations are ignored.
/// The app ships no schema package; tests cross-check the KB with JsonSchema.Net as well.
/// </summary>
public sealed class MiniJsonSchema : IDisposable
{
    private const int MaxErrors = 20;
    private readonly JsonDocument _doc;

    private MiniJsonSchema(JsonDocument doc) => _doc = doc;

    /// <summary>Parses a schema.</summary>
    /// <param name="utf8">Schema bytes.</param>
    public static MiniJsonSchema Parse(ReadOnlyMemory<byte> utf8) => new(JsonDocument.Parse(utf8));

    /// <summary>Validates an instance.</summary>
    /// <param name="instance">Instance.</param>
    /// <param name="allowAdditionalProperties">Lenient mode: a newer producer may add fields (<c>additionalProperties: false</c> is ignored).</param>
    public IReadOnlyList<string> Validate(JsonElement instance, bool allowAdditionalProperties = false)
    {
        var errors = new List<string>();
        Check(_doc.RootElement, instance, "$", errors, allowAdditionalProperties, 0);
        return errors;
    }

    /// <inheritdoc />
    public void Dispose() => _doc.Dispose();

    private void Check(JsonElement schema, JsonElement inst, string path, List<string> errors, bool lenient, int depth)
    {
        if (errors.Count >= MaxErrors || depth > 64)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{path}: not allowed");
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out var refEl) && refEl.GetString() is { } reference)
        {
            if (Resolve(reference) is { } target)
            {
                Check(target, inst, path, errors, lenient, depth + 1);
            }
            else
            {
                errors.Add($"{path}: unresolved $ref {reference}");
            }
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, inst))
        {
            errors.Add($"{path}: expected {type.GetRawText()}, got {inst.ValueKind}");
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, inst))
        {
            errors.Add($"{path}: expected const {constant.GetRawText()}");
        }

        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array
            && !enumEl.EnumerateArray().Any(e => JsonEquals(e, inst)))
        {
            errors.Add($"{path}: value {Short(inst)} is not one of {Short(enumEl)}");
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var s in allOf.EnumerateArray())
            {
                Check(s, inst, path, errors, lenient, depth + 1);
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && !anyOf.EnumerateArray().Any(s => Passes(s, inst, path, lenient, depth)))
        {
            errors.Add($"{path}: matches none of anyOf");
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.EnumerateArray().Count(s => Passes(s, inst, path, lenient, depth)) != 1)
        {
            errors.Add($"{path}: must match exactly one of oneOf");
        }

        if (inst.ValueKind == JsonValueKind.Object)
        {
            CheckObject(schema, inst, path, errors, lenient, depth);
        }
        else if (inst.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in inst.EnumerateArray())
            {
                Check(items, item, string.Create(CultureInfo.InvariantCulture, $"{path}[{i++}]"), errors, lenient, depth + 1);
            }
        }
    }

    private void CheckObject(JsonElement schema, JsonElement inst, string path, List<string> errors, bool lenient, int depth)
    {
        var hasProps = schema.TryGetProperty("properties", out var props);
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var r in required.EnumerateArray())
            {
                if (r.GetString() is { } name && !inst.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}: missing required '{name}'");
                }
            }
        }

        schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var p in inst.EnumerateObject())
        {
            var childPath = path + "." + p.Name;
            if (hasProps && props.TryGetProperty(p.Name, out var ps))
            {
                Check(ps, p.Value, childPath, errors, lenient, depth + 1);
            }
            else if (additional.ValueKind == JsonValueKind.False)
            {
                if (!lenient)
                {
                    errors.Add($"{childPath}: unknown property");
                }
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                Check(additional, p.Value, childPath, errors, lenient, depth + 1);
            }
        }
    }

    private bool Passes(JsonElement schema, JsonElement inst, string path, bool lenient, int depth)
    {
        var errs = new List<string>();
        Check(schema, inst, path, errs, lenient, depth + 1);
        return errs.Count == 0;
    }

    private JsonElement? Resolve(string reference)
    {
        if (string.Equals(reference, "#", StringComparison.Ordinal))
        {
            return _doc.RootElement;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        var current = _doc.RootElement;
        foreach (var raw in reference[2..].Split('/'))
        {
            var seg = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(seg, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static bool TypeMatches(JsonElement type, JsonElement inst)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return TypeMatches(type.GetString()!, inst);
        }

        return type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.GetString() is { } s && TypeMatches(s, inst));
    }

    private static bool TypeMatches(string type, JsonElement inst) => type switch
    {
        "object" => inst.ValueKind == JsonValueKind.Object,
        "array" => inst.ValueKind == JsonValueKind.Array,
        "string" => inst.ValueKind == JsonValueKind.String,
        "number" => inst.ValueKind == JsonValueKind.Number,
        "integer" => inst.ValueKind == JsonValueKind.Number && inst.TryGetInt64(out _),
        "boolean" => inst.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => inst.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => a.GetDouble().Equals(b.GetDouble()),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal),
        };
    }

    private static string Short(JsonElement e)
    {
        var text = e.GetRawText();
        return text.Length <= 80 ? text : text[..77] + "...";
    }
}
