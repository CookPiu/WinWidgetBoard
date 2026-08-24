using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;

namespace WinWidgetBoard.WorkspacePanel.Motion;

/// <summary>
/// Folds the realized card elements themselves.
///
/// The previous design mirrored each card into a root-level composition overlay
/// through a RedirectVisual so a fold could originate outside the ScrollViewer.
/// Handing a live XAML element's visual to the compositor as a source -- either a
/// RedirectVisual or a CompositionVisualSurface, both measured -- displaced that
/// subtree's XAML hit-test geometry by a full grid row and never restored it, so
/// the panel arranged and painted correctly while pointer input landed a row
/// lower. The spec requires the fold to leave hit-testing and UIA identity
/// untouched (docs/02-ux-design-spec.md section 9.3), so nothing samples a live
/// visual any more: each card carries its own transform and the content
/// ScrollViewer clips it.
/// </summary>
public sealed class CardFoldVisualCoordinator : IDisposable
{
    private readonly bool _reducedMotion;
    private readonly Dictionary<string, FrameworkElement> _registered =
        new(StringComparer.Ordinal);
    private readonly Dictionary<UIElement, string> _elementIds = [];
    private readonly Dictionary<string, SessionEntry> _session =
        new(StringComparer.Ordinal);
    private bool _prepared;
    private int _disposed;

    public CardFoldVisualCoordinator(bool reducedMotion)
    {
        _reducedMotion = reducedMotion;
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
            ResetElement(old);
            _elementIds.Remove(old);
        }

        _registered[instanceId] = frameworkElement;
        _elementIds[frameworkElement] = instanceId;
    }

    public void Unregister(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_elementIds.Remove(element, out string? id))
        {
            return;
        }

        // ItemsRepeater recycles elements, so a cleared card must not keep a
        // half-applied fold when it comes back for a different item.
        ResetElement(element as FrameworkElement);
        _registered.Remove(id);
        _session.Remove(id);
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
            // Preserve the caller's order. An arbitrary dictionary order scrambled
            // the per-card stagger whenever a toggle re-prepared a live session.
            return orderedIds
                .Where(_session.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var available = orderedIds
            .Where(_registered.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (_reducedMotion || available.Length == 0)
        {
            return available;
        }

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

            // Rasterize each card once for the fold. A RenderTransform scale makes
            // XAML re-rasterize the card's text and shapes every frame, which is
            // what dropped the frame rate; a bitmap cache transforms one texture
            // instead. It does not affect hit testing.
            element.CacheMode = new BitmapCache();
            CardFoldPath path = CardFoldPathResolver.Resolve(
                launcherAnchor,
                bounds,
                index,
                available.Length);
            _session[id] = new SessionEntry(
                path,
                element,
                element.RenderTransform as CompositeTransform);
        }

        _prepared = _session.Count > 0;
        return available.Where(_session.ContainsKey).ToArray();
    }

    public void Apply(IReadOnlyList<CardFoldMotionValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
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

            ApplyPresentation(entry, entry.Path.Evaluate(value.Progress));
        }
    }

    public void Complete() => ClearSession();

    public void Clear()
    {
        ClearSession();
        foreach (FrameworkElement element in _registered.Values)
        {
            ResetElement(element);
        }

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
    }

    private void ClearSession()
    {
        foreach (SessionEntry entry in _session.Values)
        {
            ResetElement(entry.Element);
        }

        _session.Clear();
        _prepared = false;
    }

    private static void ResetElement(FrameworkElement? element)
    {
        if (element is null)
        {
            return;
        }

        // Land on exact identity. A residual transform would leave the card painted
        // off its arranged position for the rest of that element's life.
        element.Opacity = 1;
        element.CacheMode = null;
        if (element.RenderTransform is not CompositeTransform transform)
        {
            return;
        }

        transform.ScaleX = 1;
        transform.ScaleY = 1;
        transform.Rotation = 0;
        transform.TranslateX = 0;
        transform.TranslateY = 0;
        transform.CenterX = 0;
        transform.CenterY = 0;
    }

    private static void ApplyPresentation(
        SessionEntry entry,
        CardFoldPresentation presentation)
    {
        entry.Element.Opacity = presentation.Opacity;
        if (entry.Transform is not CompositeTransform transform)
        {
            return;
        }

        // The fold runs on the card's own RenderTransform. The element-level
        // composition properties (Scale, Rotation, CenterPoint, RotationAxis) throw
        // UnauthorizedAccessException on these cards because a RenderTransform
        // already owns that slot, and a RenderTransform is the better answer anyway:
        // XAML hit-testing follows it, so pointer input tracks the picture even
        // mid-fold.
        transform.CenterX = entry.Path.PivotX;
        transform.CenterY = entry.Path.PivotY;

        // A rotation about the in-plane radial axis foreshortens the extent
        // perpendicular to it. Without a 3D transform that reads as an anisotropic
        // squash toward the hinge, which is the same silhouette the fold produced.
        (double scaleX, double scaleY) =
            CardFoldPathResolver.ResolveFoldScale(presentation);
        transform.ScaleX = scaleX;
        transform.ScaleY = scaleY;
        transform.Rotation = presentation.RotationZ;
        transform.TranslateX = presentation.OffsetX;
        transform.TranslateY = presentation.OffsetY;
    }

    private sealed record SessionEntry(
        CardFoldPath Path,
        FrameworkElement Element,
        CompositeTransform? Transform);
}
