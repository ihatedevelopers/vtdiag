using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VTD.App.ViewModels;
using VTD.Contracts.Analysis;

namespace VTD.App.Controls;

/// <summary>
/// A status mark for checklists: check (healthy), triangle (worth a look), cross (fails), question (couldn't check),
/// dash (not applicable). Shape AND colour differ, so it reads without colour vision.
/// </summary>
public sealed class StatusGlyph : FrameworkElement
{
    /// <summary>Status.</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(HealthStatus), typeof(StatusGlyph), new FrameworkPropertyMetadata(HealthStatus.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Status.</summary>
    public HealthStatus Status { get => (HealthStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(18, 18);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var brush = ThemeBrushes.ForStatus(this, Status);
        var pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var faint = new SolidColorBrush(Color.FromArgb(0x26, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B));
        drawingContext.DrawEllipse(faint, null, new Point(9, 9), 9, 9);
        switch (Status)
        {
            case HealthStatus.Ok:
                drawingContext.DrawGeometry(null, pen, Geometry.Parse("M5,9.5 L8,12.5 L13.5,6.5"));
                break;
            case HealthStatus.Warn:
                drawingContext.DrawGeometry(null, pen, Geometry.Parse("M9,5 L9,10 M9,13 L9,13.2"));
                break;
            case HealthStatus.Fail:
                drawingContext.DrawGeometry(null, pen, Geometry.Parse("M6,6 L12,12 M12,6 L6,12"));
                break;
            case HealthStatus.Unknown:
                drawingContext.DrawGeometry(null, pen, Geometry.Parse("M6.5,7 C6.5,4.5 11.5,4.5 11.5,7 C11.5,8.8 9,9 9,10.8 M9,13.2 L9,13.4"));
                break;
            default:
                drawingContext.DrawGeometry(null, pen, Geometry.Parse("M6,9 L12,9"));
                break;
        }
    }
}

/// <summary>Theme brush lookup shared by code-drawn glyphs and converters.</summary>
public static class ThemeBrushes
{
    /// <summary>Brush for a status.</summary>
    /// <param name="element">Element (resource lookup).</param>
    /// <param name="status">Status.</param>
    public static Brush ForStatus(FrameworkElement element, HealthStatus status)
    {
        ArgumentNullException.ThrowIfNull(element);
        var key = status switch
        {
            HealthStatus.Ok => "Good",
            HealthStatus.Warn => "Warn",
            HealthStatus.Fail => "Fail",
            HealthStatus.Unknown => "UnknownTone",
            _ => "TextFaint",
        };
        return element.TryFindResource(key + "Brush") as Brush ?? System.Windows.Media.Brushes.Gray;
    }
}

/// <summary>Badge tone → brush.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            BadgeTone.Good => "GoodBrush",
            BadgeTone.Caution => "WarnBrush",
            BadgeTone.Risk => "FailBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Bucket → accent brush (the honesty bucket colour on suspect cards).</summary>
public sealed class BucketBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Contracts.HonestyBucket.FixableHere => "GoodBrush",
            Contracts.HonestyBucket.GuidedManual => "InfoBrush",
            Contracts.HonestyBucket.ExternalNetwork or Contracts.HonestyBucket.RiotOrAccount => "WarnBrush",
            Contracts.HonestyBucket.LikelyHardware => "FailBrush",
            _ => "UnknownToneBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Null/empty/false/zero → Collapsed (parameter "invert" flips it).</summary>
public sealed class VisibleConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is string p && string.Equals(p, "invert", StringComparison.Ordinal))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Converter instances used from XAML.</summary>
public static class ConverterHelpers
{
    /// <summary>[status text, isProblem] → Visible when there is a non-problem status.</summary>
    public static IMultiValueConverter OkStatus { get; } = new OkStatusConverter();

    private sealed class OkStatusConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values is [string { Length: > 0 }, bool problem] && !problem ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
