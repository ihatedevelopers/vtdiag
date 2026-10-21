using System.Buffers.Binary;
using System.Globalization;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>
/// Event-log digest by provider and event id (never the rendered, localized message): freezes and sudden shutdowns,
/// TDRs, WHEA, NIC driver errors, disk errors, TPM events, Vanguard service failures, firmware throttling, LiveKernel
/// reports, SideBySide; Riot/Vanguard application crashes and hangs; bug checks; minidump headers (admin).
/// </summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class EventsCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <summary>A built-in query.</summary>
    /// <param name="Id">Query id.</param>
    /// <param name="Log">Log.</param>
    /// <param name="Providers">Providers.</param>
    /// <param name="Ids">Event ids.</param>
    /// <param name="Levels">Levels.</param>
    /// <param name="Fields">Whitelisted EventData fields kept in samples.</param>
    /// <param name="Keep">Optional record filter.</param>
    internal sealed record BuiltinQuery(string Id, string Log, string[] Providers, int[] Ids, int[] Levels, string[] Fields, Func<EventRec, bool>? Keep = null);

    internal static readonly string[] RiotServiceNames = ["vgc", "vgk"];

    internal static IReadOnlyList<BuiltinQuery> Queries { get; } =
    [
        new("kernel_power_41", "System", ["Microsoft-Windows-Kernel-Power"], [41], [], ["BugcheckCode", "PowerButtonTimestamp"]),
        new("unexpected_shutdown_6008", "System", ["EventLog"], [6008], [], []),
        new("display_tdr_4101", "System", ["Display"], [4101], [], []),
        new("whea_errors", "System", ["Microsoft-Windows-WHEA-Logger"], [1, 17, 18, 19, 46, 47], [], []),
        new("realtek_nic_errors", "System", ["rt640x64", "rt25cx21", "rt68cx21", "rtcx21", "rt25cx21x64", "RTL8023", "rtux64w10"], [], [], []),
        new("intel_nic_errors", "System", ["e1dexpress", "e1iexpress", "e2fexpress", "e1rexpress", "e1cexpress", "Netwtw10", "Netwtw14", "Netwtw08", "Netwtw12"], [], [1, 2, 3], []),
        new("disk_errors", "System", ["disk", "Ntfs", "stornvme", "storahci", "iaStorAC", "iaStorVD", "volmgr"], [], [1, 2, 3], []),
        new("tpm_events", "System", ["TPM", "Microsoft-Windows-TPM-WMI"], [14, 15, 17, 20], [], []),
        new("riot_service_failures", "System", ["Service Control Manager"], [7000, 7009, 7023, 7024, 7031, 7034], [], ["param1"], e => e.Data.TryGetValue("param1", out var s) && RiotServiceNames.Contains(s, StringComparer.OrdinalIgnoreCase)),
        new("firmware_throttling_37", "System", ["Microsoft-Windows-Kernel-Processor-Power"], [37], [], []),
        new("live_kernel_event", "Application", ["Windows Error Reporting"], [1001], [], ["#2", "#5"], e => e.Data.TryGetValue("#2", out var n) && n.StartsWith("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)),
        new("sidebyside_errors", "Application", ["SideBySide"], [33, 35, 59, 63, 80], [], []),
        new("codeintegrity_driver_blocks", "Microsoft-Windows-CodeIntegrity/Operational", [], [3033, 3034, 3063, 3077], [], ["FileNameBuffer"], e => e.Data.TryGetValue("FileNameBuffer", out var f) && f.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)),
    ];

    /// <inheritdoc />
    public override string Id => "events";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Logs;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var end = env.StartedUtc;
        var start = end.AddDays(-Math.Clamp(env.Request.EventWindowDays, 1, 30));
        var hits = run.Try("EventLogReader", () => Hits(env, run, start, cancellationToken));
        var digest = new EventDigest
        {
            WindowStartUtc = start,
            WindowEndUtc = end,
            Hits = hits,
            AppCrashes = run.Try("EventLogReader:Application", () => AppCrashes(env, run, start, cancellationToken)),
            BugChecks = run.Try("EventLogReader:System", () => BugChecks(env, run, start, cancellationToken)),
            Minidumps = run.Try("minidump", () => Minidumps(env, run, start)),
        };
        return new SnapshotPatch { Events = digest };
    }

    private static Fact<IReadOnlyList<EventHit>> Hits(ScanEnvironment env, RunBuilder run, DateTimeOffset since, CancellationToken ct)
    {
        const string m = "EventLogReader";
        var list = new List<EventHit>();
        var failures = 0;
        var truncated = 0;
        var tasks = Queries.Select(q => Task.Run(() => (q, env.Sources.Events.Query(new EventQuery { Log = q.Log, Providers = q.Providers, EventIds = q.Ids, Levels = q.Levels, SinceUtc = since, Max = 300, Fields = q.Fields }, ct)), ct)).ToArray();
        Task.WaitAll(tasks, ct);
        foreach (var (q, result) in tasks.Select(t => t.Result))
        {
            if (result.Status != ReadStatus.Ok)
            {
                if (result.Status != ReadStatus.NotFound)
                {
                    failures++;
                    run.Issue((env.ReasonOf(result.Status) == UnknownReason.NeedsAdmin ? "needs_admin" : "failed") + ":" + q.Id);
                }

                continue;
            }

            if (result.Truncated)
            {
                truncated++;
                run.Issue("truncated:" + q.Id);
            }

            var kept = q.Keep is null ? result.Records : result.Records.Where(q.Keep).ToList();
            foreach (var group in kept.GroupBy(r => (r.Provider, r.Id)))
            {
                var records = group.OrderBy(r => r.Utc).ToList();
                list.Add(new EventHit
                {
                    QueryId = q.Id,
                    Log = q.Log,
                    Provider = group.Key.Provider,
                    EventId = group.Key.Id,
                    Count = records.Count,
                    FirstUtc = records[0].Utc,
                    LastUtc = records[^1].Utc,
                    Samples = q.Fields.Length == 0
                        ? []
                        : records.TakeLast(5).Select(r => (IReadOnlyDictionary<string, string>)r.Data.Where(kv => q.Fields.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)).ToList(),
                });
            }
        }

        if (failures == Queries.Count)
        {
            return run.Unknown<IReadOnlyList<EventHit>>(UnknownReason.Failed, m, "all_queries_failed");
        }

        var hits = list.OrderBy(h => h.QueryId, StringComparer.Ordinal).ThenBy(h => h.Provider, StringComparer.Ordinal).ThenBy(h => h.EventId).ToList();

        // A failed query makes "no such event" unknown: partial. A capped query still returned its cap (300) records, more
        // than any threshold, so it only makes counts lower bounds and stays an issue code, not a partial list.
        return failures == 0
            ? run.Known<IReadOnlyList<EventHit>>(hits, m)
            : run.Partial<IReadOnlyList<EventHit>>(hits, m, string.Create(CultureInfo.InvariantCulture, $"failed:{failures};truncated:{truncated}"));
    }

    private static Fact<IReadOnlyList<AppCrash>> AppCrashes(ScanEnvironment env, RunBuilder run, DateTimeOffset since, CancellationToken ct)
    {
        const string m = "EventLogReader:Application";
        var errors = env.Sources.Events.Query(new EventQuery { Log = "Application", Providers = ["Application Error"], EventIds = [1000], SinceUtc = since, Max = 500, Fields = ["AppName", "AppVersion", "ModuleName", "ModuleVersion", "ExceptionCode", "#0", "#1", "#3", "#4", "#6"] }, ct);
        var hangs = env.Sources.Events.Query(new EventQuery { Log = "Application", Providers = ["Application Hang"], EventIds = [1002], SinceUtc = since, Max = 200, Fields = ["AppName", "AppVersion", "#0", "#1"] }, ct);
        var wer = env.Sources.Events.Query(new EventQuery { Log = "Application", Providers = ["Windows Error Reporting"], EventIds = [1001], SinceUtc = since, Max = 500, Fields = ["#2", "#5", "#6", "#8", "#9", "#11"] }, ct);
        if (errors.Status != ReadStatus.Ok && hangs.Status != ReadStatus.Ok && wer.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<AppCrash>>(env, errors.Status, m);
        }

        var list = new List<AppCrash>();
        foreach (var e in errors.Records)
        {
            var exe = Field(e, "AppName", "#0");
            if (exe is null || !SecurityCollector.IsRiotImage(exe))
            {
                continue;
            }

            list.Add(new AppCrash { Exe = exe, ExeVersion = Field(e, "AppVersion", "#1"), Module = Field(e, "ModuleName", "#3"), ModuleVersion = Field(e, "ModuleVersion", "#4"), ExceptionCode = Hex(Field(e, "ExceptionCode", "#6")), Kind = AppCrashKind.Crash, Utc = e.Utc });
        }

        foreach (var e in hangs.Records)
        {
            var exe = Field(e, "AppName", "#0");
            if (exe is not null && SecurityCollector.IsRiotImage(exe))
            {
                list.Add(new AppCrash { Exe = exe, ExeVersion = Field(e, "AppVersion", "#1"), Kind = AppCrashKind.Hang, Utc = e.Utc });
            }
        }

        foreach (var e in wer.Records)
        {
            var exe = Field(e, "#5");
            if (exe is null || !SecurityCollector.IsRiotImage(exe) || !string.Equals(Field(e, "#2"), "APPCRASH", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(new AppCrash { Exe = exe, ExeVersion = Field(e, "#6"), Module = Field(e, "#8"), ModuleVersion = Field(e, "#9"), ExceptionCode = Hex(Field(e, "#11")), Kind = AppCrashKind.WerReport, Utc = e.Utc });
        }

        return run.Known<IReadOnlyList<AppCrash>>(list.OrderBy(c => c.Utc).Take(200).ToList(), m);
    }

    private static Fact<IReadOnlyList<BugCheck>> BugChecks(ScanEnvironment env, RunBuilder run, DateTimeOffset since, CancellationToken ct)
    {
        const string m = "EventLogReader:System";
        var r = env.Sources.Events.Query(new EventQuery { Log = "System", Providers = ["Microsoft-Windows-WER-SystemErrorReporting"], EventIds = [1001], SinceUtc = since, Max = 100, Fields = ["param1", "param2"] }, ct);
        if (r.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<BugCheck>>(env, r.Status, m);
        }

        var list = new List<BugCheck>();
        foreach (var e in r.Records)
        {
            var p1 = Field(e, "param1");
            if (p1 is null)
            {
                continue;
            }

            var code = p1.Split(' ', 2)[0];
            var open = p1.IndexOf('(', StringComparison.Ordinal);
            var comma = p1.IndexOf(',', StringComparison.Ordinal);
            var param = open >= 0 && comma > open ? p1[(open + 1)..comma].Trim() : null;
            var dump = Field(e, "param2");
            list.Add(new BugCheck { Code = code, Utc = e.Utc, Parameter1 = param, DumpFile = dump is null ? null : PathText.FileName(dump) });
        }

        return run.Known<IReadOnlyList<BugCheck>>(list.OrderBy(b => b.Utc).ToList(), m);
    }

    private static Fact<IReadOnlyList<MinidumpMeta>> Minidumps(ScanEnvironment env, RunBuilder run, DateTimeOffset since)
    {
        const string m = "minidump";
        var dir = Path.Combine(env.WindowsDir, "Minidump");
        var listing = env.Sources.Files.List(dir, "*.dmp", recursive: false, max: 200);
        if (listing.Status == ReadStatus.NotFound)
        {
            return run.Known<IReadOnlyList<MinidumpMeta>>([], m);
        }

        if (listing.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<MinidumpMeta>>(env, listing.Status, m);
        }

        var list = new List<MinidumpMeta>();
        foreach (var f in listing.Entries.Where(e => !e.IsDirectory).OrderByDescending(e => e.ModifiedUtc).Take(20))
        {
            var header = Parse.Hex(env.Sources.Files.ReadHeader(Path.Combine(dir, f.Name), 0x48).Hex);
            string? code = null;
            if (header is { Length: >= 0x3C } && header.AsSpan(0, 8).SequenceEqual("PAGEDU64"u8))
            {
                code = "0x" + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38)).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            }

            list.Add(new MinidumpMeta { FileName = f.Name, ModifiedUtc = f.ModifiedUtc, SizeBytes = f.Size, BugCheckCode = code });
        }

        _ = since;
        return run.Known<IReadOnlyList<MinidumpMeta>>(list, m);
    }

    private static string? Field(EventRec e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.Data.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }

    private static string? Hex(string? code)
        => code is null ? null : code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code.ToLowerInvariant() : "0x" + code.ToLowerInvariant();
}
