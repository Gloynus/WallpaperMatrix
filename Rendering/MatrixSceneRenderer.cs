using System.Diagnostics;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;

namespace WallpaperMatrix.Rendering;

/// <summary>
/// Advances the Matrix simulation and publishes compact glyph instances.
/// It performs no per-frame GDI drawing and creates no full-screen bitmap.
/// </summary>
internal sealed class MatrixSceneRenderer : IDisposable
{
    private const int PaletteLevels = SignalModel.MaximumLevel;
    private const int MaximumGridCells = 1_000_000;
    private static readonly int[] ImageAtlasStyleRows = [3, 0, 4];
    private static readonly int[] Bayer4 =
    [
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5
    ];

    private readonly IntPtr _referenceWindow;
    private readonly SharedMatrixScene _scene;
    private readonly int _width;
    private readonly int _height;
    private readonly double _dpiScale;
    private readonly Random _random = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private AppSettings _settings;
    private PreparedImage? _image;
    private byte[]? _imageMask;
    private GlyphAtlasData? _pendingAtlas;
    private bool _maskDirty = true;
    private bool _wasPaused;
    private bool _disposed;
    private TimeSpan _lastFrameAt;
    private int _columns;
    private int _rows;
    private int _cellWidth;
    private int _cellHeight;
    private byte[] _rainLevels = [];
    private byte[] _nextRainLevels = [];
    private byte[] _rainStyles = [];
    private byte[] _nextRainStyles = [];
    private float[] _rainEmphasis = [];
    private float[] _nextRainEmphasis = [];
    private float[] _rainGlow = [];
    private float[] _nextRainGlow = [];
    private bool[] _rainCovered = [];
    private bool[] _nextRainCovered = [];
    private ushort[] _rainGlyphs = [];
    private ushort[] _nextRainGlyphs = [];
    private bool[] _trailOccupied = [];
    private bool[] _trailSuppressImage = [];
    private double[] _trailBornAt = [];
    private float[] _trailMemoryHoldSeconds = [];
    private float[] _trailMemoryFadeSeconds = [];
    private float[] _trailPulseHoldSeconds = [];
    private float[] _trailPulseFadeSeconds = [];
    private bool[] _trailImpulseEnabled = [];
    private float[] _trailGlowStrength = [];
    private float[] _trailBaseIntensity = [];
    private float[] _trailImageResistance = [];
    private ushort[] _trailGlyphs = [];
    private long[] _trailStreamIds = [];
    private float[] _imageLevels = [];
    private float[] _imageInitialLevels = [];
    private float[] _imageHoldSeconds = [];
    private float[] _imageFadeElapsed = [];
    private float[] _imageFadeSeconds = [];
    private ushort[] _imageGlyphs = [];
    private byte[] _imageStyles = [];
    private GlyphDensity[][] _imageGlyphDensities = [[], [], []];
    private bool[] _clockCells = [];
    private int[] _clockCellIndices = [];
    private ushort[] _clockDisplayedGlyphs = [];
    private double[] _clockBrightness = [];
    private string _clockTargetText = "";
    private int _clockTargetMinute = -1;
    private GlyphInstance[] _instances = [];
    private List<RainStream>[] _streamsByColumn = [];
    private ColumnSpawner[] _spawners = [];
    private double[] _speedDistributionLookup = [];
    private double[] _lengthDistributionLookup = [];
    private double[] _signalDistributionLookup = [];
    private double[] _filterDistributionLookup = [];
    private double[] _memoryDistributionLookup = [];
    private double _averageSpeedDistribution = 0.5;
    private double _averageLengthDistribution = 0.5;
    private double _simulationTime;
    private long _nextStreamId;

    public MatrixSceneRenderer(
        IntPtr referenceWindow,
        SharedMatrixScene scene,
        AppSettings settings)
    {
        _referenceWindow = referenceWindow;
        _scene = scene;
        _width = scene.Width;
        _height = scene.Height;
        _settings = settings.Copy();
        uint dpi = NativeMethods.GetDpiForWindow(referenceWindow);
        _dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
        RebuildCurveLookups();
        RebuildGrid();
        RebuildAtlas();
        PublishScene();
    }

    public void UpdateSettings(AppSettings settings)
    {
        bool gridGeometryChanged =
            Math.Abs(settings.FontSize - _settings.FontSize) > 0.01
            || Math.Abs(settings.GlyphStretch - _settings.GlyphStretch) > 0.01;
        bool fontFamilyChanged = !string.Equals(
            settings.FontFamily,
            _settings.FontFamily,
            StringComparison.Ordinal);
        bool atlasChanged = gridGeometryChanged
            || fontFamilyChanged
            || Math.Abs(settings.GlyphWeight - _settings.GlyphWeight) > 0.001
            || Math.Abs(settings.HeadWeight - _settings.HeadWeight) > 0.01
            || Math.Abs(settings.ClockWeight - _settings.ClockWeight) > 0.01;
        bool gridChanged = gridGeometryChanged;
        bool clockLayoutChanged = settings.ClockEnabled != _settings.ClockEnabled
            || settings.ClockPosition != _settings.ClockPosition
            || settings.ClockHorizontalMarginCells != _settings.ClockHorizontalMarginCells
            || settings.ClockVerticalMarginCells != _settings.ClockVerticalMarginCells;
        bool maskChanged = gridChanged || settings.ImageFit != _settings.ImageFit;
        bool imageModeDisabled = _settings.ImageMode && !settings.ImageMode;
        bool spawnCadenceChanged =
            Math.Abs(settings.InterceptionRate - _settings.InterceptionRate) > 0.001
            || Math.Abs(settings.Density - _settings.Density) > 0.001
            || Math.Abs(settings.SpeedMin - _settings.SpeedMin) > 0.001
            || Math.Abs(settings.SpeedMax - _settings.SpeedMax) > 0.001;
        bool curvesChanged = !FlowCurveMath.Equivalent(
                settings.SpeedCurve,
                _settings.SpeedCurve,
                increasing: true)
            || !FlowCurveMath.Equivalent(
                settings.TrailLengthCurve,
                _settings.TrailLengthCurve,
                increasing: true)
            || !FlowCurveMath.Equivalent(
                settings.SignalCurve,
                _settings.SignalCurve,
                increasing: true)
            || !FlowCurveMath.Equivalent(
                settings.StreamFilterCurve,
                _settings.StreamFilterCurve,
                increasing: true)
            || !FlowCurveMath.Equivalent(
                settings.MemoryCurve,
                _settings.MemoryCurve,
                increasing: true)
            || !CurveAdjustmentEquivalent(
                settings.SpeedCurveAdjustment,
                _settings.SpeedCurveAdjustment)
            || !CurveAdjustmentEquivalent(
                settings.TrailLengthCurveAdjustment,
                _settings.TrailLengthCurveAdjustment)
            || !CurveAdjustmentEquivalent(
                settings.SignalCurveAdjustment,
                _settings.SignalCurveAdjustment)
            || !CurveAdjustmentEquivalent(
                settings.StreamFilterCurveAdjustment,
                _settings.StreamFilterCurveAdjustment)
            || !CurveAdjustmentEquivalent(
                settings.MemoryCurveAdjustment,
                _settings.MemoryCurveAdjustment);

        _settings = settings.Copy();
        if (curvesChanged)
            RebuildCurveLookups();
        if (gridChanged)
            RebuildGrid();
        else if (clockLayoutChanged)
            RebuildClockLayout();
        if (atlasChanged)
            RebuildAtlas();
        if (maskChanged)
            _maskDirty = true;
        if (spawnCadenceChanged || curvesChanged)
        {
            foreach (ColumnSpawner spawner in _spawners)
                ScheduleNextSpawn(spawner, initial: true);
        }
        if (imageModeDisabled)
            ClearImageOverlay();
    }

