using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Ipc;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Session;

namespace VTD.Engine.Platform;

/// <summary>
/// Where the engine keeps its files. Elevated: <c>%ProgramData%\VTDiag</c> with a PROTECTED DACL (SYSTEM and
/// Administrators full control, nothing inherited from ProgramData, which lets users create folders), the interactive
/// player may only read <c>reports\</c> and <c>engine.json</c>; a folder that is a reparse point, or that a
/// non-administrator owns or can write, is never used as is (critique S-a). Limited: <c>%LocalAppData%\VTDiag</c> of the
/// player; nothing is written to ProgramData.
/// </summary>
public sealed class EngineStorage
{
    private EngineStorage(string root, bool elevated, string? playerSid)
    {
        Root = root;
        Elevated = elevated;
        PlayerSid = playerSid;
    }

    /// <summary>Root folder.</summary>
    public string Root { get; }

    /// <summary>Elevated layout.</summary>
    public bool Elevated { get; }

    /// <summary>Player allowed to read reports.</summary>
    public string? PlayerSid { get; }

    /// <summary>Session folder.</summary>
    public string SessionFolder => Path.Combine(Root, "session");

    /// <summary>Journal folder.</summary>
    public string JournalFolder => Path.Combine(Root, "journal");

    /// <summary>Raw snapshots (elevated only).</summary>
    public string SnapshotsFolder => Path.Combine(Root, "snapshots");

    /// <summary>Rolling JSON-lines logs (redacted at write time).</summary>
    public string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>Exported reports.</summary>
    public string ReportsFolder => Path.Combine(Root, "reports");

    /// <summary>Discovery file.</summary>
    public string DiscoveryPath => Path.Combine(Root, Elevated ? EngineProtocol.DiscoveryFileName : "engine-limited.json");

    /// <summary>The elevated, ACL'd layout under <c>%ProgramData%\VTDiag</c> (created or repaired).</summary>
    /// <param name="playerSid">Player (read access to reports and the discovery file).</param>
    public static EngineStorage ForElevated(string playerSid)
    {
        ArgumentNullException.ThrowIfNull(playerSid);
        var root = ElevatedRoot();
        var storage = new EngineStorage(root, true, playerSid);
        storage.SecureRoot();
        return storage;
    }

    /// <summary>The elevated root path (not created).</summary>
    public static string ElevatedRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VTDiag");

