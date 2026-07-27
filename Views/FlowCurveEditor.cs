using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WallpaperMatrix.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace WallpaperMatrix.Views;

public sealed class FlowCurveEditor : FrameworkElement
{
    private static readonly Brush PanelBrush = FrozenBrush(3, 14, 8);
    private static readonly Brush GraphBrush = FrozenBrush(5, 25, 13);
    private static readonly Brush PointBrush = FrozenBrush(158, 255, 190);
    private static readonly Brush MutedBrush = FrozenBrush(91, 145, 108);
    private static readonly Pen BorderPen = FrozenPen(22, 75, 44, 1);
    private static readonly Pen GridPen = FrozenPen(18, 62, 34, 1);
    private static readonly Pen CurvePen = FrozenPen(0, 230, 103, 2);
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface CodeTypeface = new("MS Gothic");
    private static readonly Typeface CodeSemiBoldTypeface = new(
        new System.Windows.Media.FontFamily("MS Gothic"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);
    private static readonly Typeface CodeBoldTypeface = new(
        new System.Windows.Media.FontFamily("MS Gothic"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);
    private static readonly char[] PreviewGlyphs =
        "ﾊﾐﾋｰｳｼﾅﾓﾆｻﾜﾂｵﾘ0123456789ZX".ToCharArray();
    private List<CurvePoint> _points = FlowCurveProfiles.DefaultLength();
    private string _curveKind = FlowCurveProfiles.LengthKind;
    private int _dragIndex = -1;
    private int _selectedIndex = -1;
    private double _trailMin = 0.22;
    private double _trailMax = 0.28;
    private double _density = 0.56;
    private double _interception = 0.45;
    private double _filterMin = 1.0;
    private double _filterMax = 1.0;
    private double _speedMin = 0.20;
    private double _speedMax = 1.0;
    private double _memoryMin = 0.30;
    private double _memoryMax = 0.30;
    private double _signalMin = 1.0;
    private double _signalMax = 1.0;
    private double _signalGlowKeys = 1.0;
    private double _signalGlowPriority = 1.0;
    private double _headBrightness = 0.72;
    private double _headWeight = 0.62;
    private double _headGlow = 1.0;
    private double _headImpulseDecay = 0.1;
    private double _headImpulseProbability = 1.0;
    private SignalRgb _signalColor = SignalColorModel.ToRgb(
        SignalColorModel.DefaultHue,
        SignalColorModel.DefaultBrightness);

    public event EventHandler? CurveChanged;

    public FlowCurveEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        ToolTip = "ЛКМ по полю создаёт точку; перетаскивание меняет кривую. "
            + "ПКМ по внутренней точке удаляет её. Крайние точки закреплены.";
    }

    public string CurveKind => _curveKind;

    public IReadOnlyList<CurvePoint> Points => _points;

    public void SetCurve(
        string curveKind,
        IReadOnlyList<CurvePoint>? points)
    {
        _curveKind = FlowCurveProfiles.IsSupported(curveKind)
            ? curveKind
            : FlowCurveProfiles.LengthKind;
        Cursor = _curveKind == FlowCurveProfiles.HeadPulseKind
            ? Cursors.Arrow
            : Cursors.Cross;
        _points = FlowCurveMath.Normalize(
            points,
            increasing: FlowCurveProfiles.IsIncreasing(_curveKind));
        _selectedIndex = -1;
        _dragIndex = -1;
        InvalidateVisual();
    }

    public List<CurvePoint> CopyCurve() =>
        _points.Select(point => point.Copy()).ToList();

    public void SetPreviewParameters(
        double trailMin,
        double trailMax,
        double density,
        double interception,
        double filterMin,
        double filterMax,
        double speedMin,
        double speedMax,
        double memoryMin,
        double memoryMax,
        double signalMin,
        double signalMax,
        double signalGlowKeys,
        double signalGlowPriority,
        double headBrightness,
        double headWeight,
        double headGlow,
        double headImpulseDecay,
        double headImpulseProbability,
        double signalHue,
        double signalBrightness)
    {
        _trailMin = Math.Clamp(trailMin, 0.0, 1.0);
        _trailMax = Math.Clamp(trailMax, _trailMin, 1.0);
        _density = Math.Clamp(density, 0.05, 1.0);
        _interception = Math.Clamp(interception, 0.0, 1.0);
        _filterMin = Math.Clamp(filterMin, 0.01, 1.0);
        _filterMax = Math.Clamp(filterMax, _filterMin, 1.0);
        _speedMin = Math.Clamp(speedMin, 0.01, 1.0);
        _speedMax = Math.Clamp(speedMax, _speedMin, 1.0);
        _memoryMin = Math.Clamp(
            memoryMin,
            0.0,
            TrailMemoryModel.MaximumDuration);
        _memoryMax = Math.Clamp(
            memoryMax,
            _memoryMin,
            TrailMemoryModel.MaximumDuration);
        _signalMin = Math.Clamp(signalMin, 0.0, 1.0);
        _signalMax = Math.Clamp(signalMax, _signalMin, 1.0);
        _signalGlowKeys = Math.Clamp(signalGlowKeys, 0.0, 1.0);
        _signalGlowPriority = Math.Clamp(signalGlowPriority, 0.0, 2.0);
        _headBrightness = Math.Clamp(headBrightness, 0.0, 1.0);
        _headWeight = Math.Clamp(headWeight, 0.0, 1.0);
        _headGlow = Math.Clamp(headGlow, 0.0, 2.0);
        _headImpulseDecay = Math.Clamp(
            headImpulseDecay,
            0.0,
            HeadImpulseModel.MaximumDecay);
        _headImpulseProbability = Math.Clamp(
            headImpulseProbability,
            0.0,
            1.0);
        _signalColor = SignalColorModel.ToRgb(
            signalHue,
            Math.Clamp(
                signalBrightness,
                0.0,
                SignalColorModel.MaximumBrightness));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect bounds = new(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
        drawingContext.DrawRoundedRectangle(PanelBrush, BorderPen, bounds, 5, 5);
        if (ActualWidth < 180 || ActualHeight < 220)
            return;

        Rect graph = GraphRect();
        drawingContext.DrawRoundedRectangle(GraphBrush, BorderPen, graph, 3, 3);
        for (int step = 1; step < 4; step++)
        {
            double x = graph.Left + graph.Width * step / 4.0;
            double y = graph.Top + graph.Height * step / 4.0;
            drawingContext.DrawLine(GridPen, new Point(x, graph.Top), new Point(x, graph.Bottom));
            drawingContext.DrawLine(GridPen, new Point(graph.Left, y), new Point(graph.Right, y));
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double[] lookup = FlowCurveMath.BuildLookup(
            _points,
            increasing: FlowCurveProfiles.IsIncreasing(_curveKind));
        drawingContext.PushClip(new RectangleGeometry(graph));
        if (_curveKind == FlowCurveProfiles.HeadPulseKind)
            DrawImpulseStreams(drawingContext, graph, pixelsPerDip);
        else
            DrawStreams(drawingContext, graph, lookup, pixelsPerDip);
        drawingContext.Pop();

        DrawText(
            drawingContext,
            _curveKind switch
            {
                FlowCurveProfiles.LengthKind => "ОБЪЁМ ДАННЫХ",
                FlowCurveProfiles.SpeedKind => "СКОРОСТЬ СОЕДИНЕНИЯ",
                FlowCurveProfiles.SignalKind => "СИЛА СИГНАЛА",
                FlowCurveProfiles.FilterKind => "ФИЛЬТРАЦИЯ",
                FlowCurveProfiles.MemoryKind => "ПАМЯТЬ",
                _ => "ИМПУЛЬС"
            },
            9,
            MutedBrush,
            new Point(graph.Left + 8, 5),
            pixelsPerDip);
        DrawText(
            drawingContext,
            _curveKind switch
            {
                FlowCurveProfiles.LengthKind =>
                    "РЕДКИЕ КОРОТКИЕ  →  РЕДКИЕ ДЛИННЫЕ",
                FlowCurveProfiles.SpeedKind =>
                    "РЕДКИЕ МЕДЛЕННЫЕ  →  РЕДКИЕ БЫСТРЫЕ",
                FlowCurveProfiles.SignalKind =>
                    "РЕДКИЕ СЛАБЫЕ  →  РЕДКИЕ СИЛЬНЫЕ",
                FlowCurveProfiles.FilterKind =>
                    "РАННЕЕ ЗАВЕРШЕНИЕ  →  ПОЛНЫЙ ПРОХОД",
                FlowCurveProfiles.MemoryKind =>
                    "РЕДКИЕ КОРОТКИЕ  →  РЕДКИЕ ДОЛГИЕ",
                _ => "ОТ ВЕДУЩЕГО ЗНАКА  →  К КОНЦУ ШЛЕЙФА"
            },
            9,
            MutedBrush,
            new Point(
                graph.Left + (_curveKind is FlowCurveProfiles.LengthKind
                    or FlowCurveProfiles.SpeedKind
                    or FlowCurveProfiles.FilterKind
                    ? 105
                    : 8),
                graph.Bottom + 5),
            pixelsPerDip);

        if (_curveKind == FlowCurveProfiles.HeadPulseKind)
            return;

        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < lookup.Length; index++)
            {
                double x = index / (double)(lookup.Length - 1);
                Point point = CurveToScreen(graph, x, lookup[index]);
                if (index == 0)
                    context.BeginFigure(point, false, false);
                else
                    context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, CurvePen, geometry);

        for (int index = 0; index < _points.Count; index++)
        {
            Point point = CurveToScreen(graph, _points[index].X, _points[index].Y);
            double radius = index == _selectedIndex ? 6 : 4.5;
            drawingContext.DrawEllipse(
                index == _selectedIndex ? PointBrush : GraphBrush,
                new Pen(PointBrush, index == _selectedIndex ? 2 : 1.4),
                point,
                radius,
                radius);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_curveKind == FlowCurveProfiles.HeadPulseKind)
            return;
        Focus();
        Point position = e.GetPosition(this);
        Rect graph = GraphRect();
        int hit = HitPoint(graph, position);
        if (hit >= 0)
        {
            _selectedIndex = hit;
            if (hit > 0 && hit < _points.Count - 1)
            {
                _dragIndex = hit;
                CaptureMouse();
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!graph.Contains(position) || _points.Count >= 12)
            return;
        (double x, double y) = ScreenToCurve(graph, position);
        int insert = 1;
        while (insert < _points.Count && _points[insert].X < x)
            insert++;
        CurvePoint before = _points[insert - 1];
        CurvePoint after = _points[insert];
        if (after.X - before.X <= 0.045)
            return;
        x = Math.Clamp(x, before.X + 0.02, after.X - 0.02);
        y = FlowCurveProfiles.IsIncreasing(_curveKind)
            ? Math.Clamp(y, before.Y, after.Y)
            : Math.Clamp(y, after.Y, before.Y);
        _points.Insert(insert, new CurvePoint(x, y));
        _selectedIndex = insert;
        _dragIndex = insert;
        CaptureMouse();
        NotifyChanged();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_curveKind == FlowCurveProfiles.HeadPulseKind)
            return;
        if (_dragIndex <= 0
            || _dragIndex >= _points.Count - 1
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Rect graph = GraphRect();
        (double x, double y) = ScreenToCurve(graph, e.GetPosition(this));
        CurvePoint before = _points[_dragIndex - 1];
        CurvePoint after = _points[_dragIndex + 1];
        CurvePoint current = _points[_dragIndex];
        double minimumX = before.X + 0.02;
        double maximumX = after.X - 0.02;
        if (minimumX > maximumX)
            return;
        current.X = Math.Clamp(x, minimumX, maximumX);
        current.Y = FlowCurveProfiles.IsIncreasing(_curveKind)
            ? Math.Clamp(y, before.Y, after.Y)
            : Math.Clamp(y, after.Y, before.Y);
        NotifyChanged();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int hit = HitPoint(GraphRect(), e.GetPosition(this));
        if (hit <= 0 || hit >= _points.Count - 1)
            return;
        _points.RemoveAt(hit);
        _selectedIndex = -1;
        NotifyChanged();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Delete
            || _selectedIndex <= 0
            || _selectedIndex >= _points.Count - 1)
        {
            return;
        }
        _points.RemoveAt(_selectedIndex);
        _selectedIndex = -1;
        NotifyChanged();
        e.Handled = true;
    }

    private void DrawStreams(
        DrawingContext drawingContext,
        Rect graph,
        IReadOnlyList<double> selectedLookup,
        double pixelsPerDip)
    {
        const double cellHeight = 12.2;
        int availableColumns = Math.Clamp(
            (int)Math.Floor(graph.Width / 13.5),
            12,
            72);
        double previewDensity = _curveKind == FlowCurveProfiles.LengthKind
            ? _density
            : 0.68;
        int activeColumns = Math.Clamp(
            (int)Math.Round(availableColumns * previewDensity),
            1,
            availableColumns);

        int maximumCells = Math.Max(5, (int)Math.Floor((graph.Height - 8) / cellHeight));
        int emitted = 0;
        for (int column = 0; column < availableColumns; column++)
        {
            int before = (int)Math.Floor(column * activeColumns / (double)availableColumns);
            int after = (int)Math.Floor((column + 1) * activeColumns / (double)availableColumns);
            if (after == before)
                continue;

            double quantile = (column + 0.5) / availableColumns;
            double lengthPosition = _curveKind == FlowCurveProfiles.LengthKind
                ? FlowCurveMath.SampleLookup(selectedLookup, quantile)
                : 0.5;
            double speedPosition = _curveKind == FlowCurveProfiles.SpeedKind
                ? FlowCurveMath.SampleLookup(selectedLookup, quantile)
                : 0.5;
            double filterPosition = _curveKind == FlowCurveProfiles.FilterKind
                ? FlowCurveMath.SampleLookup(selectedLookup, quantile)
                : 0.5;
            double memoryPosition = _curveKind == FlowCurveProfiles.MemoryKind
                ? FlowCurveMath.SampleLookup(selectedLookup, quantile)
                : 0.5;
            double signalPosition = _curveKind == FlowCurveProfiles.SignalKind
                ? FlowCurveMath.SampleLookup(selectedLookup, quantile)
                : 0.5;
            double speed = _curveKind == FlowCurveProfiles.SpeedKind
                ? _speedMin + speedPosition * (_speedMax - _speedMin)
                : 0.50;
            double cellsPerSecond = maximumCells * speed;
            double memoryDuration = _memoryMin
                + memoryPosition * (_memoryMax - _memoryMin);
            double signalStrength = _signalMin
                + signalPosition * (_signalMax - _signalMin);
            double trailFraction = _curveKind == FlowCurveProfiles.LengthKind
                ? _trailMin + lengthPosition * (_trailMax - _trailMin)
                : 0.58;
            int nominalCells = Math.Clamp(
                (int)Math.Round(trailFraction * maximumCells),
                0,
                maximumCells);
            double memoryReferenceCells = _curveKind == FlowCurveProfiles.MemoryKind
                ? maximumCells / TrailMemoryModel.MaximumSliderDuration
                : nominalCells;
            TrailMemoryTiming memory = TrailMemoryModel.Create(
                memoryDuration,
                memoryReferenceCells / Math.Max(0.1, cellsPerSecond));
            double x = graph.Left + (column + 0.5) * graph.Width / availableColumns;
            double verticalPhase = Hash01(column * 53 + 19);
            double filterDepth = _filterMin
                + filterPosition * (_filterMax - _filterMin);
            double headY = _curveKind switch
            {
                FlowCurveProfiles.SpeedKind =>
                    graph.Top + 7 + speedPosition * Math.Max(0, graph.Height - 20),
                FlowCurveProfiles.FilterKind =>
                    graph.Top + 7 + filterDepth * Math.Max(0, graph.Height - 20),
                FlowCurveProfiles.MemoryKind =>
                    graph.Bottom - 12,
                FlowCurveProfiles.SignalKind =>
                    graph.Bottom - 12 - verticalPhase * graph.Height * 0.08,
                _ => graph.Bottom - 5 - verticalPhase * graph.Height * 0.23
            };
            if (_curveKind == FlowCurveProfiles.LengthKind
                && Hash01(column * 97 + 7) < _interception)
            {
                DrawCompetingHead(
                    drawingContext,
                    graph,
                    column,
                    x,
                    headY,
                    emitted,
                    pixelsPerDip);
            }
            int visibleCells = _curveKind == FlowCurveProfiles.MemoryKind
                ? maximumCells
                : nominalCells;
            for (int distance = visibleCells - 1; distance >= 0; distance--)
            {
                double y = headY - distance * cellHeight;
                if (y < graph.Top - cellHeight || y > graph.Bottom)
                    continue;
                double cellAgeSeconds = distance / Math.Max(0.1, cellsPerSecond);
                double fade = _curveKind == FlowCurveProfiles.MemoryKind
                    ? TrailMemoryModel.RemainingBrightness(
                        cellAgeSeconds,
                        memory.HoldSeconds,
                        memory.FadeSeconds)
                    : Math.Pow(
                        Math.Clamp(
                            1.0 - distance / Math.Max(1.0, nominalCells),
                            0.0,
                            1.0),
                        0.72);
                if (fade <= 0.002 && distance > 0)
                    continue;
                double baseStrength = _curveKind == FlowCurveProfiles.SignalKind
                    ? signalStrength
                    : 0.90;
                double visibleStrength = fade * baseStrength;
                if (visibleStrength <= 0.002)
                    continue;
                double alpha = Math.Clamp(0.04 + visibleStrength * 0.88, 0, 0.92);
                byte red = (byte)Math.Clamp(
                    Math.Round(5 + _signalColor.Red * visibleStrength * 232),
                    0,
                    255);
                byte green = (byte)Math.Clamp(
                    Math.Round(5 + _signalColor.Green * visibleStrength * 250),
                    0,
                    255);
                byte blue = (byte)Math.Clamp(
                    Math.Round(5 + _signalColor.Blue * visibleStrength * 232),
                    0,
                    255);
                Brush glyphBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(alpha * 255),
                    red,
                    green,
                    blue));
                int speedGlyphOffset = (int)Math.Round(speed * 100);
                char glyph = PreviewGlyphs[
                    (column * 7 + distance * 3 + emitted * 5 + speedGlyphOffset)
                    % PreviewGlyphs.Length];
                FormattedText text = new(
                    glyph.ToString(),
                    CultureInfo.CurrentCulture,
                    WpfFlowDirection.LeftToRight,
                    CodeTypeface,
                    11.3,
                    glyphBrush,
                    pixelsPerDip);
                if (_curveKind == FlowCurveProfiles.SignalKind
                    && Hash01(column * 149 + 61) < _signalGlowKeys
                    && _signalGlowPriority > 0.001)
                {
                    DrawSoftPreviewHalo(
                        drawingContext,
                        new Point(x, y + 7),
                        _signalGlowPriority * visibleStrength);
                }
                drawingContext.DrawText(
                    text,
                    new Point(x - text.WidthIncludingTrailingWhitespace / 2, y));
            }
            emitted++;
        }
    }

