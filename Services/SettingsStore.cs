using System.IO;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore()
    {
        _settingsPath = PortableStorage.SettingsPath;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return OperatorDefaults.Create();

            AppSettings settings = SettingsFileCodec.DeserializeSettings(
                File.ReadAllText(_settingsPath));
            settings.Normalize();
            return settings;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                "Файл настроек повреждён или недоступен; загружен безопасный профиль.",
                exception);
            return OperatorDefaults.Create();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(
                _settingsPath,
                SettingsFileCodec.SerializeCurrentSettings(settings));
        }
        catch (Exception exception)
        {
            // A read-only profile must not take down the wallpaper process,
            // but the operator still needs evidence in the diagnostic report.
            DiagnosticLog.Write("Не удалось атомарно сохранить настройки.", exception);
        }
    }
}
