using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using MeetingCapture.Transcription;

namespace MeetingCapture.Intelligence;

public sealed class MeetingVisualizationSession
{
    internal const int TopicLabelCharacterLimit = 80;
    public const int CurrentSchemaVersion = 2;
    private readonly object _gate = new();
    private readonly List<VisualizationSegment> _segments = [];
    private readonly Dictionary<string, VisualizationSpeaker> _speakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TopicCluster> _topics = [];
    private readonly List<TopicOccurrence> _trail = [];
    private readonly Dictionary<string, DiscussionQuestion> _questions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VisualizationEvidence> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExplainedTopicLink> _explainedTopicLinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VisualizationAction> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VisualizationDecision> _decisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _topicRedirects = new(StringComparer.Ordinal);
    private readonly Queue<long> _pendingChangedRevisions = [];
    private string _sessionId = string.Empty;
    private string _title = string.Empty;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _endedAt;
    private long _transcriptRevision;
    private long _revision;
    private bool _complete;
    private bool _closed;
    private int _nextTopic;
    private int _nextOccurrence;
    private int _nextEvidence;
    private int _lastAnalyzedOrdinal;
    private int? _reanalysisFromOrdinal;
    private bool _publishingChanges;

    public event EventHandler<long>? Changed;
    public long Revision { get { lock (_gate) return _revision; } }
    public bool HasActiveSession { get { lock (_gate) return _sessionId.Length > 0 && !_closed; } }

