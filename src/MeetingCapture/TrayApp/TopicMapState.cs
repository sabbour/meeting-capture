using MeetingCapture.Intelligence;
using System.Text.RegularExpressions;

namespace MeetingCapture.TrayApp;

internal sealed record VisualizationProjection(
    string SelectedTopicId,
    string CurrentTopicId,
    IReadOnlyList<TrailItemViewModel> Trail,
    string EmptyMessage,
    string Fingerprint,
    BrainMapViewModel? BrainMap = null,
    TopicBriefViewModel? TopicBrief = null);

internal sealed record BrainMapViewModel(
    string TopicId,
    string Label,
    string Detail,
    bool IsCurrent,
    string CurrentTopicLabel,
    IReadOnlyList<BrainMapBranchViewModel> Branches,
    string EmptyMessage);

internal sealed record BrainMapBranchViewModel(
    string Id,
    string TopicId,
    string Kind,
    string Title,
    string Detail)
{
    public string AccessibleName =>
        $"{Kind}: {Title}. {Detail}. Select to open topic details.";
}

internal readonly record struct BrainMapRect(double X, double Y, double Width, double Height);

internal sealed record BrainMapLayout(
    double Width,
    double Height,
    BrainMapRect Root,
    IReadOnlyList<BrainMapRect> Branches)
{
    internal static BrainMapLayout Calculate(double viewportWidth, int branchCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(branchCount);
        var width = Math.Max(320, viewportWidth);
        const double rootHeight = 96;
        const double branchHeight = 104;
        const double horizontalGap = 20;
        const double verticalGap = 20;
        const double connectorGap = 52;
        const double margin = 20;
        var columns = width >= 960 ? 3 : width >= 520 ? 2 : 1;
        var rootWidth = Math.Min(300, width - margin * 2);
        var branchWidth = Math.Min(
            320,
            (width - margin * 2 - horizontalGap * (columns - 1)) / columns);
        var gridWidth = branchWidth * columns + horizontalGap * (columns - 1);
        var firstX = (width - gridWidth) / 2;
        var rows = branchCount == 0 ? 0 : (branchCount + columns - 1) / columns;
        var firstY = margin + rootHeight + connectorGap;
        var height = Math.Max(
            280,
            firstY + rows * branchHeight + Math.Max(0, rows - 1) * verticalGap + margin);
        var root = new BrainMapRect(
            (width - rootWidth) / 2,
            margin,
            rootWidth,
            rootHeight);
        var branches = new List<BrainMapRect>(branchCount);
        for (var index = 0; index < branchCount; index++)
        {
            var row = index / columns;
            var column = index % columns;
            branches.Add(new(
                firstX + column * (branchWidth + horizontalGap),
                firstY + row * (branchHeight + verticalGap),
                branchWidth,
                branchHeight));
        }
        return new(width, height, root, branches);
    }
}

internal sealed record TopicBriefFactViewModel(
    string Id, string Title, string EvidenceText, string Metadata);

internal sealed record TopicBriefRelationshipViewModel(string Id, string Title, string Detail);

/// <summary>An open or answered question shown in the Topic brief for the selected topic.</summary>
internal sealed record TopicBriefQuestionViewModel(string Id, string Title, bool Answered);

/// <summary>An open or completed action item shown in the Topic brief for the selected topic.</summary>
internal sealed record TopicBriefActionViewModel(string Id, string Title, bool Open)
{
    /// <summary>Negation of <see cref="Open"/> for reuse with the existing completed-item text-decoration converter.</summary>
    public bool Completed => !Open;
}

/// <summary>A recorded decision shown in the Topic brief for the selected topic.</summary>
internal sealed record TopicBriefDecisionViewModel(string Id, string Title);

/// <summary>
/// One participation-verified WorkIQ source shown in the Topic brief, bounded per A.4 (at most 5,
/// max 2 per family when alternatives exist). Never meeting evidence and never a topic edge.
/// </summary>
internal sealed record TopicBriefSourceViewModel(
    string Id, string Title, string Family, string Detail, string Snippet,
    string Timestamp, string RelevanceText, string InvolvementText,
    string SourceUrl, bool CanOpen, ContextSourceType? SourceType, string AccessibleName)
{
    public bool HasTimestamp => Timestamp.Length > 0;
    public string OpenActionName => $"Open {Family}: {Title}";

    public string Glyph => SourceType switch
    {
        ContextSourceType.TeamsMessage or ContextSourceType.TeamsChannel => "\uE8BD",
        ContextSourceType.OutlookMail => "\uE715",
        ContextSourceType.Calendar or ContextSourceType.MeetingArtifact => "\uE787",
        ContextSourceType.Document => "\uE8A5",
        ContextSourceType.LocalTranscript => "\uE8F1",
        _ => "\uE946",
    };
}

