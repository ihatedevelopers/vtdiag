using System.Diagnostics;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;

namespace VTD.Journal.Tests;

/// <summary>A temp folder deleted at the end of the test (the only place these tests write).</summary>
public sealed class TempDir : IDisposable
{
    /// <summary>Creates the folder.</summary>
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VTDiag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Full path.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            // Junctions are removed as links (never followed into their target).
            foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
            {
                Directory.Delete(d);
            }

            Directory.Delete(Path, recursive: true);
            var parent = System.IO.Path.GetDirectoryName(Path)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Journal line format, durability, torn writes, corruption fuzz, ordering, failures.</summary>
public sealed class JournalTests
{
    private const string Session = "01JB6R7X3K9M2N4P5Q6R7S8TB0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Crc32_matches_the_standard_check_value()
        => Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void A_line_round_trips_and_any_flipped_byte_is_detected()
    {
        var record = Record(1) with { Seq = 7, Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "a|crc=00000000 \"quoted\" çğ" } };
        var line = JournalLine.Encode(record);
        Assert.Equal((byte)'\n', line[^1]);
        Assert.Single(line, b => b == (byte)'\n');
        var back = JournalLine.TryDecode(line.AsSpan(0, line.Length - 1), out var issue);
        Assert.Null(issue);
        Assert.Equal(record.Args["name"], back!.Args["name"]);
        Assert.Equal(7, back.Seq);

        for (var i = 0; i < line.Length - 1; i++)
        {
            var copy = (byte[])line.Clone();
            copy[i] ^= 0x20;
            var r = JournalLine.TryDecode(copy.AsSpan(0, copy.Length - 1), out _);
            if (r is not null)
            {
                // Only the case of a CRC hex digit can flip harmlessly; the content must then be exactly the original.
                Assert.Equal(VTD.Contracts.Serialization.VtdJson.Serialize(back), VTD.Contracts.Serialization.VtdJson.Serialize(r));
            }
        }
    }

    [Fact]
    public async Task Appends_are_durable_ordered_and_survive_a_restart()
    {
        using var dir = new TempDir();
        var journal = new FileJournal(Options(dir));
        var a = await journal.AppendAsync(Record(0), Ct);
        var b = await journal.AppendAsync(Record(1), Ct);
        var c = await journal.AppendAsync(Record(0, session: "SESSION2"), Ct);
        Assert.Equal([1L, 2L, 3L], new[] { a.Seq, b.Seq, c.Seq });

        var reopened = new FileJournal(Options(dir));
        var d = await reopened.AppendAsync(Record(2), Ct);
        Assert.Equal(4, d.Seq);
        var session = reopened.ReadSession(Session);
        Assert.Equal([1L, 2L, 4L], session.Records.Select(r => r.Seq));
        Assert.Empty(session.Issues);
        Assert.Equal([1L, 2L, 3L, 4L], reopened.ReadAll().Records.Select(r => r.Seq));
        var listed = new List<JournalRecord>();
        await foreach (var r in reopened.ReadAllAsync(Ct))
        {
            listed.Add(r);
        }

        Assert.Equal(4, listed.Count);
    }

    [Fact]
    public async Task A_torn_tail_is_skipped_and_the_next_record_starts_on_a_fresh_line()
    {
        using var dir = new TempDir();
        var journal = new FileJournal(Options(dir));
        await journal.AppendAsync(Record(0), Ct);
        await journal.AppendAsync(Record(1), Ct);
        var file = System.IO.Path.Combine(journal.Directory, Session + ".jsonl");

        // Power cut in the middle of the third write.
        var torn = JournalLine.Encode(Record(2) with { Seq = 3 });
        await using (var s = new FileStream(file, FileMode.Append))
        {
            s.Write(torn, 0, torn.Length / 2);
        }

        var read = journal.ReadSession(Session);
        Assert.Equal([1L, 2L], read.Records.Select(r => r.Seq));
        Assert.Contains(read.Issues, i => i.Code == "line.torn_tail");

        var next = await new FileJournal(Options(dir)).AppendAsync(Record(3), Ct);
        var after = new FileJournal(Options(dir)).ReadSession(Session);
        Assert.Equal([1L, 2L, next.Seq], after.Records.Select(r => r.Seq));
        Assert.Contains(after.Issues, i => i.Code is "line.crc_mismatch" or "line.no_crc" or "line.bad_json");
    }

