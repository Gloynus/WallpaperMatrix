namespace WallpaperMatrix.Models;

/// <summary>
/// Non-destructive controls layered over a hand-edited distribution curve.
/// The stored points remain intact, so an operator can return every modifier
/// to zero without accumulating interpolation or rounding error.
/// </summary>
public sealed class CurveAdjustment
{
    public double Character { get; set; }
    public double HorizontalShift { get; set; }
    public double VerticalShift { get; set; }

    public CurveAdjustment Copy() => new()
    {
        Character = Character,
        HorizontalShift = HorizontalShift,
        VerticalShift = VerticalShift
    };

    public void Normalize()
    {
        Character = Math.Clamp(Character, -1.0, 1.0);
        HorizontalShift = Math.Clamp(HorizontalShift, -1.0, 1.0);
        VerticalShift = Math.Clamp(VerticalShift, -1.0, 1.0);
    }
}
