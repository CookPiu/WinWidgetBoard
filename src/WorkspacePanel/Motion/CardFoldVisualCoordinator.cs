using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WinWidgetBoard.WorkspacePanel.Motion;

/// <summary>
/// Mirrors realized cards into a root-level composition overlay so folds can
/// originate outside the ScrollViewer without reparenting ItemsRepeater items.
/// </summary>
public sealed class CardFoldVisualCoordinator : IDisposable
{
    // Composition projects orthographically by default: a fold rotation about an
    // in-plane axis and an Offset.Z both render with no foreshortening until an
    // ancestor carries a perspective matrix. One camera for the whole overlay puts
    // every card on a single vanishing point instead of giving each mirror its own.
    private const float MinimumPerspectiveDistance = 640;
    private const float PerspectiveDistanceFactor = 1.15f;

    private readonly FrameworkElement _overlayElement;
    private readonly bool _reducedMotion;
    private readonly bool _highContrast;
    private readonly ContainerVisual _overlayRoot;
    private readonly Color _themeBackground;
    private readonly Color _creaseShade;
    private readonly Color _specularShade;
    private readonly Dictionary<string, FrameworkElement> _registered =
        new(StringComparer.Ordinal);
    private readonly Dictionary<UIElement, string> _elementIds = [];
    private readonly Dictionary<string, SessionEntry> _session =
        new(StringComparer.Ordinal);
    private bool _prepared;
    private int _disposed;

    public CardFoldVisualCoordinator(
        FrameworkElement overlayElement,
        bool reducedMotion,
        bool highContrast)
    {
        _overlayElement = overlayElement ??
            throw new ArgumentNullException(nameof(overlayElement));
        _reducedMotion = reducedMotion;
        _highContrast = highContrast;

        // The crease and the specular are the darker and the lighter end of the
        // current theme rather than fixed RGB: a hard-coded near-white seam reads
        // as a defect on a dark card, and the reverse on a light one.
        var settings = new UISettings();
        _themeBackground = Opaque(settings.GetColorValue(UIColorType.Background));
        Color foreground = Opaque(settings.GetColorValue(UIColorType.Foreground));
        bool backgroundIsDarker =
            Luminance(_themeBackground) <= Luminance(foreground);
        _creaseShade = backgroundIsDarker ? _themeBackground : foreground;
        _specularShade = backgroundIsDarker ? foreground : _themeBackground;

        Visual overlayVisual =
            ElementCompositionPreview.GetElementVisual(overlayElement);
        _overlayRoot = overlayVisual.Compositor.CreateContainerVisual();
        _overlayRoot.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(
            overlayElement,
            _overlayRoot);
    }

    public bool IsPrepared => _prepared;
    public int RegisteredCount => _registered.Count;

    public void Register(string instanceId, UIElement element)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(element);
        if (element is not FrameworkElement frameworkElement)
        {
            return;
        }

