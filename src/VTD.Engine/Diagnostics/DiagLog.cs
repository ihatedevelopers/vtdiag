using System.Globalization;
using System.Text;
using System.Text.Json;
using VTD.Contracts.Ipc;

namespace VTD.Engine.Diagnostics;

/// <summary>
/// The structured rolling log of the engine and (through <see cref="IEngineRpc.SubmitClientLogAsync"/>) of the UI: JSON
/// lines, size-capped, redacted AT WRITE TIME. Every free-text value goes through the scrub function (the engine passes
/// the share-level text redactor: no user names, tokens, SIDs, serials, machine name) before it touches the disk, so a
/// log file never holds more than a debug bundle may export. A failing disk never fails the caller.
/// </summary>
/// <remarks>
/// Layout: <c>vtd.log.jsonl</c> (current) plus <c>vtd.log.1.jsonl</c> .. <c>vtd.log.N.jsonl</c> (older). Debug-level entries
/// are dropped unless <see cref="DebugMode"/> is on (the Settings toggle: the player reproduces the problem with more
/// detail in the log). The log never records RPC payloads, only names, results and timings.
/// </remarks>
public sealed class DiagLog
{
    /// <summary>The current log file name.</summary>
    public const string CurrentFileName = "vtd.log.jsonl";

    /// <summary>Largest size of one file before it rolls.</summary>
    public const long DefaultMaxFileBytes = 256 * 1024;

    /// <summary>Files kept including the current one (total cap about 1.5 MB).</summary>
    public const int DefaultMaxFiles = 6;

    private const int MaxFieldChars = 8_000;

    private readonly object _gate = new();
    private readonly string? _folder;
    private readonly TimeProvider _clock;
    private readonly Func<string, string> _scrub;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private long _currentBytes = -1;
    private volatile bool _debug;

    /// <summary>Creates a log.</summary>
    /// <param name="folder">Log folder, or null for a log that discards everything.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="scrub">Share-level text redaction applied to every free-text value.</param>
    /// <param name="maxFileBytes">Size at which the current file rolls.</param>
    /// <param name="maxFiles">Files kept.</param>
    public DiagLog(string? folder, TimeProvider clock, Func<string, string> scrub, long maxFileBytes = DefaultMaxFileBytes, int maxFiles = DefaultMaxFiles)
    {
        _folder = folder;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scrub = scrub ?? throw new ArgumentNullException(nameof(scrub));
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _maxFiles = Math.Max(2, maxFiles);
    }

    /// <summary>A log that discards everything (tests that do not care).</summary>
    public static DiagLog Disabled { get; } = new(null, TimeProvider.System, s => s);

    /// <summary>The log of this process (set once the engine is built), for last-chance handlers.</summary>
    public static DiagLog? Current { get; set; }

    /// <summary>Log folder, or null.</summary>
    public string? Folder => _folder;

    /// <summary>Debug mode: Debug-level entries are kept and RPC calls get their timing.</summary>
    public bool DebugMode
    {
        get => _debug;
        set
        {
            if (_debug != value)
            {
                _debug = value;
                Write(ClientLogLevel.Info, "engine", "debug.mode", value ? "on" : "off");
            }
        }
    }

    /// <summary>True when an entry of this level would be kept.</summary>
    /// <param name="level">Level.</param>
    public bool IsEnabled(ClientLogLevel level) => _folder is not null && (level != ClientLogLevel.Debug || _debug);

