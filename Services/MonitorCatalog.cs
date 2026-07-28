using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public static class MonitorCatalog
{
    private const int DisplayDeviceActive = 0x1;

    public static IReadOnlyList<MonitorDescriptor> Capture()
    {
        System.Windows.Forms.Screen[] screens =
            System.Windows.Forms.Screen.AllScreens
                .OrderByDescending(screen => screen.Primary)
                .ThenBy(screen => screen.Bounds.Left)
                .ThenBy(screen => screen.Bounds.Top)
                .ToArray();
        List<MonitorDescriptor> result = [];
        for (int index = 0; index < screens.Length; index++)
        {
            System.Windows.Forms.Screen screen = screens[index];
            (string id, string name) = ReadFriendlyIdentity(
                screen.DeviceName,
                index + 1);
            result.Add(new MonitorDescriptor(
                id,
                screen.DeviceName,
                name,
                screen.Bounds,
                screen.Primary));
        }
        return result;
    }

    private static (string Id, string Name) ReadFriendlyIdentity(
        string systemName,
        int displayNumber)
    {
        DisplayDevice monitor = DisplayDevice.Create();
        try
        {
            if (EnumDisplayDevices(
                    systemName,
                    0,
                    ref monitor,
                    0)
                && (monitor.StateFlags & DisplayDeviceActive) != 0)
            {
                string id = string.IsNullOrWhiteSpace(monitor.DeviceId)
                    ? systemName
                    : $"{systemName}|{monitor.DeviceId.Trim()}";
                string friendly = CleanMonitorName(monitor.DeviceString);
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = ReadEdidName(monitor.DeviceKey);
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = ReadHardwareModel(monitor.DeviceId);
                if (!string.IsNullOrWhiteSpace(friendly))
                    return (id, $"{friendly} [{displayNumber}]");
                return (id, $"МОНИТОР {displayNumber}");
            }
        }
        catch
        {
            // DeviceName remains a stable-enough fallback for the session.
        }
        return (systemName, $"МОНИТОР {displayNumber}");
    }

    private static string CleanMonitorName(string value)
    {
        string name = value?.Trim() ?? "";
        return name.Equals(
                "Generic PnP Monitor",
                StringComparison.OrdinalIgnoreCase)
            || name.Equals(
                "Generic Non-PnP Monitor",
                StringComparison.OrdinalIgnoreCase)
            ? ""
            : name;
    }

    private static string ReadEdidName(string deviceKey)
    {
        const string machinePrefix = @"\Registry\Machine\";
        if (string.IsNullOrWhiteSpace(deviceKey)
            || !deviceKey.StartsWith(
                machinePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        try
        {
            string registryPath = deviceKey[machinePrefix.Length..];
            using RegistryKey? key =
                Registry.LocalMachine.OpenSubKey(registryPath);
            using RegistryKey? nestedParameters =
                key?.OpenSubKey("Device Parameters");
            RegistryKey? parameters = nestedParameters ?? key;
            if (parameters?.GetValue("EDID") is not byte[] edid
                || edid.Length < 128)
            {
                return "";
            }
            for (int offset = 54;
                 offset + 18 <= edid.Length && offset <= 108;
                 offset += 18)
            {
                if (edid[offset] != 0
                    || edid[offset + 1] != 0
                    || edid[offset + 2] != 0
                    || edid[offset + 3] != 0xFC)
                {
                    continue;
                }
                string name = Encoding.ASCII
                    .GetString(edid, offset + 5, 13)
                    .Trim('\0', '\r', '\n', ' ');
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
            // Friendly names are optional; the stable display number remains.
        }
        return "";
    }

    private static string ReadHardwareModel(string deviceId)
    {
        string[] parts = (deviceId ?? "")
            .Split(
                ['\\', '/'],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            ? parts[1].Replace('_', ' ')
            : "";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;

        public static DisplayDevice Create() => new()
        {
            Size = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = "",
            DeviceString = "",
            DeviceId = "",
            DeviceKey = ""
        };
    }

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);
}
