using MeetingCapture.Configuration;
using MeetingCapture.Transcription;

namespace MeetingCapture.Intelligence;

/// <summary>Reasons an AI call can fail. Used to choose the message shown to the user.</summary>
public enum IntelligenceFailureKind
{
    Configuration, Authentication, Authorization,
    ModelUnavailable, RateLimited, QuotaExceeded, Network, Timeout,
    EmptyResponse, MalformedResponse,
    Disabled, SignedOut, Unknown
}

/// <summary>An <see cref="IntelligenceException"/> failure, with provider, model, and optional retry delay.</summary>
public sealed record IntelligenceFailure(
    IntelligenceFailureKind Kind,
    string Provider,
    string? ModelOrDeployment,
    string Message,
    TimeSpan? RetryAfter = null);

/// <summary>Exception for an AI failure. <see cref="Failure"/> holds the structured details.</summary>
public sealed class IntelligenceException : Exception
{
    public IntelligenceFailure Failure { get; }

    public IntelligenceException(IntelligenceFailure failure, Exception? innerException = null)
        : base(failure.Message, innerException)
    {
        Failure = failure;
    }
}

/// <summary>
/// Processes a completed transcript into structured meeting notes using an LLM.
/// </summary>
public interface INotesIntelligence
{
    /// <summary>
    /// Synthesizes structured notes from a completed transcript.
    /// Returns a populated MeetingNotes on success.
    /// Throws on LLM failure — do not return empty notes silently.
    /// </summary>
    Task<MeetingNotes> SynthesizeAsync(
        string meetingTitle,
        TranscriptionResult transcript,
        CancellationToken cancellationToken);

    /// <summary>
    /// Produces a very short (≤2 sentence) catch-up summary of a recent transcript snippet,
    /// used by the live "What did I miss?" re-entry button. Returns plain text.
    /// </summary>
    Task<string> SummarizeRecentAsync(
        string recentTranscript,
        CancellationToken cancellationToken);

    /// <summary>Same as <see cref="SummarizeRecentAsync"/> by default; implementations may override it with a longer recap.</summary>
    Task<string> CatchUpAsync(
        string recentTranscript,
        CancellationToken cancellationToken)
        => SummarizeRecentAsync(recentTranscript, cancellationToken);

    /// <summary>
    /// Returns up to 5 open questions raised but not yet answered in the transcript snippet.
    /// Used by the live Open Questions panel during recording.
    /// </summary>
    Task<List<string>> GetOpenQuestionsAsync(
        string recentTranscript,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to 5 explicit action items or commitments from the transcript snippet.
    /// Descriptions contain only details explicitly stated in the transcript.
    /// </summary>
    Task<List<string>> GetActionsAsync(
        string recentTranscript,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a minimal "ping" prompt with the given settings to validate the model is reachable.
    /// Returns (true, model/deployment name) on success, (false, error message) on failure.
    /// When <paramref name="overrideSettings"/> is provided it is used instead of the service's
    /// configured settings — allowing the Settings dialog to test unsaved form values.
    /// </summary>
    Task<(bool ok, string detail)> ValidateModelAsync(
        bool isSynthesisModel,
        IntelligenceSettings? overrideSettings,
        CancellationToken cancellationToken);
}
