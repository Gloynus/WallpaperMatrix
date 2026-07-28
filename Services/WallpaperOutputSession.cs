using System.Diagnostics;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;
using WallpaperMatrix.Rendering;

namespace WallpaperMatrix.Services;

/// <summary>
/// Owns the complete native wallpaper surface for all monitors.
/// Image sequencing, timers and fullscreen policy intentionally live outside
/// this class; it has one responsibility: output lifecycle.
/// </summary>
internal sealed class WallpaperOutputSession : IDisposable
{
    private readonly List<NativeWallpaperWindow> _windows = [];
    private readonly Action<string, Exception, bool> _failureHandler;
    private AppSettings _settings = new();
    private PreparedImage? _image;
    private IReadOnlyDictionary<string, PreparedImage?> _databaseImages =
        new Dictionary<string, PreparedImage?>();
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private MonitorOutputPlan? _plan;
    private bool _suspended;
    private bool _disposed;
    private int _screenCount;

    public bool IsRunning => _windows.Count > 0;
    public SharedMatrixScene? SharedFrame =>
        _windows.Count > 0 ? _windows[0].SharedFrame : null;
    public AttackFrameSnapshot? CaptureAttackFrame() =>
        _windows.Count > 0
            ? _windows[0].CaptureAttackFrame()
            : null;
    public int WindowCount => _screenCount;
    public int TargetWidth { get; private set; } = 2560;
    public int TargetHeight { get; private set; } = 1440;

    public WallpaperOutputSession(Action<string, Exception, bool> failureHandler)
    {
        _failureHandler = failureHandler;
    }

    public void Start(AppSettings settings, PreparedImage? image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        _settings = settings.Copy();
        _image = image;
        CreateWindows();
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
        _image = image;
        CreateWindows();
        SetImage(_image);
        if (suspended)
            Suspend();
        else
            Activate();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings.Copy();
        MonitorTopology.EnsureProfiles(_settings, _monitors);
        MonitorOutputPlan nextPlan =
            MonitorOutputPlan.Create(_settings, _monitors);
        if (_plan is null
            || !TopologyEquivalent(_plan, nextPlan))
        {
            Restart(_settings, _image, _suspended);
            return;
        }
        _plan = nextPlan;
        foreach (NativeWallpaperWindow window in _windows)
            window.UpdateSettings(nextPlan);
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

    public void StopAndRestoreDesktop() =>
        CloseWindows(restoreSystemWallpaper: true);

    private void CreateWindows()
    {
        _monitors = MonitorCatalog.Capture();
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
                    $"{monitor.SystemName} «{monitor.FriendlyName}» "
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
                    return $"{monitor.FriendlyName}: "
                        + $"поток={flow.Mode}->{flow.RootMonitorId}; "
                        + $"база={database.Mode}->{database.RootMonitorId}";
                }))
            + $"; сцен={_plan.Scenes.Count}; "
            + $"активных устройств={_plan.ActiveMonitorCount}.");

        List<NativeWallpaperWindow> created = [];
        try
        {
            NativeWallpaperWindow compositor = new(
                _plan,
                _settings,
                failureHandler: _failureHandler);
            created.Add(compositor);
            compositor.Start();
            TargetWidth = Math.Max(1, _plan.VirtualBounds.Width);
            TargetHeight = Math.Max(1, _plan.VirtualBounds.Height);

            _windows.AddRange(created);
            _screenCount = _monitors.Count;
            if (_databaseImages.Count > 0)
                compositor.SetDatabaseImages(_databaseImages);
        }
        catch
        {
            CloseWindowList(created, restoreSystemWallpaper: true);
            throw;
        }
    }

    private void CloseWindows(bool restoreSystemWallpaper)
    {
        if (_windows.Count == 0)
            return;

        NativeWallpaperWindow[] closingWindows = _windows.ToArray();
        _windows.Clear();
        _screenCount = 0;
        _plan = null;
        CloseWindowList(closingWindows, restoreSystemWallpaper);
    }

    private static bool TopologyEquivalent(
        MonitorOutputPlan left,
        MonitorOutputPlan right)
    {
        if (left.VirtualBounds != right.VirtualBounds
            || left.Scenes.Count != right.Scenes.Count
            || left.ActiveMonitorCount != right.ActiveMonitorCount)
        {
            return false;
        }
        for (int sceneIndex = 0;
             sceneIndex < left.Scenes.Count;
             sceneIndex++)
        {
            MonitorScenePlan leftScene = left.Scenes[sceneIndex];
            MonitorScenePlan rightScene = right.Scenes[sceneIndex];
            if (!string.Equals(
                    leftScene.Id,
                    rightScene.Id,
                    StringComparison.OrdinalIgnoreCase)
                || leftScene.CanvasBounds != rightScene.CanvasBounds
                || leftScene.Targets.Count != rightScene.Targets.Count)
            {
                return false;
            }
            for (int targetIndex = 0;
                 targetIndex < leftScene.Targets.Count;
                 targetIndex++)
            {
                MonitorSceneTarget leftTarget =
                    leftScene.Targets[targetIndex];
                MonitorSceneTarget rightTarget =
                    rightScene.Targets[targetIndex];
                if (!string.Equals(
                        leftTarget.MonitorId,
                        rightTarget.MonitorId,
                        StringComparison.OrdinalIgnoreCase)
                    || leftTarget.TargetBounds
                        != rightTarget.TargetBounds
                    || leftTarget.SourceBounds
                        != rightTarget.SourceBounds)
                {
                    return false;
                }
            }
        }
        return true;
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
        CloseWindows(restoreSystemWallpaper: true);
    }
}
