using Microsoft.Win32;
using System.IO;
using System.Windows.Threading;
using WallpaperMatrix.Models;
using WallpaperMatrix.Native;
using WallpaperMatrix.Rendering;

namespace WallpaperMatrix.Services;

public sealed class WallpaperManager : IDisposable
{
    private readonly ImageSequenceService _images = new();
    private readonly ImagePreparationService _imagePreparation = new();
    private readonly FullscreenApplicationMonitor _fullscreenMonitor = new();
    private readonly WallpaperOutputSession _output;
    private readonly DispatcherTimer _imageTimer;
    private readonly DispatcherTimer _displayChangeTimer;
    private readonly DispatcherTimer _sessionResumeTimer;
    private readonly DispatcherTimer _attackTimer;
    private readonly SemaphoreSlim _imageLoadGate = new(1, 1);
    private readonly Dictionary<string, DatabaseImageChannel>
        _secondaryDatabaseChannels =
            new(StringComparer.OrdinalIgnoreCase);
    private string _primaryDatabaseRoot = "";
    private AppSettings _settings;
    private DateTime _imageStartedAt;
    private ImageSourceFrame? _currentImageSource;
    private PreparedImage? _currentImage;
    private CancellationTokenSource? _imageLoadCancellation;
    private int _imageLoadGeneration;
    private int _pendingImageLoads;
    private AttackOverlayWindow? _attackOverlay;
    private bool _attackStartPending;
    private DateTime _attackRetryAfterUtc;
    private bool _manualPaused;
    private bool _fullscreenPaused;
    private bool _sessionUnavailable;
    private int _sessionResumeAttempts;
    private bool _disposed;
    private string _displayTopologySignature = "";
    private string _runtimeStatus = "ИНИЦИАЛИЗАЦИЯ ВЫВОДА";
    private bool _hasRuntimeError;

    public bool IsPaused =>
        _manualPaused || _fullscreenPaused || _sessionUnavailable;
    public bool IsManuallyPaused => _manualPaused;
    public bool IsPausedByFullscreenApp => _fullscreenPaused;
    public bool IsAttackActive =>
        _attackOverlay is not null || _attackStartPending;
    public string RuntimeStatus => _runtimeStatus;
    public bool HasRuntimeError => _hasRuntimeError;
    public string DiagnosticLogPath => DiagnosticLog.LogPath;
    public event Action? PauseStateChanged;
    public event Action? RuntimeStatusChanged;

    public WallpaperManager(AppSettings settings)
    {
        _settings = settings.Copy();
        _output = new WallpaperOutputSession(ReportWindowFailure);
        _imageTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _imageTimer.Tick += OnImageTimer;
        UpdateImageTimerInterval();
        _displayChangeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _displayChangeTimer.Tick += OnDisplayChangeTimer;
        _sessionResumeTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _sessionResumeTimer.Tick += OnSessionResumeTimer;
        _attackTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _attackTimer.Tick += OnAttackTimer;
        _fullscreenMonitor.ActivityChanged += OnFullscreenActivityChanged;
    }

