namespace WallpaperMatrix.Models;

/// <summary>
/// Describes which canvas axes an image must fill while preserving its
/// aspect ratio. The four flag combinations cover contain, width-bound,
/// height-bound, and cover placement without separate rendering modes.
/// </summary>
public sealed class ImagePlacement
{
    public bool FillHorizontal { get; set; } = true;
    public bool FillVertical { get; set; } = true;

    public ImagePlacement Copy() => new()
    {
        FillHorizontal = FillHorizontal,
        FillVertical = FillVertical
    };

    public double ResolveScale(double scaleX, double scaleY) =>
        (FillHorizontal, FillVertical) switch
        {
            (true, true) => Math.Max(scaleX, scaleY),
            (true, false) => scaleX,
            (false, true) => scaleY,
            _ => Math.Min(scaleX, scaleY)
        };

    public bool Equivalent(ImagePlacement? other) =>
        other is not null
        && FillHorizontal == other.FillHorizontal
        && FillVertical == other.FillVertical;

    public static ImagePlacement FromLegacy(string? imageFit) =>
        string.Equals(imageFit, "Fill", StringComparison.OrdinalIgnoreCase)
            ? new ImagePlacement()
            : new ImagePlacement
            {
                FillHorizontal = false,
                FillVertical = false
            };
}
