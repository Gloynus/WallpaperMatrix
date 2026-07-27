using System.Text.Json;
using System.Text.Json.Nodes;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

internal static class SettingsFileCodec
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true
    };

    public static string SerializeCurrentSettings(AppSettings settings) =>
        ToSettingsObject(settings, includeOperatorState: true)
            .ToJsonString(JsonOptions);

    public static JsonObject ToPresetSettingsObject(AppSettings settings) =>
        ToSettingsObject(settings, includeOperatorState: false);

    public static AppSettings DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
        ?? OperatorDefaults.Create();

    public static AppSettings DeserializeSettings(JsonNode? node) =>
        node?.Deserialize<AppSettings>(JsonOptions)
        ?? OperatorDefaults.Create();

    private static JsonObject ToSettingsObject(
        AppSettings settings,
        bool includeOperatorState)
    {
        AppSettings normalized = settings.Copy();
        normalized.Normalize();
        JsonObject result =
            JsonSerializer.SerializeToNode(normalized, JsonOptions)
                as JsonObject
            ?? [];
        result.Remove(nameof(AppSettings.ImagePlaylists));
        result.Remove(nameof(AppSettings.ActiveImagePlaylistId));
        if (!includeOperatorState)
        {
            result.Remove(nameof(AppSettings.ActivePresetId));
            result.Remove(nameof(AppSettings.WelcomeShown));
        }
        return result;
    }
}
