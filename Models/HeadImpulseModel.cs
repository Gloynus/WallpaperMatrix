namespace WallpaperMatrix.Models;

/// <summary>
/// Converts the operator's spatial impulse control into timing for one stream.
/// Timing is derived from stream speed, so the visible gradient occupies the
/// requested share of the trail for both slow and fast streams.
/// </summary>
public static class HeadImpulseModel
{
    public const double MaximumDecay = 2.0;

    public static HeadImpulseTiming Create(
        double decay,
        int trailLengthCells,
        double cellsPerSecond)
    {
        decay = Math.Clamp(decay, 0.0, MaximumDecay);
        double safeSpeed = Math.Max(0.1, cellsPerSecond);
        double safeLength = Math.Max(1, trailLengthCells);
        if (decay <= 0)
        {
            // The head still flashes inside its current cell, but the first
            // completed symbol behind it is already ordinary.
            return new HeadImpulseTiming(0, 1.0 / safeSpeed);
        }

        // One percent is intentionally an operator-friendly special point:
        // only the next symbol receives half of the impulse characteristics.
        if (decay <= 0.0100001)
            return new HeadImpulseTiming(0, 2.0 / safeSpeed);

        if (decay <= 1.0)
        {
            double fadeCells = Math.Max(2.0, safeLength * decay);
            return new HeadImpulseTiming(0, fadeCells / safeSpeed);
        }

        double holdCells = safeLength * (decay - 1.0);
        double fadeCellsAfterHold = safeLength * (MaximumDecay - decay);
        return new HeadImpulseTiming(
            holdCells / safeSpeed,
            fadeCellsAfterHold / safeSpeed);
    }

    public static double Emphasis(
        double ageSeconds,
        double holdSeconds,
        double fadeSeconds)
    {
        if (ageSeconds <= 0)
            return 1.0;
        if (ageSeconds <= holdSeconds)
            return 1.0;
        if (fadeSeconds <= 0)
            return 0.0;

        double remaining = Math.Clamp(
            1.0 - (ageSeconds - holdSeconds) / fadeSeconds,
            0.0,
            1.0);
        return remaining * remaining * (3.0 - 2.0 * remaining);
    }
}

public readonly record struct HeadImpulseTiming(
    double HoldSeconds,
    double FadeSeconds);
