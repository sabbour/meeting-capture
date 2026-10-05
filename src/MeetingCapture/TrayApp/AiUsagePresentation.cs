using System.Globalization;
using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

internal enum AiActivityVisualState
{
    Unavailable,
    NoActivity,
    Active,
    Idle,
    Failed,
    RateLimited,
}

internal sealed record AiUsageUiPresentation(
    AiActivityVisualState State,
    string Header,
    string Status,
    string Announcement,
    string TransitionKey);

internal static class AiUsagePresentation
{
    internal static AiUsageUiPresentation Create(AiUsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var active = snapshot.Requests.FirstOrDefault(request =>
            request.State == AiRequestState.Running);
        var latest = active ?? snapshot.Requests.FirstOrDefault();
        var state = State(snapshot, latest);
        var capability = latest is null ? string.Empty : CapabilityLabel(latest.Capability);
        var headerCapability = latest is null ? string.Empty : HeaderCapabilityLabel(latest.Capability);
        var header = Header(state, headerCapability, active?.Elapsed);
        var status = Status(state, capability, latest);
        return new(
            state,
            header,
            status,
            status,
            $"{state}:{latest?.Capability}:{latest?.Failure}");
    }

    private static AiActivityVisualState State(
        AiUsageSnapshot snapshot,
        AiRequestUsage? latest)
    {
        if (snapshot.SessionId is null)
            return AiActivityVisualState.Unavailable;
        if (latest is null)
            return AiActivityVisualState.NoActivity;
        if (latest.State == AiRequestState.Running)
            return AiActivityVisualState.Active;
        if (latest.State == AiRequestState.RateLimited)
            return AiActivityVisualState.RateLimited;
        if (latest.State == AiRequestState.Failed)
            return latest.Failure is
                AiRequestFailure.Configuration or
                AiRequestFailure.Authentication or
                AiRequestFailure.Authorization or
                AiRequestFailure.ModelUnavailable or
                AiRequestFailure.QuotaExceeded or
                AiRequestFailure.Unsupported
                ? AiActivityVisualState.Unavailable
                : AiActivityVisualState.Failed;
        return AiActivityVisualState.Idle;
    }

    private static string Header(
        AiActivityVisualState state,
        string capability,
        TimeSpan? elapsed) =>
        state switch
        {
            AiActivityVisualState.Active =>
                $"AI active · {capability} · {Elapsed(elapsed ?? TimeSpan.Zero)}",
            AiActivityVisualState.RateLimited => $"AI rate limited · {capability}",
            AiActivityVisualState.Failed => $"AI error · {capability}",
            AiActivityVisualState.Unavailable when capability.Length > 0 =>
                $"AI unavailable · {capability}",
            AiActivityVisualState.Unavailable => "AI unavailable",
            AiActivityVisualState.Idle when capability.Length > 0 => $"AI idle · {capability}",
            _ => "AI idle · No activity",
        };

    private static string Status(
        AiActivityVisualState state,
        string capability,
        AiRequestUsage? request) =>
        state switch
        {
            AiActivityVisualState.Active => $"AI is active: {capability}.",
            AiActivityVisualState.RateLimited when request?.RetryAfter is { } retry =>
                $"AI is rate limited for {capability}. Retry after {Retry(retry)}.",
            AiActivityVisualState.RateLimited => $"AI is rate limited for {capability}.",
            AiActivityVisualState.Failed => $"AI activity failed for {capability}.",
            AiActivityVisualState.Unavailable when capability.Length > 0 =>
                $"AI is unavailable for {capability}.",
            AiActivityVisualState.Unavailable => "AI activity is unavailable.",
            AiActivityVisualState.Idle when capability.Length > 0 =>
                $"AI is idle. Last activity: {capability}.",
            _ => "No AI activity has occurred in this recording.",
        };

    internal static string TotalNanoAiuMetric(double? value) =>
        value is null ? "Total Nano AIUs: Not reported" : TotalNanoAiuMetric(value.Value);

    private static string TotalNanoAiuMetric(double value)
    {
        const decimal nanoAiusPerAic = 1_000_000_000m;
        const decimal usdPerAic = 0.01m;
        var rawNanoAius = value.ToString("G", CultureInfo.CurrentCulture);
        var aic = (decimal)value / nanoAiusPerAic;
        var usd = aic * usdPerAic;
        return $"Total Nano AIUs: {rawNanoAius} · AIC equivalent: " +
            $"{aic:0.############################} AIC · AIC USD equivalent: " +
            $"${usd:0.############################} USD";
    }

    private static string CapabilityLabel(AiCapability capability) => capability switch
    {
        AiCapability.Synthesis => "Notes",
        AiCapability.Intelligence => "Questions and actions",
        AiCapability.Visualization => "Topic analysis",
        AiCapability.CatchUp => "Catch me up",
        AiCapability.WorkIqRetrieval => "Related suggestions",
        _ => "AI",
    };

    private static string HeaderCapabilityLabel(AiCapability capability) => capability switch
    {
        AiCapability.Intelligence => "Q&A",
        AiCapability.Visualization => "Topics",
        AiCapability.WorkIqRetrieval => "Related",
        _ => CapabilityLabel(capability),
    };

    private static string Elapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";

    private static string Retry(TimeSpan retry) =>
        retry.TotalMinutes >= 1
            ? $"{Math.Ceiling(retry.TotalMinutes):0} min"
            : $"{Math.Max(1, Math.Ceiling(retry.TotalSeconds)):0} sec";
}
