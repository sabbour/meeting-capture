using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

/// <summary>One bounded, headed group of grounded catch-up lines.</summary>
public sealed class CatchUpSection
{
    public required string Heading { get; init; }
    public required IReadOnlyList<string> Items { get; init; }
    public string AccessibleName => $"{Heading}: {string.Join(". ", Items)}";
}

/// <summary>
/// Composes the Catch me up result from state the session already holds: the grounded
/// visualization document plus the live questions and actions checklists. It retrieves
/// nothing and infers nothing; every line is text the session already accepted.
/// </summary>
internal static class CatchUpPresentation
{
    internal const int SectionItemLimit = 5;

    internal static IReadOnlyList<CatchUpSection> Sections(
        MeetingVisualizationDocument? document,
        string? currentTopic,
        IEnumerable<string> openQuestions,
        IEnumerable<string> openActions)
    {
        var sections = new List<CatchUpSection>();
        Add(sections, "Earlier points", EarlierTopics(document, currentTopic));
        Add(sections, "Decisions", document?.Decisions.Select(item => item.Text) ?? []);
        Add(sections, "Unresolved questions", Merge(
            document?.Questions
                .Where(item => item.QuestionState == QuestionState.Open)
                .Select(item => item.Text) ?? [],
            openQuestions));
        Add(sections, "Actions", Merge(
            document?.Actions
                .Where(item => item.State == VisualizationActionState.Open)
                .Select(item => item.Text) ?? [],
            openActions));
        return sections;
    }

    private static IEnumerable<string> EarlierTopics(
        MeetingVisualizationDocument? document,
        string? currentTopic)
    {
        if (document is null)
            return [];
        var current = Normalize(currentTopic);
        var labels = new List<string>();
        foreach (var occurrence in document.TopicTrail.Reverse())
        {
            var label = Clean(occurrence.LabelAtTime);
            if (label.Length == 0 || Normalize(label) == current)
                continue;
            if (!labels.Any(existing => Normalize(existing) == Normalize(label)))
                labels.Add(label);
        }
        return labels;
    }

    private static IEnumerable<string> Merge(IEnumerable<string> grounded, IEnumerable<string> live)
        => grounded.Concat(live);

    private static void Add(List<CatchUpSection> sections, string heading, IEnumerable<string> items)
    {
        var bounded = new List<string>();
        foreach (var item in items)
        {
            var value = Clean(item);
            if (value.Length == 0 ||
                bounded.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                continue;
            bounded.Add(value);
            if (bounded.Count == SectionItemLimit)
                break;
        }
        if (bounded.Count > 0)
            sections.Add(new CatchUpSection { Heading = heading, Items = bounded });
    }

    private static string Clean(string? value) =>
        string.Join(" ", (value ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Normalize(string? value) => Clean(value).ToUpperInvariant();
}
