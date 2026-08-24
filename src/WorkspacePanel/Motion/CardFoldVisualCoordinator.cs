using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace WinWidgetBoard.WorkspacePanel.Motion;

/// <summary>
/// Mirrors realized cards into a root-level composition overlay so folds can
/// originate outside the ScrollViewer without reparenting ItemsRepeater items.
/// </summary>
public sealed class CardFoldVisualCoordinator : IDisposable
{
    private const float CornerRadius = 12;
    private readonly FrameworkElement _overlayElement;
    private readonly bool _reducedMotion;
    private readonly bool _highContrast;
    private readonly ContainerVisual _overlayRoot;
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
        Visual overlayVisual =
            ElementCompositionPreview.GetElementVisual(overlayElement);
        _overlayRoot = overlayVisual.Compositor.CreateContainerVisual();
        _overlayRoot.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(
            overlayElement,
            _overlayRoot);
    }

    public bool IsPrepared => _prepared;

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
        if (_prepared)
        {
            return _session.Keys.ToArray();
        }

        ArgumentNullException.ThrowIfNull(coordinateRoot);
        ArgumentNullException.ThrowIfNull(orderedIds);
        var available = orderedIds
            .Where(id => _registered.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (_reducedMotion || available.Length == 0)
        {
            return available;
        }

        _overlayElement.UpdateLayout();
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
            var blocker = compositor.CreateSpriteVisual();
            blocker.Offset = new Vector3((float)rect.X, (float)rect.Y, 0);
            blocker.Size = new Vector2((float)rect.Width, (float)rect.Height);
            blocker.Brush = compositor.CreateColorBrush(ResolveCardColor(element));
            blocker.Clip = CreateRoundedClip(
                compositor,
                (float)rect.Width,
                (float)rect.Height);

            var motionRoot = compositor.CreateContainerVisual();
            motionRoot.Offset = blocker.Offset;
            motionRoot.Size = blocker.Size;
            motionRoot.CenterPoint = new Vector3(0, (float)rect.Height, 0);
            motionRoot.Clip = CreateRoundedClip(
                compositor,
                (float)rect.Width,
                (float)rect.Height);

            RedirectVisual redirect = compositor.CreateRedirectVisual(source);
            redirect.Size = motionRoot.Size;
            motionRoot.Children.InsertAtBottom(redirect);

            SpriteVisual? crease = null;
            SpriteVisual? specular = null;
            if (!_highContrast)
            {
                crease = CreateStripe(
                    compositor,
                    Color.FromArgb(255, 38, 73, 108),
                    16,
                    (float)rect.Height * 1.35f);
                specular = CreateStripe(
                    compositor,
                    Color.FromArgb(255, 255, 255, 255),
                    30,
                    (float)rect.Height * 1.35f);
                motionRoot.Children.InsertAtTop(crease);
                motionRoot.Children.InsertAtTop(specular);
            }

            _overlayRoot.Children.InsertAtTop(blocker);
            _overlayRoot.Children.InsertAtTop(motionRoot);
            _session[id] = new SessionEntry(
                CardFoldPathResolver.Resolve(
                    launcherAnchor,
                    bounds,
                    index,
                    available.Length),
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
            entry.MotionRoot.Orientation = Quaternion.CreateFromYawPitchRoll(
                DegreesToRadians(p.RotationY),
                DegreesToRadians(p.RotationX),
                DegreesToRadians(p.RotationZ));
            entry.MotionRoot.Opacity = (float)p.Opacity;
            // Hide the real card for the whole redirect session; both slot and mirror
            // disappear atomically at settle so the handoff cannot flash.
            entry.Blocker.Opacity = 0.96f;
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
        foreach (SessionEntry entry in _session.Values)
        {
            entry.Redirect.Source = null;
            entry.Blocker.Dispose();
            entry.MotionRoot.Dispose();
        }
        _session.Clear();
        _prepared = false;
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
            phaseOffset + (float)((p.Scale - 0.27) / 0.73) *
                Math.Max(1, stripe.Parent.Size.X),
            -stripe.Size.Y * 0.16f,
            2);
        stripe.Opacity = (float)p.FoldEnergy * maximumOpacity;
    }

    private static RectangleClip CreateRoundedClip(
        Compositor compositor,
        float width,
        float height)
    {
        var radius = new Vector2(CornerRadius);
        return compositor.CreateRectangleClip(
            0,
            0,
            0,
            0,
            radius,
            radius,
            radius,
            radius);
    }

    private static Color ResolveCardColor(FrameworkElement element)
    {
        if (element is Border border &&
            border.Background is SolidColorBrush brush)
        {
            Color color = brush.Color;
            color.A = 245;
            return color;
        }
        return Color.FromArgb(245, 247, 250, 253);
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
