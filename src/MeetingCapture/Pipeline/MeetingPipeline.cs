using MeetingCapture.AudioCapture;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging;

namespace MeetingCapture.Pipeline;

/// <summary>
/// Orchestrates the full meeting capture pipeline:
///   MeetingDetector → AudioCapture → Transcription → Intelligence → Storage
///
/// Lifecycle:
///   1. IMeetingDetector fires MeetingStarted → pipeline begins audio capture + transcription
///   2. IMeetingDetector fires MeetingEnded   → pipeline stops capture, calls LLM, saves notes
///   3. Manual start/stop via StartManualAsync/StopManualAsync for explicit control
/// </summary>
public sealed class MeetingPipeline : IAsyncDisposable
{
    private readonly IMeetingDetector _detector;
    private readonly IEnumerable<IAudioCapture> _captureDevices;
    private readonly ITranscriptionService _transcription;
    private readonly INotesIntelligence _intelligence;
    private readonly INotesStorage _storage;
    private readonly ILogger<MeetingPipeline> _logger;

    private CancellationTokenSource? _sessionCts;
    private MeetingSession? _currentSession;
    private bool _pipelineRunning;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? NotesSaved;

    public MeetingPipeline(
        IMeetingDetector detector,
        IEnumerable<IAudioCapture> captureDevices,
        ITranscriptionService transcription,
        INotesIntelligence intelligence,
        INotesStorage storage,
        ILogger<MeetingPipeline> logger)
    {
        _detector = detector;
        _captureDevices = captureDevices;
        _transcription = transcription;
        _intelligence = intelligence;
        _storage = storage;
        _logger = logger;

        _detector.MeetingStarted += OnMeetingStarted;
        _detector.MeetingEnded += OnMeetingEnded;
    }

    public bool IsRunning => _pipelineRunning;
    public MeetingSession? CurrentSession => _currentSession;

    public async Task StartDetectorAsync(CancellationToken cancellationToken)
    {
        await _detector.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Meeting detector active");
        RaiseStatus("Watching for meetings...");
    }

    public async Task StopDetectorAsync(CancellationToken cancellationToken)
    {
        await _detector.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_pipelineRunning)
            await StopCaptureAndProcessAsync().ConfigureAwait(false);
    }

    /// <summary>Manually begins a capture session (bypasses auto-detection).</summary>
    public async Task StartManualAsync(string title, CancellationToken cancellationToken)
    {
        _currentSession = new MeetingSession { Title = title, SourceProcess = "manual" };
        await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Manually ends the current session and triggers note synthesis.</summary>
    public async Task StopManualAsync()
    {
        _currentSession?.End();
        await StopCaptureAndProcessAsync().ConfigureAwait(false);
    }

    private void OnMeetingStarted(object? sender, MeetingSession session)
    {
        _currentSession = session;
        _ = Task.Run(async () =>
        {
            try
            {
                await StartCaptureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start capture for session {Session}", session);
                RaiseStatus($"Capture failed: {ex.Message}");
            }
        });
    }

    private void OnMeetingEnded(object? sender, MeetingSession session)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await StopCaptureAndProcessAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process session {Session}", session);
                RaiseStatus($"Processing failed: {ex.Message}");
            }
        });
    }

    private async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        if (_pipelineRunning)
        {
            _logger.LogWarning("StartCapture called while already running — ignoring");
            return;
        }

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _sessionCts.Token;

        _logger.LogInformation("Starting capture for session: {Session}", _currentSession);
        RaiseStatus($"Recording: {_currentSession?.Title}");

        await _transcription.StartAsync(ct).ConfigureAwait(false);

        foreach (var device in _captureDevices)
        {
            device.DataAvailable += OnAudioData;
            await device.StartAsync(ct).ConfigureAwait(false);
        }

        _pipelineRunning = true;
    }

    private async Task StopCaptureAndProcessAsync()
    {
        if (!_pipelineRunning)
            return;

        _pipelineRunning = false;
        _logger.LogInformation("Stopping capture and processing...");
        RaiseStatus("Processing meeting notes...");

        var ct = CancellationToken.None;

        foreach (var device in _captureDevices)
        {
            device.DataAvailable -= OnAudioData;
            await device.StopAsync(ct).ConfigureAwait(false);
        }

        var transcript = await _transcription.StopAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Transcription complete: {Words} words", transcript.WordCount);

        if (transcript.WordCount == 0)
        {
            _logger.LogWarning("Empty transcript — skipping note synthesis");
            RaiseStatus("No speech detected — no notes saved.");
            return;
        }

        var title = _currentSession?.Title ?? "Meeting";
        var notes = await _intelligence.SynthesizeAsync(title, transcript, ct).ConfigureAwait(false);
        var path = await _storage.SaveAsync(notes, ct).ConfigureAwait(false);

        _logger.LogInformation("Notes saved to: {Path}", path);
        RaiseStatus($"Notes saved: {path}");
        NotesSaved?.Invoke(this, path);

        _sessionCts?.Dispose();
        _sessionCts = null;
    }

    private void OnAudioData(object? sender, AudioDataEventArgs e)
    {
        if (!_transcription.IsTranscribing)
            return;

        _ = _transcription.PushAudioAsync(e.Buffer, e.BytesRecorded, CancellationToken.None);
    }

    private void RaiseStatus(string message) =>
        StatusChanged?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        _detector.MeetingStarted -= OnMeetingStarted;
        _detector.MeetingEnded -= OnMeetingEnded;

        if (_pipelineRunning)
            await StopCaptureAndProcessAsync().ConfigureAwait(false);

        await _transcription.DisposeAsync().ConfigureAwait(false);

        foreach (var device in _captureDevices)
            device.Dispose();

        _sessionCts?.Dispose();
        _detector.Dispose();
    }
}
