using System.Text.RegularExpressions;
using VTD.Platform.Sources;

namespace VTD.Collectors.Tests.Support;

/// <summary>
/// In-memory sources for synthetic scans. Registry keys are addressed as <c>HKLM\path</c> / <c>HKU\path</c>, WMI by class
/// name, files by absolute path. Text files hold RAW lines: the real <see cref="LineFilter"/> runs over them, exactly like
/// the live file source, so extractor privacy is exercised end to end.
/// </summary>
internal sealed partial class FakeSources : IRegistrySource, IWmiSource, IWmiMethodSource, IFileSource, IEventLogSource, ISystemInfoSource, INetworkProbeSource
{
    public Dictionary<string, RegKeyData> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, WmiResult> WmiByClass { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FileStat> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, DirListing> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string[]> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FileVersionResult> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Log, EventRec Record)> EventRecords { get; } = [];

    public List<string> DeniedTextNeedles { get; } = [];

    public Dictionary<string, ReadStatus> EventLogStatus { get; } = new(StringComparer.OrdinalIgnoreCase);

    public EnvRaw Env { get; set; } = new()
    {
        TimeZoneId = "Turkey Standard Time",
        UiCulture = "tr-TR",
        TickCount64 = 3_600_000,
        WindowsDir = @"C:\Windows",
        ProgramFiles = @"C:\Program Files",
        ProgramFilesX86 = @"C:\Program Files (x86)",
        ProgramData = @"C:\ProgramData",
        Elevated = true,
        OsArchitecture = "x64",
    };

    public ProcessList Processes { get; set; } = new() { Status = ReadStatus.Ok };

    public ServiceList Services { get; set; } = new() { Status = ReadStatus.Ok };

    public DriverList Drivers { get; set; } = new() { Status = ReadStatus.Ok };

    public DeviceList Devices { get; set; } = new() { Status = ReadStatus.Ok };

    public FirmwareRaw Firmware { get; set; } = new() { FirmwareType = 2, VariablesStatus = ReadStatus.Ok, SetupMode = 0, DbHasCa2023 = true, OsIndicationsSupported = 1 };

    public PowerRaw Power { get; set; } = new()
    {
        Status = ReadStatus.Ok,
        ActiveScheme = "381b4222-f694-41f0-9685-ff5bb260df2e",
        SchemeName = "Dengeli",
        Overlay = "00000000-0000-0000-0000-000000000000",
        UsbSelectiveSuspendAc = 1,
        UsbSelectiveSuspendDc = 1,
        ProcessorMinAc = 5,
        ProcessorMaxAc = 100,
        PowerStatusOk = true,
        AcLine = 1,
        BatteryFlag = 128,
        BatteryPercent = 255,
    };

    public MemoryRaw Memory { get; set; } = new() { Status = ReadStatus.Ok, TotalBytes = 32L << 30, AvailableBytes = 20L << 30, StandbyBytes = 6L << 30 };

    public CpuRaw Cpu { get; set; } = new() { Status = ReadStatus.Ok, Cores = 8, Logical = 16, CoresByEfficiencyClass = [8], Architecture = "x64", CpuidAvailable = true, CpuidVendor = "AuthenticAMD", Svm = true, HypervisorBit = true };

    public DisplayRaw Displays { get; set; } = new() { Status = ReadStatus.Ok };

    public NetRaw Network { get; set; } = new() { Status = ReadStatus.Ok };

    public ConnectivityRaw Connectivity { get; set; } = new() { Status = ReadStatus.Ok, Flags = 0x40 };

    public WlanRaw Wlan { get; set; } = new() { Status = ReadStatus.Unsupported };

    public TpmRaw Tpm { get; set; } = new() { Status = ReadStatus.Ok, Version = 2 };

    public BitLockerRaw BitLockerRaw { get; set; } = new() { Status = ReadStatus.Ok, Protection = 2 };

    public WuaRaw Wua { get; set; } = new() { Status = ReadStatus.Ok, IsBusy = false };

    public AudioDefaultsRaw AudioDefaults { get; set; } = new() { Status = ReadStatus.Ok };

    public BcdRaw Bcd { get; set; } = new() { Status = ReadStatus.Ok, TestSigning = false, NoIntegrityChecks = false, Nx = 0, HypervisorLaunchType = 1, Debug = false, FirmwareEntriesRead = true, FirmwareEntryPaths = [@"\efi\microsoft\boot\bootmgfw.efi"] };

    public TpmWmiRaw TpmWmi { get; set; } = new() { Status = ReadStatus.Ok, SpecVersion = "2.0", ManufacturerIdTxt = "AMD", Enabled = true, Activated = true, Ready = true };

    public ClockRaw Clock { get; set; } = new() { Status = ReadStatus.Ok, SkewSeconds = 2, Source = "ntp" };

    public PingRaw PingResult { get; set; } = new() { Status = ReadStatus.Ok, Sent = 10, RttMs = [10, 11, 12, 10, 11, 10, 13, 10, 11, 12] };

    /// <summary>Called before every system-info read (tests use it to hang or throw).</summary>
    public Action<string>? OnSystemCall { get; set; }