    /// <summary>Writes an entry. Never throws.</summary>
    /// <param name="level">Level.</param>
    /// <param name="source">Writer: <c>engine</c>, <c>app</c> or <c>cli</c>.</param>
    /// <param name="evt">Event name (a constant, never user data).</param>
    /// <param name="message">Message (scrubbed).</param>
    /// <param name="data">Name/value details (values scrubbed).</param>
    /// <param name="exception">Exception: type, message and stack trace (scrubbed).</param>
    /// <param name="utc">Event time (default now).</param>
    public void Write(ClientLogLevel level, string source, string evt, string? message = null, IEnumerable<KeyValuePair<string, string?>>? data = null, Exception? exception = null, DateTimeOffset? utc = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        try
        {
            Append(Format(level, source, evt, message, data, exception?.ToString(), utc ?? _clock.GetUtcNow()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // Logging is best effort.
        }
    }

    /// <summary>Info entry.</summary>
    /// <param name="evt">Event.</param>
    /// <param name="message">Message.</param>
    /// <param name="pairs">Alternating names and values.</param>
    public void Info(string evt, string? message = null, params string?[] pairs) => Write(ClientLogLevel.Info, "engine", evt, message, Pairs(pairs));

    /// <summary>Warning.</summary>
    /// <param name="evt">Event.</param>
    /// <param name="message">Message.</param>
    /// <param name="pairs">Alternating names and values.</param>
    public void Warn(string evt, string? message = null, params string?[] pairs) => Write(ClientLogLevel.Warning, "engine", evt, message, Pairs(pairs));

    /// <summary>Debug (kept only in debug mode).</summary>
    /// <param name="evt">Event.</param>
    /// <param name="message">Message.</param>
    /// <param name="pairs">Alternating names and values.</param>
    public void Debug(string evt, string? message = null, params string?[] pairs) => Write(ClientLogLevel.Debug, "engine", evt, message, Pairs(pairs));

    /// <summary>Error with an exception.</summary>
    /// <param name="evt">Event.</param>
    /// <param name="exception">Exception.</param>
    /// <param name="pairs">Alternating names and values.</param>
    public void Error(string evt, Exception exception, params string?[] pairs)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(ClientLogLevel.Error, "engine", evt, exception.GetType().Name, Pairs(pairs), exception);
    }

    /// <summary>Writes the UI's entries (the UI cannot write files).</summary>
    /// <param name="entries">Entries (at most 200 are taken).</param>
    public void WriteClient(IReadOnlyList<ClientLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var e in entries.Take(200))
        {
            if (e.Event is not { Length: > 0 and <= 64 } name || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                continue;
            }

            if (!IsEnabled(e.Level))
            {
                continue;
            }

            try
            {
                Append(Format(e.Level, "app", name, e.Message, null, e.Detail, e.Utc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>The log files, oldest first, with their content (for the debug bundle).</summary>
    public IReadOnlyList<(string Name, string Text)> ReadAll()
    {
        if (_folder is null || !Directory.Exists(_folder))
        {
            return [];
        }

        var result = new List<(string, string)>();
        lock (_gate)
        {
            for (var i = _maxFiles - 1; i >= 0; i--)
            {
                var name = FileName(i);
                var path = Path.Combine(_folder, name);
                try
                {
                    if (File.Exists(path))
                    {
                        result.Add((name, File.ReadAllText(path, Encoding.UTF8)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file in use: skip it.
                }
            }
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<string, string?>> Pairs(string?[] pairs)
    {
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            yield return new KeyValuePair<string, string?>(pairs[i] ?? "?", pairs[i + 1]);
        }
    }

    private static string FileName(int index) => index == 0 ? CurrentFileName : string.Create(CultureInfo.InvariantCulture, $"vtd.log.{index}.jsonl");

    private string Format(ClientLogLevel level, string source, string evt, string? message, IEnumerable<KeyValuePair<string, string?>>? data, string? exception, DateTimeOffset utc)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("t", utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("lvl", level switch { ClientLogLevel.Debug => "debug", ClientLogLevel.Warning => "warn", ClientLogLevel.Error => "error", _ => "info" });
            w.WriteString("src", source);
            w.WriteString("evt", evt);
            if (message is not null)
            {
                w.WriteString("msg", Clean(message));
            }

            if (data is not null)
            {
                var any = false;
                foreach (var (key, value) in data)
                {
                    if (!any)
                    {
                        w.WriteStartObject("d");
                        any = true;
                    }

                    w.WriteString(key, value is null ? null : Clean(value));
                }

                if (any)
                {
                    w.WriteEndObject();
                }
            }

            if (exception is not null)
            {
                w.WriteString("ex", Clean(exception));
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private string Clean(string value)
    {
        var text = value.Length > MaxFieldChars ? value[..MaxFieldChars] + "..." : value;
        return _scrub(text);
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_folder!);
            var path = Path.Combine(_folder!, CurrentFileName);
            if (_currentBytes < 0)
            {
                _currentBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            }

            var bytes = Encoding.UTF8.GetBytes(line);
            if (_currentBytes > 0 && _currentBytes + bytes.Length > _maxFileBytes)
            {
                Roll();
                _currentBytes = 0;
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            _currentBytes += bytes.Length;
        }
    }

    private void Roll()
    {
        var oldest = Path.Combine(_folder!, FileName(_maxFiles - 1));
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _maxFiles - 2; i >= 0; i--)
        {
            var from = Path.Combine(_folder!, FileName(i));
            if (File.Exists(from))
            {
                File.Move(from, Path.Combine(_folder!, FileName(i + 1)), overwrite: true);
            }
        }
    }
}
