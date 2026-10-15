using System.Security.Cryptography;
using System.Text;

namespace MeetingCapture.Intelligence;

/// <summary>A request for related context: the topic to search for, in a recording session at a transcript revision.</summary>
public sealed record ContextQuery(
    string SessionId,
    string Topic,
    long TranscriptRevision,
    bool ForceRefresh = false,
    string? TopicId = null);

/// <summary>Whether a related-context item reports a past fact or a connection inferred by the model.</summary>
public enum ClaimKind { HistoricalFact, InferredConnection }

/// <summary>The kind of source a related-context item comes from.</summary>
public enum ContextSourceType
{
    TeamsMessage, TeamsChannel, OutlookMail, Calendar, Document, MeetingArtifact, LocalTranscript,
    // Appended last so persisted numeric values stay stable.
    TeamsGroupChat
}

/// <summary>How the current user took part in a related source (for example author, attendee, or email recipient).</summary>
public enum ContextInvolvementRole
{
    TeamsAuthor,
    TeamsParticipant,
    TeamsMember,
    MeetingOrganizer,
    MeetingAttendee,
    EmailSender,
    EmailToRecipient,
    EmailCcRecipient,
    EmailBccRecipient
}

/// <summary>How the user was matched to a participant of a source: by directory object ID or by a verified alias.</summary>
public enum ContextIdentityMatchKind { ObjectId, VerifiedAlias }

/// <summary>Cleans titles returned by related-context sources.</summary>
public static class RelatedContextTitle
{
    private static readonly string[] KnownWrappers =
        ["TeamsMessage", "File", "Event", "Email"];

    /// <summary>Collapses whitespace and removes a wrapping tag such as &lt;TeamsMessage&gt;...&lt;/TeamsMessage&gt;.</summary>
    public static string Clean(string? value, ContextSourceType? sourceType)
    {
        var title = string.Join(" ", (value ?? string.Empty).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        var preferredTag = sourceType switch
        {
            ContextSourceType.TeamsMessage => "TeamsMessage",
            ContextSourceType.Document => "File",
            _ => null,
        };
        foreach (var tag in preferredTag is null
                     ? KnownWrappers
                     : KnownWrappers.Prepend(preferredTag).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var opening = $"<{tag}>";
            var closing = $"</{tag}>";
            if (title.StartsWith(opening, StringComparison.OrdinalIgnoreCase) &&
                title.EndsWith(closing, StringComparison.OrdinalIgnoreCase))
                return title[opening.Length..^closing.Length].Trim();
        }
        return title;
    }
}

/// <summary>Outcome of a related-context lookup.</summary>
public enum RelatedContextAvailability { Available, NoEvidence, Unavailable, Error }

/// <summary>A source (such as a message, email, or document) that backs a related-context item.</summary>
public sealed record ContextEvidenceSource(
    string Title,
    string Url,
    ContextSourceType SourceType,
    DateTimeOffset? ActualSourceDate,
    string? SourceId = null,
    string? InvolvementRationale = null,
    ContextInvolvementRole? InvolvementRole = null,
    ContextIdentityMatchKind? InvolvementMatchKind = null);

/// <summary>A related-context suggestion with a summary and the sources that support it.</summary>
public sealed record RelatedContextItem(
    string Title,
    string Summary,
    ClaimKind ClaimKind,
    IReadOnlyList<ContextEvidenceSource> Sources,
    string? RelevanceRationale = null,
    string? SuggestionId = null);

/// <summary>The result of one related-context lookup: items, availability, and an optional detail message.</summary>
public sealed record RelatedContextSnapshot(
    DateTimeOffset RetrievedAt,
    IReadOnlyList<RelatedContextItem> Items,
    RelatedContextAvailability Availability,
    string? Detail = null);

/// <summary>A provider of related context for a meeting topic.</summary>
public interface IRelatedContextSource
{
    /// <summary>True when the source can currently be queried.</summary>
    bool IsAvailable { get; }

    /// <summary>Changes when the source state changes (for example sign-in), so cached results can be discarded.</summary>
    long Revision { get; }

