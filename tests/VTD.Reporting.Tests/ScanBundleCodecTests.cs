using System.IO.Compression;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Assist;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary><c>.vtdscan</c> write/read, tamper and hostile-input handling.</summary>
public sealed class ScanBundleCodecTests
{
    internal static ScanBundleManifest Manifest(ScanSnapshot snapshot) => new()
    {
        Format = ScanBundleLayout.Format,
        BundleId = "01JB6R7X3K9M2N4P5Q6R7S8TE5",
        CreatedUtc = FakeClock.DefaultStart,
        AppVersion = FixtureSnapshots.AppVersion,
        KbVersion = FixtureSnapshots.KbVersion,
        SnapshotId = snapshot.Meta.SnapshotId,
        Redaction = RedactionLevel.Share,
        Parts = [],
    };

    private static ScanBundle Bundle()
    {
        var redactor = TestSupport.Redactor();
        var snapshot = redactor.Redact(TestSupport.Snapshot(Fixtures.RealtekStutter), RedactionPolicy.Share).Value;
        var analysis = redactor.Redact(CannedAnalyses.For(TestSupport.Snapshot(Fixtures.RealtekStutter)), RedactionPolicy.Share).Value;
        var monitor = redactor.Redact(TestSupport.FakeMonitorSummary(), RedactionPolicy.Share).Value;
        return new ScanBundle
        {
            Manifest = Manifest(snapshot),
            Snapshot = snapshot,
            Analysis = analysis,
            Monitor = [monitor],
            Journal = [new Contracts.Fixes.AttemptSummary { AttemptId = "01JB6R7X3K9M2N4P5Q6R7S8TE6", SessionId = "01JB6R7X3K9M2N4P5Q6R7S8TE7", FixId = "fix.net.disable_unused_adapter", TitleKey = "fix.disable_adapter.title", Status = Contracts.Fixes.AttemptStatus.Verified, Outcome = Contracts.Fixes.FixOutcome.Fixed, Reversibility = Contracts.Fixes.Reversibility.Exact, AppliedUtc = FakeClock.DefaultStart, CanUndo = true }],
            ReportHtml = "<!DOCTYPE html><html><body><p>report</p></body></html>",
        };
    }

    private static ScanBundleCodec Codec() => new(TestSupport.Redactor());

    [Fact]
    public void Round_trip_keeps_every_part_and_the_manifest_lists_hashes()
    {
        var bundle = Bundle();
        var bytes = Codec().Write(bundle);
        var read = Codec().Read(new MemoryStream(bytes));
        Assert.Equal(VtdJson.Serialize(bundle.Snapshot), VtdJson.Serialize(read.Snapshot));
        Assert.Equal(VtdJson.Serialize(bundle.Analysis!), VtdJson.Serialize(read.Analysis!));
        Assert.Equal(bundle.Journal.Count, read.Journal.Count);
        Assert.Single(read.Monitor);
        Assert.Equal(bundle.ReportHtml, read.ReportHtml);
        Assert.Equal(2, read.Manifest.Revision);
        Assert.Equal(ScanBundleLayout.Format, read.Manifest.Format);
        Assert.Equal(RedactionLevel.Share, read.Manifest.Redaction);
        Assert.Contains(read.Manifest.Parts, p => p.Path == ScanBundleLayout.Snapshot && p.Sha256.Length == 64 && p.ContentType == "application/json");
        Assert.Contains(read.Manifest.Parts, p => p.Path.StartsWith(ScanBundleLayout.MonitorFolder, StringComparison.Ordinal));
        Assert.Contains(read.Manifest.Parts, p => p.Path == ScanBundleLayout.Report && p.ContentType.StartsWith("text/html", StringComparison.Ordinal));
    }

    [Fact]
    public void Writing_is_deterministic()
        => Assert.Equal(Codec().Write(Bundle()), Codec().Write(Bundle()));

