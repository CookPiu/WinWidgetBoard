namespace WinWidgetBoard.WorkspacePanel.Motion;

public enum PanelMotionState
{
    Closed,
    Opening,
    Open,
    Closing,
}

public readonly record struct PanelMotionValue(
    double Opacity,
    double Scale,
    double OffsetX,
    double OffsetY);

public sealed class PanelMotionController
{
    private const double SpringAngularFrequency = 15.491933384829668;
    private const double ReducedMotionTimeConstant = 0.16;
    private const double SettleValueEpsilon = 0.001;
    private const double SettleVelocityEpsilon = 0.01;

    private readonly PanelMotionValue _closedValue;
    private readonly bool _reducedMotion;
    private readonly PanelMotionValue _openValue = new(1, 1, 0, 0);
    private PanelMotionValue _target;
    private PanelMotionValue _value;
    private double _opacityVelocity;
    private double _scaleVelocity;
    private double _offsetXVelocity;
    private double _offsetYVelocity;

    public PanelMotionController(
        double closedOffsetX,
        double closedOffsetY,
        bool reducedMotion)
    {
        _reducedMotion = reducedMotion;
        _closedValue = reducedMotion
            ? new(0, 1, 0, 0)
            : new(0, 0.99, closedOffsetX, closedOffsetY);
        _target = _closedValue;
        _value = _closedValue;
        State = PanelMotionState.Closed;
    }

    public PanelMotionState State { get; private set; }

    public PanelMotionValue Value => _value;

    public bool IsAnimating =>
        State is PanelMotionState.Opening or PanelMotionState.Closing;

    public void RequestOpen()
    {
        _target = _openValue;
        if (IsAtTarget())
        {
            State = PanelMotionState.Open;
            return;
        }

        State = PanelMotionState.Opening;
    }

    public void RequestClose()
    {
        _target = _closedValue;
        if (IsAtTarget())
        {
            State = PanelMotionState.Closed;
            return;
        }

        State = PanelMotionState.Closing;
    }

    public PanelMotionValue Step(TimeSpan elapsed)
    {
        if (!IsAnimating)
        {
            return _value;
        }

        double seconds = Math.Clamp(elapsed.TotalSeconds, 0.001, 0.05);
        if (_reducedMotion)
        {
            double progress = 1 - Math.Exp(-seconds / ReducedMotionTimeConstant);
            _value = Lerp(_value, _target, progress);
        }
        else
        {
            (double opacity, _opacityVelocity) = CriticallyDampedSpring.Advance(
                _value.Opacity,
                _opacityVelocity,
                _target.Opacity,
                SpringAngularFrequency,
                seconds);
            (double scale, _scaleVelocity) = CriticallyDampedSpring.Advance(
                _value.Scale,
                _scaleVelocity,
                _target.Scale,
                SpringAngularFrequency,
                seconds);
            (double offsetX, _offsetXVelocity) = CriticallyDampedSpring.Advance(
                _value.OffsetX,
                _offsetXVelocity,
                _target.OffsetX,
                SpringAngularFrequency,
                seconds);
            (double offsetY, _offsetYVelocity) = CriticallyDampedSpring.Advance(
                _value.OffsetY,
                _offsetYVelocity,
                _target.OffsetY,
                SpringAngularFrequency,
                seconds);
            _value = new(opacity, scale, offsetX, offsetY);
        }

        if (IsAtTarget())
        {
            _value = _target;
            _opacityVelocity = 0;
            _scaleVelocity = 0;
            _offsetXVelocity = 0;
            _offsetYVelocity = 0;
            State = State == PanelMotionState.Closing
                ? PanelMotionState.Closed
                : PanelMotionState.Open;
        }

        return _value;
    }

    private bool IsAtTarget() =>
        Math.Abs(_value.Opacity - _target.Opacity) <= SettleValueEpsilon &&
        Math.Abs(_value.Scale - _target.Scale) <= SettleValueEpsilon &&
        Math.Abs(_value.OffsetX - _target.OffsetX) <= SettleValueEpsilon &&
        Math.Abs(_value.OffsetY - _target.OffsetY) <= SettleValueEpsilon &&
        (_reducedMotion ||
            Math.Abs(_opacityVelocity) <= SettleVelocityEpsilon &&
            Math.Abs(_scaleVelocity) <= SettleVelocityEpsilon &&
            Math.Abs(_offsetXVelocity) <= SettleVelocityEpsilon &&
            Math.Abs(_offsetYVelocity) <= SettleVelocityEpsilon);

    private static PanelMotionValue Lerp(
        PanelMotionValue from,
        PanelMotionValue to,
        double progress) =>
        new(
            from.Opacity + (to.Opacity - from.Opacity) * progress,
            from.Scale + (to.Scale - from.Scale) * progress,
            from.OffsetX + (to.OffsetX - from.OffsetX) * progress,
            from.OffsetY + (to.OffsetY - from.OffsetY) * progress);
}
