namespace MeetingCapture.TrayApp;

/// <summary>Layout rules for the split between the topic map and the notes pane.</summary>
internal static class TopicPaneSplit
{
    internal const double DefaultMainShare = 0.6;
    internal const double SideBySideMinimumWidth = 640;
    internal const double MinimumMainWidth = 240;
    internal const double MinimumNotesWidth = 260;
    internal const double KeyboardStep = 24;

    internal static bool IsSideBySide(double bodyWidth) => bodyWidth >= SideBySideMinimumWidth;

    // Returns the fraction of the non-splitter width given to the topic map.
    internal static double MainShare(double requestedMainWidth, double bodyWidth, double splitterWidth)
    {
        var available = Math.Max(1, bodyWidth - Math.Max(0, splitterWidth));
        var minimum = Math.Min(MinimumMainWidth, available / 2);
        var maximum = Math.Max(minimum, available - MinimumNotesWidth);
        var main = double.IsNaN(requestedMainWidth) ? available * DefaultMainShare : requestedMainWidth;
        return Math.Clamp(main, minimum, maximum) / available;
    }
}
