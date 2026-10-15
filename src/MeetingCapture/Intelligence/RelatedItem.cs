namespace MeetingCapture.Intelligence;

/// <summary>An item from a related source (such as an email, Teams message, or document) shown to the user.</summary>
public record RelatedItem(
    string SourceType,   // "Email", "Teams", "Document", "Transcript", "Info"
    string Title,
    string Snippet,
    string? WebUrl,
    DateTimeOffset? Date
);
