using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using VTD.Fixes.Ports;
using static VTD.Fixes.Interop.NativeMethods;

namespace VTD.Fixes.Interop;

/// <summary>
/// Device state through Configuration Manager (reads) and the SetupAPI class installer (changes: DIF_PROPERTYCHANGE with
/// DICS_DISABLE / DICS_ENABLE in the global scope, which persists across reboots like Device Manager; DICS_PROPCHANGE to
/// restart). A device is enabled unless it has CM_PROB_DISABLED or CONFIGFLAG_DISABLED.
/// </summary>
public sealed class CfgMgrDevicePort : IDevicePort
{
    private const int ProblemDisabled = 22;

    /// <inheritdoc />
    public DeviceInfo? Query(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var rc = CmLocateDevNode(out var dn, instanceId, CmLocateDevnodePhantom);
        if (rc == CrNoSuchDevnode)
        {
            return null;
        }

        if (rc != CrSuccess)
        {
            throw new IOException("CM_Locate_DevNode failed: 0x" + rc.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        }

        return Describe(dn, instanceId);
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceInfo> EnumeratePresent()
    {
        var rc = CmGetDeviceIdListSize(out var length, null, CmGetIdListFilterPresent);
        if (rc != CrSuccess)
        {
            throw new IOException("CM_Get_Device_ID_List_Size failed.");
        }

        var buffer = new char[length + 2];
        rc = CmGetDeviceIdList(null, buffer, (uint)buffer.Length, CmGetIdListFilterPresent);
        if (rc != CrSuccess)
        {
            throw new IOException("CM_Get_Device_ID_List failed.");
        }

        var list = new List<DeviceInfo>();
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CmLocateDevNode(out var dn, id, CmLocateDevnodeNormal) == CrSuccess)
            {
                list.Add(Describe(dn, id));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public DeviceChangeResult Disable(string instanceId) => Change(instanceId, DicsDisable);

    /// <inheritdoc />
    public DeviceChangeResult Enable(string instanceId) => Change(instanceId, DicsEnable);

    /// <inheritdoc />
    public DeviceChangeResult Restart(string instanceId) => Change(instanceId, DicsPropChange);

    private static DeviceInfo Describe(uint dn, string instanceId)
    {
        var present = CmGetDevNodeStatus(out var status, out var problem, dn, 0) == CrSuccess;
        var flags = ReadDword(dn, CmDrpConfigFlags) ?? 0;
        var disabled = (present && (status & DnHasProblem) != 0 && problem == ProblemDisabled) || (flags & ConfigFlagDisabled) != 0;
        string? parent = null;
        if (CmGetParent(out var p, dn, 0) == CrSuccess)
        {
            var buf = new char[512];
            if (CmGetDeviceId(p, buf, (uint)buf.Length, 0) == CrSuccess)
            {
                parent = new string(buf).TrimEnd('\0');
            }
        }

        return new DeviceInfo
        {
            InstanceId = instanceId,
            Present = present,
            Enabled = !disabled,
            ProblemCode = present && (status & DnHasProblem) != 0 ? (int)problem : 0,
            ClassGuid = ReadString(dn, CmDrpClassGuid)?.ToLowerInvariant(),
            ParentInstanceId = parent,
            DriverKey = ReadString(dn, CmDrpDriver),
        };
    }

    private static string? ReadString(uint dn, uint property)
    {
        uint length = 0;
        var probe = CmGetDevNodeRegistryProperty(dn, property, out _, null, ref length, 0);
        if (length == 0 || (probe != CrSuccess && probe != 0x1A))
        {
            return null;
        }

        var buffer = new byte[length];
        return CmGetDevNodeRegistryProperty(dn, property, out _, buffer, ref length, 0) == CrSuccess
            ? Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0')
            : null;
    }

    private static uint? ReadDword(uint dn, uint property)
    {
        uint length = 4;
        var buffer = new byte[4];
        return CmGetDevNodeRegistryProperty(dn, property, out _, buffer, ref length, 0) == CrSuccess ? BitConverter.ToUInt32(buffer, 0) : null;
    }

    private static DeviceChangeResult Change(string instanceId, uint stateChange)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var parameters = new SpPropChangeParams
            {
                Header = new SpClassInstallHeader { CbSize = (uint)Marshal.SizeOf<SpClassInstallHeader>(), InstallFunction = DifPropertyChange },
                StateChange = stateChange,
                Scope = DicsFlagGlobal,
                HwProfile = 0,
            };
            if (!SetupDiSetClassInstallParams(set, ref data, ref parameters, (uint)Marshal.SizeOf<SpPropChangeParams>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!SetupDiCallClassInstaller(DifPropertyChange, set, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var install = new SpDevinstallParams { CbSize = (uint)Marshal.SizeOf<SpDevinstallParams>(), DriverPath = string.Empty };
            var reboot = SetupDiGetDeviceInstallParams(set, ref data, ref install) && (install.Flags & (DiNeedReboot | DiNeedRestart)) != 0;
            return new DeviceChangeResult(reboot);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
