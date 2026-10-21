using System.Globalization;
using System.Text;
using System.Text.Json;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Snapshots;

namespace VTD.Reporting;

/// <summary>What an export is made from (the engine's local-level data; nothing here is redacted yet).</summary>
public sealed record ShareExportInput
{
    /// <summary>Local-level snapshot from the engine.</summary>
    public required ScanSnapshot Snapshot { get; init; }

    /// <summary>Analysis of the snapshot.</summary>
    public AnalysisResult? Analysis { get; init; }

    /// <summary>Fix attempts (undo center rows).</summary>
    public IReadOnlyList<AttemptSummary> Attempts { get; init; } = [];

    /// <summary>Latest monitor summary.</summary>
    public MonitorSummary? Monitor { get; init; }

    /// <summary>Baseline monitor summary (before a fix).</summary>
    public MonitorSummary? Baseline { get; init; }

    /// <summary>App version.</summary>
    public required string AppVersion { get; init; }

    /// <summary>KB version.</summary>
    public required string KbVersion { get; init; }

    /// <summary>Session.</summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// Produces every <see cref="ExportKind"/>, always at share level: composes one <see cref="ReportModel"/> from the
/// snapshot, analysis (it carries entity ids and free-text evidence args), attempts and monitor summaries, redacts it in
/// ONE pass (so identifiers found anywhere are removed everywhere), builds the rendering, and returns it with its
/// SHA-256, the redaction manifest and a file name. Any sweep hit returns <see cref="ExportStatus.BlockedByRedaction"/>
/// with hits that name the section to leave out (<see cref="ExcludableSections"/>); nothing is exported then.
/// </summary>
public sealed class ShareExporter
{
    private readonly IRedactor _redactor;
    private readonly IReportBuilder _builder;
    private readonly IScanBundleCodec _codec;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;

    /// <summary>Creates the exporter.</summary>
    /// <param name="redactor">Redactor.</param>
    /// <param name="builder">Report builder.</param>
    /// <param name="codec">Bundle codec.</param>
    /// <param name="ids">Ids (report id).</param>
    /// <param name="clock">Clock (report time, file-name date).</param>
    public ShareExporter(IRedactor redactor, IReportBuilder builder, IScanBundleCodec codec, IIdGenerator ids, TimeProvider clock)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Sections the player may untick in the preview (or that a sweep hit suggests leaving out): the snapshot's JSON
    /// property names (everything except <c>meta</c> and <c>schemaVersion</c>) plus <c>analysis</c>, <c>attempts</c> and
    /// <c>monitor</c>.
    /// </summary>
    public static IReadOnlyList<string> ExcludableSections { get; } =
    [
        "apps", "processes", "startup", "services", "devices", "drivers", "events", "network", "audio", "display", "power",
        "input", "optionalFeatures", "storage", "security", "firmware", "hardware", "os", "riot", "pack", "runs",
        "analysis", "attempts", "monitor",
    ];

    /// <summary>Exports.</summary>
    /// <param name="request">Request (the level is always Share, whatever the caller wants).</param>
    /// <param name="input">Input.</param>
    public ExportResult Export(ExportRequest request, ShareExportInput input)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        var language = string.Equals(request.Language, Languages.Turkish, StringComparison.OrdinalIgnoreCase) ? Languages.Turkish : Languages.English;
        var policy = new RedactionPolicy
        {
            Level = RedactionLevel.Share,
            ExcludedSections = [.. request.ExcludedSections],
            IncludeServerIp = request.IncludeServerIp,
        };
        var reportId = _ids.NewId();
        var now = _clock.GetUtcNow();
        var raw = new ReportModel
        {
            SchemaVersion = ReportModel.CurrentSchemaVersion,
            Meta = new ReportMeta
            {
                ReportId = reportId,
                CreatedUtc = now,
                AppVersion = input.AppVersion,
                KbVersion = input.KbVersion,
                Language = language,
                HelperLanguage = Languages.English,
                MachineKey = input.Snapshot.Meta.MachineKey,
                SessionId = input.SessionId,
            },
            Snapshot = input.Snapshot,
            Analysis = input.Analysis,
            Attempts = input.Attempts,
            Monitor = input.Monitor,
            Baseline = input.Baseline,
            Redaction = new RedactionManifest { Level = RedactionLevel.Share, RulesVersion = RedactionConstants.RulesVersion },
        };

