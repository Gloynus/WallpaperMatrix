using System.Diagnostics;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;
using WallpaperMatrix.Rendering;

namespace WallpaperMatrix.Services;

internal sealed record WallpaperOutputHealth(
    bool IsHealthy,
    string Reason)
{
    public static WallpaperOutputHealth Healthy { get; } =
        new(true, "");
}

/// <summary>
/// Owns the complete native wallpaper surface for all monitors.
/// Image sequencing, timers and fullscreen policy intentionally live outside
/// this class; it has one responsibility: output lifecycle.
/// </summary>
internal sealed class WallpaperOutputSession : IDisposable
{
    private readonly List<NativeWallpaperWindow> _windows = [];
    private readonly Action<string, Exception, bool> _failureHandler;
    private readonly object _virtualOutputLock = new();
    private AppSettings _settings = new();
    private AppSettings _virtualOutputSettings = new();
    private PreparedImage? _image;
    private IReadOnlyDictionary<string, PreparedImage?> _databaseImages =
        new Dictionary<string, PreparedImage?>();
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private MonitorOutputPlan? _plan;
    private bool _suspended;
    private bool _disposed;
    private int _screenCount;
    private readonly HashSet<string> _virtualOutputRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VirtualOutputWindow> _virtualOutputs =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _windows.Count > 0;
    public bool IsVirtualOutputOpen
    {
        get
        {
            lock (_virtualOutputLock)
                return _virtualOutputs.Count > 0;
        }
    }
    public IReadOnlyList<string> VirtualOutputMonitorIds
    {
        get
        {
            lock (_virtualOutputLock)
                return _virtualOutputs.Keys.ToArray();
        }
    }
    public AttackInterfaceFrame? CaptureAttackInterface() =>
        _windows.Count > 0
            ? _windows[0].CaptureAttackInterface()
            : null;
    public AttackFrameSnapshot? BeginAttackComposition(
        AttackInterfaceFrame? interfaceFrame,
        double transitionSeconds) =>
        _windows.Count > 0
            ? _windows[0].BeginAttackComposition(
                interfaceFrame,
                transitionSeconds)
            : null;

    public void EndAttackComposition()
    {
        foreach (NativeWallpaperWindow window in _windows)
            window.EndAttackComposition();
    }
    public int WindowCount => _screenCount;
    public int TargetWidth { get; private set; } = 2560;
    public int TargetHeight { get; private set; } = 1440;
    public event Action<string, bool>? VirtualOutputStateChanged;

    public WallpaperOutputHealth CheckHealth(
        TimeSpan maximumFrameAge)
    {
        if (_disposed)
            return new WallpaperOutputHealth(false, "сеанс вывода закрыт");
        if (_windows.Count == 0)
            return new WallpaperOutputHealth(false, "окна вывода отсутствуют");

        foreach (NativeWallpaperWindow window in _windows)
        {
            if (!window.TryGetHealth(maximumFrameAge, out string reason))
                return new WallpaperOutputHealth(false, reason);
        }
        return WallpaperOutputHealth.Healthy;
    }

    public (int Width, int Height) DatabaseTargetSize(
        string rootMonitorId)
    {
        if (_plan is null || string.IsNullOrWhiteSpace(rootMonitorId))
            return (TargetWidth, TargetHeight);

        MatrixImageProjection? projection = _plan.Scenes
            .Where(scene => string.Equals(
                scene.DatabaseRootMonitorId,
                rootMonitorId,
                StringComparison.OrdinalIgnoreCase))
            .Select(scene => scene.ImageProjection)
            .OrderByDescending(item =>
                (long)item.CanvasWidth * item.CanvasHeight)
            .FirstOrDefault();
        return projection is null
            ? (TargetWidth, TargetHeight)
            : (
                Math.Max(1, projection.CanvasWidth),
                Math.Max(1, projection.CanvasHeight));
    }

    public WallpaperOutputSession(Action<string, Exception, bool> failureHandler)
    {
        _failureHandler = failureHandler;
    }

    public void Start(
        AppSettings settings,
        PreparedImage? image,
        IReadOnlyDictionary<string, PreparedImage?>? initialDatabaseImages = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        _settings = settings.Copy();
        _virtualOutputSettings = settings.Copy();
        _image = image;
        if (initialDatabaseImages is not null)
        {
            _databaseImages =
                new Dictionary<string, PreparedImage?>(
                    initialDatabaseImages,
                    StringComparer.OrdinalIgnoreCase);
        }
        CreateWindows(animateStartupReveal: true);
        SetImage(_image);
        Activate();
    }