    [Fact]
    public async Task Random_corruption_never_yields_a_wrong_record_and_never_throws()
    {
        using var dir = new TempDir();
        var journal = new FileJournal(Options(dir));
        var written = new List<JournalRecord>();
        for (var i = 0; i < 40; i++)
        {
            written.Add(await journal.AppendAsync(Record(i % 5), Ct));
        }

        var file = System.IO.Path.Combine(journal.Directory, Session + ".jsonl");
        var original = await File.ReadAllBytesAsync(file, Ct);
        var rng = new Random(20261021);
        for (var round = 0; round < 200; round++)
        {
            var bytes = (byte[])original.Clone();
            var kind = round % 4;
            switch (kind)
            {
                case 0:
                    for (var k = 0; k < 1 + rng.Next(8); k++)
                    {
                        bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
                    }

                    break;
                case 1:
                    bytes = bytes[..rng.Next(bytes.Length)];
                    break;
                case 2:
                    var at = rng.Next(bytes.Length);
                    bytes = [.. bytes[..at], .. Encoding.UTF8.GetBytes("garbage\n{\"x\":1}|crc=00000000\n"), .. bytes[at..]];
                    break;
                default:
                    var cut = rng.Next(bytes.Length);
                    bytes = [.. bytes[..cut], .. bytes[Math.Min(bytes.Length, cut + rng.Next(200))..]];
                    break;
            }

            await File.WriteAllBytesAsync(file, bytes, Ct);
            var read = new FileJournal(Options(dir)).ReadSession(Session);
            foreach (var r in read.Records)
            {
                var same = written.Single(w => w.Seq == r.Seq);
                Assert.Equal(same.Phase, r.Phase);
                Assert.Equal(same.StepIndex, r.StepIndex);
                Assert.Equal(same.Utc, r.Utc);
            }

            Assert.Equal(read.Records.Select(r => r.Seq).Order(), read.Records.Select(r => r.Seq));
        }
    }

    [Fact]
    public async Task Duplicates_and_reordering_are_reported_and_resolved()
    {
        using var dir = new TempDir();
        var journal = new FileJournal(Options(dir));
        for (var i = 0; i < 4; i++)
        {
            await journal.AppendAsync(Record(i), Ct);
        }

        var file = System.IO.Path.Combine(journal.Directory, Session + ".jsonl");
        var lines = (await File.ReadAllLinesAsync(file, Ct)).ToList();
        lines.Insert(2, lines[0]);           // duplicate of seq 1
        (lines[3], lines[4]) = (lines[4], lines[3]); // swap seq 2 and 3
        await File.WriteAllLinesAsync(file, lines, Ct);
        var read = journal.ReadSession(Session);
        Assert.Equal([1L, 2L, 3L, 4L], read.Records.Select(r => r.Seq));
        Assert.Contains(read.Issues, i => i.Code == "seq.duplicate");
        Assert.Contains(read.Issues, i => i.Code == "seq.out_of_order");

        // A record of another session pasted into this file is not trusted.
        var foreign = Encoding.UTF8.GetString(JournalLine.Encode(Record(9, session: "OTHER") with { Seq = 99 }));
        await File.AppendAllTextAsync(file, foreign, Ct);
        var again = journal.ReadSession(Session);
        Assert.DoesNotContain(again.Records, r => r.Seq == 99);
        Assert.Contains(again.Issues, i => i.Code == "session.mismatch");
    }

