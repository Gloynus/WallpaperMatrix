using System.Windows.Media.Imaging;
using System.IO;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class ImageSequenceService
{
    private const int MaximumCachedSources = 10;
    private readonly List<string> _files = [];
    private readonly object _cacheLock = new();
    private readonly Dictionary<SourceCacheKey, ImageSourceFrame> _sourceCache = [];
    private readonly LinkedList<SourceCacheKey> _sourceCacheOrder = [];
    private readonly HashSet<string> _reportedUnavailable =
        new(StringComparer.OrdinalIgnoreCase);
    private int _index = -1;

    public event Action<string, bool>? ImageAvailabilityChanged;

    public int Count => _files.Count;
    public string? CurrentPath => _index >= 0 && _index < _files.Count ? _files[_index] : null;
    public int TargetWidth { get; set; } = 2560;
    public int TargetHeight { get; set; } = 1440;

    public ImageSourceFrame? Reload(
        IReadOnlyList<ImagePlaylistEntry> entries,
        string? preferredPath = null)
    {
        Rebuild(entries);
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            _index = FindPath(preferredPath);
            if (_index >= 0)
                return TryLoad(_files[_index]);
        }
        return MoveNext();
    }

    public ImageSourceFrame? MoveNext(
        IReadOnlyList<ImagePlaylistEntry> entries,
        string? currentPath)
    {
        Rebuild(entries);
        _index = string.IsNullOrWhiteSpace(currentPath)
            ? -1
            : FindPath(currentPath);
        return MoveNext();
    }

    public ImageSourceFrame? Select(
        IReadOnlyList<ImagePlaylistEntry> entries,
        string requestedPath)
    {
        Rebuild(entries);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch
        {
            return null;
        }

        _index = FindPath(fullPath);
        // A disabled row may still be previewed once. It does not silently
        // become part of the normal enabled sequence.
        return TryLoad(fullPath);
    }

    public ImageSourceFrame? MoveNext()
    {
        if (_files.Count == 0)
            return null;

        for (int attempt = 0; attempt < _files.Count; attempt++)
        {
            _index = (_index + 1) % _files.Count;
            ImageSourceFrame? image = TryLoad(_files[_index]);
            if (image is not null)
                return image;
        }

        return null;
    }

    private void Rebuild(IReadOnlyList<ImagePlaylistEntry> entries)
    {
        _files.Clear();
        _index = -1;
        try
        {
            HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
            foreach (ImagePlaylistEntry entry in entries)
            {
                if (!entry.Enabled
                    || !ImagePlaylistCatalog.IsSupportedImage(entry.Path))
                {
                    continue;
                }
                if (!File.Exists(entry.Path))
                {
                    ReportUnavailable(entry.Path);
                    continue;
                }
                string fullPath = Path.GetFullPath(entry.Path);
                if (unique.Add(fullPath))
                    _files.Add(fullPath);
            }
        }
        catch
        {
            _files.Clear();
        }
    }

    private ImageSourceFrame? TryLoad(string path)
    {
        try
        {
            FileInfo file = new(path);
            SourceCacheKey cacheKey = new(
                file.FullName.ToUpperInvariant(),
                file.LastWriteTimeUtc.Ticks,
                file.Length);
            if (TryGetCached(cacheKey, out ImageSourceFrame cached))
            {
                ReportAvailable(file.FullName);
                return cached;
            }

            int sourceWidth;
            int sourceHeight;
            using (FileStream metadataStream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                BitmapDecoder metadata = BitmapDecoder.Create(
                    metadataStream,
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                sourceWidth = metadata.Frames[0].PixelWidth;
                sourceHeight = metadata.Frames[0].PixelHeight;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            const int maximumDecodeWidth = 1600;
            const int maximumDecodeHeight = 1200;
            const int maximumDecodePixels = 1_500_000;
            double decodeScale = Math.Min(
                1.0,
                Math.Min(
                    Math.Min(
                        maximumDecodeWidth / (double)Math.Max(1, sourceWidth),
                        maximumDecodeHeight / (double)Math.Max(1, sourceHeight)),
                    Math.Sqrt(maximumDecodePixels
                        / (double)Math.Max(1L, (long)sourceWidth * sourceHeight))));
            if (decodeScale < 0.999 && sourceWidth >= sourceHeight)
            {
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(sourceWidth * decodeScale));
            }
            else if (decodeScale < 0.999)
                image.DecodePixelHeight = Math.Max(1, (int)Math.Round(sourceHeight * decodeScale));
            image.EndInit();
            image.Freeze();
            ImageSourceFrame frame = new(
                image,
                file.FullName,
                file.LastWriteTimeUtc,
                file.Length);
            ReportAvailable(file.FullName);
            AddToCache(cacheKey, frame);
            return frame;
        }
        catch
        {
            ReportUnavailable(path);
            return null;
        }
    }

    private void ReportUnavailable(string path)
    {
        string key;
        try
        {
            key = Path.GetFullPath(path);
        }
        catch
        {
            key = path;
        }
        if (_reportedUnavailable.Add(key))
            ImageAvailabilityChanged?.Invoke(path, false);
    }

    private void ReportAvailable(string path)
    {
        _reportedUnavailable.Remove(path);
        // A sequence may have been rebuilt since the failed read while the
        // panel still retains the red state. Every successful read therefore
        // confirms availability; the bound row ignores unchanged values.
        ImageAvailabilityChanged?.Invoke(path, true);
    }

    private bool TryGetCached(
        SourceCacheKey key,
        out ImageSourceFrame frame)
    {
        lock (_cacheLock)
        {
            if (!_sourceCache.TryGetValue(key, out ImageSourceFrame? cached))
            {
                frame = null!;
                return false;
            }

            frame = cached;
            LinkedListNode<SourceCacheKey>? node =
                _sourceCacheOrder.Find(key);
            if (node is not null)
            {
                _sourceCacheOrder.Remove(node);
                _sourceCacheOrder.AddLast(node);
            }
            return true;
        }
    }

    private void AddToCache(
        SourceCacheKey key,
        ImageSourceFrame frame)
    {
        lock (_cacheLock)
        {
            SourceCacheKey[] staleKeys = _sourceCache.Keys
                .Where(existing => string.Equals(
                    existing.Path,
                    key.Path,
                    StringComparison.OrdinalIgnoreCase)
                    && existing != key)
                .ToArray();
            foreach (SourceCacheKey stale in staleKeys)
            {
                _sourceCache.Remove(stale);
                LinkedListNode<SourceCacheKey>? staleNode =
                    _sourceCacheOrder.Find(stale);
                if (staleNode is not null)
                    _sourceCacheOrder.Remove(staleNode);
            }

            if (_sourceCache.ContainsKey(key))
                return;
            _sourceCache[key] = frame;
            _sourceCacheOrder.AddLast(key);
            while (_sourceCacheOrder.Count > MaximumCachedSources)
            {
                SourceCacheKey oldest = _sourceCacheOrder.First!.Value;
                _sourceCacheOrder.RemoveFirst();
                _sourceCache.Remove(oldest);
            }
        }
    }

    private int FindPath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return -1;
        }
        return _files.FindIndex(candidate =>
            string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct SourceCacheKey(
        string Path,
        long LastWriteTicks,
        long FileLength);
}
