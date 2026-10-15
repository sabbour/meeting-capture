namespace MeetingCapture.TrayApp;

internal readonly record struct WindowBounds(int X, int Y, int Width, int Height);
internal readonly record struct RecordingLayoutAllocation(
    double OverviewMaxHeight,
    double TranscriptMaxHeight);
internal readonly record struct VisualizationLayoutAllocation(
    double PrimaryWidth,
    double VisualizationWidth);

internal static class WindowPlacement
{
    internal const int DefaultWidth = 600;
    internal const int DefaultHeight = 900;
    internal const int Margin = 8;

    internal static (int Width, int Height) DefaultSize(int dpi, WindowBounds workArea)
    {
        var scale = DpiScale(dpi);
        var margin = Scale(Margin, scale);
        return (
            Math.Min(Scale(DefaultWidth, scale), Math.Max(1, workArea.Width - 2 * margin)),
            Math.Min(Scale(DefaultHeight, scale), Math.Max(1, workArea.Height - 2 * margin)));
    }

    internal static WindowBounds Anchor(WindowBounds workArea, int width, int height, int dpi, bool taskbarAtTop)
    {
        var margin = Scale(Margin, DpiScale(dpi));
        width = Math.Min(Math.Max(1, width), Math.Max(1, workArea.Width - 2 * margin));
        height = Math.Min(Math.Max(1, height), Math.Max(1, workArea.Height - 2 * margin));
        return new(
            workArea.X + workArea.Width - width - margin,
            taskbarAtTop ? workArea.Y + margin : workArea.Y + workArea.Height - height - margin,
            width,
            height);
    }

    internal static bool TryExpandForSidePanel(
        WindowBounds current,
        WindowBounds workArea,
        int panelWidth,
        int dpi,
        out WindowBounds expanded)
    {
        var scale = DpiScale(dpi);
        var margin = Scale(Margin, scale);
        var addedWidth = Scale(panelWidth, scale);
        var availableWidth = Math.Max(1, workArea.Width - 2 * margin);
        if (current.Width + addedWidth > availableWidth)
        {
            expanded = current;
            return false;
        }

        var width = current.Width + addedWidth;
        var right = Math.Min(
            workArea.X + workArea.Width - margin,
            current.X + current.Width);
        expanded = current with
        {
            X = Math.Max(workArea.X + margin, right - width),
            Width = width,
        };
        return true;
    }

    internal static WindowBounds CollapseSidePanel(
        WindowBounds current,
        WindowBounds workArea,
        int addedWidth,
        int dpi)
    {
        var margin = Scale(Margin, DpiScale(dpi));
        var width = Math.Max(1, current.Width - Math.Max(0, addedWidth));
        var x = Math.Min(
            workArea.X + workArea.Width - width - margin,
            current.X + Math.Max(0, addedWidth));
        return current with
        {
            X = Math.Max(workArea.X + margin, x),
            Width = width,
        };
    }

    // Returns maximized bounds relative to the monitor origin, as WM_GETMINMAXINFO expects.
    internal static WindowBounds MaximizedBounds(
        WindowBounds monitor,
        WindowBounds workArea,
        int frameX,
        int frameY)
    {
        frameX = Math.Max(0, frameX);
        frameY = Math.Max(0, frameY);
        return new(
            workArea.X - monitor.X - frameX,
            workArea.Y - monitor.Y - frameY,
            Math.Max(1, workArea.Width) + 2 * frameX,
            Math.Max(1, workArea.Height) + 2 * frameY);
    }

    private static double DpiScale(int dpi) => (dpi > 0 ? dpi : 96) / 96d;
    private static int Scale(int value, double scale) => (int)Math.Round(value * scale);
}

internal static class VisualizationLayout
{
    internal const double MinimumPrimaryWidth = 480;
    internal const double MinimumVisualizationWidth = 420;
    internal const double MaximumSidePaneWidth = 720;

    internal static VisualizationLayoutAllocation Calculate(double totalWidth, bool expanded)
    {
        totalWidth = Math.Max(1, totalWidth);
        var availableVisualizationWidth = Math.Max(1, totalWidth - MinimumPrimaryWidth);

        if (!expanded)
        {
            var desired = Math.Clamp(
                totalWidth * 0.42,
                MinimumVisualizationWidth,
                MaximumSidePaneWidth);
            var visualization = Math.Min(desired, availableVisualizationWidth);
            return new(totalWidth - visualization, visualization);
        }

        var minimumTotal = MinimumPrimaryWidth + MinimumVisualizationWidth;
        var primary = totalWidth >= minimumTotal
            ? MinimumPrimaryWidth
            : totalWidth * MinimumPrimaryWidth / minimumTotal;
        return new(primary, totalWidth - primary);
    }
}

internal static class RecordingLayout
{
    internal const double DesiredOverviewHeight = 320;
    internal const double MinimumOverviewHeight = 130;
    internal const double MinimumTranscriptHeight = 130;
    internal const double DesiredTranscriptHeight = 180;

    internal static RecordingLayoutAllocation Calculate(
        double panelHeight,
        double fixedHeight)
    {
        var available = Math.Max(0, panelHeight - Math.Max(0, fixedHeight));
        var overview = Math.Clamp(available - DesiredTranscriptHeight,
            MinimumOverviewHeight, DesiredOverviewHeight);
        var transcript = Math.Max(MinimumTranscriptHeight, available - overview);
        return new(overview, transcript);
    }
}
