using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinWidgetBoard.WorkspacePanel.Shell;

/// <summary>
/// Desktop Acrylic in its thin variant: the backdrop of the panel and of a popped-out note.
///
/// The built-in <see cref="DesktopAcrylicBackdrop"/> cannot choose a variant, so this hosts the
/// controller itself. Only <c>Kind</c> is set. Tint, luminosity and fallback colour stay the
/// controller's own, which is what keeps light, dark and high contrast following the system
/// without this class tracking a theme change: once any of those four is customised the
/// controller stops applying its per-theme defaults and every theme switch becomes ours to
/// handle. The configuration handed over by the base class carries the window's theme and
/// activation state.
///
/// One instance serves one window. The panel and a note window each create their own, so the
/// per-target bookkeeping a shareable backdrop needs is deliberately absent.
/// </summary>
public sealed partial class ThinDesktopAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private DesktopAcrylicController? _controller;

    /// <summary>
    /// Whether the system can render Desktop Acrylic at all. Checked before the backdrop is
    /// assigned, because a failure inside <see cref="OnTargetConnected"/> surfaces in a XAML
    /// callback rather than at the assignment, where the caller could still fall back.
    /// </summary>
    public static bool IsSupported() => DesktopAcrylicController.IsSupported();

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        if (_controller is not null)
        {
            throw new InvalidOperationException(
                "A ThinDesktopAcrylicBackdrop serves a single window and cannot be shared.");
        }

        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
        };
        _controller.SetSystemBackdropConfiguration(
            GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        Dispose();
    }

    /// <summary>
    /// Releases the controller. Detaching the backdrop from its window already does this; the
    /// method is for an owner that drops the backdrop without the window ever detaching it.
    /// </summary>
    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
    }
}
