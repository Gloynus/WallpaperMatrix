using System.Diagnostics;
using System.Runtime.InteropServices;
using WallpaperMatrix.Models;
using WallpaperMatrix.Rendering;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Native;

/// <summary>
/// Owns one native wallpaper window and its render loop on a background thread.
/// It never participates in WPF input, focus, or layout processing.
/// </summary>
internal sealed class NativeWallpaperWindow : IDisposable
{
    private readonly DrawingRectangle _bounds;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _renderingEnabled = new(true);
    private readonly object _commandLock = new();
    private readonly object _runtimeLock = new();
    private readonly AppSettings _initialSettings;
    private readonly bool _animateStartupReveal;
    private readonly Action<string, Exception, bool>? _failureHandler;
    private MonitorOutputPlan? _pendingPlan;
    private PreparedImage? _pendingImage;
    private IReadOnlyDictionary<string, PreparedImage?>? _pendingDatabaseImages;
    private IReadOnlyDictionary<string, PreparedImage?> _activeDatabaseImages =
        new Dictionary<string, PreparedImage?>();
    private AttackInterfaceCaptureRequest? _pendingAttackInterfaceCapture;
    private AttackCompositionRequest? _pendingAttackComposition;
    private bool _stopAttackCompositionPending;
    private AttackComposition? _attackComposition;
    private PreparedImage? _activeImage;
    private MonitorOutputPlan _currentPlan;
    private bool _hasPendingImage;
    private bool _resetPendingImageOverlay;
    private IntPtr _window;
    private readonly List<SceneRuntime> _sceneRuntimes = [];
    private Direct3D11Presenter? _direct3DPresenter;
    private Exception? _startupError;
    private long _lastPresentUtcTicks;
    private int _renderFaulted;
    private int _paused;
    private bool _disposed;
    private bool _synchronizationDisposed;

    public SharedMatrixScene SharedFrame
    {
        get
        {
            lock (_runtimeLock)
            {
                return _sceneRuntimes.FirstOrDefault()?.Scene
                    ?? throw new InvalidOperationException(
                        "Общий кадр ещё не создан.");
            }
        }
    }

    public AttackFrameSnapshot BeginAttackComposition(
        AttackInterfaceFrame? interfaceFrame,
        double transitionSeconds)
    {
        AttackCompositionRequest request = new(
            interfaceFrame,
            transitionSeconds);
        lock (_commandLock)
        {
            _pendingAttackComposition = request;
            _stopAttackCompositionPending = false;
        }
        if (!request.Completed.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException(
                "Поток обоев не подготовил симуляцию АТАКИ СИСТЕМЫ.");
        }
        if (request.Error is not null)
        {
            throw new InvalidOperationException(
                "Не удалось подготовить симуляцию АТАКИ СИСТЕМЫ.",
                request.Error);
        }
        return request.Snapshot
            ?? throw new InvalidOperationException(
                "Симуляция АТАКИ СИСТЕМЫ не вернула кадр.");
    }

    public AttackInterfaceFrame CaptureAttackInterface()
    {
        AttackInterfaceCaptureRequest request = new();
        lock (_commandLock)
        {
            if (_pendingAttackInterfaceCapture is not null)
            {
                throw new InvalidOperationException(
                    "Снимок интерфейса АТАКИ уже выполняется.");
            }
            _pendingAttackInterfaceCapture = request;
        }
        if (!request.Completed.Wait(TimeSpan.FromSeconds(5)))
        {
            lock (_commandLock)
            {
                if (ReferenceEquals(_pendingAttackInterfaceCapture, request))
                    _pendingAttackInterfaceCapture = null;
            }
            throw new TimeoutException(
                "Поток обоев не подготовил визуальную карту интерфейса.");
        }
        if (request.Error is not null)
        {
            throw new InvalidOperationException(
                "Не удалось отделить интерфейс Windows от живых обоев.",
                request.Error);
        }
        return request.Frame
            ?? throw new InvalidOperationException(
                "Поток обоев не вернул визуальную карту интерфейса.");
    }

    public void EndAttackComposition()
    {
        lock (_commandLock)
        {
            _pendingAttackComposition = null;
            _stopAttackCompositionPending = true;
        }
    }

    public MatrixScenePresentation? CaptureVirtualOutputFrame(
        string monitorId)
    {
        lock (_runtimeLock)
        {
            MonitorScenePlan? scenePlan = _currentPlan.Scenes
                .FirstOrDefault(candidate =>
                    candidate.Targets.Any(target => string.Equals(
                        target.MonitorId,
                        monitorId,
                        StringComparison.OrdinalIgnoreCase)));
            if (scenePlan is null)
                return null;
            MonitorSceneTarget? target = scenePlan.Targets
                .FirstOrDefault(candidate => string.Equals(
                    candidate.MonitorId,
                    monitorId,
                    StringComparison.OrdinalIgnoreCase));
            SceneRuntime? runtime = _sceneRuntimes
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    scenePlan.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (target is null || runtime is null)
                return null;

            return new MatrixScenePresentation(
                runtime.Scene,
                new DrawingRectangle(
                    0,
                    0,
                    Math.Max(1, target.TargetBounds.Width),
                    Math.Max(1, target.TargetBounds.Height)),
                target.SourceBounds);
        }
    }

