using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VTD.Contracts.Assist;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Tests;

/// <summary>How a schema's root is shaped.</summary>
internal enum SchemaShape
{
    /// <summary>The root is one object of the type.</summary>
    Object,

    /// <summary>KB files: one object, or an array of objects.</summary>
    ObjectOrArray,
}

/// <summary>One generated schema file.</summary>
internal sealed record SchemaSpec(string Name, Type Type, SchemaShape Shape, string Title, string Description);

/// <summary>
/// Generates <c>kb/schema/*.schema.json</c> (JSON Schema 2020-12) from the C# contracts with
/// <see cref="JsonSchemaExporter"/>, using the strict <see cref="VtdJson.Options"/>, the XML documentation of
/// VTD.Contracts for descriptions, and a hand-written node for <see cref="Fact{T}"/> (its JSON is produced by a custom
/// converter the exporter cannot see through).
/// </summary>
internal static partial class SchemaGenerator
{
    public static IReadOnlyList<SchemaSpec> Specs { get; } =
    [
        new("snapshot", typeof(ScanSnapshot), SchemaShape.Object, "VTDiag scan snapshot", "One full PC scan: three-valued facts (known / unknown with reason / notApplicable) grouped in sections. Unknown is never False."),
        new("rule", typeof(RuleDef), SchemaShape.ObjectOrArray, "VTDiag KB rule", "kb/rules/<category>/<id>.json: a hypothesis or route rule made of weighted signal references."),
        new("signal", typeof(SignalDef), SchemaShape.ObjectOrArray, "VTDiag KB signal declaration", "kb/signals/*.json: declares signal ids so every reference can be checked; collector-derived signals say how they are computed."),
        new("collector", typeof(CollectorDef), SchemaShape.ObjectOrArray, "VTDiag KB pack-declared collector", "kb/collectors/*.json: read-only collectors (eventLog, uninstallRegex, loadedDriver, faultingModule, registryValue, fileExists) that let mods ship new detections as data."),
        new("conflict", typeof(ConflictDef), SchemaShape.ObjectOrArray, "VTDiag KB conflicting software", "kb/conflicts/<kind>.json: known conflicting software with detection fingerprints, imported from research/conflicts.json."),
        new("code", typeof(ErrorCodeDef), SchemaShape.ObjectOrArray, "VTDiag KB error code", "kb/codes/*.json: VAN/VAL codes with per-cause strength (logged > typed) and route verdicts."),
        new("fix", typeof(FixDef), SchemaShape.ObjectOrArray, "VTDiag KB fix", "kb/fixes/<area>/<id>.json: a fix composed only of audited primitives; no commands, scripts, URLs or free paths."),
        new("guide", typeof(GuideDef), SchemaShape.ObjectOrArray, "VTDiag KB guide", "kb/guides/<id>.json: a guided manual flow (BIOS, Secure Boot, ISP, uninstall) with branches and verification."),
        new("question", typeof(QuestionDef), SchemaShape.ObjectOrArray, "VTDiag KB question", "kb/questions/*.json: intake questions; each answer becomes an ask.* signal."),
        new("symptom", typeof(SymptomDef), SchemaShape.ObjectOrArray, "VTDiag KB symptom chip", "kb/symptoms.json: the What's wrong? chips; they set priors and never veto."),
        new("table", typeof(TableDef), SchemaShape.ObjectOrArray, "VTDiag KB lookup table", "kb/tables/*.json: data tables such as BIOS key/menu paths per vendor."),
        new("manifest", typeof(KbManifest), SchemaShape.Object, "VTDiag KB manifest", "kb/kb.manifest.json: every file of the pack with its SHA-256; signed with Ed25519 into kb.manifest.sig."),
        new("revocations", typeof(KbRevocationList), SchemaShape.Object, "VTDiag KB revocation list", "kb.revocations.json: signed, monotonic list of revoked KB versions and keys."),
        new("trusted-helpers", typeof(IReadOnlyList<TrustedHelperKey>), SchemaShape.Object, "VTDiag trusted helper keys", "kb/trusted_helpers.json: keys whose plans show as Verified helper."),
        new("strings", typeof(IReadOnlyDictionary<string, string>), SchemaShape.Object, "VTDiag KB string table", "kb/strings/<lang>.json: flat key to ICU MessageFormat text."),
        new("plan", typeof(FixPlan), SchemaShape.Object, "VTDiag helper plan (.vtdplan)", "A helper's (or a helper's AI's) plan: only KB fix, guide, question and monitor ids with entity bindings. No commands, paths, registry keys or URLs as actions. Every step still needs the player's consent."),
        new("plan-envelope", typeof(SignedPlanEnvelope), SchemaShape.Object, "VTDiag signed plan envelope", "A signed .vtdplan: base64 plan JSON bytes plus a detached Ed25519 signature over exactly those bytes."),
        new("report", typeof(ReportModel), SchemaShape.Object, "VTDiag report (.vtdreport)", "The machine-readable report: redacted snapshot, analysis with honesty buckets, actions taken, monitor summary, redaction manifest."),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Docs = new(LoadDocs);

    private static JsonSchemaExporterOptions ExporterOptions { get; } = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = Transform,
    };

    public static string Generate(SchemaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var item = Export(spec.Type);
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"urn:vtdiag:schema:{spec.Name}:1",
            ["title"] = spec.Title,
            ["description"] = spec.Description,
        };

