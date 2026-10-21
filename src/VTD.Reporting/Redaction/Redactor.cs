using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using VTD.Contracts;
using VTD.Contracts.Assist;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;

namespace VTD.Reporting;

/// <summary>
/// The redactor (design_B.md section 12). Attribute-driven first: the model is serialized with <see cref="VtdJson"/>
/// and the JSON tree is walked together with the CLR type graph (property <see cref="SensitivityAttribute"/>, else the
/// declaring type's with <c>inherit: true</c>; <see cref="Fact{T}"/> values, polymorphic records, lists and dictionary
/// values inherit the property's level). Then every string, whatever its level, gets the text scrub, and the result is
/// deserialized strictly (so it is still a valid <typeparamref name="T"/>) and swept.
/// </summary>
/// <remarks>
/// <para>Share treatment per level: <c>Identifier</c> dropped (nullable → absent, Fact → Unknown(Redacted), required or
/// list item → <c>[redacted]</c>); <c>DeviceId</c> keeps enumerator + device segment, instance tail → salted hash;
/// <c>EntityId</c> per sibling <c>kind</c> (see <see cref="ShareEntityId"/>); <c>Path</c> → <c>%USERPROFILE%\…</c>;
/// <c>Hashed</c> → <c>h:</c> + salted HMAC; <c>MacAddress</c> → OUI + hash; <c>IpAddress</c> → class (the monitor's
/// game-server IP is dropped unless <see cref="RedactionPolicy.IncludeServerIp"/>, its region text stays);
/// <c>None</c>/<c>FreeText</c> → text scrub.</para>
/// <para>The text scrub removes secrets (auth tokens, key=value secrets, ids next to their key, JWTs), e-mails, SIDs,
/// profile paths, OneDrive organizations, Windows' default computer names and every identifier value: the ones passed
/// to the constructor (the engine passes the computer name and the player's user name), the model's own
/// <c>Identifier</c> values and the user names found in its profile paths, all collected in a first pass.</para>
/// <para>Excluded sections: a <see cref="Fact{T}"/> section becomes Unknown(Redacted); a record section (for example
/// <c>network</c>) gets every inner fact Unknown(Redacted), lists and maps emptied and optional values removed. For a
/// <see cref="ReportModel"/> or <see cref="ScanBundle"/>, <c>analysis</c>, <c>attempts</c> and <c>monitor</c> can be
/// excluded too.</para>
/// <para>Local level only scrubs secrets. The salt never leaves this object.</para>
/// </remarks>
public sealed class Redactor : IRedactor
{
    private static readonly ConcurrentDictionary<(Type, string), Sensitivity?> SensitivityCache = new();
    private static readonly ConcurrentDictionary<Type, Type?> FactValueTypes = new();

    private readonly ShareTransforms _share;
    private readonly IdentifierSet _known;

    /// <summary>Creates a redactor.</summary>
    /// <param name="installSalt">Per-install random salt (at least 16 bytes). Never exported.</param>
    public Redactor(byte[] installSalt)
        : this(installSalt, null)
    {
    }

    /// <summary>Creates a redactor that also removes identifiers it cannot find in the model itself.</summary>
    /// <param name="installSalt">Per-install random salt (at least 16 bytes). Never exported.</param>
    /// <param name="knownIdentifiers">Identifier values to remove everywhere (the engine passes the computer name, the
    /// domain and the interactive player's user name; none of them are snapshot fields).</param>
    public Redactor(byte[] installSalt, IEnumerable<string>? knownIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(installSalt);
        if (installSalt.Length < 16)
        {
            throw new ArgumentException("The install salt must be at least 16 random bytes.", nameof(installSalt));
        }

        _share = new ShareTransforms([.. installSalt]);
        _known = IdentifierSet.Create(knownIdentifiers ?? []);
    }

    /// <inheritdoc />
    public Redacted<T> Redact<T>(T model, RedactionPolicy policy)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(policy);
        var info = VtdJson.Options.GetTypeInfo(typeof(T));
        var root = JsonSerializer.SerializeToNode(model, info) ?? throw new ArgumentException("The model serialized to null.", nameof(model));
        var pass = new Pass(policy, _known);

        if (policy.Level != RedactionLevel.None)
        {
            ApplyExclusions(root, typeof(T), pass);
            if (policy.Level == RedactionLevel.Share)
            {
                pass.Collecting = true;
                _ = Visit(root, typeof(T), null, string.Empty, default, pass);
                pass.Collecting = false;
                pass.Identifiers = _known.With(pass.Collected);
            }

            root = Visit(root, typeof(T), null, string.Empty, default, pass) ?? root;
        }

