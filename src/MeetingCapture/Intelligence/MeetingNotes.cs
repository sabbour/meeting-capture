namespace MeetingCapture.Intelligence;

/// <summary>Structured output produced by the LLM notes intelligence pass.</summary>
public sealed class MeetingNotes
{
    public string SessionId { get; init; } = string.Empty;
    public IntelligenceFailure? IntelligenceFailure { get; init; }
    public string MeetingTitle { get; init; } = "Meeting";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string RawTranscript { get; set; } = string.Empty;

    /// <summary>Grounded summary paragraph (4-6 sentences).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Key decisions made during the meeting.</summary>
    public List<string> Decisions { get; } = [];

    /// <summary>Action items with owner where mentioned.</summary>
    public List<ActionItem> ActionItems { get; } = [];

    /// <summary>Topics discussed, useful for searchability.</summary>
    public List<string> Topics { get; } = [];

    /// <summary>Grounded topic narratives, one bounded entry per substantive thread.</summary>
    public List<string> KeyDiscussionPoints { get; } = [];

    /// <summary>Nuances, tone observations, or notable context.</summary>
    public string Nuances { get; set; } = string.Empty;

    public string MeetingType { get; set; } = "unknown"; // standup|planning|review|demo|customer-call|internal|1:1|unknown
    public string Sentiment { get; set; } = "neutral";   // positive|neutral|mixed|tense
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> FollowUpMeetings { get; set; } = [];
    public List<string> NotableQuotes { get; set; } = [];  // attributed verbatim quotes
    public List<ParticipantInfo> Participants { get; set; } = [];
    public int EstimatedDurationMinutes { get; set; }
}

public record ParticipantInfo(string Name, string? Role);

public sealed class ActionItem
{
    public string Description { get; init; } = string.Empty;
    public string? Owner { get; init; }
    public string? DueDate { get; init; }
}
