using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Snapshots;

namespace VTD.Reporting;

/// <summary>One ranked suspect, ready for display.</summary>
internal sealed record SuspectView(string Id, string Title, string Band, string BucketName, IReadOnlyList<string> Why, bool FixAvailable, bool GuideAvailable);

/// <summary>One health line.</summary>
internal sealed record HealthView(string Title, string Status, string CssClass, string? Detail);

/// <summary>
/// Display strings derived from one (already redacted) <see cref="ReportModel"/> in one language. Renderers only
/// format and escape these; they never read the model themselves, so the three renderings say the same thing.
/// Unknown facts are never claimed: they are skipped in one-liners and shown as "couldn't check" in lists.
/// </summary>
internal sealed class ReportView
{
    private ReportView(ReportText text) => Text = text;

    public ReportText Text { get; }

    public required string Title { get; init; }

    public required string CreatedLine { get; init; }

    public required string ReportId { get; init; }

    public required HonestyBucket Bucket { get; init; }

    public required string BucketName { get; init; }

    public required string BucketSentence { get; init; }

    public string? TopLine { get; init; }

    public string? RouteLine { get; init; }

    public bool UnknownLeading { get; init; }

    public IReadOnlyList<HealthView> Health { get; init; } = [];

    public int HealthyCount { get; init; }

    public IReadOnlyList<SuspectView> Suspects { get; init; } = [];

    public IReadOnlyList<string> CouldNotCheck { get; init; } = [];

    public IReadOnlyList<string> Attempts { get; init; } = [];

    public IReadOnlyList<string> MonitorLines { get; init; } = [];

    public IReadOnlyList<string> BaselineLines { get; init; } = [];

    public IReadOnlyList<MinuteSample> Series { get; init; } = [];

    public required string Specs { get; init; }

    public required string Readiness { get; init; }

    public IReadOnlyList<string> SystemNotes { get; init; } = [];

    public IReadOnlyList<string> Coverage { get; init; } = [];

    public IReadOnlyList<string> Privacy { get; init; } = [];

    public required string Versions { get; init; }

    public required string Disclaimer { get; init; }

    public required string MachineKeyLine { get; init; }

    /// <summary>Builds the view.</summary>
    /// <param name="model">Redacted model.</param>
    /// <param name="kb">KB.</param>
    /// <param name="language">Language.</param>
    public static ReportView Build(ReportModel model, IKnowledgeBase kb, string language)
    {
        var t = new ReportText(kb, language);
        var analysis = model.Analysis;
        var bucket = analysis?.Route?.Bucket ?? analysis?.PrimaryBucket ?? HonestyBucket.Unknown;
        var suspects = BuildSuspects(analysis, t, kb);
        var top = suspects.FirstOrDefault();
        var health = HealthItems(analysis, t);
        return new ReportView(t)
        {
            Title = t.Ui("report.title"),
            CreatedLine = t.Ui("report.created", ("date", t.Date(model.Meta.CreatedUtc))),
            ReportId = model.Meta.ReportId,
            Bucket = bucket,
            BucketName = BucketNameOf(t, bucket),
            BucketSentence = t.Ui("bucket." + BucketKey(bucket) + ".sentence"),
            TopLine = top is null ? null : t.Ui("verdict.top", ("title", top.Title), ("band", top.Band)),
            RouteLine = analysis?.Route is { } route ? t.Ui("verdict.route", ("text", t.Kb(route.TextKey, null, route.TriggeredBy))) : null,
            UnknownLeading = analysis?.UnknownCause.Leading ?? true,
            Health = health,
            HealthyCount = analysis?.Health.Items.Count(i => i.Status == HealthStatus.Ok) ?? 0,
            Suspects = suspects,
            CouldNotCheck = CouldNotCheckItems(analysis, t, kb),
            Attempts = AttemptLines(model.Attempts, t),
            MonitorLines = model.Monitor is { } m ? MonitorLinesOf(m, t) : [],
            BaselineLines = model.Baseline is { } b && model.Monitor is not null ? MonitorLinesOf(b, t) : [],
            Series = model.Monitor?.Series ?? [],
            Specs = SpecsLine(model.Snapshot, t),
            Readiness = ReadinessLine(model.Snapshot, t),
            SystemNotes = SystemNotesOf(model.Snapshot, t),
            Coverage = CoverageLines(model.Snapshot, t),
            Privacy = PrivacyLines(model.Redaction, t),
            Versions = t.Ui("footer.versions", ("app", model.Meta.AppVersion), ("kb", model.Meta.KbVersion)),
            Disclaimer = DisclaimerText(kb, t, model.Meta.DisclaimerKey),
            MachineKeyLine = t.Ui("footer.machine", ("key", model.Meta.MachineKey)),
        };
    }

