namespace MeetingCapture.Intelligence;

/// <summary>Progress of related-context loading for the current topic.</summary>
public enum RelatedContextState { Pending, Refreshing, Cached, Available, NoEvidence, Unavailable, Failed }

/// <summary>Current related-context state with the topic, revisions, and latest snapshot it applies to.</summary>
public sealed record RelatedContextStatus(
    RelatedContextState State,
    string SessionId,
    string Topic,
    long TranscriptRevision,
    long SourceRevision,
    RelatedContextSnapshot? Snapshot,
    string? Detail,
    string? TopicId = null);

/// <summary>
/// Runs related-context lookups for the current topic. It avoids duplicate requests, queues one
/// pending request per topic, drops stale results, caches the last result, and publishes
/// <see cref="StatusChanged"/> as the state changes.
/// </summary>
public sealed class RelatedContextCoordinator : IDisposable
{
    private const int MaxTopicLength = 240;
    private readonly object _gate = new();
    private readonly IRelatedContextSource _source;
    private long _generation;
    private long _statusVersion;
    private Task<RelatedContextStatus>? _inFlight;
    private ContextQuery? _activeQuery;
    private readonly List<PendingQuery> _queued = new();
    private CancellationTokenSource? _requestCancellation;
    private string _topicId = string.Empty;
    private long _latestTranscriptRevision = -1;
    private RelatedContextSnapshot? _cached;
    private bool _disposed;

    public RelatedContextCoordinator(IRelatedContextSource source)
    {
        _source = source;
    }

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event EventHandler<RelatedContextStatus>? StatusChanged;

    /// <summary>The latest status.</summary>
    public RelatedContextStatus Status { get; private set; } =
        new(RelatedContextState.Pending, string.Empty, string.Empty, -1, -1, null, null);

