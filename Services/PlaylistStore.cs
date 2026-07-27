using System.IO;
using System.Text.Json;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class PlaylistStore
{
    private sealed class PlaylistDocument
    {
        public int FormatVersion { get; set; } = 1;
        public string ActivePlaylistId { get; set; } = "";
        public List<ImagePlaylist> Playlists { get; set; } = [];
    }

    private readonly string _playlistsPath;

    public PlaylistStore()
    {
        _playlistsPath = PortableStorage.PlaylistsPath;
    }

    public void LoadInto(AppSettings settings)
    {
        try
        {
            if (!File.Exists(_playlistsPath))
            {
                settings.Normalize();
                Save(settings);
                return;
            }

            PlaylistDocument document =
                JsonSerializer.Deserialize<PlaylistDocument>(
                    File.ReadAllText(_playlistsPath),
                    SettingsFileCodec.JsonOptions)
                ?? new PlaylistDocument();
            settings.ImagePlaylists = document.Playlists
                .Select(playlist => playlist.Copy())
                .ToList();
            settings.ActiveImagePlaylistId = document.ActivePlaylistId;
            settings.Normalize();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                "Файл плейлистов повреждён или недоступен; "
                + "создан пустой операторский плейлист.",
                exception);
            settings.ImagePlaylists = [new ImagePlaylist()];
            settings.ActiveImagePlaylistId = settings.ImagePlaylists[0].Id;
            settings.Normalize();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            AppSettings normalized = settings.Copy();
            normalized.Normalize();
            PlaylistDocument document = new()
            {
                ActivePlaylistId = normalized.ActiveImagePlaylistId,
                Playlists = normalized.ImagePlaylists
                    .Select(playlist => playlist.Copy())
                    .ToList()
            };
            AtomicFile.WriteAllText(
                _playlistsPath,
                JsonSerializer.Serialize(
                    document,
                    SettingsFileCodec.JsonOptions));
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                "Не удалось атомарно сохранить файл плейлистов.",
                exception);
        }
    }
}
