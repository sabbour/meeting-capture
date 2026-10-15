using System.Runtime.InteropServices;
using H.NotifyIcon.Core;
using H.NotifyIcon.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace MeetingCapture.TrayApp;

/// <summary>
/// Hosts the tray <see cref="MenuFlyout"/> in a small transparent window. H.NotifyIcon's own
/// second-window mode sizes that window from the XamlRoot scale of a flyout that has not been shown yet
/// (so 1.0 on the first open and the previous monitor's scale after a move) and ignores the flyout's
/// presenter style. Here the window is sized on every open from the DPI of the monitor under the cursor,
/// then verified against the real window rectangle before the menu is shown.
/// </summary>
internal sealed class TrayContextMenuHost : IDisposable
{
    private const int MaxResizeAttempts = 3;

    private readonly MenuFlyout _menu;
    private readonly Window _window;
    private readonly Grid _root;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _open;
    private bool _contentLoaded;
    private bool _disposed;

    public TrayContextMenuHost(MenuFlyout menu)
    {
        _menu = menu;
        _menu.Placement = FlyoutPlacementMode.Full;
        _menu.AreOpenCloseAnimationsEnabled = false;
        _menu.Closed += OnMenuClosed;

        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _window = new Window { Content = _root };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        DesktopWindowsManagerMethods.SetRoundedCorners(_hwnd);
        WindowUtilities.MakeTransparent(_hwnd);
        HwndUtilities.SetWindowStyleAsPopupWindow(_hwnd);

        _appWindow = _window.AppWindow;
        _appWindow.IsShownInSwitchers = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        _root.Loaded += OnRootLoaded;
        _window.Activated += OnWindowActivated;
    }

    /// <summary>Opens the menu at the current cursor position at full size. Safe to call repeatedly.</summary>
    public void Show()
    {
        if (_disposed || !Native.GetCursorPos(out var cursor))
            return;

        var monitor = Native.MonitorFromPoint(cursor, Native.MonitorDefaultToNearest);
        var workArea = Native.WorkAreaOf(monitor);
        var dpi = Native.DpiOf(monitor);
        var (width, height) = TrayMenuLayout.MenuSize(MeasureVisibleRows(), dpi, workArea);
        var bounds = TrayMenuLayout.Place(cursor.X, cursor.Y, width, height, workArea);

        if (_menu.IsOpen)
            _menu.Hide();

        _open = true;
        ApplyBounds(bounds);
        WindowUtilities.ShowWindow(_hwnd);
        ApplyBounds(bounds);
        WindowUtilities.SetForegroundWindow(_hwnd);

        if (_contentLoaded)
            ShowFlyout();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _open = false;
        _menu.Closed -= OnMenuClosed;
        try
        {
            if (_menu.IsOpen)
                _menu.Hide();
            _window.Close();
        }
        catch (Exception)
        {
            // The window may already be torn down during shutdown.
        }
    }

    private List<double> MeasureVisibleRows()
    {
        var rows = new List<double>();
        foreach (var item in _menu.Items)
        {
            if (item.Visibility != Visibility.Visible)
                continue;

            var separator = item is MenuFlyoutSeparator;
            if (!separator)
            {
                item.Height = TrayMenuLayout.DefaultItemHeightDip;
                item.Padding = new Thickness(11, 0, 11, 0);
            }

            var measured = 0d;
            try
            {
                item.Measure(new Size(TrayMenuLayout.MenuWidthDip, double.PositiveInfinity));
                measured = item.DesiredSize.Height;
            }
            catch (Exception)
            {
                // Fall back to the default row height below.
            }

            rows.Add(TrayMenuLayout.RowHeightDip(measured, separator));
        }

        return rows;
    }

    // Moving between monitors with different scales can make Windows rescale the window in response to
    // WM_DPICHANGED, so the real size is checked and re-applied until it matches what was requested.
    private void ApplyBounds(WindowBounds bounds)
    {
        var rect = new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        for (var attempt = 0; attempt < MaxResizeAttempts; attempt++)
        {
            _appWindow.MoveAndResize(rect);
            if (!NativeMethods.GetWindowRect(_hwnd, out var actual) ||
                TrayMenuLayout.SizeMatches(bounds, actual.Right - actual.Left, actual.Bottom - actual.Top))
                return;
        }
    }

    private void ShowFlyout()
    {
        try
        {
            _menu.ShowAt(_root, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
        }
        catch (Exception)
        {
            Close();
        }
    }

    private void Close()
    {
        _open = false;
        try
        {
            if (_menu.IsOpen)
                _menu.Hide();
        }
        finally
        {
            WindowUtilities.HideWindow(_hwnd);
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _contentLoaded = true;
        HwndUtilities.SetWindowStyleAsPopupWindow(_hwnd);
        if (_open && !_menu.IsOpen)
            ShowFlyout();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_open)
                Close();
            return;
        }

        if (_open && _contentLoaded && !_menu.IsOpen)
            ShowFlyout();
    }

    // A late Closed from a menu that was just re-opened must not hide the new one.
    private void OnMenuClosed(object? sender, object e)
    {
        if (_menu.IsOpen)
            return;
        _open = false;
        WindowUtilities.HideWindow(_hwnd);
    }

    private static class Native
    {
        internal const uint MonitorDefaultToNearest = 2;
        private const int MonitorDpiTypeEffective = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public NativeMethods.RECT rcMonitor;
            public NativeMethods.RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        internal static WindowBounds WorkAreaOf(IntPtr monitor)
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                return new WindowBounds(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
            }

            return new WindowBounds(0, 0, Math.Max(1, GetSystemMetrics(0)), Math.Max(1, GetSystemMetrics(1)));
        }

        internal static int DpiOf(IntPtr monitor)
        {
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _) == 0 &&
                dpiX > 0)
                return (int)dpiX;

            var system = GetDpiForSystem();
            return system > 0 ? (int)system : TrayMenuLayout.BaselineDpi;
        }
    }
}
