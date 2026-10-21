using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;

namespace VTD.Reporting;

/// <summary>
/// Reads and writes <c>.vtdscan</c> bundles in memory (design_B.md section 9): a zip with <see cref="ScanBundleLayout"/>
/// entry names and a <c>manifest.json</c> listing every part with SHA-256, size and content type. Writing is
/// deterministic (fixed entry order and timestamps) and refuses anything that is not share-level redacted or that the
/// sweep flags. Reading treats the file as hostile: size caps, entry-count and compression-ratio guards, only known
/// entry names (no traversal, no duplicates), format-major check, hash and size verification, then lenient
/// deserialization (a newer app may add fields).
/// </summary>
public sealed partial class ScanBundleCodec : IScanBundleCodec
{
    /// <summary>Largest uncompressed entry.</summary>
    public const long MaxEntryBytes = 32L * 1024 * 1024;

    /// <summary>Largest bundle (compressed file, and the sum of uncompressed entries).</summary>
    public const long MaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>Most entries.</summary>
    public const int MaxEntries = 64;

    /// <summary>Largest uncompressed/compressed ratio for entries above 1 MB (zip-bomb guard).</summary>
    public const int MaxCompressionRatio = 100;

    private const int CurrentFormatMajor = 1;
    private static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IRedactor _redactor;

    /// <summary>Creates the codec.</summary>
    /// <param name="redactor">Redactor (sweep on write).</param>
    public ScanBundleCodec(IRedactor redactor) => _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    /// <inheritdoc />
    public byte[] Write(ScanBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Snapshot.Meta.Redaction.Level != RedactionLevel.Share)
        {
            throw new ScanBundleException(ScanBundleException.Codes.NotRedacted, "A .vtdscan only ever holds a share-level redacted snapshot.");
        }

        var parts = new List<(string Name, byte[] Bytes, string ContentType, string Section)>
        {
            (ScanBundleLayout.Snapshot, Utf8(VtdJson.Serialize(bundle.Snapshot)), "application/json", "snapshot"),
        };
        if (bundle.Analysis is not null)
        {
            parts.Add((ScanBundleLayout.Analysis, Utf8(VtdJson.Serialize(bundle.Analysis)), "application/json", "analysis"));
        }

        if (bundle.Journal.Count > 0)
        {
            parts.Add((ScanBundleLayout.Journal, Utf8(VtdJson.Serialize(bundle.Journal)), "application/json", "attempts"));
        }

