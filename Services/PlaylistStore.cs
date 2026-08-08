using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class PlaylistStore
{
    private sealed class PlaylistDocument
    {
        public int FormatVersion { get; set; } = 5;
        public string ActivePlaylistId { get; set; } = "";
        public List<ImagePlaylist> Playlists { get; set; } = [];
        public List<PlaylistPresentation> Presentations { get; set; } = [];
        // Read only: version 2 stored a full catalog for every monitor.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<MonitorPlaylistDocument>? MonitorPlaylists { get; set; }
        public List<MonitorPlaylistSelectionDocument> MonitorSelections { get; set; } = [];
    }

    private sealed class MonitorPlaylistDocument
    {
        public string MonitorId { get; set; } = "";
        public string ActivePlaylistId { get; set; } = "";
        public List<ImagePlaylist> Playlists { get; set; } = [];
    }

    private sealed class MonitorPlaylistSelectionDocument
    {
        public string MonitorId { get; set; } = "";
        public string ActivePlaylistId { get; set; } = "";
        public List<PlaylistPresentation> Presentations { get; set; } = [];
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
            if (document.FormatVersion < 4)
            {
                ImagePlacement legacyPlacement =
                    ImagePlacement.FromLegacy(settings.ImageFit);
                foreach (ImagePlaylist playlist in document.Playlists
                             .Concat((document.MonitorPlaylists ?? [])
                                 .SelectMany(monitor => monitor.Playlists ?? [])))
                {
                    playlist.Placement = legacyPlacement.Copy();
                }
            }
            settings.ImagePlaylists = MergeCatalogs(
                document.Playlists,
                (document.MonitorPlaylists ?? [])
                    .SelectMany(monitor => monitor.Playlists ?? []));
            settings.ActiveImagePlaylistId = document.ActivePlaylistId;
            settings.PlaylistPresentations = (document.Presentations ?? [])
                .Select(presentation => presentation.Copy())
                .ToList();
            IReadOnlyList<MonitorPlaylistSelectionDocument> selections =
                document.MonitorSelections ?? [];
            foreach (MonitorProfile profile in settings.MonitorProfiles)
            {
                string monitorId = profile.MonitorId;
                string activeId = selections
                    .FirstOrDefault(selection => string.Equals(
                        selection.MonitorId,
                        monitorId,
                        StringComparison.OrdinalIgnoreCase))
                    ?.ActivePlaylistId
                    ?? (document.MonitorPlaylists ?? [])
                        .FirstOrDefault(selection => string.Equals(
                            selection.MonitorId,
                            monitorId,
                            StringComparison.OrdinalIgnoreCase))
                        ?.ActivePlaylistId
                    ?? settings.ActiveImagePlaylistId;
                profile.Settings.ActiveImagePlaylistId = activeId;
                MonitorPlaylistSelectionDocument? selection =
                    selections
                        .FirstOrDefault(candidate => string.Equals(
                            candidate.MonitorId,
                            monitorId,
                            StringComparison.OrdinalIgnoreCase));
                profile.Settings.PlaylistPresentations =
                    (selection?.Presentations ?? [])
                        .Select(presentation => presentation.Copy())
                        .ToList();
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
                Presentations = normalized.PlaylistPresentations
                    .Select(presentation => presentation.Copy())
                    .ToList(),
                MonitorSelections = normalized.MonitorProfiles
                    .Select(profile => new MonitorPlaylistSelectionDocument
                    {
                        MonitorId = profile.MonitorId,
                        ActivePlaylistId = profile.Settings.ActiveImagePlaylistId,
                        Presentations = profile.Settings.PlaylistPresentations
                            .Select(presentation => presentation.Copy())
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

    private static List<ImagePlaylist> MergeCatalogs(
        IEnumerable<ImagePlaylist> primary,
        IEnumerable<ImagePlaylist> legacy)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        return primary
            .Concat(legacy)
            .Where(playlist => playlist is not null)
            .Select(playlist => playlist.Copy())
            .Where(playlist =>
            {
                playlist.Normalize();
                return ids.Add(playlist.Id);
            })
            .ToList();
    }
}
