using VTD.Contracts.Analysis;

namespace VTD.Contracts.Monitoring;

/// <summary>Why a monitoring run happens.</summary>
public enum MonitorPurpose
{
    /// <summary>"Monitor my next match".</summary>
    UserRequested = 0,

    /// <summary>Outcome check after a fix (compared with the baseline).</summary>
    OutcomeCheck = 1,

    /// <summary>The first watched match (baseline).</summary>
    Baseline = 2,
}

/// <summary>A monitor data source; each can be switched off (the acceptance test enables them one by one).</summary>
public enum MonitorSource
{
    /// <summary>PresentMon frames (ETW; never a handle to the game).</summary>
    Frames = 0,

    /// <summary>PDH counters (CPU utility, % of max frequency, GPU engine, VRAM).</summary>
    Pdh = 1,

    /// <summary>Event-log watchers (NIC resets, TDR, WHEA, Kernel-Power, App 1000).</summary>
    Events = 2,

    /// <summary>Game-server endpoint (Kernel-Network ETW, sampled) and ICMP RTT/jitter/loss.</summary>
    Network = 3,

    /// <summary>Process starts during the match (names only).</summary>
    ProcessStarts = 4,
}

/// <summary>Options for a monitoring run.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MonitorOptions
{
    /// <summary>Session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Purpose.</summary>
    public MonitorPurpose Purpose { get; init; } = MonitorPurpose.UserRequested;

    /// <summary>Enabled sources.</summary>
    public IReadOnlyList<MonitorSource> Sources { get; init; } =
        [MonitorSource.Frames, MonitorSource.Pdh, MonitorSource.Events, MonitorSource.Network, MonitorSource.ProcessStarts];

    /// <summary>Stop after this many minutes.</summary>
    public int MaxMinutes { get; init; } = 60;

    /// <summary>Process name to watch.</summary>
    public string TargetProcessName { get; init; } = RiotProcessNames.Game;

    /// <summary>CPU budget target (% of the machine); a hard auto-stop sits above it (critique section 2 row 23).</summary>
    public double CpuBudgetPercent { get; init; } = 2.0;
}

/// <summary>1 Hz aggregate for the UI/tray. Null values = source inactive or no data.</summary>
public sealed record MonitorTick
{
    /// <summary>Sequence.</summary>
    public required long Seq { get; init; }

    /// <summary>When.</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>The game is running.</summary>
    public required bool GameRunning { get; init; }

    /// <summary>FPS over the last second.</summary>
    public double? Fps { get; init; }

    /// <summary>99th percentile frame time (ms).</summary>
    public double? FrameTimeP99Ms { get; init; }

    /// <summary>Spikes in the last second.</summary>
    public int Spikes { get; init; }

    /// <summary>CPU utility (%).</summary>
    public double? CpuUtilPct { get; init; }

    /// <summary>CPU % of maximum frequency (throttle proxy).</summary>
    public double? CpuMaxFreqPct { get; init; }

    /// <summary>GPU 3D engine utilization (%).</summary>
    public double? Gpu3dUtilPct { get; init; }

    /// <summary>Dedicated VRAM used (MB).</summary>
    public double? VramUsedMb { get; init; }

    /// <summary>RTT to the game server (ms, approximate).</summary>
    public double? RttMs { get; init; }

    /// <summary>Jitter (ms).</summary>
    public double? JitterMs { get; init; }

    /// <summary>Packet loss (%).</summary>
    public double? LossPct { get; init; }

    /// <summary>Monitor's own CPU (%), self-measured.</summary>
    public required double SelfCpuPct { get; init; }

    /// <summary>Degrade ladder step (0 = full).</summary>
    public int DegradeLevel { get; init; }
}

