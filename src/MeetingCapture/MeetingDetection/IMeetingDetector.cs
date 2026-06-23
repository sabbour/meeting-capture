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

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
