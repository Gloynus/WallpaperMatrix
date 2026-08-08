using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace WallpaperMatrix.Models;

public sealed class ImagePlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Основной плейлист";
    public List<ImagePlaylistEntry> Entries { get; set; } = [];

    public ImagePlaylist Copy() => new()
    {
        Id = Id,
        Name = Name,
        Entries = Entries.Select(entry => entry.Copy()).ToList()
    };

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id)
            ? Guid.NewGuid().ToString("N")
            : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name)
            ? "Плейлист без имени"
            : Name.Trim();
        Entries ??= [];

        HashSet<string> uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        Entries = Entries
            .Where(entry => entry is not null)
            .Select(entry => entry.Copy())
            .Where(entry =>
            {
                entry.Normalize();
                return !string.IsNullOrWhiteSpace(entry.Path)
                    && uniquePaths.Add(entry.Path);
            })
            .Take(20_000)
            .ToList();
    }
}

public sealed class ImagePlaylistEntry : INotifyPropertyChanged
{
    private string? _resolution;
    private bool? _exists;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayName =>
        System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } name
            ? name
            : Path;

    [JsonIgnore]
    public string Location =>
        System.IO.Path.GetDirectoryName(Path) ?? "";

    [JsonIgnore]
    public bool Exists => _exists ??= IsReadableImage();

    [JsonIgnore]
    public string Resolution => _resolution ??= ReadResolution();

    public ImagePlaylistEntry Copy() => new()
    {
        Path = Path,
        Enabled = Enabled,
        _resolution = _resolution
    };

    public void RefreshAvailability()
    {
        SetAvailability(IsReadableImage());
    }

    public void SetAvailability(bool available)
    {
        bool next = available;
        if (_exists == next)
            return;
        _exists = next;
        _resolution = null;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(Exists)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(Resolution)));
    }

    public void Normalize()
    {
        Path = Path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(Path))
            return;
        try
        {
            Path = System.IO.Path.GetFullPath(Path);
        }
        catch
        {
            // Keep an unavailable path visible so the operator can repair it.
        }
    }

    private string ReadResolution()
    {
        if (!File.Exists(Path))
            return "—";
        try
        {
            using FileStream stream = new(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            return $"{frame.PixelWidth}×{frame.PixelHeight}";
        }
        catch
        {
            return "—";
        }
    }

    private bool IsReadableImage()
    {
        if (!File.Exists(Path))
            return false;
        try
        {
            using FileStream stream = new(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            return decoder.Frames.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}

public static class ImagePlaylistCatalog
{
    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public static bool IsSupportedImage(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && SupportedExtensions.Contains(System.IO.Path.GetExtension(path));

    public static List<string> ExpandPaths(IEnumerable<string> sourcePaths)
    {
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                continue;
            try
            {
                if (Directory.Exists(sourcePath))
                {
                    foreach (string file in Directory
                        .EnumerateFiles(sourcePath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(IsSupportedImage)
                        .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
                    {
                        string fullPath = System.IO.Path.GetFullPath(file);
                        if (unique.Add(fullPath))
                            result.Add(fullPath);
                    }
                }
                else if (File.Exists(sourcePath) && IsSupportedImage(sourcePath))
                {
                    string fullPath = System.IO.Path.GetFullPath(sourcePath);
                    if (unique.Add(fullPath))
                        result.Add(fullPath);
                }
            }
            catch
            {
                // An inaccessible dropped folder must not prevent other files
                // from being added to the playlist.
            }
        }
        return result;
    }
}
