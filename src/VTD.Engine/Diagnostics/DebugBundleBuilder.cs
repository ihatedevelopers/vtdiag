using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;
using VTD.Reporting;

namespace VTD.Engine.Diagnostics;

/// <summary>What a debug bundle is made from (engine-local data; nothing here is redacted yet, except the logs).</summary>
public sealed record DebugBundleInput
{
    /// <summary>Session state.</summary>
    public required SessionState Session { get; init; }

    /// <summary>Last scan snapshot of the session, if any (a failed or missing scan is exactly when a bundle is needed).</summary>
    public ScanSnapshot? Snapshot { get; init; }

    /// <summary>Analysis of that snapshot, if any.</summary>
    public AnalysisResult? Analysis { get; init; }

    /// <summary>Journal files (name, JSON-lines text), newest content last; empty for a limited engine.</summary>
    public IReadOnlyList<(string Name, string Text)> Journal { get; init; } = [];

    /// <summary>Log files from <see cref="DiagLog.ReadAll"/>.</summary>
    public IReadOnlyList<(string Name, string Text)> Logs { get; init; } = [];

    /// <summary>Engine version.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>KB version.</summary>
    public required string KbVersion { get; init; }

    /// <summary>KB load problems (codes only).</summary>
    public IReadOnlyList<string> KbIssueCodes { get; init; } = [];

    /// <summary>Elevated or limited engine.</summary>
    public required PrivilegeMode Privilege { get; init; }

    /// <summary>Debug mode was on.</summary>
    public bool DebugMode { get; init; }

    /// <summary>Process start time (uptime in the environment summary).</summary>
    public DateTimeOffset? ProcessStartUtc { get; init; }
}

/// <summary>
/// Builds the <c>.vtddebug</c> zip a player sends so both a VTDiag bug and their PC problem can be understood: rolling
/// logs, the last scan snapshot, the analysis, the journal and the session state, an environment summary and a manifest.
/// Every part is redacted (the models through the attribute-driven <see cref="IRedactor"/>, the logs at write time and
/// again here, the journal as text) and then swept: a sweep hit, or any known identifier (user name, machine name) left in a part, blocks the
/// export and names the section; the player may leave that section out and try again (<see cref="ExportRequest.ExcludedSections"/>).
/// </summary>
public sealed class DebugBundleBuilder
{
    /// <summary>Bundle format id in the manifest.</summary>
    public const string Format = "vtd-debug/1";

    /// <summary>Sections that can be left out.</summary>
    public static IReadOnlyList<string> Sections { get; } = ["logs", "snapshot", "analysis", "session", "journal"];

    private const int MaxJournalChars = 512 * 1024;

    private readonly IRedactor _redactor;
    private readonly Func<string, bool> _hasKnownIdentifier;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;

    /// <summary>Creates the builder.</summary>
    /// <param name="redactor">Redactor.</param>
    /// <param name="hasKnownIdentifier">True when a text still contains the user name or machine name (the last line of defence).</param>
    /// <param name="ids">Ids.</param>
    /// <param name="clock">Clock.</param>
    public DebugBundleBuilder(IRedactor redactor, Func<string, bool> hasKnownIdentifier, IIdGenerator ids, TimeProvider clock)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _hasKnownIdentifier = hasKnownIdentifier ?? throw new ArgumentNullException(nameof(hasKnownIdentifier));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Builds the bundle.</summary>
    /// <param name="request">Request (excluded sections).</param>
    /// <param name="input">Input.</param>
    public ExportResult Build(ExportRequest request, DebugBundleInput input)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        var excluded = request.ExcludedSections.ToHashSet(StringComparer.Ordinal);
        var policy = new RedactionPolicy { Level = RedactionLevel.Share };
        var parts = new List<Part>();
        var hits = new List<SweepHit>();
        var now = _clock.GetUtcNow();

        void AddJson(string section, string name, string json)
        {
            hits.AddRange(Check(section, name, json, isJson: true));
            parts.Add(new Part(section, name, json));
        }

        void AddText(string section, string name, string text)
        {
            var scrubbed = _redactor.RedactText(text, policy);
            hits.AddRange(Check(section, name, scrubbed, isJson: false));
            parts.Add(new Part(section, name, scrubbed));
        }

        if (!excluded.Contains("session"))
        {
            if (Model(input.Session, policy, "session", hits) is { } json)
            {
                AddJson("session", "session.json", json);
            }
        }

        if (input.Snapshot is not null && !excluded.Contains("snapshot"))
        {
            if (Model(input.Snapshot, policy, "snapshot", hits) is { } json)
            {
                AddJson("snapshot", "snapshot.json", json);
            }
        }

        if (input.Analysis is not null && !excluded.Contains("analysis"))
        {
            if (Model(input.Analysis, policy, "analysis", hits) is { } json)
            {
                AddJson("analysis", "analysis.json", json);
            }
        }

        if (!excluded.Contains("logs"))
        {
            foreach (var (name, text) in input.Logs)
            {
                // Already redacted at write time: only checked here (re-scrubbing JSON text could break its quoting).
                hits.AddRange(Check("logs", "logs/" + name, text, isJson: false));
                parts.Add(new Part("logs", "logs/" + name, text));
            }
        }

        if (!excluded.Contains("journal"))
        {
            foreach (var (name, text) in input.Journal)
            {
                var tail = text.Length > MaxJournalChars ? text[^MaxJournalChars..] : text;
                AddText("journal", "journal/" + Path.GetFileName(name), tail);
            }
        }

