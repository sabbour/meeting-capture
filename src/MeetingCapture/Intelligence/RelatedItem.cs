namespace MeetingCapture.Intelligence;

public record RelatedItem(
    string SourceType,   // "Email", "Teams", "Document", "Transcript", "Info"
    string Title,
    string Snippet,
    string? WebUrl,
    DateTimeOffset? Date
);
