using System.ComponentModel;
using VTD.App.Localization;
using VTD.App.ViewModels;
using VTD.Contracts.Sessions;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VTD.App.Services;

/// <summary>
/// The tray icon (design_B.md section 7): monitoring status, open, "mark last match as bad", exit. During monitoring the
/// window hides to the tray so WPF renders nothing while the game runs. No balloons or toasts while the game runs.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _open;
    private readonly Forms.ToolStripMenuItem _markBad;
    private readonly Forms.ToolStripMenuItem _exit;
    private readonly ShellViewModel _shell;
    private readonly Drawing.Icon _image;

    /// <summary>Creates the tray icon.</summary>
    /// <param name="shell">Shell.</param>
    /// <param name="open">Shows the window.</param>
    /// <param name="exit">Exits the app.</param>
    public TrayService(ShellViewModel shell, Action open, Action exit)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(exit);
        _image = BuildIcon();
        _status = new Forms.ToolStripMenuItem { Enabled = false };
        _open = new Forms.ToolStripMenuItem();
        _open.Click += (_, _) => open();
        _markBad = new Forms.ToolStripMenuItem { Enabled = false };
        _markBad.Click += async (_, _) =>
        {
            try
            {
                await _shell.SendAsync(new MonitorMarkBadRequested { EventId = _shell.NewId(), Utc = _shell.Now }).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
            {
                // The engine went away: marking is best effort, the monitor summary simply has no mark.
            }
        };
        _exit = new Forms.ToolStripMenuItem();
        _exit.Click += (_, _) => exit();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_status, new Forms.ToolStripSeparator(), _open, _markBad, new Forms.ToolStripSeparator(), _exit]);
        _icon = new Forms.NotifyIcon { Icon = _image, ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _shell.PropertyChanged += OnShellChanged;
        Loc.Instance.LanguageChanged += (_, _) => UpdateTexts();
        UpdateTexts();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shell.PropertyChanged -= OnShellChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Session))
        {
            UpdateTexts();
        }
    }

    private void UpdateTexts()
    {
        var l = Loc.Instance;
        var monitoring = _shell.Session?.Phase == Phase.Monitoring;
        var status = monitoring ? l.Get("Tray.Monitoring") : l.Get("Tray.Idle");
        _status.Text = status;
        _icon.Text = Words.Clip("VTDiag · " + status, 60);
        _open.Text = l.Get("Tray.Open");
        _markBad.Text = l.Get("Tray.MarkBad");
        _markBad.Enabled = monitoring;
        _exit.Text = l.Get("Tray.Exit");
    }

    private static Drawing.Icon BuildIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 0x1B, 0x23, 0x2D));
            Drawing.PointF[] chamfer = [new(2, 2), new(24, 2), new(30, 8), new(30, 30), new(2, 30)];
            g.FillPolygon(bg, chamfer);
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(255, 0xED, 0xE6, 0xDA), 3.2f) { LineJoin = Drawing.Drawing2D.LineJoin.Round, StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, new Drawing.PointF[] { new(8, 16), new(14, 22), new(24, 10) });
        }

        var handle = bmp.GetHicon();
        using var temp = Drawing.Icon.FromHandle(handle);
        return (Drawing.Icon)temp.Clone();
    }
}
