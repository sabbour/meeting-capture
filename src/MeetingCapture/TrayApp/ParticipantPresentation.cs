using System.Globalization;
using MeetingCapture.WorkIq;

namespace MeetingCapture.TrayApp;

/// <summary>
/// One compact participant token. Verified joined members and scheduled invitees use the
/// same shape but are never merged: <see cref="IsVerified"/> records which list produced it.
/// </summary>
public sealed record ParticipantChip(string Initials, string DisplayName, bool IsVerified)
{
    public string AccessibleName => IsVerified
        ? $"{DisplayName}, verified joined participant"
        : $"{DisplayName}, scheduled invitee";
}

/// <summary>The few participant chips to show, and text for how many more are hidden.</summary>
internal readonly record struct ParticipantPreview(
    IReadOnlyList<ParticipantChip> Chips,
    string Overflow);

/// <summary>Status, freshness and guidance text for the participant roster.</summary>
internal readonly record struct RosterPresentation(
    string Status,
    string Freshness,
    string Guidance);

/// <summary>
/// Compact presentation for the meeting-context participants block. The flyout is a meeting
/// aid, so it shows a bounded preview instead of a full roster or invitee dump, and it keeps
/// verified joined participants strictly separate from scheduled calendar invitees.
/// </summary>
internal static class ParticipantPresentation
{
    internal const int PreviewLimit = 5;

    internal static ParticipantPreview Preview(
        IReadOnlyList<string> names,
        bool isVerified,
        int limit = PreviewLimit)
    {
        ArgumentNullException.ThrowIfNull(names);
        // Limit the visible chips but retain the full count in Overflow; the caller supplies whether they are verified.
        var shown = names.Take(limit).Select(name => new ParticipantChip(
            SpeakerAvatarPresentation.Create(name, isResolved: true).Initials,
            name,
            isVerified)).ToArray();
        var remaining = names.Count - shown.Length;
        return new(shown, remaining > 0 ? $"+{remaining}" : string.Empty);
    }

    internal static string InviteeSummary(IReadOnlyList<string> invitees)
    {
        ArgumentNullException.ThrowIfNull(invitees);
        if (invitees.Count == 0)
            return "No scheduled invitees are available.";
        return $"{invitees.Count} scheduled invitee{(invitees.Count == 1 ? "" : "s")} " +
            "\u00b7 not verified as joined.";
    }

    internal static string InviteeList(IReadOnlyList<string> invitees)
    {
        ArgumentNullException.ThrowIfNull(invitees);
        return string.Join(" \u00b7 ", invitees);
    }

    internal static RosterPresentation Roster(
        ActiveRosterSnapshot? snapshot,
        int memberCount,
        DateTimeOffset? checkedAt)
    {
        if (snapshot is null)
            return new(
                "Live roster has not been checked.",
                checkedAt is { } pending ? $"Last checked {Moment(pending)}." : string.Empty,
                string.Empty);

        // Only a provider's verified-live result is described as joined; an unavailable probe stays explicit.
        var status = snapshot.Availability switch
        {
            ActiveRosterAvailability.VerifiedLive when memberCount == 0 =>
                "Verified joined call \u00b7 partial \u00b7 no displayed participant rows.",
            ActiveRosterAvailability.VerifiedLive =>
                $"Verified joined \u00b7 partial \u00b7 {memberCount} displayed participant " +
                $"row{(memberCount == 1 ? "" : "s")}.",
            ActiveRosterAvailability.Unavailable => "No joined meeting roster is available.",
            _ => "Live participants could not be verified.",
        };
        var source = snapshot.Source == ActiveRosterSource.WorkIq ? "WorkIQ" : "Teams accessibility";
        // Keep observation time separate from the last refresh time so older cached observations are clear.
        var freshness = $"{source} \u00b7 observed {Moment(snapshot.ObservedAt)}" +
            (snapshot.IsPartial ? " \u00b7 partial coverage" : string.Empty) +
            (checkedAt is { } moment ? $" \u00b7 last checked {Moment(moment)}" : string.Empty);
        return new(status, freshness, Guidance(snapshot));
    }

    private static string Guidance(ActiveRosterSnapshot snapshot)
    {
        var limitation = (snapshot.Limitation ?? string.Empty).Trim();
        if (limitation.Length == 0)
            return string.Empty;
        return limitation.Contains("Participants pane", StringComparison.OrdinalIgnoreCase)
            ? limitation + " Then choose Refresh roster."
            : limitation;
    }

    private static string Moment(DateTimeOffset value) =>
        value.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
}
