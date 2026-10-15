using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using MeetingCapture.TrayApp;

namespace MeetingCapture.Regression;

internal static class SpeakerChecks
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-23T19:00:00Z");

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("speaker merge plan folds absorbed speakers and ignores self, blank and duplicate input", () => Run(PlanRules));
        yield return ("speaker merge plan remaps segments and moves word counts to the kept speaker", () => Run(PlanRemap));
        yield return ("speaker resolver treats combined speakers as the kept speaker without re-prompting", () => Run(ResolverAliases));
        yield return ("visualization session merges speakers, stays valid and survives a storage round trip", () => RoundTrip());
        yield return ("pipeline combines speakers while recording and after saving, persisting the merged meeting", () => PipelineMerge());
        yield return ("speaker timeline coalesces contiguous lines and keeps gaps between talking spans", () => Run(TimelineCoalescing));
        yield return ("speaker timeline places spans as fractions of a shared clamped meeting axis", () => Run(TimelineAxis));
        yield return ("speaker timeline handles empty, unknown and merged speakers", () => Run(TimelineEdges));
        yield return ("speaker panel shows a combine action and timeline bars instead of word-share percentages", () => Run(PanelMarkup));
    }

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static TranscriptionSegment Line(string text, double seconds, string? speaker, string? name = null) =>
        new(text, Start.AddSeconds(seconds), SpeakerId: speaker, SpeakerDisplayName: name);

    private static void PlanRules()
    {
        Check.True(SpeakerMergePlan.Create(["Guest-1"], "Guest-1") is null);
        Check.True(SpeakerMergePlan.Create(["guest-1", " GUEST-1 "], "Guest-1") is null);
        Check.True(SpeakerMergePlan.Create(["Guest-1", "Guest-2"], " ") is null);
        Check.True(SpeakerMergePlan.Create([null, "", "  "], "Guest-1") is null);

        var plan = SpeakerMergePlan.Create(["Guest-1", "guest-2", "Guest-2", "Guest-3", "Guest-1", null], "Guest-1")!;
        Check.Equal("Guest-1", plan.KeptId);
        Check.Equal("guest-2,Guest-3", string.Join(',', plan.AbsorbedIds));
        Check.True(plan.Absorbs("GUEST-3"));
        Check.True(!plan.Absorbs("Guest-1"));
        Check.True(!plan.Absorbs(null));
    }

    private static void PlanRemap()
    {
        var plan = SpeakerMergePlan.Create(["Guest-2", "Guest-3", "Guest-1"], "Guest-1")!;
        Check.Equal("Guest-1", plan.Remap("Guest-2"));
        Check.Equal("Guest-1", plan.Remap("guest-3"));
        Check.Equal("Guest-9", plan.Remap("Guest-9"));
        Check.Equal(null, plan.Remap((string?)null));

        var moved = plan.Remap(Line("hello", 1, "Guest-3", "Guest-3"));
        Check.Equal("Guest-1", moved.SpeakerId);
        Check.Equal("hello", moved.Text);
        var untouched = Line("other", 2, "Guest-9");
        Check.True(ReferenceEquals(untouched, plan.Remap(untouched)));

        var counts = new Dictionary<string, int> { ["Guest-1"] = 4, ["Guest-2"] = 3, ["Guest-3"] = 2, ["Guest-9"] = 7 };
        plan.MergeCounts(counts);
        Check.Equal(9, counts["Guest-1"]);
        Check.Equal(7, counts["Guest-9"]);
        Check.Equal(2, counts.Count);

        var onlyAbsorbed = new Dictionary<string, int> { ["Guest-2"] = 5 };
        plan.MergeCounts(onlyAbsorbed);
        Check.Equal(5, onlyAbsorbed["Guest-1"]);
        Check.True(!onlyAbsorbed.ContainsKey("Guest-2"));
    }

    private static void ResolverAliases()
    {
        var resolver = new SpeakerResolver();
        var prompts = new List<string>();
        resolver.UnknownSpeakerDetected += (_, id) => prompts.Add(id);
        resolver.Resolve("Guest-1", "Ahmed");
        resolver.NotifySpeaker("Guest-2");
        Check.Equal("Guest-2", string.Join(',', prompts));

        resolver.Merge(SpeakerMergePlan.Create(["Guest-1", "Guest-2"], "Guest-1")!);
        Check.Equal("Guest-1", resolver.Canonical("guest-2"));
        Check.Equal("Ahmed", resolver.GetDisplayName("Guest-2"));
        Check.True(resolver.IsResolved("Guest-2"));
        Check.Equal("Guest-2", string.Join(',', resolver.AliasesOf("Guest-1")));

        resolver.NotifySpeaker("Guest-2");
        Check.Equal(1, prompts.Count);
        resolver.Resolve("Guest-2", "Ahmed S.");
        Check.Equal("Ahmed S.", resolver.GetDisplayName("Guest-1"));

        resolver.Merge(SpeakerMergePlan.Create(["Guest-1", "Guest-3"], "Guest-3")!);
        Check.Equal("Guest-3", resolver.Canonical("Guest-2"));
        Check.Equal("Guest-3", resolver.Canonical("Guest-1"));

        resolver.Reset();
        Check.Equal("Guest-2", resolver.Canonical("Guest-2"));
    }

    private static MeetingVisualizationSession CreateSession()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("session-merge", "Demo", Start);
        session.AppendOrUpdateSegment(1, Line("First point", 5, "Guest-1", "Guest-1"));
        session.AppendOrUpdateSegment(2, Line("Second point", 10, "Guest-2", "Guest-2"));
        session.AppendOrUpdateSegment(3, Line("Third point", 15, "Guest-3", "Guest-3"));
        session.ApplySpeakerAssignment("Guest-1", "Ahmed");
        return session;
    }

    private static async Task RoundTrip()
    {
        var session = CreateSession();
        var plan = SpeakerMergePlan.Create(["Guest-1", "Guest-2", "Guest-3"], "Guest-1")!;
        session.MergeSpeakers(plan, "Ahmed");
        session.Complete(Start.AddMinutes(1), true);
        session.MergeSpeakers(SpeakerMergePlan.Create(["Guest-1", "Guest-4"], "Guest-1")!, "Ahmed");

        var merged = session.Snapshot();
        Check.True(MeetingVisualizationValidator.IsValid(merged));
        Check.Equal("Guest-1", string.Join(',', merged.Speakers.Select(speaker => speaker.SpeakerId)));
        Check.Equal("Ahmed", merged.Speakers.Single().DisplayName);
        Check.True(merged.Segments.All(segment => segment.SpeakerId == "Guest-1"));

        using var directory = new TemporaryDirectory();
        var markdown = Path.Combine(directory.Path, "merged.md");
        await File.WriteAllTextAsync(markdown, "# Merged");
        var storage = new JsonMeetingVisualizationStorage();
        await storage.SaveAsync(markdown, merged, CancellationToken.None);
        var loaded = await storage.LoadAsync(markdown, CancellationToken.None);
        Check.Equal(MeetingVisualizationLoadKind.Valid, loaded.Kind);
        Check.Equal("Ahmed", loaded.Document!.Speakers.Single().DisplayName);
        Check.Equal(3, loaded.Document.Segments.Count(segment => segment.SpeakerId == "Guest-1"));
    }

    private static async Task PipelineMerge()
    {
        await using var f = new PipelineFixture();
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
                f.Pipeline.SaveNow(sessionId);
        };
        await f.Pipeline.StartManualAsync("Synthetic speaker merge", CancellationToken.None);
        void Say(string text, string speaker) =>
            f.Transcription.CaptureFinalHandlers()!(f.Transcription,
                new(text, DateTimeOffset.UtcNow, SpeakerId: speaker));
        Say("Opening remarks", "Guest-1");
        Say("A question", "Guest-2");
        Say("Another voice", "Guest-3");
        Say("Closing remarks", "Guest-4");
        f.Pipeline.SpeakerResolver.Resolve("Guest-1", "Ahmed");

        Check.True(!await f.Pipeline.MergeSpeakersAsync(["Guest-1"], "Guest-1"));
        Check.True(await f.Pipeline.MergeSpeakersAsync(["Guest-1", "Guest-2"], "Guest-1"));
        Check.Equal("Guest-1,Guest-1,Guest-3,Guest-4",
            string.Join(',', f.Pipeline.CurrentTranscript!.Segments.Select(segment => segment.SpeakerId)));
        Check.Equal("Ahmed", f.Pipeline.CurrentTranscript.Segments[1].SpeakerDisplayName);

        Say("Late line from the absorbed speaker", "Guest-2");
        Check.Equal("Guest-1", f.Pipeline.CurrentTranscript!.Segments[^1].SpeakerId);

        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal("Guest-1,Guest-1,Guest-3,Guest-4,Guest-1",
            string.Join(',', f.Pipeline.LastCompletedTranscript!.Segments.Select(segment => segment.SpeakerId)));
        Check.Equal("Guest-1,Guest-3,Guest-4",
            string.Join(',', f.Visualization.Snapshot().Speakers.Select(speaker => speaker.SpeakerId).Order()));

        Check.True(await f.Pipeline.MergeSpeakersAsync(["Guest-3", "Guest-4"], "Guest-3"));
        Check.Equal("Guest-1,Guest-1,Guest-3,Guest-3,Guest-1",
            string.Join(',', f.Pipeline.LastCompletedTranscript!.Segments.Select(segment => segment.SpeakerId)));

        var saved = f.Saved.Last().Path;
        var loaded = await new JsonMeetingVisualizationStorage().LoadAsync(saved, CancellationToken.None);
        Check.Equal(MeetingVisualizationLoadKind.Valid, loaded.Kind);
        Check.Equal("Guest-1,Guest-3", string.Join(',', loaded.Document!.Speakers.Select(speaker => speaker.SpeakerId).Order()));
        Check.Equal("Guest-1,Guest-1,Guest-3,Guest-3,Guest-1",
            string.Join(',', loaded.Document.Segments.Select(segment => segment.SpeakerId)));
    }

    private static SpeakerTimelineRow RowOf(SpeakerTimelineModel model, string id) =>
        model.Rows.Single(row => row.SpeakerId == id);

    private static void TimelineCoalescing()
    {
        var segments = new[]
        {
            Line("one two three four five", 10, "A"),
            Line("six seven eight nine ten", 12, "A"),
            Line("eleven twelve", 14, "B"),
            Line("thirteen fourteen fifteen", 40, "A"),
        };
        var model = SpeakerTimeline.Compute(segments, Start, Start.AddSeconds(100), Start.AddSeconds(100));

        Check.Equal(100.0, model.Duration.TotalSeconds);
        Check.Equal("A,B", string.Join(',', model.Rows.Select(row => row.SpeakerId)));
        var a = RowOf(model, "A");
        // The first two lines are back to back, so they form one span; the later line stays separate.
        Check.Equal(2, a.Spans.Count);
        Check.True(Math.Abs(a.Spans[0].Start - 0.08) < 1e-9);
        Check.True(Math.Abs(a.Spans[0].End - 0.12) < 1e-9);
        Check.True(a.Spans[0].End < a.Spans[1].Start);
        Check.True(Math.Abs(a.Spans[1].End - 0.40) < 1e-9);
        Check.Equal(1, RowOf(model, "B").Spans.Count);
        Check.True(a.TalkTime > TimeSpan.Zero);
    }

    private static void TimelineAxis()
    {
        var segments = new[]
        {
            Line("short", 5, "A"),
            Line(string.Join(' ', Enumerable.Repeat("word", 200)), 500, "B"),
            Line("after the end", 900, "A"),
        };
        var model = SpeakerTimeline.Compute(segments, Start, Start.AddSeconds(600), Start.AddSeconds(600));

        Check.Equal(600.0, model.Duration.TotalSeconds);
        foreach (var span in model.Rows.SelectMany(row => row.Spans))
        {
            Check.True(span.Start >= 0 && span.End <= 1 && span.Start < span.End);
        }
        var b = RowOf(model, "B").Spans.Single();
        // A very long line is bounded by the 30 second estimate, ending at its timestamp.
        Check.True(Math.Abs(b.End - 500.0 / 600) < 1e-9);
        Check.True(Math.Abs(b.End - b.Start - 30.0 / 600) < 1e-9);
        var late = RowOf(model, "A").Spans.Last();
        Check.True(late.End <= 1.0);

        // While recording the axis ends at "now" (or the latest line, whichever is later).
        var live = SpeakerTimeline.Compute([Line("hello there", 20, "A")], Start, null, Start.AddSeconds(40));
        Check.Equal(40.0, live.Duration.TotalSeconds);
        Check.True(Math.Abs(live.Rows[0].Spans[0].End - 0.5) < 1e-9);
    }

    private static void TimelineEdges()
    {
        Check.Equal(0, SpeakerTimeline.Compute([], Start, Start.AddMinutes(1), Start.AddMinutes(1)).Rows.Count);

        var unknown = SpeakerTimeline.Compute(
            [Line("anonymous words", 10, null), Line("named words", 20, "Guest-1", "Ahmed")],
            Start, Start.AddSeconds(30), Start.AddSeconds(30));
        Check.Equal("Unknown,Guest-1", string.Join(',', unknown.Rows.Select(row => row.SpeakerId)));
        Check.Equal("Ahmed", RowOf(unknown, "Guest-1").DisplayName);

        var zero = SpeakerTimeline.Compute([Line("instant", 0, "A")], Start, Start, Start);
        Check.Equal(1, zero.Rows.Count);
        Check.Equal(0, zero.Rows[0].Spans.Count);

        var before = SpeakerTimeline.Compute([Line("early", -30, "A")], Start, Start.AddSeconds(10), Start.AddSeconds(10));
        Check.Equal(0, before.Rows[0].Spans.Count);

        var plan = SpeakerMergePlan.Create(["A", "B"], "A")!;
        var merged = SpeakerTimeline.Compute(
            new[] { Line("first words", 5, "A"), Line("second words", 6, "B"), Line("third words", 20, "A") }
                .Select(plan.Remap).ToArray(),
            Start, Start.AddSeconds(30), Start.AddSeconds(30));
        Check.Equal(1, merged.Rows.Count);
        Check.Equal(2, merged.Rows[0].Spans.Count);
    }

    private static void PanelMarkup()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FlyoutViewModels.cs.txt"));
        Check.True(xaml.Contains("CombineSpeakersButton") && xaml.Contains("OnCombineSpeakersClick"));
        Check.True(xaml.Contains("local:SpeakerTimelineBar"));
        Check.True(!xaml.Contains("PercentText") && !code.Contains("PercentText"));
    }
}
