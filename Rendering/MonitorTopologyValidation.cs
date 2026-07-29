using WallpaperMatrix.Models;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Rendering;

internal static class MonitorTopologyValidation
{
    public static void Validate()
    {
        MonitorDescriptor primary = new(
            "DISPLAY-A",
            @"\\.\DISPLAY1",
            "PRIMARY",
            1,
            new DrawingRectangle(0, 0, 1920, 1080),
            true);
        MonitorDescriptor right = new(
            "DISPLAY-B",
            @"\\.\DISPLAY2",
            "RIGHT",
            2,
            new DrawingRectangle(1920, 0, 2560, 1440),
            false);
        MonitorDescriptor upper = new(
            "DISPLAY-C",
            @"\\.\DISPLAY3",
            "UPPER",
            3,
            new DrawingRectangle(0, -1080, 1920, 1080),
            false);
        MonitorDescriptor[] monitors = [primary, right, upper];
        AppSettings settings = new();
        settings.Normalize();
        MonitorTopology.EnsureProfiles(settings, monitors);

        MonitorOutputPlan defaultPlan =
            MonitorOutputPlan.Create(settings, monitors);
        Require(
            defaultPlan.Scenes.Count == 1
            && defaultPlan.Scenes[0].Targets.Count == 3,
            "Исходная ретрансляция должна использовать одну сцену.");

        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow,
            right.Id,
            MonitorLinkMode.Isolated,
            "");
        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow,
            right.Id,
            MonitorLinkMode.Relay,
            primary.Id);
        MonitorRoute directRelay = MonitorTopology.Resolve(
                settings.MonitorProfiles,
                monitors,
                MonitorRouteDomain.Flow)
            .Single(route => route.MonitorId == right.Id);
        Require(
            directRelay.Mode == MonitorLinkMode.Relay
            && directRelay.RootMonitorId == primary.Id,
            "Переключение из изолированного потока в ретрансляцию не закрепилось.");

        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow,
            right.Id,
            MonitorLinkMode.Extend,
            primary.Id);
        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Database,
            right.Id,
            MonitorLinkMode.Extend,
            primary.Id);
        MonitorOutputPlan extended =
            MonitorOutputPlan.Create(settings, monitors);
        Require(
            extended.Scenes.All(scene =>
                scene.CanvasBounds.Width == 4480
                && scene.CanvasBounds.Height == 1440),
            "Расширенная сцена не совпала с объединённой геометрией.");
        MonitorSceneTarget relayTarget = extended.Scenes
            .SelectMany(scene => scene.Targets)
            .Single(target => target.MonitorId == upper.Id);
        Require(
            relayTarget.SourceBounds.Width == primary.Bounds.Width
            && relayTarget.SourceBounds.Height == primary.Bounds.Height,
            "Ретранслятор расширенной группы должен копировать корневой экран.");

        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow,
            upper.Id,
            MonitorLinkMode.Relay,
            right.Id);
        MonitorRoute extensionRelay = MonitorTopology.Resolve(
                settings.MonitorProfiles,
                monitors,
                MonitorRouteDomain.Flow)
            .Single(route => route.MonitorId == upper.Id);
        Require(
            extensionRelay.SourceMonitorId == right.Id
            && extensionRelay.RootMonitorId == primary.Id
            && extensionRelay.ViewMonitorId == right.Id,
            "Ретрансляция части расширенного потока была сведена к корневому экрану.");
        MonitorOutputPlan relayedExtension =
            MonitorOutputPlan.Create(settings, monitors);
        MonitorSceneTarget extensionViewport = relayedExtension.Scenes
            .SelectMany(scene => scene.Targets)
            .Single(target => target.MonitorId == upper.Id);
        Require(
            extensionViewport.SourceBounds.Width == right.Bounds.Width
            && extensionViewport.SourceBounds.Height == right.Bounds.Height,
            "Ретранслятор не получил видимую часть выбранного расширения.");
        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Database,
            upper.Id,
            MonitorLinkMode.Relay,
            right.Id);
        MonitorRoute databaseExtensionRelay = MonitorTopology.Resolve(
                settings.MonitorProfiles,
                monitors,
                MonitorRouteDomain.Database)
            .Single(route => route.MonitorId == upper.Id);
        Require(
            databaseExtensionRelay.SourceMonitorId == right.Id
            && databaseExtensionRelay.RootMonitorId == primary.Id
            && databaseExtensionRelay.ViewMonitorId == right.Id,
            "База данных не сохранила выбранную часть расширенного образа.");

        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow,
            upper.Id,
            MonitorLinkMode.Disabled,
            "");
        MonitorOutputPlan disabled =
            MonitorOutputPlan.Create(settings, monitors);
        Require(
            disabled.ActiveMonitorCount == 2,
            "Отключённый поток остался активным.");

        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow,
            primary.Id,
            MonitorLinkMode.Relay,
            right.Id);
        IReadOnlyList<MonitorRoute> routes = MonitorTopology.Resolve(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Flow);
        MonitorRoute rightRoute =
            routes.Single(route => route.MonitorId == right.Id);
        MonitorRoute primaryRoute =
            routes.Single(route => route.MonitorId == primary.Id);
        Require(
            rightRoute.Mode == MonitorLinkMode.Isolated
            && primaryRoute.RootMonitorId == right.Id,
            "Обратная ссылка не была сведена к новому корню.");

        ValidateIndependentImageProjection(primary, right);
        ValidatePortablePresets(primary, right, upper);
    }

    private static void ValidateIndependentImageProjection(
        MonitorDescriptor primary,
        MonitorDescriptor target)
    {
        MonitorDescriptor portrait = target with
        {
            Bounds = new DrawingRectangle(1920, 0, 1080, 1920)
        };
        MonitorDescriptor[] monitors = [primary, portrait];
        AppSettings settings = new();
        MonitorTopology.EnsureProfiles(settings, monitors);
        MonitorTopology.SetRoute(
            settings.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Database,
            portrait.Id,
            MonitorLinkMode.Isolated,
            "");
        MonitorProfile targetProfile = MonitorTopology.Find(
            settings.MonitorProfiles,
            portrait.Id)!;
        targetProfile.Settings.ImageFit = "Uniform";

        MonitorOutputPlan plan =
            MonitorOutputPlan.Create(settings, monitors);
        MonitorScenePlan scene = plan.Scenes.Single(item =>
            item.Targets.Any(viewport =>
                viewport.MonitorId == portrait.Id));
        Require(
            plan.Scenes.Count(item => item.IsFlowMaster) == 1
            && !scene.IsFlowMaster
            && scene.FlowRootMonitorId
                == plan.Scenes.Single(item => item.IsFlowMaster)
                    .FlowRootMonitorId,
            "Изолированная База данных создала второй генератор "
            + "вместо представления общего потока.");
        MonitorSceneTarget viewport = scene.Targets.Single(item =>
            item.MonitorId == portrait.Id);
        MatrixImageProjection projection = scene.ImageProjection;
        Require(
            projection.CanvasWidth == portrait.Bounds.Width
            && projection.CanvasHeight == portrait.Bounds.Height,
            "Изолированная база использует пропорции другого экрана.");
        Require(
            Math.Abs(
                viewport.SourceBounds.Width
                    / (double)viewport.SourceBounds.Height
                - portrait.Bounds.Width
                    / (double)portrait.Bounds.Height) < 0.002,
            "Область потока не приведена к пропорциям целевого экрана.");
        Require(
            projection.DestinationBounds == viewport.SourceBounds,
            "Образ не проецируется точно в видимую область целевого экрана.");
    }

    private static void ValidatePortablePresets(
        MonitorDescriptor primary,
        MonitorDescriptor right,
        MonitorDescriptor upper)
    {
        MonitorDescriptor[] sourceMonitors = [primary, right, upper];
        AppSettings fourScreenPreset = new();
        MonitorTopology.EnsureProfiles(
            fourScreenPreset,
            sourceMonitors);
        MonitorProfile rightProfile = MonitorTopology.Find(
            fourScreenPreset.MonitorProfiles,
            right.Id)!;
        rightProfile.Settings.FontSize = 41;
        MonitorTopology.SetRoute(
            fourScreenPreset.MonitorProfiles,
            sourceMonitors,
            MonitorRouteDomain.Flow,
            right.Id,
            MonitorLinkMode.Isolated,
            "");
        MonitorTopology.SetRoute(
            fourScreenPreset.MonitorProfiles,
            sourceMonitors,
            MonitorRouteDomain.Flow,
            primary.Id,
            MonitorLinkMode.Relay,
            right.Id);
        MonitorDescriptor recipient = new(
            "RECIPIENT",
            @"\\.\DISPLAY1",
            "RECIPIENT",
            1,
            new DrawingRectangle(0, 0, 1366, 768),
            true);
        AppSettings adaptedToOne = MonitorPresetAdapter.Adapt(
            fourScreenPreset,
            new AppSettings(),
            [recipient]);
        MonitorProfile only = adaptedToOne.MonitorProfiles.Single(
            profile => profile.WasConnected);
        Require(
            only.FlowMode == MonitorLinkMode.Isolated
            && Math.Abs(only.Settings.FontSize - 41) < 0.01,
            "Пресет с нескольких экранов не свёлся к итоговому корню.");

        AppSettings oneScreenPreset = new();
        MonitorTopology.EnsureProfiles(
            oneScreenPreset,
            [recipient]);
        oneScreenPreset.MonitorProfiles[0].Settings.FontSize = 37;
        AppSettings adaptedToMany = MonitorPresetAdapter.Adapt(
            oneScreenPreset,
            new AppSettings(),
            sourceMonitors);
        MonitorProfile adaptedPrimary = adaptedToMany.MonitorProfiles
            .Single(profile => profile.WasPrimary);
        Require(
            Math.Abs(adaptedPrimary.Settings.FontSize - 37) < 0.01
            && adaptedToMany.MonitorProfiles
                .Where(profile => !profile.WasPrimary)
                .All(profile =>
                    profile.FlowMode == MonitorLinkMode.Relay
                    && profile.FlowSourceMonitorId
                        == adaptedPrimary.MonitorId),
            "Одноэкранный пресет не развернулся ретрансляцией.");

        MonitorProfile primaryProfile = MonitorTopology.Find(
            fourScreenPreset.MonitorProfiles,
            primary.Id)!;
        primaryProfile.Settings.FontSize = 33;
        rightProfile.Settings.FontSize = 47;
        MonitorTopology.SetRoute(
            fourScreenPreset.MonitorProfiles,
            sourceMonitors,
            MonitorRouteDomain.Flow,
            right.Id,
            MonitorLinkMode.Relay,
            primary.Id);
        AppSettings sameHardware = MonitorPresetAdapter.Adapt(
            fourScreenPreset,
            fourScreenPreset,
            sourceMonitors);
        MonitorProfile sameHardwareRight = MonitorTopology.Find(
            sameHardware.MonitorProfiles,
            right.Id)!;
        Require(
            sameHardwareRight.FlowMode == MonitorLinkMode.Relay
            && Math.Abs(sameHardwareRight.Settings.FontSize - 47) < 0.01
            && AppSettingsComparer.PresetEquivalent(
                fourScreenPreset,
                sameHardware),
            "Пресет текущей топологии повторно скомпонован и ложно изменён.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