        var value = VtdJson.Deserialize<T>(root.ToJsonString(VtdJson.Options));
        var manifest = new RedactionManifest
        {
            Level = policy.Level,
            RulesVersion = RedactionConstants.RulesVersion,
            Entries = pass.Entries(),
            ExcludedSections = [.. policy.ExcludedSections],
        };
        value = Stamp(value, policy.Level, pass, manifest);
        var hits = policy.Level == RedactionLevel.Share ? Sweep(VtdJson.Serialize(value)) : [];
        return new Redacted<T> { Value = value, Manifest = manifest, SweepHits = hits };
    }

    /// <inheritdoc />
    public string RedactText(string text, RedactionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Level switch
        {
            RedactionLevel.Share => TextScrubber.Scrub(text, ScrubMode.Full, _known, null),
            RedactionLevel.Local => TextScrubber.Scrub(text, ScrubMode.SecretsOnly, IdentifierSet.Empty, null),
            _ => text,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<SweepHit> Sweep(string json) => RedactionSweep.Sweep(json);

    /// <summary>
    /// True when <paramref name="text"/> still contains an identifier this redactor was told to remove (the player's user
    /// name, the computer name, the domain). The debug bundle's last line of defence; the value is never reported.
    /// </summary>
    /// <param name="text">Text.</param>
    public bool ContainsKnownIdentifier(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !string.Equals(_known.Replace(text, null), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The id an entity has in share-level output. Deterministic per salt:
    /// <list type="bullet">
    /// <item><c>Adapter</c>, <c>Device</c>: the device-id rule (enumerator + VEN/DEV kept, tail hashed).</item>
    /// <item><c>File</c>, <c>CompatLayer</c>: the path rule (<c>%USERPROFILE%\…</c>).</item>
    /// <item><c>Service</c>, <c>Process</c>, <c>Driver</c>, <c>WindowsFeature</c>, <c>PowerSetting</c>: unchanged (invariant names).</item>
    /// <item><c>App</c> (<c>HKLM:{GUID}</c>), <c>StartupItem</c> (<c>&lt;location&gt;|&lt;name&gt;</c>), <c>RegistryValue</c>:
    /// the text scrub (profile paths, SIDs and known identifiers removed; product GUIDs and names kept), the same
    /// treatment their source fields get in the share snapshot.</item>
    /// <item><c>AudioEndpoint</c>: <c>h:</c> + salted hash (endpoint ids and names are hashed in the snapshot too).</item>
    /// </list>
    /// </summary>
    /// <param name="entity">Live entity.</param>
    public string ShareEntityId(EntityRef entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ShareEntityId(entity.Kind, entity.Id, _known);
    }

    /// <summary>The share id with extra identifiers (collected from the model or, in the plan validator, from the fresh
    /// snapshot) hashed in place, so ids stay distinct and both sides compute the same value.</summary>
    /// <param name="entity">Entity.</param>
    /// <param name="extraIdentifiers">Identifiers beyond the constructor's.</param>
    internal string ShareEntityId(EntityRef entity, IdentifierSet extraIdentifiers)
        => ShareEntityId(entity.Kind, entity.Id, _known.With(extraIdentifiers.Values));

    /// <summary>The identifiers the first pass of <see cref="Redact{T}"/> collects from a model (for the plan validator).</summary>
    /// <param name="snapshot">Snapshot.</param>
    internal IdentifierSet CollectIdentifiers(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = JsonSerializer.SerializeToNode(snapshot, VtdJson.Options.GetTypeInfo(typeof(ScanSnapshot)))!;
        var pass = new Pass(RedactionPolicy.Share, _known) { Collecting = true };
        _ = Visit(root, typeof(ScanSnapshot), null, string.Empty, default, pass);
        return IdentifierSet.Create(pass.Collected);
    }

    private string ShareEntityId(EntityKind kind, string id, IdentifierSet identifiers) => kind switch
    {
        EntityKind.Adapter or EntityKind.Device => TextScrubber.Scrub(_share.DeviceId(id), ScrubMode.Full, identifiers, null, HashIdentifier),
        EntityKind.Service or EntityKind.Process or EntityKind.Driver or EntityKind.WindowsFeature or EntityKind.PowerSetting => id,
        EntityKind.AudioEndpoint => _share.Hash(id),
        _ => TextScrubber.Scrub(id, ScrubMode.Full, identifiers, null, HashIdentifier),
    };

    private string HashIdentifier(string matched) => _share.Hash("id:" + IdentifierSet.Fold(matched));

    // ---------------------------------------------------------------- walk

    private JsonNode? Visit(JsonNode? node, Type type, Sensitivity? sensitivity, string pointer, Site site, Pass pass)
    {
        if (node is null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            return node is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? Leaf(v, v.GetValue<string>(), sensitivity, pointer, site, pass)
                : node;
        }

        if (FactValueType(type) is { } valueType)
        {
            return node is JsonObject fact ? VisitFact(fact, valueType, sensitivity, pointer, site, pass) : node;
        }

        if (type.IsEnum || type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTimeOffset) || type == typeof(DateTime)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Guid))
        {
            return node;
        }

        JsonTypeInfo info;
        try
        {
            info = VtdJson.Options.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            return node;
        }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Dictionary when node is JsonObject map && info.ElementType is { } dictValue:
                VisitDictionary(map, dictValue, sensitivity, pointer, pass);
                return node;
            case JsonTypeInfoKind.Enumerable when node is JsonArray array && info.ElementType is { } element:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    var itemSite = new Site(null, null, null, InCollection: true);
                    var replaced = Visit(child, element, sensitivity, pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), itemSite, pass);
                    if (!ReferenceEquals(replaced, child))
                    {
                        array[i] = replaced ?? JsonValue.Create(TextPatterns.Redacted);
                    }
                }

                return node;
            case JsonTypeInfoKind.Object when node is JsonObject obj:
                VisitObject(obj, info, pointer, pass);
                return node;
            default:
                return node;
        }
    }

    private void VisitObject(JsonObject obj, JsonTypeInfo info, string pointer, Pass pass)
    {
        if (info.PolymorphismOptions is { } poly
            && obj[poly.TypeDiscriminatorPropertyName] is JsonValue discriminator
            && discriminator.GetValueKind() == JsonValueKind.String)
        {
            var id = discriminator.GetValue<string>();
            var derived = poly.DerivedTypes.FirstOrDefault(d => d.TypeDiscriminator is string s && string.Equals(s, id, StringComparison.Ordinal));
            if (derived.DerivedType is not null)
            {
                info = VtdJson.Options.GetTypeInfo(derived.DerivedType);
            }
        }

        foreach (var property in info.Properties)
        {
            if (!obj.TryGetPropertyValue(property.Name, out var child) || child is null)
            {
                continue;
            }

            var sensitivity = SensitivityOf(info.Type, property);
            var site = new Site(property, obj, info.Type, InCollection: false);
            var replaced = Visit(child, property.PropertyType, sensitivity, pointer + "/" + RedactionSweep.EscapePointer(property.Name), site, pass);
            if (ReferenceEquals(replaced, child))
            {
                continue;
            }

            if (replaced is null)
            {
                obj.Remove(property.Name);
            }
            else
            {
                obj[property.Name] = replaced;
            }
        }
    }

    private JsonNode VisitFact(JsonObject fact, Type valueType, Sensitivity? sensitivity, string pointer, Site site, Pass pass)
    {
        if (sensitivity == Sensitivity.Identifier && pass.Policy.Level == RedactionLevel.Share && !pass.Collecting)
        {
            pass.Record(pointer, ShareTransforms.Rules.FactRedacted, 1);
            return UnknownRedacted();
        }

        if (fact.TryGetPropertyValue("value", out var value) && value is not null)
        {
            var valueSite = new Site(null, fact, null, InCollection: true);
            var replaced = Visit(value, valueType, sensitivity, pointer + "/value", valueSite, pass);
            if (replaced is null)
            {
                pass.Record(pointer, ShareTransforms.Rules.FactRedacted, 1);
                return UnknownRedacted();
            }

            if (!ReferenceEquals(replaced, value))
            {
                fact["value"] = replaced;
            }
        }

        foreach (var name in (string[])["detail", "source"])
        {
            if (fact[name] is JsonValue text && text.GetValueKind() == JsonValueKind.String)
            {
                var replaced = Leaf(text, text.GetValue<string>(), Sensitivity.None, pointer + "/" + name, default, pass);
                if (!ReferenceEquals(replaced, text))
                {
                    fact[name] = replaced;
                }
            }
        }

        return fact;
    }

    private void VisitDictionary(JsonObject map, Type valueType, Sensitivity? sensitivity, string pointer, Pass pass)
    {
        var entries = map.ToList();
        map.Clear();
        foreach (var (key, child) in entries)
        {
            var newKey = ScrubString(key, Sensitivity.None, pointer, pass);
            if (map.ContainsKey(newKey))
            {
                pass.Record(pointer, ShareTransforms.Rules.KeyCollision, 1);
                continue;
            }

            var childPointer = pointer + "/" + RedactionSweep.EscapePointer(newKey);
            // A key that says what its value is ({"SSID": ...}, {"Password": ...}) overrides the map's own level.
            var valueSensitivity = SensitiveKeys.Classify(key) ?? sensitivity;
            var replaced = Visit(child, valueType, valueSensitivity, childPointer, new Site(null, null, null, InCollection: true), pass);
            map[newKey] = replaced is null && child is not null ? JsonValue.Create(TextPatterns.Redacted) : replaced;
        }
    }

    private JsonNode? Leaf(JsonValue node, string value, Sensitivity? sensitivity, string pointer, Site site, Pass pass)
    {
        var level = sensitivity ?? Sensitivity.FreeText;
        if (pass.Collecting)
        {
            switch (level)
            {
                case Sensitivity.Identifier or Sensitivity.Hashed:
                    pass.Collected.Add(value);
                    break;
                case Sensitivity.MacAddress:
                    pass.Collected.AddRange(MacVariants(value));
                    break;
                case Sensitivity.IpAddress when ShareTransforms.IpClass(value) is not ("loopback" or "unspecified" or "[ip]"):
                    pass.Collected.Add(value);
                    break;
                default:
                    break;
            }

            pass.Collected.AddRange(TextScrubber.ProfileNames(value));
            return node;
        }

        if (pass.Policy.Level != RedactionLevel.Share)
        {
            var local = TextScrubber.Scrub(value, ScrubMode.SecretsOnly, IdentifierSet.Empty, (rule, count) => pass.Record(pointer, rule, count));
            return string.Equals(local, value, StringComparison.Ordinal) ? node : JsonValue.Create(local);
        }

        string? result;
        switch (level)
        {
            case Sensitivity.Identifier:
                return Drop(pointer, site, pass, ShareTransforms.Rules.IdentifierDrop);
            case Sensitivity.DeviceId:
                result = Changed(value, _share.DeviceId(value), pointer, ShareTransforms.Rules.DeviceTail, pass);
                result = ScrubString(result, level, pointer, pass);
                break;
            case Sensitivity.Hashed:
                result = Changed(value, _share.Hash(value), pointer, ShareTransforms.Rules.Hash, pass);
                break;
            case Sensitivity.MacAddress:
                result = Changed(value, _share.Mac(value), pointer, ShareTransforms.Rules.Mac, pass);
                break;
            case Sensitivity.IpAddress:
                if (IsServerIp(site))
                {
                    if (!pass.Policy.IncludeServerIp)
                    {
                        return Drop(pointer, site, pass, ShareTransforms.Rules.ServerIp);
                    }

                    result = value;
                    break;
                }

                result = Changed(value, ShareTransforms.IpClass(value), pointer, ShareTransforms.Rules.IpClass, pass);
                break;
            case Sensitivity.EntityId:
                result = EntityId(value, site, pointer, pass);
                break;
            default:
                result = ScrubString(value, level, pointer, pass);
                break;
        }

        return string.Equals(result, value, StringComparison.Ordinal) ? node : JsonValue.Create(result);
    }

    private string EntityId(string value, Site site, string pointer, Pass pass)
    {
        string share;
        if (site.Parent?["kind"] is JsonValue k && k.GetValueKind() == JsonValueKind.String && ShareTransforms.TryParseKind(k.GetValue<string>(), out var kind))
        {
            share = ShareEntityId(kind, value, pass.Identifiers);
        }
        else if (TextPatterns.LooksLikeDeviceId().IsMatch(value) && !value.Contains(':', StringComparison.Ordinal))
        {
            share = ShareEntityId(EntityKind.Device, value, pass.Identifiers);
        }
        else
        {
            share = TextScrubber.Scrub(value, ScrubMode.Full, pass.Identifiers, null, HashIdentifier);
        }

        if (!string.Equals(share, value, StringComparison.Ordinal))
        {
            pass.Record(pointer, "entity.share_id", 1);
        }

        return share;
    }

    private static IEnumerable<string> MacVariants(string mac)
    {
        var hex = new string([.. mac.Where(Uri.IsHexDigit)]);
        if (hex.Length != 12)
        {
            yield return mac;
            yield break;
        }

        var pairs = Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)).ToArray();
        yield return string.Join(':', pairs);
        yield return string.Join('-', pairs);
        yield return hex;
    }

    private static string ScrubString(string value, Sensitivity level, string pointer, Pass pass)
    {
        _ = level;
        if (pass.Collecting)
        {
            return value;
        }

        var mode = pass.Policy.Level == RedactionLevel.Share ? ScrubMode.Full : ScrubMode.SecretsOnly;
        var ids = pass.Policy.Level == RedactionLevel.Share ? pass.Identifiers : IdentifierSet.Empty;
        return TextScrubber.Scrub(value, mode, ids, (rule, count) => pass.Record(pointer, rule, count));
    }

    private static string Changed(string before, string after, string pointer, string rule, Pass pass)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            pass.Record(pointer, rule, 1);
        }

        return after;
    }

    private static JsonNode? Drop(string pointer, Site site, Pass pass, string rule)
    {
        pass.Record(pointer, rule, 1);
        var optional = site.Property is { IsRequired: false, IsSetNullable: true } && !site.InCollection;
        return optional ? null : JsonValue.Create(TextPatterns.Redacted);
    }

    private static bool IsServerIp(Site site)
        => site.Declaring == typeof(NetStats) && site.Property is { } p && string.Equals(p.Name, "endpointIp", StringComparison.Ordinal);

    private static JsonObject UnknownRedacted() => new() { ["status"] = "unknown", ["reason"] = "redacted" };

    // ---------------------------------------------------------------- exclusions

    private static void ApplyExclusions(JsonNode root, Type type, Pass pass)
    {
        var excluded = pass.Policy.ExcludedSections;
        if (excluded.Count == 0 || root is not JsonObject obj)
        {
            return;
        }

        bool Has(string name) => excluded.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));

        JsonObject? snapshot = null;
        string snapshotPointer = string.Empty;
        if (type == typeof(ScanSnapshot))
        {
            snapshot = obj;
        }
        else if (type == typeof(ReportModel) || type == typeof(ScanBundle))
        {
            snapshot = obj["snapshot"] as JsonObject;
            snapshotPointer = "/snapshot";
            if (Has("analysis") && obj.Remove("analysis"))
            {
                pass.Record("/analysis", ShareTransforms.Rules.SectionExcluded, 1);
            }

            var attemptsName = type == typeof(ReportModel) ? "attempts" : "journal";
            if ((Has("attempts") || Has("journal")) && obj.ContainsKey(attemptsName))
            {
                obj[attemptsName] = new JsonArray();
                pass.Record("/" + attemptsName, ShareTransforms.Rules.SectionExcluded, 1);
            }

            if (Has("monitor"))
            {
                if (type == typeof(ReportModel))
                {
                    obj.Remove("monitor");
                    obj.Remove("baseline");
                }
                else
                {
                    obj["monitor"] = new JsonArray();
                }

                pass.Record("/monitor", ShareTransforms.Rules.SectionExcluded, 1);
            }
        }

        if (snapshot is null)
        {
            return;
        }

        var info = VtdJson.Options.GetTypeInfo(typeof(ScanSnapshot));
        foreach (var property in info.Properties)
        {
            if (!Has(property.Name) || string.Equals(property.Name, "meta", StringComparison.Ordinal) || string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
            {
                continue;
            }

            snapshot[property.Name] = EmptySection(snapshot[property.Name], property.PropertyType);
            pass.Record(snapshotPointer + "/" + property.Name, ShareTransforms.Rules.SectionExcluded, 1);
        }
    }

    private static JsonNode? EmptySection(JsonNode? node, Type type)
    {
        if (FactValueType(type) is not null)
        {
            return UnknownRedacted();
        }

        var info = VtdJson.Options.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                return new JsonArray();
            case JsonTypeInfoKind.Dictionary:
                return new JsonObject();
            case JsonTypeInfoKind.Object when node is JsonObject section:
                foreach (var p in info.Properties)
                {
                    if (!section.ContainsKey(p.Name))
                    {
                        continue;
                    }

                    var propertyType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    if (FactValueType(propertyType) is not null)
                    {
                        section[p.Name] = UnknownRedacted();
                    }
                    else if (propertyType != typeof(string) && VtdJson.Options.GetTypeInfo(propertyType).Kind == JsonTypeInfoKind.Enumerable)
                    {
                        section[p.Name] = new JsonArray();
                    }
                    else if (propertyType != typeof(string) && VtdJson.Options.GetTypeInfo(propertyType).Kind == JsonTypeInfoKind.Dictionary)
                    {
                        section[p.Name] = new JsonObject();
                    }
                    else if (!p.IsRequired && p.IsSetNullable)
                    {
                        section.Remove(p.Name);
                    }
                }

                return section;
            default:
                return node;
        }
    }

    // ---------------------------------------------------------------- stamping

    private static T Stamp<T>(T value, RedactionLevel level, Pass pass, RedactionManifest manifest)
        where T : class
    {
        object stamped = value switch
        {
            ScanSnapshot s => StampSnapshot(s, level, pass),
            ReportModel r => r with { Snapshot = StampSnapshot(r.Snapshot, level, pass), Redaction = manifest },
            ScanBundle b => b with { Snapshot = StampSnapshot(b.Snapshot, level, pass), Manifest = b.Manifest with { Redaction = Max(b.Manifest.Redaction, level) } },
            _ => value,
        };
        return (T)stamped;
    }

    private static ScanSnapshot StampSnapshot(ScanSnapshot snapshot, RedactionLevel level, Pass pass)
    {
        var existing = snapshot.Meta.Redaction;
        var rules = new SortedSet<string>(existing.AppliedRules, StringComparer.Ordinal);
        rules.UnionWith(pass.RuleIds);
        return snapshot with
        {
            Meta = snapshot.Meta with
            {
                Redaction = new RedactionState
                {
                    Level = Max(existing.Level, level),
                    RulesVersion = RedactionConstants.RulesVersion,
                    AppliedRules = [.. rules],
                },
            },
        };
    }

    private static RedactionLevel Max(RedactionLevel a, RedactionLevel b) => (RedactionLevel)Math.Max((int)a, (int)b);

    // ---------------------------------------------------------------- metadata helpers

    private static Type? FactValueType(Type type)
        => FactValueTypes.GetOrAdd(type, t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Fact<>) ? t.GetGenericArguments()[0] : null);

    private static Sensitivity? SensitivityOf(Type owner, JsonPropertyInfo property)
        => SensitivityCache.GetOrAdd((owner, property.Name), _ =>
        {
            var member = property.AttributeProvider as PropertyInfo ?? FindProperty(owner, property.Name);
            if (member is null)
            {
                return owner.GetCustomAttribute<SensitivityAttribute>(inherit: true)?.Level;
            }

            return (member.GetCustomAttribute<SensitivityAttribute>(inherit: true)
                    ?? member.DeclaringType?.GetCustomAttribute<SensitivityAttribute>(inherit: true))?.Level;
        });

    private static PropertyInfo? FindProperty(Type owner, string jsonName)
        => owner.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(p.Name), jsonName, StringComparison.Ordinal));

    private readonly record struct Site(JsonPropertyInfo? Property, JsonObject? Parent, Type? Declaring, bool InCollection);

    private sealed class Pass(RedactionPolicy policy, IdentifierSet identifiers)
    {
        private readonly Dictionary<(string Path, string Rule), int> _counts = [];
        private readonly List<(string Path, string Rule)> _order = [];

        public RedactionPolicy Policy { get; } = policy;

        public bool Collecting { get; set; }

        public List<string> Collected { get; } = [];

        public IdentifierSet Identifiers { get; set; } = identifiers;

        public SortedSet<string> RuleIds { get; } = new(StringComparer.Ordinal);

        public void Record(string path, string rule, int count)
        {
            if (Collecting || count <= 0)
            {
                return;
            }

            RuleIds.Add(rule);
            var key = (path, rule);
            if (_counts.TryGetValue(key, out var existing))
            {
                _counts[key] = existing + count;
            }
            else
            {
                _counts[key] = count;
                _order.Add(key);
            }
        }

        public IReadOnlyList<RedactionEntry> Entries()
            => [.. _order.Select(k => new RedactionEntry { Path = k.Path, Rule = k.Rule, Count = _counts[k] })];
    }
}
