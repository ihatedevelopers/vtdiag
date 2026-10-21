using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using VTD.Contracts.Journaling;

namespace VTD.Journal;

/// <summary>Where the journal lives and how its folder is protected.</summary>
public sealed record JournalOptions
{
    /// <summary>The VTDiag data root (<c>%ProgramData%\VTDiag</c>); the journal folder must be at or under it.</summary>
    public required string DataRoot { get; init; }

    /// <summary>The journal folder (default <c>&lt;DataRoot&gt;\journal</c>).</summary>
    public string? Directory { get; init; }

    /// <summary>Require and apply the admin-only ACL on the folders (production: true; temp-folder tests: false).</summary>
    public bool AdminOnlyAcl { get; init; } = true;

    /// <summary>The journal folder actually used.</summary>
    public string JournalDirectory => Directory ?? Path.Combine(DataRoot, "journal");
}

/// <summary>A record the reader skipped or reordered (reported by recovery, never silently dropped).</summary>
/// <param name="File">Journal file name.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Code">Issue code (<c>line.crc_mismatch</c>, <c>line.no_crc</c>, <c>line.bad_json</c>, <c>line.torn_tail</c>,
/// <c>seq.duplicate</c>, <c>seq.out_of_order</c>, <c>session.mismatch</c>...).</param>
public sealed record JournalIssue(string File, long Line, string Code);

/// <summary>One read of the journal: the valid records in sequence order plus everything that was skipped.</summary>
/// <param name="Records">Valid records, ordered by <see cref="JournalRecord.Seq"/>, duplicates removed.</param>
/// <param name="Issues">Skipped or reordered lines.</param>
public sealed record JournalReadResult(IReadOnlyList<JournalRecord> Records, IReadOnlyList<JournalIssue> Issues);

/// <summary>
/// The write-ahead journal on disk (design_B.md section 6.2): <c>&lt;journal&gt;\&lt;sessionId&gt;.jsonl</c>, one
/// <see cref="JournalLine"/> per record, opened with write-through and flushed to disk (FlushFileBuffers) before
/// <see cref="AppendAsync"/> returns. Sequence numbers are monotonic across all session files. A torn tail (power cut in
/// the middle of a write) is skipped on read and the next append starts on a fresh line, so one torn record never
/// corrupts the next. Any IO failure is a <see cref="JournalWriteException"/>: the caller aborts the change before it starts.
/// </summary>
public sealed partial class FileJournal : IJournal
{
    private readonly object _gate = new();
    private readonly JournalOptions _options;
    private long _lastSeq = -1;

