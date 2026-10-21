using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;
using VTD.Testing.Fakes;

namespace VTD.Contracts.Tests;

/// <summary>The fakes honour the contracts they stand in for, so agents can trust them.</summary>
public sealed class FakesBehaviourTests
{
    private static readonly Proposer Ui = new() { Origin = EventOrigin.Ui };

    [Fact]
    public async Task Fake_engine_walks_the_whole_fix_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new FakeEngineRpc(Fixtures.RealtekStutter);
        await engine.HelloAsync(new VTD.Contracts.Ipc.ClientHello { ProtocolVersion = VTD.Contracts.Ipc.EngineProtocol.Version, AppVersion = "0.1.0", Language = Languages.English }, ct);
        await engine.SendAsync(new IntakeSubmitted { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Intake = new VTD.Contracts.Analysis.IntakeAnswers { Symptoms = ["stutter"] } }, ct);
        await engine.SendAsync(new ScanRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct);

        var proposal = (await engine.GetAnalysisAsync(ct))!.Proposals[0];
        var s = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = proposal.FixId, Binding = proposal.Binding }, ct);
        Assert.Equal(Phase.AwaitingConsent, s.Phase);

        var preview = await engine.FixRunner.PreviewAsync(proposal.FixId, proposal.Binding, Ui, ct);
        s = await engine.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = RpcContractTests.Token(engine, preview) }, ct);
        Assert.Equal(Phase.Verifying, s.Phase); // deadman armed: "Is your internet still working?"
        Assert.Contains(engine.FixRunner.Journal.Records, r => r.Phase == JournalPhase.DeadmanArmed && r.DeadlineUtc is not null);

        var attempt = Assert.Single(s.Attempts);
        s = await engine.SendAsync(new DeadmanAnswered { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), AttemptId = attempt.AttemptId, Keep = true }, ct);
        Assert.Equal(Phase.AwaitingOutcome, s.Phase);
        Assert.Contains(engine.FixRunner.Journal.Records, r => r.Phase == JournalPhase.DeadmanConfirmed);

        s = await engine.SendAsync(new OutcomeReported { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), AttemptId = attempt.AttemptId, Outcome = FixOutcome.Fixed }, ct);
        Assert.Equal(Phase.Completed, s.Phase);

        var intent = Assert.Single(engine.FixRunner.Journal.Records, r => r.Phase == JournalPhase.Intent);
        Assert.Equal(preview.PreviewHash, intent.PreviewHash);
        Assert.NotNull(intent.ConsentTokenId);
        Assert.True(engine.FixRunner.Journal.Records.Select(r => r.Seq).SequenceEqual(engine.FixRunner.Journal.Records.Select(r => r.Seq).Order()));
    }

    [Fact]
    public async Task Refused_consent_leaves_the_session_awaiting_consent_and_get_preview_returns_the_engine_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = await Scanned(ct);
        var proposal = (await engine.GetAnalysisAsync(ct))!.Proposals[0];
        var s = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = proposal.FixId, Binding = proposal.Binding }, ct);
        var preview = await engine.GetPreviewAsync(s.CurrentPreviewId!, ct);
        Assert.NotNull(preview);
        Assert.Equal(proposal.FixId, preview.FixId);
        Assert.NotNull(preview.ConsentNonce);

        var stale = RpcContractTests.Token(engine, preview) with { PreviewHash = "stale" };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = stale }, ct));
        Assert.Equal("consent.preview_changed", refused.Message);
        Assert.Equal(Phase.AwaitingConsent, (await engine.GetSessionAsync(ct)).Phase); // never stuck in Applying

        s = await engine.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = RpcContractTests.Token(engine, preview) }, ct);
        Assert.NotEqual(Phase.AwaitingConsent, s.Phase);
        Assert.Null(await engine.GetPreviewAsync(preview.PreviewId, ct)); // used

        engine.ClientOrigin = EventOrigin.Mcp;
        var agent = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = "fix.net.dns_flush", Binding = FixBinding.Empty }, ct);
        Assert.Null((await engine.GetPreviewAsync(agent.CurrentPreviewId!, ct))!.ConsentNonce); // agents never see the nonce
    }

    [Fact]
    public async Task Restart_requiring_fix_drives_awaiting_restart_and_new_commands_follow_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = await Scanned(ct);
        var s = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = "fix.display.hags_off", Binding = FixBinding.Empty }, ct);
        var preview = (await engine.GetPreviewAsync(s.CurrentPreviewId!, ct))!;
        Assert.Equal(RebootNeed.Required, preview.Reboot);
        s = await engine.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = RpcContractTests.Token(engine, preview) }, ct);
        Assert.Equal(Phase.AwaitingRestart, s.Phase);
        Assert.True(Assert.Single(s.Attempts).RebootPending);

        SessionCommand markBad = new MonitorMarkBadRequested { EventId = "e", Utc = FakeClock.DefaultStart };
        SessionCommand cancel = new ScanCancelRequested { EventId = "e", Utc = FakeClock.DefaultStart };
        Assert.True(CommandPolicy.IsAllowed(markBad, EventOrigin.Ui));
        Assert.False(CommandPolicy.IsAllowed(markBad, EventOrigin.Mcp)); // the player's own verdict
        Assert.True(CommandPolicy.IsAllowed(cancel, EventOrigin.Cli));
        Assert.Equal(Phase.AwaitingRestart, (await engine.SendAsync(cancel, ct)).Phase); // no scan running: ignored
    }

    [Fact]
    public async Task Plan_id_is_stamped_only_for_the_imported_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = await Scanned(ct);
        await engine.SendAsync(new PlanImported { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), PlanId = "01JB6R7X3K9M2N4P5Q6R7S8PLN" }, ct);
        var s = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = "fix.net.dns_flush", Binding = FixBinding.Empty, PlanId = "01JB6R7X3K9M2N4P5Q6R7S8PLN" }, ct);
        Assert.Equal("01JB6R7X3K9M2N4P5Q6R7S8PLN", s.CurrentProposal!.PlanId);
        await engine.SendAsync(new FixSkipped { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = "fix.net.dns_flush" }, ct);
        s = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = "fix.net.dns_flush", Binding = FixBinding.Empty, PlanId = "someone-elses-plan" }, ct);
        Assert.Null(s.CurrentProposal!.PlanId);
    }

    private static async Task<FakeEngineRpc> Scanned(CancellationToken ct)
    {
        var engine = new FakeEngineRpc(Fixtures.RealtekStutter);
        await engine.HelloAsync(new VTD.Contracts.Ipc.ClientHello { ProtocolVersion = VTD.Contracts.Ipc.EngineProtocol.Version, AppVersion = "0.1.0", Language = Languages.English }, ct);
        await engine.SendAsync(new IntakeSubmitted { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Intake = new VTD.Contracts.Analysis.IntakeAnswers { Symptoms = ["stutter"] } }, ct);
        await engine.SendAsync(new ScanRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct);
        return engine;
    }

    [Fact]
    public async Task Fake_fix_runner_refuses_replayed_changed_and_expired_consent()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var ids = new SequentialIdGenerator(clock);
        var runner = new FakeFixRunner(FakeKnowledgeBase.Sample(), clock, ids);
        var binding = new FixBinding { Entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal) { ["adapter"] = [new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId }] } };

        var preview = await runner.PreviewAsync("fix.net.disable_unused_adapter", binding, Ui, ct);
        var token = new ConsentToken
        {
            TokenId = ids.NewId(),
            SessionId = runner.SessionId,
            PreviewId = preview.PreviewId,
            FixId = preview.FixId,
            BindingHash = preview.BindingHash,
            PreviewHash = preview.PreviewHash,
            Nonce = preview.ConsentNonce!,
            IssuedUtc = clock.GetUtcNow(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(token with { PreviewHash = "tampered" }, null, ct));
        var applied = await runner.ApplyAsync(token, null, ct);
        Assert.Equal(AttemptStatus.Verified, applied.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(token, null, ct)); // replay

        // Expiry is the preview's engine-side expiry, whatever the token claims.
        var second = await runner.PreviewAsync("fix.net.disable_unused_adapter", binding, Ui, ct);
        clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(token with { PreviewId = second.PreviewId, Nonce = second.ConsentNonce!, PreviewHash = second.PreviewHash, IssuedUtc = clock.GetUtcNow() }, null, ct));

        var undo = await runner.UndoAsync(applied.AttemptId, UndoMode.IfUnchanged, ct);
        Assert.Equal(UndoStatus.Undone, undo.Status);
    }

    [Fact]
    public async Task One_way_fix_needs_a_second_separate_click()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var ids = new SequentialIdGenerator(clock);
        var runner = new FakeFixRunner(FakeKnowledgeBase.Sample(), clock, ids);
        var preview = await runner.PreviewAsync("fix.net.dns_flush", FixBinding.Empty, Ui, ct);
        Assert.True(preview.RequiresSecondConsent);
        var first = new ConsentToken { TokenId = ids.NewId(), SessionId = runner.SessionId, PreviewId = preview.PreviewId, FixId = preview.FixId, BindingHash = preview.BindingHash, PreviewHash = preview.PreviewHash, Nonce = preview.ConsentNonce!, IssuedUtc = clock.GetUtcNow() };

        var waiting = await runner.ApplyAsync(first, null, ct);
        Assert.Equal(AttemptStatus.AwaitingSecondConsent, waiting.Status);
        Assert.Empty(runner.Journal.Records); // nothing applied yet

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(first with { SecondConsentNonce = "guess" }, null, ct));
        var done = await runner.ApplyAsync(first with { TokenId = ids.NewId(), SecondConsentNonce = waiting.SecondConsentNonce }, null, ct);
        Assert.Equal(AttemptStatus.Verified, done.Status);
        Assert.Equal(UndoStatus.NotUndoable, (await runner.UndoAsync(done.AttemptId, UndoMode.IfUnchanged, ct)).Status);
    }

    [Fact]
    public void Fake_clock_fires_timers_in_order_only_when_advanced()
    {
        var clock = new FakeClock();
        var fired = new List<int>();
        using var t1 = clock.CreateTimer(_ => fired.Add(1), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        using var t2 = clock.CreateTimer(_ => fired.Add(2), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(fired);
        clock.Advance(TimeSpan.FromSeconds(70));
        Assert.Equal([2, 2, 1, 2], fired);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(75), clock.GetUtcNow());
    }

    [Fact]
    public async Task In_memory_state_store_falls_back_to_backup_after_a_torn_write()
    {
        var store = new InMemoryStateStore<SessionState>();
        var s1 = await new FakeEngineRpc().GetSessionAsync(TestContext.Current.CancellationToken);
        store.Save(s1);
        store.Save(s1 with { Revision = 2 });
        store.CorruptPrimary();
        var loaded = store.Load();
        Assert.Equal(StateLoadSource.Backup, loaded.Source);
        Assert.Equal(1, loaded.State!.Revision);
    }

    [Fact]
    public async Task Fake_probe_returns_unknown_for_unscripted_guards()
    {
        var probe = new FakeFactProbe().Set(GuardIds.GameRunning, Tri.False);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.GameRunning, new Dictionary<string, string>(), TestContext.Current.CancellationToken)).State);
        Assert.Equal(Tri.Unknown, (await probe.ProbeAsync(GuardIds.PendingReboot, new Dictionary<string, string>(), TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public void Fake_kb_normalizes_codes_and_falls_back_to_english()
    {
        var kb = FakeKnowledgeBase.Sample();
        Assert.Equal("van-9003", kb.FindCode("van9003")!.Def.Id);
        Assert.Equal("van-9003", kb.FindCode("VAN-9003")!.Def.Id);
        Assert.Equal("val-account-group", kb.FindCode("val 43")!.Def.Id);
        Assert.Equal(kb.Text("rule.nahimic.title", Languages.English), kb.Text("rule.nahimic.title", Languages.Turkish));
        Assert.Equal("14 Realtek driver resets in 14 days", kb.Text("rule.realtek25.ev.resets", Languages.English, new Dictionary<string, object> { ["count"] = 14 }));
    }

    [Fact]
    public void Sample_kb_fixes_only_use_catalogued_primitives_and_state_keys()
    {
        foreach (var fix in FakeKnowledgeBase.Sample().Fixes)
        {
            foreach (var step in fix.Steps)
            {
                var spec = OpCatalog.Find(step.Op);
                Assert.NotNull(spec);
                Assert.All(step.Args.Keys, k => Assert.Contains(spec!.Args, a => a.Name == k));
                Assert.All(spec!.Args.Where(a => a.Required), a => Assert.Contains(a.Name, step.Args.Keys));
            }

            foreach (var verify in fix.Verify)
            {
                var spec = OpCatalog.Find(verify.Probe);
                Assert.NotNull(spec);
                Assert.All(verify.Expect.Keys, k => Assert.Contains(k, spec!.StateKeys));
            }

            var network = fix.Steps.Any(st => OpCatalog.Find(st.Op)!.AffectsNetwork) || fix.Bind.Values.Any(b => b.Kind == EntityKind.Adapter);
            Assert.True(!network || fix.Deadman is not null, $"{fix.Id} cuts the network and needs a deadman (S15).");
        }
    }
}
