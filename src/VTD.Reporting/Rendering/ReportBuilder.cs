using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;

namespace VTD.Reporting;

/// <summary>
/// Builds the report renderings from one already-redacted <see cref="ReportModel"/> (design_B.md section 10): HTML
/// (single file, no JavaScript), Reddit/Discord Markdown (at most <see cref="ReportOptions.MarkdownMaxChars"/>), the
/// <c>.vtdreport</c> JSON and the "Copy for AI" digest. The model JSON is swept first and every rendered piece is swept
/// again; any hit blocks the whole bundle (no rendering is returned) and the hits name the section to leave out.
/// </summary>
public sealed class ReportBuilder : IReportBuilder
{
    private readonly IKnowledgeBase _kb;
    private readonly IRedactor _redactor;

    /// <summary>Creates the builder.</summary>
    /// <param name="kb">KB (texts, fix/guide/question ids for the AI digest).</param>
    /// <param name="redactor">Redactor (sweep).</param>
    public ReportBuilder(IKnowledgeBase kb, IRedactor redactor)
    {
        _kb = kb ?? throw new ArgumentNullException(nameof(kb));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    /// <summary>Suggested HTML file name: <c>VTDiag-Report-&lt;yyyymmdd&gt;-&lt;shortid&gt;.html</c>.</summary>
    /// <param name="meta">Report meta.</param>
    /// <param name="extension">Extension without the dot.</param>
    public static string ReportFileName(ReportMeta meta, string extension)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return string.Create(CultureInfo.InvariantCulture, $"VTDiag-Report-{meta.CreatedUtc.UtcDateTime:yyyyMMdd}-{ShortId(meta.ReportId)}.{extension}");
    }

    /// <summary>Short id used in file names (the last 8 characters of the ULID: the random part).</summary>
    /// <param name="ulid">ULID.</param>
    public static string ShortId(string ulid)
    {
        ArgumentNullException.ThrowIfNull(ulid);
        var safe = new string(ulid.Where(char.IsAsciiLetterOrDigit).ToArray());
        return safe.Length <= 8 ? safe : safe[^8..];
    }

    /// <inheritdoc />
    public ReportBundle Build(ReportModel model, ReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        var json = VtdJson.Serialize(model);
        var hash = CanonicalHash.OfText(json);
        var hits = new List<SweepHit>(_redactor.Sweep(json));
        if (model.Snapshot.Meta.Redaction.Level != RedactionLevel.Share)
        {
            hits.Add(new SweepHit { Path = "/snapshot/meta/redaction/level", Rule = RedactionSweep.Rules.NotShareLevel, Section = "meta" });
        }

        if (hits.Count > 0)
        {
            return Blocked(model, hash, hits);
        }

        string? html = null, markdown = null, digest = null;
        var formats = options.Formats;
        if (formats.Contains(ReportFormat.Html))
        {
            var view = ReportView.Build(model, _kb, options.Language);
            var helper = HelperView.Build(model, _kb, options.HelperLanguage);
            html = Join(HtmlRenderer.Render(view, helper, Lang(options.HelperLanguage), hash), "html", hits, System.Net.WebUtility.HtmlDecode);
        }

        if (formats.Contains(ReportFormat.Markdown))
        {
            var view = ReportView.Build(model, _kb, options.Language);
            var max = Math.Max(1, options.MarkdownMaxChars);
            markdown = Join(MarkdownRenderer.Render(view, ReportFileName(model.Meta, "html"), max), "markdown", hits, MarkdownRenderer.Unescape);
            if (markdown.Length > max)
            {
                markdown = markdown[..max];
            }
        }

        if (formats.Contains(ReportFormat.AiDigest))
        {
            digest = Join(AiDigestRenderer.Render(model, _kb), "aiDigest", hits, text => text);
        }

        if (hits.Count > 0)
        {
            return Blocked(model, hash, hits);
        }

        return new ReportBundle
        {
            Html = html,
            Markdown = markdown,
            Json = formats.Contains(ReportFormat.Json) ? json : null,
            AiDigest = digest,
            JsonSha256 = hash,
            Redaction = model.Redaction,
            SweepHits = [],
            Blocked = false,
        };
    }

    private static ReportBundle Blocked(ReportModel model, string hash, IReadOnlyList<SweepHit> hits) => new()
    {
        JsonSha256 = hash,
        Redaction = model.Redaction,
        SweepHits = [.. hits],
        Blocked = true,
    };

    private string Join(IReadOnlyList<RenderedChunk> chunks, string rendering, List<SweepHit> hits, Func<string, string> plainText)
    {
        foreach (var chunk in chunks)
        {
            // Sweep what a reader sees (entities decoded, Markdown escapes removed): "C&#58;\Users" must still hit.
            // The redactor's sweep takes JSON; a JSON string literal is the exact text.
            foreach (var hit in _redactor.Sweep(VtdJson.Serialize(plainText(chunk.Content))))
            {
                hits.Add(hit with { Path = rendering + "#" + chunk.Name, Section = chunk.Section });
            }
        }

        return string.Concat(chunks.Select(c => c.Content));
    }

    private static string Lang(string language)
        => string.Equals(language, Languages.Turkish, StringComparison.OrdinalIgnoreCase) ? Languages.Turkish : Languages.English;
}
