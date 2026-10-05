namespace MeetingCapture.AudioCapture;

/// <summary>
/// A single audio capture source (microphone OR loopback).
/// Each implementation provides a live PCM stream consumable by the transcription pipeline.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Human-readable name of this capture source.</summary>
    string Name { get; }

    /// <summary>True when the capture is actively recording.</summary>
    bool IsCapturing { get; }

    IReadOnlyList<AudioSourceHealth> Health { get; }

    /// <summary>PCM sample rate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Number of audio channels (1 = mono).</summary>
    int Channels { get; }

    /// <summary>
    /// Raised continuously during capture. Buffer contains raw PCM bytes.
    /// Do NOT hold locks or perform blocking I/O in the handler.
    /// </summary>
    event EventHandler<AudioDataEventArgs> DataAvailable;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public enum CaptureHealthState { Disabled, Ready, Active, Degraded, Failed, Stopped }

public sealed record AudioSourceHealth(
    string Source, CaptureHealthState State, long ReceivedBytes, long ReceivedBuffers,
    long DroppedBuffers, DateTimeOffset? LastAudioAt, double? Level, string? Error);

public sealed class AudioDataEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    public byte[] Buffer { get; } = buffer;
    public int BytesRecorded { get; } = bytesRecorded;
}

/// <summary>
/// Implemented by capture sources that can report when the local microphone last carried
/// significant audio. Used to correlate a diarized speaker with the local (signed-in) user:
/// the microphone stream is always the local talker, so a transcribed segment whose timing
/// overlaps recent mic activity belongs to the local user.
/// </summary>
public interface IMicActivitySource
{
    /// <summary>
    /// UTC timestamp of the most recent microphone audio above the activity RMS threshold,
    /// or <c>null</c> if the mic has not been significantly active yet.
    /// </summary>
    DateTime? MicActivityTimestamp { get; }
}