    /// <summary>The not-affiliated disclaimer; the KB text when it has one, the report's own otherwise. Never empty.</summary>
    /// <param name="kb">KB.</param>
    /// <param name="t">Text.</param>
    /// <param name="key">Disclaimer key.</param>
    public static string DisclaimerText(IKnowledgeBase kb, ReportText t, string key)
        => kb.HasText(key, t.Language) ? kb.Text(key, t.Language) : t.Ui("report.disclaimer");

    public static string BucketKey(HonestyBucket bucket) => bucket switch
    {
        HonestyBucket.FixableHere => "fixableHere",
        HonestyBucket.GuidedManual => "guidedManual",
        HonestyBucket.ExternalNetwork => "externalNetwork",
        HonestyBucket.RiotOrAccount => "riotOrAccount",
        HonestyBucket.LikelyHardware => "likelyHardware",
        _ => "unknown",
    };

    public static string BucketNameOf(ReportText t, HonestyBucket bucket) => t.Ui("bucket." + BucketKey(bucket) + ".name");

    public static string BandName(ReportText t, ConfidenceBand band) => band switch
    {
        ConfidenceBand.VeryLikely => t.Ui("band.veryLikely"),
        ConfidenceBand.Likely => t.Ui("band.likely"),
        ConfidenceBand.Possible => t.Ui("band.possible"),
        _ => t.Ui("band.longShot"),
    };

    public static string StateWord(ReportText t, Tri state) => state switch
    {
        Tri.True => t.Ui("state.true"),
        Tri.False => t.Ui("state.false"),
        Tri.NotApplicable => t.Ui("state.notApplicable"),
        _ => t.Ui("state.unknown"),
    };

    public static string ReasonWord(ReportText t, UnknownReason? reason) => t.Ui("reason." + Camel((reason ?? UnknownReason.Failed).ToString()));

    public static string SignalTitle(ReportText t, IKnowledgeBase kb, string signalId)
    {
        var def = kb.Signals.FirstOrDefault(s => string.Equals(s.Id, signalId, StringComparison.Ordinal));
        return def?.TitleKey is { } key && (kb.HasText(key, t.Language) || kb.HasText(key, Languages.English) || t.HasUi(key))
            ? t.Kb(key)
            : ReportText.HumanizeId(signalId);
    }