    public void Start()
    {
        _output.Start(_settings, _currentImage);
        RefreshSecondaryDatabaseChannels(forceReload: true);
        UpdateImageTargetSize();
        SetRuntimeStatus(
            $"ВЫВОД АКТИВЕН // DIRECT3D 11 // ЭКРАНОВ: {_output.WindowCount}",
            isError: false);
        ReloadImages();
        _imageTimer.Start();
        _attackTimer.Start();
        _fullscreenMonitor.SetEnabled(_settings.PauseDuringFullscreenApps);
        _displayTopologySignature = CaptureDisplayTopology();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void ApplySettings(AppSettings settings)
    {
        bool reloadImages = settings.ImageMode != _settings.ImageMode
            || !string.Equals(
                settings.ImagePlaylistSignature(),
                _settings.ImagePlaylistSignature(),
                StringComparison.Ordinal);
        bool reprocessImage = !AppSettingsComparer.ImagePreparationEquivalent(
            settings,
            _settings);

        _settings = settings.Copy();
        UpdateImageTimerInterval();
        _output.UpdateSettings(_settings);
        RefreshSecondaryDatabaseChannels(forceReload: false);
        bool imageTargetChanged = UpdateImageTargetSize();
        _fullscreenMonitor.SetEnabled(_settings.PauseDuringFullscreenApps);
        if (!_settings.AttackSystemEnabled && IsAttackActive)
            StopAttack();

        if (reloadImages)
            ReloadImages();
        else if ((reprocessImage || imageTargetChanged)
                 && _settings.ImageMode)
        {
            QueueImageLoad(
                _currentImageSource is null ? ImageLoadKind.Reload : ImageLoadKind.Reprocess);
        }
    }

    public void SetPaused(bool paused)
    {
        if (_manualPaused == paused)
            return;
        _manualPaused = paused;
        ApplyPauseState();
    }

    public void StartAttack()
    {
        BeginAttack(manual: true);
    }

    public void StopAttack()
    {
        _attackOverlay?.RequestExit();
    }

    private void SetFullscreenPaused(bool paused)
    {
        if (_fullscreenPaused == paused)
            return;
        _fullscreenPaused = paused;
        ApplyPauseState();
    }

    private void ApplyPauseState()
    {
        if ((_manualPaused || _fullscreenPaused || _sessionUnavailable)
            && IsAttackActive)
        {
            AbortAttackImmediately();
        }

        if (_sessionUnavailable)
        {
            // A renderer tied to the old desktop must never be resurrected
            // while the secure desktop or a disconnected session is active.
        }
        else if (_manualPaused)
        {
            StopOutputAndRestoreDesktop();
        }
        else if (_fullscreenPaused)
        {
            _output.Suspend();
        }
        else
        {
            ResumeOutput();
        }
        PauseStateChanged?.Invoke();
    }

    public void NextImage()
    {
        if (!_settings.ImageMode || Volatile.Read(ref _pendingImageLoads) > 0)
            return;
        QueueImageLoad(ImageLoadKind.Next);
    }

    public void ShowImage(
        AppSettings settings,
        string path,
        string monitorId)
    {
        ApplySettings(settings);
        IReadOnlyList<MonitorDescriptor> monitors = MonitorCatalog.Capture();
        AppSettings topology = _settings.Copy();
        MonitorTopology.EnsureProfiles(topology, monitors);
        MonitorRoute? route = MonitorTopology.Resolve(
                topology.MonitorProfiles,
                monitors,
                MonitorRouteDomain.Database)
            .FirstOrDefault(candidate => string.Equals(
                candidate.MonitorId,
                monitorId,
                StringComparison.OrdinalIgnoreCase));
        if (route is null || route.Mode == MonitorLinkMode.Disabled)
            return;
        if (string.Equals(
            route.RootMonitorId,
            _primaryDatabaseRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            QueueImageLoad(ImageLoadKind.Specific, path);
            return;
        }
        if (_secondaryDatabaseChannels.TryGetValue(
                route.RootMonitorId,
                out DatabaseImageChannel? channel))
        {
            QueueSecondaryImageLoad(
                channel,
                ImageLoadKind.Specific,
                path);
        }
    }

    public void RefreshWindows()
    {
        if (IsAttackActive)
            AbortAttackImmediately();
        _displayTopologySignature = CaptureDisplayTopology();
        if (_sessionUnavailable)
        {
            DiagnosticLog.Write(
                "Переподключение отложено: пользовательский сеанс недоступен.");
            return;
        }
        if (_manualPaused)
        {
            StopOutputAndRestoreDesktop();
            return;
        }

        try
        {
            _output.Restart(_settings, _currentImage, _fullscreenPaused);
            RefreshSecondaryDatabaseChannels(forceReload: true);
            UpdateImageTargetSize();
            SetRuntimeStatus(
                $"ВЫВОД ПЕРЕПОДКЛЮЧЁН // DIRECT3D 11 // ЭКРАНОВ: {_output.WindowCount}",
                isError: false);
        }
        catch (Exception exception)
        {
            ReportWindowFailure(
                "Не удалось переподключить слой рабочего стола.",
                exception,
                fatal: true);
            return;
        }
        if (_settings.ImageMode && _currentImageSource is not null)
            QueueImageLoad(ImageLoadKind.Reprocess);
    }

    private void ResumeOutput()
    {
        if (!_output.IsRunning)
        {
            try
            {
                _output.Start(_settings, _currentImage);
                UpdateImageTargetSize();
                SetRuntimeStatus(
                    $"ВЫВОД ВОЗОБНОВЛЁН // DIRECT3D 11 // ЭКРАНОВ: {_output.WindowCount}",
                    isError: false);
            }
            catch (Exception exception)
            {
                _output.StopAndRestoreDesktop();
                ReportWindowFailure(
                    "Не удалось возобновить слой рабочего стола.",
                    exception,
                    fatal: true);
                return;
            }
        }

        _output.Activate();
    }

    private void StopOutputAndRestoreDesktop()
    {
        _output.StopAndRestoreDesktop();
        SetRuntimeStatus("ВЫВОД ОСТАНОВЛЕН // РАБОЧИЙ СТОЛ ВОССТАНОВЛЕН", isError: false);
    }

    private bool UpdateImageTargetSize()
    {
        (int primaryWidth, int primaryHeight) =
            _output.DatabaseTargetSize(_primaryDatabaseRoot);
        bool changed = _images.TargetWidth != primaryWidth
            || _images.TargetHeight != primaryHeight;
        _images.TargetWidth = primaryWidth;
        _images.TargetHeight = primaryHeight;
        foreach (DatabaseImageChannel channel in
                 _secondaryDatabaseChannels.Values)
        {
            (int width, int height) =
                _output.DatabaseTargetSize(channel.RootMonitorId);
            channel.Sequence.TargetWidth = width;
            channel.Sequence.TargetHeight = height;
        }
        return changed;
    }

    private void ReloadImages()
    {
        if (_settings.ImageMode)
        {
            QueueImageLoad(ImageLoadKind.Reload);
            return;
        }

        Interlocked.Increment(ref _imageLoadGeneration);
        Interlocked.Exchange(ref _imageLoadCancellation, null)?.Cancel();
        ApplyLoadedImage(null, null, resetCycle: true);
    }

    private void QueueImageLoad(ImageLoadKind kind, string? requestedPath = null)
    {
        int generation = Interlocked.Increment(ref _imageLoadGeneration);
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _imageLoadCancellation,
            cancellation);
        previous?.Cancel();
        AppSettings settings = _settings.Copy();
        string playlistSignature = settings.ImagePlaylistSignature();
        ImageSourceFrame? source = _currentImageSource;
        Interlocked.Increment(ref _pendingImageLoads);
        _ = LoadImageAsync(
            kind,
            playlistSignature,
            source,
            settings,
            requestedPath,
            generation,
            cancellation);
    }

    private async Task LoadImageAsync(
        ImageLoadKind kind,
        string playlistSignature,
        ImageSourceFrame? currentSource,
        AppSettings settings,
        string? requestedPath,
        int generation,
        CancellationTokenSource cancellation)
    {
        ImageLoadResult? result = null;
        bool gateEntered = false;
        try
        {
            await _imageLoadGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            try
            {
                result = await Task.Factory.StartNew(
                    () => LoadImageAtLowPriority(
                        kind,
                        currentSource,
                        settings,
                        requestedPath,
                        cancellation.Token),
                    cancellation.Token,
                    // Fast cached cycles must reuse the worker pool rather
                    // than create a fresh operating-system thread per image.
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
            finally
            {
                if (gateEntered)
                    _imageLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            result = null;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingImageLoads);
            Interlocked.CompareExchange(ref _imageLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }

        if (_disposed
            || generation != Volatile.Read(ref _imageLoadGeneration)
            || !_settings.ImageMode
            || !string.Equals(
                playlistSignature,
                _settings.ImagePlaylistSignature(),
                StringComparison.Ordinal))
            return;
        if (kind == ImageLoadKind.Reprocess && result is null)
            return;
        if (result is not null
            && kind is ImageLoadKind.Reload or ImageLoadKind.Next
            && !IsEnabledImagePath(_settings, result.Source.Path))
        {
            // Final guard against a stale asynchronous selection: normal
            // cycling may never commit a row that has since been disabled.
            return;
        }

        ApplyLoadedImage(
            result?.Source,
            result?.Prepared,
            resetCycle: kind != ImageLoadKind.Reprocess);
    }

    private ImageLoadResult? LoadImageAtLowPriority(
        ImageLoadKind kind,
        ImageSourceFrame? currentSource,
        AppSettings settings,
        string? requestedPath,
        CancellationToken cancellationToken)
    {
        Thread thread = Thread.CurrentThread;
        ThreadPriority previousPriority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.Lowest;
            cancellationToken.ThrowIfCancellationRequested();
            ImageSourceFrame? source = kind switch
            {
                ImageLoadKind.Reload => _images.Reload(
                    settings.ActiveImagePlaylist().Entries,
                    currentSource?.Path),
                ImageLoadKind.Next => _images.MoveNext(
                    settings.ActiveImagePlaylist().Entries,
                    currentSource?.Path),
                ImageLoadKind.Specific when !string.IsNullOrWhiteSpace(requestedPath) =>
                    _images.Select(settings.ActiveImagePlaylist().Entries, requestedPath),
                _ => currentSource
            };
            if (source is null)
                return null;
            PreparedImage prepared = _imagePreparation.Prepare(
                source,
                settings,
                _images.TargetWidth,
                _images.TargetHeight,
                cancellationToken);
            return new ImageLoadResult(source, prepared);
        }
        finally
        {
            thread.Priority = previousPriority;
        }
    }

    private void ApplyLoadedImage(
        ImageSourceFrame? source,
        PreparedImage? image,
        bool resetCycle)
    {
        _currentImageSource = source;
        _currentImage = image;
        if (resetCycle)
            _imageStartedAt = DateTime.UtcNow;
        PublishDatabaseImages();
    }

    private static bool IsEnabledImagePath(AppSettings settings, string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        return settings.ActiveImagePlaylist().Entries.Any(entry =>
        {
            if (!entry.Enabled)
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(entry.Path),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }

    private void RefreshSecondaryDatabaseChannels(bool forceReload)
    {
        IReadOnlyList<MonitorDescriptor> monitors = MonitorCatalog.Capture();
        AppSettings topology = _settings.Copy();
        MonitorTopology.EnsureProfiles(topology, monitors);
        IReadOnlyList<MonitorRoute> routes = MonitorTopology.Resolve(
            topology.MonitorProfiles,
            monitors,
            MonitorRouteDomain.Database);
        Dictionary<string, MonitorRoute> routeByMonitor = routes.ToDictionary(
            route => route.MonitorId,
            StringComparer.OrdinalIgnoreCase);
        MonitorDescriptor? primary = monitors.FirstOrDefault(monitor =>
            monitor.Primary);
        _primaryDatabaseRoot = primary is not null
            && routeByMonitor.TryGetValue(primary.Id, out MonitorRoute? primaryRoute)
            && primaryRoute.Mode != MonitorLinkMode.Disabled
                ? primaryRoute.RootMonitorId
                : "";

        HashSet<string> desiredRoots = routes
            .Where(route =>
                route.Mode != MonitorLinkMode.Disabled
                && !string.Equals(
                    route.RootMonitorId,
                    _primaryDatabaseRoot,
                    StringComparison.OrdinalIgnoreCase))
            .Select(route => route.RootMonitorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string removed in _secondaryDatabaseChannels.Keys
                     .Where(root => !desiredRoots.Contains(root))
                     .ToArray())
        {
            _secondaryDatabaseChannels[removed].Cancel();
            _secondaryDatabaseChannels.Remove(removed);
        }

        foreach (string root in desiredRoots)
        {
            MonitorProfile? profile = MonitorTopology.Find(
                topology.MonitorProfiles,
                root);
            if (profile is null)
                continue;
            AppSettings nextSettings =
                profile.Settings.Copy(includeMonitorProfiles: false);
            nextSettings.Normalize(includeMonitorProfiles: false);
            if (!_secondaryDatabaseChannels.TryGetValue(
                    root,
                    out DatabaseImageChannel? channel))
            {
                (int width, int height) =
                    _output.DatabaseTargetSize(root);
                channel = new DatabaseImageChannel(root, nextSettings)
                {
                    Sequence =
                    {
                        TargetWidth = width,
                        TargetHeight = height
                    }
                };
                _secondaryDatabaseChannels[root] = channel;
                if (nextSettings.ImageMode)
                    QueueSecondaryImageLoad(channel, ImageLoadKind.Reload);
                continue;
            }

            AppSettings previousSettings = channel.Settings;
            (int nextWidth, int nextHeight) =
                _output.DatabaseTargetSize(root);
            bool targetChanged =
                channel.Sequence.TargetWidth != nextWidth
                || channel.Sequence.TargetHeight != nextHeight;
            channel.Sequence.TargetWidth = nextWidth;
            channel.Sequence.TargetHeight = nextHeight;
            bool reload = forceReload
                || nextSettings.ImageMode != previousSettings.ImageMode
                || !string.Equals(
                    nextSettings.ImagePlaylistSignature(),
                    previousSettings.ImagePlaylistSignature(),
                    StringComparison.Ordinal);
            bool reprocess =
                !AppSettingsComparer.ImagePreparationEquivalent(
                    nextSettings,
                    previousSettings)
                || targetChanged;
            channel.Settings = nextSettings;
            if (!nextSettings.ImageMode)
            {
                channel.Cancel();
                channel.Source = null;
                channel.Prepared = null;
                continue;
            }
            if (reload)
                QueueSecondaryImageLoad(channel, ImageLoadKind.Reload);
            else if (reprocess && channel.Source is not null)
                QueueSecondaryImageLoad(channel, ImageLoadKind.Reprocess);
        }
        PublishDatabaseImages();
        UpdateImageTimerInterval();
    }

    private void QueueSecondaryImageLoad(
        DatabaseImageChannel channel,
        ImageLoadKind kind,
        string? requestedPath = null)
    {
        int generation = channel.NextGeneration();
        CancellationTokenSource cancellation = channel.ReplaceCancellation();
        AppSettings settings = channel.Settings.Copy(
            includeMonitorProfiles: false);
        ImageSourceFrame? currentSource = channel.Source;
        Interlocked.Increment(ref _pendingImageLoads);
        _ = LoadSecondaryImageAsync(
            channel,
            kind,
            currentSource,
            settings,
            requestedPath,
            generation,
            cancellation);
    }

    private async Task LoadSecondaryImageAsync(
        DatabaseImageChannel channel,
        ImageLoadKind kind,
        ImageSourceFrame? currentSource,
        AppSettings settings,
        string? requestedPath,
        int generation,
        CancellationTokenSource cancellation)
    {
        ImageLoadResult? result = null;
        bool gateEntered = false;
        try
        {
            await _imageLoadGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            result = await Task.Factory.StartNew(
                () => LoadSecondaryImageAtLowPriority(
                    channel,
                    kind,
                    currentSource,
                    settings,
                    requestedPath,
                    cancellation.Token),
                cancellation.Token,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(
                $"Не удалось подготовить образ для устройства {channel.RootMonitorId}.",
                exception);
        }
        finally
        {
            if (gateEntered)
                _imageLoadGate.Release();
            Interlocked.Decrement(ref _pendingImageLoads);
            channel.ReleaseCancellation(cancellation);
            cancellation.Dispose();
        }

        if (_disposed
            || generation != channel.Generation
            || !channel.Settings.ImageMode)
        {
            return;
        }
        if (kind == ImageLoadKind.Reprocess && result is null)
            return;
        if (result is not null
            && kind is ImageLoadKind.Reload or ImageLoadKind.Next
            && !IsEnabledImagePath(channel.Settings, result.Source.Path))
        {
            return;
        }

        channel.Source = result?.Source;
        channel.Prepared = result?.Prepared;
        if (kind != ImageLoadKind.Reprocess)
            channel.StartedAtUtc = DateTime.UtcNow;
        PublishDatabaseImages();
    }

    private ImageLoadResult? LoadSecondaryImageAtLowPriority(
        DatabaseImageChannel channel,
        ImageLoadKind kind,
        ImageSourceFrame? currentSource,
        AppSettings settings,
        string? requestedPath,
        CancellationToken cancellationToken)
    {
        Thread thread = Thread.CurrentThread;
        ThreadPriority previousPriority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.Lowest;
            cancellationToken.ThrowIfCancellationRequested();
            ImageSourceFrame? source = kind switch
            {
                ImageLoadKind.Reload => channel.Sequence.Reload(
                    settings.ActiveImagePlaylist().Entries,
                    currentSource?.Path),
                ImageLoadKind.Next => channel.Sequence.MoveNext(
                    settings.ActiveImagePlaylist().Entries,
                    currentSource?.Path),
                ImageLoadKind.Specific when
                    !string.IsNullOrWhiteSpace(requestedPath) =>
                    channel.Sequence.Select(
                        settings.ActiveImagePlaylist().Entries,
                        requestedPath),
                _ => currentSource
            };
            if (source is null)
                return null;
            PreparedImage prepared = _imagePreparation.Prepare(
                source,
                settings,
                channel.Sequence.TargetWidth,
                channel.Sequence.TargetHeight,
                cancellationToken);
            return new ImageLoadResult(source, prepared);
        }
        finally
        {
            thread.Priority = previousPriority;
        }
    }

    private void PublishDatabaseImages()
    {
        Dictionary<string, PreparedImage?> images =
            new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_primaryDatabaseRoot))
        {
            images[_primaryDatabaseRoot] =
                _settings.ImageMode ? _currentImage : null;
        }
        foreach (DatabaseImageChannel channel in
                 _secondaryDatabaseChannels.Values)
        {
            images[channel.RootMonitorId] = channel.Settings.ImageMode
                ? channel.Prepared
                : null;
        }
        _output.SetDatabaseImages(images);
    }

    private void OnImageTimer(object? sender, EventArgs e)
    {
        if (IsPaused
            || _attackStartPending
            || Volatile.Read(ref _pendingImageLoads) > 0)
            return;

        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _imageStartedAt).TotalSeconds;
        if (_settings.ImageMode
            && _currentImage is not null
            && elapsed >= _settings.ImageDurationSeconds)
        {
            NextImage();
        }
        foreach (DatabaseImageChannel channel in
                 _secondaryDatabaseChannels.Values)
        {
            if (!channel.Settings.ImageMode
                || channel.Prepared is null
                || channel.IsLoading
                || (now - channel.StartedAtUtc).TotalSeconds
                    < channel.Settings.ImageDurationSeconds)
            {
                continue;
            }
            QueueSecondaryImageLoad(channel, ImageLoadKind.Next);
        }
    }

    private void UpdateImageTimerInterval()
    {
        // Poll four times within the shortest requested cycle while retaining
        // the former low wake-up rate for ordinary multi-second cycles.
        double shortestCycle = _secondaryDatabaseChannels.Values
            .Where(channel => channel.Settings.ImageMode)
            .Select(channel => channel.Settings.ImageDurationSeconds)
            .Append(_settings.ImageMode
                ? _settings.ImageDurationSeconds
                : AppSettings.MaximumImageDurationSeconds)
            .Min();
        double milliseconds = Math.Clamp(
            shortestCycle * 250.0,
            25.0,
            120.0);
        bool restart = _imageTimer.IsEnabled;
        if (restart)
            _imageTimer.Stop();
        _imageTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        if (restart)
            _imageTimer.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        });
    }

