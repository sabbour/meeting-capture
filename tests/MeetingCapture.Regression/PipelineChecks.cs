using MeetingCapture.AudioCapture;
using MeetingCapture.Diagnostics;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;

namespace MeetingCapture.Regression;

internal static class PipelineChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("pipeline start/stop/finalization share exclusive ownership", ExclusiveLifecycle);
        yield return ("pipeline serializes and drains all accepted audio before finalization", AcceptedAudioDrainsBeforeStop);
        yield return ("pipeline push completion near its deadline remains owned through Stop", AudioPushDeadlineRace);
        yield return ("pipeline discard wins before persistence and skips synthesis", DiscardBeforeSave);
        yield return ("pipeline Save now persists immediately and only once", SaveNow);
        yield return ("pipeline review timeout automatically saves", TimedAutoSave);
        yield return ("pipeline countdown boundary rejects late discard", CountdownBoundary);
        yield return ("pipeline save/discard races have exactly one winner", SaveDiscardRace);
        yield return ("pipeline exit during review preserves transcript without waiting", ExitDuringSaveReview);
        yield return ("pipeline exit before review forces save when review is created", ExitBeforeSaveReview);
        yield return ("pipeline stop during startup cancels and rolls back", StopDuringStartup);
        yield return ("pipeline each startup failure rolls back and permits retry", StartupFailures);
        yield return ("pipeline final flush persists all 1,201 segments and ignores stale callbacks", FinalFlushAndStaleEvents);
        yield return ("pipeline transcript subscribers do not hold the lifecycle gate", TranscriptSubscribersDoNotHoldLifecycleGate);
        yield return ("pipeline publishes concurrent final segments in accepted order", SegmentPublicationsStayOrdered);
        yield return ("pipeline bounds stalled audio, saves partial transcript, and retains native ownership", StalledPushSavesPartialAndRetainsOwnership);
        yield return ("pipeline AI failure keeps durable transcript and stable retry output", IntelligenceFailureAndRetry);
        yield return ("pipeline final map failure keeps generated notes and logs metadata only", FinalVisualizationFailureKeepsNotes);
        yield return ("pipeline post-synthesis Markdown failure remains a retryable partial save", PostSynthesisMarkdownFailure);
        yield return ("pipeline failed AI retry preserves defensive copy of all prior notes", PreservePriorSynthesis);
        yield return ("pipeline canceled or storage-failed retry preserves prior successful output", PreservePriorSynthesisOnInterruptedRetry);
        yield return ("pipeline retry cancellation releases gate and retains transcript", RetryCancellation);
        yield return ("pipeline disposal waits for owned retry before releasing services", DisposeDuringRetry);
        yield return ("pipeline disposal waits for timed-out speech shutdown", DisposeDuringTimedOutSpeechShutdown);
        yield return ("pipeline disposal after canceled or failed retry does not replay historical faults", DisposeAfterRetryFailure);
        yield return ("pipeline disposal still reports new service and device cleanup failures", NewDisposalFailures);
        yield return ("pipeline storage failure retains completed transcript for retry", StorageFailure);
        yield return ("pipeline failed transcription flush retains an incomplete saved transcript", FlushFailure);
        yield return ("pipeline degraded capture generates notes while retaining incomplete warning", DegradedCapture);
        yield return ("pipeline degraded capture preserves transcript when AI generation fails", DegradedCaptureIntelligenceFailure);
        yield return ("pipeline failed device shutdown cannot advertise startable", CaptureShutdownFailure);
        yield return ("pipeline retained transcription resources block restart after recognition stops", TranscriptionOwnership);
        yield return ("pipeline manual recording ignores unrelated detector end", ManualDetectionSeparation);
        yield return ("pipeline confirmed meeting end offers stop after grace instead of auto-stopping", MeetingEndOffersStopAfterGrace);
        yield return ("pipeline meeting end offer withdraws if the call resumes within grace", MeetingEndOfferWithdrawsOnResume);
        yield return ("pipeline meeting end offer never repeats for the same confirmed lifetime", MeetingEndOfferSuppressesDuplicates);
        yield return ("pipeline manual recording never raises the meeting end offer", MeetingEndOfferNeverForManualRecording);
        yield return ("pipeline offers stop only after two minutes of silence and stalled transcript", MeetingEndOfferRequiresBothSignals);
        yield return ("pipeline silence offer resets when audio resumes", MeetingEndOfferResetsOnAudio);
        yield return ("pipeline silence offer resets when transcript progresses", MeetingEndOfferResetsOnTranscript);
        yield return ("pipeline detection notification does not hold lifecycle lock across UI dispatch", DetectionDispatch);
        yield return ("pipeline ten synthetic sessions detach all per-session handlers", TenCycles);
        yield return ("pipeline audio preview starts no transcription and hands off ownership", AudioPreview);
        yield return ("pipeline push failure stops capture and saves the incomplete transcript", PushFailure);
        yield return ("pipeline Speech failure during startup preserves and saves recognized text", PushFailureDuringStartup);
        yield return ("pipeline repeated partial captions never become finalized speaker content", PartialCaptions);
        yield return ("pipeline applies synchronous speaker resolution before publishing final segment", SyncSpeakerResolution);
        yield return ("pipeline does not infer speaker identity from nearby microphone activity", NoMicIdentityGuess);
        yield return ("terminal recording status releases safe navigation before notification and retains transcript", TerminalNavigation);
        yield return ("exit cancels optional synthesis after durable transcript and tolerates repeated requests", ExitDuringSynthesis);
    }

    private static async Task DiscardBeforeSave()
    {
        var delay = new AsyncGate();
        var now = new DateTimeOffset(2026, 9, 23, 17, 45, 0, TimeSpan.Zero);
        await using var f = new PipelineFixture(
            saveDelay: (_, ct) => delay.WaitAsync(ct), getUtcNow: () => now);
        await f.Pipeline.StartManualAsync("Synthetic discard", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript to discard");
        f.Pipeline.SpeakerResolver.Resolve("Guest-1", "Synthetic Speaker");
        Check.True(f.Pipeline.SpeakerResolver.IsResolved("Guest-1"));
        var sessionId = f.Pipeline.CurrentSessionId!;
        var stop = f.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.Equal(PipelineState.ReviewingSave, f.Pipeline.State);
        Check.Equal(now.AddSeconds(5), f.Pipeline.Status.SaveDeadline);
        Check.True(f.Pipeline.CanReviewSave);
        Check.True(f.Pipeline.DiscardPendingRecording(sessionId));
        Check.True(!f.Pipeline.DiscardPendingRecording(sessionId));
        Check.True(!f.Pipeline.SaveNow(sessionId));
        await stop;
        Check.Equal(PipelineState.Discarded, f.Pipeline.State);
        Check.True(f.Pipeline.CanStart && !f.Pipeline.CanReviewSave);
        Check.True(f.Pipeline.LastCompletedTranscript is null);
        Check.Equal(0, f.Pipeline.CurrentTranscript!.WordCount);
        Check.True(!f.Pipeline.CurrentTranscript.FullText.Contains("Synthetic transcript to discard"));
        Check.True(!f.Pipeline.SpeakerResolver.IsResolved("Guest-1"));
        Check.Equal(0, f.Storage.Writes.Count);
        Check.Equal(0, f.Intelligence.Calls);
        Check.Equal(0, Directory.EnumerateFiles(f.Directory.Path).Count());
        await f.Pipeline.PrepareForExitAsync();
        Check.Equal(0, f.Storage.Writes.Count);
        Check.Equal(0, Directory.EnumerateFiles(f.Directory.Path).Count());
    }

    private static async Task SaveNow()
    {
        var delay = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await f.Pipeline.StartManualAsync("Synthetic save now", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript saved now");
        var sessionId = f.Pipeline.CurrentSessionId!;
        var stop = f.Pipeline.StopManualAsync();
        await delay.Entered;
        Check.True(f.Pipeline.SaveNow(sessionId));
        Check.True(!f.Pipeline.SaveNow(sessionId));
        Check.True(!f.Pipeline.DiscardPendingRecording(sessionId));
        await stop;
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal(2, f.Storage.Writes.Count);
        Check.Equal(1, f.Intelligence.Calls);
        Check.Equal(1, Directory.EnumerateFiles(f.Directory.Path, "*.md").Count());
    }

    private static async Task TimedAutoSave()
    {
        var delay = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await f.Pipeline.StartManualAsync("Synthetic timeout", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript saved by timeout");
        var sessionId = f.Pipeline.CurrentSessionId!;
        var stop = f.Pipeline.StopManualAsync();
        await delay.Entered;
        delay.Release();
        await stop;
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(!f.Pipeline.DiscardPendingRecording(sessionId));
        Check.Equal(2, f.Storage.Writes.Count);
        Check.Equal(1, f.Intelligence.Calls);
    }

    private static async Task CountdownBoundary()
    {
        var delay = new AsyncGate();
        var now = new DateTimeOffset(2026, 9, 23, 17, 45, 0, TimeSpan.Zero);
        await using var f = new PipelineFixture(
            saveDelay: (_, ct) => delay.WaitAsync(ct), getUtcNow: () => now);
        await f.Pipeline.StartManualAsync("Synthetic deadline", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript at countdown boundary");
        var sessionId = f.Pipeline.CurrentSessionId!;
        var stop = f.Pipeline.StopManualAsync();
        await delay.Entered;
        now = now.AddSeconds(5);
        Check.True(!f.Pipeline.DiscardPendingRecording(sessionId));
        await stop;
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal(1, f.Intelligence.Calls);
        Check.Equal(1, Directory.EnumerateFiles(f.Directory.Path, "*.md").Count());
    }

    private static async Task SaveDiscardRace()
    {
        var delay = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await f.Pipeline.StartManualAsync("Synthetic race", CancellationToken.None);
        f.Transcription.Emit("Synthetic raced transcript");
        var sessionId = f.Pipeline.CurrentSessionId!;
        var stop = f.Pipeline.StopManualAsync();
        await delay.Entered;
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            index % 2 == 0 ? f.Pipeline.SaveNow(sessionId) : f.Pipeline.DiscardPendingRecording(sessionId))));
        Check.Equal(1, attempts.Count(won => won));
        await stop;
        Check.True(f.Pipeline.State is PipelineState.Saved or PipelineState.Discarded);
        Check.Equal(f.Pipeline.State == PipelineState.Saved ? 1 : 0, f.Intelligence.Calls);
        Check.Equal(f.Pipeline.State == PipelineState.Saved ? 1 : 0,
            Directory.EnumerateFiles(f.Directory.Path, "*.md").Count());
    }

    private static async Task ExitDuringSaveReview()
    {
        var delay = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: (_, ct) => delay.WaitAsync(ct));
        await f.Pipeline.StartManualAsync("Synthetic exit review", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript preserved on exit");
        var sessionId = f.Pipeline.CurrentSessionId!;
        var stop = f.Pipeline.StopManualAsync();
        await delay.Entered;
        await f.Pipeline.PrepareForExitAsync();
        await stop;
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(!f.Pipeline.DiscardPendingRecording(sessionId));
        Check.Equal(1, f.Storage.Writes.Count);
        Check.Equal(0, f.Intelligence.Calls);
        Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Single()))
            .Contains("Synthetic transcript preserved on exit"));
    }

    private static async Task ExitBeforeSaveReview()
    {
        var flush = new AsyncGate();
        var saveDelay = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: (_, ct) => saveDelay.WaitAsync(ct));
        f.Transcription.OnStop = flush.WaitAsync;
        await f.Pipeline.StartManualAsync("Synthetic early exit", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript preserved by early exit");
        var stop = f.Pipeline.StopManualAsync();
        await flush.Entered;
        var exit = f.Pipeline.PrepareForExitAsync();
        flush.Release();
        await exit.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal(1, f.Storage.Writes.Count);
        Check.Equal(0, f.Intelligence.Calls);
        Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Single()))
            .Contains("Synthetic transcript preserved by early exit"));
    }

    private static async Task TerminalNavigation()
    {
        foreach (var withSpeech in new[] { false, true })
        {
            await using var f = new PipelineFixture();
            bool? canStartAtSaved = null;
            f.Pipeline.StateChanged += (_, status) =>
            {
                if (status.State == PipelineState.Saved) canStartAtSaved = f.Pipeline.CanStart;
            };
            await f.Pipeline.StartManualAsync("Synthetic terminal navigation", CancellationToken.None);
            if (withSpeech) f.Transcription.Emit("Synthetic finalized conversation");
            await f.Pipeline.StopManualAsync();
            Check.True(canStartAtSaved == true);
            Check.True(f.Pipeline.CanStart && !f.Pipeline.CanStop);
            var completed = f.Pipeline.LastCompletedTranscript!;
            Check.Equal(withSpeech ? 1 : 0, completed.Segments.Count);
            if (!withSpeech)
                Check.Equal("Transcript saved; no speech was recognized.", f.Pipeline.Status.Message);
            Check.True(File.Exists(f.Storage.Paths[0]));
            var savedPath = f.Storage.Paths[0];
            var savedText = await File.ReadAllTextAsync(savedPath);
            Check.True(ReferenceEquals(completed, f.Pipeline.LastCompletedTranscript));
            await f.Pipeline.StartManualAsync("Synthetic next recording", CancellationToken.None);
            Check.True(f.Pipeline.CanStop && !f.Pipeline.CanStart);
            Check.True(f.Pipeline.CurrentSessionId != completed.SessionId);
            Check.Equal(0, f.Pipeline.CurrentTranscript!.Segments.Count);
            Check.Equal(0, f.Visualization.Snapshot().Segments.Count);
            Check.Equal(0, f.Visualization.Snapshot().TopicClusters.Count);
            Check.Equal(0, f.Visualization.Snapshot().Questions.Count);
            Check.True(ReferenceEquals(completed, f.Pipeline.LastCompletedTranscript));
            f.Transcription.Emit("Only the second synthetic recording");
            await f.Pipeline.StopManualAsync();
            Check.True(f.Pipeline.CanStart);
            Check.Equal("Only the second synthetic recording", f.Pipeline.LastCompletedTranscript!.FullText);
            Check.Equal(savedText, await File.ReadAllTextAsync(savedPath));
            Check.Equal(2, f.Storage.Paths.Distinct().Count());
        }
    }

    private static async Task NoMicIdentityGuess()
    {
        await using var f = new PipelineFixture(localUser: _ => Task.FromResult<string?>("Synthetic User"));
        f.Capture.MicActivityTimestamp = DateTime.UtcNow;
        await f.Pipeline.StartManualAsync("Synthetic speaker identity", CancellationToken.None);
        f.Transcription.Emit("Synthetic remote speech");
        Check.True(!f.Pipeline.SpeakerResolver.IsResolved("Guest-1"));
        var beforeRename = f.Pipeline.CurrentTranscript!.Segments.Single();
        Check.Equal("Guest-1", beforeRename.SpeakerDisplayName);
        f.Pipeline.SpeakerResolver.Resolve("Guest-1", "Avery");
        var afterRename = f.Pipeline.CurrentTranscript!.Segments.Single();
        Check.Equal("Guest-1", afterRename.SpeakerId);
        Check.Equal("Avery", afterRename.SpeakerDisplayName);
        Check.Equal("Synthetic remote speech", afterRename.Text);
        Check.Equal("Guest-1", beforeRename.SpeakerDisplayName);
        await f.Pipeline.StopManualAsync();
    }

    private static async Task SyncSpeakerResolution()
    {
        await using var f = new PipelineFixture();
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
                f.Pipeline.SaveNow(sessionId);
        };
        f.Pipeline.SpeakerResolver.UnknownSpeakerDetected += (_, speakerId) =>
            f.Pipeline.SpeakerResolver.Resolve(speakerId, "Synthetic resolved speaker");
        SessionTranscriptSegment? published = null;
        f.Pipeline.TranscriptSegmentReceived += (_, update) => published = update;

        await f.Pipeline.StartManualAsync("Synthetic synchronous resolution", CancellationToken.None);
        f.Transcription.Emit("Resolved before publishing");

        Check.Equal("Synthetic resolved speaker",
            f.Pipeline.CurrentTranscript!.Segments.Single().SpeakerDisplayName);
        Check.Equal("Synthetic resolved speaker", published!.Segment.SpeakerDisplayName);
        Check.Equal("Synthetic resolved speaker", f.Visualization.Snapshot().Speakers.Single().DisplayName);
        await f.Pipeline.StopManualAsync();
        Check.Equal("Synthetic resolved speaker",
            f.Pipeline.LastCompletedTranscript!.Segments.Single().SpeakerDisplayName);
    }

    private static async Task ExitDuringSynthesis()
    {
        await using var f = new PipelineFixture();
        var gate = new AsyncGate();
        f.Intelligence.OnSynthesize = gate.WaitAsync;
        await f.Pipeline.StartManualAsync("Synthetic exit", CancellationToken.None);
        f.Transcription.Emit("Preserve this synthetic transcript");
        var stop = f.Pipeline.StopManualAsync();
        try
        {
            await gate.Entered;
            Check.True(File.Exists(f.Storage.Paths.Single()));
            await f.Pipeline.PrepareForExitAsync();
            await stop;
            await f.Pipeline.PrepareForExitAsync();
            Check.Equal(PipelineState.Saved, f.Pipeline.State);
            Check.True(!f.Pipeline.CanStart);
            Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Single())).Contains("Preserve this synthetic transcript"));
        }
        finally { gate.Release(); }
    }

    private static async Task PartialCaptions()
    {
        await using var f = new PipelineFixture();
        var partials = 0;
        var finals = 0;
        f.Pipeline.PartialTranscriptReceived += (_, _) => partials++;
        f.Pipeline.TranscriptSegmentReceived += (_, _) => finals++;
        await f.Pipeline.StartManualAsync("Synthetic caption isolation", CancellationToken.None);
        for (var i = 0; i < 20; i++) f.Transcription.EmitPartial("Synthetic provisional phrase");
        Check.True(partials > 0);
        Check.Equal(0, finals);
        Check.Equal(0, f.Pipeline.CurrentTranscript!.Segments.Count);
        Check.Equal(0, f.Pipeline.CurrentTranscript.WordCount);
        f.Transcription.Emit("Synthetic final phrase");
        Check.Equal(1, finals);
        Check.Equal(1, f.Pipeline.CurrentTranscript.Segments.Count);
        Check.Equal(3, f.Pipeline.CurrentTranscript.WordCount);
        await f.Pipeline.StopManualAsync();
        Check.Equal("Synthetic final phrase", f.Pipeline.LastCompletedTranscript!.FullText);
    }

    private static async Task DetectionDispatch()
    {
        await using var f = new PipelineFixture();
        var readableDuringNotification = false;
        f.Pipeline.MeetingDetected += (_, _) =>
        {
            var read = Task.Run(() => f.Pipeline.CanStart);
            readableDuringNotification = read.Wait(TimeSpan.FromSeconds(2)) && read.Result;
        };
        f.Detector.Start(new MeetingSession { SourceProcess = "Teams", Title = "Synthetic notification" });
        Check.True(readableDuringNotification);
    }

    private static async Task ExclusiveLifecycle()
    {
        await using var f = new PipelineFixture();
        var startup = new AsyncGate();
        var shutdown = new AsyncGate();
        var synthesis = new AsyncGate();
        f.Capture.OnStart = startup.WaitAsync;
        f.Capture.OnStop = shutdown.WaitAsync;
        f.Intelligence.OnSynthesize = synthesis.WaitAsync;
        var start = f.Pipeline.StartManualAsync("Synthetic gate", CancellationToken.None);
        try
        {
            await startup.Entered;
            Check.Equal(PipelineState.Starting, f.Pipeline.State);
            Check.True(f.Pipeline.CanStop && !f.Pipeline.CanStart);
            await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
            startup.Release();
            await start;
            f.Transcription.Emit("Synthetic gate text");
            var stop = f.Pipeline.StopManualAsync();
            Check.Equal(PipelineState.Stopping, f.Pipeline.State);
            Check.True(ReferenceEquals(stop, f.Pipeline.StopManualAsync()));
            await shutdown.Entered;
            Check.True(!f.Pipeline.CanStart && !f.Pipeline.CanStop);
            await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
            shutdown.Release();
            await synthesis.Entered;
            Check.Equal(PipelineState.Generating, f.Pipeline.State);
            Check.True(!f.Capture.IsCapturing && !f.Transcription.IsTranscribing);
            Check.True(File.Exists(f.Storage.Paths.Single()));
            Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Single())).Contains("Synthetic gate text"));
            await Check.ThrowsAsync<InvalidOperationException>(() =>
                f.Pipeline.RetryIntelligenceAsync(f.Pipeline.CurrentSessionId!, CancellationToken.None));
            await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
            synthesis.Release();
            await stop;
            Check.Equal(PipelineState.Saved, f.Pipeline.State);
            Check.True(f.Pipeline.CanStart);
            Check.Equal(1, f.Capture.Starts);
            Check.True(f.Statuses.Zip(f.Statuses.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence));
            Check.True(f.Statuses.Select(s => s.State).Contains(PipelineState.Finalizing));
        }
        finally { startup.Release(); shutdown.Release(); synthesis.Release(); }
    }

    private static async Task StopDuringStartup()
    {
        await using var f = new PipelineFixture();
        var startup = new AsyncGate();
        f.Capture.OnStart = startup.WaitAsync;
        var start = f.Pipeline.StartManualAsync("Synthetic canceled startup", CancellationToken.None);
        await startup.Entered;
        var stop = f.Pipeline.StopManualAsync();
        await Check.ThrowsAsync<OperationCanceledException>(() => start);
        await stop;
        AssertDetached(f);
        Check.Equal(PipelineState.Failed, f.Pipeline.State);
        Check.True(f.Pipeline.CanStart);
        f.Capture.OnStart = null;
        await f.Pipeline.StartManualAsync("Synthetic retry", CancellationToken.None);
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
    }

    private static async Task StartupFailures()
    {
        for (var step = 0; step < 3; step++)
        {
            var second = new FakeCapture();
            await using var f = new PipelineFixture(second);
            Func<CancellationToken, Task> fail = _ => throw new IOException("Synthetic startup fault");
            if (step == 0) f.Transcription.OnStart = fail;
            if (step == 1) f.Capture.OnStart = fail;
            if (step == 2) second.OnStart = fail;
            await Check.ThrowsAsync<IOException>(() => f.Pipeline.StartManualAsync("Synthetic fault", CancellationToken.None));
            AssertDetached(f);
            Check.True(!second.IsCapturing);
            Check.Equal(0, second.Subscribers);
            Check.Equal(PipelineState.Failed, f.Pipeline.State);
            Check.True(f.Pipeline.CanStart);
            f.Transcription.OnStart = null;
            f.Capture.OnStart = null;
            second.OnStart = null;
            await f.Pipeline.StartManualAsync("Synthetic clean retry", CancellationToken.None);
            await f.Pipeline.StopManualAsync();
            AssertDetached(f);
            Check.True(!second.IsCapturing);
        }
    }

    private static async Task FinalFlushAndStaleEvents()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic hour", CancellationToken.None);
        var id = f.Pipeline.CurrentSessionId!;
        var final = f.Transcription.CaptureFinalHandlers()!;
        var partial = f.Transcription.CapturePartialHandlers()!;
        var audio = f.Capture.CaptureHandlers()!;
        for (var i = 0; i < 1200; i++) f.Transcription.Emit($"Synthetic segment {i}");
        var before = f.Pipeline.CurrentTranscript!;
        f.Capture.OnStop = _ => { f.Capture.EmitAudio(); return Task.CompletedTask; };
        f.Transcription.OnStop = _ =>
        {
            Check.Equal(1, f.Transcription.Pushes);
            f.Transcription.Emit("FINAL FLUSH SENTINEL");
            return Task.CompletedTask;
        };
        await f.Pipeline.StopManualAsync();
        var completed = f.Pipeline.LastCompletedTranscript!;
        Check.Equal(id, completed.SessionId);
        Check.Equal(1200, before.Segments.Count);
        Check.Equal(1201, completed.Segments.Count);
        Check.True(completed.IsComplete);
        Check.True(completed.FullText.EndsWith("FINAL FLUSH SENTINEL"));
        Check.Equal(1201, f.Intelligence.Inputs.Single().Segments.Count);
        Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Last())).Contains("FINAL FLUSH SENTINEL"));
        f.Transcription.ClearReturnedResult();
        f.Intelligence.Inputs.Single().Segments.Clear();
        Check.Equal(1201, completed.Segments.Count);
        f.Capture.OnStop = null;
        f.Transcription.OnStop = null;
        await f.Pipeline.StartManualAsync("Synthetic new session", CancellationToken.None);
        Check.Equal(id, f.Pipeline.LastCompletedTranscript!.SessionId);
        var newId = f.Pipeline.CurrentSessionId;
        var delivered = 0;
        f.Pipeline.TranscriptSegmentReceived += (_, _) => delivered++;
        f.Pipeline.PartialTranscriptReceived += (_, _) => delivered++;
        final(f.Transcription, new TranscriptionSegment("STALE FINAL", DateTimeOffset.UtcNow));
        partial(f.Transcription, new PartialTranscript("STALE PARTIAL"));
        audio(f.Capture, new([3, 4], 2));
        Check.Equal(0, delivered);
        Check.Equal(0, f.Transcription.Pushes);
        Check.Equal(0, f.Pipeline.CurrentTranscript!.Segments.Count);
        Check.Equal(newId, f.Pipeline.CurrentSessionId);
        f.Transcription.Emit("NEW SESSION ONLY");
        Check.Equal(1, delivered);
        await f.Pipeline.StopManualAsync();
        Check.Equal(1, f.Pipeline.LastCompletedTranscript!.Segments.Count);
        Check.Equal(1201, completed.Segments.Count);
    }

    private static async Task FinalVisualizationFailureKeepsNotes()
    {
        foreach (var error in new Exception[]
        {
            new InvalidOperationException("synthetic map validation secret"),
            IntelligenceErrors.Create(IntelligenceFailureKind.Timeout, "github", "synthetic-model")
        })
        {
            await using var f = new PipelineFixture();
            await f.Pipeline.StartManualAsync("Synthetic map failure", CancellationToken.None);
            f.Transcription.Emit("SYNTHETIC PRIVATE TRANSCRIPT");
            f.Intelligence.VisualizationFailure = error;
            await f.Pipeline.StopManualAsync();

            Check.Equal(PipelineState.Saved, f.Pipeline.State);
            Check.True(f.Pipeline.Status.IntelligenceFailure is null);
            Check.Equal("Synthetic generated summary", f.Storage.Writes.Last().Summary);
            Check.True(f.Storage.Writes.Last().IntelligenceFailure is null);
            Check.True((await File.ReadAllTextAsync(f.Pipeline.Status.NotesPath!))
                .Contains("Synthetic generated summary", StringComparison.Ordinal));

            var log = await File.ReadAllTextAsync(Path.Combine(f.AiLogDirectory, AiFinalizationDiagnostics.FileName));
            Check.True(log.Contains("stage=final-visualization outcome=kept-live-map", StringComparison.Ordinal));
            Check.True(log.Contains($"type={error.GetType().Name}", StringComparison.Ordinal));
            Check.True(!log.Contains("SYNTHETIC PRIVATE TRANSCRIPT", StringComparison.Ordinal));
            Check.True(!log.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Check.True(f.Pipeline.CanStart);
        }

        var line = AiFinalizationDiagnostics.Format("unexpected-stage", "unexpected-outcome",
            new HttpRequestException("synthetic secret", null, System.Net.HttpStatusCode.BadGateway), -5);
        Check.True(line.Contains("stage=final outcome=failed kind=Unknown type=HttpRequestException http=502 elapsedMs=0",
            StringComparison.Ordinal));
        Check.True(!line.Contains("secret", StringComparison.Ordinal));
    }

    private static async Task IntelligenceFailureAndRetry()
    {
        await using var f = new PipelineFixture();
        f.Intelligence.Failure = IntelligenceErrors.Create(IntelligenceFailureKind.ModelUnavailable, "github", "synthetic-model");
        await f.Pipeline.StartManualAsync("Synthetic AI failure", CancellationToken.None);
        f.Transcription.Emit("PRESERVED SYNTHETIC TRANSCRIPT");
        await f.Pipeline.StopManualAsync();
        var id = f.Pipeline.LastCompletedTranscript!.SessionId;
        var path = f.Pipeline.Status.NotesPath!;
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal(IntelligenceFailureKind.ModelUnavailable, f.Pipeline.Status.IntelligenceFailure!.Kind);
        Check.True(f.Pipeline.LastCompletedTranscript.IsComplete);
        Check.True((await File.ReadAllTextAsync(path)).Contains("PRESERVED SYNTHETIC TRANSCRIPT"));
        Check.Equal(string.Empty, f.Storage.Writes[0].Summary);
        Check.Equal(string.Empty, f.Storage.Writes.Last().Summary);
        Check.Equal(0, f.Storage.Writes.Last().Decisions.Count);
        Check.Equal(0, f.Storage.Writes.Last().ActionItems.Count);
        var gate = new AsyncGate();
        f.Intelligence.Failure = null;
        f.Intelligence.OnSynthesize = gate.WaitAsync;
        var retry = f.Pipeline.RetryIntelligenceAsync(id, CancellationToken.None);
        try
        {
            await gate.Entered;
            Check.True(!f.Pipeline.CanStart);
            await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.RetryIntelligenceAsync(id, CancellationToken.None));
            await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
            gate.Release();
            Check.Equal(path, await retry);
        }
        finally { gate.Release(); }
        Check.True(f.Pipeline.Status.IntelligenceFailure is null);
        Check.True((await File.ReadAllTextAsync(path)).Contains("Synthetic generated summary"));
        Check.Equal(1, Directory.EnumerateFiles(f.Directory.Path, "*.md", SearchOption.AllDirectories).Count());
        Check.True(f.Intelligence.Inputs.All(input => input.FullText == "PRESERVED SYNTHETIC TRANSCRIPT"));
        await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.RetryIntelligenceAsync("stale-id", CancellationToken.None));
    }

    private static async Task PostSynthesisMarkdownFailure()
    {
        await using var f = new PipelineFixture();
        f.Storage.BeforeSave = (write, _) => write == 2
            ? Task.FromException(new IOException("Synthetic post-synthesis persistence failure"))
            : Task.CompletedTask;

        await f.Pipeline.StartManualAsync("Synthetic post-synthesis save failure", CancellationToken.None);
        f.Transcription.Emit("Synthetic durable transcript before generated notes");
        await f.Pipeline.StopManualAsync();

        var sessionId = f.Pipeline.LastCompletedTranscript!.SessionId;
        var path = f.Pipeline.Status.NotesPath!;
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal("Generated notes could not be saved. Retry AI.", f.Pipeline.Status.Error);
        Check.True(f.Pipeline.Status.Message.Contains("Transcript saved", StringComparison.Ordinal));
        Check.True(File.Exists(path));
        Check.Equal(1, f.Intelligence.Calls);
        var failedRewrite = f.Pipeline.AiDiagnostics.GetSnapshot().Events.Single(item =>
            item.Stage == AiDiagnosticStage.GeneratedMarkdownRewrite &&
            item.RetryOutcome == AiDiagnosticRetryOutcome.RetryAvailable);
        Check.Equal(nameof(IOException), failedRewrite.ExceptionType);
        Check.True(failedRewrite.ErrorCode is not null);

        f.Storage.BeforeSave = null;
        Check.Equal(path, await f.Pipeline.RetryIntelligenceAsync(sessionId, CancellationToken.None));
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(f.Pipeline.Status.Error is null);
        Check.True((await File.ReadAllTextAsync(path)).Contains("Synthetic generated summary"));
        Check.True(f.Pipeline.AiDiagnostics.GetSnapshot().Events.Any(item =>
            item.Stage == AiDiagnosticStage.GeneratedMarkdownRewrite &&
            item.RetryOutcome == AiDiagnosticRetryOutcome.RetrySucceeded));
    }

    private static void PopulateBaseline(MeetingNotes notes)
    {
        notes.Summary = "SYNTHETIC BASELINE SUMMARY";
        notes.Decisions.Add("SYNTHETIC BASELINE DECISION");
        notes.ActionItems.Add(new ActionItem { Description = "SYNTHETIC BASELINE ACTION", Owner = "Synthetic Owner", DueDate = "2026-10-01" });
        notes.Topics.Add("SyntheticTopic");
        notes.KeyDiscussionPoints.Add("SYNTHETIC BASELINE DISCUSSION");
        notes.Nuances = "Synthetic nuance";
        notes.MeetingType = "review";
        notes.Sentiment = "positive";
        notes.OpenQuestions.Add("SYNTHETIC BASELINE QUESTION");
        notes.FollowUpMeetings.Add("SYNTHETIC BASELINE FOLLOWUP");
        notes.NotableQuotes.Add("SYNTHETIC BASELINE QUOTE");
        notes.Participants.Add(new ParticipantInfo("Synthetic Participant", "Synthetic Role"));
        notes.EstimatedDurationMinutes = 60;
    }

    private static void AssertBaseline(MeetingNotes notes)
    {
        Check.Equal("SYNTHETIC BASELINE SUMMARY", notes.Summary);
        Check.Equal("SYNTHETIC BASELINE DECISION", notes.Decisions.Single());
        Check.Equal("SYNTHETIC BASELINE ACTION", notes.ActionItems.Single().Description);
        Check.Equal("Synthetic Owner", notes.ActionItems.Single().Owner);
        Check.Equal("2026-10-01", notes.ActionItems.Single().DueDate);
        Check.Equal("SyntheticTopic", notes.Topics.Single());
        Check.Equal("SYNTHETIC BASELINE DISCUSSION", notes.KeyDiscussionPoints.Single());
        Check.Equal("Synthetic nuance", notes.Nuances);
        Check.Equal("review", notes.MeetingType);
        Check.Equal("positive", notes.Sentiment);
        Check.Equal("SYNTHETIC BASELINE QUESTION", notes.OpenQuestions.Single());
        Check.Equal("SYNTHETIC BASELINE FOLLOWUP", notes.FollowUpMeetings.Single());
        Check.Equal("SYNTHETIC BASELINE QUOTE", notes.NotableQuotes.Single());
        Check.Equal(new ParticipantInfo("Synthetic Participant", "Synthetic Role"), notes.Participants.Single());
        Check.Equal(60, notes.EstimatedDurationMinutes);
        Check.Equal("Synthetic preserved baseline transcript", notes.RawTranscript);
    }

    private static async Task PreservePriorSynthesis()
    {
        await using var f = new PipelineFixture();
        f.Intelligence.PopulateNotes = PopulateBaseline;
        await f.Pipeline.StartManualAsync("Synthetic prior notes", CancellationToken.None);
        f.Transcription.Emit("Synthetic preserved baseline transcript");
        await f.Pipeline.StopManualAsync();
        var id = f.Pipeline.LastCompletedTranscript!.SessionId;
        var path = f.Pipeline.Status.NotesPath!;
        var saved = f.Storage.Writes.Last();
        var createdAt = saved.CreatedAt;
        AssertBaseline(saved);
        saved.Summary = "MUTATED STORAGE ARGUMENT";
        saved.Decisions.Clear();
        saved.ActionItems.Clear();
        saved.Topics.Clear();
        saved.KeyDiscussionPoints.Clear();
        saved.OpenQuestions.Clear();
        saved.FollowUpMeetings.Clear();
        saved.NotableQuotes.Clear();
        saved.Participants.Clear();
        f.Intelligence.Failure = IntelligenceErrors.Create(IntelligenceFailureKind.ModelUnavailable, "github", "synthetic-model");
        Check.Equal(path, await f.Pipeline.RetryIntelligenceAsync(id, CancellationToken.None));
        var preserved = f.Storage.Writes.Last();
        AssertBaseline(preserved);
        Check.Equal(id, preserved.SessionId);
        Check.Equal(createdAt, preserved.CreatedAt);
        Check.Equal(IntelligenceFailureKind.ModelUnavailable, preserved.IntelligenceFailure!.Kind);
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        var body = await File.ReadAllTextAsync(path);
        Check.True(body.Contains("SYNTHETIC BASELINE SUMMARY"));
        Check.True(body.Contains("SYNTHETIC BASELINE DECISION"));
        Check.True(body.Contains("SYNTHETIC BASELINE ACTION"));
        Check.True(body.Contains("Model unavailable"));
        Check.True(!body.Contains("MUTATED STORAGE ARGUMENT"));
        Check.Equal(1, Directory.EnumerateFiles(f.Directory.Path, "*.md").Count());
        await f.Pipeline.StartManualAsync("Synthetic following session", CancellationToken.None);
        Check.Equal(id, f.Pipeline.LastCompletedTranscript!.SessionId);
        f.Transcription.Emit("New session must not inherit old synthesis");
        await f.Pipeline.StopManualAsync();
        Check.True(f.Pipeline.LastCompletedTranscript!.SessionId != id);
        Check.Equal(string.Empty, f.Storage.Writes.Last().Summary);
        Check.Equal(0, f.Storage.Writes.Last().Decisions.Count);
        Check.Equal(0, f.Storage.Writes.Last().ActionItems.Count);
        Check.Equal("New session must not inherit old synthesis", f.Storage.Writes.Last().RawTranscript);
        Check.True((await File.ReadAllTextAsync(path)).Contains("SYNTHETIC BASELINE SUMMARY"));
    }

    private static async Task PreservePriorSynthesisOnInterruptedRetry()
    {
        foreach (var failureStage in new[] { "cancellation", "baseline-save", "replacement-save" })
        {
            await using var f = new PipelineFixture();
            f.Intelligence.PopulateNotes = PopulateBaseline;
            await f.Pipeline.StartManualAsync("Synthetic interrupted retry", CancellationToken.None);
            f.Transcription.Emit("Synthetic preserved baseline transcript");
            await f.Pipeline.StopManualAsync();
            var id = f.Pipeline.LastCompletedTranscript!.SessionId;
            var path = f.Pipeline.Status.NotesPath!;
            var baselineText = await File.ReadAllTextAsync(path);
            f.Intelligence.PopulateNotes = notes => notes.Summary = "UNSAVED REPLACEMENT SUMMARY";
            if (failureStage == "cancellation")
            {
                using var ct = new CancellationTokenSource();
                var gate = new AsyncGate();
                f.Intelligence.OnSynthesize = gate.WaitAsync;
                var retry = f.Pipeline.RetryIntelligenceAsync(id, ct.Token);
                await gate.Entered;
                AssertBaseline(f.Storage.Writes.Last());
                ct.Cancel();
                await Check.ThrowsAsync<OperationCanceledException>(() => retry);
                f.Intelligence.OnSynthesize = null;
            }
            else
            {
                var failedWrite = f.Storage.Writes.Count + (failureStage == "baseline-save" ? 1 : 2);
                f.Storage.BeforeSave = (write, _) => write == failedWrite
                    ? Task.FromException(new IOException("Synthetic retry persistence failure"))
                    : Task.CompletedTask;
                await Check.ThrowsAsync<IOException>(() => f.Pipeline.RetryIntelligenceAsync(id, CancellationToken.None));
                f.Storage.BeforeSave = null;
            }
            Check.Equal(baselineText, await File.ReadAllTextAsync(path));
            Check.Equal(path, f.Pipeline.Status.NotesPath);
            Check.True(f.Pipeline.CanStart);
            f.Intelligence.Failure = IntelligenceErrors.Create(IntelligenceFailureKind.Network, "github", "synthetic-model");
            Check.Equal(path, await f.Pipeline.RetryIntelligenceAsync(id, CancellationToken.None));
            AssertBaseline(f.Storage.Writes.Last());
            Check.Equal(IntelligenceFailureKind.Network, f.Storage.Writes.Last().IntelligenceFailure!.Kind);
            Check.True(!(await File.ReadAllTextAsync(path)).Contains("UNSAVED REPLACEMENT SUMMARY"));
        }
    }

    private static async Task TranscriptSubscribersDoNotHoldLifecycleGate()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic subscriber gate", CancellationToken.None);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Visualization.Changed += (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var emit = Task.Run(() => f.Transcription.Emit("Synthetic transcript"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check.True(await Task.Run(() => f.Pipeline.CanStop).WaitAsync(TimeSpan.FromSeconds(1)));
            await Task.Run(() => f.Capture.EmitAudio()).WaitAsync(TimeSpan.FromSeconds(1));
            Check.Equal(1, f.Transcription.Pushes);
        }
        finally
        {
            release.TrySetResult();
        }

        await emit;
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
    }

    private static async Task AcceptedAudioDrainsBeforeStop()
    {
        var secondCapture = new FakeCapture { Source = "loopback" };
        await using var f = new PipelineFixture(secondCapture,
            transcriptionTimeout: TimeSpan.FromSeconds(2));
        var pushGate = new AsyncGate();
        f.Transcription.OnPushAudio = pushGate.WaitAsync;
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
                f.Pipeline.SaveNow(sessionId);
        };
        long bytesAtStop = -1;
        f.Transcription.OnStop = _ =>
        {
            bytesAtStop = f.Transcription.TotalAudioBytesReceived;
            return Task.CompletedTask;
        };
        await f.Pipeline.StartManualAsync("Synthetic queued audio", CancellationToken.None);

        var firstFrame = new byte[] { 1, 2 };
        var secondFrame = new byte[] { 3, 4 };
        var firstEmit = Task.Run(() => f.Capture.EmitAudio(firstFrame));
        var pushesWhileBlocked = 0;
        var stopsWhileBlocked = 0;
        try
        {
            await pushGate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            var secondEmit = Task.Run(() => secondCapture.EmitAudio(secondFrame));
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            pushesWhileBlocked = f.Transcription.Pushes;
            var stop = f.Pipeline.StopManualAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            stopsWhileBlocked = f.Transcription.Stops;

            pushGate.Release();
            await Task.WhenAll(firstEmit, secondEmit).WaitAsync(TimeSpan.FromSeconds(2));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            pushGate.Release();
        }

        Check.Equal(1, pushesWhileBlocked);
        Check.Equal(0, stopsWhileBlocked);
        Check.Equal(4L, bytesAtStop);
        Check.Equal(4L, f.Transcription.TotalAudioBytesReceived);
        Check.Equal(2, f.Transcription.AudioFrames.Count);
        Check.True(f.Transcription.AudioFrames[0].SequenceEqual(new byte[] { 1, 2 }));
        Check.True(f.Transcription.AudioFrames[1].SequenceEqual(new byte[] { 3, 4 }));
        Check.True(f.Pipeline.CanStart);
    }

    private static async Task SegmentPublicationsStayOrdered()
    {
        await using var f = new PipelineFixture();
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
                f.Pipeline.SaveNow(sessionId);
        };
        await f.Pipeline.StartManualAsync("Synthetic ordered segments", CancellationToken.None);
        var visualGate = new AsyncGate();
        var changeCount = 0;
        f.Visualization.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref changeCount) == 1)
                visualGate.WaitAsync(CancellationToken.None).GetAwaiter().GetResult();
        };
        var ordinals = new List<int>();
        f.Pipeline.TranscriptSegmentReceived += (_, update) =>
        {
            lock (ordinals) ordinals.Add((int)update.Revision);
        };
        var final = f.Transcription.CaptureFinalHandlers()!;
        var first = Task.Run(() => final(f.Transcription,
            new TranscriptionSegment("Ordered first", DateTimeOffset.UtcNow)));
        try
        {
            await visualGate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            var second = Task.Run(() => final(f.Transcription,
                new TranscriptionSegment("Ordered second", DateTimeOffset.UtcNow)));
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            var currentSegments = f.Pipeline.CurrentTranscript!.Segments.Count;
            var canStop = await Task.Run(() => f.Pipeline.CanStop).WaitAsync(TimeSpan.FromSeconds(1));
            visualGate.Release();
            await second.WaitAsync(TimeSpan.FromSeconds(1));
            Check.Equal(2, currentSegments);
            Check.True(canStop);
        }
        finally
        {
            visualGate.Release();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(1));
        Check.True(ordinals.SequenceEqual(new[] { 1, 2 }));
        await f.Pipeline.StopManualAsync();
    }

    private static async Task AudioPushDeadlineRace()
    {
        const int timeoutMilliseconds = 100;
        await using var f = new PipelineFixture(
            transcriptionTimeout: TimeSpan.FromMilliseconds(timeoutMilliseconds));
        var pushGate = new AsyncGate();
        f.Transcription.OnPushAudio = pushGate.WaitAsync;
        var review = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
            {
                review.TrySetResult(sessionId);
                Check.True(!f.Pipeline.CanForceCloseAfterSavedPartial);
                f.Pipeline.SaveNow(sessionId);
            }
        };
        long bytesAtStop = -1;
        var pushesSettledAtStop = false;
        f.Transcription.OnStop = _ =>
        {
            bytesAtStop = f.Transcription.TotalAudioBytesReceived;
            pushesSettledAtStop = f.Transcription.PushesSettled.IsCompleted;
            return Task.CompletedTask;
        };
        await f.Pipeline.StartManualAsync("Synthetic deadline race", CancellationToken.None);
        var emit = Task.Run(() => f.Capture.EmitAudio([5, 6]));
        await pushGate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
        var releaseNearDeadline = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            pushGate.Release();
        });
        var stop = f.Pipeline.StopManualAsync();
        await Task.WhenAll(emit, releaseNearDeadline).WaitAsync(TimeSpan.FromSeconds(2));
        var sessionId = await review.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Check.Equal(sessionId, f.Pipeline.LastCompletedTranscript!.SessionId);
        Check.Equal(1, f.Transcription.Pushes);
        Check.Equal(1, f.Transcription.Stops);
        Check.Equal(2L, bytesAtStop);
        Check.Equal(2L, f.Transcription.TotalAudioBytesReceived);
        Check.True(pushesSettledAtStop);
        Check.True(f.Pipeline.CanStart);
    }

    private static async Task StalledPushSavesPartialAndRetainsOwnership()
    {
        await using var f = new PipelineFixture(transcriptionTimeout: TimeSpan.FromMilliseconds(80));
        var pushGate = new AsyncGate();
        var stopGate = new AsyncGate();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var review = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transcription.OnPushAudio = pushGate.WaitAsync;
        f.Transcription.PushFailure = new IOException("Synthetic late push failure");
        f.Transcription.OnStop = ct =>
        {
            stopStarted.TrySetResult();
            return stopGate.WaitAsync(ct);
        };
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
            {
                review.TrySetResult(sessionId);
                Check.True(!f.Pipeline.CanForceCloseAfterSavedPartial);
                f.Pipeline.SaveNow(sessionId);
            }
            if (f.Pipeline.CanStart) canStart.TrySetResult();
        };
        try
        {
            await f.Pipeline.StartManualAsync("Synthetic stalled speech", CancellationToken.None);
            f.Transcription.Emit("Recognized before stall");
            var staleFinal = f.Transcription.CaptureFinalHandlers()!;
            var frame = new byte[] { 17, 34 };
            var emit = Task.Run(() => f.Capture.EmitAudio(frame));
            await pushGate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            await emit.WaitAsync(TimeSpan.FromSeconds(1));
            frame[0] = 99;
            frame[1] = 100;

            var sessionId = await review.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await f.Pipeline.StopManualAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var stopStartedBeforePushSettled = stopStarted.Task.IsCompleted;
            var completed = f.Pipeline.LastCompletedTranscript!;
            Check.Equal(sessionId, completed.SessionId);
            Check.Equal(1, completed.Segments.Count);
            Check.True(completed.FullText.Contains("Recognized before stall"));
            Check.True(!completed.IsComplete);
            Check.True(f.Pipeline.Status.Error?.Contains("Speech transcription stalled", StringComparison.Ordinal) == true);
            Check.True(!f.Capture.IsCapturing);
            Check.Equal(1, f.Transcription.Pushes);
            Check.True(!f.Pipeline.CanStart);
            Check.True(File.Exists(f.Pipeline.Status.NotesPath));
            Check.True(f.Pipeline.CanForceCloseAfterSavedPartial);

            staleFinal(f.Transcription,
                new TranscriptionSegment("Late result after close", DateTimeOffset.UtcNow));
            Check.Equal(1, f.Pipeline.LastCompletedTranscript!.Segments.Count);

            pushGate.Release();
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(!stopStartedBeforePushSettled);
            Check.Equal(1, f.Transcription.AudioFrames.Count);
            Check.True(f.Transcription.AudioFrames[0].SequenceEqual(new byte[] { 17, 34 }));
            Check.True(!f.Pipeline.CanStart);

            f.Transcription.Emit("Late SDK result");
            stopGate.Release();
            await f.Transcription.StopCompleted.WaitAsync(TimeSpan.FromSeconds(2));
            await canStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(f.Pipeline.CanStart);
            Check.Equal(1, f.Pipeline.LastCompletedTranscript!.Segments.Count);

            await f.Pipeline.StartManualAsync("Synthetic next session", CancellationToken.None);
            var delivered = 0;
            f.Pipeline.TranscriptSegmentReceived += (_, _) => delivered++;
            staleFinal(f.Transcription,
                new TranscriptionSegment("Stale result in next session", DateTimeOffset.UtcNow));
            Check.Equal(0, delivered);
            Check.Equal(0, f.Pipeline.CurrentTranscript!.Segments.Count);
            await f.Pipeline.StopManualAsync();
            Check.True(f.Pipeline.CanStart);
            Check.True(!f.Pipeline.CanForceCloseAfterSavedPartial);
            await f.Pipeline.PrepareForExitAsync();
        }
        finally
        {
            pushGate.Release();
            stopGate.Release();
        }
    }

    private static async Task DisposeDuringTimedOutSpeechShutdown()
    {
        await using var f = new PipelineFixture(transcriptionTimeout: TimeSpan.FromMilliseconds(80));
        var stopGate = new AsyncGate();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var review = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transcription.OnStop = ct =>
        {
            stopStarted.TrySetResult();
            return stopGate.WaitAsync(ct);
        };
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
            {
                review.TrySetResult(sessionId);
                f.Pipeline.SaveNow(sessionId);
            }
        };
        try
        {
            await f.Pipeline.StartManualAsync("Synthetic shutdown", CancellationToken.None);
            f.Transcription.Emit("Recognized before shutdown");
            var stop = f.Pipeline.StopManualAsync();
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var sessionId = await review.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Check.Equal(sessionId, f.Pipeline.LastCompletedTranscript!.SessionId);

            var exit = f.Pipeline.PrepareForExitAsync();
            await Check.ThrowsAsync<InvalidOperationException>(() => exit);
            var dispose = f.Pipeline.DisposeAsync().AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            var disposalWaitedForSpeech = !dispose.IsCompleted && f.Transcription.Disposals == 0;

            stopGate.Release();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(disposalWaitedForSpeech);
            Check.Equal(1, f.Transcription.Disposals);
        }
        finally
        {
            stopGate.Release();
        }
    }

    private static async Task RetryCancellation()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic cancellation", CancellationToken.None);
        f.Transcription.Emit("Synthetic durable text");
        await f.Pipeline.StopManualAsync();
        var path = f.Pipeline.Status.NotesPath!;
        var gate = new AsyncGate();
        f.Intelligence.OnSynthesize = gate.WaitAsync;
        using var ct = new CancellationTokenSource();
        var retry = f.Pipeline.RetryIntelligenceAsync(f.Pipeline.LastCompletedTranscript!.SessionId, ct.Token);
        await gate.Entered;
        ct.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => retry);
        Check.True(f.Pipeline.CanStart);
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True((await File.ReadAllTextAsync(path)).Contains("Synthetic durable text"));
        f.Intelligence.OnSynthesize = null;
        await f.Pipeline.RetryIntelligenceAsync(f.Pipeline.LastCompletedTranscript.SessionId, CancellationToken.None);
        Check.Equal(path, f.Pipeline.Status.NotesPath);
    }

    private static async Task DisposeDuringRetry()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic shutdown retry", CancellationToken.None);
        f.Transcription.Emit("Synthetic shutdown text");
        f.Storage.BeforeSave = (_, _) => throw new IOException("Synthetic prior stop failure");
        await Check.ThrowsAsync<IOException>(() => f.Pipeline.StopManualAsync());
        f.Storage.BeforeSave = null;
        var gate = new AsyncGate();
        f.Intelligence.OnSynthesize = gate.WaitAsync;
        var retry = f.Pipeline.RetryIntelligenceAsync(f.Pipeline.LastCompletedTranscript!.SessionId, CancellationToken.None);
        try
        {
            await gate.Entered;
            var disposal = f.Pipeline.DisposeAsync().AsTask();
            Check.True(!disposal.IsCompleted);
            Check.Equal(0, f.Capture.Disposals);
            Check.Equal(0, f.Transcription.Disposals);
            Check.True(!f.Pipeline.CanStart);
            gate.Release();
            await retry;
            await disposal;
            Check.Equal(1, f.Capture.Disposals);
            Check.Equal(1, f.Transcription.Disposals);
        }
        finally { gate.Release(); }
    }

    private static async Task DisposeAfterRetryFailure()
    {
        foreach (var cancel in new[] { false, true })
        {
            await using var f = new PipelineFixture();
            await f.Pipeline.StartManualAsync("Synthetic final retry failure", CancellationToken.None);
            f.Transcription.Emit("Synthetic text persists through retry failure");
            await f.Pipeline.StopManualAsync();
            var sessionId = f.Pipeline.LastCompletedTranscript!.SessionId;
            if (cancel)
            {
                using var ct = new CancellationTokenSource();
                var gate = new AsyncGate();
                f.Intelligence.OnSynthesize = gate.WaitAsync;
                var retry = f.Pipeline.RetryIntelligenceAsync(sessionId, ct.Token);
                await gate.Entered;
                ct.Cancel();
                await Check.ThrowsAsync<OperationCanceledException>(() => retry);
            }
            else
            {
                f.Storage.BeforeSave = (_, _) => throw new IOException("Synthetic retry save failure");
                await Check.ThrowsAsync<IOException>(() => f.Pipeline.RetryIntelligenceAsync(sessionId, CancellationToken.None));
            }
            await f.Pipeline.DisposeAsync();
            Check.Equal(1, f.Capture.Disposals);
            Check.Equal(1, f.Transcription.Disposals);
        }
    }

    private static async Task NewDisposalFailures()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic cleanup faults", CancellationToken.None);
        await f.Pipeline.StopManualAsync();
        f.Capture.DisposeFailure = new IOException("Synthetic new capture disposal failure");
        f.Transcription.DisposeFailure = new IOException("Synthetic new speech disposal failure");
        var failure = await Check.ThrowsAsync<AggregateException>(() => f.Pipeline.DisposeAsync().AsTask());
        Check.Equal(2, failure.InnerExceptions.Count);
        Check.True(failure.InnerExceptions.Contains(f.Capture.DisposeFailure));
        Check.True(failure.InnerExceptions.Contains(f.Transcription.DisposeFailure));
        Check.Equal(1, f.Capture.Disposals);
        Check.Equal(1, f.Transcription.Disposals);
    }

    private static async Task StorageFailure()
    {
        await using var f = new PipelineFixture();
        f.Storage.BeforeSave = (_, _) => throw new IOException("Synthetic full disk");
        await f.Pipeline.StartManualAsync("Synthetic storage failure", CancellationToken.None);
        f.Transcription.Emit("Synthetic retained after save failure");
        await Check.ThrowsAsync<IOException>(() => f.Pipeline.StopManualAsync());
        Check.Equal(PipelineState.Failed, f.Pipeline.State);
        Check.Equal("Could not save the completed meeting. Check the notes location and retry.", f.Pipeline.Status.Error);
        Check.True(f.Pipeline.Status.Message.Contains("finalization failed", StringComparison.OrdinalIgnoreCase));
        Check.True(f.Pipeline.CanStart);
        Check.True(f.Pipeline.LastCompletedTranscript!.IsComplete);
        Check.Equal(0, f.Saved.Count);
        Check.Equal(0, f.Intelligence.Calls);
        AssertDetached(f);
        f.Storage.BeforeSave = null;
        var path = await f.Pipeline.RetryIntelligenceAsync(f.Pipeline.LastCompletedTranscript.SessionId, CancellationToken.None);
        Check.True((await File.ReadAllTextAsync(path)).Contains("Synthetic retained after save failure"));
    }

    private static async Task FlushFailure()
    {
        await using var f = new PipelineFixture();
        f.Transcription.OnStop = _ => throw new IOException("Synthetic flush failure");
        await f.Pipeline.StartManualAsync("Synthetic incomplete", CancellationToken.None);
        f.Transcription.Emit("Synthetic available text");
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(!f.Pipeline.LastCompletedTranscript!.IsComplete);
        Check.Equal("Synthetic available text", f.Pipeline.LastCompletedTranscript.FullText);
        Check.Equal("Transcription flush failed; available text was retained.", f.Pipeline.Status.Error);
        Check.True((await File.ReadAllTextAsync(f.Pipeline.Status.NotesPath!)).Contains("Synthetic available text"));
        Check.Equal(1, f.Intelligence.Calls);
        Check.Equal("Synthetic available text", f.Intelligence.Inputs.Single().FullText);
        AssertDetached(f);
        await f.Pipeline.StartManualAsync("Synthetic recording after flush warning", CancellationToken.None);
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
    }

    private static async Task CaptureShutdownFailure()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic shutdown fault", CancellationToken.None);
        f.Transcription.Emit("Synthetic text retained despite device fault");
        f.Capture.OnStop = _ => throw new IOException("Synthetic device would not stop");
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Failed, f.Pipeline.State);
        Check.True(f.Capture.IsCapturing);
        Check.True(!f.Pipeline.CanStart);
        Check.Equal(0, f.Intelligence.Calls);
        await Check.ThrowsAsync<InvalidOperationException>(() =>
            f.Pipeline.StartManualAsync("Must not overlap device", CancellationToken.None));
        Check.True(File.Exists(f.Pipeline.Status.NotesPath));
        Check.Equal(0, f.Capture.Subscribers);
        Check.Equal(0, f.Transcription.FinalSubscribers);
    }

    private static async Task DegradedCapture()
    {
        var visualizationStorage = new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage())
        {
            FailuresRemaining = 1
        };
        await using var f = new PipelineFixture(visualizationStorage: visualizationStorage);
        await f.Pipeline.StartManualAsync("Synthetic degraded capture", CancellationToken.None);
        f.Transcription.Emit("Synthetic durable transcript");
        f.Capture.DroppedBuffers = 1;

        await f.Pipeline.StopManualAsync();

        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(!f.Pipeline.LastCompletedTranscript!.IsComplete);
        Check.Equal(
            "Audio capture was incomplete: 1 audio buffer was dropped. " +
            "The saved transcript contains only audio that was successfully received. " +
            "If this repeats, close other high-load apps before recording again.",
            f.Pipeline.Status.Error);
        Check.Equal("Meeting notes saved.", f.Pipeline.Status.Message);
        Check.True(File.Exists(f.Pipeline.Status.NotesPath));
        Check.Equal(1, f.Intelligence.Calls);
        Check.Equal(2, f.Storage.Writes.Count);
        Check.Equal("Synthetic generated summary", f.Storage.Writes.Last().Summary);
        Check.True(!visualizationStorage.Writes.Last().Document.IsComplete);
        Check.True((await File.ReadAllTextAsync(f.Pipeline.Status.NotesPath!))
            .Contains("AI notes generated.", StringComparison.Ordinal));
        Check.True(!f.Capture.IsCapturing);
        Check.True(!f.Transcription.HasActiveResources);
        Check.True(f.Pipeline.CanStart);

        Check.Equal(ExitReadiness.Clean, await f.Pipeline.PrepareForExitAsync());
        Check.Equal(2, visualizationStorage.Saves);

        Check.True(MeetingPipeline.DescribeCaptureCompletionWarning([
            new("microphone", CaptureHealthState.Stopped, 1, 1, 0, null, null, null)
        ]) is null);
        Check.Equal(
            "Audio capture was incomplete: microphone audio was unavailable. " +
            "The saved transcript contains only audio that was successfully received. " +
            "Check Audio settings and device access before recording again.",
            MeetingPipeline.DescribeCaptureCompletionWarning([
                new("microphone", CaptureHealthState.Failed, 0, 0, 0, null, null,
                    "The configured audio source could not start; its audio is missing.")
            ]));
    }

    private static async Task DegradedCaptureIntelligenceFailure()
    {
        await using var f = new PipelineFixture();
        f.Intelligence.Failure = IntelligenceErrors.Create(
            IntelligenceFailureKind.ModelUnavailable, "github", "synthetic-model");
        await f.Pipeline.StartManualAsync("Synthetic degraded AI failure", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript preserved after degraded capture and AI failure");
        f.Capture.DroppedBuffers = 1;

        await f.Pipeline.StopManualAsync();

        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(!f.Pipeline.LastCompletedTranscript!.IsComplete);
        Check.Equal(1, f.Intelligence.Calls);
        Check.Equal(
            "Audio capture was incomplete: 1 audio buffer was dropped. " +
            "The saved transcript contains only audio that was successfully received. " +
            "If this repeats, close other high-load apps before recording again.",
            f.Pipeline.Status.Error);
        Check.Equal(IntelligenceFailureKind.ModelUnavailable, f.Pipeline.Status.IntelligenceFailure!.Kind);
        Check.Equal(string.Empty, f.Storage.Writes.Last().Summary);
        var markdown = await File.ReadAllTextAsync(f.Pipeline.Status.NotesPath!);
        Check.True(markdown.Contains("AI generation failed (ModelUnavailable).", StringComparison.Ordinal));
        Check.True(markdown.Contains(
            "Synthetic transcript preserved after degraded capture and AI failure", StringComparison.Ordinal));
    }

    private static async Task TranscriptionOwnership()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic retained speech resources", CancellationToken.None);
        f.Transcription.Emit("Synthetic text before disposal fault");
        f.Transcription.OnStop = _ =>
        {
            f.Transcription.RetainResources = true;
            throw new IOException("Synthetic speech cleanup failure");
        };
        await f.Pipeline.StopManualAsync();
        Check.True(!f.Transcription.IsTranscribing);
        Check.True(f.Transcription.HasActiveResources);
        Check.Equal(PipelineState.Failed, f.Pipeline.State);
        Check.True(!f.Pipeline.CanStart);
        Check.Equal(0, f.Intelligence.Calls);
        Check.True(!f.Pipeline.LastCompletedTranscript!.IsComplete);
        Check.Equal("Synthetic text before disposal fault", f.Pipeline.LastCompletedTranscript.FullText);
        Check.True(File.Exists(f.Pipeline.Status.NotesPath));
        await Check.ThrowsAsync<InvalidOperationException>(() =>
            f.Pipeline.StartManualAsync("Must not reuse owned speech resources", CancellationToken.None));
        Check.Equal(1, f.Transcription.Starts);
    }

    private static async Task ManualDetectionSeparation()
    {
        await using var f = new PipelineFixture();
        var detected = new MeetingSession { Title = "Synthetic detection", SourceProcess = "Teams" };
        f.Detector.Start(detected);
        await f.Pipeline.StartManualAsync("Synthetic manual", CancellationToken.None);
        var id = f.Pipeline.CurrentSessionId;
        f.Detector.End(detected);
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
        Check.Equal(id, f.Pipeline.CurrentSessionId);
        Check.Equal(0, f.Capture.Stops);
        Check.Equal(0, f.Detector.ConfirmedSessions.Count);
        f.Transcription.Emit("Synthetic manual continues");
        await f.Pipeline.StopManualAsync();
        await Check.ThrowsAsync<InvalidOperationException>(() =>
            f.Pipeline.ConfirmAndStartAsync(new MeetingSession { SourceProcess = "Teams" }, CancellationToken.None));
        Check.True(f.Pipeline.CanStart);
        Check.Equal(1, f.Capture.Starts);
        var matching = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(matching);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() =>
            f.Pipeline.ConfirmAndStartAsync(matching, canceled.Token));
        Check.Equal(0, f.Detector.ConfirmedSessions.Count);
        Check.Equal(1, f.Capture.Starts);
        await f.Pipeline.ConfirmAndStartAsync(matching, CancellationToken.None);
        Check.Equal(matching.Id, f.Detector.ConfirmedSessions.Single());
        await Check.ThrowsAsync<InvalidOperationException>(() =>
            f.Pipeline.ConfirmAndStartAsync(matching, CancellationToken.None));
        Check.Equal(1, f.Detector.ConfirmedSessions.Count);
        Check.Equal(2, f.Capture.Starts);
        f.Transcription.Emit("Synthetic detected recording");
        f.Detector.End(new MeetingSession { SourceProcess = "Zoom" });
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
        f.Detector.End(matching);
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal(matching.Id, f.Pipeline.LastCompletedTranscript!.SessionId);
    }

    private static readonly TimeSpan MeetingEndGrace = TimeSpan.FromSeconds(8);

    /// <summary>Routes the meeting-end grace delay through a controllable gate; every other delay resolves instantly.</summary>
    private static Func<TimeSpan, CancellationToken, Task> GraceGateDelay(AsyncGate grace) =>
        (span, ct) => span == MeetingEndGrace ? grace.WaitAsync(ct) : Task.CompletedTask;

    private static async Task MeetingEndOffersStopAfterGrace()
    {
        var grace = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: GraceGateDelay(grace));
        var offered = new TaskCompletionSource<MeetingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offered.TrySetResult(session);
        var matching = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(matching);
        await f.Pipeline.ConfirmAndStartAsync(matching, CancellationToken.None);
        f.Detector.End(matching);
        await grace.Entered;
        // The offer must not fire, and the pipeline must not auto-stop, before the grace elapses.
        Check.True(!offered.Task.IsCompleted);
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
        grace.Release();
        var session = await offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(matching.Id, session.Id);
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
    }

    private static async Task MeetingEndOfferWithdrawsOnResume()
    {
        var grace = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: GraceGateDelay(grace));
        var offered = new TaskCompletionSource<MeetingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offered.TrySetResult(session);
        var matching = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(matching);
        await f.Pipeline.ConfirmAndStartAsync(matching, CancellationToken.None);
        f.Detector.End(matching);
        await grace.Entered;
        // The same call resumes (detector reports a new started signal for the same app) before
        // the grace window elapses; the pending offer must be withdrawn, never shown.
        f.Detector.Start(new MeetingSession { SourceProcess = "Teams" });
        await Task.Delay(50);
        Check.True(!offered.Task.IsCompleted);
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
    }

    private static async Task MeetingEndOfferSuppressesDuplicates()
    {
        var grace = new AsyncGate();
        await using var f = new PipelineFixture(saveDelay: GraceGateDelay(grace));
        var offers = new List<MeetingSession>();
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offers.Add(session);
        var matching = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(matching);
        await f.Pipeline.ConfirmAndStartAsync(matching, CancellationToken.None);
        f.Detector.End(matching);
        await grace.Entered;
        grace.Release();
        while (offers.Count == 0) await Task.Delay(10);
        Check.Equal(1, offers.Count);
        // A duplicate end signal for the same already-ended lifetime must never re-prompt.
        f.Detector.End(matching);
        await Task.Delay(50);
        Check.Equal(1, offers.Count);
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
    }

    private static async Task MeetingEndOfferNeverForManualRecording()
    {
        var grace = new AsyncGate();
        var now = new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero);
        await using var f = new PipelineFixture(saveDelay: GraceGateDelay(grace), getUtcNow: () => now);
        var offers = new List<MeetingSession>();
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offers.Add(session);
        await f.Pipeline.StartManualAsync("Synthetic manual", CancellationToken.None);
        now = now.AddMinutes(2);
        f.Capture.EmitSilence();
        Check.Equal(0, offers.Count);
        var detected = new MeetingSession { Title = "Synthetic detection", SourceProcess = "Teams" };
        f.Detector.Start(detected);
        f.Detector.End(detected);
        await Task.Delay(50);
        Check.Equal(0, offers.Count);
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
    }

    private static async Task MeetingEndOfferRequiresBothSignals()
    {
        var now = new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero);
        await using var f = new PipelineFixture(getUtcNow: () => now);
        var offers = new List<MeetingSession>();
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offers.Add(session);
        var session = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(session);
        await f.Pipeline.ConfirmAndStartAsync(session, CancellationToken.None);

        now = now.AddMinutes(2);
        f.Capture.EmitAudio();
        Check.Equal(0, offers.Count);

        now = now.AddMinutes(2);
        f.Capture.EmitSilence();
        Check.Equal(1, offers.Count);
        Check.Equal(session.Id, offers.Single().Id);
        Check.Equal(PipelineState.Recording, f.Pipeline.State);
        Check.Equal(0, f.Capture.Stops);

        f.Capture.EmitSilence();
        Check.Equal(1, offers.Count);
        await f.Pipeline.StopManualAsync();
    }

    private static async Task MeetingEndOfferResetsOnAudio()
    {
        var now = new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero);
        await using var f = new PipelineFixture(getUtcNow: () => now);
        var offers = new List<MeetingSession>();
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offers.Add(session);
        var session = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(session);
        await f.Pipeline.ConfirmAndStartAsync(session, CancellationToken.None);

        now = now.AddMinutes(2);
        f.Capture.EmitAudio();
        now = now.AddMinutes(2).AddTicks(-1);
        f.Capture.EmitSilence();
        Check.Equal(0, offers.Count);
        now = now.AddTicks(1);
        f.Capture.EmitSilence();
        Check.Equal(1, offers.Count);
        await f.Pipeline.StopManualAsync();
    }

    private static async Task MeetingEndOfferResetsOnTranscript()
    {
        var now = new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero);
        await using var f = new PipelineFixture(getUtcNow: () => now);
        var offers = new List<MeetingSession>();
        f.Pipeline.MeetingEndedWhileRecording += (_, session) => offers.Add(session);
        var session = new MeetingSession { Title = "Synthetic confirmed", SourceProcess = "Teams" };
        f.Detector.Start(session);
        await f.Pipeline.ConfirmAndStartAsync(session, CancellationToken.None);

        now = now.AddMinutes(2).AddTicks(-1);
        f.Transcription.Emit("Synthetic transcript progress");
        now = now.AddTicks(1);
        f.Capture.EmitSilence();
        Check.Equal(0, offers.Count);
        now = now.AddMinutes(2);
        f.Capture.EmitSilence();
        Check.Equal(1, offers.Count);
        await f.Pipeline.StopManualAsync();
    }

    private static async Task TenCycles()
    {
        await using var f = new PipelineFixture();
        var identities = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            await f.Pipeline.StartManualAsync($"Synthetic cycle {i}", CancellationToken.None);
            Check.True(identities.Add(f.Pipeline.CurrentSessionId!));
            Check.Equal(0L, f.Pipeline.GetDiagnostics().Transcription.ReceivedBytes);
            Check.Equal(1, f.Capture.Subscribers);
            Check.Equal(1, f.Transcription.FinalSubscribers);
            Check.Equal(1, f.Transcription.PartialSubscribers);
            f.Capture.EmitAudio();
            f.Transcription.Emit($"Synthetic cycle {i} text");
            Check.Equal(2L, f.Pipeline.GetDiagnostics().Transcription.ReceivedBytes);
            await f.Pipeline.StopManualAsync();
            AssertDetached(f);
            Check.Equal(PipelineState.Saved, f.Pipeline.State);
            Check.Equal(1, f.Pipeline.LastCompletedTranscript!.Segments.Count);
        }
        Check.Equal(10, f.Capture.Starts);
        Check.Equal(10, f.Capture.Stops);
        Check.Equal(10, f.Transcription.Starts);
        Check.Equal(10, f.Transcription.Stops);
        await f.Pipeline.DisposeAsync();
        Check.Equal(1, f.Capture.Disposals);
        Check.Equal(1, f.Transcription.Disposals);
        Check.True(!f.Pipeline.CanStart);
    }

    private static async Task AudioPreview()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartAudioPreviewAsync(CancellationToken.None);
        Check.True(f.Capture.IsCapturing);
        Check.True(f.Pipeline.CurrentSessionId is null);
        Check.True(f.Pipeline.CurrentTranscript is null);
        Check.True(f.Pipeline.GetDiagnostics().SessionId is null);
        Check.Equal(0, f.Transcription.Starts);
        Check.Equal(0, f.Capture.Subscribers);
        await f.Pipeline.StartManualAsync("Synthetic after preview", CancellationToken.None);
        Check.Equal(1, f.Capture.Stops);
        Check.Equal(2, f.Capture.Starts);
        Check.Equal(1, f.Transcription.Starts);
        await f.Pipeline.StopManualAsync();
        await f.Pipeline.StartAudioPreviewAsync(CancellationToken.None);
        await f.Pipeline.StopAudioPreviewAsync(CancellationToken.None);
        Check.True(f.Pipeline.CanStart);
        Check.True(!f.Capture.IsCapturing);
    }

    private static async Task PushFailure()
    {
        await using var f = new PipelineFixture();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
                f.Pipeline.SaveNow(sessionId);
            if (status.State == PipelineState.Saved)
                saved.TrySetResult();
        };
        await f.Pipeline.StartManualAsync("Synthetic push failure", CancellationToken.None);
        f.Transcription.Emit("Synthetic before fault");
        f.Transcription.PushFailure = new IOException("Synthetic push fault");
        f.Transcription.HealthError = "Speech rejected audio (synthetic).";
        f.Capture.EmitAudio();
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.Equal("Speech rejected audio (synthetic).", f.Pipeline.Status.Error);
        Check.True(!f.Capture.IsCapturing);
        Check.Equal(1, f.Capture.Stops);
        var pushes = f.Transcription.Pushes;
        for (var i = 0; i < 10; i++) f.Capture.EmitAudio();
        Check.Equal(pushes, f.Transcription.Pushes);
        f.Transcription.Emit("Synthetic after fault");
        Check.True(!f.Pipeline.LastCompletedTranscript!.IsComplete);
        Check.Equal(1, f.Pipeline.LastCompletedTranscript!.Segments.Count);
        Check.True(File.Exists(f.Pipeline.Status.NotesPath));
    }

    private static async Task PushFailureDuringStartup()
    {
        await using var f = new PipelineFixture();
        var gate = new AsyncGate();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transcription.HealthError = "Speech rejected startup audio (synthetic).";
        f.Transcription.OnPushAudio = async ct =>
        {
            await gate.WaitAsync(ct);
            throw new IOException("Synthetic startup push failure");
        };
        f.Capture.OnStart = _ =>
        {
            f.Transcription.Emit("Recognized during startup");
            f.Capture.EmitAudio([3, 4]);
            return Task.CompletedTask;
        };
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave && status.SessionId is { } sessionId)
                f.Pipeline.SaveNow(sessionId);
            if (status.State == PipelineState.Saved)
                saved.TrySetResult();
        };
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            gate.Release();
        });

        await f.Pipeline.StartManualAsync("Synthetic startup push failure", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await release.WaitAsync(TimeSpan.FromSeconds(2));
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Check.Equal(1, f.Transcription.Pushes);
        Check.Equal(1, f.Capture.Stops);
        Check.True(!f.Capture.IsCapturing);
        Check.Equal("Speech rejected startup audio (synthetic).", f.Pipeline.Status.Error);
        Check.Equal("Recognized during startup", f.Pipeline.LastCompletedTranscript!.FullText);
        Check.True(!f.Pipeline.LastCompletedTranscript.IsComplete);
    }

    private static void AssertDetached(PipelineFixture f)
    {
        Check.True(!f.Capture.IsCapturing);
        Check.True(!f.Transcription.IsTranscribing);
        Check.Equal(0, f.Capture.Subscribers);
        Check.Equal(0, f.Transcription.FinalSubscribers);
        Check.Equal(0, f.Transcription.PartialSubscribers);
    }
}
