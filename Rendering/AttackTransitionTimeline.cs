namespace WallpaperMatrix.Rendering;

/// <summary>
/// Defines the two independent parts of the system-attack transition.
/// New stream glyphs are driven exclusively by the running simulation;
/// this timeline controls only the background veil that follows them.
/// </summary>
internal sealed class AttackTransitionTimeline
{
    private readonly double _durationSeconds;
    private readonly double _backgroundDelaySeconds;

    public double DurationSeconds => _durationSeconds;
    public double StreamLeadSeconds => _backgroundDelaySeconds;
    public double CompletionSeconds =>
        _backgroundDelaySeconds + _durationSeconds;

    public AttackTransitionTimeline(
        double durationSeconds,
        double streamTraversalSeconds)
    {
        _durationSeconds = Math.Max(0.001, durationSeconds);
        _backgroundDelaySeconds = Math.Max(
            0,
            streamTraversalSeconds);
    }

    public double BackgroundProgress(double elapsedSeconds)
    {
        return Math.Clamp(
            (elapsedSeconds - _backgroundDelaySeconds)
                / _durationSeconds,
            0,
            1);
    }
}

/// <summary>
/// A single immutable presentation state for the attack overlay.
/// Background coverage and overlay fading are deliberately independent
/// from the lifetime and position of stream glyphs.
/// </summary>
internal readonly record struct AttackTransitionState(
    double BackgroundProgress,
    double BackgroundOpacity,
    double GlyphOpacity)
{
    public static AttackTransitionState Active(double backgroundProgress) =>
        new(backgroundProgress, 1, 1);

    public static AttackTransitionState Fading(
        double backgroundProgress,
        double opacity) =>
        new(backgroundProgress, opacity, opacity);
}
