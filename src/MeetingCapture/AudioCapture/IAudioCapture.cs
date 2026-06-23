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

public sealed class AudioDataEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    public byte[] Buffer { get; } = buffer;
    public int BytesRecorded { get; } = bytesRecorded;
}
