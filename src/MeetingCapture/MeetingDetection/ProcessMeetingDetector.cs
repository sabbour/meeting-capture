using System.Diagnostics;
using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.MeetingDetection;

/// <summary>A running process that may be a meeting app, and whether it currently has an active audio capture session.</summary>
internal sealed record MeetingProcessSnapshot(
    int ProcessId, string ProcessName, DateTimeOffset StartedAt, bool HasActiveCapture = false);

/// <summary>Suggests meetings from known apps with a sustained active microphone capture session, not process presence alone.</summary>
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
            // A prompt can outlive the process signal that created it, so confirm only the current active session.
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
            // The key includes process start time as well as PID, so a later process reusing the PID
            // is not hidden by this dismissal.
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
            // Keep a confirmed recording from being suggested again while the same capture session
            // continues; PollOnce removes this guard after the signal resets.
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
            // A snooze hides suggestions, but does not end a meeting the user already confirmed.
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
            // Discard old evidence so the next poll must observe a fresh sustained capture period.
            _dismissed.Clear();
            _observed.Clear();
            _captureSince.Clear();
            _awaitSignalReset.Clear();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Serialize start and stop so a canceled poll loop cannot overlap a replacement loop.
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
                // Polling is background work; never run process enumeration on a UI caller.
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

            // Await the poll loop before clearing its state or releasing its cancellation source.
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
                    // A failed poll is unknown evidence, not proof that the meeting ended.
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
            // Restart the sustained-capture timer after the next successful snapshot.
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
            // Reset timers when a capture session disappears; only continuous observed capture
            // for _startDelay qualifies as a suggestion.
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
                // Keep one current suggestion, and skip processes the user already saw while their
                // capture signal has not reset.
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
            // State changed under the lock; recheck before notifying because another caller may
            // have dismissed or stopped this session in the meantime.
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
                    // Read start time only for configured apps; unrelated processes need no more inspection.
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
            // Process objects hold native handles and must be released even if one identity read fails.
            foreach (var process in processes)
                process.Dispose();
        }
        // Avoid querying all audio endpoints when no known meeting app is running.
        var capturing = result.Count == 0 ? [] : CaptureActivityReader.ReadActiveProcessIds();
        return result.Select(process => process with { HasActiveCapture = capturing.Contains(process.ProcessId) }).ToArray();
    }

    private MeetingSession? ClearActive(DateTimeOffset now)
    {
        var ended = _activeSession;
        // Reset all state tied to the old process before a later poll selects another candidate.
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
        // Raise events after the detector lock is released so subscribers can safely call back into it.
        _logger.LogInformation("Meeting detection ended: {SessionId}", ended.Id);
        MeetingEnded?.Invoke(this, ended);
    }

    private static (int Pid, DateTimeOffset Start) Identity(MeetingProcessSnapshot process)
        => (process.ProcessId, process.StartedAt);

    private static DateTimeOffset GetDeadline(DateTimeOffset now, TimeSpan duration)
    {
        // Requiring a positive interval avoids storing a cooldown that expires immediately.
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        return now.Add(duration);
    }

    public void Dispose() => StopCoreAsync(CancellationToken.None, dispose: true).GetAwaiter().GetResult();
}
