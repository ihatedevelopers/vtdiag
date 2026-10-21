using System.Text.Json;
using System.Text.RegularExpressions;
using VTD.Contracts.Reporting;

namespace VTD.Reporting;

/// <summary>
/// The export-time regex sweep (design_B.md section 12): every string value AND property name of the serialized output
/// is checked; any hit blocks a share export. A hit names the JSON pointer, the rule and the top-level section (for a
/// <see cref="ReportModel"/> the snapshot section under <c>/snapshot</c>), never the matched text.
/// </summary>
public static class RedactionSweep
{
    /// <summary>Rule ids.</summary>
    public static class Rules
    {
        /// <summary>A planted canary token (always on).</summary>
        public const string Canary = "canary";

        /// <summary>A JWT.</summary>
        public const string Jwt = "jwt";

        /// <summary>An e-mail address.</summary>
        public const string Email = "email";

        /// <summary>A user SID.</summary>
        public const string Sid = "sid";

        /// <summary><c>C:\Users\&lt;name&gt;</c>.</summary>
        public const string UserProfile = "userprofile";

        /// <summary>An auth token argument or key=value secret.</summary>
        public const string AuthToken = "auth_token";

        /// <summary>An id right next to its key (<c>puuid=…</c>).</summary>
        public const string IdNextToKey = "id_next_to_key";

        /// <summary>A network id next to its key (<c>SSID=…</c>, <c>LocalIp=…</c>).</summary>
        public const string NetworkId = "network_id";

        /// <summary>A MAC address.</summary>
        public const string Mac = "mac";

        /// <summary>The input was not valid JSON.</summary>
        public const string InvalidJson = "invalid_json";

        /// <summary>A report model whose snapshot is not share-level redacted.</summary>
        public const string NotShareLevel = "not_share_level";
    }

    /// <summary>Sweeps serialized JSON.</summary>
    /// <param name="json">JSON text.</param>
    public static IReadOnlyList<SweepHit> Sweep(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return [new SweepHit { Path = string.Empty, Rule = Rules.InvalidJson }];
        }

        using (document)
        {
            var hits = new List<SweepHit>();
            var seen = new HashSet<(string, string)>();
            Walk(document.RootElement, string.Empty, hits, seen);
            return hits;
        }
    }

    /// <summary>Sweeps plain text (a rendered HTML or Markdown section). Hits carry <paramref name="path"/> and <paramref name="section"/>.</summary>
    /// <param name="text">Text.</param>
    /// <param name="path">Location label for the hits (for example <c>html#suspects</c>).</param>
    /// <param name="section">Model section the text was rendered from.</param>
    public static IReadOnlyList<SweepHit> SweepText(string text, string path, string? section)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. Detect(text).Select(rule => new SweepHit { Path = path, Rule = rule, Section = section })];
    }

    /// <summary>Rule ids that match <paramref name="text"/> (empty when clean).</summary>
    /// <param name="text">Text.</param>
    public static IEnumerable<string> Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains(RedactionConstants.CanaryMarker, StringComparison.OrdinalIgnoreCase))
        {
            yield return Rules.Canary;
        }

        if (TextPatterns.Jwt().IsMatch(text))
        {
            yield return Rules.Jwt;
        }

        if (TextPatterns.Email().IsMatch(text))
        {
            yield return Rules.Email;
        }

        if (TextPatterns.Sid().IsMatch(text))
        {
            yield return Rules.Sid;
        }

        if (HasUserProfile(text))
        {
            yield return Rules.UserProfile;
        }

        if (HasSecretValue(TextPatterns.AuthFlag(), text) || HasSecretValue(TextPatterns.SecretKeyValue(), text) || HasSecretValue(TextPatterns.PasswordValue(), text)
            || HasSecretValue(TextPatterns.Bearer(), text) || HasSecretValue(TextPatterns.OtherAuthScheme(), text) || HasSecretValue(TextPatterns.Lockfile(), text)
            || TextPatterns.CommandLine().Matches(text).Any(m => !TextScrubber.IsPlaceholder(m.Groups[0].Value[(m.Groups[1].Length + m.Groups[2].Length)..].Trim())))
        {
            yield return Rules.AuthToken;
        }

        if (HasSecretValue(TextPatterns.IdNextToKey(), text))
        {
            yield return Rules.IdNextToKey;
        }

        if (HasSecretValue(TextPatterns.NetworkKeyValue(), text))
        {
            yield return Rules.NetworkId;
        }

        if (TextPatterns.MacAddress().IsMatch(text))
        {
            yield return Rules.Mac;
        }
    }

    /// <summary>Escapes a JSON pointer token (RFC 6901).</summary>
    /// <param name="token">Token.</param>
    public static string EscapePointer(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Top-level section of a JSON pointer (<c>/snapshot/apps/…</c> → <c>apps</c>; <c>/analysis/…</c> → <c>analysis</c>).</summary>
    /// <param name="pointer">Pointer.</param>
    public static string? SectionOf(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var segment = string.Equals(segments[0], "snapshot", StringComparison.Ordinal) && segments.Length > 1 ? segments[1] : segments[0];
        return segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }

    private static void Walk(JsonElement element, string pointer, List<SweepHit> hits, HashSet<(string, string)> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // A property name that hits is masked as "*" in every pointer, so no hit ever carries the matched text.
                    var nameHits = Detect(property.Name).Any();
                    var child = pointer + "/" + (nameHits ? "*" : EscapePointer(property.Name));
                    Check(property.Name, child, hits, seen);
                    if (property.Value.ValueKind == JsonValueKind.String
                        && SensitiveKeys.SweepRule(property.Name, property.Value.GetString() ?? string.Empty) is { } keyRule
                        && seen.Add((child, keyRule)))
                    {
                        hits.Add(new SweepHit { Path = child, Rule = keyRule, Section = SectionOf(child) });
                    }

                    Walk(property.Value, child, hits, seen);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), hits, seen);
                    i++;
                }

                break;
            case JsonValueKind.String:
                Check(element.GetString() ?? string.Empty, pointer, hits, seen);
                break;
            default:
                break;
        }
    }

    private static void Check(string text, string pointer, List<SweepHit> hits, HashSet<(string, string)> seen)
    {
        foreach (var rule in Detect(text))
        {
            if (seen.Add((pointer, rule)))
            {
                hits.Add(new SweepHit { Path = pointer, Rule = rule, Section = SectionOf(pointer) });
            }
        }
    }

    private static bool HasUserProfile(string text)
    {
        foreach (Match m in TextPatterns.ProfilePath().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim();
            if (!TextPatterns.NeutralProfileNames.Contains(name) && !TextPatterns.PlaceholderProfileNames.Contains(name) && !name.StartsWith('%'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSecretValue(Regex regex, string text)
    {
        foreach (Match m in regex.Matches(text))
        {
            if (!TextScrubber.IsPlaceholder(m.Groups[3].Value.Trim()))
            {
                return true;
            }
        }

        return false;
    }
}
