using System.Text.Json.Serialization;

namespace MeetingCapture.Intelligence;

/// <summary>
/// The saved model of a meeting used for the topic map: transcript segments, speakers, topics,
/// questions, evidence, links between topics, actions, and decisions. Items refer to each other by ID.
/// </summary>
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
    /// <summary>Segment number up to which analysis has already been applied.</summary>
    public int LastAnalyzedOrdinal { get; init; }

    /// <summary>When set, the segment number from which analysis must be redone.</summary>
    public int? ReanalysisFromOrdinal { get; init; }
}

/// <summary>One piece of transcript text. <c>Ordinal</c> gives its position in the transcript.</summary>
public sealed record VisualizationSegment(
    string Id, int Ordinal, string Text, DateTimeOffset Timestamp,
    string? SpeakerId, double? ConfidenceScore);

/// <summary>A speaker, with a flag that tells whether the name is confirmed.</summary>
public sealed record VisualizationSpeaker(string SpeakerId, string DisplayName, bool IsConfirmed);

/// <summary>A discussion topic with its current label, earlier labels, and the segment range where it appears.</summary>
public sealed record TopicCluster(
    string Id, string Label, IReadOnlyList<string> Aliases,
    int FirstOccurrenceOrdinal, int LastOccurrenceOrdinal);

/// <summary>One entry in the order topics were discussed, valid through a given segment.</summary>
public sealed record TopicOccurrence(
    string Id, string TopicId, long TranscriptRevision,
    int ThroughSegmentOrdinal, string LabelAtTime);

/// <summary>Whether a discussion question has been answered.</summary>
public enum QuestionState { Open, Answered }

/// <summary>A question raised in the meeting, linked to the evidence that supports it.</summary>
public sealed record DiscussionQuestion(
    string Id, string? TopicId, string Text,
    QuestionState QuestionState, IReadOnlyList<string> EvidenceIds);

/// <summary>Where a piece of evidence comes from.</summary>
public enum EvidenceOrigin { Transcript, RelatedContext, Analysis }

/// <summary>How well an item is backed: stated in the transcript, retrieved from a source, inferred, or suggested.</summary>
public enum VisualizationProvenance
{
    TranscriptFact,
    RetrievedFact,
    Inference,
    RelatedSourceSuggestion
}

/// <summary>
/// A supporting fact for a topic, question, action, or decision. It points to a transcript
/// segment or to a related source such as a meeting, message, email, or document.
/// </summary>
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

/// <summary>How one topic relates to another.</summary>
public enum ExplainedTopicLinkKind { Supports, Contradicts, DependsOn, BuildsOn }

/// <summary>A link between two topics with its kind, provenance, and supporting evidence.</summary>
public sealed record ExplainedTopicLink(
    string Id, string FromTopicId, string ToTopicId,
    ExplainedTopicLinkKind Kind, VisualizationProvenance Provenance,
    IReadOnlyList<string> EvidenceIds, string? InferenceLabel);

/// <summary>Progress of an action item.</summary>
public enum VisualizationActionState { Open, Completed, Withdrawn }

/// <summary>An action item found in the discussion.</summary>
public sealed record VisualizationAction(
    string Id, string? TopicId, string Text, IReadOnlyList<string> EvidenceIds)
{
    public VisualizationActionState State { get; init; } = VisualizationActionState.Open;
}

/// <summary>A decision found in the discussion.</summary>
public sealed record VisualizationDecision(
    string Id, string? TopicId, string Text, IReadOnlyList<string> EvidenceIds);

/// <summary>New questions, evidence, links, actions, and decisions found by one analysis pass.</summary>
public sealed record MeetingVisualizationAnalysisDelta(
    IReadOnlyList<DiscussionQuestion> Questions,
    IReadOnlyList<VisualizationEvidence> Evidence,
    IReadOnlyList<ExplainedTopicLink> ExplainedTopicLinks)
{
    public IReadOnlyList<VisualizationAction> Actions { get; init; } = [];
    public IReadOnlyList<VisualizationDecision> Decisions { get; init; } = [];
}

/// <summary>
/// Sets the label of a topic occurrence, optionally merging it into an existing topic
/// (<c>RedirectToTopicId</c>).
/// </summary>
public sealed record VisualizationTopicAssignment(
    string OccurrenceId, string Label, string? RedirectToTopicId, int ThroughSegmentOrdinal);

/// <summary>How an analysis result is applied to the document. Only delta updates exist.</summary>
public enum MeetingVisualizationUpdateMode { Delta }

/// <summary>Result of one analysis pass: topic labels plus a delta to apply to the document.</summary>
public sealed record MeetingVisualizationAnalysisResult(
    IReadOnlyList<VisualizationTopicAssignment> TopicAssignments,
    MeetingVisualizationAnalysisDelta Delta)
{
    public MeetingVisualizationUpdateMode UpdateMode { get; init; } =
        MeetingVisualizationUpdateMode.Delta;
    public int? ThroughSegmentOrdinal { get; init; }
    public IReadOnlyList<int> SuppliedSegmentOrdinals { get; init; } = [];
}
