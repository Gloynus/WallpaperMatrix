namespace WallpaperMatrix.Models;

public enum MonitorLinkMode
{
    Isolated,
    Relay,
    Extend,
    Disabled
}

/// <summary>
/// Persists the operator's routing and visual state for one physical display.
/// The nested settings intentionally contain no monitor profiles of their own.
/// </summary>
public sealed class MonitorProfile
{
    public string MonitorId { get; set; } = "";
    public string LastKnownName { get; set; } = "";
    public int LastKnownLeft { get; set; }
    public int LastKnownTop { get; set; }
    public int LastKnownWidth { get; set; }
    public int LastKnownHeight { get; set; }
    public bool WasPrimary { get; set; }
    public bool WasConnected { get; set; } = true;
    public MonitorLinkMode FlowMode { get; set; } = MonitorLinkMode.Isolated;
    public string FlowSourceMonitorId { get; set; } = "";
    public MonitorLinkMode DatabaseMode { get; set; } = MonitorLinkMode.Isolated;
    public string DatabaseSourceMonitorId { get; set; } = "";
    public AppSettings Settings { get; set; } = new();

    public MonitorProfile Copy()
    {
        AppSettings settings = Settings.Copy(includeMonitorProfiles: false);
        settings.MonitorProfiles = [];
        return new MonitorProfile
        {
            MonitorId = MonitorId,
            LastKnownName = LastKnownName,
            LastKnownLeft = LastKnownLeft,
            LastKnownTop = LastKnownTop,
            LastKnownWidth = LastKnownWidth,
            LastKnownHeight = LastKnownHeight,
            WasPrimary = WasPrimary,
            WasConnected = WasConnected,
            FlowMode = FlowMode,
            FlowSourceMonitorId = FlowSourceMonitorId,
            DatabaseMode = DatabaseMode,
            DatabaseSourceMonitorId = DatabaseSourceMonitorId,
            Settings = settings
        };
    }

    public void Normalize()
    {
        MonitorId = MonitorId?.Trim() ?? "";
        LastKnownName = LastKnownName?.Trim() ?? "";
        FlowSourceMonitorId = FlowSourceMonitorId?.Trim() ?? "";
        DatabaseSourceMonitorId = DatabaseSourceMonitorId?.Trim() ?? "";
        Settings ??= new AppSettings();
        Settings.MonitorProfiles = [];
        Settings.Normalize(includeMonitorProfiles: false);
    }
}
