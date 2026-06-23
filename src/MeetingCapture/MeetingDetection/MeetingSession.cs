namespace MeetingCapture.MeetingDetection;

/// <summary>Represents an active or recently ended meeting session.</summary>
public sealed class MeetingSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Display-friendly title, typically the detected app name + timestamp.</summary>
    public string Title { get; init; } = "Meeting";

    /// <summary>Name of the process that triggered detection (e.g., "Teams").</summary>
    public string SourceProcess { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsActive => EndedAt is null;

    public void End() => EndedAt = DateTimeOffset.UtcNow;

    public TimeSpan Duration =>
        (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public override string ToString() =>
        $"[{Id[..8]}] {Title} via {SourceProcess} — started {StartedAt:HH:mm:ss}";
}
