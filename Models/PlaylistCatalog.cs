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
        foreach (MonitorProfile profile in settings.MonitorProfiles)
        {
            AppSettings monitorSettings = profile.Settings;
            monitorSettings.ImagePlaylists = catalog
                .Select(playlist => playlist.Copy())
                .ToList();
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
}
