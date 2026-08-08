namespace WallpaperMatrix.Models;

/// <summary>
/// Keeps image playlists as one application-wide catalog. Monitor profiles
/// retain only their selected playlist, so isolated databases can choose
/// independently without duplicating rows, paths, or availability state.
/// </summary>
internal static class PlaylistCatalog
{
    public static void Synchronize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<ImagePlaylist> catalog = settings.ImagePlaylists;
        Dictionary<string, ImagePlaylist> playlistsById = catalog
            .ToDictionary(
                playlist => playlist.Id,
                StringComparer.OrdinalIgnoreCase);
        PrunePresentations(settings, playlistsById);
        foreach (MonitorProfile profile in settings.MonitorProfiles)
        {
            AppSettings monitorSettings = profile.Settings;
            monitorSettings.ImagePlaylists = catalog
                .Select(playlist => playlist.Copy())
                .ToList();
            PrunePresentations(monitorSettings, playlistsById);
            if (!catalog.Any(playlist => string.Equals(
                    playlist.Id,
                    monitorSettings.ActiveImagePlaylistId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                monitorSettings.ActiveImagePlaylistId =
                    settings.ActiveImagePlaylistId;
            }
        }
    }

    private static void PrunePresentations(
        AppSettings settings,
        IReadOnlyDictionary<string, ImagePlaylist> playlistsById)
    {
        settings.PlaylistPresentations.RemoveAll(presentation =>
            !playlistsById.TryGetValue(
                presentation.PlaylistId,
                out ImagePlaylist? playlist)
            || presentation.Placement.Equivalent(playlist.Placement));
    }
}