/// <summary>
/// The selected/current topic's structured detail: established facts, questions, actions,
/// decisions, and a bounded, family-diverse set of participation-verified WorkIQ suggestions.
/// This is the only home for that detail; the timeline carries chronological occurrences and
/// compact semantic topic relations.
/// </summary>
internal sealed record TopicBriefViewModel(
    string TopicId, string Label, string Status, string DiscussedAt, bool IsCurrent,
    TopicContextStatusViewModel ContextStatus,
    IReadOnlyList<TopicBriefFactViewModel> Facts,
    IReadOnlyList<TopicBriefRelationshipViewModel> Relationships,
    IReadOnlyList<TopicBriefQuestionViewModel> Questions,
    IReadOnlyList<TopicBriefActionViewModel> Actions,
    IReadOnlyList<TopicBriefDecisionViewModel> Decisions,
    IReadOnlyList<TopicBriefSourceViewModel> WorkIqSources,
    int WorkIqVerifiedTotal);

internal sealed record TrailItemViewModel(
    string Id, string TopicId, string Sequence, string Label,
    string Detail, bool IsRevisit, bool IsCurrent, bool IsSelected, bool HasNext,
    string RelationText = "",
    TopicContextStatusViewModel? ContextStatus = null)
{
    public bool HasRelations => RelationText.Length > 0;
    public bool HasContextStatus => ContextStatus is not null;

    public string AccessibleName =>
        $"Topic {Sequence}: {Label}. {Detail}." +
        (IsCurrent ? " Current topic." : string.Empty) +
        (IsSelected ? " Selected." : string.Empty) +
        (ContextStatus is null ? string.Empty : $" {ContextStatus.AccessibleName}") +
        " Select to center its mind map.";
}

