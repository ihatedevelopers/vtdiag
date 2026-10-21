using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;
using VTD.Fixes.Ports;
using VTD.Fixes.Runner;
using VTD.Fixes.Tests.Fakes;
using VTD.Journal;
using VTD.Testing.Fakes;
using static VTD.Fixes.Tests.Fakes.TestFixes;

namespace VTD.Fixes.Tests;

/// <summary>The fix runner: consent, TOCTOU, one at a time, write-ahead order, rollback, verify, deadman, undo, drift.</summary>
public sealed class RunnerTests
{
    private const string SecondNic = @"PCI\VEN_10EC&DEV_8125\01000000684CE00000";
    private const string GraphicsKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Apply_journals_intent_before_every_change_and_verifies()
    {
        var journal = new ScriptedJournal();
        using var rig = Rig.With(journal, null, MouseAccelOff);
        var p = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        Assert.True(p.CanApply, string.Join(",", p.BlockReasonKeys));
        Assert.Equal(3, p.Steps.Count);
        Assert.Equal("1", p.Steps[0].Before["data"]);
        Assert.Equal("0", p.Steps[0].Intended["data"]);

        // Write-ahead: when a step's intent is written, the system still has the step's before-state.
        var violations = new List<string>();
        journal.BeforeAppend = r =>
        {
            if (r.Phase == JournalPhase.Intent && r.StepIndex >= 0)
            {
                var name = r.Args["name"];
                var now = rig.System.Registry.GetValue(RegHive.User, @"Control Panel\Mouse", name);
                if (Ops.RegCodec.Display(now!) == "0")
                {
                    violations.Add(name);
                }
            }
        };
        var r = await rig.Runner.ApplyAsync(rig.Token(p), null, Ct);
        Assert.Empty(violations);
        Assert.Equal(AttemptStatus.Verified, r.Status);
        Assert.Equal(VerifyStatus.Passed, r.Verify!.Status);
        Assert.All(r.Steps, s => Assert.Equal(StepStatus.Applied, s.Status));
        Assert.Equal("0", Ops.RegCodec.Display(rig.System.Registry.GetValue(RegHive.User, @"Control Panel\Mouse", "MouseThreshold2")!));

        var records = await journal.ListAsync(Ct);
        Assert.Equal(
            [JournalPhase.Intent, JournalPhase.Intent, JournalPhase.Applied, JournalPhase.Intent, JournalPhase.Applied, JournalPhase.Intent, JournalPhase.Applied, JournalPhase.Applied, JournalPhase.Verified],
            records.Select(x => x.Phase));
        var start = records[0];
        Assert.Equal(JournalConventions.AttemptStep, start.StepIndex);
        Assert.Equal(p.PreviewHash, start.PreviewHash);
        Assert.NotNull(start.ConsentTokenId);
        Assert.Equal(EventOrigin.Ui, start.ProposedBy!.Origin);
        Assert.All(records, x => Assert.Equal(42u, x.BootId));
        Assert.Equal(records.Select(x => x.Seq).Order(), records.Select(x => x.Seq));
    }

    [Fact]
    public async Task Consent_is_single_use_unexpired_and_bound_to_the_preview()
    {
        using var rig = Rig.Create(MouseAccelOff);
        var p = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        var before = rig.System.Fingerprint();

        await Refused("consent.preview_changed", rig.Runner.ApplyAsync(rig.Token(p) with { PreviewHash = "00" }, null, Ct));
        await Refused("consent.preview_changed", rig.Runner.ApplyAsync(rig.Token(p) with { Nonce = "forged" }, null, Ct));
        await Refused("consent.preview_changed", rig.Runner.ApplyAsync(rig.Token(p) with { BindingHash = "00" }, null, Ct));
        await Refused("consent.wrong_session", rig.Runner.ApplyAsync(rig.Token(p) with { SessionId = "OTHER" }, null, Ct));
        await Refused("consent.unknown_preview", rig.Runner.ApplyAsync(rig.Token(p) with { PreviewId = "NOPE" }, null, Ct));
        Assert.Equal(before, rig.System.Fingerprint());

        var ok = await rig.Runner.ApplyAsync(rig.Token(p), null, Ct);
        Assert.Equal(AttemptStatus.Verified, ok.Status);
        await Refused("consent.replayed", rig.Runner.ApplyAsync(rig.Token(p), null, Ct));

        var p2 = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        rig.Clock.Advance(SessionLimits.TokenLifetime + TimeSpan.FromSeconds(1));
        await Refused("consent.expired", rig.Runner.ApplyAsync(rig.Token(p2), null, Ct));
        Assert.Null(rig.Runner.FindPreview(p2.PreviewId));
    }

