using System.Runtime.InteropServices;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;

namespace WallpaperMatrix.Rendering;

/// <summary>
/// Rasterizes the selected Windows font only when font settings change.
/// Per-frame rendering is entirely Direct3D; GDI is only a font-atlas factory.
/// </summary>
internal static class GlyphAtlasBuilder
{
    private const int StyleCount = 5;
    private const int OriginalFontWeight = 400;
    private const uint MarkMissingGlyphs = 1;
    private const uint GdiError = 0xFFFFFFFF;
    private static readonly string[] DefaultFallbackFamilies =
    [
        "MS Gothic",
        "Yu Gothic UI",
        "Meiryo UI",
        "Segoe UI Symbol"
    ];

    public static GlyphAtlasData Build(
        IntPtr referenceWindow,
        AppSettings settings,
        int cellWidth,
        int cellHeight,
        double dpiScale)
    {
        if (settings.FontSize <= 1.01)
            return BuildPointAtlas(cellWidth, cellHeight);

        int glyphCount = MatrixGlyphSet.GlyphStrings.Length;
        int width = checked(cellWidth * glyphCount);
        int height = checked(cellHeight * StyleCount);
        IntPtr referenceDc = NativeMethods.GetDC(referenceWindow);
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;
        IntPtr brush = IntPtr.Zero;
        List<FontStyleSet> fonts = [];
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(referenceDc);
            NativeMethods.BitmapInfo bitmapInfo = new()
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BitmapRgb,
                    ImageSize = (uint)(width * height * 4L)
                }
            };
            bitmap = NativeMethods.CreateDIBSection(
                referenceDc,
                ref bitmapInfo,
                NativeMethods.RgbColors,
                out IntPtr pixels,
                IntPtr.Zero,
                0);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero || pixels == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось создать атлас символов.");

            previousBitmap = NativeMethods.SelectObject(memoryDc, bitmap);
            brush = NativeMethods.CreateSolidBrush(0);
            NativeMethods.NativeRect bounds = new() { Left = 0, Top = 0, Right = width, Bottom = height };
            NativeMethods.FillRect(memoryDc, ref bounds, brush);
            NativeMethods.SetBkMode(memoryDc, NativeMethods.TransparentBackground);
            NativeMethods.SetTextAlign(memoryDc, NativeMethods.TextAlignLeftTop);
            NativeMethods.SetTextColor(memoryDc, 0x00FFFFFF);

            int naturalPixelHeight = Math.Max(
                1,
                (int)Math.Round(settings.FontSize * dpiScale));
            int pixelHeight = Math.Max(
                1,
                (int)Math.Round(
                    naturalPixelHeight
                    * GlyphGeometryModel.HeightScale(settings.GlyphStretch)));
            int pixelWidth = Math.Max(1, (int)Math.Round(naturalPixelHeight * 0.82));
            int baseWeight = settings.GlyphWeight < 0
                ? OriginalFontWeight
                    + (int)Math.Round(settings.GlyphWeight * 300)
                : OriginalFontWeight
                    + (int)Math.Round(settings.GlyphWeight * 500);
            int headWeight = Math.Clamp(
                baseWeight + (int)Math.Round(settings.HeadWeight * 300),
                100,
                900);
            int clockWeight = Math.Clamp(
                baseWeight + (int)Math.Round(settings.ClockWeight * 300),
                100,
                900);
            int imageLightWeight = Math.Clamp(baseWeight - 200, 100, 900);
            int imageBoldWeight = Math.Clamp(baseWeight + 200, 100, 900);
            foreach (string family in BuildFontChain(settings.FontFamily))
            {
                FontStyleSet set = new(
                    CreateFont(pixelHeight, pixelWidth, baseWeight, family),
                    CreateFont(pixelHeight, pixelWidth, headWeight, family),
                    CreateFont(pixelHeight, pixelWidth, clockWeight, family),
                    CreateFont(pixelHeight, pixelWidth, imageLightWeight, family),
                    CreateFont(pixelHeight, pixelWidth, imageBoldWeight, family));
                if (set.Normal != IntPtr.Zero
                    && set.Head != IntPtr.Zero
                    && set.Clock != IntPtr.Zero
                    && set.ImageLight != IntPtr.Zero
                    && set.ImageBold != IntPtr.Zero)
                {
                    fonts.Add(set);
                }
                else
                {
                    DeleteFontSet(set);
                }
            }
            if (fonts.Count == 0)
                throw new InvalidOperationException("Windows не смогла создать выбранный шрифт.");

            int[] glyphFontIndices = SelectFontForEachGlyph(memoryDc, fonts);
            DrawStyle(
                memoryDc,
                fonts.Select(set => set.Normal).ToArray(),
                glyphFontIndices,
                style: 0,
                cellWidth,
                cellHeight);
            DrawStyle(
                memoryDc,
                fonts.Select(set => set.Head).ToArray(),
                glyphFontIndices,
                style: 1,
                cellWidth,
                cellHeight);
            DrawStyle(
                memoryDc,
                fonts.Select(set => set.Clock).ToArray(),
                glyphFontIndices,
                style: 2,
                cellWidth,
                cellHeight);
            DrawStyle(
                memoryDc,
                fonts.Select(set => set.ImageLight).ToArray(),
                glyphFontIndices,
                style: 3,
                cellWidth,
                cellHeight);
            DrawStyle(
                memoryDc,
                fonts.Select(set => set.ImageBold).ToArray(),
                glyphFontIndices,
                style: 4,
                cellWidth,
                cellHeight);
            NativeMethods.GdiFlush();

            byte[] bgra = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, bgra, 0, bgra.Length);
            byte[] alpha = new byte[checked(width * height)];
            for (int source = 0, destination = 0; destination < alpha.Length; source += 4, destination++)
            {
                alpha[destination] = Math.Max(
                    bgra[source],
                    Math.Max(bgra[source + 1], bgra[source + 2]));
            }
            ApplyGlobalWeight(
                alpha,
                width,
                cellWidth,
                cellHeight,
                glyphCount,
                settings.GlyphWeight,
                pixelHeight);
            float[] inkCoverage = MeasureInkCoverage(
                alpha,
                width,
                cellWidth,
                cellHeight,
                glyphCount);
            return new GlyphAtlasData(
                alpha,
                width,
                height,
                cellWidth,
                cellHeight,
                glyphCount,
                StyleCount,
                inkCoverage);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero)
                NativeMethods.SelectObject(memoryDc, previousBitmap);
            foreach (FontStyleSet set in fonts)
                DeleteFontSet(set);
            if (brush != IntPtr.Zero)
                NativeMethods.DeleteObject(brush);
            if (bitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero)
                NativeMethods.DeleteDC(memoryDc);
            if (referenceDc != IntPtr.Zero)
                NativeMethods.ReleaseDC(referenceWindow, referenceDc);
        }
    }

    private static void ApplyGlobalWeight(
        byte[] alpha,
        int atlasWidth,
        int cellWidth,
        int cellHeight,
        int glyphCount,
        double weight,
        int pixelHeight)
    {
        if (Math.Abs(weight) < 0.001)
            return;

        bool thicken = weight > 0;
        int radius = thicken
            ? Math.Clamp(1 + pixelHeight / 18, 1, 3)
            : 1;
        double amount = thicken
            ? Math.Pow(Math.Clamp(weight, 0.0, 1.0), 0.72)
            : Math.Pow(Math.Clamp(-weight, 0.0, 1.0), 0.78) * 0.80;
        byte[] source = alpha.ToArray();
        for (int style = 0; style < StyleCount; style++)
        {
            int styleTop = style * cellHeight;
            for (int glyph = 0; glyph < glyphCount; glyph++)
            {
                int glyphLeft = glyph * cellWidth;
                for (int localY = 0; localY < cellHeight; localY++)
                {
                    int y = styleTop + localY;
                    for (int localX = 0; localX < cellWidth; localX++)
                    {
                        int x = glyphLeft + localX;
                        int sample = source[y * atlasWidth + x];
                        int extreme = sample;
                        for (int offsetY = -radius; offsetY <= radius; offsetY++)
                        {
                            int neighborY = localY + offsetY;
                            if (neighborY < 0 || neighborY >= cellHeight)
                            {
                                if (!thicken)
                                    extreme = 0;
                                continue;
                            }
                            int atlasY = styleTop + neighborY;
                            for (int offsetX = -radius; offsetX <= radius; offsetX++)
                            {
                                int neighborX = localX + offsetX;
                                int neighbor = neighborX < 0 || neighborX >= cellWidth
                                    ? 0
                                    : source[atlasY * atlasWidth + glyphLeft + neighborX];
                                extreme = thicken
                                    ? Math.Max(extreme, neighbor)
                                    : Math.Min(extreme, neighbor);
                            }
                        }
                        alpha[y * atlasWidth + x] = (byte)Math.Clamp(
                            (int)Math.Round(sample + (extreme - sample) * amount),
                            0,
                            255);
                    }
                }
            }
        }
    }

    private static GlyphAtlasData BuildPointAtlas(int cellWidth, int cellHeight)
    {
        int glyphCount = MatrixGlyphSet.GlyphStrings.Length;
        int width = checked(cellWidth * glyphCount);
        int height = checked(cellHeight * StyleCount);
        byte[] alpha = new byte[checked(width * height)];
        float[] coverage = new float[StyleCount * glyphCount];
        int centerX = cellWidth / 2;
        int centerY = cellHeight / 2;
        for (int style = 0; style < StyleCount; style++)
        {
            byte value = style == 3 ? (byte)184 : (byte)255;
            for (int glyph = 0; glyph < glyphCount; glyph++)
            {
                int x = glyph * cellWidth + centerX;
                int y = style * cellHeight + centerY;
                alpha[y * width + x] = value;
                coverage[style * glyphCount + glyph] =
                    value / (255f * cellWidth * cellHeight);
            }
        }
        return new GlyphAtlasData(
            alpha,
            width,
            height,
            cellWidth,
            cellHeight,
            glyphCount,
            StyleCount,
            coverage);
    }

    private static void DrawStyle(
        IntPtr dc,
        IReadOnlyList<IntPtr> fonts,
        IReadOnlyList<int> glyphFontIndices,
        int style,
        int cellWidth,
        int cellHeight)
    {
        IntPtr currentFont = fonts[0];
        IntPtr previousFont = NativeMethods.SelectObject(dc, currentFont);
        try
        {
            int cellTop = style * cellHeight;
            for (int glyphIndex = 0; glyphIndex < MatrixGlyphSet.GlyphStrings.Length; glyphIndex++)
            {
                IntPtr glyphFont = fonts[glyphFontIndices[glyphIndex]];
                if (glyphFont != currentFont)
                {
                    NativeMethods.SelectObject(dc, glyphFont);
                    currentFont = glyphFont;
                }

                int cellLeft = glyphIndex * cellWidth;
                int saved = NativeMethods.SaveDC(dc);
                NativeMethods.IntersectClipRect(
                    dc,
                    cellLeft,
                    cellTop,
                    cellLeft + cellWidth,
                    cellTop + cellHeight);
                string glyph = MatrixGlyphSet.GlyphStrings[glyphIndex];
                NativeMethods.GetTextExtentPoint32(dc, glyph, glyph.Length, out NativeMethods.NativeSize glyphSize);
                int x = cellLeft + Math.Max(0, (cellWidth - glyphSize.Width) / 2);
                int y = cellTop + Math.Max(0, (cellHeight - glyphSize.Height) / 2);
                NativeMethods.TextOut(dc, x, y, glyph, glyph.Length);
                if (saved != 0)
                    NativeMethods.RestoreDC(dc, saved);
            }
        }
        finally
        {
            if (previousFont != IntPtr.Zero)
                NativeMethods.SelectObject(dc, previousFont);
        }
    }

    private static int[] SelectFontForEachGlyph(
        IntPtr dc,
        IReadOnlyList<FontStyleSet> fonts)
    {
        int[] selectedFonts = new int[MatrixGlyphSet.GlyphStrings.Length];
        for (int glyphIndex = 0; glyphIndex < MatrixGlyphSet.GlyphStrings.Length; glyphIndex++)
        {
            string glyph = MatrixGlyphSet.GlyphStrings[glyphIndex];
            for (int fontIndex = 0; fontIndex < fonts.Count; fontIndex++)
            {
                if (!ContainsGlyph(dc, fonts[fontIndex].Normal, glyph))
                    continue;
                selectedFonts[glyphIndex] = fontIndex;
                break;
            }
        }
        return selectedFonts;
    }

    private static float[] MeasureInkCoverage(
        byte[] alpha,
        int atlasWidth,
        int cellWidth,
        int cellHeight,
        int glyphCount)
    {
        float[] coverage = new float[StyleCount * glyphCount];
        double scale = 1.0 / (255.0 * cellWidth * cellHeight);
        for (int style = 0; style < StyleCount; style++)
        {
            int styleTop = style * cellHeight;
            for (int glyph = 0; glyph < glyphCount; glyph++)
            {
                long ink = 0;
                int glyphLeft = glyph * cellWidth;
                for (int y = 0; y < cellHeight; y++)
                {
                    int pixel = (styleTop + y) * atlasWidth + glyphLeft;
                    for (int x = 0; x < cellWidth; x++)
                        ink += alpha[pixel + x];
                }
                coverage[style * glyphCount + glyph] = (float)(ink * scale);
            }
        }
        return coverage;
    }

    private static bool ContainsGlyph(IntPtr dc, IntPtr font, string glyph)
    {
        IntPtr previousFont = NativeMethods.SelectObject(dc, font);
        try
        {
            ushort[] indices = new ushort[1];
            uint result = NativeMethods.GetGlyphIndices(
                dc,
                glyph,
                glyph.Length,
                indices,
                MarkMissingGlyphs);
            return result != GdiError && indices[0] != ushort.MaxValue;
        }
        finally
        {
            if (previousFont != IntPtr.Zero)
                NativeMethods.SelectObject(dc, previousFont);
        }
    }

    private static IEnumerable<string> BuildFontChain(string selectedFamily)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selectedFamily) && seen.Add(selectedFamily))
            yield return selectedFamily;
        foreach (string family in DefaultFallbackFamilies)
        {
            if (seen.Add(family))
                yield return family;
        }
    }

    private static void DeleteFontSet(FontStyleSet set)
    {
        if (set.Normal != IntPtr.Zero)
            NativeMethods.DeleteObject(set.Normal);
        if (set.Head != IntPtr.Zero)
            NativeMethods.DeleteObject(set.Head);
        if (set.Clock != IntPtr.Zero)
            NativeMethods.DeleteObject(set.Clock);
        if (set.ImageLight != IntPtr.Zero)
            NativeMethods.DeleteObject(set.ImageLight);
        if (set.ImageBold != IntPtr.Zero)
            NativeMethods.DeleteObject(set.ImageBold);
    }

    private static IntPtr CreateFont(
        int pixelHeight,
        int pixelWidth,
        int weight,
        string fontFamily) =>
        NativeMethods.CreateFont(
            -pixelHeight,
            pixelWidth,
            0,
            0,
            weight,
            0,
            0,
            0,
            NativeMethods.DefaultCharset,
            NativeMethods.OutTrueTypePrecision,
            NativeMethods.ClipDefaultPrecision,
            NativeMethods.AntialiasedQuality,
            NativeMethods.FixedPitch,
            fontFamily);

    private readonly record struct FontStyleSet(
        IntPtr Normal,
        IntPtr Head,
        IntPtr Clock,
        IntPtr ImageLight,
        IntPtr ImageBold);
}
