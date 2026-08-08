using System.Diagnostics;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;
using WallpaperMatrix.Services;

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
    private readonly Random _random;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private AppSettings _settings;
    private PreparedImage? _image;
    private MatrixImageProjection _imageProjection;
    private byte[]? _imageMask;
    private byte[]? _imageInfluenceMask;
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
    private long[] _trailGenerations = [];
    private uint[] _trailRevealSeeds = [];
    private long[] _observedTrailGenerations = [];
    private float[] _imageLevels = [];
    private float[] _imageInitialLevels = [];
    private float[] _imageHoldSeconds = [];
    private float[] _imageFadeElapsed = [];
    private float[] _imageFadeSeconds = [];
    private ushort[] _imageGlyphs = [];
    private byte[] _imageStyles = [];
    private long[] _imageStreamIds = [];
    private GlyphDensity[][] _imageGlyphDensities = [[], [], []];
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
    private long _lastSlowFrameReportTimestamp;
    private bool _seedFreshStreams;
    private MatrixSceneRenderer? _flowSource;
    private double _motionScale = 1.0;

    public MatrixSceneRenderer(
        IntPtr referenceWindow,
        SharedMatrixScene scene,
        AppSettings settings,
        int? randomSeed = null,
        bool seedInitialStreams = true)
    {
        _referenceWindow = referenceWindow;
        _scene = scene;
        _width = scene.Width;
        _height = scene.Height;
        _imageProjection = new MatrixImageProjection(
            _width,
            _height,
            new System.Drawing.Rectangle(0, 0, _width, _height),
            new System.Drawing.Rectangle(0, 0, _width, _height));
        _settings = settings.Copy();
        _seedFreshStreams = seedInitialStreams;
        _random = randomSeed.HasValue
            ? new Random(randomSeed.Value)
            : new Random();
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
            || Math.Abs(settings.HeadWeight - _settings.HeadWeight) > 0.01;
        bool gridChanged = gridGeometryChanged;
        bool maskChanged = gridChanged || settings.ImageFit != _settings.ImageFit;
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
        if (atlasChanged)
            RebuildAtlas();
        if (maskChanged)
            _maskDirty = true;
        if (spawnCadenceChanged || curvesChanged)
        {
            foreach (ColumnSpawner spawner in _spawners)
                ScheduleNextSpawn(spawner, initial: true);
        }
    }

    public void SetImage(PreparedImage? image)
    {
        _image = image;
        _maskDirty = true;
    }

    public void SetImage(
        PreparedImage? image,
        MatrixImageProjection projection)
    {
        _image = image;
        _imageProjection = projection;
        _maskDirty = true;
    }

    public void ResetImageOverlay(PreparedImage? image)
    {
        _image = image;
        _maskDirty = true;
        ClearImageOverlay();
    }

    public bool RenderIfDue(bool paused) =>
        RenderIfDue(paused, _clock.Elapsed);

    public void FollowFlowFrom(MatrixSceneRenderer? source)
    {
        if (ReferenceEquals(source, this))
            source = null;
        if (ReferenceEquals(_flowSource, source))
            return;

        _flowSource = source;
        if (source is null)
        {
            Array.Clear(_observedTrailGenerations);
            return;
        }

        if (_observedTrailGenerations.Length != _trailGenerations.Length)
            _observedTrailGenerations = new long[_trailGenerations.Length];
        int count = Math.Min(
            _observedTrailGenerations.Length,
            source._trailGenerations.Length);
        Array.Copy(
            source._trailGenerations,
            _observedTrailGenerations,
            count);
    }

    /// <summary>
    /// Creates a view-local image layer for the system attack. The layer owns
    /// no stream simulation and no glyph atlas: it observes deposits from this
    /// renderer and publishes only the cells that can be visible through the
    /// requested source viewport.
    /// </summary>
    public AttackImageLayerRenderer CreateAttackImageLayer(
        SharedMatrixScene scene,
        AppSettings settings,
        PreparedImage? image,
        MatrixImageProjection projection,
        System.Drawing.Rectangle sourceBounds,
        long minimumStreamId) =>
        new(
            this,
            scene,
            settings,
            image,
            projection,
            sourceBounds,
            minimumStreamId);

    public void SetMotionScale(double scale) =>
        Volatile.Write(
            ref _motionScale,
            Math.Clamp(scale, 0.0, 1.0));

    public void ImportStateFrom(
        MatrixSceneRenderer source,
        System.Drawing.Rectangle sourceCanvas,
        System.Drawing.Rectangle targetCanvas)
    {
        GridSnapshot? snapshot = source.CaptureGridSnapshot();
        if (snapshot is null)
            return;

        ClearGridState();
        RestoreGridSnapshot(
            snapshot,
            new SpatialRestoreMap(sourceCanvas, targetCanvas));
        PublishScene();
    }

    public void RefreshPublishedScene()
    {
        BuildRainCells();
        PublishScene();
    }

    public bool RenderIfDue(bool paused, TimeSpan now)
    {
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
        dt *= Volatile.Read(ref _motionScale);
        long frameStartedAt = Stopwatch.GetTimestamp();
        if (_settings.ImageMode && _image is not null)
            EnsureImageMask();
        long maskFinishedAt = Stopwatch.GetTimestamp();
        FadeImageCells(dt);
        long fadeFinishedAt = Stopwatch.GetTimestamp();
        if (_flowSource is null)
            AdvanceStreams(dt);
        else
            SynchronizeFlowDeposits(_flowSource);
        long streamsFinishedAt = Stopwatch.GetTimestamp();
        BuildRainCells();
        long cellsFinishedAt = Stopwatch.GetTimestamp();
        PublishScene();
        ReportSlowFrame(
            frameStartedAt,
            maskFinishedAt,
            fadeFinishedAt,
            streamsFinishedAt,
            cellsFinishedAt,
            Stopwatch.GetTimestamp());
        return true;
    }

    private void ReportSlowFrame(
        long frameStartedAt,
        long maskFinishedAt,
        long fadeFinishedAt,
        long streamsFinishedAt,
        long cellsFinishedAt,
        long frameFinishedAt)
    {
        TimeSpan total = Stopwatch.GetElapsedTime(
            frameStartedAt,
            frameFinishedAt);
        if (total < TimeSpan.FromMilliseconds(120))
            return;

        long previous = _lastSlowFrameReportTimestamp;
        if (previous != 0
            && Stopwatch.GetElapsedTime(previous, frameFinishedAt)
                < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastSlowFrameReportTimestamp = frameFinishedAt;
        static double Milliseconds(long start, long end) =>
            Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
        DiagnosticLog.Write(
            $"Медленный кадр симуляции: "
            + $"всего={total.TotalMilliseconds:0} мс; "
            + $"маска={Milliseconds(frameStartedAt, maskFinishedAt):0} мс; "
            + $"образ={Milliseconds(maskFinishedAt, fadeFinishedAt):0} мс; "
            + $"струи={Milliseconds(fadeFinishedAt, streamsFinishedAt):0} мс; "
            + $"ячейки={Milliseconds(streamsFinishedAt, cellsFinishedAt):0} мс; "
            + $"публикация={Milliseconds(cellsFinishedAt, frameFinishedAt):0} мс.");
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
        MatrixSceneRenderer flow = _flowSource ?? this;
        Array.Clear(_nextRainLevels);
        Array.Clear(_nextRainStyles);
        Array.Clear(_nextRainEmphasis);
        Array.Clear(_nextRainGlow);
        Array.Clear(_nextRainCovered);
        Array.Clear(_nextRainGlyphs);
        for (int index = 0; index < flow._trailOccupied.Length; index++)
        {
            if (!flow._trailOccupied[index])
                continue;

            int row = index / _columns;
            int column = index - row * _columns;
            double horizontalShade = EdgeShade((column + 0.5) / _columns);
            double age = Math.Max(
                0,
                flow._simulationTime - flow._trailBornAt[index]);
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
            double emphasis = flow._trailImpulseEnabled[index]
                ? HeadImpulseModel.Emphasis(
                    age,
                    flow._trailPulseHoldSeconds[index],
                    flow._trailPulseFadeSeconds[index])
                : 0.0;

            double baseFade = double.IsPositiveInfinity(effectiveAge)
                ? 0.0
                : TrailMemoryModel.RemainingBrightness(
                    effectiveAge,
                    flow._trailMemoryHoldSeconds[index],
                    flow._trailMemoryFadeSeconds[index]);
            bool imageCell = _imageLevels[index] > 0.01f
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
                flow._trailGlowStrength[index] + _settings.HeadGlow * emphasis,
                0.0,
                2.0);
            if (_trailSuppressImage[index])
                continue;

            double baseIntensity = flow._trailBaseIntensity[index]
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
                _nextRainGlyphs[index] = flow._trailGlyphs[index];
                _nextRainStyles[index] = emphasis > 0.001 ? (byte)1 : (byte)0;
            }
            _nextRainEmphasis[index] = (float)emphasis;
        }

        (_rainLevels, _nextRainLevels) = (_nextRainLevels, _rainLevels);
        (_rainStyles, _nextRainStyles) = (_nextRainStyles, _rainStyles);
        (_rainEmphasis, _nextRainEmphasis) = (_nextRainEmphasis, _rainEmphasis);
        (_rainGlow, _nextRainGlow) = (_nextRainGlow, _rainGlow);
        (_rainGlyphs, _nextRainGlyphs) = (_nextRainGlyphs, _rainGlyphs);
        (_rainCovered, _nextRainCovered) = (_nextRainCovered, _rainCovered);
    }

    private void PublishScene()
    {
        MatrixSceneRenderer flow = _flowSource ?? this;
        int count = 0;
        for (int index = 0; index < _rainLevels.Length; index++)
        {
            int row = index / _columns;
            int column = index - row * _columns;
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
                    _rainGlow[index],
                    flow._trailStreamIds[index]);
                continue;
            }

            if (_rainCovered[index] || _imageLevels[index] <= 0)
                continue;
            AddInstance(
                ref count,
                column,
                row,
                _imageGlyphs[index],
                _imageLevels[index] / (double)PaletteLevels,
                _imageStyles[index],
                emphasis: 0,
                glow: 0,
                streamId: _imageStreamIds[index]);
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
            _scene.Publish(
                _instances,
                count,
                _pendingAtlas,
                parameters,
                flow._nextStreamId);
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
        double glow,
        long streamId)
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
            glow,
            streamId);
    }

    private void AdvanceStreams(double dt)
    {
        _simulationTime += dt;

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

        int index = row * _columns + column;
        bool imageInfluence = HasImageInfluenceAt(index);
        if (imageInfluence)
        {
            ReplaceImageCell(
                column,
                row,
                stream.Seed,
                stream.MemoryFadeSeconds,
                stream.Id);
        }
        if (!imageInfluence)
            ClearImageCell(index);
        uint noise = Hash((uint)column, (uint)row, stream.Seed);
        bool imageCell = _imageLevels[index] > 0.01f
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
        _trailRevealSeeds[index] = stream.Seed;
        _trailGenerations[index]++;
    }

    private void SynchronizeFlowDeposits(MatrixSceneRenderer source)
    {
        int count = Math.Min(
            _trailImageResistance.Length,
            source._trailGenerations.Length);
        if (_observedTrailGenerations.Length != _trailImageResistance.Length)
            _observedTrailGenerations = new long[_trailImageResistance.Length];

        for (int index = 0; index < count; index++)
        {
            long generation = source._trailGenerations[index];
            if (_observedTrailGenerations[index] == generation)
                continue;
            _observedTrailGenerations[index] = generation;
            if (!source._trailOccupied[index])
                continue;
            int row = index / _columns;
            int column = index - row * _columns;
            uint revealSeed = source._trailRevealSeeds[index];
            bool imageInfluence = HasImageInfluenceAt(index);
            if (imageInfluence)
            {
                ReplaceImageCell(
                    column,
                    row,
                    revealSeed,
                    source._trailMemoryFadeSeconds[index],
                    source._trailStreamIds[index]);
            }
            else
            {
                ClearImageCell(index);
            }

            uint noise = Hash((uint)column, (uint)row, revealSeed);
            bool imageCell = _imageLevels[index] > 0.01f
                && _imageStyles[index] >= 3;
            bool deliberateGap =
                (noise & 31) == 0 || ((noise >> 5) & 63) == 0;
            _trailSuppressImage[index] = deliberateGap && !imageCell;
            _trailImageResistance[index] = imageInfluence
                ? (float)_settings.ImageResistance
                : 0.0f;
        }
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

    private void FadeImageCells(double dt)
    {
        if (dt <= 0 || _imageLevels.Length == 0)
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
            _imageStreamIds[index] = 0;
        }
    }

    private void ReplaceImageCell(
        int column,
        int row,
        uint revealSeed,
        double fadeSeconds,
        long streamId)
    {
        int index = row * _columns + column;
        ImageCellReveal reveal = ResolveImageCell(
            _imageMask![index],
            column,
            row,
            revealSeed,
            fadeSeconds,
            streamId,
            _settings,
            _imageGlyphDensities,
            _columns,
            _rows);
        _imageLevels[index] = reveal.Level;
        _imageInitialLevels[index] = reveal.Level;
        _imageHoldSeconds[index] = reveal.HoldSeconds;
        _imageFadeElapsed[index] = 0;
        _imageFadeSeconds[index] = reveal.FadeSeconds;
        _imageGlyphs[index] = reveal.Glyph;
        _imageStyles[index] = reveal.Style;
        _imageStreamIds[index] = reveal.StreamId;
    }

    private static ImageCellReveal ResolveImageCell(
        byte sourceToneByte,
        int column,
        int row,
        uint revealSeed,
        double fadeSeconds,
        long streamId,
        AppSettings settings,
        GlyphDensity[][] glyphDensities,
        int columns,
        int rows)
    {
        double sourceTone = sourceToneByte / 255.0;
        double tone = ShapeImageTone(
            sourceTone,
            settings.ImageExpressiveness,
            settings.ImageToneCalmness);
        uint cellHash = Hash((uint)column, (uint)row, 0xC0DEF00Du);
        double coverage = Math.Min(1.0, tone * 1.18);
        double threshold = (
            Bayer4[((row & 3) << 2) + (column & 3)] + 0.5) / 16.0;
        if (coverage < threshold)
            return ImageCellReveal.Empty;

        int weightTier = SelectImageWeightTier(cellHash, revealSeed, tone);
        double intensity = (0.12 + tone * 0.78)
            * EdgeShade((column + 0.5) / Math.Max(1, columns))
            * EdgeShade((row + 0.5) / Math.Max(1, rows))
            * settings.ImageBrightness;
        double exactLevel = Math.Clamp(
            intensity * PaletteLevels,
            0.0,
            PaletteLevels);
        int targetLevel = (int)Math.Floor(exactLevel);
        if (UnitHash(Hash(cellHash, revealSeed, 0x51ED270Bu))
            < exactLevel - targetLevel)
        {
            targetLevel++;
        }
        targetLevel = Math.Clamp(targetLevel, 0, PaletteLevels);
        if (targetLevel == 0)
            return ImageCellReveal.Empty;

        int targetGlyph = SelectImageGlyph(
            cellHash,
            revealSeed,
            tone,
            weightTier,
            settings.ImageGlyphMatch,
            glyphDensities);
        return new ImageCellReveal(
            targetLevel,
            (float)(settings.ImageDurationSeconds * settings.ImageStability),
            (float)Math.Max(0.1, fadeSeconds),
            (ushort)targetGlyph,
            (byte)(3 + weightTier),
            streamId);
    }

    private int SelectImageGlyph(
        uint cellHash,
        uint revealSeed,
        double tone,
        int weightTier) =>
        SelectImageGlyph(
            cellHash,
            revealSeed,
            tone,
            weightTier,
            _settings.ImageGlyphMatch,
            _imageGlyphDensities);

    private static int SelectImageGlyph(
        uint cellHash,
        uint revealSeed,
        double tone,
        int weightTier,
        double glyphMatch,
        GlyphDensity[][] imageGlyphDensities)
    {
        uint choice = Hash(cellHash, revealSeed, 0x91E10DA5u);
        double matchRoll = UnitHash(Hash(choice, 0xA341316Cu, 0xC8013EA4u));
        if (glyphMatch <= 0 || matchRoll >= glyphMatch)
            return (int)(choice % MatrixGlyphSet.GlyphStrings.Length);

        GlyphDensity[] densities =
            imageGlyphDensities[Math.Clamp(weightTier, 0, 2)];
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
        _trailGenerations = new long[cellCount];
        _trailRevealSeeds = new uint[cellCount];
        _observedTrailGenerations = new long[cellCount];
        _imageLevels = new float[cellCount];
        _imageInitialLevels = new float[cellCount];
        _imageHoldSeconds = new float[cellCount];
        _imageFadeElapsed = new float[cellCount];
        _imageFadeSeconds = Enumerable.Repeat(1.0f, cellCount).ToArray();
        _imageGlyphs = new ushort[cellCount];
        _imageStyles = new byte[cellCount];
        _imageStreamIds = new long[cellCount];
        // The previous tone map belongs to the old grid. Initial streams are
        // seeded below, so detach it before they can reveal any image cells.
        _imageMask = null;
        _imageInfluenceMask = null;
        _maskDirty = true;
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
        if (_flowSource is not null)
            AlignObservedFlow(_flowSource);
    }

    private GridSnapshot? CaptureGridSnapshot()
    {
        int cellCount = _columns * _rows;
        if (_columns <= 0
            || _rows <= 0
            || _trailOccupied.Length != cellCount
            || _imageLevels.Length != cellCount
            || _imageStreamIds.Length != cellCount
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
            _trailGenerations,
            _trailRevealSeeds,
            _imageLevels,
            _imageInitialLevels,
            _imageHoldSeconds,
            _imageFadeElapsed,
            _imageFadeSeconds,
            _imageGlyphs,
            _imageStyles,
            _imageStreamIds,
            _streamsByColumn,
            _spawners);
    }

    private void SeedFreshGrid()
    {
        _simulationTime = 0;
        _nextStreamId = 0;
        for (int column = 0; column < _columns; column++)
        {
            if (_seedFreshStreams
                && _random.NextDouble() <= _settings.Density)
            {
                SpawnStream(column, firstRun: true);
            }
            ScheduleNextSpawn(_spawners[column], initial: true);
        }
        // Empty seeding is a one-shot startup policy. Any later full rebuild
        // (for example after recovery from a lost device) may restore the
        // normal populated scene instead of remaining blank indefinitely.
        _seedFreshStreams = true;
    }

    private void RestoreGridSnapshot(
        GridSnapshot snapshot,
        SpatialRestoreMap? spatialMap = null)
    {
        _simulationTime = snapshot.SimulationTime;
        _nextStreamId = snapshot.NextStreamId;
        RestoreTrailCells(snapshot, spatialMap);
        RestoreImageCells(snapshot, spatialMap);
        RestoreStreams(snapshot, spatialMap);
    }

    private void RestoreTrailCells(
        GridSnapshot snapshot,
        SpatialRestoreMap? spatialMap)
    {
        for (int oldIndex = 0;
             oldIndex < snapshot.TrailOccupied.Length;
             oldIndex++)
        {
            if (!snapshot.TrailOccupied[oldIndex])
                continue;

            int oldRow = oldIndex / snapshot.Columns;
            int oldColumn = oldIndex - oldRow * snapshot.Columns;
            int column = MapRestoredColumn(
                oldColumn,
                snapshot.Columns,
                spatialMap);
            int row = MapRestoredRow(
                oldRow,
                snapshot.Rows,
                spatialMap);
            if (column < 0 || row < 0)
                continue;
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
            _trailGenerations[index] =
                snapshot.TrailGenerations[oldIndex];
            _trailRevealSeeds[index] =
                snapshot.TrailRevealSeeds[oldIndex];
        }
    }

    private void RestoreImageCells(
        GridSnapshot snapshot,
        SpatialRestoreMap? spatialMap)
    {
        if (spatialMap is not null)
        {
            for (int oldIndex = 0;
                 oldIndex < snapshot.ImageLevels.Length;
                 oldIndex++)
            {
                float candidateLevel = snapshot.ImageLevels[oldIndex];
                if (candidateLevel <= 0.01f)
                    continue;
                int oldRow = oldIndex / snapshot.Columns;
                int oldColumn = oldIndex - oldRow * snapshot.Columns;
                int column = MapRestoredColumn(
                    oldColumn,
                    snapshot.Columns,
                    spatialMap);
                int row = MapRestoredRow(
                    oldRow,
                    snapshot.Rows,
                    spatialMap);
                if (column < 0 || row < 0)
                    continue;
                int index = row * _columns + column;
                if (_imageLevels[index] <= candidateLevel)
                    CopyImageCell(snapshot, oldIndex, index);
            }
            return;
        }

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
        _imageStreamIds[newIndex] = snapshot.ImageStreamIds[oldIndex];
    }

    private void RestoreStreams(
        GridSnapshot snapshot,
        SpatialRestoreMap? spatialMap)
    {
        bool[] mappedSpawners = new bool[_columns];
        for (int oldColumn = 0;
             oldColumn < snapshot.StreamsByColumn.Length;
             oldColumn++)
        {
            int column = MapRestoredColumn(
                oldColumn,
                snapshot.Columns,
                spatialMap);
            if (column < 0)
                continue;
            foreach (RainStream oldStream in snapshot.StreamsByColumn[oldColumn])
            {
                _streamsByColumn[column].Add(
                    ScaleStream(oldStream, snapshot.Rows, spatialMap));
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

    private RainStream ScaleStream(
        RainStream source,
        int oldRows,
        SpatialRestoreMap? spatialMap)
    {
        double rowScale = spatialMap is null
            ? _rows / (double)Math.Max(1, oldRows)
            : spatialMap.Value.SourceCanvas.Height
                / (double)Math.Max(1, oldRows)
                * _rows
                / Math.Max(1, spatialMap.Value.TargetCanvas.Height);
        double rowOffset = spatialMap is null
            ? 0
            : (spatialMap.Value.SourceCanvas.Top
                - spatialMap.Value.TargetCanvas.Top)
                / (double)Math.Max(1, spatialMap.Value.TargetCanvas.Height)
                * _rows;
        int lastWrittenRow = source.LastWrittenRow < 0
            ? -1
            : Math.Max(
                -1,
                (int)Math.Floor(
                    rowOffset
                    + (source.LastWrittenRow + 1) * rowScale) - 1);
        return new RainStream
        {
            Id = source.Id,
            Head = rowOffset + source.Head * rowScale,
            PreviousHead = rowOffset + source.PreviousHead * rowScale,
            Speed = Math.Max(0.1, source.Speed * rowScale),
            Length = Math.Max(0, (int)Math.Round(source.Length * rowScale)),
            MemoryHoldSeconds = source.MemoryHoldSeconds,
            MemoryFadeSeconds = source.MemoryFadeSeconds,
            ImpulseHoldSeconds = source.ImpulseHoldSeconds,
            ImpulseFadeSeconds = source.ImpulseFadeSeconds,
            ImpulseEnabled = source.ImpulseEnabled,
            SignalStrength = source.SignalStrength,
            GlowStrength = source.GlowStrength,
            TerminationRow = rowOffset + source.TerminationRow * rowScale,
            Seed = source.Seed,
            LastWrittenRow = lastWrittenRow
        };
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

    private int MapRestoredColumn(
        int coordinate,
        int sourceSize,
        SpatialRestoreMap? spatialMap) =>
        spatialMap is null
            ? MapCellCenter(coordinate, sourceSize, _columns)
            : MapSpatialCell(
                coordinate,
                sourceSize,
                spatialMap.Value.SourceCanvas.Left,
                spatialMap.Value.SourceCanvas.Width,
                _columns,
                spatialMap.Value.TargetCanvas.Left,
                spatialMap.Value.TargetCanvas.Width);

    private int MapRestoredRow(
        int coordinate,
        int sourceSize,
        SpatialRestoreMap? spatialMap) =>
        spatialMap is null
            ? MapCellCenter(coordinate, sourceSize, _rows)
            : MapSpatialCell(
                coordinate,
                sourceSize,
                spatialMap.Value.SourceCanvas.Top,
                spatialMap.Value.SourceCanvas.Height,
                _rows,
                spatialMap.Value.TargetCanvas.Top,
                spatialMap.Value.TargetCanvas.Height);

    private static int MapSpatialCell(
        int coordinate,
        int sourceCellCount,
        int sourceStart,
        int sourceLength,
        int targetCellCount,
        int targetStart,
        int targetLength)
    {
        double global = sourceStart
            + (coordinate + 0.5)
            / Math.Max(1, sourceCellCount)
            * Math.Max(1, sourceLength);
        int mapped = (int)Math.Floor(
            (global - targetStart)
            / Math.Max(1, targetLength)
            * targetCellCount);
        return (uint)mapped < (uint)targetCellCount
            ? mapped
            : -1;
    }

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

    private void AlignObservedFlow(MatrixSceneRenderer source)
    {
        Array.Clear(_observedTrailGenerations);
        Array.Copy(
            source._trailGenerations,
            _observedTrailGenerations,
            Math.Min(
                source._trailGenerations.Length,
                _observedTrailGenerations.Length));
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
        if (_imageStreamIds.Length > 0)
            Array.Clear(_imageStreamIds);
    }

    private void ClearGridState()
    {
        Array.Clear(_rainLevels);
        Array.Clear(_nextRainLevels);
        Array.Clear(_rainStyles);
        Array.Clear(_nextRainStyles);
        Array.Clear(_rainEmphasis);
        Array.Clear(_nextRainEmphasis);
        Array.Clear(_rainGlow);
        Array.Clear(_nextRainGlow);
        Array.Clear(_rainCovered);
        Array.Clear(_nextRainCovered);
        Array.Clear(_rainGlyphs);
        Array.Clear(_nextRainGlyphs);
        Array.Clear(_trailOccupied);
        Array.Clear(_trailSuppressImage);
        Array.Clear(_trailBornAt);
        Array.Clear(_trailMemoryHoldSeconds);
        Array.Clear(_trailMemoryFadeSeconds);
        Array.Clear(_trailPulseHoldSeconds);
        Array.Clear(_trailPulseFadeSeconds);
        Array.Clear(_trailImpulseEnabled);
        Array.Clear(_trailGlowStrength);
        Array.Clear(_trailBaseIntensity);
        Array.Clear(_trailImageResistance);
        Array.Clear(_trailGlyphs);
        Array.Clear(_trailStreamIds);
        Array.Clear(_trailGenerations);
        Array.Clear(_trailRevealSeeds);
        Array.Clear(_observedTrailGenerations);
        ClearImageOverlay();
        foreach (List<RainStream> streams in _streamsByColumn)
            streams.Clear();
        foreach (ColumnSpawner spawner in _spawners)
            spawner.NextSpawnAt = double.PositiveInfinity;
    }

    private void ClearImageCell(int index)
    {
        _imageLevels[index] = 0;
        _imageInitialLevels[index] = 0;
        _imageHoldSeconds[index] = 0;
        _imageFadeElapsed[index] = 0;
        _imageGlyphs[index] = 0;
        _imageStyles[index] = 0;
        _imageStreamIds[index] = 0;
    }

    private void EnsureImageMask()
    {
        if (!_maskDirty)
            return;

        _maskDirty = false;
        _imageMask = null;
        _imageInfluenceMask = null;
        if (_image is null || _columns <= 0 || _rows <= 0)
            return;

        try
        {
            ProjectedImageMap projected = BuildProjectedImageMap(
                _image,
                _imageProjection,
                _settings.ImageFit,
                columnStart: 0,
                rowStart: 0,
                _columns,
                _rows);
            _imageMask = projected.Tone;
            _imageInfluenceMask = projected.Influence;
        }
        catch
        {
            _imageMask = null;
            _imageInfluenceMask = null;
        }
    }

    private ProjectedImageMap BuildProjectedImageMap(
        PreparedImage image,
        MatrixImageProjection projection,
        string imageFit,
        int columnStart,
        int rowStart,
        int columnCount,
        int rowCount)
    {
        int sourceWidth = image.Width;
        int sourceHeight = image.Height;
        int canvasWidth = Math.Max(1, projection.CanvasWidth);
        int canvasHeight = Math.Max(1, projection.CanvasHeight);
        System.Drawing.Rectangle viewport = projection.ViewportBounds;
        System.Drawing.Rectangle destination = projection.DestinationBounds;
        double scaleX = canvasWidth / (double)sourceWidth;
        double scaleY = canvasHeight / (double)sourceHeight;
        double scale = imageFit == "Fill"
            ? Math.Max(scaleX, scaleY)
            : Math.Min(scaleX, scaleY);
        double drawnWidth = sourceWidth * scale;
        double drawnHeight = sourceHeight * scale;
        double offsetX = (canvasWidth - drawnWidth) * 0.5;
        double offsetY = (canvasHeight - drawnHeight) * 0.5;
        byte[] tone = new byte[checked(columnCount * rowCount)];
        byte[]? influence = image.InfluenceMap is null
            ? null
            : new byte[tone.Length];
        double[] offsets = [0.25, 0.75];
        for (int localRow = 0; localRow < rowCount; localRow++)
        {
            int row = rowStart + localRow;
            for (int localColumn = 0;
                 localColumn < columnCount;
                 localColumn++)
            {
                int column = columnStart + localColumn;
                double total = 0;
                double toneWeight = 0;
                double influenceMaximum = 0;
                int samples = 0;
                foreach (double yOffset in offsets)
                {
                    foreach (double xOffset in offsets)
                    {
                        samples++;
                        double sampleX = (column + xOffset) * _cellWidth;
                        double sampleY = (row + yOffset) * _cellHeight;
                        if (sampleX < destination.Left
                            || sampleX >= destination.Right
                            || sampleY < destination.Top
                            || sampleY >= destination.Bottom
                            || destination.Width <= 0
                            || destination.Height <= 0)
                        {
                            continue;
                        }
                        double databaseX = viewport.Left
                            + (sampleX - destination.Left)
                            * viewport.Width
                            / destination.Width;
                        double databaseY = viewport.Top
                            + (sampleY - destination.Top)
                            * viewport.Height
                            / destination.Height;
                        double sourceX = (databaseX - offsetX) / scale;
                        double sourceY = (databaseY - offsetY) / scale;
                        if (sourceX < 0
                            || sourceX >= sourceWidth
                            || sourceY < 0
                            || sourceY >= sourceHeight)
                        {
                            continue;
                        }
                        if (influence is null)
                        {
                            total += SamplePreparedTone(
                                image,
                                sourceX,
                                sourceY);
                        }
                        else
                        {
                            PreparedInfluenceSample sample =
                                SamplePreparedInfluence(
                                    image,
                                    sourceX,
                                    sourceY);
                            double sampleWeight = sample.Influence / 255.0;
                            total += sample.Tone * sampleWeight;
                            toneWeight += sampleWeight;
                            influenceMaximum = Math.Max(
                                influenceMaximum,
                                sample.Influence);
                        }
                    }
                }

                int localIndex = localRow * columnCount + localColumn;
                double divisor = influence is null
                    ? samples
                    : toneWeight;
                tone[localIndex] = divisor <= 0.000001
                    ? (byte)0
                    : (byte)Math.Clamp(
                        (int)Math.Round(total / divisor),
                        0,
                        255);
                if (influence is not null)
                {
                    // Influence is a coverage mask, not a tone. A single
                    // covered sub-sample is enough to preserve a thin icon
                    // edge or one-pixel letter; averaging would erase it.
                    influence[localIndex] = samples == 0
                        ? (byte)0
                        : (byte)Math.Clamp(
                            (int)Math.Round(influenceMaximum),
                            0,
                            255);
                }
            }
        }
        return new ProjectedImageMap(tone, influence);
    }

    private bool HasImageInfluenceAt(int index) =>
        HasImageInfluence(
            _settings.ImageMode,
            _imageMask,
            _imageInfluenceMask,
            index,
            _imageLevels.Length);

    private static bool HasImageInfluence(
        bool imageMode,
        byte[]? tone,
        byte[]? influence,
        int index,
        int expectedLength) =>
        imageMode
        && tone is not null
        && tone.Length == expectedLength
        && (influence is null
            || ((uint)index < (uint)influence.Length
                && influence[index] >= 128));

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

    private static PreparedInfluenceSample SamplePreparedInfluence(
        PreparedImage image,
        double x,
        double y)
    {
        byte[] influence = image.InfluenceMap!;
        double clampedX = Math.Clamp(x - 0.5, 0, image.Width - 1.0);
        double clampedY = Math.Clamp(y - 0.5, 0, image.Height - 1.0);
        int left = (int)Math.Floor(clampedX);
        int top = (int)Math.Floor(clampedY);
        int right = Math.Min(image.Width - 1, left + 1);
        int bottom = Math.Min(image.Height - 1, top + 1);
        double horizontal = clampedX - left;
        double vertical = clampedY - top;
        double topLeftWeight = (1.0 - horizontal) * (1.0 - vertical);
        double topRightWeight = horizontal * (1.0 - vertical);
        double bottomLeftWeight = (1.0 - horizontal) * vertical;
        double bottomRightWeight = horizontal * vertical;
        int topLeft = top * image.Width + left;
        int topRight = top * image.Width + right;
        int bottomLeft = bottom * image.Width + left;
        int bottomRight = bottom * image.Width + right;
        double topLeftCoverage = influence[topLeft] / 255.0;
        double topRightCoverage = influence[topRight] / 255.0;
        double bottomLeftCoverage = influence[bottomLeft] / 255.0;
        double bottomRightCoverage = influence[bottomRight] / 255.0;
        double coveredWeight =
            topLeftWeight * topLeftCoverage
            + topRightWeight * topRightCoverage
            + bottomLeftWeight * bottomLeftCoverage
            + bottomRightWeight * bottomRightCoverage;
        double weightedTone =
            image.ToneMap[topLeft] * topLeftWeight * topLeftCoverage
            + image.ToneMap[topRight] * topRightWeight * topRightCoverage
            + image.ToneMap[bottomLeft] * bottomLeftWeight * bottomLeftCoverage
            + image.ToneMap[bottomRight] * bottomRightWeight * bottomRightCoverage;
        double tone = coveredWeight <= 0.000001
            ? 0
            : weightedTone / coveredWeight;
        double conservativeCoverage = Math.Max(
            Math.Max(influence[topLeft], influence[topRight]),
            Math.Max(influence[bottomLeft], influence[bottomRight]));
        return new PreparedInfluenceSample(tone, conservativeCoverage);
    }

    private readonly record struct PreparedInfluenceSample(
        double Tone,
        double Influence);

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
        _imageInfluenceMask = null;
        _instances = [];
    }

    /// <summary>
    /// Compact, view-local image state used by the topmost attack surface.
    /// Stream ownership, motion, timing and glyph geometry remain in the
    /// existing renderer. This object only observes deposits and applies the
    /// ordinary image reveal/fade model to the cells visible in one target.
    /// </summary>
    internal sealed class AttackImageLayerRenderer : IDisposable
    {
        private readonly MatrixSceneRenderer _source;
        private readonly SharedMatrixScene _scene;
        private readonly System.Drawing.Rectangle _sourceBounds;
        private readonly long _minimumStreamId;
        private AppSettings _settings;
        private PreparedImage? _image;
        private MatrixImageProjection _projection;
        private byte[]? _imageMask;
        private byte[]? _imageInfluenceMask;
        private bool _maskDirty = true;
        private int _sourceColumns;
        private int _sourceRows;
        private int _sourceCellWidth;
        private int _sourceCellHeight;
        private int _columnStart;
        private int _rowStart;
        private int _columnCount;
        private int _rowCount;
        private AttackImageCell[] _cells = [];
        private GlyphInstance[] _instances = [];
        private TimeSpan _lastFrameAt;
        private long _publishedAtlasVersion = long.MinValue;
        private bool _disposed;

        internal AttackImageLayerRenderer(
            MatrixSceneRenderer source,
            SharedMatrixScene scene,
            AppSettings settings,
            PreparedImage? image,
            MatrixImageProjection projection,
            System.Drawing.Rectangle sourceBounds,
            long minimumStreamId)
        {
            _source = source;
            _scene = scene;
            _settings = settings.Copy();
            _image = image;
            _projection = projection;
            _sourceBounds = sourceBounds;
            _minimumStreamId = minimumStreamId;
            RebuildRegion();
            UpdateAndPublish(dt: 0);
        }

        public void UpdateSettings(AppSettings settings)
        {
            bool maskChanged = !string.Equals(
                settings.ImageFit,
                _settings.ImageFit,
                StringComparison.Ordinal);
            _settings = settings.Copy();
            if (maskChanged)
                _maskDirty = true;
        }

        public void SetImage(
            PreparedImage? image,
            MatrixImageProjection projection)
        {
            _image = image;
            _projection = projection;
            _maskDirty = true;
        }

        public bool RenderIfDue(TimeSpan now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureRegionGeometry();
            _scene.SetPresentationFrameRate(_settings.FramesPerSecond);
            double interval = 1.0 / _settings.FramesPerSecond;
            if ((now - _lastFrameAt).TotalSeconds < interval)
                return false;

            double dt = Math.Min(
                0.08,
                Math.Max(0.0, (now - _lastFrameAt).TotalSeconds));
            _lastFrameAt = now;
            EnsureImageMask();
            UpdateAndPublish(dt);
            return true;
        }

        private void EnsureRegionGeometry()
        {
            if (_sourceColumns == _source._columns
                && _sourceRows == _source._rows
                && _sourceCellWidth == _source._cellWidth
                && _sourceCellHeight == _source._cellHeight)
            {
                return;
            }
            RebuildRegion();
        }

        private void RebuildRegion()
        {
            _sourceColumns = _source._columns;
            _sourceRows = _source._rows;
            _sourceCellWidth = _source._cellWidth;
            _sourceCellHeight = _source._cellHeight;

            int left = Math.Clamp(_sourceBounds.Left, 0, _source._width);
            int top = Math.Clamp(_sourceBounds.Top, 0, _source._height);
            int right = Math.Clamp(_sourceBounds.Right, left, _source._width);
            int bottom = Math.Clamp(_sourceBounds.Bottom, top, _source._height);
            _columnStart = Math.Clamp(
                left / Math.Max(1, _sourceCellWidth),
                0,
                _sourceColumns);
            _rowStart = Math.Clamp(
                top / Math.Max(1, _sourceCellHeight),
                0,
                _sourceRows);
            int columnEnd = Math.Clamp(
                (int)Math.Ceiling(
                    right / (double)Math.Max(1, _sourceCellWidth)),
                _columnStart,
                _sourceColumns);
            int rowEnd = Math.Clamp(
                (int)Math.Ceiling(
                    bottom / (double)Math.Max(1, _sourceCellHeight)),
                _rowStart,
                _sourceRows);
            _columnCount = columnEnd - _columnStart;
            _rowCount = rowEnd - _rowStart;
            int cellCount = checked(_columnCount * _rowCount);

            _cells = new AttackImageCell[cellCount];
            _instances = new GlyphInstance[Math.Max(1, cellCount)];

            for (int localRow = 0; localRow < _rowCount; localRow++)
            {
                int sourceRow = _rowStart + localRow;
                int sourceOffset = sourceRow * _sourceColumns + _columnStart;
                int localOffset = localRow * _columnCount;
                if (sourceOffset < 0
                    || sourceOffset + _columnCount
                        > _source._trailGenerations.Length)
                {
                    continue;
                }
                for (int localColumn = 0;
                     localColumn < _columnCount;
                     localColumn++)
                {
                    _cells[localOffset + localColumn]
                        .ObservedTrailGeneration =
                        _source._trailGenerations[
                            sourceOffset + localColumn];
                }
            }

            _imageMask = null;
            _imageInfluenceMask = null;
            _maskDirty = true;
            _publishedAtlasVersion = long.MinValue;
        }

        private void EnsureImageMask()
        {
            if (!_maskDirty)
                return;
            _maskDirty = false;
            _imageMask = null;
            _imageInfluenceMask = null;
            if (_image is null || _columnCount <= 0 || _rowCount <= 0)
                return;

            try
            {
                ProjectedImageMap projected =
                    _source.BuildProjectedImageMap(
                        _image,
                        _projection,
                        _settings.ImageFit,
                        _columnStart,
                        _rowStart,
                        _columnCount,
                        _rowCount);
                _imageMask = projected.Tone;
                _imageInfluenceMask = projected.Influence;
            }
            catch
            {
                _imageMask = null;
                _imageInfluenceMask = null;
            }
        }

        private void UpdateAndPublish(double dt)
        {
            int count = 0;
            for (int localRow = 0; localRow < _rowCount; localRow++)
            {
                int row = _rowStart + localRow;
                for (int localColumn = 0;
                     localColumn < _columnCount;
                     localColumn++)
                {
                    int column = _columnStart + localColumn;
                    int localIndex =
                        localRow * _columnCount + localColumn;
                    int sourceIndex = row * _sourceColumns + column;
                    ref AttackImageCell cell = ref _cells[localIndex];
                    cell.Advance(dt);
                    if ((uint)sourceIndex
                        >= (uint)_source._trailGenerations.Length)
                    {
                        continue;
                    }

                    long generation =
                        _source._trailGenerations[sourceIndex];
                    if (cell.ObservedTrailGeneration != generation)
                    {
                        cell.ObservedTrailGeneration = generation;
                        if (_source._trailOccupied[sourceIndex]
                            && _source._trailStreamIds[sourceIndex]
                                > _minimumStreamId)
                        {
                            cell.HasAttackDeposit = true;
                            bool imageInfluence = HasImageInfluence(
                                _settings.ImageMode,
                                _imageMask,
                                _imageInfluenceMask,
                                localIndex,
                                _cells.Length);
                            uint revealSeed =
                                _source._trailRevealSeeds[sourceIndex];
                            if (imageInfluence)
                            {
                                ImageCellReveal reveal = ResolveImageCell(
                                    _imageMask![localIndex],
                                    column,
                                    row,
                                    revealSeed,
                                    _source._trailMemoryFadeSeconds[
                                        sourceIndex],
                                    _source._trailStreamIds[sourceIndex],
                                    _settings,
                                    _source._imageGlyphDensities,
                                    _sourceColumns,
                                    _sourceRows);
                                cell.Apply(reveal);
                            }
                            else
                            {
                                cell.ClearImage();
                            }

                            uint noise = Hash(
                                (uint)column,
                                (uint)row,
                                revealSeed);
                            bool revealedImageCell = cell.IsImageCell;
                            bool deliberateGap =
                                (noise & 31) == 0
                                || ((noise >> 5) & 63) == 0;
                            cell.SuppressImage =
                                deliberateGap && !revealedImageCell;
                            cell.ImageResistance = imageInfluence
                                ? (float)_settings.ImageResistance
                                : 0.0f;
                        }
                    }

                    bool imageCell = cell.IsImageCell;
                    bool covered = false;
                    // A pre-existing slow stream may reach this cell after a
                    // newer attack stream. It remains the wallpaper's concern
                    // and must neither appear in nor occlude the foreground
                    // image layer.
                    if (_source._trailOccupied[sourceIndex]
                        && cell.HasAttackDeposit)
                    {
                        double age = Math.Max(
                            0,
                            _source._simulationTime
                                - _source._trailBornAt[sourceIndex]);
                        double resistance = Math.Clamp(
                            cell.ImageResistance,
                            0.0f,
                            1.0f);
                        double fadeRate = resistance >= 0.999
                            ? double.PositiveInfinity
                            : 1.0 / Math.Max(0.001, 1.0 - resistance);
                        double effectiveAge =
                            double.IsPositiveInfinity(fadeRate)
                                ? double.PositiveInfinity
                                : age * fadeRate;
                        double emphasis =
                            _source._trailImpulseEnabled[sourceIndex]
                                ? HeadImpulseModel.Emphasis(
                                    age,
                                    _source._trailPulseHoldSeconds[sourceIndex],
                                    _source._trailPulseFadeSeconds[sourceIndex])
                                : 0.0;
                        double baseFade =
                            double.IsPositiveInfinity(effectiveAge)
                                ? 0.0
                                : TrailMemoryModel.RemainingBrightness(
                                    effectiveAge,
                                    _source._trailMemoryHoldSeconds[sourceIndex],
                                    _source._trailMemoryFadeSeconds[sourceIndex]);
                        if (baseFade > 0.001
                            || emphasis > 0.001
                            || imageCell)
                        {
                            covered = true;
                            if (!cell.SuppressImage)
                            {
                                double baseIntensity =
                                    _source._trailBaseIntensity[sourceIndex]
                                    * EdgeShade(
                                        (column + 0.5)
                                            / Math.Max(1, _sourceColumns))
                                    * EdgeShade(
                                        (row + 0.5)
                                            / Math.Max(1, _sourceRows));
                                double intensity = baseFade
                                    * (baseIntensity
                                        + (1.0 - baseIntensity) * emphasis);
                                int rainLevel = CalculateLevel(intensity);
                                int level = imageCell
                                    ? Math.Max(
                                        rainLevel,
                                        Math.Clamp(
                                            (int)Math.Ceiling(
                                                cell.ImageLevel),
                                            0,
                                            PaletteLevels))
                                    : rainLevel;
                                int glyph = imageCell
                                    ? cell.ImageGlyph
                                    : _source._trailGlyphs[sourceIndex];
                                float style = imageCell
                                    ? cell.ImageStyle
                                    : emphasis > 0.001 ? (byte)1 : (byte)0;
                                double glow = Math.Clamp(
                                    _source._trailGlowStrength[sourceIndex]
                                        + _settings.HeadGlow * emphasis,
                                    0.0,
                                    2.0);
                                AddInstance(
                                    ref count,
                                    column,
                                    row,
                                    glyph,
                                    level / (double)PaletteLevels,
                                    style,
                                    emphasis,
                                    glow,
                                    _source._trailStreamIds[sourceIndex]);
                            }
                        }
                    }

                    if (covered || cell.ImageLevel <= 0)
                        continue;
                    AddInstance(
                        ref count,
                        column,
                        row,
                        cell.ImageGlyph,
                        cell.ImageLevel / PaletteLevels,
                        cell.ImageStyle,
                        emphasis: 0,
                        glow: 0,
                        cell.ImageStreamId);
                }
            }

            SignalRgb signal = SignalColorModel.ToRgb(
                _settings.SignalHue,
                _settings.SignalBrightness);
            SignalRgb background = SignalColorModel.ToBackgroundRgb(
                _settings.BackgroundHue,
                _settings.BackgroundBrightness);
            MatrixRenderParameters parameters = new(
                _source._width,
                _source._height,
                _sourceCellWidth,
                _sourceCellHeight,
                _settings.HeadBrightness,
                signal.Red,
                signal.Green,
                signal.Blue,
                background.Red,
                background.Green,
                background.Blue);
            long atlasVersion = _source._scene.AtlasVersion;
            GlyphAtlasData? atlas = atlasVersion == _publishedAtlasVersion
                ? null
                : _source._scene.Atlas;
            lock (_scene.SyncRoot)
            {
                _scene.Publish(
                    _instances,
                    count,
                    atlas,
                    parameters,
                    _source._nextStreamId);
            }
            _publishedAtlasVersion = atlasVersion;
        }

        private void AddInstance(
            ref int count,
            int column,
            int row,
            int glyph,
            double level,
            float style,
            double emphasis,
            double glow,
            long streamId)
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
                glow,
                streamId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _image = null;
            _imageMask = null;
            _imageInfluenceMask = null;
            _instances = [];
            _cells = [];
        }

        private struct AttackImageCell
        {
            public long ObservedTrailGeneration;
            public long ImageStreamId;
            public float ImageResistance;
            public float ImageLevel;
            public float ImageInitialLevel;
            public float ImageHoldSeconds;
            public float ImageFadeElapsed;
            public float ImageFadeSeconds;
            public ushort ImageGlyph;
            public byte ImageStyle;
            public bool SuppressImage;
            public bool HasAttackDeposit;

            public readonly bool IsImageCell =>
                ImageLevel > 0.01f && ImageStyle >= 3;

            public void Apply(ImageCellReveal reveal)
            {
                ImageLevel = reveal.Level;
                ImageInitialLevel = reveal.Level;
                ImageHoldSeconds = reveal.HoldSeconds;
                ImageFadeElapsed = 0;
                ImageFadeSeconds = reveal.FadeSeconds;
                ImageGlyph = reveal.Glyph;
                ImageStyle = reveal.Style;
                ImageStreamId = reveal.StreamId;
            }

            public void ClearImage()
            {
                ImageLevel = 0;
                ImageInitialLevel = 0;
                ImageHoldSeconds = 0;
                ImageFadeElapsed = 0;
                ImageGlyph = 0;
                ImageStyle = 0;
                ImageStreamId = 0;
            }

            public void Advance(double dt)
            {
                if (dt <= 0 || ImageLevel <= 0)
                    return;

                float remaining = ImageHoldSeconds;
                double fadeDelta = dt;
                if (remaining > 0)
                {
                    double held = Math.Min(dt, remaining);
                    ImageHoldSeconds =
                        (float)Math.Max(0, remaining - held);
                    fadeDelta -= held;
                }
                if (fadeDelta > 0)
                    ImageFadeElapsed += (float)fadeDelta;

                double position = ImageFadeElapsed
                    / Math.Max(0.1f, ImageFadeSeconds);
                double naturalFade = TrailMemoryModel.RemainingBrightness(
                    position,
                    holdSeconds: 0.0,
                    fadeSeconds: 1.0);
                ImageLevel = (float)(ImageInitialLevel * naturalFade);
                if (ImageLevel > 0.01f)
                    return;
                ImageGlyph = 0;
                ImageStyle = 0;
                ImageStreamId = 0;
            }
        }
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

    private readonly record struct SpatialRestoreMap(
        System.Drawing.Rectangle SourceCanvas,
        System.Drawing.Rectangle TargetCanvas);

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
        long[] TrailGenerations,
        uint[] TrailRevealSeeds,
        float[] ImageLevels,
        float[] ImageInitialLevels,
        float[] ImageHoldSeconds,
        float[] ImageFadeElapsed,
        float[] ImageFadeSeconds,
        ushort[] ImageGlyphs,
        byte[] ImageStyles,
        long[] ImageStreamIds,
        List<RainStream>[] StreamsByColumn,
        ColumnSpawner[] Spawners);

    private readonly record struct ProjectedImageMap(
        byte[] Tone,
        byte[]? Influence);

    private readonly record struct ImageCellReveal(
        float Level,
        float HoldSeconds,
        float FadeSeconds,
        ushort Glyph,
        byte Style,
        long StreamId)
    {
        public static ImageCellReveal Empty { get; } = new(
            0,
            0,
            0.1f,
            0,
            0,
            0);
    }

    private readonly record struct GlyphDensity(ushort Glyph, double Density);
}
