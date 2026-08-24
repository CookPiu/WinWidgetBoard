using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using WinWidgetBoard.WorkspacePanel.Interaction;

namespace WinWidgetBoard.WorkspacePanel.Motion;

public readonly record struct MotionFrameTiming(
    int FrameCount,
    double AverageMilliseconds,
    double MaximumMilliseconds);

public sealed class PanelMotionCoordinator : IDisposable
{
    private readonly PanelMotionController _panel;
    private readonly CardFoldMotionController _folds;
    private readonly CardReturnMotionController _cardReturn;
    private readonly Action<PanelMotionValue> _applyPanel;
    private readonly Action<IReadOnlyList<CardFoldMotionValue>> _applyFolds;
    private readonly Action _completeFolds;
    private readonly Action<DragOffset> _applyReturn;
    private readonly Func<bool> _isDragActive;
    private readonly Action<DragOffset> _setDragOffset;
    private readonly Action _closeAfterMotion;
    private readonly Action<MotionFrameTiming>? _reportFrameTiming;
    private bool _closeWhenSettles;
    private bool _isRendering;
    private long _lastTimestamp;
    private int _frameCount;
    private double _totalFrameMilliseconds;
    private double _maximumFrameMilliseconds;
    private int _disposed;

    public PanelMotionCoordinator(
        bool reducedMotion,
        Action<PanelMotionValue> applyPanel,
        Action<IReadOnlyList<CardFoldMotionValue>> applyFolds,
        Action completeFolds,
        Action<DragOffset> applyReturn,
        Func<bool> isDragActive,
        Action<DragOffset> setDragOffset,
        Action closeAfterMotion,
        Action<MotionFrameTiming>? reportFrameTiming = null)
    {
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
        _reportFrameTiming = reportFrameTiming;
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
        StartRenderingIfNeeded();
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
        StartRenderingIfNeeded();
    }

    public void BeginCardReturn(DragOffset current, DragOffset target)
    {
        _cardReturn.Start(current, target);
        _applyReturn(_cardReturn.Value);
        SyncReturnIfSettled(_cardReturn.Value);
        StartRenderingIfNeeded();
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

    public void Stop() => StopRendering(reportTiming: false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopRendering(reportTiming: false);
    }

    private void ApplyCurrent()
    {
        _applyPanel(_panel.Value);
        _applyFolds(_folds.Values);
        if (!_folds.IsAnimating) _completeFolds();
    }

    private void StartRenderingIfNeeded()
    {
        if (!_panel.IsAnimating && !_folds.IsAnimating && !_cardReturn.IsAnimating) return;
        if (_isRendering) return;

        // Rendering follows the active XAML composition target, so the same spring
        // advances at the monitor's actual cadence instead of a fixed 60 Hz timer.
        _lastTimestamp = Stopwatch.GetTimestamp();
        _frameCount = 0;
        _totalFrameMilliseconds = 0;
        _maximumFrameMilliseconds = 0;
        _isRendering = true;
        CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = TimeSpan.FromSeconds(
            (now - _lastTimestamp) / (double)Stopwatch.Frequency);
        _lastTimestamp = now;
        double elapsedMilliseconds = elapsed.TotalMilliseconds;
        _frameCount++;
        _totalFrameMilliseconds += elapsedMilliseconds;
        _maximumFrameMilliseconds = Math.Max(
            _maximumFrameMilliseconds,
            elapsedMilliseconds);

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
            StopRendering(reportTiming: true);
            CloseAfterMotion();
        }
        else if (!_panel.IsAnimating && !_folds.IsAnimating && !_cardReturn.IsAnimating)
        {
            StopRendering(reportTiming: true);
        }
    }

    private void StopRendering(bool reportTiming)
    {
        if (!_isRendering) return;

        CompositionTarget.Rendering -= RenderFrame;
        _isRendering = false;
        if (reportTiming && _frameCount > 0)
        {
            _reportFrameTiming?.Invoke(new MotionFrameTiming(
                _frameCount,
                _totalFrameMilliseconds / _frameCount,
                _maximumFrameMilliseconds));
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