        if (_registered.TryGetValue(instanceId, out FrameworkElement? old) &&
            !ReferenceEquals(old, frameworkElement))
        {
            _elementIds.Remove(old);
        }
        _registered[instanceId] = frameworkElement;
        _elementIds[frameworkElement] = instanceId;
    }

    public void Unregister(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_elementIds.Remove(element, out string? id))
        {
            _registered.Remove(id);
        }
    }

    public IReadOnlyList<string> Prepare(
        FrameworkElement coordinateRoot,
        MotionPoint launcherAnchor,
        IReadOnlyList<string> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(coordinateRoot);
        ArgumentNullException.ThrowIfNull(orderedIds);
        if (_prepared)
        {
            // Preserve the caller's order. Returning _session.Keys handed the fold
            // controller an arbitrary dictionary order, which scrambled the
            // per-card stagger whenever a toggle re-prepared a live session.
            return orderedIds
                .Where(_session.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var available = orderedIds
            .Where(id => _registered.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (_reducedMotion || available.Length == 0)
        {
            return available;
        }

        ElementCompositionPreview.SetElementChildVisual(
            _overlayElement,
            _overlayRoot);
        _overlayElement.UpdateLayout();
        ApplyPerspective(
            (float)_overlayElement.ActualWidth,
            (float)_overlayElement.ActualHeight);
        Compositor compositor = _overlayRoot.Compositor;
        for (int index = 0; index < available.Length; index++)
        {
            string id = available[index];
            FrameworkElement element = _registered[id];
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                continue;
            }

            Rect rect = element.TransformToVisual(coordinateRoot).TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var bounds = new MotionRect(rect.X, rect.Y, rect.Width, rect.Height);
            if (!bounds.IsValid)
            {
                continue;
            }

            Visual source = ElementCompositionPreview.GetElementVisual(element);
            float cornerRadius = ResolveCornerRadius(element);
            var blocker = compositor.CreateSpriteVisual();
            blocker.Offset = new Vector3((float)rect.X, (float)rect.Y, 0);
            blocker.Size = new Vector2((float)rect.Width, (float)rect.Height);
            blocker.Brush = compositor.CreateColorBrush(
                ResolveCardColor(element, _themeBackground));
            // The blocker masks the stable ItemsRepeater card for the entire
            // redirect session. It is static, so set it once rather than every
            // frame, and it has to be fully opaque: at 0.96 the real card's
            // content bled through behind the travelling mirror.
            blocker.Opacity = 1;
            blocker.Clip = CreateRoundedClip(
                compositor,
                (float)rect.Width,
                (float)rect.Height,
                cornerRadius);

            var motionRoot = compositor.CreateContainerVisual();
            motionRoot.Offset = blocker.Offset;
            motionRoot.Size = blocker.Size;
            motionRoot.Clip = CreateRoundedClip(
                compositor,
                (float)rect.Width,
                (float)rect.Height,
                cornerRadius);

            RedirectVisual redirect = compositor.CreateRedirectVisual(source);
            redirect.Size = motionRoot.Size;
            motionRoot.Children.InsertAtBottom(redirect);

            SpriteVisual? crease = null;
            SpriteVisual? specular = null;
            if (!_highContrast)
            {
                crease = CreateStripe(
                    compositor,
                    _creaseShade,
                    16,
                    (float)rect.Height * 1.35f);
                specular = CreateStripe(
                    compositor,
                    _specularShade,
                    30,
                    (float)rect.Height * 1.35f);
                motionRoot.Children.InsertAtTop(crease);
                motionRoot.Children.InsertAtTop(specular);
            }

            _overlayRoot.Children.InsertAtTop(blocker);
            _overlayRoot.Children.InsertAtTop(motionRoot);
            CardFoldPath path = CardFoldPathResolver.Resolve(
                launcherAnchor,
                bounds,
                index,
                available.Length);
            motionRoot.CenterPoint = new Vector3(
                (float)path.PivotX,
                (float)path.PivotY,
                0);
            _session[id] = new SessionEntry(
                path,
                blocker,
                motionRoot,
                redirect,
                crease,
                specular);
        }

        _prepared = _session.Count > 0;
        return available.Where(_session.ContainsKey).ToArray();
    }

    public void Apply(IReadOnlyList<CardFoldMotionValue> values)
    {
        if (_reducedMotion || !_prepared)
        {
            return;
        }

        foreach (CardFoldMotionValue value in values)
        {
            if (!_session.TryGetValue(value.InstanceId, out SessionEntry? entry))
            {
                continue;
            }

            CardFoldPresentation p = entry.Path.Evaluate(value.Progress);
            entry.MotionRoot.Offset = new Vector3(
                entry.Blocker.Offset.X + (float)p.OffsetX,
                entry.Blocker.Offset.Y + (float)p.OffsetY,
                (float)p.OffsetZ);
            entry.MotionRoot.Scale = new Vector3(
                (float)p.Scale,
                (float)p.Scale,
                1);
            var radialAxis = Vector3.Normalize(new Vector3(
                (float)p.AxisX,
                (float)p.AxisY,
                0));
            Quaternion fold = Quaternion.CreateFromAxisAngle(
                radialAxis,
                DegreesToRadians(p.FoldAngle));
            Quaternion twist = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                DegreesToRadians(p.RotationZ));
            entry.MotionRoot.Orientation = Quaternion.Normalize(twist * fold);
            entry.MotionRoot.Opacity = (float)p.Opacity;
            ApplyStripe(entry.Crease, p, 0.14f, -10);
            ApplyStripe(entry.Specular, p, 0.24f, 12);
        }
    }

    public void Complete() => ClearSession();

    public void Clear()
    {
        ClearSession();
        _registered.Clear();
        _elementIds.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Clear();
        ElementCompositionPreview.SetElementChildVisual(_overlayElement, null);
        _overlayRoot.Dispose();
    }

    private void ClearSession()
    {
        _overlayRoot.Children.RemoveAll();
        _overlayRoot.TransformMatrix = Matrix4x4.Identity;
        foreach (SessionEntry entry in _session.Values)
        {
            entry.Redirect.Source = null;
            entry.Blocker.Dispose();
            entry.MotionRoot.Dispose();
        }
        _session.Clear();
        _prepared = false;
    }

    private void ApplyPerspective(float width, float height)
    {
        if (width <= 0 || height <= 0)
        {
            _overlayRoot.TransformMatrix = Matrix4x4.Identity;
            return;
        }

        float distance = Math.Max(
            MinimumPerspectiveDistance,
            Math.Max(width, height) * PerspectiveDistanceFactor);
        Matrix4x4 perspective = Matrix4x4.Identity;
        perspective.M34 = -1 / distance;

        // Project about the overlay centre. Anything at Z = 0 -- the blockers and
        // every settled mirror -- passes through this matrix unchanged, so the
        // camera cannot shift a card that has already landed.
        _overlayRoot.TransformMatrix =
            Matrix4x4.CreateTranslation(-width / 2, -height / 2, 0) *
            perspective *
            Matrix4x4.CreateTranslation(width / 2, height / 2, 0);
    }

    private static SpriteVisual CreateStripe(
        Compositor compositor,
        Color color,
        float width,
        float height)
    {
        SpriteVisual stripe = compositor.CreateSpriteVisual();
        stripe.Size = new Vector2(width, height);
        stripe.Brush = compositor.CreateColorBrush(color);
        stripe.RotationAngleInDegrees = -12;
        stripe.Opacity = 0;
        return stripe;
    }

    private static void ApplyStripe(
        SpriteVisual? stripe,
        CardFoldPresentation p,
        float maximumOpacity,
        float phaseOffset)
    {
        if (stripe is null)
        {
            return;
        }
        stripe.Offset = new Vector3(
            phaseOffset + (float)((p.Scale - CardFoldPath.ClosedScale) /
                (1 - CardFoldPath.ClosedScale)) *
                Math.Max(1, stripe.Parent.Size.X),
            -stripe.Size.Y * 0.16f,
            2);
        stripe.Opacity = (float)p.FoldEnergy * maximumOpacity;
    }

    private static RectangleClip CreateRoundedClip(
        Compositor compositor,
        float width,
        float height,
        float cornerRadius)
    {
        var radius = new Vector2(cornerRadius);
        // Right and bottom are absolute clip edges, not insets. Passing zero
        // for either edge clips the entire mirror to a zero-area rectangle.
        return compositor.CreateRectangleClip(
            0,
            0,
            width,
            height,
            radius,
            radius,
            radius,
            radius);
    }

    private static Color ResolveCardColor(
        FrameworkElement element,
        Color themeBackground)
    {
        if (element is Border border &&
            border.Background is SolidColorBrush brush)
        {
            // Card surfaces are translucent over the panel material. Compositing
            // the card's own colour onto the theme background reproduces what is
            // on screen without inventing a second palette.
            return Composite(brush.Color, themeBackground);
        }
        return themeBackground;
    }

    private static float ResolveCornerRadius(FrameworkElement element)
    {
        if (element is Border border)
        {
            return (float)border.CornerRadius.TopLeft;
        }

        if (Application.Current?.Resources is { } resources &&
            resources.ContainsKey("WwbCardCornerRadius") &&
            resources["WwbCardCornerRadius"] is CornerRadius radius)
        {
            return (float)radius.TopLeft;
        }

        return 0;
    }

    private static Color Opaque(Color color) =>
        Color.FromArgb(255, color.R, color.G, color.B);

    private static double Luminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;

    private static Color Composite(Color source, Color over)
    {
        double alpha = source.A / 255.0;
        return Color.FromArgb(
            255,
            (byte)Math.Round(source.R * alpha + over.R * (1 - alpha)),
            (byte)Math.Round(source.G * alpha + over.G * (1 - alpha)),
            (byte)Math.Round(source.B * alpha + over.B * (1 - alpha)));
    }

    private static float DegreesToRadians(double degrees) =>
        (float)(degrees * Math.PI / 180);

    private sealed record SessionEntry(
        CardFoldPath Path,
        SpriteVisual Blocker,
        ContainerVisual MotionRoot,
        RedirectVisual Redirect,
        SpriteVisual? Crease,
        SpriteVisual? Specular);
}
