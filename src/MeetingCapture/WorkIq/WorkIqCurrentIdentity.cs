namespace MeetingCapture.WorkIq;

/// <summary>
/// The tenant-bound identity of the signed-in WorkIQ user, resolved from Microsoft Graph `/me`
/// (object ID, verified mail, verified userPrincipalName). Display names and the MSAL account
/// username are never part of this identity: participation is proven only
/// by an exact object-ID match or an exact case-insensitive email/UPN match.
/// </summary>
public sealed record WorkIqCurrentIdentity(string? ObjectId, IReadOnlyList<string> VerifiedEmailAliases)
{
    public static readonly WorkIqCurrentIdentity Unresolved = new(null, []);

    /// <summary>False when neither a directory object ID nor a verified mail/UPN alias resolved.
    /// An MSAL account username alone never satisfies this.</summary>
    public bool IsResolved => !string.IsNullOrWhiteSpace(ObjectId) || VerifiedEmailAliases.Count > 0;

    public bool MatchesId(string? candidateId) =>
        !string.IsNullOrWhiteSpace(ObjectId) && !string.IsNullOrWhiteSpace(candidateId) &&
        string.Equals(ObjectId, candidateId, StringComparison.OrdinalIgnoreCase);

    public bool MatchesEmail(string? candidateEmail) =>
        !string.IsNullOrWhiteSpace(candidateEmail) &&
        VerifiedEmailAliases.Any(alias => string.Equals(alias, candidateEmail, StringComparison.OrdinalIgnoreCase));
}
