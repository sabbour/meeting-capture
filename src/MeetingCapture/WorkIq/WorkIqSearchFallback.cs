using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeetingCapture.Intelligence;

namespace MeetingCapture.WorkIq;

public sealed record WorkIqSearchHit(JsonElement Resource, string? Summary);

public sealed record WorkIqMeetingSubject(string Subject, string? Summary);

/// <summary>
/// Fallback used when WorkIQ's "retrieve" tool declines related-source requests. It runs a
/// bounded keyword search of accessible sources. Participant metadata establishes involvement
/// when available; access alone never establishes participation. Read-only.
/// </summary>
public sealed partial class WorkIqMcpClient
{
    public const string SearchMatchedBy = "Microsoft 365 keyword search";
    private const int MaxSearchHits = 25;
    private const int MaxEmailResults = 15;
    private const int MaxMeetingSubjects = 2;
    private const int MaxMeetingResults = 5;
    private static readonly TimeSpan MeetingLookback = TimeSpan.FromDays(30);
    private const int MaxTeamsMemberLookups = 5;
    private static readonly Regex SearchHighlight = new(@"</?c\d+>|<ddd/>", RegexOptions.CultureInvariant);
    private static readonly Regex SearchWord = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SearchOperators =
        new(StringComparer.OrdinalIgnoreCase) { "AND", "OR", "NOT", "NEAR" };

    public bool SearchAvailable => _searchAvailable;

    /// <summary>Plain words only, so topic text can never inject search operators or syntax.</summary>
    public static IReadOnlyList<string> RelatedContextKeywords(string topic)
    {
        var words = SearchWord.Matches(RelatedContextSearchQuery(topic))
            .Select(match => match.Value)
            .Where(word => word.Length is > 1 and <= 40 && !SearchOperators.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return words.Length == 0
            ? throw new WorkIqException(WorkIqFailure.Unsupported, "shape:searchQuery")
            : words;
    }

    public static JsonObject SearchArguments(string entityType, IReadOnlyList<string> keywords, int size)
    {
        if (entityType is not ("event" or "chatMessage" or "driveItem") || size is < 1 or > MaxSearchHits ||
            keywords.Count == 0)
            throw new ArgumentException("Unsupported search request.", nameof(entityType));
        return new JsonObject
        {
            ["actionUrl"] = "/search/query",
            ["jsonBody"] = new JsonObject
            {
                ["requests"] = new JsonArray(new JsonObject
                {
                    ["entityTypes"] = new JsonArray(entityType),
                    ["query"] = new JsonObject { ["queryString"] = string.Join(" ", keywords) },
                    ["from"] = 0,
                    ["size"] = size
                })
            }
        };
    }

    public static string EmailSearchPath(IReadOnlyList<string> keywords)
        => "/me/messages?$search=" + Uri.EscapeDataString("\"" + string.Join(" ", keywords) + "\"") +
            "&$select=subject,webLink,receivedDateTime,bodyPreview,conversationId,sender,toRecipients,ccRecipients,bccRecipients" +
            "&$top=" + MaxEmailResults.ToString(CultureInfo.InvariantCulture);

    /// <summary>Past occurrences with this exact subject. Search often returns only future
    /// occurrences of a recurring series, so the calendar is read for the same subject.</summary>
    public static string MeetingSubjectLookupPath(string subject, DateTimeOffset now)
    {
        if (subject.Length is 0 or > 300 || subject.Any(char.IsControl))
            throw new ArgumentException("Unsupported meeting subject.", nameof(subject));
        return "/me/calendarView?startDateTime=" +
            Uri.EscapeDataString((now - MeetingLookback).UtcDateTime.ToString("o", CultureInfo.InvariantCulture)) +
            "&endDateTime=" + Uri.EscapeDataString(now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)) +
            "&$filter=" + Uri.EscapeDataString("subject eq '" + subject.Replace("'", "''", StringComparison.Ordinal) + "'") +
            "&$select=subject,iCalUId,webLink,organizer,attendees,start,end,isCancelled" +
            "&$orderby=" + Uri.EscapeDataString("start/dateTime desc") +
            "&$top=" + MaxMeetingResults.ToString(CultureInfo.InvariantCulture);
    }

