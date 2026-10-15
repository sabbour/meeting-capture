namespace MeetingCapture.AudioCapture;

/// <summary>
/// Maintains a rolling root-mean-square (RMS) level over the most recent window of 16-bit PCM
/// audio, and applies saturating gain to PCM buffers.
///
/// Threading: this type is NOT thread-safe. Each instance is intended to be fed from a single
/// consumer thread (the loopback consumer feeds the loopback meter; the mic consumer feeds the mic
/// meter and applies gain). It performs only integer/float math — no COM, no UI thread, no
/// blocking — so it is safe to run on the dedicated managed consumer threads.
/// </summary>
internal sealed class RollingRms
{
    private readonly short[] _window;
    private int _index;
    private int _count;
    private double _sumSquares;

    /// <param name="sampleRate">PCM sample rate, e.g. 16000.</param>
    /// <param name="windowMs">Length of the rolling RMS window in milliseconds.</param>
    public RollingRms(int sampleRate, int windowMs)
    {
        var capacity = Math.Max(1, (int)((long)sampleRate * windowMs / 1000));
        _window = new short[capacity];
    }

    /// <summary>Current RMS magnitude (0..32768) over the rolling window. 0 when no audio seen.</summary>
    public double Rms => _count == 0 ? 0.0 : Math.Sqrt(_sumSquares / _count);

    /// <summary>
    /// Feeds a buffer of little-endian 16-bit mono PCM into the rolling window, evicting the
    /// oldest samples as the fixed-size window fills. O(n) in the number of samples.
    /// </summary>
    public void Add(byte[] pcm, int bytes)
    {
        var samples = bytes / 2;
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));

            if (_count == _window.Length)
            {
                var evicted = _window[_index];
                _sumSquares -= (double)evicted * evicted;
            }
            else
            {
                _count++;
            }

            _window[_index] = sample;
            _sumSquares += (double)sample * sample;
            _index = (_index + 1) % _window.Length;
        }
    }
}

/// <summary>
/// Static helpers for level-normalizing 16-bit PCM audio.
/// </summary>
internal static class AudioNormalizer
{
    /// <summary>Lower clamp on gain — never attenuate (1.0 = unchanged).</summary>
    public const double MinGain = 1.0;

    /// <summary>Upper clamp on gain — avoid blowing up noise/silence into clipping (~+18 dB).</summary>
    public const double MaxGain = 8.0;

    /// <summary>
    /// Below this RMS the mic is treated as effectively silent and left untouched, so we don't
    /// amplify background hiss by <see cref="MaxGain"/> when nobody is talking.
    /// </summary>
    private const double SilenceFloorRms = 30.0;

    /// <summary>
    /// Computes the gain that brings <paramref name="micRms"/> up toward <paramref name="targetRms"/>
    /// (the recent loopback level), clamped to [<see cref="MinGain"/>, <see cref="MaxGain"/>].
    /// Returns <see cref="MinGain"/> when there is no target (no loopback) or the mic is silent.
    /// </summary>
    public static double ComputeGain(double micRms, double targetRms)
    {
        if (targetRms <= 0.0 || micRms < SilenceFloorRms)
            return MinGain;

        // Match the microphone's measured level to recent loopback RMS, but never attenuate
        // the mic or amplify it by more than the configured ceiling.
        var gain = targetRms / micRms;
        if (gain < MinGain) return MinGain;
        if (gain > MaxGain) return MaxGain;
        return gain;
    }

    /// <summary>
    /// Applies <paramref name="gain"/> to little-endian 16-bit mono PCM in place using saturating
    /// arithmetic (clamped to [-32768, 32767]). A gain of 1.0 is a no-op.
    /// </summary>
    public static void ApplyGain(byte[] pcm, int bytes, double gain)
    {
        if (gain == 1.0)
            return;

        var samples = bytes / 2;
        for (var i = 0; i < samples; i++)
        {
            var idx = i * 2;
            var sample = (short)(pcm[idx] | (pcm[idx + 1] << 8));

            // Clamp before converting back to 16-bit PCM so louder input saturates instead of wrapping.
            var amplified = (int)Math.Round(sample * gain);
            if (amplified > short.MaxValue) amplified = short.MaxValue;
            else if (amplified < short.MinValue) amplified = short.MinValue;

            pcm[idx] = (byte)(amplified & 0xFF);
            pcm[idx + 1] = (byte)((amplified >> 8) & 0xFF);
        }
    }
}
