using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WallpaperMatrix.Services;

/// <summary>
/// Detects a foreground borderless/exclusive fullscreen application without
/// injecting code or opening the process. This remains friendly to anti-cheat.
/// </summary>
internal sealed class FullscreenApplicationMonitor : IDisposable
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint GetAncestorRoot = 2;
    private const int DwmExtendedFrameBounds = 9;
    private readonly System.Threading.Timer _timer;
    private int _checking;
    private bool _enabled;
    private bool _active;
    private bool _disposed;

    public event Action<bool, string?>? ActivityChanged;

    public FullscreenApplicationMonitor()
    {
        _timer = new System.Threading.Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled)
            return;
        _enabled = enabled;
        if (enabled)
        {
            _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Report(false, null);
        }
    }

    private void Check(object? state)
    {
        if (!_enabled || Interlocked.Exchange(ref _checking, 1) != 0)
            return;
        try
        {
            bool fullscreen = TryGetFullscreenForegroundProcess(out string? processName);
            Report(fullscreen, processName);
        }
        catch
        {
            Report(false, null);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void Report(bool active, string? processName)
    {
        if (_active == active)
            return;
        _active = active;
        ActivityChanged?.Invoke(active, processName);
    }

    private static bool TryGetFullscreenForegroundProcess(out string? processName)
    {
        processName = null;
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero
            || !IsWindowVisible(window)
            || IsIconic(window)
            || GetAncestor(window, GetAncestorRoot) != window)
        {
            return false;
        }

        string className = ReadClassName(window);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
            return false;

        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        MonitorInfo monitorInfo = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        NativeRect windowBounds;
        int dwmResult = DwmGetWindowAttribute(
            window,
            DwmExtendedFrameBounds,
            out windowBounds,
            Marshal.SizeOf<NativeRect>());
        if (dwmResult != 0 && !GetWindowRect(window, out windowBounds))
            return false;

        NativeRect screen = monitorInfo.Monitor;
        int screenWidth = Math.Max(1, screen.Right - screen.Left);
        int screenHeight = Math.Max(1, screen.Bottom - screen.Top);
        int intersectionWidth = Math.Max(
            0,
            Math.Min(windowBounds.Right, screen.Right) - Math.Max(windowBounds.Left, screen.Left));
        int intersectionHeight = Math.Max(
            0,
            Math.Min(windowBounds.Bottom, screen.Bottom) - Math.Max(windowBounds.Top, screen.Top));
        double coverage = intersectionWidth * (double)intersectionHeight / (screenWidth * (double)screenHeight);
        const int edgeTolerance = 12;
        bool reachesEdges = windowBounds.Left <= screen.Left + edgeTolerance
            && windowBounds.Top <= screen.Top + edgeTolerance
            && windowBounds.Right >= screen.Right - edgeTolerance
            && windowBounds.Bottom >= screen.Bottom - edgeTolerance;
        if (!reachesEdges || coverage < 0.97)
            return false;

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            processName = null;
        }
        return true;
    }

    private static string ReadClassName(IntPtr window)
    {
        StringBuilder buffer = new(128);
        return GetClassName(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _enabled = false;
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out NativeRect value,
        int valueSize);
}
