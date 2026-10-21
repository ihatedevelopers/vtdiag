using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using VTD.Platform.Sources;

namespace VTD.Platform.Native;

/// <summary>Firmware, power, memory and CPU reads.</summary>
public sealed partial class SystemInfoSource
{
    private const string EfiGlobalGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
    private const string EfiImageSecurityGuid = "{D719B2CB-3D3A-4596-A3BC-DAD00E67656F}";

    private static readonly Guid SubUsb = Guid.Parse("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid SetUsbSelectiveSuspend = Guid.Parse("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid SubProcessor = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid SetProcMin = Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid SetProcMax = Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec");

    /// <inheritdoc />
    public FirmwareRaw GetFirmware(bool privileged)
    {
        var type = NativeMethods.GetFirmwareType(out var t) ? t : 0;
        if (!privileged || !EnablePrivilege("SeSystemEnvironmentPrivilege"))
        {
            return new FirmwareRaw { FirmwareType = type, VariablesStatus = ReadStatus.AccessDenied };
        }

        var setupBuf = new byte[8];
        var setupLen = NativeMethods.GetFirmwareEnvironmentVariable("SetupMode", EfiGlobalGuid, setupBuf, (uint)setupBuf.Length);
        if (setupLen == 0 && Marshal.GetLastWin32Error() == NativeMethods.ErrorAccessDenied)
        {
            return new FirmwareRaw { FirmwareType = type, VariablesStatus = ReadStatus.AccessDenied };
        }

        var osIndBuf = new byte[8];
        var osIndLen = NativeMethods.GetFirmwareEnvironmentVariable("OsIndicationsSupported", EfiGlobalGuid, osIndBuf, (uint)osIndBuf.Length);
        var dbBuf = new byte[256 * 1024];
        var dbLen = NativeMethods.GetFirmwareEnvironmentVariable("db", EfiImageSecurityGuid, dbBuf, (uint)dbBuf.Length);
        bool? dbHasCa2023 = null;
        if (dbLen > 0)
        {
            var needle = Encoding.ASCII.GetBytes("Windows UEFI CA 2023");
            dbHasCa2023 = dbBuf.AsSpan(0, (int)dbLen).IndexOf(needle) >= 0;
        }

        return new FirmwareRaw
        {
            FirmwareType = type,
            VariablesStatus = ReadStatus.Ok,
            SetupMode = setupLen > 0 ? setupBuf[0] : null,
            OsIndicationsSupported = osIndLen >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(osIndBuf) : osIndLen > 0 ? osIndBuf[0] : null,
            DbHasCa2023 = dbHasCa2023,
        };
    }

    private static bool EnablePrivilege(string name)
    {
        const uint TokenAdjustPrivileges = 0x20;
        const uint TokenQuery = 0x8;
        const uint SePrivilegeEnabled = 0x2;
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!NativeMethods.LookupPrivilegeValue(null, name, out var luid))
            {
                return false;
            }

            var tp = new NativeMethods.TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            var ok = NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            return ok && Marshal.GetLastWin32Error() == NativeMethods.ErrorSuccess;
        }
    }

    /// <inheritdoc />
    public PowerRaw GetPower()
    {
        var statusOk = NativeMethods.GetSystemPowerStatus(out var ps);
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0 || schemePtr == IntPtr.Zero)
            {
                return PowerStatusOnly(statusOk, ps);
            }

            var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            NativeMethods.LocalFree(schemePtr);
            string? overlay = null;
            try
            {
                if (NativeMethods.PowerGetEffectiveOverlayScheme(out var o) == 0)
                {
                    overlay = o.ToString("D");
                }
            }
            catch (EntryPointNotFoundException)
            {
                overlay = null;
            }

            return new PowerRaw
            {
                Status = ReadStatus.Ok,
                ActiveScheme = scheme.ToString("D"),
                SchemeName = FriendlyName(scheme),
                Overlay = overlay,
                UsbSelectiveSuspendAc = ReadIndex(scheme, SubUsb, SetUsbSelectiveSuspend, ac: true),
                UsbSelectiveSuspendDc = ReadIndex(scheme, SubUsb, SetUsbSelectiveSuspend, ac: false),
                ProcessorMinAc = ReadIndex(scheme, SubProcessor, SetProcMin, ac: true),
                ProcessorMaxAc = ReadIndex(scheme, SubProcessor, SetProcMax, ac: true),
                PowerStatusOk = statusOk,
                AcLine = ps.AcLineStatus,
                BatteryFlag = ps.BatteryFlag,
                BatteryPercent = ps.BatteryLifePercent,
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return PowerStatusOnly(statusOk, ps) with { Status = ReadStatus.Unsupported };
        }
    }

    private static PowerRaw PowerStatusOnly(bool ok, NativeMethods.SystemPowerStatus ps) => new()
    {
        Status = ReadStatus.Failed,
        PowerStatusOk = ok,
        AcLine = ps.AcLineStatus,
        BatteryFlag = ps.BatteryFlag,
        BatteryPercent = ps.BatteryLifePercent,
    };

    private static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0 || size > 4096)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    private static int? ReadIndex(Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        uint value;
        var result = ac
            ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value)
            : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        return result == 0 ? (int)value : null;
    }

    /// <inheritdoc />
    public MemoryRaw GetMemory()
    {
        var ms = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref ms))
        {
            return new MemoryRaw { Status = ReadStatus.Failed };
        }

        return new MemoryRaw
        {
            Status = ReadStatus.Ok,
            TotalBytes = (long)ms.TotalPhys,
            AvailableBytes = (long)ms.AvailPhys,
            StandbyBytes = ReadStandby(),
        };
    }

    private static long? ReadStandby()
    {
        const uint PdhFmtLarge = 0x400;
        string[] paths =
        [
            @"\Memory\Standby Cache Normal Priority Bytes",
            @"\Memory\Standby Cache Reserve Bytes",
            @"\Memory\Standby Cache Core Bytes",
        ];
        try
        {
            if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out var query) != 0)
            {
                return null;
            }

            try
            {
                var counters = new List<IntPtr>();
                foreach (var p in paths)
                {
                    if (NativeMethods.PdhAddEnglishCounter(query, p, IntPtr.Zero, out var c) == 0)
                    {
                        counters.Add(c);
                    }
                }

                if (counters.Count == 0 || NativeMethods.PdhCollectQueryData(query) != 0)
                {
                    return null;
                }

                long total = 0;
                foreach (var c in counters)
                {
                    if (NativeMethods.PdhGetFormattedCounterValue(c, PdhFmtLarge, IntPtr.Zero, out var v) == 0)
                    {
                        total += v.LargeValue;
                    }
                }

                return total;
            }
            finally
            {
                _ = NativeMethods.PdhCloseQuery(query);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public CpuRaw GetCpu()
    {
        const int RelationProcessorCore = 0;
        uint length = 0;
        NativeMethods.GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0 || length > 1 << 20)
        {
            return new CpuRaw { Status = ReadStatus.Failed };
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                return new CpuRaw { Status = ReadStatus.Failed };
            }

            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, (int)length);
            var cores = 0;
            var logical = 0;
            var byClass = new int[16];
            var offset = 0;
            while (offset + 8 <= bytes.Length)
            {
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
                if (size <= 0)
                {
                    break;
                }

                var rel = bytes.AsSpan(offset + 8);
                var efficiency = rel[1];
                var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(rel[22..]);
                for (var g = 0; g < groupCount; g++)
                {
                    var mask = BinaryPrimitives.ReadUInt64LittleEndian(rel[(24 + (g * 16))..]);
                    logical += BitOperations.PopCount(mask);
                }

                cores++;
                if (efficiency < byClass.Length)
                {
                    byClass[efficiency]++;
                }

                offset += size;
            }

            var maxClass = Array.FindLastIndex(byClass, c => c > 0);
            var raw = new CpuRaw
            {
                Status = ReadStatus.Ok,
                Cores = cores,
                Logical = logical,
                CoresByEfficiencyClass = byClass.Take(maxClass + 1).ToList(),
                Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };
            return X86Base.IsSupported ? AddCpuid(raw) : raw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static CpuRaw AddCpuid(CpuRaw raw)
    {
        var (_, b0, c0, d0) = X86Base.CpuId(0, 0);
        Span<byte> vendor = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(vendor, b0);
        BinaryPrimitives.WriteInt32LittleEndian(vendor[4..], d0);
        BinaryPrimitives.WriteInt32LittleEndian(vendor[8..], c0);
        var (_, _, c1, _) = X86Base.CpuId(1, 0);
        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        var svm = false;
        if (unchecked((uint)maxExt) >= 0x80000001)
        {
            var (_, _, ce, _) = X86Base.CpuId(unchecked((int)0x80000001), 0);
            svm = (ce & (1 << 2)) != 0;
        }

        return raw with
        {
            CpuidAvailable = true,
            CpuidVendor = Encoding.ASCII.GetString(vendor),
            Vmx = (c1 & (1 << 5)) != 0,
            Svm = svm,
            HypervisorBit = (c1 & unchecked((int)0x80000000)) != 0,
        };
    }
}
