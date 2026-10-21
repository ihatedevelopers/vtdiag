using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VTD.Contracts.Serialization;

/// <summary>Enum converter used everywhere: camelCase names (or <see cref="JsonStringEnumMemberNameAttribute"/>), no integers.</summary>
public sealed class CamelCaseEnumConverter : JsonStringEnumConverter
{
    /// <summary>Creates the converter.</summary>
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// The one JSON contract for VTDiag: camelCase, string enums, culture-invariant (System.Text.Json is invariant by
/// design), strict (unknown properties, missing required members and nulls in non-nullable members are errors),
/// source-generated.
/// </summary>
/// <remarks>
/// Which options where:
/// <list type="bullet">
/// <item><see cref="Options"/> (strict): IPC, session state, journal, fixtures, tests, KB lint, and everything VTDiag writes.</item>
/// <item><see cref="LenientOptions"/>: data a NEWER producer may have written for this same format major — downloaded KB
/// packs, <c>.vtdplan</c>, <c>.vtdscan</c>. Check the format/schema major first and validate against
/// <c>kb/schema</c>; unknown properties are then ignored instead of rejecting a whole pack.</item>
/// </list>
/// Numbers are never NaN or Infinity: use null for "no value" (strict number handling throws on NaN).
/// </remarks>
public static class VtdJson
{
    /// <summary>
    /// Strict options. Types are gated by <see cref="VtdJsonContext"/>, metadata comes from reflection so C# initializer
    /// defaults of absent init-only members survive (see <see cref="ContractTypeInfoResolver"/>). Never use
    /// <c>VtdJsonContext.Default</c> or its <c>Options</c> directly: that path drops those defaults.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateStrict();

    /// <summary>Same contract, but unknown properties are skipped (forward-compatible reads of KB packs, plans, bundles).</summary>
    public static JsonSerializerOptions LenientOptions { get; } = CreateLenient();

    /// <summary>Deserializes data a newer producer may have written (see remarks on <see cref="VtdJson"/>).</summary>
    /// <typeparam name="T">A type registered in <see cref="VtdJsonContext"/>.</typeparam>
    /// <param name="json">The JSON text.</param>
    public static T DeserializeLenient<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var info = (JsonTypeInfo<T>)LenientOptions.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(json, info) ?? throw new JsonException($"JSON null is not a valid {typeof(T).Name}.");
    }

    private static JsonSerializerOptions CreateStrict()
    {
        var options = new JsonSerializerOptions(VtdJsonContext.Default.Options) { TypeInfoResolver = ContractTypeInfoResolver.Instance };
        options.MakeReadOnly(populateMissingResolver: false);
        return options;
    }

    private static JsonSerializerOptions CreateLenient()
    {
        var options = new JsonSerializerOptions(Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
        options.MakeReadOnly(populateMissingResolver: false);
        return options;
    }

    /// <summary>
    /// Options for StreamJsonRpc's <c>SystemTextJsonFormatter</c>: the same contract, plus a reflection fallback for the
    /// RPC library's own envelope types. Contract types still resolve through <see cref="ContractTypeInfoResolver"/>.
    /// </summary>
    public static JsonSerializerOptions CreateRpcOptions()
    {
        var options = new JsonSerializerOptions(Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractTypeInfoResolver.Instance, new DefaultJsonTypeInfoResolver()),
        };
        return options;
    }

    /// <summary>Serializes a contract value.</summary>
    /// <typeparam name="T">A type registered in <see cref="VtdJsonContext"/>.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="indented">Pretty-print with relaxed escaping (for files people read: KB, fixtures, plans; never for HTML).</param>
    public static string Serialize<T>(T value, bool indented = false)
    {
        var info = (JsonTypeInfo<T>)(indented ? Indented : Options).GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(value, info);
    }

    /// <summary>Deserializes a contract value strictly. Throws <see cref="JsonException"/> on any contract violation.</summary>
    /// <typeparam name="T">A type registered in <see cref="VtdJsonContext"/>.</typeparam>
    /// <param name="json">The JSON text.</param>
    public static T Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var info = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(json, info) ?? throw new JsonException($"JSON null is not a valid {typeof(T).Name}.");
    }

    /// <summary>Deserializes a contract value strictly from UTF-8 bytes.</summary>
    /// <typeparam name="T">A type registered in <see cref="VtdJsonContext"/>.</typeparam>
    /// <param name="utf8Json">The JSON bytes.</param>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        var info = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(utf8Json, info) ?? throw new JsonException($"JSON null is not a valid {typeof(T).Name}.");
    }

    private static JsonSerializerOptions Indented { get; } = CreateIndented();

    // Files people read (KB, fixtures, plans) keep '&', '<', non-ASCII readable. Never embed this output in HTML or script.
    private static JsonSerializerOptions CreateIndented()
    {
        var options = new JsonSerializerOptions(Options)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly(populateMissingResolver: false);
        return options;
    }
}
