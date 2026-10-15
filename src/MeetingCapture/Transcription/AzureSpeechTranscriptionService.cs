using System.Diagnostics;
using MeetingCapture.AudioCapture;
using MeetingCapture.Configuration;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.Transcription;

/// <summary>Live transcription with Azure AI Speech. Audio is pushed in and results arrive as segments.</summary>
public sealed class AzureSpeechTranscriptionService : ITranscriptionService
{
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly object _callbackGate = new();
    private readonly TranscriptionSettings _settings;
    private readonly ILogger<AzureSpeechTranscriptionService> _logger;
    private Session? _session;
    private CancellationTokenSource? _startupCancellation;
    private Task _startTask = Task.CompletedTask;
    private Task<TranscriptionResult>? _stopTask;
    private Task<bool>? _recoveryTask;
    private bool _owned, _disposed, _transcribing, _stopRequested;
    private bool _releaseFailed;
    private bool _recoveryRequired;
    private TranscriptionResult _result = new();
    private long _bytes, _writeCount;
    private DateTimeOffset? _lastTranscript;
    private double _lastWrite;
    // p95 is nearest-rank over the last 128 synchronous SDK pushes, not an unbounded session log.
    private readonly double[] _writeSamples = new double[128];
    private int _sampleCount, _sampleIndex;
    private string? _error;
    private CaptureHealthState _healthState = CaptureHealthState.Ready;

    private sealed class Session(SpeechConfig speech, AudioStreamFormat format,
        PushAudioInputStream stream, AudioConfig audio, ConversationTranscriber transcriber)
    {
        // Speech SDK 1.41.1 exposes no disposal API for SpeechConfig.
        public SpeechConfig Speech { get; } = speech;
        public AudioStreamFormat Format { get; } = format;
        public PushAudioInputStream Stream { get; } = stream;
        public AudioConfig Audio { get; } = audio;
        public ConversationTranscriber Transcriber { get; } = transcriber;
        public EventHandler<ConversationTranscriptionEventArgs>? FinalHandler, PartialHandler;
        public EventHandler<ConversationTranscriptionCanceledEventArgs>? CancelHandler;
        public bool Started;
        public bool AcceptCallbacks = true;
    }

    public event EventHandler<TranscriptionSegment>? SegmentRecognized;
    public event EventHandler<PartialTranscript>? PartialResultReceived;
    public bool IsTranscribing { get { lock (_gate) return _transcribing; } }
    public bool HasActiveResources { get { lock (_gate) return _owned; } }
    public long TotalAudioBytesReceived { get { lock (_gate) return _bytes; } }
    public TranscriptionHealth Health
    {
        get
        {
            lock (_gate)
            {
                var samples = _writeSamples.AsSpan(0, _sampleCount).ToArray();
                Array.Sort(samples);
                var p95 = samples.Length == 0 ? 0 : samples[(int)Math.Ceiling(samples.Length * .95) - 1];
                return new(_healthState, _bytes, _lastTranscript, _writeCount, _lastWrite, p95, _error);
            }
        }
    }

