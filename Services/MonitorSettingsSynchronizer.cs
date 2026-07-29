using WallpaperMatrix.Models;
using WallpaperMatrix.Rendering;

namespace WallpaperMatrix.Services;

internal static class MonitorSettingsSynchronizer
{
    public static void SynchronizePrimary(
        AppSettings settings,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            return;
        MonitorTopology.EnsureProfiles(settings, monitors);
        MonitorDescriptor primaryMonitor =
            monitors.FirstOrDefault(monitor => monitor.Primary)
            ?? monitors[0];
        MonitorProfile primary = MonitorTopology.Find(
                settings.MonitorProfiles,
                primaryMonitor.Id)
            ?? settings.MonitorProfiles[0];
        IReadOnlyDictionary<string, MonitorRoute> flowRoutes =
            MonitorTopology.Resolve(
                    settings.MonitorProfiles,
                    monitors,
                    MonitorRouteDomain.Flow)
                .ToDictionary(
                    route => route.MonitorId,
                    StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, MonitorRoute> databaseRoutes =
            MonitorTopology.Resolve(
                    settings.MonitorProfiles,
                    monitors,
                    MonitorRouteDomain.Database)
                .ToDictionary(
                    route => route.MonitorId,
                    StringComparer.OrdinalIgnoreCase);
        MonitorProfile flowRoot = MonitorTopology.Find(
                settings.MonitorProfiles,
                flowRoutes[primaryMonitor.Id].RootMonitorId)
            ?? primary;
        MonitorProfile databaseRoot = MonitorTopology.Find(
                settings.MonitorProfiles,
                databaseRoutes[primaryMonitor.Id].RootMonitorId)
            ?? primary;

        MonitorSettingsComposer.CopyFlow(flowRoot.Settings, settings);
        MonitorSettingsComposer.CopyDatabase(
            databaseRoot.Settings,
            settings);
        if (databaseRoutes[primaryMonitor.Id].Mode
            == MonitorLinkMode.Disabled)
        {
            settings.ImageMode = false;
        }
    }
}