    /// <summary>Finds related context for the topic in <paramref name="query"/>.</summary>
    Task<RelatedContextSnapshot> GetRelatedContextAsync(ContextQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Cleans and limits related-context results before display: trims text, allows only HTTPS
/// source URLs without credentials, removes duplicates, and keeps at most 5 items.
/// </summary>
public static class RelatedContextNormalizer
{
    private const int MaxItems = 5;
    private const int MaxSourcesPerItem = 4;
    private const int MaxTitleLength = 100;
    private const int MaxSummaryLength = 320;
    private const int MaxRationaleLength = 240;

    /// <summary>
    /// Returns a cleaned copy of <paramref name="snapshot"/>. Non-available results lose their items and
    /// get a fixed safe message; an available result with no valid items becomes <c>NoEvidence</c>.
    /// </summary>
    public static RelatedContextSnapshot Normalize(RelatedContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Availability != RelatedContextAvailability.Available)
            return snapshot.Availability switch
            {
                RelatedContextAvailability.NoEvidence => snapshot with
                {
                    Items = [],
                    Detail = "No related source suggestions were found."
                },
                RelatedContextAvailability.Unavailable => snapshot with
                {
                    Items = [],
                    Detail = SafeUnavailableDetail(snapshot.Detail)
                },
                _ => snapshot with
                {
                    Items = [],
                    Detail = "Related context could not be loaded."
                }
            };

        var items = snapshot.Items
            .Select(NormalizeItem)
            .Where(item => item is not null)
            .Cast<RelatedContextItem>()
            .DistinctBy(item => item.SuggestionId, StringComparer.Ordinal)
            .Take(MaxItems)
            .ToArray();
        return items.Length == 0
            ? snapshot with
            {
                Items = [],
                Availability = RelatedContextAvailability.NoEvidence,
                Detail = "No related source suggestions were found."
            }
            : snapshot with { Items = items };
    }

    private static string SafeUnavailableDetail(string? detail) => detail switch
    {
        "Work IQ is not signed in." or
        "Work IQ declined the related-sources request." or
        "Work IQ sent a reply this app could not read." or
        "Work IQ could not be reached." or
        "The Work IQ service is having a problem right now." or
        "Work IQ timed out while finding related suggestions." or
        "Your Work IQ account cannot access related suggestions." or
        "Work IQ does not support related suggestions." or
        "Work IQ request failed. See Diagnostics/related.log for the error type." => detail,
        _ => "Related context is unavailable."
    };

    private static RelatedContextItem? NormalizeItem(RelatedContextItem item)
    {
        var summary = Clean(item.Summary, MaxSummaryLength);
        var sources = item.Sources
            .Select(NormalizeSource)
            .Where(source => source is not null)
            .Cast<ContextEvidenceSource>()
            .DistinctBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourcesPerItem)
            .ToArray();
        var title = Clean(
            RelatedContextTitle.Clean(item.Title, sources.FirstOrDefault()?.SourceType),
            MaxTitleLength);
        var relevance = OptionalClean(item.RelevanceRationale, MaxRationaleLength);
        var suggestionId = OptionalClean(item.SuggestionId, 128) ??
            DeterministicSuggestionId(sources);
        return title.Length == 0 || summary.Length == 0 || sources.Length == 0
            ? null
            : item with { Title = title, Summary = summary, Sources = sources,
                RelevanceRationale = relevance, SuggestionId = suggestionId };
    }

    private static ContextEvidenceSource? NormalizeSource(ContextEvidenceSource source)
    {
        var title = Clean(RelatedContextTitle.Clean(source.Title, source.SourceType), MaxTitleLength);
        if (title.Length == 0 || !Uri.TryCreate(source.Url, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(url.Host) ||
            !string.IsNullOrEmpty(url.UserInfo))
            return null;
        var canonicalUrl = url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var id = OptionalClean(source.SourceId, 512) ?? canonicalUrl;
        var involvement = OptionalClean(source.InvolvementRationale, MaxRationaleLength);
        return source with
        {
            Title = title,
            Url = canonicalUrl,
            SourceId = id,
            InvolvementRationale = involvement
        };
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }

    private static string? OptionalClean(string? value, int maxLength)
    {
        var cleaned = Clean(value, maxLength);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string DeterministicSuggestionId(IReadOnlyList<ContextEvidenceSource> sources)
    {
        var identity = string.Join("\n", sources
            .Select(source => $"{source.SourceType}:{source.SourceId}")
            .Order(StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"context-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
