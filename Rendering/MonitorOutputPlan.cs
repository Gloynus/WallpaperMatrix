using WallpaperMatrix.Models;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Rendering;

internal sealed record MonitorSceneTarget(
    string MonitorId,
    DrawingRectangle TargetBounds,
    DrawingRectangle SourceBounds);

internal sealed record MatrixImageProjection(
    int CanvasWidth,
    int CanvasHeight,
    DrawingRectangle ViewportBounds,
    DrawingRectangle DestinationBounds);

internal sealed record MonitorScenePlan(
    string Id,
    string FlowRootMonitorId,
    string DatabaseRootMonitorId,
    DrawingRectangle CanvasBounds,
    MatrixImageProjection ImageProjection,
    AppSettings Settings,
    int RandomSeed,
    IReadOnlyList<MonitorSceneTarget> Targets);

internal sealed record MonitorOutputPlan(
    DrawingRectangle VirtualBounds,
    IReadOnlyList<MonitorScenePlan> Scenes,
    int ActiveMonitorCount)
{
    public static MonitorOutputPlan Create(
        AppSettings settings,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
            throw new InvalidOperationException(
                "Windows не сообщил ни об одном активном экране.");

        AppSettings normalized = settings.Copy();
        MonitorTopology.EnsureProfiles(normalized, monitors);
        IReadOnlyDictionary<string, MonitorRoute> flowRoutes =
            MonitorTopology.Resolve(
                    normalized.MonitorProfiles,
                    monitors,
                    MonitorRouteDomain.Flow)
                .ToDictionary(
                    route => route.MonitorId,
                    StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, MonitorRoute> databaseRoutes =
            MonitorTopology.Resolve(
                    normalized.MonitorProfiles,
                    monitors,
                    MonitorRouteDomain.Database)
                .ToDictionary(
                    route => route.MonitorId,
                    StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MonitorDescriptor> monitorById = monitors
            .ToDictionary(
                monitor => monitor.Id,
                StringComparer.OrdinalIgnoreCase);
        DrawingRectangle virtualBounds = monitors
            .Select(monitor => monitor.Bounds)
            .Aggregate(DrawingRectangle.Union);
        List<TargetDraft> targets = [];

        foreach (MonitorDescriptor monitor in monitors)
        {
            MonitorRoute flow = flowRoutes[monitor.Id];
            if (flow.Mode == MonitorLinkMode.Disabled)
                continue;
            MonitorRoute database = databaseRoutes[monitor.Id];
            string databaseRoot = database.Mode == MonitorLinkMode.Disabled
                ? ""
                : database.RootMonitorId;
            MonitorProfile targetProfile =
                MonitorTopology.Find(
                    normalized.MonitorProfiles,
                    monitor.Id)
                ?? throw new InvalidOperationException(
                    $"Профиль экрана {monitor.Id} не найден.");
            MonitorProfile flowRoot =
                MonitorTopology.Find(
                    normalized.MonitorProfiles,
                    flow.RootMonitorId)
                ?? targetProfile;
            MonitorProfile databaseRootProfile =
                MonitorTopology.Find(
                    normalized.MonitorProfiles,
                    databaseRoot)
                ?? targetProfile;

            bool flowExtended = flow.Mode == MonitorLinkMode.Extend
                || monitors.Any(candidate =>
                    flowRoutes[candidate.Id].Mode == MonitorLinkMode.Extend
                    && string.Equals(
                        flowRoutes[candidate.Id].RootMonitorId,
                        flow.RootMonitorId,
                        StringComparison.OrdinalIgnoreCase));
            DrawingRectangle canvasBounds = flowExtended
                ? monitors
                    .Where(candidate =>
                        flowRoutes[candidate.Id].Mode
                            != MonitorLinkMode.Disabled
                        && string.Equals(
                            flowRoutes[candidate.Id].RootMonitorId,
                            flow.RootMonitorId,
                            StringComparison.OrdinalIgnoreCase)
                        && (flowRoutes[candidate.Id].Mode
                                == MonitorLinkMode.Extend
                            || string.Equals(
                                candidate.Id,
                                flow.RootMonitorId,
                                StringComparison.OrdinalIgnoreCase)))
                    .Select(candidate => candidate.Bounds)
                    .DefaultIfEmpty(
                        monitorById[flow.RootMonitorId].Bounds)
                    .Aggregate(DrawingRectangle.Union)
                : monitorById[flow.RootMonitorId].Bounds;
            DrawingRectangle flowViewport = flowExtended
                ? flow.Mode == MonitorLinkMode.Relay
                    ? new DrawingRectangle(
                        monitorById[flow.RootMonitorId].Bounds.Left
                            - canvasBounds.Left,
                        monitorById[flow.RootMonitorId].Bounds.Top
                            - canvasBounds.Top,
                        monitorById[flow.RootMonitorId].Bounds.Width,
                        monitorById[flow.RootMonitorId].Bounds.Height)
                    : new DrawingRectangle(
                        monitor.Bounds.Left - canvasBounds.Left,
                        monitor.Bounds.Top - canvasBounds.Top,
                        monitor.Bounds.Width,
                        monitor.Bounds.Height)
                : new DrawingRectangle(
                    0,
                    0,
                    canvasBounds.Width,
                    canvasBounds.Height);
            DrawingRectangle presentationSource = CropToAspect(
                flowViewport,
                monitor.Bounds);

            bool databaseExtended = database.Mode != MonitorLinkMode.Disabled
                && (database.Mode == MonitorLinkMode.Extend
                    || monitors.Any(candidate =>
                        databaseRoutes[candidate.Id].Mode
                            == MonitorLinkMode.Extend
                        && string.Equals(
                            databaseRoutes[candidate.Id].RootMonitorId,
                            database.RootMonitorId,
                            StringComparison.OrdinalIgnoreCase)));
            bool relayCopiesFlowRootProjection =
                database.Mode == MonitorLinkMode.Relay
                && string.Equals(
                    flow.RootMonitorId,
                    database.RootMonitorId,
                    StringComparison.OrdinalIgnoreCase);
            DrawingRectangle databaseCanvas = databaseExtended
                ? monitors
                    .Where(candidate =>
                        databaseRoutes[candidate.Id].Mode
                            != MonitorLinkMode.Disabled
                        && string.Equals(
                            databaseRoutes[candidate.Id].RootMonitorId,
                            database.RootMonitorId,
                            StringComparison.OrdinalIgnoreCase)
                        && (databaseRoutes[candidate.Id].Mode
                                == MonitorLinkMode.Extend
                            || string.Equals(
                                candidate.Id,
                                database.RootMonitorId,
                                StringComparison.OrdinalIgnoreCase)))
                    .Select(candidate => candidate.Bounds)
                    .DefaultIfEmpty(monitor.Bounds)
                    .Aggregate(DrawingRectangle.Union)
                : database.Mode == MonitorLinkMode.Disabled
                    ? monitor.Bounds
                    : relayCopiesFlowRootProjection
                        ? monitorById[database.RootMonitorId].Bounds
                        : monitor.Bounds;
            bool sharedSpatialProjection =
                flowExtended == databaseExtended
                && canvasBounds == databaseCanvas;
            DrawingRectangle imageDestination = sharedSpatialProjection
                ? new DrawingRectangle(
                    0,
                    0,
                    canvasBounds.Width,
                    canvasBounds.Height)
                : presentationSource;
            DrawingRectangle databaseViewport = sharedSpatialProjection
                || !databaseExtended
                ? new DrawingRectangle(
                    0,
                    0,
                    databaseCanvas.Width,
                    databaseCanvas.Height)
                : new DrawingRectangle(
                    (database.Mode == MonitorLinkMode.Relay
                        ? monitorById[database.RootMonitorId].Bounds.Left
                        : monitor.Bounds.Left) - databaseCanvas.Left,
                    (database.Mode == MonitorLinkMode.Relay
                        ? monitorById[database.RootMonitorId].Bounds.Top
                        : monitor.Bounds.Top) - databaseCanvas.Top,
                    database.Mode == MonitorLinkMode.Relay
                        ? monitorById[database.RootMonitorId].Bounds.Width
                        : monitor.Bounds.Width,
                    database.Mode == MonitorLinkMode.Relay
                        ? monitorById[database.RootMonitorId].Bounds.Height
                        : monitor.Bounds.Height);
            MatrixImageProjection imageProjection = new(
                Math.Max(1, databaseCanvas.Width),
                Math.Max(1, databaseCanvas.Height),
                databaseViewport,
                imageDestination);

            AppSettings effective = MonitorSettingsComposer.Compose(
                normalized,
                targetProfile.Settings,
                flowRoot.Settings,
                databaseRootProfile.Settings,
                database.Mode == MonitorLinkMode.Disabled);
            string databaseProjection = DatabaseProjectionKey(
                monitor,
                sharedSpatialProjection,
                imageProjection);
            string sceneId = string.Join(
                "|",
                flow.RootMonitorId,
                flowExtended ? "EXTEND" : "RELAY",
                databaseRoot,
                databaseProjection,
                ClockProjectionKey(targetProfile.Settings),
                canvasBounds.Left,
                canvasBounds.Top,
                canvasBounds.Width,
                canvasBounds.Height);
            targets.Add(new TargetDraft(
                sceneId,
                flow.RootMonitorId,
                databaseRoot,
                canvasBounds,
                imageProjection,
                effective,
                StableSeed(flow.RootMonitorId),
                new MonitorSceneTarget(
                    monitor.Id,
                    new DrawingRectangle(
                        monitor.Bounds.Left - virtualBounds.Left,
                        monitor.Bounds.Top - virtualBounds.Top,
                        monitor.Bounds.Width,
                        monitor.Bounds.Height),
                    presentationSource)));
        }

        List<MonitorScenePlan> scenes = targets
            .GroupBy(target => target.SceneId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                TargetDraft first = group.First();
                return new MonitorScenePlan(
                    first.SceneId,
                    first.FlowRootMonitorId,
                    first.DatabaseRootMonitorId,
                    first.CanvasBounds,
                    first.ImageProjection,
                    first.Settings,
                    first.RandomSeed,
                    group.Select(target => target.Target).ToArray());
            })
            .ToList();
        return new MonitorOutputPlan(
            virtualBounds,
            scenes,
            targets.Count);
    }

    private static string DatabaseProjectionKey(
        MonitorDescriptor target,
        bool sharedSpatialProjection,
        MatrixImageProjection projection)
    {
        if (sharedSpatialProjection)
            return $"SHARED:{projection.CanvasWidth}x{projection.CanvasHeight}";
        return $"TARGET:{target.Id}:{projection}";
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            int hash = 17;
            foreach (char character in value.ToUpperInvariant())
                hash = hash * 31 + character;
            return hash;
        }
    }

    private static DrawingRectangle CropToAspect(
        DrawingRectangle source,
        DrawingRectangle target)
    {
        if (source.Width <= 0
            || source.Height <= 0
            || target.Width <= 0
            || target.Height <= 0)
        {
            return source;
        }

        double sourceAspect = source.Width / (double)source.Height;
        double targetAspect = target.Width / (double)target.Height;
        if (Math.Abs(sourceAspect - targetAspect) < 0.0001)
            return source;

        if (sourceAspect > targetAspect)
        {
            int width = Math.Clamp(
                (int)Math.Round(source.Height * targetAspect),
                1,
                source.Width);
            return new DrawingRectangle(
                source.Left + (source.Width - width) / 2,
                source.Top,
                width,
                source.Height);
        }

        int height = Math.Clamp(
            (int)Math.Round(source.Width / targetAspect),
            1,
            source.Height);
        return new DrawingRectangle(
            source.Left,
            source.Top + (source.Height - height) / 2,
            source.Width,
            height);
    }

    private static string ClockProjectionKey(AppSettings settings) =>
        string.Join(
            ":",
            settings.ClockEnabled,
            settings.ClockPosition,
            settings.ClockHorizontalMarginCells,
            settings.ClockVerticalMarginCells,
            settings.ClockBrightness.ToString("R"),
            settings.ClockWeight.ToString("R"));

    private sealed record TargetDraft(
        string SceneId,
        string FlowRootMonitorId,
        string DatabaseRootMonitorId,
        DrawingRectangle CanvasBounds,
        MatrixImageProjection ImageProjection,
        AppSettings Settings,
        int RandomSeed,
        MonitorSceneTarget Target);
}

