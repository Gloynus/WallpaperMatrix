namespace WallpaperMatrix.Models;

public enum MonitorRouteDomain
{
    Flow,
    Database
}

public sealed record MonitorDescriptor(
    string Id,
    string SystemName,
    string FriendlyName,
    System.Drawing.Rectangle Bounds,
    bool Primary)
{
    public string Label =>
        $"{FriendlyName} // {Bounds.Width}×{Bounds.Height}"
        + (Primary ? " // ОСНОВНОЙ" : "");
}

public sealed record MonitorRoute(
    string MonitorId,
    MonitorLinkMode Mode,
    string RootMonitorId);

/// <summary>
/// Turns operator-friendly links into a flat, cycle-free routing graph.
/// Every dependent monitor points directly to an isolated root.
/// </summary>
public static class MonitorTopology
{
    public static void EnsureProfiles(
        AppSettings settings,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.MonitorProfiles ??= [];

        Dictionary<string, MonitorProfile> existing = settings.MonitorProfiles
            .Where(profile => profile is not null)
            .Select(profile =>
            {
                profile.Normalize();
                return profile;
            })
            .Where(profile => !string.IsNullOrWhiteSpace(profile.MonitorId))
            .GroupBy(profile => profile.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        string primaryId = monitors.FirstOrDefault(monitor => monitor.Primary)?.Id
            ?? monitors.FirstOrDefault()?.Id
            ?? "";
        foreach (MonitorProfile profile in existing.Values)
            profile.WasConnected = false;
        List<MonitorProfile> active = [];
        foreach (MonitorDescriptor monitor in monitors)
        {
            if (!existing.TryGetValue(monitor.Id, out MonitorProfile? profile))
            {
                AppSettings visual = settings.Copy(includeMonitorProfiles: false);
                visual.MonitorProfiles = [];
                profile = new MonitorProfile
                {
                    MonitorId = monitor.Id,
                    LastKnownName = monitor.FriendlyName,
                    LastKnownLeft = monitor.Bounds.Left,
                    LastKnownTop = monitor.Bounds.Top,
                    LastKnownWidth = monitor.Bounds.Width,
                    LastKnownHeight = monitor.Bounds.Height,
                    WasPrimary = monitor.Primary,
                    WasConnected = true,
                    FlowMode = string.Equals(
                        monitor.Id,
                        primaryId,
                        StringComparison.OrdinalIgnoreCase)
                        ? MonitorLinkMode.Isolated
                        : MonitorLinkMode.Relay,
                    FlowSourceMonitorId = string.Equals(
                        monitor.Id,
                        primaryId,
                        StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : primaryId,
                    DatabaseMode = string.Equals(
                        monitor.Id,
                        primaryId,
                        StringComparison.OrdinalIgnoreCase)
                        ? MonitorLinkMode.Isolated
                        : MonitorLinkMode.Relay,
                    DatabaseSourceMonitorId = string.Equals(
                        monitor.Id,
                        primaryId,
                        StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : primaryId,
                    Settings = visual
                };
            }
            profile.LastKnownName = monitor.FriendlyName;
            profile.LastKnownLeft = monitor.Bounds.Left;
            profile.LastKnownTop = monitor.Bounds.Top;
            profile.LastKnownWidth = monitor.Bounds.Width;
            profile.LastKnownHeight = monitor.Bounds.Height;
            profile.WasPrimary = monitor.Primary;
            profile.WasConnected = true;
            profile.Normalize();
            active.Add(profile);
        }

        // Keep disconnected profiles so reconnecting a dock or monitor restores
        // its operator state, but active profiles always come first.
        active.AddRange(existing.Values.Where(profile =>
            !monitors.Any(monitor => string.Equals(
                monitor.Id,
                profile.MonitorId,
                StringComparison.OrdinalIgnoreCase))));
        settings.MonitorProfiles = active
            .GroupBy(profile => profile.MonitorId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(32)
            .ToList();

        Normalize(settings.MonitorProfiles, monitors, MonitorRouteDomain.Flow);
        Normalize(settings.MonitorProfiles, monitors, MonitorRouteDomain.Database);
    }

    public static void SetRoute(
        IList<MonitorProfile> profiles,
        IReadOnlyList<MonitorDescriptor> monitors,
        MonitorRouteDomain domain,
        string monitorId,
        MonitorLinkMode mode,
        string sourceMonitorId)
    {
        MonitorProfile? selected = Find(profiles, monitorId);
        if (selected is null)
            return;

        if (mode is MonitorLinkMode.Isolated or MonitorLinkMode.Disabled)
        {
            SetMode(selected, domain, mode);
            SetSource(selected, domain, "");
            Normalize(profiles, monitors, domain);
            return;
        }

        MonitorProfile? requestedSource = Find(profiles, sourceMonitorId);
        if (requestedSource is null
            || string.Equals(
                requestedSource.MonitorId,
                selected.MonitorId,
                StringComparison.OrdinalIgnoreCase))
        {
            SetMode(selected, domain, MonitorLinkMode.Isolated);
            SetSource(selected, domain, "");
            Normalize(profiles, monitors, domain);
            return;
        }

        string requestedRoot = ResolveRoot(
            profiles,
            requestedSource.MonitorId,
            domain);
        if (string.Equals(
            requestedRoot,
            selected.MonitorId,
            StringComparison.OrdinalIgnoreCase))
        {
            // The operator deliberately reversed an existing dependency. Make
            // the chosen source the new root and move the whole former group
            // to it instead of rejecting the action with a graph error.
            string newRoot = requestedSource.MonitorId;
            string formerRoot = selected.MonitorId;
            foreach (MonitorProfile profile in profiles)
            {
                string root = ResolveRoot(profiles, profile.MonitorId, domain);
                if (!string.Equals(
                    root,
                    formerRoot,
                    StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        profile.MonitorId,
                        newRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                SetSource(profile, domain, newRoot);
            }
            SetMode(requestedSource, domain, MonitorLinkMode.Isolated);
            SetSource(requestedSource, domain, "");
            requestedRoot = newRoot;
        }

        SetMode(selected, domain, mode);
        SetSource(selected, domain, requestedRoot);
        Normalize(profiles, monitors, domain);
    }

    public static IReadOnlyList<MonitorRoute> Resolve(
        IList<MonitorProfile> profiles,
        IReadOnlyList<MonitorDescriptor> monitors,
        MonitorRouteDomain domain)
    {
        Normalize(profiles, monitors, domain);
        return monitors
            .Select(monitor =>
            {
                MonitorProfile profile = Find(profiles, monitor.Id)
                    ?? throw new InvalidOperationException(
                        $"Для экрана {monitor.Id} не создан профиль.");
                MonitorLinkMode mode = GetMode(profile, domain);
                string root = mode == MonitorLinkMode.Disabled
                    ? ""
                    : ResolveRoot(profiles, monitor.Id, domain);
                return new MonitorRoute(monitor.Id, mode, root);
            })
            .ToArray();
    }

    public static string ResolveRoot(
        IEnumerable<MonitorProfile> profiles,
        string monitorId,
        MonitorRouteDomain domain)
    {
        Dictionary<string, MonitorProfile> byId = profiles
            .Where(profile => profile is not null)
            .GroupBy(profile => profile.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string current = monitorId;
        while (byId.TryGetValue(current, out MonitorProfile? profile))
        {
            if (!visited.Add(current))
                return current;
            MonitorLinkMode mode = GetMode(profile, domain);
            if (mode is MonitorLinkMode.Isolated or MonitorLinkMode.Disabled)
                return current;
            string source = GetSource(profile, domain);
            if (string.IsNullOrWhiteSpace(source)
                || !byId.ContainsKey(source))
            {
                return current;
            }
            current = source;
        }
        return monitorId;
    }

    public static MonitorProfile? Find(
        IEnumerable<MonitorProfile> profiles,
        string monitorId) =>
        profiles.FirstOrDefault(profile => string.Equals(
            profile.MonitorId,
            monitorId,
            StringComparison.OrdinalIgnoreCase));

    private static void Normalize(
        IList<MonitorProfile> profiles,
        IReadOnlyList<MonitorDescriptor> monitors,
        MonitorRouteDomain domain)
    {
        HashSet<string> activeIds = monitors
            .Select(monitor => monitor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> order = monitors
            .Select((monitor, index) => (monitor.Id, index))
            .ToDictionary(
                pair => pair.Id,
                pair => pair.index,
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MonitorProfile> byId = profiles
            .Where(profile => activeIds.Contains(profile.MonitorId))
            .GroupBy(profile => profile.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (MonitorProfile profile in byId.Values)
        {
            MonitorLinkMode mode = GetMode(profile, domain);
            string source = GetSource(profile, domain);
            if (mode is MonitorLinkMode.Isolated or MonitorLinkMode.Disabled)
            {
                SetSource(profile, domain, "");
                continue;
            }
            if (string.IsNullOrWhiteSpace(source)
                || !byId.ContainsKey(source)
                || string.Equals(
                    source,
                    profile.MonitorId,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetMode(profile, domain, MonitorLinkMode.Isolated);
                SetSource(profile, domain, "");
            }
        }

        // Break cycles deterministically at the earliest physical monitor.
        foreach (MonitorProfile start in byId.Values
                     .OrderBy(profile => order.GetValueOrDefault(
                         profile.MonitorId,
                         int.MaxValue)))
        {
            List<MonitorProfile> path = [];
            Dictionary<string, int> positions =
                new(StringComparer.OrdinalIgnoreCase);
            MonitorProfile current = start;
            while (GetMode(current, domain)
                   is MonitorLinkMode.Relay or MonitorLinkMode.Extend)
            {
                if (positions.TryGetValue(current.MonitorId, out int cycleAt))
                {
                    MonitorProfile root = path
                        .Skip(cycleAt)
                        .OrderBy(profile => order.GetValueOrDefault(
                            profile.MonitorId,
                            int.MaxValue))
                        .First();
                    SetMode(root, domain, MonitorLinkMode.Isolated);
                    SetSource(root, domain, "");
                    break;
                }
                positions[current.MonitorId] = path.Count;
                path.Add(current);
                string source = GetSource(current, domain);
                if (!byId.TryGetValue(source, out MonitorProfile? next))
                    break;
                current = next;
            }
        }

        // Collapse every legal chain to one direct root reference.
        foreach (MonitorProfile profile in byId.Values)
        {
            if (GetMode(profile, domain)
                is not (MonitorLinkMode.Relay or MonitorLinkMode.Extend))
            {
                continue;
            }
            string root = ResolveRoot(byId.Values, profile.MonitorId, domain);
            MonitorProfile? rootProfile = Find(byId.Values, root);
            if (rootProfile is null
                || GetMode(rootProfile, domain) == MonitorLinkMode.Disabled)
            {
                SetMode(profile, domain, MonitorLinkMode.Isolated);
                SetSource(profile, domain, "");
            }
            else
            {
                SetSource(profile, domain, root);
            }
        }
    }

    private static MonitorLinkMode GetMode(
        MonitorProfile profile,
        MonitorRouteDomain domain) =>
        domain == MonitorRouteDomain.Flow
            ? profile.FlowMode
            : profile.DatabaseMode;

    private static void SetMode(
        MonitorProfile profile,
        MonitorRouteDomain domain,
        MonitorLinkMode mode)
    {
        if (domain == MonitorRouteDomain.Flow)
            profile.FlowMode = mode;
        else
            profile.DatabaseMode = mode;
    }

    private static string GetSource(
        MonitorProfile profile,
        MonitorRouteDomain domain) =>
        domain == MonitorRouteDomain.Flow
            ? profile.FlowSourceMonitorId
            : profile.DatabaseSourceMonitorId;

    private static void SetSource(
        MonitorProfile profile,
        MonitorRouteDomain domain,
        string source)
    {
        if (domain == MonitorRouteDomain.Flow)
            profile.FlowSourceMonitorId = source;
        else
            profile.DatabaseSourceMonitorId = source;
    }
}