        Redacted<ReportModel> redacted;
        try
        {
            redacted = _redactor.Redact(raw, policy);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return new ExportResult { Status = ExportStatus.Failed };
        }

        if (redacted.Blocked)
        {
            return Blocked(redacted.SweepHits, redacted.Manifest);
        }

        var model = redacted.Value with { Redaction = redacted.Manifest };
        ReportFormat[] formats = request.Kind switch
        {
            ExportKind.ReportMarkdown => [ReportFormat.Markdown],
            ExportKind.ReportJson => [ReportFormat.Json],
            ExportKind.AiDigest => [ReportFormat.AiDigest],
            _ => [ReportFormat.Html],
        };
        var bundle = _builder.Build(model, new ReportOptions { Formats = formats, Language = language, HelperLanguage = Languages.English });
        if (bundle.Blocked)
        {
            return Blocked(bundle.SweepHits, model.Redaction);
        }

        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var shortId = ReportBuilder.ShortId(reportId);
        switch (request.Kind)
        {
            case ExportKind.ReportHtml:
                return Text(bundle.Html, ReportBuilder.ReportFileName(model.Meta, "html"), model.Redaction);
            case ExportKind.ReportMarkdown:
                return Text(bundle.Markdown, ReportBuilder.ReportFileName(model.Meta, "md"), model.Redaction);
            case ExportKind.ReportJson:
                return bundle.Json is null
                    ? new ExportResult { Status = ExportStatus.Failed }
                    : new ExportResult { Status = ExportStatus.Ok, FileName = ReportBuilder.ReportFileName(model.Meta, "json"), TextContent = bundle.Json, Sha256 = bundle.JsonSha256, Redaction = model.Redaction };
            case ExportKind.AiDigest:
                return Text(bundle.AiDigest, "VTDiag-AI-" + date + "-" + shortId + ".md", model.Redaction);
            case ExportKind.ScanBundle:
                return Bundle(model, bundle.Html, reportId, now, "VTDiag-" + shortId + ".vtdscan");
            default:
                return new ExportResult { Status = ExportStatus.Failed };
        }
    }

    private ExportResult Bundle(ReportModel model, string? html, string bundleId, DateTimeOffset now, string fileName)
    {
        var scan = new ScanBundle
        {
            Manifest = new ScanBundleManifest
            {
                Format = ScanBundleLayout.Format,
                BundleId = bundleId,
                Revision = model.Attempts.Count > 0 ? 2 : 1,
                CreatedUtc = now,
                AppVersion = model.Meta.AppVersion,
                KbVersion = model.Meta.KbVersion,
                SnapshotId = model.Snapshot.Meta.SnapshotId,
                Redaction = RedactionLevel.Share,
                Parts = [],
            },
            Snapshot = model.Snapshot,
            Analysis = model.Analysis,
            Journal = model.Attempts,
            Monitor = [.. new[] { model.Baseline, model.Monitor }.Where(m => m is not null).Select(m => m!)],
            ReportHtml = html,
        };

        byte[] bytes;
        try
        {
            bytes = _codec.Write(scan);
        }
        catch (ScanBundleException ex) when (ex.Code == ScanBundleException.Codes.SweepBlocked)
        {
            return Blocked(ex.SweepHits, model.Redaction);
        }
        catch (ScanBundleException)
        {
            return new ExportResult { Status = ExportStatus.Failed, Redaction = model.Redaction };
        }

        return new ExportResult
        {
            Status = ExportStatus.Ok,
            FileName = fileName,
            Base64Content = Convert.ToBase64String(bytes),
            Sha256 = CanonicalHash.OfBytes(bytes),
            Redaction = model.Redaction,
        };
    }

    private static ExportResult Text(string? content, string fileName, RedactionManifest manifest)
        => content is null
            ? new ExportResult { Status = ExportStatus.Failed, Redaction = manifest }
            : new ExportResult
            {
                Status = ExportStatus.Ok,
                FileName = fileName,
                TextContent = content,
                Sha256 = CanonicalHash.OfBytes(Encoding.UTF8.GetBytes(content)),
                Redaction = manifest,
            };

    private static ExportResult Blocked(IReadOnlyList<SweepHit> hits, RedactionManifest manifest)
        => new() { Status = ExportStatus.BlockedByRedaction, SweepHits = [.. hits], Redaction = manifest };
}
