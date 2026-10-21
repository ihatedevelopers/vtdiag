using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VTD.Collectors.Collectors;
using VTD.Collectors.Core;
using VTD.Collectors.Pack;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Capture;
using VTD.Platform.Sources;

namespace VTD.Collectors;

/// <summary>Options for <see cref="ScanOrchestrator"/>.</summary>
public sealed record ScanOrchestratorOptions
{
    /// <summary>Privilege override (replays use the captured mode); null = detect from the process token.</summary>
    public PrivilegeMode? Privilege { get; init; }

    /// <summary>Per-install salt for <see cref="SnapshotMeta.MachineKey"/> (A4 persists it and never exports it). Null = a
    /// random per-process salt, so the key is stable within one engine run only.</summary>
    public byte[]? MachineKeySalt { get; init; }

    /// <summary>App version written to the snapshot meta.</summary>
    public string AppVersion { get; init; } = DefaultAppVersion();

    /// <summary>Clock used for collector timeouts and durations (real time even when the scan clock is fake).</summary>
    public TimeProvider TimeoutClock { get; init; } = TimeProvider.System;

    /// <summary>Origin written to the snapshot meta.</summary>
    public SnapshotOrigin Origin { get; init; } = SnapshotOrigin.LiveScan;

    private static string DefaultAppVersion()
    {
        var v = typeof(ScanOrchestrator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }
}

/// <summary>
/// A1's scan orchestrator (<see cref="IScanService"/>): runs every collector in parallel, each under its own hard
/// timeout and with a limited-mode path, merges their patches, fills every section nobody produced with
/// <c>Unknown(reason)</c>, and stamps the meta (boot identity, machine key, privilege). Never throws for data problems;
/// only cancellation by the caller ends a scan early.
/// </summary>
public sealed class ScanOrchestrator : IScanService
{
    /// <summary>Collector ids in snapshot order.</summary>
    public static IReadOnlyList<string> CollectorIds { get; } =
        ["os", "hardware", "firmware", "security", "storage", "devices", "software", "network", "power", "display", "audio", "input", "riot", "events", "pack"];

    /// <summary>Collectors a Quick scan runs (health baseline).</summary>
    public static IReadOnlyList<string> QuickCollectorIds { get; } = ["os", "firmware", "security", "storage", "devices", "software", "pack"];

    private static readonly byte[] ProcessSalt = RandomNumberGenerator.GetBytes(32);

    private readonly SystemSources _sources;
    private readonly TimeProvider _clock;
    private readonly IIdGenerator _ids;
    private readonly ScanOrchestratorOptions _options;
    private readonly IReadOnlyList<CollectorBase> _collectors;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="sources">Sources (live, or a replay).</param>
    /// <param name="clock">Scan clock (start time, "last N days" windows).</param>
    /// <param name="ids">Id generator.</param>
    /// <param name="options">Options.</param>
    /// <param name="collectors">Collector set (default: <see cref="CreateDefaultCollectors"/>).</param>
    public ScanOrchestrator(SystemSources sources, TimeProvider clock, IIdGenerator ids, ScanOrchestratorOptions? options = null, IReadOnlyList<CollectorBase>? collectors = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _options = options ?? new ScanOrchestratorOptions();
        _collectors = collectors ?? CreateDefaultCollectors(sources);
    }

    /// <summary>The built-in collectors.</summary>
    /// <param name="sources">Sources.</param>
    public static IReadOnlyList<CollectorBase> CreateDefaultCollectors(SystemSources sources) =>
    [
        new OsCollector(sources), new HardwareCollector(sources), new FirmwareCollector(sources), new SecurityCollector(sources),
        new StorageCollector(sources), new DevicesCollector(sources), new SoftwareCollector(sources), new NetworkCollector(sources),
        new PowerCollector(sources), new DisplayCollector(sources), new AudioCollector(sources), new InputCollector(sources),
        new RiotCollector(sources), new EventsCollector(sources), new PackCollector(sources),
    ];

    /// <inheritdoc />
    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _clock.GetUtcNow();
        var startTimestamp = _options.TimeoutClock.GetTimestamp();
        var recorder = request.CaptureRaw ? new CaptureRecorder() : null;
        var sources = recorder is null ? _sources : CapturingSources.Wrap(_sources, recorder);
        var envRaw = sources.System.GetEnvironment();
        var privilege = _options.Privilege ?? (envRaw.Elevated ? PrivilegeMode.Elevated : PrivilegeMode.Limited);
        var context = new CollectContext { Request = request, Privilege = privilege, StartedUtc = started, Clock = _clock };
        var env = new ScanEnvironment(sources, context);
        recorder?.RecordMeta(new CaptureMeta
        {
            StartedUtc = started,
            Elevated = privilege == PrivilegeMode.Elevated,
            PlayerSid = request.Player.Sid,
            PlayerProfilePath = request.Player.ProfilePath,
            PlayerLocalAppData = request.Player.LocalAppData,
            PlayerRoamingAppData = request.Player.RoamingAppData,
            PlayerIsAdmin = request.Player.IsAdmin,
            AllowNetworkProbes = request.AllowNetworkProbes,
            EventWindowDays = request.EventWindowDays,
            Mode = request.Mode.ToString(),
        });

