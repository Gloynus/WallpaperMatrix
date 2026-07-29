using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WallpaperMatrix.Models;

namespace WallpaperMatrix.Services;

public static class MonitorCatalog
{
    private const int DisplayDeviceActive = 0x1;
    private const uint QueryOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;
    private static int _displayIdentityLogged;

    public static IReadOnlyList<MonitorDescriptor> Capture()
    {
        IReadOnlyDictionary<string, int> pathPriorities =
            ReadCcdPathPriorities();
        System.Windows.Forms.Screen[] screens =
            System.Windows.Forms.Screen.AllScreens
                .OrderByDescending(screen => screen.Primary)
                .ThenBy(screen => screen.Bounds.Left)
                .ThenBy(screen => screen.Bounds.Top)
                .ToArray();
        IReadOnlyDictionary<string, int> displayNumbers =
            BuildStableDisplayLabels(screens, pathPriorities);
        List<MonitorDescriptor> result = [];
        for (int index = 0; index < screens.Length; index++)
        {
            System.Windows.Forms.Screen screen = screens[index];
            int displayNumber = displayNumbers.TryGetValue(
                screen.DeviceName,
                out int windowsNumber)
                    ? windowsNumber
                    : index + 1;
            (string id, string name) = ReadFriendlyIdentity(
                screen.DeviceName,
                displayNumber);
            result.Add(new MonitorDescriptor(
                id,
                screen.DeviceName,
                name,
                displayNumber,
                screen.Bounds,
                screen.Primary));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, int> BuildStableDisplayLabels(
        IReadOnlyList<System.Windows.Forms.Screen> screens,
        IReadOnlyDictionary<string, int> pathPriorities)
    {
        // Windows does not expose the decorative numbers drawn by Settings as
        // a documented monitor property. Keep the primary device first and
        // use stable CCD path priority for the rest. Coordinates are
        // deliberately not involved: moving a thumbnail must not silently
        // rename a Wallpaper Matrix channel.
        return screens
            .OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => pathPriorities.GetValueOrDefault(
                screen.DeviceName,
                int.MaxValue))
            .ThenBy(
                screen => screen.DeviceName,
                StringComparer.OrdinalIgnoreCase)
            .Select((screen, index) => (screen.DeviceName, Number: index + 1))
            .ToDictionary(
                item => item.DeviceName,
                item => item.Number,
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int>
        ReadCcdPathPriorities()
    {
        Dictionary<string, int> result =
            new(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int sizeResult = GetDisplayConfigBufferSizes(
                    QueryOnlyActivePaths,
                    out uint pathCount,
                    out uint modeCount);
                if (sizeResult != ErrorSuccess)
                    return result;

                DisplayConfigPathInfo[] paths =
                    new DisplayConfigPathInfo[pathCount];
                DisplayConfigModeInfo[] modes =
                    new DisplayConfigModeInfo[modeCount];
                int queryResult = QueryDisplayConfig(
                    QueryOnlyActivePaths,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);
                if (queryResult == ErrorInsufficientBuffer)
                    continue;
                if (queryResult != ErrorSuccess)
                    return result;

                // QueryDisplayConfig guarantees path-priority order. This is
                // stable display topology data, but it is not the decorative
                // number painted by the Settings application.
                List<string> diagnostics = [];
                for (int index = 0; index < pathCount; index++)
                {
                    DisplayConfigSourceDeviceName sourceName = new()
                    {
                        Header = new DisplayConfigDeviceInfoHeader
                        {
                            Type = GetSourceName,
                            Size = (uint)Marshal.SizeOf<
                                DisplayConfigSourceDeviceName>(),
                            AdapterId = paths[index].SourceInfo.AdapterId,
                            Id = paths[index].SourceInfo.Id
                        },
                        ViewGdiDeviceName = ""
                    };
                    if (DisplayConfigGetDeviceInfo(ref sourceName)
                            != ErrorSuccess
                        || string.IsNullOrWhiteSpace(
                            sourceName.ViewGdiDeviceName))
                    {
                        continue;
                    }
                    DisplayConfigTargetDeviceName targetName = new()
                    {
                        Header = new DisplayConfigDeviceInfoHeader
                        {
                            Type = GetTargetName,
                            Size = (uint)Marshal.SizeOf<
                                DisplayConfigTargetDeviceName>(),
                            AdapterId = paths[index].TargetInfo.AdapterId,
                            Id = paths[index].TargetInfo.Id
                        },
                        MonitorFriendlyDeviceName = "",
                        MonitorDevicePath = ""
                    };
                    _ = DisplayConfigGetDeviceInfo(ref targetName);
                    result.TryAdd(
                        sourceName.ViewGdiDeviceName.Trim(),
                        index + 1);
                    diagnostics.Add(
                        $"path={index + 1}, "
                        + $"source={paths[index].SourceInfo.Id}, "
                        + $"target={paths[index].TargetInfo.Id}, "
                        + $"gdi={sourceName.ViewGdiDeviceName.Trim()}, "
                        + $"name={targetName.MonitorFriendlyDeviceName.Trim()}");
                }
                if (Interlocked.Exchange(
                        ref _displayIdentityLogged,
                        1) == 0)
                {
                    DiagnosticLog.Write(
                        "CCD-порядок экранов Windows: "
                        + string.Join("; ", diagnostics)
                        + $"; pathStruct={Marshal.SizeOf<
                            DisplayConfigPathInfo>()}; "
                        + $"modeStruct={Marshal.SizeOf<
                            DisplayConfigModeInfo>()}.");
                }
                return result;
            }
        }
        catch
        {
            // A stable primary-first fallback remains available for an
            // unusual or temporarily unavailable display driver.
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
                // Windows Settings prefers the EDID display-name descriptor.
                // EnumDisplayDevices often returns only a hardware code such
                // as XMI27B2 even though EDID contains “Mi Monitor”.
                string friendly = ReadEdidName(monitor.DeviceKey);
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = ReadEdidNameFromDeviceId(monitor.DeviceId);
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = CleanMonitorName(monitor.DeviceString);
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = ReadHardwareModel(monitor.DeviceId);
                if (!string.IsNullOrWhiteSpace(friendly))
                    return (id, friendly);
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
            return DecodeEdidName(edid);
        }
        catch
        {
            // Friendly names are optional; the stable display number remains.
        }
        return "";
    }

    private static string ReadEdidNameFromDeviceId(string deviceId)
    {
        string[] parts = (deviceId ?? "")
            .Split(
                ['\\', '/'],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return "";
        try
        {
            using RegistryKey? model = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}");
            if (model is null)
                return "";
            foreach (string instanceName in model.GetSubKeyNames())
            {
                using RegistryKey? parameters = model.OpenSubKey(
                    $@"{instanceName}\Device Parameters");
                if (parameters?.GetValue("EDID") is not byte[] edid)
                    continue;
                string name = DecodeEdidName(edid);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
            // The display number and hardware model remain safe fallbacks.
        }
        return "";
    }

    private static string DecodeEdidName(byte[] edid)
    {
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

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public DisplayConfigLuid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public DisplayConfigLuid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTwoDimensionalRegion
    {
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong PixelRate;
        public DisplayConfigRational HorizontalSyncFrequency;
        public DisplayConfigRational VerticalSyncFrequency;
        public DisplayConfigTwoDimensionalRegion ActiveSize;
        public DisplayConfigTwoDimensionalRegion TotalSize;
        public uint AdditionalSignalInfo;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetMode
    {
        public DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public DisplayConfigPoint Position;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)]
        public uint InfoType;
        [FieldOffset(4)]
        public uint Id;
        [FieldOffset(8)]
        public DisplayConfigLuid AdapterId;
        [FieldOffset(16)]
        public DisplayConfigTargetMode TargetMode;
        [FieldOffset(16)]
        public DisplayConfigSourceMode SourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public DisplayConfigLuid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(
        ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(
        ref DisplayConfigTargetDeviceName requestPacket);
}
