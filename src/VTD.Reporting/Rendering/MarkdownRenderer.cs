using System.Text;

namespace VTD.Reporting;

/// <summary>
/// "Copy for Reddit/Discord" Markdown. ALWAYS at most <c>maxChars</c> characters (Reddit comments stop at 10,000):
/// sections are added in priority order (verdict, specs, Vanguard readiness, suspects, what was tried, monitor,
/// could-not-check); list sections shrink to fewer items plus "…and N more" before a section is dropped, single lines
/// are capped, and the "attach the .html" line and the disclaimer are always kept.
/// </summary>
internal static class MarkdownRenderer
{
    private const int MaxLine = 400;

    /// <summary>Renders the Markdown pieces (in display order; dropped sections are absent).</summary>
    /// <param name="view">View.</param>
    /// <param name="htmlFileName">File name of the HTML report to mention.</param>
    /// <param name="maxChars">Cap.</param>
    public static IReadOnlyList<RenderedChunk> Render(ReportView view, string htmlFileName, int maxChars)
    {
        var t = view.Text;
        var footer = new RenderedChunk(
            "footer",
            "meta",
            "\n" + Esc(t.Ui("md.full_report", ("file", htmlFileName))) + "\n\n_" + Esc(view.Disclaimer) + "_\n");

        // (display order, priority, builder for n items, item count)
        var candidates = new List<Candidate>
        {
            new(0, 0, "head", "meta", _ => "**" + Esc(view.Title) + "** · " + Esc(view.CreatedLine) + " · `" + view.ReportId + "`\n", 0),
            new(1, 1, "verdict", "analysis", _ => Verdict(view), 0),
            new(2, 2, "specs", "hardware", _ => "**" + Esc(t.Ui("md.pc")) + ":** " + Cap(Esc(view.Specs)) + "\n", 0),
            new(3, 3, "readiness", "security", _ => "**" + Esc(t.Ui("system.readiness")) + ":** " + Cap(Esc(view.Readiness)) + "\n" + string.Concat(view.SystemNotes.Select(n => "_" + Esc(n) + "_\n")), 0),
            new(4, 4, "suspects", "analysis", n => Suspects(view, n), Math.Min(3, view.Suspects.Count)),
            new(5, 5, "tried", "attempts", n => List(t.Ui("md.tried"), view.Attempts, n, t), view.Attempts.Count),
            new(6, 6, "monitor", "monitor", _ => view.MonitorLines.Count == 0 ? string.Empty : "**" + Esc(t.Ui("section.monitor")) + ":** " + Cap(Esc(string.Join(" · ", view.MonitorLines))) + "\n", 0),
            new(7, 7, "couldnotcheck", "analysis", n => List(t.Ui("section.couldnotcheck"), view.CouldNotCheck, n, t), view.CouldNotCheck.Count),
        };

        var budget = maxChars - footer.Content.Length;
        var chosen = new List<(Candidate C, string Text)>();
        foreach (var c in candidates.OrderBy(c => c.Priority))
        {
            for (var n = c.Items; n >= 0; n--)
            {
                var text = c.Build(n);
                if (text.Length == 0)
                {
                    break;
                }

                var block = text + "\n";
                if (block.Length <= budget)
                {
                    chosen.Add((c, block));
                    budget -= block.Length;
                    break;
                }

                if (c.Items == 0)
                {
                    break;
                }
            }
        }

        var chunks = chosen.OrderBy(x => x.C.Order).Select(x => new RenderedChunk(x.C.Name, x.C.Section, x.Text)).ToList();
        chunks.Add(footer);

        // Final guarantee (a cap smaller than the footer itself): hard cut.
        var total = chunks.Sum(c => c.Content.Length);
        if (total > maxChars)
        {
            var all = string.Concat(chunks.Select(c => c.Content));
            return [new RenderedChunk("all", "meta", all[..Math.Max(0, maxChars - 1)] + "…")];
        }

        return chunks;
    }

    /// <summary>Escapes Markdown control characters in a value.</summary>
    /// <param name="text">Text.</param>
    public static string Esc(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (ch is '\\' or '*' or '_' or '`' or '[' or ']' or '#' or '<' or '>' or '|' or '~')
            {
                sb.Append('\\');
            }

            sb.Append(ch is '\r' or '\n' ? ' ' : ch);
        }

        return sb.ToString();
    }

    /// <summary>Removes the escapes <see cref="Esc"/> added (for the sweep: it must see what a reader sees).</summary>
    /// <param name="markdown">Markdown.</param>
    public static string Unescape(string markdown)
    {
        var sb = new StringBuilder(markdown.Length);
        for (var i = 0; i < markdown.Length; i++)
        {
            if (markdown[i] == '\\' && i + 1 < markdown.Length && markdown[i + 1] is '\\' or '*' or '_' or '`' or '[' or ']' or '#' or '<' or '>' or '|' or '~')
            {
                i++;
            }

            sb.Append(markdown[i]);
        }

        return sb.ToString();
    }

    private static string Cap(string line) => line.Length <= MaxLine ? line : line[..(MaxLine - 1)] + "…";

    private static string Verdict(ReportView view)
    {
        var t = view.Text;
        var sb = new StringBuilder();
        sb.Append("**").Append(Esc(t.Ui("verdict.label"))).Append(":** ").Append(Esc(view.BucketName)).Append(". ").Append(Esc(view.BucketSentence)).Append('\n');
        if (view.RouteLine is not null)
        {
            sb.Append(Cap(Esc(view.RouteLine))).Append('\n');
        }

        if (view.TopLine is not null)
        {
            sb.Append(Cap(Esc(view.TopLine))).Append('\n');
        }

        return sb.ToString();
    }

    private static string Suspects(ReportView view, int count)
    {
        var t = view.Text;
        if (view.Suspects.Count == 0)
        {
            return "**" + Esc(t.Ui("section.suspects")) + ":** " + Esc(t.Ui("suspects.none")) + "\n";
        }

        var sb = new StringBuilder();
        sb.Append("**").Append(Esc(t.Ui("section.suspects"))).Append("**\n");
        for (var i = 0; i < count; i++)
        {
            var s = view.Suspects[i];
            sb.Append(i + 1).Append(". **").Append(Cap(Esc(s.Title))).Append("** · ").Append(Esc(s.Band)).Append(" · ").Append(Esc(s.BucketName)).Append('\n');
            foreach (var why in s.Why)
            {
                sb.Append("   - ").Append(Cap(Esc(why))).Append('\n');
            }
        }

        var rest = view.Suspects.Count - count;
        if (rest > 0)
        {
            sb.Append(Esc(t.Ui("md.more", ("count", rest)))).Append('\n');
        }

        return sb.ToString();
    }

    private static string List(string title, IReadOnlyList<string> items, int count, ReportText t)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("**").Append(Esc(title)).Append("**\n");
        foreach (var item in items.Take(count))
        {
            sb.Append("- ").Append(Cap(Esc(item))).Append('\n');
        }

        if (items.Count > count)
        {
            sb.Append(Esc(t.Ui("md.more", ("count", items.Count - count)))).Append('\n');
        }

        return sb.ToString();
    }

    private sealed record Candidate(int Order, int Priority, string Name, string Section, Func<int, string> Build, int Items);
}
