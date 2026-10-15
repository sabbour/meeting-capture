using System.Text.Json;
using MeetingCapture.Intelligence;

namespace MeetingCapture.WorkIq;

/// <summary>
/// Proves current-user participation from typed provider metadata only. A hit is included only
/// when its resourceMetadata carries an explicit participation field (sender/recipient/organizer/
/// attendee/author/etc.) whose person entry resolves, by exact object ID or exact case-insensitive
/// email/UPN, to the current tenant identity. Display names, titles, content, and relevance prose
/// never establish involvement.
/// </summary>
internal static class WorkIqParticipation
{
    public static bool IsCurrentUserParticipant(
        JsonElement resourceMetadata, ContextSourceType sourceType, WorkIqCurrentIdentity identity)
        => TryGetCurrentUserInvolvement(resourceMetadata, sourceType, identity, out _, out _);

    public static bool TryGetCurrentUserInvolvement(
        JsonElement resourceMetadata,
        ContextSourceType sourceType,
        WorkIqCurrentIdentity identity,
        out ContextInvolvementRole? role,
        out ContextIdentityMatchKind? matchKind)
    {
        role = null;
        matchKind = null;
        // Names and source prose are not identity evidence. Without a verified tenant identity
        // and structured metadata, participation cannot be claimed.
        if (!identity.IsResolved || resourceMetadata.ValueKind != JsonValueKind.Object)
            return false;

        var candidates = sourceType switch
        {
            ContextSourceType.OutlookMail => new[]
            {
                ("email", "sender", ContextInvolvementRole.EmailSender),
                ("email", "toRecipients", ContextInvolvementRole.EmailToRecipient),
                ("email", "ccRecipients", ContextInvolvementRole.EmailCcRecipient),
                ("email", "bccRecipients", ContextInvolvementRole.EmailBccRecipient)
            },
            ContextSourceType.TeamsMessage or ContextSourceType.TeamsChannel or
                ContextSourceType.TeamsGroupChat => new[]
            {
                ("teams", "author", ContextInvolvementRole.TeamsAuthor),
                ("teams", "participants", ContextInvolvementRole.TeamsParticipant),
                ("teams", "members", ContextInvolvementRole.TeamsMember)
            },
            ContextSourceType.Calendar or ContextSourceType.MeetingArtifact => new[]
            {
                ("meeting", "organizer", ContextInvolvementRole.MeetingOrganizer),
                ("meeting", "attendees", ContextInvolvementRole.MeetingAttendee)
            },
            _ => [],
        };
        foreach (var candidate in candidates)
        {
            // Check only fields appropriate to this source family and report the first verified
            // role, keeping the source's actual participation type visible to the caller.
            if (!Matches(resourceMetadata, candidate.Item1, candidate.Item2, identity, out matchKind))
                continue;
            role = candidate.Item3;
            return true;
        }
        return false;
    }

    private static bool Matches(
        JsonElement resourceMetadata,
        string section,
        string field,
        WorkIqCurrentIdentity identity,
        out ContextIdentityMatchKind? matchKind)
    {
        matchKind = null;
        // Work IQ's metadata groups person details by source family; a missing field is simply
        // not proof of participation.
        if (!resourceMetadata.TryGetProperty(section, out var container) ||
            container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(field, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Array)
        {
            // Recipient, attendee, and member fields are lists; accept a match only from one
            // structured person entry.
            foreach (var entry in value.EnumerateArray())
                if (MatchesIdentity(entry, identity, out matchKind))
                    return true;
        }
        return value.ValueKind == JsonValueKind.Object &&
            MatchesIdentity(value, identity, out matchKind);
    }

    private static bool MatchesIdentity(
        JsonElement person,
        WorkIqCurrentIdentity identity,
        out ContextIdentityMatchKind? matchKind)
    {
        matchKind = null;
        if (person.ValueKind != JsonValueKind.Object)
            return false;
        // Directory IDs are stronger than aliases, so test all supported ID names first.
        if (TryGetString(person, "id", out var id) && identity.MatchesId(id))
        {
            matchKind = ContextIdentityMatchKind.ObjectId;
            return true;
        }
        if (TryGetString(person, "objectId", out var objectId) && identity.MatchesId(objectId))
        {
            matchKind = ContextIdentityMatchKind.ObjectId;
            return true;
        }
        if (TryGetString(person, "userId", out var userId) && identity.MatchesId(userId))
        {
            matchKind = ContextIdentityMatchKind.ObjectId;
            return true;
        }
        foreach (var nestedName in new[] { "user", "identity" })
            // Some provider shapes wrap the same directory object under user or identity.
            if (person.TryGetProperty(nestedName, out var nested) &&
                MatchesIdentity(nested, identity, out matchKind))
                return true;

        string? email = null;
        if (person.TryGetProperty("emailAddress", out var emailAddress) &&
            emailAddress.ValueKind == JsonValueKind.Object &&
            TryGetString(emailAddress, "address", out var address))
            email = address;
        email ??= TryGetString(person, "mail", out var mail) ? mail : null;
        email ??= TryGetString(person, "userPrincipalName", out var upn) ? upn : null;
        email ??= TryGetString(person, "address", out var plainAddress) ? plainAddress : null;
        // Only exact case-insensitive matches against verified /me aliases count; display names
        // and unverified account usernames never reach this check.
        if (!identity.MatchesEmail(email))
            return false;
        matchKind = ContextIdentityMatchKind.VerifiedAlias;
        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }
        value = null;
        return false;
    }
}
