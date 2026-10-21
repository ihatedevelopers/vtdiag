using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VTD.App.Services;

/// <summary>
/// The UI's ONLY P/Invoke (audited exception, <c>banned/rs0030-allowlist.txt</c> and <c>banned/import-allowlist.txt</c>):
/// finds out who is really at the other end of the pipe the UI just opened, so a squatter that created the pipe name
/// first can never receive the player's consent. <c>GetNamedPipeServerProcessId</c> gives the PID, then
/// <c>NtQuerySystemInformation(SystemProcessIdInformation)</c> gives that PID's image name WITHOUT opening any handle to
/// the process, and <c>QueryDosDeviceW</c> turns it into a Win32 path. No other information class is ever queried.
/// </summary>
public static class PipeServerVerifier
{
    private const int SystemProcessIdInformation = 0x58;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxImageChars = 32_767;

    /// <summary>PID of the server at the other end of a client pipe, or null.</summary>
    /// <param name="pipe">Client end.</param>
    public static int? ServerProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
#pragma warning disable RS0030 // Audited: the file is allowlisted.
        return GetNamedPipeServerProcessId(pipe, out var pid) && pid != 0 ? (int)pid : null;
#pragma warning restore RS0030
    }

    /// <summary>The full Win32 image path of a process, read from the kernel's process list by PID (no process handle), or null.</summary>
    /// <param name="processId">PID.</param>
    public static string? ImagePathOf(int processId)
    {
        if (processId <= 4)
        {
            return null;
        }

        var chars = 512;
        string? nt = null;
        while (chars <= MaxImageChars && nt is null)
        {
            var bytes = chars * 2;
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                var info = new SystemProcessIdInformationStruct
                {
                    ProcessId = processId,
                    ImageName = new UnicodeString { Length = 0, MaximumLength = (ushort)Math.Min(bytes, ushort.MaxValue - 1), Buffer = buffer },
                };
#pragma warning disable RS0030 // Audited: SystemProcessIdInformation only.
                var status = NtQuerySystemInformation(SystemProcessIdInformation, ref info, Marshal.SizeOf<SystemProcessIdInformationStruct>(), out _);
#pragma warning restore RS0030
                if (status == StatusInfoLengthMismatch)
                {
                    chars = Math.Max(chars * 2, (info.ImageName.MaximumLength / 2) + 1);
                    continue;
                }

                if (status < 0 || info.ImageName.Length == 0)
                {
                    return null;
                }

                nt = Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return nt is null ? null : ToDosPath(nt);
    }

    private static string? ToDosPath(string ntPath)
    {
        var target = new char[1024];
        foreach (var drive in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var device = drive + ":";
#pragma warning disable RS0030 // Audited: the file is allowlisted.
            var n = QueryDosDeviceW(device, target, (uint)target.Length);
#pragma warning restore RS0030
            if (n == 0)
            {
                continue;
            }

            var end = Array.IndexOf(target, '\0');
            var mapped = new string(target, 0, end < 0 ? (int)Math.Min(n, (uint)target.Length) : end);
            if (mapped.Length > 0 && ntPath.StartsWith(mapped + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return device + ntPath[mapped.Length..];
            }
        }

        return null;
    }

#pragma warning disable RS0030 // Audited: the file is allowlisted.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, ref SystemProcessIdInformationStruct systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);
#pragma warning restore RS0030

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInformationStruct
    {
        public nint ProcessId;
        public UnicodeString ImageName;
    }
}
