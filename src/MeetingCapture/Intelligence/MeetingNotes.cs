using System.Text.RegularExpressions;

namespace MeetingCapture.Intelligence;

/// <summary>Structured output produced by the LLM notes intelligence pass.</summary>
public sealed class MeetingNotes
{
    public string SessionId { get; init; } = string.Empty;
    public IntelligenceFailure? IntelligenceFailure { get; init; }
    public string MeetingTitle { get; init; } = "Meeting";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string RawTranscript { get; set; } = string.Empty;

    /// <summary>Grounded summary paragraph (4-6 sentences).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Key decisions made during the meeting.</summary>
    public List<string> Decisions { get; } = [];

    /// <summary>Action items with owner where mentioned.</summary>
    public List<ActionItem> ActionItems { get; } = [];

    /// <summary>Topics discussed, useful for searchability.</summary>
    public List<string> Topics { get; } = [];

    /// <summary>Grounded topic narratives, one bounded entry per substantive thread.</summary>
    public List<string> KeyDiscussionPoints { get; } = [];

    /// <summary>Short, transcript-grounded speaker summaries grouped in meeting topic order.</summary>
    public List<MeetingTopicNotes> TopicNotes { get; } = [];

    /// <summary>Nuances, tone observations, or notable context.</summary>
    public string Nuances { get; set; } = string.Empty;

    public string MeetingType { get; set; } = "unknown"; // standup|planning|review|demo|customer-call|internal|1:1|unknown
    public string Sentiment { get; set; } = "neutral";   // positive|neutral|mixed|tense
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> FollowUpMeetings { get; set; } = [];
    public List<string> NotableQuotes { get; set; } = [];  // attributed verbatim quotes
    public List<ParticipantInfo> Participants { get; set; } = [];
    public int EstimatedDurationMinutes { get; set; }
}

public sealed record MeetingTopicNotes(
    string TopicId,
    string Topic,
    IReadOnlyList<string> Bullets);

public static class MeetingNotesText
{
    private static readonly Regex GroundingOrdinal = new(
        @"\b(?:Grounded in (?:the )?(?:transcript )?|Discussed around )segments?\s+\d+(?:\s*[–-]\s*\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SegmentOrdinal = new(
        @"\bsegments?\s+\d+(?:\s*[–-]\s*\d+)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string WithoutSegmentOrdinals(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var cleaned = SegmentOrdinal.Replace(GroundingOrdinal.Replace(value, string.Empty), string.Empty);
        cleaned = Regex.Replace(cleaned, @"[ \t]+([,.;:])", "$1");
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ");
        return cleaned.Trim();
    }
}

public static class MeetingTopicNotesBuilder
{
    private const int BulletCharacterLimit = 220;

