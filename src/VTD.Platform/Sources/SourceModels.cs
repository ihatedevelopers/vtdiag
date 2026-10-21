namespace VTD.Platform.Sources;

/// <summary>Outcome of one raw read. Collectors map it to <see cref="VTD.Contracts.UnknownReason"/>.</summary>
public enum ReadStatus
{
    /// <summary>Read.</summary>
    Ok = 0,

    /// <summary>The key, file, class or log does not exist.</summary>
    NotFound = 1,

    /// <summary>Access denied (elevation or ACL).</summary>
    AccessDenied = 2,

    /// <summary>The call failed.</summary>
    Failed = 3,

    /// <summary>The call did not answer in time.</summary>
    TimedOut = 4,

    /// <summary>The API or class is not available on this Windows build.</summary>
    Unsupported = 5,
}

/// <summary>Registry roots the read side may open.</summary>
public enum RegHive
{
    /// <summary>HKEY_LOCAL_MACHINE (64-bit view; WOW6432Node is addressed by path).</summary>
    LocalMachine = 0,

    /// <summary>HKEY_USERS (the player's hive is <c>HKU\&lt;sid&gt;</c>).</summary>
    Users = 1,
}

/// <summary>One registry value as invariant text.</summary>
public sealed record RegValue
{
    /// <summary>Value name (empty for the default value).</summary>
    public required string Name { get; init; }

    /// <summary><c>String</c>, <c>ExpandString</c>, <c>MultiString</c>, <c>DWord</c>, <c>QWord</c>, <c>Binary</c>, <c>None</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Data: strings raw (unexpanded), multi-strings joined by <c>\n</c>, numbers as unsigned decimal, binary as lowercase hex.</summary>
    public string? Data { get; init; }
}

/// <summary>A registry key: its values and subkey names, read in one call.</summary>
public sealed record RegKeyData
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Values.</summary>
    public IReadOnlyList<RegValue> Values { get; init; } = [];

    /// <summary>Subkey names.</summary>
    public IReadOnlyList<string> SubKeys { get; init; } = [];

    /// <summary>Machine error detail.</summary>
    public string? Error { get; init; }
}

/// <summary>Result of a WQL query. Values are invariant strings; arrays are comma-joined.</summary>
public sealed record WmiResult
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Rows (property name to invariant value; null properties are omitted).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; } = [];

    /// <summary>Machine error detail (for example <c>HRESULT 0x80041003</c>).</summary>
    public string? Error { get; init; }
}

/// <summary>File or directory metadata.</summary>
public sealed record FileStat
{
    /// <summary>Status (NotFound when it does not exist).</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Is a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Size in bytes (files).</summary>
    public long Size { get; init; }

    /// <summary>Last write (UTC).</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>Creation (UTC).</summary>
    public DateTimeOffset? CreatedUtc { get; init; }
}

/// <summary>One directory entry.</summary>
public sealed record DirEntry
{
    /// <summary>Path relative to the listed directory (backslashes).</summary>
    public required string Name { get; init; }

    /// <summary>Is a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Last write (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Creation (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>A directory listing.</summary>
public sealed record DirListing
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Entries (capped).</summary>
    public IReadOnlyList<DirEntry> Entries { get; init; } = [];

    /// <summary>More entries existed than were returned.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Maps one raw text line to what may leave the file source: the transformed line, or null to drop it. Filters are
/// applied INSIDE the source, so raw lines (which can carry tokens) never reach collectors, captures or tests.
/// </summary>
/// <param name="Id">Stable id (part of the capture key, so a replay uses the same filter output).</param>
/// <param name="Map">The mapping.</param>
public sealed record LineFilter(string Id, Func<string, string?> Map);

/// <summary>Filtered lines of a text file.</summary>
public sealed record LinesResult
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Lines the filter kept (already transformed).</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>Raw lines read (kept or not).</summary>
    public int LinesRead { get; init; }

    /// <summary>The file was larger than the read limit (the tail was not read).</summary>
    public bool Truncated { get; init; }

    /// <summary>Last write (UTC).</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }
}

/// <summary>The first bytes of a binary file (headers only, never content).</summary>
public sealed record HeaderResult
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Lowercase hex of the bytes read.</summary>
    public string Hex { get; init; } = string.Empty;
}

/// <summary>Version resource of a file.</summary>
public sealed record FileVersionResult
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary><c>FileVersion</c> numbers (major.minor.build.private), when present.</summary>
    public string? FileVersion { get; init; }

    /// <summary><c>ProductVersion</c> string.</summary>
    public string? ProductVersion { get; init; }
}

/// <summary>Target of a shell link (.lnk) parsed from its LinkInfo / relative path, no COM.</summary>
public sealed record ShortcutResult
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Target path (no arguments).</summary>
    public string? TargetPath { get; init; }
}

/// <summary>An event-log query (provider/id/level/time XPath; only projected EventData fields are returned).</summary>
public sealed record EventQuery
{
    /// <summary>Log name.</summary>
    public required string Log { get; init; }

    /// <summary>Provider names (any); empty = all providers.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>Event ids (any); empty = all.</summary>
    public IReadOnlyList<int> EventIds { get; init; } = [];

    /// <summary>Levels 1..5 (any); empty = all.</summary>
    public IReadOnlyList<int> Levels { get; init; } = [];

    /// <summary>Only events at or after this time.</summary>
    public required DateTimeOffset SinceUtc { get; init; }

    /// <summary>Max events read (newest first).</summary>
    public int Max { get; init; } = 200;

    /// <summary>EventData field names to keep. Unnamed data items are named <c>#0</c>, <c>#1</c>...</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Stable key for capture/replay.</summary>
    public string Key => string.Join(
        ';',
        Log,
        string.Join(',', Providers),
        string.Join(',', EventIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        string.Join(',', Levels.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        SinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        Max.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Join(',', Fields));
}

/// <summary>One event record.</summary>
public sealed record EventRec
{
    /// <summary>Provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>Event id.</summary>
    public required int Id { get; init; }

    /// <summary>Level.</summary>
    public int Level { get; init; }

    /// <summary>Time created (UTC).</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>Projected EventData fields.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>Result of an event query.</summary>
public sealed record EventQueryResult
{
    /// <summary>Status.</summary>
    public required ReadStatus Status { get; init; }

    /// <summary>Records, newest first.</summary>
    public IReadOnlyList<EventRec> Records { get; init; } = [];

    /// <summary>The query hit <see cref="EventQuery.Max"/>.</summary>
    public bool Truncated { get; init; }

    /// <summary>Error detail.</summary>
    public string? Error { get; init; }
}
