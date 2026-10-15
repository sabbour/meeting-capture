namespace MeetingCapture.TrayApp;

/// <summary>Visibility, countdown and button text for the save review (a short window to save or discard).</summary>
internal readonly record struct SaveReviewPresentation(
    bool IsVisible,
    bool CanDecide,
    int RemainingSeconds,
    double Progress,
    string ButtonText,
    string AccessibleName)
{
    private static readonly TimeSpan ReviewDuration = TimeSpan.FromSeconds(5);

    public static SaveReviewPresentation Create(
        bool isReviewing,
        DateTimeOffset? deadline,
        DateTimeOffset now,
        bool decisionSubmitted)
    {
        if (!isReviewing)
            return new(false, false, 0, 0, string.Empty, string.Empty);

        if (deadline is null)
            return new(true, false, 0, 0, "Save now", "Save recording now. Preparing automatic save.");

        var remaining = deadline.Value - now;
        var progress = Math.Clamp(
            1 - (remaining.TotalMilliseconds / ReviewDuration.TotalMilliseconds),
            0,
            1);

        if (decisionSubmitted)
            return new(true, false, 0, progress, "Waiting...", "Waiting for recording status.");

        if (remaining <= TimeSpan.Zero)
            return new(true, false, 0, 1, "Saving...", "Automatic save is starting.");

        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return new(
            true,
            true,
            seconds,
            progress,
            $"Save now ({seconds}s)",
            $"Save recording now. Automatic save in {seconds} second{(seconds == 1 ? string.Empty : "s")}.");
    }
}
