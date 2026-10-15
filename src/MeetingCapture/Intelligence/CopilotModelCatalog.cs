using GitHub.Copilot;

namespace MeetingCapture.Intelligence;

/// <summary>A Copilot model that can be chosen in settings.</summary>
public sealed record CopilotModelOption(string Id, string Name);

/// <summary>The model list from the Copilot service and when it was retrieved.</summary>
public sealed record CopilotModelCatalogSnapshot(
    IReadOnlyList<CopilotModelOption> Models,
    DateTimeOffset RetrievedAt);

/// <summary>Why the Copilot model list could not be loaded.</summary>
public enum CopilotModelCatalogFailure { Timeout, Unavailable, Empty }

/// <summary>Thrown when the Copilot model list is unavailable, empty, or times out.</summary>
public sealed class CopilotModelCatalogException(
    CopilotModelCatalogFailure kind,
    Exception? innerException = null) : Exception(kind switch
{
    CopilotModelCatalogFailure.Timeout => "GitHub Copilot model discovery timed out.",
    CopilotModelCatalogFailure.Empty => "GitHub Copilot returned no available models.",
    _ => "GitHub Copilot models could not be loaded."
}, innerException)
{
    public CopilotModelCatalogFailure Kind { get; } = kind;
}

/// <summary>
/// Loads the list of Copilot models the user can select. Results are cached for 15 minutes,
/// requests time out after 45 seconds, and concurrent callers share one fetch.
/// Failures throw <see cref="CopilotModelCatalogException"/>.
/// </summary>
public sealed class CopilotModelCatalog
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<IReadOnlyList<CopilotModelOption>>> _fetch;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _cacheLifetime;
    private CopilotModelCatalogSnapshot? _cached;

    public CopilotModelCatalog() : this(FetchAsync, RequestTimeout, CacheLifetime) { }

    internal CopilotModelCatalog(
        Func<CancellationToken, Task<IReadOnlyList<CopilotModelOption>>> fetch,
        TimeSpan? requestTimeout = null,
        TimeSpan? cacheLifetime = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _requestTimeout = requestTimeout ?? RequestTimeout;
        _cacheLifetime = cacheLifetime ?? CacheLifetime;
        if (_requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (_cacheLifetime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
    }

    /// <summary>
    /// Returns the model list from the cache when it is still fresh, otherwise fetches it.
    /// Set <paramref name="forceRefresh"/> to skip the cache.
    /// </summary>
    public async Task<CopilotModelCatalogSnapshot> GetModelsAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        // Use a fast cache check first; callers that miss it wait for the gate and check again
        // so simultaneous settings requests share a single fetch.
        if (!forceRefresh && IsFresh(_cached))
            return _cached!;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && IsFresh(_cached))
                return _cached!;
            // The linked token preserves user cancellation while also bounding a stuck SDK call.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_requestTimeout);
            try
            {
                var models = (await _fetch(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
                    // Ignore malformed entries, fill a missing display name from the ID, then
                    // make the result stable for the settings list.
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .Select(model => new CopilotModelOption(
                        model.Id.Trim(),
                        string.IsNullOrWhiteSpace(model.Name) ? model.Id.Trim() : model.Name.Trim()))
                    .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (models.Length == 0)
                    throw new CopilotModelCatalogException(CopilotModelCatalogFailure.Empty);
                return _cached = new CopilotModelCatalogSnapshot(models, DateTimeOffset.UtcNow);
            }
            // Keep caller cancellation distinct from our timeout and from service failures.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
            {
                throw new CopilotModelCatalogException(CopilotModelCatalogFailure.Timeout, ex);
            }
            catch (CopilotModelCatalogException) { throw; }
            catch (Exception ex)
            {
                throw new CopilotModelCatalogException(CopilotModelCatalogFailure.Unavailable, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(CopilotModelCatalogSnapshot? snapshot)
        => snapshot is not null && DateTimeOffset.UtcNow - snapshot.RetrievedAt < _cacheLifetime;

    private static async Task<IReadOnlyList<CopilotModelOption>> FetchAsync(
        CancellationToken cancellationToken)
    {
        // Model discovery uses the same local Copilot CLI runtime as requests, but a short-lived
        // client keeps catalog loading independent from the notes service's request lifecycle.
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.CopilotCli
        });
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(model => new CopilotModelOption(model.Id, model.Name)).ToArray();
    }
}
