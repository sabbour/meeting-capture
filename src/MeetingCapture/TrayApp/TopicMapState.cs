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
    TopicBriefViewModel? TopicBrief = null,
    MeetingBrainMapViewModel? MeetingBrainMap = null,
    MeetingNotesViewModel? MeetingNotes = null);

internal sealed record MeetingNotesViewModel(
    IReadOnlyList<MeetingTopicNotesViewModel> Topics,
    string EmptyMessage);

internal sealed record MeetingTopicNotesViewModel(
    string Id,
    string Heading,
    IReadOnlyList<string> Bullets,
    IReadOnlyList<MeetingTopicSuggestionViewModel> Suggestions,
    bool IsCurrent,
    bool IsSelected,
    bool IsCollapsed = false)
{
    public bool HasBullets => Bullets.Count > 0;
    public bool HasNoBullets => Bullets.Count == 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsExpanded => !IsCollapsed;
    public string ToggleGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public string ToggleName => IsCollapsed
        ? $"Expand topic: {Heading}"
        : $"Collapse topic: {Heading}";
}

internal static class TopicCollapseState
{
    internal static bool Toggle(ISet<string> collapsed, string topicId)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        if (collapsed.Remove(topicId))
            return false;
        collapsed.Add(topicId);
        return true;
    }

    internal static IReadOnlyList<int> LeafCounts(
        MeetingBrainMapViewModel map,
        IReadOnlySet<string> collapsed) =>
        map.Topics
            .Select(topic => collapsed.Contains(topic.Id) ? 0 : topic.Leaves.Count)
            .ToArray();

    internal static IReadOnlyList<MeetingTopicNotesViewModel> Apply(
        IEnumerable<MeetingTopicNotesViewModel> topics,
        IReadOnlySet<string> collapsed) =>
        topics.Select(topic => topic with { IsCollapsed = collapsed.Contains(topic.Id) }).ToArray();
}

internal sealed record MeetingTopicSuggestionViewModel(
    string Id,
    string Title,
    string SourceType,
    string SourceUrl,
    bool CanOpen,
    ContextSourceType? SourceKind = null)
{
    public string OpenActionName => $"Open {SourceType}: {Title}";
    public string Glyph => TopicMapState.SourceTypeGlyph(SourceKind);
}

internal sealed record MeetingBrainMapViewModel(
    string MeetingTitle,
    string Detail,
    string CurrentTopicId,
    IReadOnlyList<MeetingBrainMapTopicViewModel> Topics,
    string EmptyMessage);

internal sealed record MeetingBrainMapTopicViewModel(
    string Id,
    string Title,
    string Detail,
    IReadOnlyList<MeetingBrainMapLeafViewModel> Leaves);

internal sealed record MeetingBrainMapLeafViewModel(
    string Id,
    string TopicId,
    string Kind,
    string Title,
    string Detail)
{
    public string AccessibleName =>
        $"{Kind}: {Title}. {Detail}. Select to view matching meeting notes.";
}

internal sealed record MeetingBrainMapTopicLayout(
    BrainMapRect Topic,
    IReadOnlyList<BrainMapRect> Leaves);

internal readonly record struct BrainMapPoint(double X, double Y);

internal readonly record struct TopicMapRgb(byte Red, byte Green, byte Blue)
{
    internal double RelativeLuminance
    {
        get
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(Red) + 0.7152 * Linear(Green) + 0.0722 * Linear(Blue);
        }
    }

    internal double ContrastRatio(TopicMapRgb other)
    {
        var lighter = Math.Max(RelativeLuminance, other.RelativeLuminance);
        var darker = Math.Min(RelativeLuminance, other.RelativeLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }
}

internal sealed record MeetingBrainMapConnectorLayout(
    string Id,
    BrainMapPoint Start,
    BrainMapPoint End);

