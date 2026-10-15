namespace MeetingCapture.Transcription;

/// <summary>Turns speech service cancellation errors into reconnect decisions and readable messages.</summary>
internal static class SpeechCancellationDiagnostics
{
    public static bool RequiresReconnect(string errorCode) =>
        errorCode.Equals("ServiceTimeout", StringComparison.Ordinal);

    public static string Describe(string errorCode, string? errorDetails)
    {
        if (errorCode.Equals("ServiceTimeout", StringComparison.Ordinal))
        {
            var inactivity = errorDetails?.Contains("inactivity", StringComparison.OrdinalIgnoreCase) == true ||
                errorDetails?.Contains("buffer size exceeded", StringComparison.OrdinalIgnoreCase) == true;
            return inactivity
                ? "Speech service timed out after service inactivity; transcription will reconnect when audio resumes."
                : "Speech service timed out; transcription will reconnect when audio resumes.";
        }

        return errorCode switch
        {
            "AuthenticationFailure" => "Speech authentication failed; check the key and resource region.",
            "BadRequest" => "Speech rejected the recognition request or PCM format.",
            "TooManyRequests" => "Speech rejected the session because the resource concurrency limit was reached.",
            "Forbidden" => "Speech rejected the session because the resource is not permitted or has no quota.",
            "ConnectionFailure" => "The connection to Speech failed; later audio was not transcribed.",
            "ServiceUnavailable" => "Speech was unavailable; later audio was not transcribed.",
            "ServiceError" => "Speech returned a service error; later audio was not transcribed.",
            _ => $"Speech transcription failed ({errorCode}); later audio was not transcribed."
        };
    }
}