    [Fact]
    public void Refuses_a_non_share_snapshot()
    {
        var bundle = Bundle();
        var local = bundle with { Snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter) };
        Assert.Equal("not_redacted", Assert.Throws<ScanBundleException>(() => Codec().Write(local)).Code);
    }

    [Fact]
    public void A_changed_part_is_a_hash_mismatch()
    {
        var entries = Entries(Codec().Write(Bundle()));
        var snapshot = entries[ScanBundleLayout.Snapshot];
        snapshot[snapshot.Length / 2] ^= 0x01;
        Assert.Equal("hash_mismatch", Read(Zip(entries)).Code);
    }

    [Fact]
    public void A_flipped_byte_in_stored_entry_data_is_a_hash_mismatch()
    {
        var entries = Entries(Codec().Write(Bundle()));
        var bytes = Zip(entries, CompressionLevel.NoCompression);
        var marker = Encoding.UTF8.GetBytes("\"machineKey\"");
        var at = bytes.AsSpan().IndexOf(marker);
        Assert.True(at > 0);
        bytes[at + 3] ^= 0x01; // inside snapshot.redacted.json data; zip CRC or our SHA-256 catches it
        Assert.Equal("hash_mismatch", Read(bytes).Code);
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("monitor/../../evil.json")]
    [InlineData("C:/Windows/evil.json")]
    [InlineData("monitor\\x.json")]
    [InlineData("extra.txt")]
    public void Unknown_or_traversal_entries_are_bad_entries(string name)
    {
        var entries = Entries(Codec().Write(Bundle()));
        entries[name] = Encoding.UTF8.GetBytes("{}");
        Assert.Equal("bad_entry", Read(Zip(entries)).Code);
    }

    [Fact]
    public void Duplicate_entries_are_bad_entries()
    {
        var entries = Entries(Codec().Write(Bundle()));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries.Append(new KeyValuePair<string, byte[]>(ScanBundleLayout.Analysis, entries[ScanBundleLayout.Analysis])))
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }

        Assert.Equal("bad_entry", Read(ms.ToArray()).Code);
    }

    [Fact]
    public void An_unlisted_known_entry_is_a_bad_entry()
    {
        var entries = Entries(Codec().Write(Bundle()));
        entries["monitor/EXTRA.json"] = entries.First(e => e.Key.StartsWith("monitor/", StringComparison.Ordinal)).Value;
        Assert.Equal("bad_entry", Read(Zip(entries)).Code);
    }

    [Fact]
    public void A_missing_part_is_reported()
    {
        var entries = Entries(Codec().Write(Bundle()));
        entries.Remove(ScanBundleLayout.Analysis);
        Assert.Equal("missing_part", Read(Zip(entries)).Code);
    }

    [Fact]
    public void Oversized_entries_and_bombs_are_too_large()
    {
        var entries = Entries(Codec().Write(Bundle()));
        entries[ScanBundleLayout.Report] = new byte[(int)ScanBundleCodec.MaxEntryBytes + 1]; // zeros compress ~1000:1
        Assert.Equal("too_large", Read(Zip(entries)).Code);
    }

    [Fact]
    public void A_file_over_the_total_cap_is_too_large_before_unzipping()
    {
        var stream = new ChunkStream(ScanBundleCodec.MaxTotalBytes + 1);
        Assert.Equal("too_large", Assert.Throws<ScanBundleException>(() => Codec().Read(stream)).Code);
    }

    [Fact]
    public void Too_many_entries_is_too_large()
    {
        var entries = Entries(Codec().Write(Bundle()));
        for (var i = 0; i < ScanBundleCodec.MaxEntries; i++)
        {
            entries["monitor/m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"] = Encoding.UTF8.GetBytes("{}");
        }

        Assert.Equal("too_large", Read(Zip(entries)).Code);
    }

    [Theory]
    [InlineData("vtdscan/2")]
    [InlineData("vtdplan/1")]
    [InlineData("")]
    public void Unknown_format_major_is_rejected(string format)
    {
        var entries = Entries(Codec().Write(Bundle()));
        var manifest = Encoding.UTF8.GetString(entries[ScanBundleLayout.Manifest]).Replace("\"vtdscan/1\"", "\"" + format + "\"", StringComparison.Ordinal);
        entries[ScanBundleLayout.Manifest] = Encoding.UTF8.GetBytes(manifest);
        Assert.Equal("format", Read(Zip(entries)).Code);
    }

    [Fact]
    public void A_newer_minor_with_unknown_fields_still_reads()
    {
        var entries = Entries(Codec().Write(Bundle()));
        var manifest = Encoding.UTF8.GetString(entries[ScanBundleLayout.Manifest]).Replace("\"vtdscan/1\"", "\"vtdscan/1.3\", \"futureField\": 7", StringComparison.Ordinal);
        entries[ScanBundleLayout.Manifest] = Encoding.UTF8.GetBytes(manifest);
        Assert.Equal(FixtureSnapshots.RealtekId, Codec().Read(new MemoryStream(Zip(entries))).Snapshot.Meta.SnapshotId);
    }

    [Fact]
    public void Not_a_zip_is_a_format_error()
        => Assert.Equal("format", Read(Encoding.UTF8.GetBytes("this is not a zip file at all")).Code);

    [Fact]
    public void A_missing_manifest_is_a_format_error()
    {
        var entries = Entries(Codec().Write(Bundle()));
        entries.Remove(ScanBundleLayout.Manifest);
        Assert.Equal("format", Read(Zip(entries)).Code);
    }

    private static ScanBundleException Read(byte[] bytes)
        => Assert.Throws<ScanBundleException>(() => Codec().Read(new MemoryStream(bytes)));

    private static Dictionary<string, byte[]> Entries(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            result[entry.FullName] = ms.ToArray();
        }

        return result;
    }

    private static byte[] Zip(Dictionary<string, byte[]> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(data);
            }
        }

        return ms.ToArray();
    }

    /// <summary>A read-only stream of <c>length</c> zero bytes, produced lazily.</summary>
    private sealed class ChunkStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