    /// <summary>Creates the journal (the folder is created and checked on first use).</summary>
    /// <param name="options">Options.</param>
    public FileJournal(JournalOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The journal folder.</summary>
    public string Directory => _options.JournalDirectory;

    /// <inheritdoc />
    public ValueTask<JournalRecord> AppendAsync(JournalRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var path = SessionPath(record.SessionId);
        lock (_gate)
        {
            try
            {
                EnsureDirectory();
                if (_lastSeq < 0)
                {
                    _lastSeq = ScanMaxSeq();
                }

                var stored = record with { Seq = _lastSeq + 1 };
                var line = JournalLine.Encode(stored);
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    var needsNewline = false;
                    if (stream.Length > 0)
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        needsNewline = stream.ReadByte() != '\n';
                    }

                    stream.Seek(0, SeekOrigin.End);
                    if (needsNewline)
                    {
                        stream.WriteByte((byte)'\n');
                    }

                    stream.Write(line);
                    stream.Flush(flushToDisk: true);
                }

                _lastSeq = stored.Seq;
                return ValueTask.FromResult(stored);
            }
            catch (IOException ex) when (ex is not JournalWriteException)
            {
                throw new JournalWriteException("The journal record could not be made durable.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new JournalWriteException("The journal folder is not writable.", ex);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalRecord> ReadAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = ReadSession(sessionId);
        foreach (var r in result.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return r;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = ReadAll();
        foreach (var r in result.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return r;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Reads one session file with the list of skipped lines.</summary>
    /// <param name="sessionId">Session.</param>
    public JournalReadResult ReadSession(string sessionId)
    {
        var path = SessionPath(sessionId);
        var records = new List<JournalRecord>();
        var issues = new List<JournalIssue>();
        lock (_gate)
        {
            if (File.Exists(path))
            {
                ReadFile(path, sessionId, records, issues);
            }
        }

        return Order(records, issues);
    }

    /// <summary>Reads every session file (undo center, recovery) with the list of skipped lines.</summary>
    public JournalReadResult ReadAll()
    {
        var records = new List<JournalRecord>();
        var issues = new List<JournalIssue>();
        lock (_gate)
        {
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.jsonl").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var session = Path.GetFileNameWithoutExtension(file);
                    if (SessionIdPattern().IsMatch(session))
                    {
                        ReadFile(file, session, records, issues);
                    }
                }
            }
        }

        return Order(records, issues);
    }

    /// <summary>Session ids become file names: ULIDs or short safe tokens only.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    private static JournalReadResult Order(List<JournalRecord> records, List<JournalIssue> issues)
    {
        var seen = new HashSet<long>();
        var unique = new List<JournalRecord>(records.Count);
        long last = long.MinValue;
        foreach (var r in records)
        {
            if (!seen.Add(r.Seq))
            {
                issues.Add(new JournalIssue(r.SessionId + ".jsonl", r.Seq, "seq.duplicate"));
                continue;
            }

            if (r.Seq < last)
            {
                issues.Add(new JournalIssue(r.SessionId + ".jsonl", r.Seq, "seq.out_of_order"));
            }

            last = Math.Max(last, r.Seq);
            unique.Add(r);
        }

        return new JournalReadResult([.. unique.OrderBy(r => r.Seq)], issues);
    }

    private static void ReadFile(string path, string sessionId, List<JournalRecord> records, List<JournalIssue> issues)
    {
        var name = Path.GetFileName(path);
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        long lineNo = 0;
        var start = 0;
        while (start < bytes.Length)
        {
            lineNo++;
            var nl = Array.IndexOf(bytes, (byte)'\n', start);
            var torn = nl < 0;
            var end = torn ? bytes.Length : nl;
            var span = bytes.AsSpan(start, end - start);
            start = torn ? bytes.Length : nl + 1;
            if (span.Length == 0)
            {
                continue;
            }

            var record = JournalLine.TryDecode(span, out var issue);
            if (record is null)
            {
                issues.Add(new JournalIssue(name, lineNo, torn ? "line.torn_tail" : issue ?? "line.bad_json"));
                continue;
            }

            if (!string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
            {
                issues.Add(new JournalIssue(name, lineNo, "session.mismatch"));
                continue;
            }

            records.Add(record);
        }
    }

    private string SessionPath(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (!SessionIdPattern().IsMatch(sessionId))
        {
            throw new ArgumentException("A session id must be a ULID or a short [A-Za-z0-9_-] token.", nameof(sessionId));
        }

        return Path.Combine(Directory, sessionId + ".jsonl");
    }

    private void EnsureDirectory() => SecureDirectory.EnsureUnder(_options.DataRoot, Directory, _options.AdminOnlyAcl);

    private long ScanMaxSeq()
    {
        var max = 0L;
        if (!System.IO.Directory.Exists(Directory))
        {
            return max;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.jsonl"))
        {
            var session = Path.GetFileNameWithoutExtension(file);
            if (!SessionIdPattern().IsMatch(session))
            {
                continue;
            }

            var records = new List<JournalRecord>();
            ReadFile(file, session, records, []);
            if (records.Count > 0)
            {
                max = Math.Max(max, records.Max(r => r.Seq));
            }
        }

        return max;
    }
}
