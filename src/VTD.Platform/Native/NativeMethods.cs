using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VTD.Platform.Native;

/// <summary>
/// Every P/Invoke of the read side. Read-only system APIs only: no function here opens another process or thread,
/// reads foreign memory, installs hooks or changes timer resolution (tests/VTD.Policy.Tests scans the binary).
/// </summary>
internal static partial class NativeMethods
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorMoreData = 234;
    internal const int ErrorServiceNotActive = 1062;
    internal const int ErrorNotFound = 1168;

    // ---- kernel32 ----
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeKernelHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(SafeKernelHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(SafeKernelHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFirmwareType(out int firmwareType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFirmwareEnvironmentVariableW")]
    internal static extern uint GetFirmwareEnvironmentVariable(string name, string guid, byte[] buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalFree(IntPtr mem);

    // ---- advapi32: own token only (enable SeSystemEnvironmentPrivilege for firmware variable reads) ----
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeKernelHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(SafeKernelHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    // ---- advapi32: SCM enumeration (SC_MANAGER_ENUMERATE_SERVICE only) ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    internal static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    // ---- psapi: loaded kernel modules (no process handle) ----
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDeviceDrivers([Out] IntPtr[]? imageBase, uint cb, out uint needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDeviceDriverBaseNameW")]
    internal static extern uint GetDeviceDriverBaseName(IntPtr imageBase, char[] baseName, uint size);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDeviceDriverFileNameW")]
    internal static extern uint GetDeviceDriverFileName(IntPtr imageBase, char[] fileName, uint size);

    // ---- cfgmgr32 ----
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    internal static extern int CmGetDeviceIdListSize(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    internal static extern int CmGetDeviceIdList(string? filter, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    internal static extern int CmLocateDevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    internal static extern int CmGetDevNodeStatus(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
    internal static extern int CmGetDevNodeProperty(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    // ---- powrprof ----
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    // ---- pdh (English counter paths: locale-free) ----
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    internal static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    internal static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    // ---- user32: display configuration ----
    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    internal static extern unsafe int QueryDisplayConfig(uint flags, ref uint numPaths, byte* paths, ref uint numModes, byte* modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern unsafe int DisplayConfigGetDeviceInfo(byte* packet);

    // ---- iphlpapi ----
    [DllImport("iphlpapi.dll")]
    internal static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [DllImport("iphlpapi.dll")]
    internal static extern unsafe int GetIpInterfaceEntry(byte* row);

    // ---- winhttp ----
    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinHttpGetDefaultProxyConfiguration(ref WinHttpProxyInfo info);

    // ---- wlanapi ----
    [DllImport("wlanapi.dll")]
    internal static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    internal static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    internal static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    internal static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    internal static extern int WlanGetNetworkBssList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr bssList);

    [DllImport("wlanapi.dll")]
    internal static extern void WlanFreeMemory(IntPtr memory);

    // ---- tbs ----
    [DllImport("tbs.dll")]
    internal static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    // ---- shell property system (BitLocker protection, no admin) ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);

    // ---- structures ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;

        public DevPropKey(string fmtId, uint pid)
        {
            FmtId = Guid.Parse(fmtId);
            Pid = pid;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PdhFmtCounterValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public long LargeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinHttpProxyInfo
    {
        public uint AccessType;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(8)]
        public int IntValue;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkListManager
    {
        void GetNetworks();

        void GetNetwork();

        void GetNetworkConnections();

        void GetNetworkConnection();

        void IsConnectedToInternet();

        void IsConnected();

        [PreserveSig]
        int GetConnectivity(out int connectivity);
    }
}

/// <summary>A kernel handle closed with CloseHandle (snapshots and the process's own token only).</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
