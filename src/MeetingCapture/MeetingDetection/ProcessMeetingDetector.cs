using System.Diagnostics;
using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.MeetingDetection;

/// <summary>A running process that may be a meeting app, and whether it currently has an active audio capture session.</summary>
internal sealed record MeetingProcessSnapshot(
    int ProcessId, string ProcessName, DateTimeOffset StartedAt, bool HasActiveCapture = false);

/// <summary>Suggests sustained microphone activity from known apps, never process presence alone.</summary>
public sealed class ProcessMeetingDetector : IMeetingDetector
{
    private readonly MeetingDetectionSettings _settings;
    private readonly ILogger<ProcessMeetingDetector> _logger;
    private readonly HashSet<string> _knownProcesses;
    private readonly Func<IReadOnlyList<MeetingProcessSnapshot>> _getProcesses;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<(int Pid, DateTimeOffset Start), DateTimeOffset> _dismissed = new();
    private readonly Dictionary<(int Pid, DateTimeOffset Start), DateTimeOffset> _captureSince = new();
    private readonly HashSet<(int Pid, DateTimeOffset Start)> _awaitSignalReset = new();
    private readonly TimeSpan _startDelay;
    private static readonly TimeSpan SignalLossGrace = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _signalLostAt;
    private HashSet<(int Pid, DateTimeOffset Start)> _observed = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private MeetingSession? _activeSession;
    private (int Pid, DateTimeOffset Start)? _activeProcess;
    private DateTimeOffset? _snoozedUntil;
    private bool _confirmed;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<MeetingSession>? MeetingStarted;

    /// <inheritdoc />
    public event EventHandler<MeetingSession>? MeetingEnded;

    /// <inheritdoc />
    public MeetingSession? ActiveSession
    {
        get { lock (_gate) return _activeSession; }
    }

    public DateTimeOffset? SnoozedUntil
    {
        get
        {
            lock (_gate)
                return _snoozedUntil > _timeProvider.GetUtcNow() ? _snoozedUntil : null;
        }
    }

    public ProcessMeetingDetector(
        IOptions<AppSettings> settings,
        ILogger<ProcessMeetingDetector> logger)
        : this(settings, logger, null, TimeProvider.System)
    {
    }

    internal ProcessMeetingDetector(
        IOptions<AppSettings> settings,
        ILogger<ProcessMeetingDetector> logger,
        Func<IReadOnlyList<MeetingProcessSnapshot>>? getProcesses,
        TimeProvider timeProvider,
        TimeSpan? startDelay = null)
    {
        _settings = settings.Value.MeetingDetection;
        _logger = logger;
        _timeProvider = timeProvider;
        _startDelay = startDelay ?? TimeSpan.FromSeconds(20);
        _knownProcesses = new HashSet<string>(
            _settings.KnownMeetingProcesses, StringComparer.OrdinalIgnoreCase);
        _getProcesses = getProcesses ?? ReadProcesses;
    }

    public bool Confirm(string sessionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeSession?.Id != sessionId || _activeSession is null || !_activeSession.IsActive)
            {
                _logger.LogDebug("Ignoring stale meeting confirmation: {SessionId}", sessionId);
                return false;
            }

