namespace MeetingCapture.AudioCapture;

/// <summary>
/// Fixed-capacity circular ring buffer for PCM audio. Retains at most
/// <see cref="MaxWindowSeconds"/> of audio; older data is silently overwritten.
/// Ring reads and writes use a private lock. Dispose drops the backing-array reference without taking that
/// lock, so it must not run at the same time as another operation.
/// </summary>
public sealed class AudioBuffer : IDisposable
{
    private byte[]? _ring;
    private int _capacity;
    private int _writePos;
    private long _totalWritten;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>PCM sample rate, e.g. 16000 Hz.</summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>Channel count (1 = mono).</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Bits per sample (16 for Speech SDK compatibility).</summary>
    public int BitsPerSample { get; init; } = 16;

    /// <summary>
    /// Maximum audio retained in the rolling window (seconds).
    /// Default 1800 s (30 min) ≈ 55 MB at 16kHz/16-bit/mono.
    /// </summary>
    public int MaxWindowSeconds { get; init; } = 1800;

    /// <summary>Bytes currently held in the buffer (capped at ring capacity).</summary>
    public long BytesRecorded
    {
        get { lock (_lock) { return _ring is null ? 0 : Math.Min(_totalWritten, _capacity); } }
    }

    public void Append(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            EnsureRing();
            int remaining = count;
            int src = offset;
            // Split large writes at the physical end of the array so a single append can wrap around.
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, _capacity - _writePos);
                Buffer.BlockCopy(buffer, src, _ring!, _writePos, chunk);
                _writePos = (_writePos + chunk) % _capacity;
                _totalWritten += chunk;
                src += chunk;
                remaining -= chunk;
            }
        }
    }

    /// <summary>Returns chronologically ordered PCM data and resets the buffer.</summary>
    public byte[] DrainAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            // Copy before resetting indices so the caller gets the complete chronological transcript.
            var data = GetOrderedData();
            _writePos = 0;
            _totalWritten = 0;
            return data;
        }
    }

    /// <summary>Returns a chronologically ordered snapshot without draining.</summary>
    public byte[] Snapshot()
    {
        lock (_lock) { return GetOrderedData(); }
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private void EnsureRing()
    {
        if (_ring is not null) return;
        // Capacity is duration * samples per second * channels * bytes per sample.
        _capacity = MaxWindowSeconds * SampleRate * Channels * (BitsPerSample / 8);
        _ring = new byte[_capacity];
    }

    private byte[] GetOrderedData()
    {
        if (_ring is null || _totalWritten == 0) return [];

        if (_totalWritten < _capacity)
        {
            // Buffer not yet full — data lives in [0 .. _writePos)
            var partial = new byte[_writePos];
            Buffer.BlockCopy(_ring, 0, partial, 0, _writePos);
            return partial;
        }

        // Buffer full and wrapped — oldest byte is at _writePos
        // Copy the tail first, then the wrapped prefix, to restore chronological order.
        var result = new byte[_capacity];
        int tail = _capacity - _writePos;
        Buffer.BlockCopy(_ring, _writePos, result, 0, tail);
        Buffer.BlockCopy(_ring, 0, result, tail, _writePos);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Dropping the array releases the retained audio; callers must stop using the buffer first.
        _ring = null;
    }
}
