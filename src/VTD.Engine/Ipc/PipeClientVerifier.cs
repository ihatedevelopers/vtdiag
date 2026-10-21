using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using VTD.Contracts;

namespace VTD.Engine.Ipc;

/// <summary>Authenticode state of an image file.</summary>
public enum SignatureState
{
    /// <summary>Not checked or the file could not be read.</summary>
    Unknown = 0,

    /// <summary>No signature.</summary>
    Unsigned = 1,

    /// <summary>Signed and the signature verifies (chain to a trusted root, file unmodified).</summary>
    Valid = 2,

    /// <summary>Signed, but the signature does not verify (modified file, untrusted or revoked chain).</summary>
    Invalid = 3,
}

/// <summary>Authenticode result with the signer's certificate thumbprint (SHA-1 hex, uppercase) when signed.</summary>
/// <param name="State">State.</param>
/// <param name="SignerThumbprint">Signer thumbprint.</param>
public sealed record SignatureCheck(SignatureState State, string? SignerThumbprint);

/// <summary>
/// The engine's ONLY use of the pipe-peer identification APIs (audited exception, <c>banned/import-allowlist.txt</c>):
/// <c>GetNamedPipeClientProcessId</c>/<c>GetNamedPipeServerProcessId</c> → PID, then
/// <c>NtQuerySystemInformation(SystemProcessIdInformation)</c> → the image name of that PID WITHOUT opening any handle to
/// the process, <c>QueryDosDeviceW</c> → a Win32 path, and <c>WinVerifyTrust</c> → the file's signature. No other
/// information class is ever queried, and a PID whose image is a Riot/Vanguard process is refused before its file is
/// touched. Used by the engine to verify its clients and by the CLI to verify the engine it connects to.
/// </summary>
public static class PipeClientVerifier
{
    private const int SystemProcessIdInformation = 0x58;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxImageChars = 32_767;

    /// <summary>PID of the client at the other end of a server pipe, or null.</summary>
    /// <param name="pipe">Server end.</param>
    public static int? ClientProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeClientProcessId(pipe, out var pid) && pid != 0 ? (int)pid : null;
    }

    /// <summary>PID of the server at the other end of a client pipe, or null.</summary>
    /// <param name="pipe">Client end.</param>
    public static int? ServerProcessId(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeServerProcessId(pipe, out var pid) && pid != 0 ? (int)pid : null;
    }

    /// <summary>
    /// The full Win32 image path of a process, read from the kernel's process list by PID (no process handle), or null.
    /// </summary>
    /// <param name="processId">PID.</param>
    public static string? ImagePathOf(int processId)
    {
        if (processId <= 4)
        {
            return null;
        }

        var nt = NtImageName(processId);
        return nt is null ? null : ToDosPath(nt);
    }

    /// <summary>True when the image is one of the Riot/Vanguard processes VTDiag never touches.</summary>
    /// <param name="imagePath">Image path.</param>
    public static bool IsProtectedImage(string? imagePath) => imagePath is not null && RiotProcessNames.IsProtected(Path.GetFileName(imagePath));

    /// <summary>Verifies the Authenticode signature of a file (no network: cached revocation data only).</summary>
    /// <param name="path">File.</param>
    public static SignatureCheck VerifySignature(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsProtectedImage(path) || !File.Exists(path))
        {
            return new SignatureCheck(SignatureState.Unknown, null);
        }

        var result = WinVerifyTrustFile(path);
        string? thumbprint = null;
        if (result != TrustENoSignature)
        {
            try
            {
#pragma warning disable SYSLIB0057 // Authenticode signer of a PE file: X509CertificateLoader does not read signed files.
                using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                thumbprint = signer.GetCertHashString();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                thumbprint = null;
            }
        }

        var state = result switch
        {
            0 => SignatureState.Valid,
            TrustENoSignature => SignatureState.Unsigned,
            _ => SignatureState.Invalid,
        };
        return new SignatureCheck(state, thumbprint);
    }

    private static string? NtImageName(int processId)
    {
        var chars = 512;
        while (chars <= MaxImageChars)
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
                var status = NtQuerySystemInformation(SystemProcessIdInformation, ref info, Marshal.SizeOf<SystemProcessIdInformationStruct>(), out _);
                if (status == StatusInfoLengthMismatch)
                {
                    chars = Math.Max(chars * 2, (info.ImageName.MaximumLength / 2) + 1);
                    continue;
                }

                if (status < 0 || info.ImageName.Length == 0)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static string? ToDosPath(string ntPath)
    {
        // \Device\HarddiskVolume3\Program Files\... → C:\Program Files\...
        var target = new char[1024];
        foreach (var drive in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var device = drive + ":";
            var n = QueryDosDeviceW(device, target, (uint)target.Length);
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

    private const int TrustENoSignature = unchecked((int)0x800B0100);

    private static int WinVerifyTrustFile(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WintrustFileInfo { CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WintrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = filePtr,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProvFlags = 0x00000010 | 0x00001000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, ref SystemProcessIdInformationStruct systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