        AddJson("environment", "environment.json", Environment(input, now));

        if (hits.Count > 0)
        {
            return new ExportResult
            {
                Status = ExportStatus.BlockedByRedaction,
                SweepHits = [.. hits],
                Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = RedactionConstants.RulesVersion, ExcludedSections = request.ExcludedSections },
            };
        }

        var bundleId = _ids.NewId();
        var manifest = Manifest(bundleId, now, input, parts, excluded);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.json", manifest, now);
            foreach (var part in parts)
            {
                Write(zip, part.Name, part.Text, now);
            }
        }

        var bytes = memory.ToArray();
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new ExportResult
        {
            Status = ExportStatus.Ok,
            FileName = "VTDiag-debug-" + date + "-" + ReportBuilder.ShortId(bundleId) + ".vtddebug",
            Base64Content = Convert.ToBase64String(bytes),
            Sha256 = CanonicalHash.OfBytes(bytes),
            Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = RedactionConstants.RulesVersion, ExcludedSections = request.ExcludedSections },
        };
    }

    private string? Model<T>(T model, RedactionPolicy policy, string section, List<SweepHit> hits)
        where T : class
    {
        try
        {
            var redacted = _redactor.Redact(model, policy);
            if (redacted.Blocked)
            {
                hits.AddRange(redacted.SweepHits.Select(h => h with { Section = h.Section ?? section }));
                return null;
            }

            return VtdJson.Serialize(redacted.Value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            hits.Add(new SweepHit { Path = string.Empty, Rule = "redaction_failed", Section = section });
            return null;
        }
    }

    private IEnumerable<SweepHit> Check(string section, string name, string text, bool isJson)
    {
        var found = isJson
            ? _redactor.Sweep(text).Select(h => h with { Path = name + h.Path, Section = section })
            : RedactionSweep.SweepText(text, name, section);
        foreach (var hit in found)
        {
            yield return hit;
        }

        if (_hasKnownIdentifier(text))
        {
            yield return new SweepHit { Path = name, Rule = "identifier", Section = section };
        }
    }

    private static string Environment(DebugBundleInput input, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            var os = System.Environment.OSVersion.Version;
            w.WriteStartObject();
            w.WriteString("engineVersion", input.EngineVersion);
            w.WriteString("kbVersion", input.KbVersion);
            w.WriteString("privilege", input.Privilege.ToString());
            w.WriteBoolean("debugMode", input.DebugMode);
            w.WriteString("windowsVersion", os.ToString());
            w.WriteNumber("windowsBuild", os.Build);
            w.WriteString("osDescription", RuntimeInformation.OSDescription);
            w.WriteString("osArchitecture", RuntimeInformation.OSArchitecture.ToString());
            w.WriteString("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
            w.WriteString("dotnet", RuntimeInformation.FrameworkDescription);
            w.WriteString("uiCulture", CultureInfo.CurrentUICulture.Name);
            w.WriteNumber("processorCount", System.Environment.ProcessorCount);
            w.WriteNumber("totalMemoryMb", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            if (input.ProcessStartUtc is { } start)
            {
                w.WriteNumber("engineUptimeSeconds", (long)(now - start).TotalSeconds);
            }

            w.WriteString("sessionPhase", input.Session.Phase.ToString());
            w.WriteNumber("sessionRevision", input.Session.Revision);
            w.WriteString("lastErrorCode", input.Session.LastErrorCode);
            w.WriteStartArray("kbIssueCodes");
            foreach (var code in input.KbIssueCodes)
            {
                w.WriteStringValue(code);
            }

            w.WriteEndArray();
            if (input.Snapshot is { } snapshot)
            {
                w.WriteStartArray("collectors");
                foreach (var run in snapshot.Runs)
                {
                    w.WriteStartObject();
                    w.WriteString("id", run.CollectorId);
                    w.WriteString("status", run.Status.ToString());
                    w.WriteNumber("elapsedMs", run.ElapsedMs);
                    w.WriteStartArray("issues");
                    foreach (var code in run.IssueCodes)
                    {
                        w.WriteStringValue(code);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Manifest(string bundleId, DateTimeOffset now, DebugBundleInput input, List<Part> parts, HashSet<string> excluded)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteString("bundleId", bundleId);
            w.WriteString("createdUtc", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("engineVersion", input.EngineVersion);
            w.WriteString("kbVersion", input.KbVersion);
            w.WriteString("privilege", input.Privilege.ToString());
            w.WriteBoolean("debugMode", input.DebugMode);
            w.WriteString("redactionLevel", "share");
            w.WriteNumber("redactionRulesVersion", RedactionConstants.RulesVersion);
            w.WriteStartArray("parts");
            foreach (var part in parts)
            {
                var bytes = Encoding.UTF8.GetBytes(part.Text);
                w.WriteStartObject();
                w.WriteString("name", part.Name);
                w.WriteString("section", part.Section);
                w.WriteNumber("bytes", bytes.Length);
                w.WriteString("sha256", CanonicalHash.OfBytes(bytes));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("absent");
            foreach (var section in Sections.Where(s => !parts.Any(p => string.Equals(p.Section, s, StringComparison.Ordinal))))
            {
                w.WriteStartObject();
                w.WriteString("section", section);
                w.WriteString("reason", excluded.Contains(section) ? "left_out_by_player" : "not_available");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(ZipArchive zip, string name, string text, DateTimeOffset now)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = now;
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }

    private sealed record Part(string Section, string Name, string Text);
}
