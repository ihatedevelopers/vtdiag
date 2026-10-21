using System.Buffers.Binary;
using System.Text;
using VTD.Platform.Sources;

namespace VTD.Platform.Native;

/// <summary>Display topology through QueryDisplayConfig (EDID names, no localized strings).</summary>
public sealed partial class SystemInfoSource
{
    private const int PathSize = 72;
    private const int ModeSize = 64;

    /// <inheritdoc />
    public unsafe DisplayRaw GetDisplays()
    {
        const uint QdcOnlyActivePaths = 0x2;
        if (NativeMethods.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var numPaths, out var numModes) != 0 || numPaths == 0)
        {
            return new DisplayRaw { Status = numPaths == 0 ? ReadStatus.Ok : ReadStatus.Failed };
        }

        var paths = new byte[numPaths * PathSize];
        var modes = new byte[numModes * ModeSize];
        int result;
        fixed (byte* p = paths)
        fixed (byte* m = modes)
        {
            result = NativeMethods.QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, p, ref numModes, m, IntPtr.Zero);
        }

        if (result != 0)
        {
            return new DisplayRaw { Status = ReadStatus.Failed };
        }

        var list = new List<DisplayPathRec>();
        for (var i = 0; i < numPaths; i++)
        {
            var path = paths.AsSpan(i * PathSize, PathSize);
            var srcAdapter = path[..8].ToArray();
            var srcModeIdx = BinaryPrimitives.ReadUInt32LittleEndian(path[12..]);
            var tgtAdapter = path.Slice(20, 8).ToArray();
            var tgtId = BinaryPrimitives.ReadUInt32LittleEndian(path[28..]);
            var tech = BinaryPrimitives.ReadUInt32LittleEndian(path[36..]);
            var num = BinaryPrimitives.ReadUInt32LittleEndian(path[48..]);
            var den = BinaryPrimitives.ReadUInt32LittleEndian(path[52..]);
            int width = 0, height = 0;
            var primary = false;
            if (srcModeIdx < numModes)
            {
                var mode = modes.AsSpan((int)srcModeIdx * ModeSize, ModeSize);
                if (BinaryPrimitives.ReadUInt32LittleEndian(mode) == 1)
                {
                    width = (int)BinaryPrimitives.ReadUInt32LittleEndian(mode[16..]);
                    height = (int)BinaryPrimitives.ReadUInt32LittleEndian(mode[20..]);
                    primary = BinaryPrimitives.ReadInt32LittleEndian(mode[28..]) == 0 && BinaryPrimitives.ReadInt32LittleEndian(mode[32..]) == 0;
                }
            }

            list.Add(new DisplayPathRec
            {
                Name = TargetName(tgtAdapter, tgtId),
                Width = width,
                Height = height,
                RefreshHz = den == 0 ? 0 : (int)Math.Round((double)num / den),
                Primary = primary,
                Hdr = AdvancedColor(tgtAdapter, tgtId),
                Internal = tech is 0x80000000 or 6 or 11 or 13,
                AdapterDevicePath = AdapterName(srcAdapter),
            });
        }

        return new DisplayRaw { Status = ReadStatus.Ok, Paths = list };
    }

    private static unsafe string? TargetName(byte[] adapter, uint id)
    {
        const int Size = 420;
        var packet = Header(2, Size, adapter, id);
        fixed (byte* p = packet)
        {
            if (NativeMethods.DisplayConfigGetDeviceInfo(p) != 0)
            {
                return null;
            }
        }

        return ReadWide(packet.AsSpan(36, 128));
    }

    private static unsafe string? AdapterName(byte[] adapter)
    {
        const int Size = 276;
        var packet = Header(4, Size, adapter, 0);
        fixed (byte* p = packet)
        {
            if (NativeMethods.DisplayConfigGetDeviceInfo(p) != 0)
            {
                return null;
            }
        }

        return ReadWide(packet.AsSpan(20, 256));
    }

    private static unsafe bool? AdvancedColor(byte[] adapter, uint id)
    {
        const int Size = 32;
        var packet = Header(9, Size, adapter, id);
        fixed (byte* p = packet)
        {
            if (NativeMethods.DisplayConfigGetDeviceInfo(p) != 0)
            {
                return null;
            }
        }

        var bits = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20));
        return (bits & 0x1) == 0 ? false : (bits & 0x2) != 0;
    }

    private static byte[] Header(uint type, int size, byte[] adapter, uint id)
    {
        var packet = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)size);
        adapter.CopyTo(packet, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), id);
        return packet;
    }

    private static string? ReadWide(ReadOnlySpan<byte> span)
    {
        var s = Encoding.Unicode.GetString(span);
        var end = s.IndexOf('\0', StringComparison.Ordinal);
        s = end >= 0 ? s[..end] : s;
        return s.Length == 0 ? null : s;
    }
}
