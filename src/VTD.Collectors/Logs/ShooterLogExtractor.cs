using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Logs;

/// <summary>
/// ShooterGame.log safe extractor (critique section 3.7): LogInit hardware line, CI server version,
/// <c>LogPlatformInitializerV2 ... Failed to initialize</c>, D3D device lost, fatal errors, LogNet failures, RSO network
/// errors, Basic Render Driver, Riot error codes. Stage 1 (<see cref="Filter"/>) runs inside the file source and emits
/// only <see cref="LogRecord"/> lines; stage 2 (<see cref="Build"/>) folds records into the digest.
/// </summary>
public static partial class ShooterLogExtractor
{
    /// <summary>Allowlisted pattern ids.</summary>
    public static IReadOnlyList<string> PatternIds { get; } =
    [
        "shooter.loginit", "shooter.version", "shooter.platform_init_failed", "shooter.d3d_device_lost", "shooter.fatal_error",
        "shooter.net_failure", "shooter.rnet_not_connected", "shooter.rso_error", "shooter.basic_render_driver",
        "shooter.out_of_video_memory", "shooter.code",
    ];

    /// <summary>The stage-1 filter (id is part of the capture key).</summary>
    public static LineFilter Filter { get; } = new("shooter.v1", Map);

    /// <summary>Stage 1: one raw line to zero or one encoded record.</summary>
    /// <param name="line">Raw line.</param>
    public static string? Map(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length > 4096 || !QuickHit(line) || LogSafety.IsSensitive(line))
        {
            return null;
        }

        var utc = Timestamp(line);
        Match m;
        if ((m = LogInit().Match(line)).Success)
        {
            return new LogRecord("shooter.loginit", utc, [LogSafety.Scrub(m.Groups[1].Value, 96, keepDottedNumbers: true), LogSafety.Scrub(m.Groups[2].Value, 96), LogSafety.Scrub(m.Groups[3].Value, 96), m.Groups[4].Success ? LogSafety.Scrub(m.Groups[4].Value, 8) : string.Empty]).Encode();
        }

        if ((m = Version().Match(line)).Success)
        {
            return new LogRecord("shooter.version", utc, [m.Groups[1].Value]).Encode();
        }

        if ((m = PlatformInit().Match(line)).Success)
        {
            return new LogRecord("shooter.platform_init_failed", utc, [m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : string.Empty]).Encode();
        }

