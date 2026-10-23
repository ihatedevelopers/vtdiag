using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;
using VTD.Fixes.Interop;
using VTD.Fixes.Runner;
using VTD.Journal;
using VTD.Knowledge;
using VTD.Testing.Fakes;

namespace VTD.Fixes.IntegrationTests;

/// <summary>The write tests run alone: nothing else may observe or change the machine while they do.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SystemWritesCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = "system-writes";
}

/// <summary>
/// Every KB fix, APPLIED, VERIFIED and UNDONE once on a real elevated throwaway Windows machine (the GitHub Actions
/// runner) through the production stack: <see cref="FixRunner"/>, the real Win32 ports and the on-disk
/// <see cref="FileJournal"/>. One theory row per fix id: arrange a real target and seed the "bad" state
/// (<see cref="ApplyUndoCases"/>), preview with a binding to it (must be applicable), apply with consent (second click for
/// one-way fixes), read the state back independently and compare with what the fix promises, verify, resolve the deadman,
/// undo, and check the before-state is back exactly and no attempt is left open. Everything the row created is removed in
/// cleanup, also on failure. Gated by <see cref="Gate.RequireSystemWrites"/>: skipped on any other machine.
/// </summary>
[Collection(SystemWritesCollection.Name)]
public sealed class ApplyUndoTests(MachineFixture machine) : IClassFixture<MachineFixture>
{
    private static readonly Proposer Ui = new() { Origin = EventOrigin.Ui };

    /// <summary>Every fix id of the bundled KB (rows).</summary>
    public static TheoryData<string> FixIds { get; } = new(LoadKb().Fixes.Select(f => f.Id).Order(StringComparer.Ordinal));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_KB_fix_has_a_real_machine_arrangement()
    {
        var kb = LoadKb().Fixes.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var arranged = ApplyUndoCases.All.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(kb.Except(arranged, StringComparer.Ordinal));
        Assert.Empty(arranged.Except(kb, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FixIds))]
    public async Task Fix_applies_verifies_and_undoes_on_a_real_machine(string fixId)
    {
        Gate.RequireSystemWrites();
        machine.NoteDataRoot();
        var clock = TimeProvider.System;
        var ids = new UlidGenerator(clock);
        var context = new FixSessionContext(ids.NewId(), "0.2.0-it") { Player = Gate.CurrentPlayer(), IsElevated = Gate.Elevated };
        var probe = FakeFactProbe.ReadyToFix();
        var lab = new Lab(machine, context, probe, Log);
        var failed = true;
        try
        {
            await RunAsync(fixId, lab, context, probe, ids);
            failed = false;
        }
        finally
        {
            var leftovers = lab.Cleanup();
            if (!failed && leftovers.Count > 0)
            {
                Assert.Fail(fixId + ": cleanup failed:\n" + string.Join("\n", leftovers));
            }
        }
    }

