using System.Net;

namespace MeetingCapture.Intelligence;

/// <summary>Only fixed messages cross the provider boundary; raw exceptions may contain prompts or keys.</summary>
public static class IntelligenceErrors
{
    public static IntelligenceException Create(
        IntelligenceFailureKind kind,
        string provider,
        string? model = null,
        TimeSpan? retryAfter = null)
        => new(new IntelligenceFailure(kind, provider, model, Message(kind, retryAfter), retryAfter));

    public static string Message(IntelligenceFailureKind kind, TimeSpan? retryAfter = null) => kind switch
    {
        IntelligenceFailureKind.Configuration => "Configuration is incomplete or invalid. Check the selected provider and model settings.",
        IntelligenceFailureKind.Authentication => "Authentication failed. Check credentials or sign in again.",
        IntelligenceFailureKind.Authorization => "Access denied. Check account permissions and consent.",
        IntelligenceFailureKind.ModelUnavailable => "Model unavailable. Select and test a supported model or deployment in Settings.",
        IntelligenceFailureKind.RateLimited => retryAfter is { } delay && delay > TimeSpan.Zero
            ? $"Copilot rate limit reached. Retry {RetryText(delay)}."
            : "Copilot rate limit reached. Retry later or select another available model.",
        IntelligenceFailureKind.QuotaExceeded => "Copilot quota is unavailable or exhausted. Check plan and model access before retrying.",
        IntelligenceFailureKind.Network => "The service could not be reached. Check the network and retry.",
        IntelligenceFailureKind.Timeout => "The service did not finish before the timeout. Retry when ready.",
        IntelligenceFailureKind.EmptyResponse => "The service returned no content. Retry the request.",
        IntelligenceFailureKind.MalformedResponse => "The service returned an invalid response. Retry the request.",
        IntelligenceFailureKind.Disabled => "Microsoft Graph is not configured. Configure Graph and restart to enable Related.",
        IntelligenceFailureKind.SignedOut => "Sign in to Microsoft Graph to enable Related.",
        _ => "The service request failed. Retry or check the provider configuration."
    };

    public static IntelligenceException FromException(Exception exception, string provider, string? model = null)
    {
        if (exception is IntelligenceException failure)
            return failure;
        var kind = exception switch
        {
            TimeoutException or OperationCanceledException => IntelligenceFailureKind.Timeout,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => IntelligenceFailureKind.Authentication,
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => IntelligenceFailureKind.Authorization,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => IntelligenceFailureKind.RateLimited,
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout } => IntelligenceFailureKind.Timeout,
            HttpRequestException { StatusCode: null } => IntelligenceFailureKind.Network,
            System.Text.Json.JsonException => IntelligenceFailureKind.MalformedResponse,
            _ => ClassifyProviderError(exception.Message)
        };
        // Do not retain the raw exception as InnerException: standard exception logging prints it.
        return Create(kind, provider, model);
    }

    public static IntelligenceFailureKind ClassifyProviderError(string? error)
    {
        var text = error ?? string.Empty;
        if (Contains("quota") || Contains("billing_not_configured"))
            return IntelligenceFailureKind.QuotaExceeded;
        if (Contains("rate_limit") || Contains("rate limit") || Contains("rate-limited") ||
            Contains("rate limited") || Contains("too many requests") || Contains("429"))
            return IntelligenceFailureKind.RateLimited;
        if (Contains("model_not_found") || Contains("deploymentnotfound") ||
            Contains("model unavailable") || Contains("model is not available") ||
            Contains("model is not supported") || Contains("unknown model") ||
            Contains("model_not_supported") || Contains("unsupported_model") ||
            Contains("model") && (Contains("not supported") || Contains("not available") || Contains("does not exist")))
            return IntelligenceFailureKind.ModelUnavailable;
        if (Contains("unauthorized") || Contains("authentication") || Contains("invalid_api_key") ||
            Contains("invalid api key") || Contains("not logged in"))
            return IntelligenceFailureKind.Authentication;
        if (Contains("forbidden") || Contains("authorization") || Contains("permission_denied") || Contains("access denied"))
            return IntelligenceFailureKind.Authorization;
        if (Contains("timed out") || Contains("timeout"))
            return IntelligenceFailureKind.Timeout;
        if (Contains("ECONNREFUSED") || Contains("ENOTFOUND") || Contains("ECONNRESET") ||
            Contains("network error") || Contains("fetch failed"))
            return IntelligenceFailureKind.Network;
        return IntelligenceFailureKind.Unknown;

        bool Contains(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static string RetryText(TimeSpan delay)
    {
        if (delay < TimeSpan.FromMinutes(1))
            return $"in {Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))} seconds";
        if (delay < TimeSpan.FromHours(1))
            return $"in {Math.Ceiling(delay.TotalMinutes):0} minutes";
        return $"in {Math.Ceiling(delay.TotalHours):0} hours";
    }
}
