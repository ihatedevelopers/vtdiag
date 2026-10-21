using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VTD.Contracts.Scanning;
using VTD.Platform.Sources;
using C = VTD.Platform.Capture.PlatformJsonContext;

namespace VTD.Platform.Capture;

/// <summary>
/// Serves a raw capture back as <see cref="SystemSources"/> (A's raw-capture fakes): the same collectors run against
/// recorded probe outputs, so real Turkish-locale captures become deterministic tests. A key that was not captured
/// answers like a missing key/file (registry, files) or a failed read (everything else), and is listed in
/// <see cref="Misses"/>.
/// </summary>
public sealed class ReplaySources
{
    private readonly Dictionary<string, string> _payloads;
    private readonly ConcurrentDictionary<string, byte> _misses = new(StringComparer.Ordinal);

    /// <summary>Creates a replay over capture entries.</summary>
    /// <param name="entries">Entries (the <c>meta:scan</c> header is optional).</param>
    public ReplaySources(IEnumerable<RawCaptureEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            _payloads.TryAdd(e.Source, e.Payload);
        }

        Sources = new SystemSources
        {
            Registry = new Reg(this),
            Wmi = new Wmi(this),
            WmiMethods = new WmiMethods(this),
            Files = new Files(this),
            Events = new Events(this),
            System = new Sys(this),
            Probes = new Probes(this),
        };
    }

    /// <summary>The replaying sources.</summary>
    public SystemSources Sources { get; }

    /// <summary>The capture header, when present.</summary>
    public CaptureMeta? Meta => _payloads.TryGetValue(CaptureFormat.MetaSource, out var p) ? JsonSerializer.Deserialize(p, C.Default.CaptureMeta) : null;

    /// <summary>Keys that were requested but not in the capture.</summary>
    public IReadOnlyCollection<string> Misses => _misses.Keys.Order(StringComparer.Ordinal).ToList();

    private T Get<T>(string key, JsonTypeInfo<T> info, Func<T> missing)
    {
        if (_payloads.TryGetValue(key, out var payload))
        {
            var value = JsonSerializer.Deserialize(payload, info);
            if (value is not null)
            {
                return value;
            }
        }

        _misses.TryAdd(key, 0);
        return missing();
    }

    private sealed class Reg(ReplaySources s) : IRegistrySource
    {
        public RegKeyData ReadKey(RegHive hive, string path)
            => s.Get(CaptureKeys.Registry(hive, path), C.Default.RegKeyData, () => new RegKeyData { Status = ReadStatus.NotFound });
    }

    private sealed class Wmi(ReplaySources s) : IWmiSource
    {
        public WmiResult Query(string wmiNamespace, string wql, TimeSpan timeout)
            => s.Get(CaptureKeys.Wmi(wmiNamespace, wql), C.Default.WmiResult, () => new WmiResult { Status = ReadStatus.Failed, Error = "not_captured" });
    }

    private sealed class WmiMethods(ReplaySources s) : IWmiMethodSource
    {
        public BcdRaw ReadBcd(TimeSpan timeout) => s.Get("wmimethod:bcd", C.Default.BcdRaw, () => new BcdRaw { Status = ReadStatus.Failed });

        public TpmWmiRaw ReadTpm(TimeSpan timeout) => s.Get("wmimethod:tpm", C.Default.TpmWmiRaw, () => new TpmWmiRaw { Status = ReadStatus.Failed });
    }

    private sealed class Files(ReplaySources s) : IFileSource
    {
        public FileStat Stat(string path) => s.Get(CaptureKeys.Stat(path), C.Default.FileStat, () => new FileStat { Status = ReadStatus.NotFound });

        public DirListing List(string path, string pattern, bool recursive, int max)
            => s.Get(CaptureKeys.List(path, pattern, recursive, max), C.Default.DirListing, () => new DirListing { Status = ReadStatus.NotFound });

        public LinesResult ReadLines(string path, LineFilter filter, long maxBytes, int maxLines)
            => s.Get(CaptureKeys.Lines(path, filter.Id, maxBytes, maxLines), C.Default.LinesResult, () => new LinesResult { Status = ReadStatus.NotFound });

        public HeaderResult ReadHeader(string path, int count) => s.Get(CaptureKeys.Header(path, count), C.Default.HeaderResult, () => new HeaderResult { Status = ReadStatus.NotFound });

        public FileVersionResult GetVersion(string path) => s.Get(CaptureKeys.Version(path), C.Default.FileVersionResult, () => new FileVersionResult { Status = ReadStatus.NotFound });

        public ShortcutResult ReadShortcut(string path) => s.Get(CaptureKeys.Shortcut(path), C.Default.ShortcutResult, () => new ShortcutResult { Status = ReadStatus.NotFound });
    }

    private sealed class Events(ReplaySources s) : IEventLogSource
    {
        public EventQueryResult Query(EventQuery query, CancellationToken cancellationToken)
            => s.Get(CaptureKeys.Events(query), C.Default.EventQueryResult, () => new EventQueryResult { Status = ReadStatus.Failed, Error = "not_captured" });
    }

    private sealed class Sys(ReplaySources s) : ISystemInfoSource
    {
        public EnvRaw GetEnvironment() => s.Get("sys:env", C.Default.EnvRaw, () => new EnvRaw
        {
            TimeZoneId = "UTC",
            UiCulture = "en-US",
            WindowsDir = "C:\\Windows",
            ProgramFiles = "C:\\Program Files",
            ProgramFilesX86 = "C:\\Program Files (x86)",
            ProgramData = "C:\\ProgramData",
        });

        public ProcessList GetProcesses() => s.Get("sys:processes", C.Default.ProcessList, () => new ProcessList { Status = ReadStatus.Failed });

        public ServiceList GetServices() => s.Get("sys:services", C.Default.ServiceList, () => new ServiceList { Status = ReadStatus.Failed });

        public DriverList GetLoadedDrivers() => s.Get("sys:drivers", C.Default.DriverList, () => new DriverList { Status = ReadStatus.Failed });

        public DeviceList GetDevices(IReadOnlyList<string> includePhantomClasses)
            => s.Get(CaptureKeys.Devices(includePhantomClasses), C.Default.DeviceList, () => new DeviceList { Status = ReadStatus.Failed });

        public FirmwareRaw GetFirmware(bool privileged) => s.Get(CaptureKeys.Firmware(privileged), C.Default.FirmwareRaw, () => new FirmwareRaw { VariablesStatus = ReadStatus.Failed });

        public PowerRaw GetPower() => s.Get("sys:power", C.Default.PowerRaw, () => new PowerRaw { Status = ReadStatus.Failed });

        public MemoryRaw GetMemory() => s.Get("sys:memory", C.Default.MemoryRaw, () => new MemoryRaw { Status = ReadStatus.Failed });

        public CpuRaw GetCpu() => s.Get("sys:cpu", C.Default.CpuRaw, () => new CpuRaw { Status = ReadStatus.Failed });

        public DisplayRaw GetDisplays() => s.Get("sys:displays", C.Default.DisplayRaw, () => new DisplayRaw { Status = ReadStatus.Failed });

        public NetRaw GetNetwork() => s.Get("sys:network", C.Default.NetRaw, () => new NetRaw { Status = ReadStatus.Failed });

        public ConnectivityRaw GetConnectivity() => s.Get("sys:connectivity", C.Default.ConnectivityRaw, () => new ConnectivityRaw { Status = ReadStatus.Failed });

        public WlanRaw GetWlan() => s.Get("sys:wlan", C.Default.WlanRaw, () => new WlanRaw { Status = ReadStatus.Failed });

        public TpmRaw GetTpm() => s.Get("sys:tpm", C.Default.TpmRaw, () => new TpmRaw { Status = ReadStatus.Failed });

        public WuaRaw GetWindowsUpdate() => s.Get("sys:wua", C.Default.WuaRaw, () => new WuaRaw { Status = ReadStatus.Failed });

        public AudioDefaultsRaw GetAudioDefaults() => s.Get("sys:audiodefaults", C.Default.AudioDefaultsRaw, () => new AudioDefaultsRaw { Status = ReadStatus.Failed });

        public BitLockerRaw GetBitLocker(string driveRoot) => s.Get(CaptureKeys.BitLocker(driveRoot), C.Default.BitLockerRaw, () => new BitLockerRaw { Status = ReadStatus.Failed });
    }

    private sealed class Probes(ReplaySources s) : INetworkProbeSource
    {
        public ClockRaw MeasureClock(TimeSpan timeout) => s.Get("probe:clock", C.Default.ClockRaw, () => new ClockRaw { Status = ReadStatus.Failed });

        public PingRaw Ping(string address, int count, TimeSpan timeout) => s.Get(CaptureKeys.Ping(address, count), C.Default.PingRaw, () => new PingRaw { Status = ReadStatus.Failed });
    }
}