        var selected = Selected(request);
        var total = selected.Count;
        var completed = 0;
        var outputs = new Dictionary<string, CollectorOutput>(StringComparer.Ordinal);
        var tasks = selected.Select(async c =>
        {
            progress?.Report(new ScanProgress { CollectorId = c.Id, Area = c.Area, Status = null, Completed = Volatile.Read(ref completed), Total = total });
            var output = await RunOne(env, c, cancellationToken).ConfigureAwait(false);
            var done = Interlocked.Increment(ref completed);
            progress?.Report(new ScanProgress { CollectorId = c.Id, Area = c.Area, Status = output.Run.Status, Completed = done, Total = total });
            return output;
        }).ToList();
        foreach (var output in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            outputs[output.Run.CollectorId] = output;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var runs = _collectors
            .Select(c => outputs.TryGetValue(c.Id, out var o) ? o.Run : new CollectorRun { CollectorId = c.Id, Status = CollectorStatus.NotRequested, ElapsedMs = 0 })
            .ToList();
        Fact<BootIdentity> boot;
        try
        {
            boot = BootIdentityOf(env);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            boot = Fact<BootIdentity>.Unknown(UnknownReason.Failed, "exception:" + e.GetType().Name, "os/registry:PrefetchParameters\\BootId");
        }
        var snapshot = Merge(env, outputs, runs, started, boot);
        var duration = (long)_options.TimeoutClock.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var capture = recorder?.Entries ?? [];
        snapshot = snapshot with
        {
            Meta = snapshot.Meta with { DurationMs = duration, CaptureId = recorder is null ? null : _ids.NewId() },
        };
        return new ScanResult { Snapshot = snapshot, Capture = capture };
    }

    private List<CollectorBase> Selected(ScanRequest request) => request.Mode switch
    {
        ScanMode.Quick => _collectors.Where(c => QuickCollectorIds.Contains(c.Id, StringComparer.Ordinal)).ToList(),
        ScanMode.Targeted => _collectors.Where(c => request.Collectors.Contains(c.Id, StringComparer.Ordinal)).ToList(),
        _ => _collectors.ToList(),
    };

    private async Task<CollectorOutput> RunOne(ScanEnvironment env, CollectorBase collector, CancellationToken cancellationToken)
    {
        var timeout = collector.Timeout > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : collector.Timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var start = _options.TimeoutClock.GetTimestamp();
        var work = collector.CollectAsync(env, cts.Token);
        var delay = Task.Delay(timeout, _options.TimeoutClock, cts.Token);
        var first = await Task.WhenAny(work, delay).ConfigureAwait(false);
        if (first == work)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                return await work.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return TimedOut(collector, start);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await cts.CancelAsync().ConfigureAwait(false);
        _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        return TimedOut(collector, start);
    }

    private CollectorOutput TimedOut(CollectorBase collector, long start) => new()
    {
        Run = new CollectorRun
        {
            CollectorId = collector.Id,
            Status = CollectorStatus.TimedOut,
            ElapsedMs = (long)_options.TimeoutClock.GetElapsedTime(start).TotalMilliseconds,
            IssueCodes = ["timeout:" + collector.Id],
        },
        Patch = new SnapshotPatch(),
    };

