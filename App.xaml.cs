using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private DispatcherTimer? _attackValidationTimer;
    private DispatcherTimer? _stopValidationTimer;
    private DispatcherTimer? _featureValidationTimer;
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
        bool validateTopology = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-topology",
                StringComparison.OrdinalIgnoreCase));
        if (validateShaders || validateAttack || validateTopology)
        {
            try
            {
                if (validateShaders || validateAttack)
                    Direct3D11Presenter.ValidateShaders();
                if (validateTopology)
                {
                    MonitorTopologyValidation.Validate();
                    IReadOnlyList<MonitorDescriptor> monitors =
                        MonitorCatalog.Capture();
                    DiagnosticLog.Write(
                        "Самопроверка маршрутизации устройств вывода "
                        + "завершена успешно. Метки каналов Wallpaper Matrix: "
                        + string.Join(
                            "; ",
                            monitors.Select(monitor =>
                                $"{monitor.DisplayNumber}="
                                + $"{monitor.SystemName} "
                                + $"«{monitor.FriendlyName}»")));
                }
                if (validateShaders || validateAttack)
                {
                    DiagnosticLog.Write(
                        "Самопроверка шейдеров D3D11 завершена успешно.");
                }
                Shutdown(0);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(
                    "Самопроверка Wallpaper Matrix завершилась ошибкой.",
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
        bool validateAttackOverlay = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-attack-overlay",
                StringComparison.OrdinalIgnoreCase));
        bool validateStop = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-stop",
                StringComparison.OrdinalIgnoreCase));
        bool validateRouteSwitch = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-route-switch",
                StringComparison.OrdinalIgnoreCase));
        bool validateVirtualOutput = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-virtual-output",
                StringComparison.OrdinalIgnoreCase));
        bool validateRecovery = e.Args.Any(argument =>
            string.Equals(
                argument,
                "--validate-recovery",
                StringComparison.OrdinalIgnoreCase));
        DiagnosticLog.Write(
            $"Параметры запуска: [{string.Join(", ", e.Args)}]; "
            + $"showSettings={forceSettings}; "
            + $"background={startInBackground}.");
        startInBackground |= validateAttackOverlay;
        startInBackground |= validateStop;
        startInBackground |= validateRouteSwitch;
        startInBackground |= validateVirtualOutput;
        startInBackground |= validateRecovery;

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
        if (validateRouteSwitch || validateAttackOverlay)
            _settings.PauseDuringFullscreenApps = false;
        if (validateAttackOverlay)
        {
            // Keep the lifecycle check short but meaningful: the reference
            // frame must already be fully opaque before interface capture.
            // This copy is never persisted to the operator's settings.
            _settings.ImageDurationSeconds = 1.0;
        }
        IReadOnlyList<MonitorDescriptor> startupMonitors =
            OutputDeviceCatalog.Capture(_settings);
        MonitorTopology.EnsureProfiles(
            _settings,
            startupMonitors);
        _playlistStore = new PlaylistStore();
        _playlistStore.LoadInto(_settings);
        MonitorSettingsSynchronizer.SynchronizePrimary(
            _settings,
            startupMonitors);

        _wallpaperManager = new WallpaperManager(_settings);
        _wallpaperManager.PauseStateChanged += OnWallpaperPauseStateChanged;
        _wallpaperManager.RuntimeStatusChanged += OnWallpaperRuntimeStatusChanged;
        _wallpaperManager.VirtualOutputStateChanged +=
            OnVirtualOutputStateChanged;
        _wallpaperManager.PlaylistImageAvailabilityChanged +=
            OnPlaylistImageAvailabilityChanged;
        try
        {
            _wallpaperManager.Start();
        }
        catch (Exception ex)
        {
            _wallpaperManager.PauseStateChanged -= OnWallpaperPauseStateChanged;
            _wallpaperManager.RuntimeStatusChanged -= OnWallpaperRuntimeStatusChanged;
            _wallpaperManager.VirtualOutputStateChanged -=
                OnVirtualOutputStateChanged;
            _wallpaperManager.PlaylistImageAvailabilityChanged -=
                OnPlaylistImageAvailabilityChanged;
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
            selectPlaylist: SelectPlaylistFromTray,
            nextImage: () => _wallpaperManager?.NextImage(),
            refreshDesktop: () => _wallpaperManager?.RefreshWindows(),
            exit: ExitApplication);
        _tray.Update(
            _settings,
            _wallpaperManager.IsManuallyPaused,
            _wallpaperManager.IsPausedByFullscreenApp);

        TryApplyAutostart(
            _settings.StartWithWindows,
            showError: false,
            claimOwnership: _settings.StartWithWindows);

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
        if (validateAttackOverlay)
        {
            int phase = 0;
            _attackValidationTimer = new DispatcherTimer(
                DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _attackValidationTimer.Tick += (_, _) =>
            {
                if (phase++ == 0)
                {
                    StartAttack();
                    _attackValidationTimer!.Interval =
                        TimeSpan.FromSeconds(12);
                    return;
                }
                _attackValidationTimer?.Stop();
                DiagnosticLog.Write(
                    "Самопроверка жизненного цикла АТАКИ СИСТЕМЫ завершена.");
                ExitApplication();
            };
            _attackValidationTimer.Start();
        }
        if (validateStop)
        {
            int phase = 0;
            _stopValidationTimer = new DispatcherTimer(
                DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _stopValidationTimer.Tick += (_, _) =>
            {
                if (phase++ == 0)
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    _wallpaperManager?.SetPaused(true);
                    clock.Stop();
                    DiagnosticLog.Write(
                        $"Самопроверка СТОП: рабочий стол освобождён за "
                        + $"{clock.Elapsed.TotalMilliseconds:0} мс.");
                    _stopValidationTimer!.Interval =
                        TimeSpan.FromSeconds(2);
                    return;
                }

                _stopValidationTimer?.Stop();
                ExitApplication();
            };
            _stopValidationTimer.Start();
        }
        if (validateVirtualOutput)
        {
            int phase = 0;
            _featureValidationTimer = new DispatcherTimer(
                DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _featureValidationTimer.Tick += (_, _) =>
            {
                if (phase++ == 0)
                {
                    AppSettings validation = _settings.Copy();
                    validation.VirtualMonitorEnabled = true;
                    validation.VirtualOutputSourceMonitorId =
                        OutputDeviceCatalog.VirtualMonitorId;
                    validation.VirtualOutputWidth = 960;
                    validation.VirtualOutputHeight = 540;
                    IReadOnlyList<MonitorDescriptor> validationDevices =
                        OutputDeviceCatalog.Capture(validation);
                    MonitorTopology.EnsureProfiles(
                        validation,
                        validationDevices);
                    MonitorDescriptor physical =
                        validationDevices.First(monitor =>
                            monitor.Primary);
                    MonitorTopology.SetRoute(
                        validation.MonitorProfiles,
                        validationDevices,
                        MonitorRouteDomain.Flow,
                        OutputDeviceCatalog.VirtualMonitorId,
                        MonitorLinkMode.Relay,
                        physical.Id);
                    validation.VirtualMonitorEnabled = true;
                    _wallpaperManager?.ApplySettings(validation);
                    _wallpaperManager?.SetVirtualOutput(
                        true,
                        validation);
                    AppSettings physicalWindow =
                        validation.Copy();
                    physicalWindow.VirtualOutputSourceMonitorId =
                        physical.Id;
                    _wallpaperManager?.SetVirtualOutput(
                        true,
                        physicalWindow);
                    return;
                }
                if (phase == 2)
                {
                    IReadOnlyList<string> openWindows =
                        _wallpaperManager?.VirtualOutputMonitorIds
                        ?? [];
                    DiagnosticLog.Write(
                        openWindows.Count >= 2
                        && openWindows.Contains(
                            OutputDeviceCatalog.VirtualMonitorId,
                            StringComparer.OrdinalIgnoreCase)
                            ? "Самопроверка виртуального устройства и независимых окон завершена успешно."
                            : "Самопроверка виртуального устройства не подтвердила два независимых окна.");
                    AppSettings closeSettings =
                        _settings.Copy();
                    closeSettings.VirtualMonitorEnabled = true;
                    closeSettings.VirtualOutputSourceMonitorId =
                        OutputDeviceCatalog.VirtualMonitorId;
                    _wallpaperManager?.SetVirtualOutput(
                        false,
                        closeSettings);
                    MonitorDescriptor? physical =
                        OutputDeviceCatalog.Capture(closeSettings)
                            .FirstOrDefault(monitor =>
                                monitor.Primary);
                    if (physical is not null)
                    {
                        AppSettings physicalWindow =
                            closeSettings.Copy();
                        physicalWindow.VirtualOutputSourceMonitorId =
                            physical.Id;
                        _wallpaperManager?.SetVirtualOutput(
                            false,
                            physicalWindow);
                    }
                    _featureValidationTimer!.Interval =
                        TimeSpan.FromSeconds(1);
                    return;
                }
                _featureValidationTimer?.Stop();
                ExitApplication();
            };
            _featureValidationTimer.Start();
        }
        else if (validateRecovery)
        {
            int phase = 0;
            _featureValidationTimer = new DispatcherTimer(
                DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _featureValidationTimer.Tick += (_, _) =>
            {
                if (phase++ == 0)
                {
                    _wallpaperManager?.SimulateOutputLossForValidation();
                    _featureValidationTimer!.Interval =
                        TimeSpan.FromSeconds(8);
                    return;
                }
                DiagnosticLog.Write(
                    _wallpaperManager?.IsOutputActive == true
                        ? "Самопроверка аварийного контура завершена успешно."
                        : "Самопроверка аварийного контура не восстановила вывод.");
                _featureValidationTimer?.Stop();
                ExitApplication();
            };
            _featureValidationTimer.Start();
        }
        if (validateRouteSwitch)
        {
            AppSettings original = _settings.Copy();
            int phase = 0;
            _stopValidationTimer = new DispatcherTimer(
                DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _stopValidationTimer.Tick += (_, _) =>
            {
                if (phase++ == 0)
                {
                    IReadOnlyList<MonitorDescriptor> monitors =
                        MonitorCatalog.Capture();
                    if (monitors.Count < 2)
                    {
                        DiagnosticLog.Write(
                            "Самопроверка бесшовной маршрутизации пропущена: "
                            + "доступен только один экран.");
                        ExitApplication();
                        return;
                    }

                    AppSettings isolated = original.Copy();
                    MonitorTopology.EnsureProfiles(isolated, monitors);
                    MonitorDescriptor target = monitors.First(monitor =>
                        !monitor.Primary);
                    MonitorTopology.SetRoute(
                        isolated.MonitorProfiles,
                        monitors,
                        MonitorRouteDomain.Flow,
                        target.Id,
                        MonitorLinkMode.Isolated,
                        "");
                    MonitorTopology.SetRoute(
                        isolated.MonitorProfiles,
                        monitors,
                        MonitorRouteDomain.Database,
                        target.Id,
                        MonitorLinkMode.Isolated,
                        "");
                    _wallpaperManager?.ApplySettings(isolated);
                    DiagnosticLog.Write(
                        "Самопроверка бесшовной маршрутизации: "
                        + $"устройство «{target.Label}» изолировано без "
                        + "пересоздания поверхности рабочего стола.");
                    return;
                }

                if (phase == 2)
                {
                    _wallpaperManager?.ApplySettings(original);
                    DiagnosticLog.Write(
                        "Самопроверка бесшовной маршрутизации: "
                        + "изолированное устройство подключено к реальной "
                        + "общей сцене ретрансляции.");
                    return;
                }

                if (phase == 3)
                {
                    IReadOnlyList<MonitorDescriptor> monitors =
                        MonitorCatalog.Capture();
                    AppSettings extended = original.Copy();
                    MonitorTopology.EnsureProfiles(extended, monitors);
                    MonitorDescriptor root = monitors.First(monitor =>
                        monitor.Primary);
                    MonitorDescriptor target = monitors.First(monitor =>
                        !monitor.Primary);
                    MonitorTopology.SetRoute(
                        extended.MonitorProfiles,
                        monitors,
                        MonitorRouteDomain.Flow,
                        target.Id,
                        MonitorLinkMode.Extend,
                        root.Id);
                    _wallpaperManager?.ApplySettings(extended);
                    DiagnosticLog.Write(
                        "Самопроверка бесшовной маршрутизации: "
                        + $"устройство «{target.Label}» расширило поток "
                        + "с переносом уже идущих струй.");
                    return;
                }

                if (phase == 4)
                {
                    _wallpaperManager?.ApplySettings(original);
                    DiagnosticLog.Write(
                        "Самопроверка бесшовной маршрутизации: "
                        + "исходная схема восстановлена без пересоздания "
                        + "поверхности рабочего стола.");
                    _stopValidationTimer!.Interval =
                        TimeSpan.FromSeconds(1);
                    return;
                }

                _stopValidationTimer?.Stop();
                ExitApplication();
            };
            _stopValidationTimer.Start();
        }

    }

    private void ShowSettings()
    {
        DiagnosticLog.Write("Открытие панели оператора запрошено.");
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            MainWindow = _settingsWindow;
            _settingsWindow.SettingsApplied += ApplySettings;
            _settingsWindow.PlaylistsSaved += SavePlaylists;
            _settingsWindow.PlaylistsReloaded += ReloadPlaylists;
            _settingsWindow.SettingsPreviewed += PreviewSettings;
            _settingsWindow.ImageRequested += PreviewImage;
            _settingsWindow.PauseRequested += SetWallpaperPaused;
            _settingsWindow.AttackRequested += StartAttack;
            _settingsWindow.VirtualOutputRequested +=
                SetVirtualOutput;
        }

        AppSettings diskPlaylists = _settings.Copy();
        _playlistStore?.LoadInto(diskPlaylists);
        if (!AppSettingsComparer.Equivalent(_settings, diskPlaylists))
        {
            _settings = diskPlaylists;
            _wallpaperManager?.ApplySettings(_settings);
        }
        _settingsWindow.LoadSettings(_settings);
        _settingsWindow.SetPauseState(_wallpaperManager?.IsManuallyPaused ?? false);
        _settingsWindow.SetVirtualOutputStates(
            _wallpaperManager?.VirtualOutputMonitorIds
                ?? []);
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
        DiagnosticLog.Write("Панель оператора показана.");
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
        CopyPlaylistState(_settings, liveDraft);
        _settings.Normalize();
        _playlistStore?.Save(_settings);
        _wallpaperManager?.ApplySettings(liveDraft);
        UpdateTrayState();
    }

    private void ReloadPlaylists(AppSettings liveDraft)
    {
        CopyPlaylistState(_settings, liveDraft);
        _settings.Normalize();
        _wallpaperManager?.ApplySettings(liveDraft);
        UpdateTrayState();
    }

    private static void CopyPlaylistState(
        AppSettings target,
        AppSettings source)
    {
        target.ImagePlaylists = source.ImagePlaylists
            .Select(playlist => playlist.Copy())
            .ToList();
        target.ActiveImagePlaylistId = source.ActiveImagePlaylistId;
        target.PlaylistPresentations = source.PlaylistPresentations
            .Select(presentation => presentation.Copy())
            .ToList();
        foreach (MonitorProfile sourceProfile in source.MonitorProfiles)
        {
            MonitorProfile? targetProfile = MonitorTopology.Find(
                target.MonitorProfiles,
                sourceProfile.MonitorId);
            if (targetProfile is null)
                continue;
            targetProfile.Settings.ActiveImagePlaylistId =
                sourceProfile.Settings.ActiveImagePlaylistId;
            targetProfile.Settings.PlaylistPresentations =
                sourceProfile.Settings.PlaylistPresentations
                    .Select(presentation => presentation.Copy())
                    .ToList();
        }
        target.Normalize();
    }

    private void PreviewImage(
        AppSettings preview,
        string path,
        string monitorId) =>
        _wallpaperManager?.ShowImage(preview, path, monitorId);

    private void OnPlaylistImageAvailabilityChanged(
        string path,
        bool available)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => _settingsWindow?.SetPlaylistImageAvailability(
                path,
                available));
    }

    private void SetWallpaperPaused(bool paused) =>
        _wallpaperManager?.SetPaused(paused);

    private void StartAttack() =>
        _wallpaperManager?.StartAttack();

    private void SetVirtualOutput(
        AppSettings settings,
        bool open)
    {
        _settings.VirtualOutputSourceMonitorId =
            settings.VirtualOutputSourceMonitorId;
        _wallpaperManager?.SetVirtualOutput(open, settings);
    }

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

    private void SelectPlaylistFromTray(
        string databaseRootMonitorId,
        string playlistId)
    {
        IReadOnlyList<MonitorDescriptor> monitors =
            OutputDeviceCatalog.Capture(_settings);
        MonitorTopology.EnsureProfiles(_settings, monitors);
        MonitorProfile? databaseProfile = MonitorTopology.Find(
            _settings.MonitorProfiles,
            databaseRootMonitorId);
        if (databaseProfile is null)
            return;

        ImagePlaylist? selected = databaseProfile.Settings.ImagePlaylists
            .FirstOrDefault(playlist => string.Equals(
                playlist.Id,
                playlistId,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            return;

        databaseProfile.Settings.ActiveImagePlaylistId = selected.Id;
        databaseProfile.Settings.OperatorPlaylistId = selected.Id;
        databaseProfile.Settings.OperatorPlaylistName = selected.Name;
        MonitorSettingsSynchronizer.SynchronizePrimary(
            _settings,
            monitors);
        _settings.Normalize();
        _settingsStore?.Save(_settings);
        _playlistStore?.Save(_settings);
        _wallpaperManager?.ApplySettings(_settings);
        UpdateTrayState();
        DiagnosticLog.Write(
            $"Плейлист базы данных {databaseRootMonitorId} переключён "
            + $"из области уведомлений: {selected.Name}.");
    }

    private void UpdateTrayState()
    {
        _tray?.Update(
            _settings,
            _wallpaperManager?.IsManuallyPaused ?? false,
            _wallpaperManager?.IsPausedByFullscreenApp ?? false);
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

    private void OnVirtualOutputStateChanged(
        string monitorId,
        bool open)
    {
        _settingsWindow?.SetVirtualOutputState(
            monitorId,
            open);
    }

    private static void TryApplyAutostart(
        bool enabled,
        bool showError,
        bool claimOwnership = true)
    {
        try
        {
            AutostartService.SetEnabled(
                enabled,
                claimOwnership);
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
        _attackValidationTimer?.Stop();
        _stopValidationTimer?.Stop();
        _featureValidationTimer?.Stop();
        _settingsWindow?.ForceClose();
        _tray?.Dispose();
        if (_wallpaperManager is not null)
        {
            _wallpaperManager.PauseStateChanged -= OnWallpaperPauseStateChanged;
            _wallpaperManager.RuntimeStatusChanged -= OnWallpaperRuntimeStatusChanged;
            _wallpaperManager.VirtualOutputStateChanged -=
                OnVirtualOutputStateChanged;
            _wallpaperManager.PlaylistImageAvailabilityChanged -=
                OnPlaylistImageAvailabilityChanged;
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
        _stopValidationTimer?.Stop();
        _featureValidationTimer?.Stop();
        _tray?.Dispose();
        _wallpaperManager?.Dispose();
        base.OnExit(e);
    }
}
