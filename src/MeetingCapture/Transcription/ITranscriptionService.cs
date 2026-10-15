using MeetingCapture.AudioCapture;

namespace MeetingCapture.Transcription;

/// <summary>Health of transcription: state, bytes received, last transcript time, and audio push timing.</summary>
public sealed record TranscriptionHealth(
    CaptureHealthState State, long ReceivedBytes, DateTimeOffset? LastTranscriptAt,
    long PushWriteCount, double LastPushWriteMilliseconds,
    double PushWriteP95Milliseconds, string? Error);

/// <summary>
/// Provides live speech-to-text transcription from a PCM audio stream.
/// Implementations must support streaming (segment-by-segment) output.
/// </summary>
public interface ITranscriptionService : IAsyncDisposable
{
    /// <summary>
    /// Raised when a recognized speech segment is available (final result).
    /// May be called from a background thread — marshal to UI thread if needed.
    /// </summary>
    event EventHandler<TranscriptionSegment> SegmentRecognized;

    /// <summary>
    /// Raised with interim/partial text (and the current speaker label from diarization)
    /// while the user is still speaking. Text may change; only
    /// <see cref="SegmentRecognized"/> produces final results.
    /// </summary>
    event EventHandler<PartialTranscript> PartialResultReceived;

    /// <summary>True while actively transcribing.</summary>
    bool IsTranscribing { get; }

    /// <summary>Includes startup, flush, and resources whose disposal failed.</summary>
    bool HasActiveResources => IsTranscribing;

    /// <summary>Total PCM bytes pushed to the engine since last StartAsync.</summary>
    long TotalAudioBytesReceived { get; }

    TranscriptionHealth Health { get; }

    /// <summary>Starts the transcription session.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Pushes raw PCM audio bytes to the transcription engine.</summary>
    Task PushAudioAsync(byte[] pcmData, int bytesRecorded, CancellationToken cancellationToken);

    /// <summary>Stops transcription and returns the complete result.</summary>
    Task<TranscriptionResult> StopAsync(CancellationToken cancellationToken);
}
