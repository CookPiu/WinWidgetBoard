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
/// A hardware reading's recent window, drawn behind its value.
///
/// The same division of labour as <see cref="TokenUsageSpendCurve"/>: the shape arrives on
/// 0..1 axes from <see cref="SystemMonitorCurveSeries"/>, which is WinUI-free and tested, and
/// this type only places it against whatever width the card has right now. Brushes and stroke
/// live in the style.
///
/// One control serves both of the card's curves. The headline's template carries the crosshair
/// parts and the row template does not; with no crosshair in the template there is nothing to
/// track, so the pointer handlers cost a row curve nothing. That is the whole difference - a
/// row backdrop and the headline plot the same series the same way.
///
/// The geometry is rebuilt on SizeChanged, so nothing it produces may feed back into layout:
/// the template keeps every shape in a Canvas and this type never sets a size on anything the
/// parent measures. The card that got this wrong took the panel down with a layout cycle.
/// </summary>
public sealed partial class SystemMonitorCurve : Control
{
    /// <summary>
    /// Room left above the highest point so a reading at its ceiling does not draw along the
    /// top edge, where it reads as a border rather than as a measurement.
    /// </summary>
    private const double TopPadding = 3d;

    private const double PipDiameter = 7d;
    private const double TipGap = 6d;

    // Registered as object for the same reason the spend curve's is: the value is a managed
    // list of a plain record, and a property type XAML cannot resolve through the generated
    // metadata fails at assignment rather than at registration.
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(object),
            typeof(SystemMonitorCurve),
            new PropertyMetadata(null, OnPointsChanged));

    private Path? _area;
    private Path? _line;
    private FrameworkElement? _crosshair;
    private FrameworkElement? _pip;
    private FrameworkElement? _tip;
    private TextBlock? _tipText;
    private SystemMonitorCurveSeries? _series;
    private bool _isTracking;

    public SystemMonitorCurve()
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

    public IReadOnlyList<SystemMonitorCurvePoint>? Points
    {
        get => GetValue(PointsProperty) as IReadOnlyList<SystemMonitorCurvePoint>;
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
        if (sender is SystemMonitorCurve curve)
        {
            curve._series = SystemMonitorCurveSeries.FromPoints(curve.Points);
            // The window slides once every tick, so the point under a resting pointer is a
            // different measurement now than it was a moment ago. Hiding is the honest
            // response: the crosshair reappears on the next pointer move, over the value it
            // is actually standing on.
            curve.HideCrosshair();
            curve.Rebuild();
        }
    }

    /// <summary>
    /// Places the polyline against the current size. The line stays open and the area closes
    /// along the baseline, stating both the drop and the return so the fill cannot come out as
    /// a wedge when the first sample sits above zero.
    /// </summary>
    private void Rebuild()
    {
        if (_area is null || _line is null)
        {
            return;
        }

        SystemMonitorCurveSeries? series = _series;
        double width = ActualWidth;
        double height = ActualHeight;
        if (series is null || width <= 0d || height <= 0d)
        {
            _area.Data = null;
            _line.Data = null;
            return;
        }

        IReadOnlyList<SystemMonitorCurvePoint> points = series.Points;
        var placed = new PointCollection();
        for (int index = 1; index < points.Count; index++)
        {
            placed.Add(Place(points[index], width, height));
        }

        Point start = Place(points[0], width, height);
        var lineFigure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
        };
        lineFigure.Segments.Add(new PolyLineSegment { Points = placed });

        var areaPoints = new PointCollection();
        foreach (Point point in placed)
        {
            areaPoints.Add(point);
        }

        areaPoints.Add(new Point(placed[^1].X, height));
        areaPoints.Add(new Point(start.X, height));
        var areaFigure = new PathFigure
        {
            StartPoint = start,
            IsClosed = true,
        };
        areaFigure.Segments.Add(new PolyLineSegment { Points = areaPoints });

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(lineFigure);
        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(areaFigure);
        _line.Data = lineGeometry;
        _area.Data = areaGeometry;
    }

    private static Point Place(SystemMonitorCurvePoint point, double width, double height) =>
        new(Math.Clamp(point.Fraction, 0d, 1d) * width, LevelToY(point.Level, height));

    private static double LevelToY(double level, double height) =>
        height - (Math.Clamp(level, 0d, 1d) * Math.Max(0d, height - TopPadding));

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsHitTestVisible)
        {
            return;
        }

        Track(e.GetCurrentPoint(this).Position.X);
    }

    /// <summary>
    /// The crosshair stands where the pointer is and the pip sits on the line above it,
    /// evaluated from the same series that was drawn. The tip names the sample the pointer is
    /// nearest and how long ago it was measured; a curve whose points carry no label - every
    /// curve but the headline's - shows nothing at all rather than an empty bubble.
    /// </summary>
    private void Track(double pointerX)
    {
        SystemMonitorCurveSeries? series = _series;
        double width = ActualWidth;
        double height = ActualHeight;
        if (series is null ||
            width <= 0d ||
            height <= 0d ||
            _crosshair is null ||
            _pip is null ||
            _tip is null ||
            _tipText is null)
        {
            return;
        }

        double x = Math.Clamp(pointerX, 0d, width);
        double fraction = x / width;
        string tipText = series.Points[series.IndexAt(fraction)].TipText;
        if (tipText.Length == 0)
        {
            HideCrosshair();
            return;
        }

        double y = LevelToY(series.LevelAt(fraction), height);
        // The crosshair lives in a Canvas and gets no stretch, so its height is set here.
        _crosshair.Height = height;
        _crosshair.Translation = new Vector3((float)x, 0f, 0f);
        _pip.Translation = new Vector3(
            (float)(x - (PipDiameter / 2d)),
            (float)(y - (PipDiameter / 2d)),
            0f);

        _tipText.Text = tipText;
        // Measured, not read back: the text was just replaced and ActualWidth still describes
        // the previous tip. A tip that would run off the right edge flips to the other side of
        // the crosshair, and the right edge is where the pointer ends up - it is "now".
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipWidth = _tip.DesiredSize.Width;
        double tipX = x + TipGap + tipWidth > width
            ? Math.Max(0d, x - TipGap - tipWidth)
            : x + TipGap;
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