internal sealed record MeetingBrainMapLayout(
    double Width,
    double Height,
    BrainMapRect Root,
    IReadOnlyList<MeetingBrainMapTopicLayout> Topics,
    IReadOnlyList<MeetingBrainMapConnectorLayout> Connectors)
{
    internal static MeetingBrainMapLayout Calculate(
        double viewportWidth,
        IReadOnlyList<int> leafCounts)
    {
        ArgumentNullException.ThrowIfNull(leafCounts);
        if (leafCounts.Any(count => count < 0))
            throw new ArgumentOutOfRangeException(nameof(leafCounts));

        var width = Math.Max(320, viewportWidth);
        const double margin = 20;
        const double rootHeight = 90;
        const double topicHeight = 104;
        const double leafHeight = 104;
        const double horizontalGap = 24;
        const double verticalGap = 12;
        const double leafGap = 10;
        const double rowGap = 28;
        const double rootGap = 48;
        var columns = width >= 1400 ? 4 : width >= 1000 ? 3 : width >= 640 ? 2 : 1;
        var topicWidth = Math.Min(
            360,
            (width - margin * 2 - horizontalGap * (columns - 1)) / columns);
        var leafWidth = Math.Max(1, topicWidth - 20);
        var rootWidth = Math.Min(360, width - margin * 2);
        var rows = leafCounts.Count == 0
            ? 0
            : (leafCounts.Count + columns - 1) / columns;
        var rowHeights = new double[rows];
        for (var row = 0; row < rows; row++)
        {
            var firstTopic = row * columns;
            var rowTopicCount = Math.Min(columns, leafCounts.Count - firstTopic);
            var tallestBlock = Enumerable.Range(firstTopic, rowTopicCount)
                .Select(index => topicHeight + verticalGap +
                    leafCounts[index] * leafHeight + Math.Max(0, leafCounts[index] - 1) * leafGap)
                .Max();
            rowHeights[row] = tallestBlock;
        }

        var firstRowY = margin + rootHeight + rootGap;
        var root = new BrainMapRect(
            (width - rootWidth) / 2,
            margin,
            rootWidth,
            rootHeight);
        var topics = new List<MeetingBrainMapTopicLayout>(leafCounts.Count);
        var rowY = firstRowY;
        for (var row = 0; row < rows; row++)
        {
            var rowStart = row * columns;
            var rowTopicCount = Math.Min(columns, leafCounts.Count - rowStart);
            for (var column = 0; column < rowTopicCount; column++)
            {
                var index = rowStart + column;
                var x = margin + column * (topicWidth + horizontalGap);
                var topic = new BrainMapRect(x, rowY, topicWidth, topicHeight);
                var leaves = new List<BrainMapRect>(leafCounts[index]);
                var leafX = x + (topicWidth - leafWidth) / 2;
                var leafY = rowY + topicHeight + verticalGap;
                for (var leaf = 0; leaf < leafCounts[index]; leaf++)
                {
                    leaves.Add(new(leafX, leafY, leafWidth, leafHeight));
                    leafY += leafHeight + leafGap;
                }
                topics.Add(new(topic, leaves));
            }
            rowY += rowHeights[row] + rowGap;
        }

        var height = Math.Max(
            280,
            (rows == 0 ? firstRowY : rowY - rowGap) + margin);
        var connectors = new List<MeetingBrainMapConnectorLayout>();
        if (topics.Count > 0)
        {
            var rootCenterX = root.X + root.Width / 2;
            var rootBottom = root.Y + root.Height;
            var busY = rootBottom + rootGap / 2;
            connectors.Add(new(
                "root:trunk",
                new(rootCenterX, rootBottom),
                new(rootCenterX, busY)));

            var lanes = Enumerable.Range(0, Math.Min(columns, topics.Count))
                .Select(column => (Column: column, X: topics[column].Topic.X - 12))
                .ToArray();
            var busStart = Math.Min(rootCenterX, lanes.Min(item => item.X));
            var busEnd = Math.Max(rootCenterX, lanes.Max(item => item.X));
            if (busEnd > busStart)
                connectors.Add(new(
                    "root:bus",
                    new(busStart, busY),
                    new(busEnd, busY)));

            foreach (var lane in lanes)
            {
                var topicIndexes = Enumerable.Range(0, topics.Count)
                    .Where(index => index % columns == lane.Column)
                    .ToArray();
                var lastTopic = topics[topicIndexes[^1]];
                var laneBottom = lastTopic.Leaves.Count == 0
                    ? lastTopic.Topic.Y + lastTopic.Topic.Height
                    : lastTopic.Leaves[^1].Y + lastTopic.Leaves[^1].Height;
                connectors.Add(new(
                    $"lane:{lane.Column}",
                    new(lane.X, busY),
                    new(lane.X, laneBottom)));

                foreach (var index in topicIndexes)
                {
                    var topic = topics[index];
                    var topicCenterY = topic.Topic.Y + topic.Topic.Height / 2;
                    connectors.Add(new(
                        $"topic:{index}",
                        new(lane.X, topicCenterY),
                        new(topic.Topic.X, topicCenterY)));
                    for (var leafIndex = 0; leafIndex < topic.Leaves.Count; leafIndex++)
                    {
                        var leaf = topic.Leaves[leafIndex];
                        var leafCenterY = leaf.Y + leaf.Height / 2;
                        connectors.Add(new(
                            $"leaf:{index}:{leafIndex}",
                            new(lane.X, leafCenterY),
                            new(leaf.X, leafCenterY)));
                    }
                }
            }
        }
        return new(width, height, root, topics, connectors);
    }
}

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

    public string Glyph => TopicMapState.SourceTypeGlyph(SourceType);
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
        " Select to view details in the whole-meeting map.";
}

