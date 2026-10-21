using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VTD.Contracts.Serialization;

/// <summary>Creates <see cref="FactJsonConverter{T}"/> instances. Works with the source-generated <see cref="VtdJsonContext"/>.</summary>
public sealed class FactJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Fact<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        var converterType = typeof(FactJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// JSON converter for <see cref="Fact{T}"/>. Shape: <c>{"status":"known|unknown|notApplicable","value":…,"reason":…,"detail":…,"source":…}</c>.
/// Rejects contradictory documents (a value on an Unknown fact, a Known fact without a value, an Unknown fact without a reason).
/// </summary>
/// <typeparam name="T">Fact value type.</typeparam>
public sealed class FactJsonConverter<T> : JsonConverter<Fact<T>>
{
    private const string StatusName = "status";
    private const string ValueName = "value";
    private const string ReasonName = "reason";
    private const string DetailName = "detail";
    private const string SourceName = "source";
    private const string CompleteName = "complete";

    /// <inheritdoc />
    public override Fact<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Fact<{typeof(T).Name}> must be a JSON object.");
        }

        FactStatus? status = null;
        T? value = default;
        var hasValue = false;
        UnknownReason? reason = null;
        string? detail = null;
        string? source = null;
        bool? complete = null;
        var comparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a property name in Fact.");
            }

            var name = reader.GetString()!;
            reader.Read();

            if (string.Equals(name, StatusName, comparison))
            {
                status = reader.GetString() switch
                {
                    "known" => FactStatus.Known,
                    "unknown" => FactStatus.Unknown,
                    "notApplicable" => FactStatus.NotApplicable,
                    var s => throw new JsonException($"Unknown fact status '{s}'."),
                };
            }
            else if (string.Equals(name, ValueName, comparison))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    throw new JsonException("A fact value must not be null; use status 'unknown' or 'notApplicable'.");
                }

                var info = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
                value = JsonSerializer.Deserialize(ref reader, info);
                hasValue = true;
            }
            else if (string.Equals(name, ReasonName, comparison))
            {
                var info = (JsonTypeInfo<UnknownReason>)options.GetTypeInfo(typeof(UnknownReason));
                reason = JsonSerializer.Deserialize(ref reader, info);
            }
            else if (string.Equals(name, DetailName, comparison))
            {
                detail = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (string.Equals(name, SourceName, comparison))
            {
                source = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (string.Equals(name, CompleteName, comparison))
            {
                complete = reader.TokenType switch
                {
                    JsonTokenType.True => true,
                    JsonTokenType.False => false,
                    _ => throw new JsonException("'complete' must be true or false."),
                };
            }
            else if (options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
            {
                throw new JsonException($"Unexpected property '{name}' in Fact.");
            }
            else
            {
                reader.Skip();
            }
        }

        switch (status)
        {
            case FactStatus.Known when hasValue && value is not null && complete == false:
                return Fact<T>.Partial(value, detail, source);
            case FactStatus.Known when hasValue && value is not null:
                return Fact<T>.Known(value, source);
            case FactStatus.Known:
                throw new JsonException("A known fact needs a non-null 'value'.");
            case FactStatus.Unknown or FactStatus.NotApplicable when complete == false:
                throw new JsonException("Only a known fact can be partial ('complete': false).");
            case FactStatus.Unknown when hasValue:
                throw new JsonException("An unknown fact must not carry a 'value' (Unknown is never False).");
            case FactStatus.Unknown when reason is null:
                throw new JsonException("An unknown fact needs a 'reason'.");
            case FactStatus.Unknown:
                return Fact<T>.Unknown(reason.Value, detail, source);
            case FactStatus.NotApplicable when hasValue || reason is not null:
                throw new JsonException("A notApplicable fact carries neither 'value' nor 'reason'.");
            case FactStatus.NotApplicable:
                return Fact<T>.NotApplicable(detail, source);
            default:
                throw new JsonException("A fact needs a 'status'.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Fact<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        writer.WriteStartObject();
        switch (value.Status)
        {
            case FactStatus.Known:
                writer.WriteString(StatusName, "known");
                writer.WritePropertyName(ValueName);
                JsonSerializer.Serialize(writer, value.Value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
                if (!value.IsComplete)
                {
                    writer.WriteBoolean(CompleteName, false);
                }

                break;
            case FactStatus.Unknown:
                writer.WriteString(StatusName, "unknown");
                writer.WritePropertyName(ReasonName);
                JsonSerializer.Serialize(writer, value.Reason ?? UnknownReason.Failed, (JsonTypeInfo<UnknownReason>)options.GetTypeInfo(typeof(UnknownReason)));
                break;
            default:
                writer.WriteString(StatusName, "notApplicable");
                break;
        }

        if (value.Detail is not null)
        {
            writer.WriteString(DetailName, value.Detail);
        }

        if (value.Source is not null)
        {
            writer.WriteString(SourceName, value.Source);
        }

        writer.WriteEndObject();
    }
}