        if (line.Contains("device being lost", StringComparison.OrdinalIgnoreCase) || line.Contains("0x887A0005", StringComparison.OrdinalIgnoreCase) || line.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase))
        {
            return new LogRecord("shooter.d3d_device_lost", utc, line.Contains("Fatal error", StringComparison.Ordinal) ? ["fatal"] : []).Encode();
        }

        if (line.Contains("Out of video memory", StringComparison.OrdinalIgnoreCase))
        {
            return new LogRecord("shooter.out_of_video_memory", utc, []).Encode();
        }

        if (line.Contains("Fatal error", StringComparison.Ordinal))
        {
            return new LogRecord("shooter.fatal_error", utc, []).Encode();
        }

        if (NetFailure().IsMatch(line))
        {
            return new LogRecord("shooter.net_failure", utc, []).Encode();
        }

        if (line.Contains("RNET_NOT_CONNECTED", StringComparison.Ordinal))
        {
            return new LogRecord("shooter.rnet_not_connected", utc, []).Encode();
        }

        if (line.Contains("LogRSOManager: Error", StringComparison.Ordinal))
        {
            return new LogRecord("shooter.rso_error", utc, []).Encode();
        }

        if (line.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase) && (line.Contains("Chosen", StringComparison.OrdinalIgnoreCase) || line.Contains("Selected", StringComparison.OrdinalIgnoreCase) || line.Contains("Using", StringComparison.OrdinalIgnoreCase)))
        {
            return new LogRecord("shooter.basic_render_driver", utc, []).Encode();
        }

        var codes = CodeNormalizer.Find(line).Distinct(StringComparer.Ordinal).ToList();
        return codes.Count > 0 ? new LogRecord("shooter.code", utc, codes).Encode() : null;
    }

    /// <summary>Stage 2: folds records of the current log and its backups into the digest.</summary>
    /// <param name="records">Records of every file read.</param>
    /// <param name="currentLastWrite">Last write of the current log.</param>
    /// <param name="backupCount">Rotated backups present.</param>
    /// <param name="codes">Detected codes sink.</param>
    public static ShooterLogDigest Build(IEnumerable<(LogRecord Record, bool Current)> records, DateTimeOffset? currentLastWrite, int backupCount, List<DetectedCode> codes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(codes);
        LogInitHardware? hardware = null;
        string? version = null;
        var failures = new Dictionary<(string, string), int>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int d3d = 0, fatal = 0, net = 0;
        var codeHits = new Dictionary<string, (int Count, DateTimeOffset? First, DateTimeOffset? Last)>(StringComparer.Ordinal);
        foreach (var (r, current) in records)
        {
            counts[r.PatternId] = counts.GetValueOrDefault(r.PatternId) + 1;
            switch (r.PatternId)
            {
                case "shooter.loginit" when current || hardware is null:
                    if (r.Groups.Count >= 4)
                    {
                        hardware = new LogInitHardware { Os = Null(r.Groups[0]), Cpu = Null(r.Groups[1]), Gpu = Null(r.Groups[2]), Disk = Null(r.Groups[3]) };
                    }

                    break;
                case "shooter.version" when current || version is null:
                    version = r.Groups.FirstOrDefault();
                    break;
                case "shooter.platform_init_failed" when r.Groups.Count >= 2:
                    var key = (r.Groups[0], r.Groups[1]);
                    failures[key] = failures.GetValueOrDefault(key) + 1;
                    break;
                case "shooter.d3d_device_lost":
                    d3d++;
                    fatal += r.Groups.Contains("fatal", StringComparer.Ordinal) ? 1 : 0;
                    break;
                case "shooter.fatal_error":
                    fatal++;
                    break;
                case "shooter.net_failure":
                    net++;
                    break;
                case "shooter.code":
                    foreach (var code in r.Groups)
                    {
                        var prev = codeHits.GetValueOrDefault(code);
                        codeHits[code] = (prev.Count + 1, Min(prev.First, r.Utc), Max(prev.Last, r.Utc));
                    }

                    break;
            }
        }

        foreach (var (code, hit) in codeHits)
        {
            codes.Add(new DetectedCode { Code = code, Source = CodeSource.ShooterLog, Count = hit.Count, FirstUtc = hit.First, LastUtc = hit.Last });
        }

        return new ShooterLogDigest
        {
            LastWriteUtc = currentLastWrite,
            BackupLogCount = backupCount,
            GameVersion = version,
            Hardware = hardware,
            PlatformInitFailures = failures
                .OrderByDescending(f => f.Value).ThenBy(f => f.Key.Item1, StringComparer.Ordinal)
                .Take(20)
                .Select(f => new PlatformInitFailure { Manager = f.Key.Item1, Error = f.Key.Item2.Length == 0 ? null : f.Key.Item2, Count = f.Value })
                .ToList(),
            D3dDeviceLostCount = d3d,
            FatalErrorCount = fatal,
            NetDisconnectCount = net,
            PatternCounts = counts,
        };
    }

    internal static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    internal static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static string? Null(string s) => s.Length == 0 ? null : s;

    private static bool QuickHit(string line)
        => line.Contains("LogInit: OS:", StringComparison.Ordinal)
           || line.Contains("CI server version:", StringComparison.Ordinal)
           || line.Contains("Failed to initialize:", StringComparison.Ordinal)
           || line.Contains("device being lost", StringComparison.OrdinalIgnoreCase)
           || line.Contains("0x887A0005", StringComparison.OrdinalIgnoreCase)
           || line.Contains("DXGI_ERROR_DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Out of video memory", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Fatal error", StringComparison.Ordinal)
           || line.Contains("LogNet:", StringComparison.Ordinal)
           || line.Contains("RNET_NOT_CONNECTED", StringComparison.Ordinal)
           || line.Contains("LogRSOManager: Error", StringComparison.Ordinal)
           || line.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase)
           || line.Contains("VAN", StringComparison.Ordinal)
           || line.Contains("VAL", StringComparison.Ordinal);

    private static DateTimeOffset? Timestamp(string line)
    {
        var m = Stamp().Match(line);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero)
            : null;
    }

    [GeneratedRegex(@"^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}):\d{3}\]", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    [GeneratedRegex(@"LogInit: OS: (.{1,120}?), CPU: (.{1,120}?), GPU: (.{1,120}?)(?:, Disk: (\w{1,8}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LogInit();

    [GeneratedRegex(@"CI server version: (release-[0-9A-Za-z.\-]{1,64})", RegexOptions.CultureInvariant)]
    private static partial Regex Version();

    [GeneratedRegex(@"LogPlatformInitializerV2: (?:Error: )?Failed to initialize: ([A-Za-z0-9_]{1,64})(?: \(error: ([A-Za-z0-9_]{1,64})\))?", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformInit();

    [GeneratedRegex(@"LogNet: .{0,200}(NetworkFailure|ConnectionLost|ConnectionTimeout|PendingConnectionFailure|UNetConnection::Close)", RegexOptions.CultureInvariant)]
    private static partial Regex NetFailure();
}
