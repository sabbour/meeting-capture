using MeetingCapture.MeetingDetection;

namespace MeetingCapture.TrayApp;

/// <summary>Chooses and orders calendar meetings to offer when the user starts a recording.</summary>
public static class CalendarMeetingList
{
    /// <summary>True if the meeting is running now, or starts within an hour before or after <paramref name="now"/>.</summary>
    public static bool IsAroundNow(MeetingTitleCandidate meeting, DateTimeOffset now)
        => meeting.Start <= now && now < meeting.End ||
           meeting.Start >= now.AddHours(-1) && meeting.Start <= now.AddHours(1);

    /// <summary>Keeps eligible online meetings: those around now first (current ones first), then later ones, then earlier ones (newest first).</summary>
    public static List<MeetingTitleCandidate> Select(IEnumerable<MeetingTitleCandidate> meetings, DateTimeOffset now)
    {
        // Filter once, then group the candidates by the order that is most useful when recording starts.
        var timed = meetings.Where(MeetingTitleResolver.IsEligibleOnlineMeeting).ToArray();
        return timed.Where(m => IsAroundNow(m, now))
            .OrderBy(m => m.Start <= now && now < m.End ? 0 : 1).ThenBy(m => m.Start)
            .Concat(timed.Where(m => !IsAroundNow(m, now) && m.Start > now).OrderBy(m => m.Start))
            .Concat(timed.Where(m => !IsAroundNow(m, now) && m.End <= now).OrderByDescending(m => m.Start))
            .ToList();
    }
}

/// <summary>Text shown in the "meeting detected" prompt.</summary>
public sealed record MeetingPromptContent(
    string Title,
    string CalendarState,
    string? Schedule,
    string? Organizer,
    string? Invitees);

/// <summary>Builds the "meeting detected" prompt text from a detected session.</summary>
public static class MeetingPromptPresentation
{
    public static MeetingPromptContent Create(MeetingSession session, string calendarState)
    {
        var meeting = session.CalendarMeeting;
        var participants = session.InviteParticipants;
        // Keep the prompt compact: show at most two names while still reporting the full invitee count.
        var organizer = !string.IsNullOrWhiteSpace(meeting?.Organizer?.Name)
            ? $"Organizer: {meeting.Organizer.Name.Trim()}"
            : null;
        var preview = participants.Select(person => person.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(2)
            .ToArray();
        var invitees = participants.Count == 0 ? null
            : $"Invitees: {participants.Count}" +
              (preview.Length == 0 ? "" : $" - {string.Join(", ", preview)}" +
               (participants.Count > preview.Length ? $" +{participants.Count - preview.Length} more" : ""));
        return new MeetingPromptContent(
            session.Title,
            calendarState,
            meeting is null ? null :
                $"Scheduled: {meeting.Start.LocalDateTime:t} - {meeting.End.LocalDateTime:t}",
            organizer,
            invitees);
    }
}
