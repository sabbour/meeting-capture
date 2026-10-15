using System.Globalization;
using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

/// <summary>One AI diagnostic event as display text.</summary>
internal sealed record AiDiagnosticEventPresentation(
    long Sequence,
    string Time,
    string Capability,
    string ProviderModel,
    string Status,
    string Duration,
    string RequestLabel,
    string RequestIdentity,
    string Usage,
    string Quota,
    string? PromptPayload,
    string? RequestPayload,
    string? ResponsePayload,
    bool HasSensitivePayload,
    bool PayloadTruncated,
    string AutomationName);

/// <summary>Turns AI diagnostic events into display text.</summary>
internal static class AiDiagnosticsPresentation
{
    internal static AiDiagnosticEventPresentation Event(AiDiagnosticEvent value)
    {
        if (value.Stage is { } stage)
        {
            var persistenceStatus = string.Join(" · ", new[]
            {
                stage.ToString(),
                value.ExceptionType,
                value.ErrorCode is { } errorCode ? $"0x{unchecked((uint)errorCode):X8}" : null,
                value.RetryOutcome?.ToString()
            }.Where(item => item is not null));
            return new(
                value.Sequence,
                string.Empty,
                stage.ToString(),
                string.Empty,
                persistenceStatus,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null,
                null,
                false,
                false,
                persistenceStatus);
        }

        var capability = value.Capability switch
        {
            AiCapability.Synthesis => "Notes synthesis",
            AiCapability.Intelligence => "Questions and actions",
            AiCapability.Visualization => "Topic analysis",
            AiCapability.CatchUp => "Catch me up",
            AiCapability.WorkIqRetrieval => "Related suggestions",
            _ => value.Capability.ToString(),
        };
        var providerModel = string.IsNullOrWhiteSpace(value.Model)
            ? $"{value.Provider} · Model not reported"
            : $"{value.Provider} · {value.Model}";
        var status = $"{value.Kind} · {value.State}";
        if (value.Failure != AiRequestFailure.None)
            status += $" · {value.Failure}";
        if (value.RetryAfter is { } retry)
            status += $" · retry after {Duration(retry)}";
        var requestIdentity = $"Request {value.RequestId} · Correlation {value.CorrelationId}";
        if (value.RequestRevision is { } revision)
            requestIdentity += $" · Revision {revision}";
        var usage = string.Join(" · ",
            $"Calls {value.CallsStarted}",
            Metric("Input tokens", value.InputTokens),
            Metric("Output tokens", value.OutputTokens),
            Metric("Cached read tokens", value.CacheReadTokens),
            Metric("Cached write tokens", value.CacheWriteTokens),
            Metric("Reasoning tokens", value.ReasoningTokens),
            Metric("Provider cost (SDK-reported)", value.ProviderCost),
            "Calculated cost: Not reported (required token rates are not reported)",
            AiUsagePresentation.TotalNanoAiuMetric(value.TotalNanoAiu));
        var quota = value.QuotaSnapshots.Count == 0
            ? "Quota: Not reported"
            : "Quota: " + string.Join("; ", value.QuotaSnapshots.Select(item =>
                $"{item.QuotaId}: {item.UsedRequests}/{item.EntitlementRequests} requests, " +
                $"{item.RemainingPercentage.ToString("0.##", CultureInfo.CurrentCulture)}% remaining" +
                (item.ResetDate is { } reset ? $", resets {reset.ToLocalTime():g}" : string.Empty)));
        var hasSensitivePayload =
            value.SensitivePromptPayload is not null ||
            value.SensitiveRequestPayload is not null ||
            value.SensitiveResponsePayload is not null;
        var time = value.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
        return new(
            value.Sequence,
            time,
            capability,
            providerModel,
            status,
            Duration(value.Duration),
            $"Request {value.RequestId}",
            requestIdentity,
            usage,
            quota,
            value.SensitivePromptPayload,
            value.SensitiveRequestPayload,
            value.SensitiveResponsePayload,
            hasSensitivePayload,
            value.SensitivePayloadTruncated,
            $"{time}. {capability}. {providerModel}. {status}. {Duration(value.Duration)}.");
    }

    private static string Metric(string label, long? value) =>
        value is null ? $"{label}: Not reported" : $"{label}: {value.Value:N0}";

    private static string Metric(string label, double? value) =>
        value is null
            ? $"{label}: Not reported"
            : $"{label}: {value.Value.ToString("G", CultureInfo.CurrentCulture)}";

    private static string Duration(TimeSpan value) =>
        value.TotalSeconds >= 1
            ? $"{value.TotalSeconds:0.##} s"
            : $"{Math.Max(0, value.TotalMilliseconds):0} ms";
}
