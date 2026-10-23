using System.Runtime.InteropServices;

namespace VTD.Fixes.IntegrationTests;

/// <summary>
/// The test helper's own P/Invokes (independent of the product's ports, so read-backs do not trust the code under test):
/// SetupAPI/newdev to install and remove a KM-TEST Loopback Adapter, cfgmgr32 to read and re-enable a devnode, powrprof
/// to seed and read power settings, and the active console session id. Nothing here opens another process.
/// </summary>
internal static class TestNative
{
    internal const int DicdGenerateId = 0x1;
    internal const int DifRemove = 0x05;
    internal const int DifRegisterDevice = 0x19;
    internal const int SpdrpHardwareId = 0x01;
    internal const int SpdrpDriver = 0x09;
    internal const int InstallFlagForce = 0x1;
    internal const uint DnHasProblem = 0x400;
    internal static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetINFClassW")]
    internal static extern bool SetupDiGetInfClass(string infName, out Guid classGuid, [Out] char[] className, int classNameSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoList")]
    internal static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoW")]
    internal static extern bool SetupDiCreateDeviceInfo(IntPtr set, string deviceName, ref Guid classGuid, string? description, IntPtr hwndParent, int flags, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiOpenDeviceInfoW")]
    internal static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr hwndParent, int flags, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    internal static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SpDevinfoData data, int property, byte[] buffer, int size);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    internal static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SpDevinfoData data, int property, out int regType, [Out] byte[]? buffer, int size, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiCallClassInstaller")]
    internal static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr set, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    internal static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevinfoData data, [Out] char[] instanceId, int size, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiDestroyDeviceInfoList")]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    internal static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath, int installFlags, out bool rebootRequired);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    internal static extern int CmLocateDevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    internal static extern int CmGetDevNodeStatus(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Enable_DevNode")]
    internal static extern int CmEnableDevNode(uint devInst, uint flags);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    internal static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    internal static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerReadACValueIndex")]
    internal static extern uint PowerReadAcValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll", EntryPoint = "PowerReadDCValueIndex")]
    internal static extern uint PowerReadDcValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll", EntryPoint = "PowerWriteACValueIndex")]
    internal static extern uint PowerWriteAcValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll", EntryPoint = "PowerWriteDCValueIndex")]
    internal static extern uint PowerWriteDcValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    internal static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    internal static extern uint WtsGetActiveConsoleSessionId();

    /// <summary>SP_DEVINFO_DATA.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }
}
