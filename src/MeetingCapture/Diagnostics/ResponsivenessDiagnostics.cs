using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MeetingCapture.Diagnostics;

internal enum ResponsivenessPhase
{
    ApplicationStart,
    AppInitialize,
    Startup,
    ConfigurationRead,
    ServiceRegistration,
    ServiceResolution,
    SettingsValidation,
    NotesDirectoryCheck,
    WindowConstruction,
    WindowInitialize,
    SettingsInitialize,
    WindowLoad,
    TrayCreate,
    WindowPosition,
    WindowShow,
    WindowActivate,
    WindowForeground,
    WindowHide,
    DetectorStart,
    VisualizationRecovery,
    WorkIqRestoreWait,
    WorkIqRestore,
    WorkIqCacheLoad,
    WorkIqCachedAccounts,
    WorkIqSilentAuthentication,
    WorkIqConnect,
    WorkIqCalendar,
    WorkIqCacheSave,
    WorkIqRelatedMeetings,
    WorkIqRelatedTeamsMessages,
    WorkIqRelatedEmail,
    WorkIqRelatedOther
}

internal enum ResponsivenessEventKind
{
    ProcessStarted,
    UiMonitoringStarted,
    PhaseBegin,
    PhaseEnd,
    PhaseFailure,
    UiStallStarted,
    UiStallOngoing,
    UiRecovered,
    UiCheckpoint,
    UiProbeRejected,
    UiProbePostDelayed,
    UiProbePostResumed,
    WriterRecovered,
    ProcessStopping
}

internal enum UiProbePostState
{
    Pending,
    Posting,
    Accepted,
    Rejected
}

