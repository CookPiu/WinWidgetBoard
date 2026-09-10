using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinWidgetBoard.WorkspacePanel.Runtime;
using Windows.Foundation;
// Implicit usings bring System.IO.Path into scope, so the shape is named explicitly.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// The hourly temperature trend and the readout that follows the pointer across it.
///
/// Built on the same footing as the token card's spend curve: a control rather than a template
/// of shapes, because the geometry is a function of whatever width the grid gave the card
/// right now; every brush and stroke stays in the style; the spline itself is WinUI-free and
/// tested (<see cref="WeatherHourlyCurveSeries"/>), and this type only scales it to pixels.
/// The geometry is rebuilt on SizeChanged, so nothing it produces may feed back into layout -
/// the template keeps every shape in a Canvas and this type never sets a size on anything the
/// parent measures. A stroked Path that the parent measures grows the card by half a pixel a
/// pass and ends as a LayoutCycleException.
///
/// Two things differ from the spend curve, and both are about what the reading means. The
/// vertical axis spans the window's own high and low rather than starting at zero, because
/// zero degrees is not a floor. And the readout names four facts - the hour, its temperature,
/// its condition and, when the forecast has one, its chance of rain - so it is a small stack
/// rather than one line of text.
///
/// The crosshair itself never eases horizontally: pointer tracking must add no latency, and a
/// line that lags the cursor reads as a stutter rather than as smoothness. What is smoothed is
/// the appearance - the whole readout fades in and out on the compositor's opacity, and the
/// tip slides to its new side of the crosshair instead of jumping - because those are not
/// under the pointer and popping is what made it feel abrupt.
/// </summary>
public sealed partial class WeatherHourlyCurve : Control
{
    /// <summary>
    /// Room above the highest point so the line does not run along the top edge, where it
    /// would be read as a border, and below it for the precipitation band.
    /// </summary>
    private const double TopPadding = 6d;

    private const double BandHeight = 10d;
    private const double BandGap = 4d;
    private const double PipDiameter = 7d;
    private const double TipGap = 8d;
    private const double BarWidth = 5d;

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(90);

