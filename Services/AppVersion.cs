namespace WallpaperMatrix.Services;

internal static class AppVersion
{
    public static string Current { get; } = Read();
    public static string DisplayName => $"Wallpaper Matrix {Current}";

    private static string Read()
    {
        Version? version = typeof(AppVersion).Assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