        var monitorNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < bundle.Monitor.Count; i++)
        {
            var summary = bundle.Monitor[i];
            var stem = Ulid.IsValid(summary.SummaryId) ? summary.SummaryId : i.ToString(CultureInfo.InvariantCulture);
            var name = ScanBundleLayout.MonitorFolder + stem + ".json";
            if (!monitorNames.Add(name))
            {
                name = ScanBundleLayout.MonitorFolder + stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".json";
                monitorNames.Add(name);
            }

            parts.Add((name, Utf8(VtdJson.Serialize(summary)), "application/json", "monitor"));
        }

        if (bundle.ReportHtml is not null)
        {
            parts.Add((ScanBundleLayout.Report, Utf8(bundle.ReportHtml), "text/html; charset=utf-8", "report"));
        }

        var hits = new List<SweepHit>();
        foreach (var part in parts)
        {
            var text = Encoding.UTF8.GetString(part.Bytes);
            var json = part.ContentType.StartsWith("application/json", StringComparison.Ordinal) ? text : VtdJson.Serialize(text);
            foreach (var hit in _redactor.Sweep(json))
            {
                hits.Add(hit with
                {
                    Path = part.Name + "#" + hit.Path,
                    Section = string.Equals(part.Section, "snapshot", StringComparison.Ordinal) ? hit.Section : part.Section,
                });
            }
        }

        if (hits.Count > 0)
        {
            throw new ScanBundleException(ScanBundleException.Codes.SweepBlocked, "The sweep found data that must not leave this PC.", hits: hits);
        }

        var manifest = bundle.Manifest with
        {
            Format = ScanBundleLayout.Format,
            Revision = bundle.Journal.Count > 0 ? Math.Max(2, bundle.Manifest.Revision) : Math.Max(1, bundle.Manifest.Revision),
            SnapshotId = bundle.Snapshot.Meta.SnapshotId,
            Redaction = RedactionLevel.Share,
            Parts = [.. parts.Select(p => new BundlePart { Path = p.Name, Sha256 = CanonicalHash.OfBytes(p.Bytes), Size = p.Bytes.LongLength, ContentType = p.ContentType })],
        };

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, ScanBundleLayout.Manifest, Utf8(VtdJson.Serialize(manifest, indented: true)));
            foreach (var part in parts)
            {
                AddEntry(zip, part.Name, part.Bytes);
            }
        }

        return output.ToArray();
    }

    /// <inheritdoc />
    public ScanBundle Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > MaxTotalBytes)
        {
            throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "The bundle is larger than 64 MB.");
        }

        var raw = ReadCapped(stream, MaxTotalBytes, ScanBundleException.Codes.TooLarge, "The bundle is larger than 64 MB.");
        CheckDeclaredEntryCount(raw);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(raw, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new ScanBundleException(ScanBundleException.Codes.Format, "Not a valid zip file.", ex);
        }

        using (zip)
        {
            Dictionary<string, byte[]> entries;
            try
            {
                entries = ReadEntries(zip);
            }
            catch (InvalidDataException ex)
            {
                throw new ScanBundleException(ScanBundleException.Codes.Format, "The zip directory is corrupt.", ex);
            }

            if (!entries.TryGetValue(ScanBundleLayout.Manifest, out var manifestBytes))
            {
                throw new ScanBundleException(ScanBundleException.Codes.Format, "manifest.json is missing.");
            }

            var manifest = ParseManifest(manifestBytes);
            VerifyParts(manifest, entries);

            var snapshotBytes = entries.TryGetValue(ScanBundleLayout.Snapshot, out var s)
                ? s
                : throw new ScanBundleException(ScanBundleException.Codes.Format, "snapshot.redacted.json is missing.");
            var snapshot = Lenient<ScanSnapshot>(snapshotBytes, ScanBundleLayout.Snapshot);
            if (manifest.Redaction != RedactionLevel.Share || snapshot.Meta.Redaction.Level != RedactionLevel.Share)
            {
                throw new ScanBundleException(ScanBundleException.Codes.NotRedacted, "The bundle's snapshot is not share-level redacted.");
            }

            return new ScanBundle
            {
                Manifest = manifest,
                Snapshot = snapshot,
                Analysis = entries.TryGetValue(ScanBundleLayout.Analysis, out var a) ? Lenient<AnalysisResult>(a, ScanBundleLayout.Analysis) : null,
                Journal = entries.TryGetValue(ScanBundleLayout.Journal, out var j) ? Lenient<IReadOnlyList<AttemptSummary>>(j, ScanBundleLayout.Journal) : [],
                Monitor =
                [
                    .. entries.Keys
                        .Where(k => k.StartsWith(ScanBundleLayout.MonitorFolder, StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal)
                        .Select(k => Lenient<MonitorSummary>(entries[k], k)),
                ],
                ReportHtml = entries.TryGetValue(ScanBundleLayout.Report, out var html) ? Encoding.UTF8.GetString(html) : null,
            };
        }
    }

    [GeneratedRegex(@"^monitor/[A-Za-z0-9_-]{1,64}\.json$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MonitorEntry();

    private static bool IsKnownName(string name)
        => name is ScanBundleLayout.Manifest or ScanBundleLayout.Snapshot or ScanBundleLayout.Analysis or ScanBundleLayout.Journal or ScanBundleLayout.Report
           || MonitorEntry().IsMatch(name);

    /// <summary>
    /// Reads the entry count from the end-of-central-directory record BEFORE the zip library materializes the directory
    /// (a small file can declare hundreds of thousands of entries). ZIP64 is refused: VTDiag never writes it.
    /// </summary>
    private static void CheckDeclaredEntryCount(byte[] raw)
    {
        const int EocdSize = 22;
        var min = Math.Max(0, raw.Length - EocdSize - ushort.MaxValue);
        for (var i = raw.Length - EocdSize; i >= min; i--)
        {
            if (raw[i] == 0x50 && raw[i + 1] == 0x4B && raw[i + 2] == 0x05 && raw[i + 3] == 0x06)
            {
                var total = raw[i + 10] | (raw[i + 11] << 8);
                if (total == 0xFFFF)
                {
                    throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "ZIP64 bundles are not supported.");
                }

                if (total > MaxEntries)
                {
                    throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "The bundle has too many entries.");
                }

                return;
            }
        }

        throw new ScanBundleException(ScanBundleException.Codes.Format, "Not a valid zip file.");
    }

    private static Dictionary<string, byte[]> ReadEntries(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "The bundle has too many entries.");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!IsKnownName(name))
            {
                throw new ScanBundleException(ScanBundleException.Codes.BadEntry, "Unknown entry in the bundle.");
            }

            if (result.ContainsKey(name))
            {
                throw new ScanBundleException(ScanBundleException.Codes.BadEntry, "Duplicate entry in the bundle.");
            }

            if (entry.Length > MaxEntryBytes || total + entry.Length > MaxTotalBytes)
            {
                throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "An entry is too large.");
            }

            if (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > MaxCompressionRatio)
            {
                throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "An entry is compressed suspiciously well (zip bomb guard).");
            }

            byte[] bytes;
            try
            {
                using var es = entry.Open();
                bytes = ReadCapped(es, Math.Min(MaxEntryBytes, entry.Length), ScanBundleException.Codes.TooLarge, "An entry is larger than its header says.");
            }
            catch (InvalidDataException ex)
            {
                throw new ScanBundleException(ScanBundleException.Codes.HashMismatch, "An entry's data is corrupt.", ex);
            }

            total += bytes.LongLength;
            if (total > MaxTotalBytes)
            {
                throw new ScanBundleException(ScanBundleException.Codes.TooLarge, "The bundle is larger than 64 MB uncompressed.");
            }

            result[name] = bytes;
        }

        return result;
    }

    private static ScanBundleManifest ParseManifest(byte[] bytes)
    {
        try
        {
            using (var doc = JsonDocument.Parse(bytes))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("format", out var format)
                    || format.ValueKind != JsonValueKind.String
                    || !IsSupportedFormat(format.GetString()!))
                {
                    throw new ScanBundleException(ScanBundleException.Codes.Format, "Unknown .vtdscan format.");
                }
            }

            return VtdJson.DeserializeLenient<ScanBundleManifest>(Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException ex)
        {
            throw new ScanBundleException(ScanBundleException.Codes.Format, "manifest.json is not valid.", ex);
        }
    }

    private static bool IsSupportedFormat(string format)
    {
        const string prefix = "vtdscan/";
        if (!format.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var majorText = format[prefix.Length..].Split('.')[0];
        return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major == CurrentFormatMajor;
    }

    private static void VerifyParts(ScanBundleManifest manifest, Dictionary<string, byte[]> entries)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in manifest.Parts)
        {
            if (!IsKnownName(part.Path) || string.Equals(part.Path, ScanBundleLayout.Manifest, StringComparison.Ordinal) || !listed.Add(part.Path))
            {
                throw new ScanBundleException(ScanBundleException.Codes.BadEntry, "The manifest lists an invalid part.");
            }

            if (!entries.TryGetValue(part.Path, out var bytes))
            {
                throw new ScanBundleException(ScanBundleException.Codes.MissingPart, "A part listed in the manifest is missing.");
            }

            if (bytes.LongLength != part.Size || !string.Equals(CanonicalHash.OfBytes(bytes), part.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ScanBundleException(ScanBundleException.Codes.HashMismatch, "A part does not match its SHA-256 in the manifest.");
            }
        }

        foreach (var name in entries.Keys)
        {
            if (!string.Equals(name, ScanBundleLayout.Manifest, StringComparison.Ordinal) && !listed.Contains(name))
            {
                throw new ScanBundleException(ScanBundleException.Codes.BadEntry, "The bundle has an entry the manifest does not list.");
            }
        }
    }

    private static T Lenient<T>(byte[] bytes, string name)
    {
        try
        {
            return VtdJson.DeserializeLenient<T>(Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException ex)
        {
            throw new ScanBundleException(ScanBundleException.Codes.Format, name + " is not valid.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ScanBundleException(ScanBundleException.Codes.Format, name + " is not valid.", ex);
        }
    }

    private static byte[] ReadCapped(Stream stream, long cap, string code, string message)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > cap)
            {
                throw new ScanBundleException(code, message);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
