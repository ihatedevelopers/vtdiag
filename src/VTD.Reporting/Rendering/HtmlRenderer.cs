using System.Globalization;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Monitoring;

namespace VTD.Reporting;

/// <summary>A rendered piece of a report and the model section it came from (for sweep hits).</summary>
/// <param name="Name">Piece name (HTML section id).</param>
/// <param name="Section">Model section (<c>analysis</c>, <c>attempts</c>, <c>monitor</c>, a snapshot section, <c>meta</c>).</param>
/// <param name="Content">Rendered text.</param>
internal sealed record RenderedChunk(string Name, string Section, string Content);

/// <summary>
/// Single-file HTML report: inline CSS, inline SVG chart, ZERO JavaScript (no script elements, no event-handler
/// attributes, no links at all), a Content-Security-Policy that forbids everything but inline style and data: images,
/// print-friendly, dark text on a light background. Every value is HTML-escaped and additionally has <c>=</c> and
/// <c>:</c> encoded as entities, so free text such as <c>onerror=</c> or <c>javascript:</c> cannot even look like
/// markup to a naive scanner.
/// </summary>
internal static class HtmlRenderer
{
    private const string Css = """
        :root{color-scheme:light}
        *{box-sizing:border-box}
        body{margin:0;background:#fff;color:#1a1a1a;font:15px/1.5 "Segoe UI",system-ui,-apple-system,Arial,sans-serif}
        main{max-width:960px;margin:0 auto;padding:24px 20px 40px}
        h1{font-size:24px;margin:0 0 4px}
        h2{font-size:18px;margin:28px 0 8px;padding-bottom:4px;border-bottom:1px solid #d0d0d0}
        h3{font-size:15px;margin:16px 0 4px}
        .meta,.small{color:#555;font-size:13px}
        .verdict{border:2px solid #888;border-radius:8px;padding:12px 16px;margin-top:16px;background:#f7f7f7}
        .verdict .bucket{font-size:18px;font-weight:700;margin:0 0 6px}
        .b-fixableHere{border-color:#2e7d32;background:#eef7ee}
        .b-guidedManual{border-color:#1565c0;background:#edf3fb}
        .b-externalNetwork{border-color:#b26a00;background:#fdf5e6}
        .b-riotOrAccount{border-color:#6a1b9a;background:#f5eef8}
        .b-likelyHardware{border-color:#b71c1c;background:#fbeeee}
        .b-unknown{border-color:#616161;background:#f3f3f3}
        ul,ol{padding-left:22px;margin:6px 0}
        li{margin:3px 0}
        .health li{list-style:none;margin-left:-22px}
        .st{display:inline-block;min-width:9.5em;font-weight:600}
        .st-ok{color:#2e7d32}.st-warn{color:#b26a00}.st-fail{color:#b71c1c}.st-unknown{color:#555}.st-na{color:#777}
        .band{font-weight:600}
        .why{color:#333}
        code,.mono{font-family:Consolas,"Cascadia Mono",monospace;font-size:13px;word-break:break-all}
        .chart{max-width:100%;height:auto;border:1px solid #ddd;border-radius:4px;background:#fff}
        footer{margin-top:32px;padding-top:8px;border-top:1px solid #d0d0d0;color:#555;font-size:13px}
        .disclaimer{font-weight:600;color:#333}
        @media print{body{font-size:12px}main{max-width:none;padding:0}h2{break-after:avoid}section{break-inside:avoid-page}}
        """;

