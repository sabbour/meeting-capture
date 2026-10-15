using System.Runtime.InteropServices;

namespace MeetingCapture.TrayApp;

internal static class NativeMethods
{
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const uint WmSetIcon = 0x0080;
    private static readonly IntPtr IconSmall = IntPtr.Zero;
    private static readonly IntPtr IconBig = new(1);

    internal static readonly IntPtr HwndTop = new(0);
    internal const uint SwpShowWindow  = 0x0040;
    internal const uint SwpNoActivate  = 0x0010;
    internal const uint SwpNoZOrder    = 0x0004;
    internal const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal static void SetWindowIcon(IntPtr hWnd, string iconPath)
    {
        if (hWnd == IntPtr.Zero || string.IsNullOrWhiteSpace(iconPath))
            return;

        var hIcon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        if (hIcon == IntPtr.Zero)
            return;

        SendMessage(hWnd, WmSetIcon, IconBig, hIcon);
        SendMessage(hWnd, WmSetIcon, IconSmall, hIcon);
    }

    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const uint WmNcDestroy = 0x0082;
    private const uint MonitorDefaultToNearest = 0x00000002;

    internal delegate IntPtr SubclassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // A captionless resizable window is maximized to the full monitor by default, which
    // puts the bottom of the window behind the taskbar. Limit it to the monitor work area
    // while keeping the default hidden resize-frame overhang.
    internal static void ConstrainMaximizedBoundsToWorkArea(IntPtr hWnd, IntPtr minMaxInfo)
    {
        if (minMaxInfo == IntPtr.Zero)
            return;
        var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(minMaxInfo);
        var bounds = WindowPlacement.MaximizedBounds(
            ToBounds(info.rcMonitor),
            ToBounds(info.rcWork),
            Math.Max(0, -mmi.ptMaxPosition.X),
            Math.Max(0, -mmi.ptMaxPosition.Y));
        mmi.ptMaxPosition = new POINT { X = bounds.X, Y = bounds.Y };
        mmi.ptMaxSize = new POINT { X = bounds.Width, Y = bounds.Height };
        Marshal.StructureToPtr(mmi, minMaxInfo, false);
    }

    private static WindowBounds ToBounds(RECT rect)
        => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
