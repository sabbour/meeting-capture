using MeetingCapture.AudioCapture;
using MeetingCapture.Transcription;

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
        yield return ("dual-source PCM clock does not backfill late source audio", () => Run(LateSource));
        yield return ("WASAPI capture uses event synchronization for both sources", () => Run(EventSynchronizedCapture));
        yield return ("Speech cancellation diagnostics classify terminal failures without raw details", () => Run(SpeechTimeout));
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

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
