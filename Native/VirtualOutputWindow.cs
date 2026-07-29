using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WallpaperMatrix.Rendering;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Native;

/// <summary>
/// A stable, ordinary top-level window intended for OBS Window Capture.
/// It presents one already-running output-device scene and owns no Matrix
/// simulation, image sequence or timing of its own.
/// </summary>
internal sealed class VirtualOutputWindow : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly string _title;
    private readonly Func<MatrixScenePresentation?> _frameProvider;
    private readonly Action<string, Exception, bool>? _failureHandler;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private IntPtr _window;
    private Exception? _startupError;
    private int _programmaticClose;
    private int _disposed;

    public event Action<bool>? Closed;

    public VirtualOutputWindow(
        int width,
        int height,
        string title,
        Func<MatrixScenePresentation?> frameProvider,
        Action<string, Exception, bool>? failureHandler)
    {
        _width = Math.Clamp(width, 320, 7680);
        _height = Math.Clamp(height, 180, 4320);
        _title = string.IsNullOrWhiteSpace(title)
            ? "Wallpaper Matrix — ВЫХОД"
            : title;
        _frameProvider = frameProvider;
        _failureHandler = failureHandler;
        _thread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = $"Wallpaper Matrix virtual output {_width}x{_height}",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(8)))
        {
            RequestClose();
            throw new TimeoutException(
                "Отдельное окно потока не подтвердило запуск.");
        }
        if (_startupError is not null)
        {
            throw new InvalidOperationException(
                "Не удалось открыть отдельное окно потока.",
                _startupError);
        }
    }

    public void RequestClose()
    {
        Interlocked.Exchange(ref _programmaticClose, 1);
        _cancellation.Cancel();
        IntPtr window = _window;
        if (window != IntPtr.Zero)
        {
            NativeWindow.PostMessage(
                window,
                NativeWindow.CloseMessage,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }

    public bool WaitForClose(TimeSpan timeout) =>
        _closed.Wait(timeout);

    private void RenderThreadMain()
    {
        Direct3D11Presenter? presenter = null;
        bool started = false;
        bool userInitiated = false;
        try
        {
            NativeWindow.EnsureClassRegistered();
            _window = NativeWindow.Create(
                _width,
                _height,
                _title);
            if (_window == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx завершился с кодом "
                    + $"{Marshal.GetLastWin32Error()}.");
            }
            NativeWindow.RegisterOwner(_window, this);

            MatrixScenePresentation? initial =
                WaitForInitialFrame(TimeSpan.FromSeconds(3));
            if (initial is null)
            {
                throw new InvalidOperationException(
                    "Выбранный источник потока не предоставляет кадр.");
            }
            presenter = Direct3D11Presenter.Create(
                _window,
                _width,
                _height,
                initial.Scene,
                transparentSurface: false);
            presenter.Present(
                _width,
                _height,
                [Transform(initial)]);
            NativeWindow.ShowWindow(
                _window,
                NativeWindow.ShowNormal);
            NativeWindow.UpdateWindow(_window);
            started = true;
            _started.Set();
            DiagnosticLog.Write(
                $"Отдельное окно потока открыто: "
                + $"renderer=0x{_window.ToInt64():X}; "
                + $"surface={_width}x{_height}.");

            SharedMatrixScene? presentedScene = initial.Scene;
            long presentedVersion = initial.Scene.Version;
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
                    {
                        userInitiated =
                            Volatile.Read(ref _programmaticClose) == 0;
                        return;
                    }
                    NativeWindow.TranslateMessage(ref message);
                    NativeWindow.DispatchMessage(ref message);
                }

                MatrixScenePresentation? frame = _frameProvider();
                if (frame is not null)
                {
                    long version = frame.Scene.Version;
                    if (!ReferenceEquals(presentedScene, frame.Scene)
                        || version != presentedVersion)
                    {
                        presenter.Present(
                            _width,
                            _height,
                            [Transform(frame)]);
                        presentedScene = frame.Scene;
                        presentedVersion = version;
                    }
                    int frameRate = Math.Clamp(
                        frame.Scene.PresentationFramesPerSecond,
                        20,
                        60);
                    _cancellation.Token.WaitHandle.WaitOne(
                        Math.Max(2, 1000 / frameRate));
                }
                else
                {
                    _cancellation.Token.WaitHandle.WaitOne(25);
                }
            }
        }
        catch (Exception exception)
        {
            _startupError = exception;
            if (!started)
                _started.Set();
            ReportFailure(
                started
                    ? "Отдельное окно потока аварийно остановлено."
                    : "Не удалось запустить отдельное окно потока.",
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
                Closed?.Invoke(userInitiated);
            }
            catch
            {
                // Native teardown must not depend on its observer.
            }
            DiagnosticLog.Write("Отдельное окно потока закрыто.");
        }
    }

    private MatrixScenePresentation? WaitForInitialFrame(
        TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!_cancellation.IsCancellationRequested
               && clock.Elapsed < timeout)
        {
            MatrixScenePresentation? frame = _frameProvider();
            if (frame is not null)
                return frame;
            _cancellation.Token.WaitHandle.WaitOne(25);
        }
        return null;
    }

    private MatrixScenePresentation Transform(
        MatrixScenePresentation frame)
    {
        DrawingRectangle source = frame.SourceBounds;
        double sourceAspect =
            source.Width / (double)Math.Max(1, source.Height);
        double targetAspect = _width / (double)_height;
        if (sourceAspect > targetAspect)
        {
            int width = Math.Max(
                1,
                (int)Math.Round(source.Height * targetAspect));
            source = new DrawingRectangle(
                source.Left + (source.Width - width) / 2,
                source.Top,
                width,
                source.Height);
        }
        else if (sourceAspect < targetAspect)
        {
            int height = Math.Max(
                1,
                (int)Math.Round(source.Width / targetAspect));
            source = new DrawingRectangle(
                source.Left,
                source.Top + (source.Height - height) / 2,
                source.Width,
                height);
        }
        return frame with
        {
            TargetBounds = new DrawingRectangle(
                0,
                0,
                _width,
                _height),
            SourceBounds = source
        };
    }

    private void OnNativeClose()
    {
        if (Volatile.Read(ref _programmaticClose) == 0)
            _cancellation.Cancel();
    }

    private void ReportFailure(string context, Exception exception)
    {
        DiagnosticLog.Write(context, exception);
        try
        {
            _failureHandler?.Invoke(context, exception, false);
        }
        catch
        {
            // Capture output must never break the desktop renderer.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        RequestClose();
        WaitForClose(TimeSpan.FromSeconds(3));
        _started.Dispose();
        _closed.Dispose();
        _cancellation.Dispose();
    }

    private static class NativeWindow
    {
        private const string ClassName =
            "WallpaperMatrix.VirtualOutput.1";
        private const uint WindowStyle =
            0x00C00000 | 0x00080000 | 0x00020000;
        private const uint ExtendedStyle =
            0x00040000 | 0x00200000;
        private const int ArrowCursor = 32512;
        private const int BlackBrush = 4;
        private const int ClassAlreadyExists = 1410;
        private const uint DestroyMessage = 0x0002;
        private const uint PaintMessage = 0x000F;
        private const uint EraseBackgroundMessage = 0x0014;
        private const uint SetIconMessage = 0x0080;
        private const int IconSmall = 0;
        private const int IconBig = 1;
        private static readonly object RegistrationLock = new();
        private static readonly ConcurrentDictionary<
            IntPtr,
            WeakReference<VirtualOutputWindow>> Owners = new();
        private static readonly WindowProcedure WindowProcedureDelegate =
            WindowProc;
        private static IntPtr _largeIcon;
        private static IntPtr _smallIcon;
        private static bool _registered;

        public const int ShowNormal = 1;
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
                    WindowProcedure = WindowProcedureDelegate,
                    Instance = GetModuleHandle(null),
                    Icon = _largeIcon,
                    Cursor = LoadCursor(
                        IntPtr.Zero,
                        new IntPtr(ArrowCursor)),
                    Background = GetStockObject(BlackBrush),
                    ClassName = ClassName,
                    SmallIcon = _smallIcon
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

        public static IntPtr Create(
            int clientWidth,
            int clientHeight,
            string title)
        {
            NativeRect outer = new()
            {
                Right = clientWidth,
                Bottom = clientHeight
            };
            AdjustWindowRectEx(
                ref outer,
                WindowStyle,
                false,
                ExtendedStyle);
            int width = outer.Right - outer.Left;
            int height = outer.Bottom - outer.Top;
            DrawingRectangle workingArea =
                System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                ?? System.Windows.Forms.SystemInformation.WorkingArea;
            int x = workingArea.Left
                + Math.Max(0, (workingArea.Width - width) / 2);
            int y = workingArea.Top
                + Math.Max(0, (workingArea.Height - height) / 2);
            IntPtr window = CreateWindowEx(
                ExtendedStyle,
                ClassName,
                title,
                WindowStyle,
                x,
                y,
                width,
                height,
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

        public static void RegisterOwner(
            IntPtr window,
            VirtualOutputWindow owner) =>
            Owners[window] = new WeakReference<VirtualOutputWindow>(owner);

        public static void UnregisterOwner(IntPtr window) =>
            Owners.TryRemove(window, out _);

        private static IntPtr WindowProc(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam)
        {
            switch (message)
            {
                case CloseMessage:
                    if (Owners.TryGetValue(
                            window,
                            out WeakReference<VirtualOutputWindow>? reference)
                        && reference.TryGetTarget(
                            out VirtualOutputWindow? owner))
                    {
                        owner.OnNativeClose();
                    }
                    DestroyWindow(window);
                    return IntPtr.Zero;
                case DestroyMessage:
                    Owners.TryRemove(window, out _);
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                case EraseBackgroundMessage:
                    return new IntPtr(1);
                case PaintMessage:
                    BeginPaint(window, out PaintStruct paint);
                    EndPaint(window, ref paint);
                    return IntPtr.Zero;
                default:
                    return DefWindowProc(
                        window,
                        message,
                        wParam,
                        lParam);
            }
        }

        private static void EnsureApplicationIcons()
        {
            if (_largeIcon != IntPtr.Zero || _smallIcon != IntPtr.Zero)
                return;
            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                return;
            ExtractIconEx(
                executablePath,
                0,
                out _largeIcon,
                out _smallIcon,
                1);
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
        private static extern bool AdjustWindowRectEx(
            ref NativeRect rectangle,
            uint style,
            bool hasMenu,
            uint extendedStyle);

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
        private static extern IntPtr DefWindowProc(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(
            IntPtr instance,
            IntPtr cursorName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int objectIndex);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(
            IntPtr window,
            int command);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr window);

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
        public static extern bool TranslateMessage(
            ref NativeMessage message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(
            ref NativeMessage message);

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
