using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinWidgetBoard.WorkspacePanel.Runtime;
using Windows.Foundation;
// Implicit usings bring System.IO.Path into scope, so the shape is named explicitly.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// The day's spend curve behind the token card's headline, and the crosshair that follows the
/// pointer across it.
///
/// A control rather than a template of shapes because the geometry is computed: the points
/// arrive on 0..1 axes and have to be placed against whatever width the card has right now,
/// which is a function of the grid, not of the data. Everything visual - the brushes, the
/// stroke, the tip's surface - stays in the style; this type only positions.
///
/// The crosshair moves compositor properties only (<see cref="UIElement.Translation"/>) and
/// toggles visibility, with no animation at all: pointer tracking is the one interaction here
/// that must add no latency, and a crosshair that eases toward the pointer is one that lags it.
/// </summary>
public sealed partial class TokenUsageSpendCurve : Control
{
    /// <summary>
    /// Room left above the curve's highest point, so the line does not run along the top edge
    /// of the control where it would be read as a border.
    /// </summary>
    private const double TopPadding = 4d;

    private const double PipDiameter = 7d;
    private const double TipGap = 6d;

    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(IReadOnlyList<TokenUsageSpendPoint>),
            typeof(TokenUsageSpendCurve),
            new PropertyMetadata(null, OnPointsChanged));

    private Path? _area;
    private Path? _line;
    private FrameworkElement? _crosshair;
    private FrameworkElement? _pip;
    private FrameworkElement? _tip;
    private TextBlock? _tipText;
    private bool _isTracking;

    public TokenUsageSpendCurve()
    {
        SizeChanged += (_, _) => Rebuild();
        PointerEntered += OnPointerMoved;
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => HideCrosshair();
        PointerCanceled += (_, _) => HideCrosshair();
        PointerCaptureLost += (_, _) => HideCrosshair();
        // Edit mode switches hit-testing off underneath a pointer that may be resting on the
        // card; no exit event follows, so the crosshair would stay where it was.
        RegisterPropertyChangedCallback(
            IsHitTestVisibleProperty,
            (_, _) =>
            {
                if (!IsHitTestVisible)
                {
                    HideCrosshair();
                }
            });
    }

    public IReadOnlyList<TokenUsageSpendPoint>? Points
    {
        get => (IReadOnlyList<TokenUsageSpendPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _area = GetTemplateChild("PART_Area") as Path;
        _line = GetTemplateChild("PART_Line") as Path;
        _crosshair = GetTemplateChild("PART_Crosshair") as FrameworkElement;
        _pip = GetTemplateChild("PART_Pip") as FrameworkElement;
        _tip = GetTemplateChild("PART_Tip") as FrameworkElement;
        _tipText = GetTemplateChild("PART_TipText") as TextBlock;
        HideCrosshair();
        Rebuild();
    }

    private static void OnPointsChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is TokenUsageSpendCurve curve)
        {
            curve.HideCrosshair();
            curve.Rebuild();
        }
    }

    /// <summary>
    /// Places every point against the current size. The area closes along the bottom edge and
    /// the line does not; both start at the origin, which is midnight with nothing spent.
    /// </summary>
    private void Rebuild()
    {
        if (_area is null || _line is null)
        {
            return;
        }

        IReadOnlyList<TokenUsageSpendPoint>? points = Points;
        double width = ActualWidth;
        double height = ActualHeight;
        if (points is null || points.Count == 0 || width <= 0d || height <= 0d)
        {
            _area.Data = null;
            _line.Data = null;
            return;
        }

        var origin = new Point(0d, height);
        var lineFigure = new PathFigure { StartPoint = origin, IsClosed = false };
        var areaFigure = new PathFigure { StartPoint = origin, IsClosed = true };
        var linePoints = new PolyLineSegment();
        var areaPoints = new PolyLineSegment();
        double lastX = 0d;
        foreach (TokenUsageSpendPoint point in points)
        {
            Point placed = Place(point, width, height);
            linePoints.Points.Add(placed);
            areaPoints.Points.Add(placed);
            lastX = placed.X;
        }

        areaPoints.Points.Add(new Point(lastX, height));
        lineFigure.Segments.Add(linePoints);
        areaFigure.Segments.Add(areaPoints);

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(lineFigure);
        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(areaFigure);
        _line.Data = lineGeometry;
        _area.Data = areaGeometry;
    }

    private static Point Place(TokenUsageSpendPoint point, double width, double height) =>
        new(
            Math.Clamp(point.Fraction, 0d, 1d) * width,
            height - Math.Clamp(point.Level, 0d, 1d) * Math.Max(0d, height - TopPadding));

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsHitTestVisible)
        {
            return;
        }

        Track(e.GetCurrentPoint(this).Position.X);
    }

    /// <summary>
    /// Snaps to the first point at or past the pointer: each point closes an hour, so the hour
    /// the pointer is inside is the one whose closing point lies to its right. Past the last
    /// point the crosshair stays on "now" rather than following the pointer into nothing.
    /// </summary>
    private void Track(double pointerX)
    {
        IReadOnlyList<TokenUsageSpendPoint>? points = Points;
        double width = ActualWidth;
        double height = ActualHeight;
        if (points is null ||
            points.Count == 0 ||
            width <= 0d ||
            height <= 0d ||
            _crosshair is null ||
            _pip is null ||
            _tip is null ||
            _tipText is null)
        {
            return;
        }

        TokenUsageSpendPoint chosen = points[^1];
        foreach (TokenUsageSpendPoint point in points)
        {
            if (Math.Clamp(point.Fraction, 0d, 1d) * width >= pointerX)
            {
                chosen = point;
                break;
            }
        }

        Point placed = Place(chosen, width, height);
        _crosshair.Translation = new Vector3((float)placed.X, 0f, 0f);
        _pip.Translation = new Vector3(
            (float)(placed.X - PipDiameter / 2d),
            (float)(placed.Y - PipDiameter / 2d),
            0f);

        _tipText.Text = chosen.TipText;
        // Measured, not read back: the text was just replaced and ActualWidth still describes
        // the previous tip. A tip that would run off the right edge flips to the other side of
        // the crosshair, because the card clips it there and the last few hours of the day are
        // exactly where the pointer ends up.
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipWidth = _tip.DesiredSize.Width;
        double tipX = placed.X + TipGap + tipWidth > width
            ? Math.Max(0d, placed.X - TipGap - tipWidth)
            : placed.X + TipGap;
        _tip.Translation = new Vector3((float)tipX, 0f, 0f);

        if (!_isTracking)
        {
            _isTracking = true;
            _crosshair.Visibility = Visibility.Visible;
            _pip.Visibility = Visibility.Visible;
            _tip.Visibility = Visibility.Visible;
        }
    }

    private void HideCrosshair()
    {
        _isTracking = false;
        if (_crosshair is not null)
        {
            _crosshair.Visibility = Visibility.Collapsed;
        }

        if (_pip is not null)
        {
            _pip.Visibility = Visibility.Collapsed;
        }

        if (_tip is not null)
        {
            _tip.Visibility = Visibility.Collapsed;
        }
    }
}