    private void OnSessionSwitch(
        object sender,
        SessionSwitchEventArgs e)
    {
        bool unavailable = e.Reason is
            SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect;
        bool available = e.Reason is
            SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect;
        if (!unavailable && !available)
            return;

        DispatchSystemEvent(() =>
        {
            if (unavailable)
                SuspendForSession($"SESSION:{e.Reason}");
            else
                ScheduleSessionResume($"SESSION:{e.Reason}");
        });
    }

    private void OnPowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Suspend or PowerModes.Resume))
            return;
        DispatchSystemEvent(() =>
        {
            if (e.Mode == PowerModes.Suspend)
                SuspendForSession("POWER:SUSPEND");
            else
                ScheduleSessionResume("POWER:RESUME");
        });
    }

    private void DispatchSystemEvent(Action action)
    {
        if (_disposed)
            return;
        System.Windows.Application? application =
            System.Windows.Application.Current;
        if (application?.Dispatcher is null)
            return;
        if (application.Dispatcher.CheckAccess())
            action();
        else
            application.Dispatcher.BeginInvoke(action);
    }

    private void SuspendForSession(string reason)
    {
        if (_disposed)
            return;
        _sessionResumeTimer.Stop();
        _sessionResumeAttempts = 0;
        if (_sessionUnavailable)
            return;

        _sessionUnavailable = true;
        AbortAttackImmediately();
        DiagnosticLog.Write(
            $"Сеанс приостановлен ({reason}); поверхность Direct3D уничтожается.");
        _output.StopAndRestoreDesktop();
        SetRuntimeStatus(
            "ВЫВОД ПРИОСТАНОВЛЕН // СЕАНС WINDOWS НЕДОСТУПЕН",
            isError: false);
        PauseStateChanged?.Invoke();
    }

    private void ScheduleSessionResume(string reason)
    {
        if (_disposed)
            return;
        DiagnosticLog.Write(
            $"Сеанс доступен ({reason}); ожидается готовность Explorer.");
        _sessionResumeTimer.Stop();
        _sessionResumeTimer.Start();
    }

    private void OnSessionResumeTimer(object? sender, EventArgs e)
    {
        _sessionResumeTimer.Stop();
        if (_disposed)
            return;
        if (!_sessionUnavailable)
            return;
        if (_manualPaused)
        {
            _sessionUnavailable = false;
            SetRuntimeStatus(
                "ВЫВОД ОСТАНОВЛЕН // РАБОЧИЙ СТОЛ ВОССТАНОВЛЕН",
                isError: false);
            PauseStateChanged?.Invoke();
            return;
        }

        try
        {
            _output.Start(_settings, _currentImage);
            if (_fullscreenPaused)
                _output.Suspend();
            UpdateImageTargetSize();
            _displayTopologySignature = CaptureDisplayTopology();
            _sessionUnavailable = false;
            _sessionResumeAttempts = 0;
            SetRuntimeStatus(
                $"ВЫВОД ВОССТАНОВЛЕН // DIRECT3D 11 // ЭКРАНОВ: {_output.WindowCount}",
                isError: false);
            PauseStateChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _output.StopAndRestoreDesktop();
            _sessionResumeAttempts++;
            DiagnosticLog.Write(
                $"Попытка восстановления после разблокировки "
                + $"{_sessionResumeAttempts}/4 не удалась.",
                exception);
            if (_sessionResumeAttempts < 4)
            {
                _sessionResumeTimer.Interval =
                    TimeSpan.FromMilliseconds(700 + 500 * _sessionResumeAttempts);
                _sessionResumeTimer.Start();
                SetRuntimeStatus(
                    $"ОЖИДАНИЕ EXPLORER // ПОПЫТКА {_sessionResumeAttempts}/4",
                    isError: false);
            }
            else
            {
                _sessionUnavailable = false;
                ReportWindowFailure(
                    "Не удалось восстановить вывод после разблокировки Windows.",
                    exception,
                    fatal: true);
            }
        }
    }

    private void OnDisplayChangeTimer(object? sender, EventArgs e)
    {
        _displayChangeTimer.Stop();
        string currentTopology = CaptureDisplayTopology();
        if (string.Equals(
                currentTopology,
                _displayTopologySignature,
                StringComparison.Ordinal))
        {
            DiagnosticLog.Write(
                "Событие изменения экранов проигнорировано: топология не изменилась.");
            return;
        }

        DiagnosticLog.Write(
            $"Изменилась топология экранов: {_displayTopologySignature} -> {currentTopology}.");
        _displayTopologySignature = currentTopology;
        RefreshWindows();
    }

    private static string CaptureDisplayTopology() =>
        string.Join(
            ";",
            System.Windows.Forms.Screen.AllScreens
                .OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(screen =>
                    $"{screen.DeviceName}|{screen.Bounds.Left},{screen.Bounds.Top},"
                    + $"{screen.Bounds.Width},{screen.Bounds.Height}|"
                    + $"{screen.WorkingArea.Left},{screen.WorkingArea.Top},"
                    + $"{screen.WorkingArea.Width},{screen.WorkingArea.Height}|"
                    + $"{screen.Primary}"));

    private void OnFullscreenActivityChanged(bool active, string? processName)
    {
        if (_disposed)
            return;
        DiagnosticLog.Write(
            active
                ? $"Пауза для полноэкранного приложения: "
                    + $"{processName ?? "неизвестный процесс"}."
                : "Полноэкранное приложение закрыто или свёрнуто; "
                    + "вывод возобновляется.");
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            () => SetFullscreenPaused(active));
    }

    private void OnAttackTimer(object? sender, EventArgs e)
    {
        if (_disposed
            || !_settings.AttackSystemEnabled
            || IsPaused
            || IsAttackActive
            || !_output.IsRunning
            || DateTime.UtcNow < _attackRetryAfterUtc)
        {
            return;
        }

        TimeSpan threshold =
            TimeSpan.FromMinutes(_settings.AttackIdleMinutes);
        if (UserIdleMonitor.IdleTime >= threshold)
            BeginAttack(manual: false);
    }

    private void BeginAttack(bool manual)
    {
        if (_disposed
            || IsPaused
            || _attackStartPending
            || _attackOverlay is not null
            || !_output.IsRunning)
        {
            return;
        }

        _attackStartPending = true;
        AppSettings settings = _settings.Copy();
        try
        {
            if (_disposed
                || IsPaused
                || !_output.IsRunning
                || (!manual
                    && UserIdleMonitor.IdleTime
                        < TimeSpan.FromMinutes(
                            settings.AttackIdleMinutes)))
            {
                return;
            }

            AttackFrameSnapshot? frame = _output.CaptureAttackFrame();
            if (frame is null)
            {
                throw new InvalidOperationException(
                    "Общий кадр потока недоступен.");
            }

            System.Drawing.Rectangle bounds =
                System.Windows.Forms.SystemInformation.VirtualScreen;
            AttackOverlayWindow overlay = new(
                bounds,
                frame,
                _settings.AttackTransitionSeconds);
            overlay.Closed += OnAttackOverlayClosed;
            overlay.ExitStarted += OnAttackExitStarted;
            _attackOverlay = overlay;
            _imageStartedAt = DateTime.UtcNow;
            overlay.Start();
            SetRuntimeStatus(
                "АТАКА СИСТЕМЫ // ИНТЕРФЕЙС ПЕРЕХВАЧЕН ПОТОКОМ",
                isError: false);
        }
        catch (Exception exception)
        {
            AbortAttackImmediately();
            _attackRetryAfterUtc =
                DateTime.UtcNow.AddMinutes(1);
            ReportWindowFailure(
                "Не удалось начать АТАКУ СИСТЕМЫ.",
                exception,
                fatal: false);
        }
        finally
        {
            _attackStartPending = false;
        }
    }

    private void OnAttackOverlayClosed()
    {
        System.Windows.Application? application =
            System.Windows.Application.Current;
        if (application?.Dispatcher is null)
            return;
        application.Dispatcher.BeginInvoke(FinishAttack);
    }

    private void OnAttackExitStarted()
    {
        System.Windows.Application? application =
            System.Windows.Application.Current;
        if (application?.Dispatcher is null)
            return;
        application.Dispatcher.BeginInvoke(
            RestoreWallpaperAfterAttack);
    }

    private void RestoreWallpaperAfterAttack()
    {
        if (_disposed || !_output.IsRunning)
            return;
        _output.UpdateSettings(_settings);
        PublishDatabaseImages();
    }

    private void FinishAttack()
    {
        AttackOverlayWindow? overlay = _attackOverlay;
        if (overlay is null)
            return;
        overlay.Closed -= OnAttackOverlayClosed;
        overlay.ExitStarted -= OnAttackExitStarted;
        _attackOverlay = null;
        overlay.Dispose();
        if (_disposed || !_output.IsRunning)
            return;

        RestoreWallpaperAfterAttack();
        SetRuntimeStatus(
            $"ВЫВОД АКТИВЕН // DIRECT3D 11 // ЭКРАНОВ: "
                + $"{_output.WindowCount}",
            isError: false);
    }

    private void AbortAttackImmediately()
    {
        AttackOverlayWindow? overlay = _attackOverlay;
        if (overlay is not null)
        {
            overlay.Closed -= OnAttackOverlayClosed;
            overlay.ExitStarted -= OnAttackExitStarted;
            _attackOverlay = null;
            overlay.RequestExit(immediate: true);
            overlay.WaitForClose(TimeSpan.FromMilliseconds(700));
            overlay.Dispose();
        }

        if (!_disposed && _output.IsRunning)
        {
            _output.UpdateSettings(_settings);
            PublishDatabaseImages();
        }
    }

    private void ReportWindowFailure(string context, Exception exception, bool fatal)
    {
        DiagnosticLog.Write(context, exception);
        void Publish()
        {
            if (fatal)
                _output.StopAndRestoreDesktop();
            SetRuntimeStatus(
                $"{(fatal ? "ОШИБКА ВЫВОДА" : "НАСТРОЙКА ОТКЛОНЕНА")} "
                + $"// {context} // {exception.GetBaseException().Message}",
                isError: true);
        }

        System.Windows.Application? application = System.Windows.Application.Current;
        if (application?.Dispatcher is null
            || application.Dispatcher.CheckAccess())
        {
            Publish();
        }
        else
        {
            application.Dispatcher.BeginInvoke(Publish);
        }
    }

    private void SetRuntimeStatus(string status, bool isError)
    {
        _runtimeStatus = status;
        _hasRuntimeError = isError;
        DiagnosticLog.Write(status);
        RuntimeStatusChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Increment(ref _imageLoadGeneration);
        Interlocked.Exchange(ref _imageLoadCancellation, null)?.Cancel();
        foreach (DatabaseImageChannel channel in
                 _secondaryDatabaseChannels.Values)
        {
            channel.Cancel();
        }
        _secondaryDatabaseChannels.Clear();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _fullscreenMonitor.ActivityChanged -= OnFullscreenActivityChanged;
        _fullscreenMonitor.Dispose();
        _imageTimer.Stop();
        _attackTimer.Stop();
        _displayChangeTimer.Stop();
        _sessionResumeTimer.Stop();
        AbortAttackImmediately();
        _imagePreparation.Clear();
        _output.Dispose();
    }

    private enum ImageLoadKind
    {
        Reload,
        Next,
        Specific,
        Reprocess
    }

    private sealed record ImageLoadResult(
        ImageSourceFrame Source,
        PreparedImage Prepared);

    private sealed class DatabaseImageChannel
    {
        private CancellationTokenSource? _cancellation;

        public string RootMonitorId { get; }
        public ImageSequenceService Sequence { get; set; } = new();
        public AppSettings Settings { get; set; }
        public ImageSourceFrame? Source { get; set; }
        public PreparedImage? Prepared { get; set; }
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public int Generation { get; private set; }
        public bool IsLoading => _cancellation is not null;

        public DatabaseImageChannel(
            string rootMonitorId,
            AppSettings settings)
        {
            RootMonitorId = rootMonitorId;
            Settings = settings;
        }

        public int NextGeneration() =>
            ++Generation;

        public CancellationTokenSource ReplaceCancellation()
        {
            CancellationTokenSource next = new();
            CancellationTokenSource? previous =
                Interlocked.Exchange(ref _cancellation, next);
            previous?.Cancel();
            previous?.Dispose();
            return next;
        }

        public void ReleaseCancellation(
            CancellationTokenSource cancellation) =>
            Interlocked.CompareExchange(
                ref _cancellation,
                null,
                cancellation);

        public void Cancel()
        {
            Generation++;
            CancellationTokenSource? current =
                Interlocked.Exchange(ref _cancellation, null);
            current?.Cancel();
            current?.Dispose();
        }
    }
}
