using System.Runtime.InteropServices;

namespace TaskDock.Platform;

/// <summary>The only place that P/Invokes. Hand-written: a dozen calls don't justify a source generator yet.</summary>
public static partial class Win32
{
    public const int WM_HOTKEY = 0x0312, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtr(nint h, int i);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial nint SetWindowLongPtr(nint h, int i, nint v);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint h, out RECT r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint h, uint flags);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(POINT p, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(nint m, ref MONITORINFO mi);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint h, int id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsHungAppWindow(nint h);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(nint h, int attr, ref int value, int size);
    [LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);

    public static void MakeToolWindow(nint hwnd, bool noActivate)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = (ex | WS_EX_TOOLWINDOW) & ~(nint)WS_EX_APPWINDOW;
        ex = noActivate ? ex | WS_EX_NOACTIVATE : ex & ~(nint)WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex);
    }

    public static void RoundCorners(nint hwnd)
    {
        var pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); // no-op on Windows 10
    }

    public static RECT WorkAreaOf(nint hwnd) => WorkArea(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public static RECT WorkAreaAt(int x, int y) => WorkArea(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));

    static RECT WorkArea(nint monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        return mi.rcWork;
    }

    public static void MoveTo(nint hwnd, int x, int y) => SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>A fullscreen D3D app, presentation mode or "busy" (e.g. a fullscreen video).</summary>
    public static bool SomethingIsFullscreen() =>
        SHQueryUserNotificationState(out var s) == 0 && s is 2 /*BUSY*/ or 3 /*D3D_FULL_SCREEN*/ or 4 /*PRESENTATION_MODE*/;
}
