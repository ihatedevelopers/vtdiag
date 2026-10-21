using System.Globalization;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>CPU, memory, GPUs (PnP + class keys), board and BIOS (SMBIOS registry copy; no serials anywhere).</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class HardwareCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <summary>AMD integrated GPU device ids (Radeon Graphics in Ryzen APUs / Ryzen 7000+ CPUs).</summary>
    internal static readonly HashSet<string> AmdIntegratedDeviceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "13C0", "164E", "15BF", "15C8", "1681", "15E7", "1636", "1638", "164C", "1506", "15DD", "15D8", "1900", "1901", "150E", "1114", "13FE",
    };

    /// <inheritdoc />
    public override string Id => "hardware";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Hardware;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var hw = new HardwareInfo
        {
            Cpu = run.Try("cpuid", () => Cpu(env, run)),
            Memory = run.Try("wmi:Win32_PhysicalMemory", () => Memory(env, run)),
            Gpus = run.Try("pnp+classkey", () => Gpus(env, run)),
            Board = run.Try("registry:HARDWARE\\BIOS", () => Board(env, run)),
        };
        return new SnapshotPatch { Hardware = hw };
    }

    private static Fact<CpuInfo> Cpu(ScanEnvironment env, RunBuilder run)
    {
        const string m = "cpuid";
        var raw = env.Sources.System.GetCpu();
        if (raw.Status != ReadStatus.Ok)
        {
            return run.FromStatus<CpuInfo>(env, raw.Status, m);
        }

        var reg = env.Hklm(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var vendorText = reg.Str("VendorIdentifier") ?? raw.CpuidVendor ?? string.Empty;
        var arch = raw.Architecture ?? "x64";
        var vendor = arch.StartsWith("arm", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Arm
            : vendorText.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Intel
            : vendorText.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Amd
            : CpuVendor.Other;
        var classes = raw.CoresByEfficiencyClass;
        var hybrid = classes.Count(c => c > 0) > 1;
        return run.Known(
            new CpuInfo
            {
                Vendor = vendor,
                Model = (reg.Str("ProcessorNameString") ?? string.Empty).Trim(),
                Architecture = arch,
                Cores = raw.Cores,
                LogicalProcessors = raw.Logical,
                Hybrid = hybrid,
                PerformanceCores = hybrid ? classes[^1] : null,
                EfficiencyCores = hybrid ? classes.Take(classes.Count - 1).Sum() : null,
                VirtualizationSupported = raw.CpuidAvailable ? raw.Vmx || raw.Svm || raw.HypervisorBit : null,
            },
            m);
    }

    private static Fact<MemoryInfo> Memory(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:Win32_PhysicalMemory";
        var mem = env.Sources.System.GetMemory();
        if (mem.Status != ReadStatus.Ok)
        {
            return run.FromStatus<MemoryInfo>(env, mem.Status, "GlobalMemoryStatusEx");
        }

        var modules = new List<MemoryModule>();
        var r = env.Sources.Wmi.Query(@"root\cimv2", "SELECT Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, Manufacturer, PartNumber FROM Win32_PhysicalMemory", TimeSpan.FromSeconds(6));
        if (r.Status == ReadStatus.Ok)
        {
            foreach (var row in r.Rows)
            {
                var cap = Parse.Long(row.GetValueOrDefault("Capacity"));
                if (cap is null or <= 0)
                {
                    continue;
                }

                modules.Add(new MemoryModule
                {
                    CapacityMb = cap.Value / (1024 * 1024),
                    RatedMts = Parse.Int(row.GetValueOrDefault("Speed")) is > 0 and var s ? s : null,
                    ConfiguredMts = Parse.Int(row.GetValueOrDefault("ConfiguredClockSpeed")) is > 0 and var c ? c : null,
                    Slot = Clean(row.GetValueOrDefault("DeviceLocator")),
                    Manufacturer = Clean(row.GetValueOrDefault("Manufacturer")),
                    PartNumber = Clean(row.GetValueOrDefault("PartNumber")),
                });
            }
        }
        else
        {
            run.Issue(env.ReasonOf(r.Status) == UnknownReason.NeedsAdmin ? "needs_admin:Win32_PhysicalMemory" : "failed:Win32_PhysicalMemory");
        }

        var total = modules.Count > 0 ? modules.Sum(x => x.CapacityMb) : mem.TotalBytes / (1024 * 1024);
        return run.Known(
            new MemoryInfo
            {
                TotalMb = total,
                AvailableMb = mem.AvailableBytes / (1024 * 1024),
                StandbyMb = mem.StandbyBytes is { } sb ? sb / (1024 * 1024) : null,
                Modules = modules,
            },
            m);
    }

    private static Fact<IReadOnlyList<GpuInfo>> Gpus(ScanEnvironment env, RunBuilder run)
    {
        const string m = "pnp+classkey";
        var devices = env.Devices;
        if (devices.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<GpuInfo>>(env, devices.Status, m);
        }

        var list = new List<GpuInfo>();
        foreach (var d in devices.Items.Where(d => d.Present && string.Equals(d.ClassGuid, DeviceClasses.Display, StringComparison.OrdinalIgnoreCase)))
        {
            var ven = PciPart(d.InstanceId, "VEN_");
            var dev = PciPart(d.InstanceId, "DEV_");
            var basic = string.Equals(d.Service, "BasicDisplay", StringComparison.OrdinalIgnoreCase) || string.Equals(ven, "1414", StringComparison.OrdinalIgnoreCase);
            var vendor = ven?.ToUpperInvariant() switch
            {
                "10DE" => GpuVendor.Nvidia,
                "1002" => GpuVendor.Amd,
                "8086" => GpuVendor.Intel,
                "1414" => GpuVendor.Microsoft,
                _ => basic ? GpuVendor.Microsoft : GpuVendor.Other,
            };
            long? vram = null;
            if (d.DriverKey is { Length: > 0 } dk)
            {
                var ck = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Class\" + dk);
                vram = ck.Num("HardwareInformation.qwMemorySize") ?? ck.Num("HardwareInformation.MemorySize") ?? BinaryDword(ck.Str("HardwareInformation.MemorySize"));
            }

            var vramMb = vram is > 0 ? vram / (1024 * 1024) : null;
            var integrated = vendor switch
            {
                GpuVendor.Intel => !IsIntelArc(dev),
                GpuVendor.Amd => (dev is not null && AmdIntegratedDeviceIds.Contains(dev)) || vramMb is < 4096,
                _ => false,
            };
            list.Add(new GpuInfo
            {
                InstanceId = d.InstanceId,
                Name = d.FriendlyName ?? "Display adapter",
                Vendor = vendor,
                PciVendorId = ven?.ToUpperInvariant(),
                PciDeviceId = dev?.ToUpperInvariant(),
                IsIntegrated = integrated,
                Enabled = d.ProblemCode != 22,
                ProblemCode = d.ProblemCode,
                DriverVersion = d.DriverVersion,
                DriverDate = d.DriverDate is { } dd ? DateOnly.FromDateTime(dd.UtcDateTime) : null,
                DriverProvider = d.DriverProvider,
                DedicatedVideoMb = vramMb,
                IsBasicDisplayAdapter = basic,
            });
        }

        return run.Known<IReadOnlyList<GpuInfo>>(list, m);
    }

    private static Fact<BoardInfo> Board(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:HARDWARE\\BIOS";
        var bios = env.Hklm(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (!bios.Ok())
        {
            return run.FromStatus<BoardInfo>(env, bios.Status, m);
        }

        DateOnly? release = DateOnly.TryParseExact(bios.Str("BIOSReleaseDate") ?? string.Empty, ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd) ? rd : null;
        bool? laptop = null;
        var chassis = env.Sources.Wmi.Query(@"root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure", TimeSpan.FromSeconds(5));
        if (chassis.Status == ReadStatus.Ok && chassis.Rows.Count > 0)
        {
            var types = Parse.IntList(chassis.Rows[0].GetValueOrDefault("ChassisTypes"));
            laptop = types.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32);
        }

        var power = env.Sources.System.GetPower();
        if (power.PowerStatusOk && power.BatteryFlag is not 128 and not 255)
        {
            laptop = true;
        }

        return run.Known(
            new BoardInfo
            {
                BoardManufacturer = Clean(bios.Str("BaseBoardManufacturer")),
                BoardProduct = Clean(bios.Str("BaseBoardProduct")),
                SystemManufacturer = Clean(bios.Str("SystemManufacturer")),
                SystemProduct = Clean(bios.Str("SystemProductName")),
                BiosVendor = Clean(bios.Str("BIOSVendor")),
                BiosVersion = Clean(bios.Str("BIOSVersion")),
                BiosReleaseDate = release,
                IsLaptop = laptop,
            },
            m);
    }

    internal static string? PciPart(string instanceId, string prefix)
    {
        var i = instanceId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + prefix.Length + 4 <= instanceId.Length ? instanceId.Substring(i + prefix.Length, 4) : null;
    }

    private static bool IsIntelArc(string? dev)
        => dev is not null && (dev.StartsWith("56", StringComparison.OrdinalIgnoreCase) || dev.StartsWith("E2", StringComparison.OrdinalIgnoreCase));

    private static long? BinaryDword(string? hex)
    {
        var b = Parse.Hex(hex);
        return b is { Length: >= 4 } ? BitConverter.ToUInt32(b, 0) : null;
    }

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) || string.Equals(t, "To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Default string", StringComparison.OrdinalIgnoreCase) ? null : t;
    }
}
