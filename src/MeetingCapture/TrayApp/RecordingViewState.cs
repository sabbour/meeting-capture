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

    public static (bool Visible, bool Enabled, string Message) NewRecordingAction(
        PipelineStatus status, bool canStart, bool preparingExit, bool retryBusy, bool previewBusy)
    {
        var visible = canStart || status.State is PipelineState.Saved or PipelineState.Discarded or PipelineState.Failed;
        var enabled = canStart && !preparingExit && !retryBusy && !previewBusy;
        var message = preparingExit ? "The app is exiting. Reopen it to start a new recording."
            : retryBusy ? "Wait for the AI retry to finish, or cancel it, before starting a new recording."
            : previewBusy ? "Wait for the audio preview to finish before starting a new recording."
            : !canStart ? "New recording is unavailable. " + (status.Error ?? status.Message)
            : "Start a separate recording. Saved notes stay unchanged.";
        return (visible, enabled, message);
    }
}