    public void SetImage(PreparedImage? image)
    {
        _image = image;
        _maskDirty = true;
        if (image is null)
            ClearImageOverlay();
    }

    public void ResetImageOverlay(PreparedImage? image)
    {
        _image = image;
        _maskDirty = true;
        ClearImageOverlay();
    }

    public bool RenderIfDue(bool paused)
    {
        TimeSpan now = _clock.Elapsed;
        if (paused)
        {
            _lastFrameAt = now;
            _wasPaused = true;
            return false;
        }

        if (_wasPaused)
        {
            _lastFrameAt = now;
            _wasPaused = false;
        }

        _scene.SetPresentationFrameRate(_settings.FramesPerSecond);
        double interval = 1.0 / _settings.FramesPerSecond;
        if ((now - _lastFrameAt).TotalSeconds < interval)
            return false;

        double dt = Math.Min(0.08, Math.Max(0.0, (now - _lastFrameAt).TotalSeconds));
        _lastFrameAt = now;
        if (_settings.ImageMode && _image is not null)
            EnsureImageMask();
        FadeImageCells(dt);
        AdvanceStreams(dt);
        BuildRainCells();
        PublishScene();
        return true;
    }

    public int RecommendedWaitMilliseconds(bool paused)
    {
        if (paused)
            return 250;

        double interval = 1.0 / _settings.FramesPerSecond;
        double remainingMilliseconds = (interval - (_clock.Elapsed - _lastFrameAt).TotalSeconds) * 1000.0;
        return Math.Clamp((int)Math.Ceiling(remainingMilliseconds), 2, 20);
    }

    private void BuildRainCells()
    {
        Array.Clear(_nextRainLevels);
        Array.Clear(_nextRainStyles);
        Array.Clear(_nextRainEmphasis);
        Array.Clear(_nextRainGlow);
        Array.Clear(_nextRainCovered);
        Array.Clear(_nextRainGlyphs);
        for (int index = 0; index < _trailOccupied.Length; index++)
        {
            if (!_trailOccupied[index])
                continue;

            int row = index / _columns;
            int column = index - row * _columns;
            double horizontalShade = EdgeShade((column + 0.5) / _columns);
            double age = Math.Max(0, _simulationTime - _trailBornAt[index]);
            double resistance = Math.Clamp(
                _trailImageResistance[index],
                0.0f,
                1.0f);
            double fadeRate = resistance >= 0.999
                ? double.PositiveInfinity
                : 1.0 / Math.Max(0.001, 1.0 - resistance);
            double effectiveAge = double.IsPositiveInfinity(fadeRate)
                ? double.PositiveInfinity
                : age * fadeRate;
            double emphasis = _trailImpulseEnabled[index]
                ? HeadImpulseModel.Emphasis(
                    age,
                    _trailPulseHoldSeconds[index],
                    _trailPulseFadeSeconds[index])
                : 0.0;

            double baseFade = double.IsPositiveInfinity(effectiveAge)
                ? 0.0
                : TrailMemoryModel.RemainingBrightness(
                    effectiveAge,
                    _trailMemoryHoldSeconds[index],
                    _trailMemoryFadeSeconds[index]);
            bool imageCell = _settings.ImageMode
                && _imageLevels[index] > 0.01f
                && _imageStyles[index] >= 3;
            if (baseFade <= 0.001
                && emphasis <= 0.001
                && !imageCell)
            {
                ClearTrailCell(index);
                continue;
            }

            _nextRainCovered[index] = true;
            _nextRainGlow[index] = (float)Math.Clamp(
                _trailGlowStrength[index] + _settings.HeadGlow * emphasis,
                0.0,
                2.0);
            if (_trailSuppressImage[index])
                continue;

            double baseIntensity = _trailBaseIntensity[index]
                * horizontalShade
                * EdgeShade((row + 0.5) / _rows);
            double intensity = baseFade
                * (baseIntensity + (1.0 - baseIntensity) * emphasis);

            int rainLevel = CalculateLevel(intensity);
            if (imageCell)
            {
                _nextRainLevels[index] = (byte)Math.Max(
                    rainLevel,
                    Math.Clamp((int)Math.Ceiling(_imageLevels[index]), 0, PaletteLevels));
                _nextRainGlyphs[index] = _imageGlyphs[index];
                _nextRainStyles[index] = _imageStyles[index];
            }
            else
            {
                _nextRainLevels[index] = (byte)rainLevel;
                _nextRainGlyphs[index] = _trailGlyphs[index];
                _nextRainStyles[index] = emphasis > 0.001 ? (byte)1 : (byte)0;
            }
            _nextRainEmphasis[index] = (float)emphasis;
        }

        ApplyClockCells();
        (_rainLevels, _nextRainLevels) = (_nextRainLevels, _rainLevels);
        (_rainStyles, _nextRainStyles) = (_nextRainStyles, _rainStyles);
        (_rainEmphasis, _nextRainEmphasis) = (_nextRainEmphasis, _rainEmphasis);
        (_rainGlow, _nextRainGlow) = (_nextRainGlow, _rainGlow);
        (_rainGlyphs, _nextRainGlyphs) = (_nextRainGlyphs, _rainGlyphs);
        (_rainCovered, _nextRainCovered) = (_nextRainCovered, _rainCovered);
    }

    private void PublishScene()
    {
        int count = 0;
        for (int index = 0; index < _rainLevels.Length; index++)
        {
            int row = index / _columns;
            int column = index - row * _columns;
            bool clockCell = _clockCells.Length > index && _clockCells[index];
            int rainLevel = _rainLevels[index];
            if (rainLevel > 0)
            {
                AddInstance(
                    ref count,
                    column,
                    row,
                    _rainGlyphs[index],
                    rainLevel / (double)PaletteLevels,
                    _rainStyles[index],
                    _rainEmphasis[index],
                    _rainGlow[index]);
                continue;
            }

            if (clockCell || _rainCovered[index] || !_settings.ImageMode || _imageLevels[index] <= 0)
                continue;
            AddInstance(
                ref count,
                column,
                row,
                _imageGlyphs[index],
                _imageLevels[index] / (double)PaletteLevels,
                _imageStyles[index],
                emphasis: 0,
                glow: 0);
        }

        SignalRgb signal = SignalColorModel.ToRgb(
            _settings.SignalHue,
            _settings.SignalBrightness);
        SignalRgb background = SignalColorModel.ToBackgroundRgb(
            _settings.BackgroundHue,
            _settings.BackgroundBrightness);
        MatrixRenderParameters parameters = new(
            _width,
            _height,
            _cellWidth,
            _cellHeight,
            _settings.HeadBrightness,
            signal.Red,
            signal.Green,
            signal.Blue,
            background.Red,
            background.Green,
            background.Blue);
        lock (_scene.SyncRoot)
        {
            _scene.Publish(_instances, count, _pendingAtlas, parameters);
            _pendingAtlas = null;
        }
    }

    private void AddInstance(
        ref int count,
        int column,
        int row,
        int glyph,
        double level,
        float style,
        double emphasis,
        double glow)
    {
        if ((uint)count >= (uint)_instances.Length)
            return;
        _instances[count++] = new GlyphInstance(
            column,
            row,
            glyph,
            level,
            style,
            emphasis,
            glow);
    }

