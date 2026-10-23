using System.IO;
using System.Globalization;
using System.Windows;
using VTD.App.Localization;
using VTD.App.Services;
using VTD.App.ViewModels;
using VTD.Contracts;
using VTD.Contracts.Knowledge;
using VTD.Knowledge;
using VTD.Testing.Fakes;

namespace VTD.App;

/// <summary>
/// VTDiag (the UI). Command line: <c>--lang en|tr|qps</c>, <c>--fixture realtek-2-5gbe-stutter|nahimic-black-screen|van9003-secureboot-off</c>,
/// <c>--helper</c> (open Helper mode), <c>--dev</c> (developer switches), <c>--demo</c> (the demo engine: fixture data,
/// nothing on this PC is read or changed; without it the app starts and talks to the real <c>VTDiag.Engine.exe</c>).
/// </summary>
public partial class App : Application
{
    /// <summary>Creates the shell for a command line (also used by tests).</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="io">User IO.</param>
    /// <param name="pacing">Demo pacing.</param>
    public static ShellViewModel CreateShell(IReadOnlyList<string> args, IUserIo io, TimeSpan pacing)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fixture = Arg(args, "--fixture") ?? Fixtures.RealtekStutter;
        if (!Fixtures.Names.Contains(fixture, StringComparer.Ordinal))
        {
            fixture = Fixtures.RealtekStutter;
        }

        var lang = Arg(args, "--lang")
            ?? (string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase) ? Languages.Turkish : Languages.English);
        Loc.Instance.Language = lang;
        var clock = TimeProvider.System;
        var demo = args.Contains("--demo", StringComparer.Ordinal);
        var log = demo ? AppLog.Null : new AppLog(clock);
        var services = new AppServices(demo ? FakeKnowledgeBase.Sample() : LoadKb(clock), demo ? new DemoEngineConnector(fixture, pacing) : new RealEngineConnector(log), io, clock, new UlidGenerator(clock), Loc.Instance)
        {
            DeveloperMode = args.Contains("--dev", StringComparer.Ordinal),
            Installer = demo ? null : new PortableInstaller(),
            Log = log,
        };
        log.Info("app.start", (demo ? "demo " : "real ") + services.AppVersion + " lang=" + lang + " windows=" + Environment.OSVersion.Version);
        var shell = new ShellViewModel(services);
        if (args.Contains("--helper", StringComparer.Ordinal))
        {
            shell.OpenSide(new HelperViewModel(shell));
        }

        return shell;
    }

    // The same bundled KB the engine loads (the UI only reads display text from it).
    private static IKnowledgeBase LoadKb(TimeProvider clock)
    {
        var options = new KbLoadOptions
        {
            BundledPath = Path.Combine(AppContext.BaseDirectory, "kb"),
            NowUtc = clock.GetUtcNow(),
            AppVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        };
        return Task.Run(() => new KnowledgeBaseLoader().LoadAsync(options, CancellationToken.None)).GetAwaiter().GetResult().Kb;
    }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        var shell = CreateShell(e.Args, new WpfUserIo(), TimeSpan.FromMilliseconds(180));

        // Last line of defence: a UI bug shows a calm error line instead of killing the app (the engine keeps the state).
        DispatcherUnhandledException += (_, args) =>
        {
            shell.Services.Log.Error("app.unhandled", args.Exception);
            shell.ShowError(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                shell.Services.Log.Error("app.crash", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            shell.Services.Log.Error("app.unobserved_task", args.Exception);
            args.SetObserved();
        };
        MainWindow = new MainWindow(shell);
        MainWindow.Show();
    }

    private static string? Arg(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
