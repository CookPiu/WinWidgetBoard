using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace WinWidgetBoard.WorkspacePanel.Motion;

public enum SurfaceMotionAnchor
{
    Center,
    Top,
    TopRight,
}

/// <summary>
/// Applies short compositor-only transitions to progressive-disclosure
/// surfaces. Layout ownership and visibility decisions remain with the view.
/// </summary>
public sealed class SurfaceMotionCoordinator : IDisposable
{
    private static readonly TimeSpan EnterDuration =
        TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration =
        TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan ReducedDuration =
        TimeSpan.FromMilliseconds(150);
    private static readonly Vector3 EnterScale =
        new(0.985f, 0.985f, 1);
    private static readonly Vector3 ExitScale =
        new(0.99f, 0.99f, 1);

    private readonly bool _reducedMotion;
    private readonly bool _highContrast;
    private readonly Dictionary<FrameworkElement, SurfaceState> _states = [];
    private long _nextVersion;
    private int _disposed;

    public SurfaceMotionCoordinator(bool reducedMotion, bool highContrast)
    {
        _reducedMotion = reducedMotion;
        _highContrast = highContrast;
    }

    public bool IsVisible(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_disposed != 0)
        {
            return element.Visibility == Visibility.Visible;
        }

        return GetRequestedVisibility(element);
    }

    public void Show(
        FrameworkElement element,
        SurfaceMotionAnchor anchor,
        bool floating = false)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_disposed != 0)
        {
            return;
        }

        bool requestedVisible = GetRequestedVisibility(element);
        if (requestedVisible)
        {
            return;
        }

        long version = ++_nextVersion;
        _states[element] = new SurfaceState(version, IsVisible: true);
        bool wasCollapsed = element.Visibility != Visibility.Visible;
        element.Visibility = Visibility.Visible;
        element.UpdateLayout();

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        SetTransformOrigin(visual, element, anchor);
        ApplyFloatingDepth(element, floating);
        if (wasCollapsed)
        {
            ResetPresentation(visual);
            visual.Opacity = 0;
            if (!_reducedMotion)
            {
                visual.Scale = EnterScale;
            }
        }

        StartTransition(
            element,
            visual,
            version,
            targetOpacity: 1,
            targetScale: Vector3.One,
            _reducedMotion ? ReducedDuration : EnterDuration,
            collapseWhenComplete: false);
    }

    public void Hide(
        FrameworkElement element,
        SurfaceMotionAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_disposed != 0)
        {
            return;
        }

        bool requestedVisible = GetRequestedVisibility(element);
        if (!requestedVisible)
        {
            return;
        }

        long version = ++_nextVersion;
        _states[element] = new SurfaceState(version, IsVisible: false);
        if (element.Visibility != Visibility.Visible)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        SetTransformOrigin(visual, element, anchor);
        StartTransition(
            element,
            visual,
            version,
            targetOpacity: 0,
            targetScale: _reducedMotion ? Vector3.One : ExitScale,
            _reducedMotion ? ReducedDuration : ExitDuration,
            collapseWhenComplete: true);
    }

    public void HideImmediately(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_disposed != 0)
        {
            return;
        }

        long version = ++_nextVersion;
        _states[element] = new SurfaceState(version, IsVisible: false);
        element.Visibility = Visibility.Collapsed;
        ResetPresentation(
            ElementCompositionPreview.GetElementVisual(element));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _states.Clear();
    }

    private bool GetRequestedVisibility(FrameworkElement element)
    {
        return _states.TryGetValue(element, out SurfaceState state)
            ? state.IsVisible
            : element.Visibility == Visibility.Visible;
    }

    private void StartTransition(
        FrameworkElement element,
        Visual visual,
        long version,
        float targetOpacity,
        Vector3 targetScale,
        TimeSpan duration,
        bool collapseWhenComplete)
    {
        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing =
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.23f, 1),
                new Vector2(0.32f, 1));
        CompositionScopedBatch batch = compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = duration;
        opacityAnimation.InsertKeyFrame(1, targetOpacity, easing);
        visual.StartAnimation(nameof(Visual.Opacity), opacityAnimation);

        if (!_reducedMotion)
        {
            var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.Duration = duration;
            scaleAnimation.InsertKeyFrame(1, targetScale, easing);
            visual.StartAnimation(nameof(Visual.Scale), scaleAnimation);
        }

        batch.Completed += (_, _) =>
        {
            if (_disposed != 0 ||
                !_states.TryGetValue(element, out SurfaceState state) ||
                state.Version != version)
            {
                return;
            }

            if (collapseWhenComplete && !state.IsVisible)
            {
                element.Visibility = Visibility.Collapsed;
            }

            ResetPresentation(visual);
        };
        batch.End();
    }

    private void ApplyFloatingDepth(
        FrameworkElement element,
        bool floating)
    {
        if (!floating || _highContrast)
        {
            return;
        }

        element.Shadow ??= new ThemeShadow();
        element.Translation = new Vector3(0, 0, 8);
    }

    private static void SetTransformOrigin(
        Visual visual,
        FrameworkElement element,
        SurfaceMotionAnchor anchor)
    {
        float width = (float)Math.Max(0, element.ActualWidth);
        float height = (float)Math.Max(0, element.ActualHeight);
        visual.CenterPoint = anchor switch
        {
            SurfaceMotionAnchor.Top => new Vector3(width / 2, 0, 0),
            SurfaceMotionAnchor.TopRight => new Vector3(width, 0, 0),
            _ => new Vector3(width / 2, height / 2, 0),
        };
    }

    private static void ResetPresentation(Visual visual)
    {
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Scale));
    }

    private readonly record struct SurfaceState(
        long Version,
        bool IsVisible);
}
