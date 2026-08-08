using System.Windows.Media.Imaging;

namespace WallpaperMatrix.Models;

public sealed record ImageSourceFrame(
    BitmapSource Bitmap,
    string Path,
    DateTime LastWriteTimeUtc,
    long FileLength);

public sealed record PreparedImage(
    byte[] ToneMap,
    int Width,
    int Height,
    string SourcePath,
    byte[]? InfluenceMap = null);