    private void AdvanceStreams(double dt)
    {
        _simulationTime += dt;
        UpdateClockTarget();
        int minimumClockLevel = MinimumClockLevel();
        for (int slot = 0; slot < _clockBrightness.Length; slot++)
            _clockBrightness[slot] = Math.Max(minimumClockLevel, _clockBrightness[slot] - dt * 6.5);

        for (int column = 0; column < _streamsByColumn.Length; column++)
        {
            ColumnSpawner spawner = _spawners[column];
            List<RainStream> streams = _streamsByColumn[column];
            if (_simulationTime >= spawner.NextSpawnAt)
            {
                // Density decides whether an idle channel wakes up. Once a
                // channel is occupied, interception controls its competing
                // heads without a second density gate suppressing them.
                double spawnChance = streams.Count > 0
                    ? _settings.InterceptionRate
                    : _settings.Density;
                if (_random.NextDouble() <= spawnChance)
                {
                    SpawnStream(column, firstRun: false);
                }
                ScheduleNextSpawn(spawner, initial: false);
            }

            for (int index = streams.Count - 1; index >= 0; index--)
            {
                RainStream stream = streams[index];
                stream.PreviousHead = stream.Head;
                stream.Head += stream.Speed * dt;
                DepositCrossedCells(column, stream);
                AdvanceClockCell(column, stream.PreviousHead, stream.Head);
                if (stream.Head > stream.TerminationRow)
                    streams.RemoveAt(index);
            }
            ConsumeCaughtStreams(column, streams);
        }
    }

    private void DepositCrossedCells(int column, RainStream stream)
    {
        int currentRow = (int)Math.Floor(stream.Head);
        int firstRow = Math.Max(0, stream.LastWrittenRow + 1);
        int lastRow = Math.Min(_rows - 1, currentRow);
        for (int row = firstRow; row <= lastRow; row++)
        {
            double bornAt = _simulationTime
                - Math.Max(0, stream.Head - row) / Math.Max(0.1, stream.Speed);
            DepositTrailCell(column, row, stream, bornAt);
        }
        stream.LastWrittenRow = Math.Max(stream.LastWrittenRow, currentRow);
    }

    private void DepositTrailCell(
        int column,
        int row,
        RainStream stream,
        double bornAt)
    {
        if ((uint)column >= (uint)_columns || (uint)row >= (uint)_rows)
            return;

        bool imageInfluence = _settings.ImageMode
            && _imageMask is not null
            && _imageMask.Length == _imageLevels.Length;
        if (imageInfluence)
        {
            ReplaceImageCell(
                column,
                row,
                stream.Seed,
                stream.MemoryFadeSeconds);
        }
        int index = row * _columns + column;
        uint noise = Hash((uint)column, (uint)row, stream.Seed);
        bool imageCell = _settings.ImageMode
            && _imageLevels[index] > 0.01f
            && _imageStyles[index] >= 3;
        bool deliberateGap = (noise & 31) == 0 || ((noise >> 5) & 63) == 0;
        _trailOccupied[index] = true;
        _trailSuppressImage[index] = deliberateGap && !imageCell;
        _trailBornAt[index] = bornAt;
        _trailMemoryHoldSeconds[index] = (float)Math.Max(
            0.0,
            stream.MemoryHoldSeconds);
        _trailMemoryFadeSeconds[index] = (float)Math.Max(
            0.0,
            stream.MemoryFadeSeconds);
        _trailPulseHoldSeconds[index] = (float)Math.Max(
            0,
            stream.ImpulseHoldSeconds);
        _trailPulseFadeSeconds[index] = (float)Math.Max(
            0,
            stream.ImpulseFadeSeconds);
        _trailImpulseEnabled[index] = stream.ImpulseEnabled;
        _trailGlowStrength[index] = (float)Math.Clamp(
            stream.GlowStrength,
            0.0,
            1.0);
        _trailBaseIntensity[index] = (float)SignalModel.QuantizeStrength(
            stream.SignalStrength);
        _trailImageResistance[index] = imageInfluence
            ? (float)_settings.ImageResistance
            : 0.0f;
        _trailGlyphs[index] = (ushort)(
            Hash(noise, (uint)(row / 3), stream.Seed)
            % MatrixGlyphSet.GlyphStrings.Length);
        _trailStreamIds[index] = stream.Id;
    }

    private void SeedInitialTrail(int column, RainStream stream)
    {
        int headRow = Math.Min(_rows - 1, (int)Math.Floor(stream.Head));
        if (headRow < 0)
            return;
        int firstRow = Math.Max(0, headRow - stream.Length + 1);
        for (int row = firstRow; row <= headRow; row++)
        {
            double bornAt = _simulationTime
                - Math.Max(0, stream.Head - row) / Math.Max(0.1, stream.Speed);
            DepositTrailCell(column, row, stream, bornAt);
        }
        stream.LastWrittenRow = headRow;
    }

    private void ApplyClockCells()
    {
        int minimumClockLevel = MinimumClockLevel();
        for (int slot = 0; slot < _clockCellIndices.Length; slot++)
        {
            int index = _clockCellIndices[slot];
            if ((uint)index >= (uint)_nextRainLevels.Length)
                continue;

            int rainLevel = _nextRainLevels[index];
            int rainClockLevel = minimumClockLevel
                + (int)Math.Round(rainLevel / (double)PaletteLevels * (PaletteLevels - minimumClockLevel));
            int clockLevel = Math.Max((int)Math.Round(_clockBrightness[slot]), rainClockLevel);
            _nextRainLevels[index] = (byte)Math.Clamp(clockLevel, minimumClockLevel, PaletteLevels);
            _nextRainGlyphs[index] = _clockDisplayedGlyphs[slot];
            _nextRainStyles[index] = 2;
            _nextRainEmphasis[index] = 0;
        }
    }

    private int MinimumClockLevel() =>
        Math.Clamp(
            (int)Math.Round(_settings.ClockBrightness * PaletteLevels),
            PaletteLevels / 2,
            PaletteLevels);

