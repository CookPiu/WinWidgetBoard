namespace WinWidgetBoard.WorkspacePanel.Motion;

public readonly record struct MotionPoint(double X, double Y);
public readonly record struct MotionRect(double X, double Y, double Width, double Height)
{
    public double Bottom => Y + Height;
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;
}

public readonly record struct CardFoldPresentation(
    double OffsetX, double OffsetY, double OffsetZ, double Scale,
    double AxisX, double AxisY, double FoldAngle, double RotationZ,
    double Opacity, double FoldEnergy);

public readonly record struct CardFoldPath(
    double StartX, double StartY, double ControlX, double ControlY,
    double PivotX, double PivotY, double AxisX, double AxisY,
    double FoldAngle, double RotationZ, double Lift)
{
    public CardFoldPresentation Evaluate(double rawProgress)
    {
        double raw = Math.Clamp(rawProgress, 0, 1);
        double progress = raw * raw * (3 - 2 * raw);
        double inverse = 1 - progress;
        double energy = Math.Sin(Math.PI * progress);
        return new(
            Quadratic(progress, StartX, ControlX),
            Quadratic(progress, StartY, ControlY),
            -175 * inverse + energy * Lift,
            0.27 + 0.73 * (1 - Math.Pow(inverse, 1.35)),
            AxisX,
            AxisY,
            inverse * FoldAngle,
            inverse * RotationZ,
            Math.Min(1, raw * 1.8),
            energy);
    }

    private static double Quadratic(double p, double start, double control)
    {
        double inverse = 1 - p;
        return inverse * inverse * start + 2 * inverse * p * control;
    }
}

public static class CardFoldPathResolver
{
    public static CardFoldPath Resolve(
        MotionPoint anchor,
        MotionRect bounds,
        int index,
        int count)
    {
        if (!bounds.IsValid)
        {
            throw new ArgumentException("Card bounds must be valid.", nameof(bounds));
        }
        if (count <= 0 || index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        double position = count <= 1 ? 0 : index / (double)(count - 1);
        double side = index % 3 == 0 ? -1 : 1;
        double centerX = bounds.X + bounds.Width / 2;
        double centerY = bounds.Y + bounds.Height / 2;
        (double axisX, double axisY) = ResolveRadialAxis(
            anchor,
            centerX,
            centerY,
            index,
            count);
        (double pivotX, double pivotY) = ResolveFacingEdgePivot(
            bounds,
            axisX,
            axisY);
        double startX = anchor.X - (bounds.X + pivotX);
        double startY = anchor.Y - (bounds.Y + pivotY);
        return new(
            startX,
            startY,
            startX * (0.84 - 0.22 * position) + side * 24,
            startY * (0.44 + 0.10 * position) + (position - 0.5) * 96,
            pivotX,
            pivotY,
            axisX,
            axisY,
            82 - position * 7,
            side * (5 + position * 3),
            28 + position * 8);
    }

    private static (double X, double Y) ResolveRadialAxis(
        MotionPoint anchor,
        double centerX,
        double centerY,
        int index,
        int count)
    {
        double deltaX = centerX - anchor.X;
        double deltaY = centerY - anchor.Y;
        double length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (length > 0.0001)
        {
            return (deltaX / length, deltaY / length);
        }

        // This only applies when an anchor lands exactly on a card center.
        // Spread coincident cards deterministically instead of reintroducing
        // a shared fallback axis.
        double angle = -Math.PI / 2 +
            2 * Math.PI * (index + 0.5) / Math.Max(1, count);
        return (Math.Cos(angle), Math.Sin(angle));
    }

    private static (double X, double Y) ResolveFacingEdgePivot(
        MotionRect bounds,
        double axisX,
        double axisY)
    {
        double halfWidth = bounds.Width / 2;
        double halfHeight = bounds.Height / 2;
        double towardAnchorX = -axisX;
        double towardAnchorY = -axisY;
        double horizontalDistance = Math.Abs(towardAnchorX) > 0.0001
            ? halfWidth / Math.Abs(towardAnchorX)
            : double.PositiveInfinity;
        double verticalDistance = Math.Abs(towardAnchorY) > 0.0001
            ? halfHeight / Math.Abs(towardAnchorY)
            : double.PositiveInfinity;
        double edgeDistance = Math.Min(horizontalDistance, verticalDistance);
        return (
            Math.Clamp(
                halfWidth + towardAnchorX * edgeDistance,
                0,
                bounds.Width),
            Math.Clamp(
                halfHeight + towardAnchorY * edgeDistance,
                0,
                bounds.Height));
    }
}
