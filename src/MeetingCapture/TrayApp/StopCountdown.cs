namespace MeetingCapture.TrayApp;

/// <summary>Countdown state shown on the Stop button while the user can still cancel.</summary>
internal readonly record struct StopCountdownPresentation(
    bool IsActive,
    int RemainingSeconds,
    double Progress,
    string ButtonText,
    string MenuText,
    string AccessibleName);

/// <summary>
/// Gives the user 5 seconds to cancel a Stop click. The UI calls <see cref="Tick"/> on a timer;
/// when the deadline passes, <see cref="Tick"/> returns true once and the caller stops the recording.
/// </summary>
internal sealed class StopCountdown
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private DateTimeOffset? _deadline;
    private object? _recording;

    public bool IsActive => _deadline is not null;
    public object? Recording => _recording;

    /// <summary>Starts the countdown, or cancels it if it is already running. Returns true when started.</summary>
    public bool Toggle(object? recording, DateTimeOffset now)
    {
        if (IsActive)
        {
            Clear();
            return false;
        }

        Start(recording, now);
        return true;
    }

    public void Start(object? recording, DateTimeOffset now)
    {
        if (IsActive) return;
        _recording = recording;
        _deadline = now + Duration;
    }

    public void Clear()
    {
        _deadline = null;
        _recording = null;
    }

    /// <summary>Clears the countdown if the recording it was started for has ended or changed.</summary>
    public bool ClearIfRecordingEnded(bool canStop, object? currentRecording)
    {
        if (!IsActive || (canStop && ReferenceEquals(currentRecording, _recording))) return false;
        Clear();
        return true;
    }

    /// <summary>Returns true exactly once, when the countdown reaches zero.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (_deadline is not { } deadline || now < deadline) return false;
        Clear();
        return true;
    }

    public StopCountdownPresentation Present(DateTimeOffset now)
    {
        if (_deadline is not { } deadline)
            return new(false, 0, 0, "Stop", "Stop Recording", "Stop recording");

        var remaining = deadline - now;
        var seconds = Math.Clamp((int)Math.Ceiling(remaining.TotalSeconds), 1, (int)Duration.TotalSeconds);
        var progress = Math.Clamp(1 - remaining.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
        var text = $"Stopping in {seconds}… (click to cancel)";
        return new(
            true,
            seconds,
            progress,
            text,
            text,
            $"Stopping recording in {seconds} second{(seconds == 1 ? string.Empty : "s")}. Click to cancel and keep recording.");
    }
}
