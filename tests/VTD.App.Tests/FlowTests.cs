using System.IO;
using System.Text;
using VTD.App.Localization;
using VTD.App.ViewModels;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Sessions;
using VTD.Reporting;
using VTD.Testing.Fakes;

namespace VTD.App.Tests;

/// <summary>The whole flow, clicked through on FakeEngineRpc (through the demo engine) in English, Turkish and pseudo.</summary>
public sealed class FlowTests
{
    [Theory]
    [InlineData(Languages.English)]
    [InlineData(Languages.Turkish)]
    [InlineData(Loc.Pseudo)]
    public Task Realtek_flow_scan_fix_deadman_outcome_report(string language) => StaHost.RunAsync(async () =>
    {
        var app = new TestApp(Fixtures.RealtekStutter, language);
        Assert.Equal(FlowStep.Describe, app.Shell.Page.Step);
        await app.ScanAsync(PrivilegeMode.Elevated, "stutter");

        Assert.Equal(["stutter"], app.Shell.Session!.Intake.Symptoms);
        var health = app.Page<HealthViewModel>();
        Assert.NotEmpty(health.Groups);
        Assert.DoesNotContain(health.Groups.SelectMany(g => g.Items), i => i.Text.Contains("health.", StringComparison.Ordinal));
        health.NextCommand.Execute(null);

        var suspects = app.Page<SuspectsViewModel>();
        var top = Assert.Single(suspects.Suspects);
        Assert.Equal(ConfidenceBand.VeryLikely, top.Band);
        Assert.Equal(HonestyBucket.FixableHere, top.Bucket);
        Assert.True(top.CanTryFix);
        Assert.NotEmpty(top.Why);
        Assert.DoesNotContain(top.Why, w => w.Contains("rule.", StringComparison.Ordinal));
        if (language == Languages.Turkish)
        {
            Assert.Contains("Kullanmadığın", top.Title, StringComparison.Ordinal);
            Assert.Equal("Çok olası", top.BandText);
        }

        await suspects.PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        var fix = app.Page<FixDetailViewModel>();
        Assert.True(fix.IsLoaded);
        Assert.False(fix.HasProposer); // the player's own pick
        Assert.True(fix.CanConsent);
        Assert.Contains(fix.Badges, b => b.Tone == BadgeTone.Good);
        Assert.NotEmpty(fix.Steps);
        Assert.NotEmpty(fix.Notes); // deadman note

        await fix.ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.Equal(Phase.Verifying, app.Shell.Session!.Phase);
        var deadman = app.Page<DeadmanViewModel>();
        Assert.InRange(deadman.SecondsLeft, 1, 60);
        await deadman.KeepCommand.ExecuteAsync();
        await app.IdleAsync();

        var outcome = app.Page<OutcomeViewModel>();
        Assert.False(outcome.AskOutcome);
        outcome.SkipTestCommand.Execute(null);
        Assert.True(outcome.AskOutcome);
        await outcome.AnswerCommand.ExecuteAsync("fixed");
        await app.IdleAsync();

        var done = app.Page<CompletedViewModel>();
        Assert.Equal(Phase.Completed, app.Shell.Session!.Phase);
        Assert.Single(done.Attempts);

        done.ReportCommand.Execute(null);
        await app.IdleAsync();
        var report = app.Page<ReportViewModel>();
        Assert.True(report.HasScan);
        Assert.NotEmpty(report.PreviewText);
        await report.CopyRedditCommand.ExecuteAsync();
        var md = app.Io.Clipboard[^1];
        Assert.InRange(md.Length, 200, 9000);
        await report.SaveHtmlCommand.ExecuteAsync();
        var html = Encoding.UTF8.GetString(app.Io.Saved[^1].Content);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Riot", html, StringComparison.Ordinal);
        await report.SaveBundleCommand.ExecuteAsync();
        var bundle = new ScanBundleCodec(new Redactor(new byte[32])).Read(new MemoryStream(app.Io.Saved[^1].Content));
        Assert.Equal(RedactionLevel.Share, bundle.Manifest.Redaction);
        await report.CopyAiCommand.ExecuteAsync();
        Assert.Contains("plan", app.Io.Clipboard[^1], StringComparison.OrdinalIgnoreCase);
        Assert.Null(app.Shell.Error);
        Assert.Empty(Loc.Instance.MissingKeys);
    });