    [Fact]
    public async Task A_change_between_preview_and_apply_is_refused_TOCTOU()
    {
        using var rig = Rig.Create(HagsOff);
        var p = await rig.Runner.PreviewAsync(HagsOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        rig.System.Registry.SetDword(RegHive.Machine, GraphicsKey, "HwSchMode", 1); // someone else changed it
        var fp = rig.System.Fingerprint();
        await Refused("consent.preview_changed", rig.Runner.ApplyAsync(rig.Token(p), null, Ct));
        Assert.Equal(fp, rig.System.Fingerprint());
        Assert.Empty(await rig.RecordsAsync());

        // A guard that flips between preview and apply also changes the preview.
        rig.System.Registry.SetDword(RegHive.Machine, GraphicsKey, "HwSchMode", 2);
        var p2 = await rig.Runner.PreviewAsync(HagsOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        rig.Probe.Set(GuardIds.GameRunning, Tri.True);
        await Refused("consent.preview_changed", rig.Runner.ApplyAsync(rig.Token(p2), null, Ct));
    }

    [Fact]
    public async Task Preview_hash_is_stable_for_the_same_state()
    {
        using var rig = Rig.Create(DisableConflictService);
        var b = Rig.Bind("svc", EntityKind.Service, "NahimicService", "RtkAudioUniversalService");
        var a = await rig.Runner.PreviewAsync(DisableConflictService.Id, b, Rig.Ui, Ct);
        var c = await rig.Runner.PreviewAsync(DisableConflictService.Id, b, Rig.Ui, Ct);
        Assert.Equal(a.PreviewHash, c.PreviewHash);
        Assert.NotEqual(a.PreviewId, c.PreviewId);
        Assert.NotEqual(a.ConsentNonce, c.ConsentNonce);
        Assert.Equal(4, a.Steps.Count); // pick all: two services x two steps
        var agent = await rig.Runner.PreviewAsync(DisableConflictService.Id, b, new Proposer { Origin = EventOrigin.Mcp, Label = "agent" }, Ct);
        Assert.NotEqual(a.PreviewHash, agent.PreviewHash);
    }

    [Fact]
    public async Task Only_one_fix_runs_at_a_time()
    {
        using var rig = Rig.Create(DisableConflictService, MouseAccelOff);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.System.Services.StopGate = gate;
        var p1 = await rig.Runner.PreviewAsync(DisableConflictService.Id, Rig.Bind("svc", EntityKind.Service, "NahimicService"), Rig.Ui, Ct);
        var p2 = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        var first = rig.Runner.ApplyAsync(rig.Token(p1), null, Ct);
        for (var i = 0; i < 100 && rig.System.Services.Writes.Count == 0; i++)
        {
            await Task.Delay(10, Ct);
        }

        await Refused("fix.busy", rig.Runner.ApplyAsync(rig.Token(p2), null, Ct));
        gate.SetResult();
        Assert.Equal(AttemptStatus.Verified, (await first).Status);
        Assert.Equal(AttemptStatus.Verified, (await rig.Runner.ApplyAsync(rig.Token(p2), null, Ct)).Status);
    }

    [Fact]
    public async Task One_way_fix_needs_a_second_click_and_a_restore_point_and_is_not_undoable()
    {
        using var rig = Rig.Create(WinsockReset);
        var p = await rig.Runner.PreviewAsync(WinsockReset.Id, FixBinding.Empty, Rig.Ui, Ct);
        Assert.True(p.RequiresSecondConsent);
        Assert.True(p.RestorePointFirst);
        Assert.Equal(RiskLevel.Medium, p.Risk);
        Assert.Equal(Reversibility.OneWay, p.Reversibility);

        var first = await rig.Runner.ApplyAsync(rig.Token(p), null, Ct);
        Assert.Equal(AttemptStatus.AwaitingSecondConsent, first.Status);
        Assert.NotNull(first.SecondConsentNonce);
        Assert.Empty(rig.System.Tools.Runs);
        Assert.Empty(await rig.RecordsAsync());
        Assert.Equal(0, rig.RestorePoints.Calls);

        await Refused("consent.second_consent_mismatch", rig.Runner.ApplyAsync(rig.Token(p, "WRONG"), null, Ct));
        var done = await rig.Runner.ApplyAsync(rig.Token(p, first.SecondConsentNonce), null, Ct);
        Assert.Equal(AttemptStatus.AwaitingReboot, done.Status);
        Assert.True(done.RebootPending);
        Assert.Equal([SystemTool.WinsockReset], rig.System.Tools.Runs);
        Assert.Equal(1, rig.RestorePoints.Calls);
        Assert.NotNull(done.Deadman);

        await rig.Runner.ConfirmDeadmanAsync(done.AttemptId, keep: true, Ct);
        var undo = await rig.Runner.UndoAsync(done.AttemptId, UndoMode.Force, Ct);
        Assert.Equal(UndoStatus.NotUndoable, undo.Status);
        var row = Assert.Single(await rig.Runner.ListAttemptsAsync(Ct));
        Assert.False(row.CanUndo);
        Assert.Equal("undo.one_way", row.UndoBlockedReasonKey);
        Assert.NotNull(row.RestorePointUtc);
    }

    [Fact]
    public async Task No_restore_point_means_no_change()
    {
        using var rig = Rig.Create(WinsockReset);
        rig.RestorePoints.ForcedStatus = RestorePointStatus.Failed;
        var p = await rig.Runner.PreviewAsync(WinsockReset.Id, FixBinding.Empty, Rig.Ui, Ct);
        var first = await rig.Runner.ApplyAsync(rig.Token(p), null, Ct);
        var r = await rig.Runner.ApplyAsync(rig.Token(p, first.SecondConsentNonce), null, Ct);
        Assert.Equal(AttemptStatus.Failed, r.Status);
        Assert.Empty(rig.System.Tools.Runs);
        Assert.Empty(await rig.RecordsAsync());
    }

    [Fact]
    public async Task An_aborting_step_failure_rolls_everything_back()
    {
        var gameDvr = new FixDef
        {
            Id = "fix.display.game_dvr_off",
            TitleKey = "t",
            ExplainKey = "e",
            UndoExplainKey = "u",
            Risk = RiskLevel.Low,
            Reversibility = Reversibility.Exact,
            Admin = false,
            Steps =
            [
                new StepDef { Op = OpIds.RegistrySetValue, Args = Map(("path", @"HKCU\System\GameConfigStore"), ("name", "GameDVR_Enabled"), ("kind", "DWord"), ("data", "0")) },
                new StepDef { Op = OpIds.RegistrySetValue, Args = Map(("path", @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR"), ("name", "AppCaptureEnabled"), ("kind", "DWord"), ("data", "0")) },
            ],
        };
        using var rig = Rig.Create(gameDvr);
        var fp = rig.System.Fingerprint();
        rig.System.Registry.FailWritesUnder = @"CurrentVersion\GameDVR";
        var (_, r) = await rig.ApplyAsync(gameDvr.Id);
        Assert.Equal(AttemptStatus.RolledBack, r.Status);
        Assert.Equal(fp, rig.System.Fingerprint());
        Assert.Equal(StepStatus.RolledBack, r.Steps.First(s => s.Index == 0).Status);
        Assert.Equal(StepStatus.Failed, r.Steps.First(s => s.Index == 1).Status);
        var row = Assert.Single(await rig.Runner.ListAttemptsAsync(Ct));
        Assert.Equal(AttemptStatus.RolledBack, row.Status);
        Assert.False(row.CanUndo);
    }

    [Fact]
    public async Task A_change_that_does_not_land_fails_the_step_and_rolls_back()
    {
        using var rig = Rig.Create(HagsOff);
        rig.System.Registry.SilentlyRevertUnder = "GraphicsDrivers";
        var fp = rig.System.Fingerprint();
        var (_, r) = await rig.ApplyAsync(HagsOff.Id);
        Assert.Equal(AttemptStatus.Failed, r.Status);
        Assert.Equal("apply.not_landed", r.Steps.Single().ErrorCode);
        Assert.Equal(fp, rig.System.Fingerprint());
    }

    [Fact]
    public async Task Verification_failure_undoes_the_change()
    {
        var wrongVerify = HagsOff with { Id = "fix.test.verify_fails", Reboot = RebootNeed.None, Verify = [HagsOff.Verify[0] with { Expect = Map(("data", "2")) }] };
        using var rig = Rig.Create(wrongVerify);
        var fp = rig.System.Fingerprint();
        var (_, r) = await rig.ApplyAsync(wrongVerify.Id);
        Assert.Equal(AttemptStatus.VerifyFailed, r.Status);
        Assert.Equal(VerifyStatus.Failed, r.Verify!.Status);
        Assert.Equal(fp, rig.System.Fingerprint());
        Assert.Equal(AttemptStatus.VerifyFailed, Assert.Single(await rig.Runner.ListAttemptsAsync(Ct)).Status);
    }

    [Fact]
    public async Task Continue_on_failure_keeps_going_and_undo_retries_in_dependency_order()
    {
        using var rig = Rig.Create(DisableConflictService);
        rig.System.Services.FailStopFor = "NahimicService";
        var (_, r) = await rig.ApplyAsync(DisableConflictService.Id, Rig.Bind("svc", EntityKind.Service, "NahimicService"));
        Assert.Equal(AttemptStatus.Verified, r.Status);
        Assert.Equal(StepStatus.Failed, r.Steps[1].Status);
        Assert.Equal(ServiceStart.Disabled, rig.System.Services.Query("NahimicService")!.StartType);
        rig.System.Services.FailStopFor = null;

        using var rig2 = Rig.Create(DisableConflictService);
        var fp = rig2.System.Fingerprint();
        var (_, r2) = await rig2.ApplyAsync(DisableConflictService.Id, Rig.Bind("svc", EntityKind.Service, "NahimicService"));
        Assert.Equal(ServiceRun.Stopped, rig2.System.Services.Query("NahimicService")!.State);

        // Reverse order runs "start" first, which the SCM refuses while Disabled; the retry pass fixes it.
        var u = await rig2.Runner.UndoAsync(r2.AttemptId, UndoMode.IfUnchanged, Ct);
        Assert.Equal(UndoStatus.Undone, u.Status);
        Assert.Equal(fp, rig2.System.Fingerprint());
    }

    [Fact]
    public async Task Journal_failure_aborts_before_the_change()
    {
        foreach (var at in new[] { 1, 2 })
        {
            var journal = new ScriptedJournal { FailAt = at };
            using var rig = Rig.With(journal, null, HagsOff);
            var fp = rig.System.Fingerprint();
            var (_, r) = await rig.ApplyAsync(HagsOff.Id);
            Assert.Equal(AttemptStatus.Failed, r.Status);
            Assert.Equal(fp, rig.System.Fingerprint());
        }

        // A journal that dies after the change: the change is rolled back.
        var late = new ScriptedJournal { FailAt = 3 };
        using var rig3 = Rig.With(late, null, HagsOff);
        var fp3 = rig3.System.Fingerprint();
        var (_, r3) = await rig3.ApplyAsync(HagsOff.Id);
        Assert.Equal(AttemptStatus.RolledBack, r3.Status);
        Assert.Equal(fp3, rig3.System.Fingerprint());
    }

    [Fact]
    public async Task Blocked_previews_say_why_and_cannot_be_applied()
    {
        var highRisk = MouseAccelOff with { Id = "fix.test.high", Risk = RiskLevel.High };
        var noDeadman = DisableSecondAdapter with { Id = "fix.test.no_deadman", Deadman = null };
        using var rig = Rig.Create(MouseAccelOff, highRisk, noDeadman, DisableSecondAdapter, HagsOff);
        rig.Probe.Set(GuardIds.GameRunning, Tri.True);
        var p = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        Assert.False(p.CanApply);
        Assert.Contains("guard.game_running", p.BlockReasonKeys);
        await Refused("consent.blocked", rig.Runner.ApplyAsync(rig.Token(p), null, Ct));

        rig.Probe.Set(GuardIds.GameRunning, Tri.Unknown);
        Assert.Contains("guard.game_running", (await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct)).BlockReasonKeys);
        rig.Probe.Set(GuardIds.GameRunning, Tri.False);

        Assert.Contains("fix.high_risk_guide_only", (await rig.Runner.PreviewAsync(highRisk.Id, FixBinding.Empty, Rig.Ui, Ct)).BlockReasonKeys);
        Assert.Contains("fix.deadman_missing", (await rig.Runner.PreviewAsync(noDeadman.Id, Rig.Bind("adapter", EntityKind.Adapter, SecondNic), Rig.Ui, Ct)).BlockReasonKeys);
        Assert.Contains("binding.missing:adapter", (await rig.Runner.PreviewAsync(DisableSecondAdapter.Id, FixBinding.Empty, Rig.Ui, Ct)).BlockReasonKeys);
        Assert.Contains("binding.kind_mismatch:adapter", (await rig.Runner.PreviewAsync(DisableSecondAdapter.Id, Rig.Bind("adapter", EntityKind.Service, "x"), Rig.Ui, Ct)).BlockReasonKeys);
        Assert.Contains("binding.not_one:adapter", (await rig.Runner.PreviewAsync(DisableSecondAdapter.Id, Rig.Bind("adapter", EntityKind.Adapter, SecondNic, @"PCI\VEN_8086&DEV_2725\WIFI1"), Rig.Ui, Ct)).BlockReasonKeys);
        Assert.Contains("policy.default_route_nic", (await rig.Runner.PreviewAsync(DisableSecondAdapter.Id, Rig.Bind("adapter", EntityKind.Adapter, @"PCI\VEN_10EC&DEV_8168\R3ECEB671D921"), Rig.Ui, Ct)).BlockReasonKeys);

        rig.Context.IsElevated = false;
        Assert.Contains("fix.needs_admin", (await rig.Runner.PreviewAsync(HagsOff.Id, FixBinding.Empty, Rig.Ui, Ct)).BlockReasonKeys);
        rig.Context.IsElevated = true;

        rig.System.Registry.SetDword(RegHive.Machine, GraphicsKey, "HwSchMode", 1);
        Assert.Contains("fix.nothing_to_change", (await rig.Runner.PreviewAsync(HagsOff.Id, FixBinding.Empty, Rig.Ui, Ct)).BlockReasonKeys);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Runner.PreviewAsync("fix.nope", FixBinding.Empty, Rig.Ui, Ct));
    }

    [Fact]
    public async Task Template_fix_from_an_agent_still_hits_the_policy()
    {
        var disableDevice = new FixDef
        {
            Id = "fix.generic.disable_device",
            TitleKey = "t",
            ExplainKey = "e",
            UndoExplainKey = "u",
            Risk = RiskLevel.Low,
            Reversibility = Reversibility.Exact,
            Template = true,
            Bind = new Dictionary<string, BindDef>(StringComparer.Ordinal) { ["dev"] = new() { Kind = EntityKind.Device } },
            Steps = [new StepDef { Op = OpIds.DeviceDisable, Args = Map(("instanceId", "$dev.id")) }],
        };
        using var rig = Rig.Create(disableDevice);
        var agent = new Proposer { Origin = EventOrigin.Mcp, Label = "Claude Code (MCP)" };
        foreach (var id in new[] { @"PCI\VEN_10DE&DEV_2684\4&2", @"SCSI\DISK&VEN_NVME\5&1", @"USB\VID_046D&PID_C539\6&1", @"PCI\VEN_8086&DEV_7A60\3&1" })
        {
            var p = await rig.Runner.PreviewAsync(disableDevice.Id, Rig.Bind("dev", EntityKind.Device, id), agent, Ct);
            Assert.False(p.CanApply, id);
            Assert.False(p.Steps.Single().Policy.Allowed);
        }

        var ok = await rig.Runner.PreviewAsync(disableDevice.Id, Rig.Bind("dev", EntityKind.Device, @"BTHHFENUM\BTHHFPAUDIO\8&HFP1"), agent, Ct);
        Assert.True(ok.CanApply, string.Join(",", ok.BlockReasonKeys));
        Assert.Equal(EventOrigin.Mcp, ok.ProposedBy.Origin);
    }

    [Fact]
    public async Task Deadman_kept_only_with_confirmation_and_a_passing_probe()
    {
        using var rig = Rig.Create(DisableSecondAdapter, MouseAccelOff);
        var bind = Rig.Bind("adapter", EntityKind.Adapter, SecondNic);
        var (_, r) = await rig.ApplyAsync(DisableSecondAdapter.Id, bind);
        Assert.Equal(AttemptStatus.Verified, r.Status);
        Assert.Equal(DeadmanStatus.Armed, r.Deadman!.Status);
        Assert.Equal(rig.Clock.GetUtcNow().AddSeconds(60), r.Deadman.DeadlineUtc);
        Assert.True(rig.Runner.DeadmanPending);
        Assert.False(rig.System.Devices.Query(SecondNic)!.Enabled);

        var other = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        await Refused("fix.deadman_pending", rig.Runner.ApplyAsync(rig.Token(other), null, Ct));

        var kept = await rig.Runner.ConfirmDeadmanAsync(r.AttemptId, keep: true, Ct);
        Assert.Equal(DeadmanStatus.Confirmed, kept.Deadman!.Status);
        Assert.False(rig.Runner.DeadmanPending);
        Assert.False(rig.System.Devices.Query(SecondNic)!.Enabled);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Runner.DrainBackgroundAsync();
        Assert.False(rig.System.Devices.Query(SecondNic)!.Enabled);
        Assert.Contains(await rig.RecordsAsync(), x => x.Phase == JournalPhase.DeadmanArmed && x.DeadlineUtc is not null);
    }

    [Fact]
    public async Task Deadman_reverts_on_timeout_on_no_and_on_a_failed_probe()
    {
        foreach (var mode in new[] { "timeout", "no", "probe" })
        {
            using var rig = Rig.Create(DisableSecondAdapter);
            var fp = rig.System.Fingerprint();
            var (_, r) = await rig.ApplyAsync(DisableSecondAdapter.Id, Rig.Bind("adapter", EntityKind.Adapter, SecondNic));
            Assert.False(rig.System.Devices.Query(SecondNic)!.Enabled);
            FixRunResult? answer = null;
            switch (mode)
            {
                case "timeout":
                    rig.Clock.Advance(TimeSpan.FromSeconds(61));
                    await rig.Runner.DrainBackgroundAsync();
                    break;
                case "no":
                    answer = await rig.Runner.ConfirmDeadmanAsync(r.AttemptId, keep: false, Ct);
                    break;
                default:
                    rig.Probe.Set(GuardIds.InternetReachable, Tri.False);
                    answer = await rig.Runner.ConfirmDeadmanAsync(r.AttemptId, keep: true, Ct);
                    break;
            }

            Assert.Equal(fp, rig.System.Fingerprint());
            Assert.False(rig.Runner.DeadmanPending);
            if (answer is not null)
            {
                Assert.Equal(DeadmanStatus.Reverted, answer.Deadman!.Status);
                Assert.Equal(AttemptStatus.RolledBack, answer.Status);
            }

            Assert.Contains(await rig.RecordsAsync(), x => x.Phase == JournalPhase.DeadmanReverted);
            Assert.Equal(AttemptStatus.RolledBack, Assert.Single(await rig.Runner.ListAttemptsAsync(Ct)).Status);
        }
    }

    [Fact]
    public async Task Undo_restores_exactly_refuses_on_conflict_and_forces_on_request()
    {
        using var rig = Rig.Create(MouseAccelOff);
        var fp = rig.System.Fingerprint();
        var (_, r) = await rig.ApplyAsync(MouseAccelOff.Id);
        var row = Assert.Single(await rig.Runner.ListAttemptsAsync(Ct));
        Assert.True(row.CanUndo);

        rig.System.Registry.SetString(RegHive.User, @"Control Panel\Mouse", "MouseSpeed", "2"); // Windows Update or the player
        var changed = rig.System.Fingerprint();
        var conflict = await rig.Runner.UndoAsync(r.AttemptId, UndoMode.IfUnchanged, Ct);
        Assert.Equal(UndoStatus.Conflict, conflict.Status);
        var finding = Assert.Single(conflict.Conflicts);
        Assert.Equal("2", finding.Actual["data"]);
        Assert.Equal(changed, rig.System.Fingerprint());
        Assert.Equal(AttemptStatus.UndoConflict, Assert.Single(await rig.Runner.ListAttemptsAsync(Ct)).Status);

        var forced = await rig.Runner.UndoAsync(r.AttemptId, UndoMode.Force, Ct);
        Assert.Equal(UndoStatus.Undone, forced.Status);
        Assert.Equal(fp, rig.System.Fingerprint());
        var after = Assert.Single(await rig.Runner.ListAttemptsAsync(Ct));
        Assert.Equal(AttemptStatus.Undone, after.Status);
        Assert.False(after.CanUndo);
        Assert.Equal(UndoStatus.NothingToUndo, (await rig.Runner.UndoAsync(r.AttemptId, UndoMode.Force, Ct)).Status);
    }

    [Fact]
    public async Task The_journal_alone_lists_and_undoes_after_a_restart()
    {
        using var rig = Rig.Create(MouseAccelOff, HagsOff);
        var fp = rig.System.Fingerprint();
        var (_, a) = await rig.ApplyAsync(MouseAccelOff.Id);
        var (_, b) = await rig.ApplyAsync(HagsOff.Id);
        Assert.Equal(AttemptStatus.AwaitingReboot, b.Status);
        Assert.Equal(VerifyStatus.DeferredUntilReboot, b.Verify!.Status);

        using var restarted = rig.Restart();
        var rows = await restarted.ListAttemptsAsync(Ct);
        Assert.Equal([AttemptStatus.Verified, AttemptStatus.AwaitingReboot], rows.Select(x => x.Status));
        var post = await restarted.VerifyAsync(b.AttemptId, Ct);
        Assert.Equal(VerifyStatus.Passed, post.Status);
        Assert.Equal(AttemptStatus.Verified, (await restarted.ListAttemptsAsync(Ct))[1].Status);

        Assert.Equal(UndoStatus.Undone, (await restarted.UndoAsync(b.AttemptId, UndoMode.IfUnchanged, Ct)).Status);
        Assert.Equal(UndoStatus.Undone, (await restarted.UndoAsync(a.AttemptId, UndoMode.IfUnchanged, Ct)).Status);
        Assert.Equal(fp, rig.System.Fingerprint());
    }

    [Fact]
    public async Task Session_only_fix_cannot_be_undone()
    {
        using var rig = Rig.Create(CloseOverlays);
        var (_, r) = await rig.ApplyAsync(CloseOverlays.Id, Rig.Bind("proc", EntityKind.Process, "RTSS.exe", "Overwolf.exe"));
        Assert.Equal(AttemptStatus.Verified, r.Status);
        Assert.DoesNotContain(rig.System.Processes.Snapshot(), p => p.ImageName is "RTSS.exe" or "Overwolf.exe");
        var u = await rig.Runner.UndoAsync(r.AttemptId, UndoMode.Force, Ct);
        Assert.Equal(UndoStatus.NotUndoable, u.Status);
        Assert.Equal("undo.session_only", u.MessageKey);
    }

    [Fact]
    public async Task Drift_watch_reports_a_reverted_setting()
    {
        using var rig = Rig.Create(MouseAccelOff);
        var (_, r) = await rig.ApplyAsync(MouseAccelOff.Id);
        Assert.Empty(await rig.Runner.CheckDriftAsync(Ct));
        rig.System.Registry.SetString(RegHive.User, @"Control Panel\Mouse", "MouseThreshold1", "6");
        var drift = Assert.Single(await rig.Runner.CheckDriftAsync(Ct));
        Assert.Equal(r.AttemptId, drift.AttemptId);
        Assert.Equal("drift.setting_reverted", drift.ReasonKey);
        Assert.Equal("6", drift.Actual["data"]);
        await rig.Runner.UndoAsync(r.AttemptId, UndoMode.Force, Ct);
        Assert.Empty(await rig.Runner.CheckDriftAsync(Ct));
    }

    [Fact]
    public async Task Cancellation_between_steps_rolls_back()
    {
        using var rig = Rig.Create(MouseAccelOff);
        var fp = rig.System.Fingerprint();
        var p = await rig.Runner.PreviewAsync(MouseAccelOff.Id, FixBinding.Empty, Rig.Ui, Ct);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(x =>
        {
            if (x.Phase == FixPhase.Apply && x.StepIndex == 1)
            {
                cts.Cancel();
            }
        });
        var r = await rig.Runner.ApplyAsync(rig.Token(p), progress, cts.Token);
        Assert.Equal(AttemptStatus.RolledBack, r.Status);
        Assert.Equal(fp, rig.System.Fingerprint());
    }

    private static async Task Refused(string code, Task<FixRunResult> call)
    {
        var ex = await Assert.ThrowsAsync<ConsentRefusedException>(() => call);
        Assert.Equal(code, ex.Code);
        Assert.Equal(code, ex.Message);
    }

    private sealed class SyncProgress(Action<FixProgress> onReport) : IProgress<FixProgress>
    {
        public void Report(FixProgress value) => onReport(value);
    }
}
