using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>Firmware type (no admin), boot-to-firmware support and dual-boot entries (admin: firmware variables, BCD).</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class FirmwareCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <inheritdoc />
    public override string Id => "firmware";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Hardware;

    /// <inheritdoc />
    public override CollectorNeeds Needs => CollectorNeeds.Admin;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var raw = env.Sources.System.GetFirmware(env.Elevated);
        var type = FirmwareTypeOf(env, raw);
        var info = new FirmwareInfo
        {
            Type = type is { } t ? run.Known(t, "GetFirmwareType") : run.Unknown<FirmwareType>(UnknownReason.Unsupported, "GetFirmwareType"),
            BootToFirmwareSupported = BootToFirmware(env, run, raw, type),
            OtherOsBootEntries = run.Try("bcd:fwbootmgr", () => OtherOs(env, run, type)),
        };
        return new SnapshotPatch { Firmware = info };
    }

    internal static FirmwareType? FirmwareTypeOf(ScanEnvironment env, FirmwareRaw raw) => raw.FirmwareType switch
    {
        2 => FirmwareType.Uefi,
        1 => FirmwareType.Bios,
        _ => env.Hklm(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State").Ok() ? FirmwareType.Uefi : null,
    };

    private static Fact<bool> BootToFirmware(ScanEnvironment env, RunBuilder run, FirmwareRaw raw, FirmwareType? type)
    {
        const string m = "firmware:OsIndicationsSupported";
        if (type == FirmwareType.Bios)
        {
            return run.NotApplicable<bool>(m, "legacy_bios");
        }

        if (!env.Elevated)
        {
            return run.Unknown<bool>(UnknownReason.NeedsAdmin, m, "not elevated");
        }

        if (raw.VariablesStatus != ReadStatus.Ok)
        {
            return run.FromStatus<bool>(env, raw.VariablesStatus, m);
        }

        return raw.OsIndicationsSupported is { } v ? run.Known((v & 1) != 0, m) : run.Unknown<bool>(UnknownReason.Unsupported, m, "variable_absent");
    }

    private static Fact<int> OtherOs(ScanEnvironment env, RunBuilder run, FirmwareType? type)
    {
        const string m = "bcd:fwbootmgr";
        if (type == FirmwareType.Bios)
        {
            return run.NotApplicable<int>(m, "legacy_bios");
        }

        if (!env.Elevated)
        {
            return run.Unknown<int>(UnknownReason.NeedsAdmin, m, "not elevated");
        }

        var bcd = env.Sources.WmiMethods.ReadBcd(TimeSpan.FromSeconds(6));
        if (bcd.Status != ReadStatus.Ok || !bcd.FirmwareEntriesRead)
        {
            return run.FromStatus<int>(env, bcd.Status == ReadStatus.Ok ? ReadStatus.Unsupported : bcd.Status, m);
        }

        var others = bcd.FirmwareEntryPaths.Count(p => p.Length > 0 && !p.EndsWith(@"\efi\microsoft\boot\bootmgfw.efi", StringComparison.OrdinalIgnoreCase));
        return run.Known(others, m);
    }
}
