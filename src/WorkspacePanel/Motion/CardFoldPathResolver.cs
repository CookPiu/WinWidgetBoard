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
    // The scale the mirror starts at. The overlay camera adds its own recession on
    // top of this (see CardFoldVisualCoordinator), so the two together set how
    // small a card reads at the hinge. Exposed because the crease and specular
    // phase is driven off the same range.
    public const double ClosedScale = 0.30;

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
            ClosedScale + (1 - ClosedScale) * (1 - Math.Pow(inverse, 1.35)),
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
    /// <summary>
    /// Longest distance, in DIP, a card travels toward the launcher entry.
    /// </summary>
    public const double MaximumTravel = 110;

    /// <summary>
    /// Splits the uniform scale into the axis-aligned pair that approximates a
    /// fold about the in-plane radial axis: the extent perpendicular to the axis
    /// foreshortens by cos(angle), the extent along it does not.
    /// </summary>
    public static (double X, double Y) ResolveFoldScale(
        CardFoldPresentation presentation)
    {
        // Weight by the squared components. The axis is a unit vector, so these sum
        // to exactly one and the pair collapses to the uniform scale when the fold
        // angle reaches zero. Weighting by the absolute components instead summed to
        // 1.414 on a diagonal axis, which left every card 41% oversized for the whole
        // animation and snapped back on the final frame.
        double foreshorten = Math.Cos(presentation.FoldAngle * Math.PI / 180);
        double alongX = presentation.AxisX * presentation.AxisX;
        double alongY = presentation.AxisY * presentation.AxisY;
        return (
            presentation.Scale * (alongX + alongY * foreshorten),
            presentation.Scale * (alongY + alongX * foreshorten));
    }

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
        // Keep the entry's direction but bound the distance. The anchor is the
        // taskbar entry, which sits outside the panel; the cards are clipped by the
        // content ScrollViewer, so a full flight from there spends most of the
        // animation invisible and reads as the card leaving the panel and jumping
        // back. A short offset along the same bearing keeps the spatial story and
        // stays inside the card's own slot.
        double reachX = anchor.X - (bounds.X + pivotX);
        double reachY = anchor.Y - (bounds.Y + pivotY);
        double reach = Math.Sqrt(reachX * reachX + reachY * reachY);
        double travelScale = reach > 0.0001
            ? Math.Min(reach, MaximumTravel) / reach
            : 0;
        double startX = reachX * travelScale;
        double startY = reachY * travelScale;
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
