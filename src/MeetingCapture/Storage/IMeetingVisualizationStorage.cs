using MeetingCapture.Intelligence;

namespace MeetingCapture.Storage;

public enum MeetingVisualizationLoadKind
{
    Valid, LegacyMissing, Corrupt, FutureSchema, StaleMismatch
}

public sealed record MeetingVisualizationLoadResult(
    MeetingVisualizationLoadKind Kind,
    MeetingVisualizationDocument? Document,
    string? Detail = null);

public interface IMeetingVisualizationStorage
{
    Task SaveAsync(string markdownPath, MeetingVisualizationDocument document, CancellationToken cancellationToken);
    Task<MeetingVisualizationLoadResult> LoadAsync(string markdownPath, CancellationToken cancellationToken);
}

public sealed class MeetingVisualizationPersistenceException : IOException
{
    public string MarkdownPath { get; }

    public MeetingVisualizationPersistenceException(string markdownPath, Exception innerException)
        : base("The transcript was saved, but its visualization data could not be saved.", innerException)
    {
        MarkdownPath = markdownPath;
    }
}

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
