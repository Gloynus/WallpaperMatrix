using System.IO;

namespace WallpaperMatrix.Services;

internal static class PortableStorage
{
    public static string ApplicationDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppContext.BaseDirectory;

    public static string DataDirectory { get; } =
        Path.Combine(ApplicationDirectory, "OperatorData");

    public static string SettingsPath { get; } =
        Path.Combine(DataDirectory, "CurrentSettings.json");

    public static string PlaylistsPath { get; } =
        Path.Combine(DataDirectory, "Playlists.json");

    public static string DiagnosticLogPath { get; } =
        Path.Combine(DataDirectory, "Diagnostics.log");
}
