using WallpaperMatrix.Models;
using WallpaperMatrix.Rendering;

namespace WallpaperMatrix.Services;

/// <summary>
/// Rebinds device-specific monitor profiles from a preset to the displays
/// connected to this computer. Hardware identifiers never have to match.
/// </summary>
internal static class MonitorPresetAdapter
{
    public static AppSettings Adapt(
        AppSettings presetSettings,
        AppSettings currentSettings,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        AppSettings result = presetSettings.Copy();
        if (monitors.Count == 0)
        {
            result.MonitorProfiles = [];
            return result;
        }

        AppSettings current = currentSettings.Copy();
        MonitorTopology.EnsureProfiles(current, monitors);
        List<MonitorProfile> sources = result.MonitorProfiles
            .Where(profile => profile.WasConnected)
            .Select(profile => profile.Copy())
            .ToList();
        if (sources.Count == 0)
        {
            sources = result.MonitorProfiles
                .Select(profile => profile.Copy())
                .ToList();
        }
        if (sources.Count == 0)
        {
            result.MonitorProfiles = [];
            MonitorTopology.EnsureProfiles(result, monitors);
            return result;
        }

        Dictionary<string, string> sourceToTarget =
            BuildMapping(sources, monitors);
        Dictionary<string, MonitorProfile> sourceById = sources
            .GroupBy(
                profile => profile.MonitorId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> targetToSource = sourceToTarget
            .ToDictionary(
                pair => pair.Value,
                pair => pair.Key,
                StringComparer.OrdinalIgnoreCase);
        List<MonitorProfile> adapted = [];

        foreach (MonitorDescriptor target in monitors)
        {
            if (!targetToSource.TryGetValue(
                    target.Id,
                    out string? sourceId)
                || !sourceById.TryGetValue(
                    sourceId,
                    out MonitorProfile? source))
            {
                continue;
            }
            MonitorProfile flowRoot = FindRoot(
                source,
                sourceById,
                MonitorRouteDomain.Flow);
            MonitorProfile databaseRoot = FindRoot(
                source,
                sourceById,
                MonitorRouteDomain.Database);
            AppSettings effective = MonitorSettingsComposer.Compose(
                result,
                source.Settings,
                flowRoot.Settings,
                databaseRoot.Settings,
                source.DatabaseMode == MonitorLinkMode.Disabled);
            (MonitorLinkMode flowMode, string flowSource) =
                TranslateRoute(
                    source,
                    flowRoot,
                    sourceToTarget,
                    MonitorRouteDomain.Flow);
            (MonitorLinkMode databaseMode, string databaseSource) =
                TranslateRoute(
                    source,
                    databaseRoot,
                    sourceToTarget,
                    MonitorRouteDomain.Database);
            adapted.Add(new MonitorProfile
            {
                MonitorId = target.Id,
                LastKnownName = target.FriendlyName,
                LastKnownLeft = target.Bounds.Left,
                LastKnownTop = target.Bounds.Top,
                LastKnownWidth = target.Bounds.Width,
                LastKnownHeight = target.Bounds.Height,
                WasPrimary = target.Primary,
                WasConnected = true,
                FlowMode = flowMode,
                FlowSourceMonitorId = flowSource,
                DatabaseMode = databaseMode,
                DatabaseSourceMonitorId = databaseSource,
                Settings = effective
            });
        }

        MonitorDescriptor primaryTarget =
            monitors.FirstOrDefault(monitor => monitor.Primary)
            ?? monitors[0];
        MonitorProfile? primaryProfile = MonitorTopology.Find(
            adapted,
            primaryTarget.Id);
        if (primaryProfile is null)
        {
            primaryProfile = new MonitorProfile
            {
                MonitorId = primaryTarget.Id,
                Settings = result.Copy(includeMonitorProfiles: false)
            };
            adapted.Add(primaryProfile);
        }
        foreach (MonitorDescriptor target in monitors)
        {
            if (MonitorTopology.Find(adapted, target.Id) is not null)
                continue;
            adapted.Add(new MonitorProfile
            {
                MonitorId = target.Id,
                LastKnownName = target.FriendlyName,
                LastKnownLeft = target.Bounds.Left,
                LastKnownTop = target.Bounds.Top,
                LastKnownWidth = target.Bounds.Width,
                LastKnownHeight = target.Bounds.Height,
                WasPrimary = target.Primary,
                WasConnected = true,
                FlowMode = MonitorLinkMode.Relay,
                FlowSourceMonitorId = primaryTarget.Id,
                DatabaseMode = MonitorLinkMode.Relay,
                DatabaseSourceMonitorId = primaryTarget.Id,
                Settings = primaryProfile.Settings.Copy(
                    includeMonitorProfiles: false)
            });
        }

        result.MonitorProfiles = adapted;
        MonitorTopology.EnsureProfiles(result, monitors);
        return result;
    }

    private static Dictionary<string, string> BuildMapping(
        IReadOnlyList<MonitorProfile> sources,
        IReadOnlyList<MonitorDescriptor> targets)
    {
        Dictionary<string, string> mapping =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedSources =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedTargets =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (MonitorDescriptor target in targets)
        {
            MonitorProfile? exact = sources.FirstOrDefault(source =>
                string.Equals(
                    source.MonitorId,
                    target.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (exact is null || !usedSources.Add(exact.MonitorId))
                continue;
            mapping[exact.MonitorId] = target.Id;
            usedTargets.Add(target.Id);
        }

        MonitorDescriptor targetPrimary =
            targets.FirstOrDefault(target => target.Primary)
            ?? targets[0];
        MonitorProfile sourcePrimary =
            sources.FirstOrDefault(source => source.WasPrimary)
            ?? sources[0];
        if (!usedTargets.Contains(targetPrimary.Id)
            && !usedSources.Contains(sourcePrimary.MonitorId))
        {
            mapping[sourcePrimary.MonitorId] = targetPrimary.Id;
            usedSources.Add(sourcePrimary.MonitorId);
            usedTargets.Add(targetPrimary.Id);
        }

        MonitorProfile geometrySourcePrimary =
            sources.FirstOrDefault(source => source.WasPrimary)
            ?? sources[0];
        foreach (MonitorDescriptor target in targets
                     .Where(candidate => !usedTargets.Contains(candidate.Id)))
        {
            MonitorProfile? best = sources
                .Where(source => !usedSources.Contains(source.MonitorId))
                .OrderBy(source => GeometryDistance(
                    source,
                    geometrySourcePrimary,
                    target,
                    targetPrimary))
                .FirstOrDefault();
            if (best is null)
                break;
            mapping[best.MonitorId] = target.Id;
            usedSources.Add(best.MonitorId);
            usedTargets.Add(target.Id);
        }
        return mapping;
    }

    private static double GeometryDistance(
        MonitorProfile source,
        MonitorProfile sourcePrimary,
        MonitorDescriptor target,
        MonitorDescriptor targetPrimary)
    {
        double sourceBaseWidth = Math.Max(1, sourcePrimary.LastKnownWidth);
        double sourceBaseHeight = Math.Max(1, sourcePrimary.LastKnownHeight);
        double targetBaseWidth = Math.Max(1, targetPrimary.Bounds.Width);
        double targetBaseHeight = Math.Max(1, targetPrimary.Bounds.Height);
        double sourceX =
            (source.LastKnownLeft - sourcePrimary.LastKnownLeft)
            / sourceBaseWidth;
        double sourceY =
            (source.LastKnownTop - sourcePrimary.LastKnownTop)
            / sourceBaseHeight;
        double targetX =
            (target.Bounds.Left - targetPrimary.Bounds.Left)
            / targetBaseWidth;
        double targetY =
            (target.Bounds.Top - targetPrimary.Bounds.Top)
            / targetBaseHeight;
        double sourceAspect = Math.Max(1, source.LastKnownWidth)
            / (double)Math.Max(1, source.LastKnownHeight);
        double targetAspect = target.Bounds.Width
            / (double)Math.Max(1, target.Bounds.Height);
        return Math.Abs(sourceX - targetX) * 3
            + Math.Abs(sourceY - targetY) * 3
            + Math.Abs(Math.Log(sourceAspect / targetAspect));
    }

    private static MonitorProfile FindRoot(
        MonitorProfile source,
        IReadOnlyDictionary<string, MonitorProfile> profiles,
        MonitorRouteDomain domain)
    {
        string rootId = MonitorTopology.ResolveRoot(
            profiles.Values,
            source.MonitorId,
            domain);
        return profiles.TryGetValue(rootId, out MonitorProfile? root)
            ? root
            : source;
    }

    private static (MonitorLinkMode Mode, string SourceId) TranslateRoute(
        MonitorProfile source,
        MonitorProfile root,
        IReadOnlyDictionary<string, string> sourceToTarget,
        MonitorRouteDomain domain)
    {
        MonitorLinkMode mode = domain == MonitorRouteDomain.Flow
            ? source.FlowMode
            : source.DatabaseMode;
        if (mode == MonitorLinkMode.Disabled)
            return (MonitorLinkMode.Disabled, "");
        if (mode == MonitorLinkMode.Isolated)
            return (MonitorLinkMode.Isolated, "");
        if (sourceToTarget.TryGetValue(
                root.MonitorId,
                out string? targetRoot))
        {
            return (mode, targetRoot);
        }
        return (MonitorLinkMode.Isolated, "");
    }
}
