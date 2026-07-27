using System.Runtime.InteropServices;

namespace WallpaperMatrix.Services;

internal static class UserIdleMonitor
{
    public static TimeSpan IdleTime
    {
        get
        {
            LastInputInfo input = new()
            {
                Size = (uint)Marshal.SizeOf<LastInputInfo>()
            };
            if (!GetLastInputInfo(ref input))
                return TimeSpan.Zero;

            uint now = unchecked((uint)Environment.TickCount);
            uint elapsed = unchecked(now - input.TickCount);
            return TimeSpan.FromMilliseconds(elapsed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
