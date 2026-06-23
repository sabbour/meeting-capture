namespace MeetingCapture.Intelligence;

/// <summary>Structured output produced by the LLM notes intelligence pass.</summary>
public sealed class MeetingNotes
{
    public string MeetingTitle { get; init; } = "Meeting";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string RawTranscript { get; set; } = string.Empty;

    /// <summary>High-level summary paragraph (2-4 sentences).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Key decisions made during the meeting.</summary>
    public List<string> Decisions { get; } = [];

    /// <summary>Action items with owner where mentioned.</summary>
    public List<ActionItem> ActionItems { get; } = [];

    /// <summary>Topics discussed, useful for searchability.</summary>
    public List<string> Topics { get; } = [];

    /// <summary>Nuances, tone observations, or notable context.</summary>
    public string Nuances { get; set; } = string.Empty;
}

public sealed class ActionItem
{
    public string Description { get; init; } = string.Empty;
    public string? Owner { get; init; }
    public string? DueDate { get; init; }
}
