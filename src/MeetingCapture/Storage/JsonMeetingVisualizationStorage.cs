using System.Security.Cryptography;
using System.Text.Json;
using MeetingCapture.Intelligence;

namespace MeetingCapture.Storage;

/// <summary>
/// Stores topic-map data as a JSON file beside the Markdown notes. The file records a SHA-256 hash
/// of the notes so a load can detect when the notes changed afterwards.
/// </summary>
public sealed class JsonMeetingVisualizationStorage : IMeetingVisualizationStorage
{
    internal const long MaxJsonBytes = 16 * 1024 * 1024;
    private const long MaxMarkdownBytes = 64 * 1024 * 1024;
    private const int MaxStringLength = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Envelope(
        int SchemaVersion, DateTimeOffset SavedAt, string? MarkdownSha256,
        MeetingVisualizationDocument? Document);

    public async Task SaveAsync(
        string markdownPath, MeetingVisualizationDocument document, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        // Validate before serializing so broken links or oversized collections never become saved state.
        if (!MeetingVisualizationValidator.IsValid(document))
            throw new InvalidOperationException("Visualization data is invalid.");
        if (new FileInfo(markdownPath).Length > MaxMarkdownBytes)
            throw new IOException("The Markdown transcript is too large to hash safely.");
        var markdown = await File.ReadAllBytesAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        var envelope = new Envelope(MeetingVisualizationSession.CurrentSchemaVersion,
            DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(markdown)), document);
        var path = Path.ChangeExtension(markdownPath, ".json");
        // The hash binds this sidecar to the exact Markdown bytes; the temporary file protects readers from partial JSON.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            if (!IsWithinJsonLimit(json.LongLength))
                throw new IOException("Visualization data is too large to save.");
            await File.WriteAllBytesAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<MeetingVisualizationLoadResult> LoadAsync(
        string markdownPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        var path = Path.ChangeExtension(markdownPath, ".json");
        if (!File.Exists(path))
            return new(MeetingVisualizationLoadKind.LegacyMissing, null);
        try
        {
            // Bound the input before parsing and cap JSON nesting to avoid spending unbounded work on a damaged file.
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxJsonBytes)
                return Corrupt();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion < 1)
                return Corrupt();
            if (schemaVersion > MeetingVisualizationSession.CurrentSchemaVersion)
                return new(MeetingVisualizationLoadKind.FutureSchema, null,
                    "Visualization data was created by a newer version.");
            // Inspect array and string sizes before materializing the object graph.
            if (!Preflight(json.RootElement, schemaVersion))
                return Corrupt();
            var envelope = json.RootElement.Deserialize<Envelope>(Json);
            if (envelope is null || envelope.SchemaVersion < 1 ||
                envelope.Document is null || envelope.MarkdownSha256 is null ||
                envelope.MarkdownSha256.Length != 64)
                return new(MeetingVisualizationLoadKind.Corrupt, null, "Visualization data is invalid.");
            if (!File.Exists(markdownPath))
                return new(MeetingVisualizationLoadKind.StaleMismatch, null, "The Markdown transcript is missing.");
            if (new FileInfo(markdownPath).Length > MaxMarkdownBytes)
                return new(MeetingVisualizationLoadKind.StaleMismatch, null,
                    "The Markdown transcript is too large to validate.");
            var markdown = await File.ReadAllBytesAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            var expected = Convert.FromHexString(envelope.MarkdownSha256);
            if (expected.Length != 32)
                return Corrupt();
            // Compare hashes in constant time and reject sidecars saved for older or different notes.
            if (!CryptographicOperations.FixedTimeEquals(
                    expected, SHA256.HashData(markdown)))
                return new(MeetingVisualizationLoadKind.StaleMismatch, null,
                    "Visualization data does not match the Markdown transcript.");
            var loadedDocument = envelope.SchemaVersion == 1
                // Schema 1 did not store the current document version, so upgrade it before validation.
                ? envelope.Document with { SchemaVersion = MeetingVisualizationSession.CurrentSchemaVersion }
                : envelope.Document;
            // Older related-context facts were suggestions, not meeting facts; keep that distinction on load.
            loadedDocument = loadedDocument with
            {
                Evidence = loadedDocument.Evidence.Select(item =>
                    item.Origin == EvidenceOrigin.RelatedContext &&
                    item.Provenance is VisualizationProvenance.RetrievedFact or
                        VisualizationProvenance.Inference
                        ? item with
                        {
                            Provenance = VisualizationProvenance.RelatedSourceSuggestion
                        }
                        : item).ToArray()
            };
            if (!MeetingVisualizationValidator.IsValid(loadedDocument))
                return new(MeetingVisualizationLoadKind.Corrupt, null, "Visualization data is invalid.");
            return new(MeetingVisualizationLoadKind.Valid, loadedDocument);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or InvalidOperationException)
        {
            return new(MeetingVisualizationLoadKind.Corrupt, null, "Visualization data could not be read.");
        }
    }

    private static MeetingVisualizationLoadResult Corrupt() =>
        new(MeetingVisualizationLoadKind.Corrupt, null, "Visualization data is invalid.");

    internal static bool IsWithinJsonLimit(long bytes) => bytes is > 0 and <= MaxJsonBytes;

    private static bool Preflight(JsonElement root, int schemaVersion)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("document", out var document) ||
            document.ValueKind != JsonValueKind.Object)
            return false;
        // Limits are checked before deserialization so a small-looking file cannot contain huge arrays.
        var limits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["segments"] = 100_000,
            ["speakers"] = 10_000,
            ["topicClusters"] = 10_000,
            ["topicTrail"] = 50_000,
            ["questions"] = 10_000,
            ["evidence"] = 50_000,
            ["relationships"] = 50_000
        };
        if (schemaVersion >= 2)
        {
            limits["actions"] = 10_000;
            limits["decisions"] = 10_000;
        }
        foreach (var (name, maximum) in limits)
            if (!document.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
                return false;
        return StringsWithinBounds(root);
    }

    private static bool StringsWithinBounds(JsonElement element)
    {
        // Check each parsed string before deserializing the document into application model objects.
        if (element.ValueKind == JsonValueKind.String)
            return (element.GetString()?.Length ?? 0) <= MaxStringLength;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().All(StringsWithinBounds);
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().All(property => StringsWithinBounds(property.Value));
        return true;
    }
}

