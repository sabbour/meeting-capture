using System.Diagnostics;
using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.AudioCapture;

/// <summary>Identifies which capture source a PCM buffer came from.</summary>
public enum AudioSource { Loopback, Microphone }

/// <summary>Mixes microphone and loopback PCM on one bounded real-time clock.</summary>
public sealed class DualSourceCapture : IAudioCapture, IMicActivitySource
{
    private const int FrameMilliseconds = 20;
    private readonly object _gate = new();
    private readonly LoopbackCapture _loopback;
    private readonly MicrophoneCapture _microphone;
    private readonly ILogger<DualSourceCapture> _logger;
    private readonly bool _useLoopback, _useMicrophone;
    private readonly AudioHealthTracker _health = new("mixer");
    private ClockAlignedPcmMixer? _pcmMixer;
    private ManualResetEvent? _mixerStop;
    private TaskCompletionSource? _drained;
    private Task? _stopTask;
    private EventHandler<AudioDataEventArgs>? _loopbackHandler, _microphoneHandler;
    private string? _loopbackError, _microphoneError;
    private Exception? _workerFailure;
    private bool _mixerStarted;
    private Thread? _mixer;
    private bool _disposed;

    public string Name => "Dual Source (Loopback + Microphone)";
    public bool IsCapturing
    {
        get
        {
            lock (_gate) return _pcmMixer is not null ||
                (_useLoopback && _loopback.IsCapturing) || (_useMicrophone && _microphone.IsCapturing);
        }
    }
    public int SampleRate { get; }
    public int Channels { get; }
    public DateTime? MicActivityTimestamp => _useMicrophone ? _microphone.MicActivityTimestamp : null;
    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    public IReadOnlyList<AudioSourceHealth> Health => Array.AsReadOnly(new[]
    {
        SourceHealth("microphone", _useMicrophone, _microphone.Health[0], _microphoneError),
        SourceHealth("loopback", _useLoopback, _loopback.Health[0], _loopbackError),
        _health.Snapshot()
    });

    private static AudioSourceHealth SourceHealth(string source, bool enabled, AudioSourceHealth health, string? error) =>
        !enabled ? new(source, CaptureHealthState.Disabled, 0, 0, 0, null, null, null) :
        error is not null ? health with { State = CaptureHealthState.Failed, Error = error } : health;

    public DualSourceCapture(LoopbackCapture loopback, MicrophoneCapture microphone,
        IOptions<AppSettings> settings, ILogger<DualSourceCapture> logger)
    {
        _loopback = loopback;
        _microphone = microphone;
        _logger = logger;
        SampleRate = settings.Value.Audio.SampleRate;
        Channels = settings.Value.Audio.Channels;
        _useLoopback = settings.Value.Audio.CaptureLoopback;
        _useMicrophone = settings.Value.Audio.CaptureMicrophone;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        ClockAlignedPcmMixer mixer;
        ManualResetEvent mixerStop;
        TaskCompletionSource drained;
        lock (_gate)
        {
            // Build one fresh pair of source rings for each run; old callbacks cannot feed the new run.
            if (IsCapturing) throw new InvalidOperationException("The mixer or an audio source is already owned.");
            if (!_useLoopback && !_useMicrophone) throw new InvalidOperationException("No audio source is enabled.");
            _pcmMixer = mixer = new(SampleRate, Channels);
            _mixerStop = mixerStop = new(false);
            _drained = drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = null;
            _workerFailure = null;
            _mixerStarted = false;
            _loopbackError = _microphoneError = null;
            _health.Reset();
        }
        _loopbackHandler = (sender, args) =>
        {
            // Check sender identity as well as the mixer instance to reject late callbacks after stop.
            if (ReferenceEquals(sender, _loopback)) Enqueue(mixer, AudioSource.Loopback, args);
        };
        _microphoneHandler = (sender, args) =>
        {
            if (ReferenceEquals(sender, _microphone)) Enqueue(mixer, AudioSource.Microphone, args);
        };
        if (_useLoopback) _loopback.DataAvailable += _loopbackHandler;
        if (_useMicrophone) _microphone.DataAvailable += _microphoneHandler;
        try
        {
            // Start the clock before the devices so initial packets wait in the rings until their frame is due.
            _mixer = new Thread(() => Mix(mixer, mixerStop, drained))
            {
                IsBackground = true, Name = "MeetingCapture-DualMixer"
            };
            _mixer.Start();
            _mixerStarted = true;
            var loopbackStarted = false;
            var microphoneStarted = false;
            if (_useLoopback)
                loopbackStarted = await TryStartAsync(_loopback, ct, error => _loopbackError = error).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Try both sources even when the first fails; one healthy device is still useful audio.
            if (_useMicrophone)
                microphoneStarted = await TryStartAsync(_microphone, ct, error => _microphoneError = error).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!loopbackStarted && !microphoneStarted)
                throw new InvalidOperationException("None of the enabled audio sources could start.");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> TryStartAsync(IAudioCapture capture, CancellationToken ct, Action<string> setError)
    {
        try { await capture.StartAsync(ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Treat one source failure as degraded capture, not total startup failure, if the other works.
            setError("The configured audio source could not start; its audio is missing.");
            _logger.LogWarning("Audio source {Source} could not start ({ErrorType}); health reports missing audio.", capture.Name, ex.GetType().Name);
            return false;
        }
    }

    private void Enqueue(ClockAlignedPcmMixer mixer, AudioSource source, AudioDataEventArgs args)
    {
        lock (_gate)
        {
            // Serialize ring writes with stop's ownership check and account for any overwritten bytes.
            if (!ReferenceEquals(_pcmMixer, mixer) || args.BytesRecorded == 0) return;
            if (mixer.Add(source, args.Buffer, args.BytesRecorded) > 0) _health.Dropped();
        }
    }

    private void Mix(ClockAlignedPcmMixer mixer, ManualResetEvent stop, TaskCompletionSource drained)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var frame = new byte[mixer.FrameBytes];
            var frameNumber = 1L;
            while (true)
            {
                // Schedule against elapsed time rather than sleeping 20 ms after each frame;
                // the latter would add processing time to every interval and drift.
                var due = TimeSpan.FromMilliseconds(frameNumber * FrameMilliseconds) - clock.Elapsed;
                if (due > TimeSpan.Zero && stop.WaitOne(due)) break;
                if (stop.WaitOne(0)) break;
                EmitFrame(mixer, frame, includeSilence: true);
                // If work ran late, skip missed deadlines instead of sending a burst of stale frames.
                frameNumber = NextFrameNumber(frameNumber, clock.Elapsed);
            }
            // Emit only real buffered samples after stopping; there is no need to extend the recording
            // with silence once the live clock has ended.
            while (mixer.ReadFrame(frame, includeSilence: false)) Emit(frame);
        }
        catch (Exception ex)
        {
            lock (_gate) _workerFailure = ex;
            _health.Failed("The audio mixer failed; some audio was not delivered.");
            _logger.LogError("Audio mixer worker failed ({ErrorType}).", ex.GetType().Name);
        }
        finally { drained.TrySetResult(); }
    }

