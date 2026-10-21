using System.Text.Json.Serialization;
using VTD.Platform.Sources;

namespace VTD.Platform.Capture;

/// <summary>Source-generated JSON for raw capture payloads (owned by A1; not part of the VTDiag contract).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(RegKeyData))]
[JsonSerializable(typeof(WmiResult))]
[JsonSerializable(typeof(FileStat))]
[JsonSerializable(typeof(DirListing))]
[JsonSerializable(typeof(LinesResult))]
[JsonSerializable(typeof(HeaderResult))]
[JsonSerializable(typeof(FileVersionResult))]
[JsonSerializable(typeof(ShortcutResult))]
[JsonSerializable(typeof(EventQueryResult))]
[JsonSerializable(typeof(EnvRaw))]
[JsonSerializable(typeof(ProcessList))]
[JsonSerializable(typeof(ServiceList))]
[JsonSerializable(typeof(DriverList))]
[JsonSerializable(typeof(DeviceList))]
[JsonSerializable(typeof(FirmwareRaw))]
[JsonSerializable(typeof(PowerRaw))]
[JsonSerializable(typeof(MemoryRaw))]
[JsonSerializable(typeof(CpuRaw))]
[JsonSerializable(typeof(DisplayRaw))]
[JsonSerializable(typeof(NetRaw))]
[JsonSerializable(typeof(ConnectivityRaw))]
[JsonSerializable(typeof(WlanRaw))]
[JsonSerializable(typeof(TpmRaw))]
[JsonSerializable(typeof(BitLockerRaw))]
[JsonSerializable(typeof(BcdRaw))]
[JsonSerializable(typeof(TpmWmiRaw))]
[JsonSerializable(typeof(ClockRaw))]
[JsonSerializable(typeof(PingRaw))]
[JsonSerializable(typeof(WuaRaw))]
[JsonSerializable(typeof(AudioDefaultsRaw))]
[JsonSerializable(typeof(CaptureMeta))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext
{
}

/// <summary>Header entry of a raw capture (<c>meta:scan</c>): what a replay needs to reproduce the scan.</summary>
public sealed record CaptureMeta
{
    /// <summary>Capture format.</summary>
    public string Format { get; init; } = CaptureFormat.Current;

    /// <summary>Scan start.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Elevated engine.</summary>
    public required bool Elevated { get; init; }

    /// <summary>Player SID (redacted in checked-in captures).</summary>
    public required string PlayerSid { get; init; }

    /// <summary>Player profile path.</summary>
    public required string PlayerProfilePath { get; init; }

    /// <summary>Player local app data.</summary>
    public required string PlayerLocalAppData { get; init; }

    /// <summary>Player roaming app data.</summary>
    public required string PlayerRoamingAppData { get; init; }

    /// <summary>Player is an administrator.</summary>
    public required bool PlayerIsAdmin { get; init; }

    /// <summary>Network probes were allowed.</summary>
    public bool AllowNetworkProbes { get; init; }

    /// <summary>Event window (days).</summary>
    public int EventWindowDays { get; init; } = 14;

    /// <summary>Scan mode name.</summary>
    public string Mode { get; init; } = "Full";
}

/// <summary>Capture format constants.</summary>
public static class CaptureFormat
{
    /// <summary>Current format id.</summary>
    public const string Current = "vtdiag-capture/1";

    /// <summary>Source key of the header entry.</summary>
    public const string MetaSource = "meta:scan";
}
