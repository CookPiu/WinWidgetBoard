using WinWidgetBoard.WorkspacePanel.Interaction;

namespace WinWidgetBoard.WorkspacePanel.Motion;

public enum CardReturnMotionState
{
    Idle,
    Returning,
}

public sealed class CardReturnMotionController
{
    private const double SpringAngularFrequency = 16.1245154965971;
    private const double SettleValueEpsilon = 0.05;
    private const double SettleVelocityEpsilon = 0.1;

    private DragOffset _target;
    private DragOffset _value;
    private double _velocityX;
    private double _velocityY;

    public CardReturnMotionController(bool reducedMotion)
    {
        ReducedMotion = reducedMotion;
        State = CardReturnMotionState.Idle;
        _target = DragOffset.Zero;
        _value = DragOffset.Zero;
    }

    public bool ReducedMotion { get; }

    public CardReturnMotionState State { get; private set; }

    public DragOffset Value => _value;

    public bool IsAnimating => State == CardReturnMotionState.Returning;

    public void Start(
        DragOffset current,
        DragOffset target,
        DragOffset initialVelocity = default)
    {
        _value = current;
        _target = target;
        _velocityX = initialVelocity.X;
        _velocityY = initialVelocity.Y;

        if (ReducedMotion || IsAtTarget())
        {
            SnapToTarget();
            return;
        }

        State = CardReturnMotionState.Returning;
    }

    public void StopAt(DragOffset value)
    {
        _value = value;
        _target = value;
        _velocityX = 0;
        _velocityY = 0;
        State = CardReturnMotionState.Idle;
    }

    public DragOffset Step(TimeSpan elapsed)
    {
        if (!IsAnimating)
        {
            return _value;
        }

        double seconds = Math.Clamp(elapsed.TotalSeconds, 0.001, 0.05);
        (double x, _velocityX) = CriticallyDampedSpring.Advance(
            _value.X,
            _velocityX,
            _target.X,
            SpringAngularFrequency,
            seconds);
        (double y, _velocityY) = CriticallyDampedSpring.Advance(
            _value.Y,
            _velocityY,
            _target.Y,
            SpringAngularFrequency,
            seconds);
        _value = new DragOffset(x, y);

        if (IsAtTarget())
        {
            SnapToTarget();
        }

        return _value;
    }

    private bool IsAtTarget() =>
        Math.Abs(_value.X - _target.X) <= SettleValueEpsilon &&
        Math.Abs(_value.Y - _target.Y) <= SettleValueEpsilon &&
        Math.Abs(_velocityX) <= SettleVelocityEpsilon &&
        Math.Abs(_velocityY) <= SettleVelocityEpsilon;

    private void SnapToTarget()
    {
        _value = _target;
        _velocityX = 0;
        _velocityY = 0;
        State = CardReturnMotionState.Idle;
    }
}
