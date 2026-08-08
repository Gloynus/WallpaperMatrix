using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

/// <summary>
/// Reads Explorer's current wallpaper without changing the desktop. The
/// returned paths are used only as the transient zero frame of a database.
/// </summary>
internal static class SystemWallpaperService
{
    private static readonly Guid DesktopWallpaperClass =
        new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    public static SystemWallpaperSnapshot Capture(
        AppSettings settings)
    {
        IReadOnlyList<MonitorDescriptor> monitors =
            OutputDeviceCatalog.Capture(settings);
        Dictionary<string, string> result =
            new(StringComparer.OrdinalIgnoreCase);
        object? comObject = null;
        DesktopWallpaperPosition position = ReadRegistryPosition();
        uint backgroundColor = ReadRegistryBackgroundColor();
        try
        {
            Type? type = Type.GetTypeFromCLSID(
                DesktopWallpaperClass,
                throwOnError: false);
            if (type is not null)
            {
                comObject = Activator.CreateInstance(type);
                if (comObject is IDesktopWallpaper desktop)
                {
                    try
                    {
                        position = desktop.GetPosition();
                        backgroundColor = desktop.GetBackgroundColor();
                    }
                    catch (COMException)
                    {
                        // The registry values above remain a complete fallback
                        // on Explorer builds that expose an older shell object.
                    }
                    uint count = desktop.GetMonitorDevicePathCount();
                    for (uint index = 0; index < count; index++)
                    {
                        string devicePath =
                            desktop.GetMonitorDevicePathAt(index);
                        desktop.GetMonitorRECT(
                            devicePath,
                            out NativeRect rect);
                        string path = desktop.GetWallpaper(devicePath);
                        if (!IsUsable(path))
                            continue;
                        MonitorDescriptor? monitor = monitors.FirstOrDefault(
                            candidate =>
                                !candidate.IsVirtual
                                && candidate.Bounds.Left == rect.Left
                                && candidate.Bounds.Top == rect.Top
                                && candidate.Bounds.Right == rect.Right
                                && candidate.Bounds.Bottom == rect.Bottom);
                        if (monitor is not null)
                            result[monitor.Id] = path;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                "Не удалось прочитать системные обои отдельных экранов.",
                exception);
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
        }

        string fallback = ReadRegistryWallpaper();
        MonitorDescriptor? primary = monitors.FirstOrDefault(monitor =>
            monitor.Primary);
        if (primary is not null
            && !result.ContainsKey(primary.Id)
            && IsUsable(fallback))
        {
            result[primary.Id] = fallback;
        }
        string sharedFallback = primary is not null
            && result.TryGetValue(primary.Id, out string? primaryPath)
                ? primaryPath
                : result.Values.FirstOrDefault() ?? fallback;
        if (IsUsable(sharedFallback))
        {
            foreach (MonitorDescriptor monitor in monitors.Where(monitor =>
                         !monitor.IsVirtual))
                result.TryAdd(monitor.Id, sharedFallback);
        }
        return new SystemWallpaperSnapshot(
            result,
            position,
            backgroundColor);
    }

    private static DesktopWallpaperPosition ReadRegistryPosition()
    {
        try
        {
            using RegistryKey? desktop = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop",
                writable: false);
            string style = Convert.ToString(
                desktop?.GetValue("WallpaperStyle")) ?? "";
            string tiled = Convert.ToString(
                desktop?.GetValue("TileWallpaper")) ?? "";
            if (string.Equals(tiled, "1", StringComparison.Ordinal))
                return DesktopWallpaperPosition.Tile;
            return style switch
            {
                "2" => DesktopWallpaperPosition.Stretch,
                "6" => DesktopWallpaperPosition.Fit,
                "10" => DesktopWallpaperPosition.Fill,
                "22" => DesktopWallpaperPosition.Span,
                _ => DesktopWallpaperPosition.Center
            };
        }
        catch
        {
            return DesktopWallpaperPosition.Fill;
        }
    }

    private static uint ReadRegistryBackgroundColor()
    {
        try
        {
            using RegistryKey? colors = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Colors",
                writable: false);
            string text = Convert.ToString(colors?.GetValue("Background"))
                ?? "";
            string[] parts = text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && byte.TryParse(parts[0], out byte red)
                && byte.TryParse(parts[1], out byte green)
                && byte.TryParse(parts[2], out byte blue))
            {
                return red | ((uint)green << 8) | ((uint)blue << 16);
            }
        }
        catch
        {
        }
        return 0;
    }

    private static string ReadRegistryWallpaper()
    {
        try
        {
            using RegistryKey? desktop = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop",
                writable: false);
            return desktop?.GetValue("WallPaper") as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && ImagePlaylistCatalog.IsSupportedImage(path)
        && File.Exists(path);

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        void GetMonitorRECT(
            [MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            out NativeRect displayRect);

        void SetBackgroundColor(uint color);

        uint GetBackgroundColor();

        void SetPosition(DesktopWallpaperPosition position);

        DesktopWallpaperPosition GetPosition();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed record SystemWallpaperSnapshot(
    IReadOnlyDictionary<string, string> Paths,
    DesktopWallpaperPosition Position,
    uint BackgroundColor);

internal enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5
}
