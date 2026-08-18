namespace WinWidgetBoard.WorkspacePanel.Motion;

internal static class CriticallyDampedSpring
{
    public static (double Value, double Velocity) Advance(
        double value,
        double velocity,
        double target,
        double angularFrequency,
        double seconds)
    {
        double displacement = value - target;
        double decay = Math.Exp(-angularFrequency * seconds);
        double velocityTerm = velocity + angularFrequency * displacement;
        double nextDisplacement =
            (displacement + velocityTerm * seconds) * decay;
        double nextVelocity =
            (velocity - angularFrequency * velocityTerm * seconds) * decay;

        return (target + nextDisplacement, nextVelocity);
    }
}