internal static class TopicMapState
{
    /// <summary>Projects the chronological topic timeline and selected topic brief.</summary>
    internal static VisualizationProjection Project(
        MeetingVisualizationDocument document,
        string? selectedTopicId,
        string? currentTopicId = null,
        IReadOnlyDictionary<string, RelatedContextState>? relatedContextStates = null,
        bool workIqAuthenticated = true,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var topics = document.TopicClusters.ToDictionary(topic => topic.Id, StringComparer.Ordinal);
        var speakers = document.Speakers.ToDictionary(
            speaker => speaker.SpeakerId,
            speaker => speaker.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        string Display(string? text) => ProjectSpeakers(text, speakers).Trim();
        var latestTopicId = document.TopicTrail.LastOrDefault()?.TopicId;
        var current = currentTopicId is not null && topics.ContainsKey(currentTopicId)
            ? currentTopicId
            : latestTopicId is not null && topics.ContainsKey(latestTopicId)
                ? latestTopicId
                : string.Empty;
        var selected = selectedTopicId is not null && topics.ContainsKey(selectedTopicId)
            ? selectedTopicId
            : current.Length > 0
                ? current
                : document.TopicClusters.FirstOrDefault()?.Id ?? string.Empty;

        var occurrenceCounts = document.TopicTrail
            .GroupBy(item => item.TopicId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var orderedTopics = document.TopicClusters
            .OrderBy(item => item.FirstOccurrenceOrdinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var topicLabels = orderedTopics.ToDictionary(
            item => item.Id, item => Display(item.Label), StringComparer.Ordinal);
        var relationCues = new Dictionary<string, List<(string Id, string Text)>>(StringComparer.Ordinal);
        foreach (var link in document.ExplainedTopicLinks)
        {
            if (link.FromTopicId == link.ToTopicId ||
                !topicLabels.TryGetValue(link.FromTopicId, out var fromLabel) ||
                !topicLabels.TryGetValue(link.ToTopicId, out var toLabel))
                continue;

            AddRelationCue(link.FromTopicId, link.Id, $"{RelationCue(link.Kind)} → {toLabel}");
            AddRelationCue(link.ToTopicId, link.Id, $"← {RelationCue(link.Kind)} {fromLabel}");
        }
        var relationTextByTopic = relationCues.ToDictionary(
            item => item.Key,
            item =>
            {
                var cues = item.Value
                .OrderBy(cue => cue.Text, StringComparer.Ordinal)
                .ThenBy(cue => cue.Id, StringComparer.Ordinal)
                .Select(cue => cue.Text)
                .ToArray();
                var visibleCues = cues.Take(2).ToList();
                var remaining = cues.Length - visibleCues.Count;
                if (remaining > 0)
                    visibleCues.Add($"+{remaining} more relations");
                return string.Join(" · ", visibleCues);
            },
            StringComparer.Ordinal);
        void AddRelationCue(string topicId, string id, string text)
        {
            if (!relationCues.TryGetValue(topicId, out var cues))
                relationCues.Add(topicId, cues = []);
            cues.Add((id, text));
        }
        TopicContextStatusViewModel ContextStatus(string topicId)
        {
            if (relatedContextStates?.TryGetValue(topicId, out var state) == true)
                return RelatedContextPresentation.TopicStatus(state);
            var hasSources = document.Evidence.Any(item =>
                item.TopicId == topicId &&
                item.Origin == EvidenceOrigin.RelatedContext &&
                item.Provenance == VisualizationProvenance.RelatedSourceSuggestion);
            if (isReadOnly)
                return hasSources
                    ? RelatedContextPresentation.TopicStatus(RelatedContextState.Cached)
                    : new(
                        TopicSemanticInfoState.NoEvidence,
                        "No verified related context in this saved meeting",
                        false,
                        "No verified related workplace suggestions were saved for this topic.");
            if (!hasSources && !workIqAuthenticated)
                return new(
                    TopicSemanticInfoState.Unavailable,
                    "WorkIQ is not signed in",
                    false,
                    "WorkIQ is not signed in. Sign in to find related workplace context for this topic.");
            return RelatedContextPresentation.TopicStatus(
                hasSources ? RelatedContextState.Available : RelatedContextState.Unavailable);
        }

        var trail = new List<TrailItemViewModel>();
        var seenTrailTopics = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.TopicTrail.Count;)
        {
            var first = document.TopicTrail[index];
            var lastIndex = index;
            while (lastIndex + 1 < document.TopicTrail.Count &&
                   StringComparer.Ordinal.Equals(
                       document.TopicTrail[lastIndex + 1].TopicId,
                       first.TopicId))
                lastIndex++;
            var last = document.TopicTrail[lastIndex];
            var runCount = lastIndex - index + 1;
            topics.TryGetValue(last.TopicId, out var topic);
            var label = Display(topic?.Label ?? last.LabelAtTime);
            var returned = !seenTrailTopics.Add(last.TopicId);
            var collapsed = runCount > 1
                ? $" · {runCount} consecutive updates collapsed"
                : string.Empty;
            var detail =
                $"{(returned ? "Returned" : "Introduced")} · through segment {last.ThroughSegmentOrdinal}{collapsed}";
            trail.Add(new(
                first.Id,
                last.TopicId,
                (trail.Count + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                label,
                detail,
                returned,
                false,
                false,
                lastIndex + 1 < document.TopicTrail.Count,
                relationTextByTopic.GetValueOrDefault(last.TopicId, string.Empty),
                ContextStatus(last.TopicId)));
            index = lastIndex + 1;
        }

        var currentTrailIndex = trail.FindLastIndex(item => item.TopicId == current);
        var selectedTrailIndex = trail.FindLastIndex(item => item.TopicId == selected);
        for (var index = 0; index < trail.Count; index++)
        {
            trail[index] = trail[index] with
            {
                IsCurrent = index == currentTrailIndex,
                IsSelected = index == selectedTrailIndex,
                HasNext = index < trail.Count - 1,
            };
        }

        var selectedTopic = selected.Length > 0 && topics.TryGetValue(selected, out var topicCluster)
            ? topicCluster
            : null;

        var topicBrief = selectedTopic is null
            ? null
            : BuildTopicBrief(
                document, selectedTopic, topicLabels[selectedTopic.Id],
                selectedTopic.Id == current, ContextStatus(selectedTopic.Id), Display);
        var brainMap = selectedTopic is null
            ? null
            : BuildBrainMap(
                document, selectedTopic, topicLabels,
                current, occurrenceCounts, Display);
        var referencedSegmentOrdinals = document.Evidence
            .Where(item => item.SegmentOrdinal is not null)
            .Select(item => item.SegmentOrdinal!.Value)
            .ToHashSet();
        var fingerprint = string.Join("|",
            document.Speakers.Select(item => $"{item.SpeakerId}:{item.DisplayName}:{item.IsConfirmed}")
                .Concat(document.Segments.Where(item => referencedSegmentOrdinals.Contains(item.Ordinal)).Select(item =>
                    $"{item.Id}:{item.Ordinal}:{item.SpeakerId}:{item.Text}:{item.ConfidenceScore}"))
                .Concat(document.TopicClusters.Select(item => $"{item.Id}:{item.Label}:{string.Join(",", item.Aliases)}"))
                .Concat(document.TopicTrail.Select(item =>
                    $"{item.Id}:{item.TopicId}:{item.TranscriptRevision}:{item.ThroughSegmentOrdinal}:{item.LabelAtTime}"))
                .Concat(document.ExplainedTopicLinks.Select(item =>
                    $"{item.Id}:{item.FromTopicId}:{item.ToTopicId}:{item.Kind}:{item.Provenance}:{string.Join(",", item.EvidenceIds)}:{item.InferenceLabel}"))
                .Concat(document.Questions.Select(item =>
                    $"{item.Id}:{item.TopicId}:{item.Text}:{item.QuestionState}:{string.Join(",", item.EvidenceIds)}"))
                .Concat(document.Actions.Select(item =>
                    $"{item.Id}:{item.TopicId}:{item.Text}:{item.State}"))
                .Concat(document.Decisions.Select(item =>
                    $"{item.Id}:{item.TopicId}:{item.Text}:{string.Join(",", item.EvidenceIds)}"))
                .Concat(document.Evidence.Select(item =>
                    $"{item.Id}:{item.TopicId}:{item.Origin}:{item.Provenance}:{item.SegmentOrdinal}:{item.SourceTitle}:{item.SourceUrl}:{item.SourceType}:{item.SourceDate}:{item.Summary}:{item.PresentationLabel}:{item.RelevanceRationale}:{item.InvolvementRole}:{item.InvolvementMatchKind}:{item.SourceId}:{item.SuggestionId}"))
                .Concat((relatedContextStates ?? new Dictionary<string, RelatedContextState>())
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}:{item.Value}"))
                .Append($"read-only:{isReadOnly}"));
        return new(
            selected,
            current,
            trail,
            document.TopicClusters.Count == 0
                ? "No accepted topics yet. The timeline will fill in as topics are accepted."
                : string.Empty,
            fingerprint,
            brainMap,
            topicBrief);
    }

    private static BrainMapViewModel BuildBrainMap(
        MeetingVisualizationDocument document,
        TopicCluster topic,
        IReadOnlyDictionary<string, string> topicLabels,
        string currentTopicId,
        IReadOnlyDictionary<string, int> occurrenceCounts,
        Func<string?, string> display)
    {
        var transcriptEvidence = document.Evidence
            .Where(item =>
                (item.Origin is EvidenceOrigin.Transcript or EvidenceOrigin.Analysis) &&
                item.Provenance == VisualizationProvenance.TranscriptFact &&
                item.SegmentOrdinal is not null)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var branches = new List<BrainMapBranchViewModel>();

        branches.AddRange(transcriptEvidence.Values
            .Where(item =>
                item.TopicId == topic.Id &&
                !string.IsNullOrWhiteSpace(item.PresentationLabel))
            .OrderBy(item => item.SegmentOrdinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new BrainMapBranchViewModel(
                item.Id,
                topic.Id,
                "Transcript fact",
                display(item.PresentationLabel),
                $"Grounded in segment {item.SegmentOrdinal}")));

        branches.AddRange(document.Questions
            .Where(item =>
                item.TopicId == topic.Id &&
                item.QuestionState == QuestionState.Open &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new BrainMapBranchViewModel(
                item.Id,
                topic.Id,
                item.QuestionState == QuestionState.Answered ? "Answered question" : "Open question",
                display(item.Text),
                "Grounded in this meeting transcript")));

        branches.AddRange(document.Decisions
            .Where(item =>
                item.TopicId == topic.Id &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new BrainMapBranchViewModel(
                item.Id,
                topic.Id,
                "Decision",
                display(item.Text),
                "Grounded in cited transcript evidence")));

        branches.AddRange(document.Actions
            .Where(item =>
                item.TopicId == topic.Id &&
                item.State == VisualizationActionState.Open &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new BrainMapBranchViewModel(
                item.Id,
                topic.Id,
                "Open action",
                display(item.Text),
                "Grounded in cited transcript evidence")));

        foreach (var link in document.ExplainedTopicLinks
                     .Where(item =>
                         (item.FromTopicId == topic.Id || item.ToTopicId == topic.Id) &&
                         item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var outgoing = link.FromTopicId == topic.Id;
            var relatedTopicId = outgoing ? link.ToTopicId : link.FromTopicId;
            if (!topicLabels.TryGetValue(relatedTopicId, out var relatedLabel))
                continue;
            var relation = outgoing
                ? $"{RelationCue(link.Kind)} →"
                : $"← {RelationCue(link.Kind)}";
            branches.Add(new(
                link.Id,
                relatedTopicId,
                "Explained relationship",
                $"{relation} {relatedLabel}",
                "Grounded in cited transcript evidence"));
        }

        var (sources, _) = SelectBoundedWorkIqSources(document.Evidence.Where(item =>
            item.TopicId == topic.Id &&
            item.Origin == EvidenceOrigin.RelatedContext &&
            item.Provenance == VisualizationProvenance.RelatedSourceSuggestion));
        branches.AddRange(sources.Select(item =>
        {
            var family = SourceTypeLabel(item.SourceType);
            var title = RelatedContextTitle.Clean(item.SourceTitle, item.SourceType);
            if (title.Length == 0)
                title = family;
            return new BrainMapBranchViewModel(
                item.Id,
                topic.Id,
                "Sourced context",
                title,
                $"Suggestion—not meeting evidence · {family}");
        }));

        return new(
            topic.Id,
            topicLabels[topic.Id],
            topic.Id == currentTopicId ? "Current topic" : "Selected earlier topic",
            topic.Id == currentTopicId,
            topicLabels.GetValueOrDefault(currentTopicId, string.Empty),
            branches,
            branches.Count == 0
                ? "No connected topics yet. Grounded relationships and meeting details will appear here."
                : string.Empty);
    }

    private static string RelationCue(ExplainedTopicLinkKind kind) => kind switch
    {
        ExplainedTopicLinkKind.Supports => "Supports",
        ExplainedTopicLinkKind.Contradicts => "Contradicts",
        ExplainedTopicLinkKind.DependsOn => "Depends on",
        ExplainedTopicLinkKind.BuildsOn => "Builds on",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Builds grounded selection details for the combined topic-map view.
    /// </summary>
    private static TopicBriefViewModel BuildTopicBrief(
        MeetingVisualizationDocument document,
        TopicCluster topic,
        string label,
        bool isCurrent,
        TopicContextStatusViewModel contextStatus,
        Func<string?, string> display)
    {
        var segmentByOrdinal = document.Segments.ToDictionary(item => item.Ordinal);
        var transcriptEvidence = document.Evidence
            .Where(item =>
                (item.Origin is EvidenceOrigin.Transcript or EvidenceOrigin.Analysis) &&
                item.Provenance == VisualizationProvenance.TranscriptFact &&
                item.SegmentOrdinal is not null &&
                segmentByOrdinal.ContainsKey(item.SegmentOrdinal.Value))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var topicEvidence = transcriptEvidence.Values
            .Where(item => item.TopicId == topic.Id)
            .OrderBy(item => item.SegmentOrdinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var timingOrdinals = topicEvidence.Select(item => item.SegmentOrdinal!.Value)
            .Concat(document.TopicTrail
                .Where(item => item.TopicId == topic.Id)
                .Select(item => item.ThroughSegmentOrdinal))
            .Distinct()
            .Order()
            .ToArray();
        var discussedAt = timingOrdinals.Length == 0
            ? "Discussion timing is not available yet."
            : FormatDiscussionTiming(timingOrdinals, segmentByOrdinal);

        var facts = topicEvidence
            .Select(item =>
            {
                var segment = segmentByOrdinal[item.SegmentOrdinal!.Value];
                var title = string.IsNullOrWhiteSpace(item.PresentationLabel)
                    ? "Grounded transcript evidence"
                    : display(item.PresentationLabel);
                return new TopicBriefFactViewModel(
                    item.Id,
                    title,
                    display(segment.Text),
                    $"Segment {segment.Ordinal} · {segment.Timestamp.LocalDateTime:HH:mm:ss}");
            })
            .ToArray();

        var relationships = document.ExplainedTopicLinks
            .Where(item =>
                (item.FromTopicId == topic.Id || item.ToTopicId == topic.Id) &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item =>
            {
                var outgoing = item.FromTopicId == topic.Id;
                var relatedTopicId = outgoing ? item.ToTopicId : item.FromTopicId;
                var relatedLabel = document.TopicClusters
                    .FirstOrDefault(candidate => candidate.Id == relatedTopicId)?.Label ?? relatedTopicId;
                var citedSegments = item.EvidenceIds
                    .Where(transcriptEvidence.ContainsKey)
                    .Select(id => transcriptEvidence[id].SegmentOrdinal!.Value)
                    .Distinct()
                    .Order()
                    .ToArray();
                var direction = outgoing
                    ? $"{RelationCue(item.Kind)} → {display(relatedLabel)}"
                    : $"{display(relatedLabel)} → {RelationCue(item.Kind)}";
                return new TopicBriefRelationshipViewModel(
                    item.Id,
                    direction,
                    $"Grounded in transcript segment{(citedSegments.Length == 1 ? string.Empty : "s")} " +
                    string.Join(", ", citedSegments));
            })
            .ToArray();

        var questions = document.Questions
            .Where(item =>
                item.TopicId == topic.Id &&
                item.QuestionState == QuestionState.Open &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new TopicBriefQuestionViewModel(
                item.Id, display(item.Text), item.QuestionState == QuestionState.Answered))
            .ToArray();

        var actions = document.Actions
            .Where(item =>
                item.TopicId == topic.Id &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new TopicBriefActionViewModel(
                item.Id, display(item.Text), item.State == VisualizationActionState.Open))
            .ToArray();

        var decisions = document.Decisions
            .Where(item =>
                item.TopicId == topic.Id &&
                item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new TopicBriefDecisionViewModel(item.Id, display(item.Text)))
            .ToArray();

        var (boundedSources, verifiedTotal) = SelectBoundedWorkIqSources(
            document.Evidence.Where(item =>
                item.Origin == EvidenceOrigin.RelatedContext &&
                item.Provenance == VisualizationProvenance.RelatedSourceSuggestion &&
                item.TopicId == topic.Id));
        var sources = boundedSources.Select(source =>
        {
            var family = SourceTypeLabel(source.SourceType);
            var title = RelatedContextTitle.Clean(source.SourceTitle, source.SourceType);
            if (title.Length == 0)
                title = family;
            var date = source.SourceDate?.ToLocalTime().ToString("g") ?? "Date unavailable";
            var canOpen = Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                !string.IsNullOrWhiteSpace(uri.Host) &&
                string.IsNullOrEmpty(uri.UserInfo);
            var url = canOpen ? uri!.AbsoluteUri : string.Empty;
            var sourceDetail = $"Suggestion—not meeting evidence · {family}";
            var snippet = display(source.Summary);
            if (snippet.Length == 0)
                snippet = "No source snippet was provided.";
            var relevance = display(source.RelevanceRationale);
            var relevanceText = relevance.Length == 0
                ? "Why relevant: WorkIQ did not provide an explanation."
                : $"Why relevant: {relevance}";
            var involvementText = source.InvolvementRole is { } role
                ? $"Your involvement: {InvolvementRoleLabel(role)}"
                : "Your involvement: Verified role was not retained.";
            var accessibleDetails = string.Join(". ", new[]
            {
                sourceDetail,
                snippet,
                $"Source date: {date}",
                relevanceText,
                involvementText,
            }.Where(value => value.Length > 0));
            return new TopicBriefSourceViewModel(
                source.Id, title, family, sourceDetail, snippet, date,
                relevanceText, involvementText,
                url, canOpen, source.SourceType,
                canOpen
                    ? $"{accessibleDetails}. Open source."
                    : $"{accessibleDetails}. No verified source link is available.");
        }).ToArray();
        contextStatus = (contextStatus.State, sources.Length > 0) switch
        {
            (TopicSemanticInfoState.Refreshing, true) => contextStatus with
            {
                Text = "Refreshing suggestions · previous results remain visible",
                AccessibleName =
                    "Refreshing related workplace suggestions. Previous results remain visible."
            },
            (TopicSemanticInfoState.Failed, true) => contextStatus with
            {
                Text = "Refresh failed · previous suggestions remain visible",
                AccessibleName =
                    "Related workplace suggestion refresh failed. Previous results remain visible."
            },
            _ => contextStatus,
        };

        return new(
            topic.Id,
            label,
            isCurrent ? "Now · Current topic" : "Earlier topic you selected",
            discussedAt,
            isCurrent,
            contextStatus,
            facts,
            relationships,
            questions,
            actions,
            decisions,
            sources,
            verifiedTotal);
    }

    private static string InvolvementRoleLabel(ContextInvolvementRole role) => role switch
    {
        ContextInvolvementRole.TeamsAuthor => "Teams author",
        ContextInvolvementRole.TeamsParticipant => "Teams participant",
        ContextInvolvementRole.TeamsMember => "Teams member",
        ContextInvolvementRole.MeetingOrganizer => "Meeting organizer",
        ContextInvolvementRole.MeetingAttendee => "Meeting attendee",
        ContextInvolvementRole.EmailSender => "Email sender",
        ContextInvolvementRole.EmailToRecipient => "Email recipient",
        ContextInvolvementRole.EmailCcRecipient => "Email Cc recipient",
        ContextInvolvementRole.EmailBccRecipient => "Email Bcc recipient",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string FormatDiscussionTiming(
        IReadOnlyList<int> ordinals,
        IReadOnlyDictionary<int, VisualizationSegment> segments)
    {
        var first = ordinals[0];
        var last = ordinals[^1];
        var firstTime = segments.GetValueOrDefault(first)?.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        var lastTime = segments.GetValueOrDefault(last)?.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        var segmentText = first == last ? $"segment {first}" : $"segments {first}–{last}";
        if (firstTime is null)
            return $"Discussed around {segmentText}.";
        var timeText = first == last || lastTime is null || firstTime == lastTime
            ? firstTime
            : $"{firstTime}–{lastTime}";
        return $"Discussed around {segmentText} · {timeText}.";
    }

    /// <summary>
    /// Bounds a topic's participation-verified WorkIQ sources to at most 5, capping any one
    /// family at 2 while alternatives exist so the visible set stays family-diverse. Preserves
    /// provider rank as relevance (already the incoming order). The dropped remainder is reported
    /// as an honest total for a "+N verified suggestions" caption, never silently discarded.
    /// </summary>
    private static (IReadOnlyList<VisualizationEvidence> Selected, int TotalVerified) SelectBoundedWorkIqSources(
        IEnumerable<VisualizationEvidence> sourceItems)
    {
        var ordered = sourceItems
            .GroupBy(item => SourceIdentity(item), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        const int maxTotal = 5;
        const int maxPerFamily = 2;
        var familyCount = ordered.Select(item => item.SourceType).Distinct().Count();
        var selected = new List<VisualizationEvidence>();
        var perFamily = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            if (selected.Count >= maxTotal)
                break;
            var familyKey = item.SourceType?.ToString() ?? "none";
            var count = perFamily.GetValueOrDefault(familyKey);
            if (familyCount > 1 && count >= maxPerFamily)
                continue;
            perFamily[familyKey] = count + 1;
            selected.Add(item);
        }
        return (selected, ordered.Length);
    }

    private static string SourceIdentity(VisualizationEvidence evidence) =>
        Uri.TryCreate(evidence.SourceUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : evidence.Id;

    internal static string SourceTypeLabel(ContextSourceType? sourceType) => sourceType switch
    {
        ContextSourceType.TeamsMessage => "Teams message",
        ContextSourceType.TeamsChannel => "Teams channel",
        ContextSourceType.OutlookMail => "Outlook mail",
        ContextSourceType.Calendar => "Calendar",
        ContextSourceType.Document => "Document",
        ContextSourceType.MeetingArtifact => "Meeting artifact",
        ContextSourceType.LocalTranscript => "Prior transcript",
        _ => "Related source",
    };

    internal static string ProjectSpeakers(
        string? text,
        IReadOnlyDictionary<string, string> speakers)
    {
        var projected = text ?? string.Empty;
        foreach (var speaker in speakers.OrderByDescending(item => item.Key.Length))
            projected = Regex.Replace(
                projected,
                $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(speaker.Key)}(?![\p{{L}}\p{{N}}_])",
                _ => speaker.Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return projected;
    }
}