    public NativeWallpaperWindow(
        MonitorOutputPlan plan,
        AppSettings settings,
        bool animateStartupReveal,
        IReadOnlyDictionary<string, PreparedImage?>? initialDatabaseImages = null,
        Action<string, Exception, bool>? failureHandler = null)
    {
        _bounds = plan.VirtualBounds;
        _currentPlan = plan;
        _initialSettings = settings.Copy();
        if (initialDatabaseImages is not null)
        {
            _activeDatabaseImages =
                new Dictionary<string, PreparedImage?>(
                    initialDatabaseImages,
                    StringComparer.OrdinalIgnoreCase);
        }
        _animateStartupReveal = animateStartupReveal;
        _failureHandler = failureHandler;
        _thread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = $"Wallpaper compositor {_bounds.Width}x{_bounds.Height}",
            Priority = ThreadPriority.Lowest
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(8)))
            throw new TimeoutException("Не удалось запустить поток отрисовки рабочего стола.");
        if (_startupError is not null)
            throw new InvalidOperationException("Не удалось создать слой живых обоев.", _startupError);
    }

    public void UpdateSettings(MonitorOutputPlan plan)
    {
        lock (_commandLock)
            _pendingPlan = plan;
    }

    public void SetImage(PreparedImage? image)
    {
        lock (_commandLock)
        {
            _pendingImage = image;
            _hasPendingImage = true;
        }
    }

    public void SetDatabaseImages(
        IReadOnlyDictionary<string, PreparedImage?> images)
    {
        lock (_commandLock)
            _pendingDatabaseImages = new Dictionary<string, PreparedImage?>(
                images,
                StringComparer.OrdinalIgnoreCase);
    }

    public void ResetImageOverlay(PreparedImage? image)
    {
        lock (_commandLock)
        {
            _pendingImage = image;
            _hasPendingImage = true;
            _resetPendingImageOverlay = true;
        }
    }

    public void SetPaused(bool paused)
    {
        SetPausedCore(paused, changeVisibility: true);
    }

    public void SetSimulationPaused(bool paused)
    {
        SetPausedCore(paused, changeVisibility: false);
    }

    public void SetMotionScale(double scale)
    {
        lock (_runtimeLock)
        {
            foreach (SceneRuntime runtime in _sceneRuntimes)
                runtime.Renderer.SetMotionScale(scale);
        }
    }

    private void SetPausedCore(bool paused, bool changeVisibility)
    {
        Volatile.Write(ref _paused, paused ? 1 : 0);
        if (paused)
        {
            _renderingEnabled.Reset();
            if (changeVisibility)
            {
                IntPtr window = _window;
                if (window != IntPtr.Zero)
                    NativeWindow.ShowWindowAsync(window, NativeWindow.ShowHide);
            }
        }
        else
        {
            if (changeVisibility)
            {
                IntPtr window = _window;
                if (window != IntPtr.Zero)
                    NativeWindow.ShowWindowAsync(window, NativeWindow.ShowNoActivate);
            }
            _renderingEnabled.Set();
        }
    }

    private void RenderThreadMain()
    {
        try
        {
            NativeWindow.EnsureClassRegistered();
            _window = NativeWindow.Create(_bounds);
            if (_window == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx завершился с кодом {Marshal.GetLastWin32Error()}.");

            if (!DesktopHost.Attach(_window, _bounds))
            {
                throw new InvalidOperationException(
                    "Explorer не предоставил безопасную поверхность под значками "
                    + "рабочего стола.");
            }
            foreach (MonitorScenePlan scenePlan in _currentPlan.Scenes)
            {
                _sceneRuntimes.Add(CreateSceneRuntime(
                    scenePlan,
                    seedInitialStreams:
                        scenePlan.IsFlowMaster && !_animateStartupReveal));
            }
            if (_sceneRuntimes.Count == 0)
                _sceneRuntimes.Add(CreateDisabledRuntime());
            ConfigureFlowSharing(_sceneRuntimes);
            _sceneRuntimes.Sort(static (left, right) =>
                right.IsFlowMaster.CompareTo(left.IsFlowMaster));
            _direct3DPresenter = Direct3D11Presenter.Create(
                _window,
                _bounds.Width,
                _bounds.Height,
                SharedFrame,
                // A permanently alpha-capable surface lets an individual
                // monitor be enabled or disabled without rebuilding the
                // native window and flashing the static wallpaper.
                transparentSurface: true);
            if (_animateStartupReveal)
                _direct3DPresenter.SetSurfaceReveal(0, 1);
            NativeWindow.ShowWindow(_window, NativeWindow.ShowNoActivate);
            NativeWindow.UpdateWindow(_window);
            long presentedVersion = long.MinValue;
            bool nonEmptyFrameConfirmed = false;
            PresentLatestFrame(
                _currentPlan,
                ref presentedVersion);
            DiagnosticLog.Write(
                $"Первый кадр передан: renderer=0x{_window.ToInt64():X}; "
                + $"compositor=True; scenes={_currentPlan.Scenes.Count}; "
                + $"viewports={_currentPlan.ActiveMonitorCount}; "
                + $"frameVersion={presentedVersion}; "
                + $"instances={TotalInstanceCount()}; "
                + $"surface={_bounds.Width}x{_bounds.Height}.");
            _started.Set();
            Stopwatch sharedClock = Stopwatch.StartNew();
            MonitorOutputPlan activePlan = _currentPlan;
            bool startupRevealCompleted = !_animateStartupReveal;
            double startupRevealDurationSeconds = Math.Clamp(
                _initialSettings.ImageDurationSeconds,
                AppSettings.MinimumImageDurationSeconds,
                AppSettings.MaximumImageDurationSeconds);

            while (!_cancellation.IsCancellationRequested)
            {
                if (Volatile.Read(ref _paused) != 0)
                {
                    TimeSpan pausedAt = sharedClock.Elapsed;
                    foreach (SceneRuntime runtime in _sceneRuntimes)
                        runtime.Renderer.RenderIfDue(
                            paused: true,
                            pausedAt);
                    try
                    {
                        _renderingEnabled.Wait(_cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                while (NativeWindow.PeekMessage(out NativeWindow.NativeMessage message, IntPtr.Zero, 0, 0, NativeWindow.RemoveMessage))
                {
                    if (message.Message == NativeWindow.QuitMessage)
                    {
                        if (!_cancellation.IsCancellationRequested)
                        {
                            throw new InvalidOperationException(
                                "Explorer уничтожил окно вывода рабочего стола.");
                        }
                        return;
                    }
                    NativeWindow.TranslateMessage(ref message);
                    NativeWindow.DispatchMessage(ref message);
                }
                DesktopHost.MaintainDesktopPlacement(_window);

                int waitMilliseconds;
                if (_sceneRuntimes.Count > 0)
                {
                    activePlan = ApplyPendingCommands(
                        activePlan,
                        ref presentedVersion);

                    // The desktop duplication frame represents the scene that
                    // DWM is displaying now. Capture against that same scene
                    // before RenderIfDue advances the simulation; otherwise a
                    // legitimate stream step would be mistaken for Windows UI.
                    AttackInterfaceCaptureRequest? interfaceCapture =
                        PeekAttackInterfaceCaptureRequest();
                    bool interfaceCapturePrepared =
                        interfaceCapture is not null
                        && _direct3DPresenter?.PrepareAttackInterfaceCapture(
                            _bounds,
                            _bounds.Width,
                            _bounds.Height) == true;
                    if (interfaceCapture is not null
                        && interfaceCapturePrepared)
                    {
                        interfaceCapture =
                            TakeAttackInterfaceCaptureRequest();
                        if (interfaceCapture is not null)
                        {
                            CompleteAttackInterfaceCapture(
                                interfaceCapture,
                                activePlan,
                                ref presentedVersion);
                        }
                    }

                    TimeSpan now = sharedClock.Elapsed;
                    foreach (SceneRuntime runtime in _sceneRuntimes)
                        runtime.Renderer.RenderIfDue(
                            paused: false,
                            now);
                    UpdateAttackComposition(now);
                    waitMilliseconds = _sceneRuntimes
                        .Min(runtime =>
                            runtime.Renderer.RecommendedWaitMilliseconds(
                                paused: false));
                }
                else
                    waitMilliseconds = 25;
                if (PeekAttackInterfaceCaptureRequest() is not null)
                {
                    // Warm-up is independent of the configured stream rate:
                    // repeat the unchanged wallpaper frame while DWM and each
                    // duplication output establish their common phase.
                    presentedVersion = long.MinValue;
                    waitMilliseconds = Math.Min(waitMilliseconds, 16);
                }
                if (!startupRevealCompleted)
                {
                    double progress = Math.Clamp(
                        sharedClock.Elapsed.TotalSeconds
                            / startupRevealDurationSeconds,
                        0,
                        1);
                    _direct3DPresenter?.SetSurfaceReveal(
                        progress,
                        1);
                    presentedVersion = long.MinValue;
                    startupRevealCompleted = progress >= 1;
                    if (startupRevealCompleted)
                    {
                        DiagnosticLog.Write(
                            "Запуск обоев завершил плавное растворение "
                            + "системного фона в поток: "
                            + $"{startupRevealDurationSeconds:0.##} с.");
                    }
                }
                PresentLatestFrame(
                    activePlan,
                    ref presentedVersion);
                if (!nonEmptyFrameConfirmed
                    && TotalInstanceCount() > 0
                    && presentedVersion >= 0)
                {
                    nonEmptyFrameConfirmed = true;
                    DiagnosticLog.Write(
                        $"Первый непустой кадр подтверждён: "
                        + $"renderer=0x{_window.ToInt64():X}; "
                        + $"frameVersion={presentedVersion}; "
                        + $"instances={TotalInstanceCount()}; "
                        + $"surface={_bounds.Width}x{_bounds.Height}.");
                }
                _cancellation.Token.WaitHandle.WaitOne(waitMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _renderFaulted, 1);
            bool hadStarted = _started.IsSet;
            _startupError = ex;
            _started.Set();
            ReportFailure(
                hadStarted
                    ? "Поток вывода обоев аварийно остановлен."
                    : "Не удалось запустить поток вывода обоев.",
                ex,
                fatal: true);
        }
        finally
        {
            DisposeAttackComposition();
            foreach (SceneRuntime runtime in _sceneRuntimes)
                runtime.Dispose();
            _sceneRuntimes.Clear();
            _direct3DPresenter?.Dispose();
            if (_window != IntPtr.Zero && NativeWindow.IsWindow(_window))
                NativeWindow.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    private void PresentLatestFrame(
        MonitorOutputPlan plan,
        ref long presentedVersion)
    {
        if (_sceneRuntimes.Count == 0)
            return;

        long version = CombinedSceneVersion();
        if (version == presentedVersion)
            return;
        IReadOnlyList<MatrixScenePresentation> presentations =
            BuildPresentations(plan);
        if (_direct3DPresenter?.Present(
                _bounds.Width,
                _bounds.Height,
                presentations) == true)
        {
            presentedVersion = version;
            Volatile.Write(
                ref _lastPresentUtcTicks,
                DateTime.UtcNow.Ticks);
        }
    }

    private AttackInterfaceCaptureRequest?
        PeekAttackInterfaceCaptureRequest()
    {
        lock (_commandLock)
            return _pendingAttackInterfaceCapture;
    }

    private AttackInterfaceCaptureRequest?
        TakeAttackInterfaceCaptureRequest()
    {
        lock (_commandLock)
        {
            AttackInterfaceCaptureRequest? request =
                _pendingAttackInterfaceCapture;
            _pendingAttackInterfaceCapture = null;
            return request;
        }
    }

    private void CompleteAttackInterfaceCapture(
        AttackInterfaceCaptureRequest request,
        MonitorOutputPlan plan,
        ref long presentedVersion)
    {
        try
        {
            Direct3D11Presenter presenter = _direct3DPresenter
                ?? throw new InvalidOperationException(
                    "D3D11-композитор живых обоев отсутствует.");
            IReadOnlyList<MatrixScenePresentation> presentations =
                BuildPresentations(plan);
            request.Frame = presenter.PresentAndCapture(
                _bounds.Width,
                _bounds.Height,
                presentations,
                _bounds,
                AttackInterfaceCaptureService.SampleScale);
            presentedVersion = CombinedSceneVersion();
            Volatile.Write(
                ref _lastPresentUtcTicks,
                DateTime.UtcNow.Ticks);
        }
        catch (Exception exception)
        {
            request.Error = exception;
        }
        finally
        {
            request.Completed.Set();
        }
    }

    private IReadOnlyList<MatrixScenePresentation> BuildPresentations(
        MonitorOutputPlan plan)
    {
        Dictionary<string, SceneRuntime> runtimes = _sceneRuntimes
            .ToDictionary(
                runtime => runtime.Id,
                StringComparer.OrdinalIgnoreCase);
        List<MatrixScenePresentation> presentations = [];
        foreach (MonitorScenePlan scenePlan in plan.Scenes)
        {
            if (!runtimes.TryGetValue(
                    scenePlan.Id,
                    out SceneRuntime? runtime))
            {
                continue;
            }
            presentations.AddRange(scenePlan.Targets
                .Where(target => !target.IsVirtual)
                .Select(target => new MatrixScenePresentation(
                    runtime.Scene,
                    target.TargetBounds,
                    target.SourceBounds)));
        }
        return presentations;
    }

    private MonitorOutputPlan ApplyPendingCommands(
        MonitorOutputPlan activePlan,
        ref long presentedVersion)
    {
        MonitorOutputPlan? pendingPlan;
        PreparedImage? image;
        IReadOnlyDictionary<string, PreparedImage?>? databaseImages;
        bool hasImage;
        bool resetImageOverlay;
        AttackCompositionRequest? attackRequest;
        bool stopAttackComposition;
        lock (_commandLock)
        {
            pendingPlan = _pendingPlan;
            _pendingPlan = null;
            image = _pendingImage;
            databaseImages = _pendingDatabaseImages;
            _pendingDatabaseImages = null;
            hasImage = _hasPendingImage;
            _hasPendingImage = false;
            resetImageOverlay = _resetPendingImageOverlay;
            _resetPendingImageOverlay = false;
            attackRequest = _pendingAttackComposition;
            _pendingAttackComposition = null;
            stopAttackComposition = _stopAttackCompositionPending;
            _stopAttackCompositionPending = false;
        }

        if (stopAttackComposition)
            DisposeAttackComposition();

        if (hasImage)
            _activeImage = image;
        if (databaseImages is not null)
        {
            _activeDatabaseImages =
                new Dictionary<string, PreparedImage?>(
                    databaseImages,
                    StringComparer.OrdinalIgnoreCase);
        }
        if (pendingPlan is not null)
        {
            try
            {
                ReconfigureRuntimes(pendingPlan);
                activePlan = pendingPlan;
                presentedVersion = long.MinValue;
            }
            catch (Exception exception)
            {
                ReportFailure(
                    "Новая маршрутизация отклонена; прежний поток продолжает работу.",
                    exception,
                    fatal: false);
            }
        }
        if (resetImageOverlay)
        {
            foreach (SceneRuntime runtime in _sceneRuntimes)
                runtime.Renderer.ResetImageOverlay(image);
        }
        else if (hasImage)
        {
            foreach (SceneRuntime runtime in _sceneRuntimes)
            {
                runtime.Renderer.SetImage(
                    image,
                    runtime.ImageProjection);
            }
        }
        if (databaseImages is not null)
        {
            foreach (SceneRuntime runtime in _sceneRuntimes)
            {
                _activeDatabaseImages.TryGetValue(
                    runtime.DatabaseRootMonitorId,
                    out PreparedImage? databaseImage);
                runtime.Renderer.SetImage(
                    databaseImage,
                    runtime.ImageProjection);
            }
            RefreshAttackDatabaseImages();
        }
        if (attackRequest is not null)
        {
            try
            {
                DisposeAttackComposition();
                _attackComposition = CreateAttackComposition(
                    activePlan,
                    attackRequest.InterfaceFrame,
                    attackRequest.TransitionSeconds);
                attackRequest.Snapshot = _attackComposition.Snapshot;
            }
            catch (Exception exception)
            {
                attackRequest.Error = exception;
            }
            finally
            {
                attackRequest.Completed.Set();
            }
        }
        return activePlan;
    }

    private AttackComposition CreateAttackComposition(
        MonitorOutputPlan plan,
        AttackInterfaceFrame? interfaceFrame,
        double transitionSeconds)
    {
        PreparedImage? interfaceImage = PrepareAttackImage(interfaceFrame);
        Dictionary<string, SceneRuntime> flowMasters = _sceneRuntimes
            .Where(runtime => runtime.IsFlowMaster)
            .GroupBy(
                runtime => runtime.FlowRootMonitorId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        List<AttackSceneRuntime> attackRuntimes = [];
        List<MatrixScenePresentation> presentations = [];
        long latestStreamId = 0;

        try
        {
            foreach (MonitorScenePlan scenePlan in plan.Scenes)
            {
                if (!flowMasters.TryGetValue(
                        scenePlan.FlowRootMonitorId,
                        out SceneRuntime? flowSource))
                {
                    continue;
                }

                bool hasDatabaseImage = scenePlan.Settings.ImageMode
                    && ImageForDatabaseRoot(
                        scenePlan.DatabaseRootMonitorId) is not null;
                foreach (MonitorSceneTarget target in scenePlan.Targets
                             .Where(target => !target.IsVirtual))
                {
                    long streamCutoff;
                    lock (flowSource.Scene.SyncRoot)
                        streamCutoff = flowSource.Scene.LatestStreamId;
                    latestStreamId = Math.Max(
                        latestStreamId,
                        streamCutoff);

                    AppSettings interfaceSettings =
                        scenePlan.Settings.Copy(
                            includeMonitorProfiles: false);
                    interfaceSettings.ImageMode = interfaceImage is not null;
                    SharedMatrixScene scene = new(
                        Math.Max(1, flowSource.Scene.Width),
                        Math.Max(1, flowSource.Scene.Height));
                    MatrixSceneRenderer.AttackImageLayerRenderer renderer =
                        flowSource.Renderer.CreateAttackImageLayer(
                        scene,
                        interfaceSettings,
                        interfaceImage,
                        AttackImageProjection(target),
                        target.SourceBounds,
                        streamCutoff);

                    AttackSceneRuntime attackRuntime = new(
                        scenePlan.DatabaseRootMonitorId,
                        scenePlan.ImageProjection,
                        scenePlan.Settings.Copy(
                            includeMonitorProfiles: false),
                        Math.Max(transitionSeconds, 0.001),
                        hasDatabaseImage,
                        scene,
                        renderer);
                    attackRuntimes.Add(attackRuntime);
                    presentations.Add(new MatrixScenePresentation(
                        scene,
                        target.TargetBounds,
                        target.SourceBounds,
                        streamCutoff));
                }
            }

            SharedMatrixScene primary = attackRuntimes.FirstOrDefault()?.Scene
                ?? throw new InvalidOperationException(
                    "Активные сцены АТАКИ СИСТЕМЫ отсутствуют.");
            AttackFrameSnapshot snapshot = new(
                primary,
                presentations,
                latestStreamId,
                interfaceFrame?.InfluencedSampleCount ?? 0);
            DiagnosticLog.Write(
                "Симуляция интерфейса АТАКИ подготовлена через ядро образов: "
                + $"областей={attackRuntimes.Count}; "
                + $"отсчётов интерфейса={interfaceFrame?.InfluencedSampleCount ?? 0}; "
                + $"захват={transitionSeconds:0.##} с; "
                + "переход к базе=первая фактическая смена после захвата.");
            return new AttackComposition(
                attackRuntimes,
                snapshot);
        }
        catch
        {
            foreach (AttackSceneRuntime runtime in attackRuntimes)
                runtime.Dispose();
            throw;
        }
    }

    private void UpdateAttackComposition(TimeSpan now)
    {
        AttackComposition? composition = _attackComposition;
        if (composition is null)
            return;

        double elapsedSeconds = composition.Clock.Elapsed.TotalSeconds;
        foreach (AttackSceneRuntime runtime in composition.Runtimes)
        {
            if (!runtime.DatabaseActive
                && !runtime.HadDatabaseImageAtStart
                && elapsedSeconds >= runtime.NoDatabaseReleaseSeconds)
            {
                ActivateAttackDatabase(runtime, image: null);
                DiagnosticLog.Write(
                    "АТАКА СИСТЕМЫ завершила отпечаток интерфейса без Базы данных: "
                    + $"root={runtime.DatabaseRootMonitorId}; "
                    + $"boundary={runtime.NoDatabaseReleaseSeconds:0.##} с.");
            }
            runtime.Renderer.RenderIfDue(now);
        }
    }

    private void RefreshAttackDatabaseImages()
    {
        AttackComposition? composition = _attackComposition;
        if (composition is null)
            return;
        foreach (AttackSceneRuntime runtime in composition.Runtimes)
        {
            PreparedImage? image =
                ImageForDatabaseRoot(runtime.DatabaseRootMonitorId);
            if (runtime.DatabaseActive)
            {
                runtime.Renderer.SetImage(
                    image,
                    runtime.DatabaseProjection);
                continue;
            }

            // This method is called only when the manager publishes a real
            // database-image change. A change that occurs before capture has
            // completed is intentionally ignored; the following publication
            // becomes the first eligible boundary.
            if (composition.Clock.Elapsed.TotalSeconds
                < runtime.CaptureSeconds)
            {
                continue;
            }

            ActivateAttackDatabase(runtime, image);
            DiagnosticLog.Write(
                "АТАКА СИСТЕМЫ передала новые струи Базе данных на "
                + "фактической смене образа: "
                + $"root={runtime.DatabaseRootMonitorId}; "
                + $"elapsed={composition.Clock.Elapsed.TotalSeconds:0.##} с.");
        }
    }

    private static void ActivateAttackDatabase(
        AttackSceneRuntime runtime,
        PreparedImage? image)
    {
        runtime.DatabaseActive = true;
        runtime.Renderer.UpdateSettings(runtime.DatabaseSettings);
        runtime.Renderer.SetImage(
            image,
            runtime.DatabaseProjection);
    }

    private void DisposeAttackComposition()
    {
        AttackComposition? composition = _attackComposition;
        _attackComposition = null;
        composition?.Dispose();
    }

    private MatrixImageProjection AttackImageProjection(
        MonitorSceneTarget target) =>
        new(
            Math.Max(1, _bounds.Width),
            Math.Max(1, _bounds.Height),
            new DrawingRectangle(
                target.TargetBounds.Left,
                target.TargetBounds.Top,
                Math.Max(1, target.TargetBounds.Width),
                Math.Max(1, target.TargetBounds.Height)),
            target.SourceBounds);

    private static PreparedImage? PrepareAttackImage(
        AttackInterfaceFrame? frame)
    {
        if (frame is null
            || frame.Width <= 0
            || frame.Height <= 0
            || frame.Samples.Length != frame.Width * frame.Height * 2)
        {
            return null;
        }

        int pixelCount = checked(frame.Width * frame.Height);
        byte[] tone = new byte[pixelCount];
        byte[] influence = new byte[pixelCount];
        for (int index = 0; index < pixelCount; index++)
        {
            tone[index] = frame.Samples[index * 2];
            influence[index] = frame.Samples[index * 2 + 1];
        }
        return new PreparedImage(
            tone,
            frame.Width,
            frame.Height,
            "ATTACK://SYSTEM-INTERFACE",
            influence);
    }

    private void ReconfigureRuntimes(MonitorOutputPlan plan)
    {
        Dictionary<string, SceneRuntime> existing = _sceneRuntimes
            .ToDictionary(
                runtime => runtime.Id,
                StringComparer.OrdinalIgnoreCase);
        List<SceneRuntime> next = [];
        List<SceneRuntime> created = [];
        List<(SceneRuntime Runtime, MonitorScenePlan Plan)> reused = [];

        try
        {
            foreach (MonitorScenePlan scenePlan in plan.Scenes)
            {
                if (!existing.Remove(
                        scenePlan.Id,
                        out SceneRuntime? runtime))
                {
                    runtime = existing.Values.FirstOrDefault(candidate =>
                        candidate.CanReuseFor(scenePlan));
                    if (runtime is not null)
                    {
                        existing.Remove(runtime.Id);
                        reused.Add((runtime, scenePlan));
                        next.Add(runtime);
                        continue;
                    }

                    SceneRuntime? transferSource = scenePlan.IsFlowMaster
                        ? existing.Values.FirstOrDefault(candidate =>
                            candidate.IsFlowMaster
                            && string.Equals(
                                candidate.FlowRootMonitorId,
                                scenePlan.FlowRootMonitorId,
                                StringComparison.OrdinalIgnoreCase))
                        : null;
                    transferSource ??= existing.Values.FirstOrDefault(
                        candidate =>
                            candidate.TargetMonitorIds.Overlaps(
                                scenePlan.Targets.Select(
                                    target => target.MonitorId)));
                    runtime = CreateSceneRuntime(
                        scenePlan,
                        seedInitialStreams: transferSource is null
                            && scenePlan.IsFlowMaster);
                    if (transferSource is not null)
                    {
                        runtime.Renderer.ImportStateFrom(
                            transferSource.Renderer,
                            transferSource.CanvasBounds,
                            scenePlan.CanvasBounds);
                        DiagnosticLog.Write(
                            $"Состояние потока перенесено без повторного запуска: "
                            + $"root={scenePlan.FlowRootMonitorId}; "
                            + $"from={transferSource.CanvasBounds}; "
                            + $"to={scenePlan.CanvasBounds}.");
                    }
                    created.Add(runtime);
                }
                else
                {
                    reused.Add((runtime, scenePlan));
                }
                next.Add(runtime);
            }

            if (next.Count == 0)
            {
                if (!existing.Remove(
                        "DISABLED",
                        out SceneRuntime? disabled))
                {
                    disabled = CreateDisabledRuntime();
                    created.Add(disabled);
                }
                next.Add(disabled);
            }
        }
        catch
        {
            foreach (SceneRuntime runtime in created)
                runtime.Dispose();
            throw;
        }

        foreach ((SceneRuntime runtime, MonitorScenePlan scenePlan) in reused)
            UpdateSceneRuntime(runtime, scenePlan);
        ConfigureFlowSharing(next);
        foreach (SceneRuntime runtime in next)
            runtime.Renderer.RefreshPublishedScene();
        next.Sort(static (left, right) =>
            right.IsFlowMaster.CompareTo(left.IsFlowMaster));

        lock (_runtimeLock)
        {
            _sceneRuntimes.Clear();
            _sceneRuntimes.AddRange(next);
            _currentPlan = plan;
        }

        foreach (SceneRuntime obsolete in existing.Values)
        {
            _direct3DPresenter?.ReleaseScene(obsolete.Scene);
            obsolete.Dispose();
        }

        DiagnosticLog.Write(
            $"Маршрутизация перестроена без пересоздания D3D11: "
            + $"renderer=0x{_window.ToInt64():X}; "
            + $"scenes={plan.Scenes.Count}; "
            + $"flowGenerators={plan.Scenes.Count(scene => scene.IsFlowMaster)}; "
            + $"viewports={plan.ActiveMonitorCount}.");
    }

    private SceneRuntime CreateSceneRuntime(
        MonitorScenePlan scenePlan,
        bool seedInitialStreams = true)
    {
        SharedMatrixScene scene = new(
            Math.Max(1, scenePlan.CanvasBounds.Width),
            Math.Max(1, scenePlan.CanvasBounds.Height));
        MatrixSceneRenderer renderer = new(
            _window,
            scene,
            scenePlan.Settings,
            scenePlan.RandomSeed,
            seedInitialStreams);
        SceneRuntime runtime = new(
            scenePlan.Id,
            scenePlan.FlowRootMonitorId,
            scenePlan.IsFlowMaster,
            scenePlan.DatabaseRootMonitorId,
            scenePlan.CanvasBounds,
            scenePlan.Targets.Select(target => target.MonitorId),
            scenePlan.ImageProjection,
            scene,
            renderer,
            scenePlan.Settings.Copy(
                includeMonitorProfiles: false));
        renderer.SetImage(
            ImageForDatabaseRoot(scenePlan.DatabaseRootMonitorId),
            scenePlan.ImageProjection);
        return runtime;
    }

    private SceneRuntime CreateDisabledRuntime()
    {
        SharedMatrixScene emptyScene = new(1, 1);
        return new SceneRuntime(
            "DISABLED",
            "",
            true,
            "",
            new DrawingRectangle(0, 0, 1, 1),
            [],
            new MatrixImageProjection(
                1,
                1,
                new DrawingRectangle(0, 0, 1, 1),
                new DrawingRectangle(0, 0, 1, 1)),
            emptyScene,
            new MatrixSceneRenderer(
                _window,
                emptyScene,
                _initialSettings,
                0),
            _initialSettings.Copy(
                includeMonitorProfiles: false));
    }

    private void UpdateSceneRuntime(
        SceneRuntime runtime,
        MonitorScenePlan scenePlan)
    {
        try
        {
            runtime.Renderer.UpdateSettings(scenePlan.Settings);
            runtime.LastGoodSettings =
                scenePlan.Settings.Copy(
                    includeMonitorProfiles: false);
        }
        catch (Exception exception)
        {
            ReportFailure(
                $"Настройка рендера отклонена; восстановлен размер "
                + $"{runtime.LastGoodSettings.FontSize:0.##} px.",
                exception,
                fatal: false);
            runtime.Renderer.UpdateSettings(runtime.LastGoodSettings);
        }

        runtime.Renderer.SetImage(
            ImageForDatabaseRoot(scenePlan.DatabaseRootMonitorId),
            scenePlan.ImageProjection);
        runtime.Id = scenePlan.Id;
        runtime.DatabaseRootMonitorId = scenePlan.DatabaseRootMonitorId;
        runtime.ImageProjection = scenePlan.ImageProjection;
        runtime.TargetMonitorIds = scenePlan.Targets
            .Select(target => target.MonitorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ConfigureFlowSharing(
        IReadOnlyList<SceneRuntime> runtimes)
    {
        Dictionary<string, SceneRuntime> masters = runtimes
            .Where(runtime => runtime.IsFlowMaster)
            .GroupBy(
                runtime => runtime.FlowRootMonitorId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (SceneRuntime runtime in runtimes)
        {
            MatrixSceneRenderer? source = null;
            if (!runtime.IsFlowMaster)
            {
                masters.TryGetValue(
                    runtime.FlowRootMonitorId,
                    out SceneRuntime? master);
                source = master?.Renderer;
            }
            runtime.Renderer.FollowFlowFrom(source);
        }
    }

    private PreparedImage? ImageForDatabaseRoot(string rootMonitorId)
    {
        if (_activeDatabaseImages.Count == 0)
            return _activeImage;
        _activeDatabaseImages.TryGetValue(
            rootMonitorId,
            out PreparedImage? image);
        return image;
    }

    private long CombinedSceneVersion()
    {
        unchecked
        {
            long version = 17;
            foreach (SceneRuntime runtime in _sceneRuntimes)
                version = version * 31 + runtime.Scene.Version;
            return version;
        }
    }

    private int TotalInstanceCount() =>
        _sceneRuntimes.Sum(runtime => runtime.Scene.InstanceCount);

    private void ReportFailure(string context, Exception exception, bool fatal)
    {
        try
        {
            _failureHandler?.Invoke(context, exception, fatal);
        }
        catch
        {
            // The renderer must not depend on diagnostics or UI callbacks.
        }
    }

    public void LogAttachmentState(string stage) =>
        DesktopHost.LogAttachmentState(_window, _bounds, stage);

    public bool IsAttachmentVisible() =>
        DesktopHost.IsAttachmentVisible(_window);

    public bool TryGetHealth(
        TimeSpan maximumFrameAge,
        out string reason)
    {
        if (_disposed)
        {
            reason = "окно вывода уже закрывается";
            return false;
        }
        if (Volatile.Read(ref _renderFaulted) != 0)
        {
            reason = "поток Direct3D завершился ошибкой";
            return false;
        }
        if (!_thread.IsAlive)
        {
            reason = "поток Direct3D не работает";
            return false;
        }
        IntPtr window = _window;
        if (window == IntPtr.Zero)
        {
            reason = "дескриптор окна вывода потерян";
            return false;
        }
        if (!DesktopHost.IsAttachmentVisible(window))
        {
            reason = "окно больше не связано с фоновым слоем Explorer";
            return false;
        }

        long lastPresentTicks =
            Volatile.Read(ref _lastPresentUtcTicks);
        if (lastPresentTicks <= 0)
        {
            reason = "Direct3D ещё не подтвердил ни одного кадра";
            return false;
        }
        TimeSpan frameAge =
            DateTime.UtcNow - new DateTime(lastPresentTicks, DateTimeKind.Utc);
        if (frameAge > maximumFrameAge)
        {
            reason =
                $"кадры не обновлялись {frameAge.TotalSeconds:0.0} с";
            return false;
        }

        reason = "";
        return true;
    }

    public void Dispose()
    {
        RequestClose();
        WaitForClose(TimeSpan.FromSeconds(2));
    }

    private sealed class AttackCompositionRequest
    {
        public AttackInterfaceFrame? InterfaceFrame { get; }
        public double TransitionSeconds { get; }
        public ManualResetEventSlim Completed { get; } = new(false);
        public AttackFrameSnapshot? Snapshot { get; set; }
        public Exception? Error { get; set; }

        public AttackCompositionRequest(
            AttackInterfaceFrame? interfaceFrame,
            double transitionSeconds)
        {
            InterfaceFrame = interfaceFrame;
            TransitionSeconds = transitionSeconds;
        }
    }

    private sealed class AttackInterfaceCaptureRequest
    {
        public ManualResetEventSlim Completed { get; } = new(false);
        public AttackInterfaceFrame? Frame { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class AttackComposition : IDisposable
    {
        public IReadOnlyList<AttackSceneRuntime> Runtimes { get; }
        public AttackFrameSnapshot Snapshot { get; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public AttackComposition(
            IReadOnlyList<AttackSceneRuntime> runtimes,
            AttackFrameSnapshot snapshot)
        {
            Runtimes = runtimes;
            Snapshot = snapshot;
        }

        public void Dispose()
        {
            Clock.Stop();
            foreach (AttackSceneRuntime runtime in Runtimes)
                runtime.Dispose();
        }
    }

    private sealed class AttackSceneRuntime : IDisposable
    {
        public string DatabaseRootMonitorId { get; }
        public MatrixImageProjection DatabaseProjection { get; }
        public AppSettings DatabaseSettings { get; }
        public double CaptureSeconds { get; }
        public bool HadDatabaseImageAtStart { get; }
        public double NoDatabaseReleaseSeconds =>
            Math.Max(CaptureSeconds * 2.0, 1.0);
        public SharedMatrixScene Scene { get; }
        public MatrixSceneRenderer.AttackImageLayerRenderer Renderer { get; }
        public bool DatabaseActive { get; set; }

        public AttackSceneRuntime(
            string databaseRootMonitorId,
            MatrixImageProjection databaseProjection,
            AppSettings databaseSettings,
            double captureSeconds,
            bool hadDatabaseImageAtStart,
            SharedMatrixScene scene,
            MatrixSceneRenderer.AttackImageLayerRenderer renderer)
        {
            DatabaseRootMonitorId = databaseRootMonitorId;
            DatabaseProjection = databaseProjection;
            DatabaseSettings = databaseSettings;
            CaptureSeconds = captureSeconds;
            HadDatabaseImageAtStart = hadDatabaseImageAtStart;
            Scene = scene;
            Renderer = renderer;
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Scene.Dispose();
        }
    }

    private sealed class SceneRuntime : IDisposable
    {
        public string Id { get; set; }
        public string FlowRootMonitorId { get; }
        public bool IsFlowMaster { get; }
        public string DatabaseRootMonitorId { get; set; }
        public DrawingRectangle CanvasBounds { get; }
        public HashSet<string> TargetMonitorIds { get; set; }
        public MatrixImageProjection ImageProjection { get; set; }
        public SharedMatrixScene Scene { get; }
        public MatrixSceneRenderer Renderer { get; }
        public AppSettings LastGoodSettings { get; set; }

        public bool CanReuseFor(MonitorScenePlan plan) =>
            string.Equals(
                FlowRootMonitorId,
                plan.FlowRootMonitorId,
                StringComparison.OrdinalIgnoreCase)
            && IsFlowMaster == plan.IsFlowMaster
            && CanvasBounds == plan.CanvasBounds
            && TargetMonitorIds.Overlaps(
                plan.Targets.Select(target => target.MonitorId));

        public SceneRuntime(
            string id,
            string flowRootMonitorId,
            bool isFlowMaster,
            string databaseRootMonitorId,
            DrawingRectangle canvasBounds,
            IEnumerable<string> targetMonitorIds,
            MatrixImageProjection imageProjection,
            SharedMatrixScene scene,
            MatrixSceneRenderer renderer,
            AppSettings lastGoodSettings)
        {
            Id = id;
            FlowRootMonitorId = flowRootMonitorId;
            IsFlowMaster = isFlowMaster;
            DatabaseRootMonitorId = databaseRootMonitorId;
            CanvasBounds = canvasBounds;
            TargetMonitorIds = targetMonitorIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            ImageProjection = imageProjection;
            Scene = scene;
            Renderer = renderer;
            LastGoodSettings = lastGoodSettings;
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Scene.Dispose();
        }
    }

    public void RequestClose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IntPtr window = _window;
        if (window != IntPtr.Zero)
        {
            NativeWindow.ShowWindowAsync(window, NativeWindow.ShowHide);
            _cancellation.Cancel();
            NativeWindow.PostMessage(window, NativeWindow.CloseMessage, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            _cancellation.Cancel();
        }
    }

    public void WaitForClose(TimeSpan timeout)
    {
        if (_thread.IsAlive && Thread.CurrentThread != _thread && timeout > TimeSpan.Zero)
            _thread.Join(timeout);
        if (_thread.IsAlive || _synchronizationDisposed)
            return;

        _synchronizationDisposed = true;
        _started.Dispose();
        _renderingEnabled.Dispose();
        _cancellation.Dispose();
    }

    private static class NativeWindow
    {
        private const string ClassName = "WallpaperMatrix.NativeWallpaper.3";
        private const uint WindowStylePopup = 0x80000000;
        private const uint ExStyleToolWindow = 0x00000080;
        private const uint ExStyleTransparent = 0x00000020;
        private const uint ExStyleNoActivate = 0x08000000;
        private const int ArrowCursor = 32512;
        private const int BlackBrush = 4;
        private const int ClassAlreadyExists = 1410;
        private const int HitTestTransparent = -1;
        private const int MouseActivateNoActivate = 3;
        private const uint EraseBackgroundMessage = 0x0014;
        private const uint PaintMessage = 0x000F;
        private const uint NonClientHitTestMessage = 0x0084;
        private const uint MouseActivateMessage = 0x0021;
        private const uint DestroyMessage = 0x0002;
        private const uint SetIconMessage = 0x0080;
        private const int IconSmall = 0;
        private const int IconBig = 1;
        private static readonly object RegistrationLock = new();
        private static readonly WindowProcedure WindowProcedureDelegate = WindowProc;
        private static IntPtr _largeIcon;
        private static IntPtr _smallIcon;
        private static bool _registered;

        public const int ShowHide = 0;
        public const int ShowNoActivate = 4;
        public const uint RemoveMessage = 0x0001;
        public const uint CloseMessage = 0x0010;
        public const uint QuitMessage = 0x0012;

        public static void EnsureClassRegistered()
        {
            lock (RegistrationLock)
            {
                if (_registered)
                    return;

                EnsureApplicationIcons();
                WindowClass windowClass = new()
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    // DirectComposition owns the window's presentation
                    // surface, so the class does not need a private HDC.
                    Style = 0,
                    WindowProcedure = WindowProcedureDelegate,
                    Instance = GetModuleHandle(null),
                    Icon = _largeIcon != IntPtr.Zero
                        ? _largeIcon
                        : _smallIcon,
                    Cursor = LoadCursor(IntPtr.Zero, new IntPtr(ArrowCursor)),
                    Background = GetStockObject(BlackBrush),
                    ClassName = ClassName,
                    SmallIcon = _smallIcon != IntPtr.Zero
                        ? _smallIcon
                        : _largeIcon
                };
                ushort atom = RegisterClassEx(ref windowClass);
                int error = Marshal.GetLastWin32Error();
                if (atom == 0 && error != ClassAlreadyExists)
                    throw new InvalidOperationException($"RegisterClassEx завершился с кодом {error}.");
                _registered = true;
            }
        }

        public static IntPtr Create(DrawingRectangle bounds)
        {
            IntPtr window = CreateWindowEx(
                ExStyleToolWindow | ExStyleTransparent | ExStyleNoActivate,
                ClassName,
                "Wallpaper Matrix Renderer",
                WindowStylePopup,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);
            if (window != IntPtr.Zero)
            {
                if (_largeIcon != IntPtr.Zero)
                {
                    SendMessage(
                        window,
                        SetIconMessage,
                        new IntPtr(IconBig),
                        _largeIcon);
                }
                if (_smallIcon != IntPtr.Zero)
                {
                    SendMessage(
                        window,
                        SetIconMessage,
                        new IntPtr(IconSmall),
                        _smallIcon);
                }
            }
            return window;
        }

        private static void EnsureApplicationIcons()
        {
            if (_largeIcon != IntPtr.Zero || _smallIcon != IntPtr.Zero)
                return;

            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                return;

            uint extracted = ExtractIconEx(
                executablePath,
                0,
                out _largeIcon,
                out _smallIcon,
                1);
            DiagnosticLog.Write(
                $"Значок нативных окон: extracted={extracted}; "
                + $"large=0x{_largeIcon.ToInt64():X}; "
                + $"small=0x{_smallIcon.ToInt64():X}.");
            // The class and its windows retain these HICON handles for the
            // lifetime of the process, so they are intentionally not destroyed.
        }

        private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case NonClientHitTestMessage:
                    return new IntPtr(HitTestTransparent);
                case MouseActivateMessage:
                    return new IntPtr(MouseActivateNoActivate);
                case EraseBackgroundMessage:
                    return new IntPtr(1);
                case PaintMessage:
                    BeginPaint(window, out PaintStruct paint);
                    EndPaint(window, ref paint);
                    return IntPtr.Zero;
                case DestroyMessage:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProc(window, message, wParam, lParam);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public WindowProcedure WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public NativePoint Point;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PaintStruct
        {
            public IntPtr DeviceContext;
            public int Erase;
            public NativeRect PaintRect;
            public int Restore;
            public int IncrementalUpdate;
            public long Reserved1;
            public long Reserved2;
            public long Reserved3;
            public long Reserved4;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string fileName,
            int iconIndex,
            out IntPtr largeIcon,
            out IntPtr smallIcon,
            uint iconCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int objectIndex);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minMessage, uint maxMessage, uint removeMessage);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);
    }
}
