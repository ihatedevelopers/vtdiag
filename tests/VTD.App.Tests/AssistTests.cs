using System.Text;
using VTD.App.ViewModels;
using VTD.Contracts;
using VTD.Contracts.Assist;
using VTD.Contracts.Ipc;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Testing.Fakes;

namespace VTD.App.Tests;

/// <summary>Assisted mode: the player shares a .vtdscan, the helper writes a plan, the player imports it.</summary>
public sealed class AssistTests
{
    [Fact]
    public Task Scan_to_helper_to_plan_to_player_round_trip() => StaHost.RunAsync(async () =>
    {
        // Player: scan, then save a scan file for a helper.
        var player = new TestApp();
        await player.ScanAsync();
        player.Shell.GoReportCommand.Execute(null);
        await player.IdleAsync();
        await player.Page<ReportViewModel>().SaveBundleCommand.ExecuteAsync();
        var bundleBytes = player.Io.Saved[^1].Content;
        Assert.EndsWith(".vtdscan", player.Io.Saved[^1].Name, StringComparison.Ordinal);

        // Helper (a different app instance, no engine): open it, read the math, write a plan.
        var helperApp = new TestApp();
        var helper = new HelperViewModel(helperApp.Shell);
        helperApp.Shell.OpenSide(helper);
        helper.Load(bundleBytes);
        Assert.True(helper.IsLoaded, helper.Status);
        Assert.NotEmpty(helper.Hypotheses);
        Assert.NotEmpty(helper.Facts);
        Assert.Contains("realtek", helper.Summary, StringComparison.OrdinalIgnoreCase);
        helper.Search = "secureBoot";
        Assert.All(helper.Facts, f => Assert.Contains("secureBoot", f, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(helper.Facts, f => f.Contains(CanaryTokens.UserName, StringComparison.OrdinalIgnoreCase));

        var fix = helper.Candidates.First(c => c.Kind == PlanStepKind.Fix && c.Id == "fix.net.disable_unused_adapter");
        Assert.NotEmpty(fix.Bind);
        fix.AddCommand.Execute(null);
        helper.Candidates.First(c => c.Kind == PlanStepKind.Monitor).AddCommand.Execute(null);
        helper.Draft[0].Note = "You're on Wi-Fi so this is safe. See https://example.com/x";
        helper.Author = "u/HelperMod";
        helper.Message = "Hey! Looks like your spare network port. Try step 1, then play a DM.";
        helper.SavePlanCommand.Execute(null);
        var planText = Encoding.UTF8.GetString(helperApp.Io.Saved[^1].Content);
        var plan = VtdJson.DeserializeLenient<FixPlan>(planText);
        Assert.Equal(2, plan.Steps.Count);
        Assert.True(plan.ExpiresUtc - plan.CreatedUtc <= PlanLimits.MaxLifetime);
        helper.CopyAiCommand.Execute(null);
        Assert.Contains("\"$schema\"", helperApp.Io.Clipboard[^1], StringComparison.Ordinal);

        // Player: open the plan. Bindings resolve to the live adapter (same install salt as the export).
        player.Shell.BackCommand.Execute(null);
        player.Shell.GoPlanImportCommand.Execute(null);
        var import = player.Page<PlanImportViewModel>();
        Assert.True(import.CanImport);
        await import.LoadAsync(planText);
        Assert.True(import.IsLoaded);
        Assert.Equal(PlanTrust.Unsigned, import.Trust);
        Assert.Contains("u/HelperMod", import.TrustText, StringComparison.Ordinal);
        Assert.Contains("spare network port", import.Message!, StringComparison.Ordinal);
        var step = import.Steps[0];
        Assert.False(step.IsGreyed, step.Reason);
        Assert.Contains(Localization.Loc.Instance.Get("Plan.LinkFromHelper"), step.Note!, StringComparison.Ordinal);
        Assert.Equal(validationPlanId(player), plan.PlanId);

        await step.StartCommand.ExecuteAsync();
        await player.IdleAsync();
        var consent = player.Page<FixDetailViewModel>();
        Assert.True(consent.HasProposer);
        Assert.Contains("u/HelperMod", consent.ProposerText!, StringComparison.Ordinal);
        Assert.True(consent.CanConsent); // every step still needs the player's own click

        static string? validationPlanId(TestApp app) => app.Shell.Session?.ImportedPlanId;
    });

    [Fact]
    public Task Expired_and_tampered_plans_are_not_usable() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        await app.ScanAsync();
        var import = new PlanImportViewModel(app.Shell);
        app.Shell.OpenSide(import);
        var now = app.Engine.Fake.Clock.GetUtcNow();
        var expired = new FixPlan
        {
            Schema = PlanLimits.CurrentSchema,
            PlanId = "01JB7S0000000000000000000A",
            ForSnapshotId = "01JB6R7X3K9M2N4P5Q6R7S8T00",
            KbMin = app.Engine.Fake.Kb.Version.Version,
            CreatedUtc = now.AddDays(-9),
            ExpiresUtc = now.AddDays(-2),
            Author = new PlanAuthor { Name = "someone" },
            Steps = [new PlanGuideStep { GuideId = "guide.secureboot.enable" }],
        };
        await import.LoadAsync(VtdJson.Serialize(expired));
        Assert.True(import.HasIssues);
        Assert.Contains(Localization.Loc.Instance.Get("PlanIssue.expired"), import.Issues);
        Assert.All(import.Steps, s => Assert.True(s.IsGreyed));
        Assert.Null(app.Shell.Session!.ImportedPlanId);

        var envelope = new SignedPlanEnvelope { Format = SignedPlanEnvelope.CurrentFormat, Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(VtdJson.Serialize(expired with { ExpiresUtc = now.AddDays(2), CreatedUtc = now }))), PublicKey = "ed25519:" + Convert.ToBase64String(new byte[32]), Signature = Convert.ToBase64String(new byte[64]) };
        await import.LoadAsync(VtdJson.Serialize(envelope));
        Assert.Equal(PlanTrust.BadSignature, import.Trust);
        Assert.Equal(Localization.Loc.Instance.Get("Plan.Trust.Bad"), import.TrustText);
        Assert.All(import.Steps, s => Assert.False(s.StartCommand.CanExecute(null)));

        await import.LoadAsync("not json at all");
        Assert.False(import.IsLoaded);
    });

    [Fact]
    public Task Helper_rejects_a_damaged_scan_file() => StaHost.RunAsync(async () =>
    {
        var app = new TestApp();
        var helper = new HelperViewModel(app.Shell);
        var bytes = await Services.DemoEngine.BuildSampleBundleAsync(Fixtures.NahimicBlackScreen);
        bytes[bytes.Length / 2] ^= 0x5A;
        helper.Load(bytes);
        Assert.False(helper.IsLoaded);
        Assert.NotNull(helper.Status);
    });

    [Fact]
    public Task Undo_proposed_by_an_agent_asks_the_player() => StaHost.RunAsync(async () =>
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
        var attempt = app.Shell.Session!.Attempts[^1].AttemptId;

        app.Engine.Fake.ClientOrigin = EventOrigin.Mcp;
        await app.Engine.SendAsync(new UndoProposed { EventId = "01JB6R7X3K9M2N4P5Q6R7S8T01", Utc = app.Shell.Now, AttemptId = attempt }, CancellationToken.None);
        app.Engine.Fake.ClientOrigin = EventOrigin.Ui;
        await app.IdleAsync();

        var ask = app.Page<UndoConsentViewModel>();
        Assert.Equal(ConsentKind.Undo, ask.Request.Kind);
        Assert.Equal(Localization.Loc.Instance.Get("Proposer.Mcp"), ask.ProposerText);
        await ask.UndoCommand.ExecuteAsync();
        await app.IdleAsync();
        Assert.Equal(Contracts.Fixes.AttemptStatus.Undone, app.Shell.Session!.Attempts[^1].Status);
    });
}
