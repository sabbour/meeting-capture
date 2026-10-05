namespace MeetingCapture.Pipeline;

/// <summary>Why exit completed with optional work left undone.</summary>
public enum ExitDeferralKind
{
    None,

    /// <summary>The sidecar could not be written but was kept for an automatic retry on the next launch.</summary>
    VisualizationRetryKept,

    /// <summary>The sidecar could not be written and the retry state could not be kept either.</summary>
    VisualizationRetryLost
}

/// <summary>
/// Result of <see cref="MeetingPipeline.PrepareForExitAsync"/>. Exit only succeeds when the
/// transcript is durable and capture is released; optional visualization persistence is
/// reported here instead of blocking the app from closing.
/// </summary>
public sealed record ExitReadiness(
    ExitDeferralKind Deferral = ExitDeferralKind.None,
    string? TranscriptPath = null,
    string? ErrorType = null)
{
    public static readonly ExitReadiness Clean = new();

    public bool HasDeferredWork => Deferral != ExitDeferralKind.None;

    /// <summary>Sanitized user guidance: never includes transcript or meeting content.</summary>
    public string? Message => Deferral switch
    {
        ExitDeferralKind.VisualizationRetryKept =>
            "Transcript and notes were saved. The optional meeting visualization data could not be written " +
            "next to them, so it was kept and will be retried automatically the next time the app starts. " +
            "Check free space and folder sync before then.",
        ExitDeferralKind.VisualizationRetryLost =>
            "Transcript and notes were saved. The optional meeting visualization data could not be written " +
            "and could not be kept for retry, so the visuals must be rebuilt from the saved transcript. " +
            "Check free space, folder sync, and storage permissions.",
        _ => null
    };
}
