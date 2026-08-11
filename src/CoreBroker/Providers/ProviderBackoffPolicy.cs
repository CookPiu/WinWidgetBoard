namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Pure configuration for exponential provider retry delay. State such as the
/// current failure count is owned by the scheduler, not by this value object.
/// </summary>
public sealed record ProviderBackoffOptions
{
    public ProviderBackoffOptions(
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        double jitterRatio)
    {
        BaseDelay = RequirePositiveFinite(baseDelay, nameof(baseDelay));
        MaxDelay = RequirePositiveFinite(maxDelay, nameof(maxDelay));
        if (MaxDelay < BaseDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDelay),
                maxDelay,
                "Maximum delay cannot be shorter than the base delay.");
        }

        if (!double.IsFinite(jitterRatio) || jitterRatio < 0 || jitterRatio > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterRatio),
                jitterRatio,
                "Jitter ratio must be finite and within [0, 1].");
        }

        JitterRatio = jitterRatio;
    }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    public double JitterRatio { get; }

    private static TimeSpan RequirePositiveFinite(
        TimeSpan value,
        string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Delay must be finite and positive.");
        }

        return value;
    }
}

/// <summary>
/// Computes one deterministic retry delay. The method has no clock, random
/// source, cancellation or mutable state; callers supply a jitter sample.
/// </summary>
public static class ProviderBackoffPolicy
{
    public static TimeSpan CalculateDelay(
        ProviderBackoffOptions options,
        int consecutiveFailures,
        double jitterUnit,
        TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (consecutiveFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailures),
                consecutiveFailures,
                "Failure count starts at one.");
        }

        if (!double.IsFinite(jitterUnit) || jitterUnit < 0 || jitterUnit >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterUnit),
                jitterUnit,
                "Jitter unit must be finite and within [0, 1).");
        }

        if (retryAfter is { } retry &&
            (retry < TimeSpan.Zero || retry == Timeout.InfiniteTimeSpan))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfter),
                retryAfter,
                "Retry-After must be finite and non-negative.");
        }

        long rawTicks = CalculateRawTicks(
            options.BaseDelay.Ticks,
            options.MaxDelay.Ticks,
            consecutiveFailures);
        long jitteredTicks = ApplySymmetricJitter(
            rawTicks,
            options.MaxDelay.Ticks,
            options.JitterRatio,
            jitterUnit);

        long resultTicks = jitteredTicks;
        if (retryAfter is { } retryAfterValue)
        {
            resultTicks = Math.Max(resultTicks, retryAfterValue.Ticks);
        }

        resultTicks = Math.Min(resultTicks, options.MaxDelay.Ticks);
        return TimeSpan.FromTicks(resultTicks);
    }

    private static long CalculateRawTicks(
        long baseTicks,
        long maxTicks,
        int consecutiveFailures)
    {
        long rawTicks = baseTicks;
        for (int failure = 1;
             failure < consecutiveFailures && rawTicks < maxTicks;
             failure++)
        {
            if (rawTicks > maxTicks / 2)
            {
                return maxTicks;
            }

            rawTicks *= 2;
        }

        return Math.Min(rawTicks, maxTicks);
    }

    private static long ApplySymmetricJitter(
        long rawTicks,
        long maxTicks,
        double jitterRatio,
        double jitterUnit)
    {
        double factor = 1 + ((2 * jitterUnit) - 1) * jitterRatio;
        double jitteredTicks = rawTicks * factor;
        if (jitteredTicks <= 0)
        {
            return 0;
        }

        if (jitteredTicks >= maxTicks)
        {
            return maxTicks;
        }

        return checked((long)Math.Round(
            jitteredTicks,
            MidpointRounding.ToEven));
    }
}