    private static async Task RunAsync(string fixId, Lab lab, FixSessionContext context, FakeFactProbe probe, UlidGenerator ids)
    {
        var clock = TimeProvider.System;
        var kb = LoadKb();
        Assert.NotNull(kb.FindFix(fixId));
        var dataRoot = MachineFixture.DataRoot;
        var journalDir = Path.Combine(dataRoot, "it-journal", context.SessionId);
        lab.DeleteLater(journalDir);
        var journal = new FileJournal(new JournalOptions { DataRoot = dataRoot, Directory = journalDir });

        var c = ApplyUndoCases.All[fixId](lab);
        Log(fixId + " | target: " + c.Target);
        var ports = RealSystemPorts.Create(context, dataRoot, clock);
        var registry = new OperationRegistry(ports, context, clock, probe);
        using var runner = new FixRunner(kb, registry, journal, probe, new FakeRestorePointService(clock), context, clock, ids);
        try
        {
            var before = c.Read();
            Log("before: " + Show(before));

            // Preview: must be applicable on the arranged target.
            var preview = await runner.PreviewAsync(fixId, c.Binding, Ui, Ct);
            foreach (var s in preview.Steps)
            {
                Log("preview step " + s.Index.ToString(CultureInfo.InvariantCulture) + " " + s.Op + " policy=" + (s.Policy.Allowed ? "allow" : s.Policy.RuleId)
                    + " before={" + Show(s.Before) + "} intended={" + Show(s.Intended) + "}");
            }

            if (!preview.CanApply)
            {
                var reasons = string.Join(", ", preview.BlockReasonKeys);
                if (preview.BlockReasonKeys.All(k => c.SkipIfBlockedBy.Contains(k, StringComparer.Ordinal)))
                {
                    Assert.Skip(fixId + ": this machine cannot host a valid target: " + reasons);
                }

                Assert.Fail(fixId + ": preview blocked on the arranged target: " + reasons);
            }

            // Apply (with the second consent for one-way fixes).
            var result = await runner.ApplyAsync(Token(preview, context.SessionId, ids, null), null, Ct);
            if (result.Status == AttemptStatus.AwaitingSecondConsent)
            {
                Log("second consent requested (" + result.MessageKey + ")");
                result = await runner.ApplyAsync(Token(preview, context.SessionId, ids, result.SecondConsentNonce), null, Ct);
            }

            Log("apply: " + result.Status.ToString() + " (" + result.MessageKey + ") deadman=" + (result.Deadman?.Status.ToString() ?? "none")
                + " steps: " + string.Join("; ", result.Steps.Select(s => s.Index.ToString(CultureInfo.InvariantCulture) + " " + s.Op + " " + s.Status.ToString() + (s.ErrorCode is null ? string.Empty : " " + s.ErrorCode))));
            Assert.True(result.Status is AttemptStatus.Verified or AttemptStatus.AwaitingReboot, fixId + ": apply ended " + result.Status.ToString() + " (" + result.MessageKey + ")");
            foreach (var s in result.Steps.Where(s => s.Status is not (StepStatus.Applied or StepStatus.Skipped)))
            {
                Assert.True(c.MayFailSteps.Contains(s.Index), fixId + ": step " + s.Index.ToString(CultureInfo.InvariantCulture) + " " + s.Op + " " + s.Status.ToString() + " " + s.ErrorCode);
            }

            var after = c.Read();
            Log("after apply: " + Show(after));
            foreach (var (key, want) in c.ExpectAfterApply)
            {
                Assert.True(after.TryGetValue(key, out var got) && string.Equals(got, want, StringComparison.Ordinal), fixId + ": after apply " + key + " = " + (got ?? "?") + ", expected " + want);
            }

            // Verify (the post-reboot pass runs the same checks now; the runner is never rebooted).
            var verify = await runner.VerifyAsync(result.AttemptId, Ct);
            Log("verify: " + verify.Status.ToString() + " " + string.Join("; ", verify.Checks.Select(k => k.Probe + "=" + k.Passed.ToString() + " {" + Show(k.Actual) + "}")));
            Assert.Equal(VerifyStatus.Passed, verify.Status);

            // Deadman: keep (then undo) or let it revert.
            var reverted = false;
            if (result.Deadman is { Status: DeadmanStatus.Armed })
            {
                var keep = c.Deadman == DeadmanChoice.Keep;
                var dm = await runner.ConfirmDeadmanAsync(result.AttemptId, keep, Ct);
                Log("deadman keep=" + Captures(keep) + ": " + dm.Status.ToString() + " " + (dm.Deadman?.Status.ToString() ?? "none") + " (" + dm.MessageKey + ")");
                if (keep)
                {
                    Assert.Equal(DeadmanStatus.Confirmed, dm.Deadman?.Status);
                }
                else
                {
                    Assert.Equal(AttemptStatus.RolledBack, dm.Status);
                    Assert.Equal(DeadmanStatus.Reverted, dm.Deadman?.Status);
                    reverted = true;
                }
            }

            Assert.False(runner.DeadmanPending, fixId + ": a deadman is still counting down");

            // Undo.
            if (!reverted)
            {
                var undo = await runner.UndoAsync(result.AttemptId, UndoMode.IfUnchanged, Ct);
                Log("undo: " + undo.Status.ToString() + " (" + undo.MessageKey + ") " + string.Join("; ", undo.Steps.Select(s => s.Index.ToString(CultureInfo.InvariantCulture) + " " + s.Op + " " + s.Status.ToString() + (s.ErrorCode is null ? string.Empty : " " + s.ErrorCode))));
                switch (c.Undo)
                {
                    case UndoExpectation.Undone:
                        Assert.Equal(UndoStatus.Undone, undo.Status);
                        break;
                    case UndoExpectation.OneWay:
                        Assert.Equal(UndoStatus.NotUndoable, undo.Status);
                        Assert.Equal("undo.one_way", undo.MessageKey);
                        break;
                    default:
                        Assert.Equal(UndoStatus.NotUndoable, undo.Status);
                        Assert.Equal("undo.session_only", undo.MessageKey);
                        break;
                }
            }

            if (c.Undo == UndoExpectation.Undone)
            {
                var restored = c.Read();
                Log("after undo: " + Show(restored));
                Assert.Equal(Show(before), Show(restored));
            }

            // Nothing left open: one attempt, in a final state.
            var attempts = await runner.ListAttemptsAsync(Ct);
            var attempt = Assert.Single(attempts);
            Log("attempt: " + attempt.Status.ToString() + " canUndo=" + Captures(attempt.CanUndo) + " (" + attempt.UndoBlockedReasonKey + ")");
            var final = reverted ? AttemptStatus.RolledBack : c.Undo == UndoExpectation.Undone ? AttemptStatus.Undone : AttemptStatus.Verified;
            Assert.Equal(final, attempt.Status);
            if (c.Undo == UndoExpectation.Undone)
            {
                Assert.False(attempt.CanUndo);
            }

            Log(fixId + ": OK");
        }
        finally
        {
            await runner.DrainBackgroundAsync();
            await DumpJournalAsync(journal);
        }
    }

