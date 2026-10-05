using MeetingCapture.AudioCapture;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging;

namespace MeetingCapture.Pipeline;

public sealed class MeetingPipeline : IAsyncDisposable
{
    private static readonly TimeSpan SaveReviewDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MeetingEndInactivityWindow = TimeSpan.FromMinutes(2);
    private const long AudibleRmsSquared = 500L * 500L;
    private readonly object _gate = new();
    private readonly IMeetingDetector _detector;
    private readonly IAudioCapture[] _devices;
    private readonly ITranscriptionService _transcription;
    private readonly INotesIntelligence _intelligence;
    private readonly IMeetingVisualizationIntelligence _visualizationIntelligence;
    private readonly INotesStorage _storage;
    private readonly MeetingVisualizationSession _visualization;
    private readonly IMeetingVisualizationStorage _visualizationStorage;
    private readonly IPendingVisualizationJournal? _pendingVisualizationJournal;
    private readonly SpeakerResolver _speakerResolver;
    private readonly Func<CancellationToken, Task<string?>> _getLocalUserDisplayName;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly ILogger<MeetingPipeline> _logger;
    private readonly AiUsageMonitor _usage;
    private readonly AiDiagnosticsStream _diagnostics;
    private PipelineStatus _status = new(0, null, PipelineState.Idle, "Ready", null, null, null);
    private SessionRun? _run;
    private MeetingSession? _pendingSession;
    private TranscriptSnapshot? _lastCompleted;
    private MeetingNotes? _lastSavedNotes;
    private string? _lastSavedPath;
    private Task _startTask = Task.CompletedTask;
    private Task? _stopTask;
    private Task<string>? _retryTask;
    private Task? _visualizationAnalysisTask;
    private string? _visualizationAnalysisSessionId;
    private long _visualizationAnalysisTranscriptRevision = -1;
    private int _visualizationAnalysisTopicCount = -1;
    private bool _visualizationAnalysisRefreshPending;
    private Task? _previewTask;
    private Task? _previewStopTask;
    private CancellationTokenSource? _previewCancellation;
    private bool _owned;
    private bool _preview;
    private bool _disposed;
    private CancellationTokenSource _exitCancellation = new();
    private bool _preparingExit;
    private Task<ExitReadiness>? _exitTask;
    private SaveReview? _saveReview;
    private string? _pendingVisualizationPath;
    private MeetingVisualizationDocument? _pendingVisualizationDocument;

    private enum SaveDecision { Save, Discard }

    private sealed class SaveReview(string sessionId, DateTimeOffset deadline)
    {
        public string SessionId { get; } = sessionId;
        public DateTimeOffset Deadline { get; } = deadline;
        public TaskCompletionSource<SaveDecision> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SessionRun(MeetingSession session, CancellationToken ct, bool confirmedDetection,
        DateTimeOffset startedRecordingAt)
    {
        public MeetingSession Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public List<TranscriptionSegment> Segments { get; } = [];
        public List<(IAudioCapture Device, EventHandler<AudioDataEventArgs> Handler)> Devices { get; } = [];
        public EventHandler<TranscriptionSegment>? FinalHandler;
        public EventHandler<PartialTranscript>? PartialHandler;
        public bool AcceptCallbacks = true;
        public bool AcceptAudio = true;
        public bool TranscriptionAttempted;
        public bool Closed;
        public bool Complete;
        public string? CaptureError;
        public string? TranscriptionFlushWarning;
        public bool EndPromptShown;
        public bool ConfirmedDetection { get; } = confirmedDetection;
        public DateTimeOffset LastAudibleAudioAt { get; set; } = startedRecordingAt;
        public DateTimeOffset LastTranscriptProgressAt { get; set; } = startedRecordingAt;
        public int LastPartialTranscriptHash { get; set; }
        public bool HasPartialTranscript { get; set; }
    }

    public MeetingPipeline(IMeetingDetector detector, IEnumerable<IAudioCapture> captureDevices,
        ITranscriptionService transcription, INotesIntelligence intelligence, INotesStorage storage,
        IMeetingVisualizationIntelligence visualizationIntelligence,
        MeetingVisualizationSession visualization, IMeetingVisualizationStorage visualizationStorage,
        SpeakerResolver speakerResolver, Func<CancellationToken, Task<string?>> getLocalUserDisplayName,
        ILogger<MeetingPipeline> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? getUtcNow = null,
        AiUsageMonitor? usage = null,
        AiDiagnosticsStream? diagnostics = null,
        IPendingVisualizationJournal? pendingVisualizationJournal = null)
    {
        _detector = detector;
        _devices = captureDevices.ToArray();
        _transcription = transcription;
        _intelligence = intelligence;
        _visualizationIntelligence = visualizationIntelligence;
        _storage = storage;
        _visualization = visualization;
        _visualizationStorage = visualizationStorage;
        _pendingVisualizationJournal = pendingVisualizationJournal;
        _speakerResolver = speakerResolver;
        _getLocalUserDisplayName = getLocalUserDisplayName;
        _logger = logger;
        _usage = usage ?? new AiUsageMonitor();
        _diagnostics = diagnostics ?? new AiDiagnosticsStream();
        _delay = delay ?? Task.Delay;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _detector.MeetingStarted += OnMeetingStarted;
        _detector.MeetingEnded += OnMeetingEnded;
        _speakerResolver.SpeakerResolved += OnSpeakerResolved;
    }

    public event EventHandler<PipelineStatus>? StateChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<NotesSavedInfo>? NotesSaved;
    public event EventHandler<MeetingSession>? MeetingDetected;
    public event EventHandler<MeetingSession>? PendingMeetingCancelled;
    public event EventHandler<MeetingSession>? MeetingEndedWhileRecording;
    public event EventHandler<SessionTranscriptSegment>? TranscriptSegmentReceived;
    public event EventHandler<SessionPartialTranscript>? PartialTranscriptReceived;