    // Registered as object for the same reason the spend curve is: the value is a managed
    // list of a plain record, which is not a WinRT type, and a property type XAML cannot
    // resolve through the generated metadata fails at assignment rather than at registration -
    // after the smoke test, on the real desktop. The getter narrows it back.
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(object),
            typeof(WeatherHourlyCurve),
            new PropertyMetadata(null, OnPointsChanged));

    /// <summary>
    /// The card's own condition-glyph selector, handed in rather than looked up. The glyph
    /// templates are page resources of the card, and a control template living in the shared
    /// style dictionary cannot reach a page's resources - resolving them here would fail at
    /// parse time on the real desktop rather than in the smoke test.
    /// </summary>
    public static readonly DependencyProperty GlyphTemplateSelectorProperty =
        DependencyProperty.Register(
            nameof(GlyphTemplateSelector),
            typeof(DataTemplateSelector),
            typeof(WeatherHourlyCurve),
            new PropertyMetadata(null, OnGlyphSelectorChanged));

    private Path? _area;
    private Path? _line;
    private Canvas? _band;
    private FrameworkElement? _readout;
    private FrameworkElement? _crosshair;
    private FrameworkElement? _pip;
    private FrameworkElement? _tip;
    private TextBlock? _tipTime;
    private TextBlock? _tipTemperature;
    private TextBlock? _tipCondition;
    private FrameworkElement? _tipPrecipitationRow;
    private TextBlock? _tipPrecipitation;
    private ContentControl? _tipGlyph;
    private WeatherHourlyCurveSeries? _series;
    private bool _isTracking;

    public WeatherHourlyCurve()
    {
        SizeChanged += (_, _) => Rebuild();
        // Registered with handledEventsToo rather than with +=. The card sits inside the
        // panel's scroll viewer and under a card surface that watches the pointer for the
        // move drag; a plain handler is skipped for anything either of them has already
        // marked handled, and the readout then never appears even though the control is
        // visible, sized and hit-testable.
        AddHandler(PointerEnteredEvent, new PointerEventHandler(OnPointerMoved), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        AddHandler(PointerExitedEvent, new PointerEventHandler(OnPointerAway), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPointerAway), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPointerAway), true);
        // Edit mode switches hit-testing off underneath a pointer that may be resting on the
        // card; no exit event follows, so the readout would stay where it was.
        RegisterPropertyChangedCallback(
            IsHitTestVisibleProperty,
            (_, _) =>
            {
                if (!IsHitTestVisible)
                {
                    HideReadout();
                }
            });
    }

    public IReadOnlyList<WeatherCurvePoint>? Points
    {
        get => GetValue(PointsProperty) as IReadOnlyList<WeatherCurvePoint>;
        set => SetValue(PointsProperty, value);
    }

    public DataTemplateSelector? GlyphTemplateSelector
    {
        get => GetValue(GlyphTemplateSelectorProperty) as DataTemplateSelector;
        set => SetValue(GlyphTemplateSelectorProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _area = GetTemplateChild("PART_Area") as Path;
        _line = GetTemplateChild("PART_Line") as Path;
        _band = GetTemplateChild("PART_Band") as Canvas;
        _readout = GetTemplateChild("PART_Readout") as FrameworkElement;
        _crosshair = GetTemplateChild("PART_Crosshair") as FrameworkElement;
        _pip = GetTemplateChild("PART_Pip") as FrameworkElement;
        _tip = GetTemplateChild("PART_Tip") as FrameworkElement;
        _tipTime = GetTemplateChild("PART_TipTime") as TextBlock;
        _tipTemperature = GetTemplateChild("PART_TipTemperature") as TextBlock;
        _tipCondition = GetTemplateChild("PART_TipCondition") as TextBlock;
        _tipPrecipitationRow = GetTemplateChild("PART_TipPrecipitationRow") as FrameworkElement;
        _tipPrecipitation = GetTemplateChild("PART_TipPrecipitation") as TextBlock;
        _tipGlyph = GetTemplateChild("PART_TipGlyph") as ContentControl;
        ApplyGlyphSelector();
        HideReadout(animate: false);
        Rebuild();
    }

    /// <summary>
    /// Assigned from code rather than template-bound. The style's ControlTemplate targets
    /// Control, and a TemplateBinding resolves its property against that target type - a
    /// custom property of this class is not on it, and the binding fails when the template is
    /// applied rather than when it is parsed, which is to say on the real desktop and not in
    /// the smoke test.
    /// </summary>
    private void ApplyGlyphSelector()
    {
        if (_tipGlyph is not null)
        {
            _tipGlyph.ContentTemplateSelector = GlyphTemplateSelector;
        }
    }

    private static void OnGlyphSelectorChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is WeatherHourlyCurve curve)
        {
            curve.ApplyGlyphSelector();
        }
    }

    private static void OnPointsChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is WeatherHourlyCurve curve)
        {
            IReadOnlyList<WeatherCurvePoint>? points = curve.Points;
            curve._series = points is { Count: > 0 }
                ? WeatherHourlyCurveSeries.FromPoints(points)
                : null;
            curve.HideReadout(animate: false);
            curve.Rebuild();
        }
    }

    /// <summary>The height the curve itself gets, once the band has taken its share.</summary>
    private double CurveHeight(double height) =>
        _series?.HasPrecipitation == true
            ? Math.Max(0d, height - BandHeight - BandGap)
            : height;

    /// <summary>
    /// Places the spline against the current size. The area closes along the bottom of the
    /// curve's own band, not the control's, so the fill fades out above the precipitation bars
    /// rather than swallowing them.
    /// </summary>
    private void Rebuild()
    {
        if (_area is null || _line is null)
        {
            return;
        }

        WeatherHourlyCurveSeries? series = _series;
        double width = ActualWidth;
        double height = ActualHeight;
        if (series is null || !series.HasCurve || width <= 0d || height <= 0d)
        {
            _area.Data = null;
            _line.Data = null;
            _band?.Children.Clear();
            return;
        }

        double curveHeight = CurveHeight(height);
        IReadOnlyList<CurveSpan> spans = series.Shape.Spans();
        var lineFigure = new PathFigure
        {
            StartPoint = Place(spans[0].Start, width, curveHeight),
            IsClosed = false,
        };
        var areaFigure = new PathFigure
        {
            StartPoint = Place(spans[0].Start, width, curveHeight),
            IsClosed = true,
        };
        foreach (CurveSpan span in spans)
        {
            lineFigure.Segments.Add(Bezier(span, width, curveHeight));
            areaFigure.Segments.Add(Bezier(span, width, curveHeight));
        }

        // Down to the baseline, then back along it. Without that second segment the closing
        // edge runs straight from the last point's foot to the first point itself, and since
        // the first point sits at whatever temperature the hour happened to be, the fill
        // becomes a wedge with a diagonal top - it reads as a shadow cast the wrong way.
        // The spend curve gets away with one segment only because its first point is the
        // origin, where that diagonal is already the bottom edge.
        Point start = Place(spans[0].Start, width, curveHeight);
        Point end = Place(spans[^1].End, width, curveHeight);
        areaFigure.Segments.Add(new LineSegment { Point = new Point(end.X, curveHeight) });
        areaFigure.Segments.Add(new LineSegment { Point = new Point(start.X, curveHeight) });

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(lineFigure);
        var areaGeometry = new PathGeometry();
        areaGeometry.Figures.Add(areaFigure);
        _line.Data = lineGeometry;
        _area.Data = areaGeometry;
        RebuildBand(series, width, height);
    }

    /// <summary>
    /// One bar per hour that has a chance of precipitation, along the bottom. Height carries
    /// the figure and so does the readout, so nothing here is said by colour alone. Hours the
    /// model gave no probability for get no bar - an absent forecast is not a dry hour.
    /// </summary>
    private void RebuildBand(WeatherHourlyCurveSeries series, double width, double height)
    {
        if (_band is null)
        {
            return;
        }

        _band.Children.Clear();
        if (!series.HasPrecipitation)
        {
            return;
        }

        double top = height - BandHeight;
        for (int index = 0; index < series.Points.Count; index++)
        {
            if (series.Points[index].PrecipitationProbabilityPercent is not { } percent ||
                percent <= 0)
            {
                continue;
            }

            double barHeight = Math.Max(1.5d, BandHeight * (percent / 100d));
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = barHeight,
                RadiusX = 1.5d,
                RadiusY = 1.5d,
                Fill = Foreground,
            };
            Canvas.SetLeft(bar, (series.FractionAt(index) * width) - (BarWidth / 2d));
            Canvas.SetTop(bar, top + (BandHeight - barHeight));
            _band.Children.Add(bar);
        }
    }

    private static BezierSegment Bezier(CurveSpan span, double width, double height) =>
        new()
        {
            Point1 = Place(span.Control1, width, height),
            Point2 = Place(span.Control2, width, height),
            Point3 = Place(span.End, width, height),
        };

    private static Point Place(CurveKnot knot, double width, double height) =>
        new(knot.X * width, LevelToY(knot.Y, height));

    private static double LevelToY(double level, double height) =>
        height - (Math.Clamp(level, 0d, 1d) * Math.Max(0d, height - TopPadding));

    private void OnPointerAway(object sender, PointerRoutedEventArgs e) => HideReadout();

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
    /// evaluated from the same spline that was drawn. The readout names the nearest hour and
    /// that hour's real figures: the broker sends hourly readings, and a temperature
    /// interpolated between two of them would be a number nobody forecast.
    /// </summary>
    private void Track(double pointerX)
    {
        WeatherHourlyCurveSeries? series = _series;
        double width = ActualWidth;
        double height = ActualHeight;
        if (series is null ||
            !series.HasCurve ||
            width <= 0d ||
            height <= 0d ||
            _crosshair is null ||
            _pip is null ||
            _tip is null)
        {
            return;
        }

        double x = Math.Clamp(pointerX, 0d, width);
        double curveHeight = CurveHeight(height);
        double y = LevelToY(series.Shape.LevelAt(x / width), curveHeight);
        WeatherCurvePoint? hour = series.PointAt(x / width);
        if (hour is null)
        {
            return;
        }

        // The crosshair lives in a Canvas and gets no stretch, so its height is set here.
        _crosshair.Height = height;
        _crosshair.Translation = new Vector3((float)x, 0f, 0f);
        _pip.Translation = new Vector3(
            (float)(x - (PipDiameter / 2d)),
            (float)(y - (PipDiameter / 2d)),
            0f);

        ApplyTipContent(hour);
        // Measured, not read back: the content was just replaced and ActualWidth still
        // describes the previous tip. A tip that would run off the right edge flips to the
        // other side of the crosshair, because the card clips it there and the last hours of
        // the window are exactly where the pointer ends up.
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipWidth = _tip.DesiredSize.Width;
        double tipX = x + TipGap + tipWidth > width
            ? Math.Max(0d, x - TipGap - tipWidth)
            : x + TipGap;
        MoveTip(tipX);
        ShowReadout();
    }

    private void ApplyTipContent(WeatherCurvePoint hour)
    {
        if (_tipTime is not null)
        {
            _tipTime.Text = hour.TimeText;
        }

        if (_tipTemperature is not null)
        {
            _tipTemperature.Text = hour.TemperatureText;
        }

        if (_tipCondition is not null)
        {
            _tipCondition.Text = hour.ConditionText;
        }

        if (_tipGlyph is not null)
        {
            _tipGlyph.Content = hour.ConditionIconId;
        }

        // The row is present only when the forecast carried a figure for this hour. A "0%"
        // where the model said nothing would be a promise the broker never made.
        if (_tipPrecipitationRow is not null)
        {
            bool hasPrecipitation = hour.PrecipitationProbabilityPercent is > 0;
            _tipPrecipitationRow.Visibility = hasPrecipitation
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (hasPrecipitation && _tipPrecipitation is not null)
            {
                _tipPrecipitation.Text = FormatPercent(
                    hour.PrecipitationProbabilityPercent!.Value);
            }
        }
    }

    private static string FormatPercent(int percent) =>
        percent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";

    /// <summary>
    /// Places the tip beside the crosshair, in the same frame and by the same kind of write.
    ///
    /// It used to ease across on a composition animation, which looked better in isolation and
    /// was wrong: the crosshair is a direct property write that lands immediately, so any
    /// animation on the tip makes the two disagree while it runs - and if the animation fails
    /// to start, the tip simply stays where it last was while the crosshair walks away from
    /// it. A readout whose label can point at a different hour than its own line is worse than
    /// one that does not glide. The smoothing that matters here is the fade, which is about
    /// appearing rather than about tracking.
    /// </summary>
    private void MoveTip(double tipX)
    {
        if (_tip is null)
        {
            return;
        }

        _tip.Translation = new Vector3((float)tipX, 0f, 0f);
    }

    /// <summary>
    /// Fades the readout up. Opacity only, so it stays on the compositor, and it is started
    /// from whatever the current value is so a pointer that re-enters mid-fade continues from
    /// where the fade had reached instead of restarting at nothing.
    /// </summary>
    private void ShowReadout()
    {
        if (_readout is null || _isTracking)
        {
            return;
        }

        _isTracking = true;
        if (ReducedMotion)
        {
            ElementCompositionPreview.GetElementVisual(_readout).StopAnimation("Opacity");
            _readout.Opacity = 1d;
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(_readout);
        Compositor compositor = visual.Compositor;
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0f, "this.CurrentValue");
        animation.InsertKeyFrame(1f, 1f);
        animation.Duration = FadeIn;
        visual.StartAnimation("Opacity", animation);
    }

    private void HideReadout() => HideReadout(animate: true);

    /// <summary>
    /// Fades the readout out. Opacity only - the element stays visible and arranged, because
    /// its tip has to remain measurable: the next hover decides which side of the crosshair
    /// the tip goes on from its width, and a collapsed element measures as nothing.
    /// </summary>
    private void HideReadout(bool animate)
    {
        _isTracking = false;
        if (_readout is null)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(_readout);
        if (!animate || ReducedMotion)
        {
            visual.StopAnimation("Opacity");
            _readout.Opacity = 0d;
            return;
        }

        Compositor compositor = visual.Compositor;
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0f, "this.CurrentValue");
        animation.InsertKeyFrame(1f, 0f);
        animation.Duration = FadeOut;
        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>
    /// Read per use rather than cached: the setting can change while the panel is resident,
    /// and this control lives as long as the card does.
    /// </summary>
    private static bool ReducedMotion =>
        !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
}
