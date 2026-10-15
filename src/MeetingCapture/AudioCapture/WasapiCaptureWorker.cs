using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingCapture.AudioCapture;

// Microphone and loopback share exactly the same ownership/drain protocol.
/// <summary>Owns one WASAPI device and converts its captured audio to drained 16 kHz mono PCM.</summary>
internal sealed class WasapiCaptureWorker(string source, Func<WasapiCapture> createCapture,
    Action<byte[], int> output, ILogger logger)
{
    private const int OutputSampleRate = 16000;
    private readonly object _gate = new();
    private Session? _session;
    private readonly AudioHealthTracker _health = new(source);
    /// <summary>Returns the current health snapshot for this capture source.</summary>
    public AudioSourceHealth Health => _health.Snapshot();
    /// <summary>Reports whether the worker currently owns an active capture session.</summary>
    public bool IsCapturing { get { lock (_gate) return _session is not null; } }

    private sealed class Session(WasapiCapture capture)
    {
        public WasapiCapture Capture { get; } = capture;
        public BlockingCollection<(byte[] Data, int Bytes)> Queue { get; } = new(300);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<WaveInEventArgs>? DataHandler;
        public EventHandler<StoppedEventArgs>? StopHandler;
        public Task? StopTask;
        public Exception? Failure;
        public bool RecordingStarted;
        public bool WorkerStarted;
        public Thread? Worker;
        public bool Accepting = true;
        public bool Released;
    }

    /// <summary>Starts the device and PCM consumer, releasing owned resources if startup fails.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Keep device creation and startup off the caller's thread because WASAPI setup can block.
        await Task.Run(async () =>
        {
            Session session;
            lock (_gate)
            {
                // A session is installed before the device starts so callbacks can verify that
                // they still belong to the current owner.
                if (_session is not null) throw new InvalidOperationException("The capture source is already owned.");
                _health.Reset();
                try { session = new Session(createCapture()); }
                catch (Exception ex)
                {
                    _health.Failed("The configured audio device could not be opened.");
                    logger.LogError("Audio source {Source} could not open ({ErrorType}).", source, ex.GetType().Name);
                    throw;
                }
                _session = session;
            }
            try
            {
                session.DataHandler = (sender, args) =>
                {
                    lock (_gate)
                    {
                        // NAudio reuses its callback buffer. Copy accepted bytes before returning,
                        // then let the separate consumer thread do conversion and delivery.
                        if (!ReferenceEquals(_session, session) || !ReferenceEquals(sender, session.Capture) ||
                            !session.Accepting || args.BytesRecorded == 0) return;
                        _health.Received(args.BytesRecorded);
                        var copy = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
                        if (!session.Queue.TryAdd((copy, args.BytesRecorded))) _health.Dropped();
                    }
                };
                session.StopHandler = (sender, args) =>
                {
                    if (!ReferenceEquals(sender, session.Capture)) return;
                    lock (_gate)
                    {
                        // Stop accepting data and close the queue as one state change so the
                        // consumer can drain every buffer that arrived before this callback.
                        if (!ReferenceEquals(_session, session) || session.Released) return;
                        if (args.Exception is not null)
                        {
                            session.Failure = args.Exception;
                            _health.Failed("The audio device stopped unexpectedly.");
                            logger.LogError("WASAPI source {Source} stopped unexpectedly ({ErrorType}).", source, args.Exception.GetType().Name);
                        }
                        else if (session.StopTask is null)
                        {
                            session.Failure = new InvalidOperationException("The audio device stopped unexpectedly.");
                            _health.Failed("The audio device stopped unexpectedly.");
                        }
                        session.Accepting = false;
                        session.Queue.CompleteAdding();
                    }
                    session.Stopped.TrySetResult();
                };
                session.Capture.DataAvailable += session.DataHandler;
                session.Capture.RecordingStopped += session.StopHandler;
                session.Worker = new Thread(() => Consume(session))
                {
                    IsBackground = true, Name = $"MeetingCapture-{source}-consumer"
                };
                // Start the consumer before WASAPI so the bounded queue is ready for the first callback.
                session.Worker.Start();
                session.WorkerStarted = true;
                ct.ThrowIfCancellationRequested();
                session.RecordingStarted = true;
                try { session.Capture.StartRecording(); }
                catch
                {
                    // A failed native start may not raise RecordingStopped; Dispose still joins its thread.
                    session.RecordingStarted = false;
                    throw;
                }
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                // Startup can fail after either worker or device resources were created; use the
                // normal stop path to drain and release whatever was successfully started.
                _health.Failed("The configured audio device could not start.");
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private void Consume(Session session)
    {
        try
        {
            var format = session.Capture.WaveFormat;
            // WASAPI packets may not align with output reads. Buffer them so the resampler can
            // produce continuous 16 kHz samples instead of treating each packet as a new stream.
            var buffered = new BufferedWaveProvider(format)
            {
                ReadFully = false, DiscardOnBufferOverflow = false, BufferDuration = TimeSpan.FromSeconds(10)
            };
            var pcm = CreatePcmProvider(buffered);
            var buffer = new byte[PcmReadBufferSize(format.SampleRate)];
            foreach (var (data, bytes) in session.Queue.GetConsumingEnumerable())
            {
                buffered.AddSamples(data, 0, bytes);
                int read;
                // Read all output currently available before taking the next device packet.
                while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // The read buffer is reused, so listeners receive their own stable copy.
                    var copy = buffer.AsSpan(0, read).ToArray();
                    _health.Level(copy, read);
                    output(copy, read);
                }
            }
        }
        catch (Exception ex)
        {
            // Stop new callbacks and complete the queue; StopCoreAsync will still join this worker.
            lock (_gate)
            {
                session.Failure = ex;
                session.Accepting = false;
                session.Queue.CompleteAdding();
            }
            _health.Failed("Audio processing failed; some audio was not delivered.");
            logger.LogError("Audio source {Source} worker failed ({ErrorType}).", source, ex.GetType().Name);
        }
        finally { session.Drained.TrySetResult(); }
    }

    /// <summary>Chooses a 20 ms PCM read for exact-rate multiples while preserving other rates' read size.</summary>
    internal static int PcmReadBufferSize(int inputSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSampleRate);
        // WDL changes fractional-rate samples with the read size, so preserve those reads.
        // Exact multiples can use 20 ms reads: 16 kHz * 2 bytes / 50 reads per second.
        return inputSampleRate % OutputSampleRate == 0
            ? OutputSampleRate * sizeof(short) / 50
            : 8192;
    }

    /// <summary>Downmixes multichannel input when needed, then resamples it to 16 kHz PCM.</summary>
    internal static IWaveProvider CreatePcmProvider(BufferedWaveProvider input)
    {
        // Convert through float samples so channels can be averaged before resampling.
        ISampleProvider samples = input.ToSampleProvider();
        if (input.WaveFormat.Channels > 1) samples = new MonoDownmixSampleProvider(samples);
        return new SampleToWaveProvider16(new WdlResamplingSampleProvider(samples, OutputSampleRate));
    }

    /// <summary>Stops capture, drains queued audio, and releases the device and consumer worker.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_session is not { } session) return Task.CompletedTask;
            // Share one stop operation among concurrent callers so the device is released once.
            return session.StopTask ??= Task.Run(() => StopCoreAsync(session));
        }
    }

    private async Task StopCoreAsync(Session session)
    {
        Exception? stopFailure = null;
        var disposed = false;
        try
        {
            if (session.RecordingStarted)
            {
                session.Capture.StopRecording();
                await session.Stopped.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            stopFailure = ex;
            _health.Failed("The audio device failed during shutdown.");
            logger.LogError("WASAPI source {Source} shutdown failed ({ErrorType}).", source, ex.GetType().Name);
        }
        finally
        {
            // Dispose joins NAudio's hardware thread. No timeout is evidence of quiescence.
            try { session.Capture.Dispose(); disposed = true; }
            catch (Exception ex) { stopFailure ??= ex; _health.Failed("Audio device disposal failed."); }
            lock (_gate)
            {
                // No callback may enqueue after completion. The consumer exits only after it has
                // processed all queued packets, and is joined before its queue is disposed.
                session.Accepting = false;
                session.Queue.CompleteAdding();
            }
            if (!session.WorkerStarted) session.Drained.TrySetResult();
            await session.Drained.Task.ConfigureAwait(false);
            if (session.WorkerStarted) session.Worker!.Join();
            session.Capture.DataAvailable -= session.DataHandler;
            session.Capture.RecordingStopped -= session.StopHandler;
            lock (_gate)
            {
                session.Released = true;
                session.Queue.Dispose();
                // Keep ownership if disposal failed: a later start must not overlap a device
                // whose native capture thread may still be alive.
                if (disposed && ReferenceEquals(_session, session)) _session = null;
            }
            _health.Stopped();
        }
        if (stopFailure is not null || session.Failure is not null)
            throw new InvalidOperationException("The audio source did not complete cleanly.", stopFailure ?? session.Failure);
    }
}

/// <summary>Averages all channels of a float sample stream into one mono channel.</summary>
internal sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _buffer = [];
    public WaveFormat WaveFormat { get; }
    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        var channels = _source.WaveFormat.Channels;
        var requested = count * channels;
        if (_buffer.Length < requested) _buffer = new float[requested];
        // ISampleProvider counts individual samples; divide by channel count to get complete frames.
        var frames = _source.Read(_buffer, 0, requested) / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            // Averaging keeps one mono sample per time point without favoring a channel.
            for (var channel = 0; channel < channels; channel++) sum += _buffer[frame * channels + channel];
            buffer[offset + frame] = sum / channels;
        }
        return frames;
    }
}