internal static class TopicMapState
{
    internal static TopicMapRgb TopicNodeBackground => new(38, 44, 58);
    internal static TopicMapRgb LeafNodeBackground => new(51, 58, 73);
    internal static TopicMapRgb NodeTextColor => new(250, 251, 255);

    /// <summary>Projects the chronological topic timeline and selected topic brief.</summary>
    internal static VisualizationProjection Project(
        MeetingVisualizationDocument document,
        string? selectedTopicId,
        string? currentTopicId = null,
        IReadOnlyDictionary<string, RelatedContextState>? relatedContextStates = null,
        bool workIqAuthenticated = true,
        bool isReadOnly = false,
        bool buildMeetingNotes = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        var topics = document.TopicClusters.ToDictionary(topic => topic.Id, StringComparer.Ordinal);
        var speakers = document.Speakers.ToDictionary(
            speaker => speaker.SpeakerId,
            speaker => speaker.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        string Display(string? text) =>
            MeetingNotesText.WithoutSegmentOrdinals(ProjectSpeakers(text, speakers).Trim());
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
                $"{(returned ? "Returned" : "Introduced")}{collapsed}";
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
        var meetingBrainMap = BuildMeetingBrainMap(
            document, orderedTopics, topicLabels, current, Display);
        var meetingNotes = buildMeetingNotes
            ? BuildMeetingNotesViewModel(
                MeetingTopicNotesBuilder.Build(document), document, selected, current)
            : null;
        var referencedSegmentOrdinals = document.Evidence
            .Where(item => item.SegmentOrdinal is not null)
            .Select(item => item.SegmentOrdinal!.Value)
            .ToHashSet();
        var fingerprint = string.Join("|",
            new[] { $"title:{document.MeetingTitle}" }
                .Concat(document.Speakers.Select(item => $"{item.SpeakerId}:{item.DisplayName}:{item.IsConfirmed}"))
                .Concat(document.Segments.Where(item => referencedSegmentOrdinals.Contains(item.Ordinal)).Select(item =>
                    $"{item.Id}:{item.Ordinal}:{item.SpeakerId}:{item.Text}:{item.ConfidenceScore}"))
                .Concat(document.TopicClusters.Select(item =>
                    $"{item.Id}:{item.Label}:{string.Join(",", item.Aliases)}:{item.FirstOccurrenceOrdinal}:{item.LastOccurrenceOrdinal}"))
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
            topicBrief,
            meetingBrainMap,
            meetingNotes);
    }

    internal static MeetingNotesViewModel BuildMeetingNotesViewModel(
        IReadOnlyList<MeetingTopicNotes> topicNotes,
        MeetingVisualizationDocument? document = null,
        string? selectedTopicId = null,
        string? currentTopicId = null)
    {
        ArgumentNullException.ThrowIfNull(topicNotes);
        var topicSources = (document?.Evidence ?? [])
            .Where(item => item.Origin == EvidenceOrigin.RelatedContext &&
                item.Provenance == VisualizationProvenance.RelatedSourceSuggestion)
            .ToLookup(item => item.TopicId, StringComparer.Ordinal);
        var topics = topicNotes
            .Select(topic => new MeetingTopicNotesViewModel(
                topic.TopicId,
                topic.Topic,
                topic.Bullets.Select(MeetingNotesText.WithoutSegmentOrdinals).ToArray(),
                document is null
                    ? []
                    : SelectBoundedWorkIqSources(topicSources[topic.TopicId])
                        .Selected
                        .Select(source =>
                        {
                            var title = RelatedContextTitle.Clean(source.SourceTitle, source.SourceType);
                            if (title.Length == 0)
                                title = SourceTypeLabel(source.SourceType);
                            var canOpen = Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out var uri) &&
                                uri.Scheme == Uri.UriSchemeHttps &&
                                !string.IsNullOrWhiteSpace(uri.Host) &&
                                string.IsNullOrEmpty(uri.UserInfo);
                            return new MeetingTopicSuggestionViewModel(
                                source.Id,
                                MeetingNotesText.WithoutSegmentOrdinals(title),
                                SourceTypeLabel(source.SourceType),
                                canOpen ? uri!.AbsoluteUri : string.Empty,
                                canOpen,
                                source.SourceType);
                        })
                        .ToArray(),
                StringComparer.Ordinal.Equals(topic.TopicId, currentTopicId),
                StringComparer.Ordinal.Equals(topic.TopicId, selectedTopicId)))
            .ToArray();
        return new(
            topics,
            topics.Length == 0
                ? "No accepted topics yet. Meeting notes will appear as topics are accepted."
                : string.Empty);
    }

