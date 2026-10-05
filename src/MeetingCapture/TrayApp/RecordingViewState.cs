using MeetingCapture.Pipeline;

namespace MeetingCapture.TrayApp;

public sealed record RecordingViewState(
    bool ShowPaging, bool ShowLatest, bool ShowCatchUp, bool CanSummarize,
    bool ShowSummaryRetry, string EmptyMessage)
{
    public static RecordingViewState Create(PipelineState state, int finalizedSegments,
        bool followLatest, bool summaryFailed, bool summaryBusy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(finalizedSegments);
        var hasSpeech = finalizedSegments > 0;
        var recording = state == PipelineState.Recording;
        return new(
            finalizedSegments > TranscriptViewport.Capacity,
            hasSpeech && !followLatest,
            recording,
            recording && hasSpeech && !summaryBusy,
            recording && hasSpeech && summaryFailed,
            hasSpeech ? "New transcript available. Choose Latest."
                : state == PipelineState.Starting ? "Starting audio and speech capture..."
                : recording ? "Waiting for recognized speech..."
                : "No finalized speech was recognized.");
    }

    public static bool ShouldShowNotice(PipelineState state, string? sessionId, string? shownSessionId) =>
        state == PipelineState.Recording && sessionId is not null && sessionId != shownSessionId;
}
