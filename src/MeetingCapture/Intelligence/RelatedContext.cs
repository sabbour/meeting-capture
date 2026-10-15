using System.Security.Cryptography;
using System.Text;

namespace MeetingCapture.Intelligence;

public sealed record ContextQuery(
    string SessionId,
    string Topic,
    long TranscriptRevision,
    bool ForceRefresh = false,
    string? TopicId = null);

public enum ClaimKind { HistoricalFact, InferredConnection }

public enum ContextSourceType
{
    TeamsMessage, TeamsChannel, OutlookMail, Calendar, Document, MeetingArtifact, LocalTranscript,
    // Appended last so persisted numeric values stay stable.
    TeamsGroupChat
}

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

public enum ContextIdentityMatchKind { ObjectId, VerifiedAlias }

public static class RelatedContextTitle
{
    private static readonly string[] KnownWrappers =
        ["TeamsMessage", "File", "Event", "Email"];

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

public enum RelatedContextAvailability { Available, NoEvidence, Unavailable, Error }

public sealed record ContextEvidenceSource(
    string Title,
    string Url,
    ContextSourceType SourceType,
    DateTimeOffset? ActualSourceDate,
    string? SourceId = null,
    string? InvolvementRationale = null,
    ContextInvolvementRole? InvolvementRole = null,
    ContextIdentityMatchKind? InvolvementMatchKind = null);

public sealed record RelatedContextItem(
    string Title,
    string Summary,
    ClaimKind ClaimKind,
    IReadOnlyList<ContextEvidenceSource> Sources,
    string? RelevanceRationale = null,
    string? SuggestionId = null);

public sealed record RelatedContextSnapshot(
    DateTimeOffset RetrievedAt,
    IReadOnlyList<RelatedContextItem> Items,
    RelatedContextAvailability Availability,
    string? Detail = null);

public interface IRelatedContextSource
{
    bool IsAvailable { get; }
    long Revision { get; }
    Task<RelatedContextSnapshot> GetRelatedContextAsync(ContextQuery query, CancellationToken cancellationToken);
}

public static class RelatedContextNormalizer
{
    private const int MaxItems = 5;
    private const int MaxSourcesPerItem = 4;
    private const int MaxTitleLength = 100;
    private const int MaxSummaryLength = 320;
    private const int MaxRationaleLength = 240;

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
