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

    // Dismissal must not read as slower than reveal. The previous 14.0 left the
    // material fading for about twice as long on close as on open.
    private const double CloseFrequency = 22.0;

    // A total duration, not a time constant. Driving the reduced-motion fade as
    // exp(-t/0.18) needed roughly 5.5 time constants (~1 s) to reach the settle
    // threshold, which made the accessible path slower than the full animation
    // and delayed the residency hide with it.
    private const double ReducedFadeSeconds = 0.18;

    private const double ValueEpsilon = 0.004;
    private const double VelocityEpsilon = 0.08;
    private readonly bool _reducedMotion;
    private double _value;
    private double _velocity;
    private double _reducedStart;
    private double _reducedElapsed;
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
        RestartReducedFade();
        State = IsAtTarget(1) ? PanelMotionState.Open : PanelMotionState.Opening;
    }

    public void RequestClose()
    {
        _targetOpen = false;
        RestartReducedFade();
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
            AdvanceReducedFade(target, seconds);
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

    private void RestartReducedFade()
    {
        if (!_reducedMotion)
        {
            return;
        }

        _reducedStart = _value;
        _reducedElapsed = 0;
    }

    private void AdvanceReducedFade(double target, double seconds)
    {
        // The span scales the duration, so a reversal continues from the displayed
        // value instead of replaying a full-length fade over a shorter distance.
        double span = Math.Abs(target - _reducedStart);
        double duration = ReducedFadeSeconds * span;
        _reducedElapsed += seconds;
        double progress = duration <= 0
            ? 1
            : Math.Clamp(_reducedElapsed / duration, 0, 1);
        _value = _reducedStart +
            (target - _reducedStart) * Smoothstep(progress);
        _velocity = 0;
    }

    private static double Smoothstep(double progress) =>
        progress * progress * (3 - 2 * progress);

    private bool IsAtTarget(double target) =>
        Math.Abs(_value - target) <= ValueEpsilon &&
        (_reducedMotion || Math.Abs(_velocity) <= VelocityEpsilon);
}
