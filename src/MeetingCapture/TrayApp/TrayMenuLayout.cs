namespace MeetingCapture.TrayApp;

/// <summary>
/// Pure size, DPI and placement math for the tray context menu window. All inputs are measured at the
/// moment the menu opens, on the monitor under the cursor, so a stale or system-wide DPI can never
/// produce a window smaller than the menu.
/// </summary>
internal static class TrayMenuLayout
{
    internal const int MenuWidthDip = 360;
    internal const int MaxHeightDip = 480;
    internal const double DefaultItemHeightDip = 32;
    internal const double DefaultSeparatorHeightDip = 9;
    internal const double ChromeDip = 8;
    internal const int FrameSlackPx = 4;
    internal const int BaselineDpi = 96;

    internal static double ScaleFor(int dpi) => dpi > 0 ? dpi / (double)BaselineDpi : 1d;

    /// <summary>Uses the measured row height when it is usable, otherwise a safe default.</summary>
    internal static double RowHeightDip(double measuredDip, bool isSeparator)
        => double.IsFinite(measuredDip) && measuredDip > 0
            ? measuredDip
            : isSeparator ? DefaultSeparatorHeightDip : DefaultItemHeightDip;

    internal static (int Width, int Height) MenuSize(
        IEnumerable<double> visibleRowHeightsDip, int dpi, WindowBounds workArea)
    {
        var scale = ScaleFor(dpi);
        // Measure visible items, add menu chrome, and clamp the result to this monitor's usable area.
        var contentDip = Math.Min(visibleRowHeightsDip.Sum() + ChromeDip, MaxHeightDip);
        var width = (int)Math.Round(MenuWidthDip * scale) + FrameSlackPx;
        var height = (int)Math.Round(contentDip * scale) + FrameSlackPx;
        return (
            Math.Min(width, Math.Max(1, workArea.Width)),
            Math.Min(height, Math.Max(1, workArea.Height)));
    }

    /// <summary>
    /// Opens upward from the cursor (as taskbar menus do), flips below it when there is no room
    /// above, and clamps the result inside the monitor work area. Coordinates are physical pixels.
    /// </summary>
    internal static WindowBounds Place(int cursorX, int cursorY, int width, int height, WindowBounds workArea)
    {
        // Align the right edge to the cursor and prefer opening upward, like a standard taskbar menu.
        var right = workArea.X + workArea.Width;
        var bottom = workArea.Y + workArea.Height;

        var x = cursorX;
        if (x + width > right)
            x = right - width;
        x = Math.Max(workArea.X, x);

        var y = cursorY - height;
        if (y < workArea.Y)
            y = cursorY;
        y = Math.Min(y, bottom - height);
        y = Math.Max(workArea.Y, y);

        return new WindowBounds(x, y, width, height);
    }

    internal static bool SizeMatches(WindowBounds expected, int actualWidth, int actualHeight)
        => actualWidth == expected.Width && actualHeight == expected.Height;
}
