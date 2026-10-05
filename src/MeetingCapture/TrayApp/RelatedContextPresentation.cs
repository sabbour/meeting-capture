using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

internal readonly record struct RelatedContextTopicPresentation(
    string Label,
    string Disclosure,
    bool IsEarlierTopic);
internal readonly record struct RelatedContextStatusPresentation(string Text, bool IsQuerying);
internal enum TopicSemanticInfoState
{
    Loading,
    NoEvidence,
    Available,
    Cached,
    Unavailable,
    Failed,
    Refreshing,
}

internal sealed record TopicContextStatusViewModel(
    TopicSemanticInfoState State,
    string Text,
    bool IsLoading,
    string AccessibleName);

internal static class RelatedContextPresentation
{
    internal static TopicContextStatusViewModel TopicStatus(RelatedContextState state) => state switch
    {
        RelatedContextState.Pending => new(
            TopicSemanticInfoState.Loading,
            "Related workplace suggestions are ready to find", false,
            "Related workplace suggestions are ready to find for this topic."),
        RelatedContextState.Refreshing => new(
            TopicSemanticInfoState.Refreshing,
            "Refreshing related workplace context", true,
            "Refreshing verified related workplace context for this topic."),
        RelatedContextState.Cached => new(
            TopicSemanticInfoState.Cached,
            "Saved related workplace suggestions", false,
            "Saved related workplace suggestions are available for this topic."),
        RelatedContextState.Available => new(
            TopicSemanticInfoState.Available,
            "Related workplace context available", false,
            "Verified related workplace context is available for this topic."),
        RelatedContextState.NoEvidence => new(
            TopicSemanticInfoState.NoEvidence,
            "No verified related context", false,
            "No verified related workplace context was found for this topic."),
        RelatedContextState.Unavailable => new(
            TopicSemanticInfoState.Unavailable,
            "Related workplace context unavailable", false,
            "Related workplace context is unavailable for this topic."),
        RelatedContextState.Failed => new(
            TopicSemanticInfoState.Failed,
            "Related workplace context could not be loaded", false,
            "Related workplace context could not be loaded for this topic."),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    internal static RelatedContextStatusPresentation Status(
        RelatedContextStatus? status,
        string? acceptedTopic,
        bool topicEligible,
        bool workIqAuthenticated,
        int itemCount)
    {
        if (!string.IsNullOrWhiteSpace(acceptedTopic) && !topicEligible)
            return new(
                "Related suggestions are skipped for long summaries; the topic map continues updating.",
                false);
        if (!workIqAuthenticated && !string.IsNullOrWhiteSpace(acceptedTopic) &&
            (status is null ||
             status.State is RelatedContextState.Pending or RelatedContextState.Unavailable))
            return new(
                "WorkIQ is signed out. Sign in to query related context for the accepted topic.",
                false);
        return status?.State switch
        {
            RelatedContextState.Refreshing when itemCount > 0 => new(
                "Updating suggestions; previous results remain visible.", true),
            RelatedContextState.Refreshing => new(
                "Querying WorkIQ for related suggestions...", true),
            RelatedContextState.Cached => new(
                $"{itemCount} cached related suggestion{(itemCount == 1 ? "" : "s")}; WorkIQ was not queried again.",
                false),
            RelatedContextState.Available => new(
                $"{itemCount} related source suggestion{(itemCount == 1 ? "" : "s")} · retrieved {status.Snapshot!.RetrievedAt.ToLocalTime():g}",
                false),
            RelatedContextState.NoEvidence => new(
                "WorkIQ found no related suggestions for this topic.", false),
            RelatedContextState.Unavailable => new(
                status.Detail ?? "WorkIQ related context is unavailable.", false),
            RelatedContextState.Failed => new(
                "Related suggestions could not be loaded. Recording and the local topic map continue.", false),
            _ when string.IsNullOrWhiteSpace(acceptedTopic) => new(
                "Waiting for a discussion topic.", false),
            _ => new("Topic accepted; waiting to query WorkIQ for suggestions.", false)
        };
    }

    internal static RelatedContextTopicPresentation Topic(
        string? queriedTopic,
        string? acceptedTopic,
        string? displayedTopic = null)
    {
        var queried = Clean(queriedTopic);
        if (queried.Length == 0)
            return new(string.Empty, string.Empty, false);

        var earlier = !string.Equals(
            Normalize(queried),
            Normalize(acceptedTopic),
            StringComparison.Ordinal);
        var display = Clean(displayedTopic);
        if (display.Length == 0)
            display = queried;
        return new(
            earlier ? $"Earlier topic · {display}" : $"Topic · {display}",
            earlier
                ? "These suggestions were retrieved for a prior topic and may be stale for the current discussion."
                : "These suggestions reflect the latest completed retrieval for this topic.",
            earlier);
    }

    private static string Normalize(string? topic) => Clean(topic).ToUpperInvariant();

    private static string Clean(string? topic) =>
        string.Join(" ", (topic ?? string.Empty).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
}

internal static class ContextTitlePresentation
{
    internal static string Clean(string? value) => RelatedContextTitle.Clean(value, null);

    internal static string UniqueDetail(string? summary, params string?[] titles)
    {
        var detail = Clean(summary);
        if (detail.Length == 0 ||
            detail.StartsWith("WorkIQ retrieval identified this ", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return titles.Select(Clean).Any(title =>
                title.Length > 0 &&
                string.Equals(title, detail, StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : detail;
    }
}
