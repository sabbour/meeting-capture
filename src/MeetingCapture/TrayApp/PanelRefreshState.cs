using MeetingCapture.Intelligence;

namespace MeetingCapture.TrayApp;

public enum PanelState
{
    Pending, Refreshing, SucceededWithResults, SucceededEmpty, Failed, Unavailable
}

public sealed record PanelStatus(
    PanelState State, string SessionId, long TranscriptRevision, long ConfigurationRevision,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, IntelligenceFailure? Failure);

public sealed class PanelRefreshState
{
    private long _generation;
    private bool _busy;
    public bool IsBusy => _busy;
    public PanelStatus Status { get; private set; } = new(PanelState.Pending, "", -1, -1, null, null, null);

    public void Invalidate(string sessionId, long configurationRevision)
    {
        _generation++;
        Status = new(PanelState.Pending, sessionId, -1, configurationRevision, null, null, null);
        // Cancellation does not prove the underlying operation finished. Keep its guard.
    }

    public long? TryBegin(string sessionId, long revision, long configurationRevision, bool force)
    {
        if (_busy || Status.SessionId != sessionId || Status.ConfigurationRevision != configurationRevision ||
            (!force && Status.TranscriptRevision >= revision))
            return null;
        _busy = true;
        Status = Status with { State = PanelState.Refreshing, LastAttemptAt = DateTimeOffset.Now, Failure = null };
        return _generation;
    }

    public bool Complete(long generation, long revision, bool hasResults, IntelligenceFailure? failure)
    {
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

    public void End(long generation)
    {
        _busy = false;
        if (generation == _generation && Status.State == PanelState.Refreshing)
            Status = Status with { State = PanelState.Pending };
    }
}

public static class InsightRetention
{
    public static IReadOnlyList<string> NewItems(IEnumerable<string> existing, IEnumerable<string> snapshot)
    {
        var seen = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Select(item => item.Trim()).Where(item => item.Length > 0 && seen.Add(item)).ToArray();
    }
}
