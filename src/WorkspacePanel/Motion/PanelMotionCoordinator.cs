using Microsoft.UI.Dispatching;
using System.Diagnostics;
using WinWidgetBoard.WorkspacePanel.Interaction;

namespace WinWidgetBoard.WorkspacePanel.Motion;

public sealed class PanelMotionCoordinator : IDisposable
{
    private readonly PanelMotionController _panel = new(false);
    private readonly CardFoldMotionController _folds;
    private readonly CardReturnMotionController _cardReturn;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<PanelMotionValue> _applyPanel;
    private readonly Action<IReadOnlyList<CardFoldMotionValue>> _applyFolds;
    private readonly Action _completeFolds;
    private readonly Action<DragOffset> _applyReturn;
    private readonly Func<bool> _isDragActive;
    private readonly Action<DragOffset> _setDragOffset;
    private readonly Action _closeAfterMotion;
    private bool _closeWhenSettles;
    private long _lastTimestamp;
    private int _disposed;

    public PanelMotionCoordinator(
        bool reducedMotion,
        DispatcherQueue dispatcher,
        Action<PanelMotionValue> applyPanel,
        Action<IReadOnlyList<CardFoldMotionValue>> applyFolds,
        Action completeFolds,
        Action<DragOffset> applyReturn,
        Func<bool> isDragActive,
        Action<DragOffset> setDragOffset,
        Action closeAfterMotion)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _panel = new PanelMotionController(reducedMotion);
        _folds = new CardFoldMotionController(reducedMotion);
        _cardReturn = new CardReturnMotionController(reducedMotion);
        _applyPanel = applyPanel ?? throw new ArgumentNullException(nameof(applyPanel));
        _applyFolds = applyFolds ?? throw new ArgumentNullException(nameof(applyFolds));
        _completeFolds = completeFolds ?? throw new ArgumentNullException(nameof(completeFolds));
        _applyReturn = applyReturn ?? throw new ArgumentNullException(nameof(applyReturn));
        _isDragActive = isDragActive ?? throw new ArgumentNullException(nameof(isDragActive));
        _setDragOffset = setDragOffset ?? throw new ArgumentNullException(nameof(setDragOffset));
        _closeAfterMotion = closeAfterMotion ?? throw new ArgumentNullException(nameof(closeAfterMotion));
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += Tick;
    }

    public PanelMotionValue PanelValue => _panel.Value;
    public bool IsClosing => _panel.State == PanelMotionState.Closing;
    public bool IsCardReturnAnimating => _cardReturn.IsAnimating;
    public bool IsCardFoldAnimating => _folds.IsAnimating;

    public void ConfigureCardFolds(IReadOnlyList<string> ids) => _folds.Configure(ids);

    public void RequestOpen()
    {
        _closeWhenSettles = false;
        _panel.RequestOpen();
        _folds.RequestOpen();
        ApplyCurrent();
        StartTimerIfNeeded();
    }

    public void RequestClose()
    {
        _closeWhenSettles = true;
        _panel.RequestClose();
        _folds.RequestClose();
        ApplyCurrent();
        if (_panel.State == PanelMotionState.Closed && !_folds.IsAnimating)
        {
            CloseAfterMotion();
            return;
        }
        StartTimerIfNeeded();
    }

    public void BeginCardReturn(DragOffset current, DragOffset target)
    {
        _cardReturn.Start(current, target);
        _applyReturn(_cardReturn.Value);
        SyncReturnIfSettled(_cardReturn.Value);
        StartTimerIfNeeded();
    }

    public void ResetCardReturn()
    {
        if (_cardReturn.IsAnimating) _cardReturn.StopAt(DragOffset.Zero);
    }

    public void InterruptCardReturn()
    {
        if (!_cardReturn.IsAnimating) return;
        DragOffset current = _cardReturn.Value;
        _cardReturn.StopAt(current);
        _setDragOffset(current);
        _applyReturn(current);
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Stop();
        _timer.Tick -= Tick;
    }

    private void ApplyCurrent()
    {
        _applyPanel(_panel.Value);
        _applyFolds(_folds.Values);
        if (!_folds.IsAnimating) _completeFolds();
    }

    private void StartTimerIfNeeded()
    {
        if (!_panel.IsAnimating && !_folds.IsAnimating && !_cardReturn.IsAnimating) return;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = TimeSpan.FromSeconds(
            (now - _lastTimestamp) / (double)Stopwatch.Frequency);
        _lastTimestamp = now;
        if (_panel.IsAnimating) _applyPanel(_panel.Step(elapsed));
        if (_folds.IsAnimating)
        {
            _applyFolds(_folds.Step(elapsed));
            if (!_folds.IsAnimating) _completeFolds();
        }
        if (_cardReturn.IsAnimating)
        {
            DragOffset offset = _cardReturn.Step(elapsed);
            _applyReturn(offset);
            SyncReturnIfSettled(offset);
        }
        if (_closeWhenSettles && !_panel.IsAnimating && !_folds.IsAnimating &&
            _panel.State == PanelMotionState.Closed)
        {
            _timer.Stop();
            CloseAfterMotion();
        }
        else if (!_panel.IsAnimating && !_folds.IsAnimating && !_cardReturn.IsAnimating)
        {
            _timer.Stop();
        }
    }

    private void SyncReturnIfSettled(DragOffset offset)
    {
        if (!_cardReturn.IsAnimating && !_isDragActive()) _setDragOffset(offset);
    }

    private void CloseAfterMotion()
    {
        _closeWhenSettles = false;
        _completeFolds();
        _closeAfterMotion();
    }
}
