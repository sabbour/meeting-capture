namespace MeetingCapture.Transcription;

/// <summary>A single recognized speech segment from the transcription stream.</summary>
public sealed record TranscriptionSegment(
    string Text,
    DateTimeOffset Timestamp,
    double? ConfidenceScore = null,
    string? SpeakerId = null);

/// <summary>Final collected transcript for a complete meeting session.</summary>
public sealed class TranscriptionResult
{
    public List<TranscriptionSegment> Segments { get; } = [];

    public string FullText =>
        string.Join(" ", Segments.Select(s => s.Text.Trim()));

    public DateTimeOffset? StartedAt =>
        Segments.Count > 0 ? Segments[0].Timestamp : null;

    public DateTimeOffset? EndedAt =>
        Segments.Count > 0 ? Segments[^1].Timestamp : null;

    public int WordCount =>
        Segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
}
