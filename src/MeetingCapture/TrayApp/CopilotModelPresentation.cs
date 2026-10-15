using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

/// <summary>A model entry in the settings list. <c>IsAvailable</c> is false for a configured model the service no longer offers.</summary>
internal sealed record CopilotModelChoice(string Id, string Display, bool IsAvailable);

/// <summary>Builds the model list for the settings page.</summary>
internal static class CopilotModelPresentation
{
    internal static IReadOnlyList<CopilotModelChoice> Merge(
        IReadOnlyList<CopilotModelOption> available,
        params string[] configuredIds)
    {
        var choices = available
            .Select(model => new CopilotModelChoice(
                model.Id,
                string.Equals(model.Name, model.Id, StringComparison.OrdinalIgnoreCase)
                    ? model.Id
                    : $"{model.Name} ({model.Id})",
                true))
            .ToList();
        foreach (var id in configuredIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Reverse())
        {
            if (choices.All(choice => !string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, new(id, $"{id} (configured; not currently available)", false));
        }
        return choices;
    }

    internal static CopilotModelChoice? FindSelection(
        IEnumerable<CopilotModelChoice> choices,
        string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : choices.FirstOrDefault(choice =>
                string.Equals(choice.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
