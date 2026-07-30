using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class PlaylistStore
{
    private sealed class PlaylistDocument
    {
        public int FormatVersion { get; set; } = 2;
        public string ActivePlaylistId { get; set; } = "";
        public List<ImagePlaylist> Playlists { get; set; } = [];
        public List<MonitorPlaylistDocument> MonitorPlaylists { get; set; } = [];
    }

    private sealed class MonitorPlaylistDocument
    {
        public string MonitorId { get; set; } = "";
        public string ActivePlaylistId { get; set; } = "";
        public List<ImagePlaylist> Playlists { get; set; } = [];
    }

    private readonly string _playlistsPath;

    public PlaylistStore()
    {
        _playlistsPath = PortableStorage.PlaylistsPath;
    }

    public string FileVersion()
    {
        try
        {
            if (!File.Exists(_playlistsPath))
                return "missing";
            using FileStream stream = new(
                _playlistsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "unavailable";
        }
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
            if (document.MonitorPlaylists is null
                || document.MonitorPlaylists.Count == 0)
            {
                foreach (MonitorProfile profile in settings.MonitorProfiles)
                {
                    profile.Settings.ImagePlaylists =
                        settings.ImagePlaylists
                            .Select(playlist => playlist.Copy())
                            .ToList();
                    profile.Settings.ActiveImagePlaylistId =
                        settings.ActiveImagePlaylistId;
                }
            }
            foreach (MonitorPlaylistDocument monitorDocument
                     in document.MonitorPlaylists ?? [])
            {
                MonitorProfile? profile = MonitorTopology.Find(
                    settings.MonitorProfiles,
                    monitorDocument.MonitorId);
                if (profile is null)
                    continue;
                profile.Settings.ImagePlaylists =
                    (monitorDocument.Playlists ?? [])
                        .Select(playlist => playlist.Copy())
                        .ToList();
                profile.Settings.ActiveImagePlaylistId =
                    monitorDocument.ActivePlaylistId;
                profile.Settings.Normalize(includeMonitorProfiles: false);
            }
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
                    .ToList(),
                MonitorPlaylists = normalized.MonitorProfiles
                    .Select(profile => new MonitorPlaylistDocument
                    {
                        MonitorId = profile.MonitorId,
                        ActivePlaylistId =
                            profile.Settings.ActiveImagePlaylistId,
                        Playlists = profile.Settings.ImagePlaylists
                            .Select(playlist => playlist.Copy())
                            .ToList()
                    })
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
