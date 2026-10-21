using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VTD.Contracts.Serialization;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;
using VTD.Journal;
using static VTD.Fixes.Interop.NativeMethods;

namespace VTD.Fixes.Interop;

/// <summary>
/// File operations for the quarantine, hardened against the elevated-operation-on-player-writable-path class (critique S-a):
/// <list type="bullet">
/// <item>Every open uses <c>FILE_FLAG_OPEN_REPARSE_POINT</c>; a file or folder that IS a reparse point is refused.</item>
/// <item>Every folder from the drive root to the source and to the destination is opened the same way and must have a
/// trusted owner (SYSTEM, Administrators, TrustedInstaller).</item>
/// <item>The file is opened once (share-read only, so nobody can write it meanwhile); its final path must equal the
/// requested path (catches junctions anywhere above it), it must have exactly one hard link and a trusted owner, and its
/// SHA-256 read through that same handle must match.</item>
/// <item>The move is a rename of THAT handle into a destination folder given as a verified, no-follow directory handle
/// (<c>FILE_RENAME_INFO.RootDirectory</c>), never replacing an existing file and never across volumes. Nothing is copied
/// and nothing is deleted.</item>
/// </list>
/// </summary>
public sealed class ReparseSafeFilePort : IFilePort
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileTraverse = 0x0020;
    private readonly string _dataRoot;
    private readonly bool _adminOnly;
    private readonly IReadOnlyList<string> _trustedOwners;

    /// <summary>Creates the port.</summary>
    /// <param name="folders">OS folders.</param>
    /// <param name="dataRoot">VTDiag data root (<c>%ProgramData%\VTDiag</c>).</param>
    /// <param name="adminOnly">Require and apply the admin-only ACL on VTDiag folders (production: true).</param>
    /// <param name="trustedOwners">Owner SIDs trusted on every path component (production: <see cref="SecureDirectory.TrustedOwners"/>).</param>
    public ReparseSafeFilePort(KnownFolders folders, string dataRoot, bool adminOnly, IReadOnlyList<string>? trustedOwners = null)
    {
        Folders = folders ?? throw new ArgumentNullException(nameof(folders));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(dataRoot);
        _adminOnly = adminOnly;
        _trustedOwners = trustedOwners ?? SecureDirectory.TrustedOwners;
        QuarantineRoot = Path.Combine(_dataRoot, "quarantine");
    }

    /// <inheritdoc />
    public KnownFolders Folders { get; }

    /// <inheritdoc />
    public string QuarantineRoot { get; }

    /// <summary>The OS folders of this PC.</summary>
    public static KnownFolders SystemFolders() => new()
    {
        Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    };

    /// <inheritdoc />
    public FileFacts Probe(string path)
    {
        RequirePath(path);
        using var h = OpenNoFollow(path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, directory: false);
        if (h is null)
        {
            return new FileFacts { Exists = false };
        }

        var tag = Tag(h);
        if ((tag.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
        {
            return new FileFacts { Exists = false };
        }

        DateTimeOffset? lastWrite = GetFileBasicInfo(h, FileBasicInfo, out var basic, (uint)Marshal.SizeOf<FileBasicInfoData>()) && basic.LastWriteTime > 0
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(basic.LastWriteTime), TimeSpan.Zero)
            : null;
        return new FileFacts { Exists = true, Size = Standard(h).EndOfFile, LastWriteUtc = lastWrite };
    }

    /// <inheritdoc />
    public string Sha256(string path)
    {
        RequirePath(path);
        using var h = OpenNoFollow(path, FileReadData | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, directory: false)
            ?? throw new FileNotFoundException("File not found.", path);
        if ((Tag(h).FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
        {
            throw new PortRefusedException("file.reparse_point");
        }

        return Hash(h);
    }

    /// <inheritdoc />
    public void SafeMove(string source, string destination, string expectedSha256)
    {
        RequirePath(source);
        RequirePath(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortRefusedException("file.cross_volume");
        }

        var destDir = Path.GetDirectoryName(destination)!;
        if (IsUnder(destDir, QuarantineRoot))
        {
            SecureDirectory.EnsureUnder(_dataRoot, destDir, _adminOnly);
        }

        CheckChain(Path.GetDirectoryName(source)!);
        CheckChain(destDir);

        using var file = OpenNoFollow(source, Delete | FileReadData | FileReadAttributes | ReadControl | Synchronize, FileShareRead, directory: false)
            ?? throw new FileNotFoundException("File not found.", source);
        var tag = Tag(file);
        if ((tag.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
        {
            throw new PortRefusedException("file.reparse_point");
        }

        if (Standard(file).NumberOfLinks != 1)
        {
            throw new PortRefusedException("file.hard_links");
        }

        if (!string.Equals(FinalPath(file), source, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortRefusedException("file.path_redirected");
        }

        if (!TrustedOwner(file))
        {
            throw new PortRefusedException("file.untrusted_owner");
        }

        if (!string.Equals(Hash(file), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortRefusedException("file.hash_mismatch");
        }

        using var dir = OpenNoFollow(destDir, FileListDirectory | FileTraverse | FileReadAttributes | ReadControl | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, directory: true)
            ?? throw new DirectoryNotFoundException("Destination folder not found.");
        var dtag = Tag(dir);
        if ((dtag.FileAttributes & FileAttributeReparsePoint) != 0 || (dtag.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new PortRefusedException("file.reparse_point");
        }

        if (!string.Equals(FinalPath(dir), Path.TrimEndingDirectorySeparator(destDir), StringComparison.OrdinalIgnoreCase) || !TrustedOwner(dir))
        {
            throw new PortRefusedException("file.path_redirected");
        }

        Rename(file, dir, Path.GetFileName(destination));
        if (!string.Equals(FinalPath(file), destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The file did not land where it was moved.");
        }
    }

    /// <inheritdoc />
    public void WriteManifest(string folder, IReadOnlyDictionary<string, string> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequirePath(folder);
        if (!IsUnder(folder, QuarantineRoot))
        {
            throw new PortRefusedException("file.manifest_outside_quarantine");
        }

        SecureDirectory.EnsureUnder(_dataRoot, folder, _adminOnly);
        CheckChain(folder);
        var path = Path.Combine(folder, "manifest.json");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(VtdJson.Serialize(manifest)));
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        RequirePath(path);
        using var h = OpenNoFollow(path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, directory: true);
        if (h is null)
        {
            return false;
        }

        var tag = Tag(h);
        return (tag.FileAttributes & FileAttributeDirectory) != 0 && (tag.FileAttributes & FileAttributeReparsePoint) == 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListDirectories(string path)
    {
        if (!DirectoryExists(path))
        {
            return [];
        }

        return [.. new DirectoryInfo(path).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName)];
    }

    /// <summary>Checks every folder from the drive root down to <paramref name="directory"/>: real directory, trusted owner.</summary>
    /// <param name="directory">Full folder path.</param>
    public void CheckChain(string directory)
    {
        RequirePath(Path.Combine(directory, "x"));
        var root = Path.GetPathRoot(directory)!;
        var current = root;
        CheckOne(current);
        foreach (var part in directory[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            CheckOne(current);
        }
    }

    private void CheckOne(string directory)
    {
        using var h = OpenNoFollow(directory, FileReadAttributes | ReadControl, FileShareRead | FileShareWrite | FileShareDelete, directory: true)
            ?? throw new DirectoryNotFoundException("A folder on the path does not exist.");
        var tag = Tag(h);
        if ((tag.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new PortRefusedException("file.reparse_point");
        }

        if ((tag.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new PortRefusedException("file.not_a_folder");
        }

        if (!TrustedOwner(h))
        {
            throw new PortRefusedException("file.untrusted_owner");
        }
    }

    private static SafeFileHandle? OpenNoFollow(string path, uint access, uint share, bool directory)
    {
        var flags = FileFlagOpenReparsePoint | (directory ? FileFlagBackupSemantics : 0);
        var h = CreateFile(@"\\?\" + path, access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err is ErrorFileNotFound or 3)
            {
                return null;
            }

            throw new Win32Exception(err);
        }

        return h;
    }

    private static FileAttributeTagInfoData Tag(SafeFileHandle h)
        => GetFileInformationByHandleEx(h, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfoData>())
            ? info
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static FileStandardInfoData Standard(SafeFileHandle h)
        => GetFileStandardInfo(h, FileStandardInfo, out var info, (uint)Marshal.SizeOf<FileStandardInfoData>())
            ? info
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static string FinalPath(SafeFileHandle h)
    {
        var buffer = new char[32768];
        var n = GetFinalPathNameByHandle(h, buffer, (uint)buffer.Length, 0);
        if (n == 0 || n >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var path = new string(buffer, 0, (int)n);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private bool TrustedOwner(SafeFileHandle h)
    {
        var rc = GetSecurityInfo(h, SeFileObject, OwnerSecurityInformation, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var sd);
        try
        {
            if (rc != 0 || owner == IntPtr.Zero)
            {
                return false;
            }

            var sid = new SecurityIdentifier(owner).Value;
            return _trustedOwners.Contains(sid, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (sd != IntPtr.Zero)
            {
                LocalFree(sd);
            }
        }
    }

    private static string Hash(SafeFileHandle h)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long offset = 0;
        while (true)
        {
            var read = RandomAccess.Read(h, buffer, offset);
            if (read <= 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static void Rename(SafeFileHandle file, SafeFileHandle directory, string name)
    {
        // FILE_RENAME_INFO (x64): Flags/ReplaceIfExists @0 (4), pad, RootDirectory @8 (8), FileNameLength @16 (4), FileName @20.
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var buffer = new byte[20 + nameBytes.Length + 2];
        var added = false;
        directory.DangerousAddRef(ref added);
        try
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 0u);
            BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), directory.DangerousGetHandle().ToInt64());
            BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), (uint)nameBytes.Length);
            nameBytes.CopyTo(buffer, 20);
            var status = NtSetInformationFile(file, out _, buffer, (uint)buffer.Length, FileRenameInformationClass);
            if (status != 0)
            {
                throw status switch
                {
                    StatusNotSameDevice => new PortRefusedException("file.cross_volume"),
                    StatusObjectNameCollision => new PortRefusedException("file.destination_exists"),
                    _ => new Win32Exception(RtlNtStatusToDosError(status)),
                };
            }
        }
        finally
        {
            if (added)
            {
                directory.DangerousRelease();
            }
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var r = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequirePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!ArgValidator.IsAbsoluteLocalPath(path))
        {
            throw new ArgumentException("Not an absolute local path.", nameof(path));
        }
    }
}
