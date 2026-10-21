using VTD.Platform.Sources;
using C = VTD.Platform.Capture.PlatformJsonContext;

namespace VTD.Platform.Capture;

/// <summary>Wraps live sources so every read is also recorded.</summary>
public static class CapturingSources
{
    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">Live sources.</param>
    /// <param name="recorder">Recorder.</param>
    public static SystemSources Wrap(SystemSources inner, CaptureRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recorder);
        return new SystemSources
        {
            Registry = new Reg(inner.Registry, recorder),
            Wmi = new Wmi(inner.Wmi, recorder),
            WmiMethods = new WmiMethods(inner.WmiMethods, recorder),
            Files = new Files(inner.Files, recorder),
            Events = new Events(inner.Events, recorder),
            System = new Sys(inner.System, recorder),
            Probes = new Probes(inner.Probes, recorder),
        };
    }

    private sealed class Reg(IRegistrySource inner, CaptureRecorder r) : IRegistrySource
    {
        public RegKeyData ReadKey(RegHive hive, string path) => r.Record(CaptureKeys.Registry(hive, path), inner.ReadKey(hive, path), C.Default.RegKeyData);
    }

    private sealed class Wmi(IWmiSource inner, CaptureRecorder r) : IWmiSource
    {
        public WmiResult Query(string wmiNamespace, string wql, TimeSpan timeout)
            => r.Record(CaptureKeys.Wmi(wmiNamespace, wql), inner.Query(wmiNamespace, wql, timeout), C.Default.WmiResult);
    }

    private sealed class WmiMethods(IWmiMethodSource inner, CaptureRecorder r) : IWmiMethodSource
    {
        public BcdRaw ReadBcd(TimeSpan timeout) => r.Record("wmimethod:bcd", inner.ReadBcd(timeout), C.Default.BcdRaw);

        public TpmWmiRaw ReadTpm(TimeSpan timeout) => r.Record("wmimethod:tpm", inner.ReadTpm(timeout), C.Default.TpmWmiRaw);
    }

    private sealed class Files(IFileSource inner, CaptureRecorder r) : IFileSource
    {
        public FileStat Stat(string path) => r.Record(CaptureKeys.Stat(path), inner.Stat(path), C.Default.FileStat);

        public DirListing List(string path, string pattern, bool recursive, int max)
            => r.Record(CaptureKeys.List(path, pattern, recursive, max), inner.List(path, pattern, recursive, max), C.Default.DirListing);

        public LinesResult ReadLines(string path, LineFilter filter, long maxBytes, int maxLines)
            => r.Record(CaptureKeys.Lines(path, filter.Id, maxBytes, maxLines), inner.ReadLines(path, filter, maxBytes, maxLines), C.Default.LinesResult);

        public HeaderResult ReadHeader(string path, int count) => r.Record(CaptureKeys.Header(path, count), inner.ReadHeader(path, count), C.Default.HeaderResult);

        public FileVersionResult GetVersion(string path) => r.Record(CaptureKeys.Version(path), inner.GetVersion(path), C.Default.FileVersionResult);

        public ShortcutResult ReadShortcut(string path) => r.Record(CaptureKeys.Shortcut(path), inner.ReadShortcut(path), C.Default.ShortcutResult);
    }

    private sealed class Events(IEventLogSource inner, CaptureRecorder r) : IEventLogSource
    {
        public EventQueryResult Query(EventQuery query, CancellationToken cancellationToken)
            => r.Record(CaptureKeys.Events(query), inner.Query(query, cancellationToken), C.Default.EventQueryResult);
    }

    private sealed class Sys(ISystemInfoSource inner, CaptureRecorder r) : ISystemInfoSource
    {
        public EnvRaw GetEnvironment() => r.Record("sys:env", inner.GetEnvironment(), C.Default.EnvRaw);

        public ProcessList GetProcesses() => r.Record("sys:processes", inner.GetProcesses(), C.Default.ProcessList);

        public ServiceList GetServices() => r.Record("sys:services", inner.GetServices(), C.Default.ServiceList);

        public DriverList GetLoadedDrivers() => r.Record("sys:drivers", inner.GetLoadedDrivers(), C.Default.DriverList);

        public DeviceList GetDevices(IReadOnlyList<string> includePhantomClasses)
            => r.Record(CaptureKeys.Devices(includePhantomClasses), inner.GetDevices(includePhantomClasses), C.Default.DeviceList);

        public FirmwareRaw GetFirmware(bool privileged) => r.Record(CaptureKeys.Firmware(privileged), inner.GetFirmware(privileged), C.Default.FirmwareRaw);

        public PowerRaw GetPower() => r.Record("sys:power", inner.GetPower(), C.Default.PowerRaw);

        public MemoryRaw GetMemory() => r.Record("sys:memory", inner.GetMemory(), C.Default.MemoryRaw);

        public CpuRaw GetCpu() => r.Record("sys:cpu", inner.GetCpu(), C.Default.CpuRaw);

        public DisplayRaw GetDisplays() => r.Record("sys:displays", inner.GetDisplays(), C.Default.DisplayRaw);

        public NetRaw GetNetwork() => r.Record("sys:network", inner.GetNetwork(), C.Default.NetRaw);

        public ConnectivityRaw GetConnectivity() => r.Record("sys:connectivity", inner.GetConnectivity(), C.Default.ConnectivityRaw);

        public WlanRaw GetWlan() => r.Record("sys:wlan", inner.GetWlan(), C.Default.WlanRaw);

        public TpmRaw GetTpm() => r.Record("sys:tpm", inner.GetTpm(), C.Default.TpmRaw);

        public WuaRaw GetWindowsUpdate() => r.Record("sys:wua", inner.GetWindowsUpdate(), C.Default.WuaRaw);

        public AudioDefaultsRaw GetAudioDefaults() => r.Record("sys:audiodefaults", inner.GetAudioDefaults(), C.Default.AudioDefaultsRaw);

        public BitLockerRaw GetBitLocker(string driveRoot) => r.Record(CaptureKeys.BitLocker(driveRoot), inner.GetBitLocker(driveRoot), C.Default.BitLockerRaw);
    }

    private sealed class Probes(INetworkProbeSource inner, CaptureRecorder r) : INetworkProbeSource
    {
        public ClockRaw MeasureClock(TimeSpan timeout) => r.Record("probe:clock", inner.MeasureClock(timeout), C.Default.ClockRaw);

        public PingRaw Ping(string address, int count, TimeSpan timeout) => r.Record(CaptureKeys.Ping(address, count), inner.Ping(address, count, timeout), C.Default.PingRaw);
    }
}
