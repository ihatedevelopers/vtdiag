using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>PnP devices (CfgMgr32, InstanceIds and problem codes) and loaded kernel drivers.</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class DevicesCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <inheritdoc />
    public override string Id => "devices";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Drivers;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var devices = run.Try("cfgmgr32", () => Devices(env, run));
        cancellationToken.ThrowIfCancellationRequested();
        var drivers = run.Try("EnumDeviceDrivers", () => LoadedDrivers(env, run, cancellationToken));
        return new SnapshotPatch { Devices = devices, Drivers = drivers };
    }

    private static Fact<IReadOnlyList<DeviceInfo>> Devices(ScanEnvironment env, RunBuilder run)
    {
        const string m = "cfgmgr32";
        var list = env.Devices;
        if (list.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<DeviceInfo>>(env, list.Status, m);
        }

        var items = list.Items
            .Select(d => new DeviceInfo
            {
                InstanceId = d.InstanceId,
                ClassGuid = d.ClassGuid ?? string.Empty,
                ClassName = d.ClassName,
                Enumerator = d.Enumerator,
                FriendlyName = d.FriendlyName,
                HardwareIds = d.HardwareIds,
                Present = d.Present,
                Enabled = d.ProblemCode != 22,
                ProblemCode = d.ProblemCode,
                Service = d.Service,
                DriverVersion = d.DriverVersion,
                DriverDate = d.DriverDate is { } dd ? DateOnly.FromDateTime(dd.UtcDateTime) : null,
                DriverProvider = d.DriverProvider,
                InfName = d.InfName,
            })
            .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return run.Known<IReadOnlyList<DeviceInfo>>(items, m);
    }

    /// <summary>Loaded drivers, memoized per scan (shared with the pack executor).</summary>
    /// <param name="env">Environment.</param>
    internal static Fact<IReadOnlyList<LoadedDriver>> LoadedDrivers(ScanEnvironment env)
        => env.Memo("devices.drivers", () => ReadDrivers(env, new RunBuilder("devices"), CancellationToken.None));

    private static Fact<IReadOnlyList<LoadedDriver>> LoadedDrivers(ScanEnvironment env, RunBuilder run, CancellationToken ct)
    {
        var fact = LoadedDrivers(env);
        if (string.Equals(fact.Source, "devices/scm:active_drivers", StringComparison.Ordinal))
        {
            run.Issue("fallback:scm_active_drivers");
        }

        ct.ThrowIfCancellationRequested();
        return fact.IsKnown
            ? (fact.IsComplete ? run.Known(fact.Value, fact.Source![("devices/".Length)..]) : run.Partial(fact.Value, fact.Source![("devices/".Length)..], fact.Detail))
            : run.Unknown<IReadOnlyList<LoadedDriver>>(fact.Reason ?? UnknownReason.Failed, fact.Source?[("devices/".Length)..] ?? "drivers", fact.Detail);
    }

    private static Fact<IReadOnlyList<LoadedDriver>> ReadDrivers(ScanEnvironment env, RunBuilder run, CancellationToken ct)
    {
        var raw = env.Sources.System.GetLoadedDrivers();
        if (raw.Status == ReadStatus.Ok)
        {
            var list = new List<LoadedDriver>();
            foreach (var d in raw.Items)
            {
                ct.ThrowIfCancellationRequested();
                var path = PathText.NormalizeImagePath(d.Path, env.WindowsDir, env.Env.ProgramFiles);
                var v = path is null ? null : env.Sources.Files.GetVersion(path);
                list.Add(new LoadedDriver { Name = d.Name, Path = d.Path, Version = v?.FileVersion });
            }

            return run.Known<IReadOnlyList<LoadedDriver>>(Sorted(list), "EnumDeviceDrivers");
        }

        // Limited mode on Windows 11 24H2+: kernel addresses are hidden from non-elevated callers, so the module list
        // comes from the SCM instead (every running kernel/file-system driver is a service).
        const string m = "scm:active_drivers";
        var services = env.Services;
        if (services.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<LoadedDriver>>(env, services.Status, m);
        }

        var drivers = new List<LoadedDriver>();
        foreach (var s in services.Items.Where(s => (s.Type & 0x3) != 0 && s.State == 4))
        {
            ct.ThrowIfCancellationRequested();
            var key = env.Hklm(@"SYSTEM\CurrentControlSet\Services\" + s.Name);
            var raw2 = key.Str("ImagePath");
            var path = PathText.NormalizeImagePath(raw2, env.WindowsDir, env.Env.ProgramFiles)
                       ?? Path.Combine(env.WindowsDir, "System32", "drivers", s.Name + ".sys");
            var v = env.Sources.Files.GetVersion(path);
            drivers.Add(new LoadedDriver { Name = PathText.FileName(path), Path = raw2 is null ? path : PathText.Executable(raw2), Version = v.FileVersion });
        }

        return run.Known<IReadOnlyList<LoadedDriver>>(Sorted(drivers), m);
    }

    private static List<LoadedDriver> Sorted(List<LoadedDriver> list)
        => list.DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
