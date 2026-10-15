namespace MeetingCapture.TrayApp;

public sealed class WorkIqConnectionStatus
{
    private long _authRevision = -1;
    public bool CalendarConnected { get; private set; }
    public bool IsRefreshing { get; private set; }
    public DateTimeOffset? LastSuccessfulRefresh { get; private set; }
    public string Message { get; private set; } = "WorkIQ signed out. Sign in to connect your calendar.";

    public static (bool SignIn, bool SignOut, bool Cancel) Actions(bool authenticated, bool busy, bool pending)
        => (!authenticated && !busy, authenticated && !busy, pending);

    public WorkIqControlState Controls(bool authenticated, bool signInBusy, bool pending,
        bool cancelling, bool restoring)
        => new(
            Actions(authenticated, signInBusy || pending, pending),
            pending && !cancelling,
            restoring ? "Restoring WorkIQ..."
                : cancelling ? "Cancelling sign-in..." : "WorkIQ sign-in in progress",
            restoring ? "Restoring only this app's protected WorkIQ sign-in..."
                : signInBusy && !pending
                    ? Message + " Waiting for authentication cleanup; recording is available."
                    : Message);

    public bool AuthenticationChanged(bool authenticated, long revision)
    {
        if (revision <= _authRevision) return false;
        _authRevision = revision;
        LastSuccessfulRefresh = null;
        SetMessage(!authenticated ? "WorkIQ signed out. Sign in to connect your calendar."
            : "WorkIQ signed in; connecting calendar...");
        return true;
    }

    public void SetMessage(string message)
    {
        CalendarConnected = false;
        IsRefreshing = false;
        Message = message;
    }

    public void BeginCalendar(int cachedMeetingCount = 0)
    {
        if (cachedMeetingCount < 0) throw new ArgumentOutOfRangeException(nameof(cachedMeetingCount));
        IsRefreshing = true;
        Message = CalendarConnected ? "WorkIQ connected; refreshing calendar..."
            : "WorkIQ signed in; connecting calendar...";
        if (cachedMeetingCount > 0)
            Message += $" Showing {cachedMeetingCount} previously loaded meetings until refresh completes; times may be outdated.";
        if (LastSuccessfulRefresh is { } at)
            Message += $" Last successful refresh {at.LocalDateTime:t}.";
    }

    public void CalendarSucceeded(int count, DateTimeOffset at)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        CalendarConnected = true;
        IsRefreshing = false;
        LastSuccessfulRefresh = at;
        Message = $"WorkIQ connected: {count} calendar events today. Last successful refresh {at.LocalDateTime:t}.";
    }

    public void CalendarFailed(string detail)
        => SetMessage("WorkIQ calendar refresh failed: " + detail +
            (LastSuccessfulRefresh is { } at ? $" Last successful refresh {at.LocalDateTime:t}." : ""));
}

public readonly record struct WorkIqControlState(
    (bool SignIn, bool SignOut, bool Cancel) Actions,
    bool CancelEnabled,
    string ProgressText,
    string StateText);
