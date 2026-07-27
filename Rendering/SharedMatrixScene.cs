using System.Runtime.InteropServices;

namespace WallpaperMatrix.Rendering;

/// <summary>
/// CPU simulation state shared by every monitor. Only the primary renderer
/// uploads this compact glyph list; the compositor draws it in every viewport.
/// </summary>
internal sealed class SharedMatrixScene : IDisposable
{
    private long _version;
    private long _atlasVersion;
    private long _latestStreamId;
    private int _presentationFramesPerSecond = 24;
    private bool _disposed;

    public object SyncRoot { get; } = new();
    public int Width { get; }
    public int Height { get; }
    public long Version => Interlocked.Read(ref _version);
    public long AtlasVersion => Interlocked.Read(ref _atlasVersion);
    public long LatestStreamId => Interlocked.Read(ref _latestStreamId);
    public int PresentationFramesPerSecond => Volatile.Read(ref _presentationFramesPerSecond);
    public GlyphInstance[] Instances { get; private set; } = [];
    public int InstanceCount { get; private set; }
    public GlyphAtlasData Atlas { get; private set; } = GlyphAtlasData.Empty;
    public MatrixRenderParameters Parameters { get; private set; }

    public SharedMatrixScene(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void Publish(
        GlyphInstance[] instances,
        int instanceCount,
        GlyphAtlasData? atlas,
        MatrixRenderParameters parameters,
        long latestStreamId)
    {
        int count = Math.Clamp(instanceCount, 0, instances.Length);
        if (Instances.Length < count)
        {
            int capacity = Math.Max(
                count,
                Math.Max(1024, Instances.Length * 2));
            Instances = new GlyphInstance[capacity];
        }
        if (count > 0)
            Array.Copy(instances, Instances, count);
        InstanceCount = count;
        Parameters = parameters;
        Interlocked.Exchange(ref _latestStreamId, latestStreamId);
        if (atlas is not null)
        {
            Atlas = atlas;
            Interlocked.Increment(ref _atlasVersion);
        }
        Interlocked.Increment(ref _version);
    }

    public void SetPresentationFrameRate(int framesPerSecond) =>
        Volatile.Write(ref _presentationFramesPerSecond, Math.Clamp(framesPerSecond, 8, 60));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Instances = [];
        InstanceCount = 0;
        Atlas = GlyphAtlasData.Empty;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GlyphInstance
{
    public float Column;
    public float Row;
    public float Glyph;
    public float Level;
    public float Style;
    public float Emphasis;
    public float Glow;
    public float StreamId;

    public GlyphInstance(
        int column,
        int row,
        int glyph,
        double level,
        float style,
        double emphasis,
        double glow,
        long streamId)
    {
        Column = column;
        Row = row;
        Glyph = glyph;
        Level = (float)level;
        Style = style;
        Emphasis = (float)emphasis;
        Glow = (float)glow;
        StreamId = streamId;
    }
}

internal sealed record GlyphAtlasData(
    byte[] Pixels,
    int Width,
    int Height,
    int CellWidth,
    int CellHeight,
    int GlyphCount,
    int StyleCount,
    float[] InkCoverage)
{
    public static GlyphAtlasData Empty { get; } = new([], 0, 0, 0, 0, 0, 0, []);
}

internal readonly record struct MatrixRenderParameters(
    int SourceWidth,
    int SourceHeight,
    int CellWidth,
    int CellHeight,
    double HeadBrightness,
    double SignalRed,
    double SignalGreen,
    double SignalBlue,
    double BackgroundRed,
    double BackgroundGreen,
    double BackgroundBlue);
