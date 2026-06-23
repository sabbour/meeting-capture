namespace MeetingCapture.AudioCapture;

/// <summary>Aggregates PCM audio data from one capture source.</summary>
public sealed class AudioBuffer : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>PCM sample rate, e.g. 16000 Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Channel count (1 = mono, 2 = stereo).</summary>
    public int Channels { get; init; }

    /// <summary>Bits per sample (16 for Speech SDK compatibility).</summary>
    public int BitsPerSample { get; init; } = 16;

    public long BytesRecorded
    {
        get { lock (_lock) { return _stream.Length; } }
    }

    public void Append(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            _stream.Write(buffer, offset, count);
        }
    }

    /// <summary>Returns a copy of all accumulated PCM data and resets the buffer.</summary>
    public byte[] DrainAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            var data = _stream.ToArray();
            _stream.SetLength(0);
            return data;
        }
    }

    /// <summary>Returns a read-only snapshot without draining.</summary>
    public byte[] Snapshot()
    {
        lock (_lock) { return _stream.ToArray(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