    private void UpdateClockTarget()
    {
        if (!_settings.ClockEnabled || _clockCellIndices.Length != 5)
            return;
        DateTime now = DateTime.Now;
        int minuteOfDay = now.Hour * 60 + now.Minute;
        if (minuteOfDay == _clockTargetMinute)
            return;
        _clockTargetMinute = minuteOfDay;
        _clockTargetText = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void AdvanceClockCell(int column, double previousHead, double currentHead)
    {
        if (_clockCellIndices.Length != 5 || _clockTargetText.Length != 5)
            return;

        int previousRow = (int)Math.Floor(previousHead);
        int currentRow = (int)Math.Floor(currentHead);
        for (int slot = 0; slot < _clockCellIndices.Length; slot++)
        {
            int index = _clockCellIndices[slot];
            int clockRow = index / _columns;
            int clockColumn = index - clockRow * _columns;
            if (clockColumn != column || previousRow >= clockRow || currentRow < clockRow)
                continue;

            int glyphIndex = MatrixGlyphSet.Glyphs.IndexOf(
                _clockTargetText[slot],
                StringComparison.Ordinal);
            if (glyphIndex >= 0)
                _clockDisplayedGlyphs[slot] = (ushort)glyphIndex;
            _clockBrightness[slot] = PaletteLevels;
        }
    }

    private void FadeImageCells(double dt)
    {
        if (!_settings.ImageMode || dt <= 0 || _imageLevels.Length == 0)
            return;

        for (int index = 0; index < _imageLevels.Length; index++)
        {
            float level = _imageLevels[index];
            if (level <= 0)
                continue;

            float remaining = _imageHoldSeconds[index];
            double fadeDelta = dt;
            if (remaining > 0)
            {
                double held = Math.Min(dt, remaining);
                _imageHoldSeconds[index] = (float)Math.Max(0, remaining - held);
                fadeDelta -= held;
            }
            if (fadeDelta > 0)
                _imageFadeElapsed[index] += (float)fadeDelta;

            double position = _imageFadeElapsed[index]
                / Math.Max(0.1f, _imageFadeSeconds[index]);
            double naturalFade = TrailMemoryModel.RemainingBrightness(
                position,
                holdSeconds: 0.0,
                fadeSeconds: 1.0);
            level = (float)(_imageInitialLevels[index] * naturalFade);
            _imageLevels[index] = level;
            if (level > 0.01f)
                continue;
            _imageGlyphs[index] = 0;
            _imageStyles[index] = 0;
        }
    }

    private void ReplaceImageCell(
        int column,
        int row,
        uint revealSeed,
        double fadeSeconds)
    {
        int index = row * _columns + column;
        double sourceTone = _imageMask![index] / 255.0;
        double tone = ShapeImageTone(
            sourceTone,
            _settings.ImageExpressiveness,
            _settings.ImageToneCalmness);
        uint cellHash = Hash((uint)column, (uint)row, 0xC0DEF00Du);
        double coverage = Math.Min(1.0, tone * 1.18);
        double threshold = (
            Bayer4[((row & 3) << 2) + (column & 3)] + 0.5) / 16.0;
        if (coverage < threshold)
        {
            _imageLevels[index] = 0;
            _imageInitialLevels[index] = 0;
            _imageHoldSeconds[index] = 0;
            _imageFadeElapsed[index] = 0;
            _imageGlyphs[index] = 0;
            _imageStyles[index] = 0;
            return;
        }

        int weightTier = SelectImageWeightTier(cellHash, revealSeed, tone);
        double intensity = (0.12 + tone * 0.78)
            * EdgeShade((column + 0.5) / _columns)
            * EdgeShade((row + 0.5) / _rows)
            * _settings.ImageBrightness;
        double exactLevel = Math.Clamp(intensity * PaletteLevels, 0.0, PaletteLevels);
        int targetLevel = (int)Math.Floor(exactLevel);
        if (UnitHash(Hash(cellHash, revealSeed, 0x51ED270Bu)) < exactLevel - targetLevel)
            targetLevel++;
        targetLevel = Math.Clamp(targetLevel, 0, PaletteLevels);
        int targetGlyph = targetLevel == 0
            ? 0
            : SelectImageGlyph(cellHash, revealSeed, tone, weightTier);
        _imageLevels[index] = targetLevel;
        _imageInitialLevels[index] = targetLevel;
        _imageHoldSeconds[index] = (float)(
            _settings.ImageDurationSeconds
            * _settings.ImageStability);
        _imageFadeElapsed[index] = 0;
        _imageFadeSeconds[index] = (float)Math.Max(0.1, fadeSeconds);
        _imageGlyphs[index] = (ushort)targetGlyph;
        _imageStyles[index] = targetLevel == 0 ? (byte)0 : (byte)(3 + weightTier);
    }

    private int SelectImageGlyph(
        uint cellHash,
        uint revealSeed,
        double tone,
        int weightTier)
    {
        uint choice = Hash(cellHash, revealSeed, 0x91E10DA5u);
        double matchRoll = UnitHash(Hash(choice, 0xA341316Cu, 0xC8013EA4u));
        if (_settings.ImageGlyphMatch <= 0 || matchRoll >= _settings.ImageGlyphMatch)
            return (int)(choice % MatrixGlyphSet.GlyphStrings.Length);

        GlyphDensity[] densities = _imageGlyphDensities[Math.Clamp(weightTier, 0, 2)];
        if (densities.Length == 0)
            return (int)(choice % MatrixGlyphSet.GlyphStrings.Length);

        int low = 0;
        int high = densities.Length - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (densities[middle].Density < tone)
                low = middle + 1;
            else
                high = middle;
        }
        if (low > 0
            && Math.Abs(densities[low - 1].Density - tone)
                <= Math.Abs(densities[low].Density - tone))
        {
            low--;
        }
        int candidateCount = Math.Min(5, densities.Length);
        int candidateStart = Math.Clamp(
            low - candidateCount / 2,
            0,
            densities.Length - candidateCount);
        uint variant = Hash(choice, 0xB5297A4Du, 0x68E31DA4u);
        return densities[candidateStart + (int)(variant % (uint)candidateCount)].Glyph;
    }

    private static int SelectImageWeightTier(uint cellHash, uint revealSeed, double tone)
    {
        // Every tone can use every weight. The image still favours lighter
        // strokes in shadows and bolder strokes in highlights, but never
        // collapses an entire bright region into bold-only glyphs.
        double thinChance = 0.16 + (1.0 - tone) * 0.36;
        const double normalChance = 0.32;
        double roll = UnitHash(Hash(cellHash, revealSeed, 0xD3A2646Cu));
        if (roll < thinChance)
            return 0;
        return roll < thinChance + normalChance ? 1 : 2;
    }

    private static double ShapeImageTone(
        double tone,
        double expressiveness,
        double calmness)
    {
        double contrast = 0.55 + expressiveness * 0.75;
        double expanded = Math.Clamp(0.5 + (tone - 0.5) * contrast, 0.0, 1.0);
        double gamma = 1.32 - Math.Min(1.0, expressiveness) * 0.32;
        double shaped = Math.Pow(expanded, gamma);
        // Calmness preserves ordering and detail while reserving less of the
        // finite glyph palette for featureless full black/full white fields.
        double inset = Math.Clamp(calmness, 0.0, 1.0) * 0.22;
        return inset + shaped * (1.0 - inset * 2.0);
    }

    private void RebuildGrid()
    {
        GridSnapshot? snapshot = CaptureGridSnapshot();
        // The film glyphs occupy nearly square cells instead of looking like
        // narrow terminal text stretched vertically. At 1 px the atlas draws
        // one physical point inside a small logical pitch. Very large desktops
        // enlarge that pitch just enough to keep live rebuilding bounded.
        _cellWidth = Math.Max(3, (int)Math.Round(_settings.FontSize * _dpiScale * 0.92));
        _cellHeight = Math.Max(
            1,
            (int)Math.Round(
                _settings.FontSize
                * _dpiScale
                * 1.04
                * GlyphGeometryModel.HeightScale(_settings.GlyphStretch)));
        long desiredCellCount =
            (long)Math.Ceiling(_width / (double)_cellWidth)
            * (long)Math.Ceiling(_height / (double)_cellHeight);
        if (desiredCellCount > MaximumGridCells)
        {
            double scale = Math.Sqrt(desiredCellCount / (double)MaximumGridCells);
            _cellWidth = Math.Max(_cellWidth, (int)Math.Ceiling(_cellWidth * scale));
            _cellHeight = Math.Max(_cellHeight, (int)Math.Ceiling(_cellHeight * scale));
        }
        _columns = (int)Math.Ceiling(_width / (double)_cellWidth);
        _rows = (int)Math.Ceiling(_height / (double)_cellHeight);
        int cellCount = _columns * _rows;
        _rainLevels = new byte[cellCount];
        _nextRainLevels = new byte[cellCount];
        _rainStyles = new byte[cellCount];
        _nextRainStyles = new byte[cellCount];
        _rainEmphasis = new float[cellCount];
        _nextRainEmphasis = new float[cellCount];
        _rainGlow = new float[cellCount];
        _nextRainGlow = new float[cellCount];
        _rainCovered = new bool[cellCount];
        _nextRainCovered = new bool[cellCount];
        _rainGlyphs = new ushort[cellCount];
        _nextRainGlyphs = new ushort[cellCount];
        _trailOccupied = new bool[cellCount];
        _trailSuppressImage = new bool[cellCount];
        _trailBornAt = new double[cellCount];
        _trailMemoryHoldSeconds = new float[cellCount];
        _trailMemoryFadeSeconds = new float[cellCount];
        _trailPulseHoldSeconds = new float[cellCount];
        _trailPulseFadeSeconds = new float[cellCount];
        _trailImpulseEnabled = new bool[cellCount];
        _trailGlowStrength = new float[cellCount];
        _trailBaseIntensity = new float[cellCount];
        _trailImageResistance = new float[cellCount];
        _trailGlyphs = new ushort[cellCount];
        _trailStreamIds = new long[cellCount];
        _imageLevels = new float[cellCount];
        _imageInitialLevels = new float[cellCount];
        _imageHoldSeconds = new float[cellCount];
        _imageFadeElapsed = new float[cellCount];
        _imageFadeSeconds = Enumerable.Repeat(1.0f, cellCount).ToArray();
        _imageGlyphs = new ushort[cellCount];
        _imageStyles = new byte[cellCount];
        // The previous tone map belongs to the old grid. Initial streams are
        // seeded below, so detach it before they can reveal any image cells.
        _imageMask = null;
        _maskDirty = true;
        _clockCells = new bool[cellCount];
        _instances = new GlyphInstance[cellCount];
        _streamsByColumn = new List<RainStream>[_columns];
        _spawners = new ColumnSpawner[_columns];
        for (int column = 0; column < _columns; column++)
        {
            _streamsByColumn[column] = [];
            _spawners[column] = new ColumnSpawner();
        }
        if (snapshot is null)
            SeedFreshGrid();
        else
            RestoreGridSnapshot(snapshot);
        RebuildClockLayout();
        if (snapshot is not null)
            RestoreClockSnapshot(snapshot);
    }

