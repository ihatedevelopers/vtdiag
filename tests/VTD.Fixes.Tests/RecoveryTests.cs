using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Fixes.Ports;
using VTD.Fixes.Tests.Fakes;
using static VTD.Fixes.Tests.Fakes.TestFixes;

namespace VTD.Fixes.Tests;

/// <summary>
/// Crash recovery: the engine is killed at every journal write of an apply (and of an undo), restarted, and recovery must
/// leave the PC consistent: all-or-nothing for multi-step fixes, roll-forward of a complete apply, network changes undone
/// unless their deadman was confirmed, in-doubt states handed to the player, and a second run finds nothing to do.
/// </summary>
public sealed class RecoveryTests
{
    private const string SecondNic = @"PCI\VEN_10EC&DEV_8125\01000000684CE00000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Fixes => new() { MouseAccelOff.Id, HagsOff.Id, DisableSecondAdapter.Id, DisableConflictService.Id };

    [Theory]
    [MemberData(nameof(Fixes))]
    public async Task A_crash_at_every_journal_write_recovers_to_a_consistent_state(string fixId)
    {
        var (fix, binding) = Case(fixId);

        // A clean run: count the writes and learn the "applied" end state.
        var clean = new ScriptedJournal();
        using var reference = Rig.With(clean, null, fix);
        var initial = reference.System.Fingerprint();
        await reference.ApplyAsync(fix.Id, binding);
        var applied = reference.System.Fingerprint();
        var total = clean.Appends;
        Assert.True(total >= 4);

        for (var crashAt = 1; crashAt <= total; crashAt++)
        {
            var journal = new ScriptedJournal { CrashAt = crashAt };
            using var rig = Rig.With(journal, null, fix);
            Assert.Equal(initial, rig.System.Fingerprint());
            await Assert.ThrowsAsync<SimulatedCrashException>(() => rig.ApplyAsync(fix.Id, binding));

            journal.Heal();
            using var engine = rig.Restart();
            var report = await engine.RunAsync(Ct);
            var state = rig.System.Fingerprint();
            var where = fixId + " crash at write " + crashAt + "/" + total;
            Assert.False(report.NeedsAttention, where);
            Assert.True(state == initial || state == applied, where + ": the PC is half-changed after recovery");
            if (fix.Deadman is not null)
            {
                Assert.True(state == initial, where + ": a network change survived without a confirmed deadman");
            }

            var rows = await engine.ListAttemptsAsync(Ct);
            Assert.All(rows, row => Assert.NotEqual(AttemptStatus.Applying, row.Status));
            if (state == applied && rows.Count > 0)
            {
                Assert.Contains(rows[0].Status, new[] { AttemptStatus.Applied, AttemptStatus.Verified, AttemptStatus.AwaitingReboot });
                Assert.True(rows[0].CanUndo, where);
            }

            var again = await engine.RunAsync(Ct);
            Assert.Empty(again.Items);
            Assert.Equal(state, rig.System.Fingerprint());
        }
    }

    [Theory]
    [MemberData(nameof(Fixes))]
    public async Task A_crash_during_undo_finishes_the_undo(string fixId)
    {
        var (fix, binding) = Case(fixId);
        var probe = new ScriptedJournal();
        using (var reference = Rig.With(probe, null, fix))
        {
            var (_, r) = await reference.ApplyAsync(fix.Id, binding);
            await Confirm(reference, r);
            var before = probe.Appends;
            await reference.Runner.UndoAsync(r.AttemptId, UndoMode.IfUnchanged, Ct);
            Assert.True(probe.Appends > before);
        }

        var undoWrites = probe.Appends;
        for (var crashAt = 1; crashAt <= undoWrites; crashAt++)
        {
            var journal = new ScriptedJournal();
            using var rig = Rig.With(journal, null, fix);
            var initial = rig.System.Fingerprint();
            var (_, r) = await rig.ApplyAsync(fix.Id, binding);
            await Confirm(rig, r);
            var applied = rig.System.Fingerprint();
            var appliedWrites = journal.Appends;
            if (crashAt <= appliedWrites)
            {
                continue;
            }

            journal.CrashAt = crashAt;
            await Assert.ThrowsAsync<SimulatedCrashException>(() => rig.Runner.UndoAsync(r.AttemptId, UndoMode.IfUnchanged, Ct));
            journal.Heal();
            using var engine = rig.Restart();
            var report = await engine.RunAsync(Ct);
            Assert.False(report.NeedsAttention);
            if (crashAt == appliedWrites + 1)
            {
                // The crash hit the undo's first record: nothing was touched yet, the change is simply still in place.
                Assert.Equal(applied, rig.System.Fingerprint());
                Assert.True((await engine.ListAttemptsAsync(Ct)).Single().CanUndo);
                continue;
            }

            Assert.Equal(initial, rig.System.Fingerprint());
            Assert.Equal(AttemptStatus.Undone, (await engine.ListAttemptsAsync(Ct)).Single().Status);
        }
    }

