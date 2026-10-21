using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VTD.App.Controls;

/// <summary>
/// VTDiag's signature panel: a rectangle with its top-right corner cut at 45° (an original, tactical shape; no Riot
/// assets). Draws its own background and 1 px outline and pads its child.
/// </summary>
public sealed class ChamferBorder : Decorator
{
    /// <summary>Fill.</summary>
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Outline.</summary>
    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Outline width.</summary>
    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(ChamferBorder), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Size of the cut corner.</summary>
    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferBorder), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Inner padding.</summary>
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder), new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Accent bar on the left edge (status colour); null for none.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(ChamferBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fill.</summary>
    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Outline.</summary>
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }

    /// <summary>Outline width.</summary>
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }

    /// <summary>Cut corner size.</summary>
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }

    /// <summary>Padding.</summary>
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    /// <summary>Left accent bar.</summary>
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        var p = Padding;
        var inner = new Size(Math.Max(0, constraint.Width - p.Left - p.Right), Math.Max(0, constraint.Height - p.Top - p.Bottom));
        Child?.Measure(inner);
        var desired = Child?.DesiredSize ?? default;
        return new Size(desired.Width + p.Left + p.Right, desired.Height + p.Top + p.Bottom);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top, Math.Max(0, arrangeSize.Width - p.Left - p.Right), Math.Max(0, arrangeSize.Height - p.Top - p.Bottom)));
        return arrangeSize;
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var t = BorderBrush is null ? 0 : BorderThickness;
        var half = t / 2;
        var c = Math.Min(Chamfer, Math.Min(w, h) / 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(half, half), true, true);
            ctx.LineTo(new Point(w - c - half, half), true, false);
            ctx.LineTo(new Point(w - half, c + half), true, false);
            ctx.LineTo(new Point(w - half, h - half), true, false);
            ctx.LineTo(new Point(half, h - half), true, false);
        }

        geometry.Freeze();
        var pen = t > 0 ? new Pen(BorderBrush, t) : null;
        pen?.Freeze();
        drawingContext.DrawGeometry(Background, pen, geometry);
        if (Accent is { } accent)
        {
            drawingContext.DrawRectangle(accent, null, new Rect(0, 0, 3, h));
        }
    }
}