    public static IReadOnlyList<MeetingTopicNotes> Build(MeetingVisualizationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segments = document.Segments.ToDictionary(item => item.Ordinal);
        var speakers = document.Speakers.ToDictionary(
            item => item.SpeakerId,
            item => item.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        var evidence = document.Evidence
            .Where(item =>
                (item.Origin is EvidenceOrigin.Transcript or EvidenceOrigin.Analysis) &&
                item.Provenance == VisualizationProvenance.TranscriptFact &&
                item.SegmentOrdinal is not null &&
                segments.ContainsKey(item.SegmentOrdinal.Value))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        var topicLabels = document.TopicClusters.ToDictionary(
            item => item.Id,
            item => MeetingNotesText.WithoutSegmentOrdinals(ProjectSpeakers(item.Label, speakers)),
            StringComparer.Ordinal);
        bool BelongsToTopic(string? assignedTopicId, IEnumerable<string> evidenceIds, string topicId)
        {
            if (StringComparer.Ordinal.Equals(assignedTopicId, topicId))
                return true;
            return assignedTopicId is null && evidenceIds.Any(id =>
                evidence.TryGetValue(id, out var evidenceItem) &&
                StringComparer.Ordinal.Equals(evidenceItem.TopicId, topicId));
        }

        return document.TopicClusters
            .OrderBy(item => item.FirstOccurrenceOrdinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(topic =>
            {
                var candidates = new List<(int Ordinal, int Priority, string Id, string Text)>();
                foreach (var decision in document.Decisions.Where(item =>
                             BelongsToTopic(item.TopicId, item.EvidenceIds, topic.Id)))
                {
                    var sources = decision.EvidenceIds
                        .Where(evidence.ContainsKey)
                        .Select(id => evidence[id])
                        .OrderBy(item => item.SegmentOrdinal)
                        .ThenBy(item => item.Id, StringComparer.Ordinal)
                        .ToArray();
                    var source = sources.FirstOrDefault();
                    if (source?.SegmentOrdinal is not int decisionOrdinal ||
                        string.IsNullOrWhiteSpace(decision.Text))
                        continue;
                    var citedSpeakers = sources
                        .Select(item => SpeakerName(segments[item.SegmentOrdinal!.Value], speakers))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var speakerSummary = citedSpeakers.Length switch
                    {
                        1 => citedSpeakers[0],
                        2 => $"{citedSpeakers[0]} and {citedSpeakers[1]}",
                        > 2 => $"{citedSpeakers[0]}, {citedSpeakers[1]}, and others",
                        _ => "unidentified speakers",
                    };
                    var decisionText = Bound(MeetingNotesText.WithoutSegmentOrdinals(
                        ProjectSpeakers(decision.Text, speakers)));
                    candidates.Add((
                        decisionOrdinal,
                        0,
                        decision.Id,
                        StartsWithAny(decisionText, citedSpeakers)
                            ? decisionText
                            : $"Decision discussed by {speakerSummary}: {decisionText}"));
                }

                foreach (var fact in evidence.Values
                             .Where(item => item.TopicId == topic.Id && item.SegmentOrdinal is not null)
                             .OrderBy(item => item.SegmentOrdinal)
                             .ThenBy(item => item.Id, StringComparer.Ordinal))
                {
                    var segment = segments[fact.SegmentOrdinal!.Value];
                    var text = string.IsNullOrWhiteSpace(fact.Summary)
                        ? segment.Text
                        : fact.Summary;
                    text = MeetingNotesText.WithoutSegmentOrdinals(ProjectSpeakers(text, speakers));
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    var factSpeaker = SpeakerName(segment, speakers);
                    candidates.Add((
                        segment.Ordinal,
                        1,
                        fact.Id,
                        StartsWithAny(text, [factSpeaker])
                            ? Bound(text)
                            : $"{factSpeaker}: {Bound(text)}"));
                }

                var bullets = candidates
                    .OrderBy(item => item.Priority)
                    .ThenBy(item => item.Ordinal)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => item.Text)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();
                return new MeetingTopicNotes(topic.Id, topicLabels[topic.Id], bullets);
            })
            .ToArray();
    }

    private static bool StartsWithAny(string text, IEnumerable<string> names) =>
        names.Any(name => !string.IsNullOrWhiteSpace(name) &&
            System.Text.RegularExpressions.Regex.IsMatch(
                text,
                $@"^\W*{System.Text.RegularExpressions.Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    private static string SpeakerName(
        VisualizationSegment segment,
        IReadOnlyDictionary<string, string> speakers)
    {
        if (string.IsNullOrWhiteSpace(segment.SpeakerId))
            return "Unidentified speaker";
        return speakers.TryGetValue(segment.SpeakerId, out var displayName) &&
               !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : segment.SpeakerId;
    }

    private static string ProjectSpeakers(
        string text,
        IReadOnlyDictionary<string, string> speakers)
    {
        var projected = text;
        foreach (var speaker in speakers.OrderByDescending(item => item.Key.Length))
            projected = System.Text.RegularExpressions.Regex.Replace(
                projected,
                $@"(?<![\p{{L}}\p{{N}}_]){System.Text.RegularExpressions.Regex.Escape(speaker.Key)}(?![\p{{L}}\p{{N}}_])",
                _ => speaker.Value,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return projected.Trim();
    }

    private static string Bound(string text)
    {
        var compact = string.Join(' ', text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= BulletCharacterLimit)
            return compact;
        return compact[..(BulletCharacterLimit - 1)].TrimEnd() + "…";
    }
}

public record ParticipantInfo(string Name, string? Role);

public sealed class ActionItem
{
    public string Description { get; init; } = string.Empty;
    public string? Owner { get; init; }
    public string? DueDate { get; init; }
}
