using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace WinWidgetBoard.WorkspacePanel.Shell;

/// <summary>
/// Fades the header status line out after a dwell, so a finished action's report does not
/// sit in the header for hours. The status rules allow a line only while something is worth
/// saying; every message here is transient by construction - an in-progress message is always
/// replaced by its outcome, and the outcome has been read long before the dwell ends.
///
/// The line's space is never collapsed: the header is a fixed frame, and a status that
/// removed its own row would shift every card under it twice per message. Only opacity moves,
/// and only on the compositor.
/// </summary>
internal sealed class StatusLineController : IDisposable
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReducedFadeDuration = TimeSpan.FromMilliseconds(150);

    private readonly TextBlock _statusText;
    private readonly bool _reducedMotion;
    private readonly DispatcherQueueTimer _dwellTimer;
    private readonly long _textChangedToken;
    private long _version;
    private bool _clearingOwnText;
    private bool _disposed;

    public StatusLineController(
        TextBlock statusText,
        DispatcherQueue dispatcherQueue,
        bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(statusText);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _statusText = statusText;
        _reducedMotion = reducedMotion;
        _dwellTimer = dispatcherQueue.CreateTimer();
        _dwellTimer.Interval = Dwell;
        _dwellTimer.IsRepeating = false;
        _dwellTimer.Tick += (_, _) => BeginFade();
        _textChangedToken = statusText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => OnTextChanged());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dwellTimer.Stop();
        _statusText.UnregisterPropertyChangedCallback(
            TextBlock.TextProperty,
            _textChangedToken);
    }

    private void OnTextChanged()
    {
        if (_disposed || _clearingOwnText)
        {
            return;
        }

        _version++;
        _dwellTimer.Stop();
        Visual visual = ElementCompositionPreview.GetElementVisual(_statusText);
        visual.StopAnimation(nameof(Visual.Opacity));
        if (string.IsNullOrEmpty(_statusText.Text))
        {
            visual.Opacity = 0;
            return;
        }

        visual.Opacity = 1;
        _dwellTimer.Start();
    }

    private void BeginFade()
    {
        if (_disposed)
        {
            return;
        }

        long version = _version;
        Visual visual = ElementCompositionPreview.GetElementVisual(_statusText);
        Compositor compositor = visual.Compositor;
        CompositionScopedBatch batch = compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = _reducedMotion ? ReducedFadeDuration : FadeDuration;
        fade.InsertKeyFrame(
            1,
            0,
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.23f, 1),
                new Vector2(0.32f, 1)));
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        batch.Completed += (_, _) =>
        {
            if (_disposed || _version != version)
            {
                return;
            }

            // The faded text is cleared so assistive tech does not keep reading a message
            // the screen no longer shows. The guard keeps the clear from re-arming the dwell.
            _clearingOwnText = true;
            _statusText.Text = string.Empty;
            _clearingOwnText = false;
        };
        batch.End();
    }
}