    internal static long NextFrameNumber(long currentFrame, TimeSpan elapsed)
    {
        // Frame numbers are one-based. Integer division finds the last elapsed boundary, so add one
        // to choose the first deadline still in the future.
        var frameTicks = TimeSpan.FromMilliseconds(FrameMilliseconds).Ticks;
        var firstFutureFrame = elapsed.Ticks / frameTicks + 1;
        return Math.Max(currentFrame + 1, firstFutureFrame);
    }

    private void EmitFrame(ClockAlignedPcmMixer mixer, byte[] frame, bool includeSilence)
    {
        if (mixer.ReadFrame(frame, includeSilence)) Emit(frame);
    }

    private void Emit(byte[] frame)
    {
        // Mix reuses a frame array, so downstream listeners must get a separate buffer.
        var copy = frame.ToArray();
        _health.Received(copy.Length);
        _health.Level(copy, copy.Length);
        DataAvailable?.Invoke(this, new(copy, copy.Length));
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_pcmMixer is null) return Task.CompletedTask;
            return _stopTask ??= Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        List<Exception> errors = [];
        // Child stop includes worker drain; keep handlers attached until both finish.
        // Stop both devices before detaching so their final accepted buffers reach the mixer.
        var sources = new List<IAudioCapture>();
        if (_useLoopback) sources.Add(_loopback);
        if (_useMicrophone) sources.Add(_microphone);
        await Task.WhenAll(sources.Select(async source =>
        {
            try { await source.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        })).ConfigureAwait(false);
        _loopback.DataAvailable -= _loopbackHandler;
        _microphone.DataAvailable -= _microphoneHandler;
        Task drained;
        lock (_gate)
        {
            // The source callbacks are now drained. Signal the mixer and wait for its final ring drain
            // before releasing its state or reusing any of the synchronization objects.
            if (!_mixerStarted) _drained!.TrySetResult();
            else _mixerStop!.Set();
            drained = _drained!.Task;
        }
        await drained.ConfigureAwait(false);
        if (_mixerStarted) _mixer!.Join();
        lock (_gate)
        {
            _mixerStop?.Dispose();
            _mixerStop = null;
            _pcmMixer = null;
            _mixer = null;
            _loopbackHandler = _microphoneHandler = null;
            if (_workerFailure is not null) errors.Add(_workerFailure);
        }
        _health.Stopped();
        if (errors.Count > 0)
        {
            _health.Failed("Audio capture or mixing did not finish cleanly.");
            throw new AggregateException("Audio capture did not finish cleanly.", errors);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally
        {
            // Dispose both children even when stopping the first one reports a failure.
            try { _loopback.Dispose(); }
            finally { _microphone.Dispose(); }
        }
    }
}
