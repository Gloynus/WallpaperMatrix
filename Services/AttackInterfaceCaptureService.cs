using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Services;

internal sealed record AttackInterfaceFrame(
    byte[] Samples,
    int Width,
    int Height,
    int WindowCount);

/// <summary>
/// Captures one composited frame and keeps only visible top-level interface
/// regions. Desktop and WorkerW surfaces are deliberately absent from the
/// mask, so the Matrix wallpaper can never be photographed and encoded into
/// itself during an attack.
/// </summary>
internal static class AttackInterfaceCaptureService
{
    private const int SampleScale = 8;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;

    public static AttackInterfaceFrame Capture(DrawingRectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                "Windows не сообщила размер виртуального рабочего стола.");
        }

        byte[] desktop = CapturePixels(bounds);
        IReadOnlyList<DrawingRectangle> windows =
            EnumerateInterfaceWindows(bounds);
        int sampleWidth = checked(
            (bounds.Width + SampleScale - 1) / SampleScale);
        int sampleHeight = checked(
            (bounds.Height + SampleScale - 1) / SampleScale);
        byte[] windowMask = new byte[checked(sampleWidth * sampleHeight)];
        foreach (DrawingRectangle window in windows)
            MarkWindow(windowMask, sampleWidth, sampleHeight, bounds, window);

        byte[] samples = new byte[checked(sampleWidth * sampleHeight * 2)];
        int sourceStride = checked(bounds.Width * 4);
        for (int sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            int sourceTop = sampleY * SampleScale;
            int sourceBottom = Math.Min(
                sourceTop + SampleScale,
                bounds.Height);
            for (int sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                int sampleIndex = sampleY * sampleWidth + sampleX;
                if (windowMask[sampleIndex] == 0)
                    continue;

                int sourceLeft = sampleX * SampleScale;
                int sourceRight = Math.Min(
                    sourceLeft + SampleScale,
                    bounds.Width);
                long red = 0;
                long green = 0;
                long blue = 0;
                int count = 0;
                for (int sourceY = sourceTop;
                     sourceY < sourceBottom;
                     sourceY++)
                {
                    int pixel = sourceY * sourceStride + sourceLeft * 4;
                    for (int sourceX = sourceLeft;
                         sourceX < sourceRight;
                         sourceX++)
                    {
                        blue += desktop[pixel];
                        green += desktop[pixel + 1];
                        red += desktop[pixel + 2];
                        pixel += 4;
                        count++;
                    }
                }

                if (count == 0)
                    continue;
                double luminance = (
                    red * 0.2126
                    + green * 0.7152
                    + blue * 0.0722)
                    / (count * 255.0);
                // Slightly lift middle tones: a dark paused movie frame must
                // remain readable without turning broad highlights into a
                // solid white plate.
                luminance = Math.Pow(
                    Math.Clamp((luminance - 0.012) / 0.976, 0.0, 1.0),
                    0.82);
                int output = sampleIndex * 2;
                samples[output] = (byte)Math.Round(luminance * 255.0);
                samples[output + 1] = byte.MaxValue;
            }
        }

        Array.Clear(desktop);
        return new AttackInterfaceFrame(
            samples,
            sampleWidth,
            sampleHeight,
            windows.Count);
    }

    private static byte[] CapturePixels(DrawingRectangle bounds)
    {
        using Bitmap bitmap = new(
            bounds.Width,
            bounds.Height,
            PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr destinationDc = graphics.GetHdc();
            IntPtr sourceDc = IntPtr.Zero;
            try
            {
                sourceDc = GetDC(IntPtr.Zero);
                if (sourceDc == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows не предоставила поверхность рабочего стола.");
                }
                if (!BitBlt(
                        destinationDc,
                        0,
                        0,
                        bounds.Width,
                        bounds.Height,
                        sourceDc,
                        bounds.Left,
                        bounds.Top,
                        SourceCopy | CaptureBlt))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Не удалось получить снимок интерфейса системы.");
                }
            }
            finally
            {
                if (sourceDc != IntPtr.Zero)
                    _ = ReleaseDC(IntPtr.Zero, sourceDc);
                graphics.ReleaseHdc(destinationDc);
            }
        }

        BitmapData data = bitmap.LockBits(
            new DrawingRectangle(0, 0, bounds.Width, bounds.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = checked(bounds.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * bounds.Height)];
            for (int row = 0; row < bounds.Height; row++)
            {
                int sourceRow = data.Stride >= 0
                    ? row
                    : bounds.Height - 1 - row;
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, sourceRow * Math.Abs(data.Stride)),
                    pixels,
                    row * rowBytes,
                    rowBytes);
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static IReadOnlyList<DrawingRectangle> EnumerateInterfaceWindows(
        DrawingRectangle desktopBounds)
    {
        List<DrawingRectangle> result = [];
        _ = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)
                || IsIconic(window)
                || IsCloaked(window))
            {
                return true;
            }

            string className = WindowClass(window);
            if (className is "Progman" or "WorkerW"
                || className.StartsWith(
                    "WallpaperMatrix.NativeWallpaper.",
                    StringComparison.Ordinal)
                || className.StartsWith(
                    "WallpaperMatrix.AttackOverlay.",
                    StringComparison.Ordinal)
                || className.StartsWith(
                    "WallpaperMatrix.VirtualOutput.",
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (!TryGetWindowBounds(window, out DrawingRectangle bounds))
                return true;
            DrawingRectangle clipped = DrawingRectangle.Intersect(
                desktopBounds,
                bounds);
            if (clipped.Width >= 2 && clipped.Height >= 2)
                result.Add(clipped);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void MarkWindow(
        byte[] mask,
        int sampleWidth,
        int sampleHeight,
        DrawingRectangle desktop,
        DrawingRectangle window)
    {
        int left = Math.Clamp(
            (window.Left - desktop.Left) / SampleScale,
            0,
            sampleWidth);
        int top = Math.Clamp(
            (window.Top - desktop.Top) / SampleScale,
            0,
            sampleHeight);
        int right = Math.Clamp(
            (window.Right - desktop.Left + SampleScale - 1) / SampleScale,
            0,
            sampleWidth);
        int bottom = Math.Clamp(
            (window.Bottom - desktop.Top + SampleScale - 1) / SampleScale,
            0,
            sampleHeight);
        for (int y = top; y < bottom; y++)
            Array.Fill(mask, byte.MaxValue, y * sampleWidth + left, right - left);
    }

    private static bool TryGetWindowBounds(
        IntPtr window,
        out DrawingRectangle bounds)
    {
        if (DwmGetWindowAttribute(
                window,
                DwmExtendedFrameBounds,
                out NativeRectangle rectangle,
                Marshal.SizeOf<NativeRectangle>()) != 0
            && !GetWindowRect(window, out rectangle))
        {
            bounds = DrawingRectangle.Empty;
            return false;
        }
        bounds = DrawingRectangle.FromLTRB(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(
            window,
            DwmCloaked,
            out int cloaked,
            sizeof(int)) == 0
        && cloaked != 0;

    private static string WindowClass(IntPtr window)
    {
        StringBuilder name = new(256);
        _ = GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProcedure callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeRectangle rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int maximumCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out NativeRectangle value,
        int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out int value,
        int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(
        IntPtr window,
        IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rasterOperation);
}