    /// <summary>
    /// Loads related context for <paramref name="query"/>. May return the current status, join a request
    /// already in progress, or queue the query. Set <c>ForceRefresh</c> to ignore the cache.
    /// </summary>
    public Task<RelatedContextStatus> RefreshAsync(ContextQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(query);
        Task<RelatedContextStatus> request;
        RelatedContextStatus? changed = null;
        long changedVersion = 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var topic = CleanTopic(query.Topic);
            var topicId = CleanTopicId(query.TopicId);
            var sourceRevision = _source.Revision;
            var sameSessionSource = Status.SessionId == query.SessionId &&
                Status.SourceRevision == sourceRevision;
            var sameContext = sameSessionSource &&
                string.Equals(_topicId, topicId, StringComparison.Ordinal);
            var staleSourceRevision = sameContext &&
                query.TranscriptRevision < _latestTranscriptRevision;
            var sameActiveRequest = _activeQuery is { } active &&
                active.SessionId == query.SessionId &&
                active.TopicId == topicId &&
                active.TranscriptRevision == query.TranscriptRevision &&
                sourceRevision == Status.SourceRevision;

            if (staleSourceRevision && !query.ForceRefresh)
            {
                request = Task.FromResult(Status);
            }
            else if (_inFlight is not null && sameActiveRequest && !query.ForceRefresh)
            {
                request = _inFlight;
            }
            else if (_inFlight is not null && _source.IsAvailable && sameSessionSource)
            {
                var pending = _queued.FirstOrDefault(item => item.Query.TopicId == topicId);
                if (pending is null)
                {
                    pending = new(query with { Topic = topic, TopicId = topicId });
                    _queued.Add(pending);
                }
                else if (query.TranscriptRevision >= pending.Query.TranscriptRevision)
                    pending.Query = query with { Topic = topic, TopicId = topicId };
                request = pending.Completion.Task;
            }
            else
            {
                if (!sameContext)
                    ResetLocked(query.SessionId, topic, query.TranscriptRevision, sourceRevision, cancel: true);
                else
                    _latestTranscriptRevision = Math.Max(_latestTranscriptRevision, query.TranscriptRevision);

                if (!_source.IsAvailable)
                {
                    _generation++;
                    _requestCancellation?.Cancel();
                    _requestCancellation?.Dispose();
                    _requestCancellation = null;
                    _inFlight = null;
                    _activeQuery = null;
                    _cached = null;
                    Status = new(RelatedContextState.Unavailable, query.SessionId, topic,
                        _latestTranscriptRevision, sourceRevision, null,
                        "Related context is unavailable.", topicId);
                    changed = Status;
                    changedVersion = ++_statusVersion;
                    request = Task.FromResult(Status);
                }
                else if (_inFlight is not null && sameContext)
                {
                    request = _inFlight;
                }
                else if (!query.ForceRefresh && _cached is not null && sameContext &&
                    Status.TranscriptRevision == query.TranscriptRevision)
                {
                    Status = Status with
                    {
                        State = RelatedContextState.Cached,
                        Topic = topic,
                        TranscriptRevision = _latestTranscriptRevision,
                        Detail = "Showing cached related context.",
                        TopicId = topicId
                    };
                    changed = Status;
                    changedVersion = ++_statusVersion;
                    request = Task.FromResult(Status);
                }
                else
                {
                    _requestCancellation?.Cancel();
                    _requestCancellation?.Dispose();
                    _requestCancellation = new();
                    var generation = ++_generation;
                    var retained = sameContext ? _cached : null;
                    Status = new(RelatedContextState.Refreshing, query.SessionId, topic,
                        _latestTranscriptRevision, sourceRevision, retained, null, topicId);
                    changed = Status;
                    changedVersion = ++_statusVersion;
                    var effective = query with { Topic = topic, TopicId = topicId };
                    request = FetchAsync(effective, topicId, sourceRevision, generation, _requestCancellation.Token);
                    _inFlight = request;
                    _activeQuery = effective;
                }
            }
        }
        if (changed is not null) Publish(changed, changedVersion);
        return request.WaitAsync(cancellationToken);
    }

    /// <summary>Cancels requests in progress and resets to the empty pending state.</summary>
    public void Invalidate()
    {
        RelatedContextStatus changed;
        long changedVersion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ResetLocked(string.Empty, string.Empty, -1, _source.Revision, cancel: true);
            changed = Status;
            changedVersion = _statusVersion;
        }
        Publish(changed, changedVersion);
    }

    private async Task<RelatedContextStatus> FetchAsync(
        ContextQuery query, string topicId, long sourceRevision, long generation, CancellationToken cancellationToken)
    {
        await Task.Yield();
        RelatedContextSnapshot? snapshot = null;
        string? failure = null;
        try
        {
            snapshot = RelatedContextNormalizer.Normalize(
                await Task.Run(
                    () => _source.GetRelatedContextAsync(query, cancellationToken),
                    cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MeetingCapture.Diagnostics.RelatedDiagnostics.Record("coordinator", _source.IsAvailable, ex);
            failure = ex is MeetingCapture.WorkIq.WorkIqException workIq
                ? MeetingCapture.WorkIq.WorkIqService.RelatedContextUnavailableMessage(workIq)
                : "Work IQ request failed. See Diagnostics/related.log for the error type.";
        }

        RelatedContextStatus status;
        var publish = false;
        long publishVersion = 0;
        RelatedContextStatus? queuedChanged = null;
        long queuedVersion = 0;
        Task<RelatedContextStatus>? queuedRequest = null;
        TaskCompletionSource<RelatedContextStatus>? queuedCompletion = null;
        lock (_gate)
        {
            if (_disposed || generation != _generation ||
                _activeQuery is not { } active ||
                active.SessionId != query.SessionId ||
                active.TopicId != topicId ||
                active.TranscriptRevision != query.TranscriptRevision ||
                Status.SourceRevision != sourceRevision)
                return Status;
            _inFlight = null;
            _activeQuery = null;
            _requestCancellation?.Dispose();
            _requestCancellation = null;

            if (_source.Revision != sourceRevision)
            {
                ResetLocked(query.SessionId, query.Topic, _latestTranscriptRevision, _source.Revision, cancel: false);
                status = Status;
                publish = true;
                publishVersion = _statusVersion;
            }
            else if (failure is not null)
            {
                Status = new(RelatedContextState.Failed, query.SessionId, query.Topic,
                    query.TranscriptRevision, sourceRevision, _cached, failure, topicId);
                status = Status;
                publish = true;
                publishVersion = ++_statusVersion;
            }
            else if (snapshot is null)
            {
                status = Status;
            }
            else
            {
                _topicId = topicId;
                _cached = snapshot.Availability is RelatedContextAvailability.Available or
                    RelatedContextAvailability.NoEvidence ? snapshot : null;
                Status = new(State(snapshot.Availability), query.SessionId, query.Topic,
                    query.TranscriptRevision, sourceRevision, snapshot, snapshot.Detail, topicId);
                status = Status;
                publish = true;
                publishVersion = ++_statusVersion;
            }

            // Deliver this topic's result before advancing the status to the next topic.
            // Otherwise the version guard discards every result except the last one.
            // A newer revision of the same topic is already queued, so this result is stale.
            var superseded = _queued.Any(pending =>
                pending.Query.SessionId == query.SessionId &&
                CleanTopicId(pending.Query.TopicId) == topicId &&
                pending.Query.TranscriptRevision > query.TranscriptRevision);
            if (publish && !superseded)
                Publish(status, publishVersion);
            publish = false;
            if (!_disposed && generation == _generation && _queued.Count > 0)
            {
                var pending = _queued[0];
                _queued.RemoveAt(0);
                var queued = pending.Query;
                queuedCompletion = pending.Completion;
                var queuedTopic = CleanTopic(queued.Topic);
                var queuedTopicId = CleanTopicId(queued.TopicId);
                _topicId = queuedTopicId;
                _latestTranscriptRevision = queued.TranscriptRevision;
                _cached = null;

                if (!_source.IsAvailable)
                {
                    Status = new(RelatedContextState.Unavailable, queued.SessionId, queuedTopic,
                        queued.TranscriptRevision, _source.Revision, null,
                        "Related context is unavailable.", queuedTopicId);
                    queuedCompletion?.TrySetResult(Status);
                }
                else
                {
                    _requestCancellation = new();
                    var queuedGeneration = ++_generation;
                    Status = new(RelatedContextState.Refreshing, queued.SessionId, queuedTopic,
                        queued.TranscriptRevision, sourceRevision, null, null, queuedTopicId);
                    var effectiveQueued = queued with { Topic = queuedTopic, TopicId = queuedTopicId };
                    queuedRequest = FetchAsync(
                        effectiveQueued,
                        queuedTopicId,
                        sourceRevision,
                        queuedGeneration,
                        _requestCancellation.Token);
                    _inFlight = queuedRequest;
                    _activeQuery = effectiveQueued;
                }
                queuedChanged = Status;
                queuedVersion = ++_statusVersion;
            }
        }
        if (publish) Publish(status, publishVersion);
        if (queuedChanged is not null) Publish(queuedChanged, queuedVersion);
        if (queuedRequest is not null && queuedCompletion is not null)
            _ = CompleteQueuedAsync(queuedRequest, queuedCompletion);
        return status;
    }

    private static async Task CompleteQueuedAsync(
        Task<RelatedContextStatus> request,
        TaskCompletionSource<RelatedContextStatus> completion)
    {
        try
        {
            completion.TrySetResult(await request.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private void ResetLocked(
        string sessionId, string topic, long transcriptRevision, long sourceRevision, bool cancel)
    {
        foreach (var pending in _queued)
            pending.Completion.TrySetCanceled();
        _queued.Clear();
        if (cancel)
        {
            _generation++;
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            _inFlight = null;
        }
        _topicId = string.Empty;
        _latestTranscriptRevision = transcriptRevision;
        _cached = null;
        Status = new(RelatedContextState.Pending, sessionId, topic, transcriptRevision,
            sourceRevision, null, null);
        _statusVersion++;
    }

    private static RelatedContextState State(RelatedContextAvailability availability) => availability switch
    {
        RelatedContextAvailability.Available => RelatedContextState.Available,
        RelatedContextAvailability.NoEvidence => RelatedContextState.NoEvidence,
        RelatedContextAvailability.Unavailable => RelatedContextState.Unavailable,
        _ => RelatedContextState.Failed
    };

    private static string CleanTopic(string topic) =>
        string.Join(" ", topic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string CleanTopicId(string? topicId) => topicId?.Trim() ?? string.Empty;

    private static void Validate(ContextQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var topic = CleanTopic(query.Topic);
        var topicId = CleanTopicId(query.TopicId);
        if (string.IsNullOrWhiteSpace(query.SessionId) || topic.Length == 0 ||
            topic.Length > MaxTopicLength || topicId.Length == 0 ||
            topicId.Length > MaxTopicLength || query.TranscriptRevision < 0)
            throw new ArgumentException(
                "Session, canonical topic ID, topic label, and source revision are required.",
                nameof(query));
    }

    private void Publish(RelatedContextStatus status, long version)
    {
        lock (_gate)
        {
            if (_disposed || version != _statusVersion)
                return;
            StatusChanged?.Invoke(this, status);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            _inFlight = null;
            _activeQuery = null;
            _cached = null;
            foreach (var pending in _queued)
                pending.Completion.TrySetCanceled();
            _queued.Clear();
        }
    }

    private sealed class PendingQuery(ContextQuery query)
    {
        public ContextQuery Query { get; set; } = query;
        public TaskCompletionSource<RelatedContextStatus> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