    private GridSnapshot? CaptureGridSnapshot()
    {
        int cellCount = _columns * _rows;
        if (_columns <= 0
            || _rows <= 0
            || _trailOccupied.Length != cellCount
            || _imageLevels.Length != cellCount
            || _streamsByColumn.Length != _columns)
        {
            return null;
        }

        return new GridSnapshot(
            _columns,
            _rows,
            _simulationTime,
            _nextStreamId,
            _trailOccupied,
            _trailSuppressImage,
            _trailBornAt,
            _trailMemoryHoldSeconds,
            _trailMemoryFadeSeconds,
            _trailPulseHoldSeconds,
            _trailPulseFadeSeconds,
            _trailImpulseEnabled,
            _trailGlowStrength,
            _trailBaseIntensity,
            _trailImageResistance,
            _trailGlyphs,
            _trailStreamIds,
            _imageLevels,
            _imageInitialLevels,
            _imageHoldSeconds,
            _imageFadeElapsed,
            _imageFadeSeconds,
            _imageGlyphs,
            _imageStyles,
            _streamsByColumn,
            _spawners,
            _clockDisplayedGlyphs,
            _clockBrightness);
    }

    private void SeedFreshGrid()
    {
        _simulationTime = 0;
        _nextStreamId = 0;
        for (int column = 0; column < _columns; column++)
        {
            if (_random.NextDouble() <= _settings.Density)
                SpawnStream(column, firstRun: true);
            ScheduleNextSpawn(_spawners[column], initial: true);
        }
    }

    private void RestoreGridSnapshot(GridSnapshot snapshot)
    {
        _simulationTime = snapshot.SimulationTime;
        _nextStreamId = snapshot.NextStreamId;
        RestoreTrailCells(snapshot);
        RestoreImageCells(snapshot);
        RestoreStreams(snapshot);
    }

    private void RestoreTrailCells(GridSnapshot snapshot)
    {
        for (int oldIndex = 0;
             oldIndex < snapshot.TrailOccupied.Length;
             oldIndex++)
        {
            if (!snapshot.TrailOccupied[oldIndex])
                continue;

            int oldRow = oldIndex / snapshot.Columns;
            int oldColumn = oldIndex - oldRow * snapshot.Columns;
            int column = MapCellCenter(oldColumn, snapshot.Columns, _columns);
            int row = MapCellCenter(oldRow, snapshot.Rows, _rows);
            int index = row * _columns + column;
            double candidateScore = TrailCellScore(
                snapshot.SimulationTime,
                snapshot.TrailBornAt[oldIndex],
                snapshot.TrailMemoryHoldSeconds[oldIndex],
                snapshot.TrailMemoryFadeSeconds[oldIndex],
                snapshot.TrailPulseHoldSeconds[oldIndex],
                snapshot.TrailPulseFadeSeconds[oldIndex],
                snapshot.TrailImpulseEnabled[oldIndex],
                snapshot.TrailBaseIntensity[oldIndex]);
            if (_trailOccupied[index])
            {
                double existingScore = TrailCellScore(
                    _simulationTime,
                    _trailBornAt[index],
                    _trailMemoryHoldSeconds[index],
                    _trailMemoryFadeSeconds[index],
                    _trailPulseHoldSeconds[index],
                    _trailPulseFadeSeconds[index],
                    _trailImpulseEnabled[index],
                    _trailBaseIntensity[index]);
                if (existingScore > candidateScore)
                    continue;
            }

            _trailOccupied[index] = true;
            _trailSuppressImage[index] =
                snapshot.TrailSuppressImage[oldIndex];
            _trailBornAt[index] = snapshot.TrailBornAt[oldIndex];
            _trailMemoryHoldSeconds[index] =
                snapshot.TrailMemoryHoldSeconds[oldIndex];
            _trailMemoryFadeSeconds[index] =
                snapshot.TrailMemoryFadeSeconds[oldIndex];
            _trailPulseHoldSeconds[index] =
                snapshot.TrailPulseHoldSeconds[oldIndex];
            _trailPulseFadeSeconds[index] =
                snapshot.TrailPulseFadeSeconds[oldIndex];
            _trailImpulseEnabled[index] =
                snapshot.TrailImpulseEnabled[oldIndex];
            _trailGlowStrength[index] =
                snapshot.TrailGlowStrength[oldIndex];
            _trailBaseIntensity[index] =
                snapshot.TrailBaseIntensity[oldIndex];
            _trailImageResistance[index] =
                snapshot.TrailImageResistance[oldIndex];
            _trailGlyphs[index] = snapshot.TrailGlyphs[oldIndex];
            _trailStreamIds[index] = snapshot.TrailStreamIds[oldIndex];
        }
    }

    private void RestoreImageCells(GridSnapshot snapshot)
    {
        bool refining = _columns >= snapshot.Columns
            && _rows >= snapshot.Rows;
        if (refining)
        {
            for (int row = 0; row < _rows; row++)
            {
                int oldRow = MapCellCenter(row, _rows, snapshot.Rows);
                for (int column = 0; column < _columns; column++)
                {
                    int oldColumn = MapCellCenter(
                        column,
                        _columns,
                        snapshot.Columns);
                    int oldIndex = oldRow * snapshot.Columns + oldColumn;
                    if (snapshot.ImageLevels[oldIndex] <= 0.01f)
                        continue;
                    CopyImageCell(snapshot, oldIndex, row * _columns + column);
                }
            }
            return;
        }

        for (int oldIndex = 0;
             oldIndex < snapshot.ImageLevels.Length;
             oldIndex++)
        {
            float candidateLevel = snapshot.ImageLevels[oldIndex];
            if (candidateLevel <= 0.01f)
                continue;
            int oldRow = oldIndex / snapshot.Columns;
            int oldColumn = oldIndex - oldRow * snapshot.Columns;
            int column = MapCellCenter(oldColumn, snapshot.Columns, _columns);
            int row = MapCellCenter(oldRow, snapshot.Rows, _rows);
            int index = row * _columns + column;
            if (_imageLevels[index] > candidateLevel)
                continue;
            CopyImageCell(snapshot, oldIndex, index);
        }
    }