    private ScanSnapshot Merge(ScanEnvironment env, Dictionary<string, CollectorOutput> outputs, List<CollectorRun> runs, DateTimeOffset started, Fact<BootIdentity> boot)
    {
        UnknownReason ReasonFor(string owner)
        {
            var run = runs.FirstOrDefault(r => r.CollectorId == owner);
            return run?.Status switch
            {
                CollectorStatus.TimedOut => UnknownReason.TimedOut,
                CollectorStatus.Failed => UnknownReason.Failed,
                CollectorStatus.SkippedNoAdmin => UnknownReason.NeedsAdmin,
                _ => UnknownReason.NotCollected,
            };
        }

        SnapshotPatch? P(string id) => outputs.TryGetValue(id, out var o) ? o.Patch : null;
        var windowStart = started.AddDays(-Math.Clamp(env.Request.EventWindowDays, 1, 30));
        var software = P("software");
        var devices = P("devices");
        var hardware = P("hardware")?.Hardware ?? HardwareInfo.Unknown(ReasonFor("hardware"));
        var storage = P("storage")?.Storage ?? StorageInfo.Unknown(ReasonFor("storage"));
        return new ScanSnapshot
        {
            SchemaVersion = ScanSnapshot.CurrentSchemaVersion,
            Meta = new SnapshotMeta
            {
                SnapshotId = _ids.NewId(),
                AppVersion = _options.AppVersion,
                KbVersion = env.Request.KbVersion,
                CreatedUtc = started,
                DurationMs = 0,
                Mode = env.Request.Mode,
                Privilege = env.Privilege,
                Origin = _options.Origin,
                Boot = boot,
                OsUiCulture = env.Env.UiCulture,
                AppLanguage = env.Request.AppLanguage,
                Redaction = new RedactionState { Level = RedactionLevel.Local, RulesVersion = 1, AppliedRules = ["log.safe_extract"] },
                MachineKey = MachineKey(hardware, storage),
            },
            Runs = runs,
            Os = P("os")?.Os ?? OsInfo.Unknown(ReasonFor("os")),
            Hardware = hardware,
            Firmware = P("firmware")?.Firmware ?? FirmwareInfo.Unknown(ReasonFor("firmware")),
            Security = P("security")?.Security ?? SecurityState.Unknown(ReasonFor("security")),
            Storage = storage,
            Devices = devices?.Devices ?? Fact<IReadOnlyList<DeviceInfo>>.Unknown(ReasonFor("devices")),
            Drivers = devices?.Drivers ?? Fact<IReadOnlyList<LoadedDriver>>.Unknown(ReasonFor("devices")),
            Services = software?.Services ?? Fact<IReadOnlyList<ServiceInfo>>.Unknown(ReasonFor("software")),
            Processes = software?.Processes ?? Fact<IReadOnlyList<ProcessInfo>>.Unknown(ReasonFor("software")),
            Apps = software?.Apps ?? Fact<IReadOnlyList<InstalledApp>>.Unknown(ReasonFor("software")),
            Startup = software?.Startup ?? Fact<IReadOnlyList<StartupItem>>.Unknown(ReasonFor("software")),
            OptionalFeatures = software?.OptionalFeatures ?? Fact<IReadOnlyList<string>>.Unknown(ReasonFor("software")),
            Network = P("network")?.Network ?? NetworkState.Unknown(ReasonFor("network")),
            Power = P("power")?.Power ?? PowerState.Unknown(ReasonFor("power")),
            Display = P("display")?.Display ?? DisplayState.Unknown(ReasonFor("display")),
            Audio = P("audio")?.Audio ?? AudioState.Unknown(ReasonFor("audio")),
            Input = P("input")?.Input ?? InputState.Unknown(ReasonFor("input")),
            Riot = P("riot")?.Riot ?? RiotState.Unknown(ReasonFor("riot")),
            Events = P("events")?.Events ?? EventDigest.Unknown(ReasonFor("events"), windowStart, started),
            Pack = P("pack")?.Pack ?? [],
        };
    }

    private string MachineKey(HardwareInfo hardware, StorageInfo storage)
    {
        var parts = new StringBuilder();
        if (hardware.Board.TryGetValue(out var b))
        {
            parts.Append(b.BoardManufacturer).Append('|').Append(b.BoardProduct).Append('|').Append(b.SystemProduct).Append('|');
        }

        if (hardware.Cpu.TryGetValue(out var c))
        {
            parts.Append(c.Model).Append('|');
        }

        if (storage.Disks.TryGetValue(out var disks) && disks.FirstOrDefault(d => d.IsSystemDisk) is { } sys)
        {
            parts.Append(sys.Model).Append('|').Append(sys.SizeGb.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var mac = HMACSHA256.HashData(_options.MachineKeySalt ?? ProcessSalt, Encoding.UTF8.GetBytes(parts.ToString()));
        return "mk1:" + Convert.ToHexStringLower(mac)[..16];
    }

    private static Fact<BootIdentity> BootIdentityOf(ScanEnvironment env)
    {
        const string source = "os/registry:PrefetchParameters\\BootId";
        var key = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
        if (!key.Ok() || key.Num("BootId") is not { } bootId)
        {
            return Fact<BootIdentity>.Unknown(key.Ok() ? UnknownReason.Unsupported : env.ReasonOf(key.Status), null, source);
        }

        var bootTime = env.StartedUtc.AddMilliseconds(-env.Env.TickCount64);
        bootTime = new DateTimeOffset(bootTime.Ticks - (bootTime.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var kind = BootKind.Unknown;
        var events = env.Sources.Events.Query(
            new EventQuery { Log = "System", Providers = ["Microsoft-Windows-Kernel-Boot"], EventIds = [27], SinceUtc = bootTime.AddMinutes(-10), Max = 1, Fields = ["BootType"] },
            CancellationToken.None);
        if (events.Status == ReadStatus.Ok && events.Records.Count > 0 && Parse.Int(events.Records[0].Data.GetValueOrDefault("BootType")) is { } bootType)
        {
            // 0 = cold boot; 1 = hybrid boot (Fast Startup) and 2 = resume from hibernation: the kernel session was not restarted.
            kind = bootType == 0 ? BootKind.Cold : BootKind.FastStartupResume;
        }

        return Fact<BootIdentity>.Known(new BootIdentity { BootId = (uint)bootId, LastBootUpTimeUtc = bootTime, Kind = kind }, source);
    }
}
