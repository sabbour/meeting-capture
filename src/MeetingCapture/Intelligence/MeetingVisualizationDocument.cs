using System.Text.Json.Serialization;

namespace MeetingCapture.Intelligence;

public sealed record MeetingVisualizationDocument(
    int SchemaVersion,
    string SessionId,
    string MeetingTitle,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long TranscriptRevision,
    bool IsComplete,
    IReadOnlyList<VisualizationSegment> Segments,
    IReadOnlyList<VisualizationSpeaker> Speakers,
    IReadOnlyList<TopicCluster> TopicClusters,
    IReadOnlyList<TopicOccurrence> TopicTrail,
    IReadOnlyList<DiscussionQuestion> Questions,
    IReadOnlyList<VisualizationEvidence> Evidence,
    [property: JsonPropertyName("relationships")]
    IReadOnlyList<ExplainedTopicLink> ExplainedTopicLinks)
{
    public IReadOnlyList<VisualizationAction> Actions { get; init; } = [];
    public IReadOnlyList<VisualizationDecision> Decisions { get; init; } = [];
    public int LastAnalyzedOrdinal { get; init; }
    public int? ReanalysisFromOrdinal { get; init; }
}

public sealed record VisualizationSegment(
    string Id, int Ordinal, string Text, DateTimeOffset Timestamp,
    string? SpeakerId, double? ConfidenceScore);

public sealed record VisualizationSpeaker(string SpeakerId, string DisplayName, bool IsConfirmed);

public sealed record TopicCluster(
    string Id, string Label, IReadOnlyList<string> Aliases,
    int FirstOccurrenceOrdinal, int LastOccurrenceOrdinal);

public sealed record TopicOccurrence(
    string Id, string TopicId, long TranscriptRevision,
    int ThroughSegmentOrdinal, string LabelAtTime);

public enum QuestionState { Open, Answered }

public sealed record DiscussionQuestion(
    string Id, string? TopicId, string Text,
    QuestionState QuestionState, IReadOnlyList<string> EvidenceIds);

public enum EvidenceOrigin { Transcript, RelatedContext, Analysis }
public enum VisualizationProvenance
{
    TranscriptFact,
    RetrievedFact,
    Inference,
    RelatedSourceSuggestion
}

public sealed record VisualizationEvidence(
    string Id, EvidenceOrigin Origin, VisualizationProvenance Provenance,
    int? SegmentOrdinal, string? TopicId, string? SourceTitle, string? SourceUrl,
    ContextSourceType? SourceType, DateTimeOffset? SourceDate, string? Summary,
    string? PresentationLabel = null,
    string? RelevanceRationale = null,
    string? InvolvementRationale = null,
    ContextInvolvementRole? InvolvementRole = null,
    ContextIdentityMatchKind? InvolvementMatchKind = null,
    string? SourceId = null,
    string? SuggestionId = null);

public enum ExplainedTopicLinkKind { Supports, Contradicts, DependsOn, BuildsOn }

public sealed record ExplainedTopicLink(
    string Id, string FromTopicId, string ToTopicId,
    ExplainedTopicLinkKind Kind, VisualizationProvenance Provenance,
    IReadOnlyList<string> EvidenceIds, string? InferenceLabel);

public enum VisualizationActionState { Open, Completed, Withdrawn }

public sealed record VisualizationAction(
    string Id, string? TopicId, string Text, IReadOnlyList<string> EvidenceIds)
{
    public VisualizationActionState State { get; init; } = VisualizationActionState.Open;
}

public sealed record VisualizationDecision(
    string Id, string? TopicId, string Text, IReadOnlyList<string> EvidenceIds);

public sealed record MeetingVisualizationAnalysisDelta(
    IReadOnlyList<DiscussionQuestion> Questions,
    IReadOnlyList<VisualizationEvidence> Evidence,
    IReadOnlyList<ExplainedTopicLink> ExplainedTopicLinks)
{
    public IReadOnlyList<VisualizationAction> Actions { get; init; } = [];
    public IReadOnlyList<VisualizationDecision> Decisions { get; init; } = [];
}

public sealed record VisualizationTopicAssignment(
    string OccurrenceId, string Label, string? RedirectToTopicId, int ThroughSegmentOrdinal);

public enum MeetingVisualizationUpdateMode { Delta }

public sealed record MeetingVisualizationAnalysisResult(
    IReadOnlyList<VisualizationTopicAssignment> TopicAssignments,
    MeetingVisualizationAnalysisDelta Delta)
{
    public MeetingVisualizationUpdateMode UpdateMode { get; init; } =
        MeetingVisualizationUpdateMode.Delta;
    public int? ThroughSegmentOrdinal { get; init; }
    public IReadOnlyList<int> SuppliedSegmentOrdinals { get; init; } = [];
}
