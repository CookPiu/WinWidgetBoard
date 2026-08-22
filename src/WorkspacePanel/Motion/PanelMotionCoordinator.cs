using Microsoft.UI.Dispatching;
using System.Diagnostics;
using WinWidgetBoard.WorkspacePanel.Interaction;

namespace WinWidgetBoard.WorkspacePanel.Motion;

/// <summary>
/// Coordinates panel and demo-card motion on one UI timer.
/// Rendering and native-window effects remain callback-owned by MainWindow.
/// </summary>
public sealed class PanelMotionCoordinator : IDisposable
{
    private readonly PanelMotionController _panelMotion;
    private readonly CardReturnMotionController _cardReturnMotion;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<PanelMotionValue> _applyPanelMotion;
    private readonly Action<DragOffset> _applyCardReturn;
    private readonly Func<bool> _isCardDragActive;
    private readonly Action<DragOffset> _setCardDragOffset;
    private readonly Action _closeAfterMotion;
    private int _disposed;
    private bool _closeWhenSettles;
    private long _lastMotionTimestamp;

    public PanelMotionCoordinator(
        double closedOffsetX,
        double closedOffsetY,
        PanelMotionAxis primaryAxis,
        bool reducedMotion,
        DispatcherQueue uiDispatcherQueue,
        Action<PanelMotionValue> applyPanelMotion,
        Action<DragOffset> applyCardReturn,
        Func<bool> isCardDragActive,
        Action<DragOffset> setCardDragOffset,
        Action closeAfterMotion)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcherQueue);
        _applyPanelMotion = applyPanelMotion ??
            throw new ArgumentNullException(nameof(applyPanelMotion));
        _applyCardReturn = applyCardReturn ??
            throw new ArgumentNullException(nameof(applyCardReturn));
        _isCardDragActive = isCardDragActive ??
            throw new ArgumentNullException(nameof(isCardDragActive));
        _setCardDragOffset = setCardDragOffset ??
            throw new ArgumentNullException(nameof(setCardDragOffset));
        _closeAfterMotion = closeAfterMotion ??
            throw new ArgumentNullException(nameof(closeAfterMotion));
        _panelMotion = new PanelMotionController(
            closedOffsetX,
            closedOffsetY,
            primaryAxis,
            reducedMotion);
        _cardReturnMotion = new CardReturnMotionController(reducedMotion);
        _timer = uiDispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += MotionTimer_Tick;
    }

    public PanelMotionValue PanelValue => _panelMotion.Value;

    public bool IsClosing => _panelMotion.State == PanelMotionState.Closing;

    public bool IsCardReturnAnimating => _cardReturnMotion.IsAnimating;

    public void RequestOpen()
    {
        _closeWhenSettles = false;
        _panelMotion.RequestOpen();
        _applyPanelMotion(_panelMotion.Value);
        StartTimerIfNeeded();
    }

    public void RequestClose()
    {
        _closeWhenSettles = true;
        _panelMotion.RequestClose();
        _applyPanelMotion(_panelMotion.Value);
        if (_panelMotion.State == PanelMotionState.Closed)
        {
            CloseAfterMotion();
            return;
        }

        StartTimerIfNeeded();
    }

    public void BeginCardReturn(
        DragOffset currentOffset,
        DragOffset targetOffset)
    {
        _cardReturnMotion.Start(currentOffset, targetOffset);
        _applyCardReturn(_cardReturnMotion.Value);
        SyncCardDragOffsetIfSettled(_cardReturnMotion.Value);
        StartTimerIfNeeded();
    }

    public void ResetCardReturn()
    {
        if (_cardReturnMotion.IsAnimating)
        {
            _cardReturnMotion.StopAt(DragOffset.Zero);
        }
    }

    public void InterruptCardReturn()
    {
        if (!_cardReturnMotion.IsAnimating)
        {
            return;
        }

        DragOffset currentOffset = _cardReturnMotion.Value;
        _cardReturnMotion.StopAt(currentOffset);
        _setCardDragOffset(currentOffset);
        _applyCardReturn(currentOffset);
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= MotionTimer_Tick;
    }

    private void StartTimerIfNeeded()
    {
        if (!_panelMotion.IsAnimating && !_cardReturnMotion.IsAnimating)
        {
            return;
        }

        _lastMotionTimestamp = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void MotionTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        long timestamp = Stopwatch.GetTimestamp();
        double seconds = (timestamp - _lastMotionTimestamp) /
            (double)Stopwatch.Frequency;
        _lastMotionTimestamp = timestamp;

        TimeSpan elapsed = TimeSpan.FromSeconds(seconds);
        if (_panelMotion.IsAnimating)
        {
            _applyPanelMotion(_panelMotion.Step(elapsed));
        }

        if (_cardReturnMotion.IsAnimating)
        {
            DragOffset returnOffset = _cardReturnMotion.Step(elapsed);
            _applyCardReturn(returnOffset);
            SyncCardDragOffsetIfSettled(returnOffset);
        }

        if (_closeWhenSettles &&
            !_panelMotion.IsAnimating &&
            _panelMotion.State == PanelMotionState.Closed)
        {
            _timer.Stop();
            CloseAfterMotion();
            return;
        }

        if (_panelMotion.IsAnimating || _cardReturnMotion.IsAnimating)
        {
            return;
        }

        _timer.Stop();
    }

    private void SyncCardDragOffsetIfSettled(DragOffset offset)
    {
        if (!_cardReturnMotion.IsAnimating && !_isCardDragActive())
        {
            _setCardDragOffset(offset);
        }
    }

    private void CloseAfterMotion()
    {
        _closeWhenSettles = false;
        _closeAfterMotion();
    }
}
