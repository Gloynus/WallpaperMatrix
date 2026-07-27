using System.Diagnostics;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;

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
    private bool _disposed;
    private int _screenCount;

    public bool IsRunning => _windows.Count > 0;
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
        foreach (NativeWallpaperWindow window in _windows)
            window.UpdateSettings(_settings);
    }

    public void SetImage(PreparedImage? image)
    {
        _image = image;
        foreach (NativeWallpaperWindow window in _windows)
            window.SetImage(image);
    }

    public void Activate()
    {
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
        System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens
            .OrderByDescending(screen => screen.Primary)
            .ToArray();
        if (screens.Length == 0)
            throw new InvalidOperationException(
                "Windows не сообщила ни об одном активном экране.");

        DiagnosticLog.Write(
            "Обнаружены экраны: "
            + string.Join(
                "; ",
                screens.Select(screen =>
                    $"{screen.DeviceName} {screen.Bounds.Width}x{screen.Bounds.Height} "
                    + $"@ ({screen.Bounds.Left},{screen.Bounds.Top}) "
                    + $"primary={screen.Primary}")));

        List<NativeWallpaperWindow> created = [];
        try
        {
            System.Drawing.Rectangle virtualBounds =
                System.Windows.Forms.SystemInformation.VirtualScreen;
            NativeWallpaperWindow compositor = new(
                virtualBounds,
                screens[0].Bounds.Size,
                screens.Select(screen => screen.Bounds).ToArray(),
                _settings,
                failureHandler: _failureHandler);
            created.Add(compositor);
            compositor.Start();
            TargetWidth = compositor.SharedFrame.Width;
            TargetHeight = compositor.SharedFrame.Height;

            _windows.AddRange(created);
            _screenCount = screens.Length;
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
        {
            if (restoreSystemWallpaper)
            {
                DesktopHost.HideWallpaperSurface();
                DesktopHost.RefreshDesktopSurface(restoreSystemWallpaper: true);
            }
            return;
        }

        CloseWindowList(_windows, restoreSystemWallpaper);
        _windows.Clear();
        _screenCount = 0;
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

        TimeSpan closeBudget = restoreSystemWallpaper
            ? TimeSpan.FromMilliseconds(900)
            : TimeSpan.FromSeconds(3);
        Stopwatch closeClock = Stopwatch.StartNew();
        for (int index = windows.Count - 1; index >= 0; index--)
        {
            TimeSpan remaining = closeBudget - closeClock.Elapsed;
            windows[index].WaitForClose(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        DesktopHost.RefreshDesktopSurface(restoreSystemWallpaper);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseWindows(restoreSystemWallpaper: true);
    }
}
