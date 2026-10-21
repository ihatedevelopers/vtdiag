using System.Globalization;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Snapshots;

namespace VTD.Reporting;

/// <summary>
/// "Copy for AI" (DESIGN.md section 3.2): a compact, redacted Markdown digest for a helper's AI agent. It holds the facts
/// that matter (specs, Vanguard readiness, ranked hypotheses with ids and evidence signals, unknowns, codes, attempts,
/// monitor numbers), the ONLY KB ids a plan may use (fixes with risk, reversibility, bind names and candidate share ids;
/// guides; questions), the plan rules and the full <c>plan.schema.json</c>, plus the <c>forSnapshotId</c> and
/// <c>kbMin</c> to put in the plan. Always English (machine section).
/// </summary>
internal static class AiDigestRenderer
{
    private const string ExamplePlanId = "01JB7Z6Q4N8V2X3Y5W7T9R1M0K";

    public static IReadOnlyList<RenderedChunk> Render(ReportModel model, IKnowledgeBase kb)
    {
        var en = ReportView.Build(model, kb, Languages.English);
        var t = en.Text;
        var analysis = model.Analysis;
        var snapshot = model.Snapshot;
        var chunks = new List<RenderedChunk>();

        var intro = new StringBuilder();
        intro.Append("# VTDiag scan digest for an AI helper\n\n")
            .Append("You are helping a VALORANT player who ran VTDiag (a free Windows diagnostic tool, not affiliated with Riot Games). ")
            .Append("Everything below is already redacted at share level: `h:` values are salted hashes and `%USERPROFILE%`/`[id]` mark removed personal data. ")
            .Append("Copy ids exactly as written. Answer in plain language, and if you suggest actions, write them as a VTDiag plan (JSON, schema below) that uses only the KB ids listed here.\n\n")
            .Append("## Values for your plan\n\n")
            .Append("- `schema`: ").Append(PlanLimits.CurrentSchema.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("- `forSnapshotId`: `").Append(C(snapshot.Meta.SnapshotId)).Append("`\n")
            .Append("- `kbMin`: `").Append(C(model.Meta.KbVersion)).Append("`\n")
            .Append("- report: `").Append(C(model.Meta.ReportId)).Append("`, app ").Append(C(model.Meta.AppVersion)).Append(", machine key `").Append(C(model.Meta.MachineKey)).Append("`\n\n");
        chunks.Add(new RenderedChunk("intro", "meta", intro.ToString()));

        var pc = new StringBuilder();
        pc.Append("## PC\n\n")
            .Append("- Specs: ").Append(en.Specs).Append('\n')
            .Append("- Vanguard readiness: ").Append(en.Readiness).Append('\n')
            .Append("- Scan: ").Append(ReportView.Camel(snapshot.Meta.Privilege.ToString())).Append(" engine, ").Append(ReportView.Camel(snapshot.Meta.Mode.ToString())).Append(" scan, Windows UI culture ").Append(C(snapshot.Meta.OsUiCulture)).Append('\n');
        foreach (var line in en.Coverage)
        {
            pc.Append("- Collector not fully read: ").Append(line).Append('\n');
        }

        pc.Append('\n');
        chunks.Add(new RenderedChunk("pc", "hardware", pc.ToString()));

        var h = new StringBuilder();
        h.Append("## Ranked hypotheses\n\n");
        if (analysis is null)
        {
            h.Append("No analysis in this export.\n\n");
        }
        else
        {
            h.Append("Posteriors are independent (two problems can co-exist) with an unknown-cause floor of ")
                .Append(HelperView.Inv(analysis.UnknownCause.Floor)).Append(". Primary bucket: `").Append(ReportView.BucketKey(analysis.PrimaryBucket)).Append("`")
                .Append(analysis.Route is { } route ? ". Route: `" + C(ReportView.Camel(route.Kind.ToString())) + "` (triggered by `" + C(route.TriggeredBy) + "`); when a route is set, do not propose fixes" : string.Empty)
                .Append(".\n\n");
            var i = 1;
            foreach (var hyp in analysis.Hypotheses.OrderByDescending(x => x.Posterior).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                h.Append(i++.ToString(CultureInfo.InvariantCulture)).Append(". `").Append(C(hyp.Id)).Append("`: ").Append(t.Kb(hyp.TitleKey, null, hyp.Id))
                    .Append(" (category `").Append(C(JsonName(hyp.Category))).Append("`, bucket `").Append(ReportView.BucketKey(hyp.Bucket)).Append("`, posterior ")
                    .Append(HelperView.Inv(hyp.Posterior)).Append(", band `").Append(ReportView.Camel(hyp.Band.ToString())).Append("`)\n");
                foreach (var c in hyp.Contributions)
                {
                    h.Append("   - signal `").Append(C(c.SignalId)).Append("` = ").Append(ReportView.Camel(c.State.ToString())).Append(", Δlog-odds ").Append(HelperView.Inv(c.DeltaLogOdds))
                        .Append(c.Counted ? string.Empty : " (not counted)");
                    var args = c.Evidence.SelectMany(e => e.Args).ToList();
                    if (args.Count > 0)
                    {
                        h.Append(", evidence ").Append(string.Join(", ", args.Select(a => C(a.Key) + "=`" + C(a.Value) + "`")));
                    }

                    h.Append('\n');
                }

                if (hyp.Entities.Count > 0)
                {
                    h.Append("   - entities: ").Append(string.Join(", ", hyp.Entities.Select(e => ReportView.Camel(e.Kind.ToString()) + " `" + C(e.Id) + "`"))).Append('\n');
                }

                if (hyp.FixIds.Count > 0 || hyp.GuideIds.Count > 0)
                {
                    h.Append("   - KB: ").Append(string.Join(", ", hyp.FixIds.Concat(hyp.GuideIds).Select(x => "`" + C(x) + "`"))).Append('\n');
                }
            }

            h.Append("\nUnknown cause: ").Append(HelperView.Inv(analysis.UnknownCause.Probability)).Append(analysis.UnknownCause.Leading ? " (leading: we could not pin it down)" : string.Empty).Append("\n\n");
            if (analysis.CouldNotCheck.Count > 0)
            {
                h.Append("Could not check (Unknown is not False):\n");
                foreach (var c in analysis.CouldNotCheck)
                {
                    h.Append("- `").Append(C(c.SignalId)).Append("`: ").Append(ReportView.Camel(c.Reason.ToString())).Append('\n');
                }

                h.Append('\n');
            }

            if (analysis.Codes.Count > 0)
            {
                h.Append("Error codes: ").Append(string.Join(", ", analysis.Codes.Select(c => "`" + C(c.Code) + "` (" + ReportView.Camel(c.Origin.ToString()) + (c.MatchedId is null ? ")" : " → `" + C(c.MatchedId) + "`)")))).Append("\n\n");
            }

            if (analysis.Anomalies.Count > 0)
            {
                h.Append("Unexplained findings: ").Append(string.Join(", ", analysis.Anomalies.Select(a => ReportView.Camel(a.Kind.ToString()) + " `" + C(a.Subject) + "`"))).Append("\n\n");
            }
        }

        chunks.Add(new RenderedChunk("hypotheses", "analysis", h.ToString()));

        var tried = new StringBuilder();
        if (model.Attempts.Count > 0)
        {
            tried.Append("## Fix attempts so far (a failed fix is evidence against its hypothesis)\n\n");
            foreach (var a in model.Attempts.OrderBy(a => a.AppliedUtc))
            {
                tried.Append("- `").Append(C(a.FixId)).Append("`: status ").Append(ReportView.Camel(a.Status.ToString())).Append(", outcome ").Append(ReportView.Camel(a.Outcome.ToString()))
                    .Append(", can undo ").Append(a.CanUndo ? "yes" : "no").Append('\n');
            }

            tried.Append('\n');
        }

        if (en.MonitorLines.Count > 0)
        {
            tried.Append("## Monitored match\n\n");
            foreach (var line in en.MonitorLines)
            {
                tried.Append("- ").Append(line).Append('\n');
            }

            foreach (var d in model.Monitor?.Detectors ?? [])
            {
                tried.Append("- detector `").Append(C(d.Id)).Append("` = ").Append(ReportView.Camel(d.State.ToString())).Append(", strength ").Append(HelperView.Inv(d.Strength)).Append('\n');
            }

            tried.Append('\n');
        }

        if (tried.Length > 0)
        {
            chunks.Add(new RenderedChunk("attempts", "attempts", tried.ToString()));
        }

        chunks.Add(new RenderedChunk("kb", "analysis", KbIds(model, kb)));
        chunks.Add(new RenderedChunk("rules", "meta", Rules()));
        chunks.Add(new RenderedChunk("schema", "meta", "## plan.schema.json\n\n```json\n" + PlanSchema.Json.TrimEnd() + "\n```\n\n"));
        chunks.Add(new RenderedChunk("example", "meta", Example(model, kb)));
        return chunks;
    }

    private static string KbIds(ReportModel model, IKnowledgeBase kb)
    {
        var candidates = Candidates(model);
        var sb = new StringBuilder();
        sb.Append("## KB ids you may use (nothing else)\n\n### Fixes\n\n");
        foreach (var fix in kb.Fixes.Where(f => f.Deprecated is null).OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            sb.Append("- `").Append(C(fix.Id)).Append("`: risk ").Append(ReportView.Camel(fix.Risk.ToString())).Append(", reversibility ").Append(ReportView.Camel(fix.Reversibility.ToString()))
                .Append(", reboot ").Append(ReportView.Camel(fix.Reboot.ToString()));
            if (fix.Risk == Contracts.Fixes.RiskLevel.High)
            {
                sb.Append(" (HIGH RISK: will be greyed out; use a guide instead)");
            }

            if (fix.AppliesTo.Count > 0)
            {
                sb.Append(", for ").Append(string.Join(", ", fix.AppliesTo.Select(a => "`" + C(a) + "`")));
            }

            sb.Append('\n');
            foreach (var (name, bind) in fix.Bind.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                sb.Append("  - bind `").Append(C(name)).Append("`: ").Append(ReportView.Camel(bind.Kind.ToString())).Append(", ").Append(bind.Pick == Contracts.Fixes.BindPick.One ? "exactly one id" : "one or more ids");
                var ids = candidates.TryGetValue(bind.Kind, out var list) ? list : [];
                sb.Append(ids.Count == 0 ? ", no candidate in this scan" : ", candidates: " + string.Join(", ", ids.Select(c => "`" + C(c.Id) + "`" + (c.Label is null ? string.Empty : " (" + C(c.Label) + ")")))).Append('\n');
            }
        }

        sb.Append("\n### Guides\n\n");
        foreach (var g in kb.Guides.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            sb.Append("- `").Append(C(g.Id)).Append("`: bucket `").Append(ReportView.BucketKey(g.Bucket)).Append("`, risk ").Append(ReportView.Camel(g.Risk.ToString()));
            if (g.Branches.Count > 0)
            {
                sb.Append(", branches ").Append(string.Join(", ", g.Branches.Select(b => "`" + C(b.Id) + "`")));
            }

            sb.Append('\n');
        }

        sb.Append("\n### Questions\n\n");
        foreach (var q in kb.Questions.OrderBy(q => q.Id, StringComparer.Ordinal))
        {
            sb.Append("- `").Append(C(q.Id)).Append("`\n");
        }

        sb.Append("\n### Monitor\n\n- a `monitor` step with `minutes` from 1 to 60 (the player plays a match while VTDiag watches FPS, stutters and ping)\n\n");
        return sb.ToString();
    }

    private static string Rules()
        => "## Plan rules (the player's app enforces them; a plan that breaks one is rejected or its step is greyed out)\n\n"
           + "1. Steps are only KB ids from the lists above: `fix`, `guide`, `question` or `monitor`. Never commands, scripts, file paths, registry keys or URLs as actions.\n"
           + "2. At most " + PlanLimits.MaxSteps.ToString(CultureInfo.InvariantCulture) + " steps. `expiresUtc` at most 7 days after `createdUtc`; `createdUtc` must not be in the future.\n"
           + "3. `planId` is a new ULID (26 characters, Crockford base32). `forSnapshotId` and `kbMin` exactly as given above.\n"
           + "4. A fix step must give every bind name of that fix, with ids copied exactly from its candidates. The player's app rescans and maps them to live entities; an id that no longer matches greys the step out.\n"
           + "5. High-risk changes (BIOS, Secure Boot, firmware) are guides only.\n"
           + "6. `message` (at most " + PlanLimits.MaxMessageLength.ToString(CultureInfo.InvariantCulture) + " characters) and step `note`s (at most " + PlanLimits.MaxNoteLength.ToString(CultureInfo.InvariantCulture) + ") are plain text shown to the player. Links are shown as \"link from helper\" and never opened automatically.\n"
           + "7. Every step needs the player's own OK, one at a time; there is no \"apply all\", and a signature never removes consent. Never claim a PC change can fix a Riot/account-side or ISP problem.\n"
           + "8. Output the plan as a single JSON document (save it as `VTDiag-<shortid>.vtdplan`).\n\n";

    private static string Example(ReportModel model, IKnowledgeBase kb)
    {
        var steps = new List<PlanStep>();
        if (model.Analysis?.Proposals.FirstOrDefault(p => p.Available && kb.FindFix(p.FixId) is { Risk: not Contracts.Fixes.RiskLevel.High }) is { } proposal)
        {
            steps.Add(new PlanFixStep
            {
                FixId = proposal.FixId,
                Bind = proposal.Binding.Entities.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)[.. e.Value.Select(v => v.Id)], StringComparer.Ordinal),
                Note = "Try this first. You can undo it with one click.",
            });
            steps.Add(new PlanMonitorStep { Minutes = 10, Note = "Play one Deathmatch so we can compare." });
        }
        else if (model.Analysis?.Guides.FirstOrDefault() is { } guide && kb.FindGuide(guide.GuideId) is not null)
        {
            steps.Add(new PlanGuideStep { GuideId = guide.GuideId, BranchId = guide.BranchId, Note = "Follow these steps, then scan again." });
        }
        else
        {
            steps.Add(new PlanMonitorStep { Minutes = 10, Note = "Play one match so we can see what happens." });
        }

