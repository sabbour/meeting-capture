using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

/// <summary>Stage of an insight panel refresh.</summary>
public enum PanelState
{
    Pending, Refreshing, SucceededWithResults, SucceededEmpty, Failed, Unavailable
}

/// <summary>Latest refresh result for one panel: the state, which transcript and settings revisions it used, and any failure.</summary>
public sealed record PanelStatus(
    PanelState State, string SessionId, long TranscriptRevision, long ConfigurationRevision,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, IntelligenceFailure? Failure);

/// <summary>
/// Tracks refreshes of one insight panel. Only one refresh runs at a time, and a result from an
/// older generation (after <see cref="Invalidate"/>) or an older transcript revision is ignored.
/// </summary>
public sealed class PanelRefreshState
{
    private long _generation;
    private bool _busy;

    /// <summary>True while a refresh started by <see cref="TryBegin"/> has not yet ended.</summary>
    public bool IsBusy => _busy;

    /// <summary>The latest status of the panel.</summary>
    public PanelStatus Status { get; private set; } = new(PanelState.Pending, "", -1, -1, null, null, null);

    /// <summary>Resets the panel to pending for a new session or settings revision, and drops results from earlier refreshes.</summary>
    public void Invalidate(string sessionId, long configurationRevision)
    {
        // Every in-flight result carries the old generation, so it cannot repopulate the new session or settings.
        _generation++;
        Status = new(PanelState.Pending, sessionId, -1, configurationRevision, null, null, null);
        // Cancellation does not prove the underlying operation finished. Keep its guard.
    }

    /// <summary>Starts a refresh. Returns its generation, or null if one is already running or the transcript has not advanced (unless forced).</summary>
    public long? TryBegin(string sessionId, long revision, long configurationRevision, bool force)
    {
        // Require the current session/settings; unless forced, also require newer transcript data.
        if (_busy || Status.SessionId != sessionId || Status.ConfigurationRevision != configurationRevision ||
            (!force && Status.TranscriptRevision >= revision))
            return null;
        _busy = true;
        Status = Status with { State = PanelState.Refreshing, LastAttemptAt = DateTimeOffset.Now, Failure = null };
        return _generation;
    }

    /// <summary>Records a refresh result. Returns false and ignores it if it is stale.</summary>
    public bool Complete(long generation, long revision, bool hasResults, IntelligenceFailure? failure)
    {
        // Reject work started before invalidation and snapshots older than the result already shown.
        if (generation != _generation || revision < Status.TranscriptRevision)
            return false;
        var state = failure is null
            ? hasResults ? PanelState.SucceededWithResults : PanelState.SucceededEmpty
            : failure.Kind is IntelligenceFailureKind.Disabled or IntelligenceFailureKind.SignedOut
                ? PanelState.Unavailable : PanelState.Failed;
        Status = Status with
        {
            State = state,
            TranscriptRevision = revision,
            LastSuccessAt = failure is null ? DateTimeOffset.Now : Status.LastSuccessAt,
            Failure = failure
        };
        return true;
    }

    /// <summary>Marks the refresh as finished so a new one can start. A refresh that left no result returns to pending.</summary>
    public void End(long generation)
    {
        _busy = false;
        // A refresh can end without a result when it was cancelled; allow the next eligible request to run.
        if (generation == _generation && Status.State == PanelState.Refreshing)
            Status = Status with { State = PanelState.Pending };
    }
}

/// <summary>Keeps insight lists stable between refreshes.</summary>
public static class InsightRetention
{
    /// <summary>Returns trimmed snapshot items that are not already in <paramref name="existing"/> (case-insensitive, no repeats).</summary>
    public static IReadOnlyList<string> NewItems(IEnumerable<string> existing, IEnumerable<string> snapshot)
    {
        // Add each trimmed value to the same set used for old entries, so duplicates in either list are removed.
        var seen = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Select(item => item.Trim()).Where(item => item.Length > 0 && seen.Add(item)).ToArray();
    }
}