public sealed class ResponsivenessDiagnostics : IDisposable
{
    internal const int QueueCapacity = 128;
    internal const int FileSizeLimit = 1024 * 1024;
    internal const string FileName = "responsiveness.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly uint _uiThreadId;
    private readonly string? _directory;
    private readonly string _buildVersion = typeof(ResponsivenessDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";
    private readonly Guid _buildId = typeof(ResponsivenessDiagnostics).Module.ModuleVersionId;
    private readonly Channel<LogEntry> _events = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly AutoResetEvent _writeReady = new(false);
    private readonly AutoResetEvent _postReady = new(false);
    private readonly Task _writer;
    private Task? _monitor;
    private Task? _poster;
    private Action<string>? _reportFailure;
    private PhaseScope? _activeUiPhase;
    private int _lastUiPhase = -1;
    private int _lastBackgroundPhase = -1;
    private UiState _state = new();
    private long _sequence;
    private long _operation;
    private long _dropped;
    private long _written;
    private int _stopped;

    internal ResponsivenessDiagnostics(
        uint uiThreadId, string? directory = null, TimeProvider? time = null,
        Action<string>? reportFailure = null)
    {
        _uiThreadId = uiThreadId;
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _started = _time.GetTimestamp();
        _reportFailure = reportFailure;
        _stopToken = _stop.Token;
        _writer = Task.Factory.StartNew(Write, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Enqueue(Snapshot(ResponsivenessEventKind.ProcessStarted));
    }

    internal long DroppedEvents => Interlocked.Read(ref _dropped);
    internal long WrittenEvents => Interlocked.Read(ref _written);
    internal int PendingEvents => _events.Reader.Count;
    internal Task Completion => Task.WhenAll(
        _writer, _monitor ?? Task.CompletedTask, _poster ?? Task.CompletedTask);

    internal void SetFailureReporter(Action<string> reportFailure)
        => Volatile.Write(ref _reportFailure, reportFailure);

    internal void SetWindowState(bool? visible = null, bool? active = null)
    {
        var state = Volatile.Read(ref _state);
        Volatile.Write(ref _state, state with
        {
            WindowVisible = visible ?? state.WindowVisible,
            WindowActive = active ?? state.WindowActive
        });
    }

    internal void SetWorkIqState(bool authenticated, bool busy, bool calendarBusy)
    {
        var state = Volatile.Read(ref _state);
        Volatile.Write(ref _state, state with
        {
            WorkIqAuthenticated = authenticated,
            WorkIqBusy = busy,
            CalendarBusy = calendarBusy
        });
    }

    internal void SetCaptureState(bool active)
    {
        var state = Volatile.Read(ref _state);
        Volatile.Write(ref _state, state with { CaptureActive = active });
    }

    internal PhaseScope BeginUiPhase(ResponsivenessPhase phase) => BeginPhase(phase, true);
    internal PhaseScope BeginBackgroundPhase(ResponsivenessPhase phase) => BeginPhase(phase, false);

    private PhaseScope BeginPhase(ResponsivenessPhase phase, bool ui)
    {
        var scope = new PhaseScope(this, phase, Interlocked.Increment(ref _operation),
            _time.GetTimestamp(), ui, ui ? Volatile.Read(ref _activeUiPhase) : null);
        if (ui) Volatile.Write(ref _activeUiPhase, scope);
        Enqueue(Snapshot(ResponsivenessEventKind.PhaseBegin) with
        {
            Phase = phase, OperationId = scope.Id
        });
        return scope;
    }

    internal void StartMonitor(Func<Action, bool> post)
    {
        if (_monitor is not null || Volatile.Read(ref _stopped) != 0) return;
        Enqueue(Snapshot(ResponsivenessEventKind.UiMonitoringStarted));
        var heartbeat = new UiHeartbeat(_time, post, observation =>
            Enqueue(Snapshot(observation.Kind) with
            {
                ProbeSentUtc = observation.ProbeSentUtc,
                LastUiResponseUtc = observation.LastUiResponseUtc,
                UiResponseDelayMs = observation.Delay.TotalMilliseconds,
                ExceptionType = observation.ExceptionType,
                ProbePostState = observation.ProbePostState,
                ProbePostStartedUtc = observation.ProbePostStartedUtc,
                ProbePostCompletedUtc = observation.ProbePostCompletedUtc,
                ProbePostDurationMs = observation.ProbePostDurationMs,
                MonitorIntervalMs = observation.MonitorIntervalMs
            }));
        _poster = Task.Factory.StartNew(() =>
        {
            while (!_stopToken.IsCancellationRequested)
            {
                _postReady.WaitOne();
                if (_stopToken.IsCancellationRequested) return;
                heartbeat.PostPending();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _monitor = Task.Factory.StartNew(() =>
        {
            while (!_stopToken.IsCancellationRequested)
            {
                if (heartbeat.Tick()) _postReady.Set();
                if (_stopToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) return;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        Enqueue(Snapshot(ResponsivenessEventKind.ProcessStopping), stopping: true);
        _stop.Cancel();
        _events.Writer.TryComplete();
        _writeReady.Set();
        _postReady.Set();
        _ = Completion.ContinueWith(_ =>
        {
            _writeReady.Dispose();
            _postReady.Dispose();
            _stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private LogEntry Snapshot(ResponsivenessEventKind kind)
    {
        var now = _time.GetTimestamp();
        var active = Volatile.Read(ref _activeUiPhase);
        var state = Volatile.Read(ref _state);
        var uiPhase = Volatile.Read(ref _lastUiPhase);
        var backgroundPhase = Volatile.Read(ref _lastBackgroundPhase);
        return new LogEntry(
            1, Interlocked.Increment(ref _sequence), _time.GetUtcNow(),
            _time.GetElapsedTime(_started, now).TotalMilliseconds, kind,
            Environment.ProcessId, _uiThreadId, _buildVersion, _buildId)
        {
            ActiveUiPhase = active?.Phase,
            ActiveUiPhaseElapsedMs = active is null ? null : _time.GetElapsedTime(active.Started, now).TotalMilliseconds,
            LastCompletedUiPhase = uiPhase < 0 ? null : (ResponsivenessPhase)uiPhase,
            LastCompletedBackgroundPhase = backgroundPhase < 0 ? null : (ResponsivenessPhase)backgroundPhase,
            WindowVisible = state.WindowVisible,
            WindowActive = state.WindowActive,
            WorkIqAuthenticated = state.WorkIqAuthenticated,
            WorkIqBusy = state.WorkIqBusy,
            CalendarBusy = state.CalendarBusy,
            CaptureActive = state.CaptureActive
        };
    }

    private void Enqueue(LogEntry entry, bool stopping = false)
    {
        if (!stopping && Volatile.Read(ref _stopped) != 0) return;
        if (_events.Writer.TryWrite(entry))
        {
            try { _writeReady.Set(); }
            catch (ObjectDisposedException) when (_stopToken.IsCancellationRequested) { }
        }
        else Interlocked.Increment(ref _dropped);
    }

    private void ReportFailure(Exception error)
    {
        var type = error.GetType().Name;
        if (Volatile.Read(ref _reportFailure) is { } report)
            report(type);
        else
            Trace.TraceWarning("Cannot write the Meeting Capture window-response log ({0}).", type);
    }

    private void Write()
    {
        FileStream? stream = null;
        var directory = _directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingCapture", "Diagnostics");
        var path = Path.Combine(directory, FileName);
        var hadFailure = false;
        var opened = false;
        try
        {
            while (true)
            {
                while (_events.Reader.TryRead(out var entry))
                {
                    while (true)
                    {
                        try
                        {
                            if (hadFailure)
                            {
                                WriteEntry(Snapshot(ResponsivenessEventKind.WriterRecovered));
                                hadFailure = false;
                            }
                            WriteEntry(entry);
                            break;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            ReportFailure(error);
                            hadFailure = true;
                            CloseStream();
                            if (_stopToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))) return;
                        }
                    }
                }
                if (_stopToken.IsCancellationRequested) return;
                _writeReady.WaitOne();
            }
        }
        finally
        {
            CloseStream();
        }

        void WriteEntry(LogEntry entry)
        {
            if (stream is null)
            {
                Directory.CreateDirectory(directory);
                if (!opened && File.Exists(path)) RotateFiles();
                stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.None);
                opened = true;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry with { DroppedEvents = DroppedEvents }, JsonOptions);
            if (stream.Length + bytes.Length + 1 > FileSizeLimit)
            {
                stream.Dispose();
                stream = null;
                RotateFiles();
                stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.None);
            }
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            stream.Flush();
            Interlocked.Increment(ref _written);
        }

        void RotateFiles()
        {
            var previous = Path.Combine(directory, "responsiveness.1.jsonl");
            if (File.Exists(previous))
                File.Move(previous, Path.Combine(directory, "responsiveness.2.jsonl"), overwrite: true);
            File.Move(path, previous, overwrite: true);
        }

        void CloseStream()
        {
            if (stream is null) return;
            try { stream.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ReportFailure(error); }
            finally { stream = null; }
        }
    }

    internal sealed class PhaseScope(
        ResponsivenessDiagnostics owner, ResponsivenessPhase phase,
        long id, long started, bool ui, PhaseScope? parent) : IDisposable
    {
        internal ResponsivenessPhase Phase { get; } = phase;
        internal long Id { get; } = id;
        internal long Started { get; } = started;
        private int _ended;

        internal void Fail(Exception error)
            => owner.Enqueue(owner.Snapshot(ResponsivenessEventKind.PhaseFailure) with
            {
                Phase = Phase, OperationId = Id, ExceptionType = error.GetType().Name
            });

        // Detail must be a fixed classification (for example a failure enum name), never message text.
        internal void Fail(Exception error, string detail)
            => owner.Enqueue(owner.Snapshot(ResponsivenessEventKind.PhaseFailure) with
            {
                Phase = Phase, OperationId = Id, ExceptionType = $"{error.GetType().Name}:{detail}"
            });

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            if (ui)
            {
                Interlocked.CompareExchange(ref owner._activeUiPhase, parent, this);
                Volatile.Write(ref owner._lastUiPhase, (int)Phase);
            }
            else Volatile.Write(ref owner._lastBackgroundPhase, (int)Phase);
            owner.Enqueue(owner.Snapshot(ResponsivenessEventKind.PhaseEnd) with
            {
                Phase = Phase,
                OperationId = Id,
                DurationMs = owner._time.GetElapsedTime(Started).TotalMilliseconds
            });
        }
    }

    private sealed record UiState(
        bool? WindowVisible = null, bool? WindowActive = null,
        bool? WorkIqAuthenticated = null, bool? WorkIqBusy = null,
        bool? CalendarBusy = null, bool? CaptureActive = null);

    private sealed record LogEntry(
        int SchemaVersion, long Sequence, DateTimeOffset Utc, double ElapsedMs,
        ResponsivenessEventKind Kind, int ProcessId, uint UiThreadId, string BuildVersion, Guid BuildId)
    {
        public ResponsivenessPhase? Phase { get; init; }
        public long? OperationId { get; init; }
        public double? DurationMs { get; init; }
        public string? ExceptionType { get; init; }
        public ResponsivenessPhase? ActiveUiPhase { get; init; }
        public double? ActiveUiPhaseElapsedMs { get; init; }
        public ResponsivenessPhase? LastCompletedUiPhase { get; init; }
        public ResponsivenessPhase? LastCompletedBackgroundPhase { get; init; }
        public bool? WindowVisible { get; init; }
        public bool? WindowActive { get; init; }
        public bool? WorkIqAuthenticated { get; init; }
        public bool? WorkIqBusy { get; init; }
        public bool? CalendarBusy { get; init; }
        public bool? CaptureActive { get; init; }
        public DateTimeOffset? ProbeSentUtc { get; init; }
        public DateTimeOffset? LastUiResponseUtc { get; init; }
        public double? UiResponseDelayMs { get; init; }
        public UiProbePostState? ProbePostState { get; init; }
        public DateTimeOffset? ProbePostStartedUtc { get; init; }
        public DateTimeOffset? ProbePostCompletedUtc { get; init; }
        public double? ProbePostDurationMs { get; init; }
        public double? MonitorIntervalMs { get; init; }
        public long DroppedEvents { get; init; }
    }
}

internal sealed class UiHeartbeat(
    TimeProvider time, Func<Action, bool> post, Action<UiHeartbeat.Observation> record)
{
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);
    private Probe? _pending;
    private DateTimeOffset? _lastResponseUtc;
    private TimeSpan _lastDelay;
    private long _lastCheckpoint = time.GetTimestamp();
    private long _lastTick = time.GetTimestamp();
    private long _lastStallReport;
    private long _lastPostReport;
    private bool _stallReported;
    private bool _postDelayed;
    private bool _rejected;

    internal bool Tick()
    {
        var now = time.GetTimestamp();
        var utc = time.GetUtcNow();
        var interval = time.GetElapsedTime(_lastTick, now);
        _lastTick = now;
        var responded = false;
        Probe? observedProbe = null;
        if (Volatile.Read(ref _pending) is { } probe)
        {
            observedProbe = probe;
            var response = Volatile.Read(ref probe.Response);
            var delivery = Volatile.Read(ref probe.Delivery);
            var delay = time.GetElapsedTime(probe.Sent, response?.Timestamp ?? now);
            if (delivery.State is UiProbePostState.Pending or UiProbePostState.Posting)
            {
                if (time.GetElapsedTime(probe.Sent, now) >= StallThreshold &&
                    (!_postDelayed || time.GetElapsedTime(_lastPostReport, now) >= TimeSpan.FromSeconds(10)))
                {
                    _postDelayed = true;
                    _lastPostReport = now;
                    Record(ResponsivenessEventKind.UiProbePostDelayed, probe, delivery,
                        time.GetElapsedTime(probe.Sent, now), response?.Utc ?? _lastResponseUtc);
                }
                return false;
            }
            if (_postDelayed && delivery.State == UiProbePostState.Accepted)
                Record(ResponsivenessEventKind.UiProbePostResumed, probe, delivery,
                    delay, response?.Utc ?? _lastResponseUtc);
            _postDelayed = false;
            if (delivery.State == UiProbePostState.Rejected)
            {
                if (!_rejected)
                    Record(ResponsivenessEventKind.UiProbeRejected, probe, delivery,
                        TimeSpan.Zero, _lastResponseUtc);
                _rejected = true;
                Volatile.Write(ref _pending, null);
            }
            else
            {
                _rejected = false;
                var uiWait = time.GetElapsedTime(delivery.Completed, response?.Timestamp ?? now);
                if (uiWait >= StallThreshold && !_stallReported)
                {
                    _stallReported = true;
                    _lastStallReport = now;
                    Record(ResponsivenessEventKind.UiStallStarted, probe, delivery, delay, _lastResponseUtc);
                }
                if (response is not null)
                {
                    responded = true;
                    _lastResponseUtc = response.Utc;
                    _lastDelay = delay;
                    if (_stallReported)
                        Record(ResponsivenessEventKind.UiRecovered, probe, delivery, delay, response.Utc);
                    _stallReported = false;
                    Volatile.Write(ref _pending, null);
                }
                else
                {
                    if (_stallReported && time.GetElapsedTime(_lastStallReport, now) >= TimeSpan.FromSeconds(10))
                    {
                        _lastStallReport = now;
                        Record(ResponsivenessEventKind.UiStallOngoing, probe, delivery, delay, _lastResponseUtc);
                    }
                    return false;
                }
            }
        }

        if (responded && observedProbe is { } acknowledged &&
            time.GetElapsedTime(_lastCheckpoint, now) >= TimeSpan.FromSeconds(30))
        {
            _lastCheckpoint = now;
            Record(ResponsivenessEventKind.UiCheckpoint, acknowledged,
                Volatile.Read(ref acknowledged.Delivery), _lastDelay, _lastResponseUtc);
        }
        var next = new Probe(now, utc);
        Volatile.Write(ref _pending, next);
        return true;

        void Record(ResponsivenessEventKind kind, Probe current, Delivery delivery,
            TimeSpan delay, DateTimeOffset? responseUtc)
            => record(new(kind, utc,
                kind == ResponsivenessEventKind.UiCheckpoint ? null : current.SentUtc,
                responseUtc, delay, delivery.ExceptionType)
            {
                ProbePostState = delivery.State,
                ProbePostStartedUtc = delivery.StartedUtc,
                ProbePostCompletedUtc = delivery.CompletedUtc,
                ProbePostDurationMs = delivery.Started is { } started
                    ? time.GetElapsedTime(started,
                        delivery.State is UiProbePostState.Accepted or UiProbePostState.Rejected
                            ? delivery.Completed : now).TotalMilliseconds : null,
                MonitorIntervalMs = interval.TotalMilliseconds
            });
    }

    internal void PostPending()
    {
        var next = Volatile.Read(ref _pending);
        if (next is null || Interlocked.CompareExchange(ref next.PostStarted, 1, 0) != 0) return;
        var started = time.GetTimestamp();
        var startedUtc = time.GetUtcNow();
        Volatile.Write(ref next.Delivery, new(UiProbePostState.Posting, started, startedUtc));
        string? exceptionType = null;
        bool accepted;
        try
        {
            accepted = post(() => Volatile.Write(ref next.Response,
                new Response(time.GetTimestamp(), time.GetUtcNow())));
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            exceptionType = error.GetType().Name;
            accepted = false;
        }
        Volatile.Write(ref next.Delivery, new(
            accepted ? UiProbePostState.Accepted : UiProbePostState.Rejected,
            started, startedUtc, time.GetTimestamp(), time.GetUtcNow(), exceptionType));
    }

    internal sealed record Observation(
        ResponsivenessEventKind Kind, DateTimeOffset Utc, DateTimeOffset? ProbeSentUtc,
        DateTimeOffset? LastUiResponseUtc, TimeSpan Delay, string? ExceptionType = null)
    {
        internal UiProbePostState? ProbePostState { get; init; }
        internal DateTimeOffset? ProbePostStartedUtc { get; init; }
        internal DateTimeOffset? ProbePostCompletedUtc { get; init; }
        internal double? ProbePostDurationMs { get; init; }
        internal double? MonitorIntervalMs { get; init; }
    }

    private sealed class Probe(long sent, DateTimeOffset sentUtc)
    {
        internal long Sent { get; } = sent;
        internal DateTimeOffset SentUtc { get; } = sentUtc;
        internal int PostStarted;
        internal Delivery Delivery = new(UiProbePostState.Pending);
        internal Response? Response;
    }

    private sealed record Delivery(
        UiProbePostState State, long? Started = null, DateTimeOffset? StartedUtc = null,
        long Completed = 0, DateTimeOffset? CompletedUtc = null, string? ExceptionType = null);

    private sealed record Response(long Timestamp, DateTimeOffset Utc);
}