    private void CopyImageCell(
        GridSnapshot snapshot,
        int oldIndex,
        int newIndex)
    {
        _imageLevels[newIndex] = snapshot.ImageLevels[oldIndex];
        _imageInitialLevels[newIndex] =
            snapshot.ImageInitialLevels[oldIndex];
        _imageHoldSeconds[newIndex] =
            snapshot.ImageHoldSeconds[oldIndex];
        _imageFadeElapsed[newIndex] =
            snapshot.ImageFadeElapsed[oldIndex];
        _imageFadeSeconds[newIndex] =
            snapshot.ImageFadeSeconds[oldIndex];
        _imageGlyphs[newIndex] = snapshot.ImageGlyphs[oldIndex];
        _imageStyles[newIndex] = snapshot.ImageStyles[oldIndex];
    }

    private void RestoreStreams(GridSnapshot snapshot)
    {
        bool[] mappedSpawners = new bool[_columns];
        for (int oldColumn = 0;
             oldColumn < snapshot.StreamsByColumn.Length;
             oldColumn++)
        {
            int column = MapCellCenter(
                oldColumn,
                snapshot.Columns,
                _columns);
            foreach (RainStream oldStream in snapshot.StreamsByColumn[oldColumn])
            {
                _streamsByColumn[column].Add(
                    ScaleStream(oldStream, snapshot.Rows));
            }

            if ((uint)oldColumn >= (uint)snapshot.Spawners.Length)
                continue;
            double nextSpawnAt = snapshot.Spawners[oldColumn].NextSpawnAt;
            if (!mappedSpawners[column]
                || nextSpawnAt < _spawners[column].NextSpawnAt)
            {
                _spawners[column].NextSpawnAt = nextSpawnAt;
                mappedSpawners[column] = true;
            }
        }

        for (int column = 0; column < _columns; column++)
        {
            if (!mappedSpawners[column])
                ScheduleNextSpawn(_spawners[column], initial: true);
        }
    }

    private RainStream ScaleStream(RainStream source, int oldRows)
    {
        double rowScale = _rows / (double)Math.Max(1, oldRows);
        int lastWrittenRow = source.LastWrittenRow < 0
            ? -1
            : Math.Max(
                -1,
                (int)Math.Floor((source.LastWrittenRow + 1) * rowScale) - 1);
        return new RainStream
        {
            Id = source.Id,
            Head = source.Head * rowScale,
            PreviousHead = source.PreviousHead * rowScale,
            Speed = Math.Max(0.1, source.Speed * rowScale),
            Length = Math.Max(0, (int)Math.Round(source.Length * rowScale)),
            MemoryHoldSeconds = source.MemoryHoldSeconds,
            MemoryFadeSeconds = source.MemoryFadeSeconds,
            ImpulseHoldSeconds = source.ImpulseHoldSeconds,
            ImpulseFadeSeconds = source.ImpulseFadeSeconds,
            ImpulseEnabled = source.ImpulseEnabled,
            SignalStrength = source.SignalStrength,
            GlowStrength = source.GlowStrength,
            TerminationRow = source.TerminationRow * rowScale,
            Seed = source.Seed,
            LastWrittenRow = lastWrittenRow
        };
    }

    private void RestoreClockSnapshot(GridSnapshot snapshot)
    {
        int count = Math.Min(
            _clockDisplayedGlyphs.Length,
            snapshot.ClockDisplayedGlyphs.Length);
        for (int index = 0; index < count; index++)
        {
            _clockDisplayedGlyphs[index] =
                snapshot.ClockDisplayedGlyphs[index];
            if (index < snapshot.ClockBrightness.Length)
            {
                _clockBrightness[index] =
                    snapshot.ClockBrightness[index];
            }
        }
    }

    private static int MapCellCenter(
        int coordinate,
        int sourceSize,
        int targetSize) =>
        Math.Clamp(
            (int)Math.Floor(
                (coordinate + 0.5)
                / Math.Max(1, sourceSize)
                * targetSize),
            0,
            Math.Max(0, targetSize - 1));

    private static double TrailCellScore(
        double simulationTime,
        double bornAt,
        double memoryHoldSeconds,
        double memoryFadeSeconds,
        double pulseHoldSeconds,
        double pulseFadeSeconds,
        bool impulseEnabled,
        double baseIntensity)
    {
        double age = Math.Max(0, simulationTime - bornAt);
        double memory = TrailMemoryModel.RemainingBrightness(
            age,
            memoryHoldSeconds,
            memoryFadeSeconds);
        double impulse = impulseEnabled
            ? HeadImpulseModel.Emphasis(
                age,
                pulseHoldSeconds,
                pulseFadeSeconds)
            : 0;
        return Math.Max(
            memory * Math.Clamp(baseIntensity, 0, 1),
            impulse);
    }

    private void RebuildClockLayout()
    {
        if (_clockCells.Length != _columns * _rows)
            _clockCells = new bool[_columns * _rows];
        else
            Array.Clear(_clockCells);
        _clockCellIndices = [];
        _clockDisplayedGlyphs = [];
        _clockBrightness = [];
        _clockTargetText = "";
        _clockTargetMinute = -1;
        if (!_settings.ClockEnabled || _columns < 5 || _rows < 1)
            return;

        int layoutColumns = Math.Clamp(_width / _cellWidth, 5, _columns);
        int layoutRows = Math.Clamp(_height / _cellHeight, 1, _rows);
        int horizontalMargin = Math.Clamp(
            _settings.ClockHorizontalMarginCells,
            0,
            Math.Max(0, layoutColumns - 5));
        int verticalMargin = Math.Clamp(
            _settings.ClockVerticalMarginCells,
            0,
            Math.Max(0, layoutRows - 1));
        int column = Math.Max(0, (layoutColumns - 5) / 2);
        int row = Math.Max(0, (layoutRows - 1) / 2);
        switch (_settings.ClockPosition)
        {
            case "Top":
                row = verticalMargin;
                break;
            case "TopRight":
                column = layoutColumns - 5 - horizontalMargin;
                row = verticalMargin;
                break;
            case "Right":
                column = layoutColumns - 5 - horizontalMargin;
                break;
            case "BottomRight":
                column = layoutColumns - 5 - horizontalMargin;
                row = layoutRows - 1 - verticalMargin;
                break;
            case "Bottom":
                row = layoutRows - 1 - verticalMargin;
                break;
            case "BottomLeft":
                column = horizontalMargin;
                row = layoutRows - 1 - verticalMargin;
                break;
            case "Left":
                column = horizontalMargin;
                break;
            case "TopLeft":
                column = horizontalMargin;
                row = verticalMargin;
                break;
        }

        column = Math.Clamp(column, 0, layoutColumns - 5);
        row = Math.Clamp(row, 0, layoutRows - 1);
        DateTime now = DateTime.Now;
        _clockTargetMinute = now.Hour * 60 + now.Minute;
        _clockTargetText = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        _clockCellIndices = new int[5];
        _clockDisplayedGlyphs = new ushort[5];
        _clockBrightness = new double[5];
        int minimumClockLevel = MinimumClockLevel();
        for (int slot = 0; slot < 5; slot++)
        {
            _clockCellIndices[slot] = row * _columns + column + slot;
            _clockCells[_clockCellIndices[slot]] = true;
            _clockDisplayedGlyphs[slot] = (ushort)Math.Max(
                0,
                MatrixGlyphSet.Glyphs.IndexOf(
                    _clockTargetText[slot],
                    StringComparison.Ordinal));
            _clockBrightness[slot] = minimumClockLevel;
        }
    }

