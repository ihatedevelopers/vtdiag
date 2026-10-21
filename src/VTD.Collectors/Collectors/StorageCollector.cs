using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>Disks (MSFT_Disk/MSFT_PhysicalDisk, no serials), volumes (Win32_Volume), storage controllers (PnP).</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class StorageCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <summary>Intel VMD controller PCI device ids.</summary>
    internal static readonly string[] VmdDeviceIds = ["9A0B", "467F", "A77F", "7D0B", "AD0B", "09AB", "201D", "28C0", "476F", "A73F", "7E4D"];

    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    /// <inheritdoc />
    public override string Id => "storage";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Drivers;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var disksWmi = env.Sources.Wmi.Query(StorageNs, "SELECT Number, PartitionStyle, IsBoot, IsSystem, BusType, Size, FriendlyName, Model, Path FROM MSFT_Disk", TimeSpan.FromSeconds(8));
        var physical = env.Sources.Wmi.Query(StorageNs, "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk", TimeSpan.FromSeconds(8));
        var disks = run.Try("wmi:MSFT_PhysicalDisk", () => Disks(env, run, disksWmi, physical));
        var info = new StorageInfo
        {
            Disks = disks,
            SystemDiskPartitionStyle = disks.TryGetValue(out var list) && list.FirstOrDefault(d => d.IsSystemDisk) is { } sys
                ? run.Known(sys.Style, "wmi:MSFT_Disk")
                : disks.IsKnown ? run.Unknown<PartitionStyle>(UnknownReason.Unsupported, "wmi:MSFT_Disk", "no_boot_disk") : run.FromStatus<PartitionStyle>(env, disksWmi.Status, "wmi:MSFT_Disk"),
            Volumes = run.Try("wmi:Win32_Volume", () => Volumes(env, run)),
            Controllers = run.Try("pnp", () => Controllers(env, run, disksWmi)),
        };
        return new SnapshotPatch { Storage = info };
    }

    private static Fact<IReadOnlyList<DiskInfo>> Disks(ScanEnvironment env, RunBuilder run, WmiResult disks, WmiResult physical)
    {
        const string m = "wmi:MSFT_PhysicalDisk";
        if (disks.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<DiskInfo>>(env, disks.Status, "wmi:MSFT_Disk");
        }

        var media = physical.Status == ReadStatus.Ok
            ? physical.Rows.Where(r => Parse.Int(r.GetValueOrDefault("DeviceId")) is not null).ToDictionary(r => Parse.Int(r.GetValueOrDefault("DeviceId"))!.Value, r => r)
            : [];
        var list = new List<DiskInfo>();
        foreach (var row in disks.Rows)
        {
            if (Parse.Int(row.GetValueOrDefault("Number")) is not { } number)
            {
                continue;
            }

            media.TryGetValue(number, out var pd);
            var bus = Parse.Int(pd?.GetValueOrDefault("BusType") ?? row.GetValueOrDefault("BusType"));
            list.Add(new DiskInfo
            {
                Number = number,
                Model = (row.GetValueOrDefault("FriendlyName") ?? row.GetValueOrDefault("Model"))?.Trim(),
                Media = Parse.Int(pd?.GetValueOrDefault("MediaType")) switch { 3 => DiskMediaType.Hdd, 4 => DiskMediaType.Ssd, 5 => DiskMediaType.Scm, _ => DiskMediaType.Unknown },
                Bus = bus switch { 3 or 11 => DiskBusType.Sata, 17 => DiskBusType.Nvme, 7 => DiskBusType.Usb, 8 => DiskBusType.Raid, 10 => DiskBusType.Sas, 14 or 15 or 16 => DiskBusType.Virtual, _ => DiskBusType.Other },
                SizeGb = (Parse.Long(row.GetValueOrDefault("Size")) ?? 0) / (1024L * 1024 * 1024),
                Style = Parse.Int(row.GetValueOrDefault("PartitionStyle")) switch { 1 => PartitionStyle.Mbr, 2 => PartitionStyle.Gpt, _ => PartitionStyle.Raw },
                IsSystemDisk = Parse.Bool(row.GetValueOrDefault("IsBoot")) == true,
            });
        }

        if (physical.Status != ReadStatus.Ok)
        {
            run.Issue("unavailable:MSFT_PhysicalDisk");
        }

        return run.Known<IReadOnlyList<DiskInfo>>(list.OrderBy(d => d.Number).ToList(), m);
    }

    private static Fact<IReadOnlyList<VolumeInfo>> Volumes(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:Win32_Volume";
        var r = env.Sources.Wmi.Query(@"root\cimv2", "SELECT DriveLetter, FileSystem, Capacity, FreeSpace, DirtyBitSet, DriveType FROM Win32_Volume", TimeSpan.FromSeconds(8));
        if (r.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<VolumeInfo>>(env, r.Status, m);
        }

        var riotDrive = RiotLocator.Locate(env).RiotDrive;
        var list = new List<VolumeInfo>();
        foreach (var row in r.Rows)
        {
            var letter = row.GetValueOrDefault("DriveLetter");
            if (string.IsNullOrEmpty(letter) || Parse.Int(row.GetValueOrDefault("DriveType")) is not (3 or 2))
            {
                continue;
            }

            letter = letter.ToUpperInvariant();
            list.Add(new VolumeInfo
            {
                DriveLetter = letter,
                FileSystem = row.GetValueOrDefault("FileSystem"),
                SizeMb = (Parse.Long(row.GetValueOrDefault("Capacity")) ?? 0) / (1024 * 1024),
                FreeMb = (Parse.Long(row.GetValueOrDefault("FreeSpace")) ?? 0) / (1024 * 1024),
                Dirty = Parse.Bool(row.GetValueOrDefault("DirtyBitSet")),
                IsSystem = string.Equals(letter, env.SystemDrive, StringComparison.OrdinalIgnoreCase),
                HostsRiotGames = riotDrive is not null && string.Equals(letter, riotDrive, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (list.Count > 0 && list.All(v => v.Dirty is null))
        {
            run.Issue(env.Elevated ? "unavailable:DirtyBitSet" : "needs_admin:DirtyBitSet");
        }

        return run.Known<IReadOnlyList<VolumeInfo>>(list.OrderBy(v => v.DriveLetter, StringComparer.Ordinal).ToList(), m);
    }

    private static Fact<IReadOnlyList<StorageControllerInfo>> Controllers(ScanEnvironment env, RunBuilder run, WmiResult disks)
    {
        const string m = "pnp";
        var devices = env.Devices;
        if (devices.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<StorageControllerInfo>>(env, devices.Status, m);
        }

        var byId = devices.Items.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        var controllers = devices.Items
            .Where(d => d.Present && (string.Equals(d.ClassGuid, DeviceClasses.ScsiAdapter, StringComparison.OrdinalIgnoreCase) || string.Equals(d.ClassGuid, DeviceClasses.Hdc, StringComparison.OrdinalIgnoreCase)))
            .Where(d => d.Enumerator is "PCI" or "pci" || d.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var bootController = BootControllerId(disks, byId, controllers);
        var list = new List<StorageControllerInfo>();
        foreach (var d in controllers)
        {
            string? driverFile = null;
            if (d.Service is { Length: > 0 } svc)
            {
                var key = env.Hklm(@"SYSTEM\CurrentControlSet\Services\" + svc);
                var image = PathText.NormalizeImagePath(key.Str("ImagePath"), env.WindowsDir, env.Env.ProgramFiles);
                driverFile = image is null ? svc.ToLowerInvariant() + ".sys" : PathText.FileName(image).ToLowerInvariant();
            }

            var dev = HardwareCollector.PciPart(d.InstanceId, "DEV_");
            var ven = HardwareCollector.PciPart(d.InstanceId, "VEN_");
            var isVmd = (string.Equals(ven, "8086", StringComparison.OrdinalIgnoreCase) && dev is not null && VmdDeviceIds.Contains(dev, StringComparer.OrdinalIgnoreCase))
                        || string.Equals(d.Service, "iaStorVD", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(d.Service, "iaVROC", StringComparison.OrdinalIgnoreCase);
            list.Add(new StorageControllerInfo
            {
                InstanceId = d.InstanceId,
                Name = d.FriendlyName,
                DriverFile = driverFile,
                DriverVersion = d.DriverVersion,
                IsVmd = isVmd,
                IsBootController = bootController is not null && string.Equals(bootController, d.InstanceId, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (bootController is null)
        {
            run.Issue("unresolved:boot_controller");
        }

        return run.Known<IReadOnlyList<StorageControllerInfo>>(list, m);
    }

    private static string? BootControllerId(WmiResult disks, Dictionary<string, DeviceRec> byId, List<DeviceRec> controllers)
    {
        if (disks.Status != ReadStatus.Ok)
        {
            return null;
        }

        var boot = disks.Rows.FirstOrDefault(r => Parse.Bool(r.GetValueOrDefault("IsBoot")) == true);
        var path = boot?.GetValueOrDefault("Path");
        if (path is null)
        {
            return null;
        }

        var id = DevicePathToInstanceId(path);
        var ids = new HashSet<string>(controllers.Select(c => c.InstanceId), StringComparer.OrdinalIgnoreCase);
        for (var hop = 0; hop < 5 && id is not null; hop++)
        {
            if (ids.Contains(id))
            {
                return byId[id].InstanceId;
            }

            id = byId.TryGetValue(id, out var node) ? node.Parent : null;
        }

        return null;
    }

    /// <summary><c>\\?\scsi#disk&amp;ven_x#5&amp;1&amp;0#{guid}</c> to <c>SCSI\DISK&amp;VEN_X\5&amp;1&amp;0</c>.</summary>
    /// <param name="devicePath">Device interface path.</param>
    internal static string? DevicePathToInstanceId(string devicePath)
    {
        var p = devicePath;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        var guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0)
        {
            p = p[..guid];
        }

        return p.Length == 0 ? null : p.Replace('#', '\\');
    }
}
