namespace MeetingCapture.Transcription;

/// <summary>Typed reason a speech transcription session could not start.</summary>
public enum TranscriptionStartFailureKind
{
    Unknown,
    CredentialsMissing,
    CredentialsRejected,
    ConfigurationInvalid,
    ServiceUnreachable
}

/// <summary>
/// Thrown when transcription startup fails. The message is a fixed, sanitized string:
/// provider payloads, endpoints, and keys are never propagated.
/// </summary>
public sealed class TranscriptionStartException(
    TranscriptionStartFailureKind kind, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public TranscriptionStartFailureKind Kind { get; } = kind;
}

/// <summary>Classifies speech startup faults into typed, sanitized failures.</summary>
internal static class SpeechStartupDiagnostics
{
    public static TranscriptionStartException Describe(TranscriptionStartFailureKind kind, Exception? inner = null) =>
        new(kind, Message(kind), inner);

    public static string Message(TranscriptionStartFailureKind kind) => kind switch
    {
        TranscriptionStartFailureKind.CredentialsMissing =>
            "Speech credentials are not configured.",
        TranscriptionStartFailureKind.CredentialsRejected =>
            "Speech rejected the configured credentials or region.",
        TranscriptionStartFailureKind.ConfigurationInvalid =>
            "The Speech endpoint, region, or language configuration is not valid.",
        TranscriptionStartFailureKind.ServiceUnreachable =>
            "The Speech service could not be reached.",
        _ => "Speech transcription could not start."
    };

    /// <summary>Maps an SDK or transport fault to a typed kind without retaining provider text.</summary>
    public static TranscriptionStartFailureKind Classify(Exception error)
    {
        if (error is TranscriptionStartException typed) return typed.Kind;
        return ClassifyMessage(Flatten(error));
    }

    /// <summary>Classifies an already-sanitized failure message produced by this app.</summary>
    public static TranscriptionStartFailureKind ClassifyMessage(string message)
    {
        var text = (message ?? string.Empty).ToLowerInvariant();
        if (Contains(text, "authentication", "401", "403", "unauthorized", "forbidden", "subscription key",
                "invalid key", "accessdenied"))
            return TranscriptionStartFailureKind.CredentialsRejected;
        if (Contains(text, "connection", "connectionfailure", "no such host", "name or service not known",
                "network", "timed out", "timeout", "unreachable", "proxy", "serviceunavailable", "unavailable", "503"))
            return TranscriptionStartFailureKind.ServiceUnreachable;
        if (Contains(text, "badrequest", "400", "invalid uri", "invalid region", "unsupported", "malformed",
                "not a valid", "invalid value"))
            return TranscriptionStartFailureKind.ConfigurationInvalid;
        return TranscriptionStartFailureKind.Unknown;
    }

    private static string Flatten(Exception error)
    {
        var text = new System.Text.StringBuilder();
        for (var current = error; current is not null; current = current.InnerException)
        {
            text.Append(current.Message).Append('\n');
            if (current is AggregateException aggregate)
                foreach (var nested in aggregate.InnerExceptions) text.Append(nested.Message).Append('\n');
        }
        return text.ToString().ToLowerInvariant();
    }

    private static bool Contains(string text, params string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
}
