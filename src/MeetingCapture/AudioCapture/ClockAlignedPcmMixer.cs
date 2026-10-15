namespace MeetingCapture.AudioCapture;

/// <summary>
/// Mixes loopback and microphone 16-bit mono PCM into fixed-size frames. Each source has its own
/// bounded ring buffer; when the mixed peak would clip, the frame is scaled down.
/// </summary>
internal sealed class ClockAlignedPcmMixer
{
    private readonly object _gate = new();
    private readonly PcmRing _loopback;
    private readonly PcmRing _microphone;
    private readonly int[] _sums;

    public int FrameBytes { get; }

    public ClockAlignedPcmMixer(int sampleRate, int channels, int frameMilliseconds = 20,
        int maxBufferedMilliseconds = 2000)
    {
        if (sampleRate <= 0 || channels != 1)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "The mixer requires mono PCM.");
        if (frameMilliseconds <= 0 || maxBufferedMilliseconds < frameMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));

        // PCM16 uses two bytes per sample. The defaults yield 640-byte 20 ms frames and
        // a two-second ring for each source at 16 kHz mono.
        FrameBytes = sampleRate * channels * sizeof(short) * frameMilliseconds / 1000;
        var capacity = sampleRate * channels * sizeof(short) * maxBufferedMilliseconds / 1000;
        FrameBytes &= ~1;
        capacity &= ~1;
        _loopback = new(capacity);
        _microphone = new(capacity);
        _sums = new int[FrameBytes / sizeof(short)];
    }

    /// <summary>Queues PCM bytes for one source. Returns the number of bytes the buffer accepted.</summary>
    public int Add(AudioSource source, byte[] buffer, int bytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        bytes = Math.Min(bytes, buffer.Length) & ~1;
        if (bytes <= 0) return 0;
        // Mixing and producer writes share this lock so a frame cannot read half a packet update.
        lock (_gate) return Queue(source).Write(buffer, bytes);
    }

    /// <summary>
    /// Writes one mixed frame to <paramref name="output"/>. Returns false when no audio is buffered
    /// and <paramref name="includeSilence"/> is false.
    /// </summary>
    public bool ReadFrame(byte[] output, bool includeSilence)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length < FrameBytes) throw new ArgumentException("The output buffer is too small.", nameof(output));

        lock (_gate)
        {
            // During live capture, keep emitting fixed-size frames and fill any source that has not
            // delivered enough samples with zero. After stop, omit frames once both queues are empty.
            if (!includeSilence && _loopback.Count == 0 && _microphone.Count == 0) return false;

            var peak = 0;
            for (var i = 0; i < _sums.Length; i++)
            {
                // Missing samples from either source read as zero, so the two rings align by time.
                var sum = _loopback.ReadSample() + _microphone.ReadSample();
                _sums[i] = sum;
                peak = Math.Max(peak, Math.Abs(sum));
            }

            // Apply one frame-wide scale when needed, preserving the relative level of both sources.
            var scale = peak <= short.MaxValue ? 1.0 : (double)short.MaxValue / peak;
            for (var i = 0; i < _sums.Length; i++)
            {
                var sample = (short)Math.Clamp((int)Math.Round(_sums[i] * scale), short.MinValue, short.MaxValue);
                output[i * 2] = (byte)sample;
                output[i * 2 + 1] = (byte)(sample >> 8);
            }
            return true;
        }
    }

    public bool HasBufferedAudio
    {
        get { lock (_gate) return _loopback.Count > 0 || _microphone.Count > 0; }
    }

    private PcmRing Queue(AudioSource source) => source == AudioSource.Loopback ? _loopback : _microphone;

    private sealed class PcmRing(int capacity)
    {
        private readonly byte[] _buffer = new byte[capacity];
        private int _read;
        private int _write;
        public int Count { get; private set; }

        public int Write(byte[] source, int bytes)
        {
            // Keep the newest audio when a source falls behind; discard whole 16-bit samples so
            // the next read still starts on a sample boundary.
            var overflow = Math.Max(0, Count + bytes - _buffer.Length);
            overflow = (overflow + 1) & ~1;
            var discard = Math.Min(overflow, Count);
            if (discard > 0)
            {
                _read = (_read + discard) % _buffer.Length;
                Count -= discard;
            }

            var sourceOffset = overflow - discard;
            bytes -= sourceOffset;
            CopyIn(source, sourceOffset, bytes);
            return overflow;
        }

        public short ReadSample()
        {
            // Return zero for a source without a complete sample; do not wait for more bytes to arrive.
            if (Count < sizeof(short)) return 0;
            var low = _buffer[_read];
            _read = (_read + 1) % _buffer.Length;
            var high = _buffer[_read];
            _read = (_read + 1) % _buffer.Length;
            Count -= sizeof(short);
            return (short)(low | (high << 8));
        }

        private void CopyIn(byte[] source, int offset, int bytes)
        {
            var first = Math.Min(bytes, _buffer.Length - _write);
            Buffer.BlockCopy(source, offset, _buffer, _write, first);
            var second = bytes - first;
            if (second > 0) Buffer.BlockCopy(source, offset + first, _buffer, 0, second);
            _write = (_write + bytes) % _buffer.Length;
            Count += bytes;
        }
    }
}
