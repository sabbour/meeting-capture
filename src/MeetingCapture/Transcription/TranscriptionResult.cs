namespace MeetingCapture.Transcription;

/// <summary>A single recognized speech segment from the transcription stream.</summary>
public sealed record TranscriptionSegment(
    string Text,
    DateTimeOffset Timestamp,
    double? ConfidenceScore = null,
    string? SpeakerId = null,
    string? SpeakerDisplayName = null);

/// <summary>
/// An interim/partial transcription result. Carries the current speaker label (from
/// diarization) so the UI can render the speaker prefix on live, not-yet-final text.
/// </summary>
public sealed record PartialTranscript(string Text, string? SpeakerId = null);

/// <summary>A point-in-time copy of a session's transcript. <c>Revision</c> increases as it changes; <c>IsComplete</c> is true once recording ended.</summary>
public sealed record TranscriptSnapshot
{
    public string SessionId { get; }
    public string Title { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; }
    public long Revision { get; }
    public bool IsComplete { get; }
    public IReadOnlyList<TranscriptionSegment> Segments { get; }

    public TranscriptSnapshot(string SessionId, string Title, DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt, long Revision, bool IsComplete,
        IReadOnlyList<TranscriptionSegment> Segments)
    {
        this.SessionId = SessionId;
        this.Title = Title;
        this.StartedAt = StartedAt;
        this.EndedAt = EndedAt;
        this.Revision = Revision;
        this.IsComplete = IsComplete;
        this.Segments = Array.AsReadOnly(Segments.ToArray());
    }

    public string FullText => string.Join(" ", Segments.Select(s => s.Text.Trim()));
    public int WordCount => Segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    public TranscriptionResult ToTranscriptionResult()
    {
        var result = new TranscriptionResult();
        result.Segments.AddRange(Segments);
        return result;
    }
}

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
