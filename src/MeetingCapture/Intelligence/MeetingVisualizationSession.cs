using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using MeetingCapture.Transcription;

namespace MeetingCapture.Intelligence;

/// <summary>
/// Holds the live meeting visualization model for one recording session: transcript segments,
/// speakers, topics, evidence, and analysis results. Thread-safe. Changes bump <see cref="Revision"/>
/// and raise <see cref="Changed"/>. Call <see cref="Snapshot"/> to read an immutable copy.
/// </summary>
public sealed class MeetingVisualizationSession
{
    internal const int TopicLabelCharacterLimit = 80;
    public const int CurrentSchemaVersion = 2;
    private readonly object _gate = new();
    private readonly List<VisualizationSegment> _segments = [];
    private readonly Dictionary<string, VisualizationSpeaker> _speakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _speakerAliases = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Raised with the new revision after the session content changes.</summary>
    public event EventHandler<long>? Changed;

    /// <summary>Increases each time the session content changes.</summary>
    public long Revision { get { lock (_gate) return _revision; } }

    /// <summary>True after <see cref="Begin"/> and before <see cref="Complete"/>.</summary>
    public bool HasActiveSession { get { lock (_gate) return _sessionId.Length > 0 && !_closed; } }

    /// <summary>Clears previous data and starts a new session.</summary>
    public void Begin(string sessionId, string meetingTitle, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        bool publish;
        lock (_gate)
        {
            // Reuse the session object between recordings, but clear every prior ID map and
            // counter before publishing the new session.
            ClearLocked();
            _sessionId = sessionId;
            _title = Clean(meetingTitle);
            _startedAt = startedAt;
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    /// <summary>Adds the transcript segment at <paramref name="ordinal"/>, or replaces it if it already exists.</summary>
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
            // Stable ordinal-derived IDs let later corrections replace a segment without
            // changing the references stored by the visualization.
            var value = new VisualizationSegment($"seg-{ordinal:D6}", ordinal, segment.Text,
                segment.Timestamp, segment.SpeakerId, segment.ConfidenceScore);
            var index = _segments.FindIndex(item => item.Ordinal == ordinal);
            if (index >= 0)
            {
                if (_segments[index] != value)
                {
                    // A corrected transcript segment invalidates analysis that depended on it.
                    _segments[index] = value;
                    InvalidateAnalysisFromLocked(ordinal);
                }
            }
            else
            {
                // Appending only the next ordinal keeps the transcript a gap-free source of truth.
                if (ordinal != _segments.Count + 1)
                    throw new InvalidOperationException("Segment ordinals must be contiguous.");
                _segments.Add(value);
            }
            _transcriptRevision = Math.Max(_transcriptRevision, ordinal);
            // Create an unconfirmed speaker entry on first sight; callers can later replace it
            // with a user-confirmed display name.
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

    /// <summary>
    /// Moves every segment of the absorbed speakers to the kept speaker and drops the absorbed entries.
    /// Allowed after the session completes so a saved meeting can be corrected before it is re-saved.
    /// </summary>
    public void MergeSpeakers(SpeakerMergePlan plan, string keptDisplayName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        bool publish;
        lock (_gate)
        {
            RequireSession();
            // Preserve the kept speaker's confirmation state; an automatic rename must not
            // accidentally turn an unconfirmed label into a confirmed identity.
            var keptName = string.IsNullOrWhiteSpace(keptDisplayName) ? plan.KeptId : keptDisplayName.Trim();
            var confirmed = _speakers.TryGetValue(plan.KeptId, out var existing) && existing.IsConfirmed;
            _speakers[plan.KeptId] = new(plan.KeptId, keptName, confirmed);
            foreach (var id in plan.AbsorbedIds)
            {
                _speakers.Remove(id);
                _speakerAliases[id] = plan.KeptId;
            }
            foreach (var alias in _speakerAliases.Where(pair => plan.Absorbs(pair.Value)).Select(pair => pair.Key).ToArray())
                _speakerAliases[alias] = plan.KeptId;
            // Rewrite historical segments as well as the alias map so snapshots and future
            // evidence use one canonical speaker ID.
            for (var index = 0; index < _segments.Count; index++)
                if (plan.Absorbs(_segments[index].SpeakerId))
                    _segments[index] = _segments[index] with { SpeakerId = plan.KeptId };
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    /// <summary>
    /// Adds a new topic, or updates the topic given by <paramref name="existingTopicId"/> with the new label.
    /// Adds a topic-trail entry when the topic changes from the previous one.
    /// </summary>
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
                // A previous model redirect may have merged this ID; always update its surviving
                // canonical topic rather than reviving the old one.
                existingTopicId = ResolveTopicIdLocked(existingTopicId);
                topic = _topics.SingleOrDefault(item => item.Id == existingTopicId)
                    ?? throw new InvalidOperationException("The supplied topic ID does not exist.");
            }
            else
            {
                // Allocate IDs locally so topic identity does not depend on model-generated text.
                var id = $"topic-{++_nextTopic:D4}";
                topic = new(id, clean, [clean], throughSegmentOrdinal, throughSegmentOrdinal);
                _topics.Add(topic);
            }

            var aliases = topic.Aliases.Contains(clean, StringComparer.OrdinalIgnoreCase)
                ? topic.Aliases : topic.Aliases.Append(clean).ToArray();
            // Keep prior labels as aliases, but update the current label and segment span.
            var updated = topic with
            {
                Label = clean,
                Aliases = aliases,
                FirstOccurrenceOrdinal = Math.Min(topic.FirstOccurrenceOrdinal, throughSegmentOrdinal),
                LastOccurrenceOrdinal = Math.Max(topic.LastOccurrenceOrdinal, throughSegmentOrdinal)
            };
            _topics[_topics.IndexOf(topic)] = updated;
            if (_trail.Count == 0 || ResolveTopicIdLocked(_trail[^1].TopicId) != updated.Id)
                // The trail records topic changes only; repeated model updates to the same topic
                // do not create noisy duplicate visits.
                _trail.Add(new($"occ-{++_nextOccurrence:D6}", updated.Id, transcriptRevision,
                    throughSegmentOrdinal, clean));
            publish = ChangedLocked();
            result = updated;
        }
        PublishChanged(publish);
        return result;
    }

    /// <summary>Stores each source in the snapshot as related-source evidence for a topic. Existing evidence for the same URL is updated.</summary>
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
                    // Refresh the same URL in place so its stable evidence ID survives while
                    // title, date, and explanation can be updated from the latest lookup.
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

    /// <summary>Applies only the delta (questions, evidence, links, actions, decisions) without changing topic labels.</summary>
    public void ApplyAnalysisDelta(MeetingVisualizationAnalysisDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        bool publish;
        // Delta-only callers do not change topic identities; validate the additions against the
        // current document snapshot under the same lock used for the eventual commit.
        lock (_gate)
            publish = ApplyAnalysisLocked(new([], delta), SnapshotLocked(), requireTopicAssignments: false);
        PublishChanged(publish);
    }

    /// <summary>
    /// Applies a full analysis result, including topic assignments. <paramref name="analyzedPrefix"/>
    /// is the document state that was sent for analysis.
    /// </summary>
    public void ApplyAnalysisResult(
        MeetingVisualizationDocument analyzedPrefix, MeetingVisualizationAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(analyzedPrefix);
        ArgumentNullException.ThrowIfNull(result);
        bool publish;
        // Full analysis is checked against the exact prefix that was sent to the model so a
        // response cannot be applied to a different recording or corrected transcript.
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
        // Model work happens outside the session lock. Recheck the analyzed prefix before
        // changing anything so stale replies cannot overwrite newer transcript state.
        if (analyzedPrefix.SessionId != _sessionId ||
            analyzedPrefix.TranscriptRevision > _transcriptRevision ||
            analyzedPrefix.Segments.Count > _segments.Count)
            throw new InvalidOperationException("Visualization analysis is stale.");
        for (var index = 0; index < analyzedPrefix.Segments.Count; index++)
        {
            var analyzed = analyzedPrefix.Segments[index];
            var current = _segments[index];
            if (analyzed.Id != current.Id || analyzed.Ordinal != current.Ordinal ||
                analyzed.Text != current.Text ||
                !string.Equals(CanonicalSpeakerLocked(analyzed.SpeakerId), current.SpeakerId, StringComparison.OrdinalIgnoreCase))
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
            // Apply each model-declared merge to temporary copies first. The real collections
            // are not changed until all assignments and evidence pass validation.
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
                // A redirect is the model's explicit identity decision; do not infer merges by
                // comparing topic wording in the client.
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
                    // Flatten redirect chains after each merge so later links resolve directly
                    // to the surviving topic.
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
        // Distinct canonical topics need distinct labels; fail the whole update if the model
        // returned duplicates instead of silently choosing one.
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

        // Remap existing references when a topic was merged, then validate and add the new
        // evidence before validating questions, actions, decisions, and links that cite it.
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
            // Questions must point only to existing topics and transcript-backed citations;
            // retrieved suggestions cannot answer a participant's meeting question.
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
            // Resolve merged topic IDs first, then reject self-links and require transcript
            // evidence on both sides of any remaining relation.
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
            // Actions are displayable only when they cite transcript facts, never a retrieved
            // source or an unsupported inference.
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
            // Decisions use the same transcript-only grounding rule as action items.
            var remapped = decision.TopicId is null
                ? decision : decision with { TopicId = ResolveTopicId(decision.TopicId, redirects) };
            ValidateGroundedStateItem(
                remapped.Id, remapped.TopicId, remapped.Text, remapped.EvidenceIds,
                topics, evidence, "Decision");
            decisions[remapped.Id] = remapped;
        }
        var throughOrdinal = result.ThroughSegmentOrdinal ??
            result.SuppliedSegmentOrdinals.DefaultIfEmpty(_lastAnalyzedOrdinal).Max();
        // Verify the model's analysis cursor against the exact source window before recording
        // progress; corrections remain pending until the corrected range has been reanalyzed.
        if (throughOrdinal < 0 || throughOrdinal > analyzedPrefix.Segments.Count)
            throw new InvalidOperationException("Visualization analysis cursor is invalid.");
        if (result.SuppliedSegmentOrdinals.Count > 0 &&
            (result.SuppliedSegmentOrdinals.Distinct().Count() !=
                result.SuppliedSegmentOrdinals.Count ||
             result.SuppliedSegmentOrdinals.Any(
                 ordinal => ordinal < 1 || ordinal > analyzedPrefix.Segments.Count) ||
             throughOrdinal != result.SuppliedSegmentOrdinals.Max()))
            throw new InvalidOperationException("Visualization source segment window is invalid.");
        // Commit the already-validated copies together. No partially applied result is visible.
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

    /// <summary>Marks the session as ended. After this, most changes are rejected, but speaker merges are still allowed.</summary>
    public void Complete(DateTimeOffset endedAt, bool isComplete)
    {
        bool publish;
        lock (_gate)
        {
            RequireSession();
            // Closing prevents transcript/topic edits but leaves speaker corrections available
            // for a saved meeting that needs cleanup before persistence.
            _endedAt = endedAt;
            _complete = isComplete;
            _closed = true;
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    /// <summary>Returns an immutable copy of the current session. Throws if no session was started.</summary>
    public MeetingVisualizationDocument Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private MeetingVisualizationDocument SnapshotLocked()
    {
        RequireSession();
        // Copy every collection while holding the lock; the returned document is a stable
        // point-in-time input for analysis, persistence, and UI rendering.
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
            // Clear the model but still publish a revision so observers can discard old views.
            ClearLocked();
            publish = ChangedLocked();
        }
        PublishChanged(publish);
    }

    private void ValidateEvidence(VisualizationEvidence evidence, IReadOnlyList<TopicCluster>? topics = null)
    {
        topics ??= _topics;
        // All source URLs are treated as untrusted input, even when they came from an earlier
        // lookup or persisted document.
        if (string.IsNullOrWhiteSpace(evidence.Id) || !Enum.IsDefined(evidence.Origin) ||
            !Enum.IsDefined(evidence.Provenance))
            throw new InvalidOperationException("Evidence ID, origin, and provenance are required.");
        if (evidence.TopicId is not null && topics.All(topic => topic.Id != evidence.TopicId))
            throw new InvalidOperationException("Evidence references an unknown topic.");
        if (evidence.SourceUrl is not null && !SafeHttps(evidence.SourceUrl))
            throw new InvalidOperationException("Evidence contains an unsafe source URL.");
        if (evidence.Provenance == VisualizationProvenance.TranscriptFact)
        {
            // A transcript fact must resolve to an actual segment and have a compact label and
            // summary suitable for the evidence panel.
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
            // Related sources are suggestions only and must retain a safe citation to be useful.
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
        // Validate the full reference set before returning any evidence to callers.
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
        // Shared checks keep actions and decisions bounded and transcript-grounded.
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text) || text.Length > 240 ||
            topicId is not null && topics.All(topic => topic.Id != topicId))
            throw new InvalidOperationException($"{name} is invalid.");
        var cited = RequireEvidence(evidenceIds, evidence);
        if (cited.Any(item => item.Provenance != VisualizationProvenance.TranscriptFact))
            throw new InvalidOperationException($"{name} requires transcript evidence.");
    }

    private void InvalidateAnalysisFromLocked(int ordinal)
    {
        // Keep earlier analysis intact. Remove only generated evidence that cites the corrected
        // segment or a later one, then remove every item that depended on that evidence.
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
        // Materialize the keys first because the dictionary is changed during the removal pass.
        foreach (var id in values
            .Where(item => evidenceIds(item.Value).Any(removedEvidence.Contains))
            .Select(item => item.Key)
            .ToArray())
            values.Remove(id);
    }

    private static void RequireUnique(IEnumerable<string> ids, string name)
    {
        // Stable IDs are map keys throughout the document; duplicates would overwrite entries
        // and make a model response ambiguous.
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException($"Visualization {name} contain duplicate or empty IDs.");
    }

    private static void Replace<T>(Dictionary<string, T> target, Dictionary<string, T> values)
    {
        // Replace only after a complete candidate dictionary has passed validation.
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
            // Redirected neighboring occurrences become one trail visit, while a later return
            // to the topic remains a separate chronological visit.
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

    private string? CanonicalSpeakerLocked(string? speakerId) =>
        speakerId is not null && _speakerAliases.TryGetValue(speakerId, out var kept) ? kept : speakerId;

    private bool ChangedLocked()
    {
        // Queue each revision so nested changes raised by observers are delivered in order.
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
        // Drain outside the lock to allow handlers to read the session or trigger another
        // update. Continue notifying remaining handlers, then rethrow the first failure.
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
        // Reset data and generated-ID counters together so IDs are unique within each session.
        _segments.Clear();
        _speakers.Clear();
        _speakerAliases.Clear();
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
        // Follow explicit merge redirects and detect cycles instead of returning an arbitrary
        // topic if the redirect map is corrupted.
        while (redirects.TryGetValue(current, out var next))
        {
            if (!visited.Add(current))
                throw new InvalidOperationException("Topic redirects contain a cycle.");
            current = next;
        }
        return current;
    }
}
