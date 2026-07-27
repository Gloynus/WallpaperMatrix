using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperMatrix.Models;

/// <summary>
/// Immutable BGRA snapshot used only during the in-memory attack transition.
/// It is never written to disk or included in diagnostics.
/// </summary>
internal sealed record CapturedDesktopFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Left,
    int Top)
{
    public CapturedDesktopFrame Crop(System.Drawing.Rectangle bounds)
    {
        int cropLeft = Math.Clamp(bounds.Left - Left, 0, Width);
        int cropTop = Math.Clamp(bounds.Top - Top, 0, Height);
        int cropRight = Math.Clamp(
            bounds.Right - Left,
            cropLeft,
            Width);
        int cropBottom = Math.Clamp(
            bounds.Bottom - Top,
            cropTop,
            Height);
        int cropWidth = cropRight - cropLeft;
        int cropHeight = cropBottom - cropTop;
        if (cropWidth <= 0 || cropHeight <= 0)
            return this;

        int sourceStride = checked(Width * 4);
        int targetStride = checked(cropWidth * 4);
        byte[] cropped = new byte[checked(targetStride * cropHeight)];
        for (int row = 0; row < cropHeight; row++)
        {
            Buffer.BlockCopy(
                Pixels,
                checked((cropTop + row) * sourceStride + cropLeft * 4),
                cropped,
                row * targetStride,
                targetStride);
        }
        return new CapturedDesktopFrame(
            cropped,
            cropWidth,
            cropHeight,
            bounds.Left,
            bounds.Top);
    }

    public ImageSourceFrame ToImageSourceFrame()
    {
        int stride = checked(Width * 4);
        BitmapSource bitmap = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            Pixels,
            stride);
        bitmap.Freeze();
        return new ImageSourceFrame(
            bitmap,
            "memory://attack-system/desktop",
            DateTime.UtcNow,
            Pixels.LongLength);
    }
}