    public void Begin(string sessionId, string meetingTitle, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        bool publish;
        lock (_gate)
        {
            ClearLocked();
            _sessionId = sessionId;
            _title = Clean(meetingTitle);
            _startedAt = startedAt;
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    public void AppendOrUpdateSegment(int ordinal, TranscriptionSegment segment)
        => UpdateSegment(null, ordinal, segment, null);

    internal bool TryAppendOrUpdateSegment(
        string sessionId, int ordinal, TranscriptionSegment segment, Func<bool> isCurrent)
        => UpdateSegment(sessionId, ordinal, segment, isCurrent);

    private bool UpdateSegment(
        string? expectedSessionId, int ordinal, TranscriptionSegment segment, Func<bool>? isCurrent)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (ordinal < 1 || string.IsNullOrWhiteSpace(segment.Text))
            throw new ArgumentException("A positive ordinal and transcript text are required.");
        bool publish;
        lock (_gate)
        {
            if (expectedSessionId is null)
                RequireMutable();
            else if (_closed || _sessionId != expectedSessionId || isCurrent is null || !isCurrent())
                return false;
            var value = new VisualizationSegment($"seg-{ordinal:D6}", ordinal, segment.Text,
                segment.Timestamp, segment.SpeakerId, segment.ConfidenceScore);
            var index = _segments.FindIndex(item => item.Ordinal == ordinal);
            if (index >= 0)
            {
                if (_segments[index] != value)
                {
                    _segments[index] = value;
                    InvalidateAnalysisFromLocked(ordinal);
                }
            }
            else
            {
                if (ordinal != _segments.Count + 1)
                    throw new InvalidOperationException("Segment ordinals must be contiguous.");
                _segments.Add(value);
            }
            _transcriptRevision = Math.Max(_transcriptRevision, ordinal);
            if (!string.IsNullOrWhiteSpace(segment.SpeakerId) && !_speakers.ContainsKey(segment.SpeakerId))
                _speakers.Add(segment.SpeakerId, new(segment.SpeakerId,
                    segment.SpeakerDisplayName ?? segment.SpeakerId, false));
            publish = ChangedLocked();
        }
        PublishChanged(publish);
        return true;
    }

    public void ApplySpeakerAssignment(string speakerId, string displayName, bool isConfirmed = true)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(displayName)) return;
        bool publish;
        lock (_gate)
        {
            RequireSession();
            _speakers[speakerId.Trim()] = new(speakerId.Trim(), displayName.Trim(), isConfirmed);
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    public TopicCluster AcceptTopic(
        string label, long transcriptRevision, int throughSegmentOrdinal, string? existingTopicId = null)
    {
        var clean = Clean(label);
        if (clean.Length == 0 || clean.Length > TopicLabelCharacterLimit)
            throw new ArgumentException(
                $"Topic label must contain at most {TopicLabelCharacterLimit} characters.",
                nameof(label));
        TopicCluster result;
        bool publish;
        lock (_gate)
        {
            RequireMutable();
            if (throughSegmentOrdinal < 0 || throughSegmentOrdinal > _segments.Count ||
                transcriptRevision < 0 || transcriptRevision > _transcriptRevision)
                throw new ArgumentOutOfRangeException(nameof(throughSegmentOrdinal));

            TopicCluster topic;
            if (existingTopicId is not null)
            {
                existingTopicId = ResolveTopicIdLocked(existingTopicId);
                topic = _topics.SingleOrDefault(item => item.Id == existingTopicId)
                    ?? throw new InvalidOperationException("The supplied topic ID does not exist.");
            }
            else
            {
                var id = $"topic-{++_nextTopic:D4}";
                topic = new(id, clean, [clean], throughSegmentOrdinal, throughSegmentOrdinal);
                _topics.Add(topic);
            }

            var aliases = topic.Aliases.Contains(clean, StringComparer.OrdinalIgnoreCase)
                ? topic.Aliases : topic.Aliases.Append(clean).ToArray();
            var updated = topic with
            {
                Label = clean,
                Aliases = aliases,
                FirstOccurrenceOrdinal = Math.Min(topic.FirstOccurrenceOrdinal, throughSegmentOrdinal),
                LastOccurrenceOrdinal = Math.Max(topic.LastOccurrenceOrdinal, throughSegmentOrdinal)
            };
            _topics[_topics.IndexOf(topic)] = updated;
            if (_trail.Count == 0 || ResolveTopicIdLocked(_trail[^1].TopicId) != updated.Id)
                _trail.Add(new($"occ-{++_nextOccurrence:D6}", updated.Id, transcriptRevision,
                    throughSegmentOrdinal, clean));
            publish = ChangedLocked();
            result = updated;
        }
        PublishChanged(publish);
        return result;
    }

    public void ApplyRelatedContext(string topicId, RelatedContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var publish = false;
        lock (_gate)
        {
            RequireMutable();
            topicId = ResolveTopicIdLocked(topicId);
            if (_topics.All(topic => topic.Id != topicId))
                throw new InvalidOperationException("Related context references an unknown topic.");
            var changed = false;
            foreach (var item in snapshot.Items)
                foreach (var source in item.Sources)
                {
                    const VisualizationProvenance provenance =
                        VisualizationProvenance.RelatedSourceSuggestion;
                    var existing = _evidence.Values.FirstOrDefault(value =>
                        value.Origin == EvidenceOrigin.RelatedContext &&
                        value.TopicId == topicId && value.Provenance == provenance &&
                        string.Equals(value.SourceUrl, source.Url, StringComparison.OrdinalIgnoreCase));
                    var id = existing?.Id ?? $"evidence-{++_nextEvidence:D6}";
                    var updated = new VisualizationEvidence(id, EvidenceOrigin.RelatedContext,
                        provenance, null, topicId, source.Title, source.Url, source.SourceType,
                        source.ActualSourceDate, item.Summary,
                        RelevanceRationale: item.RelevanceRationale,
                        InvolvementRationale: source.InvolvementRationale,
                        InvolvementRole: source.InvolvementRole,
                        InvolvementMatchKind: source.InvolvementMatchKind,
                        SourceId: source.SourceId,
                        SuggestionId: item.SuggestionId);
                    if (existing != updated)
                    {
                        _evidence[id] = updated;
                        changed = true;
                    }
                }
            if (changed) publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    public void ApplyAnalysisDelta(MeetingVisualizationAnalysisDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        bool publish;
        lock (_gate)
            publish = ApplyAnalysisLocked(new([], delta), SnapshotLocked(), requireTopicAssignments: false);
        PublishChanged(publish);
    }

    public void ApplyAnalysisResult(
        MeetingVisualizationDocument analyzedPrefix, MeetingVisualizationAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(analyzedPrefix);
        ArgumentNullException.ThrowIfNull(result);
        bool publish;
        lock (_gate)
            publish = ApplyAnalysisLocked(result, analyzedPrefix, requireTopicAssignments: true);
        PublishChanged(publish);
    }

    private bool ApplyAnalysisLocked(
        MeetingVisualizationAnalysisResult result,
        MeetingVisualizationDocument analyzedPrefix,
        bool requireTopicAssignments)
    {
        RequireSession();
        if (analyzedPrefix.SessionId != _sessionId ||
            analyzedPrefix.TranscriptRevision > _transcriptRevision ||
            analyzedPrefix.Segments.Count > _segments.Count)
            throw new InvalidOperationException("Visualization analysis is stale.");
        for (var index = 0; index < analyzedPrefix.Segments.Count; index++)
        {
            var analyzed = analyzedPrefix.Segments[index];
            var current = _segments[index];
            if (analyzed.Id != current.Id || analyzed.Ordinal != current.Ordinal ||
                analyzed.Text != current.Text || analyzed.SpeakerId != current.SpeakerId)
                throw new InvalidOperationException("The analyzed transcript prefix changed.");
        }
        var analyzedRevision = analyzedPrefix.TranscriptRevision;
        RequireUnique(result.TopicAssignments.Select(item => item.OccurrenceId), "topic assignments");
        RequireUnique(result.Delta.Evidence.Select(item => item.Id), "evidence");
        RequireUnique(result.Delta.Questions.Select(item => item.Id), "questions");
        RequireUnique(result.Delta.ExplainedTopicLinks.Select(item => item.Id), "explained topic links");
        RequireUnique(result.Delta.Actions.Select(item => item.Id), "actions");
        RequireUnique(result.Delta.Decisions.Select(item => item.Id), "decisions");

        var suppliedOccurrences = analyzedPrefix.TopicTrail.TakeLast(20).ToArray();
        if (requireTopicAssignments && (
            result.TopicAssignments.Count != suppliedOccurrences.Length ||
            result.TopicAssignments.Where((assignment, index) =>
                assignment.OccurrenceId != suppliedOccurrences[index].Id ||
                assignment.ThroughSegmentOrdinal != suppliedOccurrences[index].ThroughSegmentOrdinal).Any()))
            throw new InvalidOperationException(
                "Topic assignments must preserve every supplied occurrence in chronological order.");

        var topics = _topics.ToList();
        var redirects = new Dictionary<string, string>(_topicRedirects, StringComparer.Ordinal);
        var trail = _trail.ToList();
        var nextTopic = _nextTopic;
        var assignmentsByOccurrence = result.TopicAssignments.ToDictionary(
            item => item.OccurrenceId, StringComparer.Ordinal);
        foreach (var assignment in result.TopicAssignments)
        {
            var label = Clean(assignment.Label);
            if (label.Length == 0 || label.Length > TopicLabelCharacterLimit)
                throw new InvalidOperationException("Topic assignment is invalid.");
            var occurrence = suppliedOccurrences.Single(
                item => item.Id == assignment.OccurrenceId);
            var sourceTopicId = ResolveTopicId(occurrence.TopicId, redirects);
            var sourceTopic = topics.SingleOrDefault(item => item.Id == sourceTopicId)
                ?? throw new InvalidOperationException(
                    "Topic assignment occurrence references an unknown topic.");
            var topic = sourceTopic;
            if (assignment.RedirectToTopicId is not null)
            {
                var existingTopicId = ResolveTopicId(assignment.RedirectToTopicId, redirects);
                topic = topics.SingleOrDefault(item => item.Id == existingTopicId)
                    ?? throw new InvalidOperationException("Topic assignment references an unknown topic.");
                if (sourceTopic.Id != topic.Id)
                {
                    var mergedAliases = topic.Aliases.Concat(sourceTopic.Aliases)
                        .Append(label).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    topic = topic with
                    {
                        Aliases = mergedAliases,
                        FirstOccurrenceOrdinal = Math.Min(
                            topic.FirstOccurrenceOrdinal, sourceTopic.FirstOccurrenceOrdinal),
                        LastOccurrenceOrdinal = Math.Max(
                            topic.LastOccurrenceOrdinal, sourceTopic.LastOccurrenceOrdinal)
                    };
                    topics[topics.FindIndex(item => item.Id == topic.Id)] = topic;
                    topics.Remove(sourceTopic);
                    redirects[sourceTopic.Id] = topic.Id;
                    foreach (var key in redirects.Keys.ToArray())
                        redirects[key] = ResolveTopicId(redirects[key], redirects);
                }
            }
            var topicAliases = topic.Aliases.Contains(label, StringComparer.OrdinalIgnoreCase)
                ? topic.Aliases : topic.Aliases.Append(label).ToArray();
            var updated = topic with
            {
                Label = label,
                Aliases = topicAliases,
                FirstOccurrenceOrdinal = Math.Min(topic.FirstOccurrenceOrdinal, assignment.ThroughSegmentOrdinal),
                LastOccurrenceOrdinal = Math.Max(topic.LastOccurrenceOrdinal, assignment.ThroughSegmentOrdinal)
            };
            topics[topics.IndexOf(topic)] = updated;
        }
        var occurrenceById = suppliedOccurrences.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (result.TopicAssignments
            .GroupBy(
                assignment => ResolveTopicId(
                    assignment.RedirectToTopicId ??
                    occurrenceById[assignment.OccurrenceId].TopicId,
                    redirects),
                StringComparer.Ordinal)
            .Any(group => group.Select(item => Clean(item.Label))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            throw new InvalidOperationException(
                "Assignments for one canonical topic must use one stable label.");
        if (topics.GroupBy(item => Clean(item.Label), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            throw new InvalidOperationException(
                "Distinct canonical topics cannot share a label; the model must redirect or differentiate them.");
        trail = CoalesceTrail(trail
            .Select(item => assignmentsByOccurrence.TryGetValue(item.Id, out var assignment)
                ? item with
                {
                    TopicId = ResolveTopicId(
                        assignment.RedirectToTopicId ?? item.TopicId, redirects),
                    TranscriptRevision = analyzedRevision,
                    LabelAtTime = Clean(assignment.Label)
                }
                : item)
            .OrderBy(item => item.ThroughSegmentOrdinal), redirects);

        var evidence = _evidence.Values.ToDictionary(item => item.Id, item => item.TopicId is null
            ? item : item with { TopicId = ResolveTopicId(item.TopicId, redirects) }, StringComparer.Ordinal);
        foreach (var item in result.Delta.Evidence)
        {
            var remapped = item.TopicId is null
                ? item : item with { TopicId = ResolveTopicId(item.TopicId, redirects) };
            if (result.SuppliedSegmentOrdinals.Count > 0 &&
                remapped.Provenance == VisualizationProvenance.TranscriptFact &&
                (remapped.SegmentOrdinal is not int sourceOrdinal ||
                 !result.SuppliedSegmentOrdinals.Contains(sourceOrdinal)))
                throw new InvalidOperationException(
                    "New transcript evidence must reference a supplied source segment.");
            ValidateEvidence(remapped, topics);
            evidence[remapped.Id] = remapped;
        }
        if (evidence.Values
            .Where(item => item.Origin == EvidenceOrigin.Analysis &&
                item.Provenance == VisualizationProvenance.TranscriptFact)
            .GroupBy(item => $"{item.TopicId}\0{item.PresentationLabel}",
                StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            throw new InvalidOperationException(
                "A topic cannot contain duplicate takeaway labels.");
        if (evidence.Values
            .Where(item => item.Origin == EvidenceOrigin.Analysis &&
                item.Provenance == VisualizationProvenance.TranscriptFact)
            .GroupBy(item => item.TopicId, StringComparer.Ordinal)
            .Any(group => group.Count() > 4))
            throw new InvalidOperationException(
                "A topic cannot contain more than four analyzed transcript facts.");
        for (var index = 0; index < topics.Count; index++)
        {
            var topic = topics[index];
            var groundedOrdinals = evidence.Values
                .Where(item => item.TopicId == topic.Id &&
                    item.Provenance == VisualizationProvenance.TranscriptFact &&
                    item.SegmentOrdinal is not null)
                .Select(item => item.SegmentOrdinal!.Value)
                .ToArray();
            if (groundedOrdinals.Length > 0)
                topics[index] = topic with
                {
                    FirstOccurrenceOrdinal = Math.Min(
                        topic.FirstOccurrenceOrdinal, groundedOrdinals.Min()),
                    LastOccurrenceOrdinal = Math.Max(
                        topic.LastOccurrenceOrdinal, groundedOrdinals.Max())
                };
        }
        var questions = _questions.Values.ToDictionary(item => item.Id, item => item.TopicId is null
            ? item : item with { TopicId = ResolveTopicId(item.TopicId, redirects) }, StringComparer.Ordinal);
        foreach (var question in result.Delta.Questions)
        {
            var remapped = question.TopicId is null
                ? question : question with { TopicId = ResolveTopicId(question.TopicId, redirects) };
            if (!Enum.IsDefined(remapped.QuestionState) ||
                remapped.TopicId is not null && topics.All(topic => topic.Id != remapped.TopicId) ||
                string.IsNullOrWhiteSpace(remapped.Id) || string.IsNullOrWhiteSpace(remapped.Text) ||
                remapped.Text.Length > 240)
                throw new InvalidOperationException("Question is invalid.");
            var cited = RequireEvidence(remapped.EvidenceIds, evidence);
            if (cited.All(item => item.Provenance != VisualizationProvenance.TranscriptFact))
                throw new InvalidOperationException("Question is not grounded in transcript evidence.");
            questions[remapped.Id] = remapped;
        }
        var explainedTopicLinks = _explainedTopicLinks.Values
            .Select(item => item with
            {
                FromTopicId = ResolveTopicId(item.FromTopicId, redirects),
                ToTopicId = ResolveTopicId(item.ToTopicId, redirects)
            })
            .Where(item => item.FromTopicId != item.ToTopicId)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var link in result.Delta.ExplainedTopicLinks)
        {
            var remapped = link with
            {
                FromTopicId = ResolveTopicId(link.FromTopicId, redirects),
                ToTopicId = ResolveTopicId(link.ToTopicId, redirects)
            };
            if (remapped.FromTopicId == remapped.ToTopicId)
                continue;
            if (!Enum.IsDefined(remapped.Kind) || !Enum.IsDefined(remapped.Provenance) ||
                string.IsNullOrWhiteSpace(remapped.Id) ||
                topics.All(topic => topic.Id != remapped.FromTopicId) ||
                topics.All(topic => topic.Id != remapped.ToTopicId))
                throw new InvalidOperationException("Explained topic link references invalid topics.");
            var cited = RequireEvidence(remapped.EvidenceIds, evidence);
            if (cited.Any(item => item.Origin == EvidenceOrigin.RelatedContext ||
                item.Provenance != VisualizationProvenance.TranscriptFact))
                throw new InvalidOperationException(
                    "Explained topic links require transcript evidence.");
            if (!cited.Any(item => item.TopicId == remapped.FromTopicId) ||
                !cited.Any(item => item.TopicId == remapped.ToTopicId))
                throw new InvalidOperationException(
                    "Explained topic links require cited transcript evidence on both topics.");
            var explicitlyStated = remapped.Provenance == VisualizationProvenance.TranscriptFact;
            if (explicitlyStated
                ? !string.IsNullOrWhiteSpace(remapped.InferenceLabel)
                : remapped.Provenance != VisualizationProvenance.Inference ||
                  string.IsNullOrWhiteSpace(remapped.InferenceLabel) ||
                  remapped.InferenceLabel.Length > 120)
                throw new InvalidOperationException(
                    "Stated topic links carry no inference label; inferred links require a bounded one.");
            explainedTopicLinks[remapped.Id] = remapped;
        }
        var actions = _actions.Values.ToDictionary(item => item.Id, item => item.TopicId is null
            ? item : item with { TopicId = ResolveTopicId(item.TopicId, redirects) }, StringComparer.Ordinal);
        foreach (var action in result.Delta.Actions)
        {
            var remapped = action.TopicId is null
                ? action : action with { TopicId = ResolveTopicId(action.TopicId, redirects) };
            if (!Enum.IsDefined(remapped.State))
                throw new InvalidOperationException("Action state is invalid.");
            ValidateGroundedStateItem(
                remapped.Id, remapped.TopicId, remapped.Text, remapped.EvidenceIds,
                topics, evidence, "Action");
            actions[remapped.Id] = remapped;
        }
        var decisions = _decisions.Values.ToDictionary(item => item.Id, item => item.TopicId is null
            ? item : item with { TopicId = ResolveTopicId(item.TopicId, redirects) }, StringComparer.Ordinal);
        foreach (var decision in result.Delta.Decisions)
        {
            var remapped = decision.TopicId is null
                ? decision : decision with { TopicId = ResolveTopicId(decision.TopicId, redirects) };
            ValidateGroundedStateItem(
                remapped.Id, remapped.TopicId, remapped.Text, remapped.EvidenceIds,
                topics, evidence, "Decision");
            decisions[remapped.Id] = remapped;
        }
        var throughOrdinal = result.ThroughSegmentOrdinal ??
            result.SuppliedSegmentOrdinals.DefaultIfEmpty(_lastAnalyzedOrdinal).Max();
        if (throughOrdinal < 0 || throughOrdinal > analyzedPrefix.Segments.Count)
            throw new InvalidOperationException("Visualization analysis cursor is invalid.");
        if (result.SuppliedSegmentOrdinals.Count > 0 &&
            (result.SuppliedSegmentOrdinals.Distinct().Count() !=
                result.SuppliedSegmentOrdinals.Count ||
             result.SuppliedSegmentOrdinals.Any(
                 ordinal => ordinal < 1 || ordinal > analyzedPrefix.Segments.Count) ||
             throughOrdinal != result.SuppliedSegmentOrdinals.Max()))
            throw new InvalidOperationException("Visualization source segment window is invalid.");
        _topics.Clear();
        _topics.AddRange(topics);
        _topicRedirects.Clear();
        foreach (var item in redirects) _topicRedirects.Add(item.Key, item.Value);
        _trail.Clear();
        _trail.AddRange(trail);
        Replace(_evidence, evidence);
        Replace(_questions, questions);
        Replace(_explainedTopicLinks, explainedTopicLinks);
        Replace(_actions, actions);
        Replace(_decisions, decisions);
        _nextTopic = nextTopic;
        _lastAnalyzedOrdinal = Math.Max(_lastAnalyzedOrdinal, throughOrdinal);
        if (_reanalysisFromOrdinal is { } invalidated &&
            throughOrdinal >= invalidated)
            _reanalysisFromOrdinal = null;
        return ChangedLocked();
    }

    public void Complete(DateTimeOffset endedAt, bool isComplete)
    {
        bool publish;
        lock (_gate)
        {
            RequireSession();
            _endedAt = endedAt;
            _complete = isComplete;
            _closed = true;
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    public MeetingVisualizationDocument Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private MeetingVisualizationDocument SnapshotLocked()
    {
        RequireSession();
        return new(CurrentSchemaVersion, _sessionId, _title, _startedAt, _endedAt,
            _transcriptRevision, _complete, _segments.ToArray(), _speakers.Values.ToArray(),
            _topics.ToArray(), _trail.ToArray(), _questions.Values.ToArray(),
            _evidence.Values.ToArray(), _explainedTopicLinks.Values.ToArray())
        {
            Actions = _actions.Values.ToArray(),
            Decisions = _decisions.Values.ToArray(),
            LastAnalyzedOrdinal = _lastAnalyzedOrdinal,
            ReanalysisFromOrdinal = _reanalysisFromOrdinal
        };
    }

    public void Clear()
    {
        bool publish;
        lock (_gate)
        {
            ClearLocked();
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    private void ValidateEvidence(VisualizationEvidence evidence, IReadOnlyList<TopicCluster>? topics = null)
    {
        topics ??= _topics;
        if (string.IsNullOrWhiteSpace(evidence.Id) || !Enum.IsDefined(evidence.Origin) ||
            !Enum.IsDefined(evidence.Provenance))
            throw new InvalidOperationException("Evidence ID, origin, and provenance are required.");
        if (evidence.TopicId is not null && topics.All(topic => topic.Id != evidence.TopicId))
            throw new InvalidOperationException("Evidence references an unknown topic.");
        if (evidence.SourceUrl is not null && !SafeHttps(evidence.SourceUrl))
            throw new InvalidOperationException("Evidence contains an unsafe source URL.");
        if (evidence.Provenance == VisualizationProvenance.TranscriptFact)
        {
            if (evidence.Origin == EvidenceOrigin.RelatedContext || evidence.SegmentOrdinal is null ||
                _segments.All(segment => segment.Ordinal != evidence.SegmentOrdinal) ||
                evidence.TopicId is null ||
                string.IsNullOrWhiteSpace(evidence.PresentationLabel) ||
                evidence.PresentationLabel.Length > 120 ||
                string.IsNullOrWhiteSpace(evidence.Summary) ||
                evidence.Summary.Length > 480)
                throw new InvalidOperationException("Transcript evidence references an unknown segment.");
        }
        else if (evidence.Provenance == VisualizationProvenance.RelatedSourceSuggestion)
        {
            if (evidence.Origin != EvidenceOrigin.RelatedContext ||
                string.IsNullOrWhiteSpace(evidence.SourceTitle) ||
                !SafeHttps(evidence.SourceUrl))
                throw new InvalidOperationException(
                    "Related source suggestions require a safe cited source.");
        }
        else if (string.IsNullOrWhiteSpace(evidence.Summary))
            throw new InvalidOperationException("Inference evidence requires an explicit label.");
    }

    private static IReadOnlyList<VisualizationEvidence> RequireEvidence(
        IReadOnlyList<string> ids, IReadOnlyDictionary<string, VisualizationEvidence> evidence, bool allowEmpty = false)
    {
        if ((!allowEmpty && ids.Count == 0) || ids.Any(id => !evidence.ContainsKey(id)))
            throw new InvalidOperationException("Evidence references are missing or invalid.");
        return ids.Select(id => evidence[id]).ToArray();
    }

    private static void ValidateGroundedStateItem(
        string id,
        string? topicId,
        string text,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<TopicCluster> topics,
        IReadOnlyDictionary<string, VisualizationEvidence> evidence,
        string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text) || text.Length > 240 ||
            topicId is not null && topics.All(topic => topic.Id != topicId))
            throw new InvalidOperationException($"{name} is invalid.");
        var cited = RequireEvidence(evidenceIds, evidence);
        if (cited.Any(item => item.Provenance != VisualizationProvenance.TranscriptFact))
            throw new InvalidOperationException($"{name} requires transcript evidence.");
    }

    private void InvalidateAnalysisFromLocked(int ordinal)
    {
        _reanalysisFromOrdinal = Math.Min(_reanalysisFromOrdinal ?? ordinal, ordinal);
        var removedEvidence = _evidence.Values
            .Where(item => item.Origin == EvidenceOrigin.Analysis &&
                item.SegmentOrdinal >= ordinal)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in removedEvidence)
            _evidence.Remove(id);
        RemoveUnsupported(_questions, item => item.EvidenceIds, removedEvidence);
        RemoveUnsupported(_explainedTopicLinks, item => item.EvidenceIds, removedEvidence);
        RemoveUnsupported(_actions, item => item.EvidenceIds, removedEvidence);
        RemoveUnsupported(_decisions, item => item.EvidenceIds, removedEvidence);
    }

    private static void RemoveUnsupported<T>(
        Dictionary<string, T> values,
        Func<T, IReadOnlyList<string>> evidenceIds,
        IReadOnlySet<string> removedEvidence)
    {
        foreach (var id in values
            .Where(item => evidenceIds(item.Value).Any(removedEvidence.Contains))
            .Select(item => item.Key)
            .ToArray())
            values.Remove(id);
    }

    private static void RequireUnique(IEnumerable<string> ids, string name)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException($"Visualization {name} contain duplicate or empty IDs.");
    }

    private static void Replace<T>(Dictionary<string, T> target, Dictionary<string, T> values)
    {
        target.Clear();
        foreach (var item in values) target.Add(item.Key, item.Value);
    }

    private static List<TopicOccurrence> CoalesceTrail(
        IEnumerable<TopicOccurrence> occurrences,
        IReadOnlyDictionary<string, string> redirects)
    {
        var result = new List<TopicOccurrence>();
        foreach (var occurrence in occurrences)
        {
            var canonical = occurrence with { TopicId = ResolveTopicId(occurrence.TopicId, redirects) };
            if (result.Count == 0 || result[^1].TopicId != canonical.TopicId)
                result.Add(canonical);
        }
        return result;
    }

    private static bool SafeHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    private void RequireSession()
    {
        if (_sessionId.Length == 0) throw new InvalidOperationException("No visualization session is active.");
    }

    private void RequireMutable()
    {
        RequireSession();
        if (_closed) throw new InvalidOperationException("The visualization session is complete.");
    }

    private bool ChangedLocked()
    {
        _revision++;
        _pendingChangedRevisions.Enqueue(_revision);
        if (_publishingChanges) return false;
        _publishingChanges = true;
        return true;
    }

    private void PublishChanged(bool publish)
    {
        if (!publish) return;
        Exception? firstFailure = null;
        while (true)
        {
            long revision;
            EventHandler<long>? handlers;
            lock (_gate)
            {
                if (_pendingChangedRevisions.Count == 0)
                {
                    _publishingChanges = false;
                    break;
                }
                revision = _pendingChangedRevisions.Dequeue();
                handlers = Changed;
            }
            if (handlers is null) continue;
            foreach (EventHandler<long> handler in handlers.GetInvocationList())
                try { handler(this, revision); }
                catch (Exception ex) { firstFailure ??= ex; }
        }
        if (firstFailure is not null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    private void ClearLocked()
    {
        _segments.Clear();
        _speakers.Clear();
        _topics.Clear();
        _trail.Clear();
        _questions.Clear();
        _evidence.Clear();
        _explainedTopicLinks.Clear();
        _actions.Clear();
        _decisions.Clear();
        _topicRedirects.Clear();
        _sessionId = string.Empty;
        _title = string.Empty;
        _startedAt = default;
        _endedAt = null;
        _transcriptRevision = 0;
        _complete = false;
        _closed = false;
        _nextTopic = 0;
        _nextOccurrence = 0;
        _nextEvidence = 0;
        _lastAnalyzedOrdinal = 0;
        _reanalysisFromOrdinal = null;
    }

    private static string Clean(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    /// <summary>Maps a topic ID a model redirect merged away onto its surviving canonical topic.</summary>
    public string ResolveCanonicalTopicId(string topicId)
    {
        if (string.IsNullOrWhiteSpace(topicId)) return topicId;
        lock (_gate) return ResolveTopicIdLocked(topicId);
    }

    private string ResolveTopicIdLocked(string topicId) => ResolveTopicId(topicId, _topicRedirects);

    private static string ResolveTopicId(string topicId, IReadOnlyDictionary<string, string> redirects)
    {
        var current = topicId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (redirects.TryGetValue(current, out var next))
        {
            if (!visited.Add(current))
                throw new InvalidOperationException("Topic redirects contain a cycle.");
            current = next;
        }
        return current;
    }
}