    private static ConsentToken Token(FixPreview p, string sessionId, UlidGenerator ids, string? second) => new()
    {
        TokenId = ids.NewId(),
        SessionId = sessionId,
        PreviewId = p.PreviewId,
        FixId = p.FixId,
        BindingHash = p.BindingHash,
        PreviewHash = p.PreviewHash,
        Nonce = p.ConsentNonce!,
        IssuedUtc = TimeProvider.System.GetUtcNow(),
        SecondConsentNonce = second,
    };

    private static async Task DumpJournalAsync(IJournal journal)
    {
        var lines = new List<string>();
        try
        {
            await foreach (var r in journal.ReadAllAsync(CancellationToken.None))
            {
                lines.Add(r.Seq.ToString(CultureInfo.InvariantCulture) + " step " + r.StepIndex.ToString(CultureInfo.InvariantCulture) + " " + r.Op + " " + r.Phase.ToString() + (r.ErrorCode is null ? string.Empty : " " + r.ErrorCode));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add("journal unreadable: " + ex.Message);
        }

        Log("journal:\n  " + string.Join("\n  ", lines));
    }

    private static string Show(IReadOnlyDictionary<string, string>? map)
        => map is null ? string.Empty : string.Join(", ", map.Where(kv => !kv.Key.StartsWith('_')).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    private static string Captures(bool value) => value ? "true" : "false";

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static IKnowledgeBase LoadKb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        var result = new KnowledgeBaseLoader([]).LoadAsync(new KbLoadOptions
        {
            BundledPath = Path.Combine(dir!.FullName, "kb"),
            NowUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
            AppVersion = "0.2.0",
        }, CancellationToken.None).GetAwaiter().GetResult();
        return result.Kb;
    }
}
