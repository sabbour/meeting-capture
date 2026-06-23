using MeetingCapture.Transcription;

namespace MeetingCapture.Intelligence;

/// <summary>
/// Processes a completed transcript into structured meeting notes using an LLM.
/// </summary>
public interface INotesIntelligence
{
    /// <summary>
    /// Synthesizes structured notes from a completed transcript.
    /// Returns a populated MeetingNotes on success.
    /// Throws on LLM failure — do not return empty notes silently.
    /// </summary>
    Task<MeetingNotes> SynthesizeAsync(
        string meetingTitle,
        TranscriptionResult transcript,
        CancellationToken cancellationToken);
}
