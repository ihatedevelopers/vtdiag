using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VTD.Contracts.Serialization;

/// <summary>
/// The resolver behind <see cref="VtdJson"/>: a type is serializable only if <see cref="VtdJsonContext"/> knows it
/// (registered, or reachable from a registered type), but its metadata is built by reflection.
/// </summary>
/// <remarks>
/// Why not the source-generated metadata itself: for records with init-only members the generator builds the object
/// with an object initializer that assigns EVERY init-only member, so a member ABSENT from the JSON becomes
/// <c>default(T)</c> instead of keeping its C# initializer (<c>GuideDef.Risk</c> High → Low, <c>FixDef.Admin</c>
/// true → false, <c>RuleDef.Rev</c> 1 → 0, empty lists → null). Reflection metadata constructs the object first and sets
/// only the members present, so initializer defaults survive. Every option (camelCase, strict numbers, required members,
/// nullable annotations, unmapped-member handling, converters) still comes from the context's
/// <c>JsonSourceGenerationOptions</c>, so the wire contract is unchanged. Keeping the context as the gate keeps the
/// "register every Fact&lt;T&gt; payload" discipline: an unknown type still throws <see cref="NotSupportedException"/>.
/// </remarks>
internal sealed class ContractTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver reflection = new();

    /// <summary>The shared instance.</summary>
    public static ContractTypeInfoResolver Instance { get; } = new();

    /// <inheritdoc/>
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        => VtdJsonContext.Default.GetTypeInfo(type) is null ? null : this.reflection.GetTypeInfo(type, options);
}
