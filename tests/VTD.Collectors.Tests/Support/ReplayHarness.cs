using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Platform.Capture;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests.Support;

/// <summary>Replays a raw capture through the real orchestrator and collectors.</summary>
internal static class ReplayHarness
{
    public static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    public static IReadOnlyList<RawCaptureEntry> LoadCapture(string path) => VtdJson.Deserialize<IReadOnlyList<RawCaptureEntry>>(File.ReadAllText(path));

    public static async Task<(ScanSnapshot Snapshot, IReadOnlyCollection<string> Misses)> ReplayAsync(IReadOnlyList<RawCaptureEntry> entries, CancellationToken ct)
    {
        var replay = new ReplaySources(entries);
        var meta = replay.Meta ?? throw new InvalidOperationException("capture has no meta:scan entry");
        var clock = new FakeClock(meta.StartedUtc);
        var orchestrator = new ScanOrchestrator(
            replay.Sources,
            clock,
            new SequentialIdGenerator(clock),
            new ScanOrchestratorOptions { Privilege = meta.Elevated ? PrivilegeMode.Elevated : PrivilegeMode.Limited, MachineKeySalt = Salt, AppVersion = "0.0.1", Origin = SnapshotOrigin.Replay });
        var request = new ScanRequest
        {
            KbVersion = "replay",
            AllowNetworkProbes = meta.AllowNetworkProbes,
            EventWindowDays = meta.EventWindowDays,
            Player = new PlayerContext
            {
                Sid = meta.PlayerSid,
                ProfilePath = meta.PlayerProfilePath,
                LocalAppData = meta.PlayerLocalAppData,
                RoamingAppData = meta.PlayerRoamingAppData,
                IsAdmin = meta.PlayerIsAdmin,
            },
        };
        var result = await orchestrator.ScanAsync(request, null, ct);
        return (Normalize(result.Snapshot), replay.Misses);
    }

    /// <summary>Zeroes wall-clock durations so replays compare byte for byte.</summary>
    public static ScanSnapshot Normalize(ScanSnapshot s) => s with
    {
        Meta = s.Meta with { DurationMs = 0, CaptureId = null },
        Runs = s.Runs.Select(r => r with { ElapsedMs = 0 }).ToList(),
    };
}
