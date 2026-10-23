using VTD.App.Localization;
using VTD.App.Services;
using VTD.App.ViewModels;
using VTD.Testing.Fakes;

namespace VTD.App.Tests;

/// <summary>The first Fix press of an unpacked zip: one UAC prompt copies VTDiag to Program Files, then it reopens from there.</summary>
public sealed class InstallTests
{
    [Fact]
    public async Task Copy_success_reopens_the_installed_app()
    {
        var installer = new FakeInstaller(0);
        var page = new InstallViewModel(Shell(installer));
        await page.ContinueCommand.ExecuteAsync();
        Assert.Equal(1, installer.Installs);
        Assert.NotNull(installer.ReopenedIn);
        Assert.Null(page.ErrorText);
    }

    [Theory]
    [InlineData(null, "Install.Declined")]
    [InlineData(4, "Install.Busy")]
    [InlineData(5, "Install.Failed")]
    public async Task A_declined_prompt_or_a_failed_copy_stays_here_and_says_so(int? code, string key)
    {
        var installer = new FakeInstaller(code);
        var page = new InstallViewModel(Shell(installer));
        await page.ContinueCommand.ExecuteAsync();
        Assert.Null(installer.ReopenedIn);
        Assert.Equal(Loc.Instance.Get(key), page.ErrorText);
        Assert.False(page.IsBusy);
        Assert.True(page.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void The_demo_never_offers_the_copy()
    {
        Assert.False(new TestApp().Shell.NeedsInstallToFix);
    }

    private static ShellViewModel Shell(IPortableInstaller installer)
    {
        // The UI language stays as is: it is process-global and the screenshot tests own a window bound to it.
        var clock = new FakeClock();
        var services = new AppServices(FakeKnowledgeBase.Sample(), new DemoEngineConnector(Fixtures.RealtekStutter, TimeSpan.Zero), new FakeUserIo(), clock, new SequentialIdGenerator(clock), Loc.Instance)
        {
            Installer = installer,
        };
        return new ShellViewModel(services);
    }

    private sealed class FakeInstaller(int? code) : IPortableInstaller
    {
        public int Installs { get; private set; }

        public string? ReopenedIn { get; private set; }

        public Task<int?> InstallAsync(CancellationToken cancellationToken)
        {
            Installs++;
            return Task.FromResult(code);
        }

        public bool ReopenInstalled(string language)
        {
            ReopenedIn = language;
            return true;
        }
    }
}
