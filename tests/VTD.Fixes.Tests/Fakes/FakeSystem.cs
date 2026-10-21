using System.Security.Cryptography;
using VTD.Contracts;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Tests.Fakes;

/// <summary>
/// An in-memory PC behind every <see cref="SystemPorts"/> port: services, devices, network, registry, power, firewall,
/// Defender, BCD, processes, files, tools, System Protection and the boot clock. Nothing here touches the real machine.
/// Failure switches let tests break a single port call.
/// </summary>
public sealed class FakeSystem
{
    /// <summary>Creates an empty PC.</summary>
    /// <param name="clock">Clock (boot time, file times).</param>
    public FakeSystem(TimeProvider clock)
    {
        Clock = clock;
        Services = new FakeServices();
        Devices = new FakeDevices();
        Network = new FakeNetwork(Devices);
        Registry = new FakeRegistry();
        Power = new FakePower();
        Firewall = new FakeFirewall();
        Defender = new FakeDefender();
        Bcd = new FakeBcd();
        Processes = new FakeProcesses();
        Files = new FakeFiles();
        Tools = new FakeTools();
        SystemProtection = new FakeSystemProtection();
        Boot = new FakeBoot { BootUtc = clock.GetUtcNow().AddHours(-2) };
    }

    /// <summary>Clock.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Services.</summary>
    public FakeServices Services { get; }

    /// <summary>Devices.</summary>
    public FakeDevices Devices { get; }

    /// <summary>Network.</summary>
    public FakeNetwork Network { get; }

    /// <summary>Registry.</summary>
    public FakeRegistry Registry { get; }

    /// <summary>Power.</summary>
    public FakePower Power { get; }

    /// <summary>Firewall.</summary>
    public FakeFirewall Firewall { get; }

    /// <summary>Defender.</summary>
    public FakeDefender Defender { get; }

    /// <summary>BCD.</summary>
    public FakeBcd Bcd { get; }

    /// <summary>Processes.</summary>
    public FakeProcesses Processes { get; }

    /// <summary>Files.</summary>
    public FakeFiles Files { get; }

    /// <summary>Tools.</summary>
    public FakeTools Tools { get; }

    /// <summary>System Protection.</summary>
    public FakeSystemProtection SystemProtection { get; }

    /// <summary>Boot clock.</summary>
    public FakeBoot Boot { get; }

    /// <summary>The port set.</summary>
    public SystemPorts Ports => new()
    {
        Services = Services,
        Devices = Devices,
        Network = Network,
        Registry = Registry,
        Power = Power,
        Firewall = Firewall,
        Defender = Defender,
        Bcd = Bcd,
        Processes = Processes,
        Files = Files,
        Tools = Tools,
        SystemProtection = SystemProtection,
        Boot = Boot,
    };

    /// <summary>A whole-system state hash (every port's content), for "nothing changed" assertions.</summary>
    public string Fingerprint() => CanonicalHash.OfText(string.Join("\n", Services.Dump(), Devices.Dump(), Network.Dump(), Registry.Dump(), Power.Dump(), Firewall.Dump(), Defender.Dump(), Bcd.Dump(), Processes.Dump(), Files.Dump(), SystemProtection.Dump()));