    [Fact]
    public Task Monitor_test_feeds_did_it_help() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<DeadmanViewModel>().KeepCommand.ExecuteAsync();
        await app.IdleAsync();
        var outcome = app.Page<OutcomeViewModel>();
        Assert.True(outcome.CanMonitor);
        await outcome.StartTestCommand.ExecuteAsync();
        await app.IdleAsync();
        var after = app.Page<OutcomeViewModel>();
        Assert.True(after.AskOutcome);
        Assert.True(after.HasTestLines);
        Assert.Equal(10, app.Shell.Ticks.Count);
    });

    [Fact]
    public Task Not_fixed_goes_back_to_the_suspects() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp(Fixtures.NahimicBlackScreen);
        await app.ScanAsync(PrivilegeMode.Elevated, "launch_black_screen");
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        var top = Assert.Single(app.Page<SuspectsViewModel>().Suspects);
        Assert.True(top.HasGuide || top.HasFix);
        await top.TryFixCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        var outcome = app.Page<OutcomeViewModel>();
        outcome.SkipTestCommand.Execute(null);
        await outcome.AnswerCommand.ExecuteAsync("notfixed");
        await app.IdleAsync();
        Assert.Equal(Phase.Proposing, app.Shell.Session!.Phase);
        app.Page<SuspectsViewModel>();
        Assert.Equal(FixOutcome.NotFixed, app.Shell.Session.Attempts[^1].Outcome);
    });

    [Fact]
    public Task Limited_van9003_shows_unknowns_honestly_and_guides_through_bios() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp(Fixtures.Van9003SecureBootOff);
        await app.ScanAsync(PrivilegeMode.Limited, "error_code");
        Assert.True(app.Shell.IsLimited);
        var health = app.Page<HealthViewModel>();
        Assert.True(health.ShowLimitedNote);
        var items = health.Groups.SelectMany(g => g.Items).ToList();
        var unknown = items.Where(i => i.Status == HealthStatus.Unknown).ToList();
        Assert.NotEmpty(unknown);
        Assert.All(unknown, u => Assert.Contains(Words.Unknown(UnknownReason.NeedsAdmin), u.Detail!, StringComparison.Ordinal));
        Assert.Contains(items, i => i.Status == HealthStatus.Fail);
        Assert.Contains(Loc.Instance.Format("Health.CouldNotCheck", ("count", unknown.Count)), health.Summary, StringComparison.Ordinal);
        health.NextCommand.Execute(null);

        var suspects = app.Page<SuspectsViewModel>();
        Assert.Equal(HonestyBucket.GuidedManual, suspects.PrimaryBucket);
        Assert.True(suspects.HasCouldNotCheck);
        Assert.Equal(Loc.Instance.Get("Suspects.ShowSteps"), suspects.PrimaryText);
        await suspects.PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();

        var guide = app.Page<GuideViewModel>();
        Assert.NotEmpty(guide.Steps);
        Assert.True(guide.HasPreflight);
        Assert.True(guide.HasOtherBranches);
        await guide.Steps[0].DoneCommand.ExecuteAsync();
        await app.IdleAsync();
        guide = app.Page<GuideViewModel>();
        Assert.True(guide.Steps[0].IsDone);
        var restartStep = guide.Steps.First(s => s.HasAction);
        await restartStep.ActionCommand!.ExecuteAsync();
        await app.IdleAsync();

        var restart = app.Page<RestartViewModel>();
        Assert.True(restart.IsFirmware);
        Assert.NotEmpty(restart.Warnings);
        Assert.True(restart.RestartNowCommand.CanExecute(null));
        await restart.RestartNowCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.Equal(Phase.RestartRequested, app.Shell.Session!.Phase);
        var restarting = app.Page<RestartingViewModel>();
        Assert.True(restarting.IsFirmware);
        restarting.SimulateResumeCommand.Execute(null);
        await app.IdleAsync();
        app.Page<ResumeViewModel>().ContinueCommand.Execute(null);
        await app.IdleAsync();
        app.Page<OutcomeViewModel>();
    });

    [Fact]
    public Task Limited_mode_blocks_fixes_with_a_plain_reason() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp(Fixtures.RealtekStutter);
        await app.ScanAsync(PrivilegeMode.Limited);
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        var top = app.Page<SuspectsViewModel>().Suspects[0];
        Assert.False(top.CanTryFix);
        Assert.Equal(Loc.Instance.Get("Suspects.NeedsPermission"), top.UnavailableText);
    });

    [Fact]
    public Task Agent_proposal_shows_badge_and_needs_a_second_separate_click() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        await app.Engine.SimulateAgentProposalAsync();
        await app.IdleAsync();

        var fix = app.Page<FixDetailViewModel>();
        Assert.True(fix.HasProposer);
        Assert.True(fix.IsAgentProposal);
        Assert.Contains("Claude Code (MCP)", fix.ProposerText!, StringComparison.Ordinal);
        Assert.False(fix.IsSecondConsent);
        Assert.True(fix.CanConsent);

        // The agent itself can never consent: the engine refuses ConsentGiven from a non-UI origin.
        app.Engine.Fake.ClientOrigin = EventOrigin.Mcp;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Engine.SendAsync(new ConsentGiven { EventId = "01JB6R7X3K9M2N4P5Q6R7S8T00", Utc = app.Shell.Now, Token = new ConsentToken { TokenId = "t", SessionId = "s", PreviewId = "p", FixId = "f", BindingHash = "b", PreviewHash = "h", Nonce = "n", IssuedUtc = app.Shell.Now } }, CancellationToken.None));
        app.Engine.Fake.ClientOrigin = EventOrigin.Ui;

        await fix.ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        var second = app.Page<FixDetailViewModel>();
        Assert.True(second.IsSecondConsent, $"err={app.Shell.Error} pending={app.Shell.PendingConsent?.Kind} phase={app.Shell.Session?.Phase}");
        Assert.False(second.SecondConsentCommand.CanExecute(null)); // "I understand" not ticked yet
        Assert.False(second.ConsentCommand.CanExecute(null));
        second.Understood = true;
        Assert.True(second.SecondConsentCommand.CanExecute(null));
        await second.SecondConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.Null(app.Shell.Error);
        Assert.Equal(Phase.AwaitingOutcome, app.Shell.Session!.Phase);
        Assert.Contains(app.Shell.Session.Attempts, a => a.FixId == "fix.net.dns_flush" && a.Reversibility == Reversibility.OneWay);

        app.Shell.GoUndoCommand.Execute(null);
        await app.IdleAsync();
        var undo = app.Page<UndoCenterViewModel>();
        var row = Assert.Single(undo.Rows);
        Assert.True(row.IsOneWay);
        Assert.False(row.CanUndo); // no fake undo button for one-way changes
        Assert.NotNull(row.OneWayText);
    });

    [Fact]
    public Task A_second_fix_starts_clean_and_one_way_undo_is_never_offered() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<DeadmanViewModel>().KeepCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<OutcomeViewModel>().StartTestCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.NotEmpty(app.Shell.Ticks);
        await app.Page<OutcomeViewModel>().AnswerCommand.ExecuteAsync("notfixed");
        await app.IdleAsync();

        // Second fix (proposed by an agent): its outcome screen must not reuse the first test's numbers.
        await app.Engine.SimulateAgentProposalAsync();
        await app.IdleAsync();
        var fix = app.Page<FixDetailViewModel>();
        Assert.False(fix.IsSecondConsent);
        Assert.DoesNotContain(app.Shell.Services.Loc.Get("Proposer.PlanNoName"), fix.ProposerText!, StringComparison.Ordinal);
        await fix.ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        var second = app.Page<FixDetailViewModel>();
        second.Understood = true;
        await second.SecondConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        var outcome = app.Page<OutcomeViewModel>();
        Assert.False(outcome.AskOutcome);
        Assert.Empty(app.Shell.Ticks);
        Assert.Null(app.Shell.PendingConsent);

        // An agent proposes undoing the one-way change: the player is asked, but there is no undo button to press.
        var oneWay = app.Shell.Session!.Attempts.Single(a => a.Reversibility == Reversibility.OneWay).AttemptId;
        app.Engine.Fake.ClientOrigin = EventOrigin.Mcp;
        await app.Engine.SendAsync(new UndoProposed { EventId = "01JB6R7X3K9M2N4P5Q6R7S8T02", Utc = app.Shell.Now, AttemptId = oneWay }, CancellationToken.None);
        app.Engine.Fake.ClientOrigin = EventOrigin.Ui;
        await app.IdleAsync();
        var ask = app.Page<UndoConsentViewModel>();
        Assert.False(ask.CanUndo);
        Assert.False(ask.UndoCommand.CanExecute(null));
    });

    [Fact]
    public Task Undo_center_undoes_a_fix() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<DeadmanViewModel>().KeepCommand.ExecuteAsync();
        await app.IdleAsync();
        app.Shell.GoUndoCommand.Execute(null);
        await app.IdleAsync();
        var undo = app.Page<UndoCenterViewModel>();
        var row = Assert.Single(undo.Rows);
        Assert.True(row.CanUndo);
        await row.UndoCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.Equal(AttemptStatus.Undone, app.Shell.Session!.Attempts[^1].Status);
        app.Shell.BackCommand.Execute(null);
        await app.IdleAsync();
        Assert.False(app.Shell.IsSidePage);
    });

    [Fact]
    public Task Deadman_undo_rolls_back() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<DeadmanViewModel>().UndoCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.Equal(Phase.Proposing, app.Shell.Session!.Phase);
        Assert.Equal(AttemptStatus.RolledBack, app.Shell.Session.Attempts[^1].Status);
    });

    [Fact]
    public Task Skip_returns_to_suspects_and_hides_the_skipped_fix() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().SkipCommand.ExecuteAsync();
        await app.IdleAsync();
        var suspects = app.Page<SuspectsViewModel>();
        Assert.False(suspects.Suspects[0].HasFix);
    });

    [Fact]
    public Task Intake_code_box_suggests_and_recognises_codes() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        app.Page<WelcomeViewModel>().StartCommand.Execute(null);
        var intake = app.Page<IntakeViewModel>();
        Assert.False(intake.NextCommand.CanExecute(null));
        intake.Code = "van9";
        Assert.Contains(intake.Suggestions, s => s.Id == "VAN 9003");
        intake.PickSuggestionCommand.Execute("VAN 9003");
        Assert.Contains("Secure Boot", intake.CodeHint!, StringComparison.Ordinal);
        intake.Code = "zzz 1";
        Assert.Equal(Loc.Instance.Get("Intake.CodeUnknown"), intake.CodeHint);
        Assert.True(intake.NextCommand.CanExecute(null));
        var league = Assert.Single(intake.Questions);
        league.PickCommand.Execute("no");
        Assert.Equal("no", league.Answer);
        intake.NextCommand.Execute(null);
        Assert.Equal(["zzz 1"], app.Shell.Intake.TypedCodes);
        Assert.Equal("no", app.Shell.Intake.Answers["ask.league_also_broken"]);
    });

    [Fact]
    public Task Language_switch_updates_the_open_page() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        var health = app.Page<HealthViewModel>();
        var english = health.Headline;
        app.Shell.SetLanguageCommand.Execute(Languages.Turkish);
        Assert.NotEqual(english, health.Headline);
        Assert.StartsWith("İyi haber", health.Headline, StringComparison.Ordinal);
        Assert.Equal("Sonuçlar", app.Shell.Rail[2].Label);
    });

    [Fact]
    public Task Unknown_reason_strings_exist_for_every_value() => StaHost.RunAsync(() =>
    {
        foreach (var reason in Enum.GetValues<UnknownReason>())
        {
            Assert.True(Loc.Has("Unknown." + reason), reason.ToString());
        }

        foreach (var bucket in Enum.GetValues<HonestyBucket>())
        {
            Assert.True(Loc.Has("Bucket." + bucket) && Loc.Has("Bucket." + bucket + ".Explain"), bucket.ToString());
        }

        foreach (var s in Enum.GetValues<AttemptStatus>())
        {
            Assert.True(Loc.Has("Attempt." + s), s.ToString());
        }

        foreach (var s in Enum.GetValues<HealthGroup>())
        {
            Assert.True(Loc.Has("HealthGroup." + s), s.ToString());
        }

        foreach (var s in Enum.GetValues<Phase>().Where(p => p is Phase.Completed or Phase.RolledBack or Phase.Abandoned))
        {
            Assert.True(Loc.Has("Completed.Title." + s) && Loc.Has("Completed.Sub." + s), s.ToString());
        }

        foreach (var s in Enum.GetValues<ResumeMechanism>())
        {
            Assert.True(Loc.Has("Restart.Resume." + s), s.ToString());
        }

        foreach (var id in ShareExporter.ExcludableSections)
        {
            Assert.True(Loc.Has("Section." + id), id);
        }

        foreach (var op in OpCatalog.All.Select(o => o.Op.Split('.')[0]).Distinct(StringComparer.Ordinal))
        {
            Assert.True(Loc.Has("Op." + op), op);
        }

        return Task.CompletedTask;
    });
}