    public PipelineStatus Status { get { lock (_gate) return _status; } }
    public PipelineState State => Status.State;
    public string? CurrentSessionId { get { lock (_gate) return _preview ? null : _run?.Session.Id; } }
    public MeetingSession? CurrentSession { get { lock (_gate) return _preview ? null : _run?.Session; } }
    public bool CanStart { get { lock (_gate) return !_disposed && !_preparingExit && !_owned &&
        _pendingVisualizationPath is null && IsResting(_status.State); } }
    public bool CanStop { get { lock (_gate) return _run is { Closed: false } && _status.State is PipelineState.Starting or PipelineState.Recording; } }
    public bool CanReviewSave { get { lock (_gate) return _saveReview is not null; } }
    public bool IsRunning => State == PipelineState.Recording && !_preview;
    public SpeakerResolver SpeakerResolver => _speakerResolver;
    public AiUsageMonitor AiUsage => _usage;
    public AiDiagnosticsStream AiDiagnostics => _diagnostics;
    public TranscriptSnapshot? CurrentTranscript { get { lock (_gate) return _preview || _run is null ? null : Snapshot(_run); } }
    public TranscriptSnapshot? LastCompletedTranscript { get { lock (_gate) return _lastCompleted; } }
    private static bool IsResting(PipelineState state) =>
        state is PipelineState.Idle or PipelineState.Saved or PipelineState.Discarded or PipelineState.Failed;

    public PipelineDiagnostics GetDiagnostics()
    {
        var sessionId = CurrentSessionId;
        return new(sessionId, DateTimeOffset.UtcNow,
            Array.AsReadOnly(_devices.SelectMany(d => d.Health).ToArray()), _transcription.Health);
    }

    /// <summary>
    /// Classifies why a recording could not start so the UI can give specific, actionable guidance.
    /// The result is sanitized: it never contains provider payloads, endpoints, keys, or meeting content.
    /// </summary>
    public RecordingStartFailure DescribeStartFailure(Exception? error)
    {
        lock (_gate) return DescribeStartFailureCore(error, _owned && ResourcesStillOwned());
    }

    private RecordingStartFailure DescribeStartFailureCore(Exception? error, bool resourcesRetained) =>
        RecordingStartFailures.Classify(error,
            _devices.SelectMany(device => device.Health).ToArray(), _transcription.Health, resourcesRetained);

    public Task StartDetectorAsync(CancellationToken ct) => _detector.StartAsync(ct);

    public async Task StopDetectorAsync(CancellationToken ct)
    {
        await _detector.StopAsync(ct).ConfigureAwait(false);
        await StopOwnedOperationsAsync().ConfigureAwait(false);
    }

    public bool DismissPendingMeeting(string sessionId, TimeSpan cooldown)
    {
        var dismissed = _detector.Dismiss(sessionId, cooldown);
        lock (_gate)
            if (dismissed && _pendingSession?.Id == sessionId) _pendingSession = null;
        return dismissed;
    }

    public void SnoozeDetection(TimeSpan duration)
    {
        _detector.Snooze(duration);
        MeetingSession? pending;
        lock (_gate) { pending = _pendingSession; _pendingSession = null; }
        if (pending is not null) Publish(PendingMeetingCancelled, pending);
    }

    public void ResumeDetection() => _detector.Resume();

    public Task StartManualAsync(string title, CancellationToken ct) =>
        StartAsync(new MeetingSession { Title = title, SourceProcess = "manual" }, ct);

    public Task StartManualAsync(string title, MeetingTitleCandidate meeting, CancellationToken ct)
    {
        var session = new MeetingSession { Title = title, SourceProcess = "manual" };
        if (!session.TryAttachCalendarMeeting(session.Id, meeting))
            throw new RecordingStartException(RecordingStartFailureKind.IneligibleCalendarMeeting);
        return StartAsync(session, ct);
    }

    public Task ConfirmAndStartAsync(MeetingSession session, CancellationToken ct) =>
        StartAsync(session, ct, confirmDetection: true);

