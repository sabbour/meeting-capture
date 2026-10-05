using MeetingCapture.Intelligence;

namespace MeetingCapture.Regression;

internal static class AiDiagnosticsChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("AI diagnostics redact content by default", DefaultRedaction);
        yield return ("AI diagnostics capture raw session content only when enabled", OptInCapture);
        yield return ("AI diagnostics always remove secrets and auth headers", SecretExclusion);
        yield return ("AI diagnostics sanitize sensitive prompt fields", PromptSecretExclusion);
        yield return ("AI diagnostics evict oldest bounded events", BoundedEviction);
        yield return ("AI diagnostics clear reset and reject stale completion", ClearAndReset);
        yield return ("AI diagnostics complete concurrent requests safely", ConcurrentCompletion);
        yield return ("AI diagnostics classify persistence failures without retaining error text", PersistenceFailureMetadata);
    }

    private static Task DefaultRedaction()
    {
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");
        var request = stream.Begin(
            AiCapability.Intelligence, "GitHub Copilot", "test-model",
            7, "private prompt");
        request.CallStarted();
        request.Complete(true, "private response");

        var snapshot = stream.GetSnapshot();
        Check.True(!snapshot.SensitiveContentCaptureEnabled);
        Check.Equal(3, snapshot.Events.Count);
        Check.True(snapshot.Events.All(item =>
            item.SensitivePromptPayload is null &&
            item.SensitiveRequestPayload is null &&
            item.SensitiveResponsePayload is null));
        Check.Equal(7L, snapshot.Events[0].RequestRevision);
        Check.Equal(AiRequestState.Succeeded, snapshot.Events[^1].State);
        return Task.CompletedTask;
    }

    private static Task OptInCapture()
    {
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");
        stream.SetSensitiveContentCapture(true);
        var request = stream.Begin(
            AiCapability.Visualization, "GitHub Copilot", "test-model",
            sensitiveRequestPayload: "{ malformed request",
            sensitivePromptPayload: "untrusted transcript is data");
        request.Complete(false, "{ malformed response");

        var snapshot = stream.GetSnapshot();
        Check.True(snapshot.SensitiveContentCaptureEnabled);
        Check.Equal("untrusted transcript is data", snapshot.Events[0].SensitivePromptPayload);
        Check.Equal("{ malformed request", snapshot.Events[0].SensitiveRequestPayload);
        Check.Equal("{ malformed response", snapshot.Events[^1].SensitiveResponsePayload);
        stream.SetSensitiveContentCapture(false);
        Check.True(stream.GetSnapshot().Events.All(item =>
            item.SensitivePromptPayload is null &&
            item.SensitiveRequestPayload is null &&
            item.SensitiveResponsePayload is null));
        return Task.CompletedTask;
    }

    private static Task SecretExclusion()
    {
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");
        stream.SetSensitiveContentCapture(true);
        var request = stream.Begin(
            AiCapability.Synthesis,
            "GitHub Copilot",
            "test-model",
            sensitiveRequestPayload:
                "Authorization: Bearer request-secret\napiKey=request-key\nprompt=allowed");
        request.Complete(true,
            """{"access_token":"response-secret","answer":"allowed"}""");

        var payloads = stream.GetSnapshot().Events
            .SelectMany(item => new[]
            {
                item.SensitiveRequestPayload,
                item.SensitiveResponsePayload
            })
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
        Check.True(payloads.All(item =>
            !item.Contains("request-secret", StringComparison.Ordinal) &&
            !item.Contains("request-key", StringComparison.Ordinal) &&
            !item.Contains("response-secret", StringComparison.Ordinal)));
        Check.True(payloads.Any(item => item.Contains("[REDACTED]", StringComparison.Ordinal)));
        return Task.CompletedTask;
    }

    private static Task PromptSecretExclusion()
    {
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");
        stream.SetSensitiveContentCapture(true);
        var request = stream.Begin(
            AiCapability.Intelligence,
            "GitHub Copilot",
            "test-model",
            sensitivePromptPayload:
                "{\"client_secret\":\"prompt-sensitive-value\",\"instruction\":\"allowed\"}");
        request.Complete(true);

        var prompt = stream.GetSnapshot().Events[0].SensitivePromptPayload;
        Check.True(prompt is not null);
        Check.True(!prompt.Contains("prompt-sensitive-value", StringComparison.Ordinal));
        Check.True(prompt.Contains("[REDACTED]", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task BoundedEviction()
    {
        var stream = new AiDiagnosticsStream(3);
        stream.Reset("session-a");
        for (var index = 0; index < 4; index++)
        {
            var request = stream.Begin(AiCapability.Intelligence, "provider", null);
            request.Complete(true);
        }
        var events = stream.GetSnapshot().Events;
        Check.Equal(3, events.Count);
        Check.True(events.Select(item => item.Sequence).SequenceEqual([6L, 7L, 8L]));
        return Task.CompletedTask;
    }

    private static Task ClearAndReset()
    {
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");
        stream.SetSensitiveContentCapture(true);
        var stale = stream.Begin(AiCapability.CatchUp, "provider", null);
        stream.Clear();
        stale.Complete(true, "late response");
        Check.Equal(0, stream.GetSnapshot().Events.Count);

        stream.Reset("session-b");
        var snapshot = stream.GetSnapshot();
        Check.Equal("session-b", snapshot.SessionId);
        Check.True(!snapshot.SensitiveContentCaptureEnabled);
        Check.Equal(0, snapshot.Events.Count);
        return Task.CompletedTask;
    }

    private static async Task ConcurrentCompletion()
    {
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");
        var requests = Enumerable.Range(0, 32)
            .Select(_ => stream.Begin(AiCapability.Intelligence, "provider", "model"))
            .ToArray();
        await Task.WhenAll(requests.Select((request, index) => Task.Run(() =>
        {
            request.CallStarted();
            request.ReportUsage(index + 1, 1, null, null, null);
            request.Complete(true);
        })));

        var events = stream.GetSnapshot().Events;
        Check.Equal(32, events.Where(item =>
            item.Kind == AiDiagnosticEventKind.Completed).Count());
        Check.Equal(32, events.Where(item =>
            item.Kind == AiDiagnosticEventKind.Completed)
            .Select(item => item.RequestId).Distinct().Count());
        Check.True(events.Where(item => item.Kind == AiDiagnosticEventKind.Completed)
            .All(item => item.State == AiRequestState.Succeeded &&
                item.CallsStarted == 1 &&
                item.InputTokens is > 0));
    }

    private static Task PersistenceFailureMetadata()
    {
        const string privateError = "private meeting transcript and path";
        var stream = new AiDiagnosticsStream();
        stream.Reset("session-a");

        var persistenceFailure = new IOException(privateError);
        stream.RecordPersistence(
            AiDiagnosticStage.GeneratedMarkdownRewrite,
            persistenceFailure,
            AiDiagnosticRetryOutcome.RetryAvailable);
        var request = stream.Begin(AiCapability.Synthesis, "provider", "model");
        request.Fail(
            AiRequestFailure.Unknown,
            stage: AiDiagnosticStage.CopilotRequest,
            exception: new InvalidOperationException(privateError),
            retryOutcome: AiDiagnosticRetryOutcome.RetryAvailable);

        var events = stream.GetSnapshot().Events;
        var rewrite = events.Single(item => item.Stage == AiDiagnosticStage.GeneratedMarkdownRewrite);
        Check.Equal(AiDiagnosticEventKind.Persistence, rewrite.Kind);
        Check.Equal(nameof(IOException), rewrite.ExceptionType);
        Check.Equal(persistenceFailure.HResult, rewrite.ErrorCode);
        Check.Equal(AiDiagnosticRetryOutcome.RetryAvailable, rewrite.RetryOutcome);
        var provider = events.Single(item => item.Stage == AiDiagnosticStage.CopilotRequest);
        Check.Equal(nameof(InvalidOperationException), provider.ExceptionType);
        Check.True(events.All(item =>
            item.SensitivePromptPayload is null &&
            item.SensitiveRequestPayload is null &&
            item.SensitiveResponsePayload is null &&
            !(item.ExceptionType?.Contains(privateError, StringComparison.Ordinal) ?? false)));

        var presentation = MeetingCapture.TrayApp.AiDiagnosticsPresentation.Event(
            provider with
            {
                SessionId = privateError,
                CorrelationId = privateError,
                Provider = privateError,
                Model = privateError,
                SensitivePromptPayload = privateError,
                SensitiveRequestPayload = privateError,
                SensitiveResponsePayload = privateError
            });
        var visible = string.Join("|",
            presentation.Time,
            presentation.Capability,
            presentation.ProviderModel,
            presentation.Status,
            presentation.Duration,
            presentation.RequestLabel,
            presentation.RequestIdentity,
            presentation.Usage,
            presentation.Quota,
            presentation.PromptPayload,
            presentation.RequestPayload,
            presentation.ResponsePayload,
            presentation.AutomationName);
        Check.True(!visible.Contains(privateError, StringComparison.Ordinal));
        Check.True(visible.Contains(nameof(AiDiagnosticStage.CopilotRequest), StringComparison.Ordinal));
        Check.True(visible.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Check.True(visible.Contains("0x", StringComparison.Ordinal));
        Check.True(visible.Contains(nameof(AiDiagnosticRetryOutcome.RetryAvailable), StringComparison.Ordinal));
        Check.True(!presentation.HasSensitivePayload);
        return Task.CompletedTask;
    }
}