    public static string? TeamsMembersPath(JsonElement resource)
    {
        if (resource.ValueKind != JsonValueKind.Object)
            return null;
        if (resource.TryGetProperty("channelIdentity", out var channel) &&
            channel.ValueKind == JsonValueKind.Object &&
            BoundedId(channel, "teamId") is { } teamId &&
            BoundedId(channel, "channelId") is { } channelId)
            return "/teams/" + Uri.EscapeDataString(teamId) + "/channels/" +
                Uri.EscapeDataString(channelId) + "/members?$top=100";
        return BoundedId(resource, "chatId") is { } chatId
            ? "/chats/" + Uri.EscapeDataString(chatId) + "/members"
            : null;
    }

    /// <summary>Searches one source family and returns only results that prove the signed-in
    /// user took part, exactly like the retrieve path.</summary>
    public async Task<RelatedContextSnapshot> SearchRelatedContextAsync(
        string token, string topic, string capability, DateTimeOffset retrievedAt,
        WorkIqCurrentIdentity currentIdentity, CancellationToken ct)
    {
        var keywords = RelatedContextKeywords(topic);
        switch (capability)
        {
            case "Email":
                return EmailSearchContext(
                    await FetchResultAsync(token, EmailSearchPath(keywords), ct).ConfigureAwait(false),
                    retrievedAt, currentIdentity);
            case "Meetings":
            {
                if (!_searchAvailable)
                    throw new WorkIqException(WorkIqFailure.Unsupported, "shape:searchTool");
                var hits = ParseSearchHits(await SearchAsync(token, "event", keywords, ct).ConfigureAwait(false));
                var subjects = MeetingSubjects(hits);
                if (subjects.Count == 0)
                    return new RelatedContextSnapshot(retrievedAt, [], RelatedContextAvailability.NoEvidence);
                var now = DateTimeOffset.UtcNow;
                var lookups = await FetchManyAsync(token,
                    subjects.Select(subject => MeetingSubjectLookupPath(subject.Subject, now)).ToArray(),
                    ct).ConfigureAwait(false);
                return MeetingSearchContext(subjects, lookups, now, retrievedAt, currentIdentity);
            }
            case "TeamsMessages":
            {
                if (!_searchAvailable)
                    throw new WorkIqException(WorkIqFailure.Unsupported, "shape:searchTool");
                var hits = ParseSearchHits(await SearchAsync(token, "chatMessage", keywords, ct).ConfigureAwait(false));
                if (hits.Count == 0)
                    return new RelatedContextSnapshot(retrievedAt, [], RelatedContextAvailability.NoEvidence);
                var paths = hits.Select(hit => TeamsMembersPath(hit.Resource))
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxTeamsMemberLookups)
                    .ToArray();
                JsonElement? members = paths.Length == 0
                    ? null
                    : await FetchManyAsync(token, paths, ct).ConfigureAwait(false);
                return TeamsSearchContext(hits, paths, members, retrievedAt, currentIdentity);
            }
            case "OneDriveAndSharePoint":
            {
                if (!_searchAvailable)
                    throw new WorkIqException(WorkIqFailure.Unsupported, "shape:searchTool");
                return DocumentSearchContext(
                    ParseSearchHits(await SearchAsync(token, "driveItem", keywords, ct).ConfigureAwait(false)),
                    retrievedAt, currentIdentity);
            }
            default:
                throw new WorkIqException(WorkIqFailure.Unsupported, "shape:searchCapability");
        }
    }

    private Task<JsonElement> SearchAsync(string token, string entityType, IReadOnlyList<string> keywords, CancellationToken ct)
        => RequestAsync("tools/call", new
        {
            name = "do_action",
            arguments = SearchArguments(entityType, keywords,
                entityType == "event" ? MaxSearchHits : 15)
        }, token, ct);

    private Task<JsonElement> FetchManyAsync(string token, string[] paths, CancellationToken ct)
        => RequestAsync("tools/call", new { name = "fetch", arguments = new { entityUrls = paths } }, token, ct);

    public static IReadOnlyList<WorkIqSearchHit> ParseSearchHits(JsonElement result)
    {
        try
        {
            ThrowIfToolError(result);
            if (!result.TryGetProperty("structuredContent", out var structured) ||
                structured.ValueKind != JsonValueKind.Object)
                throw new WorkIqException(WorkIqFailure.Malformed, "shape:searchResponse");
            if (structured.TryGetProperty("statusCode", out var statusValue) &&
                statusValue.TryGetInt32(out var status))
                EnsureStatus(status, includeHttpCode: true);
            var hits = new List<WorkIqSearchHit>();
            if (!structured.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("value", out var responses) ||
                responses.ValueKind != JsonValueKind.Array)
                return hits;
            foreach (var response in responses.EnumerateArray())
            {
                if (response.ValueKind != JsonValueKind.Object ||
                    !response.TryGetProperty("hitsContainers", out var containers) ||
                    containers.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var container in containers.EnumerateArray())
                {
                    if (container.ValueKind != JsonValueKind.Object ||
                        !container.TryGetProperty("hits", out var containerHits) ||
                        containerHits.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var hit in containerHits.EnumerateArray())
                    {
                        if (hit.ValueKind != JsonValueKind.Object ||
                            !hit.TryGetProperty("resource", out var resource) ||
                            resource.ValueKind != JsonValueKind.Object)
                            continue;
                        hits.Add(new WorkIqSearchHit(resource.Clone(), CleanSummary(
                            hit.TryGetProperty("summary", out var summary) &&
                            summary.ValueKind == JsonValueKind.String ? summary.GetString() : null)));
                        if (hits.Count == MaxSearchHits)
                            return hits;
                    }
                }
            }
            return hits;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WorkIqException(WorkIqFailure.Malformed, "shape:searchResponse");
        }
    }

    /// <summary>Distinct exact subjects of meeting hits, in search rank order.</summary>
    public static IReadOnlyList<WorkIqMeetingSubject> MeetingSubjects(IReadOnlyList<WorkIqSearchHit> hits)
    {
        var subjects = new List<WorkIqMeetingSubject>();
        foreach (var hit in hits)
        {
            if (!hit.Resource.TryGetProperty("subject", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { Length: > 0 and <= 300 } subject ||
                string.IsNullOrWhiteSpace(subject) || subject.Any(char.IsControl) ||
                subjects.Any(existing => string.Equals(existing.Subject, subject, StringComparison.OrdinalIgnoreCase)))
                continue;
            subjects.Add(new WorkIqMeetingSubject(subject, hit.Summary));
            if (subjects.Count == MaxMeetingSubjects)
                break;
        }
        return subjects;
    }

    public static RelatedContextSnapshot EmailSearchContext(
        JsonElement fetchResult, DateTimeOffset retrievedAt, WorkIqCurrentIdentity currentIdentity)
    {
        var entry = ParseFetchEntries(fetchResult, 1)[0];
        EnsureStatus(entry.Status, includeHttpCode: true);
        var hits = new JsonArray();
        var seenConversations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in ValueArray(entry.Data).Take(MaxEmailResults))
        {
            // Keep only the top-ranked message of a thread; messages without a thread id stay.
            if (BoundedId(message, "conversationId") is { } conversation && !seenConversations.Add(conversation))
                continue;
            var email = new JsonObject();
            foreach (var field in new[] { "sender", "toRecipients", "ccRecipients", "bccRecipients" })
                if (message.TryGetProperty(field, out var value))
                    email[field] = JsonNode.Parse(value.GetRawText());
            hits.Add(SearchRetrievalHit(
                OptionalBoundedString(message, "webLink", 2048), "message",
                OptionalBoundedString(message, "id", 512),
                OptionalBoundedString(message, "subject", 300),
                OptionalBoundedString(message, "receivedDateTime", 64),
                CleanSummary(OptionalBoundedString(message, "bodyPreview", 1000)),
                "email", email));
        }
        return SearchContext(hits, retrievedAt, currentIdentity);
    }

    /// <summary>Only meetings that already ended; a future meeting cannot be prior context for
    /// the current one.</summary>
    public static RelatedContextSnapshot MeetingSearchContext(
        IReadOnlyList<WorkIqMeetingSubject> subjects, JsonElement fetchResult, DateTimeOffset now,
        DateTimeOffset retrievedAt, WorkIqCurrentIdentity currentIdentity)
    {
        var entries = ParseFetchEntries(fetchResult, subjects.Count);
        if (entries.Count > 0 && entries.All(entry => entry.Status is < 200 or >= 300))
            EnsureStatus(entries[0].Status, includeHttpCode: true);
        var found = new List<(JsonElement Item, string Uid, DateTimeOffset Start, string? Summary)>();
        for (var index = 0; index < subjects.Count; index++)
        {
            if (entries[index].Status is < 200 or >= 300)
                continue;
            foreach (var item in ValueArray(entries[index].Data))
            {
                if (BoundedId(item, "iCalUId") is not { } uid ||
                    !item.TryGetProperty("subject", out var subjectValue) ||
                    subjectValue.ValueKind != JsonValueKind.String ||
                    !string.Equals(subjectValue.GetString(), subjects[index].Subject, StringComparison.OrdinalIgnoreCase) ||
                    item.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True ||
                    !item.TryGetProperty("start", out var startValue) || startValue.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("end", out var endValue) || endValue.ValueKind != JsonValueKind.Object)
                    continue;
                DateTimeOffset start, end;
                try
                {
                    start = ParseTime(startValue);
                    end = ParseTime(endValue);
                }
                catch (Exception ex) when (ex is WorkIqException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    continue;
                }
                if (end <= start || end > now || end - start > TimeSpan.FromDays(1) ||
                    found.Any(existing => existing.Uid == uid && existing.Start == start))
                    continue;
                found.Add((item, uid, start, subjects[index].Summary));
            }
        }
        var hits = new JsonArray();
        foreach (var (match, uid, start, summary) in found.OrderByDescending(meeting => meeting.Start).Take(MaxMeetingResults))
        {
            var meeting = new JsonObject();
            foreach (var field in new[] { "organizer", "attendees" })
                if (match.TryGetProperty(field, out var value))
                    meeting[field] = JsonNode.Parse(value.GetRawText());
            var date = start.ToString("o", CultureInfo.InvariantCulture);
            hits.Add(SearchRetrievalHit(
                OptionalBoundedString(match, "webLink", 2048), "event", uid + ":" + date,
                OptionalBoundedString(match, "subject", 300), date, summary, "meeting", meeting));
        }
        return SearchContext(hits, retrievedAt, currentIdentity);
    }

    public static RelatedContextSnapshot TeamsSearchContext(
        IReadOnlyList<WorkIqSearchHit> searchHits, IReadOnlyList<string> memberPaths,
        JsonElement? membersResult, DateTimeOffset retrievedAt, WorkIqCurrentIdentity currentIdentity)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (membersResult is { } result && memberPaths.Count > 0)
        {
            var entries = ParseFetchEntries(result, memberPaths.Count);
            for (var index = 0; index < memberPaths.Count; index++)
                if (entries[index].Status is >= 200 and < 300 &&
                    entries[index].Data.ValueKind == JsonValueKind.Object &&
                    entries[index].Data.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.Array)
                    members[memberPaths[index]] = value;
        }
        var hits = new JsonArray();
        foreach (var hit in searchHits)
        {
            var resource = hit.Resource;
            var channel0 = resource.TryGetProperty("channelIdentity", out var identity) &&
                identity.ValueKind == JsonValueKind.Object &&
                BoundedId(identity, "teamId") is not null && BoundedId(identity, "channelId") is not null;
            var teams = new JsonObject();
            if (resource.TryGetProperty("from", out var from))
                teams["author"] = JsonNode.Parse(from.GetRawText());
            if (TeamsMembersPath(resource) is { } path && members.TryGetValue(path, out var memberList))
            {
                teams["members"] = JsonNode.Parse(memberList.GetRawText());
                if (!channel0 && !resource.TryGetProperty("chatType", out _) &&
                    memberList.GetArrayLength() >= 2)
                    teams["chatType"] = memberList.GetArrayLength() > 2 ? "group" : "oneOnOne";
            }
            if (resource.TryGetProperty("chatType", out var chatType) && chatType.ValueKind == JsonValueKind.String)
                teams["chatType"] = chatType.GetString();
            var channel = channel0;
            var messageId = OptionalBoundedString(resource, "id", 256);
            var chatId = BoundedId(resource, "chatId");
            hits.Add(SearchRetrievalHit(
                OptionalBoundedString(resource, "webLink", 2048),
                channel ? "channelMessage" : "chatMessage",
                messageId is null ? null : (chatId is null ? messageId : chatId + ":" + messageId),
                OptionalBoundedString(resource, "subject", 300) ?? Compact(hit.Summary, 80),
                OptionalBoundedString(resource, "createdDateTime", 64),
                hit.Summary, "teams", teams));
        }
        return SearchContext(hits, retrievedAt, currentIdentity);
    }

    /// <summary>OneDrive/SharePoint files Microsoft 365 search returned for the signed-in user.
    /// Access is not authorship, so no involvement is recorded.</summary>
    public static RelatedContextSnapshot DocumentSearchContext(
        IReadOnlyList<WorkIqSearchHit> searchHits, DateTimeOffset retrievedAt,
        WorkIqCurrentIdentity currentIdentity)
    {
        var hits = new JsonArray();
        foreach (var hit in searchHits)
        {
            var resource = hit.Resource;
            hits.Add(SearchRetrievalHit(
                OptionalBoundedString(resource, "webUrl", 2048), "driveItem",
                OptionalBoundedString(resource, "id", 512),
                OptionalBoundedString(resource, "name", 300),
                OptionalBoundedString(resource, "lastModifiedDateTime", 64),
                hit.Summary, "document", new JsonObject()));
        }
        return SearchContext(hits, retrievedAt, currentIdentity);
    }

    private static RelatedContextSnapshot SearchContext(
        JsonArray hits, DateTimeOffset retrievedAt, WorkIqCurrentIdentity currentIdentity)
    {
        if (hits.Count == 0)
            return new RelatedContextSnapshot(retrievedAt, [], RelatedContextAvailability.NoEvidence);
        var envelope = new JsonObject
        {
            ["structuredContent"] = new JsonObject
            {
                ["application/vnd.ms-workiq.retrieval"] = new JsonObject { ["retrievalHits"] = hits }
            }
        };
        using var document = JsonDocument.Parse(envelope.ToJsonString());
        return ParseRetrievedContext(document.RootElement, retrievedAt, currentIdentity, SearchMatchedBy,
            allowSearchReturned: true);
    }

    private static JsonObject SearchRetrievalHit(
        string? webUrl, string entityType, string? id, string? title, string? date, string? snippet,
        string section, JsonObject participation)
    {
        var general = new JsonObject();
        if (title is not null) general["title"] = title;
        if (date is not null) general["date"] = date;
        if (snippet is not null) general["snippet"] = snippet;
        var hit = new JsonObject
        {
            ["entityType"] = entityType,
            ["resourceMetadata"] = new JsonObject { ["general"] = general, [section] = participation }
        };
        if (webUrl is not null) hit["webUrl"] = webUrl;
        if (id is not null) hit["id"] = id;
        return hit;
    }

    private static List<(int Status, JsonElement Data)> ParseFetchEntries(JsonElement result, int expected)
    {
        try
        {
            ThrowIfToolError(result);
            var results = result.GetProperty("structuredContent").GetProperty("results");
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != expected)
                throw new WorkIqException(WorkIqFailure.Malformed, "shape:fetchResults");
            var entries = new List<(int, JsonElement)>();
            foreach (var entry in results.EnumerateArray())
            {
                var status = entry.GetProperty("statusCode").GetInt32();
                if (status == 401)
                    throw new WorkIqException(WorkIqFailure.Authentication, "http:401");
                entries.Add((status, entry.TryGetProperty("data", out var data) ? data.Clone() : default));
            }
            return entries;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WorkIqException(WorkIqFailure.Malformed, "shape:fetchResults");
        }
    }

    private static void ThrowIfToolError(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new WorkIqException(WorkIqFailure.Malformed, "shape:result");
        if (!result.TryGetProperty("isError", out var isError))
            return;
        if (isError.ValueKind == JsonValueKind.True)
            throw new WorkIqException(WorkIqFailure.ServiceDeclined, ToolErrorCode(result));
        if (isError.ValueKind != JsonValueKind.False)
            throw new WorkIqException(WorkIqFailure.Malformed, "shape:isError");
    }

    private static IEnumerable<JsonElement> ValueArray(JsonElement data)
        => data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    private static string? BoundedId(JsonElement item, string key)
        => item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 and <= 512 } id && !id.Any(char.IsControl)
            ? id
            : null;

    private static string? CleanSummary(string? summary)
        => summary is null ? null : Compact(WebUtility.HtmlDecode(SearchHighlight.Replace(summary, "")), 320);
}