    private void SpawnStream(int column, bool firstRun)
    {
        double speedPosition = FlowCurveMath.SampleLookup(
            _speedDistributionLookup,
            _random.NextDouble());
        double screenHeightsPerSecond = _settings.SpeedMin
            + speedPosition * (_settings.SpeedMax - _settings.SpeedMin);
        double lengthPosition = FlowCurveMath.SampleLookup(
            _lengthDistributionLookup,
            _random.NextDouble());
        double trailLength = _settings.TrailLengthMin
            + lengthPosition * (_settings.TrailLengthMax - _settings.TrailLengthMin);
        double filterPosition = FlowCurveMath.SampleLookup(
            _filterDistributionLookup,
            _random.NextDouble());
        double lifetime = _settings.StreamLifetimeMin
            + filterPosition
                * (_settings.StreamLifetimeMax - _settings.StreamLifetimeMin);
        int length = Math.Max(0, (int)Math.Round(_rows * trailLength));
        double terminationRow = Math.Clamp(
            _rows * lifetime,
            1.0,
            _rows + 1.0);
        double head = firstRun
            ? _random.NextDouble() * (terminationRow + length) - length
            : -1.0 - _random.NextDouble() * Math.Min(_rows * 0.12, length * 0.28);
        double speed = _rows * screenHeightsPerSecond;
        double memoryPosition = FlowCurveMath.SampleLookup(
            _memoryDistributionLookup,
            _random.NextDouble());
        double memoryDuration = _settings.MemoryDurationMin
            + memoryPosition
                * (_settings.MemoryDurationMax - _settings.MemoryDurationMin);
        TrailMemoryTiming memory = TrailMemoryModel.Create(
            memoryDuration,
            length / Math.Max(0.1, speed));
        double signalPosition = FlowCurveMath.SampleLookup(
            _signalDistributionLookup,
            _random.NextDouble());
        double signalStrength = _settings.SignalStrengthMin
            + signalPosition
                * (_settings.SignalStrengthMax - _settings.SignalStrengthMin);
        signalStrength = SignalModel.QuantizeStrength(signalStrength);
        bool impulseEnabled = _random.NextDouble()
            < _settings.HeadImpulseProbability;
        HeadImpulseTiming impulse = impulseEnabled
            ? HeadImpulseModel.Create(
                _settings.HeadImpulseDecay,
                length,
                speed)
            : default;
        double glowStrength = _random.NextDouble() < _settings.SignalGlowKeys
            ? _settings.SignalGlowPriority
            : 0.0;
        RainStream stream = new()
        {
            Id = ++_nextStreamId,
            Head = head,
            PreviousHead = head,
            Speed = speed,
            Length = length,
            MemoryHoldSeconds = memory.HoldSeconds,
            MemoryFadeSeconds = memory.FadeSeconds,
            ImpulseHoldSeconds = impulse.HoldSeconds,
            ImpulseFadeSeconds = impulse.FadeSeconds,
            ImpulseEnabled = impulseEnabled,
            SignalStrength = signalStrength,
            GlowStrength = glowStrength,
            Seed = (uint)_random.Next(),
            LastWrittenRow = (int)Math.Floor(head),
            TerminationRow = terminationRow
        };
        _streamsByColumn[column].Add(stream);
        if (firstRun)
            SeedInitialTrail(column, stream);
    }

    private void ScheduleNextSpawn(ColumnSpawner spawner, bool initial)
    {
        double averageScreenHeightsPerSecond = _settings.SpeedMin
            + _averageSpeedDistribution
                * (_settings.SpeedMax - _settings.SpeedMin);
        double averageSpeed = _rows * averageScreenHeightsPerSecond;
        double traversal = _rows / Math.Max(0.1, averageSpeed);
        // Every column receives spawn opportunities at the same cadence.
        // Density is the chance for an idle channel; interception is the
        // chance for an occupied one. This makes 0% mean no competing heads
        // and 100% use every opportunity without coupling the two controls.
        double interval = Math.Max(
            0.30,
            traversal
                * 0.30
                * (0.78 + _random.NextDouble() * 0.44));
        spawner.NextSpawnAt = initial
            ? _simulationTime + _random.NextDouble() * interval
            : _simulationTime + interval;
    }

    private void ConsumeCaughtStreams(int column, List<RainStream> streams)
    {
        if (streams.Count < 2)
            return;

        HashSet<long>? consumed = null;
        for (int newerIndex = streams.Count - 1; newerIndex > 0; newerIndex--)
        {
            RainStream newer = streams[newerIndex];
            for (int olderIndex = newerIndex - 1; olderIndex >= 0; olderIndex--)
            {
                RainStream older = streams[olderIndex];
                if (newer.Id <= older.Id
                    || newer.Speed <= older.Speed
                    || newer.PreviousHead >= older.PreviousHead
                    || newer.Head < older.Head)
                {
                    continue;
                }
                (consumed ??= []).Add(older.Id);
            }
        }
        if (consumed is not null)
        {
            foreach (long streamId in consumed)
                ClearOwnedTrailCells(column, streamId);
            streams.RemoveAll(stream => consumed.Contains(stream.Id));
        }
    }

    private void ClearOwnedTrailCells(int column, long streamId)
    {
        for (int row = 0; row < _rows; row++)
        {
            int index = row * _columns + column;
            if (_trailStreamIds[index] == streamId)
                ClearTrailCell(index);
        }
    }

    private void ClearTrailCell(int index)
    {
        _trailOccupied[index] = false;
        _trailSuppressImage[index] = false;
        _trailBornAt[index] = 0;
        _trailMemoryHoldSeconds[index] = 0;
        _trailMemoryFadeSeconds[index] = 0;
        _trailPulseHoldSeconds[index] = 0;
        _trailPulseFadeSeconds[index] = 0;
        _trailImpulseEnabled[index] = false;
        _trailGlowStrength[index] = 0;
        _trailBaseIntensity[index] = 0;
        _trailImageResistance[index] = 0;
        _trailGlyphs[index] = 0;
        _trailStreamIds[index] = 0;
    }

    private void RebuildAtlas()
    {
        GlyphAtlasData atlas = GlyphAtlasBuilder.Build(
            _referenceWindow,
            _settings,
            _cellWidth,
            _cellHeight,
            _dpiScale);
        RebuildImageGlyphDensities(atlas);
        _pendingAtlas = atlas;
    }

    private void RebuildImageGlyphDensities(GlyphAtlasData atlas)
    {
        GlyphDensity[][] result = new GlyphDensity[3][];
        for (int tier = 0; tier < result.Length; tier++)
        {
            int style = ImageAtlasStyleRows[tier];
            int offset = style * atlas.GlyphCount;
            if (style >= atlas.StyleCount
                || offset < 0
                || offset + atlas.GlyphCount > atlas.InkCoverage.Length)
            {
                result[tier] = [];
                continue;
            }

            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (int glyph = 0; glyph < atlas.GlyphCount; glyph++)
            {
                float density = atlas.InkCoverage[offset + glyph];
                minimum = Math.Min(minimum, density);
                maximum = Math.Max(maximum, density);
            }

            double spread = Math.Max(0.000001, maximum - minimum);
            result[tier] = Enumerable.Range(0, atlas.GlyphCount)
                .Select(glyph => new GlyphDensity(
                    (ushort)glyph,
                    (atlas.InkCoverage[offset + glyph] - minimum) / spread))
                .OrderBy(item => item.Density)
                .ToArray();
        }
        _imageGlyphDensities = result;
    }

