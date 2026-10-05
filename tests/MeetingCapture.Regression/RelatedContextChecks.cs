using System.Collections.Concurrent;
using MeetingCapture.Intelligence;

namespace MeetingCapture.Regression;

internal static class RelatedContextChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("related context normalizes grounded multi-source evidence and rejects unsafe URLs", Normalization);
        yield return ("related context caches stable topics and supports explicit refresh", CacheAndRefresh);
        yield return ("related context source synchronous setup does not block the UI context", SourceStartsOffUiThread);
        yield return ("related context invalidation cancels a fetch before its worker starts", InvalidationBeforeFetchStarts);
        yield return ("related context hands the latest topic off after one slow request", TopicVariationPacing);
        yield return ("related context accepts only the latest source revision completion", SourceRevisionPacing);
        yield return ("related context never publishes stale loading after a fast result", EventOrdering);
        yield return ("related context singleflight survives caller cancellation", SingleflightAndCancellation);
        yield return ("related context rejects stale session and auth revision results", StaleResults);
    }

    private static Task Normalization()
    {
        var date = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var snapshot = RelatedContextNormalizer.Normalize(new(date,
        [
            new(" <File>Prior launch decision</File> ", " Agreed to use staged rollout. ", ClaimKind.HistoricalFact,
            [
                new("<File>Planning notes</File>", "https://example.test/docs/plan", ContextSourceType.Document, date,
                    "source-1", "Verified signed-in account in structured document.author metadata."),
                new("<TeamsMessage>Sample thread</TeamsMessage>", "https://example.test/message/1", ContextSourceType.TeamsMessage, null),
                new("Unsafe", "http://example.test/item", ContextSourceType.Document, null),
                new("Credential URL", "https://user@example.test/item", ContextSourceType.Document, null),
                new("Mail", "https://example.test/mail/1", ContextSourceType.OutlookMail, null),
                new("Meeting", "https://example.test/meeting/1", ContextSourceType.MeetingArtifact, null),
                new("Extra", "https://example.test/extra/1", ContextSourceType.TeamsChannel, null)
            ]),
            new("No evidence", "Dropped", ClaimKind.InferredConnection,
                [new("Unsafe", "javascript:alert(1)", ContextSourceType.OutlookMail, null)])
        ], RelatedContextAvailability.Available));
        Check.Equal(RelatedContextAvailability.Available, snapshot.Availability);
        Check.Equal(1, snapshot.Items.Count);
        Check.Equal("Prior launch decision", snapshot.Items[0].Title);
        Check.Equal(4, snapshot.Items[0].Sources.Count);
        Check.Equal("Planning notes", snapshot.Items[0].Sources[0].Title);
        Check.Equal("source-1", snapshot.Items[0].Sources[0].SourceId);
        Check.True(snapshot.Items[0].Sources[0].InvolvementRationale!.Contains(
            "document.author", StringComparison.Ordinal));
        Check.Equal("Sample thread", snapshot.Items[0].Sources[1].Title);
        Check.Equal("https://example.test/message/1", snapshot.Items[0].Sources[1].SourceId);
        Check.True(snapshot.Items[0].SuggestionId!.StartsWith("context-", StringComparison.Ordinal));
        Check.Equal(date, snapshot.Items[0].Sources[0].ActualSourceDate);
        Check.True(snapshot.Items[0].Sources[1].ActualSourceDate is null);
        Check.True(snapshot.Items[0].Sources.All(source => source.Url.StartsWith("https://")));
        var unavailable = RelatedContextNormalizer.Normalize(new(date,
            snapshot.Items, RelatedContextAvailability.Unavailable, "synthetic private detail"));
        Check.Equal(0, unavailable.Items.Count);
        Check.Equal("Related context is unavailable.", unavailable.Detail);
        return Task.CompletedTask;
    }

    private static async Task CacheAndRefresh()
    {
        var source = new FakeRelatedContextSource();
        using var coordinator = new RelatedContextCoordinator(source);
        var events = new List<RelatedContextStatus>();
        coordinator.StatusChanged += (_, status) => events.Add(status);
        var first = await coordinator.RefreshAsync(
            new("session-a", "Launch plan", 10, TopicId: "topic-launch"), CancellationToken.None);
        Check.Equal(RelatedContextState.Available, first.State);
        Check.Equal(1, source.Calls);
        var cached = await coordinator.RefreshAsync(
            new("session-a", "Renamed launch plan", 10, TopicId: "topic-launch"), CancellationToken.None);
        Check.Equal(1, source.Calls);
        Check.Equal(RelatedContextState.Cached, cached.State);
        Check.Equal(10L, cached.TranscriptRevision);
        Check.True(ReferenceEquals(first.Snapshot, cached.Snapshot));
        Check.Equal(RelatedContextState.Cached, events[^1].State);
        var refreshed = await coordinator.RefreshAsync(
            new("session-a", "Launch plan", 12, ForceRefresh: true, TopicId: "topic-launch"),
            CancellationToken.None);
        Check.Equal(2, source.Calls);
        Check.Equal(12L, refreshed.TranscriptRevision);
        source.IsAvailable = false;
        var unavailable = await coordinator.RefreshAsync(
            new("session-a", "Other topic", 13, TopicId: "topic-other"), CancellationToken.None);
        Check.Equal(RelatedContextState.Unavailable, unavailable.State);
        Check.True(unavailable.Snapshot is null);
        Check.Throws<ArgumentException>(() => coordinator.RefreshAsync(
            new("session-a", new string('x', 241), 14, TopicId: "topic-long"), CancellationToken.None));
        Check.Throws<ArgumentException>(() => coordinator.RefreshAsync(
            new("session-a", "Missing identity", 14), CancellationToken.None));
    }

    private static async Task SingleflightAndCancellation()
    {
        var gate = new AsyncGate();
        var source = new FakeRelatedContextSource { BeforeReturn = gate.WaitAsync };
        using var coordinator = new RelatedContextCoordinator(source);
        using var cancelled = new CancellationTokenSource();
        var first = coordinator.RefreshAsync(
            new("session-a", "Stable topic", 20, TopicId: "topic-stable"), cancelled.Token);
        await gate.Entered;
        var second = coordinator.RefreshAsync(
            new("session-a", "Stable topic", 20, TopicId: "topic-stable"), CancellationToken.None);
        cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => first);
        Check.Equal(1, source.Calls);
        gate.Release();
        var result = await second;
        Check.Equal(RelatedContextState.Available, result.State);
        Check.Equal(20L, result.TranscriptRevision);
        Check.Equal(1, source.Calls);
    }

    private static async Task SourceStartsOffUiThread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sourceThreadId = 0;
        var source = new FakeRelatedContextSource
        {
            BeforeReturn = _ =>
            {
                Volatile.Write(ref sourceThreadId, Environment.CurrentManagedThreadId);
                entered.Set();
                release.Wait();
                return Task.CompletedTask;
            }
        };
        using var coordinator = new RelatedContextCoordinator(source);
        var context = new PumpedSynchronizationContext();
        var started = new TaskCompletionSource<Task<RelatedContextStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RelatedContextStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThreadId = 0;
        var uiThread = new Thread(() =>
        {
            Volatile.Write(ref uiThreadId, Environment.CurrentManagedThreadId);
            SynchronizationContext.SetSynchronizationContext(context);
            var request = coordinator.RefreshAsync(
                new("session-ui", "Blocked topic", 1, TopicId: "topic-ui"), CancellationToken.None);
            started.TrySetResult(request);
            try
            {
                while (!request.IsCompleted)
                    context.PumpOne(TimeSpan.FromMilliseconds(20));
                finished.TrySetResult(request.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                finished.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };
        uiThread.Start();

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var heartbeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(_ => heartbeat.TrySetResult(true), null);
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(Volatile.Read(ref sourceThreadId) != Volatile.Read(ref uiThreadId));
            release.Set();
            var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(RelatedContextState.Available, result.State);
            Check.Equal(1, source.Calls);
            Check.Equal(RelatedContextState.Available, coordinator.Status.State);
        }
        finally
        {
            release.Set();
            if (started.Task.IsCompleted)
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            uiThread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task TopicVariationPacing()
    {
        var gate = new AsyncGate();
        var source = new FakeRelatedContextSource { BeforeReturn = gate.WaitAsync };
        using var coordinator = new RelatedContextCoordinator(source);
        var events = new List<RelatedContextStatus>();
        coordinator.StatusChanged += (_, status) => events.Add(status);
        var first = coordinator.RefreshAsync(
            new("session-a", "Initial topic", 1, TopicId: "topic-a"), CancellationToken.None);
        await gate.Entered;
        using var cancelled = new CancellationTokenSource();
        var second = coordinator.RefreshAsync(
            new("session-a", "Rephrased topic", 2, TopicId: "topic-b"), cancelled.Token);
        var third = coordinator.RefreshAsync(
            new("session-a", "Newest phrasing", 3, TopicId: "topic-c"), CancellationToken.None);
        Check.Equal(1, source.Calls);
        cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => second);
        source.BeforeReturn = null;
        gate.Release();
        var firstResult = await first;
        var latest = await third;
        Check.Equal(2, source.Calls);
        Check.Equal("topic-a,topic-c", string.Join(",", source.Queries.Select(query => query.TopicId)));
        Check.Equal("1,3", string.Join(",", source.Queries.Select(query => query.TranscriptRevision)));
        Check.Equal("Initial topic", firstResult.Topic);
        Check.Equal("Newest phrasing", latest.Topic);
        Check.Equal(RelatedContextState.Available, coordinator.Status.State);
        Check.Equal("Newest phrasing", coordinator.Status.Topic);
        Check.Equal("topic-c", coordinator.Status.TopicId);
        Check.Equal(3L, coordinator.Status.TranscriptRevision);
        Check.True(coordinator.Status.Snapshot!.Items.Single().Title.Contains(
            "Newest phrasing", StringComparison.Ordinal));
        Check.True(!events.Any(status =>
            status.State == RelatedContextState.Available && status.Topic == "Initial topic"));
        Check.Equal("Newest phrasing", events[^1].Topic);
    }

    private static async Task EventOrdering()
    {
        var source = new FakeRelatedContextSource();
        using var coordinator = new RelatedContextCoordinator(source);
        var events = new List<RelatedContextStatus>();
        coordinator.StatusChanged += (_, status) => events.Add(status);
        var result = await coordinator.RefreshAsync(
            new("session-a", "Fast topic", 1, TopicId: "topic-fast"), CancellationToken.None);
        Check.Equal(RelatedContextState.Available, result.State);
        Check.True(events.Count > 0);
        Check.Equal(RelatedContextState.Available, events[^1].State);
        var available = events.FindIndex(status => status.State == RelatedContextState.Available);
        var lateRefreshing = events.FindIndex(available + 1,
            status => status.State == RelatedContextState.Refreshing);
        Check.Equal(-1, lateRefreshing);
        var cached = await coordinator.RefreshAsync(
            new("session-a", "Fast topic", 1, TopicId: "topic-fast"), CancellationToken.None);
        Check.Equal(RelatedContextState.Cached, cached.State);
        Check.Equal(1, source.Calls);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => coordinator.RefreshAsync(
            new("session-a", "Canceled before start", 2, ForceRefresh: true, TopicId: "topic-fast"),
            cancelled.Token));
        Check.Equal(1, source.Calls);
    }

    private static async Task InvalidationBeforeFetchStarts()
    {
        var source = new FakeRelatedContextSource();
        using var coordinator = new RelatedContextCoordinator(source);
        var context = new PumpedSynchronizationContext();
        var finished = new TaskCompletionSource<RelatedContextStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var request = coordinator.RefreshAsync(
                    new("session-cancel", "Cancelled topic", 1, TopicId: "topic-cancel"),
                    CancellationToken.None);
                coordinator.Invalidate();
                while (!request.IsCompleted)
                    context.PumpOne(TimeSpan.FromMilliseconds(20));
                finished.TrySetResult(request.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                finished.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };
        uiThread.Start();

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(RelatedContextState.Pending, result.State);
        Check.Equal(0, source.Calls);
        Check.Equal(RelatedContextState.Pending, coordinator.Status.State);
        Check.True(uiThread.Join(TimeSpan.FromSeconds(1)));
    }

    private static async Task SourceRevisionPacing()
    {
        var gate = new AsyncGate();
        var source = new FakeRelatedContextSource { BeforeReturn = gate.WaitAsync };
        using var coordinator = new RelatedContextCoordinator(source);
        var events = new List<RelatedContextStatus>();
        coordinator.StatusChanged += (_, status) => events.Add(status);
        var first = coordinator.RefreshAsync(
            new("session-a", "Stable topic", 1, TopicId: "topic-stable"),
            CancellationToken.None);
        await gate.Entered;
        var second = coordinator.RefreshAsync(
            new("session-a", "Stable topic", 2, TopicId: "topic-stable"),
            CancellationToken.None);
        var latest = coordinator.RefreshAsync(
            new("session-a", "Updated label", 3, TopicId: "topic-stable"),
            CancellationToken.None);
        source.BeforeReturn = null;
        gate.Release();
        await first;
        var latestStatus = await latest;
        var secondStatus = await second;
        Check.Equal(2, source.Calls);
        Check.Equal("1,3", string.Join(",", source.Queries.Select(query => query.TranscriptRevision)));
        Check.Equal(3L, latestStatus.TranscriptRevision);
        Check.Equal(latestStatus, secondStatus);
        Check.Equal("topic-stable", latestStatus.TopicId);
        Check.True(!events.Any(status =>
            status.State == RelatedContextState.Available && status.TranscriptRevision == 1));
    }

    private static async Task StaleResults()
    {
        var firstGate = new AsyncGate();
        var source = new FakeRelatedContextSource { BeforeReturn = firstGate.WaitAsync };
        using var coordinator = new RelatedContextCoordinator(source);
        var staleSession = coordinator.RefreshAsync(
            new("session-a", "Topic A", 30, TopicId: "topic-a"), CancellationToken.None);
        await firstGate.Entered;
        source.BeforeReturn = null;
        var current = await coordinator.RefreshAsync(
            new("session-b", "Topic B", 1, TopicId: "topic-b"), CancellationToken.None);
        firstGate.Release();
        await staleSession;
        Check.Equal("session-b", coordinator.Status.SessionId);
        Check.Equal("Topic B", coordinator.Status.Topic);
        Check.Equal(current.Snapshot, coordinator.Status.Snapshot);

        var authGate = new AsyncGate();
        source.BeforeReturn = authGate.WaitAsync;
        var staleAuth = coordinator.RefreshAsync(
            new("session-b", "Topic B", 2, ForceRefresh: true, TopicId: "topic-b"),
            CancellationToken.None);
        await authGate.Entered;
        source.Revision++;
        authGate.Release();
        var authResult = await staleAuth;
        Check.Equal(RelatedContextState.Pending, authResult.State);
        Check.True(authResult.Snapshot is null);
        Check.Equal(source.Revision, authResult.SourceRevision);
    }

    private sealed class FakeRelatedContextSource : IRelatedContextSource
    {
        public bool IsAvailable { get; set; } = true;
        public long Revision { get; set; } = 1;
        public int Calls { get; private set; }
        public List<ContextQuery> Queries { get; } = [];
        public Func<CancellationToken, Task>? BeforeReturn { get; set; }

        public async Task<RelatedContextSnapshot> GetRelatedContextAsync(
            ContextQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Queries.Add(query);
            if (BeforeReturn is not null) await BeforeReturn(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(DateTimeOffset.UtcNow,
            [
                new($"Related to {query.Topic}", "Grounded synthetic connection.",
                    ClaimKind.InferredConnection,
                    [new("Synthetic source", "https://contoso.example/source",
                        ContextSourceType.Document, null)])
            ], RelatedContextAvailability.Available);
        }
    }

    private sealed class PumpedSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state)
            => _callbacks.Add((callback, state));

        public void PumpOne(TimeSpan timeout)
        {
            if (_callbacks.TryTake(out var work, timeout))
                work.Callback(work.State);
        }
    }
}
