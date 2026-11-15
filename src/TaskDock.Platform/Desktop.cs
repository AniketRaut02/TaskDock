using System.Runtime.InteropServices;
using System.Text;

namespace TaskDock.Platform;

/// <summary>Top-level windows and processes: picker hit-testing, generic tiles, focus.</summary>
public static partial class Desktop
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const int SW_RESTORE = 9;
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    delegate bool EnumProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumWindows(EnumProc f, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll", EntryPoint = "IsWindow")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowNative(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint h, out uint pid);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetWindowText(nint h, [Out] char[] s, int n);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BringWindowToTop(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AttachThreadInput(uint a, uint b, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Win32.POINT p);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtr(nint h, int i);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial nint SetWindowLongPtr(nint h, int i, nint v);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryFullProcessImageName(nint p, uint flags, [Out] char[] name, ref int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint h, int attr, out Win32.RECT r, int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint h, int attr, out int v, int size);

    public static bool IsWindow(nint h) => h != 0 && IsWindowNative(h);
    public static bool IsHung(nint h) => Win32.IsHungAppWindow(h);
    public static nint Foreground() => GetForegroundWindow();

    public static int ProcessId(nint h) { GetWindowThreadProcessId(h, out var pid); return (int)pid; }

    public static string Title(nint h)
    {
        var buf = new char[512];
        var n = GetWindowText(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    /// <summary>Full image path, or null for processes we can't query (elevated, gone).</summary>
    public static string? ImagePath(int pid)
    {
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (p == 0) return null;
        try
        {
            var buf = new char[1024];
            var size = buf.Length;
            return QueryFullProcessImageName(p, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { CloseHandle(p); }
    }

    /// <summary>The visible frame (no invisible resize borders).</summary>
    public static Win32.RECT Frame(nint h) =>
        DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) == 0 ? r : Rect(h);

    static Win32.RECT Rect(nint h) { Win32.GetWindowRect(h, out var r); return r; }

    static bool IsCloaked(nint h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;

    /// <summary>Visible, uncloaked, non-empty top-level windows in z-order, top first.</summary>
    public static List<nint> TopLevel()
    {
        var list = new List<nint>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && !IsCloaked(h))
            {
                var r = Frame(h);
                if (r.Width > 0 && r.Height > 0) list.Add(h);
            }
            return true;
        }, 0);
        return list;
    }

    public static Win32.POINT Cursor() { GetCursorPos(out var p); return p; }

    /// <summary>Topmost real window under a screen point, skipping our own process (widget and overlay).</summary>
    public static nint WindowAt(int x, int y)
    {
        var self = Environment.ProcessId;
        foreach (var h in TopLevel())
        {
            var r = Frame(h);
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom && ProcessId(h) != self) return h;
        }
        return 0;
    }

    /// <summary>
    /// A no-activate window often can't hand focus over. The click is fresh user input, so this process
    /// may usually set the foreground; AttachThreadInput is the fallback.
    /// </summary>
    public static void Focus(nint target)
    {
        if (!IsWindow(target)) return;
        if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
        if (SetForegroundWindow(target)) return;
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(target);
        SetForegroundWindow(target);
        AttachThreadInput(me, fgThread, false);
    }

    /// <summary>For the picker overlay: mouse passes through, never activates, not in Alt-Tab.</summary>
    public static void MakeClickThrough(nint h) =>
        SetWindowLongPtr(h, GWL_EXSTYLE, GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

    public static void SetBounds(nint h, Win32.RECT r) => SetWindowPos(h, 0, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Target of a .lnk via the Windows Script Host shortcut object; null if it can't be read.</summary>
    public static string? ShortcutTarget(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try { return (string)shell.CreateShortcut(lnk).TargetPath is { Length: > 0 } t ? t : null; }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception) { return null; }
    }
}
