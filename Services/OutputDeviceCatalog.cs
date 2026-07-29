using WallpaperMatrix.Models;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Services;

/// <summary>
/// Combines physical displays reported by Windows with the single portable
/// virtual output device owned by Wallpaper Matrix. The virtual bounds are
/// logical scene coordinates only and never alter the Windows desktop.
/// </summary>
internal static class OutputDeviceCatalog
{
    public const string VirtualMonitorId =
        "wallpaper-matrix://virtual-monitor";

    public static IReadOnlyList<MonitorDescriptor> Capture(
        AppSettings settings)
    {
        List<MonitorDescriptor> devices =
            MonitorCatalog.Capture().ToList();
        if (devices.Count == 0)
            return devices;

        MonitorDescriptor primary =
            devices.FirstOrDefault(device => device.Primary)
            ?? devices[0];
        DrawingRectangle physicalDesktop = devices
            .Select(device => device.Bounds)
            .Aggregate(DrawingRectangle.Union);
        DrawingRectangle bounds = Place(
            primary.Bounds,
            physicalDesktop,
            settings.VirtualOutputWidth,
            settings.VirtualOutputHeight,
            settings.VirtualMonitorOffsetX,
            settings.VirtualMonitorOffsetY,
            settings.VirtualMonitorDock);
        devices.Add(new MonitorDescriptor(
            VirtualMonitorId,
            "VIRTUAL",
            "Виртуальный монитор",
            0,
            bounds,
            Primary: false,
            IsVirtual: true));
        return devices;
    }

    public static bool IsVirtual(string monitorId) =>
        string.Equals(
            monitorId,
            VirtualMonitorId,
            StringComparison.OrdinalIgnoreCase);

    private static DrawingRectangle Place(
        DrawingRectangle primary,
        DrawingRectangle physicalDesktop,
        int requestedWidth,
        int requestedHeight,
        int? requestedOffsetX,
        int? requestedOffsetY,
        string dock)
    {
        int width = Math.Clamp(requestedWidth, 320, 7680);
        int height = Math.Clamp(requestedHeight, 180, 4320);
        if (requestedOffsetX.HasValue && requestedOffsetY.HasValue)
        {
            return new DrawingRectangle(
                primary.Left + requestedOffsetX.Value,
                primary.Top + requestedOffsetY.Value,
                width,
                height);
        }

        const int gap = 160;
        return dock switch
        {
            "Left" => new DrawingRectangle(
                physicalDesktop.Left - width - gap,
                primary.Top,
                width,
                height),
            "Top" => new DrawingRectangle(
                primary.Left,
                physicalDesktop.Top - height - gap,
                width,
                height),
            "Bottom" => new DrawingRectangle(
                primary.Left,
                physicalDesktop.Bottom + gap,
                width,
                height),
            _ => new DrawingRectangle(
                physicalDesktop.Right + gap,
                primary.Top,
                width,
                height)
        };
    }
}
