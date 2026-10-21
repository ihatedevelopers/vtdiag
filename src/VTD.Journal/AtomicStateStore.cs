using System.Text.Json;
using VTD.Contracts.Journaling;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;

namespace VTD.Journal;

/// <summary>
/// Atomic, durable state file (design_B.md section 6.2): <c>Save</c> writes <c>&lt;name&gt;.json.tmp</c> with
/// write-through, flushes it to disk, then <c>File.Replace(tmp, &lt;name&gt;.json, &lt;name&gt;.bak)</c>. <c>Load</c> falls back
/// from the primary to the backup and, when both are unreadable or fail validation, to an injected
/// <see cref="IStateRebuilder{T}"/> over the journal.
/// </summary>
/// <typeparam name="T">State type registered in the JSON contract.</typeparam>
public sealed class AtomicStateStore<T> : IStateStore<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _directory;
    private readonly bool _adminOnly;
    private readonly Func<T, bool>? _validate;
    private readonly IStateRebuilder<T>? _rebuilder;
    private readonly Func<IReadOnlyList<JournalRecord>>? _journalRecords;

    /// <summary>Creates the store.</summary>
    /// <param name="dataRoot">The VTDiag data root; <paramref name="directory"/> must be at or under it.</param>
    /// <param name="directory">Folder of the state files (for example <c>&lt;root&gt;\session</c>).</param>
    /// <param name="name">Base file name (default <c>current</c>).</param>
    /// <param name="adminOnly">Require and apply the admin-only ACL on the folder.</param>
    /// <param name="validate">Extra validation after a strict parse (false = treat as unreadable).</param>
    /// <param name="rebuilder">Rebuilds the state from the journal when both files are unreadable.</param>
    /// <param name="journalRecords">Supplies the journal records for the rebuilder (for example <see cref="FileJournal.ReadAll"/>).</param>
    public AtomicStateStore(
        string dataRoot,
        string directory,
        string name = "current",
        bool adminOnly = true,
        Func<T, bool>? validate = null,
        IStateRebuilder<T>? rebuilder = null,
        Func<IReadOnlyList<JournalRecord>>? journalRecords = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid state file name.", nameof(name));
        }

        _root = dataRoot;
        _directory = directory;
        _adminOnly = adminOnly;
        _validate = validate;
        _rebuilder = rebuilder;
        _journalRecords = journalRecords;
        PrimaryPath = Path.Combine(directory, name + ".json");
        BackupPath = Path.Combine(directory, name + ".bak");
        TempPath = Path.Combine(directory, name + ".json.tmp");
    }

    /// <summary>The primary file.</summary>
    public string PrimaryPath { get; }

    /// <summary>The backup file.</summary>
    public string BackupPath { get; }

    /// <summary>The temporary file written first.</summary>
    public string TempPath { get; }

    /// <inheritdoc />
    public StateLoadResult<T> Load()
    {
        lock (_gate)
        {
            var issues = new List<string>();
            if (TryRead(PrimaryPath, "primary", issues, out var primary))
            {
                return new StateLoadResult<T> { State = primary, Source = StateLoadSource.Primary, Issues = issues };
            }

            if (TryRead(BackupPath, "backup", issues, out var backup))
            {
                return new StateLoadResult<T> { State = backup, Source = StateLoadSource.Backup, Issues = issues };
            }

            var anyFile = issues.Any(i => i.EndsWith(".unreadable", StringComparison.Ordinal) || i.EndsWith(".invalid", StringComparison.Ordinal));
            if (_rebuilder is not null && _journalRecords is not null)
            {
                T? rebuilt;
                try
                {
                    rebuilt = _rebuilder.Rebuild(_journalRecords());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    issues.Add("journal.unreadable");
                    rebuilt = null;
                }

                if (rebuilt is not null)
                {
                    return new StateLoadResult<T> { State = rebuilt, Source = StateLoadSource.RebuiltFromJournal, Issues = issues };
                }

                if (anyFile)
                {
                    issues.Add("journal.empty");
                }
            }

            return new StateLoadResult<T> { Source = StateLoadSource.Empty, Issues = issues };
        }
    }

    /// <inheritdoc />
    public void Save(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = VtdJson.Serialize(state);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        lock (_gate)
        {
            SecureDirectory.EnsureUnder(_root, _directory, _adminOnly);
            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(PrimaryPath))
            {
                File.Replace(TempPath, PrimaryPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TempPath, PrimaryPath);
            }
        }
    }

    private bool TryRead(string path, string label, List<string> issues, out T? state)
    {
        state = null;
        if (!File.Exists(path))
        {
            issues.Add(label + ".missing");
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var value = VtdJson.Deserialize<T>(bytes);
            if (_validate is not null && !_validate(value))
            {
                issues.Add(label + ".invalid");
                return false;
            }

            state = value;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            issues.Add(label + ".unreadable");
            return false;
        }
    }
}
