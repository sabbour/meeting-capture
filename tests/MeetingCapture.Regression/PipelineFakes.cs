using MeetingCapture.AudioCapture;
using MeetingCapture.Configuration;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetingCapture.Regression;

internal sealed class FakeCapture : IAudioCapture, IMicActivitySource
{
    public string Name => "synthetic-" + Source;
    public bool IsCapturing { get; private set; }
    public int SampleRate => 16000;
    public int Channels => 1;
    public DateTime? MicActivityTimestamp { get; set; }
    /// <summary>Health source label ("microphone" or "loopback") used by startup classification.</summary>
    public string Source { get; init; } = "microphone";
    public CaptureHealthState? HealthState { get; set; }
    public string? HealthError { get; set; }
    public long DroppedBuffers { get; set; }
    public IReadOnlyList<AudioSourceHealth> Health => [
        new(Source, HealthState ?? (IsCapturing ? CaptureHealthState.Active : CaptureHealthState.Stopped),
            0, 0, DroppedBuffers, null, null, HealthError)];
    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    public Func<CancellationToken, Task>? OnStart { get; set; }
    public Func<CancellationToken, Task>? OnStop { get; set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int Disposals { get; private set; }
    public Exception? DisposeFailure { get; set; }
    public int Subscribers => DataAvailable?.GetInvocationList().Length ?? 0;
    public EventHandler<AudioDataEventArgs>? CaptureHandlers() => DataAvailable;

    public async Task StartAsync(CancellationToken ct)
    {
        Check.True(!IsCapturing);
        Starts++;
        IsCapturing = true;
        if (OnStart is not null) await OnStart(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Stops++;
        if (OnStop is not null) await OnStop(ct);
        IsCapturing = false;
    }

    public void EmitAudio(byte[]? buffer = null)
    {
        buffer ??= [1, 2];
        DataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, buffer.Length));
    }

    public void EmitSilence() => EmitAudio([0, 0]);
    public void Dispose()
    {
        Disposals++;
        IsCapturing = false;
        if (DisposeFailure is not null) throw DisposeFailure;
    }
}

internal sealed class FakeTranscription : ITranscriptionService
{
    private TranscriptionResult _result = new();
    public event EventHandler<TranscriptionSegment>? SegmentRecognized;
    public event EventHandler<PartialTranscript>? PartialResultReceived;
    public bool IsTranscribing { get; private set; }
    public bool RetainResources { get; set; }
    public bool HasActiveResources => IsTranscribing || RetainResources;
    public long TotalAudioBytesReceived { get; private set; }
    public CaptureHealthState? HealthState { get; set; }
    public string? HealthError { get; set; }
    public TranscriptionHealth Health => new(
        HealthState ?? (IsTranscribing ? CaptureHealthState.Active : CaptureHealthState.Stopped),
        TotalAudioBytesReceived, null, Pushes, 0, 0, HealthError);
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int Disposals { get; private set; }
    public int Pushes { get; private set; }
    public int FinalSubscribers => SegmentRecognized?.GetInvocationList().Length ?? 0;
    public int PartialSubscribers => PartialResultReceived?.GetInvocationList().Length ?? 0;
    public Func<CancellationToken, Task>? OnStart { get; set; }
    public Func<CancellationToken, Task>? OnStop { get; set; }
    public Exception? PushFailure { get; set; }
    public Exception? DisposeFailure { get; set; }

    public async Task StartAsync(CancellationToken ct)
    {
        Check.True(!IsTranscribing);
        Starts++;
        IsTranscribing = true;
        TotalAudioBytesReceived = 0;
        Pushes = 0;
        _result = new();
        if (OnStart is not null) await OnStart(ct);
    }

    public Task PushAudioAsync(byte[] pcmData, int bytesRecorded, CancellationToken ct)
    {
        Check.True(IsTranscribing);
        ct.ThrowIfCancellationRequested();
        Pushes++;
        if (PushFailure is not null) throw PushFailure;
        TotalAudioBytesReceived += bytesRecorded;
        return Task.CompletedTask;
    }

    public async Task<TranscriptionResult> StopAsync(CancellationToken ct)
    {
        Stops++;
        try
        {
            if (OnStop is not null) await OnStop(ct);
            return _result;
        }
        finally { IsTranscribing = false; }
    }