    /// <summary>A typical gaming PC with the entities the KB fixes act on.</summary>
    /// <param name="clock">Clock.</param>
    public static FakeSystem Typical(TimeProvider clock)
    {
        var s = new FakeSystem(clock);
        s.Services.Add(new ServiceInfo { Name = "NahimicService", StartType = ServiceStart.Automatic, Delayed = false, State = ServiceRun.Running, ImagePath = @"C:\Windows\System32\NahimicService.exe" });
        s.Services.Add(new ServiceInfo { Name = "RtkAudioUniversalService", StartType = ServiceStart.Automatic, Delayed = true, State = ServiceRun.Running });
        s.Services.Add(new ServiceInfo { Name = "vgc", StartType = ServiceStart.Manual, Delayed = false, State = ServiceRun.Stopped });
        s.Services.Add(new ServiceInfo { Name = "vgk", StartType = ServiceStart.System, Delayed = false, State = ServiceRun.Stopped, IsDriver = true });
        s.Services.Add(new ServiceInfo { Name = "WSearch", StartType = ServiceStart.Automatic, Delayed = true, State = ServiceRun.Running, IsSharedProcess = true });
        s.Services.Add(new ServiceInfo { Name = "iaStorAVC", StartType = ServiceStart.Boot, Delayed = false, State = ServiceRun.Running, IsDriver = true });
        s.Services.Add(new ServiceInfo { Name = "WinRing0_1_2_0", StartType = ServiceStart.Manual, Delayed = false, State = ServiceRun.Stopped, IsDriver = true });
        s.Services.Add(new ServiceInfo { Name = "RemoteRegistry", StartType = ServiceStart.Disabled, Delayed = false, State = ServiceRun.Stopped });

        // Device tree: PCI root → NVMe controller → disk; PCI root → GPU; USB host → hub → mouse HID; NICs; BT radio → HFP.
        s.Devices.Add("ACPI\\PNP0A08\\0", "{4d36e97d-e325-11ce-bfc1-08002be10318}", null);
        s.Devices.Add("PCI\\VEN_144D&DEV_A808\\4&1", "{4d36e97b-e325-11ce-bfc1-08002be10318}", "ACPI\\PNP0A08\\0");
        s.Devices.Add("SCSI\\DISK&VEN_NVME\\5&1", "{4d36e967-e325-11ce-bfc1-08002be10318}", "PCI\\VEN_144D&DEV_A808\\4&1");
        s.Devices.Add("PCI\\VEN_10DE&DEV_2684\\4&2", "{4d36e968-e325-11ce-bfc1-08002be10318}", "ACPI\\PNP0A08\\0");
        s.Devices.Add("PCI\\VEN_8086&DEV_7A60\\3&1", "{36fc9e60-c465-11cf-8056-444553540000}", "ACPI\\PNP0A08\\0");
        s.Devices.Add("USB\\VID_046D&PID_C539\\6&1", "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}", "PCI\\VEN_8086&DEV_7A60\\3&1");
        s.Devices.Add("PCI\\VEN_10EC&DEV_8168\\R3ECEB671D921", ProtectedClassNet, "ACPI\\PNP0A08\\0", driverKey: "{4d36e972-e325-11ce-bfc1-08002be10318}\\0001");
        s.Devices.Add("PCI\\VEN_10EC&DEV_8125\\01000000684CE00000", ProtectedClassNet, "ACPI\\PNP0A08\\0", driverKey: "{4d36e972-e325-11ce-bfc1-08002be10318}\\0002");
        s.Devices.Add("PCI\\VEN_8086&DEV_2725\\WIFI1", ProtectedClassNet, "ACPI\\PNP0A08\\0", driverKey: "{4d36e972-e325-11ce-bfc1-08002be10318}\\0003");
        s.Devices.Add("USB\\VID_8087&PID_0032\\BT1", "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", "PCI\\VEN_8086&DEV_7A60\\3&1");
        s.Devices.Add("BTHHFENUM\\BTHHFPAUDIO\\8&HFP1", "{4d36e96c-e325-11ce-bfc1-08002be10318}", "USB\\VID_8087&PID_0032\\BT1");
        s.Network.Map("PCI\\VEN_10EC&DEV_8168\\R3ECEB671D921", new Guid("634bd4ce-78ab-4cc3-9bad-a142370ed57f"), defaultRoute: true, metric: new InterfaceMetric(25, true), ipv6: true);
        s.Network.Map("PCI\\VEN_10EC&DEV_8125\\01000000684CE00000", new Guid("11111111-2222-3333-4444-555555555555"), defaultRoute: false, metric: new InterfaceMetric(5, false), ipv6: true);
        s.Network.Map("PCI\\VEN_8086&DEV_2725\\WIFI1", new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), defaultRoute: false, metric: new InterfaceMetric(35, true), ipv6: true);