            _confirmed = true;
            _activeSession.FreezeTitle();
            return true;
        }
    }

    public bool Dismiss(string sessionId, TimeSpan cooldown)
    {
        MeetingSession? ended;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            var until = GetDeadline(now, cooldown);
            if (_activeSession?.Id != sessionId || _activeProcess is null || _confirmed)
            {
                _logger.LogDebug("Ignoring stale or confirmed meeting dismissal: {SessionId}", sessionId);
                return false;
            }

            _dismissed[_activeProcess.Value] = until;
            ended = ClearActive(now);
        }
        NotifyEnded(ended);
        return true;
    }

    public void Complete(string sessionId)
    {
        MeetingSession? ended;
        lock (_gate)
        {
            if (_activeSession?.Id != sessionId) return;
            if (_activeProcess is { } process && _captureSince.ContainsKey(process))
                _awaitSignalReset.Add(process);
            ended = ClearActive(_timeProvider.GetUtcNow());
        }
        NotifyEnded(ended);
    }

    public void Snooze(TimeSpan duration)
    {
        MeetingSession? ended;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            _snoozedUntil = GetDeadline(now, duration);
            ended = _confirmed ? null : ClearActive(now);
        }
        NotifyEnded(ended);
    }

    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _snoozedUntil = null;
            _dismissed.Clear();
            _observed.Clear();
            _captureSince.Clear();
            _awaitSignalReset.Clear();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pollTask is { IsCompleted: false })
                    return;

                _cts?.Dispose();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = _cts.Token;
                _pollTask = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => StopCoreAsync(cancellationToken, dispose: false);

    private async Task StopCoreAsync(CancellationToken cancellationToken, bool dispose)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
                _disposed |= dispose;

            if (_cts is not null)
                await _cts.CancelAsync().ConfigureAwait(false);
            if (_pollTask is not null)
                await _pollTask.ConfigureAwait(false);

            MeetingSession? ended;
            lock (_gate)
            {
                ended = ClearActive(_timeProvider.GetUtcNow());
                _observed.Clear();
                _captureSince.Clear();
                _awaitSignalReset.Clear();
                _cts?.Dispose();
                _cts = null;
                _pollTask = null;
            }
            NotifyEnded(ended);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.PollIntervalSeconds));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PollOnce();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Meeting detection poll failed; retaining the previous detection");
                }

                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping polling is an expected lifecycle transition.
        }
    }

    internal void PollOnce()
    {
        // Read the entire snapshot before changing state: a failed enumeration is not an empty result.
        IReadOnlyList<MeetingProcessSnapshot> snapshot;
        try { snapshot = _getProcesses(); }
        catch
        {
            // Unknown intervals cannot count toward sustained evidence, but are not proof a call ended.
            lock (_gate) _captureSince.Clear();
            throw;
        }
        var processes = snapshot
            .Where(process => _knownProcesses.Contains(process.ProcessName))
            .OrderByDescending(process => process.StartedAt)
            .ThenBy(process => process.ProcessId)
            .ToArray();
        var present = processes.Select(Identity).ToHashSet();
        MeetingSession? ended = null;
        MeetingSession? started = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            var now = _timeProvider.GetUtcNow();
            var capturing = processes.Where(process => process.HasActiveCapture).Select(Identity).ToHashSet();
            foreach (var key in _captureSince.Keys.ToArray())
                if (!capturing.Contains(key)) _captureSince.Remove(key);
            _awaitSignalReset.RemoveWhere(key => !capturing.Contains(key));
            foreach (var key in capturing)
                _captureSince.TryAdd(key, now);
            var qualified = capturing.Where(key => now - _captureSince[key] >= _startDelay &&
                !_awaitSignalReset.Contains(key)).ToHashSet();
            foreach (var key in _dismissed.Keys.ToArray())
                if (!present.Contains(key) || _dismissed[key] <= now)
                    _dismissed.Remove(key);

            if (_activeProcess is not null && !present.Contains(_activeProcess.Value))
                ended = ClearActive(now);
            else if (_activeProcess is { } active)
            {
                if (capturing.Contains(active)) _signalLostAt = null;
                else
                {
                    _signalLostAt ??= now;
                    // A muted call can close its microphone stream. Only the user (or process exit)
                    // ends a confirmed recording; signal loss expires unconfirmed suggestions.
                    if (!_confirmed && now - _signalLostAt >= SignalLossGrace)
                        ended = ClearActive(now);
                }
            }

            if (!_confirmed && !(_snoozedUntil > now))
            {
                var candidate = processes.FirstOrDefault(process =>
                    qualified.Contains(Identity(process)) &&
                    !_dismissed.ContainsKey(Identity(process)) &&
                    (_activeSession is null || !_observed.Contains(Identity(process))));
                if (candidate is not null && _activeProcess != Identity(candidate))
                {
                    ended ??= ClearActive(now);
                    _activeProcess = Identity(candidate);
                    started = _activeSession = new MeetingSession
                    {
                        Title = MeetingTitleResolver.FallbackTitle(candidate.ProcessName),
                        SourceProcess = candidate.ProcessName,
                        StartedAt = now,
                        SignalStartedAt = _captureSince[Identity(candidate)]
                    };
                }
            }
            _observed = qualified;
        }

        NotifyEnded(ended);
        if (started is not null)
        {
            lock (_gate)
                if (!ReferenceEquals(_activeSession, started))
                    return;
            _logger.LogInformation("Sustained meeting-app capture suggestion: {SessionId}", started.Id);
            MeetingStarted?.Invoke(this, started);
        }
    }

    private IReadOnlyList<MeetingProcessSnapshot> ReadProcesses()
    {
        var result = new List<MeetingProcessSnapshot>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var name = process.ProcessName;
                    if (_knownProcesses.Contains(name))
                        result.Add(new MeetingProcessSnapshot(
                            process.Id, name, new DateTimeOffset(process.StartTime).ToUniversalTime()));
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and reading its identity.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
        var capturing = result.Count == 0 ? [] : CaptureActivityReader.ReadActiveProcessIds();
        return result.Select(process => process with { HasActiveCapture = capturing.Contains(process.ProcessId) }).ToArray();
    }

    private MeetingSession? ClearActive(DateTimeOffset now)
    {
        var ended = _activeSession;
        ended?.End(now);
        _activeSession = null;
        _activeProcess = null;
        _confirmed = false;
        _signalLostAt = null;
        return ended;
    }

    private void NotifyEnded(MeetingSession? ended)
    {
        if (ended is null)
            return;
        _logger.LogInformation("Meeting detection ended: {SessionId}", ended.Id);
        MeetingEnded?.Invoke(this, ended);
    }

    private static (int Pid, DateTimeOffset Start) Identity(MeetingProcessSnapshot process)
        => (process.ProcessId, process.StartedAt);

    private static DateTimeOffset GetDeadline(DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        return now.Add(duration);
    }

    public void Dispose() => StopCoreAsync(CancellationToken.None, dispose: true).GetAwaiter().GetResult();
}
