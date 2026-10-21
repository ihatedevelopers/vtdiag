using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Management;
using VTD.Contracts;
using VTD.Fixes.Ops;
using VTD.Fixes.Ports;
using static VTD.Fixes.Interop.NativeMethods;

namespace VTD.Fixes.Interop;

/// <summary>
/// Network configuration: the adapter's interface GUID from its own driver key, default-route detection from the routing
/// table (GetBestInterfaceEx towards a public address; no packet is sent), the IPv4 metric through
/// Get/SetIpInterfaceEntry, and the ms_tcpip6 binding through MSFT_NetAdapterBindingSettingData.
/// </summary>
public sealed class NetworkPort : INetworkPort
{
    private const int RowSize = 256; // MIB_IPINTERFACE_ROW is 168 bytes on x64; the extra room is harmless.
    private readonly IDevicePort _devices;
    private readonly IRegistryPort _registry;

    /// <summary>Creates the port.</summary>
    /// <param name="devices">Device port (driver key).</param>
    /// <param name="registry">Registry port (NetCfgInstanceId).</param>
    public NetworkPort(IDevicePort devices, IRegistryPort registry)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public Guid? InterfaceGuidOf(string instanceId)
    {
        var d = _devices.Query(instanceId);
        if (d?.DriverKey is not { } driverKey || !driverKey.StartsWith("{4d36e972-e325-11ce-bfc1-08002be10318}\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var v = _registry.GetValue(RegHive.Machine, "SYSTEM\\CurrentControlSet\\Control\\Class\\" + driverKey, "NetCfgInstanceId");
        return v is not null && Guid.TryParse(RegCodec.Display(v), out var g) ? g : null;
    }

    /// <inheritdoc />
    public Tri CarriesDefaultRoute(Guid interfaceGuid)
    {
        var g = interfaceGuid;
        if (ConvertInterfaceGuidToLuid(ref g, out var luid) != 0)
        {
            return Tri.Unknown;
        }

        // IPv4 1.1.1.1 and IPv6 2606:4700:4700::1111: routing-table lookups only.
        var v4 = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(v4, 2);
        v4[4] = 1;
        v4[5] = 1;
        v4[6] = 1;
        v4[7] = 1;
        var v6 = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(v6, 23);
        byte[] addr6 = [0x26, 0x06, 0x47, 0x00, 0x47, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0x11, 0x11];
        addr6.CopyTo(v6, 8);
        foreach (var dest in new[] { v4, v6 })
        {
            if (GetBestInterfaceEx(dest, out var index) == 0 && ConvertInterfaceIndexToLuid(index, out var bestLuid) == 0 && bestLuid == luid)
            {
                return Tri.True;
            }
        }

        return Tri.False;
    }

    /// <inheritdoc />
    public InterfaceMetric? GetIpv4Metric(Guid interfaceGuid)
    {
        var row = Row(interfaceGuid);
        return row is null ? null : new InterfaceMetric((int)BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(148)), row[44] != 0);
    }

    /// <inheritdoc />
    public void SetIpv4Metric(Guid interfaceGuid, bool automatic, int metric)
    {
        var row = Row(interfaceGuid) ?? throw new IOException("The adapter has no IPv4 interface.");
        row[44] = automatic ? (byte)1 : (byte)0;
        if (!automatic)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(148), (uint)Math.Clamp(metric, 1, 9999));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(144), 0); // SitePrefixLength must be 0 for IPv4 or Set fails
        var rc = SetIpInterfaceEntry(row);
        if (rc != 0)
        {
            throw new Win32Exception(rc);
        }
    }

    /// <inheritdoc />
    public bool? GetIpv6Bound(Guid interfaceGuid)
    {
        using var binding = Binding(interfaceGuid);
        return binding?["Enabled"] as bool?;
    }

    /// <inheritdoc />
    public void SetIpv6Bound(Guid interfaceGuid, bool bound)
    {
        using var binding = Binding(interfaceGuid) ?? throw new IOException("The adapter has no ms_tcpip6 binding.");
        using var result = binding.InvokeMethod(bound ? "Enable" : "Disable", null, null);
        var rc = result?["ReturnValue"] switch
        {
            uint u => (long)u,
            int i => i,
            _ => -1,
        };
        if (rc != 0)
        {
            throw new IOException("MSFT_NetAdapterBindingSettingData." + (bound ? "Enable" : "Disable") + " returned " + rc.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    private static byte[]? Row(Guid interfaceGuid)
    {
        var g = interfaceGuid;
        if (ConvertInterfaceGuidToLuid(ref g, out var luid) != 0)
        {
            return null;
        }

        var row = new byte[RowSize];
        InitializeIpInterfaceEntry(row);
        BinaryPrimitives.WriteUInt16LittleEndian(row, 2); // AF_INET
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(8), luid);
        return GetIpInterfaceEntry(row) == 0 ? row : null;
    }

    private static ManagementObject? Binding(Guid interfaceGuid)
    {
        var scope = new ManagementScope(@"root\StandardCimv2");
        scope.Connect();
        var instance = "{" + interfaceGuid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}::ms_tcpip6";
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_NetAdapterBindingSettingData WHERE ComponentID = 'ms_tcpip6'"));
        using var results = searcher.Get();
        ManagementObject? found = null;
        foreach (ManagementObject mo in results)
        {
            if (found is null && string.Equals(mo["InstanceID"] as string, instance, StringComparison.OrdinalIgnoreCase))
            {
                found = mo;
            }
            else
            {
                mo.Dispose();
            }
        }

        return found;
    }
}
