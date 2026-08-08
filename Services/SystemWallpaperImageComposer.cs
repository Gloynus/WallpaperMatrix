using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

/// <summary>
/// Materializes the wallpaper exactly as Windows lays it out on one output.
/// The resulting bitmap already has the output aspect ratio, so database
/// Fit/Fill settings cannot shift the transient zero image at startup.
/// </summary>
internal static class SystemWallpaperImageComposer
{
    public static ImageSourceFrame Compose(
        string path,
        int targetWidth,
        int targetHeight,
        DesktopWallpaperPosition position,
        uint backgroundColor)
    {
        FileInfo file = new(path);
        BitmapSource source = LoadOriginal(file.FullName);
        int width = Math.Max(1, targetWidth);
        int height = Math.Max(1, targetHeight);
        DrawingVisual visual = new();
        RenderOptions.SetBitmapScalingMode(
            visual,
            BitmapScalingMode.HighQuality);
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                new SolidColorBrush(ColorFromColorRef(backgroundColor)),
                null,
                new Rect(0, 0, width, height));
            DrawWallpaper(drawing, source, width, height, position);
        }

        RenderTargetBitmap bitmap = new(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new ImageSourceFrame(
            bitmap,
            file.FullName,
            file.LastWriteTimeUtc,
            file.Length);
    }

    private static BitmapSource LoadOriginal(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        source.Freeze();
        return source;
    }

    private static void DrawWallpaper(
        DrawingContext drawing,
        BitmapSource source,
        int targetWidth,
        int targetHeight,
        DesktopWallpaperPosition position)
    {
        double sourceWidth = Math.Max(1, source.PixelWidth);
        double sourceHeight = Math.Max(1, source.PixelHeight);
        if (position == DesktopWallpaperPosition.Tile)
        {
            for (double y = 0; y < targetHeight; y += sourceHeight)
            {
                for (double x = 0; x < targetWidth; x += sourceWidth)
                {
                    drawing.DrawImage(
                        source,
                        new Rect(x, y, sourceWidth, sourceHeight));
                }
            }
            return;
        }

        Rect destination;
        switch (position)
        {
            case DesktopWallpaperPosition.Stretch:
                destination = new Rect(0, 0, targetWidth, targetHeight);
                break;
            case DesktopWallpaperPosition.Center:
                destination = Centered(
                    targetWidth,
                    targetHeight,
                    sourceWidth,
                    sourceHeight);
                break;
            case DesktopWallpaperPosition.Fit:
                destination = Scaled(
                    targetWidth,
                    targetHeight,
                    sourceWidth,
                    sourceHeight,
                    fill: false);
                break;
            case DesktopWallpaperPosition.Fill:
            case DesktopWallpaperPosition.Span:
            default:
                destination = Scaled(
                    targetWidth,
                    targetHeight,
                    sourceWidth,
                    sourceHeight,
                    fill: true,
                    windowsFillAlignment: true);
                break;
        }
        drawing.DrawImage(source, destination);
    }

    private static Rect Centered(
        double targetWidth,
        double targetHeight,
        double width,
        double height) =>
        new(
            (targetWidth - width) * 0.5,
            (targetHeight - height) * 0.5,
            width,
            height);

    private static Rect Scaled(
        double targetWidth,
        double targetHeight,
        double sourceWidth,
        double sourceHeight,
        bool fill,
        bool windowsFillAlignment = false)
    {
        double scaleX = targetWidth / sourceWidth;
        double scaleY = targetHeight / sourceHeight;
        double scale = fill
            ? Math.Max(scaleX, scaleY)
            : Math.Min(scaleX, scaleY);
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        double left = (targetWidth - width) * 0.5;
        // Explorer's Fill renderer biases a vertical crop toward the upper
        // third instead of centring it. Matching that rule prevents the zero
        // image from sliding upward while new streams replace the desktop.
        double top = windowsFillAlignment && height > targetHeight
            ? (targetHeight - height) / 3.0
            : (targetHeight - height) * 0.5;
        return new Rect(left, top, width, height);
    }

    private static System.Windows.Media.Color ColorFromColorRef(uint color) =>
        System.Windows.Media.Color.FromRgb(
            (byte)(color & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)((color >> 16) & 0xFF));
}
