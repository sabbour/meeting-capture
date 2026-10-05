namespace MeetingCapture.Intelligence;

public enum RelatedContextState { Pending, Refreshing, Cached, Available, NoEvidence, Unavailable, Failed }

public sealed record RelatedContextStatus(
    RelatedContextState State,
    string SessionId,
    string Topic,
    long TranscriptRevision,
    long SourceRevision,
    RelatedContextSnapshot? Snapshot,
    string? Detail,
    string? TopicId = null);

public sealed class RelatedContextCoordinator : IDisposable
{
    private const int MaxTopicLength = 240;
    private readonly object _gate = new();
    private readonly IRelatedContextSource _source;
    private long _generation;
    private long _statusVersion;
    private Task<RelatedContextStatus>? _inFlight;
    private ContextQuery? _activeQuery;
    private ContextQuery? _queuedQuery;
    private TaskCompletionSource<RelatedContextStatus>? _queuedCompletion;
    private CancellationTokenSource? _requestCancellation;
    private string _topicId = string.Empty;
    private long _latestTranscriptRevision = -1;
    private RelatedContextSnapshot? _cached;
    private bool _disposed;

    public RelatedContextCoordinator(IRelatedContextSource source)
    {
        _source = source;
    }

    public event EventHandler<RelatedContextStatus>? StatusChanged;

    public RelatedContextStatus Status { get; private set; } =
        new(RelatedContextState.Pending, string.Empty, string.Empty, -1, -1, null, null);

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
                _queuedCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_queuedQuery is null ||
                    _queuedQuery.TopicId != topicId ||
                    query.TranscriptRevision >= _queuedQuery.TranscriptRevision)
                    _queuedQuery = query with { Topic = topic, TopicId = topicId };
                request = _queuedCompletion.Task;
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
        catch (Exception)
        {
            failure = "Related context could not be loaded.";
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

            if (_queuedQuery is not null)
            {
                var queued = _queuedQuery;
                queuedCompletion = _queuedCompletion;
                _queuedQuery = null;
                _queuedCompletion = null;
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
        _queuedCompletion?.TrySetCanceled();
        _queuedCompletion = null;
        _queuedQuery = null;
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
            _queuedCompletion?.TrySetCanceled();
            _queuedCompletion = null;
            _queuedQuery = null;
        }
    }
}
