using MeetingCapture.AudioCapture;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Pipeline;
using MeetingCapture.Transcription;

namespace MeetingCapture.Regression;

/// <summary>
/// Covers the reproduced startup failure (a retained visualization retry silently blocking every new
/// recording behind one generic message) and its sibling typed startup failures.
/// </summary>
internal static class StartupFailureChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("startup retained visualization retry blocks recording with actionable guidance",
            PendingVisualizationBlocksStart);
        yield return ("startup device faults are typed per source instead of one generic message",
            AudioSourceFailuresAreTyped);
        yield return ("startup speech faults are typed, sanitized, and actionable", SpeechFailuresAreTyped);
        yield return ("startup guard faults are typed and never report false success", GuardFailuresAreTyped);
        yield return ("startup retained capture ownership outranks other guidance", RetainedOwnershipGuidance);
    }

    private static async Task PendingVisualizationBlocksStart()
    {
        var storage = new ObservedVisualizationStorage(new MeetingCapture.Storage.JsonMeetingVisualizationStorage())
        {
            BeforeSave = (save, _) => save >= 2
                ? Task.FromException(new IOException("Synthetic visualization failure."))
                : Task.CompletedTask
        };
        await using var f = new PipelineFixture(visualizationStorage: storage);
        await f.Pipeline.StartManualAsync("Synthetic pending visualization", CancellationToken.None);
        f.Transcription.Emit("Synthetic transcript retained for retry");
        await f.Pipeline.StopManualAsync();
        Check.Equal(PipelineState.Saved, f.Pipeline.State);
        Check.True(f.Pipeline.Status.Message.Contains("visualization data is pending retry",
            StringComparison.OrdinalIgnoreCase));
        Check.True(!f.Pipeline.CanStart);

        var blocked = await Check.ThrowsAsync<RecordingStartException>(
            () => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
        Check.Equal(RecordingStartFailureKind.PendingVisualizationRetry, blocked.Kind);
        Check.Equal(blocked.Failure, f.Pipeline.DescribeStartFailure(blocked));
        Check.True(blocked.Failure.Message.Contains("Retry AI", StringComparison.Ordinal));
        Check.True(blocked.Failure.Message.Contains("visualization", StringComparison.OrdinalIgnoreCase));
        Check.True(!blocked.Failure.Message.Contains("Check audio and transcription settings", StringComparison.Ordinal));
        // No false success: nothing started, and the block survives a second attempt.
        Check.True(!f.Pipeline.IsRunning && f.Pipeline.State == PipelineState.Saved);
        Check.Equal(1, f.Capture.Starts);
        Check.Equal(RecordingStartFailureKind.PendingVisualizationRetry,
            (await Check.ThrowsAsync<RecordingStartException>(
                () => f.Pipeline.StartManualAsync("Rejected again", CancellationToken.None))).Kind);
    }

    private static async Task AudioSourceFailuresAreTyped()
    {
        var expectations = new[]
        {
            (Microphone: true, Loopback: false, Kind: RecordingStartFailureKind.MicrophoneUnavailable),
            (Microphone: false, Loopback: true, Kind: RecordingStartFailureKind.LoopbackUnavailable),
            (Microphone: true, Loopback: true, Kind: RecordingStartFailureKind.AudioUnavailable)
        };
        foreach (var (microphoneFails, loopbackFails, kind) in expectations)
        {
            var loopback = new FakeCapture { Source = "loopback" };
            await using var f = new PipelineFixture(loopback);
            Func<CancellationToken, Task> fail = _ => throw new IOException("Synthetic device fault");
            if (microphoneFails) { f.Capture.OnStart = fail; f.Capture.HealthState = CaptureHealthState.Failed; }
            if (loopbackFails) { loopback.OnStart = fail; loopback.HealthState = CaptureHealthState.Failed; }
            var error = await Check.ThrowsAsync<IOException>(
                () => f.Pipeline.StartManualAsync("Synthetic device failure", CancellationToken.None));
            var failure = f.Pipeline.DescribeStartFailure(error);
            Check.Equal(kind, failure.Kind);
            Check.Equal(failure.Message, f.Pipeline.Status.Error);
            Check.Equal(PipelineState.Failed, f.Pipeline.State);
            Check.True(!f.Pipeline.IsRunning && f.Pipeline.CanStart);
        }
    }

    private static async Task SpeechFailuresAreTyped()
    {
        var expectations = new[]
        {
            (Kind: TranscriptionStartFailureKind.CredentialsMissing, Expected: RecordingStartFailureKind.SpeechCredentialsMissing),
            (Kind: TranscriptionStartFailureKind.CredentialsRejected, Expected: RecordingStartFailureKind.SpeechCredentialsRejected),
            (Kind: TranscriptionStartFailureKind.ConfigurationInvalid, Expected: RecordingStartFailureKind.SpeechConfigurationInvalid),
            (Kind: TranscriptionStartFailureKind.ServiceUnreachable, Expected: RecordingStartFailureKind.SpeechUnreachable),
            (Kind: TranscriptionStartFailureKind.Unknown, Expected: RecordingStartFailureKind.SpeechStartFailed)
        };
        foreach (var (speechKind, expected) in expectations)
        {
            await using var f = new PipelineFixture();
            f.Transcription.OnStart = _ => throw new TranscriptionStartException(
                speechKind, "Synthetic speech failure", new IOException("wss://synthetic-secret-endpoint"));
            var error = await Check.ThrowsAsync<TranscriptionStartException>(
                () => f.Pipeline.StartManualAsync("Synthetic speech failure", CancellationToken.None));
            var failure = f.Pipeline.DescribeStartFailure(error);
            Check.Equal(expected, failure.Kind);
            Check.Equal(failure.Message, f.Pipeline.Status.Error);
            Check.True(!failure.Message.Contains("synthetic-secret-endpoint", StringComparison.OrdinalIgnoreCase));
            Check.Equal(PipelineState.Failed, f.Pipeline.State);
            Check.True(!f.Pipeline.IsRunning);
            // Rollback still permits an immediate clean retry.
            f.Transcription.OnStart = null;
            await f.Pipeline.StartManualAsync("Synthetic clean retry", CancellationToken.None);
            await f.Pipeline.StopManualAsync();
            Check.Equal(PipelineState.Saved, f.Pipeline.State);
        }
        Check.Equal(RecordingStartFailureKind.SpeechCredentialsMissing, RecordingStartFailures.Classify(
            new TranscriptionStartException(TranscriptionStartFailureKind.CredentialsMissing, "Synthetic"),
            [], null).Kind);
    }

    private static async Task GuardFailuresAreTyped()
    {
        await using var f = new PipelineFixture();
        var ineligible = new MeetingTitleCandidate("Synthetic offline item",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), null, null);
        var calendar = await Check.ThrowsAsync<RecordingStartException>(
            () => f.Pipeline.StartManualAsync("Rejected", ineligible, CancellationToken.None));
        Check.Equal(RecordingStartFailureKind.IneligibleCalendarMeeting, calendar.Kind);
        Check.Equal(PipelineState.Idle, f.Pipeline.State);

        var startup = new AsyncGate();
        f.Capture.OnStart = startup.WaitAsync;
        var start = f.Pipeline.StartManualAsync("Synthetic guard", CancellationToken.None);
        try
        {
            await startup.Entered;
            var busy = await Check.ThrowsAsync<RecordingStartException>(
                () => f.Pipeline.StartManualAsync("Rejected", CancellationToken.None));
            Check.Equal(RecordingStartFailureKind.AlreadyInProgress, busy.Kind);
            Check.Equal(busy.Failure, f.Pipeline.DescribeStartFailure(busy));
        }
        finally { startup.Release(); }
        await start;

        var stop = f.Pipeline.StopManualAsync();
        await stop;
        f.Capture.OnStart = startup.WaitAsync;
        var canceled = new PipelineFixture();
        await using (canceled)
        {
            canceled.Capture.OnStart = new AsyncGate().WaitAsync;
            var pending = canceled.Pipeline.StartManualAsync("Synthetic canceled", CancellationToken.None);
            var stopping = canceled.Pipeline.StopManualAsync();
            var cancellation = await Check.ThrowsAsync<OperationCanceledException>(() => pending);
            await stopping;
            Check.Equal(RecordingStartFailureKind.Canceled,
                canceled.Pipeline.DescribeStartFailure(cancellation).Kind);
            Check.Equal(PipelineState.Failed, canceled.Pipeline.State);
            Check.True(canceled.Pipeline.CanStart);
        }
    }

    private static async Task RetainedOwnershipGuidance()
    {
        await using var f = new PipelineFixture();
        f.Transcription.RetainResources = true;
        f.Transcription.OnStart = _ => throw new IOException("Synthetic retained ownership fault");
        var error = await Check.ThrowsAsync<IOException>(
            () => f.Pipeline.StartManualAsync("Synthetic retained", CancellationToken.None));
        var failure = f.Pipeline.DescribeStartFailure(error);
        Check.Equal(RecordingStartFailureKind.RetainedCaptureOwnership, failure.Kind);
        Check.True(failure.Message.Contains("Restart", StringComparison.OrdinalIgnoreCase));
        Check.Equal(failure.Message, f.Pipeline.Status.Error);
        Check.True(!f.Pipeline.CanStart);
        f.Transcription.RetainResources = false;
    }
}