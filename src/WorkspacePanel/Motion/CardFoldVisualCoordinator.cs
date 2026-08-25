using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
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

            CardFoldPath path = CardFoldPathResolver.Resolve(
                launcherAnchor,
                bounds,
                index,
                available.Length);
            _session[id] = new SessionEntry(
                path,
                element,
                ElementCompositionPreview.GetElementVisual(element));
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

        // Land on exact identity. This is the part that must never be skipped: a
        // residual composition transform paints the card off its arranged position
        // for the rest of that element's life, and hit testing - which never moved -
        // would then disagree with the picture. Elements are recycled, so a card that
        // returns from the repeater must return unfolded.
        element.Opacity = 1;
        element.CacheMode = null;
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Scale = Vector3.One;
        visual.RotationAngleInDegrees = 0;
        visual.Offset = Vector3.Zero;
        visual.CenterPoint = Vector3.Zero;
    }

    private static void ApplyPresentation(
        SessionEntry entry,
        CardFoldPresentation presentation)
    {
        // Opacity stays on the element: measured at zero cost, and it keeps the
        // fade in the same place the rest of the panel's fades live.
        entry.Element.Opacity = presentation.Opacity;

        // The geometry runs on the card's own composition visual, not on a
        // RenderTransform.
        //
        // Measured on the reference machine, writing a CompositeTransform on six
        // cards per frame is the whole cost of the fold: with it the motion averaged
        // 12.19 ms open and 15.69 ms close on a 165 Hz display; writing only the
        // opacity averaged 5.98 and 5.94, which is the panel's frame budget exactly.
        // A bitmap cache made no difference at all (12.20 / 16.22), so the earlier
        // assumption that it avoided re-rasterization did not hold.
        //
        // The trade this makes is real and bounded: a composition transform is not in
        // the XAML hit-test chain, so during the fold the pointer hits where the card
        // was arranged rather than where it is painted. That is acceptable for the
        // length of an animation and only for that length - ResetElement returns every
        // property to identity, which is what keeps this from becoming the persistent
        // render/input mismatch that a redirected visual once caused. Sampling a live
        // visual into another tree remains banned; this only sets properties on the
        // element's own visual.
        entry.Visual.CenterPoint = new Vector3(
            (float)entry.Path.PivotX,
            (float)entry.Path.PivotY,
            0);

        // A rotation about the in-plane radial axis foreshortens the extent
        // perpendicular to it. Without a 3D transform that reads as an anisotropic
        // squash toward the hinge, which is the same silhouette the fold produced.
        (double scaleX, double scaleY) =
            CardFoldPathResolver.ResolveFoldScale(presentation);
        entry.Visual.Scale = new Vector3((float)scaleX, (float)scaleY, 1);
        entry.Visual.RotationAngleInDegrees = (float)presentation.RotationZ;
        entry.Visual.Offset = new Vector3(
            (float)presentation.OffsetX,
            (float)presentation.OffsetY,
            0);
    }

    private sealed record SessionEntry(
        CardFoldPath Path,
        FrameworkElement Element,
        Visual Visual);
}
