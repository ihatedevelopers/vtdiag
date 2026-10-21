using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Reporting;

/// <summary>What to redact for one export.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RedactionPolicy
{
    /// <summary>Share-level policy with nothing excluded.</summary>
    public static RedactionPolicy Share { get; } = new() { Level = RedactionLevel.Share };

    /// <summary>Level.</summary>
    public required RedactionLevel Level { get; init; }

    /// <summary>Sections the player unticked in the preview (snapshot property names, for example <c>apps</c>, <c>processes</c>).</summary>
    public IReadOnlyList<string> ExcludedSections { get; init; } = [];

    /// <summary>Keep the game server IP (player ticked "include for ISP ticket").</summary>
    public bool IncludeServerIp { get; init; }
}

/// <summary>One applied redaction.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RedactionEntry
{
    /// <summary>JSON pointer.</summary>
    public required string Path { get; init; }

    /// <summary>Rule id.</summary>
    public required string Rule { get; init; }

    /// <summary>Replacements.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>Redaction manifest ("Here is exactly what's in this file").</summary>
[Sensitivity(Sensitivity.None)]
public sealed record RedactionManifest
{
    /// <summary>Level.</summary>
    public required RedactionLevel Level { get; init; }

    /// <summary>Rule set version.</summary>
    public required int RulesVersion { get; init; }

    /// <summary>Entries.</summary>
    public IReadOnlyList<RedactionEntry> Entries { get; init; } = [];

    /// <summary>Excluded sections.</summary>
    public IReadOnlyList<string> ExcludedSections { get; init; } = [];
}

/// <summary>
/// A regex-sweep hit in serialized output (email, JWT, token-like GUID, SID, <c>C:\Users\&lt;name&gt;</c>, planted canary).
/// The matched text is never included. Any hit in a share export BLOCKS the export, so the gap gets fixed in the schema.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record SweepHit
{
    /// <summary>JSON pointer of the field.</summary>
    public required string Path { get; init; }

    /// <summary>Pattern id (for example <c>jwt</c>, <c>email</c>, <c>sid</c>, <c>userprofile</c>, <c>canary</c>).</summary>
    public required string Rule { get; init; }

    /// <summary>Top-level snapshot section the hit is in (for example <c>apps</c>), so the UI can offer
    /// "leave this section out and try again" instead of a dead end.</summary>
    public string? Section { get; init; }
}

/// <summary>Redaction constants shared by the sweep (A6), fixtures (A1, A2's FixtureGen) and tests.</summary>
public static class RedactionConstants
{
    /// <summary>Current redaction rule set version.</summary>
    public const int RulesVersion = 1;

    /// <summary>Marker inside every planted canary token (ordinal, case-insensitive). The production sweep always has a
    /// <c>canary</c> rule for it, so a canary that survives redaction blocks the export in tests and in the field.</summary>
    public const string CanaryMarker = "VTDCANARY";
}

/// <summary>A redacted value.</summary>
/// <typeparam name="T">Model type.</typeparam>
public sealed record Redacted<T>
    where T : class
{
    /// <summary>Redacted copy.</summary>
    public required T Value { get; init; }

    /// <summary>Manifest.</summary>
    public required RedactionManifest Manifest { get; init; }

    /// <summary>Sweep hits.</summary>
    public IReadOnlyList<SweepHit> SweepHits { get; init; } = [];

    /// <summary>Export must not proceed.</summary>
    public bool Blocked => SweepHits.Count > 0;
}

/// <summary>
/// The redactor (A6): attribute-driven (<see cref="SensitivityAttribute"/>) then a regex sweep. Runs on every export and
/// every log line. Holds the per-install salt, which is never exported.
/// </summary>
public interface IRedactor
{
    /// <summary>Redacts a contract model.</summary>
    /// <typeparam name="T">A type registered in the JSON contract.</typeparam>
    /// <param name="model">Model.</param>
    /// <param name="policy">Policy.</param>
    Redacted<T> Redact<T>(T model, RedactionPolicy policy)
        where T : class;

    /// <summary>Redacts free text (log lines, notes).</summary>
    /// <param name="text">Text.</param>
    /// <param name="policy">Policy.</param>
    string RedactText(string text, RedactionPolicy policy);

    /// <summary>Runs the regex sweep over serialized JSON.</summary>
    /// <param name="json">Serialized JSON.</param>
    IReadOnlyList<SweepHit> Sweep(string json);

    /// <summary>
    /// The id an entity has in share-level output (device ids keep enumerator + VEN/DEV and get a salted-hash tail; hashed
    /// ids are salted hashes; service/process/driver names are unchanged). Deterministic per install (same salt), so the
    /// <see cref="IPlanValidator"/> maps a helper plan's ids back to live entities by computing this for each candidate.
    /// </summary>
    /// <param name="entity">Live entity.</param>
    string ShareEntityId(EntityRef entity);
}

/// <summary>Report metadata.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ReportMeta
{
    /// <summary>ULID.</summary>
    public required string ReportId { get; init; }

    /// <summary>Created.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>App version.</summary>
    public required string AppVersion { get; init; }

    /// <summary>KB version.</summary>
    public required string KbVersion { get; init; }

    /// <summary>Player-facing language.</summary>
    public required string Language { get; init; }

    /// <summary>Language of the helper/machine section (default English so the subreddit can read it).</summary>
    public string HelperLanguage { get; init; } = Languages.English;

    /// <summary>Machine key (see <see cref="SnapshotMeta.MachineKey"/>).</summary>
    public required string MachineKey { get; init; }

    /// <summary>Session.</summary>
    public string? SessionId { get; init; }

    /// <summary>"Not affiliated with Riot Games" disclaimer key (in every report).</summary>
    public string DisclaimerKey { get; init; } = "report.disclaimer.not_affiliated";
}

/// <summary>Telemetry-free outcome card the player can paste (design_A.md section 11). No identifiers.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record OutcomeCard
{
    /// <summary>Card text (<c>VTD1 outcome · …</c>).</summary>
    public required string Text { get; init; }

    /// <summary>Random de-duplication nonce.</summary>
    public required string Nonce { get; init; }
}

/// <summary>Everything a report renders. Schema: <c>kb/schema/report.schema.json</c>.</summary>
public sealed record ReportModel
{
    /// <summary>Current schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Meta.</summary>
    public required ReportMeta Meta { get; init; }

    /// <summary>Redacted snapshot.</summary>
    public required ScanSnapshot Snapshot { get; init; }

    /// <summary>Analysis.</summary>
    public AnalysisResult? Analysis { get; init; }

    /// <summary>Actions taken (status, verify, undo state, reboot).</summary>
    public IReadOnlyList<AttemptSummary> Attempts { get; init; } = [];

    /// <summary>Latest monitor summary.</summary>
    public MonitorSummary? Monitor { get; init; }

    /// <summary>Baseline monitor summary (before/after).</summary>
    public MonitorSummary? Baseline { get; init; }

    /// <summary>Redaction manifest.</summary>
    public required RedactionManifest Redaction { get; init; }

    /// <summary>Outcome card.</summary>
    public OutcomeCard? OutcomeCard { get; init; }
}

/// <summary>Report format.</summary>
public enum ReportFormat
{
    /// <summary>Single-file HTML, inline CSS and SVG, NO JavaScript.</summary>
    Html = 0,

    /// <summary>Reddit/Discord Markdown, at most 9,000 characters.</summary>
    Markdown = 1,

    /// <summary>The <c>.vtdreport</c> JSON.</summary>
    Json = 2,

    /// <summary>"Copy for AI": redacted digest + <c>plan.schema.json</c>, so a helper's AI can answer and write a plan.</summary>
    AiDigest = 3,
}

/// <summary>Report options.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ReportOptions
{
    /// <summary>Formats.</summary>
    public IReadOnlyList<ReportFormat> Formats { get; init; } = [ReportFormat.Html, ReportFormat.Markdown, ReportFormat.Json];

    /// <summary>Player-facing language.</summary>
    public string Language { get; init; } = Languages.English;

    /// <summary>Helper section language.</summary>
    public string HelperLanguage { get; init; } = Languages.English;

    /// <summary>Markdown character cap (Reddit comments are limited to 10k).</summary>
    public int MarkdownMaxChars { get; init; } = 9000;
}

/// <summary>Rendered report.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ReportBundle
{
    /// <summary>HTML.</summary>
    public string? Html { get; init; }

    /// <summary>Markdown.</summary>
    public string? Markdown { get; init; }

    /// <summary>JSON.</summary>
    public string? Json { get; init; }

    /// <summary>AI digest.</summary>
    public string? AiDigest { get; init; }

    /// <summary>SHA-256 of the JSON rendering (lets a mod tell the report was not edited).</summary>
    public required string JsonSha256 { get; init; }

    /// <summary>Redaction manifest.</summary>
    public required RedactionManifest Redaction { get; init; }

    /// <summary>Sweep hits (non-empty = blocked).</summary>
    public IReadOnlyList<SweepHit> SweepHits { get; init; } = [];

    /// <summary>Blocked by the sweep.</summary>
    public required bool Blocked { get; init; }
}

/// <summary>Builds reports (A6).</summary>
public interface IReportBuilder
{
    /// <summary>Builds the requested renderings from one model.</summary>
    /// <param name="model">Model (already redacted).</param>
    /// <param name="options">Options.</param>
    ReportBundle Build(ReportModel model, ReportOptions options);
}

/// <summary>Reads and writes <c>.vtdscan</c> zip bundles in memory (A6). The engine or a SaveFileDialog stream writes the bytes.</summary>
public interface IScanBundleCodec
{
    /// <summary>Writes a bundle (always share-level redacted).</summary>
    /// <param name="bundle">Bundle.</param>
    byte[] Write(ScanBundle bundle);

    /// <summary>Reads and verifies a bundle (manifest hashes).</summary>
    /// <param name="stream">Stream.</param>
    ScanBundle Read(Stream stream);
}

/// <summary>
/// Validates a <c>.vtdplan</c> against a FRESH snapshot (A6): schema, KB-ids only, kbMin, expiry, max 10 steps, bindings
/// resolve (through <see cref="IRedactor.ShareEntityId"/>) to live entities, signature and trust from the KB's trusted
/// helper keys. Guards and ProtectedTargetPolicy are then re-checked by the engine's preview for each step when it runs.
/// </summary>
public interface IPlanValidator
{
    /// <summary>Validates.</summary>
    /// <param name="planText">File content: a plain <see cref="FixPlan"/> or a <see cref="SignedPlanEnvelope"/>.</param>
    /// <param name="freshSnapshot">A fresh (unredacted, local) scan of this PC.</param>
    /// <param name="kb">KB in use.</param>
    /// <param name="nowUtc">Trusted time (after clock-skew handling).</param>
    /// <param name="freshAnalysis">Analysis of <paramref name="freshSnapshot"/> when available: a non-template fix may then
    /// bind only entities that analysis offers for it. Null = that restriction cannot be checked (the engine's preview
    /// still re-checks guards and ProtectedTargetPolicy).</param>
    PlanValidation Validate(string planText, ScanSnapshot freshSnapshot, IKnowledgeBase kb, DateTimeOffset nowUtc, AnalysisResult? freshAnalysis);
}
