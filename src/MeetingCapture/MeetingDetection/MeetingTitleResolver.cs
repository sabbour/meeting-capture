namespace MeetingCapture.MeetingDetection;

/// <summary>A person invited to a calendar meeting. Rooms and other resources are not treated as people.</summary>
public sealed record MeetingInvitee(string? Name, string? Email, string? Type = null)
{
    public bool IsHuman =>
        !string.Equals(Type, "resource", StringComparison.OrdinalIgnoreCase) &&
        (!string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Email));

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : !string.IsNullOrWhiteSpace(Email) ? Email.Trim() : "Unknown attendee";
}

/// <summary>A calendar event that might match the meeting being detected.</summary>
public sealed record MeetingTitleCandidate(
    string Subject, DateTimeOffset Start, DateTimeOffset End,
    string? OnlineMeetingProvider, string? JoinUrl, bool IsCancelled = false, bool IsAllDay = false,
    bool? IsOnlineMeeting = null, bool? IsOrganizer = null, MeetingInvitee? Organizer = null,
    IReadOnlyList<MeetingInvitee>? Attendees = null);
/// <summary>The chosen meeting title (null if none could be trusted), with a reason and the matched event.</summary>
public sealed record MeetingTitleResolution(string? Title, string Detail, MeetingTitleCandidate? Meeting = null);

/// <summary>
/// Chooses a meeting title from calendar events. A title is used only when exactly one verified
/// online meeting for the same platform is in progress or starts within five minutes of detection.
/// </summary>
public static class MeetingTitleResolver
{
    /// <summary>Returns a display name for the meeting app's process name.</summary>
    public static string AppLabel(string process) => process.ToLowerInvariant() switch
    {
        "teams" or "ms-teams" => "Microsoft Teams",
        "zoom" => "Zoom",
        "webex" or "webexmta" or "ciscocollabhost" => "Webex",
        "lync" => "Skype for Business",
        _ => process
    };

    /// <summary>Returns a generic title, such as "Teams meeting", when no calendar title is available.</summary>
    public static string FallbackTitle(string process) => process.ToLowerInvariant() switch
    {
        "teams" or "ms-teams" or "msteams" => "Teams meeting",
        "zoom" => "Zoom meeting",
        "webex" or "webexmta" or "ciscocollabhost" or "webexteams" => "Webex meeting",
        "lync" => "Skype for Business meeting",
        _ => "Meeting"
    };

    /// <summary>Returns the matching meeting title, or null. See <see cref="ResolveWithReason"/> for the reason.</summary>
    public static string? Resolve(string process, DateTimeOffset detectedAt, IEnumerable<MeetingTitleCandidate> meetings)
        => ResolveWithReason(process, detectedAt, meetings).Title;

    /// <summary>Like <see cref="Resolve"/>, but also explains why no title was chosen.</summary>
    public static MeetingTitleResolution ResolveWithReason(string process, DateTimeOffset detectedAt, IEnumerable<MeetingTitleCandidate> meetings)
    {
        var all = meetings.Where(IsEligibleOnlineMeeting).ToArray();
        if (all.Length == 0) return new(null, "WorkIQ found no trustworthy online meeting candidates.");
        var platform = AppLabel(process);
        var timed = all.Where(meeting =>
            meeting.End > detectedAt && meeting.Start <= detectedAt.AddMinutes(5) &&
            meeting.End > meeting.Start).ToArray();
        if (timed.Length == 0) return new(null, "WorkIQ found no online meetings overlapping this signal.");
        var candidates = MatchingCandidates(process, detectedAt, timed);
        if (candidates.Count == 0)
            return new(null, $"Calendar events overlap this time, but none has verified {platform} meeting metadata.");
        if (candidates.Count > 1)
            return new(null, $"{candidates.Count} {platform} meetings overlap this signal; choose the correct meeting.");
        var subject = candidates[0].Subject.Trim();
        return subject.Length == 0 || subject == "(no subject)" ? new(null, "The matching calendar event has no usable title.")
            : new(string.Join(" ", subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                "One calendar event matches the time and meeting platform.", candidates[0]);
    }

    /// <summary>
    /// Returns eligible online meetings for the app's platform that are in progress or start within
    /// five minutes of detection, ordered by start.
    /// </summary>
    public static IReadOnlyList<MeetingTitleCandidate> MatchingCandidates(
        string process, DateTimeOffset detectedAt, IEnumerable<MeetingTitleCandidate> meetings)
    {
        var platform = AppLabel(process);
        return meetings.Where(IsEligibleOnlineMeeting)
            .Where(meeting => meeting.End > detectedAt &&
                meeting.Start <= detectedAt.AddMinutes(5) &&
                meeting.End > meeting.Start &&
                MatchesPlatform(platform, meeting))
            .OrderBy(meeting => meeting.Start)
            .ToArray();
    }

    /// <summary>
    /// True for a non-cancelled, timed event with online-call evidence and enough human attendees
    /// to be a real meeting.
    /// </summary>
    public static bool IsEligibleOnlineMeeting(MeetingTitleCandidate meeting)
    {
        if (meeting.IsCancelled || meeting.IsAllDay || meeting.End <= meeting.Start || !HasOnlineCallEvidence(meeting))
            return false;
        var attendees = meeting.Attendees?.Where(a => a.IsHuman).ToArray();
        if (attendees is null) return false;
        if (meeting.IsOrganizer == true) return attendees.Length > 0;
        if (meeting.IsOrganizer == false) return meeting.Organizer?.IsHuman == true || attendees.Length > 1;
        return attendees.Select(Identity).Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1;
    }

    /// <summary>Returns the distinct human attendees, plus the organizer when the user is not the organizer.</summary>
    public static IReadOnlyList<MeetingInvitee> InviteParticipants(MeetingTitleCandidate? meeting)
    {
        if (meeting is null) return [];
        var people = meeting.Attendees?.Where(a => a.IsHuman).ToList() ?? [];
        if (meeting.IsOrganizer == false && meeting.Organizer?.IsHuman == true)
            people.Add(meeting.Organizer);
        return people.GroupBy(Identity, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0).Select(group => group.First()).ToArray();
    }

    private static bool HasOnlineCallEvidence(MeetingTitleCandidate meeting)
    {
        var provider = meeting.OnlineMeetingProvider?.Trim();
        return meeting.IsOnlineMeeting == true ||
            !string.IsNullOrEmpty(provider) &&
            !string.Equals(provider, "unknown", StringComparison.OrdinalIgnoreCase) ||
            Uri.TryCreate(meeting.JoinUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }

    private static string Identity(MeetingInvitee attendee) =>
        !string.IsNullOrWhiteSpace(attendee.Email) ? attendee.Email.Trim() :
        !string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Name.Trim() : "";

    /// <summary>Checks that the event's provider or join-link domain belongs to the given platform.</summary>
    public static bool MatchesPlatform(string platform, MeetingTitleCandidate meeting)
    {
        var provider = meeting.OnlineMeetingProvider;
        var host = Uri.TryCreate(meeting.JoinUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps ? uri.Host : "";
        static bool Domain(string host, string domain) =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        return platform switch
        {
            "Microsoft Teams" => string.Equals(provider, "teamsForBusiness", StringComparison.OrdinalIgnoreCase) ||
                Domain(host, "teams.microsoft.com") || Domain(host, "teams.live.com") || Domain(host, "teams.cloud.microsoft"),
            "Zoom" => Domain(host, "zoom.us") || Domain(host, "zoom.com"),
            "Webex" => Domain(host, "webex.com"),
            "Skype for Business" => string.Equals(provider, "skypeForBusiness", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
