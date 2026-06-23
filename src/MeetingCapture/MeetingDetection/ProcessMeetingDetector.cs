using System.Diagnostics;
using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.MeetingDetection;

/// <summary>
/// Detects meetings by polling for known meeting application processes.
/// This is the Phase 1 detection strategy. Vasquez/Bishop can layer in
/// Windows Audio Session API (WASAPI) activity detection in a later phase.
/// </summary>
public sealed class ProcessMeetingDetector : IMeetingDetector
{
    private readonly MeetingDetectionSettings _settings;
    private readonly ILogger<ProcessMeetingDetector> _logger;
    private readonly HashSet<string> _knownProcesses;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private MeetingSession? _activeSession;

    public event EventHandler<MeetingSession>? MeetingStarted;
    public event EventHandler<MeetingSession>? MeetingEnded;

    public MeetingSession? ActiveSession => _activeSession;

    public ProcessMeetingDetector(
        IOptions<AppSettings> settings,
        ILogger<ProcessMeetingDetector> logger)
    {
        _settings = settings.Value.MeetingDetection;
        _logger = logger;
        _knownProcesses = new HashSet<string>(
            _settings.KnownMeetingProcesses,
            StringComparer.OrdinalIgnoreCase);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcessMeetingDetector starting. Watching: {Processes}",
            string.Join(", ", _knownProcesses));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_pollTask is not null)
                await _pollTask.ConfigureAwait(false);
        }

        if (_activeSession is not null)
        {
            _activeSession.End();
            MeetingEnded?.Invoke(this, _activeSession);
            _activeSession = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Max(1, _settings.PollIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var detected = DetectActiveMeetingProcess();

                if (detected is not null && _activeSession is null)
                {
                    _activeSession = new MeetingSession
                    {
                        Title = $"{detected} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
                        SourceProcess = detected
                    };
                    _logger.LogInformation("Meeting started: {Session}", _activeSession);
                    MeetingStarted?.Invoke(this, _activeSession);
                }
                else if (detected is null && _activeSession is not null)
                {
                    _activeSession.End();
                    _logger.LogInformation("Meeting ended: {Session}", _activeSession);
                    MeetingEnded?.Invoke(this, _activeSession);
                    _activeSession = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during meeting process detection poll");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private string? DetectActiveMeetingProcess()
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if (_knownProcesses.Contains(proc.ProcessName))
                        return proc.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and name read — safe to skip.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process enumeration failed");
        }

        return null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
