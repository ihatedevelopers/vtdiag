using VTD.App.Localization;
using VTD.App.Services;
using VTD.App.ViewModels;
using VTD.Contracts;
using VTD.Testing.Fakes;

namespace VTD.App.Tests;

/// <summary>Records clipboard and saves; returns a preset file for "open".</summary>
internal sealed class FakeUserIo : IUserIo
{
    public List<string> Clipboard { get; } = [];

    public List<(string Name, byte[] Content)> Saved { get; } = [];

    public List<string> Links { get; } = [];

    public (string Name, byte[] Content)? NextOpen { get; set; }

    public void SetClipboard(string text) => Clipboard.Add(text);

    public bool Save(string suggestedName, string filter, byte[] content)
    {
        Saved.Add((suggestedName, content));
        return true;
    }

    public (string Name, byte[] Content)? Open(string filter) => NextOpen;

    public bool OpenLink(string url)
    {
        Links.Add(url);
        return LinkOpener.IsAllowed(url);
    }
}

/// <summary>A shell on the demo engine with a fake clock-free setup (no pacing).</summary>
internal sealed class TestApp
{
    public TestApp(string fixture = Fixtures.RealtekStutter, string language = Languages.English)
    {
        Loc.Instance.Language = language;
        Io = new FakeUserIo();
        Connector = new DemoEngineConnector(fixture, TimeSpan.Zero);
        var clock = new FakeClock();
        Services = new AppServices(FakeKnowledgeBase.Sample(), Connector, Io, clock, new SequentialIdGenerator(clock), Loc.Instance);
        Shell = new ShellViewModel(Services);
    }

    public FakeUserIo Io { get; }

    public DemoEngineConnector Connector { get; }

    public AppServices Services { get; }

    public ShellViewModel Shell { get; }

    public DemoEngine Engine => Connector.Current ?? throw new InvalidOperationException("Not connected.");

    public T Page<T>()
        where T : PageViewModel
        => Shell.Page as T ?? throw new Xunit.Sdk.XunitException($"Expected page {typeof(T).Name}, got {Shell.Page.GetType().Name} (phase {Shell.Session?.Phase}).");

    public async Task IdleAsync()
    {
        await Shell.IdleAsync().ConfigureAwait(true);
        await Shell.IdleAsync().ConfigureAwait(true);
    }

    /// <summary>Welcome → intake (symptoms) → permission → scan → health.</summary>
    public async Task ScanAsync(PrivilegeMode mode = PrivilegeMode.Elevated, params string[] symptoms)
    {
        Page<WelcomeViewModel>().StartCommand.Execute(null);
        var intake = Page<IntakeViewModel>();
        foreach (var chip in intake.Symptoms.Where(c => symptoms.Contains(c.Id, StringComparer.Ordinal)))
        {
            chip.IsSelected = true;
        }

        if (symptoms.Length == 0)
        {
            intake.Symptoms[0].IsSelected = true;
        }

        Assert.True(intake.NextCommand.CanExecute(null));
        intake.NextCommand.Execute(null);
        var permission = Page<PermissionViewModel>();
        await (mode == PrivilegeMode.Elevated ? permission.ContinueCommand : permission.LimitedCommand).ExecuteAsync().ConfigureAwait(true);
        await IdleAsync().ConfigureAwait(true);
    }
}
