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
/// stroke, the tip's surface - stays in the style; this type only positions. The shape itself
/// (a shape-preserving spline through the points) is <see cref="TokenUsageSpendCurveShape"/>,
/// which is WinUI-free and tested; this type scales it to pixels.
///
/// The crosshair moves compositor properties only (<see cref="UIElement.Translation"/>) and
/// toggles visibility, with no animation at all: pointer tracking is the one interaction here
/// that must add no latency, and a crosshair that eases toward the pointer is one that lags it.
/// It follows the pointer continuously along the curve; the tip, though, always names the hour
/// the pointer is inside and that hour's real figures, because the broker has hourly totals
/// and a number interpolated between them would be an invention.
///
/// The geometry is rebuilt on SizeChanged, so nothing it produces may feed back into layout:
/// the template keeps every shape in a Canvas (see the style) and this type never sets a size
/// on anything the parent measures.
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

    // Registered as object: the value is a managed list of a plain record, which is not a
    // WinRT type, and a property type XAML cannot resolve through the generated metadata
    // fails at assignment rather than at registration - after the smoke test, on the real
    // desktop. The getter narrows it back.
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(object),
            typeof(TokenUsageSpendCurve),
            new PropertyMetadata(null, OnPointsChanged));

    private Path? _area;
    private Path? _line;
    private FrameworkElement? _crosshair;
    private FrameworkElement? _pip;
    private FrameworkElement? _tip;
    private TextBlock? _tipText;
    private TokenUsageSpendCurveShape? _shape;
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
        get => GetValue(PointsProperty) as IReadOnlyList<TokenUsageSpendPoint>;
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
            IReadOnlyList<TokenUsageSpendPoint>? points = curve.Points;
            curve._shape = points is { Count: > 0 }
                ? TokenUsageSpendCurveShape.FromPoints(points)
                : null;
            curve.HideCrosshair();
            curve.Rebuild();
        }
    }

    /// <summary>
    /// Places the spline against the current size. The area closes along the bottom edge and
    /// the line does not; both start at the origin, which is midnight with nothing spent.
    /// </summary>
    private void Rebuild()
    {
        if (_area is null || _line is null)
        {
            return;
        }

        TokenUsageSpendCurveShape? shape = _shape;
        double width = ActualWidth;
        double height = ActualHeight;
        if (shape is null || !shape.HasSpans || width <= 0d || height <= 0d)
        {
            _area.Data = null;
            _line.Data = null;
            return;
        }

        IReadOnlyList<TokenUsageCurveSpan> spans = shape.Spans();
        var lineFigure = new PathFigure
        {
            StartPoint = Place(spans[0].Start, width, height),
            IsClosed = false,
        };
        var areaFigure = new PathFigure
        {
            StartPoint = Place(spans[0].Start, width, height),
            IsClosed = true,
        };
        foreach (TokenUsageCurveSpan span in spans)
        {
            lineFigure.Segments.Add(Bezier(span, width, height));
            areaFigure.Segments.Add(Bezier(span, width, height));
        }

        Point end = Place(spans[^1].End, width, height);
        areaFigure.Segments.Add(new LineSegment { Point = new Point(end.X, height) });

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(lineFigure);
        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(areaFigure);
        _line.Data = lineGeometry;
        _area.Data = areaGeometry;
    }

    private static BezierSegment Bezier(TokenUsageCurveSpan span, double width, double height) =>
        new()
        {
            Point1 = Place(span.Control1, width, height),
            Point2 = Place(span.Control2, width, height),
            Point3 = Place(span.End, width, height),
        };

    private static Point Place(TokenUsageCurveKnot knot, double width, double height) =>
        new(knot.X * width, LevelToY(knot.Y, height));

    private static double LevelToY(double level, double height) =>
        height - Math.Clamp(level, 0d, 1d) * Math.Max(0d, height - TopPadding);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsHitTestVisible)
        {
            return;
        }

        Track(e.GetCurrentPoint(this).Position.X);
    }

    /// <summary>
    /// The crosshair stands where the pointer is and the pip sits on the curve above it,
    /// evaluated from the same spline that was drawn. The tip names the hour the pointer is
    /// inside: each point closes an hour, so that is the first point at or past the pointer.
    /// Past the last point everything stays on "now" rather than following the pointer into
    /// hours that have not happened.
    /// </summary>
    private void Track(double pointerX)
    {
        IReadOnlyList<TokenUsageSpendPoint>? points = Points;
        TokenUsageSpendCurveShape? shape = _shape;
        double width = ActualWidth;
        double height = ActualHeight;
        if (points is null ||
            points.Count == 0 ||
            shape is null ||
            width <= 0d ||
            height <= 0d ||
            _crosshair is null ||
            _pip is null ||
            _tip is null ||
            _tipText is null)
        {
            return;
        }

        TokenUsageSpendPoint hour = points[^1];
        foreach (TokenUsageSpendPoint point in points)
        {
            if (Math.Clamp(point.Fraction, 0d, 1d) * width >= pointerX)
            {
                hour = point;
                break;
            }
        }

        double x = Math.Clamp(pointerX, 0d, Math.Clamp(points[^1].Fraction, 0d, 1d) * width);
        double y = LevelToY(shape.LevelAt(x / width), height);
        // The crosshair lives in a Canvas and gets no stretch, so its height is set here.
        _crosshair.Height = height;
        _crosshair.Translation = new Vector3((float)x, 0f, 0f);
        _pip.Translation = new Vector3(
            (float)(x - PipDiameter / 2d),
            (float)(y - PipDiameter / 2d),
            0f);

        _tipText.Text = hour.TipText;
        // Measured, not read back: the text was just replaced and ActualWidth still describes
        // the previous tip. A tip that would run off the right edge flips to the other side of
        // the crosshair, because the card clips it there and the last few hours of the day are
        // exactly where the pointer ends up.
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