    public AzureSpeechTranscriptionService(IOptions<AppSettings> settings, ILogger<AzureSpeechTranscriptionService> logger)
    {
        _settings = settings.Value.Transcription;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
        => BeginStartAsync(ct, resetSessionMetrics: true);

    private Task BeginStartAsync(CancellationToken ct, bool resetSessionMetrics)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_owned) throw new InvalidOperationException("A transcription session is already owned.");
            _owned = true;
            if (resetSessionMetrics)
            {
                _result = new();
                _bytes = _writeCount = 0;
                _lastTranscript = null;
                _lastWrite = 0;
                _sampleCount = _sampleIndex = 0;
            }
            _error = null;
            _recoveryRequired = false;
            _stopRequested = false;
            _healthState = CaptureHealthState.Ready;
            _startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _stopTask = null;
            _startTask = Task.Run(() => StartCoreAsync(_startupCancellation.Token));
            return _startTask;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var key = !string.IsNullOrWhiteSpace(_settings.ApiKey) ? _settings.ApiKey :
                Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
            if (string.IsNullOrWhiteSpace(key))
                throw SpeechStartupDiagnostics.Describe(TranscriptionStartFailureKind.CredentialsMissing);
            if (!Uri.TryCreate(_settings.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps)
                throw SpeechStartupDiagnostics.Describe(TranscriptionStartFailureKind.ConfigurationInvalid);

            SpeechConfig? speech = null;
            AudioStreamFormat? format = null;
            PushAudioInputStream? stream = null;
            AudioConfig? audio = null;
            ConversationTranscriber? transcriber = null;
            Session session;
            try
            {
                speech = BuildSpeechConfig(key, endpoint);
                speech.EnableDictation();
                format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
                stream = AudioInputStream.CreatePushStream(format);
                audio = AudioConfig.FromStreamInput(stream);
                transcriber = new ConversationTranscriber(speech, audio);
                session = new(speech, format, stream, audio, transcriber);
            }
            catch (Exception creationError)
            {
                List<Exception> cleanupErrors = [];
                foreach (var resource in new IDisposable?[] { transcriber, audio, stream, format })
                    try { resource?.Dispose(); }
                    catch (Exception ex) { cleanupErrors.Add(ex); _releaseFailed = true; }
                if (cleanupErrors.Count > 0)
                    throw new AggregateException("Speech startup rollback failed.", cleanupErrors.Prepend(creationError));
                throw;
            }
            lock (_gate) _session = session;
            session.FinalHandler = (sender, args) =>
            {
                lock (_callbackGate)
                {
                    TranscriptionSegment segment;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_session, session) || !ReferenceEquals(sender, session.Transcriber) ||
                            !session.AcceptCallbacks || args.Result.Reason != ResultReason.RecognizedSpeech ||
                            string.IsNullOrWhiteSpace(args.Result.Text)) return;
                        segment = new TranscriptionSegment(args.Result.Text, DateTimeOffset.UtcNow,
                            SpeakerId: NormalizeSpeaker(args.Result.SpeakerId));
                        _result.Segments.Add(segment);
                        _lastTranscript = DateTimeOffset.UtcNow;
                    }
                    Publish(SegmentRecognized, segment);
                }
            };
            session.PartialHandler = (sender, args) =>
            {
                lock (_callbackGate)
                {
                    lock (_gate)
                        if (!ReferenceEquals(_session, session) || !ReferenceEquals(sender, session.Transcriber) ||
                            !session.AcceptCallbacks || args.Result.Reason != ResultReason.RecognizingSpeech ||
                            string.IsNullOrWhiteSpace(args.Result.Text)) return;
                    Publish(PartialResultReceived, new PartialTranscript(args.Result.Text, NormalizeSpeaker(args.Result.SpeakerId)));
                }
            };
            session.CancelHandler = (sender, args) =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_session, session) || !ReferenceEquals(sender, session.Transcriber) ||
                        !session.AcceptCallbacks || args.Reason != CancellationReason.Error) return;
                    if (SpeechCancellationDiagnostics.RequiresReconnect(args.ErrorCode.ToString()))
                    {
                        // The continuous-recognition service has an inactivity limit. Keep the
                        // accumulated transcript and reconnect before the next PCM write.
                        _transcribing = false;
                        _recoveryRequired = true;
                        _healthState = CaptureHealthState.Ready;
                        _logger.LogWarning("Speech service timed out; reconnecting when audio resumes.");
                        return;
                    }
                    _transcribing = false;
                    Fail(SpeechCancellationDiagnostics.Describe(args.ErrorCode.ToString(), args.ErrorDetails));
                }
            };
            transcriber.Transcribed += session.FinalHandler;
            transcriber.Transcribing += session.PartialHandler;
            transcriber.Canceled += session.CancelHandler;
            // SDK startup is not cancelable. Retain ownership until it actually completes, then roll back.
            await transcriber.StartTranscribingAsync().ConfigureAwait(false);
            session.Started = true;
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_error is not null) throw SpeechStartupDiagnostics.Describe(SpeechStartupDiagnostics.ClassifyMessage(_error));
                _transcribing = true;
                _healthState = CaptureHealthState.Active;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) Fail("Speech startup canceled.");
            try { await ReleaseSessionAsync().ConfigureAwait(false); }
            finally { ReleaseOwnership(); }
            throw;
        }
        catch (Exception ex)
        {
            // Startup faults stay typed and sanitized: SDK text can carry endpoint or payload detail.
            var failure = ex as TranscriptionStartException ??
                SpeechStartupDiagnostics.Describe(SpeechStartupDiagnostics.Classify(ex), ex);
            lock (_gate) Fail(failure.Message);
            try { await ReleaseSessionAsync().ConfigureAwait(false); }
            finally { ReleaseOwnership(); }
            throw failure;
        }
    }

    public async Task PushAudioAsync(byte[] pcmData, int bytesRecorded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcmData);
        if (bytesRecorded < 0 || bytesRecorded > pcmData.Length || bytesRecorded % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRecorded));
        ct.ThrowIfCancellationRequested();
        if (bytesRecorded == 0) return;
        Task<bool>? recovery;
        lock (_gate) recovery = _recoveryRequired ? StartRecoveryLocked(ct) : null;
        if (recovery is not null && !await recovery.ConfigureAwait(false))
            throw new InvalidOperationException("Speech transcription could not reconnect.");
        lock (_writeGate)
        {
            Session session;
            lock (_gate)
            {
                if (!_transcribing || _session is null)
                    throw new InvalidOperationException("Speech transcription is not accepting audio.");
                session = _session;
            }
            var started = Stopwatch.GetTimestamp();
            try
            {
                session.Stream.Write(pcmData, bytesRecorded);
                lock (_gate) _bytes += bytesRecorded;
            }
            catch
            {
                lock (_gate)
                {
                    _transcribing = false;
                    Fail("Writing audio to Speech failed; some audio was not transcribed.");
                }
                throw;
            }
            finally
            {
                var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (_gate)
                {
                    _lastWrite = duration;
                    _writeCount++;
                    _writeSamples[_sampleIndex] = duration;
                    _sampleIndex = (_sampleIndex + 1) % _writeSamples.Length;
                    _sampleCount = Math.Min(_sampleCount + 1, _writeSamples.Length);
                }
            }
        }
    }

    private Task<bool> StartRecoveryLocked(CancellationToken ct)
    {
        if (_recoveryTask is { IsCompleted: false }) return _recoveryTask;
        return _recoveryTask = Task.Run(async () =>
        {
            try
            {
                try { await ReleaseSessionAsync().ConfigureAwait(false); }
                finally { ReleaseOwnership(); }
                lock (_gate)
                    if (_disposed || _stopRequested) return false;
                await BeginStartAsync(ct, resetSessionMetrics: false).ConfigureAwait(false);
                _logger.LogInformation("Speech transcription reconnected after service inactivity.");
                return true;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _recoveryRequired = false;
                    Fail("Speech transcription could not reconnect after service inactivity.");
                }
                _logger.LogError("Speech reconnection failed ({ErrorType}).", ex.GetType().Name);
                return false;
            }
        });
    }

    public Task<TranscriptionResult> StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _stopRequested = true;
            if (_stopTask is not null) return _stopTask;
            if (!_owned) return Task.FromResult(CopyResult());
            if (_recoveryTask is { IsCompleted: false } recovery)
            {
                return _stopTask = Task.Run(async () =>
                {
                    await recovery.ConfigureAwait(false);
                    lock (_gate) _stopTask = null;
                    return await StopAsync(ct).ConfigureAwait(false);
                });
            }
            _startupCancellation?.Cancel();
            var startup = _startTask;
            return _stopTask = Task.Run(async () =>
            {
                try { await startup.ConfigureAwait(false); }
                catch (Exception) when (!_owned) { return CopyResult(); }
                try
                {
                    await ReleaseSessionAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_error is not null) throw new InvalidOperationException(_error);
                        _healthState = CaptureHealthState.Stopped;
                        return CopyResult();
                    }
                }
                finally { ReleaseOwnership(); }
            });
        }
    }

    private async Task ReleaseSessionAsync()
    {
        Session? session;
        lock (_gate) session = _session;
        if (session is null) return;
        List<Exception> errors = [];
        try
        {
            lock (_writeGate)
            {
                lock (_gate) _transcribing = false;
                session.Stream.Close();
            }
        }
        catch (Exception ex) { errors.Add(ex); }
        try
        {
            if (session.Started) await session.Transcriber.StopTranscribingAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { errors.Add(ex); }
        lock (_callbackGate)
        {
            lock (_gate)
            {
                session.AcceptCallbacks = false;
                session.Transcriber.Transcribed -= session.FinalHandler;
                session.Transcriber.Transcribing -= session.PartialHandler;
                session.Transcriber.Canceled -= session.CancelHandler;
            }
        }
        foreach (var resource in new IDisposable[] { session.Transcriber, session.Audio, session.Stream, session.Format })
            try { resource.Dispose(); }
            catch (Exception ex) { errors.Add(ex); _releaseFailed = true; }
        lock (_gate)
        {
            if (!_releaseFailed && ReferenceEquals(_session, session)) _session = null;
            if (errors.Count > 0) Fail("Speech shutdown failed; available transcript was retained.");
        }
        if (errors.Count > 0) throw new AggregateException("Speech shutdown failed.", errors);
    }

    private void ReleaseOwnership()
    {
        lock (_gate)
        {
            _owned = _releaseFailed;
            _transcribing = false;
            _recoveryRequired = false;
            _startupCancellation?.Dispose();
            _startupCancellation = null;
        }
    }

    private TranscriptionResult CopyResult()
    {
        lock (_gate)
        {
            var copy = new TranscriptionResult();
            copy.Segments.AddRange(_result.Segments);
            return copy;
        }
    }

    private void Fail(string message)
    {
        if (_error is null) _logger.LogError("{SpeechFailure}", message);
        _error ??= message;
        _healthState = CaptureHealthState.Failed;
    }

    private void Publish<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, args); }
            catch (Exception ex)
            {
                lock (_gate) Fail("A transcription consumer failed to accept a result.");
                _logger.LogError("Speech result consumer failed ({ErrorType}).", ex.GetType().Name);
            }
    }

    private static string NormalizeSpeaker(string? id) => string.IsNullOrWhiteSpace(id) ? "Unknown" : id;

    private SpeechConfig BuildSpeechConfig(string key, Uri endpoint)
    {
        var region = _settings.Region;
        var host = endpoint.Host.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(region) &&
            (host.EndsWith(".api.cognitive.microsoft.com", StringComparison.Ordinal) ||
             host.EndsWith(".stt.speech.microsoft.com", StringComparison.Ordinal) ||
             host.EndsWith(".tts.speech.microsoft.com", StringComparison.Ordinal)))
            region = host.Split('.')[0];
        var config = !string.IsNullOrWhiteSpace(region) ? SpeechConfig.FromSubscription(key, region) :
            SpeechConfig.FromEndpoint(new Uri($"wss://{host}/stt/speech/recognition/conversation/cognitiveservices/v1"), key);
        if (!string.IsNullOrWhiteSpace(_settings.Language)) config.SpeechRecognitionLanguage = _settings.Language;
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "300");
        config.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "0");
        config.SetProperty(PropertyId.SpeechServiceResponse_ProfanityOption, "raw");
        config.SetProperty(PropertyId.SpeechServiceResponse_DiarizeIntermediateResults, "true");
        config.SetProperty("ConversationTranscription_MaxSpeakers", "6");
        config.OutputFormat = OutputFormat.Detailed;
        return config;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
