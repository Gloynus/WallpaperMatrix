using System.Runtime.InteropServices;

namespace WallpaperMatrix.Native;

internal static class NativeMethods
{
    public const int TransparentBackground = 1;
    public const uint TextAlignLeftTop = 0;
    public const uint DefaultCharset = 1;
    public const uint OutTrueTypePrecision = 4;
    public const uint ClipDefaultPrecision = 0;
    public const uint AntialiasedQuality = 4;
    public const uint FixedPitch = 1;
    public const uint RgbColors = 0;
    public const uint BitmapRgb = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr dc, ref NativeRect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(
        IntPtr dc,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(int colorRef);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextAlign(IntPtr dc, uint align);

    [DllImport("gdi32.dll")]
    public static extern int SetTextColor(IntPtr dc, int colorRef);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetGlyphIndicesW")]
    public static extern uint GetGlyphIndices(
        IntPtr dc,
        string text,
        int count,
        [Out] ushort[] glyphIndices,
        uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTextExtentPoint32(
        IntPtr dc,
        string text,
        int count,
        out NativeSize size);

    [DllImport("gdi32.dll")]
    public static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    public static extern int SaveDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern bool RestoreDC(IntPtr dc, int savedDc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    public static extern IntPtr CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);
}