    [Fact]
    public async Task A_write_that_cannot_be_made_durable_throws_JournalWriteException()
    {
        using var dir = new TempDir();
        var journal = new FileJournal(Options(dir));
        await journal.AppendAsync(Record(0), Ct);
        var file = System.IO.Path.Combine(journal.Directory, Session + ".jsonl");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        await Assert.ThrowsAsync<JournalWriteException>(async () => await journal.AppendAsync(Record(1), Ct));
        File.SetAttributes(file, FileAttributes.Normal);
        Assert.Single(journal.ReadSession(Session).Records);
        await Assert.ThrowsAsync<ArgumentException>(async () => await journal.AppendAsync(Record(1, session: @"..\evil"), Ct));
    }

    [Fact]
    public void The_journal_refuses_a_junction_in_its_folder_chain()
    {
        using var dir = new TempDir();
        var target = System.IO.Path.Combine(dir.Path, "elsewhere");
        Directory.CreateDirectory(target);
        var link = System.IO.Path.Combine(dir.Path, "root");
        if (!Junction(link, target))
        {
            Assert.Skip("mklink /J is not available here");
        }

        var journal = new FileJournal(new JournalOptions { DataRoot = link, AdminOnlyAcl = false });
        Assert.Throws<JournalWriteException>(() => journal.AppendAsync(Record(0), Ct).AsTask().GetAwaiter().GetResult());
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void Attempt_history_folds_records_and_round_trips_the_binding()
    {
        var binding = new FixBinding
        {
            Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal)
            {
                ["svc"] = [new EntityRef { Kind = EntityKind.Service, Id = "NahimicService", DisplayName = "Nahimic" }, new EntityRef { Kind = EntityKind.Service, Id = "A.B" }],
            },
        };
        var args = JournalConventions.EncodeBinding(binding).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        args[JournalConventions.StartArgs.StepCount] = "2";
        var records = new List<JournalRecord>
        {
            Record(JournalConventions.AttemptStep) with { Seq = 1, Op = JournalConventions.AttemptOp, Args = args },
            Record(0) with { Seq = 2, Before = Cap("b") },
            Record(0) with { Seq = 3, Phase = JournalPhase.Applied, After = Cap("a") },
            Record(1) with { Seq = 4, Before = Cap("b1") },
        };
        var log = Assert.Single(AttemptHistory.Build(records));
        Assert.Equal(2, log.StepCount);
        Assert.Equal(binding.ComputeHash(), log.Binding.ComputeHash());
        Assert.Equal("Nahimic", log.Binding.Entities["svc"][0].DisplayName);
        Assert.False(log.Steps[0].ApplyInFlight);
        Assert.True(log.Steps[1].ApplyInFlight);
        Assert.Equal("a", log.Steps[0].ExpectedAfter!.Data["s"]);
        Assert.False(log.AllApplied);
    }

    private static StateCapture Cap(string v)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal) { ["s"] = v };
        return new StateCapture { Op = "x", Data = data, Hash = CanonicalHash.Of(data) };
    }

    private static JournalOptions Options(TempDir dir) => new() { DataRoot = dir.Path, AdminOnlyAcl = false };

    private static JournalRecord Record(int step, string session = Session) => new()
    {
        Utc = new DateTimeOffset(2026, 10, 21, 18, 0, step < 0 ? 0 : step, TimeSpan.Zero),
        SessionId = session,
        AttemptId = "01JB6R7X3K9M2N4P5Q6R7S8TC0",
        FixId = "fix.test",
        StepIndex = step,
        Op = "registry.setValue",
        Phase = JournalPhase.Intent,
        AppVersion = "0.2.0",
        KbVersion = "kb",
        ProposedBy = new Proposer { Origin = EventOrigin.Ui },
    };

    /// <summary>Creates a directory junction with cmd's mklink (no admin needed); false when that is not possible.</summary>
    internal static bool Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }
}
