using System.Text.RegularExpressions;
using VTD.Collectors.Core;
using VTD.Collectors.Logs;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>
/// Riot Client, VALORANT and Vanguard: installs, services, files, vgc log metadata (never content), compat layers,
/// whitelisted game settings, ShooterGame.log / Riot Client log / crash-context safe extractors, detected codes, and
/// whether the game or client runs (process NAME only; no handle is ever opened to a Riot or Vanguard process).
/// </summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed partial class RiotCollector(SystemSources sources) : CollectorBase(sources)
{
    private const long MaxLogBytes = 48L * 1024 * 1024;
    private const long MaxTotalShooterBytes = 160L * 1024 * 1024;
    private const int MaxBackupsParsed = 5;
    private const int MaxClientLogsParsed = 12;

    /// <inheritdoc />
    public override string Id => "riot";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Game;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var loc = RiotLocator.Locate(env);
        var codes = new List<DetectedCode>();
        var shooter = run.Try("ShooterGame.log", () => ShooterLog(env, run, codes, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        var client = run.Try("RiotClientLogs", () => ClientLogs(env, run, codes, cancellationToken));
        var crashes = run.Try("Crashes", () => Crashes(env, run, codes));
        var processes = env.Processes;
        var state = new RiotState
        {
            Client = run.Try("RiotClientInstalls.json", () => ClientInstall(env, run, loc)),
            Valorant = run.Try("product_settings", () => ValorantInstall(env, run, loc, shooter)),
            Vanguard = run.Try("vanguard", () => Vanguard(env, run, loc)),
            CompatLayers = run.Try("registry:AppCompatFlags\\Layers", () => CompatLayers(env, run)),
            GameConfig = run.Try("GameUserSettings.ini", () => GameConfig(env, run)),
            ShooterLog = shooter,
            ClientLogs = client,
            Crashes = crashes,
            DetectedCodes = shooter.IsKnown || client.IsKnown || crashes.IsKnown
                ? run.Known<IReadOnlyList<DetectedCode>>(MergeCodes(codes), "logs")
                : run.Unknown<IReadOnlyList<DetectedCode>>(UnknownReason.NotCollected, "logs", "no_log_readable"),
            GameRunning = processes.Status == ReadStatus.Ok
                ? run.Known(processes.Items.Any(p => string.Equals(p.Name, RiotProcessNames.Game, StringComparison.OrdinalIgnoreCase)), "toolhelp")
                : run.FromStatus<bool>(env, processes.Status, "toolhelp"),
            ClientRunning = processes.Status == ReadStatus.Ok
                ? run.Known(processes.Items.Any(p => IsClientProcess(p.Name)), "toolhelp")
                : run.FromStatus<bool>(env, processes.Status, "toolhelp"),
        };
        return new SnapshotPatch { Riot = state };
    }

    internal static bool IsClientProcess(string name)
        => name.Equals(RiotProcessNames.RiotClientServices, StringComparison.OrdinalIgnoreCase)
           || name.Equals("RiotClientUx.exe", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Riot Client.exe", StringComparison.OrdinalIgnoreCase);

    private static Fact<RiotInstall> ClientInstall(ScanEnvironment env, RunBuilder run, RiotLocation loc)
    {
        const string m = "RiotClientInstalls.json";
        if (loc.ClientExe is null)
        {
            return loc.InstallsStatus is ReadStatus.Ok or ReadStatus.NotFound ? run.Known(new RiotInstall { Installed = false }, m) : run.FromStatus<RiotInstall>(env, loc.InstallsStatus, m);
        }

        var stat = env.Sources.Files.Stat(loc.ClientExe);
        var version = stat.Status == ReadStatus.Ok ? env.Sources.Files.GetVersion(loc.ClientExe) : null;
        return run.Known(Install(env, stat.Status == ReadStatus.Ok, loc.ClientDir, version?.ProductVersion ?? version?.FileVersion), m);
    }

    private static Fact<RiotInstall> ValorantInstall(ScanEnvironment env, RunBuilder run, RiotLocation loc, Fact<ShooterLogDigest> shooter)
    {
        const string m = "product_settings";
        if (loc.ValorantDir is null)
        {
            return loc.MetadataConclusive ? run.Known(new RiotInstall { Installed = false }, m) : run.FromStatus<RiotInstall>(env, loc.SettingsStatus is ReadStatus.Ok or ReadStatus.NotFound ? loc.InstallsStatus : loc.SettingsStatus, m);
        }

        var exe = Path.Combine(loc.ValorantDir, "ShooterGame", "Binaries", "Win64", RiotProcessNames.Game);
        var installed = env.Sources.Files.Stat(exe).Status == ReadStatus.Ok || env.Sources.Files.Stat(loc.ValorantDir).Status == ReadStatus.Ok;
        var version = shooter.TryGetValue(out var log) ? log.GameVersion : null;
        return run.Known(Install(env, installed, loc.ValorantDir, version), m);
    }

    private static RiotInstall Install(ScanEnvironment env, bool installed, string? dir, string? version)
    {
        var drive = PathText.Drive(dir);
        return new RiotInstall
        {
            Installed = installed,
            Path = dir,
            Version = version,
            Drive = drive,
            OnSystemDrive = drive is null ? null : string.Equals(drive, env.SystemDrive, StringComparison.OrdinalIgnoreCase),
            PathHasNonAscii = dir is null ? null : PathText.HasNonAscii(dir),
        };
    }

    private static Fact<VanguardInfo> Vanguard(ScanEnvironment env, RunBuilder run, RiotLocation loc)
    {
        const string m = "vanguard";
        var vgc = ServiceReader.Read(env, "vgc");
        var vgk = ServiceReader.Read(env, "vgk");
        var dir = env.Sources.Files.Stat(loc.VanguardDir);
        var files = new List<RiotFileInfo>
        {
            FileInfoOf(env, loc.VanguardDir, "vgc.exe", "vanguard", version: true),
            FileInfoOf(env, loc.VanguardDir, "vgk.sys", "vanguard", version: true),
            FileInfoOf(env, loc.VanguardDir, "vgkbootstatus.dat", "vanguard", version: false),
            FileInfoOf(env, Path.Combine(env.WindowsDir, "System32"), "392667600.dat", "system32", version: false),
            FileInfoOf(env, env.WindowsDir, "392667600.dat", "windows", version: false),
        };
        var installed = vgc is not null || vgk is not null || dir.Status == ReadStatus.Ok;
        VgcLogDigest? logs = null;
        var listing = env.Sources.Files.List(Path.Combine(loc.VanguardDir, "Logs"), "vgc_*.log", recursive: false, max: 2000);
        if (listing.Status == ReadStatus.Ok)
        {
            var start = env.StartedUtc;
            var entries = listing.Entries.Where(e => !e.IsDirectory).OrderByDescending(e => e.ModifiedUtc).ToList();
            logs = new VgcLogDigest
            {
                FileCount = entries.Count,
                NewestUtc = entries.Count > 0 ? entries[0].ModifiedUtc : null,
                OldestUtc = entries.Count > 0 ? entries[^1].ModifiedUtc : null,
                CountLastHour = entries.Count(e => e.ModifiedUtc > start.AddHours(-1) && e.ModifiedUtc <= start.AddMinutes(5)),
                CountLast24Hours = entries.Count(e => e.ModifiedUtc > start.AddHours(-24) && e.ModifiedUtc <= start.AddMinutes(5)),
                TotalBytes = entries.Sum(e => e.Size),
                Recent = entries.Take(20).Select(e => new LogFileMeta { Name = PathText.FileName(e.Name), SizeBytes = e.Size, ModifiedUtc = e.ModifiedUtc }).ToList(),
            };
        }
        else if (listing.Status != ReadStatus.NotFound)
        {
            run.Issue((env.ReasonOf(listing.Status) == UnknownReason.NeedsAdmin ? "needs_admin" : "failed") + ":vgc_logs");
        }

        var vgcFile = files[0];
        return run.Known(
            new VanguardInfo
            {
                Installed = installed,
                Vgc = vgc,
                Vgk = vgk,
                InstallPath = dir.Status == ReadStatus.Ok ? loc.VanguardDir : null,
                Version = vgcFile.Version,
                Files = files,
                Logs = logs,
            },
            m);
    }

    private static RiotFileInfo FileInfoOf(ScanEnvironment env, string dir, string name, string location, bool version)
    {
        var path = Path.Combine(dir, name);
        var stat = env.Sources.Files.Stat(path);
        if (stat.Status != ReadStatus.Ok || stat.IsDirectory)
        {
            return new RiotFileInfo { Name = name, Location = location, Exists = false };
        }

        var v = version ? env.Sources.Files.GetVersion(path) : null;
        return new RiotFileInfo { Name = name, Location = location, Exists = true, SizeBytes = stat.Size, ModifiedUtc = stat.ModifiedUtc, Version = v?.FileVersion ?? v?.ProductVersion };
    }

    private static Fact<IReadOnlyList<CompatLayerEntry>> CompatLayers(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:AppCompatFlags\\Layers";
        const string path = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        var user = env.Hkcu(path);
        var machine = env.Hklm(path);
        if ((!user.Ok() && user.Status != ReadStatus.NotFound) || (!machine.Ok() && machine.Status != ReadStatus.NotFound))
        {
            return run.FromStatus<IReadOnlyList<CompatLayerEntry>>(env, !user.Ok() && user.Status != ReadStatus.NotFound ? user.Status : machine.Status, m);
        }

        var list = new List<CompatLayerEntry>();
        foreach (var (key, hive) in new[] { (user, AppHive.Hkcu), (machine, AppHive.Hklm) })
        {
            foreach (var v in key.Values)
            {
                if (!IsRiotPath(v.Name))
                {
                    continue;
                }

                list.Add(new CompatLayerEntry
                {
                    ImageName = PathText.FileName(v.Name),
                    ExePath = v.Name,
                    Hive = hive,
                    Layers = (v.Data ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(16).ToList(),
                });
            }
        }

        return run.Known<IReadOnlyList<CompatLayerEntry>>(list, m);
    }

    internal static bool IsRiotPath(string path)
        => RiotProcessNames.IsProtected(PathText.FileName(path))
           || path.Contains(@"\Riot Games\", StringComparison.OrdinalIgnoreCase)
           || path.Contains(@"\Riot Vanguard\", StringComparison.OrdinalIgnoreCase);

    private static Fact<GameConfigDigest> GameConfig(ScanEnvironment env, RunBuilder run)
    {
        const string m = "GameUserSettings.ini";
        var configDir = Path.Combine(env.Player.LocalAppData, "VALORANT", "Saved", "Config");
        var dirs = env.Sources.Files.List(configDir, "*", recursive: false, max: 500);
        if (dirs.Status == ReadStatus.NotFound)
        {
            return run.NotApplicable<GameConfigDigest>(m, "no_config_folder");
        }

        if (dirs.Status != ReadStatus.Ok)
        {
            return run.FromStatus<GameConfigDigest>(env, dirs.Status, m);
        }

        var accounts = dirs.Entries.Count(e => e.IsDirectory && AccountFolder().IsMatch(e.Name));
        var files = env.Sources.Files.List(configDir, "GameUserSettings.ini", recursive: true, max: 500);
        var newest = files.Entries.Where(e => !e.IsDirectory).OrderByDescending(e => e.ModifiedUtc).FirstOrDefault();
        if (newest is null)
        {
            return run.Known(new GameConfigDigest { AccountConfigCount = accounts }, m);
        }

        var read = env.Sources.Files.ReadLines(Path.Combine(configDir, newest.Name), GameConfigExtractor.Filter, 1 << 20, 200);
        if (read.Status != ReadStatus.Ok)
        {
            return run.FromStatus<GameConfigDigest>(env, read.Status, m);
        }

        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sections = 0;
        var corrupt = newest.Size == 0;
        foreach (var line in read.Lines)
        {
            var parts = line.Split('|');
            switch (parts[0])
            {
                case "section":
                    sections++;
                    break;
                case "corrupt":
                    corrupt = true;
                    break;
                case "kv" when parts.Length == 3:
                    settings[parts[1]] = parts[2];
                    break;
            }
        }

        if (read.Status == ReadStatus.Ok && sections == 0)
        {
            corrupt = true;
        }

        return run.Known(new GameConfigDigest { AccountConfigCount = accounts, Settings = settings, CorruptMarkers = corrupt }, m);
    }

    private static Fact<ShooterLogDigest> ShooterLog(ScanEnvironment env, RunBuilder run, List<DetectedCode> codes, CancellationToken ct)
    {
        const string m = "ShooterGame.log";
        var logDir = Path.Combine(env.Player.LocalAppData, "VALORANT", "Saved", "Logs");
        var listing = env.Sources.Files.List(logDir, "ShooterGame*.log", recursive: false, max: 200);
        if (listing.Status == ReadStatus.NotFound)
        {
            return run.NotApplicable<ShooterLogDigest>(m, "no_log_folder");
        }

        if (listing.Status != ReadStatus.Ok)
        {
            return run.FromStatus<ShooterLogDigest>(env, listing.Status, m);
        }

        var current = listing.Entries.FirstOrDefault(e => string.Equals(e.Name, "ShooterGame.log", StringComparison.OrdinalIgnoreCase));
        var backups = listing.Entries.Where(e => e.Name.StartsWith("ShooterGame-backup-", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.ModifiedUtc).ToList();
        var since = env.StartedUtc.AddDays(-Math.Clamp(env.Request.EventWindowDays, 1, 30));
        var toRead = new List<(DirEntry Entry, bool Current)>();
        if (current is not null)
        {
            toRead.Add((current, true));
        }

        toRead.AddRange(backups.Where(b => b.ModifiedUtc >= since).Take(MaxBackupsParsed).Select(b => (b, false)));
        var records = new List<(LogRecord, bool)>();
        var unreadable = 0;
        var lastFailure = ReadStatus.Failed;
        long budget = MaxTotalShooterBytes;
        foreach (var (entry, isCurrent) in toRead)
        {
            ct.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                run.Issue("truncated:shooter_backups");
                break;
            }

            var limit = Math.Min(MaxLogBytes, budget);
            var read = env.Sources.Files.ReadLines(Path.Combine(logDir, entry.Name), ShooterLogExtractor.Filter, limit, 20_000);
            budget -= Math.Min(entry.Size, limit);
            if (read.Status != ReadStatus.Ok)
            {
                unreadable++;
                lastFailure = read.Status;
                run.Issue("unreadable:" + (isCurrent ? "ShooterGame.log" : "ShooterGame-backup"));
                continue;
            }

            if (read.Truncated)
            {
                run.Issue("truncated:" + (isCurrent ? "ShooterGame.log" : "ShooterGame-backup"));
            }

            records.AddRange(read.Lines.Select(LogRecord.Decode).Where(r => r is not null).Select(r => (r!, isCurrent)));
        }

        if (toRead.Count > 0 && unreadable == toRead.Count)
        {
            return run.FromStatus<ShooterLogDigest>(env, lastFailure, m);
        }

        return run.Known(ShooterLogExtractor.Build(records, current?.ModifiedUtc, backups.Count, codes), m);
    }

    private static Fact<RiotClientLogDigest> ClientLogs(ScanEnvironment env, RunBuilder run, List<DetectedCode> codes, CancellationToken ct)
    {
        const string m = "RiotClientLogs";
        var dir = Path.Combine(env.Player.LocalAppData, "Riot Games", "Riot Client", "Logs");
        var listing = env.Sources.Files.List(dir, "*.log", recursive: true, max: 5000);
        if (listing.Status == ReadStatus.NotFound)
        {
            return run.NotApplicable<RiotClientLogDigest>(m, "no_log_folder");
        }

        if (listing.Status != ReadStatus.Ok)
        {
            return run.FromStatus<RiotClientLogDigest>(env, listing.Status, m);
        }

        var files = listing.Entries.Where(e => !e.IsDirectory).OrderByDescending(e => e.ModifiedUtc).ToList();
        var since = env.StartedUtc.AddDays(-Math.Clamp(env.Request.EventWindowDays, 1, 30));
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var codeHits = new Dictionary<string, (int Count, DateTimeOffset First, DateTimeOffset Last)>(StringComparer.Ordinal);
        var parsed = files.Where(f => f.ModifiedUtc >= since && f.Size > 0).Take(MaxClientLogsParsed).ToList();
        var unreadable = 0;
        var lastFailure = ReadStatus.Failed;
        foreach (var f in parsed)
        {
            ct.ThrowIfCancellationRequested();
            var read = env.Sources.Files.ReadLines(Path.Combine(dir, f.Name), RiotClientLogExtractor.Filter, 8L * 1024 * 1024, 5000);
            if (read.Status != ReadStatus.Ok)
            {
                unreadable++;
                lastFailure = read.Status;
                run.Issue("unreadable:riot_client_log");
                continue;
            }

            foreach (var r in read.Lines.Select(LogRecord.Decode).Where(r => r is not null))
            {
                counts[r!.PatternId] = counts.GetValueOrDefault(r.PatternId) + 1;
                foreach (var code in r.Groups.Where(g => g.StartsWith("VA", StringComparison.Ordinal)))
                {
                    var prev = codeHits.TryGetValue(code, out var p) ? p : (Count: 0, First: f.ModifiedUtc, Last: f.ModifiedUtc);
                    codeHits[code] = (prev.Count + 1, prev.First < f.ModifiedUtc ? prev.First : f.ModifiedUtc, prev.Last > f.ModifiedUtc ? prev.Last : f.ModifiedUtc);
                }
            }
        }

        if (parsed.Count > 0 && unreadable == parsed.Count)
        {
            return run.FromStatus<RiotClientLogDigest>(env, lastFailure, m);
        }

        foreach (var (code, hit) in codeHits)
        {
            codes.Add(new DetectedCode { Code = code, Source = CodeSource.RiotClientLog, Count = hit.Count, FirstUtc = hit.First, LastUtc = hit.Last });
        }

        return run.Known(new RiotClientLogDigest { FileCount = files.Count, NewestUtc = files.Count > 0 ? files[0].ModifiedUtc : null, PatternCounts = counts }, m);
    }

    private static Fact<IReadOnlyList<CrashReportMeta>> Crashes(ScanEnvironment env, RunBuilder run, List<DetectedCode> codes)
    {
        const string m = "Crashes";
        var dir = Path.Combine(env.Player.LocalAppData, "VALORANT", "Saved", "Crashes");
        var listing = env.Sources.Files.List(dir, "*", recursive: false, max: 500);
        if (listing.Status == ReadStatus.NotFound)
        {
            return run.Known<IReadOnlyList<CrashReportMeta>>([], m);
        }

        if (listing.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<CrashReportMeta>>(env, listing.Status, m);
        }

        var list = new List<CrashReportMeta>();
        foreach (var d in listing.Entries
                     .Where(e => e.IsDirectory && (e.Name.StartsWith("UECC-", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("UE4CC-", StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(e => e.CreatedUtc)
                     .Take(20))
        {
            var read = env.Sources.Files.ReadLines(Path.Combine(dir, d.Name, "CrashContext.runtime-xml"), CrashContextExtractor.Filter, 4L * 1024 * 1024, 16);
            string? type = null, error = null, drive = null;
            foreach (var line in read.Lines)
            {
                var parts = line.Split('|');
                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "type":
                        type ??= parts[1];
                        break;
                    case "error":
                        error ??= parts[1];
                        break;
                    case "drive" when parts[1].Length > 0:
                        drive ??= parts[1];
                        break;
                }
            }

            list.Add(new CrashReportMeta { FolderName = d.Name, CreatedUtc = d.CreatedUtc, CrashType = type, ErrorPattern = error, InstallDrive = drive });
        }

        _ = codes;
        return run.Known<IReadOnlyList<CrashReportMeta>>(list, m);
    }

    private static IReadOnlyList<DetectedCode> MergeCodes(List<DetectedCode> codes)
        => codes
            .GroupBy(c => (c.Code, c.Source))
            .Select(g => new DetectedCode
            {
                Code = g.Key.Code,
                Source = g.Key.Source,
                Count = g.Sum(x => x.Count),
                FirstUtc = g.Min(x => x.FirstUtc),
                LastUtc = g.Max(x => x.LastUtc),
            })
            .OrderBy(c => c.Code, StringComparer.Ordinal)
            .ThenBy(c => c.Source)
            .ToList();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[a-z]{2,8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AccountFolder();
}
