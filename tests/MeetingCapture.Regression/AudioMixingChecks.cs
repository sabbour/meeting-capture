using MeetingCapture.AudioCapture;
using MeetingCapture.Transcription;
using NAudio.Wave;

namespace MeetingCapture.Regression;

internal static class AudioMixingChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("dual-source one-second PCM remains one second", () => Run(Duration));
        yield return ("dual-source PCM mixes exact samples and limits peaks", () => Run(Amplitude));
        yield return ("dual-source PCM aligns fragments and fills missing input with silence", () => Run(Fragments));
        yield return ("dual-source PCM preserves a single available source", () => Run(SingleSource));
        yield return ("dual-source PCM restart state is isolated", () => Run(Restart));
        yield return ("dual-source PCM overload is bounded and drains newest audio", () => Run(Overload));
        yield return ("dual-source mixer skips stale frame deadlines after a consumer stall", () => Run(SkipMissedFrames));
        yield return ("dual-source PCM clock does not backfill late source audio", () => Run(LateSource));
        yield return ("WASAPI capture uses event synchronization for both sources", () => Run(EventSynchronizedCapture));
        yield return ("WASAPI resampling limits warmed conversion allocations", () => Run(ConversionAllocations));
        yield return ("WASAPI resampling preserves samples duration and channel mixing", () => Run(ConversionSamples));
        yield return ("WASAPI resampling drains short packets in FIFO order", () => Run(ConversionDrain));
        yield return ("WASAPI resampling isolates conversion state between sessions", () => Run(ConversionSessions));
        yield return ("Speech cancellation diagnostics classify terminal failures without raw details", () => Run(SpeechTimeout));
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    private static void ConversionAllocations()
    {
        foreach (var rate in new[] { 16000, 32000, 48000, 96000, 192000 })
        foreach (var channels in new[] { 1, 2, 6 })
        {
            var buffered = ConversionInput(rate, channels);
            var pcm = WasapiCaptureWorker.CreatePcmProvider(buffered);
            var input = SyntheticPacket(rate, channels, 0, rate / 10);
            var output = new byte[WasapiCaptureWorker.PcmReadBufferSize(rate)];
            Check.Equal(640, output.Length);
            var measuredBytes = 0;
            var allocatedBefore = 0L;
            var maxReadAllocation = 0L;
            for (var packet = 0; packet < 364; packet++)
            {
                if (packet == 64) allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                buffered.AddSamples(input, 0, input.Length);
                while (true)
                {
                    var beforeRead = GC.GetAllocatedBytesForCurrentThread();
                    var read = pcm.Read(output, 0, output.Length);
                    if (packet >= 64)
                    {
                        maxReadAllocation = Math.Max(maxReadAllocation,
                            GC.GetAllocatedBytesForCurrentThread() - beforeRead);
                        measuredBytes += read;
                    }
                    Check.Equal(0, read % sizeof(short));
                    if (read == 0) break;
                }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var budget = 10_000_000L * Math.Max(1, rate / 48000);
            Check.True(allocated < budget);
            Check.True(maxReadAllocation < 85000);
            Check.Equal(300 * 16000 / 10 * sizeof(short), measuredBytes);
        }
    }

    private static void ConversionSamples()
    {
        foreach (var rate in new[] { 16000, 32000, 44100, 48000, 96000, 192000 })
        foreach (var channels in new[] { 1, 2, 6 })
        {
            var packets = Enumerable.Repeat(rate / 10, 30).ToArray();
            var previous = ConvertSynthetic(rate, channels, 8192, packets);
            var current = ConvertSynthetic(rate, channels, WasapiCaptureWorker.PcmReadBufferSize(rate), packets);
            Check.Equal(30 * 16000 / 10 * sizeof(short), current.Length);
            Check.True(current.SequenceEqual(previous));
            Check.True(ReadSamples(current).Any(sample => sample != 0));
        }
        Check.Equal(8192, WasapiCaptureWorker.PcmReadBufferSize(44100));
        Check.Throws<ArgumentOutOfRangeException>(() => WasapiCaptureWorker.PcmReadBufferSize(0));
        Check.Throws<ArgumentOutOfRangeException>(() => WasapiCaptureWorker.PcmReadBufferSize(-1));
    }

    private static void ConversionDrain()
    {
        int[] frames = [3, 21, 297, 300, 927, 1800, 48];
        foreach (var channels in new[] { 1, 2, 6 })
        {
            var current = ConvertSynthetic(48000, channels,
                WasapiCaptureWorker.PcmReadBufferSize(48000), frames);
            var previous = ConvertSynthetic(48000, channels, 8192, frames);
            Check.Equal(frames.Sum() / 3 * sizeof(short), current.Length);
            Check.True(current.SequenceEqual(previous));
        }
    }

    private static void ConversionSessions()
    {
        var first = ConversionInput(48000, 2);
        var firstPcm = WasapiCaptureWorker.CreatePcmProvider(first);
        var source = SyntheticPacket(48000, 2, 0, 4800);
        first.AddSamples(source, 0, source.Length);
        var output = new byte[WasapiCaptureWorker.PcmReadBufferSize(48000)];
        Check.True(firstPcm.Read(output, 0, output.Length) > 0);

        var second = ConversionInput(48000, 2);
        var secondPcm = WasapiCaptureWorker.CreatePcmProvider(second);
        second.AddSamples(new byte[source.Length], 0, source.Length);
        var bytes = 0;
        int read;
        while ((read = secondPcm.Read(output, 0, output.Length)) > 0)
        {
            bytes += read;
            Check.True(output.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0);
        }
        Check.Equal(3200, bytes);
    }

    private static BufferedWaveProvider ConversionInput(int rate, int channels)
        => new(WaveFormat.CreateIeeeFloatWaveFormat(rate, channels))
        {
            ReadFully = false, DiscardOnBufferOverflow = false, BufferDuration = TimeSpan.FromSeconds(10)
        };

    private static byte[] SyntheticPacket(int rate, int channels, int firstFrame, int frames)
    {
        var bytes = new byte[frames * channels * sizeof(float)];
        for (var frame = 0; frame < frames; frame++)
        for (var channel = 0; channel < channels; channel++)
        {
            var sample = (float)((channel + 1) * 0.05 *
                Math.Sin(2 * Math.PI * 440 * (firstFrame + frame) / rate));
            BitConverter.TryWriteBytes(bytes.AsSpan((frame * channels + channel) * sizeof(float)), sample);
        }
        return bytes;
    }

    private static byte[] ConvertSynthetic(int rate, int channels, int outputBytes, int[] packetFrames)
    {
        var buffered = ConversionInput(rate, channels);
        var pcm = WasapiCaptureWorker.CreatePcmProvider(buffered);
        var output = new byte[outputBytes];
        var result = new List<byte>();
        var firstFrame = 0;
        foreach (var frames in packetFrames)
        {
            var input = SyntheticPacket(rate, channels, firstFrame, frames);
            firstFrame += frames;
            buffered.AddSamples(input, 0, input.Length);
            int read;
            while ((read = pcm.Read(output, 0, output.Length)) > 0)
                result.AddRange(output.AsSpan(0, read).ToArray());
        }
        Check.Equal(0, pcm.Read(output, 0, output.Length));
        return result.ToArray();
    }

    private static void Duration()
    {
        var mixer = new ClockAlignedPcmMixer(16000, 1);
        var oneSecond = Samples(Enumerable.Repeat((short)1000, 16000).ToArray());
        Check.Equal(0, mixer.Add(AudioSource.Microphone, oneSecond, oneSecond.Length));
        Check.Equal(0, mixer.Add(AudioSource.Loopback, oneSecond, oneSecond.Length));
        var frame = new byte[mixer.FrameBytes];
        var bytes = 0;
        while (mixer.ReadFrame(frame, includeSilence: false)) bytes += frame.Length;
        Check.Equal(16000 * sizeof(short), bytes);
    }

    private static void Amplitude()
    {
        var mixer = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 20);
        mixer.Add(AudioSource.Microphone, Samples(1000, -1000, 20000, -20000), 8);
        mixer.Add(AudioSource.Loopback, Samples(2000, 500, 20000, -20000), 8);
        var frame = new byte[mixer.FrameBytes];
        Check.True(mixer.ReadFrame(frame, includeSilence: false));
        var mixed = ReadSamples(frame);
        Check.Equal((short)2458, mixed[0]);
        Check.Equal((short)-410, mixed[1]);
        Check.Equal(short.MaxValue, mixed[2]);
        Check.Equal((short)-32767, mixed[3]);
    }

    private static void Fragments()
    {
        var mixer = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 4);
        var mic = Samples(100, 200, 300, 400);
        mixer.Add(AudioSource.Microphone, mic[..2], 2);
        mixer.Add(AudioSource.Microphone, mic[2..], 6);
        mixer.Add(AudioSource.Loopback, Samples(10, 20), 4);
        var frame = new byte[mixer.FrameBytes];
        Check.True(mixer.ReadFrame(frame, includeSilence: false));
        Check.True(ReadSamples(frame).SequenceEqual(new short[] { 110, 220, 300, 400 }));
        Check.True(!mixer.ReadFrame(frame, includeSilence: false));
        Check.True(mixer.ReadFrame(frame, includeSilence: true));
        Check.True(ReadSamples(frame).All(sample => sample == 0));
    }

    private static void SingleSource()
    {
        var mixer = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 4);
        var source = Samples(123, -456, 789, -1011);
        mixer.Add(AudioSource.Microphone, source, source.Length);
        var frame = new byte[mixer.FrameBytes];
        Check.True(mixer.ReadFrame(frame, includeSilence: false));
        Check.True(ReadSamples(frame).SequenceEqual(new short[] { 123, -456, 789, -1011 }));
    }

    private static void Restart()
    {
        var first = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 2);
        first.Add(AudioSource.Microphone, Samples(111, 222), 4);
        var restarted = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 2);
        restarted.Add(AudioSource.Loopback, Samples(333, 444), 4);
        var frame = new byte[restarted.FrameBytes];
        Check.True(restarted.ReadFrame(frame, includeSilence: false));
        Check.True(ReadSamples(frame).SequenceEqual(new short[] { 333, 444 }));
        Check.True(!restarted.HasBufferedAudio);
    }

    private static void Overload()
    {
        var mixer = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 20, maxBufferedMilliseconds: 40);
        var input = Samples(Enumerable.Range(1, 60).Select(i => (short)i).ToArray());
        Check.Equal(40, mixer.Add(AudioSource.Microphone, input, input.Length));
        var frame = new byte[mixer.FrameBytes];
        var drained = new List<short>();
        while (mixer.ReadFrame(frame, includeSilence: false)) drained.AddRange(ReadSamples(frame));
        Check.Equal(40, drained.Count);
        Check.Equal((short)21, drained[0]);
        Check.Equal((short)60, drained[^1]);
    }

    private static void SkipMissedFrames()
    {
        Check.Equal(2L, DualSourceCapture.NextFrameNumber(1, TimeSpan.FromMilliseconds(20)));
        Check.Equal(3L, DualSourceCapture.NextFrameNumber(2, TimeSpan.FromMilliseconds(40)));
        Check.Equal(6L, DualSourceCapture.NextFrameNumber(2, TimeSpan.FromMilliseconds(100)));
        Check.Equal(30001L, DualSourceCapture.NextFrameNumber(2, TimeSpan.FromMinutes(10)));
        Check.Equal(51L, DualSourceCapture.NextFrameNumber(50, TimeSpan.FromSeconds(1)));

        var mixer = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 20, maxBufferedMilliseconds: 80);
        var retained = Enumerable.Range(1, 60).Select(value => (short)value).ToArray();
        var input = Samples(retained);
        Check.Equal(0, mixer.Add(AudioSource.Microphone, input, input.Length));
        var frame = new byte[mixer.FrameBytes];
        Check.True(mixer.ReadFrame(frame, includeSilence: false));
        Check.True(ReadSamples(frame).SequenceEqual(retained[..20]));

        _ = DualSourceCapture.NextFrameNumber(2, TimeSpan.FromMilliseconds(100));
        var drained = new List<short>();
        while (mixer.ReadFrame(frame, includeSilence: false))
            drained.AddRange(ReadSamples(frame));
        Check.True(drained.SequenceEqual(retained[20..]));
    }

    private static void LateSource()
    {
        var mixer = new ClockAlignedPcmMixer(1000, 1, frameMilliseconds: 2);
        var frame = new byte[mixer.FrameBytes];
        Check.True(mixer.ReadFrame(frame, includeSilence: true));
        Check.True(ReadSamples(frame).All(sample => sample == 0));
        mixer.Add(AudioSource.Microphone, Samples(700, 800), 4);
        Check.True(mixer.ReadFrame(frame, includeSilence: true));
        Check.True(ReadSamples(frame).SequenceEqual(new short[] { 700, 800 }));
    }

    private static void EventSynchronizedCapture()
    {
        var microphone = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MicrophoneCapture.cs.txt"));
        var loopback = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LoopbackCapture.cs.txt"));
        Check.True(microphone.Contains(
            "new WasapiCapture(WasapiCapture.GetDefaultCaptureDevice(), true, 100)",
            StringComparison.Ordinal));
        Check.True(loopback.Contains(
            ": WasapiCapture(WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(), true, 100)",
            StringComparison.Ordinal));
        Check.True(loopback.Contains(
            "AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags()",
            StringComparison.Ordinal));
    }

    private static void SpeechTimeout()
    {
        Check.True(SpeechCancellationDiagnostics.RequiresReconnect("ServiceTimeout"));
        Check.True(!SpeechCancellationDiagnostics.RequiresReconnect("AuthenticationFailure"));
        Check.Equal(
            "Speech service timed out; transcription will reconnect when audio resumes.",
            SpeechCancellationDiagnostics.Describe("ServiceTimeout", null));
        Check.Equal(
            "Speech service timed out after service inactivity; transcription will reconnect when audio resumes.",
            SpeechCancellationDiagnostics.Describe(
                "ServiceTimeout", "Due to service inactivity the client buffer size exceeded."));
        Check.True(!SpeechCancellationDiagnostics.Describe(
            "ServiceTimeout", "synthetic private detail").Contains("synthetic", StringComparison.Ordinal));
    }

    private static byte[] Samples(params short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static short[] ReadSamples(byte[] bytes)
    {
        var samples = new short[bytes.Length / sizeof(short)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }
}