    private static async Task Confirm(Rig rig, FixRunResult r)
    {
        if (r.Deadman is not null)
        {
            await rig.Runner.ConfirmDeadmanAsync(r.AttemptId, keep: true, Ct);
        }
    }

    [Fact]
    public async Task Deadman_armed_and_never_confirmed_is_undone_at_the_next_start()
    {
        using var rig = Rig.Create(DisableSecondAdapter);
        var fp = rig.System.Fingerprint();
        var (_, r) = await rig.ApplyAsync(DisableSecondAdapter.Id, Rig.Bind("adapter", EntityKind.Adapter, SecondNic));
        Assert.Equal(DeadmanStatus.Armed, r.Deadman!.Status);
        Assert.False(rig.System.Devices.Query(SecondNic)!.Enabled);

        // Power cut inside the countdown: a fresh engine, no timer, only the journal.
        using var engine = rig.Restart();
        var report = await engine.RunAsync(Ct);
        Assert.Contains(report.Items, i => i.Resolution == RecoveryResolution.RolledBack);
        Assert.Equal(fp, rig.System.Fingerprint());
        Assert.Contains(await rig.RecordsAsync(), x => x.Phase == JournalPhase.DeadmanReverted);
        Assert.Equal(AttemptStatus.RolledBack, (await engine.ListAttemptsAsync(Ct)).Single().Status);
    }

    [Fact]
    public async Task A_confirmed_deadman_survives_a_restart()
    {
        using var rig = Rig.Create(DisableSecondAdapter);
        var (_, r) = await rig.ApplyAsync(DisableSecondAdapter.Id, Rig.Bind("adapter", EntityKind.Adapter, SecondNic));
        await rig.Runner.ConfirmDeadmanAsync(r.AttemptId, keep: true, Ct);
        using var engine = rig.Restart();
        var report = await engine.RunAsync(Ct);
        Assert.Empty(report.Items);
        Assert.False(rig.System.Devices.Query(SecondNic)!.Enabled);
    }

    [Fact]
    public async Task A_state_that_matches_neither_side_is_handed_to_the_player()
    {
        var journal = new ScriptedJournal { CrashAt = 3 }; // start, intent(0), CRASH at applied(0): the change happened
        using var rig = Rig.With(journal, null, HagsOff);
        var initial = rig.System.Fingerprint();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => rig.ApplyAsync(HagsOff.Id));
        rig.System.Registry.SetDword(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 7); // neither 2 nor 1
        journal.Heal();
        using var engine = rig.Restart();
        var report = await engine.RunAsync(Ct);
        Assert.True(report.NeedsAttention);
        var item = Assert.Single(report.Items, i => i.Resolution == RecoveryResolution.InDoubt);
        Assert.Equal("2", item.Before!.Data["data"]);
        Assert.Equal("7", item.Current!.Data["data"]);
        Assert.Equal("1", item.Intended!.Data["data"]);
        var row = (await engine.ListAttemptsAsync(Ct)).Single();
        Assert.Equal(AttemptStatus.InDoubt, row.Status);
        Assert.True(row.CanUndo);
        Assert.DoesNotContain((await engine.RunAsync(Ct)).Items, i => i.Resolution is RecoveryResolution.RolledBack or RecoveryResolution.RolledForward);

        // "Restore previous": undo with Force puts the captured before-state back.
        Assert.Equal(UndoStatus.Undone, (await engine.UndoAsync(row.AttemptId, UndoMode.Force, Ct)).Status);
        Assert.Equal(initial, rig.System.Fingerprint());
    }

    [Fact]
    public async Task Recovery_on_an_empty_journal_does_nothing()
    {
        using var rig = Rig.Create(HagsOff);
        var report = await rig.Runner.RunAsync(Ct);
        Assert.Empty(report.Items);
        Assert.False(report.NeedsAttention);
        Assert.Null(report.MessageKey);
    }

    private static (FixDef Fix, FixBinding Binding) Case(string fixId) => fixId switch
    {
        "fix.input.mouse_accel_off" => (MouseAccelOff, FixBinding.Empty),
        "fix.display.hags_off" => (HagsOff, FixBinding.Empty),
        "fix.net.disable_second_adapter" => (DisableSecondAdapter, Rig.Bind("adapter", EntityKind.Adapter, SecondNic)),
        _ => (DisableConflictService, Rig.Bind("svc", EntityKind.Service, "NahimicService", "RtkAudioUniversalService")),
    };
}
