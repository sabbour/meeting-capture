namespace MeetingCapture.Transcription;

/// <summary>
/// Provides live speech-to-text transcription from a PCM audio stream.
/// Implementations must support streaming (segment-by-segment) output.
/// </summary>
public interface ITranscriptionService : IAsyncDisposable
{
    /// <summary>
    /// Raised when a recognized speech segment is available.
    /// May be called from a background thread — marshal to UI thread if needed.
    /// </summary>
    event EventHandler<TranscriptionSegment> SegmentRecognized;

    /// <summary>True while actively transcribing.</summary>
    bool IsTranscribing { get; }

    /// <summary>
    /// Starts the transcription session.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pushes raw PCM audio bytes to the transcription engine.
    /// </summary>
    Task PushAudioAsync(byte[] pcmData, int bytesRecorded, CancellationToken cancellationToken);

    /// <summary>
    /// Stops transcription and returns the complete result.
    /// </summary>
    Task<TranscriptionResult> StopAsync(CancellationToken cancellationToken);
}