    public int ProcessCalls { get; private set; }

    public VTD.Platform.Sources.SystemSources ToSources() => new()
    {
        Registry = this,
        Wmi = this,
        WmiMethods = this,
        Files = this,
        Events = this,
        System = this,
        Probes = this,
    };

    // ---- builders ----
    public FakeSources Reg(string fullPath, params (string Name, string Kind, string? Data)[] values)
    {
        var existing = Keys.TryGetValue(fullPath, out var k) ? k : new RegKeyData { Status = ReadStatus.Ok };
        Keys[fullPath] = existing with { Status = ReadStatus.Ok, Values = existing.Values.Concat(values.Select(v => new RegValue { Name = v.Name, Kind = v.Kind, Data = v.Data })).ToList() };
        var slash = fullPath.LastIndexOf('\\');
        if (slash > 0)
        {
            var parent = fullPath[..slash];
            var child = fullPath[(slash + 1)..];
            var p = Keys.TryGetValue(parent, out var pk) ? pk : new RegKeyData { Status = ReadStatus.Ok };
            if (!p.SubKeys.Contains(child, StringComparer.OrdinalIgnoreCase))
            {
                Keys[parent] = p with { SubKeys = p.SubKeys.Append(child).ToList() };
            }
        }

        return this;
    }

    public FakeSources Denied(string fullPath)
    {
        Keys[fullPath] = new RegKeyData { Status = ReadStatus.AccessDenied };
        return this;
    }

    public FakeSources Wmi(string className, params Dictionary<string, string>[] rows)
    {
        WmiByClass[className] = new WmiResult { Status = ReadStatus.Ok, Rows = rows };
        return this;
    }

    public FakeSources File(string path, long size, DateTimeOffset modified, params string[] lines)
    {
        Stats[path] = new FileStat { Status = ReadStatus.Ok, Size = size, ModifiedUtc = modified, CreatedUtc = modified };
        if (lines.Length > 0)
        {
            Texts[path] = lines;
        }

        var dir = Path.GetDirectoryName(path)!;
        AddEntry(dir, new DirEntry { Name = Path.GetFileName(path), Size = size, ModifiedUtc = modified, CreatedUtc = modified });
        return this;
    }

    public FakeSources Folder(string path, DateTimeOffset created)
    {
        Stats[path] = new FileStat { Status = ReadStatus.Ok, IsDirectory = true, ModifiedUtc = created, CreatedUtc = created };
        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            AddEntry(parent, new DirEntry { Name = Path.GetFileName(path), IsDirectory = true, ModifiedUtc = created, CreatedUtc = created });
        }

