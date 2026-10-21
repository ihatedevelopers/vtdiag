using System.Buffers.Binary;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using VTD.Platform.Sources;

namespace VTD.Platform.Native;

/// <summary>Network interfaces, routing decision, connectivity, WLAN and the BitLocker shell property.</summary>
public sealed partial class SystemInfoSource
{
    /// <inheritdoc />
    public NetRaw GetNetwork()
    {
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return new NetRaw { Status = ReadStatus.Failed };
        }

        var list = new List<NetIfRec>();
        foreach (var nic in nics)
        {
            IPInterfaceProperties? ip = null;
            var index = 0;
            try
            {
                ip = nic.GetIPProperties();
                index = ip.GetIPv4Properties()?.Index ?? 0;
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                index = 0;
            }

            var (metric, auto) = index > 0 ? Ipv4Metric(index) : (null, null);
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            list.Add(new NetIfRec
            {
                Guid = nic.Id.ToUpperInvariant(),
                Description = nic.Description,
                Alias = nic.Name,
                IfType = (int)nic.NetworkInterfaceType,
                OperStatus = (int)nic.OperationalStatus,
                SpeedBps = nic.Speed,
                Mac = mac.Length == 6 ? string.Join(':', mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : null,
                Ipv4 = ip?.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList() ?? [],
                Dns = ip?.DnsAddresses.Select(a => a.ToString()).Take(8).ToList() ?? [],
                Gateways = ip?.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Select(g => g.Address.ToString()).ToList() ?? [],
                Ipv4Index = index,
                Ipv4Metric = metric,
                AutomaticMetric = auto,
            });
        }

        var best = NativeMethods.GetBestInterface(0x08080808, out var bestIndex) == 0 ? (int)bestIndex : 0;
        return new NetRaw { Status = ReadStatus.Ok, Interfaces = list, BestIpv4Index = best, WinHttpProxy = WinHttpProxySet() };
    }