    public static string Camel(string pascal) => pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];

    private static IReadOnlyList<SuspectView> BuildSuspects(AnalysisResult? analysis, ReportText t, IKnowledgeBase kb)
    {
        if (analysis is null)
        {
            return [];
        }

        var list = new List<SuspectView>();
        foreach (var h in analysis.Hypotheses.OrderByDescending(h => h.Posterior).ThenBy(h => h.Id, StringComparer.Ordinal))
        {
            var why = h.Contributions
                .Where(c => c.Counted && c.DeltaLogOdds > 0 && (c.State == Tri.True || c.State == Tri.False))
                .OrderByDescending(c => c.DeltaLogOdds)
                .ThenBy(c => c.EvidenceId, StringComparer.Ordinal)
                .Take(3)
                .Select(c => WhyText(c, t, kb))
                .ToList();
            list.Add(new SuspectView(
                h.Id,
                t.Kb(h.TitleKey, null, h.Id),
                BandName(t, h.Band),
                BucketNameOf(t, h.Bucket),
                why,
                h.FixIds.Count > 0 && analysis.Proposals.Any(p => p.Available && h.FixIds.Contains(p.FixId, StringComparer.Ordinal)),
                h.GuideIds.Count > 0));
        }

        return list;
    }

    private static string WhyText(EvidenceContribution c, ReportText t, IKnowledgeBase kb)
    {
        var evidence = c.Evidence.FirstOrDefault();
        var key = c.TextKey ?? evidence?.TextKey;
        if (key is not null && (kb.HasText(key, t.Language) || kb.HasText(key, Languages.English) || t.HasUi(key)))
        {
            return t.Kb(key, evidence?.Args);
        }

        return t.Ui("suspects.evidence_state", ("signal", SignalTitle(t, kb, c.SignalId)), ("state", StateWord(t, c.State)));
    }

    private static IReadOnlyList<HealthView> HealthItems(AnalysisResult? analysis, ReportText t)
    {
        if (analysis is null)
        {
            return [];
        }

        return
        [
            .. analysis.Health.Items
                .OrderBy(i => i.Group)
                .Select(i => new HealthView(
                    t.Kb(i.TitleKey, i.Args, i.Id),
                    i.Status switch
                    {
                        HealthStatus.Ok => t.Ui("health.status.ok"),
                        HealthStatus.Warn => t.Ui("health.status.warn"),
                        HealthStatus.Fail => t.Ui("health.status.fail"),
                        HealthStatus.NotApplicable => t.Ui("health.status.notApplicable"),
                        _ => t.Ui("health.status.unknown", ("reason", ReasonWord(t, i.UnknownReason))),
                    },
                    i.Status switch
                    {
                        HealthStatus.Ok => "ok",
                        HealthStatus.Warn => "warn",
                        HealthStatus.Fail => "fail",
                        HealthStatus.NotApplicable => "na",
                        _ => "unknown",
                    },
                    i.DetailKey is null ? null : t.Kb(i.DetailKey, i.Args))),
        ];
    }

    private static IReadOnlyList<string> CouldNotCheckItems(AnalysisResult? analysis, ReportText t, IKnowledgeBase kb)
    {
        if (analysis is null)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var c in analysis.CouldNotCheck)
        {
            // TextKey names WHAT could not be checked (the signal title); the reason always comes from Reason.
            var what = c.TextKey is { } key && (kb.HasText(key, t.Language) || kb.HasText(key, Languages.English))
                ? t.Kb(key)
                : SignalTitle(t, kb, c.SignalId);
            var line = t.Ui("couldnotcheck.item", ("what", what), ("reason", ReasonWord(t, c.Reason)));
            if (c.AffectsHypotheses.Count > 0)
            {
                var titles = c.AffectsHypotheses
                    .Select(id => analysis.Hypotheses.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.Ordinal)) is { } h ? t.Kb(h.TitleKey, null, h.Id) : ReportText.Humanize(id));
                line += ". " + t.Ui("couldnotcheck.affects", ("list", string.Join("; ", titles)));
            }

            lines.Add(line);
        }

        return lines;
    }

    private static IReadOnlyList<string> AttemptLines(IReadOnlyList<AttemptSummary> attempts, ReportText t)
    {
        var lines = new List<string>();
        foreach (var a in attempts.OrderBy(a => a.AppliedUtc))
        {
            var parts = new List<string>
            {
                t.Ui("attempt.status." + Camel(a.Status.ToString())),
                t.Ui("attempt.outcome." + Camel(a.Outcome.ToString())),
            };
            if (a.Reversibility == Reversibility.OneWay)
            {
                parts.Add(t.Ui("attempt.oneway"));
            }
            else
            {
                parts.Add(a.CanUndo ? t.Ui("attempt.undo.available") : t.Ui("attempt.undo.unavailable"));
            }

            if (a.Status == AttemptStatus.AwaitingReboot)
            {
                parts.Add(t.Ui("attempt.reboot"));
            }

            if (a.RestorePointUtc is { } rp)
            {
                parts.Add(t.Ui("attempt.restore_point", ("date", t.Date(rp))));
            }

            lines.Add(t.Kb(a.TitleKey, null, a.FixId) + ": " + string.Join(" · ", parts));
        }

        return lines;
    }

    private static IReadOnlyList<string> MonitorLinesOf(MonitorSummary m, ReportText t)
    {
        var lines = new List<string>();
        var minutes = Math.Max(0, (m.EndUtc - m.StartUtc).TotalMinutes);
        lines.Add(t.Ui("monitor.duration", ("minutes", Math.Round(minutes))));
        if (m.InsufficientData)
        {
            lines.Add(t.Ui("monitor.insufficient"));
        }

        if (m.Frames is { } f)
        {
            var frames = new List<string>
            {
                t.Ui("monitor.fps", ("fps", Math.Round(f.AvgFps))),
                t.Ui("monitor.low", ("low", Math.Round(f.P1LowFps))),
                t.Ui("monitor.spikes", ("spikes", f.SpikesPerMin)),
            };
            if (f.PeriodSeconds is { } period && f.PeriodicityScore is >= 0.5)
            {
                frames.Add(t.Ui("monitor.periodic", ("period", period)));
            }

            lines.Add(string.Join(" · ", frames));
        }

        if (m.Network is { } n)
        {
            var net = new List<string>();
            if (n.RttP50Ms is { } p50)
            {
                net.Add(t.Ui("monitor.ping", ("p50", Math.Round(p50)), ("p95", Math.Round(n.RttP95Ms ?? p50))));
            }

            if (n.JitterMs is { } jitter)
            {
                net.Add(t.Ui("monitor.jitter", ("jitter", jitter)));
            }

            if (n.LossPct is { } loss)
            {
                net.Add(t.Ui("monitor.loss", ("loss", loss)));
            }

            if (n.LossAtFirstHop == true)
            {
                net.Add(t.Ui("monitor.loss_first_hop"));
            }

            if (!string.IsNullOrEmpty(n.EndpointRegion))
            {
                net.Add(t.Ui("monitor.region", ("region", n.EndpointRegion)));
            }

            if (n.Indicative)
            {
                net.Add(t.Ui("monitor.approximate"));
            }

            if (net.Count > 0)
            {
                lines.Add(string.Join(" · ", net));
            }
        }

        foreach (var c in m.Correlations.OrderByDescending(c => c.HitRatio).Take(3))
        {
            lines.Add(t.Ui("monitor.correlation", ("ratio", Math.Round(c.HitRatio * 100)), ("source", c.SourceId)));
        }

        lines.Add(t.Ui("monitor.self", ("cpu", m.Self.AvgCpuPct)));
        return lines;
    }

    private static string SpecsLine(ScanSnapshot s, ReportText t)
    {
        var parts = new List<string>();
        if (s.Os.ProductName.TryGetValue(out var product))
        {
            var build = s.Os.Build.TryGetValue(out var b) ? b : 0;
            if (build >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                product = "Windows 11" + product["Windows 10".Length..];
            }

            var version = s.Os.DisplayVersion.TryGetValue(out var dv) ? " " + dv : string.Empty;
            var buildText = build > 0
                ? string.Create(CultureInfo.InvariantCulture, $" ({build}{(s.Os.Ubr.TryGetValue(out var ubr) ? "." + ubr.ToString(CultureInfo.InvariantCulture) : string.Empty)})")
                : string.Empty;
            parts.Add(product + version + buildText);
        }

        if (s.Hardware.Cpu.TryGetValue(out var cpu))
        {
            parts.Add(cpu.Model.Trim());
        }

        if (s.Hardware.Memory.TryGetValue(out var memory))
        {
            parts.Add(t.Ui("system.ram", ("gb", (int)Math.Round(memory.TotalMb / 1024.0))));
        }

        if (s.Hardware.Gpus.TryGetValue(out var gpus))
        {
            foreach (var gpu in gpus.Where(g => !g.IsBasicDisplayAdapter).OrderBy(g => g.IsIntegrated))
            {
                parts.Add(gpu.DriverVersion is { } dv ? gpu.Name + " (" + t.Ui("system.driver", ("version", dv)) + ")" : gpu.Name);
            }
        }

        if (s.Storage.Disks.TryGetValue(out var disks) && disks.FirstOrDefault(d => d.IsSystemDisk) is { } disk && disk.Model is not null)
        {
            parts.Add(disk.Model + " (" + disk.Bus.ToString().ToUpperInvariant() + ")");
        }

        if (s.Hardware.Board.TryGetValue(out var board))
        {
            var name = string.Join(' ', new[] { board.BoardManufacturer, board.BoardProduct }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (board.BiosVersion is { } bios)
            {
                name = (name + " " + t.Ui("system.bios", ("version", bios))).Trim();
            }

            if (name.Length > 0)
            {
                parts.Add(name);
            }
        }

        return parts.Count == 0 ? t.Ui("value.unknown") : string.Join(" · ", parts);
    }

    private static string ReadinessLine(ScanSnapshot s, ReportText t)
    {
        string Bool(Fact<bool> fact, string yes, string no) => fact.Status switch
        {
            FactStatus.Known => fact.Value ? yes : no,
            FactStatus.NotApplicable => t.Ui("value.notApplicable"),
            _ => t.Ui("value.unknown"),
        };

        var parts = new List<string>();
        var tpm = s.Security.Tpm.Status switch
        {
            FactStatus.Known => s.Security.Tpm.Value.Present && (s.Security.Tpm.Value.SpecVersion?.StartsWith('2') ?? false) ? t.Ui("value.yes") : t.Ui("value.no"),
            FactStatus.NotApplicable => t.Ui("value.no"),
            _ => t.Ui("value.unknown"),
        };
        parts.Add(t.Ui("readiness.tpm") + ": " + tpm);
        parts.Add(t.Ui("readiness.secureboot") + ": " + Bool(s.Security.SecureBootEnabled, t.Ui("value.on"), t.Ui("value.off")));
        parts.Add(t.Ui("readiness.uefi") + ": " + (s.Firmware.Type.TryGetValue(out var fw) ? (fw == FirmwareType.Uefi ? t.Ui("value.yes") : t.Ui("value.legacy")) : t.Ui("value.unknown")));
        parts.Add(t.Ui("readiness.gpt") + ": " + (s.Storage.SystemDiskPartitionStyle.TryGetValue(out var style) ? (style == PartitionStyle.Gpt ? t.Ui("value.yes") : t.Ui("value.mbr")) : t.Ui("value.unknown")));
        parts.Add(t.Ui("readiness.hvci") + ": " + (s.Security.Vbs.TryGetValue(out var vbs) ? (vbs.HvciRunning ? t.Ui("value.on") : t.Ui("value.off")) : t.Ui("value.unknown")));
        parts.Add(t.Ui("readiness.virtualization") + ": " + Bool(s.Security.VirtualizationFirmwareEnabled, t.Ui("value.on"), t.Ui("value.off")));
        var testSigning = s.Security.Bcd.TryGetValue(out var bcd) && bcd.TestSigning is { } ts ? (ts ? t.Ui("value.on") : t.Ui("value.off")) : t.Ui("value.unknown");
        parts.Add(t.Ui("readiness.testsigning") + ": " + testSigning);
        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<string> SystemNotesOf(ScanSnapshot s, ReportText t)
    {
        var notes = new List<string>();
        if (s.Meta.Privilege == PrivilegeMode.Limited)
        {
            notes.Add(t.Ui("system.limited"));
        }

        if (s.Os.IsVirtualMachine.TryGetValue(out var vm) && vm)
        {
            notes.Add(t.Ui("system.vm"));
        }

        return notes;
    }

    private static IReadOnlyList<string> CoverageLines(ScanSnapshot s, ReportText t)
        =>
        [
            .. s.Runs
                .Where(r => r.Status != CollectorStatus.Ok)
                .Select(r => r.CollectorId + ": " + t.Ui("coverage.status." + Camel(r.Status.ToString()))
                    + (r.IssueCodes.Count > 0 ? " (" + string.Join(", ", r.IssueCodes.Take(5)) + (r.IssueCodes.Count > 5 ? ", …" : string.Empty) + ")" : string.Empty)),
        ];

    private static IReadOnlyList<string> PrivacyLines(RedactionManifest manifest, ReportText t)
    {
        var lines = new List<string> { t.Ui("privacy.level") };
        var count = manifest.Entries.Sum(e => e.Count);
        if (count > 0)
        {
            var rules = string.Join(", ", manifest.Entries.Select(e => e.Rule).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            lines.Add(t.Ui("privacy.count", ("count", count), ("rules", rules)));
        }

        if (manifest.ExcludedSections.Count > 0)
        {
            lines.Add(t.Ui("privacy.excluded", ("sections", string.Join(", ", manifest.ExcludedSections))));
        }

        lines.Add(t.Ui("privacy.never"));
        return lines;
    }
}
