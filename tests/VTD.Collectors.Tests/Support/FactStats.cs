using System.Text.Json;
using System.Text.RegularExpressions;
using VTD.Collectors.Capture;
using VTD.Contracts;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests.Support;

/// <summary>Counts facts in a serialized snapshot by status and unknown reason.</summary>
internal sealed record FactStats(int Known, int NotApplicable, IReadOnlyDictionary<string, int> UnknownByReason)
{
    public int Unknown => UnknownByReason.Values.Sum();

    public static FactStats Of(string snapshotJson)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        int known = 0, na = 0;
        var unknown = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && (e.TryGetProperty("source", out _) || e.TryGetProperty("reason", out _) || e.TryGetProperty("value", out _)))
                {
                    switch (s.GetString())
                    {
                        case "known":
                            known++;
                            break;
                        case "notApplicable":
                            na++;
                            break;
                        case "unknown":
                            var r = e.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "?" : "?";
                            unknown[r] = unknown.GetValueOrDefault(r) + 1;
                            break;
                    }
                }

                foreach (var p in e.EnumerateObject())
                {
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray())
                {
                    Walk(x);
                }
            }
        }

        Walk(doc.RootElement);
        return new FactStats(known, na, unknown);
    }

    public override string ToString()
        => $"known {Known}, notApplicable {NotApplicable}, unknown {Unknown} ({string.Join(", ", UnknownByReason.Select(kv => kv.Key + " " + kv.Value))})";
}

/// <summary>Finds identifiers that must not survive in a checked-in capture or snapshot.</summary>
internal static partial class LeakScan
{
    public static IReadOnlyList<string> Find(string text, PlayerContext? player = null, string? userName = null, string? machineName = null)
    {
        var hits = new List<string>();
        void Check(string? needle, string what)
        {
            if (!string.IsNullOrEmpty(needle) && needle.Length >= 3 && text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(what);
            }
        }

        if (player is not null)
        {
            Check(player.Sid, "player sid");
            Check(Path.GetFileName(player.ProfilePath.TrimEnd('\\')), "profile folder name");
        }

        Check(userName, "user name");
        Check(machineName, "machine name");
        foreach (var canary in CanaryTokens.All)
        {
            Check(canary, "canary " + canary);
        }

        foreach (Match m in Sid().Matches(text))
        {
            if (!string.Equals(m.Value, CaptureRedactor.FakeSid, StringComparison.Ordinal) && !string.Equals(m.Value, FakePlayer.Default.Sid, StringComparison.Ordinal))
            {
                hits.Add("sid " + m.Value[..12] + "...");
            }
        }

        foreach (Match m in Mac().Matches(text))
        {
            if (!m.Value.StartsWith("02:00:00:", StringComparison.OrdinalIgnoreCase) && m.Value is not "00:00:00:00:00:00")
            {
                hits.Add("mac");
            }
        }

        if (Jwt().IsMatch(text))
        {
            hits.Add("jwt");
        }

        if (Email().IsMatch(text))
        {
            hits.Add("email");
        }

        foreach (Match m in Profile().Matches(text))
        {
            var seg = m.Groups[1].Value;
            if (seg is not "<user>" and not "Public" and not "Default" and not "All Users" and not "%USERNAME%")
            {
                hits.Add("profile path segment");
            }
        }

        return hits.Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"S-1-5-21-\d+-\d+-\d+-\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Sid();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?i)[a-z]:(?:\\\\|\\|/)+users(?:\\\\|\\|/)+([^\\/""<>|]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Profile();
}