    public void Emit(string text)
    {
        var segment = new TranscriptionSegment(text, DateTimeOffset.UtcNow, SpeakerId: "Guest-1");
        _result.Segments.Add(segment);
        SegmentRecognized?.Invoke(this, segment);
    }

    public void EmitPartial(string text) => PartialResultReceived?.Invoke(this, new(text, "Guest-1"));
    public EventHandler<TranscriptionSegment>? CaptureFinalHandlers() => SegmentRecognized;
    public EventHandler<PartialTranscript>? CapturePartialHandlers() => PartialResultReceived;
    public void ClearReturnedResult() => _result.Segments.Clear();
    public ValueTask DisposeAsync()
    {
        Disposals++;
        IsTranscribing = false;
        RetainResources = false;
        if (DisposeFailure is not null) throw DisposeFailure;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeIntelligence : INotesIntelligence, IMeetingVisualizationIntelligence
{
    public int Calls { get; private set; }
    public List<TranscriptionResult> Inputs { get; } = [];
    public Func<CancellationToken, Task>? OnSynthesize { get; set; }
    public Action<MeetingNotes>? PopulateNotes { get; set; }
    public Exception? Failure { get; set; }
    public int VisualizationCalls { get; private set; }
    public List<MeetingVisualizationDocument> VisualizationInputs { get; } = [];
    public MeetingVisualizationAnalysisResult VisualizationResult { get; set; } =
        new([], new([], [], []));
    public Exception? VisualizationFailure { get; set; }
    public Func<CancellationToken, Task>? OnVisualization { get; set; }
    public Func<MeetingVisualizationDocument, MeetingVisualizationAnalysisResult>?
        BuildVisualizationResult { get; set; }
    public async Task<MeetingNotes> SynthesizeAsync(string title, TranscriptionResult transcript, CancellationToken ct)
    {
        Calls++;
        Inputs.Add(transcript);
        if (OnSynthesize is not null) await OnSynthesize(ct);
        ct.ThrowIfCancellationRequested();
        if (Failure is not null) throw Failure;
        var notes = new MeetingNotes { MeetingTitle = title, Summary = "Synthetic generated summary", RawTranscript = transcript.FullText };
        PopulateNotes?.Invoke(notes);
        return notes;
    }

    public Task<string> SummarizeRecentAsync(string text, CancellationToken ct) => Task.FromResult("Synthetic summary");
    public Task<List<string>> GetOpenQuestionsAsync(string text, CancellationToken ct) => Task.FromResult(new List<string>());
    public Task<List<string>> GetActionsAsync(string text, CancellationToken ct) => Task.FromResult(new List<string>());
    public Task<(bool ok, string detail)> ValidateModelAsync(bool synthesis, IntelligenceSettings? settings, CancellationToken ct)
        => Task.FromResult((true, "Synthetic validation"));

    public async Task<MeetingVisualizationAnalysisResult> AnalyzeVisualizationAsync(
        MeetingVisualizationDocument document, CancellationToken ct)
    {
        VisualizationCalls++;
        VisualizationInputs.Add(document);
        if (OnVisualization is not null) await OnVisualization(ct);
        ct.ThrowIfCancellationRequested();
        if (VisualizationFailure is not null) throw VisualizationFailure;
        return BuildVisualizationResult?.Invoke(document) ?? VisualizationResult;
    }
}

internal sealed class FakeDetector : IMeetingDetector
{
    public event EventHandler<MeetingSession>? MeetingStarted;
    public event EventHandler<MeetingSession>? MeetingEnded;
    public MeetingSession? ActiveSession { get; private set; }
    public DateTimeOffset? SnoozedUntil { get; private set; }
    public List<string> ConfirmedSessions { get; } = [];
    public List<string> CompletedSessions { get; } = [];
    public void Complete(string sessionId)
    {
        if (ActiveSession?.Id != sessionId) return;
        CompletedSessions.Add(sessionId);
        End(ActiveSession);
    }
    public bool Confirm(string sessionId)
    {
        if (ActiveSession?.Id != sessionId) return false;
        ConfirmedSessions.Add(sessionId);
        return true;
    }
    public bool Dismiss(string sessionId, TimeSpan cooldown)
    {
        if (ActiveSession?.Id != sessionId) return false;
        End(ActiveSession);
        return true;
    }
    public void Snooze(TimeSpan duration) => SnoozedUntil = DateTimeOffset.UtcNow + duration;
    public void Resume() => SnoozedUntil = null;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public void Start(MeetingSession session) { ActiveSession = session; MeetingStarted?.Invoke(this, session); }
    public void End(MeetingSession session)
    {
        session.End();
        if (ActiveSession?.Id == session.Id) ActiveSession = null;
        MeetingEnded?.Invoke(this, session);
    }
    public void Dispose() { }
}

internal sealed class ObservedStorage(INotesStorage inner) : INotesStorage
{
    public List<MeetingNotes> Writes { get; } = [];
    public List<string> Paths { get; } = [];
    public Func<int, CancellationToken, Task>? BeforeSave { get; set; }
    public async Task<string> SaveAsync(MeetingNotes notes, CancellationToken ct)
    {
        Writes.Add(notes);
        if (BeforeSave is not null) await BeforeSave(Writes.Count, ct);
        var path = await inner.SaveAsync(notes, ct);
        Paths.Add(path);
        return path;
    }
}

internal sealed class ObservedVisualizationStorage(IMeetingVisualizationStorage inner) : IMeetingVisualizationStorage
{
    public int Saves { get; private set; }
    public int FailuresRemaining { get; set; }
    public Func<int, CancellationToken, Task>? BeforeSave { get; set; }
    public List<(string Path, MeetingVisualizationDocument Document)> Writes { get; } = [];

    public async Task SaveAsync(
        string markdownPath, MeetingVisualizationDocument document, CancellationToken cancellationToken)
    {
        Saves++;
        Writes.Add((markdownPath, document));
        if (BeforeSave is not null)
            await BeforeSave(Saves, cancellationToken);
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new IOException("Synthetic visualization storage failure.");
        }
        await inner.SaveAsync(markdownPath, document, cancellationToken);
    }

    public Task<MeetingVisualizationLoadResult> LoadAsync(
        string markdownPath, CancellationToken cancellationToken) =>
        inner.LoadAsync(markdownPath, cancellationToken);
}

internal sealed class PipelineFixture : IAsyncDisposable
{
    public TemporaryDirectory Directory { get; } = new();
    public FakeCapture Capture { get; } = new();
    public FakeTranscription Transcription { get; } = new();
    public FakeIntelligence Intelligence { get; } = new();
    public FakeDetector Detector { get; } = new();
    public ObservedStorage Storage { get; }
    public MeetingVisualizationSession Visualization { get; } = new();
    public AiUsageMonitor Usage { get; } = new();
    public ObservedVisualizationStorage VisualizationStorage { get; }
    public FilePendingVisualizationJournal Journal { get; }
    public MeetingPipeline Pipeline { get; }
    public List<PipelineStatus> Statuses { get; } = [];
    public List<NotesSavedInfo> Saved { get; } = [];

    public PipelineFixture(FakeCapture? second = null, Func<CancellationToken, Task<string?>>? localUser = null,
        Func<TimeSpan, CancellationToken, Task>? saveDelay = null,
        Func<DateTimeOffset>? getUtcNow = null,
        ObservedVisualizationStorage? visualizationStorage = null)
    {
        Storage = new(CoreChecks.CreateStorage(Directory.Path));
        VisualizationStorage = visualizationStorage ??
            new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage());
        Journal = new(System.IO.Path.Combine(Directory.Path, "pending-visualizations"));
        Pipeline = new(Detector, second is null ? [Capture] : [Capture, second],
            Transcription, Intelligence, Storage, Intelligence, Visualization, VisualizationStorage, new SpeakerResolver(),
            localUser ?? (_ => Task.FromResult<string?>(null)), NullLogger<MeetingPipeline>.Instance,
            saveDelay ?? ((_, _) => Task.CompletedTask), getUtcNow, Usage,
            pendingVisualizationJournal: Journal);
        Pipeline.StateChanged += (_, status) => Statuses.Add(status);
        Pipeline.NotesSaved += (_, saved) => Saved.Add(saved);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Pipeline.DisposeAsync(); }
        finally { Directory.Dispose(); }
    }
}
