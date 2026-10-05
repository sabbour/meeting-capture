using System.Globalization;

namespace MeetingCapture.TrayApp;

internal readonly record struct SpeakerAvatarPresentation(string Initials, string AccessibleName)
{
    public static SpeakerAvatarPresentation Create(string displayName, bool isResolved)
    {
        if (!isResolved || string.IsNullOrWhiteSpace(displayName))
            return new("?", "Unknown speaker");

        var name = displayName.Trim();
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length == 1
            ? FirstCharacter(parts[0])
            : FirstCharacter(parts[0]) + FirstCharacter(parts[^1]);
        return new(initials.ToUpperInvariant(), $"{name} profile picture");
    }

    private static string FirstCharacter(string value)
        => string.IsNullOrEmpty(value) ? string.Empty : StringInfo.GetNextTextElement(value);
}
