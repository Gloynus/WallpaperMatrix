namespace WallpaperMatrix.Models;

/// <summary>
/// Converts the operator's hue into the vivid single-colour signal used by
/// both glyph bodies and their phosphor halo.
/// </summary>
internal static class SignalColorModel
{
    public const double DefaultHue = 145.0;
    public const double DefaultBrightness = 0.91;
    public const double MaximumBrightness = 2.0;

    public static double NormalizeHue(double hue)
    {
        if (!double.IsFinite(hue))
            return DefaultHue;
        double normalized = hue % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    public static SignalRgb ToRgb(double hue) =>
        ToRgb(hue, DefaultBrightness);

    public static SignalRgb ToRgb(double hue, double brightness) =>
        FromHueBrightness(hue, brightness);

    public static SignalRgb ToBackgroundRgb(double hue, double brightness) =>
        FromHueBrightness(hue, brightness);

    private static SignalRgb FromHueBrightness(double hue, double brightness)
    {
        brightness = Math.Clamp(brightness, 0.0, MaximumBrightness);
        SignalRgb spectrum = FromHsv(
            NormalizeHue(hue),
            saturation: 1.0,
            value: Math.Min(1.0, brightness));
        if (brightness <= 1.0)
            return spectrum;

        double whiteMix = brightness - 1.0;
        return new SignalRgb(
            spectrum.Red + (1.0 - spectrum.Red) * whiteMix,
            spectrum.Green + (1.0 - spectrum.Green) * whiteMix,
            spectrum.Blue + (1.0 - spectrum.Blue) * whiteMix);
    }

    private static SignalRgb FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double sector = hue / 60.0;
        double second = chroma * (1.0 - Math.Abs(sector % 2.0 - 1.0));
        (double red, double green, double blue) = (int)Math.Floor(sector) switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };
        double match = value - chroma;
        return new SignalRgb(red + match, green + match, blue + match);
    }
}

internal readonly record struct SignalRgb(double Red, double Green, double Blue);
