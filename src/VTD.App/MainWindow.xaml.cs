using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VTD.App.Localization;
using VTD.App.Services;
using VTD.App.ViewModels;
using VTD.Contracts.Sessions;

namespace VTD.App;

/// <summary>The main window: custom dark chrome (WPF WindowChrome, no interop), the shell, the tray.</summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private TrayService? _tray;
    private bool _exiting;

    /// <summary>Creates the window.</summary>
    /// <param name="vm">Shell view model.</param>
    public MainWindow(ShellViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        Shell.DataContext = vm;
        vm.AttentionRequested += (_, _) => RequestAttention();
        vm.PropertyChanged += OnShellChanged;
        Loc.Instance.LanguageChanged += (_, _) => Title = Loc.Instance.Get("Window.Title");
        Title = Loc.Instance.Get("Window.Title");
        Loaded += (_, _) => _tray ??= new TrayService(vm, BringForward, ExitApp);
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("MinButton") is Button min)
        {
            min.Click += (_, _) => WindowState = WindowState.Minimized;
        }

        if (GetTemplateChild("MaxButton") is Button max)
        {
            max.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        if (GetTemplateChild("CloseButton") is Button close)
        {
            close.Click += (_, _) => Close();
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_exiting && _vm.Session?.Phase == Phase.Applying)
        {
            // The engine keeps going even if the window dies; closing now only hides progress.
            e.Cancel = true;
            _vm.Error = Loc.Instance.Get("Applying.DontClose");
            return;
        }

        if (!_exiting && _vm.Session?.Phase == Phase.Monitoring)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        base.OnClosed(e);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Session) && _vm.Session?.Phase == Phase.Monitoring && IsVisible)
        {
            Hide(); // no WPF rendering while the game runs
        }
    }

    /// <summary>
    /// A decision is waiting (consent, possibly proposed by an agent). Never steal the foreground (the player may be in a
    /// match): show the window without activating it and mark the taskbar button; during monitoring stay in the tray.
    /// </summary>
    private void RequestAttention()
    {
        if (_vm.Session?.Phase == Phase.Monitoring)
        {
            return;
        }

        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Paused;
        TaskbarItemInfo.ProgressValue = 1;
        if (!IsVisible)
        {
            ShowActivated = false;
            Show();
            ShowActivated = true;
        }
    }

    /// <inheritdoc />
    protected override void OnActivated(EventArgs e)
    {
        if (TaskbarItemInfo is not null)
        {
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }

        base.OnActivated(e);
    }

    private void BringForward()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }
}