    internal static string? CurrentTopicSelection(VisualizationProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return projection.CurrentTopicId.Length == 0 ? null : projection.CurrentTopicId;
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
                "Grounded in this meeting's transcript")));

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

    private static MeetingBrainMapViewModel BuildMeetingBrainMap(
        MeetingVisualizationDocument document,
        IReadOnlyList<TopicCluster> orderedTopics,
        IReadOnlyDictionary<string, string> topicLabels,
        string currentTopicId,
        Func<string?, string> display)
    {
        var segments = document.Segments.ToDictionary(item => item.Ordinal);
        var speakers = document.Speakers.ToDictionary(
            item => item.SpeakerId,
            item => item.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        var transcriptEvidence = document.Evidence
            .Where(item =>
                (item.Origin is EvidenceOrigin.Transcript or EvidenceOrigin.Analysis) &&
                item.Provenance == VisualizationProvenance.TranscriptFact &&
                item.SegmentOrdinal is not null &&
                segments.ContainsKey(item.SegmentOrdinal.Value))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        bool BelongsToTopic(string? assignedTopicId, IReadOnlyList<string> evidenceIds, string topicId)
        {
            if (StringComparer.Ordinal.Equals(assignedTopicId, topicId))
                return true;
            return assignedTopicId is null && evidenceIds.Any(id =>
                transcriptEvidence.TryGetValue(id, out var evidenceItem) &&
                StringComparer.Ordinal.Equals(evidenceItem.TopicId, topicId));
        }

        string EvidenceDetail(IEnumerable<string> evidenceIds)
        {
            var cited = evidenceIds
                .Where(transcriptEvidence.ContainsKey)
                .Select(id => transcriptEvidence[id])
                .OrderBy(item => item.SegmentOrdinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (cited?.SegmentOrdinal is not int ordinal)
                return "Grounded in this meeting transcript";
            var segment = segments[ordinal];
            var speaker = string.IsNullOrWhiteSpace(segment.SpeakerId)
                ? "Unidentified speaker"
                : speakers.GetValueOrDefault(segment.SpeakerId, segment.SpeakerId);
            return $"{speaker} · {segment.Timestamp.LocalDateTime:HH:mm:ss}";
        }

        var projectedTopics = orderedTopics.Select(topic =>
        {
            var leaves = new List<MeetingBrainMapLeafViewModel>();
            foreach (var fact in transcriptEvidence.Values
                         .Where(item => item.TopicId == topic.Id)
                         .OrderBy(item => item.SegmentOrdinal)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                var label = string.IsNullOrWhiteSpace(fact.PresentationLabel)
                    ? fact.Summary
                    : fact.PresentationLabel;
                if (string.IsNullOrWhiteSpace(label))
                    label = "Transcript key point";
                var factDetail = string.IsNullOrWhiteSpace(fact.Summary)
                    ? EvidenceDetail([fact.Id])
                    : $"{EvidenceDetail([fact.Id])} · {display(fact.Summary)}";
                leaves.Add(new(
                    fact.Id,
                    topic.Id,
                    "Key point",
                    display(label),
                    factDetail));
            }

            leaves.AddRange(document.Decisions
                .Where(item =>
                    BelongsToTopic(item.TopicId, item.EvidenceIds, topic.Id) &&
                    item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new MeetingBrainMapLeafViewModel(
                    item.Id,
                    topic.Id,
                    "Decision",
                    display(item.Text),
                    EvidenceDetail(item.EvidenceIds))));

            leaves.AddRange(document.Questions
                .Where(item =>
                    BelongsToTopic(item.TopicId, item.EvidenceIds, topic.Id) &&
                    item.QuestionState == QuestionState.Open &&
                    item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new MeetingBrainMapLeafViewModel(
                    item.Id,
                    topic.Id,
                    "Open question",
                    display(item.Text),
                    EvidenceDetail(item.EvidenceIds))));

            leaves.AddRange(document.Actions
                .Where(item =>
                    BelongsToTopic(item.TopicId, item.EvidenceIds, topic.Id) &&
                    item.State is VisualizationActionState.Open or VisualizationActionState.Completed &&
                    item.EvidenceIds.Any(transcriptEvidence.ContainsKey))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new MeetingBrainMapLeafViewModel(
                    item.Id,
                    topic.Id,
                    item.State == VisualizationActionState.Completed ? "Completed action" : "Action",
                    display(item.Text),
                    EvidenceDetail(item.EvidenceIds))));

            var detail = leaves.Count == 0
                ? "No transcript-grounded details yet"
                : $"{leaves.Count} grounded point{(leaves.Count == 1 ? string.Empty : "s")}";
            if (topic.Id == currentTopicId)
                detail = $"Current topic · {detail}";
            return new MeetingBrainMapTopicViewModel(
                topic.Id,
                topicLabels[topic.Id],
                detail,
                leaves);
        }).ToArray();

        return new(
            document.MeetingTitle,
            $"Whole meeting · {projectedTopics.Length} topic{(projectedTopics.Length == 1 ? string.Empty : "s")}",
            currentTopicId,
            projectedTopics,
            projectedTopics.Length == 0
                ? "No accepted topics yet. The topic map will grow as topics are accepted."
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
                    segment.Timestamp.LocalDateTime.ToString("HH:mm:ss"));
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
                var citedTimes = item.EvidenceIds
                    .Where(transcriptEvidence.ContainsKey)
                    .Select(id => transcriptEvidence[id].SegmentOrdinal!.Value)
                    .Distinct()
                    .Order()
                    .Select(ordinal => segmentByOrdinal[ordinal].Timestamp.LocalDateTime.ToString("HH:mm:ss"))
                    .ToArray();
                var direction = outgoing
                    ? $"{RelationCue(item.Kind)} → {display(relatedLabel)}"
                    : $"{display(relatedLabel)} → {RelationCue(item.Kind)}";
                return new TopicBriefRelationshipViewModel(
                    item.Id,
                    direction,
                    $"Grounded in this meeting's transcript · " +
                    string.Join(", ", citedTimes));
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
                : "Your involvement: Not established; access does not show participation.";
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
        if (firstTime is null)
            return "Discussion timing is not available yet.";
        var timeText = first == last || lastTime is null || firstTime == lastTime
            ? firstTime
            : $"{firstTime}–{lastTime}";
        return $"Discussed around {timeText}.";
    }

    /// <summary>
    /// Takes two rounds across source families before filling remaining slots by provider rank.
    /// </summary>
    private static (IReadOnlyList<VisualizationEvidence> Selected, int TotalVerified) SelectBoundedWorkIqSources(
        IEnumerable<VisualizationEvidence> sourceItems)
    {
        var ordered = sourceItems
            .GroupBy(item => SourceIdentity(item), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        const int maxTotal = 5;
        var selected = new List<VisualizationEvidence>();
        var families = ordered.GroupBy(item => item.SourceType)
            .Select(group => new Queue<VisualizationEvidence>(group)).ToArray();
        for (var round = 0; round < 2 && selected.Count < maxTotal; round++)
        {
            foreach (var family in families)
            {
                if (family.TryDequeue(out var item))
                    selected.Add(item);
                if (selected.Count == maxTotal)
                    break;
            }
        }
        var selectedIds = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        selected.AddRange(ordered.Where(item => !selectedIds.Contains(item.Id))
            .Take(maxTotal - selected.Count));
        return (selected, ordered.Length);
    }

    private static string SourceIdentity(VisualizationEvidence evidence) =>
        Uri.TryCreate(evidence.SourceUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? "url:" + uri.AbsoluteUri
            : $"title:{evidence.SourceType}:" + RelatedContextTitle.Clean(
                evidence.SourceTitle, evidence.SourceType);

    internal static string SourceTypeGlyph(ContextSourceType? sourceType) => sourceType switch
    {
        ContextSourceType.TeamsMessage => "\uE8BD",
        ContextSourceType.TeamsGroupChat => "\uE902",
        ContextSourceType.TeamsChannel => "\uE8A4",
        ContextSourceType.OutlookMail => "\uE715",
        ContextSourceType.Calendar or ContextSourceType.MeetingArtifact => "\uE787",
        ContextSourceType.Document => "\uE8A5",
        ContextSourceType.LocalTranscript => "\uE8F1",
        _ => "\uE946",
    };

    internal static string SourceTypeLabel(ContextSourceType? sourceType) => sourceType switch
    {
        ContextSourceType.TeamsMessage => "Teams message",
        ContextSourceType.TeamsGroupChat => "Teams group chat",
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