    /// <summary>The limited layout under the player's own <c>%LocalAppData%\VTDiag</c>.</summary>
    /// <param name="localAppData">Player's LocalAppData.</param>
    public static EngineStorage ForLimited(string localAppData)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        return new EngineStorage(Path.Combine(localAppData, "VTDiag"), false, null);
    }

    /// <summary>True when a path (or any existing parent up to the drive) is a reparse point (symlink/junction).</summary>
    /// <param name="path">Path.</param>
    public static bool HasReparsePoint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Creates a sub-folder of the root (inherits the root's protected DACL).</summary>
    /// <param name="folder">Folder.</param>
    public string Ensure(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (HasReparsePoint(folder))
        {
            throw new IOException("storage.reparse_point");
        }

        Directory.CreateDirectory(folder);
        if (HasReparsePoint(folder))
        {
            throw new IOException("storage.reparse_point");
        }

        return folder;
    }

    /// <summary>Per-install salt (machine key, share ids). Created once; never exported.</summary>
    public byte[] LoadOrCreateSalt()
    {
        var path = Path.Combine(Ensure(Root), "install.salt");
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 32)
            {
                return bytes;
            }
        }

        var salt = RandomNumberGenerator.GetBytes(32);
        WriteFile(path, salt, playerReadable: false);
        return salt;
    }

    /// <summary>The revocation state seen so far (serial and every revoked key/version), or an empty one.</summary>
    public KbRevocationList LoadRevocations(DateTimeOffset now)
    {
        var path = Path.Combine(Root, "kb-trust.json");
        try
        {
            if (File.Exists(path))
            {
                return VtdJson.Deserialize<KbRevocationList>(File.ReadAllText(path));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: start from the bundled lists (revocation then only grows again).
        }

        return new KbRevocationList { Serial = 0, IssuedUtc = now };
    }

    /// <summary>Persists the revocation state when it grew (revocation only ever grows).</summary>
    /// <param name="previous">What was loaded.</param>
    /// <param name="result">KB load result.</param>
    /// <param name="now">Now.</param>
    public void SaveRevocations(KbRevocationList previous, KbLoadResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(result);
        var next = new KbRevocationList
        {
            Serial = Math.Max(previous.Serial, result.RevocationSerial),
            IssuedUtc = now,
            RevokedKeyIds = [.. previous.RevokedKeyIds.Union(result.RevokedKeyIds, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            RevokedVersions = [.. previous.RevokedVersions.Union(result.RevokedVersions, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
        if (next.Serial == previous.Serial && next.RevokedKeyIds.SequenceEqual(previous.RevokedKeyIds) && next.RevokedVersions.SequenceEqual(previous.RevokedVersions))
        {
            return;
        }

        WriteFile(Path.Combine(Ensure(Root), "kb-trust.json"), Encoding.UTF8.GetBytes(VtdJson.Serialize(next)), playerReadable: false);
    }

    /// <summary>Writes the discovery file (player-readable; only administrators can write it).</summary>
    /// <param name="discovery">Discovery.</param>
    public void WriteDiscovery(EngineDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        WriteFile(DiscoveryPath, Encoding.UTF8.GetBytes(VtdJson.Serialize(discovery)), playerReadable: true);
    }

    /// <summary>Removes the discovery file (engine exit).</summary>
    public void DeleteDiscovery()
    {
        try
        {
            File.Delete(DiscoveryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a stale file is harmless (clients verify the PID and the pipe).
        }
    }

    /// <summary>Atomic write: temp file, flush to disk, replace.</summary>
    /// <param name="path">Target.</param>
    /// <param name="bytes">Content.</param>
    /// <param name="playerReadable">Grant the player read access (elevated layout).</param>
    public void WriteFile(string path, byte[] bytes, bool playerReadable)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        var directory = Ensure(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(directory, Path.GetFileName(path) + ".tmp");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (Elevated)
        {
            new FileInfo(temp).SetAccessControl(FileAcl(playerReadable));
        }

        File.Move(temp, path, overwrite: true);
    }

    private FileSecurity FileAcl(bool playerReadable)
    {
        var acl = new FileSecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (playerReadable && PlayerSid is not null)
        {
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(PlayerSid), FileSystemRights.Read, AccessControlType.Allow));
        }

        return acl;
    }

    private void SecureRoot()
    {
        if (HasReparsePoint(Root))
        {
            throw new IOException("storage.reparse_point");
        }

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectorySecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));

        // The player may traverse the root and list it (to find engine.json and reports), nothing more.
        if (PlayerSid is not null)
        {
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(PlayerSid), FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }

        var root = new DirectoryInfo(Root);
        if (!root.Exists)
        {
            root.Create(acl);
        }
        else
        {
            // A pre-existing folder could have been created by anyone (ProgramData lets users create folders): take
            // ownership for Administrators and replace the whole DACL.
            root.SetAccessControl(acl);
        }

        if (HasReparsePoint(Root))
        {
            throw new IOException("storage.reparse_point");
        }

        var reports = new DirectoryInfo(Ensure(ReportsFolder));
        var reportsAcl = reports.GetAccessControl();
        if (PlayerSid is not null)
        {
            reportsAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(PlayerSid), FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
            reports.SetAccessControl(reportsAcl);
        }
    }
}

/// <summary>
/// <see cref="IArtifactStore"/> that also keeps raw (local-level) snapshots, analyses and monitor summaries on disk in
/// the admin-only <c>snapshots\</c> folder, so a restarted elevated engine can still show and export the session.
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private const int Keep = 20;
    private readonly MemoryArtifactStore _memory = new();
    private readonly EngineStorage _storage;

    /// <summary>Creates the store.</summary>
    /// <param name="storage">Storage (elevated).</param>
    public FileArtifactStore(EngineStorage storage) => _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    /// <inheritdoc />
    public void Put(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _memory.Put(snapshot);
        Write(snapshot.Meta.SnapshotId + ".snapshot.json", VtdJson.Serialize(snapshot));
    }

    /// <inheritdoc />
    public void Put(AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        _memory.Put(analysis);
        Write(analysis.AnalysisId + ".analysis.json", VtdJson.Serialize(analysis));
    }

    /// <inheritdoc />
    public void Put(MonitorSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _memory.Put(summary);
        Write(summary.SummaryId + ".monitor.json", VtdJson.Serialize(summary));
    }

    /// <inheritdoc />
    public ScanSnapshot? Snapshot(string? snapshotId) => _memory.Snapshot(snapshotId) ?? Read<ScanSnapshot>(snapshotId, ".snapshot.json");

    /// <inheritdoc />
    public AnalysisResult? Analysis(string? analysisId) => _memory.Analysis(analysisId) ?? Read<AnalysisResult>(analysisId, ".analysis.json");

    /// <inheritdoc />
    public MonitorSummary? Summary(string? summaryId) => _memory.Summary(summaryId) ?? Read<MonitorSummary>(summaryId, ".monitor.json");

    private void Write(string name, string json)
    {
        try
        {
            var folder = _storage.Ensure(_storage.SnapshotsFolder);
            _storage.WriteFile(Path.Combine(folder, name), Encoding.UTF8.GetBytes(json), playerReadable: false);
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Keep * 3))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory copy still serves this engine run.
        }
    }

    private T? Read<T>(string? id, string suffix)
        where T : class
    {
        if (id is null || !Ulid.IsValid(id))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(_storage.SnapshotsFolder, id + suffix);
            return File.Exists(path) ? VtdJson.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