/// <summary>Checks that topic-map data is well formed and within size limits before it is saved or used.</summary>
public static class MeetingVisualizationValidator
{
    private const int MaxIdLength = 256;
    private const int MaxLabelLength = 2_000;
    private const int MaxTextLength = 100_000;

    public static bool IsValid(MeetingVisualizationDocument? document)
    {
        // First reject null collections, unsupported versions, impossible counters, and oversized top-level data.
        if (document is null ||
            document.Segments is null || document.Speakers is null ||
            document.TopicClusters is null || document.TopicTrail is null ||
            document.Questions is null || document.Evidence is null ||
            document.ExplainedTopicLinks is null || document.Actions is null ||
            document.Decisions is null ||
            document.SchemaVersion != MeetingVisualizationSession.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(document.SessionId) || document.TranscriptRevision < 0 ||
            document.Segments.Count > 100_000 || document.TopicClusters.Count > 10_000 ||
            document.Speakers.Count > 10_000 || document.TopicTrail.Count > 50_000 ||
            document.Evidence.Count > 50_000 || document.Questions.Count > 10_000 ||
            document.ExplainedTopicLinks.Count > 50_000 ||
            document.Actions.Count > 10_000 || document.Decisions.Count > 10_000 ||
            document.LastAnalyzedOrdinal < 0 ||
            document.LastAnalyzedOrdinal > document.Segments.Count ||
            document.ReanalysisFromOrdinal is < 1 ||
            document.ReanalysisFromOrdinal > document.Segments.Count ||
            !ValidId(document.SessionId) ||
            !ValidText(document.MeetingTitle, MaxLabelLength) ||
            (document.EndedAt is { } endedAt && endedAt < document.StartedAt))
            return false;
        if (document.Segments.Any(item => item is null) ||
            document.Speakers.Any(item => item is null) ||
            document.TopicClusters.Any(item => item is null) ||
            document.TopicTrail.Any(item => item is null) ||
            document.Questions.Any(item => item is null) ||
            document.Evidence.Any(item => item is null) ||
            document.ExplainedTopicLinks.Any(item => item is null) ||
            document.Actions.Any(item => item is null) ||
            document.Decisions.Any(item => item is null))
            return false;
        // IDs are used as dictionary keys and cross-links, so duplicates would make a loaded map ambiguous.
        if (!Unique(document.Segments.Select(item => item.Id)) ||
            !Unique(document.Speakers.Select(item => item.SpeakerId), StringComparer.OrdinalIgnoreCase) ||
            !Unique(document.TopicClusters.Select(item => item.Id)) ||
            !Unique(document.TopicTrail.Select(item => item.Id)) ||
            !Unique(document.Evidence.Select(item => item.Id)) ||
            !Unique(document.Questions.Select(item => item.Id)) ||
            !Unique(document.ExplainedTopicLinks.Select(item => item.Id)) ||
            !Unique(document.Actions.Select(item => item.Id)) ||
            !Unique(document.Decisions.Select(item => item.Id)))
            return false;
        var ordinals = document.Segments.Select(item => item.Ordinal).ToHashSet();
        var topics = document.TopicClusters.Select(item => item.Id).ToHashSet();
        var evidence = document.Evidence.Select(item => item.Id).ToHashSet();
        var speakers = document.Speakers.Select(item => item.SpeakerId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidenceById = document.Evidence.ToDictionary(item => item.Id);
        // A topic may have at most one analysis fact for the same displayed claim.
        if (document.Evidence
            .Where(item => item.Origin == EvidenceOrigin.Analysis &&
                item.Provenance == VisualizationProvenance.TranscriptFact)
            .GroupBy(item => $"{item.TopicId}\0{item.PresentationLabel}",
                StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            return false;
        // Validate the relationships only after all referenced ID sets have been built.
        return document.Segments.Select(item => item.Ordinal).SequenceEqual(
                Enumerable.Range(1, document.Segments.Count)) &&
            document.Segments.All(item => item.Id == $"seg-{item.Ordinal:D6}" &&
                ValidText(item.Text, MaxTextLength) &&
                (item.SpeakerId is null || speakers.Contains(item.SpeakerId)) &&
                (item.ConfidenceScore is null or >= 0 and <= 1)) &&
            document.Speakers.All(item => !string.IsNullOrWhiteSpace(item.SpeakerId) &&
                ValidId(item.SpeakerId) && ValidText(item.DisplayName, MaxLabelLength)) &&
            document.TopicClusters.All(item => item.Id.StartsWith("topic-", StringComparison.Ordinal) &&
                ValidId(item.Id) && ValidText(item.Label, MaxLabelLength) &&
                item.Aliases is { Count: > 0 and <= 100 } &&
                item.Aliases.All(alias => ValidText(alias, MaxLabelLength)) &&
                item.FirstOccurrenceOrdinal >= 0 &&
                item.LastOccurrenceOrdinal >= item.FirstOccurrenceOrdinal &&
                item.LastOccurrenceOrdinal <= document.Segments.Count) &&
            document.TopicTrail.All(item => topics.Contains(item.TopicId) &&
                item.Id.StartsWith("occ-", StringComparison.Ordinal) &&
                ValidId(item.Id) &&
                item.TranscriptRevision >= 0 && item.TranscriptRevision <= document.TranscriptRevision &&
                ValidText(item.LabelAtTime, MaxLabelLength) &&
                item.ThroughSegmentOrdinal >= 0 && item.ThroughSegmentOrdinal <= document.Segments.Count) &&
            document.Evidence.All(item => ValidEvidence(item, ordinals, topics)) &&
            document.ExplainedTopicLinks.All(item => ValidId(item.Id) &&
                Enum.IsDefined(item.Kind) && Enum.IsDefined(item.Provenance) &&
                topics.Contains(item.FromTopicId) &&
                topics.Contains(item.ToTopicId) && item.FromTopicId != item.ToTopicId &&
                item.EvidenceIds is { Count: > 0 and <= 100 } &&
                item.EvidenceIds.All(evidence.Contains) &&
                (item.Provenance == VisualizationProvenance.TranscriptFact
                    ? string.IsNullOrWhiteSpace(item.InferenceLabel)
                    : item.Provenance == VisualizationProvenance.Inference &&
                      ValidText(item.InferenceLabel, 120)) &&
                item.EvidenceIds.All(id =>
                    evidenceById[id].Provenance == VisualizationProvenance.TranscriptFact) &&
                item.EvidenceIds.Any(id => evidenceById[id].TopicId == item.FromTopicId) &&
                item.EvidenceIds.Any(id => evidenceById[id].TopicId == item.ToTopicId)) &&
            document.Questions.All(item => ValidId(item.Id) &&
                Enum.IsDefined(item.QuestionState) && ValidText(item.Text, 240) &&
                (item.TopicId is null || topics.Contains(item.TopicId)) &&
                item.EvidenceIds is { Count: > 0 and <= 100 } &&
                item.EvidenceIds.All(evidence.Contains) &&
                item.EvidenceIds.Any(id =>
                    evidenceById[id].Provenance == VisualizationProvenance.TranscriptFact)) &&
            document.Actions.All(item => ValidGroundedStateItem(
                item.Id, item.TopicId, item.Text, item.EvidenceIds,
                topics, evidence, evidenceById) && Enum.IsDefined(item.State)) &&
            document.Decisions.All(item => ValidGroundedStateItem(
                item.Id, item.TopicId, item.Text, item.EvidenceIds,
                topics, evidence, evidenceById));
    }

    private static bool ValidEvidence(
        VisualizationEvidence item, IReadOnlySet<int> ordinals, IReadOnlySet<string> topics) =>
        !string.IsNullOrWhiteSpace(item.Id) &&
        ValidId(item.Id) &&
        Enum.IsDefined(item.Origin) && Enum.IsDefined(item.Provenance) &&
        (item.TopicId is null || topics.Contains(item.TopicId)) &&
        (item.SourceType is null || Enum.IsDefined(item.SourceType.Value)) &&
        (item.SourceUrl is null || SafeHttps(item.SourceUrl)) &&
        (item.SourceTitle is null || ValidText(item.SourceTitle, MaxLabelLength)) &&
        (item.Summary is null || ValidText(item.Summary, MaxTextLength)) &&
        (item.PresentationLabel is null || ValidPresentationLabel(item.PresentationLabel)) &&
        (item.RelevanceRationale is null || ValidText(item.RelevanceRationale, 240)) &&
        (item.InvolvementRationale is null || ValidText(item.InvolvementRationale, 240)) &&
        (item.InvolvementRole is null || Enum.IsDefined(item.InvolvementRole.Value)) &&
        (item.InvolvementMatchKind is null || Enum.IsDefined(item.InvolvementMatchKind.Value)) &&
        (item.SourceId is null || ValidText(item.SourceId, 512)) &&
        (item.SuggestionId is null || ValidText(item.SuggestionId, 128)) &&
        item.Provenance switch
        {
            VisualizationProvenance.TranscriptFact =>
                item.Origin != EvidenceOrigin.RelatedContext &&
                item.SegmentOrdinal is int ordinal && ordinals.Contains(ordinal) &&
                (item.Origin != EvidenceOrigin.Analysis ||
                    item.Summary is { Length: > 0 and <= 480 }),
            VisualizationProvenance.RelatedSourceSuggestion =>
                item.Origin == EvidenceOrigin.RelatedContext &&
                !string.IsNullOrWhiteSpace(item.SourceTitle) && SafeHttps(item.SourceUrl),
            VisualizationProvenance.Inference =>
                item.Origin == EvidenceOrigin.Analysis &&
                !string.IsNullOrWhiteSpace(item.Summary),
            _ => false
        };

    private static bool ValidPresentationLabel(string value)
    {
        if (!ValidText(value, 120)) return false;
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 8;
    }

    private static bool ValidGroundedStateItem(
        string id,
        string? topicId,
        string text,
        IReadOnlyList<string> evidenceIds,
        IReadOnlySet<string> topics,
        IReadOnlySet<string> evidence,
        IReadOnlyDictionary<string, VisualizationEvidence> evidenceById) =>
        ValidId(id) && ValidText(text, 240) &&
        (topicId is null || topics.Contains(topicId)) &&
        evidenceIds is { Count: > 0 and <= 20 } &&
        evidenceIds.All(evidence.Contains) &&
        evidenceIds.All(id => evidenceById[id].Provenance == VisualizationProvenance.TranscriptFact);

    private static bool SafeHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    private static bool Unique(IEnumerable<string> values, IEqualityComparer<string>? comparer = null)
    {
        var items = values.ToArray();
        return items.All(item => !string.IsNullOrWhiteSpace(item)) &&
            items.Distinct(comparer ?? StringComparer.Ordinal).Count() == items.Length;
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdLength;

    private static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
