namespace MeetingCapture.MeetingDetection;

/// <summary>
/// Detects when a meeting starts or ends and raises events accordingly.
/// Implementations must be safe to start/stop multiple times.
/// </summary>
public interface IMeetingDetector : IDisposable
{
    /// <summary>Raised when a new meeting session is detected.</summary>
    event EventHandler<MeetingSession> MeetingStarted;

    /// <summary>Raised when the detected meeting session ends.</summary>
    event EventHandler<MeetingSession> MeetingEnded;

    /// <summary>Currently active session, or null if no meeting is detected.</summary>
    MeetingSession? ActiveSession { get; }

    /// <summary>End of prompt snoozing, or null when prompts are enabled.</summary>
    DateTimeOffset? SnoozedUntil { get; }

    /// <summary>Pin the current detection to its process lifetime; reject stale session IDs.</summary>
    bool Confirm(string sessionId);

    /// <summary>Release a completed recording; suppress the same ongoing activity until its signal resets.</summary>
    void Complete(string sessionId);

    /// <summary>Dismiss an unconfirmed detection for a positive cooldown; reject stale IDs.</summary>
    bool Dismiss(string sessionId, TimeSpan cooldown);

    /// <summary>Suppress prompts for a positive duration without ending a confirmed meeting.</summary>
    void Snooze(TimeSpan duration);

    /// <summary>Clear snooze and dismissal cooldowns; reevaluate on the next poll.</summary>
    void Resume();

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
