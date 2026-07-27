namespace WallpaperMatrix.Models;

public static class SignalModel
{
    public const int MaximumLevel = 16;

    public static double QuantizeStrength(double value) =>
        Math.Round(
            Math.Clamp(value, 0.0, 1.0) * MaximumLevel,
            MidpointRounding.AwayFromZero)
        / MaximumLevel;
}
