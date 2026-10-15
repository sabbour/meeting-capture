namespace MeetingCapture.AudioCapture;

/// <summary>Thread-safe counters and state for one audio source, reported as <see cref="AudioSourceHealth"/> snapshots.</summary>
internal sealed class AudioHealthTracker(string source)
{
    private readonly object _gate = new();
    private CaptureHealthState _state = CaptureHealthState.Ready;
    private long _bytes, _buffers, _drops;
    private DateTimeOffset? _lastAudio;
    private double? _level;
    private string? _error;

    public AudioSourceHealth Snapshot()
    {
        lock (_gate) return new(source, _state, _bytes, _buffers, _drops, _lastAudio, _level, _error);
    }

    public void Reset(bool enabled = true)
    {
        lock (_gate)
        {
            _state = enabled ? CaptureHealthState.Ready : CaptureHealthState.Disabled;
            _bytes = _buffers = _drops = 0;
            _lastAudio = null;
            _level = null;
            _error = null;
        }
    }

    public void Received(int bytes)
    {
        lock (_gate)
        {
            _bytes += bytes;
            _buffers++;
            _lastAudio = DateTimeOffset.UtcNow;
            if (_state == CaptureHealthState.Ready) _state = CaptureHealthState.Active;
        }
    }

    public void Level(byte[] pcm, int bytes)
    {
        if (bytes < 2) return;
        double sum = 0;
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += (double)sample * sample;
        }
        lock (_gate) _level = Math.Clamp(Math.Sqrt(sum / (bytes / 2)) / 32768.0, 0, 1);
    }

    public void Dropped()
    {
        lock (_gate)
        {
            _drops++;
            if (_state != CaptureHealthState.Failed) _state = CaptureHealthState.Degraded;
            _error ??= "Audio buffers were dropped because processing could not keep up.";
        }
    }

    public void Failed(string error)
    {
        lock (_gate) { _state = CaptureHealthState.Failed; _error = error; }
    }

    public void Stopped()
    {
        lock (_gate)
        {
            if (_state is CaptureHealthState.Ready or CaptureHealthState.Active)
                _state = CaptureHealthState.Stopped;
        }
    }
}