internal static class MonitorSettingsComposer
{
    public static AppSettings Compose(
        AppSettings global,
        AppSettings target,
        AppSettings flow,
        AppSettings database,
        bool databaseDisabled)
    {
        AppSettings result = target.Copy(includeMonitorProfiles: false);
        CopyFlow(flow, result);
        CopyDatabase(database, result);
        if (databaseDisabled)
            result.ImageMode = false;
        result.FramesPerSecond = global.FramesPerSecond;
        result.StartWithWindows = global.StartWithWindows;
        result.PauseDuringFullscreenApps =
            global.PauseDuringFullscreenApps;
        result.AttackSystemEnabled = global.AttackSystemEnabled;
        result.AttackIdleMinutes = global.AttackIdleMinutes;
        result.AttackTransitionSeconds =
            global.AttackTransitionSeconds;
        result.MonitorProfiles = [];
        result.Normalize(includeMonitorProfiles: false);
        return result;
    }

    public static void CopyFlow(AppSettings source, AppSettings target)
    {
        target.SpeedMin = source.SpeedMin;
        target.SpeedMax = source.SpeedMax;
        target.Density = source.Density;
        target.FontSize = source.FontSize;
        target.GlyphStretch = source.GlyphStretch;
        target.GlyphWeight = source.GlyphWeight;
        target.SignalHue = source.SignalHue;
        target.SignalBrightness = source.SignalBrightness;
        target.BackgroundHue = source.BackgroundHue;
        target.BackgroundBrightness = source.BackgroundBrightness;
        target.TrailLengthMin = source.TrailLengthMin;
        target.TrailLengthMax = source.TrailLengthMax;
        target.MemoryDurationMin = source.MemoryDurationMin;
        target.MemoryDurationMax = source.MemoryDurationMax;
        target.SignalStrengthMin = source.SignalStrengthMin;
        target.SignalStrengthMax = source.SignalStrengthMax;
        target.SignalGlowKeys = source.SignalGlowKeys;
        target.SignalGlowPriority = source.SignalGlowPriority;
        target.HeadBrightness = source.HeadBrightness;
        target.HeadGlow = source.HeadGlow;
        target.HeadImpulseDecay = source.HeadImpulseDecay;
        target.HeadImpulseProbability = source.HeadImpulseProbability;
        target.HeadWeight = source.HeadWeight;
        target.InterceptionRate = source.InterceptionRate;
        target.StreamLifetimeMin = source.StreamLifetimeMin;
        target.StreamLifetimeMax = source.StreamLifetimeMax;
        target.SpeedCurve =
            source.SpeedCurve.Select(point => point.Copy()).ToList();
        target.TrailLengthCurve =
            source.TrailLengthCurve.Select(point => point.Copy()).ToList();
        target.SignalCurve =
            source.SignalCurve.Select(point => point.Copy()).ToList();
        target.StreamFilterCurve =
            source.StreamFilterCurve.Select(point => point.Copy()).ToList();
        target.MemoryCurve =
            source.MemoryCurve.Select(point => point.Copy()).ToList();
        target.SpeedCurveAdjustment =
            source.SpeedCurveAdjustment.Copy();
        target.TrailLengthCurveAdjustment =
            source.TrailLengthCurveAdjustment.Copy();
        target.SignalCurveAdjustment =
            source.SignalCurveAdjustment.Copy();
        target.StreamFilterCurveAdjustment =
            source.StreamFilterCurveAdjustment.Copy();
        target.MemoryCurveAdjustment =
            source.MemoryCurveAdjustment.Copy();
        target.FontFamily = source.FontFamily;
    }

    public static void CopyDatabase(
        AppSettings source,
        AppSettings target)
    {
        target.ImageMode = source.ImageMode;
        target.ImagePlaylists =
            source.ImagePlaylists.Select(playlist => playlist.Copy()).ToList();
        target.ActiveImagePlaylistId = source.ActiveImagePlaylistId;
        target.ImageDurationSeconds = source.ImageDurationSeconds;
        target.ImageFit = source.ImageFit;
        target.ImageExpressiveness = source.ImageExpressiveness;
        target.ImageGlyphMatch = source.ImageGlyphMatch;
        target.ImageStability = source.ImageStability;
        target.ImageResistance = source.ImageResistance;
        target.ImageBrightness = source.ImageBrightness;
        target.ImagePreparationMode = source.ImagePreparationMode;
        target.ImageLocalContrast = source.ImageLocalContrast;
        target.ImageDetailStrength = source.ImageDetailStrength;
        target.ImageEdgeStrength = source.ImageEdgeStrength;
        target.ImageShadowBalance = source.ImageShadowBalance;
        target.ImagePaletteAdaptation = source.ImagePaletteAdaptation;
        target.ImageToneCalmness = source.ImageToneCalmness;
        target.ImageStructureMode = source.ImageStructureMode;
    }

}
