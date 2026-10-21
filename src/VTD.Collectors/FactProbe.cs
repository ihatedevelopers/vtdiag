using System.Globalization;
using VTD.Collectors.Collectors;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Scanning;
using VTD.Platform.Sources;

namespace VTD.Collectors;

/// <summary>
/// Fresh, targeted live probes for guards (S3) and post-reboot verification: every <see cref="GuardIds"/> id. Each call
/// reads the PC again (nothing is cached between calls). Failures return Unknown, which never passes a Block guard.
/// </summary>
public sealed class FactProbe : IFactProbe
{
    private const string VtdMonitorImage = "VTDiag.Monitor.exe";

    private readonly SystemSources _sources;
    private readonly PlayerContext _player;
    private readonly TimeProvider _clock;
    private readonly PrivilegeMode _privilege;

    /// <summary>Creates the probe.</summary>
    /// <param name="sources">Sources.</param>
    /// <param name="player">Interactive player (from the pipe).</param>
    /// <param name="clock">Clock.</param>
    /// <param name="privilege">Engine privilege.</param>
    public FactProbe(SystemSources sources, PlayerContext player, TimeProvider clock, PrivilegeMode privilege)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _privilege = privilege;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedIds => GuardIds.All;

    /// <inheritdoc />
    public Task<SignalResult> ProbeAsync(string probeId, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probeId);
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () =>
            {
                try
                {
                    return Probe(probeId, args, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return Unknown(probeId, UnknownReason.Failed);
                }
            },
            cancellationToken);
    }

    private ScanEnvironment NewEnvironment()
    {
        var request = new ScanRequest { KbVersion = "probe", Player = _player, Mode = ScanMode.Targeted, AllowNetworkProbes = false };
        return new ScanEnvironment(_sources, new CollectContext { Request = request, Privilege = _privilege, StartedUtc = _clock.GetUtcNow(), Clock = _clock });
    }

    private SignalResult Probe(string id, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var env = NewEnvironment();
        switch (id)
        {
            case GuardIds.GameRunning:
                return ProcessIs(env, id, n => string.Equals(n, RiotProcessNames.Game, StringComparison.OrdinalIgnoreCase));
            case GuardIds.ClientRunning:
                return ProcessIs(env, id, RiotCollector.IsClientProcess);
            case GuardIds.MonitorActive:
                return ProcessIs(env, id, n => string.Equals(n, VtdMonitorImage, StringComparison.OrdinalIgnoreCase));
            case GuardIds.PendingReboot:
                return FromFact(id, OsCollector.PendingReboot(env, new RunBuilder("os")).Map(p => p.ComponentServicing || p.WindowsUpdate || p.PendingFileRename), "os.pendingReboot");
            case GuardIds.WindowsUpdateInstalling:
                return WuInstalling(env, id);
            case GuardIds.OnAcPower:
            case GuardIds.BatteryAbove:
                return Power(env, id, args);
            case GuardIds.DiskFreeAbove:
                return DiskFree(env, id, args);
            case GuardIds.AdapterNotDefaultRoute:
            case GuardIds.OtherAdapterOnline:
            case GuardIds.InternetReachable:
                return Network(env, id, args, ct);
            case GuardIds.ServiceExists:
                return ServiceExists(env, id, args);
            case GuardIds.BitLockerOn:
                return BitLocker(env, id);
            case GuardIds.ModifiedWindows:
                var markers = OsCollector.ModifiedMarkers(env);
                return Result(id, markers.Count > 0, ("markers", string.Join(',', markers)));
            case GuardIds.ClockOk:
                return Clock(env, id, args);
            default:
                return Unknown(id, UnknownReason.NotCollected);
        }
    }

    private static SignalResult ProcessIs(ScanEnvironment env, string id, Func<string, bool> match)
    {
        var p = env.Sources.System.GetProcesses();
        return p.Status != ReadStatus.Ok ? Unknown(id, env.ReasonOf(p.Status)) : Result(id, p.Items.Any(x => match(x.Name)));
    }

    private static SignalResult WuInstalling(ScanEnvironment env, string id)
        => FromFact(id, OsCollector.WuInstalling(env, new RunBuilder("os")), "os.windowsUpdateInstalling");

    private static SignalResult Power(ScanEnvironment env, string id, IReadOnlyDictionary<string, string> args)
    {
        var p = env.Sources.System.GetPower();
        if (!p.PowerStatusOk || p.AcLine == 255)
        {
            return Unknown(id, UnknownReason.Failed);
        }

        var onAc = p.AcLine == 1;
        if (id == GuardIds.OnAcPower)
        {
            return Result(id, onAc);
        }

        var threshold = Parse.Int(args.GetValueOrDefault("percent")) ?? 50;
        if (onAc || p.BatteryFlag == 128)
        {
            return Result(id, true, ("onAc", onAc ? "true" : "false"));
        }

        return p.BatteryPercent is >= 0 and <= 100
            ? Result(id, p.BatteryPercent > threshold, ("percent", p.BatteryPercent.ToString(CultureInfo.InvariantCulture)))
            : Unknown(id, UnknownReason.Failed);
    }

    private static SignalResult DiskFree(ScanEnvironment env, string id, IReadOnlyDictionary<string, string> args)
    {
        var mb = Parse.Long(args.GetValueOrDefault("mb")) ?? 200;
        var r = env.Sources.Wmi.Query(@"root\cimv2", "SELECT DriveLetter, FreeSpace FROM Win32_Volume WHERE DriveLetter = '" + env.SystemDrive + "'", TimeSpan.FromSeconds(5));
        var free = r.Status == ReadStatus.Ok && r.Rows.Count > 0 ? Parse.Long(r.Rows[0].GetValueOrDefault("FreeSpace")) : null;
        if (free is null)
        {
            return Unknown(id, env.ReasonOf(r.Status == ReadStatus.Ok ? ReadStatus.Failed : r.Status));
        }

        var freeMb = free.Value / (1024 * 1024);
        return Result(id, freeMb > mb, ("freeMb", freeMb.ToString(CultureInfo.InvariantCulture)));
    }

    private SignalResult Network(ScanEnvironment env, string id, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        if (id == GuardIds.InternetReachable)
        {
            var c = env.Sources.System.GetConnectivity();
            return c.Status == ReadStatus.Ok ? Result(id, (c.Flags & 0x440) != 0) : Unknown(id, env.ReasonOf(c.Status));
        }

        CollectorOutput output;
        try
        {
            output = new NetworkCollector(_sources).CollectAsync(env, ct).WaitAsync(TimeSpan.FromSeconds(10), ct).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            return Unknown(id, UnknownReason.TimedOut);
        }
        var adapters = output.Patch.Network?.Adapters;
        if (adapters is null || !adapters.TryGetValue(out var list))
        {
            return Unknown(id, adapters?.Reason ?? UnknownReason.Failed);
        }

        if (id == GuardIds.AdapterNotDefaultRoute)
        {
            var instance = args.GetValueOrDefault("instanceId");
            if (string.IsNullOrEmpty(instance))
            {
                return Unknown(id, UnknownReason.Failed);
            }

            var adapter = list.FirstOrDefault(a => string.Equals(a.InstanceId, instance, StringComparison.OrdinalIgnoreCase));
            var entity = new EntityRef { Kind = EntityKind.Adapter, Id = instance };
            return adapter is null
                ? new SignalResult { Id = id, State = Tri.True, Entities = [entity] }
                : new SignalResult { Id = id, State = adapter.IsDefaultRoute ? Tri.False : Tri.True, Entities = [entity] };
        }

        var except = args.GetValueOrDefault("exceptInstanceId");
        var other = list.Where(a => a.Physical && a.Enabled && a.Status == Contracts.Snapshots.AdapterOperStatus.Up && !string.Equals(a.InstanceId, except, StringComparison.OrdinalIgnoreCase)).ToList();
        var net = env.Sources.System.GetNetwork();
        if (net.Status != ReadStatus.Ok)
        {
            return Unknown(id, env.ReasonOf(net.Status));
        }

        var withGateway = other.Where(a => net.Interfaces.Any(i => string.Equals(i.Guid, a.InterfaceGuid, StringComparison.OrdinalIgnoreCase) && i.Gateways.Count > 0)).ToList();
        return new SignalResult { Id = id, State = withGateway.Count > 0 ? Tri.True : Tri.False, Entities = withGateway.Select(a => new EntityRef { Kind = EntityKind.Adapter, Id = a.InstanceId }).ToList() };
    }

    private static SignalResult ServiceExists(ScanEnvironment env, string id, IReadOnlyDictionary<string, string> args)
    {
        var name = args.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\', StringComparison.Ordinal))
        {
            return Unknown(id, UnknownReason.Failed);
        }

        var key = env.Hklm(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key.Status switch
        {
            ReadStatus.Ok => new SignalResult { Id = id, State = Tri.True, Entities = [new EntityRef { Kind = EntityKind.Service, Id = name }] },
            ReadStatus.NotFound => Result(id, false),
            _ => Unknown(id, env.ReasonOf(key.Status)),
        };
    }

    private static SignalResult BitLocker(ScanEnvironment env, string id)
    {
        var raw = env.Sources.System.GetBitLocker(env.SystemDrive + "\\");
        if (raw.Status != ReadStatus.Ok)
        {
            return Unknown(id, env.ReasonOf(raw.Status));
        }

        return Result(id, raw.Protection is 1 or 3 or 4 or 5 or 6, ("protection", (raw.Protection ?? 0).ToString(CultureInfo.InvariantCulture)));
    }

    private static SignalResult Clock(ScanEnvironment env, string id, IReadOnlyDictionary<string, string> args)
    {
        var max = Parse.Long(args.GetValueOrDefault("maxSkewSeconds")) ?? 300;
        var c = env.Sources.Probes.MeasureClock(TimeSpan.FromSeconds(3));
        if (c.Status != ReadStatus.Ok || c.SkewSeconds is null)
        {
            return Unknown(id, c.Status == ReadStatus.TimedOut ? UnknownReason.TimedOut : UnknownReason.Failed);
        }

        return Result(id, Math.Abs(c.SkewSeconds.Value) <= max, ("skewSeconds", c.SkewSeconds.Value.ToString(CultureInfo.InvariantCulture)), ("source", c.Source ?? "ntp"));
    }

    private static SignalResult FromFact(string id, VTD.Contracts.Fact<bool> fact, string factPath)
        => fact.TryGetValue(out var v)
            ? Result(id, v) with { Evidence = [new Evidence { TextKey = "probe." + id, FactPath = factPath }] }
            : Unknown(id, fact.Reason ?? UnknownReason.Failed);

    private static SignalResult Result(string id, bool value, params (string Key, string Value)[] args) => new()
    {
        Id = id,
        State = value ? Tri.True : Tri.False,
        Evidence = args.Length == 0 ? [] : [new Evidence { TextKey = "probe." + id, Args = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal) }],
    };

    private static SignalResult Unknown(string id, UnknownReason reason) => new() { Id = id, State = Tri.Unknown, UnknownReason = reason };
}
