using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WallpaperMatrix.Models;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Services;

/// <summary>
/// Takes one composited desktop snapshot for the attack transition. Continuous
/// recording is deliberately avoided: one frame is enough for the takeover
/// effect and keeps both privacy exposure and GPU/CPU work bounded.
/// </summary>
internal static class DesktopCaptureService
{
    public static CapturedDesktopFrame CaptureVirtualDesktop()
    {
        DrawingRectangle bounds =
            System.Windows.Forms.SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                "Windows не сообщила размер виртуального рабочего стола.");
        }

        using Bitmap bitmap = new(
            bounds.Width,
            bounds.Height,
            PixelFormat.Format32bppArgb);
        _ = DwmFlush();
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

        DrawingRectangle localBounds =
            new(0, 0, bounds.Width, bounds.Height);
        BitmapData data = bitmap.LockBits(
            localBounds,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = checked(bounds.Width * 4);
            byte[] pixels =
                new byte[checked(rowBytes * bounds.Height)];
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(
                    data.Scan0,
                    pixels,
                    0,
                    pixels.Length);
            }
            else
            {
                for (int row = 0; row < bounds.Height; row++)
                {
                    int sourceRow = data.Stride >= 0
                        ? row
                        : bounds.Height - 1 - row;
                    Marshal.Copy(
                        IntPtr.Add(
                            data.Scan0,
                            sourceRow * Math.Abs(data.Stride)),
                        pixels,
                        row * rowBytes,
                        rowBytes);
                }
            }

            return new CapturedDesktopFrame(
                pixels,
                bounds.Width,
                bounds.Height,
                bounds.Left,
                bounds.Top);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
