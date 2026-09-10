using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace WinWidgetBoard.WorkspacePanel.Layout;

/// <summary>
/// One day's high-to-low span, drawn against the scale the whole forecast shares. Reading five
/// rows of two numbers tells you each day in isolation; reading five bars on one scale tells
/// you the week - which day is the warm one, and where the spread narrows.
///
/// A control rather than a three-column grid because the two ends are fractions of a measured
/// width, and a star column inside these card templates is measured against infinity and
/// arranged past the clip (the same trap the forecast block and the settings sheet both hit).
/// The track and the fill are placed in a Canvas on SizeChanged, so nothing here feeds back
/// into layout.
///
/// The bar never carries the reading on its own: both temperatures are printed at its ends, so
/// nothing is said by length or colour alone.
/// </summary>
public sealed partial class WeatherRangeBar : Control
{
    public static readonly DependencyProperty OffsetProperty =
        DependencyProperty.Register(
            nameof(Offset),
            typeof(double),
            typeof(WeatherRangeBar),
            new PropertyMetadata(0d, OnGeometryChanged));

    public static readonly DependencyProperty LengthProperty =
        DependencyProperty.Register(
            nameof(Length),
            typeof(double),
            typeof(WeatherRangeBar),
            new PropertyMetadata(0d, OnGeometryChanged));

    private Rectangle? _fill;

    public WeatherRangeBar()
    {
        SizeChanged += (_, _) => Rebuild();
    }

    /// <summary>Where the day's low sits on the forecast's shared scale, 0 to 1.</summary>
    public double Offset
    {
        get => (double)GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    /// <summary>How much of that scale the day spans, 0 to 1.</summary>
    public double Length
    {
        get => (double)GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _fill = GetTemplateChild("PART_Fill") as Rectangle;
        Rebuild();
    }

    private static void OnGeometryChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is WeatherRangeBar bar)
        {
            bar.Rebuild();
        }
    }

    private void Rebuild()
    {
        if (_fill is null)
        {
            return;
        }

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0d || height <= 0d)
        {
            return;
        }

        double offset = Math.Clamp(Offset, 0d, 1d);
        double length = Math.Clamp(Length, 0d, 1d - offset);
        // A day whose high and low round to the same figure still gets a visible mark: a bar
        // of no width reads as missing data rather than as a day that did not move.
        double fillWidth = Math.Max(height, length * width);
        _fill.Width = Math.Min(fillWidth, width);
        _fill.Height = height;
        Canvas.SetLeft(_fill, Math.Min(offset * width, width - _fill.Width));
        Canvas.SetTop(_fill, 0d);
    }
}
