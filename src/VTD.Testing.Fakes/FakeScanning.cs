using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;

namespace VTD.Testing.Fakes;

/// <summary>
/// <see cref="IScanService"/> that returns a fixture snapshot (by default the embedded <c>realtek-2-5gbe-stutter</c>),
/// reports one progress step per collector run, and honours cancellation. The returned snapshot gets the request's KB
/// version and language; everything else is the fixture as checked in.
/// </summary>
public sealed class FakeScanService : IScanService
{
    private readonly Func<ScanSnapshot> _snapshot;

    /// <summary>Creates the service around a snapshot factory.</summary>
    /// <param name="snapshot">Factory (called once per scan).</param>
    public FakeScanService(Func<ScanSnapshot> snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>Scans "return" the embedded fixture <paramref name="fixtureName"/>.</summary>
    /// <param name="fixtureName">One of <see cref="Fixtures.Names"/>.</param>
    public static FakeScanService FromFixture(string fixtureName) => new(() => Fixtures.Load(fixtureName));

    /// <summary>Number of scans performed.</summary>
    public int ScanCount { get; private set; }

    /// <summary>The last request received.</summary>
    public ScanRequest? LastRequest { get; private set; }

    /// <inheritdoc />
    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ScanCount++;
        LastRequest = request;
        var snapshot = _snapshot();
        var total = snapshot.Runs.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress
            {
                CollectorId = snapshot.Runs[i].CollectorId,
                Area = AreaOf(snapshot.Runs[i].CollectorId),
                Status = snapshot.Runs[i].Status,
                Completed = i + 1,
                Total = total,
            });
        }

        var result = snapshot with { Meta = snapshot.Meta with { KbVersion = request.KbVersion, AppLanguage = request.AppLanguage } };
        IReadOnlyList<RawCaptureEntry> capture = request.CaptureRaw
            ? [new RawCaptureEntry { CollectorId = "security", Source = "registry:HKLM\\SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State", Payload = "{\"UEFISecureBootEnabled\":1}" }]
            : [];
        return Task.FromResult(new ScanResult { Snapshot = result, Capture = capture });
    }

    private static ScanArea AreaOf(string collectorId) => collectorId switch
    {
        "os" => ScanArea.Windows,
        "hardware" or "firmware" or "power" or "display" or "audio" or "input" => ScanArea.Hardware,
        "security" => ScanArea.Security,
        "storage" or "devices" => ScanArea.Drivers,
        "software" => ScanArea.Apps,
        "network" => ScanArea.Network,
        "riot" => ScanArea.Game,
        _ => ScanArea.Logs,
    };
}

/// <summary>
/// <see cref="IFactProbe"/> with scripted answers. Unscripted ids return Unknown(NotCollected), so a guard that nobody
/// scripted blocks (Unknown never passes a Block guard), exactly like the real thing would on a failed probe.
/// </summary>
public sealed class FakeFactProbe : IFactProbe
{
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>, SignalResult>> _answers = new(StringComparer.Ordinal);

    /// <summary>A probe pre-scripted for a healthy PC where fixes may run: game and client closed, no pending reboot,
    /// no WU install, on AC, enough disk, monitor idle, internet up, BitLocker off.</summary>
    public static FakeFactProbe ReadyToFix()
    {
        var probe = new FakeFactProbe();
        probe.Set(GuardIds.GameRunning, Tri.False)
             .Set(GuardIds.ClientRunning, Tri.False)
             .Set(GuardIds.PendingReboot, Tri.False)
             .Set(GuardIds.WindowsUpdateInstalling, Tri.False)
             .Set(GuardIds.OnAcPower, Tri.True)
             .Set(GuardIds.BatteryAbove, Tri.True)
             .Set(GuardIds.DiskFreeAbove, Tri.True)
             .Set(GuardIds.MonitorActive, Tri.False)
             .Set(GuardIds.InternetReachable, Tri.True)
             .Set(GuardIds.AdapterNotDefaultRoute, Tri.True)
             .Set(GuardIds.OtherAdapterOnline, Tri.True)
             .Set(GuardIds.ServiceExists, Tri.True)
             .Set(GuardIds.BitLockerOn, Tri.False)
             .Set(GuardIds.ModifiedWindows, Tri.False);
        return probe;
    }

    /// <summary>Probe calls in order (id and args).</summary>
    public List<(string Id, IReadOnlyDictionary<string, string> Args)> Calls { get; } = [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedIds => _answers.Keys;

    /// <summary>Scripts a constant answer.</summary>
    /// <param name="id">Probe id.</param>
    /// <param name="state">State.</param>
    public FakeFactProbe Set(string id, Tri state)
    {
        _answers[id] = _ => new SignalResult { Id = id, State = state, UnknownReason = state == Tri.Unknown ? UnknownReason.Failed : null };
        return this;
    }

    /// <summary>Scripts an answer that depends on the arguments.</summary>
    /// <param name="id">Probe id.</param>
    /// <param name="answer">Answer factory.</param>
    public FakeFactProbe Set(string id, Func<IReadOnlyDictionary<string, string>, SignalResult> answer)
    {
        _answers[id] = answer ?? throw new ArgumentNullException(nameof(answer));
        return this;
    }

    /// <inheritdoc />
    public Task<SignalResult> ProbeAsync(string probeId, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probeId);
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((probeId, args));
        return Task.FromResult(_answers.TryGetValue(probeId, out var answer)
            ? answer(args)
            : new SignalResult { Id = probeId, State = Tri.Unknown, UnknownReason = UnknownReason.NotCollected });
    }
}