    public void Restart(
        AppSettings settings,
        PreparedImage? image,
        bool suspended)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseWindows(restoreSystemWallpaper: false);
        _settings = settings.Copy();
        _virtualOutputSettings = settings.Copy();
        _image = image;
        CreateWindows(animateStartupReveal: false);
        SetImage(_image);
        if (suspended)
            Suspend();
        else
            Activate();
    }

    public void UpdateSettings(AppSettings settings)
    {
        bool virtualConfigurationChanged =
            !VirtualOutputConfigurationEquivalent(
                _virtualOutputSettings,
                settings);
        _settings = settings.Copy();
        _virtualOutputSettings = settings.Copy();
        IReadOnlyList<MonitorDescriptor> nextMonitors =
            OutputDeviceCatalog.Capture(_settings);
        MonitorTopology.EnsureProfiles(_settings, nextMonitors);
        MonitorOutputPlan nextPlan =
            MonitorOutputPlan.Create(_settings, nextMonitors);
        if (_plan is null
            || _plan.VirtualBounds != nextPlan.VirtualBounds)
        {
            Restart(_settings, _image, _suspended);
            return;
        }
        _monitors = nextMonitors;
        _plan = nextPlan;
        TargetWidth = Math.Max(1, nextPlan.VirtualBounds.Width);
        TargetHeight = Math.Max(1, nextPlan.VirtualBounds.Height);
        foreach (NativeWallpaperWindow window in _windows)
            window.UpdateSettings(nextPlan);
        string[] unavailable;
        lock (_virtualOutputLock)
        {
            unavailable = _virtualOutputRequests
                .Where(request => !_monitors.Any(monitor =>
                    string.Equals(
                        monitor.Id,
                        request,
                        StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        foreach (string monitorId in unavailable)
            CloseVirtualOutput(monitorId, preserveRequest: false);
        bool virtualRequested;
        lock (_virtualOutputLock)
        {
            virtualRequested = _virtualOutputRequests.Contains(
                OutputDeviceCatalog.VirtualMonitorId);
        }
        if (virtualConfigurationChanged && virtualRequested)
        {
            RecreateVirtualOutput(
                OutputDeviceCatalog.VirtualMonitorId);
        }
    }

    public void SetVirtualOutput(
        bool open,
        AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool configurationChanged =
            !VirtualOutputConfigurationEquivalent(
                _virtualOutputSettings,
                settings);
        _virtualOutputSettings = settings.Copy();
        _virtualOutputSettings.Normalize();
        string monitorId = ResolveVirtualOutputSource(
            _virtualOutputSettings.VirtualOutputSourceMonitorId);
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            throw new InvalidOperationException(
                "Для отдельного окна нет активного устройства вывода.");
        }
        if (!open)
        {
            CloseVirtualOutput(
                monitorId,
                preserveRequest: false);
            return;
        }
        lock (_virtualOutputLock)
            _virtualOutputRequests.Add(monitorId);
        if (IsRunning)
        {
            bool alreadyOpen;
            lock (_virtualOutputLock)
                alreadyOpen = _virtualOutputs.ContainsKey(monitorId);
            if (!alreadyOpen)
                StartVirtualOutput(monitorId);
            else if (configurationChanged
                     && OutputDeviceCatalog.IsVirtual(monitorId))
                RecreateVirtualOutput(monitorId);
        }
    }

    public void SetImage(PreparedImage? image)
    {
        _image = image;
        foreach (NativeWallpaperWindow window in _windows)
            window.SetImage(image);
    }

    public void SetDatabaseImages(
        IReadOnlyDictionary<string, PreparedImage?> images)
    {
        _databaseImages = new Dictionary<string, PreparedImage?>(
            images,
            StringComparer.OrdinalIgnoreCase);
        foreach (NativeWallpaperWindow window in _windows)
            window.SetDatabaseImages(_databaseImages);
    }

    public void ResetImageOverlay(PreparedImage? image)
    {
        _image = image;
        foreach (NativeWallpaperWindow window in _windows)
            window.ResetImageOverlay(image);
    }

    public void Activate()
    {
        _suspended = false;
        foreach (NativeWallpaperWindow window in _windows)
            window.SetPaused(false);
        DesktopHost.ShowWallpaperSurface();
        if (!WaitUntilVisible(TimeSpan.FromMilliseconds(1500)))
        {
            foreach (NativeWallpaperWindow window in _windows)
                window.LogAttachmentState("слой Explorer не показан");

            throw new InvalidOperationException(
                "Explorer не сделал окна вывода рабочего стола видимыми.");
        }

        foreach (NativeWallpaperWindow window in _windows)
            window.LogAttachmentState("вывод подтверждён");
    }

    private bool WaitUntilVisible(TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            if (_windows.Count > 0
                && _windows.All(window => window.IsAttachmentVisible()))
            {
                return true;
            }
            Thread.Sleep(25);
        }
        while (clock.Elapsed < timeout);

        return false;
    }

    public void Suspend()
    {
        _suspended = true;
        foreach (NativeWallpaperWindow window in _windows)
            window.SetPaused(true);
        DesktopHost.HideWallpaperSurface();
        // A fullscreen transition is driven by the foreground application.
        // Forcing Explorer to synchronously redraw every WorkerW/Progman child
        // from this UI-thread path can deadlock with shell activation (Photos
        // is a reproducible example). Hiding our window and its host is enough:
        // DWM reveals Explorer's already composed wallpaper underneath.
    }

    public void FreezeMotion(bool frozen)
    {
        foreach (NativeWallpaperWindow window in _windows)
            window.SetSimulationPaused(frozen);
    }

    public void SetMotionScale(double scale)
    {
        foreach (NativeWallpaperWindow window in _windows)
            window.SetMotionScale(scale);
    }

    public void StopAndRestoreDesktop() =>
        CloseWindows(restoreSystemWallpaper: true);

    private void CreateWindows(bool animateStartupReveal)
    {
        _monitors = OutputDeviceCatalog.Capture(_settings);
        if (_monitors.Count == 0)
            throw new InvalidOperationException(
                "Windows не сообщила ни об одном активном экране.");
        MonitorTopology.EnsureProfiles(_settings, _monitors);
        _plan = MonitorOutputPlan.Create(_settings, _monitors);

        DiagnosticLog.Write(
            "Обнаружены экраны: "
            + string.Join(
                "; ",
                _monitors.Select(monitor =>
                    $"экран {monitor.DisplayNumber}: "
                    + $"{monitor.SystemName} «{monitor.FriendlyName}» "
                    + $"{monitor.Bounds.Width}x{monitor.Bounds.Height} "
                    + $"@ ({monitor.Bounds.Left},{monitor.Bounds.Top}) "
                    + $"primary={monitor.Primary}")));
        IReadOnlyDictionary<string, MonitorRoute> flowRoutes =
            MonitorTopology.Resolve(
                    _settings.MonitorProfiles,
                    _monitors,
                    MonitorRouteDomain.Flow)
                .ToDictionary(
                    route => route.MonitorId,
                    StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, MonitorRoute> databaseRoutes =
            MonitorTopology.Resolve(
                    _settings.MonitorProfiles,
                    _monitors,
                    MonitorRouteDomain.Database)
                .ToDictionary(
                    route => route.MonitorId,
                    StringComparer.OrdinalIgnoreCase);
        DiagnosticLog.Write(
            "Маршрутизация устройств: "
            + string.Join(
                "; ",
                _monitors.Select(monitor =>
                {
                    MonitorRoute flow = flowRoutes[monitor.Id];
                    MonitorRoute database = databaseRoutes[monitor.Id];
                    return $"{monitor.FriendlyName} "
                        + $"[{monitor.DisplayNumber}]: "
                        + $"поток={flow.Mode}"
                        + $"(source={flow.SourceMonitorId}, "
                        + $"view={flow.ViewMonitorId}, "
                        + $"root={flow.RootMonitorId}); "
                        + $"база={database.Mode}"
                        + $"(source={database.SourceMonitorId}, "
                        + $"view={database.ViewMonitorId}, "
                        + $"root={database.RootMonitorId})";
                }))
            + $"; сцен={_plan.Scenes.Count}; "
            + $"генераторов потока={_plan.Scenes.Count(scene => scene.IsFlowMaster)}; "
            + $"активных устройств={_plan.ActiveMonitorCount}.");

        List<NativeWallpaperWindow> created = [];
        try
        {
            NativeWallpaperWindow compositor = new(
                _plan,
                _settings,
                animateStartupReveal,
                _databaseImages,
                failureHandler: _failureHandler);
            created.Add(compositor);
            compositor.Start();
            TargetWidth = Math.Max(1, _plan.VirtualBounds.Width);
            TargetHeight = Math.Max(1, _plan.VirtualBounds.Height);

            _windows.AddRange(created);
            _screenCount = _monitors.Count(monitor => !monitor.IsVirtual);
            string[] requestedOutputs;
            lock (_virtualOutputLock)
                requestedOutputs = _virtualOutputRequests.ToArray();
            foreach (string monitorId in requestedOutputs)
            {
                try
                {
                    StartVirtualOutput(monitorId);
                }
                catch (Exception exception)
                {
                    // A capture window is auxiliary. If it cannot be restored
                    // after a desktop/output rebuild, keep the wallpaper alive
                    // and only drop the capture request.
                    lock (_virtualOutputLock)
                        _virtualOutputRequests.Remove(monitorId);
                    DiagnosticLog.Write(
                        $"Отдельное окно «{monitorId}» не восстановлено после "
                        + "переподключения; основной вывод продолжает работу.",
                        exception);
                }
            }
        }
        catch
        {
            _windows.Clear();
            _screenCount = 0;
            _plan = null;
            CloseWindowList(created, restoreSystemWallpaper: true);
            throw;
        }
    }

    private void CloseWindows(bool restoreSystemWallpaper)
    {
        CloseAllVirtualOutputs(preserveRequests: true);
        if (_windows.Count == 0)
            return;

        NativeWallpaperWindow[] closingWindows = _windows.ToArray();
        _windows.Clear();
        _screenCount = 0;
        _plan = null;
        CloseWindowList(closingWindows, restoreSystemWallpaper);
    }

    private static void CloseWindowList(
        IReadOnlyList<NativeWallpaperWindow> windows,
        bool restoreSystemWallpaper)
    {
        // On Stop the parent must disappear before its children, otherwise DWM
        // can retain their last composed frame. During a live display rebuild
        // the WorkerW stays visible: an empty hidden WorkerW may be reclaimed by
        // Explorer before the replacement renderer is attached to it.
        if (restoreSystemWallpaper)
            DesktopHost.HideWallpaperSurface();
        for (int index = windows.Count - 1; index >= 0; index--)
            windows[index].RequestClose();

        if (restoreSystemWallpaper)
        {
            // The visible surface is already gone. Explorer restoration and
            // D3D teardown must not hold the WPF dispatcher: Stop should feel
            // instantaneous even when a video driver takes time to release a
            // swap chain.
            DesktopHost.RefreshDesktopSurface(
                restoreSystemWallpaper: true);
            CompleteCloseInBackground(windows);
            return;
        }

        TimeSpan closeBudget = TimeSpan.FromSeconds(3);
        Stopwatch closeClock = Stopwatch.StartNew();
        for (int index = windows.Count - 1; index >= 0; index--)
        {
            TimeSpan remaining = closeBudget - closeClock.Elapsed;
            windows[index].WaitForClose(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        DesktopHost.RefreshDesktopSurface(restoreSystemWallpaper);
    }

    private static void CompleteCloseInBackground(
        IReadOnlyList<NativeWallpaperWindow> windows)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Stopwatch closeClock = Stopwatch.StartNew();
                TimeSpan closeBudget = TimeSpan.FromSeconds(8);
                for (int index = windows.Count - 1; index >= 0; index--)
                {
                    TimeSpan remaining =
                        closeBudget - closeClock.Elapsed;
                    windows[index].WaitForClose(
                        remaining > TimeSpan.Zero
                            ? remaining
                            : TimeSpan.Zero);
                }
                DiagnosticLog.Write(
                    $"Фоновое освобождение вывода завершено за "
                    + $"{closeClock.Elapsed.TotalMilliseconds:0} мс.");
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(
                    "Фоновое освобождение вывода завершилось ошибкой.",
                    exception);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseAllVirtualOutputs(preserveRequests: false);
        CloseWindows(restoreSystemWallpaper: true);
    }

    private void RecreateVirtualOutput(string monitorId)
    {
        CloseVirtualOutput(monitorId, preserveRequest: true);
        bool requested;
        lock (_virtualOutputLock)
            requested = _virtualOutputRequests.Contains(monitorId);
        if (requested && IsRunning)
            StartVirtualOutput(monitorId);
    }

    private void StartVirtualOutput(string sourceMonitorId)
    {
        if (!IsRunning)
            return;
        bool requested;
        lock (_virtualOutputLock)
            requested = _virtualOutputRequests.Contains(sourceMonitorId);
        if (!requested)
            return;
        MonitorDescriptor? source = _monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.Id,
                sourceMonitorId,
                StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            throw new InvalidOperationException(
                "Для отдельного окна нет активного источника потока.");
        }

        int outputWidth = source.IsVirtual
            ? _virtualOutputSettings.VirtualOutputWidth
            : source.Bounds.Width;
        int outputHeight = source.IsVirtual
            ? _virtualOutputSettings.VirtualOutputHeight
            : source.Bounds.Height;
        VirtualOutputWindow window = new(
            outputWidth,
            outputHeight,
            $"Wallpaper Matrix — {source.FriendlyName.ToUpperInvariant()}",
            () => CaptureVirtualOutputFrame(sourceMonitorId),
            _failureHandler);
        window.Closed += userInitiated =>
            OnVirtualOutputClosed(
                sourceMonitorId,
                window,
                userInitiated);
        lock (_virtualOutputLock)
        {
            if (_virtualOutputs.ContainsKey(sourceMonitorId))
            {
                window.Dispose();
                return;
            }
            _virtualOutputs[sourceMonitorId] = window;
        }
        try
        {
            window.Start();
            VirtualOutputStateChanged?.Invoke(
                sourceMonitorId,
                true);
        }
        catch
        {
            lock (_virtualOutputLock)
            {
                if (_virtualOutputs.TryGetValue(
                        sourceMonitorId,
                        out VirtualOutputWindow? current)
                    && ReferenceEquals(current, window))
                {
                    _virtualOutputs.Remove(sourceMonitorId);
                }
                _virtualOutputRequests.Remove(sourceMonitorId);
            }
            window.Dispose();
            VirtualOutputStateChanged?.Invoke(
                sourceMonitorId,
                false);
            throw;
        }
    }

    private void CloseVirtualOutput(
        string monitorId,
        bool preserveRequest)
    {
        VirtualOutputWindow? window;
        lock (_virtualOutputLock)
        {
            if (!preserveRequest)
                _virtualOutputRequests.Remove(monitorId);
            _virtualOutputs.Remove(monitorId, out window);
        }
        if (window is null)
            return;
        window.RequestClose();
        window.WaitForClose(TimeSpan.FromSeconds(3));
        window.Dispose();
        VirtualOutputStateChanged?.Invoke(monitorId, false);
    }

    private void CloseAllVirtualOutputs(bool preserveRequests)
    {
        string[] monitorIds;
        lock (_virtualOutputLock)
        {
            monitorIds = _virtualOutputs.Keys.ToArray();
            if (!preserveRequests)
                _virtualOutputRequests.Clear();
        }
        foreach (string monitorId in monitorIds)
        {
            CloseVirtualOutput(
                monitorId,
                preserveRequest: preserveRequests);
        }
    }

    private void OnVirtualOutputClosed(
        string monitorId,
        VirtualOutputWindow window,
        bool userInitiated)
    {
        bool wasCurrent;
        lock (_virtualOutputLock)
        {
            wasCurrent = _virtualOutputs.TryGetValue(
                monitorId,
                out VirtualOutputWindow? current)
                && ReferenceEquals(current, window);
            if (wasCurrent)
                _virtualOutputs.Remove(monitorId);
            if (userInitiated)
                _virtualOutputRequests.Remove(monitorId);
        }
        if (!wasCurrent)
            return;
        VirtualOutputStateChanged?.Invoke(monitorId, false);
        _ = Task.Run(window.Dispose);
    }

    private MatrixScenePresentation? CaptureVirtualOutputFrame(
        string monitorId)
    {
        NativeWallpaperWindow? compositor =
            _windows.FirstOrDefault();
        return compositor?.CaptureVirtualOutputFrame(monitorId);
    }

    private string ResolveVirtualOutputSource(string requested)
    {
        if (_windows.Count == 0 || _monitors.Count == 0)
            return "";
        if (!string.IsNullOrWhiteSpace(requested)
            && _monitors.Any(monitor => string.Equals(
                monitor.Id,
                requested,
                StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        foreach (MonitorDescriptor monitor in _monitors
                     .OrderByDescending(item => item.Primary))
        {
            if (CaptureVirtualOutputFrame(monitor.Id) is not null)
                return monitor.Id;
        }
        return "";
    }

    private static bool VirtualOutputConfigurationEquivalent(
        AppSettings left,
        AppSettings right) =>
        left.VirtualOutputWidth == right.VirtualOutputWidth
        && left.VirtualOutputHeight == right.VirtualOutputHeight
        && left.VirtualMonitorEnabled == right.VirtualMonitorEnabled
        && left.VirtualMonitorOffsetX == right.VirtualMonitorOffsetX
        && left.VirtualMonitorOffsetY == right.VirtualMonitorOffsetY;
}
