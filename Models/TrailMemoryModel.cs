namespace WallpaperMatrix.Models;

/// <summary>
/// Converts the dimensionless Memory setting into per-stream timing.
/// The stream's configured Data volume is the reference distance. Memory
/// stretches one continuous brightness gradient over a multiple of that
/// distance; it never inserts a full-brightness holding plateau.
/// </summary>
public static class TrailMemoryModel
{
    public const double MaximumSliderDuration = 3.0;
    public const double MaximumDuration = 10.0;

    public static TrailMemoryTiming Create(
        double duration,
        double referenceSeconds)
    {
        duration = Math.Clamp(duration, 0.0, MaximumDuration);
        referenceSeconds = Math.Max(0.001, referenceSeconds);
        return new TrailMemoryTiming(0.0, duration * referenceSeconds);
    }

    public static double RemainingBrightness(
        double elapsedSeconds,
        double holdSeconds,
        double fadeSeconds)
    {
        if (elapsedSeconds <= holdSeconds)
            return 1.0;
        if (fadeSeconds <= 0.0001)
            return 0.0;

        double position = Math.Clamp(
            (elapsedSeconds - holdSeconds) / fadeSeconds,
            0.0,
            1.0);
        double remaining = 1.0 - position;
        // A fixed filmic response is intentionally not user-adjustable:
        // the Memory curve distributes durations, it does not reshape them.
        return remaining * remaining * (3.0 - 2.0 * remaining);
    }
}

public readonly record struct TrailMemoryTiming(
    double HoldSeconds,
    double FadeSeconds)
{
    public double TotalSeconds => HoldSeconds + FadeSeconds;
}
