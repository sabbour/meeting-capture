using MeetingCapture.Intelligence;

namespace MeetingCapture.Storage;

/// <summary>Outcome of loading a saved topic map. Only <c>Valid</c> carries a usable document.</summary>
public enum MeetingVisualizationLoadKind
{
    Valid, LegacyMissing, Corrupt, FutureSchema, StaleMismatch
}

/// <summary>The result of a load: its kind, the document when valid, and optional detail.</summary>
public sealed record MeetingVisualizationLoadResult(
    MeetingVisualizationLoadKind Kind,
    MeetingVisualizationDocument? Document,
    string? Detail = null);

/// <summary>Saves and loads the topic-map data stored next to a meeting's Markdown notes.</summary>
public interface IMeetingVisualizationStorage
{
    /// <summary>Saves the document for the notes file at <paramref name="markdownPath"/>.</summary>
    Task SaveAsync(string markdownPath, MeetingVisualizationDocument document, CancellationToken cancellationToken);

    /// <summary>Loads and checks the saved document for the notes file. Problems are reported in the result, not thrown.</summary>
    Task<MeetingVisualizationLoadResult> LoadAsync(string markdownPath, CancellationToken cancellationToken);
}

/// <summary>Thrown when the transcript was saved but its topic-map data could not be.</summary>
public sealed class MeetingVisualizationPersistenceException : IOException
{
    public string MarkdownPath { get; }

    public MeetingVisualizationPersistenceException(string markdownPath, Exception innerException)
        : base("The transcript was saved, but its visualization data could not be saved.", innerException)
    {
        MarkdownPath = markdownPath;
    }
}

/// <summary>Thrown when the transcript was saved but saving its topic-map data was canceled.</summary>
public sealed class MeetingVisualizationPersistenceCanceledException : OperationCanceledException
{
    public string MarkdownPath { get; }

    public MeetingVisualizationPersistenceCanceledException(
        string markdownPath, OperationCanceledException innerException, CancellationToken cancellationToken)
        : base("The transcript was saved, but visualization persistence was canceled.",
            innerException, cancellationToken)
    {
        MarkdownPath = markdownPath;
    }
}
