using MeetingCapture.AudioCapture;
using MeetingCapture.Diagnostics;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging;

namespace MeetingCapture.Pipeline;

/// <summary>
/// Coordinates a recording from start to saved notes: meeting detection, audio capture,
/// transcription, live insights and topic map, the save review, final notes, and exit.
/// Public events report progress. Capture lifetime is owned by the pipeline.
/// </summary>
public sealed class MeetingPipeline : IAsyncDisposable
{
    private static readonly TimeSpan SaveReviewDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MeetingEndInactivityWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TranscriptionCallTimeout = TimeSpan.FromSeconds(5);
    private const long AudibleRmsSquared = 500L * 500L;
    private readonly object _gate = new();
    private readonly string? _aiFinalizationLogDirectory;
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
    private readonly TimeSpan _transcriptionTimeout;
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
    private Task? _disposeTask;
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
        // Audio callbacks can arrive concurrently, so pushes form a task chain and are also tracked
        // separately when shutdown must wait for them or defer the speech flush.
        public List<Task> PendingAudioPushes { get; } = [];
        public List<Task> PendingTranscriptionOperations { get; } = [];
        public HashSet<Task> TimedOutAudioPushes { get; } = [];
        public TaskCompletionSource TranscriptionOperationsSettled { get; set; } = CompletedSignal();
        public Task AudioPushTail = Task.CompletedTask;
        public object SegmentPublicationGate { get; } = new();
        // SDK callbacks may finish out of order; ordinals let publication wait for earlier segments.
        public int NextSegmentPublicationOrdinal = 1;
        public EventHandler<TranscriptionSegment>? FinalHandler;
        public EventHandler<PartialTranscript>? PartialHandler;
        public volatile bool AcceptCallbacks = true;
        public bool AcceptAudio = true;
        public bool TranscriptionAttempted;
        public bool AudioDeliveryFailed;
        public bool TranscriptionStopRequested;
        public bool AutomaticStopRequested;
        public bool Closed;
        public bool Complete;
        public string? CaptureError;
        public string? TranscriptionFlushWarning;
        public bool EndPromptShown;
        public bool ConfirmedDetection { get; } = confirmedDetection;

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult();
            return signal;
        }
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
        IPendingVisualizationJournal? pendingVisualizationJournal = null,
        TimeSpan? transcriptionTimeout = null,
        string? aiFinalizationLogDirectory = null)
    {
        _aiFinalizationLogDirectory = aiFinalizationLogDirectory;
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
        _transcriptionTimeout = transcriptionTimeout ?? TranscriptionCallTimeout;
        if (_transcriptionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(transcriptionTimeout));
        _delay = delay ?? Task.Delay;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _detector.MeetingStarted += OnMeetingStarted;
        _detector.MeetingEnded += OnMeetingEnded;
        _speakerResolver.SpeakerResolved += OnSpeakerResolved;
    }

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event EventHandler<PipelineStatus>? StateChanged;

    /// <summary>Raised with a short status message for display.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Raised after notes are saved.</summary>
    public event EventHandler<NotesSavedInfo>? NotesSaved;

    /// <summary>Raised when the detector finds a possible meeting that the user can confirm or dismiss.</summary>
    public event EventHandler<MeetingSession>? MeetingDetected;

    /// <summary>Raised when a possible meeting is withdrawn before it was confirmed.</summary>
    public event EventHandler<MeetingSession>? PendingMeetingCancelled;

    /// <summary>Raised when the detected call ends while a recording is still running.</summary>
    public event EventHandler<MeetingSession>? MeetingEndedWhileRecording;

    /// <summary>Raised for each final transcript segment.</summary>
    public event EventHandler<SessionTranscriptSegment>? TranscriptSegmentReceived;

    /// <summary>Raised for each in-progress (partial) transcript update.</summary>
    public event EventHandler<SessionPartialTranscript>? PartialTranscriptReceived;

    /// <summary>The latest pipeline status.</summary>
    public PipelineStatus Status { get { lock (_gate) return _status; } }

    /// <summary>The current pipeline state.</summary>
    public PipelineState State => Status.State;

    /// <summary>ID of the current recording session, or null when none (an audio preview is not a session).</summary>
    public string? CurrentSessionId { get { lock (_gate) return _preview ? null : _run?.Session.Id; } }

    /// <summary>The current recording session, or null when none.</summary>
    public MeetingSession? CurrentSession { get { lock (_gate) return _preview ? null : _run?.Session; } }

    /// <summary>True when a new recording can start: the pipeline is resting, not owned, not exiting, and has no pending topic-map save.</summary>
    public bool CanStart { get { lock (_gate) return !_disposed && !_preparingExit && !_owned &&
        _pendingVisualizationPath is null && IsResting(_status.State); } }

    /// <summary>True while a recording is starting or running and can be stopped.</summary>
    public bool CanStop { get { lock (_gate) return _run is { Closed: false } && _status.State is PipelineState.Starting or PipelineState.Recording; } }

    /// <summary>True while a save review is open, so the user can save or discard.</summary>
    public bool CanReviewSave { get { lock (_gate) return _saveReview is not null; } }

    /// <summary>
    /// True only when a failed recording left a durable partial transcript saved, with transcription
    /// work still pending, so the app can be closed after the user confirms.
    /// </summary>
    public bool CanForceCloseAfterSavedPartial
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && !_preparingExit && !_preview &&
                    _status.State == PipelineState.Failed &&
                    _saveReview is null && _retryTask is not { IsCompleted: false } &&
                    _stopTask is not { IsCompleted: false } && _startTask.IsCompleted &&
                    _pendingVisualizationPath is null && !_devices.Any(device => device.IsCapturing) &&
                    _run is { Closed: true, Complete: false, CaptureError: not null } run &&
                    _lastCompleted is { IsComplete: false, Segments.Count: > 0 } completed &&
                    completed.SessionId == run.Session.Id &&
                    _lastSavedPath is not null &&
                    string.Equals(_status.NotesPath, _lastSavedPath, StringComparison.OrdinalIgnoreCase) &&
                    run.PendingTranscriptionOperations.Any(operation => !operation.IsCompleted);
            }
        }
    }
    /// <summary>True while a recording is in progress (not an audio preview).</summary>
    public bool IsRunning => State == PipelineState.Recording && !_preview;

    /// <summary>Maps diarized speaker IDs to display names.</summary>
    public SpeakerResolver SpeakerResolver => _speakerResolver;

    /// <summary>AI usage for the current session.</summary>
    public AiUsageMonitor AiUsage => _usage;

    /// <summary>Recent AI diagnostic events for the current session.</summary>
    public AiDiagnosticsStream AiDiagnostics => _diagnostics;

    /// <summary>Snapshot of the transcript of the current recording, or null when none.</summary>
    public TranscriptSnapshot? CurrentTranscript { get { lock (_gate) return _preview || _run is null ? null : Snapshot(_run); } }

    /// <summary>Snapshot of the transcript of the most recent finished recording.</summary>
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

    /// <summary>Starts the meeting detector.</summary>
    public Task StartDetectorAsync(CancellationToken ct) => _detector.StartAsync(ct);

    /// <summary>Stops the meeting detector and any operations the pipeline owns.</summary>
    public async Task StopDetectorAsync(CancellationToken ct)
    {
        // Stop new detections before stopping operations already owned by the pipeline.
        await _detector.StopAsync(ct).ConfigureAwait(false);
        await StopOwnedOperationsAsync().ConfigureAwait(false);
    }

    /// <summary>Dismisses a detected meeting that was not confirmed, and hides it for the cooldown period.</summary>
    public bool DismissPendingMeeting(string sessionId, TimeSpan cooldown)
    {
        var dismissed = _detector.Dismiss(sessionId, cooldown);
        lock (_gate)
            if (dismissed && _pendingSession?.Id == sessionId) _pendingSession = null;
        return dismissed;
    }

    /// <summary>Pauses meeting prompts for a while and cancels any pending prompt.</summary>
    public void SnoozeDetection(TimeSpan duration)
    {
        _detector.Snooze(duration);
        MeetingSession? pending;
        lock (_gate) { pending = _pendingSession; _pendingSession = null; }
        if (pending is not null) Publish(PendingMeetingCancelled, pending);
    }

    /// <summary>Turns meeting prompts back on after a snooze or dismissal.</summary>
    public void ResumeDetection() => _detector.Resume();

    /// <summary>Starts a recording chosen by the user, with the given title.</summary>
    public Task StartManualAsync(string title, CancellationToken ct) =>
        StartAsync(new MeetingSession { Title = title, SourceProcess = "manual" }, ct);

    /// <summary>
    /// Starts a recording linked to a calendar meeting. Throws <see cref="RecordingStartException"/>
    /// if the meeting is not eligible.
    /// </summary>
    public Task StartManualAsync(string title, MeetingTitleCandidate meeting, CancellationToken ct)
    {
        var session = new MeetingSession { Title = title, SourceProcess = "manual" };
        if (!session.TryAttachCalendarMeeting(session.Id, meeting))
            throw new RecordingStartException(RecordingStartFailureKind.IneligibleCalendarMeeting);
        return StartAsync(session, ct);
    }

    /// <summary>Confirms a detected meeting and starts recording it.</summary>
    public Task ConfirmAndStartAsync(MeetingSession session, CancellationToken ct) =>
        StartAsync(session, ct, confirmDetection: true);

    private Task StartAsync(MeetingSession session, CancellationToken ct, bool confirmDetection = false)
    {
        lock (_gate)
        {
            // Reject new ownership before mutating the current run; all start checks share this lock.
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
            // Reserve the pipeline before launching background startup so a second caller cannot race it.
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
            // The review window is based on the injected clock so status and timeout behavior agree in tests.
            var review = new SaveReview(sessionId, _getUtcNow().Add(SaveReviewDuration));
            _saveReview = review;
            SetStatus(PipelineState.ReviewingSave,
                "Recording stopped. Save now or discard before automatic save.", saveDeadline: review.Deadline);
            // Exit cannot wait for user input; preserving the transcript is the safe default.
            if (_preparingExit) review.Decision.TrySetResult(SaveDecision.Save);
            return review;
        }
    }

    private async Task<SaveDecision> WaitForSaveDecisionAsync(SaveReview review)
    {
        using var timeoutCancellation = new CancellationTokenSource();
        // Race the user's choice against the fixed review deadline, then cancel the unused delay task.
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

    /// <summary>Saves the recording now during the save review. Returns false if no matching review is open.</summary>
    public bool SaveNow(string sessionId) => TryDecideSave(sessionId, SaveDecision.Save);

    /// <summary>Discards the recording during the save review. Returns false if no matching review is open.</summary>
    public bool DiscardPendingRecording(string sessionId) =>
        TryDecideSave(sessionId, SaveDecision.Discard);

    private bool TryDecideSave(string sessionId, SaveDecision decision)
    {
        lock (_gate)
        {
            if (_saveReview is not { } review || review.SessionId != sessionId)
                return false;
            // A late discard must not erase work after the automatic-save deadline has passed.
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
            // A preview owns the same devices, so stop and fully drain it before starting the recording.
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
            // Local-user lookup is optional and must not delay capture startup.
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
            // Start Speech before subscribing to capture, so no audio can arrive before the stream is ready.
            await transcription.StartAsync(run.Cancellation.Token).ConfigureAwait(false);
            foreach (var device in _devices)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                EventHandler<AudioDataEventArgs> handler = (sender, args) =>
                {
                    var offerStop = false;
                    Task push;
                    lock (_gate)
                    {
                        // Reject late device callbacks and copy the NAudio buffer before its producer reuses it.
                        if (!ReferenceEquals(sender, device) || !ReferenceEquals(_run, run) || !run.AcceptCallbacks || !run.AcceptAudio)
                            return;
                        offerStop = NoteAudioActivity(run, args);
                        var pcmData = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
                        push = QueueAudioPush(run, transcription, pcmData, args.BytesRecorded);
                    }
                    if (offerStop)
                        Publish(MeetingEndedWhileRecording, run.Session);
                    try
                    {
                        // Bound how long a capture callback can wait for Speech; a stalled SDK must not
                        // block the device consumer forever.
                        push.WaitAsync(_transcriptionTimeout).GetAwaiter().GetResult();
                    }
                    catch (TimeoutException)
                    {
                        if (push.IsCompleted)
                        {
                            try { push.GetAwaiter().GetResult(); }
                            catch (Exception ex) { HandleAudioPushFailure(run, transcription, ex); }
                            return;
                        }
                        var requestStop = false;
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_run, run) || run.Closed) return;
                            run.TimedOutAudioPushes.Add(push);
                            // Stop accepting new buffers now; the pending operation is observed later
                            // so its eventual failure is not lost.
                            run.AcceptAudio = false;
                            run.AudioDeliveryFailed = true;
                            if (run.CaptureError is null)
                            {
                                run.CaptureError =
                                    "Speech transcription stalled; audio during the stall was not saved, and only the recognized transcript is available.";
                                if (_status.State is PipelineState.Starting or PipelineState.Recording)
                                    SetStatus(_status.State, "Speech stalled; stopping to save the recognized transcript.",
                                        error: run.CaptureError);
                            }
                            requestStop = RequestAutomaticStopLocked(run);
                        }
                        if (requestStop)
                            _ = Task.Run(StopAfterTranscriptionFailureAsync);
                    }
                    catch (Exception ex)
                    {
                        HandleAudioPushFailure(run, transcription, ex);
                    }
                };
                run.Devices.Add((device, handler));
                device.DataAvailable += handler;
                // Start devices one at a time so cancellation or a later failure can clean up only
                // those that were actually attached and started.
                await device.StartAsync(run.Cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                    if (run.AutomaticStopRequested) return;
            }
            lock (_gate)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                if (run.AutomaticStopRequested) return;
                SetStatus(PipelineState.Recording,
                    run.CaptureError is null ? "Recording" : "Transcription stopped; stop recording to save.",
                    error: run.CaptureError);
            }
        }
        catch (Exception ex)
        {
            if (run.AutomaticStopRequested)
            {
                _logger.LogWarning("Capture startup ended after transcription failure ({ErrorType}).",
                    ex.GetType().Name);
                return;
            }
            await CloseCaptureAsync(run, startupFailed: true).ConfigureAwait(false);
            _detector.Complete(run.Session.Id);
            RecordingStartFailure failure;
            lock (_gate)
            {
                // Keep the pipeline owned if cleanup could not release a native or Speech resource.
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
                // Apply the optional result only to the still-active run; a slow lookup must not rename a later one.
                if (ReferenceEquals(_run, run) && !run.Closed && !run.Cancellation.IsCancellationRequested && name is not null)
                    _speakerResolver.SetLocalUser(name);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Optional local speaker lookup failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    private Task QueueAudioPush(SessionRun run, ITranscriptionService transcription, byte[] pcmData, int bytesRecorded)
    {
        var previous = run.AudioPushTail;
        var push = Task.Run(async () =>
        {
            // Preserve capture order even if several source callbacks arrive before Speech finishes a write.
            await previous.ConfigureAwait(false);
            lock (_gate)
                if (run.AudioDeliveryFailed) return;
            try
            {
                await transcription.PushAudioAsync(pcmData, bytesRecorded, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleAudioPushFailure(run, transcription, ex);
                throw;
            }
        });
        run.AudioPushTail = push;
        run.PendingAudioPushes.Add(push);
        // Observe completion immediately so the pending-operation count and stalled-write state stay current.
        TrackPendingTranscriptionOperation(run, push, "audio push", completed =>
        {
            run.PendingAudioPushes.Remove(push);
            var timedOut = run.TimedOutAudioPushes.Remove(push);
            if (completed.IsFaulted && timedOut)
                _logger.LogError("Late transcription audio push failed ({ErrorType}).",
                    completed.Exception!.GetBaseException().GetType().Name);
            if (completed.IsFaulted && !run.AudioDeliveryFailed)
                HandleAudioPushFailure(run, _transcription, completed.Exception!.GetBaseException());
        }, logFailure: false);
        return push;
    }

    private void HandleAudioPushFailure(SessionRun run, ITranscriptionService transcription, Exception ex)
    {
        lock (_gate)
            if (!ReferenceEquals(_run, run) || run.Closed || run.AudioDeliveryFailed) return;
        var error = transcription.Health.Error;
        var requestStop = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run) || run.Closed || run.AudioDeliveryFailed) return;
            run.AcceptAudio = false;
            run.AudioDeliveryFailed = true;
            // Prefer the service's sanitized explanation when available; otherwise report a generic delivery failure.
            run.CaptureError = string.IsNullOrWhiteSpace(error)
                ? "Audio could not be delivered to transcription."
                : error;
            if (_status.State is PipelineState.Starting or PipelineState.Recording)
            {
                SetStatus(_status.State, "Transcription failed; stopping to save.",
                    error: run.CaptureError);
                requestStop = RequestAutomaticStopLocked(run);
            }
        }
        _logger.LogError("Audio push failed ({ErrorType}).", ex.GetType().Name);
        if (requestStop)
            _ = Task.Run(StopAfterTranscriptionFailureAsync);
    }

    private bool RequestAutomaticStopLocked(SessionRun run)
    {
        // Multiple callbacks can report the same failure; schedule only one fail-fast stop.
        if (run.AutomaticStopRequested || _stopTask is not null ||
            _status.State is not (PipelineState.Starting or PipelineState.Recording))
            return false;
        run.AutomaticStopRequested = true;
        return true;
    }

    /// <summary>Stops the current recording. Returns a completed task when there is nothing to stop.</summary>
    public Task StopManualAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            if (_run is null || _run.Closed || !_owned || _preview) return Task.CompletedTask;
            var run = _run;
            var startup = _startTask;
            // If Speech failed during startup, do not cancel before rollback can preserve its partial transcript.
            var preservePartialDuringStartup = run.AutomaticStopRequested &&
                _status.State == PipelineState.Starting;
            _stopTask = Task.Run(() => StopCoreAsync(run, startup));
            SetStatus(PipelineState.Stopping, "Stopping capture...");
            if (!preservePartialDuringStartup)
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
            // Startup may still own callbacks or devices; wait for it before beginning the final drain.
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
                // Discard removes the in-memory transcript and map, but does not run final AI work.
                Finish(PipelineState.Discarded, "Recording discarded.");
                return;
            }
            lock (_gate) if (ReferenceEquals(_saveReview, review)) _saveReview = null;
            SetStatus(PipelineState.Finalizing, "Saving transcript...");
            // Persist the transcript before optional AI so recognized speech survives any later failure.
            path = await PersistNotesAsync(snapshot, CreateNotes(snapshot), CancellationToken.None).ConfigureAwait(false);
            Publish(NotesSaved, new NotesSavedInfo(snapshot.SessionId, path));
            if (run.CaptureError is not null && ResourcesStillOwned())
            {
                Finish(PipelineState.Failed,
                    WithVisualizationStatus("Available transcript saved; capture did not complete cleanly.", path),
                    path, run.CaptureError);
                return;
            }
            if (run.TranscriptionFlushWarning is not null && ResourcesStillOwned())
            {
                Finish(PipelineState.Failed,
                    WithVisualizationStatus("Available transcript saved; final speech flush did not complete.", path),
                    path, run.TranscriptionFlushWarning);
                return;
            }
            try
            {
                // A stalled final speech flush keeps the recognized text; notes are still generated from it.
                await GenerateAndSaveAsync(snapshot, path, _exitCancellation.Token, allowPartialSave: true,
                    completionWarning: run.TranscriptionFlushWarning ?? run.CaptureError)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_exitCancellation.IsCancellationRequested)
            {
                // Exit cancellation does not undo the durable transcript or notes already saved.
                Finish(PipelineState.Saved, "Transcript saved.", path,
                    run.TranscriptionFlushWarning ?? run.CaptureError);
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
            // Stop devices first so their callbacks finish before the handlers and audio-push queue are closed.
            await Task.WhenAll(run.Devices.Select(async entry =>
                await entry.Device.StopAsync(CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
                run.CaptureError = run.CaptureError is null
                    ? "Audio capture did not stop cleanly."
                    : $"{run.CaptureError} Audio capture did not stop cleanly.";
            _logger.LogError("Capture shutdown failed ({ErrorType}).", ex.GetType().Name);
        }
        finally
        {
            lock (_gate) run.AcceptAudio = false;
            foreach (var (device, handler) in run.Devices) device.DataAvailable -= handler;
        }
        var captureWarning = DescribeCaptureCompletionWarning(_devices.SelectMany(device => device.Health));
        if (captureWarning is not null)
            lock (_gate)
                run.CaptureError = run.CaptureError is null
                    ? captureWarning
                    : $"{run.CaptureError} {captureWarning}";
        if (!startupFailed) SetStatus(PipelineState.Finalizing, "Flushing final transcript...");
        var flushed = false;
        if (run.TranscriptionAttempted)
        {
            Task[] pendingPushes;
            bool audioDeliveryFailed;
            lock (_gate)
            {
                pendingPushes = run.PendingAudioPushes.Where(push => !push.IsCompleted).ToArray();
                audioDeliveryFailed = run.AudioDeliveryFailed;
            }
            if (pendingPushes.Length > 0 && !audioDeliveryFailed)
            {
                try
                {
                    // Allow already queued writes a bounded chance to finish before closing the Speech stream.
                    await Task.WhenAll(pendingPushes).WaitAsync(_transcriptionTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    MarkAudioDeliveryStalled(run);
                }
                catch (Exception ex)
                {
                    HandleAudioPushFailure(run, _transcription, ex);
                }
            }

            lock (_gate)
                pendingPushes = run.PendingAudioPushes.Where(push => !push.IsCompleted).ToArray();
            if (pendingPushes.Length > 0)
            {
                lock (_gate)
                {
                    run.TranscriptionFlushWarning =
                        "Final speech flush was deferred until a stalled audio push settles; recognized text was retained.";
                    run.CaptureError ??= run.TranscriptionFlushWarning;
                }
                DeferTranscriptionStop(run, pendingPushes);
                // Closing the stream while a write is still running can race the SDK; defer it until that write settles.
                _logger.LogWarning("Speech finalization was deferred because an audio push is still active.");
            }
            else
            {
                var stopTask = StartTranscriptionStop(run);
                try
                {
                    var result = await stopTask.WaitAsync(_transcriptionTimeout).ConfigureAwait(false);
                    TranscriptionSegment[]? finalSegments = null;
                    lock (_gate)
                    {
                        // The service result is authoritative, including segments emitted during its flush.
                        if (!run.Closed && result.Segments.Count >= run.Segments.Count)
                        {
                            run.Segments.Clear();
                            run.Segments.AddRange(result.Segments.Select(Enrich));
                            finalSegments = run.Segments.ToArray();
                        }
                    }
                    if (finalSegments is not null)
                        // Refresh the map from the final service result, which can include speech recognized
                        // while StopAsync flushed its internal buffers.
                        for (var index = 0; index < finalSegments.Length; index++)
                            _visualization.AppendOrUpdateSegment(index + 1, finalSegments[index]);
                    flushed = true;
                }
                catch (TimeoutException)
                {
                    lock (_gate)
                    {
                        run.TranscriptionFlushWarning =
                            "Final speech flush stalled; available recognized text was retained.";
                        run.CaptureError ??= run.TranscriptionFlushWarning;
                    }
                    _logger.LogWarning("Speech finalization timed out; saving the recognized transcript.");
                }
                catch (Exception ex)
                {
                    lock (_gate)
                        run.TranscriptionFlushWarning = "Transcription flush failed; available text was retained.";
                    _logger.LogError("Transcription shutdown failed ({ErrorType}).", ex.GetType().Name);
                }
            }
        }
        lock (_gate)
        {
            // Close callback acceptance only after the flush decision, then freeze one final snapshot.
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

    private void MarkAudioDeliveryStalled(SessionRun run)
    {
        lock (_gate)
        {
            // The caller has already hit the timeout; preserve recognized text and stop queuing audio.
            run.AcceptAudio = false;
            run.AudioDeliveryFailed = true;
            run.CaptureError ??=
                "Speech transcription stalled; audio during the stall was not saved, and only the recognized transcript is available.";
            foreach (var push in run.PendingAudioPushes.Where(push => !push.IsCompleted))
                run.TimedOutAudioPushes.Add(push);
            if (_status.State is PipelineState.Stopping or PipelineState.Finalizing)
                SetStatus(_status.State, "Speech stalled; saving the recognized transcript.",
                    error: run.CaptureError);
        }
    }

    private Task<TranscriptionResult> StartTranscriptionStop(SessionRun run)
    {
        lock (_gate) run.TranscriptionStopRequested = true;
        // Speech shutdown may outlive the capture callback thread, so run and track it as owned work.
        var stopTask = Task.Run(() => _transcription.StopAsync(CancellationToken.None));
        TrackPendingTranscriptionOperation(run, stopTask, "speech shutdown");
        return stopTask;
    }

    private void DeferTranscriptionStop(SessionRun run, Task[] pendingPushes)
    {
        Task deferredStop;
        lock (_gate)
        {
            if (run.TranscriptionStopRequested) return;
            run.TranscriptionStopRequested = true;
            deferredStop = Task.Run(async () =>
            {
                // Wait for all captured writes to settle before closing the push stream.
                try { await Task.WhenAll(pendingPushes).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning("Audio delivery ended with {ErrorType}; stopping Speech after it settled.",
                        ex.GetType().Name);
                }

                var stopTask = Task.Run(() => _transcription.StopAsync(CancellationToken.None));
                TrackPendingTranscriptionOperation(run, stopTask, "speech shutdown");
                try { await stopTask.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning("Deferred Speech shutdown failed ({ErrorType}).", ex.GetType().Name);
                }
            });
        }
        TrackPendingTranscriptionOperation(run, deferredStop, "deferred speech shutdown");
    }

    private async Task StopAfterTranscriptionFailureAsync()
    {
        try { await StopManualAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError("Fail-fast recording stop failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    private void TrackPendingTranscriptionOperation(SessionRun run, Task operation, string name,
        Action<Task>? onCompleted = null, bool logFailure = true)
    {
        lock (_gate)
        {
            // Replace the completed signal when the first operation is added; shutdown can await
            // this signal without polling the list.
            if (run.PendingTranscriptionOperations.Count == 0)
                run.TranscriptionOperationsSettled =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
            run.PendingTranscriptionOperations.Add(operation);
        }
        ObservePendingTranscriptionOperation(run, operation, name, onCompleted, logFailure);
    }

    private void ObservePendingTranscriptionOperation(SessionRun run, Task operation, string name,
        Action<Task>? onCompleted = null, bool logFailure = true)
    {
        _ = operation.ContinueWith(completed =>
        {
            if (logFailure && completed.IsFaulted)
                _logger.LogError("Transcription {Operation} failed ({ErrorType}).", name,
                    completed.Exception!.GetBaseException().GetType().Name);
            else if (logFailure && completed.IsCanceled)
                _logger.LogWarning("Transcription {Operation} was canceled.", name);

            lock (_gate)
            {
                // Remove before signaling so a waiter that wakes sees the settled collection.
                run.PendingTranscriptionOperations.Remove(operation);
                onCompleted?.Invoke(completed);
                if (run.PendingTranscriptionOperations.Count == 0)
                    run.TranscriptionOperationsSettled.TrySetResult();
                var wasOwned = _owned;
                _owned = ResourcesStillOwned();
                if (wasOwned && !_owned && ReferenceEquals(_run, run) &&
                    _status.State == PipelineState.Failed)
                {
                    var status = _status;
                    SetStatus(status.State, status.Message, status.NotesPath, status.Error,
                        status.IntelligenceFailure, status.SessionId, status.SaveDeadline);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForTranscriptionOperationsAsync()
    {
        while (true)
        {
            Task settled;
            lock (_gate)
            {
                if (_run is not { PendingTranscriptionOperations.Count: > 0 } run) return;
                settled = run.TranscriptionOperationsSettled.Task;
            }
            // A new operation can be registered after one signal; recheck under the lock each time.
            await settled.ConfigureAwait(false);
        }
    }

    internal static string? DescribeCaptureCompletionWarning(IEnumerable<AudioSourceHealth> health)
    {
        // Only surface failures or actual buffer loss; a clean stopped source is not a warning.
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
        // Report all dropped buffers as one count while keeping device failures human-readable.
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

    /// <summary>Runs final note generation again for the last finished session. Only allowed when no recording or finalization is active.</summary>
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
            // Reserve the pipeline while it rewrites notes and retries AI for this exact saved transcript.
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
                    // Save the current transcript first so a retry never relies only on in-memory state.
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

    /// <summary>Asks for a new topic-map analysis of the current transcript.</summary>
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
            // Skip duplicate analysis when neither transcript nor topic trail changed.
            if (_visualizationAnalysisTask is { IsCompletedSuccessfully: true } &&
                _visualizationAnalysisSessionId == document.SessionId &&
                _visualizationAnalysisTranscriptRevision == document.TranscriptRevision &&
                _visualizationAnalysisTopicCount == document.TopicTrail.Count)
                return Task.CompletedTask;
            if (_visualizationAnalysisTask is { IsCompleted: false })
            {
                // Coalesce rapid transcript updates into one follow-up pass rather than starting parallel model calls.
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
                    // Analyze outside the pipeline lock; apply only while this recording is still current.
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
                        // Use the newest transcript for the one coalesced refresh, not the snapshot
                        // that started the earlier request.
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
            // There is no speech to summarize; the transcript file still contains the completed session.
            Finish(PipelineState.Saved,
                WithVisualizationStatus("Transcript saved; no speech was recognized.", path), path,
                completionWarning);
            return path;
        }
        ct.ThrowIfCancellationRequested();
        SetStatus(PipelineState.Generating, "Transcript saved. Generating meeting notes...", path, sessionId: snapshot.SessionId);
        MeetingNotes notes;
        IntelligenceFailure? failure = null;
        // The final topic-map pass only refines the map built live during recording. Its failure must not
        // mark the meeting's notes as failed, or every small map validation problem asks the user to Retry AI.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var document = _visualization.Snapshot();
            var result = await _visualizationIntelligence.AnalyzeVisualizationAsync(document, ct)
                .WaitAsync(ct).ConfigureAwait(false);
            bool stale;
            lock (_gate)
                // Avoid applying a slow result to a newer session or transcript revision.
                stale = _lastCompleted?.SessionId != snapshot.SessionId ||
                    _lastCompleted.Revision != snapshot.Revision;
            if (stale)
                AiFinalizationDiagnostics.Record("final-visualization", "stale-skipped", null,
                    elapsed.ElapsedMilliseconds, _aiFinalizationLogDirectory);
            else
                _visualization.ApplyAnalysisResult(document, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failed optional map refinement does not prevent final notes from using the live map.
            AiFinalizationDiagnostics.Record("final-visualization", "kept-live-map", ex,
                elapsed.ElapsedMilliseconds, _aiFinalizationLogDirectory);
            _logger.LogWarning("Final topic map update failed ({ErrorType}); keeping the live topic map.",
                ex.GetType().Name);
        }
        elapsed.Restart();
        try
        {
            var generated = await _intelligence.SynthesizeAsync(snapshot.Title, snapshot.ToTranscriptionResult(), ct)
                .WaitAsync(ct).ConfigureAwait(false);
            notes = CreateNotes(snapshot, generated, failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Preserve the transcript and any prior notes so the user can retry intelligence later.
            failure ??= FailureFrom(ex, "Note generation failed. Check settings and retry.");
            AiFinalizationDiagnostics.Record("final-notes", "retry-available", ex,
                elapsed.ElapsedMilliseconds, _aiFinalizationLogDirectory);
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
            // During normal finalization, keep the already-durable transcript if the generated rewrite fails.
            _logger.LogWarning(
                "Generated notes could not be saved ({ErrorType}); preserving the durable transcript for retry.",
                ex.GetType().Name);
            AiFinalizationDiagnostics.Record("final-save", "retry-available", ex, 0, _aiFinalizationLogDirectory);
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
        // Sidecar failure is separate from Markdown success; explain that retry remains pending.
        lock (_gate)
            return _pendingVisualizationPath == path
                ? message + " Visualization data is pending retry when the app exits."
                : message;
    }

    private MeetingNotes PreservedNotes(TranscriptSnapshot snapshot)
    {
        // Rebuild transcript-owned fields while retaining the last saved generated sections.
        lock (_gate)
            return CreateNotes(snapshot, _lastSavedNotes, _lastSavedNotes?.IntelligenceFailure);
    }

    private async Task<string> PersistNotesAsync(
        TranscriptSnapshot snapshot, MeetingNotes notes, CancellationToken ct)
    {
        // Snapshot the map once so the Markdown topic notes and companion JSON describe the same revision.
        var document = _visualization.Snapshot();
        var persistedNotes = CreateNotes(snapshot, notes, notes.IntelligenceFailure);
        persistedNotes.TopicNotes.Clear();
        persistedNotes.TopicNotes.AddRange(MeetingTopicNotesBuilder.Build(document));
        var cachedNotes = CreateNotes(snapshot, persistedNotes, persistedNotes.IntelligenceFailure);
        // Save Markdown first: it is the required durable record; the sidecar is optional and retryable.
        var path = await _storage.SaveAsync(persistedNotes, ct).ConfigureAwait(false);
        var retryingPendingVisualization = false;
        lock (_gate)
        {
            if (_lastSavedPath is not null && !string.Equals(_lastSavedPath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Notes storage changed the completed session's stable path.");
            retryingPendingVisualization = _pendingVisualizationPath is not null &&
                string.Equals(_pendingVisualizationPath, path, StringComparison.OrdinalIgnoreCase);
            _lastSavedNotes = cachedNotes;
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
            // Keep the document and stable path in memory so exit or a later save can retry the sidecar.
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
        // Always take identity and raw transcript from the immutable snapshot; copy generated lists
        // so later model or UI edits do not mutate another saved result.
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
            notes.TopicNotes.AddRange(generated.TopicNotes);
            notes.Decisions.AddRange(generated.Decisions);
            notes.ActionItems.AddRange(generated.ActionItems);
            notes.Topics.AddRange(generated.Topics);
            notes.KeyDiscussionPoints.AddRange(generated.KeyDiscussionPoints);
        }
        return notes;
    }

    /// <summary>Starts audio capture without recording, so the user can check input levels.</summary>
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
                        // Preview starts devices only; no transcript service or session run is created.
                        await device.StartAsync(previewToken).ConfigureAwait(false);
                    }
                    previewToken.ThrowIfCancellationRequested();
                    SetStatus(PipelineState.Idle, "Audio preview active (not recording).");
                }
                catch
                {
                    // A partially started preview still owns some devices, so always stop all configured sources.
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

    /// <summary>Stops the audio preview.</summary>
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
                // Wait for startup rollback before issuing stop, avoiding start/stop races on the devices.
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
        TranscriptionSegment enriched;
        SessionTranscriptSegment update;
        int ordinal;
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run) || !run.AcceptCallbacks) return;
            // Reserve a position under the state lock; publication can then wait for earlier SDK callbacks.
            run.LastTranscriptProgressAt = _getUtcNow();
            segment = CanonicalSpeaker(segment);
            enriched = segment;
            run.Segments.Add(enriched);
            ordinal = run.Segments.Count;
        }

        lock (run.SegmentPublicationGate)
        {
            // Speech callbacks may run concurrently. Preserve transcript order while keeping the main
            // pipeline lock free during event publication.
            while (ordinal != run.NextSegmentPublicationOrdinal)
                Monitor.Wait(run.SegmentPublicationGate);
            try
            {
                if (!IsCurrentRunAcceptingCallbacks(run)) return;
                if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
                    _speakerResolver.NotifySpeaker(segment.SpeakerId);
                lock (_gate)
                {
                    if (!ReferenceEquals(_run, run) || !run.AcceptCallbacks) return;
                    segment = CanonicalSpeaker(segment);
                    enriched = Enrich(segment);
                    run.Segments[ordinal - 1] = enriched;
                    update = new SessionTranscriptSegment(run.Session.Id, ordinal, enriched);
                }
                if (!_visualization.TryAppendOrUpdateSegment(
                        run.Session.Id, ordinal, enriched, () => IsCurrentRunAcceptingCallbacks(run)))
                    return;
                if (!IsCurrentRunAcceptingCallbacks(run)) return;
                Publish(TranscriptSegmentReceived, update);
            }
            finally
            {
                run.NextSegmentPublicationOrdinal++;
                Monitor.PulseAll(run.SegmentPublicationGate);
            }
        }
    }

    private bool IsCurrentRunAcceptingCallbacks(SessionRun run)
    {
        return ReferenceEquals(Volatile.Read(ref _run), run) && run.AcceptCallbacks;
    }

    private void NotePartialTranscriptProgress(SessionRun run, PartialTranscript partial)
    {
        if (string.IsNullOrWhiteSpace(partial.Text)) return;
        var hash = StringComparer.Ordinal.GetHashCode(partial.Text);
        // Repeated identical partials are not progress; only changed recognized text resets inactivity timing.
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

        // Offer a manual stop only for confirmed detections after both audible audio and transcript
        // progress have been quiet for the full inactivity window.
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
        // Compare mean squared sample amplitude with the 500-RMS threshold without a square root.
        return squares >= AudibleRmsSquared * samples;
    }

    private void OnSpeakerResolved(object? sender, SpeakerResolvedEventArgs args)
    {
        lock (_gate)
        {
            if (_run is { Closed: false } run)
            {
                // Refresh stored segments after a user assigns a display name; the transcript uses IDs as identity.
                for (var index = 0; index < run.Segments.Count; index++)
                    if (string.Equals(run.Segments[index].SpeakerId, args.SpeakerId,
                            StringComparison.OrdinalIgnoreCase))
                        run.Segments[index] = Enrich(run.Segments[index]);
            }
        }
        try
        {
            if (_visualization.HasActiveSession)
                _visualization.ApplySpeakerAssignment(args.SpeakerId, args.DisplayName);
        }
        catch (InvalidOperationException) { }
    }

    private TranscriptionSegment Enrich(TranscriptionSegment segment) =>
        segment with { SpeakerDisplayName = _speakerResolver.GetDisplayName(segment.SpeakerId) };

    private TranscriptionSegment CanonicalSpeaker(TranscriptionSegment segment) =>
        string.IsNullOrWhiteSpace(segment.SpeakerId) ? segment
            : segment with { SpeakerId = _speakerResolver.Canonical(segment.SpeakerId) };

    /// <summary>
    /// Combines speakers into <paramref name="keptSpeakerId"/>: transcript lines, the topic map's speaker data
    /// and, for a finished meeting, the saved notes and companion JSON all move to the kept speaker.
    /// Returns false when there was nothing to combine.
    /// </summary>
    public async Task<bool> MergeSpeakersAsync(
        IEnumerable<string?> speakerIds, string keptSpeakerId, CancellationToken ct = default)
    {
        var plan = SpeakerMergePlan.Create(speakerIds, keptSpeakerId);
        if (plan is null) return false;

        TranscriptSnapshot? persistSnapshot = null;
        string keptName;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparingExit) throw new InvalidOperationException("Exit is already in progress.");
            var state = _status.State;
            if (state != PipelineState.Recording && !IsResting(state))
                throw new InvalidOperationException("Speakers can be combined while recording or after the meeting is saved.");

            // Update IDs and display names as one pipeline operation, then persist finished notes if needed.
            _speakerResolver.Merge(plan);
            keptName = _speakerResolver.GetDisplayName(plan.KeptId);
            if (_run is { } run)
                for (var index = 0; index < run.Segments.Count; index++)
                    run.Segments[index] = Enrich(plan.Remap(run.Segments[index]));
            if (_lastCompleted is { } completed && (_run is null || _run.Session.Id == completed.SessionId))
            {
                _lastCompleted = new TranscriptSnapshot(completed.SessionId, completed.Title, completed.StartedAt,
                    completed.EndedAt, completed.Revision, completed.IsComplete,
                    completed.Segments.Select(segment => Enrich(plan.Remap(segment))).ToArray());
                if (!_owned && IsResting(state) && _lastSavedPath is not null)
                {
                    _owned = true;
                    persistSnapshot = _lastCompleted;
                }
            }
        }

        try
        {
            // The saved map is separate from transcript identity, so merge its speaker references too.
            _visualization.MergeSpeakers(plan, keptName);
        }
        catch (InvalidOperationException) { }

        if (persistSnapshot is null) return true;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _exitCancellation.Token);
            await PersistNotesAsync(persistSnapshot, PreservedNotes(persistSnapshot), lifetime.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _owned = ResourcesStillOwned();
        }
        return true;
    }

    private TranscriptSnapshot Snapshot(SessionRun run) => new(run.Session.Id, run.Session.Title,
        run.Session.StartedAt, run.Session.EndedAt, run.Segments.Count, run.Complete,
        run.Segments.Select(Enrich).ToArray());

    private void OnMeetingStarted(object? sender, MeetingSession session)
    {
        lock (_gate)
        {
            // Leave the detected call pending when recording, preview, exit, or disposal already owns the app.
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
            // Withdraw an unconfirmed prompt immediately when its detected process ends.
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
        // Watch the same process name for a new detector session during the grace period.
        void OnResume(object? _, MeetingSession resumedSession)
        {
            if (string.Equals(resumedSession.SourceProcess, sourceProcess, StringComparison.Ordinal))
                resumed.TrySetResult();
        }
        _detector.MeetingStarted += OnResume;
        try
        {
            // Do not stop automatically: a call can briefly report no active capture while reconnecting.
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

    /// <summary>Returns a short recap of the recent transcript for the "What did I miss?" button.</summary>
    public Task<string> WhatDidIMissAsync(string recentTranscript, CancellationToken ct) =>
        _intelligence.CatchUpAsync(recentTranscript, ct);

    private void Finish(PipelineState state, string message, string? path = null, string? error = null,
        IntelligenceFailure? intelligenceFailure = null)
    {
        lock (_gate)
        {
            // Recheck live ownership at the final state transition; a timed-out operation may still be running.
            _owned = ResourcesStillOwned();
            if (_owned)
            {
                state = PipelineState.Failed;
                error ??= "Capture resources could not be released. Restart the application before recording again.";
            }
            SetStatus(state, message, path, error, intelligenceFailure);
        }
    }

    private bool ResourcesStillOwned() =>
        // Capture devices, Speech, and tracked background pushes all count as ownership.
        _devices.Any(d => d.IsCapturing) || _transcription.HasActiveResources ||
        (_run?.PendingTranscriptionOperations.Any(operation => !operation.IsCompleted) ?? false);

    private void SetStatus(PipelineState state, string message, string? path = null, string? error = null,
        IntelligenceFailure? intelligenceFailure = null, string? sessionId = null,
        DateTimeOffset? saveDeadline = null)
    {
        lock (_gate)
        {
            // Sequence numbers let consumers ignore delayed notifications from background work.
            _status = new(_status.Sequence + 1, sessionId ?? (_preview ? null : _run?.Session.Id),
                state, message, path, error, intelligenceFailure, saveDeadline);
            Publish(StateChanged, _status);
            Publish(StatusChanged, message);
        }
    }

    private void Publish<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) return;
        // Isolate subscribers so one view throwing cannot prevent other listeners from seeing the update.
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, value); }
            catch (Exception ex) { _logger.LogError("Pipeline event subscriber failed ({ErrorType}).", ex.GetType().Name); }
    }

    private Task StopOwnedOperationsAsync()
    {
        lock (_gate)
        {
            // Observe earlier failures during shutdown, then join the active stop, preview, and retry work.
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

    /// <summary>
    /// Prepares the app to close: cancels work in progress, makes the transcript durable, and
    /// releases capture. The same task is returned if called again. See <see cref="ExitReadiness"/>.
    /// </summary>
    public Task<ExitReadiness> PrepareForExitAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_exitTask is not null) return _exitTask;
            // Make exit preparation single-flight before starting the asynchronous cleanup.
            _preparingExit = true;
            return _exitTask = Task.Run(PrepareForExitCoreAsync);
        }
    }

    private async Task<ExitReadiness> PrepareForExitCoreAsync()
    {
        try
        {
            // Cancel AI work first; the current save review is forced to save so the transcript becomes durable.
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
                // Never close while a device, Speech object, or audio push is still owned.
                if (_owned || ResourcesStillOwned())
                    throw new InvalidOperationException("Capture resources are still owned. Exit was not completed.");
                recovery = _lastSavedPath is null ? _lastCompleted : null;
            }
            if (recovery is not null)
            {
                // Recover a transcript that was recognized but not yet written before attempting optional sidecars.
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
                // Retry optional visualization data once without holding capture resources or keeping the app open.
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
                // Permit a later exit attempt after a required cleanup or transcript save failed.
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
            // Report the lost retry plainly; the transcript itself is already saved and remains usable.
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
            // Persist a sidecar retry record so a later launch can recover it without reopening this session.
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

    /// <summary>Stops the pipeline and releases its resources. Safe to call more than once.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is { IsCompleted: false } pending) return new(pending);
            if (_disposed) return ValueTask.CompletedTask;
            // Mark disposed before cleanup so new public operations fail instead of racing resource release.
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Detach event sources first so disposal cannot start new pipeline work.
        _detector.MeetingStarted -= OnMeetingStarted;
        _detector.MeetingEnded -= OnMeetingEnded;
        _speakerResolver.SpeakerResolved -= OnSpeakerResolved;
        Exception? stopFailure = null;
        try
        {
            await StopOwnedOperationsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopFailure = ex;
        }
        // Even if stop reported a failure, wait for every tracked Speech operation before disposing its service.
        await WaitForTranscriptionOperationsAsync().ConfigureAwait(false);
        _diagnostics.Clear();
        List<Exception> errors = [];
        if (stopFailure is not null) errors.Add(stopFailure);
        try { await _transcription.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        // Dispose every device independently so one failure does not leak the remaining sources.
        foreach (var device in _devices)
            try { device.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
        if (errors.Count == 1 && stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
        if (errors.Count > 0) throw new AggregateException("Pipeline resources failed to dispose cleanly.", errors);
    }
}