    /// <summary>Renders the HTML pieces (sections in document order).</summary>
    /// <param name="view">Player-language view.</param>
    /// <param name="helper">Helper section groups.</param>
    /// <param name="helperLanguage">Helper language.</param>
    /// <param name="jsonSha256">SHA-256 of the JSON rendering.</param>
    public static IReadOnlyList<RenderedChunk> Render(ReportView view, IReadOnlyList<HelperGroup> helper, string helperLanguage, string jsonSha256)
    {
        var t = view.Text;
        var chunks = new List<RenderedChunk>();

        var head = new StringBuilder();
        head.Append("<!DOCTYPE html>\n<html lang=\"").Append(t.Language).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
            .Append("<title>").Append(E(view.Title + " " + view.ReportId)).Append("</title>\n<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n<main>\n")
            .Append("<header>\n<h1>").Append(E(view.Title)).Append("</h1>\n<p class=\"meta\">").Append(E(view.CreatedLine)).Append(" · ")
            .Append(E(t.Ui("report.id"))).Append(" <span class=\"mono\">").Append(E(view.ReportId)).Append("</span></p>\n</header>\n");
        chunks.Add(new RenderedChunk("head", "meta", head.ToString()));

        var verdict = new StringBuilder();
        verdict.Append("<section id=\"verdict\" class=\"verdict b-").Append(ReportView.BucketKey(view.Bucket)).Append("\">\n<h2>").Append(E(t.Ui("section.summary"))).Append("</h2>\n")
            .Append("<p class=\"bucket\">").Append(E(t.Ui("verdict.label") + ": " + view.BucketName)).Append("</p>\n")
            .Append("<p>").Append(E(view.BucketSentence)).Append("</p>\n");
        if (view.RouteLine is not null)
        {
            verdict.Append("<p>").Append(E(view.RouteLine)).Append("</p>\n");
        }

        if (view.TopLine is not null)
        {
            verdict.Append("<p>").Append(E(view.TopLine)).Append("</p>\n");
        }

        if (view.UnknownLeading)
        {
            verdict.Append("<p>").Append(E(t.Ui("verdict.unknown_leading"))).Append("</p>\n");
        }

        verdict.Append("</section>\n");
        chunks.Add(new RenderedChunk("verdict", "analysis", verdict.ToString()));

        var health = new StringBuilder();
        health.Append("<section id=\"health\">\n<h2>").Append(E(t.Ui("section.health"))).Append("</h2>\n");
        if (view.Health.Count == 0)
        {
            health.Append("<p>").Append(E(t.Ui("health.none"))).Append("</p>\n");
        }
        else
        {
            if (view.HealthyCount > 0)
            {
                health.Append("<p>").Append(E(t.Ui("health.good_news", ("count", view.HealthyCount)))).Append("</p>\n");
            }

            health.Append("<ul class=\"health\">\n");
            foreach (var h in view.Health)
            {
                health.Append("<li><span class=\"st st-").Append(h.CssClass).Append("\">").Append(E(h.Status)).Append("</span> ").Append(E(h.Title));
                if (h.Detail is not null)
                {
                    health.Append(" <span class=\"small\">").Append(E(h.Detail)).Append("</span>");
                }

                health.Append("</li>\n");
            }

            health.Append("</ul>\n");
        }

        health.Append("</section>\n");
        chunks.Add(new RenderedChunk("health", "analysis", health.ToString()));

        var suspects = new StringBuilder();
        suspects.Append("<section id=\"suspects\">\n<h2>").Append(E(t.Ui("section.suspects"))).Append("</h2>\n");
        if (view.Suspects.Count == 0)
        {
            suspects.Append("<p>").Append(E(t.Ui("suspects.none"))).Append("</p>\n");
        }
        else
        {
            suspects.Append("<ol>\n");
            foreach (var s in view.Suspects)
            {
                suspects.Append("<li><strong>").Append(E(s.Title)).Append("</strong> · <span class=\"band\">").Append(E(s.Band)).Append("</span> · ")
                    .Append(E(s.BucketName));
                if (s.Why.Count > 0)
                {
                    suspects.Append("\n<div class=\"why\">").Append(E(t.Ui("suspects.why"))).Append("</div>\n<ul>\n");
                    foreach (var w in s.Why)
                    {
                        suspects.Append("<li>").Append(E(w)).Append("</li>\n");
                    }

                    suspects.Append("</ul>");
                }

                if (s.FixAvailable)
                {
                    suspects.Append("\n<div class=\"small\">").Append(E(t.Ui("suspects.fix_available"))).Append("</div>");
                }
                else if (s.GuideAvailable)
                {
                    suspects.Append("\n<div class=\"small\">").Append(E(t.Ui("suspects.guide_available"))).Append("</div>");
                }

                suspects.Append("</li>\n");
            }

            suspects.Append("</ol>\n");
        }

        suspects.Append("</section>\n");
        chunks.Add(new RenderedChunk("suspects", "analysis", suspects.ToString()));

        chunks.Add(new RenderedChunk("couldnotcheck", "analysis", ListSection("couldnotcheck", t.Ui("section.couldnotcheck"), view.CouldNotCheck, t.Ui("couldnotcheck.none"))));
        chunks.Add(new RenderedChunk("actions", "attempts", ListSection("actions", t.Ui("section.actions"), view.Attempts, t.Ui("actions.none"))));

        var monitor = new StringBuilder();
        monitor.Append("<section id=\"monitor\">\n<h2>").Append(E(t.Ui("section.monitor"))).Append("</h2>\n");
        if (view.MonitorLines.Count == 0)
        {
            monitor.Append("<p>").Append(E(t.Ui("monitor.none"))).Append("</p>\n");
        }
        else
        {
            if (view.BaselineLines.Count > 0)
            {
                monitor.Append("<h3>").Append(E(t.Ui("monitor.before"))).Append("</h3>\n<ul>\n");
                foreach (var line in view.BaselineLines)
                {
                    monitor.Append("<li>").Append(E(line)).Append("</li>\n");
                }

                monitor.Append("</ul>\n<h3>").Append(E(t.Ui("monitor.after"))).Append("</h3>\n");
            }

            monitor.Append("<ul>\n");
            foreach (var line in view.MonitorLines)
            {
                monitor.Append("<li>").Append(E(line)).Append("</li>\n");
            }

            monitor.Append("</ul>\n");
            monitor.Append(Chart(view.Series, t));
        }

        monitor.Append("</section>\n");
        chunks.Add(new RenderedChunk("monitor", "monitor", monitor.ToString()));

        var system = new StringBuilder();
        system.Append("<section id=\"system\">\n<h2>").Append(E(t.Ui("section.system"))).Append("</h2>\n")
            .Append("<p><strong>").Append(E(t.Ui("system.specs"))).Append("</strong>: ").Append(E(view.Specs)).Append("</p>\n")
            .Append("<p><strong>").Append(E(t.Ui("system.readiness"))).Append("</strong>: ").Append(E(view.Readiness)).Append("</p>\n");
        foreach (var note in view.SystemNotes)
        {
            system.Append("<p class=\"small\">").Append(E(note)).Append("</p>\n");
        }

        system.Append("</section>\n");
        chunks.Add(new RenderedChunk("system", "hardware", system.ToString()));

        chunks.Add(new RenderedChunk("coverage", "runs", ListSection("coverage", t.Ui("section.coverage"), view.Coverage, t.Ui("coverage.all_ok"))));

        if (helper.Count > 0)
        {
            var h = new StringBuilder();
            h.Append("<section id=\"helper\" lang=\"").Append(helperLanguage).Append("\">\n");
            var first = true;
            foreach (var group in helper)
            {
                h.Append(first ? "<h2>" : "<h3>").Append(E(group.Title)).Append(first ? "</h2>\n" : "</h3>\n").Append("<ul class=\"mono\">\n");
                foreach (var line in group.Lines)
                {
                    h.Append("<li>").Append(E(line)).Append("</li>\n");
                }

                h.Append("</ul>\n");
                first = false;
            }

            h.Append("</section>\n");
            chunks.Add(new RenderedChunk("helper", "analysis", h.ToString()));
        }

        chunks.Add(new RenderedChunk("privacy", "meta", ListSection("privacy", t.Ui("section.privacy"), view.Privacy, string.Empty)));

        var footer = new StringBuilder();
        footer.Append("<footer>\n<p>").Append(E(view.Versions)).Append("<br>").Append(E(t.Ui("report.id"))).Append(" <span class=\"mono\">").Append(E(view.ReportId)).Append("</span><br>")
            .Append(E(view.MachineKeyLine)).Append("<br>").Append(E(t.Ui("footer.hash", ("hash", jsonSha256)))).Append("</p>\n")
            .Append("<p class=\"disclaimer\">").Append(E(view.Disclaimer)).Append("</p>\n</footer>\n</main>\n</body>\n</html>\n");
        chunks.Add(new RenderedChunk("footer", "meta", footer.ToString()));
        return chunks;
    }

