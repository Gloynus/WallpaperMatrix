using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Views;

/// <summary>
/// A deterministic, non-animated terminal sample. It mirrors the selected
/// typeface, geometry, weight and colours without starting a second renderer.
/// </summary>
internal sealed class TerminalPreview : FrameworkElement
{
    private const string Glyphs =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾊﾋﾌﾍﾎ";

    private string _fontFamily = "MS Gothic";
    private double _fontSize = 30;
    private double _stretch;
    private double _weight;
    private double _signalHue = SignalColorModel.DefaultHue;
    private double _signalBrightness = SignalColorModel.DefaultBrightness;
    private double _backgroundHue = SignalColorModel.DefaultHue;
    private double _backgroundBrightness = 0.03;

    public void SetParameters(
        string fontFamily,
        double fontSize,
        double stretch,
        double weight,
        double signalHue,
        double signalBrightness,
        double backgroundHue,
        double backgroundBrightness)
    {
        _fontFamily = string.IsNullOrWhiteSpace(fontFamily)
            ? "MS Gothic"
            : fontFamily;
        _fontSize = Math.Clamp(fontSize, 1.0, 48.0);
        _stretch = Math.Clamp(stretch, -99.0, 200.0);
        _weight = Math.Clamp(weight, -1.0, 1.0);
        _signalHue = SignalColorModel.NormalizeHue(signalHue);
        _signalBrightness = Math.Clamp(
            signalBrightness,
            0.0,
            SignalColorModel.MaximumBrightness);
        _backgroundHue = SignalColorModel.NormalizeHue(backgroundHue);
        _backgroundBrightness = Math.Clamp(
            backgroundBrightness,
            0.0,
            SignalColorModel.MaximumBrightness);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect bounds = new(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        SignalRgb background = SignalColorModel.ToBackgroundRgb(
            _backgroundHue,
            _backgroundBrightness);
        SignalRgb signal = SignalColorModel.ToRgb(
            _signalHue,
            _signalBrightness);
        drawingContext.DrawRectangle(
            Brush(background, 255),
            null,
            bounds);

        // WPF font sizes are device-independent pixels and are DPI-scaled by
        // the framework, exactly as the native atlas scales the configured
        // value. Do not shrink the sample to fit: the number of visible rows
        // is part of the preview and must match the real terminal density.
        double previewSize = _fontSize;
        double heightScale = GlyphGeometryModel.HeightScale(_stretch);
        double rowHeight = Math.Max(1.0, previewSize * 1.04 * heightScale);
        double columnWidth = Math.Max(3.0, previewSize * 0.92);
        int columns = Math.Max(1, (int)Math.Ceiling(bounds.Width / columnWidth));
        int rows = Math.Max(1, (int)Math.Ceiling(bounds.Height / rowHeight));

        if (_fontSize <= 1.5)
        {
            DrawDots(drawingContext, signal, columns, rows, columnWidth, rowHeight);
            return;
        }

        Typeface typeface = CreateTypeface();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawingContext.PushClip(new RectangleGeometry(bounds));
        for (int column = 0; column < columns; column++)
        {
            int head = 1 + PositiveHash(column, 17) % Math.Max(2, rows + 2);
            int trail = 4 + PositiveHash(column, 31) % Math.Max(4, rows + 3);
            for (int row = 0; row < rows; row++)
            {
                int distance = head - row;
                if (distance < 0 || distance >= trail)
                    continue;

                double fade = 1.0 - distance / (double)Math.Max(1, trail);
                byte alpha = distance == 0
                    ? (byte)255
                    : (byte)Math.Clamp(28 + fade * 190, 0, 235);
                SignalRgb glyphColor = distance == 0
                    ? Mix(signal, new SignalRgb(1, 1, 1), 0.76)
                    : signal;
                char glyph = Glyphs[
                    PositiveHash(column * 131 + row * 47, 73) % Glyphs.Length];
                FormattedText text = new(
                    glyph.ToString(),
                    CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    previewSize,
                    Brush(glyphColor, alpha),
                    pixelsPerDip);
                double x = column * columnWidth
                    + Math.Max(0, (columnWidth - text.WidthIncludingTrailingWhitespace) * 0.5);
                double y = row * rowHeight;
                drawingContext.PushTransform(
                    new ScaleTransform(1.0, heightScale, x, y));
                drawingContext.DrawText(text, new System.Windows.Point(x, y));
                drawingContext.Pop();
            }
        }
        drawingContext.Pop();
    }

    private void DrawDots(
        DrawingContext drawingContext,
        SignalRgb signal,
        int columns,
        int rows,
        double columnWidth,
        double rowHeight)
    {
        for (int column = 0; column < columns; column++)
        {
            int head = PositiveHash(column, 19) % Math.Max(1, rows);
            for (int row = 0; row <= head; row++)
            {
                double fade = (row + 1.0) / (head + 1.0);
                byte alpha = (byte)Math.Clamp(35 + fade * 220, 0, 255);
                drawingContext.DrawRectangle(
                    Brush(signal, alpha),
                    null,
                    new Rect(
                        column * columnWidth,
                        row * rowHeight,
                        1,
                        1));
            }
        }
    }

    private Typeface CreateTypeface()
    {
        int openTypeWeight = _weight < 0
            ? 400 + (int)Math.Round(_weight * 300)
            : 400 + (int)Math.Round(_weight * 500);
        try
        {
            return new Typeface(
                new System.Windows.Media.FontFamily(_fontFamily),
                FontStyles.Normal,
                FontWeight.FromOpenTypeWeight(Math.Clamp(openTypeWeight, 100, 900)),
                FontStretches.Normal);
        }
        catch
        {
            return new Typeface(
                new System.Windows.Media.FontFamily("MS Gothic"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal);
        }
    }

    private static SolidColorBrush Brush(SignalRgb color, byte alpha)
    {
        System.Windows.Media.Color mediaColor = System.Windows.Media.Color.FromArgb(
            alpha,
            Channel(color.Red),
            Channel(color.Green),
            Channel(color.Blue));
        SolidColorBrush brush = new(mediaColor);
        brush.Freeze();
        return brush;
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static SignalRgb Mix(SignalRgb first, SignalRgb second, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return new SignalRgb(
            first.Red + (second.Red - first.Red) * amount,
            first.Green + (second.Green - first.Green) * amount,
            first.Blue + (second.Blue - first.Blue) * amount);
    }

    private static int PositiveHash(int value, int salt)
    {
        uint hash = (uint)(value + salt * 0x9E37);
        hash ^= hash >> 16;
        hash *= 0x7FEB352D;
        hash ^= hash >> 15;
        return (int)(hash & 0x7FFFFFFF);
    }
}
