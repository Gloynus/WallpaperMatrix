using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WallpaperMatrix.Models;
using WallpaperMatrix.Rendering;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Native;

/// <summary>
/// Presents the already-running Matrix scene over the complete virtual
/// desktop. It owns no simulation state, so entering and leaving the attack
/// cannot reset streams, clocks, glyphs or image timing.
/// </summary>
internal sealed class AttackOverlayWindow : IDisposable
{
    private const double ExitTransitionSeconds = 0.18;
    private readonly DrawingRectangle _bounds;
    private readonly SharedMatrixScene _scene;
    private CapturedDesktopFrame? _desktop;
    private MatrixScenePresentation[] _presentations;
    private readonly double _transitionSeconds;
    private readonly bool _autoReleaseDesktopImage;
    private readonly long _existingStreamCutoff;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private IntPtr _window;
    private Exception? _startupError;
    private int _exitRequested;
    private int _immediateExit;
    private int _releaseDesktopImage;
    private int _disposed;
    private long _inputArmedAt;

    public event Action? Closed;
    public event Action? ExitStarted;

    public AttackOverlayWindow(
        DrawingRectangle bounds,
        AttackFrameSnapshot frame,
        CapturedDesktopFrame desktop,
        double transitionSeconds,
        bool autoReleaseDesktopImage)
    {
        _bounds = bounds;
        _scene = frame.PrimaryScene;
        _desktop = desktop;
        _presentations = frame.Presentations.ToArray();
        _transitionSeconds = Math.Clamp(transitionSeconds, 1.0, 30.0);
        _autoReleaseDesktopImage = autoReleaseDesktopImage;
        _existingStreamCutoff = frame.LatestStreamId;
        _thread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = "Wallpaper Matrix attack overlay",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(8)))
        {
            RequestExit(immediate: true);
            throw new TimeoutException(
                "Не удалось запустить поверхность АТАКИ СИСТЕМЫ.");
        }
        if (_startupError is not null)
        {
            throw new InvalidOperationException(
                "Не удалось создать поверхность АТАКИ СИСТЕМЫ.",
                _startupError);
        }
    }

    public void RequestExit(bool immediate = false)
    {
        Interlocked.Exchange(ref _exitRequested, 1);
        if (immediate)
            Interlocked.Exchange(ref _immediateExit, 1);
        IntPtr window = _window;
        if (window != IntPtr.Zero)
        {
            NativeWindow.PostMessage(
                window,
                NativeWindow.WakeMessage,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }

    public bool WaitForClose(TimeSpan timeout) =>
        _closed.Wait(timeout);

    public void ReleaseDesktopImage()
    {
        Interlocked.Exchange(ref _releaseDesktopImage, 1);
        IntPtr window = _window;
        if (window != IntPtr.Zero)
        {
            NativeWindow.PostMessage(
                window,
                NativeWindow.WakeMessage,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }

    private void RenderThreadMain()
    {
        Direct3D11Presenter? presenter = null;
        bool started = false;
        try
        {
            NativeWindow.EnsureClassRegistered();
            _window = NativeWindow.Create(_bounds);
            if (_window == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx завершился с кодом "
                    + $"{Marshal.GetLastWin32Error()}.");
            }
            NativeWindow.RegisterOwner(_window, this);

            presenter = Direct3D11Presenter.Create(
                _window,
                _bounds.Width,
                _bounds.Height,
                _scene,
                transparentSurface: true);
            CapturedDesktopFrame desktop = _desktop
                ?? throw new InvalidOperationException(
                    "Снимок перехода недоступен.");
            presenter.SetTransitionBackground(desktop);
            _desktop = null;
            presenter.SetTransitionState(1, 1);
            presenter.SetAttackGlyphState(
                _existingStreamCutoff,
                screenshotStreamCutoff: null,
                haloFactor: 1);
            presenter.Present(
                _bounds.Width,
                _bounds.Height,
                _presentations);

            NativeWindow.ShowAsTopmost(_window, _bounds);
            _inputArmedAt = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * 0.70);
            Stopwatch attackClock = Stopwatch.StartNew();
            Stopwatch? exitClock = null;
            long? screenshotStreamCutoff = null;
            double nextTopmostCheckSeconds = 0.5;
            double exitStartDesktopOpacity = 0;
            started = true;
            _started.Set();
            DiagnosticLog.Write(
                $"АТАКА СИСТЕМЫ начата: "
                + $"renderer=0x{_window.ToInt64():X}; "
                + $"surface={_bounds.Width}x{_bounds.Height}; "
                + $"viewports={_presentations.Length}; "
                + $"streamCutoff={_existingStreamCutoff}; "
                + $"transition={_transitionSeconds:0.##}s.");

            while (!_cancellation.IsCancellationRequested)
            {
                while (NativeWindow.PeekMessage(
                    out NativeWindow.NativeMessage message,
                    IntPtr.Zero,
                    0,
                    0,
                    NativeWindow.RemoveMessage))
                {
                    if (message.Message == NativeWindow.QuitMessage)
                        return;
                    NativeWindow.TranslateMessage(ref message);
                    NativeWindow.DispatchMessage(ref message);
                }

                double elapsedSeconds =
                    attackClock.Elapsed.TotalSeconds;
                if (_autoReleaseDesktopImage
                    && elapsedSeconds >= _transitionSeconds * 2.0)
                {
                    Interlocked.Exchange(
                        ref _releaseDesktopImage,
                        1);
                }
                if (elapsedSeconds > 0.70
                    && UserIdleMonitor.IdleTime
                        < TimeSpan.FromMilliseconds(350))
                {
                    RequestExit();
                }
                if (elapsedSeconds >= nextTopmostCheckSeconds)
                {
                    NativeWindow.KeepTopmost(_window);
                    nextTopmostCheckSeconds =
                        elapsedSeconds + 0.5;
                }

                if (!screenshotStreamCutoff.HasValue
                    && Volatile.Read(ref _releaseDesktopImage) != 0)
                {
                    screenshotStreamCutoff =
                        _scene.LatestStreamId;
                    _presentations = _presentations
                        .Select(presentation =>
                        {
                            long cutoff;
                            lock (presentation.Scene.SyncRoot)
                            {
                                cutoff =
                                    presentation.Scene.LatestStreamId;
                            }
                            return presentation with
                            {
                                ScreenshotStreamCutoff = cutoff
                            };
                        })
                        .ToArray();
                    DiagnosticLog.Write(
                        "АТАКА СИСТЕМЫ: отпечаток интерфейса больше не "
                        + "назначается новым струям и будет стёрт потоком.");
                }

                bool exiting =
                    Volatile.Read(ref _exitRequested) != 0;
                if (exiting
                    && Volatile.Read(ref _immediateExit) != 0)
                {
                    break;
                }

                double desktopOpacity;
                double glyphOpacity;
                if (exiting)
                {
                    if (exitClock is null)
                    {
                        exitClock = Stopwatch.StartNew();
                        exitStartDesktopOpacity =
                            AttackDesktopOpacity(attackClock.Elapsed.TotalSeconds);
                        try
                        {
                            ExitStarted?.Invoke();
                        }
                        catch
                        {
                            // Exit animation must not depend on its observer.
                        }
                    }
                    double progress = Math.Clamp(
                        exitClock.Elapsed.TotalSeconds
                            / ExitTransitionSeconds,
                        0,
                        1);
                    double eased = SmoothStep(progress);
                    desktopOpacity = exitStartDesktopOpacity
                        + (1.0 - exitStartDesktopOpacity) * eased;
                    glyphOpacity = 1.0 - eased;
                    if (progress >= 1)
                        break;
                }
                else
                {
                    desktopOpacity =
                        AttackDesktopOpacity(attackClock.Elapsed.TotalSeconds);
                    glyphOpacity = 1;
                }

                presenter.SetTransitionState(
                    desktopOpacity,
                    glyphOpacity);
                presenter.SetAttackGlyphState(
                    _existingStreamCutoff,
                    screenshotStreamCutoff,
                    AttackHaloFactor(elapsedSeconds));
                presenter.Present(
                    _bounds.Width,
                    _bounds.Height,
                    _presentations);

                int frameRate = Math.Clamp(
                    _scene.PresentationFramesPerSecond,
                    20,
                    60);
                _cancellation.Token.WaitHandle.WaitOne(
                    Math.Max(2, 1000 / frameRate));
            }
        }
        catch (Exception exception)
        {
            _startupError = exception;
            if (!started)
                _started.Set();
            DiagnosticLog.Write(
                started
                    ? "Поверхность АТАКИ СИСТЕМЫ аварийно остановлена."
                    : "Не удалось запустить АТАКУ СИСТЕМЫ.",
                exception);
        }
        finally
        {
            presenter?.Dispose();
            IntPtr window = _window;
            if (window != IntPtr.Zero)
            {
                NativeWindow.UnregisterOwner(window);
                if (NativeWindow.IsWindow(window))
                    NativeWindow.DestroyWindow(window);
            }
            _window = IntPtr.Zero;
            _started.Set();
            _closed.Set();
            try
            {
                Closed?.Invoke();
            }
            catch
            {
                // The native surface must always finish teardown.
            }
            DiagnosticLog.Write("АТАКА СИСТЕМЫ завершена.");
        }
    }

    private double AttackDesktopOpacity(double elapsedSeconds)
    {
        const double revealDelay = 0.35;
        double progress = Math.Clamp(
            (elapsedSeconds - revealDelay) / _transitionSeconds,
            0,
            1);
        return 1.0 - SmoothStep(progress);
    }

    private double AttackHaloFactor(double elapsedSeconds)
    {
        const double revealDelay = 0.35;
        double captureProgress = Math.Clamp(
            (elapsedSeconds - revealDelay) / _transitionSeconds,
            0,
            1);
        return 1.0 - SmoothStep(Math.Clamp(
            (captureProgress - 0.72) / 0.28,
            0,
            1));
    }

    private static double SmoothStep(double value) =>
        value * value * (3.0 - 2.0 * value);

    internal void OnNativeInput()
    {
        if (Stopwatch.GetTimestamp()
            < Volatile.Read(ref _inputArmedAt))
        {
            return;
        }
        if (UserIdleMonitor.IdleTime
            < TimeSpan.FromMilliseconds(500))
        {
            RequestExit();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RequestExit(immediate: true);
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        if (_thread.IsAlive)
        {
            _cancellation.Cancel();
            IntPtr window = _window;
            if (window != IntPtr.Zero)
            {
                NativeWindow.PostMessage(
                    window,
                    NativeWindow.WakeMessage,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            if (Thread.CurrentThread != _thread)
                _thread.Join(TimeSpan.FromSeconds(1));
            if (_thread.IsAlive)
                return;
        }
        _cancellation.Cancel();
        _closed.Dispose();
        _started.Dispose();
        _cancellation.Dispose();
    }

    private static class NativeWindow
    {
        private const string ClassName =
            "WallpaperMatrix.AttackOverlay.1";
        private const uint WindowStylePopup = 0x80000000;
        private const uint ExStyleToolWindow = 0x00000080;
        private const uint ExStyleNoRedirectionBitmap = 0x00200000;
        private const int ClassAlreadyExists = 1410;
        private const int ArrowCursor = 32512;
        private const int ShowNormal = 5;
        private const uint NoOwnerZOrder = 0x0200;
        private const uint ShowWindowFlag = 0x0040;
        private const uint NoSize = 0x0001;
        private const uint NoMove = 0x0002;
        private const uint NoActivate = 0x0010;
        private const uint SetCursorMessage = 0x0020;
        private const uint MouseMoveMessage = 0x0200;
        private const uint LeftButtonDownMessage = 0x0201;
        private const uint RightButtonDownMessage = 0x0204;
        private const uint MiddleButtonDownMessage = 0x0207;
        private const uint MouseWheelMessage = 0x020A;
        private const uint KeyDownMessage = 0x0100;
        private const uint SystemKeyDownMessage = 0x0104;
        private const uint CloseMessage = 0x0010;
        private const uint DestroyMessage = 0x0002;
        private const uint PaintMessage = 0x000F;
        private const uint EraseBackgroundMessage = 0x0014;
        private static readonly IntPtr Topmost = new(-1);
        private static readonly object RegistrationLock = new();
        private static readonly WindowProcedure WindowProcedureDelegate =
            WindowProc;
        private static readonly ConcurrentDictionary<IntPtr, AttackOverlayWindow>
            Owners = new();
        private static bool _registered;

        public const uint WakeMessage = 0x0400 + 73;
        public const uint RemoveMessage = 0x0001;
        public const uint QuitMessage = 0x0012;

        public static void EnsureClassRegistered()
        {
            lock (RegistrationLock)
            {
                if (_registered)
                    return;
                WindowClass windowClass = new()
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    WindowProcedure = WindowProcedureDelegate,
                    Instance = GetModuleHandle(null),
                    Cursor = LoadCursor(
                        IntPtr.Zero,
                        new IntPtr(ArrowCursor)),
                    Background = IntPtr.Zero,
                    ClassName = ClassName
                };
                ushort atom = RegisterClassEx(ref windowClass);
                int error = Marshal.GetLastWin32Error();
                if (atom == 0 && error != ClassAlreadyExists)
                {
                    throw new InvalidOperationException(
                        $"RegisterClassEx завершился с кодом {error}.");
                }
                _registered = true;
            }
        }

        public static IntPtr Create(DrawingRectangle bounds) =>
            CreateWindowEx(
                ExStyleToolWindow | ExStyleNoRedirectionBitmap,
                ClassName,
                "Wallpaper Matrix — АТАКА СИСТЕМЫ",
                WindowStylePopup,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);

        public static void ShowAsTopmost(
            IntPtr window,
            DrawingRectangle bounds)
        {
            ShowWindow(window, ShowNormal);
            SetWindowPos(
                window,
                Topmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                NoOwnerZOrder | ShowWindowFlag);
            SetForegroundWindow(window);
            SetFocus(window);
            UpdateWindow(window);
        }

        public static void KeepTopmost(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
                return;
            SetWindowPos(
                window,
                Topmost,
                0,
                0,
                0,
                0,
                NoSize
                    | NoMove
                    | NoActivate
                    | NoOwnerZOrder);
        }

        public static void RegisterOwner(
            IntPtr window,
            AttackOverlayWindow owner) =>
            Owners[window] = owner;

        public static void UnregisterOwner(IntPtr window) =>
            Owners.TryRemove(window, out _);

        private static IntPtr WindowProc(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam)
        {
            if (message == SetCursorMessage)
            {
                SetCursor(IntPtr.Zero);
                return new IntPtr(1);
            }

            if (message is MouseMoveMessage
                or LeftButtonDownMessage
                or RightButtonDownMessage
                or MiddleButtonDownMessage
                or MouseWheelMessage
                or KeyDownMessage
                or SystemKeyDownMessage)
            {
                if (Owners.TryGetValue(
                    window,
                    out AttackOverlayWindow? owner))
                {
                    owner.OnNativeInput();
                }
                return IntPtr.Zero;
            }

            switch (message)
            {
                case WakeMessage:
                    return IntPtr.Zero;
                case EraseBackgroundMessage:
                    return new IntPtr(1);
                case PaintMessage:
                    BeginPaint(window, out PaintStruct paint);
                    EndPaint(window, ref paint);
                    return IntPtr.Zero;
                case CloseMessage:
                    if (Owners.TryGetValue(
                        window,
                        out AttackOverlayWindow? owner))
                    {
                        owner.RequestExit(immediate: true);
                        return IntPtr.Zero;
                    }
                    break;
                case DestroyMessage:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProc(window, message, wParam, lParam);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcedure(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

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
        private static extern ushort RegisterClassEx(
            ref WindowClass windowClass);

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

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(
            IntPtr instance,
            IntPtr cursorName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(
            out NativeMessage message,
            IntPtr window,
            uint minMessage,
            uint maxMessage,
            uint removeMessage);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(
            IntPtr window,
            out PaintStruct paint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(
            IntPtr window,
            ref PaintStruct paint);
    }
}
