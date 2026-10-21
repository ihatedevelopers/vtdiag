using System.Buffers.Binary;
using System.Text;
using VTD.Platform.Sources;

namespace VTD.Platform.Native;

/// <summary>PnP device enumeration through CfgMgr32 (read-only property queries, InstanceIds, no localized matching).</summary>
public sealed partial class SystemInfoSource
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint CmLocateDevnodePhantom = 1;

    private const string DevPkeyDevice = "a45c254e-df1c-4efd-8020-67d146a850e0";
    private const string DevPkeyDriver = "a8b865dd-2e3d-4094-ad97-e593a70c75d6";
    private const string DevPkeyNode = "4340a6c5-93fa-4706-972c-7b648008a5a7";

    private static NativeMethods.DevPropKey keyDeviceDesc = new(DevPkeyDevice, 2);
    private static NativeMethods.DevPropKey keyHardwareIds = new(DevPkeyDevice, 3);
    private static NativeMethods.DevPropKey keyService = new(DevPkeyDevice, 6);
    private static NativeMethods.DevPropKey keyClass = new(DevPkeyDevice, 9);
    private static NativeMethods.DevPropKey keyClassGuid = new(DevPkeyDevice, 10);
    private static NativeMethods.DevPropKey keyFriendlyName = new(DevPkeyDevice, 14);
    private static NativeMethods.DevPropKey keyEnumerator = new(DevPkeyDevice, 24);
    private static NativeMethods.DevPropKey keyDriverKey = new(DevPkeyDevice, 11);
    private static NativeMethods.DevPropKey keyDriverDate = new(DevPkeyDriver, 2);
    private static NativeMethods.DevPropKey keyDriverVersion = new(DevPkeyDriver, 3);
    private static NativeMethods.DevPropKey keyDriverInf = new(DevPkeyDriver, 5);
    private static NativeMethods.DevPropKey keyDriverProvider = new(DevPkeyDriver, 9);
    private static NativeMethods.DevPropKey keyProblemCode = new(DevPkeyNode, 3);
    private static NativeMethods.DevPropKey keyParent = new(DevPkeyNode, 8);

    /// <inheritdoc />
    public DeviceList GetDevices(IReadOnlyList<string> includePhantomClasses)
    {
        ArgumentNullException.ThrowIfNull(includePhantomClasses);
        try
        {
            if (NativeMethods.CmGetDeviceIdListSize(out var length, null, 0) != CrSuccess || length == 0)
            {
                return new DeviceList { Status = ReadStatus.Failed };
            }

            var buffer = new char[length + 2];
            if (NativeMethods.CmGetDeviceIdList(null, buffer, (uint)buffer.Length, 0) != CrSuccess)
            {
                return new DeviceList { Status = ReadStatus.Failed };
            }

            var phantomClasses = new HashSet<string>(includePhantomClasses, StringComparer.OrdinalIgnoreCase);
            var items = new List<DeviceRec>();
            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (NativeMethods.CmLocateDevNode(out var devInst, id, CmLocateDevnodePhantom) != CrSuccess)
                {
                    continue;
                }

                var present = NativeMethods.CmGetDevNodeStatus(out var status, out var problem, devInst, 0) == CrSuccess;
                var classGuid = GetGuid(devInst, ref keyClassGuid);
                if (!present && (classGuid is null || !phantomClasses.Contains(classGuid)))
                {
                    continue;
                }

                var friendly = GetString(devInst, ref keyFriendlyName) ?? GetString(devInst, ref keyDeviceDesc);
                var slash = id.IndexOf('\\', StringComparison.Ordinal);
                items.Add(new DeviceRec
                {
                    InstanceId = id,
                    ClassGuid = classGuid,
                    ClassName = GetString(devInst, ref keyClass),
                    Enumerator = GetString(devInst, ref keyEnumerator) ?? (slash > 0 ? id[..slash] : null),
                    FriendlyName = friendly,
                    HardwareIds = GetStringList(devInst, ref keyHardwareIds),
                    Present = present,
                    ProblemCode = present ? (int)problem : (int)(GetUInt32(devInst, ref keyProblemCode) ?? 0),
                    StatusFlags = present ? status : 0,
                    Service = GetString(devInst, ref keyService),
                    DriverVersion = GetString(devInst, ref keyDriverVersion),
                    DriverDate = GetFileTime(devInst, ref keyDriverDate),
                    DriverProvider = GetString(devInst, ref keyDriverProvider),
                    InfName = GetString(devInst, ref keyDriverInf),
                    DriverKey = GetString(devInst, ref keyDriverKey),
                    Parent = GetString(devInst, ref keyParent),
                });
            }

            return new DeviceList { Status = ReadStatus.Ok, Items = items };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new DeviceList { Status = ReadStatus.Unsupported };
        }
    }

    private static byte[]? GetRaw(uint devInst, ref NativeMethods.DevPropKey key, out uint type)
    {
        uint size = 0;
        var cr = NativeMethods.CmGetDevNodeProperty(devInst, ref key, out type, null, ref size, 0);
        if (cr != CrBufferSmall || size == 0 || size > 1 << 16)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeMethods.CmGetDevNodeProperty(devInst, ref key, out type, buffer, ref size, 0) == CrSuccess ? buffer : null;
    }

    private static string? GetString(uint devInst, ref NativeMethods.DevPropKey key)
    {
        var raw = GetRaw(devInst, ref key, out _);
        if (raw is null)
        {
            return null;
        }

        var s = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static IReadOnlyList<string> GetStringList(uint devInst, ref NativeMethods.DevPropKey key)
    {
        var raw = GetRaw(devInst, ref key, out _);
        return raw is null ? [] : Encoding.Unicode.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(16).ToList();
    }

    private static string? GetGuid(uint devInst, ref NativeMethods.DevPropKey key)
    {
        var raw = GetRaw(devInst, ref key, out _);
        return raw is { Length: 16 } ? new Guid(raw).ToString("B").ToLowerInvariant() : null;
    }

    private static uint? GetUInt32(uint devInst, ref NativeMethods.DevPropKey key)
    {
        var raw = GetRaw(devInst, ref key, out _);
        return raw is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(raw) : null;
    }

    private static DateTimeOffset? GetFileTime(uint devInst, ref NativeMethods.DevPropKey key)
    {
        var raw = GetRaw(devInst, ref key, out _);
        if (raw is not { Length: 8 })
        {
            return null;
        }

        var ft = BinaryPrimitives.ReadInt64LittleEndian(raw);
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(ft).Date, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