    /// <summary>HTML-escapes text and encodes <c>=</c> and <c>:</c> as entities.</summary>
    /// <param name="text">Text.</param>
    public static string E(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                '=' => "&#61;",
                ':' => "&#58;",
                '`' => "&#96;",
                _ when char.IsControl(ch) && ch is not '\n' and not '\t' => " ",
                _ => ch.ToString(),
            });
        }

        return sb.ToString();
    }

    private static string ListSection(string id, string title, IReadOnlyList<string> items, string empty)
    {
        var sb = new StringBuilder();
        sb.Append("<section id=\"").Append(id).Append("\">\n<h2>").Append(E(title)).Append("</h2>\n");
        if (items.Count == 0)
        {
            if (empty.Length > 0)
            {
                sb.Append("<p>").Append(E(empty)).Append("</p>\n");
            }
        }
        else
        {
            sb.Append("<ul>\n");
            foreach (var item in items)
            {
                sb.Append("<li>").Append(E(item)).Append("</li>\n");
            }

            sb.Append("</ul>\n");
        }

        sb.Append("</section>\n");
        return sb.ToString();
    }

    private static string Chart(IReadOnlyList<MinuteSample> series, ReportText t)
    {
        var points = series.Where(s => s.Fps is not null).OrderBy(s => s.Utc).ToList();
        if (points.Count < 2)
        {
            return string.Empty;
        }

        const double W = 640, H = 180, L = 44, R = 12, T = 22, B = 26;
        var max = points.Max(p => Math.Max(p.Fps ?? 0, p.P1LowFps ?? 0));
        max = max <= 0 ? 1 : Math.Ceiling(max / 50) * 50;
        string X(int i) => (L + ((W - L - R) * i / (points.Count - 1))).ToString("0.#", CultureInfo.InvariantCulture);
        string Y(double v) => (T + ((H - T - B) * (1 - (v / max)))).ToString("0.#", CultureInfo.InvariantCulture);
        string Line(Func<MinuteSample, double?> pick)
            => string.Join(' ', points.Select((p, i) => pick(p) is { } v ? X(i) + "," + Y(v) : null).Where(s => s is not null));

        var sb = new StringBuilder();
        sb.Append("<svg class=\"chart\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 180\" width=\"640\" height=\"180\" role=\"img\" aria-label=\"")
            .Append(E(t.Ui("monitor.chart_title"))).Append("\">\n")
            .Append("<rect x=\"0\" y=\"0\" width=\"640\" height=\"180\" fill=\"#ffffff\"/>\n");
        foreach (var frac in (double[])[0, 0.5, 1])
        {
            var y = Y(max * frac);
            sb.Append("<line x1=\"").Append(L.ToString(CultureInfo.InvariantCulture)).Append("\" x2=\"").Append((W - R).ToString(CultureInfo.InvariantCulture))
                .Append("\" y1=\"").Append(y).Append("\" y2=\"").Append(y).Append("\" stroke=\"#e0e0e0\"/>\n")
                .Append("<text x=\"4\" y=\"").Append(y).Append("\" font-size=\"11\" fill=\"#555\">").Append((max * frac).ToString("0", CultureInfo.InvariantCulture)).Append("</text>\n");
        }

        sb.Append("<polyline fill=\"none\" stroke=\"#1565c0\" stroke-width=\"2\" points=\"").Append(Line(p => p.Fps)).Append("\"/>\n");
        if (points.Any(p => p.P1LowFps is not null))
        {
            sb.Append("<polyline fill=\"none\" stroke=\"#b71c1c\" stroke-width=\"1.5\" stroke-dasharray=\"4 3\" points=\"").Append(Line(p => p.P1LowFps)).Append("\"/>\n");
        }

        sb.Append("<text x=\"").Append(L.ToString(CultureInfo.InvariantCulture)).Append("\" y=\"14\" font-size=\"12\" fill=\"#1565c0\">").Append(E(t.Ui("monitor.chart_fps"))).Append("</text>\n")
            .Append("<text x=\"200\" y=\"14\" font-size=\"12\" fill=\"#b71c1c\">").Append(E(t.Ui("monitor.chart_low"))).Append("</text>\n")
            .Append("<text x=\"").Append(L.ToString(CultureInfo.InvariantCulture)).Append("\" y=\"174\" font-size=\"11\" fill=\"#555\">").Append(E(t.Ui("monitor.chart_title"))).Append("</text>\n")
            .Append("</svg>\n");
        return sb.ToString();
    }
}
