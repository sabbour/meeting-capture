namespace MeetingCapture.MeetingDetection;

/// <summary>Represents an active or recently ended meeting session.</summary>
public sealed class MeetingSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    private string _title = "Meeting";
    private readonly object _titleGate = new();
    private bool _titleFrozen;
    private MeetingTitleCandidate? _calendarMeeting;
    /// <summary>Resolved calendar subject, or an honest detected-app fallback.</summary>
    public string Title { get => _title; init => _title = value; }
    public string TitleSource { get; private set; } = "Calendar confirmation is not available.";
    public MeetingTitleCandidate? CalendarMeeting { get { lock (_titleGate) return _calendarMeeting; } }
    public IReadOnlyList<MeetingInvitee> InviteParticipants
    {
        get { lock (_titleGate) return MeetingTitleResolver.InviteParticipants(_calendarMeeting); }
    }

    public bool TrySetPendingTitle(string? pendingSessionId, string title,
        string source = "One calendar event matches the time and meeting platform.")
    {
        lock (_titleGate)
        {
            // Only the live prompt may update a title, and confirmation freezes it so later calendar
            // lookups cannot rename a recording that has already started.
            if (_titleFrozen || pendingSessionId != Id || !IsActive || string.IsNullOrWhiteSpace(title))
                return false;
            _title = title;
            TitleSource = source;
            return true;
        }
    }

    public bool TryAttachCalendarMeeting(string? pendingSessionId, MeetingTitleCandidate meeting)
    {
        lock (_titleGate)
        {
            // Keep the attached event consistent with the same session and eligibility rules used
            // when the title is resolved.
            if (_titleFrozen || pendingSessionId != Id || !IsActive ||
                !MeetingTitleResolver.IsEligibleOnlineMeeting(meeting))
                return false;
            _calendarMeeting = meeting;
            return true;
        }
    }

    // Called when the user confirms this session or recording begins.
    internal void FreezeTitle() { lock (_titleGate) _titleFrozen = true; }

    /// <summary>Name of the process that triggered detection (e.g., "Teams").</summary>
    public string SourceProcess { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset SignalStartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsActive => EndedAt is null;

    public void End() => End(DateTimeOffset.UtcNow);

    internal void End(DateTimeOffset endedAt) { lock (_titleGate) EndedAt ??= endedAt; }

    public TimeSpan Duration =>
        (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public override string ToString() =>
        $"[{Id[..8]}] {Title} via {SourceProcess} — started {StartedAt:HH:mm:ss}";
}