/// <summary>Summary emitted at game exit; its <see cref="Detectors"/> become <c>mon.*</c> signals for re-ranking.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MonitorSummary
{
    /// <summary>ULID.</summary>
    public required string SummaryId { get; init; }

    /// <summary>Session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Purpose.</summary>
    public required MonitorPurpose Purpose { get; init; }

    /// <summary>Start.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>End.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Too little data (short match, under 2,000 frames): detectors are Unknown(InsufficientData).</summary>
    public bool InsufficientData { get; init; }

    /// <summary>Frame statistics.</summary>
    public FrameStats? Frames { get; init; }

    /// <summary>Spikes with context (at most 200).</summary>
    public IReadOnlyList<SpikeEvent> Spikes { get; init; } = [];

    /// <summary>Spike/event correlations.</summary>
    public IReadOnlyList<CorrelationResult> Correlations { get; init; } = [];

    /// <summary>Network statistics.</summary>
    public NetStats? Network { get; init; }

    /// <summary>CPU statistics.</summary>
    public CpuStats? Cpu { get; init; }

    /// <summary>GPU statistics.</summary>
    public GpuStats? Gpu { get; init; }

    /// <summary>Detector outputs as signals (for example <c>mon.frametime.periodic_spikes</c>, <c>mon.spikes_correlate.nic_event</c>).</summary>
    public IReadOnlyList<SignalResult> Detectors { get; init; } = [];

    /// <summary>Self-budget report.</summary>
    public required SelfBudgetReport Self { get; init; }

    /// <summary>Per-source status.</summary>
    public IReadOnlyList<MonitorSourceStatus> Sources { get; init; } = [];

    /// <summary>Per-minute series (at most 600 points).</summary>
    public IReadOnlyList<MinuteSample> Series { get; init; } = [];

    /// <summary>Segments the player marked as bad from the tray.</summary>
    public IReadOnlyList<MarkedSegment> MarkedBad { get; init; } = [];
}