        // Realtek driver keys declare the power-saving keywords.
        foreach (var key in new[] { "0001", "0002" })
        {
            var baseKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\" + key;
            s.Registry.SetString(RegHive.Machine, baseKey, "NetCfgInstanceId", key == "0001" ? "{634BD4CE-78AB-4CC3-9BAD-A142370ED57F}" : "{11111111-2222-3333-4444-555555555555}");
            foreach (var kw in new[] { "*EEE", "EnableGreenEthernet", "AdvancedEEE" })
            {
                s.Registry.SetString(RegHive.Machine, baseKey, kw, "1");
                s.Registry.SetString(RegHive.Machine, baseKey + @"\Ndi\params\" + kw, "type", "enum");
                s.Registry.SetString(RegHive.Machine, baseKey + @"\Ndi\params\" + kw + @"\enum", "0", "Disabled");
                s.Registry.SetString(RegHive.Machine, baseKey + @"\Ndi\params\" + kw + @"\enum", "1", "Enabled");
            }
        }

        s.Registry.SetDword(RegHive.User, @"System\GameConfigStore", "GameDVR_Enabled", 1);
        s.Registry.SetDword(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2);
        s.Registry.SetDword(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1);
        s.Registry.SetString(RegHive.User, @"Control Panel\Mouse", "MouseSpeed", "1");
        s.Registry.SetString(RegHive.User, @"Control Panel\Mouse", "MouseThreshold1", "6");
        s.Registry.SetString(RegHive.User, @"Control Panel\Mouse", "MouseThreshold2", "10");
        s.Registry.SetString(RegHive.User, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "~ WIN7RTM");
        s.Registry.Set(RegHive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT-Win64-Shipping.exe", "MitigationOptions", new RegValue(RegValue.Binary, [0x00, 0x01, 0x02, 0x03, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
        s.Registry.SetDword(RegHive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT-Win64-Shipping.exe\PerfOptions", "CpuPriorityClass", 3);
        s.Registry.SetString(RegHive.User, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Discord", "\"C:\\Users\\p\\AppData\\Local\\Discord\\Update.exe\" --processStart Discord.exe");
        s.Registry.SetString(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Riot Vanguard", "\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\"");
        s.Registry.SetString(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "RtkAudUService", "\"C:\\Windows\\System32\\RtkAudUService64.exe\" -background");

        s.Power.Schemes.AddRange([Balanced, HighPerformance]);
        s.Power.Active = Balanced;
        s.Power.Values[(Balanced, ProcessorSubgroup, ProcessorMax, true)] = 80;
        s.Power.Values[(Balanced, ProcessorSubgroup, ProcessorMax, false)] = 80;
        s.Power.Values[(Balanced, UsbSubgroup, UsbSuspend, true)] = 1;
        s.Power.Values[(Balanced, UsbSubgroup, UsbSuspend, false)] = 1;

        s.Processes.Add(4100, "RTSS.exe", 1, windows: 2);
        s.Processes.Add(4101, "Overwolf.exe", 1, windows: 1);
        s.Processes.Add(4102, "Stubborn.exe", 1, windows: 1, closes: false);
        s.Processes.Add(4103, "NahimicSvc64.exe", 0, windows: 0);
        s.Processes.Add(4200, RiotProcessNames.RiotClientServices, 1, windows: 1);

        s.Files.AddDirectory(@"C:\Windows");
        s.Files.AddDirectory(@"C:\Program Files\Riot Vanguard");
        s.Files.AddDirectory(@"C:\Riot Games\VALORANT");
        s.Files.Add(@"C:\Windows\392667600.dat", [1, 2, 3, 4, 5], s.Boot.BootUtc!.Value.AddDays(-3));
        s.Files.Add(@"C:\Program Files\Riot Vanguard\vgkbootstatus.dat", [9, 9], s.Boot.BootUtc!.Value.AddDays(-1));
        return s;
    }

    /// <summary>Net class.</summary>
    public const string ProtectedClassNet = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Balanced scheme.</summary>
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>High performance scheme.</summary>
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>Processor subgroup.</summary>
    public static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Processor max state.</summary>
    public static readonly Guid ProcessorMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    /// <summary>USB subgroup.</summary>
    public static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");

    /// <summary>USB selective suspend.</summary>
    public static readonly Guid UsbSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
}

/// <summary>Fake SCM.</summary>
public sealed class FakeServices : IServicePort
{
    private readonly Dictionary<string, ServiceInfo> _services = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calls that changed something.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>When set, writes to this service throw.</summary>
    public string? FailWritesFor { get; set; }

    /// <summary>When set, stopping this service throws.</summary>
    public string? FailStopFor { get; set; }

    /// <summary>When set, a stop waits for this gate (to hold an apply in the middle of a step).</summary>
    public TaskCompletionSource? StopGate { get; set; }

    /// <summary>Services that refuse to start while Disabled (like the real SCM).</summary>
    public bool EnforceDisabled { get; set; } = true;

    /// <summary>Adds a service.</summary>
    /// <param name="info">Service.</param>
    public void Add(ServiceInfo info) => _services[info.Name] = info;

    /// <summary>Changes a service behind VTDiag's back.</summary>
    /// <param name="name">Name.</param>
    /// <param name="change">Change.</param>
    public void Mutate(string name, Func<ServiceInfo, ServiceInfo> change) => _services[name] = change(_services[name]);

    /// <inheritdoc />
    public ServiceInfo? Query(string name) => _services.TryGetValue(name, out var s) ? s : null;

    /// <inheritdoc />
    public void SetStartType(string name, ServiceStart startType, bool delayed)
    {
        Fail(name);
        _services[name] = Get(name) with { StartType = startType, Delayed = delayed };
        Writes.Add("start:" + name);
    }

    /// <inheritdoc />
    public async Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Fail(name);
        if (string.Equals(FailStopFor, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.ComponentModel.Win32Exception(1051);
        }

        if (StopGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        _services[name] = Get(name) with { State = ServiceRun.Stopped };
        Writes.Add("stop:" + name);
    }

    /// <inheritdoc />
    public Task StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Fail(name);
        var s = Get(name);
        if (EnforceDisabled && s.StartType == ServiceStart.Disabled)
        {
            throw new System.ComponentModel.Win32Exception(1058);
        }

        _services[name] = s with { State = ServiceRun.Running };
        Writes.Add("run:" + name);
        return Task.CompletedTask;
    }

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", _services.Values.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => $"{s.Name}={s.StartType}/{s.Delayed}/{s.State}"));

    private ServiceInfo Get(string name) => _services.TryGetValue(name, out var s) ? s : throw new System.ComponentModel.Win32Exception(1060);

    private void Fail(string name)
    {
        if (string.Equals(FailWritesFor, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.ComponentModel.Win32Exception(5);
        }
    }
}

/// <summary>Fake device tree.</summary>
public sealed class FakeDevices : IDevicePort
{
    private readonly Dictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Restart calls.</summary>
    public List<string> Restarts { get; } = [];

    /// <summary>When set, every disable reports that a reboot is needed.</summary>
    public bool DisableNeedsReboot { get; set; }

    /// <summary>When set, enumeration throws (the policy must deny).</summary>
    public bool FailEnumeration { get; set; }

    /// <summary>Adds a present, enabled device.</summary>
    /// <param name="id">Instance id.</param>
    /// <param name="classGuid">Class.</param>
    /// <param name="parent">Parent id.</param>
    /// <param name="driverKey">Driver key.</param>
    public void Add(string id, string classGuid, string? parent, string? driverKey = null)
        => _devices[id] = new DeviceInfo { InstanceId = id, Present = true, Enabled = true, ProblemCode = 0, ClassGuid = classGuid, ParentInstanceId = parent, DriverKey = driverKey };

    /// <summary>Changes a device behind VTDiag's back.</summary>
    /// <param name="id">Instance id.</param>
    /// <param name="change">Change.</param>
    public void Mutate(string id, Func<DeviceInfo, DeviceInfo> change) => _devices[id] = change(_devices[id]);

    /// <inheritdoc />
    public DeviceInfo? Query(string instanceId) => _devices.TryGetValue(instanceId, out var d) ? d : null;

    /// <inheritdoc />
    public IReadOnlyList<DeviceInfo> EnumeratePresent()
        => FailEnumeration ? throw new IOException("enumeration failed") : [.. _devices.Values.Where(d => d.Present)];

    /// <inheritdoc />
    public DeviceChangeResult Disable(string instanceId)
    {
        _devices[instanceId] = Get(instanceId) with { Enabled = false, ProblemCode = 22 };
        return new DeviceChangeResult(DisableNeedsReboot);
    }

    /// <inheritdoc />
    public DeviceChangeResult Enable(string instanceId)
    {
        _devices[instanceId] = Get(instanceId) with { Enabled = true, ProblemCode = 0 };
        return new DeviceChangeResult(false);
    }

    /// <inheritdoc />
    public DeviceChangeResult Restart(string instanceId)
    {
        _ = Get(instanceId);
        Restarts.Add(instanceId);
        return new DeviceChangeResult(false);
    }

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", _devices.Values.OrderBy(d => d.InstanceId, StringComparer.Ordinal).Select(d => $"{d.InstanceId}={d.Present}/{d.Enabled}/{d.ProblemCode}"));

    private DeviceInfo Get(string id) => _devices.TryGetValue(id, out var d) ? d : throw new IOException("no such device");
}

/// <summary>Fake network configuration.</summary>
public sealed class FakeNetwork(FakeDevices devices) : INetworkPort
{
    private readonly Dictionary<string, Guid> _guids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _defaultRoutes = [];
    private readonly Dictionary<Guid, InterfaceMetric> _metrics = [];
    private readonly Dictionary<Guid, bool> _ipv6 = [];
    private readonly Dictionary<Guid, int> _autoMetrics = [];

    /// <summary>When set, route lookups report Unknown.</summary>
    public bool RouteUnknown { get; set; }

    /// <summary>Maps an adapter.</summary>
    /// <param name="instanceId">Instance id.</param>
    /// <param name="guid">Interface GUID.</param>
    /// <param name="defaultRoute">Carries the default route.</param>
    /// <param name="metric">IPv4 metric.</param>
    /// <param name="ipv6">IPv6 bound.</param>
    public void Map(string instanceId, Guid guid, bool defaultRoute, InterfaceMetric metric, bool ipv6)
    {
        _guids[instanceId] = guid;
        if (defaultRoute)
        {
            _defaultRoutes.Add(guid);
        }

        _metrics[guid] = metric;
        _autoMetrics[guid] = metric.Automatic ? metric.Metric : 25; // what Windows computes from the link speed
        _ipv6[guid] = ipv6;
    }

    /// <summary>Moves the default route.</summary>
    /// <param name="instanceId">Adapter.</param>
    /// <param name="carries">Carries it.</param>
    public void SetDefaultRoute(string instanceId, bool carries)
    {
        if (carries)
        {
            _defaultRoutes.Add(_guids[instanceId]);
        }
        else
        {
            _defaultRoutes.Remove(_guids[instanceId]);
        }
    }

    /// <inheritdoc />
    public Guid? InterfaceGuidOf(string instanceId) => devices.Query(instanceId) is not null && _guids.TryGetValue(instanceId, out var g) ? g : null;

    /// <inheritdoc />
    public Tri CarriesDefaultRoute(Guid interfaceGuid) => RouteUnknown ? Tri.Unknown : _defaultRoutes.Contains(interfaceGuid) ? Tri.True : Tri.False;

    /// <inheritdoc />
    public InterfaceMetric? GetIpv4Metric(Guid interfaceGuid) => _metrics.TryGetValue(interfaceGuid, out var m) ? m : null;

    /// <inheritdoc />
    public void SetIpv4Metric(Guid interfaceGuid, bool automatic, int metric)
        => _metrics[interfaceGuid] = automatic ? new InterfaceMetric(_autoMetrics.GetValueOrDefault(interfaceGuid, 25), true) : new InterfaceMetric(metric, false);

    /// <inheritdoc />
    public bool? GetIpv6Bound(Guid interfaceGuid) => _ipv6.TryGetValue(interfaceGuid, out var b) ? b : null;

    /// <inheritdoc />
    public void SetIpv6Bound(Guid interfaceGuid, bool bound) => _ipv6[interfaceGuid] = bound;

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", _metrics.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value.Metric}/{m.Value.Automatic}/{_ipv6[m.Key]}/{_defaultRoutes.Contains(m.Key)}"));
}

/// <summary>Fake registry (both hives; HKCU means the player's hive).</summary>
public sealed class FakeRegistry : IRegistryPort
{
    private readonly Dictionary<string, Dictionary<string, (string Name, RegValue Value)>> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, writes under keys containing this text throw (Tamper Protection, policy lock).</summary>
    public string? FailWritesUnder { get; set; }

    /// <summary>When set, writes under keys containing this text are silently reverted (a value Windows resets).</summary>
    public string? SilentlyRevertUnder { get; set; }

    /// <summary>Writes performed.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Sets a REG_SZ.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="key">Key.</param>
    /// <param name="name">Name.</param>
    /// <param name="text">Text.</param>
    public void SetString(RegHive hive, string key, string name, string text) => Set(hive, key, name, VTD.Fixes.Ops.RegCodec.Encode("String", text)!);

    /// <summary>Sets a REG_DWORD.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="key">Key.</param>
    /// <param name="name">Name.</param>
    /// <param name="value">Value.</param>
    public void SetDword(RegHive hive, string key, string name, uint value) => Set(hive, key, name, new RegValue(RegValue.DWord, BitConverter.GetBytes(value)));

    /// <summary>Sets a raw value (test setup; not counted).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="key">Key.</param>
    /// <param name="name">Name.</param>
    /// <param name="value">Value.</param>
    public void Set(RegHive hive, string key, string name, RegValue value)
    {
        var k = Key(hive, key);
        if (!_keys.TryGetValue(k, out var values))
        {
            _keys[k] = values = new Dictionary<string, (string, RegValue)>(StringComparer.OrdinalIgnoreCase);
        }

        values[name] = (name, value);
        EnsureParents(hive, key);
    }

    /// <summary>Removes a value (test setup).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="key">Key.</param>
    /// <param name="name">Name.</param>
    public void Remove(RegHive hive, string key, string name)
    {
        if (_keys.TryGetValue(Key(hive, key), out var values))
        {
            values.Remove(name);
        }
    }

    /// <inheritdoc />
    public RegValue? GetValue(RegHive hive, string subKey, string name)
        => _keys.TryGetValue(Key(hive, subKey), out var values) && values.TryGetValue(name, out var v) ? v.Value with { Data = (byte[])v.Value.Data.Clone() } : null;

    /// <inheritdoc />
    public bool KeyExists(RegHive hive, string subKey) => _keys.ContainsKey(Key(hive, subKey));

    /// <inheritdoc />
    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string subKey)
    {
        var prefix = Key(hive, subKey) + "\\";
        return [.. _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0).Select(k => k[prefix.Length..])];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetValueNames(RegHive hive, string subKey)
        => _keys.TryGetValue(Key(hive, subKey), out var values) ? [.. values.Values.Select(v => v.Name)] : [];

    /// <inheritdoc />
    public void SetValue(RegHive hive, string subKey, string name, RegValue value)
    {
        if (FailWritesUnder is { } f && subKey.Contains(f, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("blocked");
        }

        WriteCount++;
        if (SilentlyRevertUnder is { } r && subKey.Contains(r, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Set(hive, subKey, name, value with { Data = (byte[])value.Data.Clone() });
    }

    /// <inheritdoc />
    public bool DeleteValue(RegHive hive, string subKey, string name)
    {
        if (FailWritesUnder is { } f && subKey.Contains(f, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("blocked");
        }

        WriteCount++;
        return _keys.TryGetValue(Key(hive, subKey), out var values) && values.Remove(name);
    }

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", _keys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
        .SelectMany(k => k.Value.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(v => k.Key.ToUpperInvariant() + "|" + v.Name.ToUpperInvariant() + "=" + v.Value.Type + ":" + Convert.ToHexString(v.Value.Data))));

    private static string Key(RegHive hive, string key) => (hive == RegHive.Machine ? "HKLM\\" : "HKU\\") + key;

    private void EnsureParents(RegHive hive, string key)
    {
        var parts = key.Split('\\');
        for (var i = 1; i < parts.Length; i++)
        {
            var k = Key(hive, string.Join('\\', parts[..i]));
            if (!_keys.ContainsKey(k))
            {
                _keys[k] = new Dictionary<string, (string, RegValue)>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}

/// <summary>Fake power schemes.</summary>
public sealed class FakePower : IPowerPort
{
    /// <summary>Installed schemes.</summary>
    public List<Guid> Schemes { get; } = [];

    /// <summary>Active scheme.</summary>
    public Guid Active { get; set; }

    /// <summary>Values.</summary>
    public Dictionary<(Guid Scheme, Guid Sub, Guid Setting, bool Ac), uint> Values { get; } = [];

    /// <summary>Activation calls.</summary>
    public int Activations { get; private set; }

    /// <inheritdoc />
    public Guid GetActiveScheme() => Active;

    /// <inheritdoc />
    public IReadOnlyList<Guid> EnumerateSchemes() => Schemes;

    /// <inheritdoc />
    public uint? ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac) => Values.TryGetValue((scheme, subgroup, setting, ac), out var v) ? v : null;

    /// <inheritdoc />
    public void WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value) => Values[(scheme, subgroup, setting, ac)] = value;

    /// <inheritdoc />
    public void SetActiveScheme(Guid scheme)
    {
        if (!Schemes.Contains(scheme))
        {
            throw new IOException("no such scheme");
        }

        Active = scheme;
        Activations++;
    }

    /// <summary>State dump.</summary>
    public string Dump() => Active + ";" + string.Join(";", Values.OrderBy(v => v.Key.ToString(), StringComparer.Ordinal).Select(v => v.Key + "=" + v.Value));
}

/// <summary>Fake firewall.</summary>
public sealed class FakeFirewall : IFirewallPort
{
    /// <summary>Rules by name.</summary>
    public Dictionary<string, FirewallRuleInfo> Rules { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public FirewallRuleInfo? GetRule(string name) => Rules.TryGetValue(name, out var r) ? r : null;

    /// <inheritdoc />
    public void AddRule(FirewallRuleInfo rule) => Rules[rule.Name] = rule;

    /// <inheritdoc />
    public void RemoveRule(string name) => Rules.Remove(name);

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", Rules.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => r.ToString()));
}

/// <summary>Fake Defender.</summary>
public sealed class FakeDefender : IDefenderPort
{
    /// <summary>Exclusions.</summary>
    public HashSet<string> Exclusions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tamper Protection: writes are silently ignored.</summary>
    public bool TamperProtected { get; set; }

    /// <inheritdoc />
    public bool IsExcluded(string path) => Exclusions.Contains(path);

    /// <inheritdoc />
    public void AddExclusion(string path)
    {
        if (!TamperProtected)
        {
            Exclusions.Add(path);
        }
    }

    /// <inheritdoc />
    public void RemoveExclusion(string path) => Exclusions.Remove(path);

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", Exclusions.Order(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Fake BCD.</summary>
public sealed class FakeBcd : IBcdPort
{
    /// <summary>Elements.</summary>
    public Dictionary<BcdElement, string> Elements { get; } = [];

    /// <inheritdoc />
    public string? Get(BcdElement element) => Elements.TryGetValue(element, out var v) ? v : null;

    /// <inheritdoc />
    public void Set(BcdElement element, string value) => Elements[element] = value;

    /// <inheritdoc />
    public void Delete(BcdElement element) => Elements.Remove(element);

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", Elements.OrderBy(e => e.Key).Select(e => e.Key + "=" + e.Value));
}

/// <summary>Fake processes: a process exits on WM_CLOSE unless told otherwise.</summary>
public sealed class FakeProcesses : IProcessPort
{
    private readonly List<(ProcessEntry Entry, int Windows, bool Closes)> _list = [];

    /// <summary>Console session.</summary>
    public int ConsoleSession { get; set; } = 1;

    /// <summary>WM_CLOSE posts.</summary>
    public List<int> Posted { get; } = [];

    /// <summary>Adds a process.</summary>
    /// <param name="pid">PID.</param>
    /// <param name="image">Image.</param>
    /// <param name="session">Session.</param>
    /// <param name="windows">Top-level windows.</param>
    /// <param name="closes">Exits on WM_CLOSE.</param>
    public void Add(int pid, string image, int session, int windows, bool closes = true) => _list.Add((new ProcessEntry(pid, image, session), windows, closes));

    /// <inheritdoc />
    public IReadOnlyList<ProcessEntry> Snapshot() => [.. _list.Select(p => p.Entry)];

    /// <inheritdoc />
    public int ActiveConsoleSessionId() => ConsoleSession;

    /// <inheritdoc />
    public int PostClose(int processId)
    {
        var i = _list.FindIndex(p => p.Entry.ProcessId == processId);
        if (i < 0)
        {
            return 0;
        }

        var p = _list[i];
        Posted.Add(processId);
        if (p.Windows > 0 && p.Closes)
        {
            _list.RemoveAt(i);
        }

        return p.Windows;
    }

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", _list.Select(p => p.Entry.ProcessId + "=" + p.Entry.ImageName));
}

/// <summary>Fake file system with reparse points and owners modelled as flags.</summary>
public sealed class FakeFiles : IFilePort
{
    private readonly Dictionary<string, (byte[] Content, DateTimeOffset LastWrite)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths (files or folders) that are reparse points: any move through them is refused.</summary>
    public HashSet<string> ReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Manifests written.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Manifests { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Moves performed.</summary>
    public List<(string From, string To)> Moves { get; } = [];

    /// <inheritdoc />
    public KnownFolders Folders { get; } = new() { Windows = @"C:\Windows", ProgramFiles = @"C:\Program Files", ProgramFilesX86 = @"C:\Program Files (x86)", ProgramData = @"C:\ProgramData" };

    /// <inheritdoc />
    public string QuarantineRoot => @"C:\ProgramData\VTDiag\quarantine";

    /// <summary>Adds a file.</summary>
    /// <param name="path">Path.</param>
    /// <param name="content">Content.</param>
    /// <param name="lastWrite">Last write.</param>
    public void Add(string path, byte[] content, DateTimeOffset lastWrite)
    {
        _files[path] = (content, lastWrite);
        AddDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>Adds a folder (and its parents).</summary>
    /// <param name="path">Path.</param>
    public void AddDirectory(string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            _dirs.Add(p);
        }
    }

    /// <summary>Deletes a file (test setup: simulates someone else removing it).</summary>
    /// <param name="path">Path.</param>
    public void Remove(string path) => _files.Remove(path);

    /// <inheritdoc />
    public FileFacts Probe(string path)
        => _files.TryGetValue(path, out var f) && !ReparsePoints.Contains(path)
            ? new FileFacts { Exists = true, Size = f.Content.Length, LastWriteUtc = f.LastWrite }
            : new FileFacts { Exists = false };

    /// <inheritdoc />
    public string Sha256(string path) => _files.TryGetValue(path, out var f) ? Convert.ToHexStringLower(SHA256.HashData(f.Content)) : throw new FileNotFoundException(path);

    /// <inheritdoc />
    public void SafeMove(string source, string destination, string expectedSha256)
    {
        foreach (var p in Ancestors(source).Concat(Ancestors(destination)).Append(source))
        {
            if (ReparsePoints.Contains(p))
            {
                throw new PortRefusedException("file.reparse_point");
            }
        }

        if (!_files.TryGetValue(source, out var f))
        {
            throw new FileNotFoundException(source);
        }

        if (!string.Equals(Sha256(source), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortRefusedException("file.hash_mismatch");
        }

        if (_files.ContainsKey(destination))
        {
            throw new PortRefusedException("file.destination_exists");
        }

        AddDirectory(Path.GetDirectoryName(destination)!);
        _files.Remove(source);
        _files[destination] = f;
        Moves.Add((source, destination));
    }

    /// <inheritdoc />
    public void WriteManifest(string folder, IReadOnlyDictionary<string, string> manifest) => Manifests[folder] = manifest;

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _dirs.Contains(path) && !ReparsePoints.Contains(path);

    /// <inheritdoc />
    public IReadOnlyList<string> ListDirectories(string path)
        => [.. _dirs.Where(d => string.Equals(Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase) && !ReparsePoints.Contains(d))];

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", _files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => f.Key.ToUpperInvariant() + "=" + Convert.ToHexString(f.Value.Content)));

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var p = Path.GetDirectoryName(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            yield return p;
        }
    }
}

/// <summary>Fake tool runner.</summary>
public sealed class FakeTools : IToolRunner
{
    /// <summary>Runs.</summary>
    public List<SystemTool> Runs { get; } = [];

    /// <summary>Exit codes (default 0).</summary>
    public Dictionary<SystemTool, int> ExitCodes { get; } = [];

    /// <summary>Called after a run (for example to fix the clock skew).</summary>
    public Action<SystemTool>? OnRun { get; set; }

    /// <inheritdoc />
    public Task<int> RunAsync(SystemTool tool, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Runs.Add(tool);
        OnRun?.Invoke(tool);
        return Task.FromResult(ExitCodes.TryGetValue(tool, out var c) ? c : 0);
    }
}

/// <summary>Fake System Protection.</summary>
public sealed class FakeSystemProtection : ISystemProtectionPort
{
    /// <summary>Protected drives.</summary>
    public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Max size.</summary>
    public int? Percent { get; set; } = 5;

    /// <inheritdoc />
    public bool IsEnabled(string drive) => Enabled.Contains(drive);

    /// <inheritdoc />
    public int? MaxSizePercent() => Percent;

    /// <inheritdoc />
    public void Enable(string drive) => Enabled.Add(drive);

    /// <summary>State dump.</summary>
    public string Dump() => string.Join(";", Enabled.Order(StringComparer.OrdinalIgnoreCase)) + "/" + Percent;
}

/// <summary>Fake boot clock.</summary>
public sealed class FakeBoot : IBootClock
{
    /// <summary>Boot time.</summary>
    public DateTimeOffset? BootUtc { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? LastBootUtc() => BootUtc;
}