        return this;
    }

    public FakeSources Event(string log, string provider, int id, DateTimeOffset utc, params (string Key, string Value)[] data)
    {
        EventRecords.Add((log, new EventRec { Provider = provider, Id = id, Level = 2, Utc = utc, Data = data.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal) }));
        return this;
    }

    private void AddEntry(string dir, DirEntry entry)
    {
        var listing = Dirs.TryGetValue(dir, out var l) ? l : new DirListing { Status = ReadStatus.Ok };
        Dirs[dir] = listing with { Entries = listing.Entries.Where(e => !string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase)).Append(entry).ToList() };
        if (!Stats.ContainsKey(dir))
        {
            Stats[dir] = new FileStat { Status = ReadStatus.Ok, IsDirectory = true };
        }

        var parent = Path.GetDirectoryName(dir);
        if (parent is not null && !(Dirs.TryGetValue(parent, out var pl) && pl.Entries.Any(e => string.Equals(e.Name, Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))))
        {
            AddEntry(parent, new DirEntry { Name = Path.GetFileName(dir), IsDirectory = true, ModifiedUtc = entry.ModifiedUtc, CreatedUtc = entry.CreatedUtc });
        }
    }

    // ---- IRegistrySource ----
    public RegKeyData ReadKey(RegHive hive, string path)
        => Keys.TryGetValue((hive == RegHive.Users ? "HKU\\" : "HKLM\\") + path, out var k) ? k : new RegKeyData { Status = ReadStatus.NotFound };

    // ---- IWmiSource ----
    public WmiResult Query(string wmiNamespace, string wql, TimeSpan timeout)
    {
        var m = FromClass().Match(wql);
        return m.Success && WmiByClass.TryGetValue(m.Groups[1].Value, out var r) ? r : new WmiResult { Status = ReadStatus.NotFound };
    }

    // ---- IWmiMethodSource ----
    public BcdRaw ReadBcd(TimeSpan timeout) => Bcd;

    public TpmWmiRaw ReadTpm(TimeSpan timeout) => TpmWmi;

    // ---- IFileSource ----
    public FileStat Stat(string path) => Stats.TryGetValue(path, out var s) ? s : new FileStat { Status = ReadStatus.NotFound };

    public DirListing List(string path, string pattern, bool recursive, int max)
    {
        if (!Dirs.TryGetValue(path, out var listing))
        {
            return new DirListing { Status = Stats.TryGetValue(path, out var s) && s.Status == ReadStatus.AccessDenied ? ReadStatus.AccessDenied : ReadStatus.NotFound };
        }

        if (listing.Status != ReadStatus.Ok)
        {
            return listing;
        }

        var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var entries = listing.Entries.Where(e => regex.IsMatch(Path.GetFileName(e.Name))).ToList();
        if (recursive)
        {
            foreach (var sub in listing.Entries.Where(e => e.IsDirectory))
            {
                var child = List(Path.Combine(path, sub.Name), pattern, true, max);
                entries.AddRange(child.Entries.Select(c => c with { Name = Path.Combine(sub.Name, c.Name) }));
            }
        }

        return new DirListing { Status = ReadStatus.Ok, Entries = entries.Take(max).ToList(), Truncated = entries.Count > max };
    }

    public LinesResult ReadLines(string path, LineFilter filter, long maxBytes, int maxLines)
    {
        if (DeniedTextNeedles.Any(n => path.Contains(n, StringComparison.OrdinalIgnoreCase)))
        {
            return new LinesResult { Status = ReadStatus.AccessDenied };
        }

        if (!Texts.TryGetValue(path, out var lines))
        {
            return new LinesResult { Status = Stats.ContainsKey(path) ? ReadStatus.Ok : ReadStatus.NotFound };
        }

        var kept = lines.Select(filter.Map).Where(l => l is not null).Select(l => l!).Take(maxLines).ToList();
        return new LinesResult { Status = ReadStatus.Ok, Lines = kept, LinesRead = lines.Length, ModifiedUtc = Stats.GetValueOrDefault(path)?.ModifiedUtc };
    }

    public HeaderResult ReadHeader(string path, int count) => new() { Status = ReadStatus.NotFound };

    public FileVersionResult GetVersion(string path) => Versions.TryGetValue(path, out var v) ? v : new FileVersionResult { Status = Stats.ContainsKey(path) ? ReadStatus.Ok : ReadStatus.NotFound };

    public ShortcutResult ReadShortcut(string path) => new() { Status = ReadStatus.Ok, TargetPath = @"C:\Program Files\Tool\tool.exe" };

    // ---- IEventLogSource ----
    public EventQueryResult Query(EventQuery query, CancellationToken cancellationToken)
    {
        if (EventLogStatus.TryGetValue(query.Log, out var status))
        {
            return new EventQueryResult { Status = status };
        }

        var records = EventRecords
            .Where(e => string.Equals(e.Log, query.Log, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Record)
            .Where(r => (query.Providers.Count == 0 || query.Providers.Contains(r.Provider, StringComparer.OrdinalIgnoreCase))
                        && (query.EventIds.Count == 0 || query.EventIds.Contains(r.Id))
                        && r.Utc >= query.SinceUtc)
            .OrderByDescending(r => r.Utc)
            .Select(r => r with { Data = r.Data.Where(kv => query.Fields.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) })
            .ToList();
        return new EventQueryResult { Status = ReadStatus.Ok, Records = records.Take(query.Max).ToList(), Truncated = records.Count > query.Max };
    }

    // ---- ISystemInfoSource ----
    public EnvRaw GetEnvironment() => Call("env", () => Env);

    public ProcessList GetProcesses() => Call("processes", () =>
    {
        ProcessCalls++;
        return Processes;
    });

    public ServiceList GetServices() => Call("services", () => Services);

    public DriverList GetLoadedDrivers() => Call("drivers", () => Drivers);

    public DeviceList GetDevices(IReadOnlyList<string> includePhantomClasses) => Call("devices", () => Devices);

    public FirmwareRaw GetFirmware(bool privileged) => Call("firmware", () => privileged ? Firmware : Firmware with { VariablesStatus = ReadStatus.AccessDenied, SetupMode = null, DbHasCa2023 = null, OsIndicationsSupported = null });

    public PowerRaw GetPower() => Call("power", () => Power);

    public MemoryRaw GetMemory() => Call("memory", () => Memory);

    public CpuRaw GetCpu() => Call("cpu", () => Cpu);

    public DisplayRaw GetDisplays() => Call("displays", () => Displays);

    public NetRaw GetNetwork() => Call("network", () => Network);

    public ConnectivityRaw GetConnectivity() => Call("connectivity", () => Connectivity);

    public WlanRaw GetWlan() => Call("wlan", () => Wlan);

    public TpmRaw GetTpm() => Call("tpm", () => Tpm);

    public WuaRaw GetWindowsUpdate() => Call("wua", () => Wua);

    public AudioDefaultsRaw GetAudioDefaults() => Call("audiodefaults", () => AudioDefaults);

    public BitLockerRaw GetBitLocker(string driveRoot) => Call("bitlocker", () => BitLockerRaw);

    // ---- INetworkProbeSource ----
    public ClockRaw MeasureClock(TimeSpan timeout) => Clock;

    public PingRaw Ping(string address, int count, TimeSpan timeout) => PingResult with { Sent = count };

    private T Call<T>(string name, Func<T> f)
    {
        OnSystemCall?.Invoke(name);
        return f();
    }

    [GeneratedRegex(@"\bFROM\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromClass();
}
