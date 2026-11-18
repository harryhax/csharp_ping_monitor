using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NetworkDiagnostics.App.Models;

namespace NetworkDiagnostics.App.Controls;

/// <summary>
/// Lightweight latency chart. Successful replies are drawn as a line; failed probes break the line and are
/// shown as translucent red bands (never as 0 ms).
/// </summary>
public sealed class LatencyGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphPoint>?> PointsProperty =
        AvaloniaProperty.Register<LatencyGraph, IReadOnlyList<GraphPoint>?>(nameof(Points));

    /// <summary>Incremented by the view model whenever <see cref="Points"/> changes in place.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<LatencyGraph, int>(nameof(Version));

    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<LatencyGraph, double>(nameof(DurationSeconds), 600);

    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<LatencyGraph, IBrush>(nameof(LineBrush), Brushes.DodgerBlue);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<LatencyGraph, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<LatencyGraph, IBrush?>(nameof(TextBrush));

    private static readonly IBrush FailureBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xD9, 0x30, 0x25));
    private static readonly IBrush FailureTickBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));

    static LatencyGraph()
    {
        AffectsRender<LatencyGraph>(PointsProperty, VersionProperty, DurationSecondsProperty, LineBrushProperty, GridBrushProperty, TextBrushProperty);
    }

    public IReadOnlyList<GraphPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    public double DurationSeconds { get => GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }

    public IBrush LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    /// <summary>Rounds up to 1, 2 or 5 × 10^n so axis labels are readable.</summary>
    public static double NiceCeiling(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
        {
            return 1;
        }

        var exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var f = value / exp;
        var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * exp;
    }

    public override void Render(DrawingContext context)
    {
        const double left = 48, right = 8, top = 8, bottom = 20;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < left + right + 10 || h < top + bottom + 10)
        {
            return;
        }

        var plot = new Rect(left, top, w - left - right, h - top - bottom);
        var gridPen = new Pen(GridBrush ?? Brushes.Gray, 1);
        var textBrush = TextBrush ?? Brushes.Gray;
        var typeface = new Typeface(FontFamily.Default);
        var points = Points ?? [];
        var duration = Math.Max(DurationSeconds, 1);

        var maxLatency = 0.0;
        foreach (var p in points)
        {
            if (p.LatencyMs is { } l && l > maxLatency)
            {
                maxLatency = l;
            }
        }

        // Four grid steps of a "nice" size, so every axis label is a round number.
        var yMax = NiceCeiling(Math.Max(maxLatency * 1.1, 4) / 4) * 4;

        // Horizontal grid + y labels.
        for (var i = 0; i <= 4; i++)
        {
            var v = yMax * i / 4;
            var y = plot.Bottom - (plot.Height * i / 4);
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = new FormattedText(v.ToString(v < 10 ? "0.##" : "0", CultureInfo.CurrentCulture) + " ms", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, textBrush);
            context.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        // X labels.
        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            var t = TimeSpan.FromSeconds(duration * frac);
            var label = new FormattedText(t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, textBrush);
            var x = plot.Left + plot.Width * frac - (frac == 0 ? 0 : frac == 1 ? label.Width : label.Width / 2);
            context.DrawText(label, new Point(x, plot.Bottom + 4));
        }

        if (points.Count == 0)
        {
            var empty = new FormattedText("No measurements yet", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, textBrush);
            context.DrawText(empty, new Point(plot.Center.X - empty.Width / 2, plot.Center.Y - empty.Height / 2));
            return;
        }

        double X(double seconds) => plot.Left + Math.Clamp(seconds / duration, 0, 1) * plot.Width;
        double Y(double ms) => plot.Bottom - Math.Clamp(ms / yMax, 0, 1) * plot.Height;

        // Failure bands first, so the line draws on top.
        var bandWidth = Math.Max(2, plot.Width / Math.Max(points.Count, duration));
        foreach (var p in points)
        {
            if (p.LatencyMs is null)
            {
                var x = X(p.TimeSeconds);
                context.FillRectangle(FailureBrush, new Rect(x - bandWidth / 2, plot.Top, bandWidth, plot.Height));
                context.FillRectangle(FailureTickBrush, new Rect(x - bandWidth / 2, plot.Top, bandWidth, 4));
            }
        }

        var linePen = new Pen(LineBrush, 1.6, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var inFigure = false;
            foreach (var p in points)
            {
                if (p.LatencyMs is not { } ms)
                {
                    if (inFigure)
                    {
                        g.EndFigure(false);
                        inFigure = false;
                    }

                    continue;
                }

                var pt = new Point(X(p.TimeSeconds), Y(ms));
                if (!inFigure)
                {
                    g.BeginFigure(pt, false);
                    inFigure = true;
                }
                else
                {
                    g.LineTo(pt);
                }
            }

            if (inFigure)
            {
                g.EndFigure(false);
            }
        }

        context.DrawGeometry(null, linePen, geometry);

        // Dots make isolated successes (surrounded by failures) visible.
        if (points.Count <= 120)
        {
            foreach (var p in points)
            {
                if (p.LatencyMs is { } ms)
                {
                    context.DrawEllipse(LineBrush, null, new Point(X(p.TimeSeconds), Y(ms)), 2, 2);
                }
            }
        }
    }
}
