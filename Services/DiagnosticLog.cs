using System.Text;
using System.IO;

namespace WallpaperMatrix.Services;

internal static class DiagnosticLog
{
    private static readonly object SyncRoot = new();

    public static string LogPath { get; } = PortableStorage.DiagnosticLogPath;

    public static void Write(string message)
    {
        try
        {
            lock (SyncRoot)
            {
                string? directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become another reason for the wallpaper
            // process to stop.
        }
    }

    public static void Write(string context, Exception exception) =>
        Write($"{context}{Environment.NewLine}{exception}");

    public static string BuildReport(string runtimeStatus)
    {
        StringBuilder report = new();
        report.AppendLine("Wallpaper Matrix 3.3.0");
        report.AppendLine($"Время: {DateTime.Now:O}");
        report.AppendLine($"ОС: {Environment.OSVersion}");
        report.AppendLine($"Процесс: {Environment.ProcessPath}");
        report.AppendLine($"64-bit: {Environment.Is64BitProcess}");
        report.AppendLine($"Логических процессоров: {Environment.ProcessorCount}");
        report.AppendLine($"Состояние: {runtimeStatus}");
        report.AppendLine($"Каталог данных: {PortableStorage.DataDirectory}");
        report.AppendLine($"Настройки: {PortableStorage.SettingsPath}");
        report.AppendLine($"Плейлисты: {PortableStorage.PlaylistsPath}");
        report.AppendLine($"Журнал: {LogPath}");
        return report.ToString();
    }
}
