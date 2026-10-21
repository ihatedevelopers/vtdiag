using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VTD.Fixes.Interop;

/// <summary>
/// Every P/Invoke of the write side. Nothing here opens another process or thread, reads or writes foreign memory,
/// installs a hook or changes the timer resolution (tests/VTD.Policy.Tests scans the binary). Process work is limited to
/// a ToolHelp snapshot (names and ids) and posting WM_CLOSE to windows.
/// </summary>
internal static class NativeMethods
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorMoreData = 234;
    internal const int ErrorServiceDoesNotExist = 1060;
    internal const int ErrorServiceNotActive = 1062;
    internal const int ErrorServiceAlreadyRunning = 1056;

    // ---- advapi32: SCM ----
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceChangeConfig = 0x0002;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceStart = 0x0010;
    internal const uint ServiceStop = 0x0020;
    internal const uint ServiceNoChange = 0xFFFFFFFF;
    internal const uint ServiceControlStop = 0x00000001;
    internal const int ServiceConfigDelayedAutoStartInfo = 3;
    internal const int ServiceConfigLaunchProtected = 12;
    internal const int ScStatusProcessInfo = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    internal static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    internal static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig2(IntPtr service, int infoLevel, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref ServiceStatusProcess buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, ref ServiceDelayedAutoStartInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartService(IntPtr service, uint argCount, IntPtr args);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceDelayedAutoStartInfo
    {
        public int DelayedAutostart;
    }

    // QUERY_SERVICE_CONFIGW header (pointers follow; strings are read with Marshal).
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryServiceConfigHeader
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    // ---- advapi32: registry (no-follow opens) ----
    internal const uint RegOptionNonVolatile = 0x0;
    internal const uint RegOptionOpenLink = 0x8;
    internal const int KeyQueryValue = 0x0001;
    internal const int KeySetValue = 0x0002;
    internal const int KeyCreateSubKey = 0x0004;
    internal const int KeyEnumerateSubKeys = 0x0008;
    internal const int KeyWow64_64Key = 0x0100;
    internal const int RegLink = 6;
    internal const uint RegCreatedNewKey = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    internal static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, uint options, int samDesired, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    internal static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, uint reserved, string? cls, uint options, int samDesired, IntPtr security, out SafeRegistryHandle result, out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    internal static extern int RegQueryValueEx(SafeRegistryHandle key, string? valueName, IntPtr reserved, out int type, byte[]? data, ref int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    internal static extern int RegSetValueEx(SafeRegistryHandle key, string? valueName, int reserved, int type, byte[] data, int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegDeleteValueW")]
    internal static extern int RegDeleteValue(SafeRegistryHandle key, string? valueName);

    // ---- cfgmgr32 ----
    internal const int CrSuccess = 0;
    internal const int CrNoSuchDevnode = 0x0D;
    internal const int CrNoSuchValue = 0x25;
    internal const uint CmLocateDevnodeNormal = 0x0;
    internal const uint CmLocateDevnodePhantom = 0x1;
    internal const uint CmGetIdListFilterPresent = 0x100;
    internal const uint DnHasProblem = 0x00000400;
    internal const uint CmDrpClassGuid = 0x09;
    internal const uint CmDrpDriver = 0x0A;
    internal const uint CmDrpConfigFlags = 0x0B;
    internal const uint ConfigFlagDisabled = 0x1;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    internal static extern int CmLocateDevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    internal static extern int CmGetDevNodeStatus(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    internal static extern int CmGetDevNodeRegistryProperty(uint devInst, uint property, out uint regType, byte[]? buffer, ref uint length, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    internal static extern int CmGetParent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    internal static extern int CmGetDeviceId(uint devInst, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    internal static extern int CmGetDeviceIdListSize(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    internal static extern int CmGetDeviceIdList(string? filter, char[] buffer, uint length, uint flags);

    // ---- setupapi: state changes through the class installer (what Device Manager does) ----
    internal const uint DifPropertyChange = 0x00000012;
    internal const uint DicsEnable = 0x00000001;
    internal const uint DicsDisable = 0x00000002;
    internal const uint DicsPropChange = 0x00000003;
    internal const uint DicsFlagGlobal = 0x00000001;
    internal const uint DiNeedRestart = 0x00000080;
    internal const uint DiNeedReboot = 0x00000100;

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiOpenDeviceInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr hwndParent, uint flags, ref SpDevinfoData data);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetClassInstallParamsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SpDevinfoData data, ref SpPropChangeParams parameters, uint size);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SpDevinfoData data);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInstallParamsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref SpDevinfoData data, ref SpDevinstallParams parameters);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpClassInstallHeader
    {
        public uint CbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpPropChangeParams
    {
        public SpClassInstallHeader Header;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SpDevinstallParams
    {
        public uint CbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr HwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public IntPtr ClassInstallReserved;
        public uint Reserved;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DriverPath;
    }

    // ---- iphlpapi ----
    [DllImport("iphlpapi.dll")]
    internal static extern int ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);

    [DllImport("iphlpapi.dll")]
    internal static extern int ConvertInterfaceIndexToLuid(uint index, out ulong luid);

    [DllImport("iphlpapi.dll")]
    internal static extern int GetBestInterfaceEx(byte[] destAddr, out uint bestIfIndex);

    [DllImport("iphlpapi.dll")]
    internal static extern void InitializeIpInterfaceEntry(byte[] row);

    [DllImport("iphlpapi.dll")]
    internal static extern int GetIpInterfaceEntry(byte[] row);

    [DllImport("iphlpapi.dll")]
    internal static extern int SetIpInterfaceEntry(byte[] row);

    // ---- powrprof ----
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroup, uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);

    // ---- kernel32 ----
    internal const uint Th32csSnapProcess = 0x00000002;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

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

    // ---- user32: WM_CLOSE to top-level windows (no handle to the process) ----
    internal const uint WmClose = 0x0010;

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- kernel32: reparse-safe file handling ----
    internal const uint FileReadData = 0x0001;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint Delete = 0x00010000;
    internal const uint ReadControl = 0x00020000;
    internal const uint Synchronize = 0x00100000;
    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint FileShareDelete = 0x4;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileAttributeReparsePoint = 0x400;
    internal const uint FileAttributeDirectory = 0x10;
    internal const int FileBasicInfo = 0;
    internal const int FileStandardInfo = 1;
    internal const int FileRenameInfo = 3;
    internal const int FileAttributeTagInfo = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfoData info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileStandardInfo(SafeFileHandle file, int infoClass, out FileStandardInfoData info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileBasicInfo(SafeFileHandle file, int infoClass, out FileBasicInfoData info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, uint size);

    // ntdll: rename an open file relative to an open directory handle (kernel32's SetFileInformationByHandle rewrites
    // the name against the current directory, which breaks RootDirectory-relative renames).
    internal const int FileRenameInformationClass = 10;
    internal const uint StatusObjectNameCollision = 0xC0000035;
    internal const uint StatusNotSameDevice = 0xC00000D4;

    [DllImport("ntdll.dll")]
    internal static extern uint NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus, byte[] info, uint length, int infoClass);

    [DllImport("ntdll.dll")]
    internal static extern int RtlNtStatusToDosError(uint status);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] buffer, uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfoData
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInfoData
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfoData
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    // ---- advapi32: owner of a file object (through the no-follow handle) ----
    internal const int SeFileObject = 1;
    internal const uint OwnerSecurityInformation = 0x1;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);
}

/// <summary>A ToolHelp snapshot handle (not a process handle).</summary>
internal sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSnapshotHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