        var plan = new FixPlan
        {
            Schema = PlanLimits.CurrentSchema,
            PlanId = ExamplePlanId,
            ForSnapshotId = model.Snapshot.Meta.SnapshotId,
            ForMachineKey = model.Snapshot.Meta.MachineKey,
            KbMin = model.Meta.KbVersion,
            CreatedUtc = model.Meta.CreatedUtc,
            ExpiresUtc = model.Meta.CreatedUtc.AddDays(3),
            Author = new PlanAuthor { Name = "your name" },
            Message = "Here is what I would try, in order.",
            Steps = steps,
        };
        return "## Example plan (replace planId with a new ULID and the dates with now / now + up to 7 days)\n\n```json\n"
               + Contracts.Serialization.VtdJson.Serialize(plan, indented: true) + "\n```\n";
    }

    private static Dictionary<EntityKind, List<(string Id, string? Label)>> Candidates(ReportModel model)
    {
        var map = new Dictionary<EntityKind, List<(string Id, string? Label)>>();
        void Add(EntityKind kind, string? id, string? label = null)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (!map.TryGetValue(kind, out var list))
            {
                list = [];
                map[kind] = list;
            }

            var existing = list.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                if (list[existing].Label is null && label is not null)
                {
                    list[existing] = (list[existing].Id, label);
                }
            }
            else if (list.Count < 25)
            {
                list.Add((id, label));
            }
        }

        if (model.Analysis is { } analysis)
        {
            foreach (var e in analysis.Hypotheses.SelectMany(h => h.Entities))
            {
                Add(e.Kind, e.Id, e.DisplayName);
            }

            foreach (var e in analysis.Proposals.SelectMany(p => p.Binding.Entities.Values).SelectMany(v => v))
            {
                Add(e.Kind, e.Id, e.DisplayName);
            }
        }

        if (model.Snapshot.Network.Adapters.TryGetValue(out var adapters))
        {
            foreach (var a in adapters)
            {
                Add(EntityKind.Adapter, a.InstanceId, a.Description + (a.IsDefaultRoute ? ", carries the internet connection: never disable" : string.Empty) + ", " + ReportView.Camel(a.Status.ToString()));
            }
        }

        if (model.Snapshot.Devices.TryGetValue(out var devices))
        {
            foreach (var d in devices.Where(d => d.ProblemCode != 0))
            {
                Add(EntityKind.Device, d.InstanceId, d.FriendlyName + ", problem code " + d.ProblemCode.ToString(CultureInfo.InvariantCulture));
            }
        }

        return map;
    }

    private static string JsonName(ProblemCategory category)
        => Contracts.Serialization.VtdJson.Serialize(category).Trim('"');

    /// <summary>Makes a value safe inside a Markdown code span.</summary>
    private static string C(string value) => value.Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ');
}