/// <summary>Frame statistics.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FrameStats
{
    /// <summary>Frames.</summary>
    public required long FrameCount { get; init; }

    /// <summary>Average FPS.</summary>
    public required double AvgFps { get; init; }

    /// <summary>1% low FPS.</summary>
    public required double P1LowFps { get; init; }

    /// <summary>0.1% low FPS.</summary>
    public double? P01LowFps { get; init; }

    /// <summary>Spikes per minute (&lt;1 good, 1-5 noticeable, &gt;5 bad).</summary>
    public required double SpikesPerMin { get; init; }

    /// <summary>Periodicity score of inter-spike intervals (autocorrelation peak strength 0..1).</summary>
    public double? PeriodicityScore { get; init; }

    /// <summary>Period (s) when periodic.</summary>
    public double? PeriodSeconds { get; init; }

    /// <summary>Share of frames per PresentMode (for example <c>Hardware: Independent Flip</c>, <c>Composed: Flip</c>).</summary>
    public IReadOnlyDictionary<string, double> PresentModeShare { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, double>.Empty;
}

/// <summary>A frame-time spike.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record SpikeEvent
{
    /// <summary>When.</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>Frame time (ms).</summary>
    public required double FrameMs { get; init; }

    /// <summary>Source ids of events within ±1 s.</summary>
    public IReadOnlyList<string> NearbyEvents { get; init; } = [];
}

/// <summary>Correlation of spikes with an event source.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record CorrelationResult
{
    /// <summary>Source id (for example <c>realtek_nic_errors</c>, <c>process:Discord.exe</c>).</summary>
    public required string SourceId { get; init; }

    /// <summary>Spikes with a source event within the window.</summary>
    public required int Hits { get; init; }

    /// <summary>Share of spikes explained.</summary>
    public required double HitRatio { get; init; }

    /// <summary>Lift over a randomly shifted baseline.</summary>
    public double? Lift { get; init; }
}

/// <summary>Network statistics. The server IP is local-only; exports show region/ASN.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record NetStats
{
    /// <summary>Region/ASN label (for example <c>AWS Frankfurt</c>).</summary>
    public string? EndpointRegion { get; init; }

    /// <summary>Server IP (local only).</summary>
    [Sensitivity(Sensitivity.IpAddress)]
    public string? EndpointIp { get; init; }

    /// <summary>Median RTT (ms).</summary>
    public double? RttP50Ms { get; init; }

    /// <summary>95th percentile RTT (ms).</summary>
    public double? RttP95Ms { get; init; }

    /// <summary>Jitter (ms, RFC 3550).</summary>
    public double? JitterMs { get; init; }

    /// <summary>Loss (%).</summary>
    public double? LossPct { get; init; }

    /// <summary>Loss already at hop 1 (local network) vs only beyond the ISP hop.</summary>
    public bool? LossAtFirstHop { get; init; }

    /// <summary>ICMP was filtered; numbers are from a fallback hop ("approximate").</summary>
    public bool Indicative { get; init; } = true;
}

/// <summary>CPU statistics.</summary>
public sealed record CpuStats
{
    /// <summary>Average utility (%).</summary>
    public required double AvgUtilPct { get; init; }

    /// <summary>5th percentile of % of max frequency while GPU busy.</summary>
    public double? P5MaxFreqPct { get; init; }

    /// <summary>Seconds below 70% of max frequency with utility above 50%.</summary>
    public int ThrottleSeconds { get; init; }
}

/// <summary>GPU statistics.</summary>
public sealed record GpuStats
{
    /// <summary>Average 3D utilization (%).</summary>
    public required double Avg3dUtilPct { get; init; }

    /// <summary>Peak dedicated VRAM (MB).</summary>
    public double? MaxVramUsedMb { get; init; }

    /// <summary>Display 4101 (TDR) events during the match.</summary>
    public int TdrCount { get; init; }
}

/// <summary>The monitor's own cost.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record SelfBudgetReport
{
    /// <summary>Average CPU (% of the machine).</summary>
    public required double AvgCpuPct { get; init; }

    /// <summary>p99 per-second CPU.</summary>
    public required double P99CpuPct { get; init; }

    /// <summary>Peak working set (MB, target at most 100).</summary>
    public required double MaxWorkingSetMb { get; init; }

    /// <summary>Degrade steps taken (for example <c>drop_vendor_gpu</c>, <c>pdh_half_rate</c>, <c>stop_network</c>, <c>frames_only</c>).</summary>
    public IReadOnlyList<string> DegradeSteps { get; init; } = [];

    /// <summary>Stopped to protect FPS.</summary>
    public bool StoppedForBudget { get; init; }
}

/// <summary>Monitor source state.</summary>
public enum MonitorSourceState
{
    /// <summary>Active.</summary>
    Active = 0,

    /// <summary>Degraded (lower rate).</summary>
    Degraded = 1,

    /// <summary>Stopped by the budget or the player.</summary>
    Stopped = 2,

    /// <summary>Unavailable (PresentMon missing/blocked, ICMP filtered...).</summary>
    Unavailable = 3,
}

/// <summary>Status of one source.</summary>
public sealed record MonitorSourceStatus
{
    /// <summary>Source.</summary>
    public required MonitorSource Source { get; init; }

    /// <summary>State.</summary>
    public required MonitorSourceState State { get; init; }

    /// <summary>Why unavailable.</summary>
    public UnknownReason? Reason { get; init; }
}

/// <summary>One minute of the series.</summary>
public sealed record MinuteSample
{
    /// <summary>Minute start.</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>Average FPS.</summary>
    public double? Fps { get; init; }

    /// <summary>1% low FPS.</summary>
    public double? P1LowFps { get; init; }

    /// <summary>Spikes.</summary>
    public int Spikes { get; init; }

    /// <summary>Median RTT.</summary>
    public double? RttMs { get; init; }

    /// <summary>Minimum % of max frequency.</summary>
    public double? CpuMaxFreqPct { get; init; }
}

/// <summary>A segment the player marked as bad.</summary>
public sealed record MarkedSegment
{
    /// <summary>Start.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>End.</summary>
    public required DateTimeOffset EndUtc { get; init; }
}

/// <summary>
/// The live monitor (A5, M3): its own low-priority process. Rules: no handle of any kind to Riot/Vanguard processes,
/// no injection, hooks, overlays or toasts during the game, never <c>timeBeginPeriod</c>, never runs together with a
/// scan or a fix. Exposed over a pipe to the engine (StreamJsonRpc supports <see cref="IAsyncEnumerable{T}"/>).
/// </summary>
public interface IMonitorService
{
    /// <summary>Starts.</summary>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task StartAsync(MonitorOptions options, CancellationToken cancellationToken);

    /// <summary>1 Hz ticks until stopped.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    IAsyncEnumerable<MonitorTick> TicksAsync(CancellationToken cancellationToken);

    /// <summary>Marks the last match/segment as bad (tray).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task MarkBadAsync(CancellationToken cancellationToken);

    /// <summary>Stops and summarizes.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<MonitorSummary> StopAsync(CancellationToken cancellationToken);
}
