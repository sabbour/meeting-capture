namespace MeetingCapture.Transcription;

/// <summary>Turns speech service cancellation errors into reconnect decisions and readable messages.</summary>
internal static class SpeechCancellationDiagnostics
{
    // A service timeout is recoverable when audio resumes; other errors stop the current stream.
    public static bool RequiresReconnect(string errorCode) =>
        errorCode.Equals("ServiceTimeout", StringComparison.Ordinal);

    public static string Describe(string errorCode, string? errorDetails)
    {
        if (errorCode.Equals("ServiceTimeout", StringComparison.Ordinal))
        {
            // The service reports inactivity and oversized-buffer timeouts differently, but both
            // mean there is no active speech stream to keep open.
            var inactivity = errorDetails?.Contains("inactivity", StringComparison.OrdinalIgnoreCase) == true ||
                errorDetails?.Contains("buffer size exceeded", StringComparison.OrdinalIgnoreCase) == true;
            return inactivity
                ? "Speech service timed out after service inactivity; transcription will reconnect when audio resumes."
                : "Speech service timed out; transcription will reconnect when audio resumes.";
        }

        // Keep SDK detail out of the UI and explain whether later audio can still be transcribed.
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
