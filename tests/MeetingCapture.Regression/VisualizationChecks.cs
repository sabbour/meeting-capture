using MeetingCapture.Intelligence;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetingCapture.Regression;

internal static class VisualizationChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("visualization keeps canonical IDs through aliases and speaker projection", CanonicalIds);
        yield return ("visualization rejects duplicate canonical model labels without semantic client matching", SemanticTopicOwnership);
        yield return ("analysis canonically merges UI-created reworded topic clusters", CanonicalReassignment);
        yield return ("topic refreshes coalesce paraphrases but preserve switches and returns", TopicRefreshNoise);
        yield return ("one coherent introduction collapses eight provisional topic paraphrases", TopicProliferation);
        yield return ("continuing topic labels and spans evolve through grounded later evidence", TopicEvolution);
        yield return ("visualization publishes ordered changes outside its model lock", ChangedPublishing);
        yield return ("visualization keeps suggestions separate from explained topic links", Grounding);
        yield return ("visualization analysis upserts atomically and rejects inference laundering", AnalysisUpdates);
        yield return ("sequential visualization deltas preserve prior stable map state", SequentialDeltas);
        yield return ("visualization intelligence parses one grounded shared delta", AnalysisParsing);
        yield return ("visualization prompt state stays bounded and corrections reanalyze cited sources", IncrementalContext);
        yield return ("visualization JSON round trips and classifies legacy corrupt future and stale data", StorageOutcomes);
        yield return ("visualization JSON preserves grounded inferred topic links", InferredRelationshipPersistence);
        yield return ("visualization writer and reader enforce identical size and collection bounds", StorageBounds);
        yield return ("pipeline analysis produces persisted topic graph data", PipelineAnalysis);
        yield return ("pipeline live visualization analysis is shared and revision guarded", LiveAnalysis);
        yield return ("visualization rejects replaced sessions and corrected analyzed prefixes", AnalysisPrefixRejection);
        yield return ("pipeline writes and updates companion visualization JSON", PipelinePersistence);
        yield return ("pipeline preserves Markdown and retries failed visualization JSON", PipelineFailureRetry);
        yield return ("pipeline exit retries pending visualization JSON once", PipelineExitRetry);
        yield return ("pipeline cancellation after Markdown preserves pending visualization retry", PipelineCancellationRetry);
        yield return ("pipeline discard creates no visualization sidecar", PipelineDiscard);
    }

    private static Task CanonicalIds()
    {
        var session = CreateSession();
        var first = session.AcceptTopic("Release planning", 1, 1);
        var alias = session.AcceptTopic(" release   planning ", 1, 1, first.Id);
        var reassigned = session.AcceptTopic("Shipping discussion", 1, 1, first.Id);
        session.ApplySpeakerAssignment("Guest-1", "Alex", true);
        var snapshot = session.Snapshot();

        Check.Equal("seg-000001", snapshot.Segments.Single().Id);
        Check.Equal(first.Id, alias.Id);
        Check.Equal(first.Id, reassigned.Id);
        Check.Equal(1, snapshot.TopicClusters.Count);
        Check.Equal(1, snapshot.TopicTrail.Count);
        Check.Equal("Guest-1", snapshot.Segments.Single().SpeakerId);
        Check.Equal("Alex", snapshot.Speakers.Single().DisplayName);
        Check.Equal("Discuss release options", snapshot.Segments.Single().Text);
        return Task.CompletedTask;
    }

    private static async Task ChangedPublishing()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-events", "Events", DateTimeOffset.UtcNow);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var revisions = new List<long>();
        var revisionsGate = new object();
        session.Changed += (_, revision) =>
        {
            lock (revisionsGate) revisions.Add(revision);
            if (revision == 2)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var first = Task.Run(() => session.AppendOrUpdateSegment(1,
            new("First segment", DateTimeOffset.UtcNow, SpeakerId: "Guest-1")));
        Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var concurrent = Task.Run(() =>
        {
            _ = session.Snapshot();
            session.ApplySpeakerAssignment("Guest-1", "Alex");
        });
        await concurrent.WaitAsync(TimeSpan.FromSeconds(2));
        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));

        lock (revisionsGate)
            Check.True(revisions.SequenceEqual([2L, 3L]));
    }

    private static Task SemanticTopicOwnership()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-semantic-owner", "Semantic ownership", DateTimeOffset.UtcNow);
        session.AppendOrUpdateSegment(1, new(
            "The coach described changes to the batting stance.", DateTimeOffset.UtcNow));
        var mechanics = session.AcceptTopic("Batting mechanics", 1, 1);
        session.AppendOrUpdateSegment(2, new(
            "The group discussed scheduling the next practice.", DateTimeOffset.UtcNow));
        session.AcceptTopic("Practice scheduling", 2, 2);

        var analyzed = session.Snapshot();
        Check.Equal(2, analyzed.TopicClusters.Count);
        var duplicateResponse = $$"""
            {
              "updateMode":"Delta",
              "throughSegmentOrdinal":2,
              "topicAssignments":[
                {"occurrenceId":"{{analyzed.TopicTrail[0].Id}}","label":"Hitting advice","redirectToTopicId":null,"throughSegmentOrdinal":1},
                {"occurrenceId":"{{analyzed.TopicTrail[1].Id}}","label":"Hitting advice","redirectToTopicId":null,"throughSegmentOrdinal":2}
              ],
              "evidence":[],
              "questions":[],
              "explainedTopicLinks":[],
              "actions":[],
              "decisions":[]
            }
            """;
        var duplicate = IntelligenceResponseParser.ParseVisualizationAnalysis(
            duplicateResponse, "synthetic", "synthetic-model");
        Check.Throws<InvalidOperationException>(() =>
            session.ApplyAnalysisResult(analyzed, duplicate));

        var unchanged = session.Snapshot();
        Check.Equal(2, unchanged.TopicClusters.Count);
        Check.True(unchanged.TopicClusters.Select(item => item.Label)
            .SequenceEqual(["Batting mechanics", "Practice scheduling"]));
        Check.Equal(2, unchanged.TopicTrail.Count);

        Check.Throws<InvalidOperationException>(() =>
            session.ApplyAnalysisResult(analyzed, new(
                [
                    new(analyzed.TopicTrail[0].Id, "Batting mechanics", mechanics.Id, 1),
                    new(analyzed.TopicTrail[1].Id, "Practice scheduling", mechanics.Id, 2)
                ],
                new([], [], []))));

        session.ApplyAnalysisResult(analyzed, new(
            [
                new(analyzed.TopicTrail[0].Id, "Batting mechanics", mechanics.Id, 1),
                new(analyzed.TopicTrail[1].Id, "Batting mechanics", mechanics.Id, 2)
            ],
            new([], [], []))
        {
            ThroughSegmentOrdinal = 2,
            SuppliedSegmentOrdinals = [1, 2]
        });
        var merged = session.Snapshot();
        Check.Equal(1, merged.TopicClusters.Count);
        Check.Equal(mechanics.Id, merged.TopicClusters.Single().Id);
        Check.Equal("Batting mechanics", merged.TopicClusters.Single().Label);
        return Task.CompletedTask;
    }

    private static Task CanonicalReassignment()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-merge", "Merge", DateTimeOffset.UtcNow);
        session.AppendOrUpdateSegment(1, new("Discuss the release plan", DateTimeOffset.UtcNow));
        var release = session.AcceptTopic("release plan", 1, 1);
        session.AppendOrUpdateSegment(2, new("The shipping plan is unchanged", DateTimeOffset.UtcNow));
        var provisional = session.AcceptTopic("shipping plan", 2, 2);
        session.ApplyRelatedContext(provisional.Id, new(DateTimeOffset.UtcNow,
            [new("Source", "Related shipping context.", ClaimKind.HistoricalFact,
                [new("Source", "https://example.test/first", ContextSourceType.Document, null)])],
            RelatedContextAvailability.Available));
        var analyzed = session.Snapshot();
        var occurrence = analyzed.TopicTrail.Single(item => item.ThroughSegmentOrdinal == 2);
        session.ApplyAnalysisResult(analyzed, new(
            [
                new(analyzed.TopicTrail.Single(item => item.ThroughSegmentOrdinal == 1).Id,
                    "shipping plan", release.Id, 1),
                new(occurrence.Id, "shipping plan", release.Id, 2)
            ],
            new(
                [new("question-merge", provisional.Id, "Is the plan unchanged?",
                    QuestionState.Open, ["fact-merge"])],
                [new("fact-merge", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                    2, provisional.Id, null, null, null, null, "The shipping plan was discussed.",
                    "Shipping plan")],
                [])));

        var merged = session.Snapshot();
        Check.Equal(1, merged.TopicClusters.Count);
        Check.Equal(release.Id, merged.TopicClusters.Single().Id);
        Check.True(merged.TopicClusters.Single().Aliases.Contains("release plan"));
        Check.True(merged.TopicClusters.Single().Aliases.Contains("shipping plan"));
        Check.Equal(1, merged.TopicTrail.Count);
        Check.True(merged.TopicTrail.All(item => item.TopicId == release.Id));
        Check.True(merged.Evidence.All(item => item.TopicId == release.Id));
        Check.Equal(release.Id, merged.Questions.Single().TopicId);

        session.ApplyRelatedContext(provisional.Id, new(DateTimeOffset.UtcNow,
            [new("Delayed source", "Delayed WorkIQ context.", ClaimKind.HistoricalFact,
                [new("Delayed source", "https://example.test/delayed", ContextSourceType.Document, null)])],
            RelatedContextAvailability.Available));
        var delayed = session.Snapshot();
        Check.Equal(2, delayed.Evidence.Count(item => item.Origin == EvidenceOrigin.RelatedContext));
        Check.True(delayed.Evidence.All(item => item.TopicId == release.Id));
        Check.True(delayed.ExplainedTopicLinks.All(item =>
            delayed.TopicClusters.Any(topic => topic.Id == item.FromTopicId) &&
            delayed.TopicClusters.Any(topic => topic.Id == item.ToTopicId)));
        return Task.CompletedTask;
    }

    private static Task TopicRefreshNoise()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-topic-noise", "Topic noise", DateTimeOffset.UtcNow);
        session.AppendOrUpdateSegment(1, new("Kubernetes coordinates container workloads.", DateTimeOffset.UtcNow));
        var kubernetes = session.AcceptTopic("Kubernetes orchestration", 1, 1);
        session.AppendOrUpdateSegment(2, new("The orchestration discussion continues.", DateTimeOffset.UtcNow));
        session.AcceptTopic("Kubernetes orchestration", 2, 2);
        Check.Equal(2, session.Snapshot().TopicTrail.Count);

        session.AppendOrUpdateSegment(3, new("Managed clusters provide the same orchestration capability.", DateTimeOffset.UtcNow));
        var provisional = session.AcceptTopic("Managed cluster architecture", 3, 3);
        var analyzed = session.Snapshot();
        var occurrence = analyzed.TopicTrail.Single(item => item.ThroughSegmentOrdinal == 3);
        session.ApplyAnalysisResult(analyzed, new(
            [
                .. analyzed.TopicTrail.Select(item =>
                    new VisualizationTopicAssignment(
                        item.Id, "Kubernetes platform", kubernetes.Id,
                        item.ThroughSegmentOrdinal))
            ],
            new([], [
                new("fact-platform", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                    3, provisional.Id, null, null, null, null,
                    "Managed clusters continue the Kubernetes discussion.", "Managed clusters")
            ], [])));

        var merged = session.Snapshot();
        Check.Equal(1, merged.TopicClusters.Count);
        Check.Equal(1, merged.TopicTrail.Count);
        Check.True(merged.TopicClusters.Single().Aliases.Contains("Managed cluster architecture"));
        Check.True(merged.TopicClusters.Single().Aliases.Contains("Kubernetes platform"));
        Check.Equal(kubernetes.Id, merged.Evidence.Single().TopicId);

        session.AppendOrUpdateSegment(4, new("The discussion switches to microservice deployment.", DateTimeOffset.UtcNow));
        var deployment = session.AcceptTopic("Microservice deployment", 4, 4);
        session.AppendOrUpdateSegment(5, new("The group returns to Kubernetes orchestration.", DateTimeOffset.UtcNow));
        session.AcceptTopic("Kubernetes platform", 5, 5);
        var beforeRefresh = session.Snapshot();
        var occurrences = beforeRefresh.TopicTrail.ToDictionary(
            item => item.ThroughSegmentOrdinal);
        session.ApplyAnalysisResult(beforeRefresh, new([
            new(occurrences[1].Id, "Kubernetes platform", kubernetes.Id, 1),
            new(occurrences[4].Id, "Microservice deployment", deployment.Id, 4),
            new(occurrences[5].Id, "Kubernetes platform", kubernetes.Id, 5)
        ], new([], [], [])));

        var final = session.Snapshot();
        Check.Equal(2, final.TopicClusters.Count);
        Check.Equal(3, final.TopicTrail.Count);
        Check.True(final.TopicTrail.Select(item => item.TopicId)
            .SequenceEqual([kubernetes.Id, deployment.Id, kubernetes.Id]));
        return Task.CompletedTask;
    }

    private static Task TopicProliferation()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-eight-topics", "Coherent introduction", DateTimeOffset.UtcNow);
        var labels = new[]
        {
            "Kubernetes introduction",
            "Container orchestration overview",
            "Managed Kubernetes services",
            "Kubernetes cluster architecture",
            "Container workload coordination",
            "Kubernetes deployment model",
            "Orchestrated microservices",
            "Kubernetes platform concepts"
        };
        string? canonicalId = null;
        for (var index = 0; index < labels.Length; index++)
        {
            var ordinal = index + 1;
            session.AppendOrUpdateSegment(ordinal, new(
                $"The introduction continues with Kubernetes concept {ordinal}.",
                DateTimeOffset.UtcNow.AddSeconds(ordinal)));
            canonicalId ??= session.AcceptTopic(labels[index], ordinal, ordinal).Id;
            if (ordinal > 1) session.AcceptTopic(labels[index], ordinal, ordinal);
        }
        var analyzed = session.Snapshot();
        Check.Equal(8, analyzed.TopicClusters.Count);
        Check.Equal(8, analyzed.TopicTrail.Count);
        session.ApplyAnalysisResult(analyzed, new(
            analyzed.TopicTrail.Select(item =>
                new VisualizationTopicAssignment(
                    item.Id, "Kubernetes fundamentals", canonicalId, item.ThroughSegmentOrdinal))
                .ToArray(),
            new([], [], [])));

        var collapsed = session.Snapshot();
        Check.Equal(1, collapsed.TopicClusters.Count);
        Check.Equal(1, collapsed.TopicTrail.Count);
        Check.Equal(9, collapsed.TopicClusters.Single().Aliases.Count);
        return Task.CompletedTask;
    }

    private static Task TopicEvolution()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-topic-evolution", "Evolving topic", DateTimeOffset.UtcNow);
        session.AppendOrUpdateSegment(1, new("An opening analogy introduces usability.", DateTimeOffset.UtcNow));
        var topic = session.AcceptTopic("Out-of-box usability preference", 1, 1);
        session.AppendOrUpdateSegment(2, new("The main discussion moves to a unified platform experience.", DateTimeOffset.UtcNow));
        session.AppendOrUpdateSegment(3, new("The platform dashboard is the concrete current subject.", DateTimeOffset.UtcNow));
        var analyzed = session.Snapshot();
        session.ApplyAnalysisResult(analyzed, new(
            [new(analyzed.TopicTrail.Single().Id, "Unified platform experience", topic.Id, 1)],
            new([], [
                new("fact-opening", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                    1, topic.Id, null, null, null, null, "An analogy introduced usability.",
                    "Usability analogy"),
                new("fact-current", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                    3, topic.Id, null, null, null, null,
                    "The current discussion concerns a unified platform experience.",
                    "Unified platform")
            ], [])));

        var evolved = session.Snapshot();
        Check.Equal(1, evolved.TopicClusters.Count);
        Check.Equal(1, evolved.TopicTrail.Count);
        Check.Equal("Unified platform experience", evolved.TopicClusters.Single().Label);
        Check.Equal(1, evolved.TopicClusters.Single().FirstOccurrenceOrdinal);
        Check.Equal(3, evolved.TopicClusters.Single().LastOccurrenceOrdinal);
        return Task.CompletedTask;
    }

    private static Task Grounding()
    {
        var session = CreateSession();
        var first = session.AcceptTopic("Release", 1, 1);
        var second = session.AcceptTopic("Risks", 1, 1);
        var related = new RelatedContextSnapshot(DateTimeOffset.UtcNow,
            [new("Related release sources", "Open each source to verify relevance.",
                ClaimKind.HistoricalFact,
                [
                    new("Teams thread", "https://teams.microsoft.com/l/message/release",
                        ContextSourceType.TeamsMessage, null,
                        SourceId: "teams-release",
                        InvolvementRationale: "Verified Teams author by exact directory object ID.",
                        InvolvementRole: ContextInvolvementRole.TeamsAuthor,
                        InvolvementMatchKind: ContextIdentityMatchKind.ObjectId),
                    new("Release channel", "https://teams.microsoft.com/l/channel/release",
                        ContextSourceType.TeamsChannel, null),
                    new("Prior review", "https://teams.microsoft.com/l/meeting/release",
                        ContextSourceType.MeetingArtifact, null),
                    new("Release mail", "https://outlook.office.com/mail/release",
                        ContextSourceType.OutlookMail, null),
                    new("Release plan", "https://example.test/plan",
                        ContextSourceType.Document, null)
                ], RelevanceRationale: "The source discusses release readiness.",
                SuggestionId: "workiq-release")],
            RelatedContextAvailability.Available);
        session.ApplyRelatedContext(first.Id, related);
        var suggestions = session.Snapshot().Evidence;
        Check.Equal(0, session.Snapshot().ExplainedTopicLinks.Count);
        Check.Equal(5, suggestions.Count);
        Check.True(suggestions.All(item =>
            item.TopicId == first.Id &&
            item.Origin == EvidenceOrigin.RelatedContext &&
            item.Provenance == VisualizationProvenance.RelatedSourceSuggestion));
        Check.Equal(5, suggestions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Check.Equal(5, suggestions.Select(item => item.SourceType).Distinct().Count());
        Check.Equal("The source discusses release readiness.",
            suggestions.Single(item => item.SourceType == ContextSourceType.TeamsMessage)
                .RelevanceRationale);
        var teamsSuggestion = suggestions.Single(
            item => item.SourceType == ContextSourceType.TeamsMessage);
        Check.Equal("Verified Teams author by exact directory object ID.",
            teamsSuggestion.InvolvementRationale);
        Check.Equal(ContextInvolvementRole.TeamsAuthor, teamsSuggestion.InvolvementRole);
        Check.Equal(ContextIdentityMatchKind.ObjectId, teamsSuggestion.InvolvementMatchKind);
        Check.Equal("teams-release", teamsSuggestion.SourceId);
        Check.Equal("workiq-release", teamsSuggestion.SuggestionId);
        var contextRevision = session.Revision;
        session.ApplyRelatedContext(first.Id, related);
        Check.Equal(contextRevision, session.Revision);
        Check.Equal(5, session.Snapshot().Evidence.Count);
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [],
            [new("source-link", first.Id, second.Id, ExplainedTopicLinkKind.Supports,
                VisualizationProvenance.TranscriptFact, [suggestions[0].Id], null)])));

        var evidence = new VisualizationEvidence("analysis-1", EvidenceOrigin.Analysis,
            VisualizationProvenance.TranscriptFact, 1, first.Id,
            null, null, null, null, "Release was discussed.", "Release options");
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [],
            [new("topic-link-1", first.Id, second.Id, ExplainedTopicLinkKind.Supports,
                VisualizationProvenance.TranscriptFact, ["missing"], null)])));

        var question = new DiscussionQuestion("question-1", first.Id, "Ship now?",
            QuestionState.Answered, ["analysis-1"]);
        var secondEvidence = new VisualizationEvidence("analysis-2", EvidenceOrigin.Analysis,
            VisualizationProvenance.TranscriptFact, 1, second.Id,
            null, null, null, null, "Risks were raised.", "Release risks");
        // A link needs cited transcript evidence on both topics.
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(
            new([], [evidence, secondEvidence],
                [new("one-sided-link", first.Id, second.Id, ExplainedTopicLinkKind.DependsOn,
                    VisualizationProvenance.TranscriptFact, ["analysis-1"], null)])));
        session.ApplyAnalysisDelta(new([question], [evidence, secondEvidence],
            [new("topic-link-1", first.Id, second.Id, ExplainedTopicLinkKind.DependsOn,
                VisualizationProvenance.TranscriptFact, ["analysis-1", "analysis-2"], null)]));
        // Transcript-grounded inference is allowed when both sides are cited and labelled.
        session.ApplyAnalysisDelta(new([], [],
            [new("topic-link-2", second.Id, first.Id, ExplainedTopicLinkKind.BuildsOn,
                VisualizationProvenance.Inference, ["analysis-2", "analysis-1"],
                "Risks continue the release thread")]));
        // An inferred link without a stated inference label is rejected.
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [],
            [new("topic-link-3", first.Id, second.Id, ExplainedTopicLinkKind.BuildsOn,
                VisualizationProvenance.Inference, ["analysis-1", "analysis-2"], null)])));
        var snapshot = session.Snapshot();
        Check.Equal(2, snapshot.ExplainedTopicLinks.Count);
        Check.True(snapshot.ExplainedTopicLinks.Any(item =>
            item.Kind == ExplainedTopicLinkKind.BuildsOn &&
            item.Provenance == VisualizationProvenance.Inference));
        Check.Equal(1, snapshot.Questions.Count);
        return Task.CompletedTask;
    }

    private static Task AnalysisUpdates()
    {
        var session = CreateSession();
        var first = session.AcceptTopic("Release", 1, 1);
        var second = session.AcceptTopic("Risks", 1, 1);
        var fact = new VisualizationEvidence("fact-1", EvidenceOrigin.Analysis,
            VisualizationProvenance.TranscriptFact, 1, first.Id,
            null, null, null, null, "Release options were discussed.", "Release options");
        var question = new DiscussionQuestion("question-1", first.Id, "Ship now?",
            QuestionState.Open, ["fact-1"]);
        session.ApplyAnalysisDelta(new([question], [fact], []));
        session.ApplyAnalysisDelta(new(
            [question with { Text = "When should we ship?", QuestionState = QuestionState.Answered }],
            [fact with { Summary = "Release timing options were discussed." }], []));
        var updated = session.Snapshot();
        Check.Equal(1, updated.Evidence.Count);
        Check.Equal("When should we ship?", updated.Questions.Single().Text);

        var before = session.Snapshot();
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [
            new("fact-2", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                1, second.Id, null, null, null, null, "Risk was discussed.", "Release risk")
        ], [
            new("duplicate", first.Id, second.Id, ExplainedTopicLinkKind.DependsOn,
                VisualizationProvenance.TranscriptFact, ["fact-1"], null),
            new("duplicate", second.Id, first.Id, ExplainedTopicLinkKind.Supports,
                VisualizationProvenance.TranscriptFact, ["fact-1"], null)
        ])));
        Check.Equal(before.Evidence.Count, session.Snapshot().Evidence.Count);
        Check.Equal(before.ExplainedTopicLinks.Count, session.Snapshot().ExplainedTopicLinks.Count);

        var inference = new VisualizationEvidence("inference-1", EvidenceOrigin.Analysis,
            VisualizationProvenance.Inference, null, first.Id,
            null, null, null, null, "Possible relationship.");
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [inference], [
            new("topic-link-1", first.Id, second.Id, ExplainedTopicLinkKind.Supports,
                VisualizationProvenance.TranscriptFact, ["inference-1"], null)
        ])));
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [
            new("missing-label", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                1, first.Id, null, null, null, null, "Grounded but unlabeled.")
        ], [])));
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [
            .. Enumerable.Range(2, 4).Select(index => new VisualizationEvidence(
                $"fact-{index}", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                1, first.Id, null, null, null, null, $"Fact {index}.", $"Fact {index}"))
        ], [])));
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisDelta(new([], [
            new("duplicate-label-1", EvidenceOrigin.Analysis,
                VisualizationProvenance.TranscriptFact, 1, first.Id,
                null, null, null, null, "The first grounded takeaway.", "Release timing"),
            new("duplicate-label-2", EvidenceOrigin.Analysis,
                VisualizationProvenance.TranscriptFact, 1, first.Id,
                null, null, null, null, "The second grounded takeaway.", "Release timing")
        ], [])));
        return Task.CompletedTask;
    }

    private static Task AnalysisParsing()
    {
        var session = CreateSession();
        var topic = session.AcceptTopic("Release", 1, 1);
        var occurrence = session.Snapshot().TopicTrail.Single();
        var response = $$"""
            {
              "updateMode":"Delta",
              "throughSegmentOrdinal":1,
              "throughSegmentOrdinal":1,
              "topicAssignments":[{"occurrenceId":"{{occurrence.Id}}","label":"Shipping","redirectToTopicId":"{{topic.Id}}","throughSegmentOrdinal":1}],
              "evidence":[{"id":"fact-1","provenance":"TranscriptFact","segmentOrdinal":1,"topicId":"{{topic.Id}}","summary":"Release options were discussed.","presentationLabel":"Release options"}],
              "questions":[{"id":"question-1","topicId":"{{topic.Id}}","text":"Ship now?","questionState":"Answered","evidenceIds":["fact-1"]}],
              "explainedTopicLinks":[],
              "actions":[],
              "decisions":[],
              "actions":[{"id":"action-1","topicId":"{{topic.Id}}","text":"Prepare release","actionState":"Open","evidenceIds":["fact-1"]}],
              "decisions":[{"id":"decision-1","topicId":"{{topic.Id}}","text":"Ship","evidenceIds":["fact-1"]}]
            }
            """;
        var result = IntelligenceResponseParser.ParseVisualizationAnalysis(
            response, "synthetic", "synthetic-model");
        var fenced = IntelligenceResponseParser.ParseVisualizationAnalysis(
            $"```json\n{response}\n```", "synthetic", "synthetic-model");
        Check.Equal(MeetingVisualizationUpdateMode.Delta, result.UpdateMode);
        Check.Equal(result.ThroughSegmentOrdinal, fenced.ThroughSegmentOrdinal);
        Check.Equal(topic.Id, result.TopicAssignments.Single().RedirectToTopicId);
        Check.Equal("Release options", result.Delta.Evidence.Single().PresentationLabel);
        Check.Equal(1, result.Delta.Actions.Count);
        Check.Equal(1, result.Delta.Decisions.Count);
        Check.Equal(1, result.ThroughSegmentOrdinal);
        var message = CopilotNotesService.BuildVisualizationMessage(session.Snapshot());
        Check.True(message.Contains("\"Ordinal\":1", StringComparison.Ordinal));
        Check.True(message.Contains(topic.Id, StringComparison.Ordinal));
        Check.True(message.Contains(occurrence.Id, StringComparison.Ordinal));
        Check.True(message.Contains("\"acceptedTopicOccurrences\"", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "Semantically consolidate paraphrases", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "at most four TranscriptFact evidence branches", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "Chronology, adjacency, shared vocabulary", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "primary nodes are canonical meeting topics", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "RELATED SOURCE SUGGESTIONS", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "two to four non-overlapping grounded takeaways", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "at-a-glance meeting support", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "wall of text", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "additive/upsert update", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "never a replacement graph", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "explicit redirectToTopicId", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "global canonical-label invariant", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "do not return two separate", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "same subject means an", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "Never make clinical", StringComparison.Ordinal));
        Check.True(CopilotNotesService.VisualizationRepairPrompt.Contains(
            "Do not use Markdown fences", StringComparison.Ordinal));
        Check.True(CopilotNotesService.CurrentTopicRepairPrompt.Contains(
            "exactly the one property named topic", StringComparison.Ordinal));
        session.ApplyRelatedContext(topic.Id, new(DateTimeOffset.UtcNow,
            [new("Private source title", "Private source summary.", ClaimKind.InferredConnection,
                [new("Private source title", "https://example.test/private",
                    ContextSourceType.Document, null)])],
            RelatedContextAvailability.Available));
        var safeMessage = CopilotNotesService.BuildVisualizationMessage(session.Snapshot());
        Check.True(safeMessage.Contains("\"compactState\"", StringComparison.Ordinal));
        Check.True(!safeMessage.Contains("Private source title", StringComparison.Ordinal));
        Check.True(!safeMessage.Contains("Private source summary", StringComparison.Ordinal));
        Check.True(!safeMessage.Contains("https://example.test/private", StringComparison.Ordinal));
        Check.Throws<IntelligenceException>(() =>
            IntelligenceResponseParser.ParseVisualizationAnalysis(
                response.Replace("\"TranscriptFact\"", "\"NotAProvenance\""),
                "synthetic", "synthetic-model"));
        Check.Throws<IntelligenceException>(() =>
            IntelligenceResponseParser.ParseVisualizationAnalysis(
                response.Replace("\"TranscriptFact\"", "\"RelatedSourceSuggestion\""),
                "synthetic", "synthetic-model"));
        Check.Throws<IntelligenceException>(() =>
            IntelligenceResponseParser.ParseVisualizationAnalysis(
                response.Replace("\"updateMode\":\"Delta\",", string.Empty,
                    StringComparison.Ordinal),
                "synthetic", "synthetic-model"));
        Check.Throws<IntelligenceException>(() =>
            IntelligenceResponseParser.ParseVisualizationAnalysis(
                response.Replace(
                    "Release options were discussed.",
                    new string('x', 481),
                    StringComparison.Ordinal),
                "synthetic", "synthetic-model"));
        Check.Throws<IntelligenceException>(() =>
            IntelligenceResponseParser.ParseVisualizationAnalysis(
                response.Replace(
                    "\"label\":\"Shipping\"",
                    $"\"label\":\"{new string('x', MeetingVisualizationSession.TopicLabelCharacterLimit + 1)}\"",
                    StringComparison.Ordinal),
                "synthetic", "synthetic-model"));
        var recommendation = $$"""
            {
              "updateMode":"Delta",
              "throughSegmentOrdinal":1,
              "throughSegmentOrdinal":1,
              "topicAssignments":[],
              "evidence":[{"id":"fact-recommendation","provenance":"TranscriptFact","segmentOrdinal":1,"topicId":"{{topic.Id}}","summary":"A recommendation was discussed.","presentationLabel":"Suggested approach"}],
              "questions":[{"id":"question-recommendation","topicId":"{{topic.Id}}","text":"Should this approach be considered?","questionState":"Open","evidenceIds":["fact-recommendation"]}],
              "explainedTopicLinks":[],
              "actions":[],
              "decisions":[],
              "actions":[],
              "decisions":[]
            }
            """;
        Check.Equal(1, IntelligenceResponseParser.ParseVisualizationAnalysis(
            recommendation, "synthetic", "synthetic-model").Delta.Questions.Count);

        var unknownOccurrence = IntelligenceResponseParser.ParseVisualizationAnalysis(
            response.Replace(occurrence.Id, "occ-unknown"),
            "synthetic", "synthetic-model");
        Check.Throws<InvalidOperationException>(() =>
            session.ApplyAnalysisResult(session.Snapshot(), unknownOccurrence));
        var unknownSegment = IntelligenceResponseParser.ParseVisualizationAnalysis(
            response.Replace("\"segmentOrdinal\":1", "\"segmentOrdinal\":99"),
            "synthetic", "synthetic-model");
        Check.Throws<InvalidOperationException>(() =>
            session.ApplyAnalysisResult(session.Snapshot(), unknownSegment));
        var unknownTopic = IntelligenceResponseParser.ParseVisualizationAnalysis(
            response.Replace(topic.Id, "topic-unknown"),
            "synthetic", "synthetic-model");
        Check.Throws<InvalidOperationException>(() =>
            session.ApplyAnalysisResult(session.Snapshot(), unknownTopic));
        return Task.CompletedTask;
    }

    private static Task SequentialDeltas()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-deltas", "Sequential deltas", DateTimeOffset.UtcNow);
        session.AppendOrUpdateSegment(1, new("Release timing was reviewed.", DateTimeOffset.UtcNow));
        var release = session.AcceptTopic("Release timing", 1, 1);
        var firstPrefix = session.Snapshot();
        var firstFact = new VisualizationEvidence(
            "fact-release", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
            1, release.Id, null, null, null, null,
            "The group reviewed release timing.", "Release timing");
        session.ApplyAnalysisResult(firstPrefix, new(
            [new(firstPrefix.TopicTrail.Single().Id, "Release timing", release.Id, 1)],
            new([], [firstFact], [])
            {
                Actions =
                [
                    new("action-release", release.Id, "Confirm the release date.", ["fact-release"])
                ]
            }));
        session.ApplyRelatedContext(release.Id, new(
            DateTimeOffset.UtcNow,
            [new("Prior launch plan", "Possibly relevant prior planning material.",
                ClaimKind.InferredConnection,
                [new("Launch plan", "https://example.test/launch",
                    ContextSourceType.Document, null)])],
            RelatedContextAvailability.Available));
        var afterFirst = session.Snapshot();
        var sourceId = afterFirst.Evidence.Single(
            item => item.Origin == EvidenceOrigin.RelatedContext).Id;

        session.AppendOrUpdateSegment(2, new("The discussion moved to rollout risk.", DateTimeOffset.UtcNow));
        var risk = session.AcceptTopic("Rollout risk", 2, 2);
        var secondPrefix = session.Snapshot();
        var assignments = secondPrefix.TopicTrail.Select(item =>
            new VisualizationTopicAssignment(
                item.Id,
                item.TopicId == release.Id ? "Release timing" : "Rollout risk",
                item.TopicId,
                item.ThroughSegmentOrdinal)).ToArray();
        session.ApplyAnalysisResult(secondPrefix, new(
            assignments,
            new([], [
                new("fact-risk", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                    2, risk.Id, null, null, null, null,
                    "The group discussed rollout risk.", "Rollout risk")
            ], [])));

        var final = session.Snapshot();
        Check.Equal(2, final.TopicClusters.Count);
        Check.True(final.TopicClusters.Select(item => item.Id)
            .SequenceEqual([release.Id, risk.Id]));
        Check.Equal(2, final.TopicTrail.Count);
        Check.True(final.Evidence.Any(item => item == firstFact));
        Check.True(final.Evidence.Any(item => item.Id == "fact-risk" && item.TopicId == risk.Id));
        Check.True(final.Evidence.Any(item => item.Id == sourceId &&
            item.Provenance == VisualizationProvenance.RelatedSourceSuggestion));
        Check.Equal("action-release", final.Actions.Single().Id);
        return Task.CompletedTask;
    }

    private static Task IncrementalContext()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-incremental", "Long meeting", DateTimeOffset.UtcNow);
        for (var ordinal = 1; ordinal <= 80; ordinal++)
            session.AppendOrUpdateSegment(ordinal, new(
                $"Finalized segment {ordinal}", DateTimeOffset.UtcNow.AddSeconds(ordinal)));
        var topic = session.AcceptTopic("Release planning", 80, 80);
        var analyzed = session.Snapshot();
        var occurrence = analyzed.TopicTrail.Single();
        var sourceWindow = new[] { 1, 80 };
        var fact = new VisualizationEvidence(
            "fact-early", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
            1, topic.Id, null, null, null, null, "The initial release constraint.",
            "Initial constraint");
        session.ApplyAnalysisResult(analyzed, new(
            [new(occurrence.Id, "Release planning", topic.Id, 80)],
            new(
                [new("question-open", topic.Id, "When can release start?",
                    QuestionState.Open, ["fact-early"])],
                [fact],
                [] )
            {
                Actions =
                [
                    new("action-open", topic.Id, "Prepare the release plan.", ["fact-early"])
                ],
                Decisions =
                [
                    new("decision-explicit", topic.Id, "Use staged rollout.", ["fact-early"])
                ]
            })
        {
            ThroughSegmentOrdinal = 80,
            SuppliedSegmentOrdinals = sourceWindow
        });

        var advanced = session.Snapshot();
        Check.Equal(80, advanced.LastAnalyzedOrdinal);
        Check.Equal(1, advanced.Actions.Count);
        Check.Equal(1, advanced.Decisions.Count);
        var message = CopilotNotesService.BuildVisualizationMessage(advanced);
        using var payload = JsonDocument.Parse(message[(message.IndexOf('\n') + 1)..]);
        var root = payload.RootElement;
        var recent = root.GetProperty("recentSegments");
        Check.Equal(CopilotNotesService.VisualizationRecentSegmentLimit, recent.GetArrayLength());
        Check.Equal(21, recent[0].GetProperty("Ordinal").GetInt32());
        Check.Equal(80, recent[recent.GetArrayLength() - 1].GetProperty("Ordinal").GetInt32());
        var compact = root.GetProperty("compactState");
        Check.Equal(80, compact.GetProperty("lastAnalyzedOrdinal").GetInt32());
        // Acceptance 9: every canonical topic stays referenceable within the prompt budget.
        var catalog = root.GetProperty("canonicalTopicCatalog");
        Check.Equal(1, catalog.GetArrayLength());
        Check.Equal(topic.Id, catalog[0].GetProperty("Id").GetString());
        Check.Equal("Release planning", catalog[0].GetProperty("label").GetString());
        var manyTopics = advanced with
        {
            TopicClusters =
            [
                .. Enumerable.Range(1, 120).Select(index => new TopicCluster(
                    $"older-topic-{index:000}",
                    $"Older meeting topic {index}",
                    [$"Older meeting topic {index}"],
                    index,
                    index)),
                .. advanced.TopicClusters
            ],
            Segments =
            [
                .. advanced.Segments.Select(segment => segment with
                {
                    Text = string.Join(' ', Enumerable.Repeat(segment.Text, 120))
                })
            ],
        };
        var budgetMessage = CopilotNotesService.BuildVisualizationMessage(manyTopics);
        Check.True(budgetMessage.Length <= CopilotNotesService.VisualizationPromptCharacterLimit);
        using var budgetPayload = JsonDocument.Parse(
            budgetMessage[(budgetMessage.IndexOf('\n') + 1)..]);
        var budgetCatalog = budgetPayload.RootElement.GetProperty("canonicalTopicCatalog");
        Check.Equal(121, budgetCatalog.GetArrayLength());
        Check.True(budgetCatalog.EnumerateArray().Any(item =>
            item.GetProperty("Id").GetString() == "older-topic-001"));
        Check.True(budgetCatalog.EnumerateArray().Any(item =>
            item.GetProperty("Id").GetString() == topic.Id));
        Check.Equal(1, compact.GetProperty("unresolvedQuestions").GetArrayLength());
        Check.Equal(1, compact.GetProperty("unresolvedActions").GetArrayLength());
        Check.Equal(1, compact.GetProperty("explicitDecisions").GetArrayLength());

        session.AppendOrUpdateSegment(1, new(
            "Corrected initial release constraint.", DateTimeOffset.UtcNow.AddSeconds(1)));
        var invalidated = session.Snapshot();
        Check.Equal(1, invalidated.ReanalysisFromOrdinal);
        Check.Equal(0, invalidated.Evidence.Count(item => item.Id == "fact-early"));
        Check.Equal(0, invalidated.Questions.Count);
        Check.Equal(0, invalidated.Actions.Count);
        Check.Equal(0, invalidated.Decisions.Count);
        var rebuildMessage = CopilotNotesService.BuildVisualizationMessage(invalidated);
        using var rebuildPayload = JsonDocument.Parse(
            rebuildMessage[(rebuildMessage.IndexOf('\n') + 1)..]);
        var rebuildSegments = rebuildPayload.RootElement.GetProperty("recentSegments")
            .EnumerateArray().Select(item => item.GetProperty("Ordinal").GetInt32()).ToArray();
        Check.True(rebuildSegments.Length <= CopilotNotesService.VisualizationRecentSegmentLimit);
        Check.True(rebuildSegments.Contains(1));
        Check.True(rebuildSegments.Contains(80));

        var invalidReference = new MeetingVisualizationAnalysisResult(
            [new(occurrence.Id, "Release planning", topic.Id, 80)],
            new([], [fact], []))
        {
            ThroughSegmentOrdinal = 80,
            SuppliedSegmentOrdinals = [80]
        };
        Check.Throws<InvalidOperationException>(() =>
            session.ApplyAnalysisResult(invalidated, invalidReference));

        var correctedFact = fact with
        {
            Summary = "The corrected initial release constraint.",
            PresentationLabel = "Corrected constraint"
        };
        session.ApplyAnalysisResult(invalidated, new(
            [new(occurrence.Id, "Release planning", topic.Id, 80)],
            new([], [correctedFact], []))
        {
            ThroughSegmentOrdinal = 80,
            SuppliedSegmentOrdinals = rebuildSegments
        });
        var rebuilt = session.Snapshot();
        Check.True(rebuilt.ReanalysisFromOrdinal is null);
        Check.Equal(80, rebuilt.LastAnalyzedOrdinal);
        Check.Equal("Corrected constraint", rebuilt.Evidence.Single().PresentationLabel);
        return Task.CompletedTask;
    }

    private static async Task StorageOutcomes()
    {
        using var directory = new TemporaryDirectory();
        var markdown = Path.Combine(directory.Path, "2026-09-23_19-00_demo_hash.md");
        await File.WriteAllTextAsync(markdown, "# Demo");
        var session = CreateSession();
        var topic = session.AcceptTopic("Release", 1, 1);
        session.ApplyRelatedContext(topic.Id, new(DateTimeOffset.UtcNow,
            [new("Prior plan", "A related planning record.", ClaimKind.HistoricalFact,
                [new("Planning record", "https://example.test/plan", ContextSourceType.Document, null)])],
            RelatedContextAvailability.Available));
        session.Complete(DateTimeOffset.UtcNow, true);
        var storage = new JsonMeetingVisualizationStorage();

        var missing = await storage.LoadAsync(markdown, CancellationToken.None);
        Check.Equal(MeetingVisualizationLoadKind.LegacyMissing, missing.Kind);
        await storage.SaveAsync(markdown, session.Snapshot(), CancellationToken.None);
        var valid = await storage.LoadAsync(markdown, CancellationToken.None);
        Check.Equal(MeetingVisualizationLoadKind.Valid, valid.Kind);
        Check.Equal("session-1", valid.Document!.SessionId);
        Check.Equal(topic.Id, valid.Document.Evidence.Single().TopicId);

        var json = Path.ChangeExtension(markdown, ".json");
        var original = await File.ReadAllTextAsync(json);
        await File.WriteAllTextAsync(markdown, "# Changed");
        Check.Equal(MeetingVisualizationLoadKind.StaleMismatch,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        await File.WriteAllTextAsync(json, """{"schemaVersion":99}""");
        Check.Equal(MeetingVisualizationLoadKind.FutureSchema,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);
        await File.WriteAllTextAsync(json, "{");
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        await File.WriteAllTextAsync(markdown, "# Demo");
        var malformed = JsonNode.Parse(original)!.AsObject();
        malformed["document"] = null;
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        malformed = JsonNode.Parse(original)!.AsObject();
        malformed.Remove("markdownSha256");
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        malformed = JsonNode.Parse(original)!.AsObject();
        malformed["document"]!["evidence"]![0]!["provenance"] = 999;
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        malformed = JsonNode.Parse(original)!.AsObject();
        malformed["document"]!["evidence"]![0]!["origin"] = 999;
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        malformed = JsonNode.Parse(original)!.AsObject();
        malformed["document"]!["segments"] = null;
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        malformed = JsonNode.Parse(original)!.AsObject();
        malformed["document"]!["evidence"]![0]!["provenance"] = 2;
        malformed["document"]!["evidence"]![0]!["sourceUrl"] = "http://example.test/unsafe";
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);

        malformed = JsonNode.Parse(original)!.AsObject();
        malformed["document"]!["segments"]![0]!["speakerId"] = "missing-speaker";
        await File.WriteAllTextAsync(json, malformed.ToJsonString());
        Check.Equal(MeetingVisualizationLoadKind.Corrupt,
            (await storage.LoadAsync(markdown, CancellationToken.None)).Kind);
    }

    private static async Task InferredRelationshipPersistence()
    {
        using var directory = new TemporaryDirectory();
        var markdown = Path.Combine(directory.Path, "inferred-link.md");
        await File.WriteAllTextAsync(markdown, "# Synthetic");
        var session = CreateSession();
        var first = session.AcceptTopic("Release", 1, 1);
        var second = session.AcceptTopic("Risks", 1, 1);
        session.ApplyAnalysisDelta(new([], [
            new("fact-release", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                1, first.Id, null, null, null, null, "A release was discussed.", "Release options"),
            new("fact-risk", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                1, second.Id, null, null, null, null, "A risk was discussed.", "Release risks")
        ], [
            new("inferred-link", first.Id, second.Id, ExplainedTopicLinkKind.DependsOn,
                VisualizationProvenance.Inference, ["fact-release", "fact-risk"],
                "The risk may affect the release")
        ]));
        session.Complete(DateTimeOffset.UtcNow, true);
        var storage = new JsonMeetingVisualizationStorage();

        Check.True(MeetingVisualizationValidator.IsValid(session.Snapshot()));
        await storage.SaveAsync(markdown, session.Snapshot(), CancellationToken.None);
        var loaded = await storage.LoadAsync(markdown, CancellationToken.None);

        Check.Equal(MeetingVisualizationLoadKind.Valid, loaded.Kind);
        Check.Equal(VisualizationProvenance.Inference,
            loaded.Document!.ExplainedTopicLinks.Single().Provenance);
    }

    private static Task StorageBounds()
    {
        Check.True(JsonMeetingVisualizationStorage.IsWithinJsonLimit(
            JsonMeetingVisualizationStorage.MaxJsonBytes));
        Check.True(!JsonMeetingVisualizationStorage.IsWithinJsonLimit(
            JsonMeetingVisualizationStorage.MaxJsonBytes + 1));

        var session = CreateSession();
        var document = session.Snapshot();
        var speakers = Enumerable.Range(0, 10_001)
            .Select(index => new VisualizationSpeaker($"speaker-{index}", $"Speaker {index}", false))
            .ToArray();
        Check.True(!MeetingVisualizationValidator.IsValid(document with { Speakers = speakers }));

        var topic = session.AcceptTopic("Release", 1, 1);
        document = session.Snapshot();
        var trail = Enumerable.Range(0, 50_001)
            .Select(index => new TopicOccurrence(
                $"occ-{index:D6}", topic.Id, 1, 1, "Release"))
            .ToArray();
        Check.True(!MeetingVisualizationValidator.IsValid(document with { TopicTrail = trail }));
        return Task.CompletedTask;
    }

    private static async Task PipelineAnalysis()
    {
        var delay = new AsyncGate();
        await using var fixture = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        fixture.Intelligence.VisualizationResult = new(
            [
                new("occ-000001", "Shipping schedule", "topic-0001", 1),
                new("occ-000002", "Testing dependency", null, 2)
            ],
            new(
                [new("question-ship", "topic-0001", "Ship Friday or Monday?",
                    QuestionState.Answered, ["fact-question"])],
                [
                    new("fact-question", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                        1, "topic-0001", null, null, null, null,
                        "Friday and Monday were proposed.", "Schedule options"),
                    new("fact-decision", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                        2, "topic-0001", null, null, null, null,
                        "The team decided to ship Monday.", "Monday decision"),
                    new("fact-testing", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                        2, "topic-0002", null, null, null, null,
                        "Testing finishes Friday.", "Testing completion")
                ],
                [new("relationship-testing", "topic-0001", "topic-0002",
                    ExplainedTopicLinkKind.DependsOn, VisualizationProvenance.TranscriptFact,
                    ["fact-decision", "fact-testing"], null)]));
        await fixture.Pipeline.StartManualAsync("Release planning", CancellationToken.None);
        fixture.Transcription.Emit("Should we ship Friday or Monday?");
        fixture.Visualization.AcceptTopic("Release plan", 1, 1);
        fixture.Transcription.Emit("We decided to ship Monday because testing finishes Friday.");
        fixture.Visualization.AcceptTopic("Testing dependency", 2, 2);
        var sessionId = fixture.Pipeline.CurrentSessionId!;
        var stop = fixture.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(fixture.Pipeline.SaveNow(sessionId));
        await stop;

        var loaded = await fixture.VisualizationStorage.LoadAsync(
            fixture.Storage.Paths.Last(), CancellationToken.None);
        Check.Equal(MeetingVisualizationLoadKind.Valid, loaded.Kind);
        Check.Equal(1, fixture.Intelligence.VisualizationCalls);
        Check.Equal(2, loaded.Document!.TopicClusters.Count);
        Check.True(loaded.Document.TopicClusters.Single(item => item.Id == "topic-0001")
            .Aliases.Contains("Shipping schedule"));
        Check.Equal(1, loaded.Document.ExplainedTopicLinks.Count);
    }

    private static async Task LiveAnalysis()
    {
        await using var fixture = new PipelineFixture();
        await fixture.Pipeline.StartManualAsync("Live visualization", CancellationToken.None);
        fixture.Transcription.Emit("Discuss the release schedule");
        var topic = fixture.Visualization.AcceptTopic("Release plan", 1, 1);
        var gate = new AsyncGate();
        fixture.Intelligence.OnVisualization = gate.WaitAsync;
        fixture.Intelligence.BuildVisualizationResult = document => new(
            document.TopicTrail.TakeLast(20).Select(item => new VisualizationTopicAssignment(
                item.Id,
                item.TopicId == topic.Id ? "Shipping schedule" : "Testing dependency",
                item.TopicId,
                item.ThroughSegmentOrdinal)).ToArray(),
            new(
                [new("question-live", topic.Id, "When should we ship?",
                    QuestionState.Open, ["fact-live"])],
                [new("fact-live", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                    1, topic.Id, null, null, null, null,
                    "The release schedule was discussed.", "Release schedule")],
                []));

        var first = fixture.Pipeline.RefreshVisualizationAnalysisAsync(CancellationToken.None);
        await gate.Entered;
        var second = fixture.Pipeline.RefreshVisualizationAnalysisAsync(CancellationToken.None);
        Check.Equal(1, fixture.Intelligence.VisualizationCalls);
        fixture.Transcription.Emit("Testing must finish first");
        var laterTopic = fixture.Visualization.AcceptTopic("Testing", 2, 2);
        fixture.Transcription.Emit("Testing is still in progress");
        var trailing = fixture.Pipeline.RefreshVisualizationAnalysisAsync(CancellationToken.None);
        gate.Release();
        await Task.WhenAll(first, second, trailing);
        var snapshot = fixture.Visualization.Snapshot();
        Check.Equal(2, fixture.Intelligence.VisualizationCalls);
        Check.Equal(3L, fixture.Intelligence.VisualizationInputs[1].TranscriptRevision);
        Check.Equal(2, fixture.Intelligence.VisualizationInputs[1].TopicTrail.Count);
        Check.True(snapshot.TopicClusters.Single(item => item.Id == topic.Id)
            .Aliases.Contains("Shipping schedule"));
        Check.Equal(laterTopic.Id, snapshot.TopicTrail.Last().TopicId);
        Check.Equal(3L, snapshot.TranscriptRevision);
        Check.Equal(1, snapshot.Questions.Count);

        await fixture.Pipeline.PrepareForExitAsync();
    }

    private static Task AnalysisPrefixRejection()
    {
        var session = CreateSession();
        var topic = session.AcceptTopic("Release", 1, 1);
        var captured = session.Snapshot();
        var result = new MeetingVisualizationAnalysisResult(
            [new(captured.TopicTrail.Single().Id, "Shipping", topic.Id, 1)],
            new([], [], []));

        session.AppendOrUpdateSegment(1, new("Corrected release discussion",
            captured.Segments[0].Timestamp, SpeakerId: "Guest-1"));
        Check.Throws<InvalidOperationException>(() => session.ApplyAnalysisResult(captured, result));

        var replacement = new MeetingVisualizationSession();
        replacement.Begin("replacement-session", "Replacement", DateTimeOffset.UtcNow);
        replacement.AppendOrUpdateSegment(1, new("Different meeting", DateTimeOffset.UtcNow));
        Check.Throws<InvalidOperationException>(() => replacement.ApplyAnalysisResult(captured, result));
        return Task.CompletedTask;
    }

    private static async Task PipelinePersistence()
    {
        var delay = new AsyncGate();
        await using var fixture = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await fixture.Pipeline.StartManualAsync("Visualization save", CancellationToken.None);
        fixture.Transcription.Emit("Discuss release options");
        var sessionId = fixture.Pipeline.CurrentSessionId!;
        var stop = fixture.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(fixture.Pipeline.SaveNow(sessionId));
        await stop;

        var markdown = fixture.Storage.Paths.Last();
        var sidecar = Path.ChangeExtension(markdown, ".json");
        Check.True(File.Exists(sidecar));
        var loaded = await fixture.VisualizationStorage.LoadAsync(markdown, CancellationToken.None);
        Check.Equal(MeetingVisualizationLoadKind.Valid, loaded.Kind);
        Check.Equal(1, loaded.Document!.Segments.Count);
        Check.True(loaded.Document.IsComplete);
        Check.Equal(2, fixture.VisualizationStorage.Saves);
        Check.True(fixture.VisualizationStorage.Writes[0].Document.IsComplete);
        Check.True(fixture.VisualizationStorage.Writes[1].Document.IsComplete);
    }

    private static async Task PipelineFailureRetry()
    {
        var visualizationStorage = new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage())
        {
            BeforeSave = (save, _) => save == 2
                ? Task.FromException(new IOException("Synthetic final visualization failure."))
                : Task.CompletedTask
        };
        var delay = new AsyncGate();
        await using var fixture = new PipelineFixture(
            saveDelay: (_, ct) => delay.WaitAsync(ct), visualizationStorage: visualizationStorage);
        await fixture.Pipeline.StartManualAsync("Visualization retry", CancellationToken.None);
        fixture.Transcription.Emit("Keep the transcript if its sidecar fails");
        var sessionId = fixture.Pipeline.CurrentSessionId!;
        var stop = fixture.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(fixture.Pipeline.SaveNow(sessionId));
        await stop;

        var markdown = fixture.Storage.Paths.Last();
        Check.True(File.Exists(markdown));
        Check.True(File.Exists(Path.ChangeExtension(markdown, ".json")));
        Check.Equal(PipelineState.Saved, fixture.Pipeline.State);
        Check.Equal(markdown, fixture.Pipeline.Status.NotesPath);
        Check.True(fixture.Pipeline.Status.Message.Contains("visualization data is pending retry",
            StringComparison.OrdinalIgnoreCase));
        var pendingDiagnostic = fixture.Pipeline.AiDiagnostics.GetSnapshot().Events
            .Single(item => item.Stage == AiDiagnosticStage.CompanionJsonWrite);
        Check.Equal(nameof(IOException), pendingDiagnostic.ExceptionType);
        Check.Equal(AiDiagnosticRetryOutcome.PendingExitRetry, pendingDiagnostic.RetryOutcome);
        Check.True(!(pendingDiagnostic.ExceptionType?.Contains("Synthetic", StringComparison.Ordinal) ?? false));
        Check.Equal(MeetingVisualizationLoadKind.StaleMismatch,
            (await visualizationStorage.LoadAsync(markdown, CancellationToken.None)).Kind);

        Check.Equal(markdown,
            await fixture.Pipeline.RetryIntelligenceAsync(sessionId, CancellationToken.None));
        Check.True(File.Exists(Path.ChangeExtension(markdown, ".json")));
        Check.Equal(MeetingVisualizationLoadKind.Valid,
            (await visualizationStorage.LoadAsync(markdown, CancellationToken.None)).Kind);
        Check.True(fixture.Pipeline.AiDiagnostics.GetSnapshot().Events.Any(item =>
            item.Stage == AiDiagnosticStage.CompanionJsonRetry &&
            item.RetryOutcome == AiDiagnosticRetryOutcome.RetrySucceeded));
    }

    private static async Task PipelineExitRetry()
    {
        var visualizationStorage = new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage())
        {
            BeforeSave = (save, _) => save >= 2
                ? Task.FromException(new IOException("Synthetic final visualization failure."))
                : Task.CompletedTask
        };
        var delay = new AsyncGate();
        await using var fixture = new PipelineFixture(
            saveDelay: (_, ct) => delay.WaitAsync(ct), visualizationStorage: visualizationStorage);
        await fixture.Pipeline.StartManualAsync("Visualization exit retry", CancellationToken.None);
        fixture.Transcription.Emit("Retry pending visualization on exit");
        var sessionId = fixture.Pipeline.CurrentSessionId!;
        var stop = fixture.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(fixture.Pipeline.SaveNow(sessionId));
        await stop;
        var markdown = fixture.Storage.Paths.Last();
        Check.Equal(PipelineState.Saved, fixture.Pipeline.State);
        Check.True(File.Exists(Path.ChangeExtension(markdown, ".json")));
        Check.Throws<InvalidOperationException>(() =>
            fixture.Pipeline.StartManualAsync("Must retry first", CancellationToken.None));

        var readiness = await fixture.Pipeline.PrepareForExitAsync();
        Check.Equal(ExitDeferralKind.VisualizationRetryKept, readiness.Deferral);
        Check.Equal(MeetingVisualizationLoadKind.StaleMismatch,
            (await visualizationStorage.LoadAsync(markdown, CancellationToken.None)).Kind);
        Check.Equal(3, visualizationStorage.Saves);
        Check.Equal(1, (await fixture.Journal.DrainAsync(
            new JsonMeetingVisualizationStorage(), CancellationToken.None)).Count);
        Check.Equal(MeetingVisualizationLoadKind.Valid,
            (await visualizationStorage.LoadAsync(markdown, CancellationToken.None)).Kind);
    }

    private static async Task PipelineDiscard()
    {
        var delay = new AsyncGate();
        await using var fixture = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await fixture.Pipeline.StartManualAsync("Visualization discard", CancellationToken.None);
        fixture.Transcription.Emit("Discard this synthetic recording");
        var sessionId = fixture.Pipeline.CurrentSessionId!;
        var stop = fixture.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(fixture.Pipeline.DiscardPendingRecording(sessionId));
        await stop;
        Check.Equal(0, Directory.EnumerateFiles(fixture.Directory.Path).Count());
        Check.True(!fixture.Visualization.HasActiveSession);
    }

    private static async Task PipelineCancellationRetry()
    {
        var delay = new AsyncGate();
        await using var fixture = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await fixture.Pipeline.StartManualAsync("Visualization cancellation", CancellationToken.None);
        fixture.Transcription.Emit("Preserve pending visualization after cancellation");
        var sessionId = fixture.Pipeline.CurrentSessionId!;
        var stop = fixture.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(fixture.Pipeline.SaveNow(sessionId));
        await stop;

        var sidecarGate = new AsyncGate();
        fixture.VisualizationStorage.BeforeSave = (call, ct) =>
            call == 3 ? sidecarGate.WaitAsync(ct) : Task.CompletedTask;
        using var cancellation = new CancellationTokenSource();
        var retry = fixture.Pipeline.RetryIntelligenceAsync(sessionId, cancellation.Token);
        await sidecarGate.Entered;
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => retry);
        Check.Throws<InvalidOperationException>(() =>
            fixture.Pipeline.StartManualAsync("Blocked until recovery", CancellationToken.None));

        fixture.VisualizationStorage.BeforeSave = null;
        await fixture.Pipeline.PrepareForExitAsync();
        Check.Equal(4, fixture.VisualizationStorage.Saves);
    }

    private static MeetingVisualizationSession CreateSession()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-1", "Demo", DateTimeOffset.Parse("2026-09-23T19:00:00-07:00"));
        session.AppendOrUpdateSegment(1, new("Discuss release options",
            DateTimeOffset.Parse("2026-09-23T19:00:05-07:00"), SpeakerId: "Guest-1"));
        return session;
    }

}
