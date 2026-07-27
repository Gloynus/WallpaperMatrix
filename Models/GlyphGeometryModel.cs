namespace WallpaperMatrix.Models;

internal static class GlyphGeometryModel
{
    /// <summary>
    /// Maps the operator scale to the requested anchors: -99 is one percent of
    /// the natural height, 0 is natural height, and 200 is twice natural.
    /// </summary>
    public static double HeightScale(double stretch) =>
        stretch <= 0
            ? 1.0 + Math.Clamp(stretch, -99.0, 0.0) / 100.0
            : 1.0 + Math.Clamp(stretch, 0.0, 200.0) / 200.0;
}
