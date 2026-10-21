using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VTD.App.Controls;
using VTD.App.Localization;
using VTD.App.Mvvm;
using VTD.App.Services;
using VTD.App.ViewModels;
using VTD.Contracts;

namespace VTD.App.Tests;

/// <summary>Strings (EN/TR/pseudo) and the consent button's hardening.</summary>
public sealed partial class LocalizationAndSafetyTests
{
    [Fact]
    public void Every_english_key_has_a_turkish_translation_with_the_same_placeholders()
    {
        var keys = Loc.AllKeys();
        Assert.True(keys.Count > 400, "resx not embedded?");
        foreach (var key in keys)
        {
            var en = Loc.Raw(key, Languages.English);
            var tr = Loc.Raw(key, Languages.Turkish);
            Assert.False(string.IsNullOrWhiteSpace(tr), "missing tr: " + key);
            Assert.Equal(Placeholders(en!), Placeholders(tr!));
        }
    }

    [Fact]
    public void Turkish_is_actually_translated()
    {
        var keys = Loc.AllKeys().Where(k => !k.StartsWith("Report.Filter", StringComparison.Ordinal)).ToList();
        var same = keys.Count(k => string.Equals(Loc.Raw(k, Languages.English), Loc.Raw(k, Languages.Turkish), StringComparison.Ordinal));
        Assert.True(same < keys.Count / 10, $"{same} of {keys.Count} Turkish strings equal the English ones");
        Assert.Contains("ı", Loc.Raw("Welcome.Promise1", Languages.Turkish)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Pseudo_localization_is_40_percent_longer_and_keeps_placeholders()
    {
        const string text = "Good news: {count} things look healthy.";
        var p = Loc.Pseudolocalize(text);
        Assert.True(p.Length >= text.Length * 1.4, p);
        Assert.Contains("{count}", p, StringComparison.Ordinal);
        Assert.StartsWith("[", p, StringComparison.Ordinal);
        Assert.EndsWith("]", p, StringComparison.Ordinal);
    }

    [Fact]
    public Task Kb_text_falls_back_without_showing_raw_keys() => StaHost.RunAsync(() =>
    {
        var text = new KbText(Testing.Fakes.FakeKnowledgeBase.Sample(), Loc.Instance);
        Loc.Instance.Language = Languages.Turkish;
        try
        {
            Assert.Equal("Kullanmadığın ağ girişini kapat", text.Get("fix.disable_adapter.title"));
            Assert.Equal("14 günde 7 Realtek sürücü sıfırlaması", text.Get("rule.realtek25.ev.resets", new Dictionary<string, string>(StringComparer.Ordinal) { ["count"] = "7" }));
            Assert.Equal("Unheard of thing", text.Get("rule.unheard_of_thing.title"));
        }
        finally
        {
            Loc.Instance.Language = Languages.English;
        }

        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("https://support.riotgames.com/riot/client/error-van-9003", true)]
    [InlineData("https://aka.ms/myrecoverykey", true)]
    [InlineData("http://support.riotgames.com/x", false)]
    [InlineData("https://evil.example/support.riotgames.com", false)]
    [InlineData("https://user@support.riotgames.com/", false)]
    [InlineData("https://support.riotgames.com:8443/", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:windowsupdate", false)]
    public void Only_official_https_links_open(string url, bool allowed) => Assert.Equal(allowed, LinkOpener.IsAllowed(url));

    [Fact]
    public void Helper_links_are_marked_and_never_actions()
    {
        var marked = PlanImportViewModel.MarkLinks("See https://evil.example/run.exe and www.x.test now");
        Assert.Equal(2, Regex.Matches(marked, Regex.Escape(Loc.Instance.Get("Plan.LinkFromHelper"))).Count);
    }

    [Fact]
    public Task Consent_button_ignores_automation_and_programmatic_clicks() => StaHost.RunAsync(async () =>
    {
        var clicks = 0;
        var button = new ConsentButton { Content = "Fix it", Command = new RelayCommand(() => clicks++), ArmDelay = TimeSpan.Zero };
        var window = OffscreenWindow(button);
        try
        {
            await Task.Delay(50);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(button.IsArmed);

            // UI Automation can't press it.
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            Assert.Null(peer.GetPattern(PatternInterface.Invoke));

            // Raising Click from code doesn't run the command.
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // Neither does the protected OnClick path without a real press (what automation and IsDefault would use).
            typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, null);
            Assert.Equal(0, clicks);

            // It never becomes the window's default or cancel button (Enter/Esc can't consent).
            button.IsDefault = true;
            button.IsCancel = true;
            Assert.False(button.IsDefault);
            Assert.False(button.IsCancel);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Consent_button_is_not_armed_right_after_it_appears() => StaHost.RunAsync(async () =>
    {
        var button = new ConsentButton { Content = "Fix it" };
        var window = OffscreenWindow(button);
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(button.IsArmed);
            await Task.Delay(900);
            Assert.True(button.IsArmed);
        }
        finally
        {
            window.Close();
        }
    });

    internal static Window OffscreenWindow(UIElement content)
    {
        // Never visible to the person at the PC: off-screen, not activated, no taskbar entry.
        var window = new Window
        {
            Content = content,
            Width = 300,
            Height = 200,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        return window;
    }

    private static List<string> Placeholders(string s) => [.. PlaceholderPattern().Matches(s).Select(m => m.Value).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{[a-zA-Z]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
