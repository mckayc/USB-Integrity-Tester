using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using UsbIntegrityTester.App.Logging;

namespace UsbIntegrityTester.App.Controls;

/// <summary>
/// A minimal trend line — no axes, no legend, no plot-area background — drawn directly with WPF
/// primitives so it always matches the app's theme brushes and never carries a library's own
/// default (e.g. LiveCharts' white plot background). Meant for small "how did this number move"
/// cards, not for a chart someone needs to read exact values off of.
/// </summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public IReadOnlyList<double>? Points
    {
        get => (IReadOnlyList<double>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    // A bound ObservableCollection<double> mutates in place (Add/RemoveAt) without the DP value
    // itself ever changing, so AffectsRender alone never fires — this listens for the live-update
    // case (a chart that keeps growing while a test runs) and forces a repaint on every change.
    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= sparkline.OnPointsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += sparkline.OnPointsCollectionChanged;
    }

    private void OnPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>When set, the y-axis is fixed to [0, BaselineMax] instead of auto-scaling to the
    /// data's own min/max. Useful for a claim-vs-measured feel where the same scale should apply
    /// across a run instead of every dip/spike re-normalizing the whole line.</summary>
    public static readonly DependencyProperty BaselineMaxProperty = DependencyProperty.Register(
        nameof(BaselineMax), typeof(double?), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? BaselineMax
    {
        get => (double?)GetValue(BaselineMaxProperty);
        set => SetValue(BaselineMaxProperty, value);
    }

    /// <summary>Hard cap on points actually drawn. A "Full" capacity scan or a long sustained speed
    /// test can produce many thousands of samples — rendering every one adds nothing visible at
    /// sparkline size and risks pathological geometry, so long series are evenly downsampled.</summary>
    private const int MaxRenderedPoints = 400;

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            RenderCore(dc);
        }
        catch (Exception ex)
        {
            // A trend line is a nice-to-have, not load-bearing — if bad data (NaN, a mismatched
            // binding, whatever) makes it this far, skip drawing rather than risk taking the
            // whole app down from inside WPF's render pass, where normal exception handling
            // doesn't reliably catch things.
            AppLog.Error($"Sparkline render failed, skipping: {ex}");
        }
    }

    private void RenderCore(DrawingContext dc)
    {
        var rawPoints = Points;
        var width = ActualWidth;
        var height = ActualHeight;
        if (rawPoints is null || rawPoints.Count < 2 || width <= 0 || height <= 0) return;
        if (double.IsNaN(width) || double.IsInfinity(width) || double.IsNaN(height) || double.IsInfinity(height)) return;

        var points = Sanitize(rawPoints);
        if (points.Count < 2) return;

        var min = BaselineMax.HasValue ? 0 : points.Min();
        var max = BaselineMax ?? points.Max();
        var range = max - min;
        if (double.IsNaN(range) || double.IsInfinity(range) || range < 1e-9) range = 1; // flat/degenerate — avoid divide-by-zero, draw it centered

        var stepX = width / (points.Count - 1);

        Point PointAt(int i)
        {
            var x = i * stepX;
            var normalized = (points[i] - min) / range;
            var y = height - Math.Clamp(normalized, 0, 1) * height;
            return new Point(x, y);
        }

        if (Fill is not null)
        {
            var fillGeometry = new StreamGeometry();
            using (var ctx = fillGeometry.Open())
            {
                ctx.BeginFigure(new Point(0, height), true, true);
                ctx.LineTo(PointAt(0), false, false);
                for (var i = 1; i < points.Count; i++) ctx.LineTo(PointAt(i), true, false);
                ctx.LineTo(new Point(width, height), true, false);
            }
            fillGeometry.Freeze();
            dc.DrawGeometry(Fill, null, fillGeometry);
        }

        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(PointAt(0), false, false);
            for (var i = 1; i < points.Count; i++) ctx.LineTo(PointAt(i), true, false);
        }
        lineGeometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, StrokeThickness), lineGeometry);
    }

    /// <summary>Drops any non-finite sample (NaN/Infinity can otherwise reach WPF's geometry and
    /// native rendering layer, which is unforgiving of them) and evenly downsamples long series.</summary>
    private static IReadOnlyList<double> Sanitize(IReadOnlyList<double> rawPoints)
    {
        var finite = rawPoints.Count <= MaxRenderedPoints && rawPoints.All(double.IsFinite)
            ? rawPoints
            : rawPoints.Where(double.IsFinite).ToList();

        if (finite.Count <= MaxRenderedPoints) return finite;

        var downsampled = new List<double>(MaxRenderedPoints);
        for (var i = 0; i < MaxRenderedPoints; i++)
            downsampled.Add(finite[i * (finite.Count - 1) / (MaxRenderedPoints - 1)]);
        return downsampled;
    }
}
