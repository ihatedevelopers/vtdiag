using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VTD.App.Localization;
using VTD.App.ViewModels;
using VTD.App.Views;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.App.Tests;

/// <summary>
/// Renders real screens (the app's own XAML and theme) to PNG in an off-screen, never-activated window and saves them to
/// <c>docs/screenshots/</c> for review (with <c>VTD_UPDATE_SCREENSHOTS=1</c>). Also asserts each screen lays out without throwing and with no missing strings.
/// </summary>
public sealed class ScreenshotTests
{
    private const int W = 1120;
    private const int H = 760;

    [Fact]
    public Task Render_the_main_screens() => StaHost.RunAsync(async () =>
    {
        // Welcome / intake / scanning
        var app = new TestApp();
        await Shot(app, "01-welcome-en");
        Loc.Instance.Language = Languages.Turkish;
        app.Page<WelcomeViewModel>().StartCommand.Execute(null);
        var intake = app.Page<IntakeViewModel>();
        intake.Symptoms.First(s => s.Id == "stutter").IsSelected = true;
        intake.Code = "van9";
        await Shot(app, "02-intake-tr");
        Loc.Instance.Language = Languages.English;

        var scan = new ScanViewModel(app.Shell);
        app.Shell.ScanProgress.AddRange(
        [
            new ScanProgress { CollectorId = "os", Area = ScanArea.Windows, Status = CollectorStatus.Ok, Completed = 1, Total = 9 },
            new ScanProgress { CollectorId = "hardware", Area = ScanArea.Hardware, Status = CollectorStatus.Ok, Completed = 2, Total = 9 },
            new ScanProgress { CollectorId = "security", Area = ScanArea.Security, Status = CollectorStatus.Partial, Completed = 3, Total = 9 },
            new ScanProgress { CollectorId = "devices", Area = ScanArea.Drivers, Status = CollectorStatus.TimedOut, Completed = 4, Total = 9 },
            new ScanProgress { CollectorId = "software", Area = ScanArea.Apps, Completed = 4, Total = 9 },
        ]);
        app.Shell.ShowFlow(scan);
        scan.Refresh();
        await Shot(app, "03-scanning-en");

        // Realtek: suspects, consent, deadman, report
        app = new TestApp();
        await app.ScanAsync(PrivilegeMode.Elevated, "stutter");
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await Shot(app, "05-suspects-en");
        Loc.Instance.Language = Loc.Pseudo;
        await Shot(app, "13-suspects-pseudo");
        Loc.Instance.Language = Languages.English;
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        await Shot(app, "06-fix-consent-en", armed: true);
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        await Shot(app, "08-deadman-en", armed: true);
        await app.Page<DeadmanViewModel>().KeepCommand.ExecuteAsync();
        await app.IdleAsync();
        app.Shell.GoReportCommand.Execute(null);
        await app.IdleAsync();
        await Shot(app, "11-report-en");
        app.Shell.GoUndoCommand.Execute(null);
        await app.IdleAsync();
        await Shot(app, "15-undo-center-en");

        // Agent proposal + one-way second click
        app = new TestApp();
        await app.ScanAsync();
        await app.Engine.SimulateAgentProposalAsync();
        await app.IdleAsync();
        await app.Page<FixDetailViewModel>().ConsentCommand.ExecuteAsync();
        await app.IdleAsync();
        app.Page<FixDetailViewModel>().Understood = true;
        await Shot(app, "07-agent-second-consent-en", armed: true);

        // VAN 9003 limited: health with unknowns, guide, restart into BIOS
        app = new TestApp(Fixtures.Van9003SecureBootOff);
        await app.ScanAsync(PrivilegeMode.Limited, "error_code");
        await Shot(app, "04-health-limited-en");
        app.Page<HealthViewModel>().NextCommand.Execute(null);
        await app.Page<SuspectsViewModel>().PrimaryCommand.ExecuteAsync();
        await app.IdleAsync();
        Loc.Instance.Language = Languages.Turkish;
        await Shot(app, "10-guide-tr");
        Loc.Instance.Language = Languages.English;
        await app.Page<GuideViewModel>().Steps.First(s => s.HasAction).ActionCommand!.ExecuteAsync();
        await app.IdleAsync();
        await Shot(app, "09-restart-bios-en", armed: true);

        // Helper mode
        app = new TestApp();
        var helper = new HelperViewModel(app.Shell);
        app.Shell.OpenSide(helper);
        helper.Load(await Services.DemoEngine.BuildSampleBundleAsync(Fixtures.RealtekStutter));
        await Shot(app, "12-helper-en");

        // Plan import
        app = new TestApp();
        await app.ScanAsync();
        var import = new PlanImportViewModel(app.Shell);
        app.Shell.OpenSide(import);
        var adapter = new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId };
        var plan = $$"""
            {"schema":1,"planId":"01JB7S0000000000000000000A","forSnapshotId":"{{app.Shell.Session!.SnapshotId}}","kbMin":"{{app.Engine.Fake.Kb.Version.Version}}",
             "createdUtc":"2026-10-21T17:00:00Z","expiresUtc":"2026-10-25T17:00:00Z","author":{"name":"u/HelperMod"},
             "message":"Hey! Looks like your spare network port. Try step 1, then play a Deathmatch.",
             "steps":[{"kind":"fix","fixId":"fix.net.disable_unused_adapter","bind":{"adapter":["{{app.Engine.Redactor.ShareEntityId(adapter).Replace("\\", "\\\\", StringComparison.Ordinal)}}"]},"note":"You're on Wi-Fi, so this is safe."},
                      {"kind":"monitor","minutes":10,"note":"Play one Deathmatch."},
                      {"kind":"fix","fixId":"fix.not.in.kb"}]}
            """;
        await import.LoadAsync(plan);
        await Shot(app, "14-plan-import-en");

        Assert.Empty(Loc.Instance.MissingKeys);
    });

    private static async Task Shot(TestApp app, string name, bool armed = false)
    {
        var view = new ShellView { DataContext = app.Shell, Width = W, Height = H };
        var window = LocalizationAndSafetyTests.OffscreenWindow(view);
        window.Width = W;
        window.Height = H;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            if (armed)
            {
                await Task.Delay(1700); // let consent buttons arm, as a player would see them
            }

            // The OS may clamp an off-screen window to the work area; lay the view out at the reference size ourselves.
            view.Measure(new Size(W, H));
            view.Arrange(new Rect(0, 0, W, H));
            var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            // VTD_UPDATE_SCREENSHOTS=1 refreshes docs/screenshots; otherwise they go next to the test binaries.
            var dir = string.Equals(Environment.GetEnvironmentVariable("VTD_UPDATE_SCREENSHOTS"), "1", StringComparison.Ordinal)
                ? Path.Combine(RepoRoot(), "docs", "screenshots")
                : Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            using var file = File.Create(Path.Combine(dir, name + ".png"));
            encoder.Save(file);
            Assert.True(view.ActualHeight > 0);
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
