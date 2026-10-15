using System.Globalization;

namespace MeetingCapture.TrayApp;

/// <summary>Initials and accessible name for a speaker avatar. Unresolved speakers show "?".</summary>
internal readonly record struct SpeakerAvatarPresentation(string Initials, string AccessibleName)
{
    public static SpeakerAvatarPresentation Create(string displayName, bool isResolved)
    {
        // Never turn an unresolved diarization label into a person identity or profile-picture name.
        if (!isResolved || string.IsNullOrWhiteSpace(displayName))
            return new("?", "Unknown speaker");

        var name = displayName.Trim();
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Use the first and last name parts, and take full Unicode text elements rather than single UTF-16 units.
        var initials = parts.Length == 1
            ? FirstCharacter(parts[0])
            : FirstCharacter(parts[0]) + FirstCharacter(parts[^1]);
        return new(initials.ToUpperInvariant(), $"{name} profile picture");
    }

    private static string FirstCharacter(string value)
        => string.IsNullOrEmpty(value) ? string.Empty : StringInfo.GetNextTextElement(value);
}
