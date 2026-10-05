namespace MeetingCapture.Graph;

/// <summary>Bounds the caller's wait without abandoning ownership of a late authentication result.</summary>
public sealed class BoundedSignIn(TimeSpan timeout, Action<Exception> reportCleanupFailure) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private int _isBusy;
    private volatile bool _disposed;

    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;

    public async Task RunAsync<T>(Func<CancellationToken, Task<T>> authenticate, Action<T> publish,
        CancellationToken cancellationToken) where T : IDisposable
    {
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
                throw new InvalidOperationException("A previous sign-in is still finishing. Local recording remains available.");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(timeout);
            _active = lifetime;
            Volatile.Write(ref _isBusy, 1);
        }

        var token = lifetime.Token;
        var operation = Task.Run(() => authenticate(token), token);
        var published = false;
        try
        {
            var result = await operation.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                publish(result);
                published = true;
            }
        }
        catch (OperationCanceledException) when (!_disposed && !cancellationToken.IsCancellationRequested && token.IsCancellationRequested)
        {
            throw new TimeoutException("Microsoft Graph sign-in timed out. Local recording remains available.");
        }
        finally
        {
            // A cancelled await is not proof MSAL stopped. Keep the guard until actual cleanup finishes.
            var cleanup = FinishAsync(operation, lifetime, published);
            if (operation.IsCompleted) await cleanup.ConfigureAwait(false);
        }
    }

    private async Task FinishAsync<T>(Task<T> operation, CancellationTokenSource lifetime, bool published)
        where T : IDisposable
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            if (!published) result.Dispose();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { reportCleanupFailure(ex); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, lifetime))
                {
                    _active = null;
                    Volatile.Write(ref _isBusy, 0);
                }
                lifetime.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _active?.Cancel();
        }
    }
}
