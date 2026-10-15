using MeetingCapture.AudioCapture;
using MeetingCapture.Intelligence;
using MeetingCapture.Transcription;

namespace MeetingCapture.Pipeline;

/// <summary>Stages of a recording, from detection to saved notes.</summary>
public enum PipelineState
{
    Idle, Starting, Recording, Stopping, ReviewingSave, Finalizing, Generating, Saved, Discarded, Failed
}

/// <summary>One status update from the pipeline. <c>Sequence</c> orders updates; the notes path or error is set when known.</summary>
public sealed record PipelineStatus(
    long Sequence, string? SessionId, PipelineState State, string Message,
    string? NotesPath, string? Error, IntelligenceFailure? IntelligenceFailure,
    DateTimeOffset? SaveDeadline = null);

/// <summary>A final transcript segment tagged with its recording session and transcript revision.</summary>
public sealed record SessionTranscriptSegment(string SessionId, long Revision, TranscriptionSegment Segment);

/// <summary>An in-progress (partial) transcript tagged with its recording session.</summary>
public sealed record SessionPartialTranscript(string SessionId, PartialTranscript Partial);

/// <summary>Tells which session was saved and the path of the saved notes.</summary>
public sealed record NotesSavedInfo(string SessionId, string Path);

/// <summary>Audio and transcription health at a point in time.</summary>
public sealed record PipelineDiagnostics(
    string? SessionId, DateTimeOffset CapturedAt,
    IReadOnlyList<AudioSourceHealth> Audio, TranscriptionHealth Transcription);
