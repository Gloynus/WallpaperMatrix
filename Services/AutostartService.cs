using Microsoft.Win32;
using System.IO;

namespace WallpaperMatrix.Services;

public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WallpaperMatrix";

    public static void SetEnabled(
        bool enabled,
        bool claimOwnership = true)
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        string executable = Path.GetFullPath(
            Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Не найден путь к исполняемому файлу."));
        string? registeredCommand =
            key.GetValue(ValueName) as string;

        if (enabled)
        {
            bool hasOwner =
                !string.IsNullOrWhiteSpace(registeredCommand);
            if (claimOwnership
                || !hasOwner
                || CommandBelongsTo(
                    registeredCommand,
                    executable))
            {
                key.SetValue(
                    ValueName,
                    $"\"{executable}\" --background",
                    RegistryValueKind.String);
            }

            if (claimOwnership)
            {
                RemoveDuplicateWallpaperMatrixEntries(
                    key,
                    executable,
                    removeEveryCopy: true);
                DiagnosticLog.Write(
                    $"Автозапуск передан текущему экземпляру: {executable}");
            }
        }
        else
        {
            if (claimOwnership
                || CommandBelongsTo(registeredCommand, executable))
            {
                key.DeleteValue(
                    ValueName,
                    throwOnMissingValue: false);
            }
            RemoveDuplicateWallpaperMatrixEntries(
                key,
                executable,
                removeEveryCopy: claimOwnership);
        }
    }

    private static void RemoveDuplicateWallpaperMatrixEntries(
        RegistryKey key,
        string currentExecutable,
        bool removeEveryCopy)
    {
        foreach (string name in key.GetValueNames())
        {
            if (string.Equals(
                    name,
                    ValueName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? command = key.GetValue(name) as string;
            string? executable = ExecutableFromCommand(command);
            if (executable is null
                || !string.Equals(
                    Path.GetFileName(executable),
                    "WallpaperMatrix.exe",
                    StringComparison.OrdinalIgnoreCase)
                || (!removeEveryCopy
                    && !PathsEqual(
                        executable,
                        currentExecutable)))
            {
                continue;
            }

            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static bool CommandBelongsTo(
        string? command,
        string executable)
    {
        string? registeredExecutable =
            ExecutableFromCommand(command);
        return registeredExecutable is not null
            && PathsEqual(registeredExecutable, executable);
    }

    private static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        string trimmed = command.Trim();
        string candidate;
        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1)
                return null;
            candidate = trimmed[1..closingQuote];
        }
        else
        {
            int separator = trimmed.IndexOf(' ');
            candidate = separator < 0
                ? trimmed
                : trimmed[..separator];
        }

        try
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(candidate));
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
