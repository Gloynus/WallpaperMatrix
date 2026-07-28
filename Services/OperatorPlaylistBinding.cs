using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

/// <summary>
/// Stores only a portable reference to the playlist selected by an operator
/// preset. Playlist rows and machine-specific image paths remain in the
/// separate playlist document.
/// </summary>
internal static class OperatorPlaylistBinding
{
    public static void Stamp(AppSettings settings)
    {
        StampOne(settings);
        foreach (MonitorProfile profile in settings.MonitorProfiles)
            StampOne(profile.Settings);
    }

    public static void Apply(
        AppSettings presetSettings,
        AppSettings currentSettings)
    {
        BindOne(presetSettings, currentSettings);
        foreach (MonitorProfile profile in presetSettings.MonitorProfiles)
        {
            MonitorProfile? currentProfile = MonitorTopology.Find(
                currentSettings.MonitorProfiles,
                profile.MonitorId);
            BindOne(
                profile.Settings,
                currentProfile?.Settings ?? currentSettings);
        }
    }

    private static void StampOne(AppSettings settings)
    {
        if (settings.ImagePlaylists.Count == 0)
            return;

        ImagePlaylist selected = settings.ActiveImagePlaylist();
        settings.OperatorPlaylistId = selected.Id;
        settings.OperatorPlaylistName = selected.Name.Trim();
    }

    private static void BindOne(
        AppSettings presetSettings,
        AppSettings currentSettings)
    {
        List<ImagePlaylist> available = currentSettings.ImagePlaylists
            .Select(playlist => playlist.Copy())
            .ToList();
        if (available.Count == 0)
            available.Add(new ImagePlaylist());

        ImagePlaylist selected =
            available.FirstOrDefault(playlist => string.Equals(
                playlist.Id,
                presetSettings.OperatorPlaylistId,
                StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(playlist => string.Equals(
                playlist.Name.Trim(),
                presetSettings.OperatorPlaylistName,
                StringComparison.CurrentCultureIgnoreCase))
            ?? available.FirstOrDefault(playlist => string.Equals(
                playlist.Id,
                currentSettings.ActiveImagePlaylistId,
                StringComparison.OrdinalIgnoreCase))
            ?? available[0];

        presetSettings.ImagePlaylists = available;
        presetSettings.ActiveImagePlaylistId = selected.Id;
        presetSettings.OperatorPlaylistId = selected.Id;
        presetSettings.OperatorPlaylistName = selected.Name.Trim();
    }
}
