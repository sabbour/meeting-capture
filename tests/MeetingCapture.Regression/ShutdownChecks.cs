using System.Diagnostics;
using MeetingCapture.Intelligence;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;

namespace MeetingCapture.Regression;

internal static class ShutdownChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("exit after successful recording is single-flight and blocks new work", SuccessfulExit);
        yield return ("exit cannot skip a historical failed save and preserves recovery", FailedSave);
        yield return ("exit waits for durable transcript save before finishing", SaveGate);
        yield return ("exit abandons uncooperative optional synthesis without late writes", UncooperativeSynthesis);
        yield return ("exit cancels externally requested AI retry without replaying cancellation", CancelRetry);
        yield return ("exit refuses remaining capture ownership without disposing recovery", CaptureOwnership);
        yield return ("window close stops layout callbacks and never re-shows a closing window", ClosingWindowGuards);
        yield return ("crash log keeps exception types and stacks but no message text", CrashLogMetadataOnly);
        yield return ("saved partial Speech exit requires explicit confirmation", SavedPartialClosePolicy);
        yield return ("exit completes when only the optional visualization sidecar cannot be saved",
            PendingSidecarNeverBlocksExit);
        yield return ("exit still refuses capture ownership when a visualization sidecar is pending",
            CaptureOwnershipOutranksPendingSidecar);
        yield return ("deferred visualization retries survive until they succeed or lose their transcript",
            DeferredVisualizationJournalRecovery);
        yield return ("shutdown deadline includes synchronously blocked disposal and reports errors", CleanupDeadline);
    }

    private static Task ClosingWindowGuards()
    {
        var window = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        var app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs.txt"));
        var update = window[window.IndexOf("private void UpdateTitleBarInteractiveRegions()", StringComparison.Ordinal)..];
        Check.True(update[..update.IndexOf("SetRegionRects", StringComparison.Ordinal)].Contains("if (_allowClose) return;"));
        var close = window[window.IndexOf("public void CloseForReal()", StringComparison.Ordinal)..];
        close = close[..close.IndexOf("Close();", StringComparison.Ordinal)];
        Check.True(close.Contains("if (!_preparingExit) PrepareForExit();"));
        Check.True(close.Contains("AppTitleBar.SizeChanged -= OnTitleBarSizeChanged;"));
        Check.True(close.Contains("TitleBarActions.LayoutUpdated -= OnTitleBarActionsLayoutUpdated;"));
        Check.True(close.Contains("Activated -= OnWindowActivated;"));
        Check.True(window.Contains("public void ShowFromTray()\r\n    {\r\n        if (_allowClose) return;")
            || window.Contains("public void ShowFromTray()\n    {\n        if (_allowClose) return;"));
        Check.True(app.Contains("_mainWindow.IsClosing"));
        Check.True(app.Contains("UnhandledException += (_, e) => CrashDiagnostics.Record"));
        return Task.CompletedTask;
    }

    private static Task CrashLogMetadataOnly()
    {
        using var temp = new TemporaryDirectory();
        Exception error;
        try { throw new InvalidOperationException("PRIVATE-MESSAGE-TEXT", new IOException("PRIVATE-INNER-TEXT")); }
        catch (Exception ex) { error = ex; }
        Check.True(MeetingCapture.Diagnostics.CrashDiagnostics.Record("test", error, temp.Path));
        var text = File.ReadAllText(Path.Combine(temp.Path, MeetingCapture.Diagnostics.CrashDiagnostics.FileName));
        Check.True(text.Contains("type=System.InvalidOperationException"));
        Check.True(text.Contains("type=System.IO.IOException"));
        Check.True(text.Contains(nameof(CrashLogMetadataOnly)));
        Check.True(!text.Contains("PRIVATE"));
        return Task.CompletedTask;
    }

    private static async Task SuccessfulExit()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic completed exit", CancellationToken.None);
        f.Transcription.Emit("Synthetic saved transcript");
        await f.Pipeline.StopManualAsync();
        var writes = f.Storage.Writes.Count;
        var exit = f.Pipeline.PrepareForExitAsync();
        Check.True(ReferenceEquals(exit, f.Pipeline.PrepareForExitAsync()));
        await exit;
        Check.True(!f.Pipeline.CanStart);
        Check.Equal(writes, f.Storage.Writes.Count);
        await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
        await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.StartAudioPreviewAsync(CancellationToken.None));
    }

    private static async Task FailedSave()
    {
        await using var f = new PipelineFixture();
        f.Storage.BeforeSave = (_, _) => throw new IOException("Synthetic full disk");
        await f.Pipeline.StartManualAsync("Synthetic recovery", CancellationToken.None);
        f.Transcription.Emit("Keep this synthetic transcript");
        await Check.ThrowsAsync<IOException>(() => f.Pipeline.StopManualAsync());
        await Check.ThrowsAsync<IOException>(() => f.Pipeline.PrepareForExitAsync());
        Check.Equal("Keep this synthetic transcript", f.Pipeline.LastCompletedTranscript!.FullText);
        Check.Equal(0, f.Capture.Disposals);
        Check.Equal(0, f.Transcription.Disposals);
        Check.True(f.Pipeline.CanStart);
        f.Storage.BeforeSave = null;
        // Failed exit must not leave every future AI request permanently cancelled.
        var path = await f.Pipeline.RetryIntelligenceAsync(f.Pipeline.LastCompletedTranscript.SessionId, CancellationToken.None);
        Check.True(f.Intelligence.Calls > 0);
        Check.True((await File.ReadAllTextAsync(path)).Contains("Keep this synthetic transcript"));
        await f.Pipeline.StartManualAsync("Synthetic automatic recovery", CancellationToken.None);
        f.Transcription.Emit("Recover on the next exit attempt");
        f.Storage.BeforeSave = (_, _) => throw new IOException("Synthetic failed finalization");
        await Check.ThrowsAsync<IOException>(() => f.Pipeline.StopManualAsync());
        f.Storage.BeforeSave = null;
        await f.Pipeline.PrepareForExitAsync();
        Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Last())).Contains("Recover on the next exit attempt"));
    }

    private static async Task SaveGate()
    {
        await using var f = new PipelineFixture();
        var gate = new AsyncGate();
        f.Storage.BeforeSave = (_, ct) => gate.WaitAsync(ct);
        await f.Pipeline.StartManualAsync("Synthetic flush", CancellationToken.None);
        f.Transcription.Emit("Synthetic durable text");
        var exit = f.Pipeline.PrepareForExitAsync();
        try
        {
            await gate.Entered;
            Check.True(!exit.IsCompleted && !f.Pipeline.CanStart);
            Check.True(ReferenceEquals(exit, f.Pipeline.PrepareForExitAsync()));
        }
        finally { gate.Release(); }
        await exit;
        Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Single())).Contains("Synthetic durable text"));
    }

    private static async Task UncooperativeSynthesis()
    {
        await using var f = new PipelineFixture();
        var gate = new AsyncGate();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Intelligence.OnSynthesize = async _ =>
        {
            await gate.WaitAsync(CancellationToken.None);
            returned.SetResult();
        };
        await f.Pipeline.StartManualAsync("Synthetic late model", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript before remote work");
        var stop = f.Pipeline.StopManualAsync();
        try
        {
            await gate.Entered;
            await f.Pipeline.PrepareForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await stop;
            Check.Equal(1, f.Storage.Writes.Count);
            Check.True(File.Exists(f.Storage.Paths.Single()));
        }
        finally { gate.Release(); }
        await returned.Task;
        Check.Equal(1, f.Storage.Writes.Count);
    }

    private static async Task CancelRetry()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic external retry", CancellationToken.None);
        f.Transcription.Emit("Synthetic retry transcript");
        await f.Pipeline.StopManualAsync();
        var gate = new AsyncGate();
        f.Intelligence.OnSynthesize = gate.WaitAsync;
        var retry = f.Pipeline.RetryIntelligenceAsync(f.Pipeline.LastCompletedTranscript!.SessionId, CancellationToken.None);
        try
        {
            await gate.Entered;
            await f.Pipeline.PrepareForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Check.ThrowsAsync<OperationCanceledException>(() => retry);
            await f.Pipeline.PrepareForExitAsync();
            Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Last())).Contains("Synthetic retry transcript"));
        }
        finally { gate.Release(); }
    }

    private static async Task CaptureOwnership()
    {
        await using var f = new PipelineFixture();
        await f.Pipeline.StartManualAsync("Synthetic held capture", CancellationToken.None);
        f.Transcription.Emit("Synthetic available text");
        f.Capture.OnStop = _ => throw new IOException("Synthetic device fault");
        await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.PrepareForExitAsync());
        Check.True(f.Capture.IsCapturing && !f.Pipeline.CanStart);
        Check.Equal(0, f.Capture.Disposals);
        Check.True(File.Exists(f.Storage.Paths.Single()));
    }

    private static async Task SavedPartialClosePolicy()
    {
        await using var f = new PipelineFixture(transcriptionTimeout: TimeSpan.FromMilliseconds(80));
        var pushGate = new AsyncGate();
        var review = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownershipReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transcription.OnPushAudio = pushGate.WaitAsync;
        f.Pipeline.StateChanged += (_, status) =>
        {
            if (status.State == PipelineState.ReviewingSave)
            {
                Check.True(!f.Pipeline.CanForceCloseAfterSavedPartial);
                f.Pipeline.SaveNow(status.SessionId!);
                review.TrySetResult();
            }
            if (f.Pipeline.CanStart)
                ownershipReleased.TrySetResult();
        };
        await f.Pipeline.StartManualAsync("Synthetic saved-partial close", CancellationToken.None);
        f.Transcription.Emit("Recognized before the stalled write");
        var emit = Task.Run(() => f.Capture.EmitAudio([1, 2]));
        try
        {
            await pushGate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            await emit.WaitAsync(TimeSpan.FromSeconds(1));
            await review.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await f.Pipeline.StopManualAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Check.True(File.Exists(f.Pipeline.Status.NotesPath));
            Check.True(!f.Capture.IsCapturing);
            Check.True(f.Pipeline.CanForceCloseAfterSavedPartial);
            Check.True(!ExitReadiness.ShouldCloseAfterSavedPartial(false, true));
            Check.True(!ExitReadiness.ShouldCloseAfterSavedPartial(true, false));
            Check.True(ExitReadiness.ShouldCloseAfterSavedPartial(true, true));

            pushGate.Release();
            await f.Transcription.PushesSettled.WaitAsync(TimeSpan.FromSeconds(2));
            await ownershipReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retryGate = new AsyncGate();
            f.Intelligence.OnSynthesize = retryGate.WaitAsync;
            var retry = f.Pipeline.RetryIntelligenceAsync(
                f.Pipeline.LastCompletedTranscript!.SessionId, CancellationToken.None);
            await retryGate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            Check.True(!f.Pipeline.CanForceCloseAfterSavedPartial);
            retryGate.Release();
            await retry.WaitAsync(TimeSpan.FromSeconds(2));

            await f.Pipeline.PrepareForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(!f.Pipeline.CanForceCloseAfterSavedPartial);
        }
        finally
        {
            pushGate.Release();
        }
    }

    /// <summary>
    /// Reproduces the blocked safe exit: a durable transcript plus a visualization sidecar whose
    /// retry keeps failing must never make the app permanently unexitable.
    /// </summary>
    private static async Task PendingSidecarNeverBlocksExit()
    {
        var visualizationStorage = new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage())
        {
            BeforeSave = (save, _) => save >= 2
                ? Task.FromException(new InvalidOperationException("Synthetic invalid visualization."))
                : Task.CompletedTask
        };
        await using var f = new PipelineFixture(visualizationStorage: visualizationStorage);
        await f.Pipeline.StartManualAsync("Synthetic pending sidecar exit", CancellationToken.None);
        f.Transcription.Emit("Synthetic durable transcript for exit");
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(f.Pipeline.Status.Message.Contains("visualization data is pending retry", StringComparison.OrdinalIgnoreCase));
        var markdown = f.Storage.Paths.Last();
        var sidecar = Path.ChangeExtension(markdown, ".json");
        Check.True(File.Exists(markdown) && File.Exists(sidecar));
        Check.Equal(MeetingVisualizationLoadKind.StaleMismatch,
            (await f.VisualizationStorage.LoadAsync(markdown, CancellationToken.None)).Kind);

        var readiness = await f.Pipeline.PrepareForExitAsync();

        // Exit succeeded: transcript durable, capture released, optional work reported not claimed.
        Check.Equal(ExitDeferralKind.VisualizationRetryKept, readiness.Deferral);
        Check.Equal(markdown, readiness.TranscriptPath);
        Check.True(readiness.HasDeferredWork);
        Check.True(readiness.Message!.Contains("retried automatically", StringComparison.Ordinal));
        Check.True(!readiness.Message.Contains("Synthetic durable transcript", StringComparison.Ordinal));
        Check.True(!f.Capture.IsCapturing && !f.Pipeline.CanStart);
        Check.Equal(3, visualizationStorage.Saves);
        Check.True((await File.ReadAllTextAsync(markdown)).Contains("Synthetic durable transcript for exit"));
        // Exit stays single-flight and never reverts to the blocked state.
        Check.True(ReferenceEquals(f.Pipeline.PrepareForExitAsync(), f.Pipeline.PrepareForExitAsync()));
        Check.Equal(ExitDeferralKind.VisualizationRetryKept, (await f.Pipeline.PrepareForExitAsync()).Deferral);

        // The retry was retained, not discarded: the next launch recovers it exactly once.
        var next = new JsonMeetingVisualizationStorage();
        Check.Equal(1, (await f.Journal.DrainAsync(next, CancellationToken.None)).Count);
        Check.True(File.Exists(sidecar));
        Check.Equal(MeetingVisualizationLoadKind.Valid,
            (await next.LoadAsync(markdown, CancellationToken.None)).Kind);
        Check.Equal(0, (await f.Journal.DrainAsync(next, CancellationToken.None)).Count);
    }

    /// <summary>Unreleased capture stays a hard exit blocker even when a sidecar is also pending.</summary>
    private static async Task CaptureOwnershipOutranksPendingSidecar()
    {
        var visualizationStorage = new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage())
        {
            FailuresRemaining = 5
        };
        await using var f = new PipelineFixture(visualizationStorage: visualizationStorage);
        await f.Pipeline.StartManualAsync("Synthetic held capture", CancellationToken.None);
        f.Transcription.Emit("Synthetic text held by capture");
        f.Capture.OnStop = _ => throw new IOException("Synthetic device fault");

        await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.PrepareForExitAsync());

        Check.True(f.Capture.IsCapturing && !f.Pipeline.CanStart);
        Check.Equal(0, f.Capture.Disposals);
        Check.True(File.Exists(f.Storage.Paths.Single()));
        // Deferral must never be used to paper over capture that was not released.
        Check.Equal(0, Directory.Exists(f.Journal.Root)
            ? Directory.EnumerateFiles(f.Journal.Root, "*.json").Count() : 0);
        // Retrying exit still refuses while capture is held.
        await Check.ThrowsAsync<InvalidOperationException>(() => f.Pipeline.PrepareForExitAsync());
    }

    private static async Task DeferredVisualizationJournalRecovery()
    {
        using var directory = new TemporaryDirectory();
        var diagnostics = new AiDiagnosticsStream();
        var journal = new FilePendingVisualizationJournal(
            Path.Combine(directory.Path, "journal"), diagnostics);
        var markdown = Path.Combine(directory.Path, "meeting.md");
        await File.WriteAllTextAsync(markdown, "# Synthetic transcript");
        var session = new MeetingCapture.Intelligence.MeetingVisualizationSession();
        session.Begin("session-journal", "Synthetic", DateTimeOffset.UtcNow);
        session.AppendOrUpdateSegment(1, new("Synthetic segment", DateTimeOffset.UtcNow));
        var document = session.Snapshot();

        await journal.RecordAsync(markdown, document, CancellationToken.None);
        await journal.RecordAsync(markdown, document, CancellationToken.None);
        Check.Equal(1, Directory.EnumerateFiles(journal.Root, "*.json").Count());

        var failing = new ObservedVisualizationStorage(new JsonMeetingVisualizationStorage())
        {
            FailuresRemaining = 1
        };
        Check.Equal(0, (await journal.DrainAsync(failing, CancellationToken.None)).Count);
        var failedRetry = diagnostics.GetSnapshot().Events.Single(item =>
            item.Stage == AiDiagnosticStage.CompanionJsonRetry &&
            item.RetryOutcome == AiDiagnosticRetryOutcome.RetryFailed);
        Check.Equal(nameof(IOException), failedRetry.ExceptionType);
        Check.Equal(1, Directory.EnumerateFiles(journal.Root, "*.json").Count());
        Check.Equal(1, (await journal.DrainAsync(failing, CancellationToken.None)).Count);
        Check.True(diagnostics.GetSnapshot().Events.Any(item =>
            item.Stage == AiDiagnosticStage.CompanionJsonRetry &&
            item.RetryOutcome == AiDiagnosticRetryOutcome.RetrySucceeded));
        Check.Equal(0, Directory.EnumerateFiles(journal.Root, "*.json").Count());

        // A journaled retry whose transcript no longer exists cannot be attached to anything.
        await journal.RecordAsync(markdown, document, CancellationToken.None);
        File.Delete(markdown);
        File.Delete(Path.ChangeExtension(markdown, ".json"));
        Check.Equal(0, (await journal.DrainAsync(failing, CancellationToken.None)).Count);
        Check.Equal(0, Directory.EnumerateFiles(journal.Root, "*.json").Count());
    }

    private static async Task CleanupDeadline()
    {
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        try
        {
            await Check.ThrowsAsync<TimeoutException>(() => ShutdownCleanup.RunAsync(() =>
            {
                release.Wait();
                finished.SetResult();
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(50)));
            Check.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        await finished.Task;
        await Check.ThrowsAsync<IOException>(() =>
            ShutdownCleanup.RunAsync(() => throw new IOException("Synthetic dispose fault"), TimeSpan.FromSeconds(1)));
    }
}