    private void ClearImageOverlay()
    {
        if (_imageLevels.Length > 0)
            Array.Clear(_imageLevels);
        if (_imageInitialLevels.Length > 0)
            Array.Clear(_imageInitialLevels);
        if (_imageHoldSeconds.Length > 0)
            Array.Clear(_imageHoldSeconds);
        if (_imageFadeElapsed.Length > 0)
            Array.Clear(_imageFadeElapsed);
        if (_imageGlyphs.Length > 0)
            Array.Clear(_imageGlyphs);
        if (_imageStyles.Length > 0)
            Array.Clear(_imageStyles);
    }

    private void EnsureImageMask()
    {
        if (!_maskDirty)
            return;

        _maskDirty = false;
        _imageMask = null;
        if (_image is null || _columns <= 0 || _rows <= 0)
            return;

        try
        {
            int sourceWidth = _image.Width;
            int sourceHeight = _image.Height;
            double scaleX = _width / (double)sourceWidth;
            double scaleY = _height / (double)sourceHeight;
            double scale = _settings.ImageFit == "Fill"
                ? Math.Max(scaleX, scaleY)
                : Math.Min(scaleX, scaleY);
            double drawnWidth = sourceWidth * scale;
            double drawnHeight = sourceHeight * scale;
            double offsetX = (_width - drawnWidth) * 0.5;
            double offsetY = (_height - drawnHeight) * 0.5;
            byte[] mask = new byte[_columns * _rows];
            double[] offsets = [0.25, 0.75];
            for (int row = 0; row < _rows; row++)
            {
                for (int column = 0; column < _columns; column++)
                {
                    double total = 0;
                    int samples = 0;
                    foreach (double yOffset in offsets)
                    {
                        foreach (double xOffset in offsets)
                        {
                            samples++;
                            double sampleX = (column + xOffset) * _cellWidth;
                            double sampleY = (row + yOffset) * _cellHeight;
                            double sourceX = (sampleX - offsetX) / scale;
                            double sourceY = (sampleY - offsetY) / scale;
                            if (sourceX < 0
                                || sourceX >= sourceWidth
                                || sourceY < 0
                                || sourceY >= sourceHeight)
                            {
                                continue;
                            }
                            total += SamplePreparedTone(_image, sourceX, sourceY);
                        }
                    }
                    mask[row * _columns + column] = samples == 0
                        ? (byte)0
                        : (byte)Math.Clamp((int)Math.Round(total / samples), 0, 255);
                }
            }
            _imageMask = mask;
        }
        catch
        {
            _imageMask = null;
        }
    }

    private static double SamplePreparedTone(
        PreparedImage image,
        double x,
        double y)
    {
        double clampedX = Math.Clamp(x - 0.5, 0, image.Width - 1.0);
        double clampedY = Math.Clamp(y - 0.5, 0, image.Height - 1.0);
        int left = (int)Math.Floor(clampedX);
        int top = (int)Math.Floor(clampedY);
        int right = Math.Min(image.Width - 1, left + 1);
        int bottom = Math.Min(image.Height - 1, top + 1);
        double horizontal = clampedX - left;
        double vertical = clampedY - top;
        double topTone = image.ToneMap[top * image.Width + left]
            + (image.ToneMap[top * image.Width + right]
                - image.ToneMap[top * image.Width + left]) * horizontal;
        double bottomTone = image.ToneMap[bottom * image.Width + left]
            + (image.ToneMap[bottom * image.Width + right]
                - image.ToneMap[bottom * image.Width + left]) * horizontal;
        return topTone + (bottomTone - topTone) * vertical;
    }

    private void RebuildCurveLookups()
    {
        double[] speed = FlowCurveMath.BuildLookup(
            _settings.SpeedCurve,
            increasing: true,
            _settings.SpeedCurveAdjustment,
            invertVerticalShift: true);
        double[] length = FlowCurveMath.BuildLookup(
            _settings.TrailLengthCurve,
            increasing: true,
            _settings.TrailLengthCurveAdjustment);
        double[] signal = FlowCurveMath.BuildLookup(
            _settings.SignalCurve,
            increasing: true,
            _settings.SignalCurveAdjustment);
        double[] filter = FlowCurveMath.BuildLookup(
            _settings.StreamFilterCurve,
            increasing: true,
            _settings.StreamFilterCurveAdjustment,
            invertVerticalShift: true);
        double[] memory = FlowCurveMath.BuildLookup(
            _settings.MemoryCurve,
            increasing: true,
            _settings.MemoryCurveAdjustment);
        _speedDistributionLookup = speed;
        _lengthDistributionLookup = length;
        _signalDistributionLookup = signal;
        _filterDistributionLookup = filter;
        _memoryDistributionLookup = memory;
        _averageSpeedDistribution = _speedDistributionLookup.Length == 0
            ? 0.5
            : _speedDistributionLookup.Average();
        _averageLengthDistribution = _lengthDistributionLookup.Length == 0
            ? 0.5
            : _lengthDistributionLookup.Average();
    }

    private static int CalculateLevel(double intensity) =>
        Math.Clamp((int)Math.Round(intensity * PaletteLevels), 0, PaletteLevels);

    private static double EdgeShade(double position)
    {
        double fromCenter = Math.Abs(position - 0.5) * 2.0;
        return 1.0 - 0.34 * fromCenter * fromCenter;
    }

    private static bool CurveAdjustmentEquivalent(
        CurveAdjustment left,
        CurveAdjustment right) =>
        Math.Abs(left.Character - right.Character) <= 0.0001
        && Math.Abs(left.HorizontalShift - right.HorizontalShift) <= 0.0001
        && Math.Abs(left.VerticalShift - right.VerticalShift) <= 0.0001;

    private static uint Hash(uint a, uint b, uint c)
    {
        uint value = a * 0x9E3779B1u ^ b * 0x85EBCA77u ^ c;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }

    private static double UnitHash(uint value) => value / 4294967296.0;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _image = null;
        _imageMask = null;
        _instances = [];
    }

    private sealed class RainStream
    {
        public long Id;
        public double Head;
        public double PreviousHead;
        public double Speed;
        public int Length;
        public double MemoryHoldSeconds;
        public double MemoryFadeSeconds;
        public double ImpulseHoldSeconds;
        public double ImpulseFadeSeconds;
        public bool ImpulseEnabled;
        public double SignalStrength;
        public double GlowStrength;
        public double TerminationRow;
        public uint Seed;
        public int LastWrittenRow;
    }

    private sealed class ColumnSpawner
    {
        public double NextSpawnAt;
    }

    private sealed record GridSnapshot(
        int Columns,
        int Rows,
        double SimulationTime,
        long NextStreamId,
        bool[] TrailOccupied,
        bool[] TrailSuppressImage,
        double[] TrailBornAt,
        float[] TrailMemoryHoldSeconds,
        float[] TrailMemoryFadeSeconds,
        float[] TrailPulseHoldSeconds,
        float[] TrailPulseFadeSeconds,
        bool[] TrailImpulseEnabled,
        float[] TrailGlowStrength,
        float[] TrailBaseIntensity,
        float[] TrailImageResistance,
        ushort[] TrailGlyphs,
        long[] TrailStreamIds,
        float[] ImageLevels,
        float[] ImageInitialLevels,
        float[] ImageHoldSeconds,
        float[] ImageFadeElapsed,
        float[] ImageFadeSeconds,
        ushort[] ImageGlyphs,
        byte[] ImageStyles,
        List<RainStream>[] StreamsByColumn,
        ColumnSpawner[] Spawners,
        ushort[] ClockDisplayedGlyphs,
        double[] ClockBrightness);

    private readonly record struct GlyphDensity(ushort Glyph, double Density);
}