    private static unsafe (int? Metric, bool? Auto) Ipv4Metric(int index)
    {
        // MIB_IPINTERFACE_ROW (x64): Family@0, InterfaceLuid@8, InterfaceIndex@16, UseAutomaticMetric@44, Metric@148.
        var row = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(row, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), (uint)index);
        fixed (byte* p = row)
        {
            if (NativeMethods.GetIpInterfaceEntry(p) != 0)
            {
                return (null, null);
            }
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(row) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(16)) != (uint)index)
        {
            return (null, null);
        }

        var metric = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(148));
        return metric > 100_000 ? (null, row[44] != 0) : ((int)metric, row[44] != 0);
    }

    private static bool? WinHttpProxySet()
    {
        var info = default(NativeMethods.WinHttpProxyInfo);
        try
        {
            if (!NativeMethods.WinHttpGetDefaultProxyConfiguration(ref info))
            {
                return null;
            }

            return info.AccessType == 3;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (info.Proxy != IntPtr.Zero)
            {
                NativeMethods.GlobalFree(info.Proxy);
            }

            if (info.ProxyBypass != IntPtr.Zero)
            {
                NativeMethods.GlobalFree(info.ProxyBypass);
            }
        }
    }

    /// <inheritdoc />
    public ConnectivityRaw GetConnectivity()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: false);
            if (type is null || Activator.CreateInstance(type) is not NativeMethods.INetworkListManager nlm)
            {
                return new ConnectivityRaw { Status = ReadStatus.Unsupported };
            }

            try
            {
                return nlm.GetConnectivity(out var flags) == 0
                    ? new ConnectivityRaw { Status = ReadStatus.Ok, Flags = flags }
                    : new ConnectivityRaw { Status = ReadStatus.Failed };
            }
            finally
            {
                Marshal.FinalReleaseComObject(nlm);
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidComObjectException)
        {
            return new ConnectivityRaw { Status = ReadStatus.Failed };
        }
    }

    /// <inheritdoc />
    public WlanRaw GetWlan()
    {
        IntPtr handle;
        try
        {
            var open = NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (open == NativeMethods.ErrorServiceNotActive)
            {
                return new WlanRaw { Status = ReadStatus.Unsupported };
            }

            if (open != 0)
            {
                return new WlanRaw { Status = open == NativeMethods.ErrorAccessDenied ? ReadStatus.AccessDenied : ReadStatus.Failed };
            }
        }
        catch (DllNotFoundException)
        {
            return new WlanRaw { Status = ReadStatus.Unsupported };
        }

        try
        {
            if (NativeMethods.WlanEnumInterfaces(handle, IntPtr.Zero, out var listPtr) != 0)
            {
                return new WlanRaw { Status = ReadStatus.Failed };
            }

            var result = new List<WlanIfRec>();
            try
            {
                var count = Marshal.ReadInt32(listPtr);
                for (var i = 0; i < count && i < 16; i++)
                {
                    var item = listPtr + 8 + (i * 532);
                    var bytes = new byte[16];
                    Marshal.Copy(item, bytes, 0, 16);
                    var guid = new Guid(bytes);
                    var state = Marshal.ReadInt32(item, 16 + 512);
                    result.Add(QueryWlan(handle, guid, state == 1));
                }
            }
            finally
            {
                NativeMethods.WlanFreeMemory(listPtr);
            }

            return new WlanRaw { Status = ReadStatus.Ok, Interfaces = result };
        }
        finally
        {
            _ = NativeMethods.WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static WlanIfRec QueryWlan(IntPtr handle, Guid guid, bool connected)
    {
        const int OpcodeCurrentConnection = 7;
        const int OpcodeChannelNumber = 8;
        const int OpcodeRssi = 0x10000102;
        var rec = new WlanIfRec { Guid = guid.ToString("B").ToUpperInvariant(), Connected = connected };
        if (!connected)
        {
            return rec;
        }

        byte[]? bssid = null;
        if (NativeMethods.WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out var size, out var data, IntPtr.Zero) == 0 && data != IntPtr.Zero)
        {
            try
            {
                if (size >= 588)
                {
                    var conn = new byte[588];
                    Marshal.Copy(data, conn, 0, conn.Length);
                    var ssidLen = (int)Math.Min(32, BinaryPrimitives.ReadUInt32LittleEndian(conn.AsSpan(520)));
                    var ssid = ssidLen > 0 ? Encoding.UTF8.GetString(conn, 524, ssidLen) : null;
                    bssid = conn.AsSpan(560, 6).ToArray();
                    rec = rec with
                    {
                        Ssid = string.IsNullOrEmpty(ssid) ? null : ssid,
                        PhyType = BinaryPrimitives.ReadInt32LittleEndian(conn.AsSpan(568)),
                        SignalQuality = (int)BinaryPrimitives.ReadUInt32LittleEndian(conn.AsSpan(576)),
                        RxRateKbps = (int)BinaryPrimitives.ReadUInt32LittleEndian(conn.AsSpan(580)),
                    };
                }
            }
            finally
            {
                NativeMethods.WlanFreeMemory(data);
            }
        }

        if (NativeMethods.WlanQueryInterface(handle, ref guid, OpcodeRssi, IntPtr.Zero, out var rssiSize, out var rssiData, IntPtr.Zero) == 0 && rssiData != IntPtr.Zero)
        {
            if (rssiSize >= 4)
            {
                rec = rec with { Rssi = Marshal.ReadInt32(rssiData) };
            }

            NativeMethods.WlanFreeMemory(rssiData);
        }

        int? frequency = bssid is not null && bssid.Any(b => b != 0) ? BssFrequency(handle, guid, bssid) : null;
        if (frequency is null && NativeMethods.WlanQueryInterface(handle, ref guid, OpcodeChannelNumber, IntPtr.Zero, out var chSize, out var chData, IntPtr.Zero) == 0 && chData != IntPtr.Zero)
        {
            if (chSize >= 4)
            {
                var channel = Marshal.ReadInt32(chData);
                frequency = channel is >= 1 and <= 14 ? 2_407_000 + (channel * 5000) : channel >= 32 ? 5_000_000 + (channel * 5000) : null;
            }

            NativeMethods.WlanFreeMemory(chData);
        }

        return rec with { FrequencyKhz = frequency };
    }

    private static int? BssFrequency(IntPtr handle, Guid guid, byte[] bssid)
    {
        const int EntrySize = 360;
        if (NativeMethods.WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 1, false, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var count = Marshal.ReadInt32(list, 4);
            var entry = new byte[EntrySize];
            for (var i = 0; i < count && i < 512; i++)
            {
                Marshal.Copy(list + 8 + (i * EntrySize), entry, 0, EntrySize);
                if (entry.AsSpan(40, 6).SequenceEqual(bssid))
                {
                    return (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(92));
                }
            }

            return null;
        }
        finally
        {
            NativeMethods.WlanFreeMemory(list);
        }
    }

    /// <inheritdoc />
    public BitLockerRaw GetBitLocker(string driveRoot)
    {
        ArgumentNullException.ThrowIfNull(driveRoot);
        BitLockerRaw result = new() { Status = ReadStatus.Failed };
        var thread = new Thread(() => result = ReadBitLocker(driveRoot)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(5)) ? result : new BitLockerRaw { Status = ReadStatus.TimedOut };
    }

    private static BitLockerRaw ReadBitLocker(string driveRoot)
    {
        try
        {
            if (NativeMethods.PSGetPropertyKeyFromName("System.Volume.BitLockerProtection", out var key) != 0)
            {
                return new BitLockerRaw { Status = ReadStatus.Unsupported };
            }

            var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
            if (NativeMethods.SHGetPropertyStoreFromParsingName(driveRoot, IntPtr.Zero, 0, ref iid, out var store) != 0 || store is null)
            {
                return new BitLockerRaw { Status = ReadStatus.Failed };
            }

            try
            {
                if (store.GetValue(ref key, out var value) != 0)
                {
                    return new BitLockerRaw { Status = ReadStatus.Failed };
                }

                try
                {
                    // VT_EMPTY = 0 (not encryptable / no value), VT_I4 = 3, VT_UI4 = 19.
                    return value.Vt is 3 or 19
                        ? new BitLockerRaw { Status = ReadStatus.Ok, Protection = value.IntValue }
                        : new BitLockerRaw { Status = ReadStatus.Ok, Protection = null };
                }
                finally
                {
                    _ = NativeMethods.PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(store);
            }
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            return new BitLockerRaw { Status = ReadStatus.Failed };
        }
    }
}
