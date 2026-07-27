using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WallpaperMatrix.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WallpaperMatrix.Native;

internal static class DesktopHost
{
    private enum DesktopSurfaceKind
    {
        None,
        DedicatedWorker,
        RaisedDesktop,
        DefViewBackground
    }

    private static readonly object HostLock = new();
    private static IntPtr _cachedHost;
    private static DesktopSurfaceKind _cachedSurfaceKind;
    private static IntPtr _preparedIconView;
    private static IntPtr _originalIconBackground;
    private static IntPtr _originalIconTextBackground;
    private static IntPtr _originalIconExtendedStyle;
    private static IntPtr _preparedDefView;
    private static IntPtr _originalDefViewStyle;
    private static IntPtr _raisedDesktopDefView;
    private static IntPtr _raisedDesktopWorker;
    private static IntPtr _raisedDesktopRenderer;
    private static long _nextRaisedDesktopMaintenanceTick;
    private const uint SpawnWorkerMessage = 0x052C;
    private const uint SettingChangeMessage = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SpiSetDesktopWallpaper = 0x0014;
    private const uint SpiGetDesktopWallpaper = 0x0073;
    private const uint SpifSendChange = 0x0002;
    private const int ShowHide = 0;
    private const int ShowNoActivate = 4;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsPopup = 0x80000000L;
    private const long WsChild = 0x40000000L;
    private const long WsClipSiblings = 0x04000000L;
    private const long WsClipChildren = 0x02000000L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExNoRedirectionBitmap = 0x00200000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint LwaAlpha = 0x00000002;
    private const byte FullyOpaque = 255;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint GwHwndNext = 2;
    private const uint GwHwndPrevious = 3;
    private const uint LvmGetBackgroundColor = 0x1000;
    private const uint LvmSetBackgroundColor = 0x1001;
    private const uint LvmGetTextBackgroundColor = 0x1025;
    private const uint LvmSetTextBackgroundColor = 0x1026;
    private const uint LvmSetExtendedStyle = 0x1036;
    private const uint LvmGetExtendedStyle = 0x1037;
    private const long LvsExTransparentBackground = 0x00400000;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr ColorNone = new(-1);

