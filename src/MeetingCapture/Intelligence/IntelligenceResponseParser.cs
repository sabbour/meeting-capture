using System.Text.Json;
using MeetingCapture.Transcription;

namespace MeetingCapture.Intelligence;

/// <summary>
/// Validates and converts raw model responses into summaries, topics, and insight lists.
/// Throws <see cref="IntelligenceException"/> when a response is empty or malformed.
/// </summary>
public static class IntelligenceResponseParser
{
    public static string ParseSummary(string response, string provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw IntelligenceErrors.Create(IntelligenceFailureKind.EmptyResponse, provider, model);
        return response.Trim();
    }

    public static List<string> ParseInsights(string response, string provider, string? model)
    {
        ParseSummary(response, provider, model);
        try
        {
            using var document = JsonDocument.Parse(response);
            var values = Strings(document.RootElement);
            if (values.Count > 5) throw new JsonException();
            return values;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, provider, model);
        }
    }

    /// <summary>Upper bound for a catch-up recap, which is prose rather than a topic label.</summary>
    public const int CatchUpCharacterLimit = 1200;

    public static string ParseTopic(string response, string provider, string? model)
        => ParseTopicValue(
            response, provider, model, MeetingVisualizationSession.TopicLabelCharacterLimit);

    /// <summary>
    /// Parses the same single-property response shape as <see cref="ParseTopic"/> with the
    /// catch-up length bound, so a grounded multi-sentence recap is not rejected as malformed.
    /// </summary>
    public static string ParseCatchUp(string response, string provider, string? model)
        => ParseTopicValue(response, provider, model, CatchUpCharacterLimit);

    private static string ParseTopicValue(
        string response, string provider, string? model, int maximumLength)
    {
        ParseSummary(response, provider, model);
        try
        {
            using var document = ParseJsonObjectResponse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1)
                throw new JsonException();
            return RequiredString(root.GetProperty("topic"), maximumLength);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, provider, model);
        }
    }

    public static MeetingNotes ParseNotes(
        string title, TranscriptionResult transcript, string response, string provider, string? model)
    {
        ParseSummary(response, provider, model);
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var notes = new MeetingNotes
            {
                MeetingTitle = title,
                RawTranscript = transcript.FullText,
                Summary = RequiredString(root.GetProperty("summary")),
                MeetingType = RequiredString(root.GetProperty("meetingType")),
                Sentiment = RequiredString(root.GetProperty("sentiment")),
                EstimatedDurationMinutes = root.GetProperty("estimatedDurationMinutes").GetInt32()
            };
            if (notes.EstimatedDurationMinutes < 0) throw new JsonException();
            if (root.TryGetProperty("nuances", out var nuances))
                notes.Nuances = OptionalString(nuances) ?? string.Empty;
            notes.Topics.AddRange(Strings(root.GetProperty("topics")));
            notes.KeyDiscussionPoints.AddRange(Strings(root.GetProperty("keyDiscussionPoints")));
            notes.Decisions.AddRange(Strings(root.GetProperty("decisions")));
            notes.OpenQuestions.AddRange(Strings(root.GetProperty("openQuestions")));
            notes.NotableQuotes.AddRange(Strings(root.GetProperty("notableQuotes")));
            notes.FollowUpMeetings.AddRange(Strings(root.GetProperty("followUpMeetings")));
            foreach (var participant in root.GetProperty("participants").EnumerateArray())
                notes.Participants.Add(new(
                    RequiredString(participant.GetProperty("name")),
                    OptionalString(participant.GetProperty("role"))));
            foreach (var action in root.GetProperty("actionItems").EnumerateArray())
                notes.ActionItems.Add(new()
                {
                    Description = RequiredString(action.GetProperty("description")),
                    Owner = OptionalString(action.GetProperty("owner")),
                    DueDate = OptionalString(action.GetProperty("dueDate"))
                });
            return notes;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, provider, model);
        }
    }

    public static MeetingVisualizationAnalysisResult ParseVisualizationAnalysis(
        string response, string provider, string? model)
    {
        ParseSummary(response, provider, model);
        try
        {
            using var document = ParseJsonObjectResponse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var updateMode = RequiredEnum<MeetingVisualizationUpdateMode>(
                root.GetProperty("updateMode"));
            var assignments = root.GetProperty("topicAssignments").EnumerateArray()
                    .Select(item => new VisualizationTopicAssignment(
                    RequiredString(item.GetProperty("occurrenceId")),
                    RequiredString(
                        item.GetProperty("label"),
                        MeetingVisualizationSession.TopicLabelCharacterLimit),
                    NullableString(item, "redirectToTopicId"),
                    item.GetProperty("throughSegmentOrdinal").GetInt32()))
                    .TakeBounded(20);
                var evidence = root.GetProperty("evidence").EnumerateArray()
                    .Select(item => new VisualizationEvidence(
                        RequiredString(item.GetProperty("id")),
                        EvidenceOrigin.Analysis,
                        AnalysisProvenance(item.GetProperty("provenance")),
                        NullableInt(item, "segmentOrdinal"),
                        NullableString(item, "topicId"),
                        null, null, null, null,
                        RequiredString(item.GetProperty("summary"), 480),
                        NullableString(item, "presentationLabel", 120)))
                    .TakeBounded(100);
                if (evidence
                    .Where(item => item.Provenance == VisualizationProvenance.TranscriptFact)
                    .GroupBy(item => $"{item.TopicId}\0{item.PresentationLabel}",
                        StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
                    throw new JsonException();
                var questions = root.GetProperty("questions").EnumerateArray()
                    .Select(item => new DiscussionQuestion(
                        RequiredString(item.GetProperty("id")),
                        NullableString(item, "topicId"),
                        RequiredString(item.GetProperty("text"), 240),
                        RequiredEnum<QuestionState>(item.GetProperty("questionState")),
                        StringArray(item.GetProperty("evidenceIds"), 20)))
                    .TakeBounded(50);
                var explainedTopicLinks = root.GetProperty("explainedTopicLinks").EnumerateArray()
                    .Select(item => new ExplainedTopicLink(
                        RequiredString(item.GetProperty("id")),
                        RequiredString(item.GetProperty("fromTopicId")),
                        RequiredString(item.GetProperty("toTopicId")),
                        RequiredEnum<ExplainedTopicLinkKind>(item.GetProperty("kind")),
                        RequiredEnum<VisualizationProvenance>(item.GetProperty("provenance")),
                        StringArray(item.GetProperty("evidenceIds"), 20),
                        NullableString(item, "inferenceLabel")))
                    .TakeBounded(100);
                var actions = root.GetProperty("actions").EnumerateArray()
                    .Select(item => new VisualizationAction(
                        RequiredString(item.GetProperty("id")),
                        NullableString(item, "topicId"),
                        RequiredString(item.GetProperty("text"), 240),
                        StringArray(item.GetProperty("evidenceIds"), 20))
                    {
                        State = RequiredEnum<VisualizationActionState>(
                            item.GetProperty("actionState"))
                    })
                    .TakeBounded(20);
                var decisions = root.GetProperty("decisions").EnumerateArray()
                    .Select(item => new VisualizationDecision(
                        RequiredString(item.GetProperty("id")),
                        NullableString(item, "topicId"),
                        RequiredString(item.GetProperty("text"), 240),
                        StringArray(item.GetProperty("evidenceIds"), 20)))
                    .TakeBounded(20);
            return new(assignments, new MeetingVisualizationAnalysisDelta(
                questions, evidence, explainedTopicLinks)
            {
                Actions = actions,
                Decisions = decisions
            })
            {
                UpdateMode = updateMode,
                ThroughSegmentOrdinal = root.GetProperty("throughSegmentOrdinal").GetInt32()
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, provider, model);
        }
    }

    public static string ParseModelValidation(string response, string provider, string? model)
    {
        var reply = ParseSummary(response, provider, model);
        if (!string.Equals(reply, "OK", StringComparison.OrdinalIgnoreCase))
            throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, provider, model);
        return model ?? provider;
    }

    private static List<string> Strings(JsonElement element)
        => element.EnumerateArray().Select(item => RequiredString(item)).ToList();

    internal static string VisualizationResponseContext(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            return "code-fenced JSON response";
        if (!trimmed.StartsWith('{'))
            return "response did not begin with a JSON object";
        return "JSON object did not satisfy the required visualization schema";
    }

    private static JsonDocument ParseJsonObjectResponse(string response)
    {
        var payload = response.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal) &&
            payload.EndsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = payload.IndexOf('\n');
            if (firstNewline < 0)
                throw new JsonException();
            payload = payload[(firstNewline + 1)..^3].Trim();
        }
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
    }

    private static string RequiredString(JsonElement element)
        => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new JsonException();

    private static string RequiredString(JsonElement element, int maximumLength)
    {
        var value = RequiredString(element);
        return value.Length <= maximumLength ? value : throw new JsonException();
    }

    private static string? OptionalString(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : RequiredString(element);

    private static string? NullableString(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : RequiredString(value);

    private static string? NullableString(JsonElement parent, string name, int maximumLength) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : RequiredString(value, maximumLength);

    private static int? NullableInt(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : value.GetInt32();

    private static T RequiredEnum<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.String &&
        Enum.TryParse<T>(element.GetString(), ignoreCase: true, out var value) &&
        Enum.IsDefined(value)
            ? value : throw new JsonException();

    private static VisualizationProvenance AnalysisProvenance(JsonElement element)
    {
        var value = RequiredEnum<VisualizationProvenance>(element);
        return value is VisualizationProvenance.TranscriptFact or VisualizationProvenance.Inference
            ? value
            : throw new JsonException();
    }

    private static IReadOnlyList<string> StringArray(JsonElement element, int maximum)
    {
        var values = Strings(element);
        if (values.Count > maximum) throw new JsonException();
        return values;
    }

    private static IReadOnlyList<T> TakeBounded<T>(this IEnumerable<T> values, int maximum)
    {
        var result = values.Take(maximum + 1).ToArray();
        if (result.Length > maximum) throw new JsonException();
        return result;
    }
}
