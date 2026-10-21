using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;

namespace VTD.Journal.Tests;

/// <summary>Atomic state store: replace + .bak, fallback chain, journal rebuild, folder hygiene.</summary>
public sealed class StateStoreTests
{
    [Fact]
    public void Save_replaces_atomically_and_keeps_the_previous_copy()
    {
        using var dir = new TempDir();
        var store = Store(dir);
        Assert.Equal(StateLoadSource.Empty, store.Load().Source);
        store.Save(State("one"));
        store.Save(State("two"));
        var loaded = store.Load();
        Assert.Equal(StateLoadSource.Primary, loaded.Source);
        Assert.Equal("two", loaded.State!.FixId);
        Assert.True(File.Exists(store.BackupPath));
        Assert.False(File.Exists(store.TempPath));
    }

    [Fact]
    public void A_torn_primary_falls_back_to_the_backup_then_to_the_journal()
    {
        using var dir = new TempDir();
        var rebuilder = new CountingRebuilder();
        var store = Store(dir, rebuilder, () => [new JournalRecord { Utc = default, SessionId = "S", AttemptId = "A", FixId = "rebuilt", StepIndex = 0, Op = "x", Phase = JournalPhase.Intent, AppVersion = "v", KbVersion = "k" }]);
        store.Save(State("one"));
        store.Save(State("two"));
        var bytes = File.ReadAllBytes(store.PrimaryPath);
        File.WriteAllBytes(store.PrimaryPath, bytes[..(bytes.Length / 2)]);
        var fromBak = store.Load();
        Assert.Equal(StateLoadSource.Backup, fromBak.Source);
        Assert.Equal("one", fromBak.State!.FixId);
        Assert.Contains("primary.unreadable", fromBak.Issues);

        File.WriteAllText(store.BackupPath, "{ not json");
        var rebuilt = store.Load();
        Assert.Equal(StateLoadSource.RebuiltFromJournal, rebuilt.Source);
        Assert.Equal("rebuilt", rebuilt.State!.FixId);
        Assert.Equal(1, rebuilder.Calls);

        rebuilder.ReturnNull = true;
        var empty = store.Load();
        Assert.Equal(StateLoadSource.Empty, empty.Source);
        Assert.Contains("journal.empty", empty.Issues);
    }

    [Fact]
    public void A_state_that_fails_validation_is_not_used()
    {
        using var dir = new TempDir();
        var store = new AtomicStateStore<AttemptSummary>(dir.Path, Path.Combine(dir.Path, "session"), adminOnly: false, validate: s => s.FixId != "bad");
        store.Save(State("good"));
        store.Save(State("bad"));
        var loaded = store.Load();
        Assert.Equal(StateLoadSource.Backup, loaded.Source);
        Assert.Equal("good", loaded.State!.FixId);
        Assert.Contains("primary.invalid", loaded.Issues);
    }

    [Fact]
    public void A_leftover_temp_file_from_a_crash_is_ignored_and_replaced()
    {
        using var dir = new TempDir();
        var store = Store(dir);
        store.Save(State("one"));
        File.WriteAllText(store.TempPath, "half written");
        Assert.Equal("one", store.Load().State!.FixId);
        store.Save(State("two"));
        Assert.Equal("two", store.Load().State!.FixId);
    }

    [Fact]
    public void Folders_outside_the_data_root_are_refused()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentException>(() => SecureDirectory.EnsureUnder(Path.Combine(dir.Path, "root"), Path.Combine(dir.Path, "elsewhere"), adminOnly: false));
        SecureDirectory.EnsureUnder(dir.Path, Path.Combine(dir.Path, "a", "b", "c"), adminOnly: false);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "a", "b", "c")));
    }

    private static AtomicStateStore<AttemptSummary> Store(TempDir dir, IStateRebuilder<AttemptSummary>? rebuilder = null, Func<IReadOnlyList<JournalRecord>>? records = null)
        => new(dir.Path, Path.Combine(dir.Path, "session"), adminOnly: false, rebuilder: rebuilder, journalRecords: records);

    private static AttemptSummary State(string fixId) => new()
    {
        AttemptId = "01JB6R7X3K9M2N4P5Q6R7S8TC0",
        SessionId = "01JB6R7X3K9M2N4P5Q6R7S8TB0",
        FixId = fixId,
        TitleKey = "t",
        Status = AttemptStatus.Verified,
        Outcome = FixOutcome.Pending,
        Reversibility = Reversibility.Exact,
        AppliedUtc = new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero),
        CanUndo = true,
    };

    private sealed class CountingRebuilder : IStateRebuilder<AttemptSummary>
    {
        public int Calls { get; private set; }

        public bool ReturnNull { get; set; }

        public AttemptSummary? Rebuild(IReadOnlyList<JournalRecord> records)
        {
            Calls++;
            return ReturnNull || records.Count == 0 ? null : State(records[0].FixId);
        }
    }
}