    public static bool Attach(IntPtr window, DrawingRectangle monitorBounds)
    {
        IntPtr host = FindDesktopHost(out DesktopSurfaceKind surfaceKind);
        if (host == IntPtr.Zero)
        {
            DiagnosticLog.Write(
                "Встраивание вывода отменено: Explorer не предоставил безопасный "
                + "слой DedicatedWorker, RaisedDesktop или SHELLDLL_DefView.");
            return false;
        }

        long style = GetWindowLong(window, GwlStyle).ToInt64();
        style = (style & ~WsPopup)
            | WsChild
            | WsClipChildren
            | WsClipSiblings;
        if (!TrySetWindowLong(window, GwlStyle, new IntPtr(style), out int styleError))
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: не удалось изменить стиль окна "
                + $"{FormatHandle(window)}; Win32={styleError}.");
            return false;
        }

        long exStyle = GetWindowLong(window, GwlExStyle).ToInt64();
        exStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
        if (surfaceKind is
                DesktopSurfaceKind.DefViewBackground
                or DesktopSurfaceKind.RaisedDesktop)
            exStyle |= WsExLayered;
        if (!TrySetWindowLong(window, GwlExStyle, new IntPtr(exStyle), out int exStyleError))
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: не удалось изменить расширенный стиль окна "
                + $"{FormatHandle(window)}; Win32={exStyleError}.");
            return false;
        }
        if ((surfaceKind is
                DesktopSurfaceKind.DefViewBackground
                or DesktopSurfaceKind.RaisedDesktop)
            && !SetLayeredWindowAttributes(
                window,
                0,
                FullyOpaque,
                LwaAlpha))
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: не удалось создать отдельный "
                + $"композитный слой для окна {FormatHandle(window)}; "
                + $"Win32={Marshal.GetLastPInvokeError()}.");
            return false;
        }
        Marshal.SetLastPInvokeError(0);
        SetParent(window, host);
        int parentError = Marshal.GetLastPInvokeError();
        IntPtr actualParent = GetParent(window);
        if (actualParent != host)
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: SetParent не закрепил окно "
                + $"{FormatHandle(window)} в {FormatHandle(host)}; "
                + $"actualParent={FormatHandle(actualParent)}; Win32={parentError}.");
            return false;
        }

        if (!GetWindowRect(host, out NativeRect hostRect)
            || hostRect.Right <= hostRect.Left
            || hostRect.Bottom <= hostRect.Top)
        {
            int error = Marshal.GetLastPInvokeError();
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: слой {FormatHandle(host)} сообщил "
                + $"некорректную геометрию {FormatRect(hostRect)}; Win32={error}.");
            return false;
        }

        int x = monitorBounds.Left - hostRect.Left;
        int y = monitorBounds.Top - hostRect.Top;
        if (surfaceKind == DesktopSurfaceKind.RaisedDesktop)
            EnsureRaisedDesktopWorkerAtBottom();
        IntPtr insertAfter = surfaceKind switch
        {
            DesktopSurfaceKind.DefViewBackground => HwndBottom,
            DesktopSurfaceKind.RaisedDesktop => _raisedDesktopDefView,
            _ => HwndTop
        };
        if (!SetWindowPos(
                window,
                insertAfter,
                x,
                y,
                monitorBounds.Width,
                monitorBounds.Height,
                SwpNoActivate | SwpFrameChanged))
        {
            int error = Marshal.GetLastPInvokeError();
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: SetWindowPos не разместил окно "
                + $"{FormatHandle(window)}; Win32={error}.");
            return false;
        }

        if (!GetClientRect(window, out NativeRect clientRect)
            || clientRect.Right - clientRect.Left != monitorBounds.Width
            || clientRect.Bottom - clientRect.Top != monitorBounds.Height)
        {
            int error = Marshal.GetLastPInvokeError();
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: клиентская область окна "
                + $"{FormatHandle(window)} имеет размер "
                + $"{clientRect.Right - clientRect.Left}x{clientRect.Bottom - clientRect.Top}, "
                + $"ожидалось {monitorBounds.Width}x{monitorBounds.Height}; Win32={error}.");
            return false;
        }

        if (surfaceKind == DesktopSurfaceKind.DefViewBackground
            && !IsBehindIconView(window, host))
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: окно {FormatHandle(window)} "
                + $"не удалось подтвердить под веткой значков SHELLDLL_DefView "
                + $"{FormatHandle(host)}.");
            return false;
        }
        if (surfaceKind == DesktopSurfaceKind.RaisedDesktop
            && !IsRaisedDesktopRendererPlacement(
                window,
                host,
                _raisedDesktopDefView,
                _raisedDesktopWorker))
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: окно {FormatHandle(window)} "
                + $"не удалось разместить между SHELLDLL_DefView "
                + $"{FormatHandle(_raisedDesktopDefView)} и WorkerW "
                + $"{FormatHandle(_raisedDesktopWorker)}.");
            return false;
        }
        if (surfaceKind == DesktopSurfaceKind.RaisedDesktop)
        {
            _raisedDesktopRenderer = window;
            _nextRaisedDesktopMaintenanceTick =
                Environment.TickCount64 + 1250;
        }
        if (surfaceKind == DesktopSurfaceKind.DefViewBackground
            && !PrepareDefViewComposition(host))
        {
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: SHELLDLL_DefView "
                + $"{FormatHandle(host)} не принял режим отсечения фоновой "
                + "отрисовки под дочерним окном.");
            return false;
        }
        if (surfaceKind == DesktopSurfaceKind.DefViewBackground
            && !PrepareTransparentIconView(host))
        {
            RestoreDefViewComposition();
            DiagnosticLog.Write(
                $"Встраивание вывода отменено: фон списка значков "
                + $"{FormatHandle(FindDescendantWindow(host, "SysListView32"))} "
                + "не удалось перевести в прозрачный режим.");
            return false;
        }

        bool zOrderVerified = surfaceKind switch
        {
            DesktopSurfaceKind.DefViewBackground =>
                IsBehindIconView(window, host),
            DesktopSurfaceKind.RaisedDesktop =>
                IsRaisedDesktopRendererPlacement(
                    window,
                    host,
                    _raisedDesktopDefView,
                    _raisedDesktopWorker),
            _ => true
        };
        string iconBackgroundState =
            surfaceKind == DesktopSurfaceKind.DefViewBackground
                ? IsTransparentIconViewPrepared(host).ToString()
                : "not-required";
        string defViewCompositionState =
            surfaceKind == DesktopSurfaceKind.DefViewBackground
                ? IsDefViewCompositionPrepared(host).ToString()
                : "not-required";
        DiagnosticLog.Write(
            $"Слой Explorer выбран: host={FormatHandle(host)} "
            + $"class={GetWindowClass(host)} surface={surfaceKind} "
            + $"visibleBeforeActivation={IsWindowVisible(host)} rect={FormatRect(hostRect)}; "
            + $"renderer={FormatHandle(window)} parentVerified=True "
            + $"zOrderVerified={zOrderVerified} "
            + $"iconBackgroundTransparent={iconBackgroundState} "
            + $"defViewComposition={defViewCompositionState} "
            + $"client={clientRect.Right - clientRect.Left}x{clientRect.Bottom - clientRect.Top} "
            + $"monitor={FormatBounds(monitorBounds)}.");
        return true;
    }

    public static void LogAttachmentState(
        IntPtr window,
        DrawingRectangle expectedBounds,
        string stage)
    {
        IntPtr parent = window == IntPtr.Zero
            ? IntPtr.Zero
            : GetParent(window);
        NativeRect windowRect = default;
        NativeRect clientRect = default;
        NativeRect parentRect = default;
        bool windowRectAvailable = window != IntPtr.Zero
            && GetWindowRect(window, out windowRect);
        bool clientRectAvailable = window != IntPtr.Zero
            && GetClientRect(window, out clientRect);
        bool parentRectAvailable = parent != IntPtr.Zero
            && GetWindowRect(parent, out parentRect);
        bool zOrderVerified = _cachedSurfaceKind switch
        {
            DesktopSurfaceKind.DefViewBackground =>
                IsBehindIconView(window, parent),
            DesktopSurfaceKind.RaisedDesktop =>
                IsRaisedDesktopRendererPlacement(
                    window,
                    parent,
                    _raisedDesktopDefView,
                    _raisedDesktopWorker),
            _ => true
        };
        string iconBackgroundState =
            _cachedSurfaceKind == DesktopSurfaceKind.DefViewBackground
                ? IsTransparentIconViewPrepared(parent).ToString()
                : "not-required";
        string defViewCompositionState =
            _cachedSurfaceKind == DesktopSurfaceKind.DefViewBackground
                ? IsDefViewCompositionPrepared(parent).ToString()
                : "not-required";

        DiagnosticLog.Write(
            $"Проверка слоя ({stage}): renderer={FormatHandle(window)} "
            + $"exists={window != IntPtr.Zero && IsWindow(window)} "
            + $"visible={window != IntPtr.Zero && IsWindowVisible(window)} "
            + $"rect={(windowRectAvailable ? FormatRect(windowRect) : "недоступен")} "
            + $"client={(clientRectAvailable ? FormatRect(clientRect) : "недоступен")} "
            + $"expected={FormatBounds(expectedBounds)}; "
            + $"parent={FormatHandle(parent)} class={GetWindowClass(parent)} "
            + $"visible={parent != IntPtr.Zero && IsWindowVisible(parent)} "
            + $"rect={(parentRectAvailable ? FormatRect(parentRect) : "недоступен")} "
            + $"cachedHost={FormatHandle(_cachedHost)} "
            + $"surface={_cachedSurfaceKind} "
            + $"raisedDefView={FormatHandle(_raisedDesktopDefView)} "
            + $"raisedWorker={FormatHandle(_raisedDesktopWorker)} "
            + $"zOrderVerified={zOrderVerified} "
            + $"iconBackgroundTransparent={iconBackgroundState} "
            + $"defViewComposition={defViewCompositionState} "
            + $"parentMatches={parent != IntPtr.Zero && parent == _cachedHost}.");
    }

    public static bool IsAttachmentVisible(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window))
            return false;
        lock (HostLock)
        {
            bool attached = _cachedHost != IntPtr.Zero
                && IsWindow(_cachedHost)
                && IsWindowVisible(_cachedHost)
                && GetParent(window) == _cachedHost;
            return attached
                && (_cachedSurfaceKind switch
                {
                    DesktopSurfaceKind.DefViewBackground =>
                        IsBehindIconView(window, _cachedHost)
                        && IsTransparentIconViewPrepared(_cachedHost)
                        && IsDefViewCompositionPrepared(_cachedHost),
                    DesktopSurfaceKind.RaisedDesktop =>
                        IsRaisedDesktopRendererPlacement(
                            window,
                            _cachedHost,
                            _raisedDesktopDefView,
                            _raisedDesktopWorker),
                    _ => true
                });
        }
    }

    public static void MaintainDesktopPlacement(IntPtr window)
    {
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextRaisedDesktopMaintenanceTick))
            return;
        Volatile.Write(
            ref _nextRaisedDesktopMaintenanceTick,
            now + 1250);

        lock (HostLock)
        {
            if (_cachedSurfaceKind != DesktopSurfaceKind.RaisedDesktop
                || window == IntPtr.Zero
                || !IsWindow(window)
                || _cachedHost == IntPtr.Zero
                || !IsWindow(_cachedHost)
                || _raisedDesktopDefView == IntPtr.Zero
                || !IsWindow(_raisedDesktopDefView)
                || _raisedDesktopWorker == IntPtr.Zero
                || !IsWindow(_raisedDesktopWorker))
            {
                return;
            }

            bool workerCorrected = EnsureRaisedDesktopWorkerAtBottom();
            bool rendererCorrected = false;
            if (!IsRaisedDesktopRendererPlacement(
                    window,
                    _cachedHost,
                    _raisedDesktopDefView,
                    _raisedDesktopWorker))
            {
                rendererCorrected = SetWindowPos(
                    window,
                    _raisedDesktopDefView,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove
                    | SwpNoSize
                    | SwpNoActivate);
            }

            if (workerCorrected || rendererCorrected)
            {
                DiagnosticLog.Write(
                    $"Z-порядок RaisedDesktop восстановлен: "
                    + $"renderer={FormatHandle(window)}; "
                    + $"progman={FormatHandle(_cachedHost)}; "
                    + $"defView={FormatHandle(_raisedDesktopDefView)}; "
                    + $"worker={FormatHandle(_raisedDesktopWorker)}; "
                    + $"workerCorrected={workerCorrected}; "
                    + $"rendererCorrected={rendererCorrected}.");
            }
        }
    }

    public static void HideWallpaperSurface()
    {
        lock (HostLock)
        {
            if (_cachedSurfaceKind == DesktopSurfaceKind.DedicatedWorker
                && _cachedHost != IntPtr.Zero
                && IsWindow(_cachedHost))
            {
                bool accepted = ShowWindowAsync(_cachedHost, ShowHide);
                DiagnosticLog.Write(
                    $"Слой WorkerW скрыт: host={FormatHandle(_cachedHost)} "
                    + $"surface={_cachedSurfaceKind} "
                    + $"commandAccepted={accepted} visibleNow={IsWindowVisible(_cachedHost)}.");
            }
            else if (_cachedSurfaceKind == DesktopSurfaceKind.DefViewBackground)
            {
                RestoreIconViewBackground();
                RestoreDefViewComposition();
                DiagnosticLog.Write(
                    $"Фоновый слой SHELLDLL_DefView скрыт: host={FormatHandle(_cachedHost)}; "
                    + "фон списка значков и стили Explorer восстановлены.");
            }
            else if (_cachedSurfaceKind == DesktopSurfaceKind.RaisedDesktop)
            {
                DiagnosticLog.Write(
                    $"Слой RaisedDesktop скрыт вместе с окном рендера: "
                    + $"progman={FormatHandle(_cachedHost)}; "
                    + "окна Explorer не изменялись.");
            }
        }
    }

    public static void ShowWallpaperSurface()
    {
        lock (HostLock)
        {
            if (_cachedSurfaceKind == DesktopSurfaceKind.DedicatedWorker
                && _cachedHost != IntPtr.Zero
                && IsWindow(_cachedHost))
            {
                bool accepted = ShowWindowAsync(_cachedHost, ShowNoActivate);
                DiagnosticLog.Write(
                    $"Слой WorkerW показан: host={FormatHandle(_cachedHost)} "
                    + $"surface={_cachedSurfaceKind} "
                    + $"commandAccepted={accepted} "
                    + $"visibleNow={IsWindowVisible(_cachedHost)}.");
            }
            else if (_cachedSurfaceKind == DesktopSurfaceKind.DefViewBackground
                && _cachedHost != IntPtr.Zero
                && IsWindow(_cachedHost))
            {
                bool composition = PrepareDefViewComposition(_cachedHost);
                bool transparent = composition
                    && PrepareTransparentIconView(_cachedHost);
                DiagnosticLog.Write(
                    $"Фоновый слой SHELLDLL_DefView готов: host={FormatHandle(_cachedHost)} "
                    + $"visible={IsWindowVisible(_cachedHost)}; "
                    + $"iconBackgroundTransparent={transparent}; "
                    + $"defViewComposition={composition}; "
                    + "видимость управляется окнами рендера.");
            }
            else if (_cachedSurfaceKind == DesktopSurfaceKind.RaisedDesktop
                && _cachedHost != IntPtr.Zero
                && IsWindow(_cachedHost))
            {
                bool workerCorrected =
                    EnsureRaisedDesktopWorkerAtBottom();
                bool rendererCorrected = false;
                if (_raisedDesktopRenderer != IntPtr.Zero
                    && IsWindow(_raisedDesktopRenderer)
                    && !IsRaisedDesktopRendererPlacement(
                        _raisedDesktopRenderer,
                        _cachedHost,
                        _raisedDesktopDefView,
                        _raisedDesktopWorker))
                {
                    rendererCorrected = SetWindowPos(
                        _raisedDesktopRenderer,
                        _raisedDesktopDefView,
                        0,
                        0,
                        0,
                        0,
                        SwpNoMove
                        | SwpNoSize
                        | SwpNoActivate);
                }
                DiagnosticLog.Write(
                    $"Слой RaisedDesktop готов: "
                    + $"progman={FormatHandle(_cachedHost)}; "
                    + $"defView={FormatHandle(_raisedDesktopDefView)}; "
                    + $"worker={FormatHandle(_raisedDesktopWorker)}; "
                    + $"workerCorrected={workerCorrected}; "
                    + $"rendererCorrected={rendererCorrected}; "
                    + "значки и ввод остаются в SHELLDLL_DefView.");
            }
            else
            {
                DiagnosticLog.Write(
                    $"Показ слоя Explorer невозможен: host={FormatHandle(_cachedHost)} "
                    + $"exists={_cachedHost != IntPtr.Zero && IsWindow(_cachedHost)} "
                    + $"visible={_cachedHost != IntPtr.Zero && IsWindowVisible(_cachedHost)}.");
            }
        }
    }

    public static void RefreshDesktopSurface(bool restoreSystemWallpaper)
    {
        lock (HostLock)
        {
            const uint redrawFlags = 0x0001  // RDW_INVALIDATE
                | 0x0004                    // RDW_ERASE
                | 0x0080                    // RDW_ALLCHILDREN
                | 0x0100                    // RDW_UPDATENOW
                | 0x0200                    // RDW_ERASENOW
                | 0x0400;                   // RDW_FRAME

            if (restoreSystemWallpaper)
            {
                RestoreIconViewBackground();
                RestoreDefViewComposition();
                bool raisedDesktop =
                    _cachedSurfaceKind == DesktopSurfaceKind.RaisedDesktop;
                // The dedicated WorkerW is an empty shell surface created for
                // animated wallpaper. Hiding it reveals Explorer's untouched
                // wallpaper surface instead of leaving its last composed frame.
                if (_cachedSurfaceKind == DesktopSurfaceKind.DedicatedWorker
                    && _cachedHost != IntPtr.Zero
                    && IsWindow(_cachedHost))
                    ShowWindowAsync(_cachedHost, ShowHide);

                if (raisedDesktop)
                {
                    if (_raisedDesktopDefView != IntPtr.Zero
                        && IsWindow(_raisedDesktopDefView))
                    {
                        InvalidateRect(
                            _raisedDesktopDefView,
                            IntPtr.Zero,
                            erase: false);
                    }
                    DiagnosticLog.Write(
                        "RaisedDesktop отключён: окно рендера удалено; "
                        + "системные обои и окна Explorer не перестраивались.");
                }
                else
                {
                    SendNotifyMessageString(
                        new IntPtr(0xFFFF),
                        SettingChangeMessage,
                        new IntPtr(SpiSetDesktopWallpaper),
                        "Control Panel\\Desktop");

                    RedrawExplorerSurfaces(redrawFlags);
                    ReapplyCurrentSystemWallpaper();
                    RedrawExplorerSurfaces(redrawFlags);
                }

                _cachedHost = IntPtr.Zero;
                _cachedSurfaceKind = DesktopSurfaceKind.None;
                _raisedDesktopDefView = IntPtr.Zero;
                _raisedDesktopWorker = IntPtr.Zero;
                _raisedDesktopRenderer = IntPtr.Zero;
                return;
            }

            RedrawExplorerSurfaces(redrawFlags);
        }
    }

    private static void RedrawExplorerSurfaces(uint redrawFlags)
    {
        EnumWindows((topLevel, _) =>
        {
            string className = GetWindowClass(topLevel);
            if (className is "WorkerW" or "Progman")
            {
                InvalidateRect(topLevel, IntPtr.Zero, erase: true);
                RedrawWindow(topLevel, IntPtr.Zero, IntPtr.Zero, redrawFlags);

                IntPtr shellView = FindDescendantWindow(
                    topLevel,
                    "SHELLDLL_DefView");
                if (shellView != IntPtr.Zero)
                {
                    InvalidateRect(shellView, IntPtr.Zero, erase: true);
                    RedrawWindow(
                        shellView,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        redrawFlags);
                }
            }
            return true;
        }, IntPtr.Zero);

        IntPtr desktop = GetDesktopWindow();
        if (desktop != IntPtr.Zero)
        {
            InvalidateRect(desktop, IntPtr.Zero, erase: true);
            RedrawWindow(desktop, IntPtr.Zero, IntPtr.Zero, redrawFlags);
        }
    }

    private static void ReapplyCurrentSystemWallpaper()
    {
        StringBuilder wallpaperPath = new(32768);
        if (!SystemParametersInfoGet(
                SpiGetDesktopWallpaper,
                (uint)wallpaperPath.Capacity,
                wallpaperPath,
                0))
        {
            DiagnosticLog.Write(
                $"Не удалось запросить текущие системные обои; "
                + $"Win32={Marshal.GetLastPInvokeError()}.");
            return;
        }

        string path = wallpaperPath.ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            DiagnosticLog.Write(
                "Системные обои используют сплошной цвет или управляются оболочкой; "
                + "выполнена принудительная перерисовка Explorer.");
            return;
        }

        bool reapplied = SystemParametersInfoSet(
            SpiSetDesktopWallpaper,
            0,
            path,
            SpifSendChange);
        DiagnosticLog.Write(
            $"Восстановление системных обоев: reapplied={reapplied}; "
            + $"path={path}; Win32={(reapplied ? 0 : Marshal.GetLastPInvokeError())}.");
    }

    private static IntPtr FindDesktopHost(out DesktopSurfaceKind surfaceKind)
    {
        lock (HostLock)
        {
            if (_cachedHost != IntPtr.Zero
                && IsWindow(_cachedHost)
                && (_cachedSurfaceKind != DesktopSurfaceKind.DefViewBackground
                    || IsSafeDefViewHost(_cachedHost))
                && (_cachedSurfaceKind != DesktopSurfaceKind.RaisedDesktop
                    || IsSafeRaisedDesktop(
                        _cachedHost,
                        _raisedDesktopDefView,
                        _raisedDesktopWorker)))
            {
                surfaceKind = _cachedSurfaceKind;
                return _cachedHost;
            }

            RestoreIconViewBackground();
            RestoreDefViewComposition();
            _cachedHost = IntPtr.Zero;
            _cachedSurfaceKind = DesktopSurfaceKind.None;
            _raisedDesktopDefView = IntPtr.Zero;
            _raisedDesktopWorker = IntPtr.Zero;
            _raisedDesktopRenderer = IntPtr.Zero;
            surfaceKind = DesktopSurfaceKind.None;
            IntPtr progman = FindWindow("Progman", null);

            HashSet<IntPtr> workersBeforeRequest = EnumerateTopLevelWindows()
                .Where(window => GetWindowClass(window) == "WorkerW")
                .ToHashSet();

            string classicRequest = "not-sent:no-progman";
            IntPtr worker = FindWorkerWindow(workersBeforeRequest);
            IntPtr raisedWorker = IntPtr.Zero;
            if (progman != IntPtr.Zero)
            {
                classicRequest = SendWorkerRequest(
                    progman,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    "classic");
                worker = WaitForWorkerWindow(
                    workersBeforeRequest,
                    TimeSpan.FromMilliseconds(600));
                raisedWorker = FindDescendantWindow(
                    progman,
                    "WorkerW");
            }
            string modernPrepareRequest = "not-sent";
            string modernCommitRequest = "not-sent";
            if (progman != IntPtr.Zero && worker == IntPtr.Zero)
            {
                // A nested WorkerW is not proof that its pixels are actually
                // composited. Some Windows 11 builds keep it behind an opaque
                // DefView wallpaper surface. Always exhaust the modern request
                // before accepting any fallback.
                // Windows 11 expects the modern pair in the documented
                // prepare (0), then commit (1) order.
                modernPrepareRequest = SendWorkerRequest(
                    progman,
                    new IntPtr(0xD),
                    IntPtr.Zero,
                    "modern-prepare");
                modernCommitRequest = SendWorkerRequest(
                    progman,
                    new IntPtr(0xD),
                    new IntPtr(1),
                    "modern-commit");
                worker = WaitForWorkerWindow(
                    workersBeforeRequest,
                    TimeSpan.FromSeconds(2));
                raisedWorker = FindDescendantWindow(
                    progman,
                    "WorkerW");
            }

            IntPtr defView = FindDescendantWindow(
                progman,
                "SHELLDLL_DefView");
            if (defView == IntPtr.Zero)
            {
                foreach (IntPtr desktopWindow in EnumerateTopLevelWindows())
                {
                    if (GetWindowClass(desktopWindow) is not ("Progman" or "WorkerW"))
                        continue;
                    defView = FindDescendantWindow(
                        desktopWindow,
                        "SHELLDLL_DefView");
                    if (defView != IntPtr.Zero)
                        break;
                }
            }
            bool raisedDesktopComposition = HasRaisedDesktopComposition(
                progman,
                defView);
            bool raisedDesktopDetected = IsRaisedDesktopShell(
                progman,
                defView,
                raisedWorker);
            bool defViewSafe = !raisedDesktopComposition
                && IsSafeDefViewHost(defView);
            bool raisedDesktopSafe = IsSafeRaisedDesktop(
                progman,
                defView,
                raisedWorker);
            if (IsUsableHost(worker))
            {
                _cachedHost = worker;
                _cachedSurfaceKind = DesktopSurfaceKind.DedicatedWorker;
            }
            else if (raisedDesktopSafe)
            {
                // Windows 11 raised desktop: Progman owns a layered DefView
                // with the real icons and a child WorkerW with the static
                // wallpaper. Our own layered child belongs between them.
                _cachedHost = progman;
                _cachedSurfaceKind = DesktopSurfaceKind.RaisedDesktop;
                _raisedDesktopDefView =
                    FindDirectChildBranch(progman, defView);
                _raisedDesktopWorker =
                    FindDirectChildBranch(progman, raisedWorker);
            }
            else if (defViewSafe)
            {
                // DefView paints the static wallpaper in this topology. A
                // renderer parented directly to it sits above that background;
                // HWND_BOTTOM plus a transparent SysListView32 keeps the icons
                // and all desktop input above the renderer.
                _cachedHost = defView;
                _cachedSurfaceKind =
                    DesktopSurfaceKind.DefViewBackground;
            }
            surfaceKind = _cachedSurfaceKind;
            DiagnosticLog.Write(
                $"Запрос WorkerW: {classicRequest}; {modernPrepareRequest}; "
                + $"{modernCommitRequest}. Топология Explorer: "
                + $"{DescribeExplorerTopology()}; progman={DescribeWindow(progman)}; "
                    + $"workerCandidate={DescribeWindow(worker)}; "
                    + $"raisedWorker={DescribeWindow(raisedWorker)}; "
                    + $"raisedDesktopComposition={raisedDesktopComposition}; "
                    + $"raisedDesktopDetected={raisedDesktopDetected}; "
                    + $"raisedDesktopSafe={raisedDesktopSafe}; "
                    + $"defView={DescribeWindow(defView)}; "
                    + $"defViewSafe={defViewSafe}; "
                    + $"progmanEx=0x{GetWindowLong(progman, GwlExStyle).ToInt64():X}; "
                    + $"defViewEx=0x{GetWindowLong(defView, GwlExStyle).ToInt64():X}; "
                    + $"selected={DescribeWindow(_cachedHost)}; surface={surfaceKind}.");
            if (surfaceKind == DesktopSurfaceKind.DedicatedWorker)
            {
                // Keep the host hidden while its child windows and their first
                // DirectComposition frames are being prepared. Showing it only after all
                // monitors are ready prevents a one-frame flash of Explorer's
                // wallpaper when output is resumed.
                ShowWindowAsync(_cachedHost, ShowHide);
            }
            return _cachedHost;
        }
    }

    private static string SendWorkerRequest(
        IntPtr progman,
        IntPtr wParam,
        IntPtr lParam,
        string name)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr callResult = SendMessageTimeout(
            progman,
            SpawnWorkerMessage,
            wParam,
            lParam,
            SmtoAbortIfHung,
            1000,
            out IntPtr messageResult);
        int error = Marshal.GetLastPInvokeError();
        return $"{name}=sent:{callResult != IntPtr.Zero},"
            + $"result:0x{messageResult.ToInt64():X},Win32:{error}";
    }

    private static IntPtr WaitForWorkerWindow(
        IReadOnlySet<IntPtr> workersBeforeRequest,
        TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            IntPtr worker = FindWorkerWindow(workersBeforeRequest);
            if (worker != IntPtr.Zero)
                return worker;
            Thread.Sleep(50);
        }
        while (clock.Elapsed < timeout);

        return IntPtr.Zero;
    }

    private static IntPtr FindWorkerWindow(
        IReadOnlySet<IntPtr> workersBeforeRequest)
    {
        List<IntPtr> topLevels = EnumerateTopLevelWindows();
        List<int> defViewHosts = [];
        List<IntPtr> emptyWorkers = [];

        for (int index = 0; index < topLevels.Count; index++)
        {
            IntPtr window = topLevels[index];
            string className = GetWindowClass(window);
            bool desktopHostClass = className is "Progman" or "WorkerW";
            bool hasDefView = desktopHostClass
                && FindDescendantWindow(
                    window,
                    "SHELLDLL_DefView") != IntPtr.Zero;
            if (desktopHostClass && hasDefView)
                defViewHosts.Add(index);
            if (className == "WorkerW"
                && !hasDefView
                && IsUsableHost(window))
            {
                emptyWorkers.Add(window);
            }
        }

        // Classic Explorer places the wallpaper WorkerW immediately below the
        // top-level window that owns SHELLDLL_DefView.
        foreach (int hostIndex in defViewHosts)
        {
            for (int index = hostIndex + 1; index < topLevels.Count; index++)
            {
                IntPtr candidate = topLevels[index];
                if (GetWindowClass(candidate) == "WorkerW"
                    && emptyWorkers.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // Newer builds can publish the empty WorkerW before moving DefView into
        // its final place. A newly created, full-size empty WorkerW is safe.
        IntPtr[] newlyCreated = emptyWorkers
            .Where(window => !workersBeforeRequest.Contains(window))
            .ToArray();
        if (newlyCreated.Length == 1)
            return newlyCreated[0];

        // If Explorer exposes exactly one empty WorkerW, there is no ambiguity.
        return emptyWorkers.Count == 1 ? emptyWorkers[0] : IntPtr.Zero;
    }

    private static List<IntPtr> EnumerateTopLevelWindows()
    {
        List<IntPtr> windows = [];
        EnumWindows((window, _) =>
        {
            windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindDescendantWindow(
        IntPtr parent,
        string requestedClass)
    {
        if (parent == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (child, _) =>
        {
            if (string.Equals(
                    GetWindowClass(child),
                    requestedClass,
                    StringComparison.Ordinal))
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string DescribeExplorerTopology()
    {
        List<string> descriptions = [];
        foreach (IntPtr window in EnumerateTopLevelWindows())
        {
            string className = GetWindowClass(window);
            bool desktopHostClass = className is "Progman" or "WorkerW";
            bool hasDefView = desktopHostClass
                && FindDescendantWindow(
                    window,
                    "SHELLDLL_DefView") != IntPtr.Zero;
            bool hasRaisedWorker = className == "Progman"
                && FindDescendantWindow(
                    window,
                    "WorkerW") != IntPtr.Zero;
            if (className == "Progman"
                || hasDefView
                || (className == "WorkerW" && IsUsableHost(window)))
            {
                descriptions.Add(
                    $"{FormatHandle(window)}:{className}:"
                    + $"visible={IsWindowVisible(window)}:"
                    + $"defView={hasDefView}:"
                    + $"raisedWorker={hasRaisedWorker}");
            }
        }
        return descriptions.Count == 0
            ? "desktop-windows=none"
            : string.Join(",", descriptions);
    }

    private static bool IsSafeDefViewHost(IntPtr defView) =>
        defView != IntPtr.Zero
        && GetWindowClass(defView) == "SHELLDLL_DefView"
        && CoversDesktopWorkingAreas(defView)
        && FindDescendantWindow(defView, "SysListView32") != IntPtr.Zero;

    private static bool IsSafeRaisedDesktop(
        IntPtr progman,
        IntPtr defView,
        IntPtr worker)
    {
        if (progman == IntPtr.Zero
            || GetWindowClass(progman) != "Progman"
            || !IsUsableHost(progman)
            || !IsWindowVisible(progman)
            || defView == IntPtr.Zero
            || GetWindowClass(defView) != "SHELLDLL_DefView"
            || !IsWindowVisible(defView)
            || worker == IntPtr.Zero
            || GetWindowClass(worker) != "WorkerW"
            || !IsUsableHost(worker))
        {
            return false;
        }

        if (!IsRaisedDesktopShell(progman, defView, worker))
        {
            return false;
        }

        IntPtr defViewBranch = FindDirectChildBranch(progman, defView);
        IntPtr workerBranch = FindDirectChildBranch(
            progman,
            worker);
        if (defViewBranch == IntPtr.Zero
            || workerBranch == IntPtr.Zero
            || defViewBranch == workerBranch
            || !IsWindowVisible(defView))
        {
            return false;
        }

        for (IntPtr sibling = GetWindow(workerBranch, GwHwndPrevious);
             sibling != IntPtr.Zero;
             sibling = GetWindow(sibling, GwHwndPrevious))
        {
            if (sibling == defViewBranch)
                return true;
        }
        return false;
    }

    private static bool IsRaisedDesktopShell(
        IntPtr progman,
        IntPtr defView,
        IntPtr worker)
    {
        if (!HasRaisedDesktopComposition(progman, defView)
            || worker == IntPtr.Zero
            || GetWindowClass(worker) != "WorkerW")
        {
            return false;
        }

        return FindDirectChildBranch(progman, worker) != IntPtr.Zero;
    }

    private static bool HasRaisedDesktopComposition(
        IntPtr progman,
        IntPtr defView)
    {
        if (progman == IntPtr.Zero
            || defView == IntPtr.Zero
            || GetWindowClass(progman) != "Progman"
            || GetWindowClass(defView) != "SHELLDLL_DefView")
        {
            return false;
        }

        long progmanExStyle =
            GetWindowLong(progman, GwlExStyle).ToInt64();
        long defViewExStyle =
            GetWindowLong(defView, GwlExStyle).ToInt64();
        return (progmanExStyle & WsExNoRedirectionBitmap) != 0
            && (defViewExStyle & WsExLayered) != 0
            && FindDirectChildBranch(progman, defView) != IntPtr.Zero;
    }

    private static bool IsRaisedDesktopRendererPlacement(
        IntPtr renderer,
        IntPtr progman,
        IntPtr defView,
        IntPtr worker)
    {
        if (renderer == IntPtr.Zero
            || progman == IntPtr.Zero
            || defView == IntPtr.Zero
            || worker == IntPtr.Zero
            || GetParent(renderer) != progman
            || GetParent(defView) != progman
            || GetParent(worker) != progman)
        {
            return false;
        }

        return IsSiblingAbove(defView, renderer)
            && IsSiblingAbove(renderer, worker);
    }

    private static bool EnsureRaisedDesktopWorkerAtBottom()
    {
        IntPtr worker = _raisedDesktopWorker;
        if (worker == IntPtr.Zero
            || !IsWindow(worker)
            || GetParent(worker) != _cachedHost
            || GetWindow(worker, GwHwndNext) == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowPos(
            worker,
            HwndBottom,
            0,
            0,
            0,
            0,
            SwpNoMove
            | SwpNoSize
            | SwpNoActivate);
    }

    private static bool IsSiblingAbove(
        IntPtr upper,
        IntPtr lower)
    {
        if (upper == IntPtr.Zero
            || lower == IntPtr.Zero
            || GetParent(upper) != GetParent(lower))
        {
            return false;
        }

        for (IntPtr sibling = GetWindow(lower, GwHwndPrevious);
             sibling != IntPtr.Zero;
             sibling = GetWindow(sibling, GwHwndPrevious))
        {
            if (sibling == upper)
                return true;
        }
        return false;
    }

    private static bool IsBehindIconView(IntPtr renderer, IntPtr defView)
    {
        if (renderer == IntPtr.Zero
            || defView == IntPtr.Zero
            || GetParent(renderer) != defView)
        {
            return false;
        }

        IntPtr iconView = FindDescendantWindow(defView, "SysListView32");
        IntPtr iconBranch = FindDirectChildBranch(defView, iconView);
        if (iconBranch == IntPtr.Zero || iconBranch == renderer)
            return false;

        for (IntPtr sibling = GetWindow(renderer, GwHwndPrevious);
             sibling != IntPtr.Zero;
             sibling = GetWindow(sibling, GwHwndPrevious))
        {
            if (sibling == iconBranch)
                return true;
        }
        return false;
    }

    private static bool PrepareDefViewComposition(IntPtr defView)
    {
        if (defView == IntPtr.Zero
            || !IsWindow(defView)
            || GetWindowClass(defView) != "SHELLDLL_DefView")
        {
            return false;
        }

        if (_preparedDefView != IntPtr.Zero
            && _preparedDefView != defView)
        {
            RestoreDefViewComposition();
        }
        if (_preparedDefView == IntPtr.Zero)
        {
            _preparedDefView = defView;
            _originalDefViewStyle = GetWindowLong(defView, GwlStyle);
        }

        long desiredStyle =
            _originalDefViewStyle.ToInt64() | WsClipChildren;
        bool styleSet = TrySetWindowLong(
            defView,
            GwlStyle,
            new IntPtr(desiredStyle),
            out int styleError);
        bool frameUpdated = styleSet
            && SetWindowPos(
                defView,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove
                | SwpNoSize
                | SwpNoZOrder
                | SwpNoActivate
                | SwpFrameChanged);
        if (frameUpdated)
            RedrawWindow(defView, IntPtr.Zero, IntPtr.Zero, 0x0185);

        bool prepared = styleSet
            && frameUpdated
            && IsDefViewCompositionPrepared(defView);
        DiagnosticLog.Write(
            $"Композиция SHELLDLL_DefView: host={FormatHandle(defView)}; "
            + $"prepared={prepared}; "
            + $"originalStyle=0x{_originalDefViewStyle.ToInt64():X}; "
            + $"currentStyle=0x{GetWindowLong(defView, GwlStyle).ToInt64():X}; "
            + $"Win32={(prepared ? 0 : styleError)}.");
        if (!prepared)
            RestoreDefViewComposition();
        return prepared;
    }

    private static bool IsDefViewCompositionPrepared(IntPtr defView)
    {
        if (defView == IntPtr.Zero
            || defView != _preparedDefView
            || !IsWindow(defView))
        {
            return false;
        }

        long style = GetWindowLong(defView, GwlStyle).ToInt64();
        return (style & WsClipChildren) != 0;
    }

    private static void RestoreDefViewComposition()
    {
        IntPtr defView = _preparedDefView;
        if (defView != IntPtr.Zero && IsWindow(defView))
        {
            TrySetWindowLong(
                defView,
                GwlStyle,
                _originalDefViewStyle,
                out _);
            SetWindowPos(
                defView,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove
                | SwpNoSize
                | SwpNoZOrder
                | SwpNoActivate
                | SwpFrameChanged);
            RedrawWindow(defView, IntPtr.Zero, IntPtr.Zero, 0x0185);
        }

        _preparedDefView = IntPtr.Zero;
        _originalDefViewStyle = IntPtr.Zero;
    }

    private static bool PrepareTransparentIconView(IntPtr defView)
    {
        IntPtr iconView = FindDescendantWindow(
            defView,
            "SysListView32");
        if (iconView == IntPtr.Zero || !IsWindow(iconView))
            return false;

        if (_preparedIconView != IntPtr.Zero
            && _preparedIconView != iconView)
        {
            RestoreIconViewBackground();
        }
        if (_preparedIconView == IntPtr.Zero)
        {
            if (!TrySendListViewMessage(
                    iconView,
                    LvmGetBackgroundColor,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _originalIconBackground)
                || !TrySendListViewMessage(
                    iconView,
                    LvmGetTextBackgroundColor,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _originalIconTextBackground)
                || !TrySendListViewMessage(
                    iconView,
                    LvmGetExtendedStyle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _originalIconExtendedStyle))
            {
                return false;
            }
            _preparedIconView = iconView;
        }

        bool backgroundSent = TrySendListViewMessage(
            iconView,
            LvmSetBackgroundColor,
            IntPtr.Zero,
            ColorNone,
            out IntPtr backgroundResult);
        bool textBackgroundSent = TrySendListViewMessage(
            iconView,
            LvmSetTextBackgroundColor,
            IntPtr.Zero,
            ColorNone,
            out IntPtr textBackgroundResult);
        long extendedStyle =
            _originalIconExtendedStyle.ToInt64()
            | LvsExTransparentBackground;
        bool styleSent = TrySendListViewMessage(
            iconView,
            LvmSetExtendedStyle,
            new IntPtr(LvsExTransparentBackground),
            new IntPtr(extendedStyle),
            out _);
        InvalidateRect(iconView, IntPtr.Zero, erase: true);

        bool prepared = backgroundSent
            && textBackgroundSent
            && styleSent
            && backgroundResult != IntPtr.Zero
            && textBackgroundResult != IntPtr.Zero
            && IsTransparentIconViewPrepared(defView);
        TrySendListViewMessage(
            iconView,
            LvmGetBackgroundColor,
            IntPtr.Zero,
            IntPtr.Zero,
            out IntPtr currentBackground);
        TrySendListViewMessage(
            iconView,
            LvmGetExtendedStyle,
            IntPtr.Zero,
            IntPtr.Zero,
            out IntPtr currentExtendedStyle);
        DiagnosticLog.Write(
            $"Фон списка значков: iconView={FormatHandle(iconView)}; "
            + $"prepared={prepared}; "
            + $"originalBk=0x{_originalIconBackground.ToInt64():X}; "
            + $"originalTextBk=0x{_originalIconTextBackground.ToInt64():X}; "
            + $"originalEx=0x{_originalIconExtendedStyle.ToInt64():X}; "
            + $"currentBk=0x{currentBackground.ToInt64():X}; "
            + $"currentEx=0x{currentExtendedStyle.ToInt64():X}.");
        if (!prepared)
            RestoreIconViewBackground();
        return prepared;
    }

    private static bool IsTransparentIconViewPrepared(IntPtr defView)
    {
        IntPtr iconView = FindDescendantWindow(
            defView,
            "SysListView32");
        if (iconView == IntPtr.Zero
            || iconView != _preparedIconView
            || !IsWindow(iconView))
        {
            return false;
        }

        if (!TrySendListViewMessage(
                iconView,
                LvmGetBackgroundColor,
                IntPtr.Zero,
                IntPtr.Zero,
                out IntPtr backgroundResult)
            || !TrySendListViewMessage(
                iconView,
                LvmGetExtendedStyle,
                IntPtr.Zero,
                IntPtr.Zero,
                out IntPtr extendedStyleResult))
        {
            return false;
        }
        long background = backgroundResult.ToInt64();
        long extendedStyle = extendedStyleResult.ToInt64();
        return unchecked((uint)background) == uint.MaxValue
            && (extendedStyle & LvsExTransparentBackground) != 0;
    }

    private static void RestoreIconViewBackground()
    {
        IntPtr iconView = _preparedIconView;
        if (iconView != IntPtr.Zero && IsWindow(iconView))
        {
            TrySendListViewMessage(
                iconView,
                LvmSetBackgroundColor,
                IntPtr.Zero,
                _originalIconBackground,
                out _);
            TrySendListViewMessage(
                iconView,
                LvmSetTextBackgroundColor,
                IntPtr.Zero,
                _originalIconTextBackground,
                out _);
            TrySendListViewMessage(
                iconView,
                LvmSetExtendedStyle,
                new IntPtr(LvsExTransparentBackground),
                _originalIconExtendedStyle,
                out _);
            InvalidateRect(iconView, IntPtr.Zero, erase: true);
        }

        _preparedIconView = IntPtr.Zero;
        _originalIconBackground = IntPtr.Zero;
        _originalIconTextBackground = IntPtr.Zero;
        _originalIconExtendedStyle = IntPtr.Zero;
    }

    private static bool TrySendListViewMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
    {
        result = IntPtr.Zero;
        if (window == IntPtr.Zero || !IsWindow(window))
            return false;
        return SendMessageTimeout(
            window,
            message,
            wParam,
            lParam,
            SmtoAbortIfHung,
            400,
            out result) != IntPtr.Zero;
    }

    private static IntPtr FindDirectChildBranch(IntPtr parent, IntPtr descendant)
    {
        if (parent == IntPtr.Zero || descendant == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr current = descendant;
        while (current != IntPtr.Zero)
        {
            IntPtr currentParent = GetParent(current);
            if (currentParent == parent)
                return current;
            current = currentParent;
        }
        return IntPtr.Zero;
    }

    private static bool IsUsableHost(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
            return false;
        if (!GetWindowRect(window, out NativeRect rect))
            return false;

        DrawingRectangle virtualScreen =
            System.Windows.Forms.SystemInformation.VirtualScreen;
        const int tolerance = 8;
        return rect.Left <= virtualScreen.Left + tolerance
            && rect.Top <= virtualScreen.Top + tolerance
            && rect.Right >= virtualScreen.Right - tolerance
            && rect.Bottom >= virtualScreen.Bottom - tolerance;
    }

    private static bool CoversDesktopWorkingAreas(IntPtr window)
    {
        if (window == IntPtr.Zero
            || !IsWindow(window)
            || !GetWindowRect(window, out NativeRect rect))
        {
            return false;
        }

        const int tolerance = 8;
        return System.Windows.Forms.Screen.AllScreens.All(screen =>
        {
            DrawingRectangle area = screen.WorkingArea;
            return rect.Left <= area.Left + tolerance
                && rect.Top <= area.Top + tolerance
                && rect.Right >= area.Right - tolerance
                && rect.Bottom >= area.Bottom - tolerance;
        });
    }

    private static bool TrySetWindowLong(
        IntPtr window,
        int index,
        IntPtr value,
        out int error)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = SetWindowLong(window, index, value);
        error = Marshal.GetLastPInvokeError();
        return previous != IntPtr.Zero || error == 0;
    }

    private static string DescribeWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return "0x0";
        bool hasRect = GetWindowRect(window, out NativeRect rect);
        return $"{FormatHandle(window)} class={GetWindowClass(window)} "
            + $"exists={IsWindow(window)} visible={IsWindowVisible(window)} "
            + $"rect={(hasRect ? FormatRect(rect) : "недоступен")}";
    }

    private static string GetWindowClass(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return "—";
        StringBuilder className = new(128);
        return GetClassName(window, className, className.Capacity) > 0
            ? className.ToString()
            : "неизвестен";
    }

    private static string FormatHandle(IntPtr window) =>
        $"0x{window.ToInt64():X}";

    private static string FormatRect(NativeRect rect) =>
        $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) "
        + $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top}";

    private static string FormatBounds(DrawingRectangle bounds) =>
        $"({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}";

    private static IntPtr GetWindowLong(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr child);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    private static extern bool SendNotifyMessageString(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoGet(
        uint action,
        uint parameter,
        StringBuilder value,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoSet(
        uint action,
        uint parameter,
        string value,
        uint flags);
}
