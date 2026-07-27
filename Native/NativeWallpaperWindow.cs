using System.Runtime.InteropServices;
using WallpaperMatrix.Models;
using WallpaperMatrix.Rendering;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace WallpaperMatrix.Native;

/// <summary>
/// Owns one native wallpaper window and its render loop on a background thread.
/// It never participates in WPF input, focus, or layout processing.
/// </summary>
internal sealed class NativeWallpaperWindow : IDisposable
{
    private readonly DrawingRectangle _bounds;
    private readonly DrawingSize _sceneSize;
    private IReadOnlyList<DrawingRectangle> _viewports;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _renderingEnabled = new(true);
    private readonly object _commandLock = new();
    private readonly AppSettings _initialSettings;
    private readonly Action<string, Exception, bool>? _failureHandler;
    private AppSettings _lastGoodSettings;
    private AppSettings? _pendingSettings;
    private PreparedImage? _pendingImage;
    private bool _hasPendingImage;
    private bool _resetPendingImageOverlay;
    private IntPtr _window;
    private SharedMatrixScene? _sharedFrame;
    private Direct3D11Presenter? _direct3DPresenter;
    private Exception? _startupError;
    private int _paused;
    private int _presentationSuppressed;
    private bool _disposed;
    private bool _synchronizationDisposed;

    public SharedMatrixScene SharedFrame => _sharedFrame
        ?? throw new InvalidOperationException("Общий кадр ещё не создан.");

    public NativeWallpaperWindow(
        DrawingRectangle bounds,
        DrawingSize sceneSize,
        IReadOnlyList<DrawingRectangle> monitorBounds,
        AppSettings settings,
        Action<string, Exception, bool>? failureHandler = null)
    {
        _bounds = bounds;
        _sceneSize = sceneSize;
        _viewports = BuildViewports(bounds, monitorBounds);
        _initialSettings = settings.Copy();
        _lastGoodSettings = settings.Copy();
        _failureHandler = failureHandler;
        _thread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = $"Wallpaper compositor {bounds.Width}x{bounds.Height}",
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

    public void UpdateSettings(AppSettings settings)
    {
        lock (_commandLock)
            _pendingSettings = settings.Copy();
    }

    public void SetImage(PreparedImage? image)
    {
        lock (_commandLock)
        {
            _pendingImage = image;
            _hasPendingImage = true;
        }
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
        Volatile.Write(ref _paused, paused ? 1 : 0);
        if (paused)
        {
            _renderingEnabled.Reset();
            IntPtr window = _window;
            if (window != IntPtr.Zero)
                NativeWindow.ShowWindowAsync(window, NativeWindow.ShowHide);
        }
        else
        {
            IntPtr window = _window;
            if (window != IntPtr.Zero)
                NativeWindow.ShowWindowAsync(window, NativeWindow.ShowNoActivate);
            _renderingEnabled.Set();
        }
    }

    public void SetPresentationSuppressed(bool suppressed) =>
        Volatile.Write(
            ref _presentationSuppressed,
            suppressed ? 1 : 0);

    private void RenderThreadMain()
    {
        MatrixSceneRenderer? renderer = null;
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
            _sharedFrame = new SharedMatrixScene(
                _sceneSize.Width,
                _sceneSize.Height);
            renderer = new MatrixSceneRenderer(
                _window,
                _sharedFrame,
                _initialSettings);
            _direct3DPresenter = Direct3D11Presenter.Create(
                _window,
                _bounds.Width,
                _bounds.Height,
                SharedFrame);
            NativeWindow.ShowWindow(_window, NativeWindow.ShowNoActivate);
            NativeWindow.UpdateWindow(_window);
            long presentedVersion = -1;
            bool nonEmptyFrameConfirmed = false;
            PresentLatestFrame(ref presentedVersion);
            DiagnosticLog.Write(
                $"Первый кадр передан: renderer=0x{_window.ToInt64():X}; "
                + $"compositor=True; viewports={_viewports.Count}; "
                + $"frameVersion={presentedVersion}; "
                + $"instances={SharedFrame.InstanceCount}; "
                + $"surface={_bounds.Width}x{_bounds.Height}.");
            _started.Set();

            while (!_cancellation.IsCancellationRequested)
            {
                if (Volatile.Read(ref _paused) != 0)
                {
                    renderer?.RenderIfDue(paused: true);
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
                if (renderer is not null)
                {
                    ApplyPendingCommands(renderer);
                    renderer.RenderIfDue(paused: false);
                    waitMilliseconds = renderer.RecommendedWaitMilliseconds(paused: false);
                }
                else
                    waitMilliseconds = 25;
                if (Volatile.Read(ref _presentationSuppressed) == 0)
                    PresentLatestFrame(ref presentedVersion);
                if (!nonEmptyFrameConfirmed
                    && SharedFrame.InstanceCount > 0
                    && presentedVersion >= 0)
                {
                    nonEmptyFrameConfirmed = true;
                    DiagnosticLog.Write(
                        $"Первый непустой кадр подтверждён: "
                        + $"renderer=0x{_window.ToInt64():X}; "
                        + $"frameVersion={presentedVersion}; "
                        + $"instances={SharedFrame.InstanceCount}; "
                        + $"surface={_bounds.Width}x{_bounds.Height}.");
                }
                _cancellation.Token.WaitHandle.WaitOne(waitMilliseconds);
            }
        }
        catch (Exception ex)
        {
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
            renderer?.Dispose();
            _direct3DPresenter?.Dispose();
            _sharedFrame?.Dispose();
            if (_window != IntPtr.Zero && NativeWindow.IsWindow(_window))
                NativeWindow.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    private void PresentLatestFrame(ref long presentedVersion)
    {
        SharedMatrixScene? frame = _sharedFrame;
        if (frame is null)
            return;

        long version = frame.Version;
        if (version == presentedVersion)
            return;
        if (_direct3DPresenter?.Present(
                _bounds.Width,
                _bounds.Height,
                _viewports) == true)
            presentedVersion = version;
    }

    private static IReadOnlyList<DrawingRectangle> BuildViewports(
        DrawingRectangle bounds,
        IEnumerable<DrawingRectangle> monitorBounds) =>
        monitorBounds
            .Select(monitor => new DrawingRectangle(
                monitor.Left - bounds.Left,
                monitor.Top - bounds.Top,
                monitor.Width,
                monitor.Height))
            .ToArray();

    private void ApplyPendingCommands(MatrixSceneRenderer renderer)
    {
        AppSettings? settings;
        PreparedImage? image;
        bool hasImage;
        bool resetImageOverlay;
        lock (_commandLock)
        {
            settings = _pendingSettings;
            _pendingSettings = null;
            image = _pendingImage;
            hasImage = _hasPendingImage;
            _hasPendingImage = false;
            resetImageOverlay = _resetPendingImageOverlay;
            _resetPendingImageOverlay = false;
        }

        if (settings is not null)
        {
            try
            {
                renderer.UpdateSettings(settings);
                _lastGoodSettings = settings.Copy();
            }
            catch (Exception exception)
            {
                ReportFailure(
                    $"Настройка рендера отклонена; восстановлен размер "
                    + $"{_lastGoodSettings.FontSize:0.##} px.",
                    exception,
                    fatal: false);
                renderer.UpdateSettings(_lastGoodSettings);
            }
        }
        if (resetImageOverlay)
            renderer.ResetImageOverlay(image);
        else if (hasImage)
            renderer.SetImage(image);
    }

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

    public void Dispose()
    {
        RequestClose();
        WaitForClose(TimeSpan.FromSeconds(2));
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
