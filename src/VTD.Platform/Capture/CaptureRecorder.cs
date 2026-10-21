using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VTD.Contracts.Scanning;
using VTD.Platform.Sources;

namespace VTD.Platform.Capture;

/// <summary>
/// Records raw probe outputs during a scan (<c>vtdiag scan --capture</c>). Every source read is recorded once under a
/// deterministic key; a replay serves the same keys back. Text content only ever arrives here already filtered
/// (see <see cref="LineFilter"/>).
/// </summary>
public sealed class CaptureRecorder
{
    private static readonly AsyncLocal<string?> Collector = new();
    private readonly ConcurrentDictionary<string, RawCaptureEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>The collector the current async flow is running (stamped on each entry).</summary>
    public static string? CurrentCollector
    {
        get => Collector.Value;
        set => Collector.Value = value;
    }

    /// <summary>Entries recorded so far, sorted by source key (stable across runs).</summary>
    public IReadOnlyList<RawCaptureEntry> Entries => _entries.Values.OrderBy(e => e.Source, StringComparer.Ordinal).ToList();

    /// <summary>Records the header entry.</summary>
    /// <param name="meta">Meta.</param>
    public void RecordMeta(CaptureMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        _entries[CaptureFormat.MetaSource] = new RawCaptureEntry
        {
            CollectorId = "orchestrator",
            Source = CaptureFormat.MetaSource,
            Payload = JsonSerializer.Serialize(meta, PlatformJsonContext.Default.CaptureMeta),
        };
    }

    internal T Record<T>(string source, T value, JsonTypeInfo<T> info)
    {
        _entries.TryAdd(source, new RawCaptureEntry
        {
            CollectorId = CurrentCollector ?? "unknown",
            Source = source,
            Payload = JsonSerializer.Serialize(value, info),
        });
        return value;
    }
}

/// <summary>Capture keys shared by the capturing and replaying sources.</summary>
internal static class CaptureKeys
{
    public static string Registry(RegHive hive, string path) => "registry:" + (hive == RegHive.Users ? "HKU\\" : "HKLM\\") + path;

    public static string Wmi(string ns, string wql) => "wmi:" + ns + ":" + wql;

    public static string Stat(string path) => "file.stat:" + path;

    public static string List(string path, string pattern, bool recursive, int max)
        => string.Create(CultureInfo.InvariantCulture, $"file.list:{path}|{pattern}|{(recursive ? "r" : "n")}|{max}");

    public static string Lines(string path, string filterId, long maxBytes, int maxLines)
        => string.Create(CultureInfo.InvariantCulture, $"file.lines:{filterId}:{path}|{maxBytes}|{maxLines}");

    public static string Header(string path, int count) => string.Create(CultureInfo.InvariantCulture, $"file.header:{path}|{count}");

    public static string Version(string path) => "file.version:" + path;

    public static string Shortcut(string path) => "file.lnk:" + path;

    public static string Events(EventQuery q) => "events:" + q.Key;

    public static string Devices(IReadOnlyList<string> classes) => "sys:devices:" + string.Join(',', classes);

    public static string Firmware(bool privileged) => privileged ? "sys:firmware:privileged" : "sys:firmware";

    public static string BitLocker(string drive) => "sys:bitlocker:" + drive;

    public static string Ping(string address, int count) => string.Create(CultureInfo.InvariantCulture, $"probe:ping:{address}|{count}");
}
