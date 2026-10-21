using System.Runtime.CompilerServices;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Monitoring;

namespace VTD.Testing.Fakes;

/// <summary>
/// <see cref="IMonitorService"/> that replays a short scripted match: periodic spikes every ~4 s lined up with NIC
/// resets (the Realtek pattern). Ticks are produced on demand (no real timers), one per call of the enumerator.
/// </summary>
/// <param name="clock">Clock for timestamps.</param>
/// <param name="ids">Ids.</param>
public sealed class FakeMonitorService(TimeProvider clock, IIdGenerator ids) : IMonitorService
{
    private MonitorOptions? _options;
    private DateTimeOffset _start;
    private bool _markedBad;

    /// <summary>Number of ticks <see cref="TicksAsync"/> yields before ending.</summary>
    public int TickCount { get; set; } = 10;

    /// <summary>True while started.</summary>
    public bool Running => _options is not null;

    /// <inheritdoc />
    public Task StartAsync(MonitorOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_options is not null)
        {
            throw new InvalidOperationException("Monitor already running.");
        }

        _options = options;
        _start = clock.GetUtcNow();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MonitorTick> TicksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < TickCount && _options is not null; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MonitorTick
            {
                Seq = i + 1,
                Utc = _start.AddSeconds(i + 1),
                GameRunning = true,
                Fps = i % 4 == 3 ? 61 : 238,
                FrameTimeP99Ms = i % 4 == 3 ? 92 : 5.1,
                Spikes = i % 4 == 3 ? 1 : 0,
                CpuUtilPct = 31,
                CpuMaxFreqPct = 104,
                Gpu3dUtilPct = 72,
                VramUsedMb = 5_900,
                RttMs = 42,
                JitterMs = 1.8,
                LossPct = 0,
                SelfCpuPct = 0.7,
            };
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public Task MarkBadAsync(CancellationToken cancellationToken)
    {
        _markedBad = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MonitorSummary> StopAsync(CancellationToken cancellationToken)
    {
        var options = _options ?? throw new InvalidOperationException("Monitor is not running.");
        _options = null;
        var end = clock.GetUtcNow() > _start ? clock.GetUtcNow() : _start.AddMinutes(30);
        return Task.FromResult(new MonitorSummary
        {
            SummaryId = ids.NewId(),
            SessionId = options.SessionId,
            Purpose = options.Purpose,
            StartUtc = _start,
            EndUtc = end,
            Frames = new FrameStats { FrameCount = 412_000, AvgFps = 229, P1LowFps = 64, P01LowFps = 22, SpikesPerMin = 14, PeriodicityScore = 0.81, PeriodSeconds = 4.1 },
            Correlations = [new CorrelationResult { SourceId = "realtek_nic_errors", Hits = 29, HitRatio = 0.78, Lift = 6.2 }],
            Network = new NetStats { EndpointRegion = "AWS Frankfurt", RttP50Ms = 41, RttP95Ms = 47, JitterMs = 1.9, LossPct = 0, LossAtFirstHop = false, Indicative = true },
            Detectors =
            [
                new SignalResult { Id = "mon.frametime.periodic_spikes", State = Tri.True, Strength = 0.81, Evidence = [new Evidence { TextKey = "mon.periodic", Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["period"] = "4.1" }, Source = EvidenceSource.Monitor }] },
                new SignalResult { Id = "mon.spikes_correlate.nic_event", State = Tri.True, Strength = 0.78, Evidence = [new Evidence { TextKey = "mon.correlate.nic", Source = EvidenceSource.Monitor }] },
            ],
            Self = new SelfBudgetReport { AvgCpuPct = 0.8, P99CpuPct = 2.1, MaxWorkingSetMb = 71 },
            Sources = options.Sources.Select(s => new MonitorSourceStatus { Source = s, State = MonitorSourceState.Active }).ToList(),
            MarkedBad = _markedBad ? [new MarkedSegment { StartUtc = _start, EndUtc = end }] : [],
        });
    }
}

/// <summary>
/// Canary tokens for redaction tests (DESIGN.md: "canary tests plant tokens in fixtures"). Plant them in every free-text
/// field of a fixture; a share-level export must contain none of them, and the regex sweep must block an export that
/// still does. All but <see cref="Sid"/> and <see cref="Puuid"/> contain
/// <see cref="VTD.Contracts.Reporting.RedactionConstants.CanaryMarker"/>; those two exercise the shape rules (SID, id-next-to-key).
/// </summary>
public static class CanaryTokens
{
    /// <summary>A Riot-style auth argument.</summary>
    public const string RemotingAuthToken = "--remoting-auth-token=VTDCANARY_q8Zr2pX7uLmN4kTs";

    /// <summary>A JWT-shaped string.</summary>
    public const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJWVERDQU5BUlkifQ.VTDCANARYsig0123456789abcdef";

    /// <summary>A profile path with a user name.</summary>
    public const string UserProfilePath = "C:\\Users\\VtdCanaryUser\\AppData\\Local\\VALORANT";

    /// <summary>A user name.</summary>
    public const string UserName = "VtdCanaryUser";

    /// <summary>A computer name.</summary>
    public const string ComputerName = "VTDCANARY-PC";

    /// <summary>An e-mail address.</summary>
    public const string Email = "vtdcanary@example.invalid";

    /// <summary>A Windows SID.</summary>
    public const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    /// <summary>A PUUID-like id next to its key.</summary>
    public const string Puuid = "puuid=0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";

    /// <summary>All canaries.</summary>
    public static IReadOnlyList<string> All { get; } = [RemotingAuthToken, Jwt, UserProfilePath, UserName, ComputerName, Email, Sid, Puuid];
}
