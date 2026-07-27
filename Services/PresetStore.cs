using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public sealed class PresetStore
{
    private const string FilePrefix = "Preset - ";
    private const string FilePattern = "Preset - *.json";
    private const int FormatVersion = 1;

    public IReadOnlyList<OperatorPreset> LoadAll()
    {
        if (!Directory.Exists(PortableStorage.DataDirectory))
            return [];

        List<OperatorPreset> loaded = [];
        foreach (string path in Directory.EnumerateFiles(
            PortableStorage.DataDirectory,
            FilePattern,
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                JsonObject root = JsonNode.Parse(File.ReadAllText(path))
                    as JsonObject
                    ?? throw new InvalidDataException(
                        "Корневой элемент пресета не является объектом.");
                string id = root[nameof(OperatorPreset.Id)]?.GetValue<string>()
                    ?? "";
                string name =
                    root[nameof(OperatorPreset.Name)]?.GetValue<string>()
                    ?? "";
                DateTime modifiedAt =
                    root[nameof(OperatorPreset.ModifiedAt)]
                        ?.GetValue<DateTime>()
                    ?? File.GetLastWriteTime(path);
                if (string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidDataException(
                        "В пресете отсутствует идентификатор или имя.");
                }

                AppSettings settings =
                    SettingsFileCodec.DeserializeSettings(
                        root[nameof(OperatorPreset.Settings)]);
                settings.Normalize();
                loaded.Add(new OperatorPreset
                {
                    Id = id.Trim(),
                    Name = name.Trim(),
                    ModifiedAt = modifiedAt,
                    Settings = settings,
                    FilePath = path
                });
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(
                    $"Файл пресета пропущен: {path}",
                    exception);
            }
        }

        return loaded
            .GroupBy(preset => preset.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(preset => preset.ModifiedAt)
                .First())
            .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public OperatorPreset Create(
        string name,
        AppSettings settings,
        IReadOnlyCollection<OperatorPreset> existing)
    {
        string normalizedName = NormalizeName(name);
        if (existing.Any(preset => string.Equals(
            preset.Name,
            normalizedName,
            StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Пресет с таким именем уже существует.");
        }

        OperatorPreset preset = new()
        {
            Name = normalizedName
        };
        Save(preset, settings);
        return preset;
    }

    public void Save(OperatorPreset preset, AppSettings settings)
    {
        string normalizedName = NormalizeName(preset.Name);
        DateTime modifiedAt = DateTime.Now;
        string destination = Path.Combine(
            PortableStorage.DataDirectory,
            BuildFileName(normalizedName, modifiedAt));

        JsonObject root = new()
        {
            ["FormatVersion"] = FormatVersion,
            [nameof(OperatorPreset.Id)] = preset.Id,
            [nameof(OperatorPreset.Name)] = normalizedName,
            [nameof(OperatorPreset.ModifiedAt)] = modifiedAt,
            [nameof(OperatorPreset.Settings)] =
                SettingsFileCodec.ToPresetSettingsObject(settings)
        };
        AtomicFile.WriteAllText(
            destination,
            root.ToJsonString(SettingsFileCodec.JsonOptions));

        string previousPath = preset.FilePath;
        preset.Name = normalizedName;
        preset.ModifiedAt = modifiedAt;
        preset.Settings = settings.Copy();
        preset.Settings.ImagePlaylists = [];
        preset.Settings.ActiveImagePlaylistId = "";
        preset.Settings.ActivePresetId = "";
        preset.FilePath = destination;

        if (!string.IsNullOrWhiteSpace(previousPath)
            && !PathsEqual(previousPath, destination)
            && IsPresetPath(previousPath)
            && File.Exists(previousPath))
        {
            File.Delete(previousPath);
        }
    }

    public void Delete(OperatorPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.FilePath)
            || !IsPresetPath(preset.FilePath))
        {
            throw new InvalidOperationException(
                "Файл пресета находится вне каталога OperatorData.");
        }
        if (File.Exists(preset.FilePath))
            File.Delete(preset.FilePath);
    }

    private static string NormalizeName(string name)
    {
        string normalized = name.Trim();
        if (normalized.Length == 0)
            throw new InvalidOperationException("Введите имя пресета.");
        return normalized.Length <= 80
            ? normalized
            : normalized[..80].Trim();
    }

    private static string BuildFileName(string name, DateTime modifiedAt)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safeName = new(name
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        safeName = safeName.Trim(' ', '.');
        if (safeName.Length == 0)
            safeName = "Operator";
        string timestamp = modifiedAt.ToString(
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture);
        return $"{FilePrefix}{safeName} - {timestamp}.json";
    }

    private static bool IsPresetPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string dataDirectory = Path.GetFullPath(
            PortableStorage.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(
                dataDirectory,
                StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullPath).StartsWith(
                FilePrefix,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