    private Task StartAsync(MeetingSession session, CancellationToken ct, bool confirmDetection = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparingExit) throw new RecordingStartException(RecordingStartFailureKind.ExitInProgress);
            if (_pendingVisualizationPath is not null)
                throw new RecordingStartException(RecordingStartFailureKind.PendingVisualizationRetry);
            ct.ThrowIfCancellationRequested();
            if (_owned && !(_preview && _previewTask is { IsCompletedSuccessfully: true } && _previewStopTask is null))
                throw new RecordingStartException(RecordingStartFailureKind.AlreadyInProgress);
            if (!_preview && !IsResting(_status.State))
                throw new RecordingStartException(RecordingStartFailureKind.PipelineNotReady);
            if (confirmDetection && !_detector.Confirm(session.Id))
                throw new RecordingStartException(RecordingStartFailureKind.DetectionExpired);
            session.FreezeTitle();
            var stopPreview = _preview;
            _owned = true;
            _preview = false;
            _pendingSession = null;
            _stopTask = null;
            _retryTask = null;
            _visualizationAnalysisTask = null;
            _visualizationAnalysisSessionId = null;
            _visualizationAnalysisTranscriptRevision = -1;
            _visualizationAnalysisTopicCount = -1;
            _visualizationAnalysisRefreshPending = false;
            _usage.Reset(session.Id);
            _diagnostics.Reset(session.Id);
            _visualization.Begin(session.Id, session.Title, session.StartedAt);
            var run = new SessionRun(session, ct, confirmDetection, _getUtcNow());
            _run = run;
            _startTask = Task.Run(() => StartCoreAsync(run, stopPreview));
            SetStatus(PipelineState.Starting, "Starting capture...");
            return _startTask;
        }
    }

    private SaveReview BeginSaveReview(string sessionId)
    {
        lock (_gate)
        {
            var review = new SaveReview(sessionId, _getUtcNow().Add(SaveReviewDuration));
            _saveReview = review;
            SetStatus(PipelineState.ReviewingSave,
                "Recording stopped. Save now or discard before automatic save.", saveDeadline: review.Deadline);
            if (_preparingExit) review.Decision.TrySetResult(SaveDecision.Save);
            return review;
        }
    }

    private async Task<SaveDecision> WaitForSaveDecisionAsync(SaveReview review)
    {
        using var timeoutCancellation = new CancellationTokenSource();
        var timeout = _delay(SaveReviewDuration, timeoutCancellation.Token);
        var winner = await Task.WhenAny(review.Decision.Task, timeout).ConfigureAwait(false);
        if (winner == timeout)
        {
            await timeout.ConfigureAwait(false);
            TryDecideSave(review.SessionId, SaveDecision.Save);
        }
        else
        {
            await timeoutCancellation.CancelAsync().ConfigureAwait(false);
            try { await timeout.ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested) { }
        }
        return await review.Decision.Task.ConfigureAwait(false);
    }

    public bool SaveNow(string sessionId) => TryDecideSave(sessionId, SaveDecision.Save);

    public bool DiscardPendingRecording(string sessionId) =>
        TryDecideSave(sessionId, SaveDecision.Discard);

    private bool TryDecideSave(string sessionId, SaveDecision decision)
    {
        lock (_gate)
        {
            if (_saveReview is not { } review || review.SessionId != sessionId)
                return false;
            if (decision == SaveDecision.Discard && _getUtcNow() >= review.Deadline)
            {
                review.Decision.TrySetResult(SaveDecision.Save);
                return false;
            }
            return review.Decision.TrySetResult(decision);
        }
    }

    private async Task StartCoreAsync(SessionRun run, bool stopPreview)
    {
        try
        {
            CancellationToken token;
            lock (_gate) token = run.Cancellation.Token;
            if (stopPreview) await StopDevicesAsync().ConfigureAwait(false);
            if (stopPreview)
            {
                _previewCancellation?.Dispose();
                _previewCancellation = null;
            }
            token.ThrowIfCancellationRequested();
            if (_devices.Length == 0)
                throw new RecordingStartException(RecordingStartFailureKind.NoAudioSource);
            if (_devices.Any(d => d.SampleRate != 16000 || d.Channels != 1))
                throw new RecordingStartException(RecordingStartFailureKind.UnsupportedAudioFormat);
            _speakerResolver.Reset();
            _ = Task.Run(() => ResolveLocalUserAsync(run));
            var transcription = _transcription;
            run.FinalHandler = (sender, segment) =>
            {
                if (ReferenceEquals(sender, transcription)) AcceptSegment(run, segment);
            };
            run.PartialHandler = (sender, partial) =>
            {
                lock (_gate)
                    if (ReferenceEquals(sender, transcription) && ReferenceEquals(_run, run) && run.AcceptCallbacks)
                    {
                        NotePartialTranscriptProgress(run, partial);
                        Publish(PartialTranscriptReceived, new SessionPartialTranscript(run.Session.Id, partial));
                    }
            };
            transcription.SegmentRecognized += run.FinalHandler;
            transcription.PartialResultReceived += run.PartialHandler;
            run.TranscriptionAttempted = true;
            await transcription.StartAsync(run.Cancellation.Token).ConfigureAwait(false);
            foreach (var device in _devices)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                EventHandler<AudioDataEventArgs> handler = (sender, args) =>
                {
                    var offerStop = false;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(sender, device) || !ReferenceEquals(_run, run) || !run.AcceptCallbacks || !run.AcceptAudio)
                            return;
                        offerStop = NoteAudioActivity(run, args);
                    }
                    if (offerStop)
                        Publish(MeetingEndedWhileRecording, run.Session);
                    try
                    {
                        // The existing audio worker owns this synchronous SDK write; do not orphan a push task.
                        transcription.PushAudioAsync(args.Buffer, args.BytesRecorded, CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_run, run) || !run.AcceptAudio) return;
                            run.AcceptAudio = false;
                            _logger.LogError("Audio push failed ({ErrorType}).", ex.GetType().Name);
                            run.CaptureError = transcription.Health.Error ?? "Audio could not be delivered to transcription.";
                            if (State == PipelineState.Recording)
                                SetStatus(PipelineState.Recording, "Transcription stopped; stop recording to save.",
                                    error: run.CaptureError);
                        }
                    }
                };
                run.Devices.Add((device, handler));
                device.DataAvailable += handler;
                await device.StartAsync(run.Cancellation.Token).ConfigureAwait(false);
            }
            lock (_gate)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                SetStatus(PipelineState.Recording,
                    run.CaptureError is null ? "Recording" : "Transcription stopped; stop recording to save.",
                    error: run.CaptureError);
            }
        }
        catch (Exception ex)
        {
            await CloseCaptureAsync(run, startupFailed: true).ConfigureAwait(false);
            _detector.Complete(run.Session.Id);
            RecordingStartFailure failure;
            lock (_gate)
            {
                _owned = ResourcesStillOwned();
                failure = DescribeStartFailureCore(ex, _owned);
                SetStatus(PipelineState.Failed,
                    ex is OperationCanceledException ? "Capture startup canceled." : "Capture could not start.",
                    // Startup guidance is the most specific signal available; capture health is folded into it.
                    error: failure.Message);
            }
            _logger.LogWarning("Capture startup ended ({ErrorType}) classified as {StartFailureKind}.",
                ex.GetType().Name, failure.Kind);
            throw;
        }
    }

    private async Task ResolveLocalUserAsync(SessionRun run)
    {
        try
        {
            CancellationToken token;
            lock (_gate)
            {
                if (!ReferenceEquals(_run, run) || run.Closed) return;
                token = run.Cancellation.Token;
            }
            var name = await _getLocalUserDisplayName(token).ConfigureAwait(false);
            lock (_gate)
                if (ReferenceEquals(_run, run) && !run.Closed && !run.Cancellation.IsCancellationRequested && name is not null)
                    _speakerResolver.SetLocalUser(name);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Optional local speaker lookup failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    public Task StopManualAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            if (_run is null || _run.Closed || !_owned || _preview) return Task.CompletedTask;
            var run = _run;
            var startup = _startTask;
            _stopTask = Task.Run(() => StopCoreAsync(run, startup));
            SetStatus(PipelineState.Stopping, "Stopping capture...");
            run.Cancellation.Cancel();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(SessionRun run, Task startup)
    {
        lock (_gate) if (run.Closed) return;
        try { await startup.ConfigureAwait(false); }
        catch (Exception) when (run.Closed) { return; } // Startup already published its rollback outcome.
        string? path = null;
        try
        {
            await CloseCaptureAsync(run, startupFailed: false).ConfigureAwait(false);
            _detector.Complete(run.Session.Id);
            var snapshot = LastCompletedTranscript!;
            var review = BeginSaveReview(snapshot.SessionId);
            SaveDecision decision;
            try
            {
                decision = await WaitForSaveDecisionAsync(review).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate) if (ReferenceEquals(_saveReview, review)) _saveReview = null;
                throw;
            }
            if (decision == SaveDecision.Discard)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_saveReview, review)) _saveReview = null;
                    run.Segments.Clear();
                    _speakerResolver.Reset();
                    _lastCompleted = null;
                    _lastSavedNotes = null;
                    _lastSavedPath = null;
                    _pendingVisualizationPath = null;
                    _pendingVisualizationDocument = null;
                    _visualization.Clear();
                }
                Finish(PipelineState.Discarded, "Recording discarded.");
                return;
            }
            lock (_gate) if (ReferenceEquals(_saveReview, review)) _saveReview = null;
            SetStatus(PipelineState.Finalizing, "Saving transcript...");
            path = await PersistNotesAsync(snapshot, CreateNotes(snapshot), CancellationToken.None).ConfigureAwait(false);
            Publish(NotesSaved, new NotesSavedInfo(snapshot.SessionId, path));
            if (run.CaptureError is not null && ResourcesStillOwned())
            {
                Finish(PipelineState.Failed,
                    WithVisualizationStatus("Available transcript saved; capture did not complete cleanly.", path),
                    path, run.CaptureError);
                return;
            }
            if (run.TranscriptionFlushWarning is not null)
            {
                Finish(PipelineState.Saved,
                    WithVisualizationStatus("Available transcript saved; final speech flush did not complete.", path),
                    path, run.TranscriptionFlushWarning);
                return;
            }
            try
            {
                await GenerateAndSaveAsync(snapshot, path, _exitCancellation.Token, allowPartialSave: true,
                    completionWarning: run.CaptureError)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_exitCancellation.IsCancellationRequested)
            {
                Finish(PipelineState.Saved, "Transcript saved.", path, run.CaptureError);
            }
        }
        catch (Exception ex)
        {
            if (ex is MeetingVisualizationPersistenceException partial)
                path = partial.MarkdownPath;
            else if (ex is MeetingVisualizationPersistenceCanceledException canceled)
                path = canceled.MarkdownPath;
            var message = "Meeting finalization failed; transcript remains available.";
            if (path is not null) message = WithVisualizationStatus(message, path);
            Finish(PipelineState.Failed, message, path,
                ex is MeetingVisualizationPersistenceException or MeetingVisualizationPersistenceCanceledException
                    ? "Transcript saved, but visualization data could not be saved. Exit or retry will try again."
                    : "Could not save the completed meeting. Check the notes location and retry.");
            _logger.LogError("Meeting finalization failed ({ErrorType}).", ex.GetType().Name);
            throw;
        }
    }

    private async Task CloseCaptureAsync(SessionRun run, bool startupFailed)
    {
        try
        {
            await Task.WhenAll(run.Devices.Select(async entry =>
                await entry.Device.StopAsync(CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            run.CaptureError = "Audio capture did not stop cleanly.";
            _logger.LogError("Capture shutdown failed ({ErrorType}).", ex.GetType().Name);
        }
        finally
        {
            foreach (var (device, handler) in run.Devices) device.DataAvailable -= handler;
        }
        run.CaptureError ??= DescribeCaptureCompletionWarning(
            _devices.SelectMany(device => device.Health));
        if (!startupFailed) SetStatus(PipelineState.Finalizing, "Flushing final transcript...");
        var flushed = false;
        if (run.TranscriptionAttempted)
        {
            try
            {
                var result = await _transcription.StopAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    // The service result is authoritative, including segments emitted during its flush.
                    if (result.Segments.Count >= run.Segments.Count)
                    {
                        run.Segments.Clear();
                        run.Segments.AddRange(result.Segments.Select(Enrich));
                        for (var index = 0; index < run.Segments.Count; index++)
                            _visualization.AppendOrUpdateSegment(index + 1, run.Segments[index]);
                    }
                }
                flushed = true;
            }
            catch (Exception ex)
            {
                run.TranscriptionFlushWarning = "Transcription flush failed; available text was retained.";
                _logger.LogError("Transcription shutdown failed ({ErrorType}).", ex.GetType().Name);
            }
        }
        lock (_gate)
        {
            _transcription.SegmentRecognized -= run.FinalHandler;
            _transcription.PartialResultReceived -= run.PartialHandler;
            run.AcceptCallbacks = false;
            run.Session.End();
            run.Closed = true;
            run.Complete = flushed && !startupFailed && run.CaptureError is null;
            _lastCompleted = Snapshot(run);
            _visualization.Complete(run.Session.EndedAt ?? DateTimeOffset.UtcNow, run.Complete);
            _lastSavedNotes = null;
            _lastSavedPath = null;
            run.Cancellation.Cancel();
            run.Cancellation.Dispose();
        }
    }

    internal static string? DescribeCaptureCompletionWarning(IEnumerable<AudioSourceHealth> health)
    {
        var affected = health
            .Where(source => source.State == CaptureHealthState.Failed || source.DroppedBuffers > 0)
            .ToArray();
        if (affected.Length == 0)
            return null;

        var failed = affected
            .Where(source => source.State == CaptureHealthState.Failed)
            .Select(source => source.Source switch
            {
                "microphone" => "microphone audio was unavailable",
                "loopback" => "speaker audio was unavailable",
                "mixer" => "audio processing failed",
                _ => "an audio source was unavailable",
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var dropped = affected.Sum(source => source.DroppedBuffers);
        var problems = failed.ToList();
        if (dropped > 0)
            problems.Add($"{dropped:N0} audio buffer{(dropped == 1 ? " was" : "s were")} dropped");

        var action = failed.Length > 0
            ? "Check Audio settings and device access before recording again."
            : "If this repeats, close other high-load apps before recording again.";
        return $"Audio capture was incomplete: {string.Join("; ", problems)}. " +
            $"The saved transcript contains only audio that was successfully received. {action}";
    }

    public Task<string> RetryIntelligenceAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparingExit) throw new InvalidOperationException("Exit is already in progress.");
            ct.ThrowIfCancellationRequested();
            if (_owned || !IsResting(_status.State))
                throw new InvalidOperationException("Retry is available only after capture and finalization finish.");
            if (_lastCompleted is not { } snapshot || snapshot.SessionId != sessionId)
                throw new InvalidOperationException("The requested completed transcript is not available.");
            _owned = true;
            _retryTask = Task.Run(async () =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _exitCancellation.Token);
                var token = lifetime.Token;
                string? path;
                lock (_gate) path = _lastSavedPath;
                try
                {
                    lock (_gate) token.ThrowIfCancellationRequested();
                    path = await PersistNotesAsync(snapshot, PreservedNotes(snapshot), token).ConfigureAwait(false);
                    Publish(NotesSaved, new NotesSavedInfo(sessionId, path));
                    return await GenerateAndSaveAsync(snapshot, path, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Finish(ex is OperationCanceledException && path is not null ? PipelineState.Saved : PipelineState.Failed,
                        path is null ? "Transcript save failed." : "Transcript saved; intelligence retry did not complete.",
                        path, ex is OperationCanceledException ? "Retry canceled." : "Retry failed; the transcript remains available.");
                    throw;
                }
            });
            SetStatus(PipelineState.Finalizing, "Saving transcript before retry...", sessionId: sessionId);
            return _retryTask;
        }
    }

    public Task RefreshVisualizationAnalysisAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task analysis;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparingExit || _run is not { Closed: false } run)
                throw new InvalidOperationException("Visualization analysis requires an active recording.");
            var document = _visualization.Snapshot();
            if (_visualizationAnalysisTask is { IsCompletedSuccessfully: true } &&
                _visualizationAnalysisSessionId == document.SessionId &&
                _visualizationAnalysisTranscriptRevision == document.TranscriptRevision &&
                _visualizationAnalysisTopicCount == document.TopicTrail.Count)
                return Task.CompletedTask;
            if (_visualizationAnalysisTask is { IsCompleted: false })
            {
                if (_visualizationAnalysisSessionId == document.SessionId &&
                    (_visualizationAnalysisTranscriptRevision < document.TranscriptRevision ||
                     _visualizationAnalysisTopicCount < document.TopicTrail.Count))
                    _visualizationAnalysisRefreshPending = true;
                return _visualizationAnalysisTask.WaitAsync(ct);
            }
            _visualizationAnalysisSessionId = document.SessionId;
            _visualizationAnalysisTranscriptRevision = document.TranscriptRevision;
            _visualizationAnalysisTopicCount = document.TopicTrail.Count;
            _visualizationAnalysisRefreshPending = false;
            analysis = _visualizationAnalysisTask = Task.Run(async () =>
            {
                var analyzed = document;
                while (true)
                {
                    var result = await _visualizationIntelligence.AnalyzeVisualizationAsync(
                        analyzed, run.Cancellation.Token).ConfigureAwait(false);
                    lock (_gate)
                        if (_run != run || run.Closed)
                            return;
                    _visualization.ApplyAnalysisResult(analyzed, result);
                    lock (_gate)
                    {
                        if (_run != run || run.Closed || !_visualizationAnalysisRefreshPending)
                            return;
                        _visualizationAnalysisRefreshPending = false;
                        analyzed = _visualization.Snapshot();
                        _visualizationAnalysisSessionId = analyzed.SessionId;
                        _visualizationAnalysisTranscriptRevision = analyzed.TranscriptRevision;
                        _visualizationAnalysisTopicCount = analyzed.TopicTrail.Count;
                    }
                }
            });
        }
        return analysis.WaitAsync(ct);
    }

    private async Task<string> GenerateAndSaveAsync(
        TranscriptSnapshot snapshot, string path, CancellationToken ct, bool allowPartialSave = false,
        string? completionWarning = null)
    {
        if (snapshot.WordCount == 0)
        {
            Finish(PipelineState.Saved,
                WithVisualizationStatus("Transcript saved; no speech was recognized.", path), path,
                completionWarning);
            return path;
        }
        ct.ThrowIfCancellationRequested();
        SetStatus(PipelineState.Generating, "Transcript saved. Generating meeting notes...", path, sessionId: snapshot.SessionId);
        MeetingNotes notes;
        IntelligenceFailure? failure = null;
        try
        {
            var document = _visualization.Snapshot();
            var result = await _visualizationIntelligence.AnalyzeVisualizationAsync(document, ct)
                .WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
                if (_lastCompleted?.SessionId != snapshot.SessionId ||
                    _lastCompleted.Revision != snapshot.Revision)
                    throw new OperationCanceledException("Visualization analysis became stale.", ct);
            _visualization.ApplyAnalysisResult(document, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failure = FailureFrom(ex, "Visualization analysis failed. Check settings and retry.");
            _logger.LogWarning("Visualization analysis failed ({ErrorType}); preserving grounded transcript data.",
                ex.GetType().Name);
        }
        try
        {
            var generated = await _intelligence.SynthesizeAsync(snapshot.Title, snapshot.ToTranscriptionResult(), ct)
                .WaitAsync(ct).ConfigureAwait(false);
            notes = CreateNotes(snapshot, generated, failure);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failure ??= FailureFrom(ex, "Note generation failed. Check settings and retry.");
            _logger.LogWarning("Note generation failed ({ErrorType}); preserving transcript.", ex.GetType().Name);
            notes = CreateNotes(snapshot, PreservedNotes(snapshot), failure);
        }
        string updatedPath;
        try
        {
            updatedPath = await PersistNotesAsync(snapshot, notes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!allowPartialSave)
            {
                _diagnostics.RecordPersistence(
                    AiDiagnosticStage.GeneratedMarkdownRewrite,
                    ex,
                    AiDiagnosticRetryOutcome.RetryFailed);
                throw;
            }
            _logger.LogWarning(
                "Generated notes could not be saved ({ErrorType}); preserving the durable transcript for retry.",
                ex.GetType().Name);
            _diagnostics.RecordPersistence(
                AiDiagnosticStage.GeneratedMarkdownRewrite,
                ex,
                AiDiagnosticRetryOutcome.RetryAvailable);
            Finish(PipelineState.Saved,
                WithVisualizationStatus("Transcript saved, but generated notes could not be saved. Retry AI.", path),
                path, completionWarning ?? "Generated notes could not be saved. Retry AI.", failure);
            return path;
        }
        if (!string.Equals(path, updatedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Notes storage changed the completed session's stable path.");
        if (!allowPartialSave)
            _diagnostics.RecordPersistence(
                AiDiagnosticStage.GeneratedMarkdownRewrite,
                null,
                AiDiagnosticRetryOutcome.RetrySucceeded);
        Publish(NotesSaved, new NotesSavedInfo(snapshot.SessionId, path));
        var message = failure is null ? "Meeting notes saved." :
            "Transcript and any previous notes preserved; intelligence failed. Retry is available.";
        Finish(PipelineState.Saved, WithVisualizationStatus(message, path),
            path, completionWarning, failure);
        return path;
    }

    private string WithVisualizationStatus(string message, string path)
    {
        lock (_gate)
            return _pendingVisualizationPath == path
                ? message + " Visualization data is pending retry when the app exits."
                : message;
    }

    private MeetingNotes PreservedNotes(TranscriptSnapshot snapshot)
    {
        lock (_gate)
            return CreateNotes(snapshot, _lastSavedNotes, _lastSavedNotes?.IntelligenceFailure);
    }

    private async Task<string> PersistNotesAsync(
        TranscriptSnapshot snapshot, MeetingNotes notes, CancellationToken ct)
    {
        var path = await _storage.SaveAsync(notes, ct).ConfigureAwait(false);
        var document = _visualization.Snapshot();
        var retryingPendingVisualization = false;
        lock (_gate)
        {
            if (_lastSavedPath is not null && !string.Equals(_lastSavedPath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Notes storage changed the completed session's stable path.");
            retryingPendingVisualization = _pendingVisualizationPath is not null &&
                string.Equals(_pendingVisualizationPath, path, StringComparison.OrdinalIgnoreCase);
            _lastSavedNotes = CreateNotes(snapshot, notes, notes.IntelligenceFailure);
            _lastSavedPath = path;
            _pendingVisualizationPath = path;
            _pendingVisualizationDocument = document;
        }
        try
        {
            await _visualizationStorage.SaveAsync(path, document, ct).ConfigureAwait(false);
            lock (_gate)
                if (_pendingVisualizationPath == path)
                {
                    _pendingVisualizationPath = null;
                    _pendingVisualizationDocument = null;
                }
            if (retryingPendingVisualization)
                _diagnostics.RecordPersistence(
                    AiDiagnosticStage.CompanionJsonRetry,
                    null,
                    AiDiagnosticRetryOutcome.RetrySucceeded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            System.Text.Json.JsonException or System.Security.SecurityException or OperationCanceledException)
        {
            _logger.LogWarning("Visualization data could not be saved ({ErrorType}); it remains pending for exit retry.",
                ex.GetBaseException().GetType().Name);
            _diagnostics.RecordPersistence(
                AiDiagnosticStage.CompanionJsonWrite,
                ex,
                AiDiagnosticRetryOutcome.PendingExitRetry);
        }
        return path;
    }

    private static IntelligenceFailure FailureFrom(Exception ex, string message) =>
        ex is IntelligenceException intelligenceError ? intelligenceError.Failure :
            new(IntelligenceFailureKind.Unknown, "intelligence", null, message);

    private static MeetingNotes CreateNotes(TranscriptSnapshot snapshot, MeetingNotes? generated = null, IntelligenceFailure? failure = null)
    {
        var notes = new MeetingNotes
        {
            SessionId = snapshot.SessionId, MeetingTitle = snapshot.Title, CreatedAt = snapshot.StartedAt,
            RawTranscript = snapshot.FullText, IntelligenceFailure = failure,
            Summary = generated?.Summary ?? string.Empty, Nuances = generated?.Nuances ?? string.Empty,
            MeetingType = generated?.MeetingType ?? "unknown", Sentiment = generated?.Sentiment ?? "neutral",
            EstimatedDurationMinutes = generated?.EstimatedDurationMinutes ?? 0,
            OpenQuestions = generated?.OpenQuestions.ToList() ?? [],
            FollowUpMeetings = generated?.FollowUpMeetings.ToList() ?? [],
            NotableQuotes = generated?.NotableQuotes.ToList() ?? [],
            Participants = generated?.Participants.ToList() ?? []
        };
        if (generated is not null)
        {
            notes.Decisions.AddRange(generated.Decisions);
            notes.ActionItems.AddRange(generated.ActionItems);
            notes.Topics.AddRange(generated.Topics);
            notes.KeyDiscussionPoints.AddRange(generated.KeyDiscussionPoints);
        }
        return notes;
    }

    public Task StartAudioPreviewAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparingExit) throw new InvalidOperationException("Exit is already in progress.");
            ct.ThrowIfCancellationRequested();
            if (_owned || !IsResting(_status.State))
                throw new InvalidOperationException("Audio preview requires an idle pipeline.");
            _owned = true;
            _preview = true;
            _previewStopTask = null;
            _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var previewToken = _previewCancellation.Token;
            _previewTask = Task.Run(async () =>
            {
                try
                {
                    lock (_gate) previewToken.ThrowIfCancellationRequested();
                    if (_devices.Length == 0) throw new InvalidOperationException("No audio source is enabled.");
                    foreach (var device in _devices)
                    {
                        previewToken.ThrowIfCancellationRequested();
                        await device.StartAsync(previewToken).ConfigureAwait(false);
                    }
                    previewToken.ThrowIfCancellationRequested();
                    SetStatus(PipelineState.Idle, "Audio preview active (not recording).");
                }
                catch
                {
                    try { await StopDevicesAsync().ConfigureAwait(false); }
                    finally
                    {
                        lock (_gate) _preview = false;
                        _previewCancellation?.Dispose();
                        _previewCancellation = null;
                        Finish(PipelineState.Failed, "Audio preview failed.", error: "Check the configured audio devices.");
                    }
                    throw;
                }
            });
            SetStatus(PipelineState.Idle, "Starting audio preview...");
            return _previewTask;
        }
    }

    public Task StopAudioPreviewAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_previewStopTask is not null) return _previewStopTask;
            if (!_preview) return Task.CompletedTask;
            _previewCancellation?.Cancel();
            var startup = _previewTask ?? Task.CompletedTask;
            _previewStopTask = Task.Run(async () =>
            {
                try { await startup.ConfigureAwait(false); }
                catch (Exception) when (!_preview) { return; }
                try
                {
                    await StopDevicesAsync().ConfigureAwait(false);
                    lock (_gate) _preview = false;
                    _previewCancellation?.Dispose();
                    _previewCancellation = null;
                    Finish(PipelineState.Idle, "Audio preview stopped.");
                }
                catch
                {
                    lock (_gate) _preview = false;
                    _previewCancellation?.Dispose();
                    _previewCancellation = null;
                    Finish(PipelineState.Failed, "Audio preview shutdown failed.", error: "An audio device failed to stop cleanly.");
                    throw;
                }
            });
            return _previewStopTask;
        }
    }

    private Task StopDevicesAsync() => Task.WhenAll(_devices.Select(async device =>
        await device.StopAsync(CancellationToken.None).ConfigureAwait(false)));

    private void AcceptSegment(SessionRun run, TranscriptionSegment segment)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run) || !run.AcceptCallbacks) return;
            run.LastTranscriptProgressAt = _getUtcNow();
            if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
            {
                _speakerResolver.NotifySpeaker(segment.SpeakerId);
            }
            var enriched = Enrich(segment);
            run.Segments.Add(enriched);
            _visualization.AppendOrUpdateSegment(run.Segments.Count, enriched);
            Publish(TranscriptSegmentReceived, new SessionTranscriptSegment(run.Session.Id, run.Segments.Count, enriched));
        }
    }

    private void NotePartialTranscriptProgress(SessionRun run, PartialTranscript partial)
    {
        if (string.IsNullOrWhiteSpace(partial.Text)) return;
        var hash = StringComparer.Ordinal.GetHashCode(partial.Text);
        if (!run.HasPartialTranscript || run.LastPartialTranscriptHash != hash)
        {
            run.HasPartialTranscript = true;
            run.LastPartialTranscriptHash = hash;
            run.LastTranscriptProgressAt = _getUtcNow();
        }
    }

    private bool NoteAudioActivity(SessionRun run, AudioDataEventArgs audio)
    {
        var now = _getUtcNow();
        if (HasAudibleAudio(audio))
        {
            run.LastAudibleAudioAt = now;
            return false;
        }

        if (!run.ConfirmedDetection || run.EndPromptShown ||
            now - run.LastAudibleAudioAt < MeetingEndInactivityWindow ||
            now - run.LastTranscriptProgressAt < MeetingEndInactivityWindow)
            return false;

        run.EndPromptShown = true;
        return true;
    }

    private static bool HasAudibleAudio(AudioDataEventArgs audio)
    {
        if (audio.BytesRecorded < 2) return false;
        long squares = 0;
        var samples = audio.BytesRecorded / 2;
        for (var index = 0; index < samples; index++)
        {
            var offset = index * 2;
            var sample = (short)(audio.Buffer[offset] | (audio.Buffer[offset + 1] << 8));
            squares += (long)sample * sample;
        }
        return squares >= AudibleRmsSquared * samples;
    }

    private void OnSpeakerResolved(object? sender, SpeakerResolvedEventArgs args)
    {
        try
        {
            if (_visualization.HasActiveSession)
                _visualization.ApplySpeakerAssignment(args.SpeakerId, args.DisplayName);
        }
        catch (InvalidOperationException) { }
    }

    private TranscriptionSegment Enrich(TranscriptionSegment segment) =>
        segment with { SpeakerDisplayName = _speakerResolver.GetDisplayName(segment.SpeakerId) };

    private TranscriptSnapshot Snapshot(SessionRun run) => new(run.Session.Id, run.Session.Title,
        run.Session.StartedAt, run.Session.EndedAt, run.Segments.Count, run.Complete,
        run.Segments.Select(Enrich).ToArray());

    private void OnMeetingStarted(object? sender, MeetingSession session)
    {
        lock (_gate)
        {
            if (_owned || _disposed || _preparingExit) return;
            _pendingSession = session;
        }
        Publish(MeetingDetected, session);
    }

    private static readonly TimeSpan MeetingEndPromptGrace = TimeSpan.FromSeconds(8);

    private void OnMeetingEnded(object? sender, MeetingSession session)
    {
        SessionRun? matched = null;
        lock (_gate)
        {
            if (_pendingSession?.Id == session.Id)
            {
                _pendingSession = null;
                Publish(PendingMeetingCancelled, session);
            }
            if (_run is { Closed: false } run && run.Session.Id == session.Id &&
                run.Session.SourceProcess != "manual" && !run.EndPromptShown)
                matched = run;
        }
        if (matched is not null)
            _ = OfferStopAfterGraceAsync(matched);
    }

    /// <summary>
    /// The detector already confirmed this call ended; wait out a short grace window (reusing the
    /// same detector lifetime signal, not a new watcher) so a call that resumes withdraws the offer,
    /// then let the user decide instead of auto-stopping the recording.
    /// </summary>
    private async Task OfferStopAfterGraceAsync(SessionRun run)
    {
        var sourceProcess = run.Session.SourceProcess;
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResume(object? _, MeetingSession resumedSession)
        {
            if (string.Equals(resumedSession.SourceProcess, sourceProcess, StringComparison.Ordinal))
                resumed.TrySetResult();
        }
        _detector.MeetingStarted += OnResume;
        try
        {
            var graceTask = _delay(MeetingEndPromptGrace, CancellationToken.None);
            var completed = await Task.WhenAny(graceTask, resumed.Task).ConfigureAwait(false);
            if (completed == resumed.Task) return; // The call resumed before the grace elapsed; withdraw.
            lock (_gate)
            {
                if (!ReferenceEquals(_run, run) || run.Closed || run.EndPromptShown) return;
                run.EndPromptShown = true;
            }
            Publish(MeetingEndedWhileRecording, run.Session);
        }
        catch (Exception ex)
        {
            _logger.LogError("Meeting-end offer failed ({ErrorType}).", ex.GetType().Name);
        }
        finally
        {
            _detector.MeetingStarted -= OnResume;
        }
    }

    public Task<string> WhatDidIMissAsync(string recentTranscript, CancellationToken ct) =>
        _intelligence.CatchUpAsync(recentTranscript, ct);

    private void Finish(PipelineState state, string message, string? path = null, string? error = null,
        IntelligenceFailure? intelligenceFailure = null)
    {
        lock (_gate)
        {
            _owned = ResourcesStillOwned();
            if (_owned)
            {
                state = PipelineState.Failed;
                error = "Capture resources could not be released. Restart the application before recording again.";
            }
            SetStatus(state, message, path, error, intelligenceFailure);
        }
    }

    private bool ResourcesStillOwned() => _devices.Any(d => d.IsCapturing) || _transcription.HasActiveResources;

    private void SetStatus(PipelineState state, string message, string? path = null, string? error = null,
        IntelligenceFailure? intelligenceFailure = null, string? sessionId = null,
        DateTimeOffset? saveDeadline = null)
    {
        lock (_gate)
        {
            _status = new(_status.Sequence + 1, sessionId ?? (_preview ? null : _run?.Session.Id),
                state, message, path, error, intelligenceFailure, saveDeadline);
            Publish(StateChanged, _status);
            Publish(StatusChanged, message);
        }
    }

    private void Publish<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, value); }
            catch (Exception ex) { _logger.LogError("Pipeline event subscriber failed ({ErrorType}).", ex.GetType().Name); }
    }

    private Task StopOwnedOperationsAsync()
    {
        lock (_gate)
        {
            foreach (var operation in new[]
                { _startTask, _stopTask, _previewTask, _previewStopTask, _retryTask, _visualizationAnalysisTask })
                if (operation is { IsFaulted: true })
                    _logger.LogDebug("Observing previously reported operation failure during shutdown ({ErrorType}).",
                        operation.Exception!.GetBaseException().GetType().Name);
            var stop = _stopTask is { IsCompleted: false } ? _stopTask :
                _run is { Closed: false } && _owned && !_preview ? StopManualAsync() : Task.CompletedTask;
            var preview = _preview ? StopAudioPreviewAsync(CancellationToken.None) :
                _previewStopTask is { IsCompleted: false } ? _previewStopTask : Task.CompletedTask;
            var retry = _retryTask is { IsCompleted: false } ? _retryTask : Task.CompletedTask;
            return Task.WhenAll(stop, preview, retry);
        }

    }

    public Task<ExitReadiness> PrepareForExitAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_exitTask is not null) return _exitTask;
            _preparingExit = true;
            return _exitTask = Task.Run(PrepareForExitCoreAsync);
        }
    }

    private async Task<ExitReadiness> PrepareForExitCoreAsync()
    {
        try
        {
            await _exitCancellation.CancelAsync().ConfigureAwait(false);
            lock (_gate)
                if (_saveReview is { } review)
                    review.Decision.TrySetResult(SaveDecision.Save);
            try { await StopOwnedOperationsAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning("Finalization ended with {ErrorType}; checking capture ownership and transcript durability before exit.",
                    ex.GetType().Name);
            }
            TranscriptSnapshot? recovery;
            lock (_gate)
            {
                if (_owned || ResourcesStillOwned())
                    throw new InvalidOperationException("Capture resources are still owned. Exit was not completed.");
                recovery = _lastSavedPath is null ? _lastCompleted : null;
            }
            if (recovery is not null)
            {
                var path = await PersistNotesAsync(
                    recovery, PreservedNotes(recovery), CancellationToken.None).ConfigureAwait(false);
                Publish(NotesSaved, new NotesSavedInfo(recovery.SessionId, path));
                Finish(PipelineState.Saved, "Recovered transcript saved before exit.", path);
            }
            string? pendingPath;
            MeetingVisualizationDocument? pendingDocument;
            lock (_gate)
            {
                pendingPath = _pendingVisualizationPath;
                pendingDocument = _pendingVisualizationDocument;
            }
            if (pendingPath is null || pendingDocument is null) return ExitReadiness.Clean;
            try
            {
                await _visualizationStorage.SaveAsync(
                    pendingPath, pendingDocument, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _pendingVisualizationPath = null;
                    _pendingVisualizationDocument = null;
                }
                _diagnostics.RecordPersistence(
                    AiDiagnosticStage.CompanionJsonRetry,
                    null,
                    AiDiagnosticRetryOutcome.RetrySucceeded);
                return ExitReadiness.Clean;
            }
            catch (Exception ex)
            {
                // The transcript is durable and capture is released, so optional visualization data
                // must never keep the app open. Keep the retry recoverable and report it instead.
                return await DeferVisualizationRetryAsync(pendingPath, pendingDocument, ex).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                _preparingExit = false;
                _exitTask = null;
                _exitCancellation.Dispose();
                _exitCancellation = new();
            }
            throw;
        }
    }

    /// <summary>
    /// Keeps a failed optional sidecar write recoverable without holding exit. Never claims success.
    /// </summary>
    private async Task<ExitReadiness> DeferVisualizationRetryAsync(
        string pendingPath, MeetingVisualizationDocument pendingDocument, Exception failure)
    {
        var errorType = failure.GetBaseException().GetType().Name;
        if (_pendingVisualizationJournal is null)
        {
            _diagnostics.RecordPersistence(
                AiDiagnosticStage.VisualizationJournal,
                failure,
                AiDiagnosticRetryOutcome.RecoveryUnavailable);
            _logger.LogWarning(
                "Visualization data could not be saved on exit ({ErrorType}) and no retry journal is configured; " +
                "the transcript is saved and capture is released.", errorType);
            return new(ExitDeferralKind.VisualizationRetryLost, pendingPath, errorType);
        }
        try
        {
            await _pendingVisualizationJournal
                .RecordAsync(pendingPath, pendingDocument, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _pendingVisualizationPath = null;
                _pendingVisualizationDocument = null;
            }
            _diagnostics.RecordPersistence(
                AiDiagnosticStage.VisualizationJournal,
                null,
                AiDiagnosticRetryOutcome.QueuedForAppRecovery);
            _logger.LogWarning(
                "Visualization data could not be saved on exit ({ErrorType}); it was kept for retry on the next launch.",
                errorType);
            return new(ExitDeferralKind.VisualizationRetryKept, pendingPath, errorType);
        }
        catch (Exception journalFailure)
        {
            _diagnostics.RecordPersistence(
                AiDiagnosticStage.VisualizationJournal,
                journalFailure,
                AiDiagnosticRetryOutcome.RecoveryUnavailable);
            _logger.LogError(
                "Visualization data could not be saved on exit ({ErrorType}) and the retry could not be kept ({JournalErrorType}).",
                errorType, journalFailure.GetBaseException().GetType().Name);
            return new(ExitDeferralKind.VisualizationRetryLost, pendingPath, errorType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _detector.MeetingStarted -= OnMeetingStarted;
        _detector.MeetingEnded -= OnMeetingEnded;
        _speakerResolver.SpeakerResolved -= OnSpeakerResolved;
        try
        {
            await StopOwnedOperationsAsync().ConfigureAwait(false);
        }
        finally
        {
            _diagnostics.Clear();
            List<Exception> errors = [];
            try { await _transcription.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
            foreach (var device in _devices)
                try { device.Dispose(); }
                catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("Pipeline resources failed to dispose cleanly.", errors);
        }
    }
}