        if (spec.Shape == SchemaShape.ObjectOrArray)
        {
            root["$defs"] = new JsonObject { ["item"] = item };
            root["anyOf"] = new JsonArray(
                new JsonObject { ["$ref"] = "#/$defs/item" },
                new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/item" } });
        }
        else if (item is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToList())
            {
                if (key is "description")
                {
                    continue;
                }

                obj.Remove(key);
                root[key] = value;
            }
        }

        var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return RepoPaths.Normalize(text);
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        var type = context.TypeInfo.Type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Fact<>))
        {
            node = FactSchema(type.GetGenericArguments()[0]);
        }

        var description = context.PropertyInfo?.AttributeProvider is PropertyInfo prop
            ? Doc("P:" + DocName(prop.DeclaringType!) + "." + prop.Name)
            : context.PropertyInfo is null ? Doc("T:" + DocName(type)) : null;

        if (description is not null)
        {
            if (node is not JsonObject)
            {
                node = new JsonObject();
            }

            ((JsonObject)node)["description"] = description;
        }

        return node;
    }

    /// <summary>
    /// Exports one type and inlines every <c>$ref</c> the exporter produced for repeated types. The refs are JSON pointers
    /// into this export's own document, so they must be resolved before the node is embedded anywhere else.
    /// </summary>
    private static JsonNode Export(Type type)
    {
        var root = JsonSchemaExporter.GetJsonSchemaAsNode(VtdJson.Options, type, ExporterOptions);
        return Inline(root, root, 0);
    }

    private static JsonNode Inline(JsonNode node, JsonNode root, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("Recursive contract type: schemas must not contain recursion.");
        }

        switch (node)
        {
            case JsonObject obj when obj["$ref"] is JsonValue refValue && refValue.GetValue<string>().StartsWith("#/", StringComparison.Ordinal):
                var target = Resolve(root, refValue.GetValue<string>()).DeepClone();
                var resolved = Inline(target, root, depth + 1);
                if (resolved is JsonObject resolvedObj)
                {
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (key != "$ref")
                        {
                            obj.Remove(key);
                            resolvedObj[key] = value;
                        }
                    }
                }

                return resolved;
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is not null)
                    {
                        var replaced = Inline(value, root, depth + 1);
                        if (!ReferenceEquals(replaced, value))
                        {
                            obj[key] = replaced;
                        }
                    }
                }

                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is { } element)
                    {
                        var replaced = Inline(element, root, depth + 1);
                        if (!ReferenceEquals(replaced, element))
                        {
                            arr[i] = replaced;
                        }
                    }
                }

                return arr;
            default:
                return node;
        }
    }

    private static JsonNode Resolve(JsonNode root, string pointer)
    {
        var current = root;
        foreach (var raw in pointer[2..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject o => o[segment] ?? throw new InvalidOperationException($"Unresolvable $ref {pointer}"),
                JsonArray a => a[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] ?? throw new InvalidOperationException($"Unresolvable $ref {pointer}"),
                _ => throw new InvalidOperationException($"Unresolvable $ref {pointer}"),
            };
        }

        return current;
    }

    private static JsonObject FactSchema(Type valueType)
    {
        var value = Export(valueType);
        var reason = Export(typeof(UnknownReason));
        static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

        JsonObject Branch(string status, JsonObject extra, params string[] required)
        {
            var props = new JsonObject { ["status"] = new JsonObject { ["const"] = status } };
            foreach (var (k, v) in extra.ToList())
            {
                extra.Remove(k);
                props[k] = v;
            }

            props["detail"] = Text("Short invariant machine detail, never PII.");
            props["source"] = Text("Provenance: <collectorId>/<method>.");
            var req = new JsonArray();
            foreach (var r in required)
            {
                req.Add(r);
            }

            return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req, ["additionalProperties"] = false };
        }

        return new JsonObject
        {
            ["oneOf"] = new JsonArray(
                Branch("known", new JsonObject { ["value"] = value, ["complete"] = new JsonObject { ["type"] = "boolean", ["description"] = "false: a partial list (some entries unreadable or capped); absence from it is Unknown." } }, "status", "value"),
                Branch("unknown", new JsonObject { ["reason"] = reason }, "status", "reason"),
                Branch("notApplicable", new JsonObject(), "status")),
        };
    }

    private static string DocName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    private static string? Doc(string id) => Docs.Value.TryGetValue(id, out var text) ? text : null;

    private static IReadOnlyDictionary<string, string> LoadDocs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "VTD.Contracts.xml");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("VTD.Contracts.xml (documentation file) must be copied next to the test assembly.", path);
        }

        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null)
            {
                continue;
            }

            result[name] = Flatten(summary);
        }

        return result;
    }

    private static string Flatten(XElement element)
    {
        var parts = new List<string>();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    parts.Add(text.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } see:
                    var cref = see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? string.Empty;
                    var shortName = cref.Contains(':', StringComparison.Ordinal) ? cref[(cref.IndexOf(':', StringComparison.Ordinal) + 1)..] : cref;
                    shortName = shortName.Split('(')[0];
                    var segments = shortName.Split('.');
                    parts.Add(see.Value.Length > 0 ? see.Value : segments[^1].Split('`')[0]);
                    break;
                case XElement { Name.LocalName: "item" } item:
                    parts.Add(" - " + Flatten(item));
                    break;
                case XElement child:
                    parts.Add(Flatten(child));
                    break;
            }
        }

        return Whitespace().Replace(string.Concat(parts), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