    private void DrawImpulseStreams(
        DrawingContext drawingContext,
        Rect graph,
        double pixelsPerDip)
    {
        const double cellHeight = 12.2;
        int maximumCells = Math.Max(5, (int)Math.Floor((graph.Height - 10) / cellHeight));
        int trailCells = Math.Clamp((int)Math.Round(maximumCells * 0.78), 5, maximumCells);
        double speed = maximumCells * 0.50;
        int streamCount = Math.Clamp((int)Math.Floor(graph.Width / 24.0), 8, 30);
        for (int column = 0; column < streamCount; column++)
        {
            bool impulseEnabled = Hash01(column * 163 + 41)
                < _headImpulseProbability;
            HeadImpulseTiming impulse = impulseEnabled
                ? HeadImpulseModel.Create(
                    _headImpulseDecay,
                    trailCells,
                    speed)
                : default;
            double x = graph.Left
                + (column + 0.5) * graph.Width / streamCount;
            double headY = graph.Bottom
                - 9
                - Hash01(column * 71 + 17) * 18;

            for (int distance = trailCells - 1; distance >= 0; distance--)
            {
                double age = distance / speed;
                double emphasis = impulseEnabled
                    ? HeadImpulseModel.Emphasis(
                        age,
                        impulse.HoldSeconds,
                        impulse.FadeSeconds)
                    : 0.0;
                double natural = Math.Pow(
                    Math.Clamp(
                        1.0 - distance / (double)trailCells,
                        0.0,
                        1.0),
                    0.72);
                double alpha = Math.Clamp(0.08 + natural * 0.84, 0.0, 0.94);
                double whiteMix = emphasis * (0.28 + _headBrightness * 0.68);
                byte red = PreviewChannel(_signalColor.Red, natural, whiteMix);
                byte green = PreviewChannel(_signalColor.Green, natural, whiteMix);
                byte blue = PreviewChannel(_signalColor.Blue, natural, whiteMix);
                Brush glyphBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(alpha * 255),
                    red,
                    green,
                    blue));
                double effectiveWeight = _headWeight * emphasis;
                Typeface typeface = effectiveWeight >= 0.67
                    ? CodeBoldTypeface
                    : effectiveWeight >= 0.28
                        ? CodeSemiBoldTypeface
                        : CodeTypeface;
                char glyph = PreviewGlyphs[
                    (column * 7 + distance * 5 + 7)
                    % PreviewGlyphs.Length];
                FormattedText text = new(
                    glyph.ToString(),
                    CultureInfo.CurrentCulture,
                    WpfFlowDirection.LeftToRight,
                    typeface,
                    11.4 + effectiveWeight * 0.8,
                    glyphBrush,
                    pixelsPerDip);
                double y = headY - distance * cellHeight;
                if (_headGlow > 0.001 && emphasis > 0.001)
                {
                    DrawSoftPreviewHalo(
                        drawingContext,
                        new Point(x, y + 7),
                        _headGlow * emphasis * natural);
                }
                drawingContext.DrawText(
                    text,
                    new Point(
                        x - text.WidthIncludingTrailingWhitespace / 2,
                        y));
            }
        }
    }

    private void DrawSoftPreviewHalo(
        DrawingContext drawingContext,
        Point center,
        double strength)
    {
        strength = Math.Clamp(strength, 0.0, 2.0);
        if (strength <= 0.001)
            return;

        for (int ring = 3; ring >= 1; ring--)
        {
            double ringShare = ring switch
            {
                3 => 0.16,
                2 => 0.24,
                _ => 0.34
            };
            Brush glowBrush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(
                    (int)Math.Round(255 * Math.Min(1.0, strength) * ringShare),
                    0,
                    255),
                (byte)Math.Round(_signalColor.Red * 224),
                (byte)Math.Round(_signalColor.Green * 224),
                (byte)Math.Round(_signalColor.Blue * 224)));
            double radius = 3.2 + ring * (2.0 + strength * 1.7);
            drawingContext.DrawEllipse(
                glowBrush,
                null,
                center,
                radius,
                radius);
        }
    }

    private static byte PreviewChannel(
        double signalChannel,
        double natural,
        double whiteMix)
    {
        double body = 0.02 + signalChannel * natural * 0.93;
        return (byte)Math.Clamp(
            (int)Math.Round((body + (1.0 - body) * whiteMix) * 255),
            0,
            255);
    }

    private static void DrawCompetingHead(
        DrawingContext drawingContext,
        Rect graph,
        int column,
        double x,
        double olderHeadY,
        int emitted,
        double pixelsPerDip)
    {
        const double cellHeight = 12.2;
        double availableAbove = Math.Max(18, olderHeadY - graph.Top);
        double pursuitDistance = Math.Min(
            availableAbove - 4,
            graph.Height * (0.12 + Hash01(column * 109 + 31) * 0.26));
        double headY = Math.Max(graph.Top + 5, olderHeadY - pursuitDistance);
        int cells = 3 + (int)Math.Round(Hash01(column * 127 + 43) * 3);
        for (int distance = cells - 1; distance >= 0; distance--)
        {
            double intensity = 1.0 - distance / (double)(cells + 1);
            byte alpha = (byte)Math.Round((0.25 + intensity * 0.55) * 255);
            Brush glyphBrush = new SolidColorBrush(Color.FromArgb(
                alpha,
                (byte)Math.Round(20 + intensity * 170),
                (byte)Math.Round(120 + intensity * 125),
                (byte)Math.Round(55 + intensity * 150)));
            char glyph = PreviewGlyphs[
                (column * 11 + distance * 5 + emitted * 3)
                % PreviewGlyphs.Length];
            FormattedText text = new(
                glyph.ToString(),
                CultureInfo.CurrentCulture,
                WpfFlowDirection.LeftToRight,
                distance == 0 ? CodeSemiBoldTypeface : CodeTypeface,
                distance == 0 ? 11.9 : 11.2,
                glyphBrush,
                pixelsPerDip);
            drawingContext.DrawText(
                text,
                new Point(
                    x - text.WidthIncludingTrailingWhitespace / 2,
                    headY - distance * cellHeight));
        }
    }

    private Rect GraphRect() => new(
        18,
        22,
        Math.Max(90, ActualWidth - 36),
        Math.Max(120, ActualHeight - 48));

    private static double Hash01(int value)
    {
        uint hash = unchecked((uint)value);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return (hash & 0x00FFFFFFu) / 16777215.0;
    }

    private int HitPoint(Rect graph, Point position)
    {
        double best = 10;
        int result = -1;
        for (int index = 0; index < _points.Count; index++)
        {
            Point point = CurveToScreen(graph, _points[index].X, _points[index].Y);
            double distance = (point - position).Length;
            if (distance >= best)
                continue;
            best = distance;
            result = index;
        }
        return result;
    }

    private Point CurveToScreen(Rect graph, double x, double y)
    {
        double visualY = _curveKind is FlowCurveProfiles.SpeedKind
            or FlowCurveProfiles.FilterKind
            ? 1.0 - y
            : y;
        return new Point(
            graph.Left + Math.Clamp(x, 0, 1) * graph.Width,
            graph.Bottom - Math.Clamp(visualY, 0, 1) * graph.Height);
    }

    private (double X, double Y) ScreenToCurve(Rect graph, Point point)
    {
        double visualY = Math.Clamp((graph.Bottom - point.Y) / graph.Height, 0, 1);
        return (
            Math.Clamp((point.X - graph.Left) / graph.Width, 0, 1),
            _curveKind is FlowCurveProfiles.SpeedKind
                or FlowCurveProfiles.FilterKind
                ? 1.0 - visualY
                : visualY
        );
    }

    private void NotifyChanged()
    {
        InvalidateVisual();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        double size,
        Brush brush,
        Point origin,
        double pixelsPerDip)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.CurrentCulture,
            WpfFlowDirection.LeftToRight,
            UiTypeface,
            size,
            brush,
            pixelsPerDip);
        context.DrawText(formatted, origin);
    }

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte red, byte green, byte blue, double thickness)
    {
        Pen pen = new(FrozenBrush(red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }
}
