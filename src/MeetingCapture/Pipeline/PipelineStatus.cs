using MeetingCapture.AudioCapture;
using MeetingCapture.Intelligence;
using MeetingCapture.Transcription;

namespace MeetingCapture.Pipeline;

public enum PipelineState
{
    Idle, Starting, Recording, Stopping, ReviewingSave, Finalizing, Generating, Saved, Discarded, Failed
}

public sealed record PipelineStatus(
    long Sequence, string? SessionId, PipelineState State, string Message,
    string? NotesPath, string? Error, IntelligenceFailure? IntelligenceFailure,
    DateTimeOffset? SaveDeadline = null);

public sealed record SessionTranscriptSegment(string SessionId, long Revision, TranscriptionSegment Segment);
public sealed record SessionPartialTranscript(string SessionId, PartialTranscript Partial);
public sealed record NotesSavedInfo(string SessionId, string Path);
public sealed record PipelineDiagnostics(
    string? SessionId, DateTimeOffset CapturedAt,
    IReadOnlyList<AudioSourceHealth> Audio, TranscriptionHealth Transcription);
