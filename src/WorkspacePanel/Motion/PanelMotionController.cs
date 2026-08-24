namespace WinWidgetBoard.WorkspacePanel.Motion;

public enum PanelMotionState
{
    Closed,
    Opening,
    Open,
    Closing,
}

public readonly record struct PanelMotionValue(double Opacity);

/// <summary>
/// Keeps the native panel at final geometry and animates material opacity.
/// Cards own independent fold channels.
/// </summary>
public sealed class PanelMotionController
{
    private const double OpenFrequency = 25.5;
    private const double CloseFrequency = 14.0;
    private const double ReducedTimeConstant = 0.18;
    private const double ValueEpsilon = 0.004;
    private const double VelocityEpsilon = 0.08;
    private readonly bool _reducedMotion;
    private double _value;
    private double _velocity;
    private bool _targetOpen;

    public PanelMotionController(bool reducedMotion)
    {
        _reducedMotion = reducedMotion;
        State = PanelMotionState.Closed;
    }

    public PanelMotionState State { get; private set; }
    public PanelMotionValue Value => new(_value);
    public bool IsAnimating =>
        State is PanelMotionState.Opening or PanelMotionState.Closing;

    public void RequestOpen()
    {
        _targetOpen = true;
        State = IsAtTarget(1) ? PanelMotionState.Open : PanelMotionState.Opening;
    }

    public void RequestClose()
    {
        _targetOpen = false;
        State = IsAtTarget(0) ? PanelMotionState.Closed : PanelMotionState.Closing;
    }

    public PanelMotionValue Step(TimeSpan elapsed)
    {
        if (!IsAnimating)
        {
            return Value;
        }

        double seconds = Math.Clamp(elapsed.TotalSeconds, 0.001, 0.05);
        double target = _targetOpen ? 1 : 0;
        if (_reducedMotion)
        {
            double progress = 1 - Math.Exp(-seconds / ReducedTimeConstant);
            _value += (target - _value) * progress;
            _velocity = 0;
        }
        else
        {
            (_value, _velocity) = CriticallyDampedSpring.Advance(
                _value,
                _velocity,
                target,
                _targetOpen ? OpenFrequency : CloseFrequency,
                seconds);
        }

        if (IsAtTarget(target))
        {
            _value = target;
            _velocity = 0;
            State = _targetOpen ? PanelMotionState.Open : PanelMotionState.Closed;
        }

        return Value;
    }

    private bool IsAtTarget(double target) =>
        Math.Abs(_value - target) <= ValueEpsilon &&
        (_reducedMotion || Math.Abs(_velocity) <= VelocityEpsilon);
}
