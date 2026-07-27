using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;
using WallpaperMatrix.Rendering;
using WallpaperMatrix.Services;
using WallpaperMatrix.Views;

namespace WallpaperMatrix;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private SettingsStore? _settingsStore;
    private PlaylistStore? _playlistStore;
    private AppSettings _settings = new();
    private WallpaperManager? _wallpaperManager;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
    private bool _handlingDispatcherFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        bool validateShaders = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-shaders",
                StringComparison.OrdinalIgnoreCase));
        bool validateAttack = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-attack",
                StringComparison.OrdinalIgnoreCase));
        if (validateShaders || validateAttack)
        {
            try
            {
                Direct3D11Presenter.ValidateShaders();
                if (validateAttack)
                {
                    CapturedDesktopFrame capture =
                        DesktopCaptureService.CaptureVirtualDesktop();
                    DiagnosticLog.Write(
                        $"Самопроверка захвата АТАКИ СИСТЕМЫ завершена успешно: "
                        + $"{capture.Width}x{capture.Height}; "
                        + $"начало=({capture.Left},{capture.Top}); "
                        + $"BGRA={capture.Pixels.Length} байт.");
                }
                DiagnosticLog.Write(
                    "Самопроверка шейдеров D3D11 завершена успешно.");
                Shutdown(0);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(
                    "Самопроверка шейдеров D3D11 завершилась ошибкой.",
                    exception);
                Shutdown(1);
            }
            return;
        }
        bool startInBackground = e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        bool forceSettings = e.Args.Any(argument =>
            string.Equals(argument, "--show-settings", StringComparison.OrdinalIgnoreCase));
        bool forceAttack = e.Args.Any(argument =>
            string.Equals(argument, "--attack-now", StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(true, "Local\\WallpaperMatrix.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "Wallpaper Matrix уже работает. Ищите зелёный значок в области уведомлений.",
                "Wallpaper Matrix",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _playlistStore = new PlaylistStore();
        _playlistStore.LoadInto(_settings);

        _wallpaperManager = new WallpaperManager(_settings);
        _wallpaperManager.PauseStateChanged += OnWallpaperPauseStateChanged;
        _wallpaperManager.RuntimeStatusChanged += OnWallpaperRuntimeStatusChanged;
        try
        {
            _wallpaperManager.Start();
        }
        catch (Exception ex)
        {
            _wallpaperManager.PauseStateChanged -= OnWallpaperPauseStateChanged;
            _wallpaperManager.RuntimeStatusChanged -= OnWallpaperRuntimeStatusChanged;
            DiagnosticLog.Write("Запуск живых обоев завершился ошибкой.", ex);
            _wallpaperManager.Dispose();
            _wallpaperManager = null;
            System.Windows.MessageBox.Show(
                $"Не удалось запустить живые обои.\n\n"
                + ex.GetBaseException().Message
                + $"\n\nДля вывода требуются Windows 10/11, Direct3D 11 "
                + $"и доступный фоновый слой рабочего стола Explorer."
                + $"\n\nЖурнал диагностики:\n{DiagnosticLog.LogPath}",
                "Wallpaper Matrix",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _tray = new TrayService(
            showSettings: ShowSettings,
            togglePaused: TogglePaused,
            toggleImageMode: ToggleImageMode,
            nextImage: () => _wallpaperManager?.NextImage(),
            refreshDesktop: () => _wallpaperManager?.RefreshWindows(),
            exit: ExitApplication);
        _tray.Update(
            _settings,
            _wallpaperManager.IsManuallyPaused,
            _wallpaperManager.IsPausedByFullscreenApp);

        TryApplyAutostart(_settings.StartWithWindows, showError: false);

        if (forceSettings || (!_settings.WelcomeShown && !startInBackground))
        {
            if (!_settings.WelcomeShown)
            {
                _settings.WelcomeShown = true;
                _settingsStore.Save(_settings);
            }
            ShowSettings();
        }
        if (forceAttack)
            Dispatcher.BeginInvoke(StartAttack);

    }

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            MainWindow = _settingsWindow;
            _settingsWindow.SettingsApplied += ApplySettings;
            _settingsWindow.PlaylistsSaved += SavePlaylists;
            _settingsWindow.SettingsPreviewed += PreviewSettings;
            _settingsWindow.ImageRequested += PreviewImage;
            _settingsWindow.PauseRequested += SetWallpaperPaused;
            _settingsWindow.AttackRequested += StartAttack;
        }

        _settingsWindow.LoadSettings(_settings);
        _settingsWindow.SetPauseState(_wallpaperManager?.IsManuallyPaused ?? false);
        if (_wallpaperManager is not null)
        {
            _settingsWindow.SetRuntimeStatus(
                _wallpaperManager.RuntimeStatus,
                _wallpaperManager.HasRuntimeError,
                _wallpaperManager.DiagnosticLogPath);
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ApplySettings(AppSettings updated)
    {
        _settings = updated.Copy();
        _settingsStore?.Save(_settings);
        _playlistStore?.Save(_settings);
        TryApplyAutostart(_settings.StartWithWindows, showError: true);
        _wallpaperManager?.ApplySettings(_settings);
        _tray?.Update(
            _settings,
            _wallpaperManager?.IsManuallyPaused ?? false,
            _wallpaperManager?.IsPausedByFullscreenApp ?? false);
    }

    private void PreviewSettings(AppSettings preview)
    {
        // Idle activation is a system policy, not a visual shader preview.
        // Keep it on the last applied value until the operator presses Apply.
        preview.AttackSystemEnabled = _settings.AttackSystemEnabled;
        preview.AttackIdleMinutes = _settings.AttackIdleMinutes;
        preview.AttackTransitionSeconds =
            _settings.AttackTransitionSeconds;
        _wallpaperManager?.ApplySettings(preview);
    }

    private void SavePlaylists(AppSettings liveDraft)
    {
        _settings.ImagePlaylists = liveDraft.ImagePlaylists
            .Select(playlist => playlist.Copy())
            .ToList();
        _settings.ActiveImagePlaylistId = liveDraft.ActiveImagePlaylistId;
        _settings.Normalize();
        _playlistStore?.Save(_settings);
        _wallpaperManager?.ApplySettings(liveDraft);
    }

    private void PreviewImage(AppSettings preview, string path) =>
        _wallpaperManager?.ShowImage(preview, path);

    private void SetWallpaperPaused(bool paused) =>
        _wallpaperManager?.SetPaused(paused);

    private void StartAttack() =>
        _wallpaperManager?.StartAttack();

    private void TogglePaused()
    {
        if (_wallpaperManager is null)
            return;

        _wallpaperManager.SetPaused(!_wallpaperManager.IsManuallyPaused);
        _tray?.Update(
            _settings,
            _wallpaperManager.IsManuallyPaused,
            _wallpaperManager.IsPausedByFullscreenApp);
        _settingsWindow?.SetPauseState(_wallpaperManager.IsManuallyPaused);
    }

    private void ToggleImageMode()
    {
        _settings.ImageMode = !_settings.ImageMode;
        _settingsStore?.Save(_settings);
        _wallpaperManager?.ApplySettings(_settings);
        _tray?.Update(
            _settings,
            _wallpaperManager?.IsManuallyPaused ?? false,
            _wallpaperManager?.IsPausedByFullscreenApp ?? false);
        _settingsWindow?.LoadSettings(_settings);
    }

    private void OnWallpaperPauseStateChanged()
    {
        if (_wallpaperManager is null)
            return;
        _tray?.Update(
            _settings,
            _wallpaperManager.IsManuallyPaused,
            _wallpaperManager.IsPausedByFullscreenApp);
        _settingsWindow?.SetPauseState(_wallpaperManager.IsManuallyPaused);
    }

    private void OnWallpaperRuntimeStatusChanged()
    {
        if (_wallpaperManager is null)
            return;
        _settingsWindow?.SetRuntimeStatus(
            _wallpaperManager.RuntimeStatus,
            _wallpaperManager.HasRuntimeError,
            _wallpaperManager.DiagnosticLogPath);
        if (_wallpaperManager.HasRuntimeError)
            _tray?.ShowError(_wallpaperManager.RuntimeStatus);
    }

    private static void TryApplyAutostart(bool enabled, bool showError)
    {
        try
        {
            AutostartService.SetEnabled(enabled);
        }
        catch (Exception ex) when (showError)
        {
            System.Windows.MessageBox.Show(
                $"Не удалось изменить автозапуск:\n{ex.Message}",
                "Wallpaper Matrix",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Wallpaper should still run even if policy blocks the Run registry key.
        }
    }

    private void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        _settingsWindow?.ForceClose();
        _tray?.Dispose();
        if (_wallpaperManager is not null)
        {
            _wallpaperManager.PauseStateChanged -= OnWallpaperPauseStateChanged;
            _wallpaperManager.RuntimeStatusChanged -= OnWallpaperRuntimeStatusChanged;
        }
        _wallpaperManager?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        // WPF Shutdown re-enters WinForms teardown for the tray window and can
        // block Explorer after every owned resource has already been released.
        TerminateProcess(GetCurrentProcess(), 0);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Write(
            "Необработанная ошибка операторской консоли. Вывод остановлен, рабочий стол восстановлен.",
            e.Exception);
        e.Handled = true;
        if (_handlingDispatcherFailure)
            return;

        _handlingDispatcherFailure = true;
        try
        {
            try
            {
                _wallpaperManager?.SetPaused(true);
            }
            catch
            {
                DesktopHost.RefreshDesktopSurface(restoreSystemWallpaper: true);
            }

            System.Windows.MessageBox.Show(
                "Параметр не был применён из-за внутренней ошибки.\n\n"
                + "Вывод кода остановлен, обычные обои Windows восстановлены. "
                + "Подробности записаны в журнал диагностики.",
                "Wallpaper Matrix",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _handlingDispatcherFailure = false;
        }
    }

    private void OnDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException("Неизвестная авария процесса.");
        DiagnosticLog.Write(
            "Авария процесса. Перед завершением выполняется восстановление рабочего стола.",
            exception);
        try
        {
            DesktopHost.RefreshDesktopSurface(restoreSystemWallpaper: true);
        }
        catch
        {
            // The process is already terminating; restoration is best effort.
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLog.Write("Фоновая задача завершилась ошибкой.", e.Exception);
        e.SetObserved();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _wallpaperManager?.Dispose();
        base.OnExit(e);
    }
}
